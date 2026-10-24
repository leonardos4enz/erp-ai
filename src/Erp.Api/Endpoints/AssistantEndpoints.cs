using Erp.Api.Assistant;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 011: asistente IA. Solo modeladores (lo revisa el servicio); el asistente propone y la persona aplica.</summary>
public static class AssistantEndpoints
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assistant").RequireAuthorization();
        g.MapGet("/", (HttpContext http, AssistantService svc, CancellationToken ct) => svc.StatusAsync(http.CurrentUser(), ct));
        g.MapPut("/key", (AssistantKeyBody body, HttpContext http, AssistantService svc, CancellationToken ct) => svc.SetKeyAsync(body, http, ct));
        g.MapDelete("/key", (HttpContext http, AssistantService svc, CancellationToken ct) => svc.ClearKeyAsync(http, ct));
        g.MapGet("/conversations", (HttpContext http, AssistantService svc, CancellationToken ct) => svc.ConversationsAsync(http.CurrentUser(), ct));
        g.MapGet("/conversations/{id:int}", (int id, HttpContext http, AssistantService svc, CancellationToken ct) =>
            svc.ConversationAsync(id, http.CurrentUser(), ct));
        g.MapPost("/requests", (AssistantRequestBody body, HttpContext http, AssistantService svc, CancellationToken ct) =>
            svc.RequestAsync(body, http, ct));
        g.MapPost("/requests/{id:int}/apply", async (int id, HttpContext http, AssistantService svc, CancellationToken ct) =>
        {
            // Spec 012: la huella viaja en el cuerpo, pero el cuerpo es opcional para responder 400/409 con su motivo (como
            // parámetro, una petición sin JSON ni siquiera encontraba la ruta).
            var body = http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<AssistantApplyBody>(ct) : null;
            var (changeSetId, proposal) = await svc.ApplyAsync(id, body, http, ct);
            return new { changeSetId, proposal };
        });
        g.MapPost("/requests/{id:int}/discard", (int id, HttpContext http, AssistantService svc, CancellationToken ct) =>
            svc.DiscardAsync(id, http, ct));
    }
}
