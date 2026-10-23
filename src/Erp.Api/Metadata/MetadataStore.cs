using System.Data.Common;
using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;

namespace Erp.Api.Metadata;

/// <summary>
/// Lee el modelo desde <c>meta.Entity</c>/<c>meta.Field</c> y lo mantiene en memoria.
/// Se invalida al aplicar un plan y caduca a los 30 s para cubrir despliegues con varias instancias.
/// </summary>
public sealed class MetadataStore(Db db)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IReadOnlyDictionary<string, EntityDef> Model, DateTime LoadedAt)? _cache;

    public async Task<IReadOnlyDictionary<string, EntityDef>> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTime.UtcNow - c.LoadedAt < Ttl) return c.Model;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && DateTime.UtcNow - c2.LoadedAt < Ttl) return c2.Model;
            await using var cn = await db.OpenAsync(ct);
            var model = await LoadAsync(cn, null);
            _cache = (model, DateTime.UtcNow);
            return model;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate() => _cache = null;

    /// <summary>Entidad activa o 404. El nombre de la URL nunca llega a SQL: se usa el de los metadatos.</summary>
    public async Task<EntityDef> GetActiveAsync(string name, CancellationToken ct = default)
    {
        var model = await GetAsync(ct);
        return model.TryGetValue(name, out var e) && !e.IsArchived ? e : throw new NotFound("El catálogo no existe.");
    }

    internal static async Task<IReadOnlyDictionary<string, EntityDef>> LoadAsync(DbConnection cn, DbTransaction? tx)
    {
        var entities = await cn.QueryAsync<EntityRow>(
            "SELECT Id, Name, Label, LabelPlural, Icon, Description, DisplayField, IsArchived FROM meta.Entity", transaction: tx);
        var fields = (await cn.QueryAsync<FieldRow>("""
            SELECT EntityId, Name, Label, Type, IsRequired, IsUnique, MaxLength, [Precision], Scale, OptionsJson,
                   ReferenceEntity, DisplayOrder, ShowInList, RequiredForRolesJson, IsArchived
              FROM meta.Field
            """, transaction: tx)).ToLookup(f => f.EntityId);

        return entities.ToDictionary(e => e.Name, e => new EntityDef
        {
            Name = e.Name, Label = e.Label, LabelPlural = e.LabelPlural, Icon = e.Icon,
            Description = e.Description, DisplayField = e.DisplayField, IsArchived = e.IsArchived,
            Fields = fields[e.Id].OrderBy(f => f.DisplayOrder).Select(f => new FieldDef
            {
                Name = f.Name, Label = f.Label, Type = f.Type, Required = f.IsRequired, Unique = f.IsUnique,
                MaxLength = f.MaxLength, Precision = f.Precision, Scale = f.Scale,
                Options = ParseList(f.OptionsJson), ReferenceEntity = f.ReferenceEntity,
                DisplayOrder = f.DisplayOrder, ShowInList = f.ShowInList,
                RequiredForRoles = ParseList(f.RequiredForRolesJson), IsArchived = f.IsArchived,
            }).ToList(),
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string>? ParseList(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<string>>(json);

    private sealed record EntityRow(int Id, string Name, string Label, string LabelPlural, string Icon,
        string? Description, string? DisplayField, bool IsArchived);

    private sealed record FieldRow(int EntityId, string Name, string Label, string Type, bool IsRequired, bool IsUnique,
        int? MaxLength, int? Precision, int? Scale, string? OptionsJson, string? ReferenceEntity, int DisplayOrder,
        bool ShowInList, string? RequiredForRolesJson, bool IsArchived);
}
