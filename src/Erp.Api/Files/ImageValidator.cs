using System.Buffers.Binary;
using System.IO.Compression;

namespace Erp.Api.Files;

/// <summary>Imagen aceptada: tipo fijado por el servidor (nunca el del cliente) y dimensiones leídas del propio archivo.</summary>
public sealed record ImageInfo(string ContentType, int Width, int Height);

/// <summary>
/// Spec 027 (H1 CA2-CA3): valida una imagen por su <b>firma de bytes y su estructura completa</b>. Solo PNG y JPEG
/// (decisión 2). Rechaza lo que un decodificador aceptaría «de buena fe» pero sirve para atacar: datos después del final
/// (polyglots), dimensiones excesivas, datos comprimidos que no corresponden a las dimensiones (bombas de descompresión),
/// fragmentos críticos desconocidos y variantes que los visores de PDF no soportan. Es puro: no decodifica píxeles.
/// </summary>
public static class ImageValidator
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxSide = 3000;
    public const long MaxPixels = 6_000_000;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>La imagen y su tipo, o el mensaje de por qué no se acepta (en español, con cómo resolverlo).</summary>
    public static (ImageInfo? Image, string? Error) Validate(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return (null, "El archivo está vacío.");
        if (data.Length > MaxBytes) return (null, $"La imagen pesa más de {MaxBytes / 1024 / 1024} MB: redúcela o vuelve a exportarla con más compresión.");
        if (data.StartsWith(PngSignature)) return Checked(ValidatePng(data));
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return Checked(ValidateJpeg(data));
        return (null, Describe(data) is { } other
            ? $"El archivo es {other}: solo se aceptan imágenes PNG o JPEG."
            : "El archivo no es una imagen PNG ni JPEG (se revisa su contenido, no su extensión).");
    }

    private static (ImageInfo?, string?) Checked((ImageInfo? Image, string? Error) r)
    {
        if (r.Image is not { } img) return r;
        if (img.Width <= 0 || img.Height <= 0) return (null, "La imagen declara dimensiones inválidas.");
        if (img.Width > MaxSide || img.Height > MaxSide || (long)img.Width * img.Height > MaxPixels)
            return (null, $"La imagen mide {img.Width} × {img.Height} px; el máximo es {MaxSide} px por lado y {MaxPixels / 1_000_000} megapíxeles. Redúcela antes de subirla.");
        return r;
    }

    /// <summary>Formatos conocidos que se rechazan con un mensaje claro.</summary>
    private static string? Describe(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8)) return "WebP";
        if (d.Length >= 6 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8))) return "GIF";
        if (d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M') return "BMP";
        if (d.Length >= 4 && d[..4].SequenceEqual("%PDF"u8)) return "un PDF";
        var head = System.Text.Encoding.UTF8.GetString(d[..Math.Min(d.Length, 512)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<')) return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "SVG (no se acepta: puede contener scripts)" : "un documento HTML o XML";
        return null;
    }

    // ---------------- PNG ----------------

    private static (ImageInfo?, string?) ValidatePng(ReadOnlySpan<byte> d)
    {
        const string Damaged = "El PNG está dañado o incompleto: vuelve a exportarlo.";
        var pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var sawIhdr = false;
        var sawIdat = false;
        var idatEnded = false;
        var sawPlte = false;
        using var idat = new MemoryStream();
        while (true)
        {
            if (pos + 12 > d.Length) return (null, Damaged);
            var length = BinaryPrimitives.ReadUInt32BigEndian(d[pos..]);
            if (length > (uint)(d.Length - pos - 12)) return (null, Damaged);
            var type = d.Slice(pos + 4, 4);
            var body = d.Slice(pos + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(d[(pos + 8 + (int)length)..]);
            if (Crc32(d.Slice(pos + 4, 4 + (int)length)) != crc) return (null, Damaged);
            foreach (var c in type) if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return (null, Damaged);
            var name = System.Text.Encoding.ASCII.GetString(type);
            pos += 12 + (int)length;

            if (!sawIhdr)
            {
                if (name != "IHDR" || length != 13) return (null, Damaged);
                sawIhdr = true;
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                bitDepth = body[8];
                colorType = body[9];
                interlace = body[12];
                var validDepth = colorType switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => bitDepth is 8 or 16,
                    _ => false,
                };
                if (!validDepth || body[10] != 0 || body[11] != 0 || interlace > 1) return (null, Damaged);
                // Antes de descomprimir nada: las dimensiones son las que deciden cuánta memoria se usaría.
                if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
                    return (null, $"La imagen mide {width} × {height} px; el máximo es {MaxSide} px por lado y {MaxPixels / 1_000_000} megapíxeles. Redúcela antes de subirla.");
                continue;
            }
            switch (name)
            {
                case "IHDR": return (null, Damaged);
                case "PLTE": sawPlte = true; break;
                case "IDAT":
                    if (idatEnded) return (null, Damaged); // los IDAT son consecutivos
                    sawIdat = true;
                    idat.Write(body);
                    break;
                case "IEND":
                    if (length != 0 || !sawIdat) return (null, Damaged);
                    if (colorType == 3 && !sawPlte) return (null, Damaged);
                    // Polyglot: nada puede seguir al final de la imagen.
                    if (pos != d.Length) return (null, "El PNG trae datos después de su final (posible archivo disfrazado): vuelve a exportarlo.");
                    return InflateMatches(idat.ToArray(), ExpectedRaw(width, height, bitDepth, colorType, interlace))
                        ? (new ImageInfo(Png, width, height), null)
                        : (null, "Los datos del PNG no corresponden a sus dimensiones: vuelve a exportarlo.");
                case "acTL" or "fcTL" or "fdAT":
                    return (null, "El PNG es animado (APNG): sube una imagen fija.");
                default:
                    // Fragmento crítico (mayúscula inicial) que no es de la norma: un decodificador debe rechazarlo.
                    if (char.IsUpper(name[0])) return (null, Damaged);
                    break;
            }
            if (name != "IDAT" && sawIdat) idatEnded = true;
        }
    }

    /// <summary>Bytes sin comprimir que deben salir de los IDAT (filtro por renglón incluido), también entrelazado.</summary>
    internal static long ExpectedRaw(int width, int height, int bitDepth, int colorType, int interlace)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        long Row(long w) => w == 0 ? 0 : 1 + (w * channels * bitDepth + 7) / 8;
        if (interlace == 0) return Row(width) * height;
        // Adam7: siete pasadas con su propio ancho y alto.
        int[] x0 = [0, 4, 0, 2, 0, 1, 0], dx = [8, 8, 4, 4, 2, 2, 1], y0 = [0, 0, 4, 0, 2, 0, 1], dy = [8, 8, 8, 4, 4, 2, 2];
        long total = 0;
        for (var p = 0; p < 7; p++)
        {
            long w = width <= x0[p] ? 0 : (width - x0[p] + dx[p] - 1) / dx[p];
            long h = height <= y0[p] ? 0 : (height - y0[p] + dy[p] - 1) / dy[p];
            if (w > 0 && h > 0) total += Row(w) * h;
        }
        return total;
    }

    /// <summary>Descomprime con tope: se detiene en cuanto pasa del tamaño esperado (bomba) y exige el tamaño exacto.</summary>
    private static bool InflateMatches(byte[] compressed, long expected)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > expected) return false;
            }
            return total == expected;
        }
        catch (InvalidDataException) { return false; }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ---------------- JPEG ----------------

    private static (ImageInfo?, string?) ValidateJpeg(ReadOnlySpan<byte> d)
    {
        const string Damaged = "El JPEG está dañado o incompleto: vuelve a exportarlo.";
        var pos = 2;
        int width = 0, height = 0;
        var sawFrame = false;
        var sawScan = false;
        while (true)
        {
            if (pos + 2 > d.Length) return (null, Damaged);
            if (d[pos] != 0xFF) return (null, Damaged);
            var marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // relleno entre marcadores
            pos += 2;
            if (marker == 0xD9) // EOI
            {
                if (!sawFrame || !sawScan) return (null, Damaged);
                if (pos != d.Length) return (null, "El JPEG trae datos después de su final (posible archivo disfrazado): vuelve a exportarlo.");
                return (new ImageInfo(Jpeg, width, height), null);
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) return (null, Damaged); // sin longitud fuera de un escaneo
            if (pos + 2 > d.Length) return (null, Damaged);
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[pos..]);
            if (length < 2 || pos + length > d.Length) return (null, Damaged);
            var seg = d.Slice(pos + 2, length - 2);
            pos += length;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2: // base, secuencial extendido y progresivo (Huffman)
                    if (sawFrame || seg.Length < 6) return (null, Damaged);
                    sawFrame = true;
                    if (seg[0] != 8) return (null, "El JPEG usa 12 bits por canal: vuelve a exportarlo con 8 bits.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(seg[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(seg[3..]);
                    var components = seg[5];
                    if (components is not (1 or 3) || seg.Length != 6 + 3 * components)
                        return (null, "El JPEG está en CMYK u otro espacio de color: vuelve a exportarlo en RGB o escala de grises.");
                    if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
                        return (null, $"La imagen mide {width} × {height} px; el máximo es {MaxSide} px por lado y {MaxPixels / 1_000_000} megapíxeles. Redúcela antes de subirla.");
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    return (null, "El JPEG usa una compresión sin pérdida o aritmética que los visores de PDF no soportan: vuelve a exportarlo.");
                case 0xDA: // inicio de escaneo: datos codificados hasta el siguiente marcador que no sea relleno ni reinicio
                    if (!sawFrame) return (null, Damaged);
                    sawScan = true;
                    while (true)
                    {
                        if (pos + 1 >= d.Length) return (null, Damaged);
                        if (d[pos] == 0xFF && d[pos + 1] != 0x00 && d[pos + 1] is not (>= 0xD0 and <= 0xD7) && d[pos + 1] != 0xFF) break;
                        pos++;
                    }
                    break;
            }
        }
    }
}
