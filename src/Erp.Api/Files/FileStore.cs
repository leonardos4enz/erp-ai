using System.Data.Common;
using System.Security.Cryptography;
using Dapper;

namespace Erp.Api.Files;

/// <summary>Usos de un archivo: el logo de una empresa (010) o una imagen de plantilla con su clave.</summary>
public static class FilePurposes
{
    public const string Logo = "logo";
    public const string Image = "image";
}

public sealed record StoredFile(int Id, string Purpose, string? FileKey, string ContentType, int Size, int Width, int Height, string Sha256,
    string CreatedBy, DateTime CreatedAt)
{
    /// <summary>URL del mismo origen con la huella: cambia si cambia el contenido (caché segura).</summary>
    public string Url => $"/api/files/{Id}?v={Sha256}";
}

/// <summary>
/// Spec 027: archivos de imagen del entorno en <c>meta.[File]</c>. Solo se guarda lo que <see cref="ImageValidator"/>
/// acepta, con el tipo que fijó el validador. Los identificadores van como parámetros; nunca hay SQL con datos.
/// </summary>
public static class FileStore
{
    private const string Columns = """
        f.Id, f.Purpose, f.FileKey, f.ContentType, f.Size, f.Width, f.Height, f.Sha256, u.DisplayName AS CreatedBy, f.CreatedAt
        """;

    /// <summary>Valida y guarda; devuelve el archivo o lanza <see cref="Infrastructure.ValidationFailed"/> con el motivo.</summary>
    public static async Task<StoredFile> SaveAsync(DbConnection cn, DbTransaction tx, byte[] data, string purpose, string? key, int userId)
    {
        var (image, error) = ImageValidator.Validate(data);
        if (image is null)
            throw new Infrastructure.ValidationFailed(new Dictionary<string, string[]> { ["file"] = [error!] });
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.[File] (Purpose, FileKey, ContentType, Size, Width, Height, Sha256, Content, CreatedBy)
            OUTPUT inserted.Id VALUES (@purpose, @key, @type, @size, @w, @h, @sha, @data, @userId)
            """, new { purpose, key, type = image.ContentType, size = data.Length, w = image.Width, h = image.Height, sha, data, userId }, tx);
        return (await FindAsync(cn, tx, id))!;
    }

    public static async Task ArchiveAsync(DbConnection cn, DbTransaction? tx, int id) =>
        await cn.ExecuteAsync("UPDATE meta.[File] SET IsArchived = 1, ArchivedAt = SYSUTCDATETIME() WHERE Id = @id AND IsArchived = 0", new { id }, tx);

    public static async Task<StoredFile?> FindAsync(DbConnection cn, DbTransaction? tx, int id) =>
        (await cn.QueryAsync<StoredFile>($"SELECT {Columns} FROM meta.[File] f JOIN meta.[User] u ON u.Id = f.CreatedBy WHERE f.Id = @id", new { id }, tx))
        .FirstOrDefault();

    /// <summary>Imágenes de plantilla vigentes, por clave.</summary>
    public static async Task<IReadOnlyList<StoredFile>> ImagesAsync(DbConnection cn, DbTransaction? tx = null) =>
        (await cn.QueryAsync<StoredFile>($"""
            SELECT {Columns} FROM meta.[File] f JOIN meta.[User] u ON u.Id = f.CreatedBy
             WHERE f.Purpose = @purpose AND f.IsArchived = 0 ORDER BY f.FileKey
            """, new { purpose = FilePurposes.Image }, tx)).ToList();

    /// <summary>Contenido de un archivo vigente (los archivados no se sirven).</summary>
    public static async Task<(byte[] Content, string ContentType, string Sha256)?> ContentAsync(DbConnection cn, int id, CancellationToken ct)
    {
        var row = await cn.QueryFirstOrDefaultAsync<(byte[] Content, string ContentType, string Sha256)?>(new CommandDefinition(
            "SELECT Content, ContentType, Sha256 FROM meta.[File] WHERE Id = @id AND IsArchived = 0", new { id }, cancellationToken: ct));
        return row;
    }

    /// <summary>Contenido de las imágenes de plantilla vigentes con esas claves (para un PDF).</summary>
    public static async Task<IReadOnlyDictionary<string, byte[]>> ImagesByKeyAsync(DbConnection cn, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return new Dictionary<string, byte[]>();
        var rows = await cn.QueryAsync<(string FileKey, byte[] Content)>(new CommandDefinition(
            "SELECT FileKey, Content FROM meta.[File] WHERE Purpose = @purpose AND IsArchived = 0 AND FileKey IN @keys",
            new { purpose = FilePurposes.Image, keys = keys.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.FileKey, r => r.Content, StringComparer.OrdinalIgnoreCase);
    }
}
