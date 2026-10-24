using Erp.Api.Automations;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 016: tareas programadas por entorno y bitácora de automatizaciones. Solo superusuario (lo revisa el servicio).</summary>
public static class AutomationEndpoints
{
    public static void MapAutomations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/automations").RequireAuthorization();
        g.MapGet("/schedules", (HttpContext http, AutomationService svc, CancellationToken ct) => svc.ListSchedulesAsync(http.CurrentUser(), ct));
        g.MapPut("/schedules/{entity}/{name}", (string entity, string name, SetScheduleRequest req, HttpContext http, AutomationService svc, CancellationToken ct) =>
            svc.SetActiveAsync(entity, name, req, http, ct));
        g.MapPost("/schedules/{entity}/{name}/run", async (string entity, string name, HttpContext http, AutomationService svc, CancellationToken ct) =>
            new { runId = await svc.RunNowAsync(entity, name, http, ct) });
        g.MapGet("/runs", (string? kind, string? status, string? entity, string? name, HttpContext http, AutomationService svc, CancellationToken ct) =>
            svc.RunsAsync(kind, status, entity, name, http.CurrentUser(), ct));
        g.MapGet("/runs/{id:long}", async (long id, HttpContext http, AutomationService svc, CancellationToken ct) =>
        {
            var d = await svc.RunAsync(id, http.CurrentUser(), ct);
            return new { run = d.Run, errors = d.Errors };
        });
        g.MapPost("/runs/{id:long}/retry", async (long id, HttpContext http, AutomationService svc, CancellationToken ct) =>
        {
            await svc.RetryAsync(id, http, ct);
            return Results.NoContent();
        });
    }
}
