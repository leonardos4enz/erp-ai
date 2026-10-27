using System.Collections.Concurrent;
using System.Security.Cryptography;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 027: PDF de un registro con una plantilla (H5) y vista previa del modelador (H4 CA6). El PDF se genera con la
/// seguridad de quien lo pide; nada se guarda salvo la auditoría (y la vista previa, 5 minutos en memoria).
/// </summary>
public static class PrintEndpoints
{
    public const string RatePolicy = "print";
    public sealed record PreviewRequest(string? Entity, long Id, PrintTemplate? Template);

    public static void MapPrint(this IEndpointRouteBuilder app)
    {
        var data = app.MapGroup("/api/data/{entity}/{id:long}").RequireAuthorization();
        data.MapGet("/print", (string entity, long id, HttpContext http, RecordService records, CancellationToken ct) =>
            records.PrintChoicesAsync(entity, id, http.CurrentUser(), ct)).Requires(Capability.Records);

        data.MapGet("/pdf", async (string entity, long id, string? template, HttpContext http, RecordService records, Db db, CancellationToken ct) =>
        {
            var r = await records.PrintAsync(entity, id, template, http.CurrentUser(), ct);
            await using (var cn = await db.OpenAsync(ct))
                await Audit.WriteAsync(cn, null, http, AuditActions.PrintPdf, r.Entity, id.ToString(), after: new { template = r.Template });
            PdfHeaders(http, frameable: false);
            // El nombre sale de los metadatos y del folio (caracteres seguros), nunca de la petición.
            return Results.File(r.Pdf, "application/pdf", r.FileName);
        }).Requires(Capability.Records).Writes(Effect.AuditTrail).Costly("genera un PDF").RequireRateLimiting(RatePolicy);

        var model = app.MapGroup("/api/model/print").RequireAuthorization();
        model.MapPost("/preview", async (PreviewRequest req, HttpContext http, RecordService records, MetadataStore store, Db db, CancellationToken ct) =>
        {
            var user = http.CurrentUser();
            if (req.Template is null || !Identifiers.IsValid(req.Entity, out _)) throw new AppException(400, "Indica el catálogo, el registro y la plantilla.");
            var e = await store.GetActiveAsync(req.Entity!, ct);
            var modelDefs = await store.GetAsync(ct);
            await using (var cn = await db.OpenAsync(ct))
            {
                var roles = await PlanService.LoadRolesAsync(cn, null);
                // La misma validación que el compilador: una plantilla inválida no se genera.
                var errors = PrintRules.Validate(e, [req.Template], modelDefs, roles).ToList();
                if (errors.Count > 0) throw new ValidationFailed(new Dictionary<string, string[]> { ["template"] = errors.ToArray() }, "La plantilla tiene errores.");
            }
            var r = await records.PrintAsync(e.Name, req.Id, null, user, ct, req.Template);
            return new { token = Previews.Put(user.Id, r.Pdf), warnings = r.Warnings };
        }).Requires(Capability.Modeler).ReadsOnly().Costly("genera un PDF").RequireRateLimiting(RatePolicy);

        model.MapGet("/preview/{token}", (string token, HttpContext http) =>
        {
            var pdf = Previews.Get(http.CurrentUser().Id, token) ?? throw new NotFound("La vista previa caducó: vuelve a generarla.");
            PdfHeaders(http, frameable: true);
            return Results.Bytes(pdf, "application/pdf");
        }).Requires(Capability.Modeler);
    }

    /// <summary>
    /// El PDF lo dibuja el visor del navegador. La vista previa se muestra en un marco del mismo origen; el resto conserva
    /// <c>frame-ancestors 'none'</c>. Ninguno se guarda en caché.
    /// </summary>
    private static void PdfHeaders(HttpContext http, bool frameable)
    {
        var h = http.Response.Headers;
        h.CacheControl = "no-store";
        h.XContentTypeOptions = "nosniff";
        h.ContentSecurityPolicy = frameable ? "frame-ancestors 'self'" : "frame-ancestors 'none'";
        h.XFrameOptions = frameable ? "SAMEORIGIN" : "DENY";
        if (frameable) h.ContentDisposition = "inline; filename=\"vista-previa.pdf\"";
    }

    /// <summary>Vistas previas en memoria: 5 minutos, ligadas a quien las generó, máximo 3 por usuario y 200 en total.</summary>
    internal static class Previews
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        private const int PerUser = 3, Total = 200;
        private static readonly ConcurrentDictionary<string, (int User, byte[] Pdf, DateTime At)> Items = new();

        public static string Put(int user, byte[] pdf)
        {
            var now = DateTime.UtcNow;
            foreach (var (k, v) in Items) if (now - v.At > Ttl) Items.TryRemove(k, out _);
            foreach (var old in Items.Where(x => x.Value.User == user).OrderByDescending(x => x.Value.At).Skip(PerUser - 1).ToList())
                Items.TryRemove(old.Key, out _);
            while (Items.Count >= Total && Items.OrderBy(x => x.Value.At).FirstOrDefault() is { Key: not null } oldest) Items.TryRemove(oldest.Key, out _);
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            Items[token] = (user, pdf, now);
            return token;
        }

        public static byte[]? Get(int user, string token) =>
            Items.TryGetValue(token, out var v) && v.User == user && DateTime.UtcNow - v.At <= Ttl ? v.Pdf : null;
    }
}
