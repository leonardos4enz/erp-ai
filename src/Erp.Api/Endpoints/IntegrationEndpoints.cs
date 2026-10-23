using Erp.Api.Integrations;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 008: destinos de webhooks, bitácora de entregas y claves de API. Solo superusuario (lo revisan los servicios).</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/admin/webhooks").RequireAuthorization();
        hooks.MapGet("/", (HttpContext http, WebhookService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct));
        hooks.MapGet("/deliveries", (string? entity, string? name, string? status, HttpContext http, WebhookService svc, CancellationToken ct) =>
            svc.DeliveriesAsync(entity, name, status, http.CurrentUser(), ct));
        hooks.MapPost("/deliveries/{id:long}/retry", async (long id, HttpContext http, WebhookService svc, CancellationToken ct) =>
        {
            await svc.RetryAsync(id, http, ct);
            return Results.NoContent();
        });
        hooks.MapPut("/{entity}/{name}", async (string entity, string name, ConfigureWebhookRequest req, HttpContext http, WebhookService svc, CancellationToken ct) =>
            new { secret = await svc.ConfigureAsync(entity, name, req, http, ct) });
        hooks.MapPost("/{entity}/{name}/rotate", async (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
            new { secret = await svc.RotateAsync(entity, name, http, ct) });
        hooks.MapPost("/{entity}/{name}/test", (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
            svc.TestAsync(entity, name, http, ct));
        hooks.MapDelete("/{entity}/{name}", async (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(entity, name, http, ct);
            return Results.NoContent();
        });

        var keys = app.MapGroup("/api/admin/api-keys").RequireAuthorization();
        keys.MapGet("/", (HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct));
        keys.MapPost("/", (CreateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.CreateAsync(req, http, ct));
        keys.MapPut("/{id:int}", (int id, UpdateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.UpdateAsync(id, req, http, ct));
        keys.MapPost("/{id:int}/rotate", (int id, RotateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.RotateAsync(id, req, http, ct));
        keys.MapPost("/{id:int}/revoke", async (int id, HttpContext http, ApiKeyService svc, CancellationToken ct) =>
        {
            await svc.RevokeAsync(id, http, ct);
            return Results.NoContent();
        });
    }
}
