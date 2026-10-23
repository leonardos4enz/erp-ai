using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Erp.Api.Data;
using Erp.Api.Endpoints;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 2 * 1024 * 1024; // 2 MB: los catálogos no reciben archivos
});

// ---------- JSON ----------
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.AllowOutOfOrderMetadataProperties = true; // "op" puede venir en cualquier posición
    o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip;
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JsonOptions>>().Value.SerializerOptions);

// ---------- Servicios ----------
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
{
    c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier;
    c.ProblemDetails.Type = null;
});
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<MetadataStore>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<UserContextLoader>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<RecordService>();
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ViewService>();
// Spec 008: integraciones. Las llaves de Data Protection (cifran destino y secreto de webhooks) viven fuera del repositorio.
var dp = builder.Services.AddDataProtection().SetApplicationName("ErpAi");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
var webhookOptions = WebhookOptions.From(builder.Configuration);
builder.Services.AddSingleton(webhookOptions);
builder.Services.AddSingleton(new WebhookUrlPolicy(webhookOptions.AllowHttp, webhookOptions.AllowedInternal));
builder.Services.AddSingleton<WebhookSecrets>();
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddHostedService<WebhookDispatcher>();

// ---------- Autenticación: cookie (ADR-0002) ----------
// Spec 008: con «Authorization: Bearer» se usa la clave de API (y nunca la cookie); si no, la cookie.
builder.Services.AddAuthentication("erp")
    .AddPolicyScheme("erp", "erp", o => o.ForwardDefaultSelector = http =>
        ApiKeyHandler.IsBearer(http.Request) ? ApiKeyHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyHandler.SchemeName, null)
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
builder.Services.AddAuthorization();

// ---------- Rate limiting ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        // Configurable solo para que la suite E2E (que inicia muchas sesiones) no se bloquee a sí misma.
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Security:LoginAttemptsPerMinute", 10),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

await Bootstrap.RunAsync(app.Services, app.Configuration, app.Logger);

// ---------- Pipeline ----------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// SPA compilada (web/dist). En desarrollo también puede usarse Vite en :5173 con proxy a esta API.
var spaPath = Path.GetFullPath(app.Configuration["Spa:Path"] ?? Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist"));
var spaFiles = Directory.Exists(spaPath) ? new PhysicalFileProvider(spaPath) : null;
if (spaFiles is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFiles });
}

app.UseAuthentication();
app.UseApiKeyScope();
app.UseRateLimiter();
app.UseAuthorization();
app.UseCsrfHeaderCheck();

app.MapAuth();
app.MapModel();
app.MapData();
app.MapAdmin();
app.MapAudit();
app.MapAnalytics();
app.MapIntegrations();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapFallback("/api/{**path}", () => Results.Problem(title: "Ruta no encontrada.", statusCode: 404));

if (spaFiles is not null)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFiles });

app.Run();

public partial class Program;
