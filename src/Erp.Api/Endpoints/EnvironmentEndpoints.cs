using Erp.Api.Environments;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 009: exportar paquetes y recibirlos con diff y aprobación. Solo superusuario (lo revisa el servicio).</summary>
public static class EnvironmentEndpoints
{
    public static void MapEnvironments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/environments").RequireAuthorization();
        g.MapGet("/", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.InfoAsync(http.CurrentUser(), ct));
        g.MapGet("/exportable", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.ExportableAsync(http.CurrentUser(), ct));
        g.MapPost("/export/preview", (ExportRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.ExportPreviewAsync(req, http.CurrentUser(), ct));
        g.MapPost("/export", async (ExportRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            var (content, fileName) = await svc.ExportAsync(req, http, ct);
            return Results.File(content, "application/json", fileName);
        });

        g.MapGet("/imports", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct));
        // El cuerpo es el archivo tal cual (máx. 2 MB por Kestrel), como la importación de datos de la 008.
        g.MapPost("/imports", async (HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            return await svc.UploadAsync(ms.ToArray(), http, ct);
        });
        g.MapGet("/imports/{id:int}", (int id, HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.DetailAsync(id, http.CurrentUser(), ct));
        g.MapPost("/imports/{id:int}/approve", (int id, ApproveRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.ApproveAsync(id, req, http, ct));
        g.MapPost("/imports/{id:int}/reject", (int id, RejectRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.RejectAsync(id, req, http, ct));
        // Spec 017: crea la solicitud de reversión; se revisa, aprueba y aplica con los mismos endpoints que un paquete.
        g.MapPost("/imports/{id:int}/revert", (int id, HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.RevertAsync(id, http, ct));
        g.MapPost("/imports/{id:int}/apply", async (int id, ApplyRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            var (changeSetId, import) = await svc.ApplyAsync(id, req, http, ct);
            return new { changeSetId, import };
        });
    }
}
