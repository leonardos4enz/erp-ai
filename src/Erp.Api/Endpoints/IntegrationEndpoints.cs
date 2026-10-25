using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 008: destinos de webhooks, bitácora de entregas y claves de API. Solo superusuario (lo revisan los servicios).</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/admin/webhooks").RequireAuthorization();
        hooks.MapGet("/", (HttpContext http, WebhookService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        hooks.MapGet("/deliveries", (string? entity, string? name, string? status, HttpContext http, WebhookService svc, CancellationToken ct) =>
            svc.DeliveriesAsync(entity, name, status, http.CurrentUser(), ct)).Requires(Capability.Superuser);
        hooks.MapPost("/deliveries/{id:long}/retry", async (long id, HttpContext http, WebhookService svc, CancellationToken ct) =>
        {
            await svc.RetryAsync(id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Superuser).Writes(Effect.Config);
        hooks.MapPut("/{entity}/{name}", async (string entity, string name, ConfigureWebhookRequest req, HttpContext http, WebhookService svc, CancellationToken ct) =>
            new { secret = await svc.ConfigureAsync(entity, name, req, http, ct) }).Requires(Capability.Superuser).Writes(Effect.Config);
        hooks.MapPost("/{entity}/{name}/rotate", async (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
            new { secret = await svc.RotateAsync(entity, name, http, ct) }).Requires(Capability.Superuser).Writes(Effect.Config);
        hooks.MapPost("/{entity}/{name}/test", (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
            svc.TestAsync(entity, name, http, ct)).Requires(Capability.Superuser).Writes(Effect.Config);
        hooks.MapDelete("/{entity}/{name}", async (string entity, string name, HttpContext http, WebhookService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(entity, name, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Superuser).Writes(Effect.Config);

        var keys = app.MapGroup("/api/admin/api-keys").RequireAuthorization();
        keys.MapGet("/", (HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        keys.MapPost("/", (CreateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.CreateAsync(req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.SecurityApproval);
        keys.MapPut("/{id:int}", (int id, UpdateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.UpdateAsync(id, req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.SecurityApproval);
        keys.MapPost("/{id:int}/rotate", (int id, RotateApiKeyRequest req, HttpContext http, ApiKeyService svc, CancellationToken ct) => svc.RotateAsync(id, req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.NoGrant);
        keys.MapPost("/{id:int}/revoke", async (int id, HttpContext http, ApiKeyService svc, CancellationToken ct) =>
        {
            await svc.RevokeAsync(id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Superuser).Writes(Effect.Security, Gates.NoGrant);

        // Spec 018: clientes OAuth (client credentials).
        var clients = app.MapGroup("/api/admin/oauth-clients").RequireAuthorization();
        clients.MapGet("/", (HttpContext http, OAuthClientService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        clients.MapGet("/endpoints", (HttpContext http) =>
        {
            var origin = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
            return new { token = $"{origin}/oauth/token", odata = $"{origin}/odata/" };
        }).Requires(Capability.Superuser);
        clients.MapPost("/", (SaveOAuthClientRequest req, HttpContext http, OAuthClientService svc, CancellationToken ct) => svc.CreateAsync(req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.SecurityApproval);
        clients.MapPut("/{id:int}", (int id, SaveOAuthClientRequest req, HttpContext http, OAuthClientService svc, CancellationToken ct) => svc.UpdateAsync(id, req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.SecurityApproval);
        clients.MapPost("/{id:int}/rotate", (int id, RotateOAuthClientRequest req, HttpContext http, OAuthClientService svc, CancellationToken ct) => svc.RotateAsync(id, req, http, ct))
            .Requires(Capability.Superuser).Writes(Effect.Security, Gates.NoGrant);
        clients.MapPost("/{id:int}/revoke", async (int id, HttpContext http, OAuthClientService svc, CancellationToken ct) =>
        {
            await svc.RevokeAsync(id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Superuser).Writes(Effect.Security, Gates.NoGrant);
    }

    /// <summary>Spec 018 (H2): servidor de tokens. Anónimo (el cliente se autentica en la petición) y con límite por dirección.</summary>
    public static void MapOAuthToken(this IEndpointRouteBuilder app)
    {
        app.MapPost("/oauth/token", (HttpContext http, OAuthTokenService svc, CancellationToken ct) => svc.IssueAsync(http, ct))
            .Requires(Capability.Anonymous).Writes(Effect.Session).Costly("valida el secreto del cliente").RequireRateLimiting("oauth").DisableAntiforgery();
        app.MapMethods("/oauth/token", ["GET", "PUT", "PATCH", "DELETE"], (HttpContext http) =>
            OAuthTokenService.Error(http, 405, "invalid_request", "El servidor de tokens solo acepta POST.")).Requires(Capability.Anonymous).ReadsOnly();
    }
}
