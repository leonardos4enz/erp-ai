using Dapper;
using Erp.Api.Files;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 027: imágenes del entorno. Se sirven autenticadas, del mismo origen, con el tipo que fijó el servidor, sin poder
/// ejecutarse (<c>nosniff</c>, CSP <c>sandbox</c>) y con caché por huella. Las sube quien administra el modelo (imágenes de
/// plantillas) o el superusuario (logo de la empresa, en <see cref="CompanyEndpoints"/>).
/// </summary>
public static class FileEndpoints
{
    public sealed record ImageDto(int Id, string Key, string ContentType, int Size, int Width, int Height, string Sha256, string CreatedBy,
        DateTime CreatedAt, string Url);

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        // Spec 029 (H1 CA5): solo las pantallas de Imágenes y Empresas las muestran; el PDF las incrusta en el servidor.
        app.MapGet("/api/files/{id:int}", Serve).Requires(Capability.Modeler);

        var g = app.MapGroup("/api/admin/images").RequireAuthorization();
        g.MapGet("/", async (HttpContext http, Db db) =>
        {
            await using var cn = await db.OpenAsync();
            return (await FileStore.ImagesAsync(cn)).Select(ToDto);
        }).Requires(Capability.Modeler);
        g.MapPut("/{key}", async (string key, HttpContext http, Db db, CancellationToken ct) =>
        {
            if (!Identifiers.IsValid(key, out var err))
                throw new ValidationFailed(new Dictionary<string, string[]> { ["key"] = [err ?? "Clave no válida."] });
            var data = await ReadBodyAsync(http, ct);
            await using var cn = await db.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            var previous = (await FileStore.ImagesAsync(cn, tx)).FirstOrDefault(f => string.Equals(f.FileKey, key, StringComparison.OrdinalIgnoreCase));
            // Reemplazar archiva la anterior (constitución II.9); la clave queda libre para la nueva.
            if (previous is not null) await FileStore.ArchiveAsync(cn, tx, previous.Id);
            var saved = await FileStore.SaveAsync(cn, tx, data, FilePurposes.Image, previous?.FileKey ?? key, http.CurrentUser().Id);
            await Audit.WriteAsync(cn, tx, http, AuditActions.FileChange, "Imagen", saved.FileKey,
                before: previous is null ? null : Summary(previous), after: Summary(saved));
            await tx.CommitAsync(ct);
            return ToDto(saved);
        }).Requires(Capability.Modeler).Writes(Effect.Config);
        g.MapDelete("/{key}", async (string key, HttpContext http, Db db, CancellationToken ct) =>
        {
            await using var cn = await db.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            var previous = (await FileStore.ImagesAsync(cn, tx)).FirstOrDefault(f => string.Equals(f.FileKey, key, StringComparison.OrdinalIgnoreCase))
                           ?? throw new NotFound("La imagen no existe.");
            await FileStore.ArchiveAsync(cn, tx, previous.Id);
            await Audit.WriteAsync(cn, tx, http, AuditActions.FileChange, "Imagen", previous.FileKey, before: Summary(previous));
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).Requires(Capability.Modeler).Writes(Effect.Config);
    }

    internal static ImageDto ToDto(StoredFile f) =>
        new(f.Id, f.FileKey ?? "", f.ContentType, f.Size, f.Width, f.Height, f.Sha256, f.CreatedBy, DateTime.SpecifyKind(f.CreatedAt, DateTimeKind.Utc), f.Url);

    /// <summary>Lo que la auditoría guarda de un archivo: nunca el contenido.</summary>
    internal static object Summary(StoredFile f) => new { f.Id, key = f.FileKey, f.ContentType, f.Size, f.Width, f.Height, f.Sha256 };

    /// <summary>El cuerpo es la imagen tal cual. Se lee con tope: más de 1 MB ni siquiera se termina de leer.</summary>
    internal static async Task<byte[]> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > ImageValidator.MaxBytes)
                throw new ValidationFailed(new Dictionary<string, string[]>
                    { ["file"] = [$"La imagen pesa más de {ImageValidator.MaxBytes / 1024 / 1024} MB: redúcela o vuelve a exportarla con más compresión."] });
        }
        return ms.ToArray();
    }

    private static async Task<IResult> Serve(int id, string? v, HttpContext http, Db db, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        if (await FileStore.ContentAsync(cn, id, ct) is not { } file) throw new NotFound("El archivo no existe.");
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        // Aunque alguien lo abra directo en el navegador, no ejecuta nada ni carga nada.
        h.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
        h.ContentDisposition = $"inline; filename=\"imagen-{id}{(file.ContentType == ImageValidator.Png ? ".png" : ".jpg")}\"";
        var etag = $"\"{file.Sha256}\"";
        h.ETag = etag;
        // La URL lleva la huella: si coincide, el contenido no cambia nunca (caché privada por usuario).
        h.CacheControl = string.Equals(v, file.Sha256, StringComparison.Ordinal) ? "private, max-age=31536000, immutable" : "private, no-cache";
        if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(file.Content, file.ContentType);
    }
}
