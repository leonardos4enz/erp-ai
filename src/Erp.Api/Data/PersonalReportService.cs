using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Data;

/// <param name="Available">¿Corre hoy para su dueño? (un campo que dejó de ver, un catálogo archivado: no).</param>
public sealed record PersonalReportSummary(int Id, string Label, string Entity, string EntityLabel, string? Chart, int Version,
    bool Available, DateTime UpdatedAt);
/// <param name="Problems">Por qué no corre hoy (vacío si corre).</param>
public sealed record PersonalReport(int Id, string Label, int Version, PersonalReportDef Definition, bool Available,
    IReadOnlyList<string> Problems);
public sealed record SavePersonalReportRequest(string? Label, PersonalReportDef? Definition, int? Version);

/// <summary>
/// Spec 041: reportes personales. Son datos de su dueño (no metadatos): nadie más los lee ni los cambia (404 igual que
/// inexistente), cambiar y borrar llevan la versión que se abrió (409 si otra pestaña se adelantó) y cada cambio queda en la
/// bitácora en la misma transacción. La definición se valida contra el dueño al guardar y otra vez en cada corrida.
/// </summary>
public sealed class PersonalReportService(Db db, MetadataStore store)
{
    public const int MaxPerUser = 50;

    private sealed record Row(int Id, string EntityName, string Label, string DefinitionJson, int Version, DateTime CreatedAt, DateTime? UpdatedAt);

    public async Task<IReadOnlyList<PersonalReportSummary>> ListAsync(UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(
            "SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalReport WHERE UserId = @uid ORDER BY Label",
            new { uid = user.Id });
        return rows.Select(r =>
        {
            var d = Parse(r);
            var available = Problems(d, r.Label, user, model).Count == 0;
            // Un catálogo que ya no lee (o archivado) no da ni su etiqueta: solo el identificador que el dueño eligió.
            var label = available && model.TryGetValue(r.EntityName, out var e) ? e.LabelPlural : r.EntityName;
            return new PersonalReportSummary(r.Id, r.Label, r.EntityName, label, d.Chart, r.Version, available, r.UpdatedAt ?? r.CreatedAt);
        }).ToList();
    }

    public async Task<PersonalReport> GetAsync(int id, UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var r = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalReport WHERE Id = @id AND UserId = @uid",
            new { id, uid = user.Id }) ?? throw NotFound();
        var d = Parse(r);
        var problems = Problems(d, r.Label, user, model);
        return new PersonalReport(r.Id, r.Label, r.Version, d, problems.Count == 0, problems);
    }

    public async Task<PersonalReport> SaveAsync(int? id, SavePersonalReportRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var model = await store.GetAsync(ct);
        var label = req.Label?.Trim() ?? "";
        if (label.Length is 0 or > InterfaceRules.MaxLabel)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["label"] = [$"Escribe un nombre de 1 a {InterfaceRules.MaxLabel} caracteres."] });

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Spec 041 (H3 CA3): un candado por dueño hasta el commit. Dos altas o cambios simultáneos no ven el mismo «no hay ese
        // nombre» ni el mismo conteo (con candados de rango, dos altas pueden bloquearse entre sí y una morir como víctima).
        var locked = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, new { res = $"erp-personal-report:{user.Id}" }, tx);
        if (locked < 0) throw new Conflict("Hay otro cambio tuyo a tus reportes en curso. Intenta de nuevo.");
        // Regresión (revisión adversarial 041, F-F): la versión se comprueba antes de validar (una versión vieja con una
        // definición que ya no vale respondía 400 y, ya corregida, 409).
        Row? prev = null;
        if (id is not null)
        {
            prev = await cn.QuerySingleOrDefaultAsync<Row>(
                "SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalReport WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
                new { id, uid = user.Id }, tx) ?? throw NotFound();
            if (req.Version is null || prev.Version != req.Version) throw Stale();
        }
        var (e, _) = PersonalReports.Check(req.Definition, label, user, model);
        // Regresión (revisión adversarial 041, F-C): un reporte no cambia de catálogo (su bitácora quedaría bajo el otro).
        if (prev is not null && !string.Equals(prev.EntityName, e.Name, StringComparison.OrdinalIgnoreCase))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["report"] = ["Un reporte guardado no cambia de catálogo; usa «Guardar como» para otro."] });
        var d = req.Definition! with { Entity = e.Name };
        var def = JsonSerializer.Serialize(d, MetadataStore.Json);
        var p = new { uid = user.Id, e = e.Name, label, def, id, v = req.Version };
        if (await cn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM meta.PersonalReport WHERE UserId = @uid AND Label = @label AND (@id IS NULL OR Id <> @id)", p, tx) is not null)
            throw DuplicateLabel();
        int version;
        try
        {
            if (prev is null)
            {
                var count = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.PersonalReport WHERE UserId = @uid", p, tx);
                if (count >= MaxPerUser)
                    throw new ValidationFailed(new Dictionary<string, string[]> { ["label"] = [$"Máximo {MaxPerUser} reportes personales por persona."] });
                id = await cn.ExecuteScalarAsync<int>(
                    "INSERT meta.PersonalReport (UserId, EntityName, Label, DefinitionJson) OUTPUT inserted.Id VALUES (@uid, @e, @label, @def)", p, tx);
                version = 1;
            }
            else
            {
                await cn.ExecuteAsync(
                    "UPDATE meta.PersonalReport SET Label = @label, DefinitionJson = @def, Version = Version + 1, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id AND UserId = @uid AND Version = @v",
                    p, tx);
                version = prev.Version + 1;
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { throw DuplicateLabel(); } // respaldo: UNIQUE (UserId, Label)
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalReportChange, AuditEntity(e.Name, model), id.ToString(),
            prev is null ? null : Trail(prev.Label, prev.DefinitionJson), Trail(label, def));
        await tx.CommitAsync(ct);
        return new PersonalReport(id!.Value, label, version, d, true, []);
    }

    public async Task DeleteAsync(int id, int? version, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var prev = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalReport WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
            new { id, uid = user.Id }, tx) ?? throw NotFound();
        if (version is null || prev.Version != version) throw Stale();
        await cn.ExecuteAsync("DELETE FROM meta.PersonalReport WHERE Id = @id AND UserId = @uid", new { id, uid = user.Id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalReportChange, AuditEntity(prev.EntityName, model), id.ToString(),
            before: Trail(prev.Label, prev.DefinitionJson));
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Regresión (revisión adversarial 041, F-B): la bitácora no lleva la definición (rutas a campos o catálogos que el
    /// auditor quizá no ve y los valores de los filtros), solo el nombre y la huella SHA-256 de la definición: dice que
    /// cambió y cuándo, sin mostrar el reporte personal de otro.
    /// </summary>
    private static object Trail(string label, string definition) => new
    {
        label, definitionSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition))).ToLowerInvariant(),
    };

    /// <summary>Regresión (revisión adversarial 041, F-D): el de una partida va bajo su documento (la bitácora filtra por catálogo raíz).</summary>
    private static string AuditEntity(string entity, IReadOnlyDictionary<string, EntityDef> model) =>
        model.TryGetValue(entity, out var e) && e.IsChild && e.ParentEntity is { } parent ? parent : entity;

    private static PersonalReportDef Parse(Row r) =>
        (JsonSerializer.Deserialize<PersonalReportDef>(r.DefinitionJson, MetadataStore.Json) ?? new PersonalReportDef()) with { Entity = r.EntityName };

    /// <summary>Por qué no corre hoy para el usuario (lista vacía si corre).</summary>
    private static IReadOnlyList<string> Problems(PersonalReportDef d, string label, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        try
        {
            PersonalReports.Check(d, label, user, model);
            return [];
        }
        catch (NotFound) { return ["El catálogo del reporte ya no existe o ya no lo puedes consultar."]; }
        catch (ValidationFailed ex) { return ex.Errors?.SelectMany(x => x.Value).ToList() ?? [ex.Message]; }
    }

    private static NotFound NotFound() => new("El reporte no existe.");
    private static Conflict Stale() => new("Otra pestaña o sesión cambió este reporte después de que lo abriste. Vuelve a abrirlo para ver la versión actual.");
    private static ValidationFailed DuplicateLabel() =>
        new(new Dictionary<string, string[]> { ["label"] = ["Ya tienes un reporte con ese nombre."] });
}
