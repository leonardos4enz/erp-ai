using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Erp.Api.Data;

/// <summary>
/// Spec 007: exportación de un reporte a CSV o Excel con exactamente lo que se ve (incluye el total general).
/// Ninguno de los dos formatos lleva fórmulas: en CSV se neutraliza el texto que una hoja de cálculo ejecutaría.
/// </summary>
public static class ReportExport
{
    public static byte[] Csv(ReportResult r)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string> cells) => sb.Append(string.Join(",", cells)).Append("\r\n");
        Line(r.Columns.Select(c => CsvCell(c.Label)));
        foreach (var row in r.Rows) Line(r.Columns.Select(c => CsvCell(row.GetValueOrDefault(c.Key))));
        if (r.Columns.Any(c => c.Kind == "group"))
            Line(r.Columns.Select((c, i) => c.Kind == "measure" ? CsvCell(r.Totals.GetValueOrDefault(c.Key)) : i == 0 ? CsvCell("Total") : ""));
        // BOM: Excel abre el archivo como UTF-8 (acentos y ñ).
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// Inyección de fórmulas (CSV injection): un texto que empieza con = + - @, tabulador o retorno se antepone con
    /// apóstrofo para que la hoja lo trate como texto. Los números van tal cual (cultura invariante).
    /// </summary>
    public static string CsvCell(object? v)
    {
        if (v is null) return "";
        if (v is decimal or double or int or long) return Convert.ToString(v, CultureInfo.InvariantCulture)!;
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s != s.Trim() ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>Libro de Excel mínimo (Office Open XML) con una hoja; texto como cadena en línea y números como número.</summary>
    public static byte[] Xlsx(ReportResult r)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Entry("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Entry("_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Entry("xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            // El nombre de la hoja admite 31 caracteres y no admite : \ / ? * [ ]
            var sheet = new string(r.Label.Where(c => !":\\/?*[]".Contains(c)).Take(31).ToArray());
            Entry("xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Xml(sheet.Length > 0 ? sheet : "Reporte")}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            // Estilo 1: encabezado en negritas.
            Entry("xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>
                """);

            var rows = new StringBuilder();
            var n = 0;
            void Row(IEnumerable<object?> cells, bool bold = false)
            {
                n++;
                rows.Append($"<row r=\"{n}\">");
                var c = 0;
                foreach (var v in cells)
                {
                    var refName = $"{ColumnName(c++)}{n}";
                    var style = bold ? " s=\"1\"" : "";
                    if (v is null) continue;
                    if (v is decimal or double or int or long)
                        rows.Append($"<c r=\"{refName}\"{style}><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>");
                    else
                        rows.Append($"<c r=\"{refName}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Xml(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")}</t></is></c>");
                }
                rows.Append("</row>");
            }
            Row(r.Columns.Select(c => (object?)c.Label), bold: true);
            foreach (var row in r.Rows) Row(r.Columns.Select(c => row.GetValueOrDefault(c.Key)));
            if (r.Columns.Any(c => c.Kind == "group"))
                Row(r.Columns.Select((c, i) => c.Kind == "measure" ? r.Totals.GetValueOrDefault(c.Key) : i == 0 ? "Total" : null), bold: true);
            Entry("xl/worksheets/sheet1.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>
                """);
        }
        return ms.ToArray();
    }

    private static string ColumnName(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    /// <summary>Escapa XML y quita caracteres de control que invalidarían el archivo.</summary>
    private static string Xml(string s) =>
        SecurityElement.Escape(new string(s.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray()))!;
}
