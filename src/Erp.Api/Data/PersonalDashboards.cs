using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Data;

/// <summary>
/// Spec 042: tarjeta de un tablero personal. Una sola fuente: un reporte del modelo (<see cref="Entity"/> +
/// <see cref="Report"/>) o un reporte personal del dueño del tablero (<see cref="PersonalReport"/>).
/// </summary>
public sealed record PersonalWidget
{
    public string? Type { get; init; }
    public string? Label { get; init; }
    public string? Entity { get; init; }
    public string? Report { get; init; }
    public int? PersonalReport { get; init; }
    public bool Wide { get; init; }
}

public sealed record SavePersonalDashboardRequest(string? Label, string? Description, IReadOnlyList<PersonalWidget?>? Widgets, int? Version);
/// <param name="Shared">Lo compartió con algún rol.</param>
public sealed record PersonalDashboardSummary(int Id, string Label, string? Description, int Version, bool Shared, DateTime UpdatedAt);
public sealed record SharedDashboardSummary(int Id, string Label, string? Description, string Owner, DateTime UpdatedAt);
public sealed record PersonalDashboardList(IReadOnlyList<PersonalDashboardSummary> Mine, IReadOnlyList<SharedDashboardSummary> Shared);
/// <param name="Link">Ruta de la SPA para abrir el reporte de la tarjeta; nula si quien ve no puede abrirlo.</param>
/// <param name="Problems">Solo al dueño: por qué no corre.</param>
public sealed record PersonalCard(string Type, string Label, bool Wide, string? Entity, string? Report, int? PersonalReport, string? Link,
    bool Available, IReadOnlyList<string> Problems, ReportResult? Result);
/// <param name="Widgets">Solo al dueño: la definición para editarlo.</param>
public sealed record PersonalDashboardResult(int Id, string Label, string? Description, int Version, bool Owned, string Owner,
    IReadOnlyList<string>? Roles, IReadOnlyList<string>? ShareableRoles, IReadOnlyList<PersonalWidget>? Widgets, IReadOnlyList<PersonalCard> Cards);

/// <summary>Spec 042: el reporte personal al que apunta una tarjeta (siempre del dueño del tablero).</summary>
public sealed record CardReport(int Id, string Label, PersonalReportDef Definition);

/// <summary>
/// Spec 042: tarjetas de un tablero personal. Se validan contra el dueño al guardar y se resuelven contra **quien ve** en cada
/// corrida, con la misma regla que la 041 (<see cref="PersonalReports.Check"/>) y que los tableros del modelo
/// (<see cref="ReportService.Available"/>, tipo compatible con el reporte).
/// </summary>
public static class PersonalDashboards
{
    public const int MaxPerUser = 20;
    public const string Unavailable = "El reporte no existe o no puedes verlo.";

    private sealed record Def(IReadOnlyList<PersonalWidget>? Widgets);

    /// <summary>
    /// El catálogo, el reporte y los filtros con los que corre la tarjeta para <paramref name="user"/>, o por qué no corre.
    /// </summary>
    public static (EntityDef E, ReportDef R, IReadOnlyList<ViewFilter> Filters)? Resolve(PersonalWidget w, UserContext user,
        IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<int, CardReport> reports, List<string> problems)
    {
        EntityDef e;
        ReportDef r;
        IReadOnlyList<ViewFilter> filters = [];
        if (w.PersonalReport is { } id)
        {
            if (!reports.TryGetValue(id, out var pr)) { problems.Add("El reporte personal ya no existe."); return null; }
            try { (e, r) = PersonalReports.Check(pr.Definition, pr.Label, user, model); }
            catch (NotFound) { problems.Add("El catálogo del reporte ya no existe o ya no lo puedes consultar."); return null; }
            catch (ValidationFailed ex) { problems.AddRange(ex.Errors?.SelectMany(x => x.Value) ?? [ex.Message]); return null; }
            filters = pr.Definition.Filters ?? [];
        }
        else
        {
            if (!model.TryGetValue(w.Entity ?? "", out var me) || me.IsArchived
                || me.Reports?.FirstOrDefault(x => string.Equals(x.Name, w.Report, StringComparison.OrdinalIgnoreCase)) is not { } mr
                || !ReportService.Available(me, mr, user, model))
            {
                problems.Add(Unavailable);
                return null;
            }
            (e, r) = (me, mr);
        }
        var grouped = r.GroupBy is { Count: > 0 };
        if (w.Type == "chart" && !grouped) { problems.Add("Una gráfica usa un reporte con agrupación."); return null; }
        if (w.Type == "kpi" && grouped && !InterfaceRules.IsComparable(e, r, model))
        {
            problems.Add("Un indicador usa un reporte sin agrupación o agrupado solo por una fecha con periodo.");
            return null;
        }
        return (e, r, filters);
    }

    /// <summary>Valida las tarjetas contra el dueño (al guardar). Mensajes con el número de la tarjeta.</summary>
    public static List<string> Validate(IReadOnlyList<PersonalWidget?>? widgets, UserContext owner, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlyDictionary<int, CardReport> reports)
    {
        var msgs = new List<string>();
        if (widgets is null || widgets.Count is 0 or > InterfaceRules.MaxWidgets)
        {
            msgs.Add($"El tablero necesita de 1 a {InterfaceRules.MaxWidgets} tarjetas.");
            return msgs;
        }
        for (var i = 0; i < widgets.Count; i++)
        {
            var where = $"Tarjeta {i + 1}";
            var w = widgets[i];
            if (w is null) { msgs.Add($"{where}: está vacía."); continue; }
            if (!InterfaceRules.WidgetTypes.Contains(w.Type ?? "")) { msgs.Add($"{where}: el tipo '{w.Type}' no existe (kpi, chart, table)."); continue; }
            if (w.Label?.Trim().Length > InterfaceRules.MaxLabel) msgs.Add($"{where}: el título excede {InterfaceRules.MaxLabel} caracteres.");
            var fromModel = !string.IsNullOrWhiteSpace(w.Entity) || !string.IsNullOrWhiteSpace(w.Report);
            if (fromModel == (w.PersonalReport is not null) || (fromModel && (string.IsNullOrWhiteSpace(w.Entity) || string.IsNullOrWhiteSpace(w.Report))))
            {
                msgs.Add($"{where}: elige un reporte (del modelo o uno tuyo).");
                continue;
            }
            var problems = new List<string>();
            Resolve(w, owner, model, reports, problems);
            msgs.AddRange(problems.Select(p => $"{where}: {char.ToLowerInvariant(p[0])}{p[1..]}"));
        }
        return msgs;
    }

    public static string Serialize(IReadOnlyList<PersonalWidget> widgets) => JsonSerializer.Serialize(new Def(widgets), MetadataStore.Json);

    public static IReadOnlyList<PersonalWidget> Parse(string json) =>
        JsonSerializer.Deserialize<Def>(json, MetadataStore.Json)?.Widgets ?? [];

    /// <summary>Lo que se guarda de una tarjeta: título recortado y solo la fuente elegida.</summary>
    public static PersonalWidget Normalize(PersonalWidget w) => w.PersonalReport is not null
        ? new PersonalWidget { Type = w.Type, Label = Blank(w.Label), PersonalReport = w.PersonalReport, Wide = w.Wide }
        : new PersonalWidget { Type = w.Type, Label = Blank(w.Label), Entity = w.Entity!.Trim(), Report = w.Report!.Trim(), Wide = w.Wide };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Spec 042: tableros personales. Datos de su dueño: nadie más los cambia, borra ni comparte (404 igual que inexistente); se
/// abren por el dueño o por quien tiene un rol con el que se compartió (revalidado). Cada tarjeta corre con **quien ve**.
/// Cambiar, borrar y compartir llevan la versión que se abrió; cada cambio queda en la bitácora en la misma transacción.
/// </summary>
public sealed class PersonalDashboardService(Db db, MetadataStore store, ReportService reports)
{
    public const string ShareTable = "meta.PersonalDashboardShare";
    public const string ShareKey = "DashboardId";
    public const int MaxDefinition = PersonalReports.MaxDefinition;
    private const string AuditEntity = "PersonalDashboard";

    /// <summary>Por propiedades (no por constructor): cada consulta trae solo las columnas que usa.</summary>
    private sealed class Row
    {
        public int Id { get; init; }
        public int UserId { get; init; }
        public string Label { get; init; } = "";
        public string? Description { get; init; }
        public string DefinitionJson { get; init; } = "";
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public string Owner { get; init; } = "";
        public bool Shared { get; init; }
    }
    private sealed record ReportRow(int Id, string EntityName, string Label, string DefinitionJson);

    public async Task<PersonalDashboardList> ListAsync(UserContext user, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var mine = await cn.QueryAsync<Row>($"""
            SELECT Id, UserId, Label, Description, DefinitionJson, Version, CreatedAt, UpdatedAt,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM {ShareTable} s WHERE s.{ShareKey} = d.Id) THEN 1 ELSE 0 END AS bit) AS Shared
              FROM meta.PersonalDashboard d WHERE UserId = @uid ORDER BY Label
            """, new { uid = user.Id });
        var shared = (await cn.QueryAsync<Row>($"""
            SELECT d.Id, d.UserId, d.Label, d.Description, d.DefinitionJson, d.Version, d.CreatedAt, d.UpdatedAt, u.DisplayName AS Owner
              FROM meta.PersonalDashboard d JOIN meta.[User] u ON u.Id = d.UserId
             WHERE d.UserId <> @uid AND {PersonalSharing.Visible(ShareTable, ShareKey, "d")}
             ORDER BY d.Label, u.DisplayName
            """, new { uid = user.Id, viewerRoles = user.Roles })).ToList();
        var valid = await PersonalSharing.RevalidateAsync(ShareTable, ShareKey, shared.Select(d => (d.Id, d.UserId)).ToList(), user.Roles, cn);
        shared = shared.Where(d => valid.Contains(d.Id)).ToList();
        return new PersonalDashboardList(
            mine.Select(d => new PersonalDashboardSummary(d.Id, d.Label, d.Description, d.Version, d.Shared, d.UpdatedAt ?? d.CreatedAt)).ToList(),
            shared.Select(d => new SharedDashboardSummary(d.Id, d.Label, d.Description, d.Owner, d.UpdatedAt ?? d.CreatedAt)).ToList());
    }

    /// <summary>Abre el tablero y corre cada tarjeta con quien lo ve (las que no corren: al dueño con su motivo; a otro, se omiten).</summary>
    public async Task<PersonalDashboardResult> GetAsync(int id, UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        Row d;
        Dictionary<int, CardReport> cardReports;
        HashSet<int> openable;
        IReadOnlyList<string>? roles = null, shareable = null;
        await using (var cn = await db.OpenAsync(ct))
        {
            d = await cn.QuerySingleOrDefaultAsync<Row>($"""
                SELECT d.Id, d.UserId, d.Label, d.Description, d.DefinitionJson, d.Version, d.CreatedAt, d.UpdatedAt, u.DisplayName AS Owner
                  FROM meta.PersonalDashboard d JOIN meta.[User] u ON u.Id = d.UserId
                 WHERE d.Id = @id AND (d.UserId = @uid OR {PersonalSharing.Visible(ShareTable, ShareKey, "d")})
                """, new { id, uid = user.Id, viewerRoles = user.Roles }) ?? throw NotFound();
            if (d.UserId != user.Id && !(await PersonalSharing.RevalidateAsync(ShareTable, ShareKey, [(d.Id, d.UserId)], user.Roles, cn)).Contains(d.Id))
                throw NotFound();
            var widgets0 = PersonalDashboards.Parse(d.DefinitionJson);
            cardReports = await CardReportsAsync(cn, null, d.UserId, widgets0);
            var ids = cardReports.Keys.ToList();
            // Un reporte personal de una tarjeta se puede abrir si es de quien ve o si también se le compartió.
            openable = d.UserId == user.Id ? ids.ToHashSet() : await PersonalSharing.RevalidateAsync(
                PersonalReportService.ShareTable, PersonalReportService.ShareKey,
                (await cn.QueryAsync<(int Id, int UserId)>($"""
                    SELECT p.Id, p.UserId FROM meta.PersonalReport p
                     WHERE p.Id IN @ids AND {PersonalSharing.Visible(PersonalReportService.ShareTable, PersonalReportService.ShareKey, "p")}
                    """, new { ids, viewerRoles = user.Roles })).ToList(), user.Roles, cn);
            if (d.UserId == user.Id)
            {
                roles = await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, d.Id, cn);
                shareable = await PersonalSharing.ShareableAsync(user, cn);
            }
        }
        var owned = d.UserId == user.Id;
        var widgets = PersonalDashboards.Parse(d.DefinitionJson);
        var cards = new List<PersonalCard>();
        foreach (var w in widgets)
        {
            var problems = new List<string>();
            var resolved = PersonalDashboards.Resolve(w, user, model, cardReports, problems);
            if (resolved is not var (e, r, filters))
            {
                if (!owned) continue; // spec 042 (decisión 7): a quien lo ve compartido no se le nombra lo que no ve
                var fallback = w.PersonalReport is { } pid && cardReports.TryGetValue(pid, out var pr) ? pr.Label : w.Report ?? "";
                cards.Add(new PersonalCard(w.Type ?? "table", w.Label ?? fallback, w.Wide, w.Entity, w.Report, w.PersonalReport, null, false, problems, null));
                continue;
            }
            var result = await reports.RunAsync(e, r, filters, user, model, ct, compare: w.Type == "kpi");
            var link = w.PersonalReport is { } p
                ? (openable.Contains(p) ? $"/explore/{p}" : null)
                : $"/reports/{Uri.EscapeDataString(e.Name)}/{Uri.EscapeDataString(r.Name)}";
            cards.Add(new PersonalCard(w.Type!, w.Label ?? r.Label, w.Wide, w.PersonalReport is null ? e.Name : null, w.PersonalReport is null ? r.Name : null,
                w.PersonalReport is { } q && openable.Contains(q) ? q : null, link, true, [], result));
        }
        return new PersonalDashboardResult(d.Id, d.Label, d.Description, d.Version, owned, d.Owner, roles, shareable, owned ? widgets : null, cards);
    }

    public async Task<PersonalDashboardResult> SaveAsync(int? id, SavePersonalDashboardRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var model = await store.GetAsync(ct);
        var label = req.Label?.Trim() ?? "";
        var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        var errors = new Dictionary<string, string[]>();
        if (label.Length is 0 or > InterfaceRules.MaxLabel) errors["label"] = [$"Escribe un nombre de 1 a {InterfaceRules.MaxLabel} caracteres."];
        if (description?.Length > InterfaceRules.MaxDescription) errors["description"] = [$"La descripción excede {InterfaceRules.MaxDescription} caracteres."];

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Un candado por dueño hasta el commit (como la 041): altas simultáneas no ven el mismo conteo ni el mismo «no hay ese nombre».
        var locked = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, new { res = $"erp-personal-dashboard:{user.Id}" }, tx);
        if (locked < 0) throw new Conflict("Hay otro cambio tuyo a tus tableros en curso. Intenta de nuevo.");
        Row? prev = null;
        if (id is not null)
        {
            prev = await cn.QuerySingleOrDefaultAsync<Row>(
                "SELECT Id, UserId, Label, Description, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalDashboard WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
                new { id, uid = user.Id }, tx) ?? throw NotFound();
            // La versión se comprueba antes de validar (041 F-F): una versión vieja responde 409 aunque lo demás no valga.
            if (req.Version is null || prev.Version != req.Version) throw Stale();
        }
        var widgets = (req.Widgets ?? []).ToList();
        var problems = PersonalDashboards.Validate(req.Widgets, user, model,
            await CardReportsAsync(cn, tx, user.Id, widgets.OfType<PersonalWidget>()));
        if (problems.Count > 0) errors["widgets"] = problems.Distinct().ToArray();
        if (errors.Count > 0) throw new ValidationFailed(errors, "El tablero no es válido.");
        var def = PersonalDashboards.Serialize(widgets.Select(w => PersonalDashboards.Normalize(w!)).ToList());
        if (def.Length > MaxDefinition)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["widgets"] = [$"La definición excede {MaxDefinition} caracteres."] });
        var p = new { uid = user.Id, label, description, def, id, v = req.Version };
        if (await cn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM meta.PersonalDashboard WHERE UserId = @uid AND Label = @label AND (@id IS NULL OR Id <> @id)", p, tx) is not null)
            throw DuplicateLabel();
        int version;
        try
        {
            if (prev is null)
            {
                var count = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.PersonalDashboard WHERE UserId = @uid", p, tx);
                if (count >= PersonalDashboards.MaxPerUser)
                    throw new ValidationFailed(new Dictionary<string, string[]> { ["label"] = [$"Máximo {PersonalDashboards.MaxPerUser} tableros personales por persona."] });
                id = await cn.ExecuteScalarAsync<int>("""
                    INSERT meta.PersonalDashboard (UserId, Label, Description, DefinitionJson) OUTPUT inserted.Id VALUES (@uid, @label, @description, @def)
                    """, p, tx);
                version = 1;
            }
            else
            {
                await cn.ExecuteAsync("""
                    UPDATE meta.PersonalDashboard SET Label = @label, Description = @description, DefinitionJson = @def, Version = Version + 1,
                           UpdatedAt = SYSUTCDATETIME() WHERE Id = @id AND UserId = @uid AND Version = @v
                    """, p, tx);
                version = prev.Version + 1;
            }
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { throw DuplicateLabel(); } // respaldo: UNIQUE (UserId, Label)
        var roles = prev is null ? [] : await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, id!.Value, cn, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalDashboardChange, AuditEntity, id.ToString(),
            prev is null ? null : Trail(prev.Label, prev.DefinitionJson, roles), Trail(label, def, roles));
        await tx.CommitAsync(ct);
        return new PersonalDashboardResult(id!.Value, label, description, version, true, user.DisplayName, roles, null,
            PersonalDashboards.Parse(def), []);
    }

    public async Task DeleteAsync(int id, int? version, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var prev = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, UserId, Label, Description, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalDashboard WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
            new { id, uid = user.Id }, tx) ?? throw NotFound();
        if (version is null || prev.Version != version) throw Stale();
        var roles = await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, id, cn, tx);
        await cn.ExecuteAsync("DELETE FROM meta.PersonalDashboard WHERE Id = @id AND UserId = @uid", new { id, uid = user.Id }, tx); // el reparto, en cascada
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalDashboardChange, AuditEntity, id.ToString(),
            before: Trail(prev.Label, prev.DefinitionJson, roles));
        await tx.CommitAsync(ct);
    }

    /// <summary>Spec 042 (H3 CA1): comparte con los roles pedidos (solo roles que tiene) con la versión que se abrió.</summary>
    public async Task<ShareResult> ShareAsync(int id, ShareRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var prev = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, UserId, Label, Description, DefinitionJson, Version, CreatedAt, UpdatedAt FROM meta.PersonalDashboard WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid",
            new { id, uid = user.Id }, tx) ?? throw NotFound();
        if (req.Version is null || prev.Version != req.Version) throw Stale();
        var roles = await PersonalSharing.ResolveAsync(req.Roles, user, cn, tx);
        var before = await PersonalSharing.RolesOfAsync(ShareTable, ShareKey, id, cn, tx);
        await PersonalSharing.ReplaceAsync(ShareTable, ShareKey, id, roles, cn, tx);
        await cn.ExecuteAsync("UPDATE meta.PersonalDashboard SET Version = Version + 1, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
        var after = roles.Select(x => x.Name).Order(StringComparer.CurrentCulture).ToList();
        await Audit.WriteAsync(cn, tx, http, AuditActions.PersonalDashboardChange, AuditEntity, id.ToString(),
            Trail(prev.Label, prev.DefinitionJson, before), Trail(prev.Label, prev.DefinitionJson, after));
        await tx.CommitAsync(ct);
        return new ShareResult(id, prev.Version + 1, after);
    }

    /// <summary>Los reportes personales del dueño a los que apuntan las tarjetas (los de otro no cuentan: decisión 6).</summary>
    private static async Task<Dictionary<int, CardReport>> CardReportsAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx,
        int owner, IEnumerable<PersonalWidget> widgets)
    {
        var ids = widgets.Select(w => w.PersonalReport).OfType<int>().Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await cn.QueryAsync<ReportRow>(
            "SELECT Id, EntityName, Label, DefinitionJson FROM meta.PersonalReport WHERE UserId = @owner AND Id IN @ids", new { owner, ids }, tx);
        return rows.ToDictionary(r => r.Id, r => new CardReport(r.Id, r.Label,
            (JsonSerializer.Deserialize<PersonalReportDef>(r.DefinitionJson, MetadataStore.Json) ?? new PersonalReportDef()) with { Entity = r.EntityName }));
    }

    /// <summary>Nombre, huella de las tarjetas y roles: no la definición (como los reportes personales, 041 F-B).</summary>
    private static object Trail(string label, string definition, IReadOnlyList<string> roles) =>
        new { label, definitionSha256 = PersonalReportService.Sha256(definition), roles };

    private static NotFound NotFound() => new("El tablero no existe.");
    private static Conflict Stale() => new("Otra pestaña o sesión cambió este tablero después de que lo abriste. Vuelve a abrirlo para ver la versión actual.");
    private static ValidationFailed DuplicateLabel() =>
        new(new Dictionary<string, string[]> { ["label"] = ["Ya tienes un tablero con ese nombre."] });
}
