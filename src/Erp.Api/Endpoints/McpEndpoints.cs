using Erp.Api.Infrastructure;
using Erp.Api.Mcp;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 038: servidor MCP por persona. Metadatos (RFC 9728, RFC 8414), registro dinámico (RFC 7591), autorización con
/// consentimiento de la persona, revocación (RFC 7009), el propio <c>/mcp</c> (JSON-RPC) y las conexiones de la persona. El
/// canje y la renovación de tokens van por <c>POST /oauth/token</c> (018).
/// </summary>
public static class McpEndpoints
{
    /// <summary>Revisión adversarial F-07: los metadatos salen de la URL pública (o de la petición): ninguna caché los guarda.</summary>
    private static IResult Metadata(HttpContext http, object body)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(body);
    }

    public static void MapMcp(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext http, McpOAuthService svc) => Metadata(http, svc.ProtectedResource(http)))
            .Requires(Capability.Anonymous);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext http, McpOAuthService svc) => Metadata(http, svc.ProtectedResource(http)))
            .Requires(Capability.Anonymous);
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext http, McpOAuthService svc) => Metadata(http, svc.AuthorizationServer(http)))
            .Requires(Capability.Anonymous);

        app.MapPost("/oauth/register", (HttpContext http, McpOAuthService svc, CancellationToken ct) => svc.RegisterAsync(http, ct))
            .Requires(Capability.Anonymous).Writes(Effect.Config).RequireRateLimiting("oauth").DisableAntiforgery();
        // La pantalla de consentimiento es de la SPA (con la sesión de la persona); el servidor solo la lleva ahí con la consulta.
        app.MapGet("/oauth/authorize", (HttpContext http, McpOptions options) => options.Enabled
                ? Results.Redirect("/conectar" + http.Request.QueryString.Value)
                : Results.Problem(title: "Ruta no encontrada.", statusCode: 404))
            .Requires(Capability.Anonymous);
        app.MapPost("/oauth/revoke", (HttpContext http, McpOAuthService svc, CancellationToken ct) => svc.RevokeAsync(http, ct))
            .Requires(Capability.Anonymous).Writes(Effect.Session).RequireRateLimiting("oauth").DisableAntiforgery();

        // Solo el token MCP llega aquí (el selector de esquema ignora la cookie en /mcp: sin CSRF posible).
        app.MapPost("/mcp", (HttpContext http, McpServer server, CancellationToken ct) => server.HandleAsync(http, ct))
            .Requires(Capability.Authenticated).ReadsOnly().Costly("despacha herramientas del catálogo de Studio", "Mcp:Burst y Mcp:CallsPerMinute por persona, más los del despachador")
            .DisableAntiforgery();
        app.MapMethods("/mcp", ["GET", "DELETE"], (McpOptions options) => options.Enabled
                ? Results.Json(new { title = "Este servidor MCP no abre flujos ni sesiones: usa POST.", status = 405 }, statusCode: 405)
                : Results.Problem(title: "Ruta no encontrada.", statusCode: 404))
            .Requires(Capability.Authenticated).ReadsOnly();

        var g = app.MapGroup("/api/mcp").RequireAuthorization();
        g.MapGet("/authorize", (HttpContext http, McpOAuthService svc, CancellationToken ct) => svc.CheckAsync(http, ct))
            .Requires(Capability.Authenticated);
        g.MapPost("/authorize", (McpAuthorizeRequest req, HttpContext http, McpOAuthService svc, CancellationToken ct) => svc.DecideAsync(req, http, ct))
            .Requires(Capability.Authenticated).Writes(Effect.Session);
        g.MapGet("/connections", (HttpContext http, McpOAuthService svc, CancellationToken ct) => svc.ConnectionsAsync(http, ct))
            .Requires(Capability.Authenticated);
        g.MapDelete("/connections/{id:int}", async (int id, HttpContext http, McpOAuthService svc, CancellationToken ct) =>
        {
            await svc.RevokeConnectionAsync(id, http, ct);
            return Results.NoContent();
        }).Requires(Capability.Authenticated).Writes(Effect.Session);
    }
}
