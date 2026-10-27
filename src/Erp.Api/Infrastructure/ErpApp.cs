using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Erp.Api.Assistant;
using Erp.Api.Automations;
using Erp.Api.Data;
using Erp.Api.Endpoints;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

namespace Erp.Api.Infrastructure;

/// <summary>
/// Spec 029: servicios y rutas de la aplicación, fuera de <c>Program</c> para que la prueba de inventario construya las rutas
/// (y lea sus metadatos de capacidad) sin base de datos.
/// </summary>
public static class ErpApp
{
    public static void AddErp(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        // ---------- JSON ----------
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.AllowOutOfOrderMetadataProperties = true; // "op" puede venir en cualquier posición
            o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip;
        });
        services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JsonOptions>>().Value.SerializerOptions);

        // ---------- Servicios ----------
        services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        {
            c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier;
            c.ProblemDetails.Type = null;
        });
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.AddSingleton<Db>();
        services.AddSingleton<MetadataStore>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<UserContextLoader>();
        services.AddScoped<PlanService>();
        services.AddScoped<RecordService>();
        services.AddScoped<ModuleService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ViewService>();
        // Spec 008: integraciones. Las llaves de Data Protection (cifran destino y secreto de webhooks) viven fuera del repositorio.
        var dp = services.AddDataProtection().SetApplicationName("ErpAi");
        if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        var webhookOptions = WebhookOptions.From(config);
        services.AddSingleton(webhookOptions);
        services.AddSingleton(new WebhookUrlPolicy(webhookOptions.AllowHttp, webhookOptions.AllowedInternal));
        services.AddSingleton<WebhookSecrets>();
        services.AddSingleton<WebhookSender>();
        services.AddScoped<WebhookService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<OAuthClientService>(); // spec 018
        services.AddScoped<OAuthTokenService>();
        services.AddHostedService<WebhookDispatcher>();
        // Spec 016: automatizaciones asíncronas y tareas programadas.
        services.AddSingleton(AutomationOptions.From(config));
        services.AddScoped<AutomationService>();
        services.AddHostedService<AutomationDispatcher>();
        // Spec 009: entornos. Nombre, aprobación obligatoria y clave de firma son configuración del servidor, no de la aplicación.
        services.AddSingleton(EnvironmentOptions.From(config));
        services.AddScoped<EnvironmentService>();
        services.AddSingleton<SecurityApproval>(); // spec 015
        // Spec 011: asistente IA. La clave de Anthropic la guarda un administrador desde la app (cifrada con Data Protection);
        // si el servidor la trae por configuración (user-secrets o variable de entorno), esa manda.
        services.AddSingleton(AssistantOptions.From(config));
        services.AddSingleton<IAssistantModel>(sp => sp.GetRequiredService<AnthropicAssistantModel>());
        services.AddSingleton<AssistantKeys>();
        services.AddScoped<AssistantService>();
        // Spec 033: ERP AI Studio. Las herramientas son rutas de la API ejecutadas con la identidad de la persona; la copia
        // aislada levanta estos mismos servicios (ServiceBlueprint) sobre otra base.
        services.AddSingleton(new Erp.Api.Studio.ServiceBlueprint(services));
        services.AddSingleton(Erp.Api.Studio.StudioOptions.From(config));
        services.AddSingleton<AnthropicAssistantModel>();
        services.AddSingleton<Erp.Api.Studio.IStudioModel, Erp.Api.Studio.AnthropicStudioModel>();
        services.AddSingleton<Erp.Api.Studio.RouteDispatcher>();
        services.AddSingleton<Erp.Api.Studio.SandboxFactory>();
        services.AddSingleton<Erp.Api.Studio.StudioToolRunner>();
        services.AddScoped<Erp.Api.Studio.StudioAgent>();
        services.AddScoped<Erp.Api.Studio.StudioService>();
        services.AddHostedService<Erp.Api.Studio.StudioDispatcher>();

        // ---------- Autenticación: cookie (ADR-0002) ----------
        // Spec 008: con «Authorization: Bearer» se usa la clave de API (y nunca la cookie); si no, la cookie. Spec 018: «Bearer erpat_…» es un token OAuth.
        services.AddAuthentication("erp")
            .AddPolicyScheme("erp", "erp", o => o.ForwardDefaultSelector = http =>
                OAuthFormat.IsBearerToken(http.Request) ? OAuthTokenHandler.SchemeName
                : ApiKeyHandler.IsBearer(http.Request) ? ApiKeyHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, OAuthTokenHandler>(OAuthTokenHandler.SchemeName, null)
            .AddCookie(o =>
            {
                o.Cookie.Name = "erp.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                // API: responder 401/403 en lugar de redirigir a una página de login.
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
                // En cada petición se revalida contra la BD: un usuario desactivado o con contraseña cambiada pierde la sesión al instante.
                o.Events.OnValidatePrincipal = async c =>
                {
                    var loader = c.HttpContext.RequestServices.GetRequiredService<UserContextLoader>();
                    var ok = int.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                             & Guid.TryParse(c.Principal?.FindFirstValue(AuthEndpoints.StampClaim), out var stamp);
                    var user = ok ? await loader.LoadAsync(id, stamp, c.HttpContext.RequestAborted) : null;
                    if (user is null || user.IsService)
                    {
                        c.RejectPrincipal();
                        await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }
                    c.HttpContext.Items[UserContextExtensions.ItemKey] = user;
                };
            });
        // Spec 031 (H6, auditoría 5): una ruta sin capacidad declarada (p. ej. mapeada fuera de MapErpRoutes, que no sale en
        // el inventario) se niega en lugar de quedar anónima. Las anónimas lo declaran (AllowAnonymous).
        services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .AddRequirements(new UndeclaredRouteRequirement()).Build());
        // Spec 029: capacidad declarada por ruta.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, CapabilityHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, CapabilityResultHandler>();

        // ---------- Rate limiting ----------
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                // Configurable solo para que la suite E2E (que inicia muchas sesiones) no se bloquee a sí misma.
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:LoginAttemptsPerMinute", 10),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            // Spec 018: el servidor de tokens, por dirección, como el inicio de sesión.
            o.AddPolicy("oauth", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:TokenRequestsPerMinute", 30),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            // Spec 027 (H5 CA4): generar un PDF cuesta CPU y memoria; por usuario.
            o.AddPolicy(PrintEndpoints.RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:PrintsPerMinute", 30),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            // Spec 029 (H5 CA3): la búsqueda global consulta todos los catálogos con folio; por usuario.
            o.AddPolicy(RatePolicies.Search, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:SearchesPerMinute", 60),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            // Spec 029 (H1 CA2): exportar e importar leen o escriben miles de filas; por usuario.
            o.AddPolicy(RatePolicies.Bulk, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:BulkPerMinute", 20),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            // Spec 031 (H6): tableros (un reporte por tarjeta), reportes y OData agregan o recorren muchas filas; por usuario.
            o.AddPolicy(RatePolicies.Aggregate, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:AggregatesPerMinute", 120),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                // Configurable solo para la suite E2E, que hace como administrador más de 600 peticiones por minuto.
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("Security:RequestsPerMinute", 600),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
        });
    }

    public static void MapErpRoutes(this IEndpointRouteBuilder app)
    {
        app.MapAuth();
        app.MapModel();
        app.MapData();
        app.MapAdmin();
        app.MapSecurityChanges(); // spec 015
        app.MapAudit();
        app.MapAnalytics();
        app.MapIntegrations();
        app.MapAutomations();
        app.MapEnvironments();
        app.MapCompanies();
        app.MapAssistant();
        app.MapStudio(); // spec 033
        app.MapOAuthToken(); // spec 018
        app.MapOData();
        app.MapExplorer(); // spec 025
        app.MapFiles(); // spec 027
        app.MapPrint();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).Requires(Capability.Anonymous);
        app.MapFallback("/api/{**path}", () => Results.Problem(title: "Ruta no encontrada.", statusCode: 404)).Requires(Capability.Anonymous);
        app.MapFallback("/oauth/{**path}", () => Results.Problem(title: "Ruta no encontrada.", statusCode: 404)).Requires(Capability.Anonymous);
        app.MapFallback("/odata/{**path}", () => ODataEndpoints.Error(404, "NotFound", "Ruta no encontrada.")).Requires(Capability.Anonymous);
    }
}

public static class RatePolicies
{
    public const string Search = "search";
    public const string Bulk = "bulk";
    public const string Aggregate = "aggregate";
}
