using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Erp.Api.Infrastructure;

namespace Erp.Api.Data;

/// <summary>Una tabla leída de un archivo: encabezados y filas de texto (número de fila tal como se ve en la hoja).</summary>
public sealed record ImportTable(string Format, IReadOnlyList<string> Headers, IReadOnlyList<(int Row, IReadOnlyList<string?> Cells)> Rows);

/// <summary>
/// Spec 008: lectores de CSV y Excel (.xlsx) sin bibliotecas externas. Defensas: tamaño, filas y columnas acotadas;
/// en XLSX, número de entradas y tamaño descomprimido limitados (zip bomb) y DTD prohibido (XXE).
/// </summary>
public static class ImportReader
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRows = 1000;
    public const int MaxColumns = 200;
    public const int MaxCell = 10_000;
    public const int MaxZipEntries = 50;
    public const long MaxEntryBytes = 20 * 1024 * 1024;

    public static ImportTable Read(byte[] data)
    {
        if (data.Length == 0) throw new AppException(400, "El archivo está vacío.");
        if (data.Length > MaxBytes) throw new AppException(400, "El archivo supera 2 MB.");
        // Un .xlsx es un ZIP («PK\x03\x04»); cualquier otra cosa se lee como CSV.
        return data is [0x50, 0x4B, 0x03, 0x04, ..] ? Xlsx(data) : Csv(Decode(data));
    }

    /// <summary>UTF-8 (con o sin BOM); si no es UTF-8 válido, Latin-1 (lo que guarda Excel en «CSV» en Windows).</summary>
    public static string Decode(byte[] data)
    {
        var span = data.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[Encoding.UTF8.Preamble.Length..];
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(span); }
    }

    // ---------------- CSV (RFC 4180) ----------------

    public static ImportTable Csv(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var firstLine = text.Split('\n', 2)[0];
        // Excel en español guarda con «;»: se elige el separador que más aparece en el encabezado.
        var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var records = new List<(int Row, List<string?> Cells)>();
        var cells = new List<string?>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        var line = 1;
        var start = 1;

        void EndCell()
        {
            if (cell.Length > MaxCell) throw new AppException(400, $"Fila {start}: una celda supera {MaxCell} caracteres.");
            cells.Add(cell.ToString());
            cell.Clear();
            if (cells.Count > MaxColumns) throw new AppException(400, $"El archivo tiene más de {MaxColumns} columnas.");
        }
        void EndRecord()
        {
            EndCell();
            records.Add((start, cells));
            if (records.Count > MaxRows + 1) throw new AppException(400, $"El archivo tiene más de {MaxRows} filas.");
            cells = [];
            any = false;
            start = line;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0: quoted = true; any = true; break;
                case '\r': break;
                case '\n': line++; EndRecord(); break;
                default:
                    if (c == sep) { EndCell(); any = true; }
                    else { cell.Append(c); any = true; }
                    break;
            }
        }
        if (quoted) throw new AppException(400, "El CSV tiene comillas sin cerrar.");
        if (any || cell.Length > 0 || cells.Count > 0) EndRecord();
        return Table("csv", records.Select(r => (r.Row, (IReadOnlyList<string?>)r.Cells)).ToList());
    }

    private static ImportTable Table(string format, List<(int Row, IReadOnlyList<string?> Cells)> records)
    {
        // Las filas totalmente vacías (p. ej. al final de la hoja) se omiten.
        var rows = records.Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count == 0) throw new AppException(400, "El archivo no tiene encabezados.");
        var headers = rows[0].Cells.Select(h => (h ?? "").Trim()).ToList();
        var data = rows.Skip(1).ToList();
        if (data.Count > MaxRows) throw new AppException(400, $"El archivo tiene más de {MaxRows} filas.");
        return new ImportTable(format, headers, data);
    }

    // ---------------- XLSX (Office Open XML) ----------------

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = false,
        MaxCharactersInDocument = MaxEntryBytes,
    };

    public static ImportTable Xlsx(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries) throw new AppException(400, "El archivo de Excel no es válido.");
            var shared = zip.GetEntry("xl/sharedStrings.xml") is { } ss ? SharedStrings(ss) : [];
            var sheet = FirstSheet(zip) ?? throw new AppException(400, "El archivo de Excel no tiene hojas.");
            return Table("xlsx", SheetRows(sheet, shared));
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or OverflowException or IOException)
        {
            throw new AppException(400, "El archivo de Excel no es válido.");
        }
    }

    private static XmlReader Open(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw new AppException(400, "El archivo de Excel es demasiado grande.");
        return XmlReader.Create(new LimitedStream(entry.Open(), MaxEntryBytes), XmlSettings);
    }

    /// <summary>Primera hoja según el libro y sus relaciones (no se asume el nombre «sheet1.xml»).</summary>
    private static ZipArchiveEntry? FirstSheet(ZipArchive zip)
    {
        string? rid = null;
        if (zip.GetEntry("xl/workbook.xml") is { } wb)
            using (var r = Open(wb))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                    {
                        rid = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                        break;
                    }
        if (rid is not null && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
            using (var r = Open(rels))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") == rid)
                    {
                        var target = r.GetAttribute("Target") ?? "";
                        var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
                        return zip.GetEntry(path);
                    }
        return zip.GetEntry("xl/worksheets/sheet1.xml");
    }

    private static List<string> SharedStrings(ZipArchiveEntry entry)
    {
        var list = new List<string>();
        using var r = Open(entry);
        StringBuilder? current = null;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") current = new StringBuilder();
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && current is not null) current.Append(r.ReadElementContentAsString());
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si" && current is not null)
            {
                list.Add(current.ToString());
                current = null;
                if (list.Count > 1_000_000) throw new AppException(400, "El archivo de Excel es demasiado grande.");
            }
        }
        return list;
    }

    private static List<(int Row, IReadOnlyList<string?> Cells)> SheetRows(ZipArchiveEntry entry, List<string> shared)
    {
        var rows = new List<(int, IReadOnlyList<string?>)>();
        using var r = Open(entry);
        List<string?>? cells = null;
        var rowNumber = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
            {
                rowNumber = int.TryParse(r.GetAttribute("r"), out var n) ? n : rowNumber + 1;
                cells = [];
                if (r.IsEmptyElement) { rows.Add((rowNumber, cells)); cells = null; }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && cells is not null)
            {
                rows.Add((rowNumber, cells));
                if (rows.Count > MaxRows + 1) throw new AppException(400, $"El archivo tiene más de {MaxRows} filas.");
                cells = null;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && cells is not null)
            {
                var col = ColumnIndex(r.GetAttribute("r")) ?? cells.Count;
                if (col >= MaxColumns) throw new AppException(400, $"El archivo tiene más de {MaxColumns} columnas.");
                var type = r.GetAttribute("t");
                var value = r.IsEmptyElement ? null : CellValue(r, type, shared);
                while (cells.Count < col) cells.Add(null);
                if (cells.Count == col) cells.Add(value); else cells[col] = value;
            }
        }
        return rows;
    }

    private static string? CellValue(XmlReader r, string? type, List<string> shared)
    {
        string? v = null;
        var inline = new StringBuilder();
        var depth = r.Depth;
        while (r.Read() && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.LocalName == "v") v = r.ReadElementContentAsString();
            else if (r.LocalName == "t") inline.Append(r.ReadElementContentAsString());
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;
        }
        return type switch
        {
            "s" => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : null,
            "inlineStr" => inline.ToString(),
            "b" => v == "1" ? "TRUE" : "FALSE",
            _ => v,
        };
    }

    /// <summary>«AB12» → 27 (base 0).</summary>
    private static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else break;
            if (n > MaxColumns) return MaxColumns;
        }
        return n == 0 ? null : n - 1;
    }

    /// <summary>Corta la lectura al llegar al límite: un ZIP puede declarar un tamaño falso.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > max) throw new AppException(400, "El archivo de Excel es demasiado grande.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
