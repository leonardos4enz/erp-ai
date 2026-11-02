using Erp.Api.Companion;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 046: el Acompañante. Para cualquier persona (no cuentas de servicio): sus conversaciones y las acciones que el agente
/// le propone. Confirmar una acción es la única ruta que escribe datos, y lo hace por la ruta de siempre (ADR-0010).
/// </summary>
public static class CompanionEndpoints
{
    public static void MapCompanion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/companion").RequireAuthorization();
        g.MapGet("/", (HttpContext http, CompanionService svc, CancellationToken ct) => svc.StatusAsync(http.CurrentUser(), ct)).Requires(Capability.Authenticated);
        g.MapGet("/conversations", (HttpContext http, CompanionService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Authenticated);
        g.MapPost("/conversations", async (CompanionMessageBody body, HttpContext http, CompanionService svc, CancellationToken ct) =>
            Results.Accepted("/api/companion/conversations", await svc.StartAsync(body, http, ct)))
            .Requires(Capability.Authenticated).Writes(Effect.Session)
            .Costly("un agente que llama al modelo de lenguaje por pasos", ownLimit: "Companion:ConversationsPerDay, Companion:CostPerDayUsd y una conversación activa por persona");
        g.MapGet("/conversations/{id:int}", (int id, int? after, HttpContext http, CompanionService svc, CancellationToken ct) =>
            svc.GetAsync(id, after, http.CurrentUser(), ct)).Requires(Capability.Authenticated);
        g.MapPost("/conversations/{id:int}/messages", async (int id, CompanionMessageBody body, HttpContext http, CompanionService svc, CancellationToken ct) =>
            Results.Accepted($"/api/companion/conversations/{id}", await svc.SendAsync(id, body, http, ct)))
            .Requires(Capability.Authenticated).Writes(Effect.Session)
            .Costly("otro turno del agente, que llama al modelo de lenguaje por pasos", ownLimit: "Companion:MaxTurns, los topes de la conversación, Companion:CostPerDayUsd y una conversación activa por persona");
        g.MapGet("/conversations/{id:int}/stream", (int id, int? after, HttpContext http, CompanionStream stream, CancellationToken ct) =>
            stream.RunAsync(id, after, http, ct))
            .Requires(Capability.Authenticated)
            .Costly("una conexión abierta (SSE) que sigue la conversación", ownLimit: "Companion:MaxStreamsPerUser y Companion:StreamSeconds");
        g.MapPost("/conversations/{id:int}/cancel", (int id, HttpContext http, CompanionService svc, CancellationToken ct) =>
            svc.CancelAsync(id, http, ct)).Requires(Capability.Authenticated).Writes(Effect.Session);
        g.MapDelete("/conversations/{id:int}", async (int id, HttpContext http, CompanionService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Authenticated).Writes(Effect.Session);
        // H4: la acción la ejecuta la ruta de datos despachada con la identidad de quien confirma; aquí solo se exige sesión de
        // registros y la huella de lo que vio.
        g.MapPost("/actions/{id:int}/confirm", async (int id, HttpContext http, CompanionActions actions, CancellationToken ct) =>
        {
            var body = http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<CompanionConfirmBody>(ct) : null;
            var (action, record) = await actions.ConfirmAsync(id, body, http, ct);
            return new { action, record };
        }).Requires(Capability.Records).Writes(Effect.Data).AppliesReviewed("hash");
        // Spec 048 (H1 CA2): «No» o «No, y dile qué cambiar» (`reason`, opcional); decidir la última pendiente sigue el turno.
        g.MapPost("/actions/{id:int}/discard", async (int id, HttpContext http, CompanionActions actions, CancellationToken ct) =>
        {
            var body = http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<CompanionDiscardBody>(ct) : null;
            return await actions.DiscardAsync(id, body, http, ct);
        }).Requires(Capability.Authenticated).Writes(Effect.Session);
    }
}
