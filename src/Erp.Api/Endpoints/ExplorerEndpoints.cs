using System.Text;
using Dapper;
using Erp.Api.Explorer;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 025: explorador del modelo y de la base de datos. Solo lectura (solo <c>GET</c>) y solo para quien administra el
/// modelo; el esquema <c>meta</c>, solo superusuario. Nunca lee filas de negocio ni conteos.
/// </summary>
public static class ExplorerEndpoints
{
    public const int MaxTables = 500;

    public sealed record EntityCard(EntityDef Entity, AppRef App, string Table, bool TableExists, IReadOnlyList<ModelExplorer.PackageRef> Packages,
        IReadOnlyList<DashboardRef> Dashboards, IReadOnlyList<RelEdge> ReferencedBy, IReadOnlyList<RoleAccess> Roles);
    public sealed record DashboardRef(string Name, string Label);

    public static void MapExplorer(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/explorer").RequireAuthorization();

        g.MapGet("/database", async (HttpContext http, Db db, MetadataStore store, string? schema, string? app, string? q, CancellationToken ct) =>
            await DatabaseAsync(http, db, store, schema, app, q, ct)).Requires(Capability.Modeler);

        g.MapGet("/database/ddl", async (HttpContext http, Db db, MetadataStore store, string? schema, string? app, string? q, string? tables, CancellationToken ct) =>
        {
            var names = (tables ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length > MaxTables) throw new AppException(400, $"Pide como máximo {MaxTables} tablas.");
            var result = await DatabaseAsync(http, db, store, schema, app, q, ct);
            var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chosen = wanted.Count == 0 ? result.Tables : result.Tables.Where(t => wanted.Contains(t.Name)).ToList();
            var script = DdlScript.For(result.Schema, chosen, DateTime.UtcNow);
            return Results.File(Encoding.UTF8.GetBytes(script), "text/plain; charset=utf-8", $"esquema-{result.Schema}.sql");
        }).Requires(Capability.Modeler);

        g.MapGet("/relations", async (HttpContext http, MetadataStore store, string? app, string? q, CancellationToken ct) =>
        {
            CheckQuery(q);
            return ModelExplorer.Relations(await store.GetAsync(ct), await store.GetAppsAsync(ct), Blank(app), q);
        }).Requires(Capability.Modeler);

        g.MapGet("/entities/{name}", async (string name, HttpContext http, Db db, MetadataStore store, ModuleService modules, CancellationToken ct) =>
        {
            var model = await store.GetAsync(ct);
            if (!model.TryGetValue(name, out var e)) throw new NotFound("El catálogo no existe.");
            var apps = await store.GetAppsAsync(ct);
            var dashboards = await store.GetDashboardsAsync(ct);
            await using var cn = await db.OpenAsync(ct);
            var exists = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
                SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'app' AND t.name = @name
                """, new { name = e.Name }, cancellationToken: ct)) is not null;
            var installed = (await cn.QueryAsync<(string Id, string Version)>("SELECT Id, Version FROM meta.Module"))
                .ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase);
            var security = await SecurityAsync(cn, model, apps, ct);
            var appName = PhysicalModel.AppOf(e, model);
            var appRef = PhysicalModel.AppRefs(model, apps).FirstOrDefault(a => string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase))
                         ?? new AppRef(appName, appName, Apps.ImplicitIcon, Apps.DefaultOrder);
            var root = e.IsChild ? e.ParentEntity! : e.Name;
            return new EntityCard(e with { Version = ModelVersion.Of(e) }, appRef, $"app.{e.Name}", exists,
                ModelExplorer.Packages(e.Name, modules.Packages(), installed),
                dashboards.Values.Where(d => !d.IsArchived && d.Widgets.Any(w => string.Equals(w.Entity, e.Name, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(d => d.Label).Select(d => new DashboardRef(d.Name, d.Label)).ToList(),
                ModelExplorer.Relations(model, apps).Edges.Where(x => string.Equals(x.To, e.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
                security.Cells.Where(c => string.Equals(c.Entity, root, StringComparison.OrdinalIgnoreCase)).ToList());
        }).Requires(Capability.Modeler);

        g.MapGet("/usages", async (HttpContext http, MetadataStore store, string? entity, string? field, CancellationToken ct) =>
        {
            var model = await store.GetAsync(ct);
            if (!model.TryGetValue(entity ?? "", out var e)) throw new NotFound("El catálogo no existe.");
            var f = Blank(field) is { } fn ? e.FindField(fn) ?? throw new NotFound("El campo no existe.") : null;
            return UsageIndex.For(model, await store.GetDashboardsAsync(ct)).Of(e.Name, f?.Name);
        }).Requires(Capability.Modeler);

        g.MapGet("/automations", async (HttpContext http, MetadataStore store, CancellationToken ct) =>
        {
            return ModelExplorer.Flow(await store.GetAsync(ct), await store.GetAppsAsync(ct));
        }).Requires(Capability.Modeler);

        g.MapGet("/security", async (HttpContext http, Db db, MetadataStore store, CancellationToken ct) =>
        {
            await using var cn = await db.OpenAsync(ct);
            return await SecurityAsync(cn, await store.GetAsync(ct), await store.GetAppsAsync(ct), ct);
        }).Requires(Capability.Modeler);

        g.MapGet("/navigation", async (HttpContext http, MetadataStore store, CancellationToken ct) =>
        {
            return ModelExplorer.Navigation(await store.GetAsync(ct), await store.GetAppsAsync(ct));
        }).Requires(Capability.Modeler);

        // Spec 033: inventario de rutas en vivo y registro de riesgos (herramientas del agente de ERP AI Studio).
        g.MapGet("/routes", (EndpointDataSource endpoints, string? q) =>
        {
            if (q is { Length: > ApiInventory.MaxQuery }) throw new AppException(400, $"El texto a buscar admite como máximo {ApiInventory.MaxQuery} caracteres.");
            return ApiInventory.Routes(endpoints, Blank(q));
        }).Requires(Capability.Modeler);

        g.MapGet("/risks", (string? q) =>
        {
            if (q is { Length: > ApiInventory.MaxQuery }) throw new AppException(400, $"El texto a buscar admite como máximo {ApiInventory.MaxQuery} caracteres.");
            return ApiInventory.Risks(Blank(q));
        }).Requires(Capability.Modeler);
    }

    private static async Task<DbSchema> DatabaseAsync(HttpContext http, Db db, MetadataStore store, string? schema, string? app, string? q, CancellationToken ct)
    {
        var u = http.CurrentUser();
        CheckQuery(q);
        // Constitución II.1: el esquema que llega a SQL es el de la lista blanca, nunca el texto de la petición.
        var allowed = DbCatalog.Allowed(Blank(schema), u.IsSuperuser);
        if (allowed is null)
        {
            if (string.Equals(schema, DbCatalog.Meta, StringComparison.OrdinalIgnoreCase)) throw new Forbidden("Solo un superusuario ve las tablas del sistema.");
            throw new AppException(400, "Esquema no válido: usa app o meta.");
        }
        var (model, apps) = (await store.GetAsync(ct), await store.GetAppsAsync(ct));
        await using var cn = await db.OpenAsync(ct);
        var catalog = await DbCatalog.ReadAsync(cn, allowed, ct);
        // Las tablas del sistema no pertenecen a un módulo: el filtro por módulo solo aplica a app.
        return PhysicalModel.Build(allowed, catalog, model, apps, allowed == DbCatalog.App ? Blank(app) : null, q);
    }

    private static async Task<SecurityMatrix> SecurityAsync(System.Data.Common.DbConnection cn, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlyDictionary<string, AppDef> apps, CancellationToken ct)
    {
        var roles = (await cn.QueryAsync<RoleInfo>(new CommandDefinition("SELECT Id, Name, Description, IsSuperuser FROM meta.Role", cancellationToken: ct))).ToList();
        var perms = (await cn.QueryAsync<PermWithRole>(new CommandDefinition(
            "SELECT RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete FROM meta.EntityPermission", cancellationToken: ct)))
            .Select(p => (p.RoleId, new PermRow(p.EntityName, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete))).ToList();
        var edges = (await cn.QueryAsync<(int RoleId, int ParentRoleId)>(new CommandDefinition(
            "SELECT RoleId, ParentRoleId FROM meta.RoleInherit", cancellationToken: ct))).ToList();
        return ModelExplorer.Security(model, roles, perms, edges, apps);
    }

    private sealed record PermWithRole(int RoleId, string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);

    private static void CheckQuery(string? q)
    {
        if (q is { Length: > PhysicalModel.MaxQuery }) throw new AppException(400, $"El texto a buscar admite como máximo {PhysicalModel.MaxQuery} caracteres.");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
