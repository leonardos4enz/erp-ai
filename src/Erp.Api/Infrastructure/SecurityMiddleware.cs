namespace Erp.Api.Infrastructure;

public static class SecurityMiddleware
{
    public const string CsrfHeader = "X-ERP-Request";

    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Constitución II.5: cabeceras de endurecimiento en todas las respuestas.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var h = http.Response.Headers;
        h.ContentSecurityPolicy = Csp;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        // Spec 018: el servidor de tokens y OData también son API (tokens y datos nunca en caché).
        if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/odata") || http.Request.Path.StartsWithSegments("/oauth"))
            h.CacheControl = "no-store";
        await next();
    });

    /// <summary>¿La petición se autenticó con una credencial de integración (clave de API o token OAuth) y no con la sesión?</summary>
    public static bool IsBearerCredential(HttpContext http) =>
        http.User.Identity?.AuthenticationType is Integrations.ApiKeyHandler.SchemeName or Integrations.OAuthTokenHandler.SchemeName;

    /// <summary>
    /// Spec 008: una clave de API solo llega a datos, reportes y OData; administración, modelo y auditoría responden 403.
    /// Spec 018: un token OAuth, además, solo a lo que sus alcances permiten (<see cref="Integrations.OAuthScopes.Required"/>).
    /// </summary>
    public static IApplicationBuilder UseBearerScope(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (BearerScope.Check(BearerCredential.Of(http.User), http.Request.Path, http.Request.Method) is { } denied)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            if (denied.WwwAuthenticate is { } challenge) http.Response.Headers.WWWAuthenticate = challenge;
            await http.Response.WriteAsJsonAsync(new { title = denied.Title, status = 403 });
            return;
        }
        await next();
    });

    /// <summary>
    /// Defensa CSRF: las peticiones que modifican estado deben traer una cabecera personalizada.
    /// Un formulario de otro sitio no puede enviarla y, sin CORS habilitado, tampoco un script ajeno.
    /// Se combina con cookies SameSite=Strict.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var method = http.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        // Spec 008/018: una petición con clave de API o token no lleva credenciales ambientales (cookies): no aplica CSRF.
        var apiKey = IsBearerCredential(http);
        if (!safe && !apiKey && http.Request.Path.StartsWithSegments("/api") && http.Request.Headers[CsrfHeader] != "1")
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Petición rechazada (falta cabecera anti-CSRF).", status = 403 });
            return;
        }
        await next();
    });
}

/// <summary>
/// Spec 034 (auditoría 6 A-03): la credencial de integración con la que llega una petición (clave de API o token OAuth y sus
/// alcances). Null = la sesión de la persona (cookie), que no tiene alcances. El despachador de Studio y del MCP la recibe
/// para volver a aplicar <see cref="BearerScope.Check"/>.
/// </summary>
public sealed record BearerCredential(string Scheme, string? Scopes = null)
{
    public static BearerCredential? Of(System.Security.Claims.ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType is Integrations.ApiKeyHandler.SchemeName or Integrations.OAuthTokenHandler.SchemeName
            ? new(user.Identity.AuthenticationType!, user.FindFirst(Integrations.OAuthScopes.Claim)?.Value)
            : null;
}

/// <summary>
/// Spec 008: una clave de API solo llega a datos, reportes y OData. Spec 018: un token OAuth, además, solo a lo que sus
/// alcances permiten. Spec 034: función pura que usan el middleware y el despachador (una sola regla para los dos caminos).
/// </summary>
public static class BearerScope
{
    public sealed record Denial(string Title, string? WwwAuthenticate = null);

    public static Denial? Check(BearerCredential? credential, PathString path, string method)
    {
        if (credential?.Scheme == Integrations.ApiKeyHandler.SchemeName && !Integrations.ApiKeyHandler.AllowedPaths.Any(p => path.StartsWithSegments(p)))
            return new("Una clave de API solo puede usar la API de datos, reportes y OData.");
        if (credential?.Scheme == Integrations.OAuthTokenHandler.SchemeName &&
            Integrations.OAuthScopes.Required(path, method) is var required &&
            (required is null || required.Length > 0 && !(credential.Scopes ?? "").Split(' ').Contains(required)))
            return required is null
                ? new("Un token OAuth solo puede usar la API de datos, reportes y OData.", "Bearer error=\"insufficient_scope\"")
                : new($"El token no tiene el alcance {required}.", $"Bearer error=\"insufficient_scope\", scope=\"{required}\"");
        return null;
    }
}
