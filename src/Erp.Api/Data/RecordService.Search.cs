using System.Globalization;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record SearchHit(string Entity, string EntityLabel, long Id, string Label);

public sealed partial class RecordService
{
    public const int MinGlobalSearch = 2;
    public const int MaxGlobalSearch = 50;
    public const int GlobalSearchPerEntity = 5;
    public const int GlobalSearchTotal = 20;
    public const int GlobalSearchMaxEntities = 30;

    /// <summary>
    /// Spec 024 (H5): documentos por folio para la búsqueda global. Solo catálogos que el usuario puede leer, con un folio
    /// (campo autonumérico) que puede ver; cada consulta lleva el alcance por registro y empresa (006/010).
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string? q, UserContext user, CancellationToken ct)
    {
        var term = q?.Trim() ?? "";
        if (term.Length is < MinGlobalSearch or > MaxGlobalSearch)
            throw new AppException(400, $"Escribe de {MinGlobalSearch} a {MaxGlobalSearch} caracteres para buscar.");
        var model = await store.GetAsync(ct);
        var targets = model.Values
            .Where(e => !e.IsArchived && !e.IsChild && user.Access(e.Name).Read)
            .Select(e => (Entity: e, Folio: e.ActiveFields.FirstOrDefault(f => f.Type == FieldTypes.Autonumber && FieldAccess.CanSee(e, f, user))))
            .Where(x => x.Folio is not null)
            .OrderBy(x => x.Entity.LabelPlural, StringComparer.CurrentCulture)
            .Take(GlobalSearchMaxEntities)
            .ToList();
        var hits = new List<SearchHit>();
        await using var cn = await db.OpenAsync(ct);
        foreach (var (e, folio) in targets)
        {
            if (hits.Count >= GlobalSearchTotal) break;
            await using var cmd = cn.CreateCommand();
            var where = "t.[IsDeleted] = 0";
            if (RecordFilter.For(e, user, write: false) is { } scope)
            {
                scope.AddTo(cmd.Parameters);
                where += " AND " + scope.Sql;
            }
            cmd.Parameters.Add(new SqlParameter("@q", "%" + EscapeLike(term) + "%"));
            var column = $"t.{Quote(folio!.Name)}";
            var take = Math.Min(GlobalSearchPerEntity, GlobalSearchTotal - hits.Count);
            cmd.CommandText = $"SELECT TOP ({take}) t.[Id], {column} FROM {AppTable(e.Name)} t WHERE {where} AND {column} LIKE @q ESCAPE '\\' ORDER BY {column}";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                hits.Add(new SearchHit(e.Name, e.Label, r.GetInt64(0),
                    r.IsDBNull(1) ? $"#{r.GetInt64(0)}" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)!));
        }
        return hits;
    }
}
