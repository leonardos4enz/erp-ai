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
        var scheme = http.User.Identity?.AuthenticationType;
        if (scheme == Integrations.ApiKeyHandler.SchemeName &&
            !Integrations.ApiKeyHandler.AllowedPaths.Any(p => http.Request.Path.StartsWithSegments(p)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Una clave de API solo puede usar la API de datos, reportes y OData.", status = 403 });
            return;
        }
        if (scheme == Integrations.OAuthTokenHandler.SchemeName &&
            Integrations.OAuthScopes.Required(http.Request.Path, http.Request.Method) is var required &&
            (required is null || required.Length > 0 && !(http.User.FindFirst(Integrations.OAuthScopes.Claim)?.Value ?? "").Split(' ').Contains(required)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.Headers.WWWAuthenticate = required is null
                ? "Bearer error=\"insufficient_scope\""
                : $"Bearer error=\"insufficient_scope\", scope=\"{required}\"";
            await http.Response.WriteAsJsonAsync(new
            {
                title = required is null ? "Un token OAuth solo puede usar la API de datos, reportes y OData."
                    : $"El token no tiene el alcance {required}.",
                status = 403,
            });
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
