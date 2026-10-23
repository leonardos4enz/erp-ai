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
        if (http.Request.Path.StartsWithSegments("/api"))
            h.CacheControl = "no-store";
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
        if (!safe && http.Request.Path.StartsWithSegments("/api") && http.Request.Headers[CsrfHeader] != "1")
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Petición rechazada (falta cabecera anti-CSRF).", status = 403 });
            return;
        }
        await next();
    });
}
