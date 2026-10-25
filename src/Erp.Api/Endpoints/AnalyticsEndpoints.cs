using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 007: vistas guardadas, reportes y tableros. Toda la seguridad se aplica en los servicios.</summary>
public static class AnalyticsEndpoints
{
    /// <param name="Apps">Spec 024: módulos de los catálogos de sus tarjetas visibles para el usuario.</param>
    public sealed record DashboardSummary(string Name, string Label, string Icon, string? Description, IReadOnlyList<string> Apps);
    public sealed record WidgetResult(string Type, string Label, bool Wide, string Entity, string Report, int? Measure, ReportResult Result);
    public sealed record DashboardResult(string Name, string Label, string Icon, string? Description, IReadOnlyList<WidgetResult> Widgets,
        DashboardDef? Definition);

    public static void MapAnalytics(this IEndpointRouteBuilder app)
    {
        var views = app.MapGroup("/api/views/{entity}").RequireAuthorization();
        views.MapGet("/", (string entity, HttpContext http, ViewService svc, CancellationToken ct) => svc.ListAsync(entity, http.CurrentUser(), ct)).Requires(Capability.Records);
        views.MapPost("/", (string entity, SaveViewRequest req, HttpContext http, ViewService svc, CancellationToken ct) => svc.SaveAsync(entity, null, req, http, ct))
            .Requires(Capability.Records).Writes(Effect.Session);
        views.MapPut("/{id:int}", (string entity, int id, SaveViewRequest req, HttpContext http, ViewService svc, CancellationToken ct) => svc.SaveAsync(entity, id, req, http, ct))
            .Requires(Capability.Records).Writes(Effect.Session);
        views.MapDelete("/{id:int}", async (string entity, int id, HttpContext http, ViewService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(entity, id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Records).Writes(Effect.Session);

        var reports = app.MapGroup("/api/reports").RequireAuthorization();
        reports.MapGet("/", (HttpContext http, ReportService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Records);
        reports.MapGet("/{entity}/{name}", (string entity, string name, string? filters, HttpContext http, ReportService svc, CancellationToken ct) =>
            svc.RunAsync(entity, name, FilterSql.Parse(filters), http.CurrentUser(), ct)).Requires(Capability.Records)
            .Costly("agrega el catálogo completo").RequireRateLimiting(RatePolicies.Aggregate);
        reports.MapGet("/{entity}/{name}/export", async (string entity, string name, string? format, string? filters,
            HttpContext http, ReportService svc, CancellationToken ct) =>
        {
            var r = await svc.RunAsync(entity, name, FilterSql.Parse(filters), http.CurrentUser(), ct);
            // El nombre del archivo sale de los metadatos (identificador validado), nunca de la petición.
            var file = $"{r.Entity}-{r.Name}-{DateTime.Now:yyyyMMdd}";
            return format?.ToLowerInvariant() switch
            {
                "xlsx" => Results.File(ReportExport.Xlsx(r), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx"),
                "csv" or null => Results.File(ReportExport.Csv(r), "text/csv; charset=utf-8", file + ".csv"),
                _ => throw new AppException(400, "Formato no válido (csv o xlsx)."),
            };
        }).Requires(Capability.Records).Costly("hasta 1000 grupos en un archivo").RequireRateLimiting(RatePolicies.Bulk);

        var boards = app.MapGroup("/api/dashboards").RequireAuthorization();
        boards.MapGet("/", async (HttpContext http, MetadataStore store, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var model = await store.GetAsync(ct);
            return (await store.GetDashboardsAsync(ct)).Values
                .Where(d => Visible(d, u, model))
                .OrderBy(d => d.Label, StringComparer.CurrentCulture)
                .Select(d => new DashboardSummary(d.Name, d.Label, d.Icon ?? "layout-dashboard", d.Description, AppsOf(d, u, model)))
                .ToList();
        }).Requires(Capability.Records);
        boards.MapGet("/{name}", async (string name, HttpContext http, MetadataStore store, ReportService svc, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var model = await store.GetAsync(ct);
            if (!(await store.GetDashboardsAsync(ct)).TryGetValue(name, out var d) || !Visible(d, u, model))
                throw new NotFound("El tablero no existe.");
            var widgets = new List<WidgetResult>();
            foreach (var w in d.Widgets)
            {
                if (Widget(w, u, model) is not var (e, r)) continue;
                var result = await svc.RunAsync(e, r, [], u, model, ct);
                widgets.Add(new WidgetResult(w.Type, w.Label ?? r.Label, w.Wide, e.Name, r.Name, w.Measure, result));
            }
            return new DashboardResult(d.Name, d.Label, d.Icon ?? "layout-dashboard", d.Description, widgets, u.CanManageModel ? d : null);
        }).Requires(Capability.Records).Costly("corre un reporte por tarjeta").RequireRateLimiting(RatePolicies.Aggregate); // spec 031
    }

    private static IReadOnlyList<string> AppsOf(DashboardDef d, UserContext u, IReadOnlyDictionary<string, EntityDef> model) =>
        d.Widgets.Select(w => Widget(w, u, model)).OfType<(EntityDef E, ReportDef R)>()
            .Select(x => x.E.IsChild && model.TryGetValue(x.E.ParentEntity!, out var p) ? p : x.E)
            .Select(Apps.Of).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Un tablero se ofrece si el usuario tiene uno de sus roles y puede ver al menos una de sus tarjetas.</summary>
    private static bool Visible(DashboardDef d, UserContext u, IReadOnlyDictionary<string, EntityDef> model) =>
        !d.IsArchived && (u.IsSuperuser || d.Roles is null || u.HasRole(d.Roles)) && d.Widgets.Any(w => Widget(w, u, model) is not null);

    private static (EntityDef, ReportDef)? Widget(DashboardWidget w, UserContext u, IReadOnlyDictionary<string, EntityDef> model) =>
        model.TryGetValue(w.Entity, out var e) && !e.IsArchived &&
        e.Reports?.FirstOrDefault(r => string.Equals(r.Name, w.Report, StringComparison.OrdinalIgnoreCase)) is { } r &&
        ReportService.Available(e, r, u, model) ? (e, r) : null;
}
