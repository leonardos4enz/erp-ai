using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Endpoints;

/// <summary>Spec 033: ERP AI Studio. Solo modeladores; el agente trabaja en segundo plano y la persona aplica su propuesta.</summary>
public static class StudioEndpoints
{
    public static void MapStudio(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/studio").RequireAuthorization();
        g.MapGet("/", (HttpContext http, StudioService svc, CancellationToken ct) => svc.StatusAsync(http.CurrentUser(), ct)).Requires(Capability.Modeler);
        g.MapGet("/tools", (StudioService svc) => svc.Tools()).Requires(Capability.Modeler);
        g.MapGet("/sessions", (HttpContext http, StudioService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Modeler);
        g.MapGet("/sessions/{id:int}", (int id, int? after, HttpContext http, StudioService svc, CancellationToken ct) =>
            svc.GetAsync(id, after, http.CurrentUser(), ct)).Requires(Capability.Modeler);
        g.MapPost("/sessions", async (StudioStartBody body, HttpContext http, StudioService svc, CancellationToken ct) =>
            Results.Accepted($"/api/studio/sessions", await svc.StartAsync(body, http, ct)))
            .Requires(Capability.Modeler).Writes(Effect.Session)
            .Costly("un agente que llama al modelo de lenguaje por pasos", ownLimit: "Studio:SessionsPerDay, Studio:CostPerDayUsd y una sesión activa por persona");
        g.MapPost("/sessions/{id:int}/cancel", (int id, HttpContext http, StudioService svc, CancellationToken ct) =>
            svc.CancelAsync(id, http, ct)).Requires(Capability.Modeler).Writes(Effect.Session);
        g.MapPost("/sessions/{id:int}/discard", (int id, HttpContext http, StudioService svc, CancellationToken ct) =>
            svc.DiscardAsync(id, http, ct)).Requires(Capability.Modeler).Writes(Effect.Session);
        g.MapPost("/sessions/{id:int}/apply", async (int id, HttpContext http, StudioService svc, CancellationToken ct) =>
        {
            // Como el asistente: el cuerpo es opcional para responder 400/409 con su motivo.
            var body = http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<StudioApplyBody>(ct) : null;
            var (changeSetId, session) = await svc.ApplyAsync(id, body, http, ct);
            return new { changeSetId, session };
        }).Requires(Capability.Modeler).Writes(Effect.Model, Gates.DirectChange).AppliesReviewed("reviewHash");
    }
}
