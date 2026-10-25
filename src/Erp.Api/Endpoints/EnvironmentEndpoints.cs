using Erp.Api.Infrastructure;
using Erp.Api.Environments;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 009: exportar paquetes y recibirlos con diff y aprobación. Solo superusuario (lo revisa el servicio).</summary>
public static class EnvironmentEndpoints
{
    public static void MapEnvironments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/environments").RequireAuthorization();
        g.MapGet("/", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.InfoAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        g.MapGet("/exportable", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.ExportableAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        g.MapPost("/export/preview", (ExportRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.ExportPreviewAsync(req, http.CurrentUser(), ct)).Requires(Capability.Superuser).ReadsOnly();
        g.MapPost("/export", async (ExportRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            var (content, fileName) = await svc.ExportAsync(req, http, ct);
            return Results.File(content, "application/json", fileName);
        }).Requires(Capability.Superuser).Writes(Effect.Config);

        g.MapGet("/imports", (HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.ListAsync(http.CurrentUser(), ct)).Requires(Capability.Superuser);
        // El cuerpo es el archivo tal cual (máx. 2 MB por Kestrel), como la importación de datos de la 008.
        g.MapPost("/imports", async (HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            return await svc.UploadAsync(ms.ToArray(), http, ct);
        }).Requires(Capability.Superuser).Writes(Effect.Config);
        g.MapGet("/imports/{id:int}", (int id, HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.DetailAsync(id, http.CurrentUser(), ct)).Requires(Capability.Superuser);
        g.MapPost("/imports/{id:int}/approve", (int id, ApproveRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.ApproveAsync(id, req, http, ct)).Requires(Capability.Superuser).Writes(Effect.Config).AppliesReviewed("diffHash");
        g.MapPost("/imports/{id:int}/reject", (int id, RejectRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
            svc.RejectAsync(id, req, http, ct)).Requires(Capability.Superuser).Writes(Effect.Config);
        // Spec 017: crea la solicitud de reversión; se revisa, aprueba y aplica con los mismos endpoints que un paquete.
        g.MapPost("/imports/{id:int}/revert", (int id, HttpContext http, EnvironmentService svc, CancellationToken ct) => svc.RevertAsync(id, http, ct)).Requires(Capability.Superuser).Writes(Effect.Config);
        g.MapPost("/imports/{id:int}/apply", async (int id, ApplyRequest req, HttpContext http, EnvironmentService svc, CancellationToken ct) =>
        {
            var (changeSetId, import) = await svc.ApplyAsync(id, req, http, ct);
            return new { changeSetId, import };
        }).Requires(Capability.Superuser).Writes(Effect.Model, Gates.PackageApproval).AppliesReviewed("diffHash");
    }
}
