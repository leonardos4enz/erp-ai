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

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    // 2 MB: los catálogos no reciben archivos; las imágenes (spec 027) se cortan en 1 MB al leerse.
    k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
});

builder.AddErp();

var app = builder.Build();

await Bootstrap.RunAsync(app.Services, app.Configuration, app.Logger);
// Spec 014 (H6 CA2): con aprobación y cambios directos a la vez, la compuerta de cuatro ojos se puede saltar.
if (app.Services.GetRequiredService<EnvironmentOptions>() is { RequireApproval: true, AllowDirectChanges: true })
    app.Logger.LogWarning("Environments:AllowDirectChanges está activo en un entorno que exige aprobación: planes, módulos y el " +
                          "asistente cambian el modelo sin pasar por un paquete aprobado. Úsalo solo en suites de prueba.");

// ---------- Pipeline ----------
// Spec 040 (H1 CA5, R-021-5 y R-018-4): detrás de un proxy listado, la dirección y el esquema del cliente (límites, URL).
if (ClientAddress.ForwardedOptions(app.Configuration) is { } forwarded) app.UseForwardedHeaders(forwarded);
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
app.UseBearerScope();
app.UseRateLimiter();
app.UseAuthorization();
app.UseCsrfHeaderCheck();

app.MapErpRoutes();

if (spaFiles is not null)
    // La SPA es pública (pide sesión al cargar): la política por omisión de la 031 niega lo que no se declara.
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spaFiles }).AllowAnonymous();

app.Run();

public partial class Program;
