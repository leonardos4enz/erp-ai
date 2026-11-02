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
/// <param name="Shared">Spec 042: lo compartió con algún rol.</param>
public sealed record PersonalReportSummary(int Id, string Label, string Entity, string EntityLabel, string? Chart, int Version,
    bool Available, DateTime UpdatedAt, bool Shared);
/// <summary>Spec 042: un reporte compartido con quien pide. Catálogo y gráfica solo si corre para él.</summary>
public sealed record SharedReportSummary(int Id, string Label, string Owner, string? Entity, string? EntityLabel, string? Chart,
    bool Available, DateTime UpdatedAt);
/// <param name="Definition">Spec 042: nula si es compartido y usa algo que quien lo abre no puede ver.</param>
/// <param name="Problems">Por qué no corre hoy (vacío si corre).</param>
/// <param name="Owned">Spec 042: ¿es de quien lo abre? (si no, se lo compartieron y no lo puede cambiar).</param>
/// <param name="Roles">Spec 042: solo al dueño, con qué roles lo compartió.</param>
/// <param name="ShareableRoles">Spec 042: solo al dueño, con qué roles puede compartirlo.</param>
public sealed record PersonalReport(int Id, string Label, int Version, PersonalReportDef? Definition, bool Available,
    IReadOnlyList<string> Problems, bool Owned = true, string? Owner = null, IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? ShareableRoles = null);
public sealed record SavePersonalReportRequest(string? Label, PersonalReportDef? Definition, int? Version);

/// <summary>
/// Spec 041: reportes personales. Son datos de su dueño (no metadatos): nadie más los lee ni los cambia (404 igual que
/// inexistente), cambiar y borrar llevan la versión que se abrió (409 si otra pestaña se adelantó) y cada cambio queda en la
/// bitácora en la misma transacción. La definición se valida contra el dueño al guardar y otra vez en cada corrida.
/// </summary>
public sealed class PersonalReportService(Db db, MetadataStore store)
{
    public const int MaxPerUser = 50;
    public const string ShareTable = "meta.PersonalReportShare";
    public const string ShareKey = "ReportId";
    /// <summary>Spec 042 (H4 CA2): lo que ve quien abre un reporte compartido que no corre para él (sin nombrar lo que falta).</summary>
    public const string NotForYou = "No disponible para ti: usa algo que no puedes ver.";

    private sealed record Row(int Id, string EntityName, string Label, string DefinitionJson, int Version, DateTime CreatedAt, DateTime? UpdatedAt);
    private sealed record ListRow(int Id, string EntityName, string Label, string DefinitionJson, int Version, DateTime CreatedAt, DateTime? UpdatedAt,
        bool Shared)
    {
        public Row ToRow() => new(Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt);
    }
    private sealed record SharedRow(int Id, string EntityName, string Label, string DefinitionJson, int Version, DateTime CreatedAt,
        DateTime? UpdatedAt, string Owner, int UserId)
    {
        public Row ToRow() => new(Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt);
    }

    public async Task<IReadOnlyList<PersonalReportSummary>> ListAsync(UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<ListRow>($"""
            SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM {ShareTable} s WHERE s.{ShareKey} = p.Id) THEN 1 ELSE 0 END AS bit) AS Shared
              FROM meta.PersonalReport p WHERE UserId = @uid ORDER BY Label
            """, new { uid = user.Id });
        return rows.Select(r =>
        {
            var d = Parse(r.ToRow());
            var available = Problems(d, r.Label, user, model).Count == 0;
            // Un catálogo que ya no lee (o archivado) no da ni su etiqueta: solo el identificador que el dueño eligió.
            var label = available && model.TryGetValue(r.EntityName, out var e) ? e.LabelPlural : r.EntityName;
            return new PersonalReportSummary(r.Id, r.Label, r.EntityName, label, d.Chart, r.Version, available, r.UpdatedAt ?? r.CreatedAt, r.Shared);
        }).ToList();
    }

    /// <summary>
    /// Spec 042 (H2 CA2): los reportes que otros compartieron con alguno de los roles de quien pide (revalidado: el dueño activo
    /// y con el rol). El catálogo solo se nombra si el reporte corre para quien pide.
    /// </summary>
    public async Task<IReadOnlyList<SharedReportSummary>> SharedAsync(UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var candidates = (await cn.QueryAsync<SharedRow>($"""
            SELECT TOP (@max) p.Id, p.EntityName, p.Label, p.DefinitionJson, p.Version, p.CreatedAt, p.UpdatedAt, u.DisplayName AS Owner, p.UserId
              FROM meta.PersonalReport p JOIN meta.[User] u ON u.Id = p.UserId
             WHERE p.UserId <> @uid AND {PersonalSharing.Visible(ShareTable, ShareKey, "p")}
             ORDER BY COALESCE(p.UpdatedAt, p.CreatedAt) DESC, p.Id DESC
            """, new { uid = user.Id, viewerRoles = user.Roles, max = PersonalSharing.MaxListed }))
            .OrderBy(x => x.Label, StringComparer.CurrentCulture).ThenBy(x => x.Owner, StringComparer.CurrentCulture).ToList(); // los más recientes, por nombre
        var valid = await PersonalSharing.RevalidateAsync(ShareTable, ShareKey, candidates.Select(r => (r.Id, r.UserId)).ToList(), user.Roles, cn);
        return candidates.Where(r => valid.Contains(r.Id)).Select(r =>
        {
            var d = Parse(r.ToRow());
            return Problems(d, r.Label, user, model).Count == 0
                ? new SharedReportSummary(r.Id, r.Label, r.Owner, r.EntityName, model[r.EntityName].LabelPlural, d.Chart, true, r.UpdatedAt ?? r.CreatedAt)
                : new SharedReportSummary(r.Id, r.Label, r.Owner, null, null, null, false, r.UpdatedAt ?? r.CreatedAt);
        }).ToList();
    }

    public async Task<PersonalReport> GetAsync(int id, UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        // Spec 042: el suyo o uno compartido con alguno de sus roles (revalidado); cualquier otro, 404 igual que inexistente.
        var r = await cn.QuerySingleOrDefaultAsync<SharedRow>($"""
            SELECT p.Id, p.EntityName, p.Label, p.DefinitionJson, p.Version, p.CreatedAt, p.UpdatedAt, u.DisplayName AS Owner, p.UserId
              FROM meta.PersonalReport p JOIN meta.[User] u ON u.Id = p.UserId
             WHERE p.Id = @id AND (p.UserId = @uid OR {PersonalSharing.Visible(ShareTable, ShareKey, "p")})
            """, new { id, uid = user.Id, viewerRoles = user.Roles }) ?? throw NotFound();
        if (r.UserId != user.Id && !(await PersonalSharing.RevalidateAsync(ShareTable, ShareKey, [(r.Id, r.UserId)], user.Roles, cn)).Contains(r.Id))
            throw NotFound();
        var d = Parse(r.ToRow());
        var problems = Problems(d, r.Label, user, model);
        if (r.UserId == user.Id)
            return new PersonalReport(r.Id, r.Label, r.Version, d, problems.Count == 0, problems, true, r.Owner,
                await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, r.Id, cn), await PersonalSharing.ShareableAsync(user, cn));
        // Spec 042 (H4 CA2): a quien se lo compartieron no se le entrega una definición que nombra lo que no ve, ni la etiqueta
        // de los valores de los filtros (el nombre de un registro que quizá está fuera de su alcance).
        return problems.Count > 0
            ? new PersonalReport(r.Id, r.Label, r.Version, null, false, [NotForYou], false, r.Owner)
            : new PersonalReport(r.Id, r.Label, r.Version, WithoutFilterLabels(d), true, [], false, r.Owner);
    }

    /// <summary>Spec 042 (H2 CA1): comparte con los roles pedidos (solo roles que tiene) con la versión que se abrió.</summary>
    public async Task<ShareResult> ShareAsync(int id, ShareRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var prev = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, EntityName, Label, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalReport WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
            new { id, uid = user.Id }, tx) ?? throw NotFound();
        if (req.Version is null || prev.Version != req.Version) throw Stale();
        var roles = await PersonalSharing.ResolveAsync(req.Roles, user, cn, tx);
        var before = await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, id, cn, tx);
        await PersonalSharing.ReplaceAsync(ShareTable, ShareKey, id, roles, cn, tx);
        await cn.ExecuteAsync("UPDATE meta.PersonalReport SET Version = Version + 1, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
        var after = roles.Select(x => x.Name).Order(StringComparer.CurrentCulture).ToList();
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalReportChange, AuditEntity(prev.EntityName, model), id.ToString(),
            Trail(prev.Label, prev.DefinitionJson, before), Trail(prev.Label, prev.DefinitionJson, after));
        await tx.CommitAsync(ct);
        return new ShareResult(id, prev.Version + 1, after);
    }

    /// <summary>Spec 042: la definición sin la etiqueta de presentación de los valores de los filtros.</summary>
    public static PersonalReportDef WithoutFilterLabels(PersonalReportDef d) =>
        d.Filters is null ? d : d with { Filters = d.Filters.Select(f => f with { Label = null }).ToList() };

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
    private static object Trail(string label, string definition) => new { label, definitionSha256 = Sha256(definition) };

    /// <summary>Spec 042: con los roles con los que está compartido.</summary>
    private static object Trail(string label, string definition, IReadOnlyList<string> roles) =>
        new { label, definitionSha256 = Sha256(definition), roles };

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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
