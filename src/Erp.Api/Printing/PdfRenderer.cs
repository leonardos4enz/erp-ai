using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Erp.Api.Printing;

/// <summary>
/// Spec 027 (ADR-0007): dibuja un <see cref="PrintDocument"/> con MigraDoc. Todo texto entra como texto (modelo de objetos,
/// nunca el lenguaje DDL de MigraDoc): lo que escribe una persona o trae un registro no puede cambiar el formato.
/// </summary>
public static class PdfRenderer
{
    public const string Font = "Lato";
    private static readonly object InitLock = new();
    private static bool _initialized;

    private static readonly Color Ink = new(0x1F, 0x29, 0x37);
    private static readonly Color Muted = new(0x6B, 0x72, 0x80);
    private static readonly Color Rule = new(0xD1, 0xD5, 0xDB);
    private static readonly Color HeadFill = new(0xF3, 0xF4, 0xF6);

    /// <summary>La fuente es del ensamblado: PDFsharp no busca fuentes del sistema (mismo resultado en Windows y Linux).</summary>
    public static void EnsureFonts()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
            _initialized = true;
        }
    }

    public static byte[] Render(PrintDocument d)
    {
        EnsureFonts();
        var doc = new Document();
        doc.Info.Title = Clean(d.Title);
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 9;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var section = doc.AddSection();
        var ps = section.PageSetup;
        var (w, h) = d.Page.A4 ? (210.0, 297.0) : (215.9, 279.4);
        if (d.Page.Landscape) (w, h) = (h, w);
        ps.PageWidth = Unit.FromMillimeter(w);
        ps.PageHeight = Unit.FromMillimeter(h);
        ps.Orientation = Orientation.Portrait;
        var m = Unit.FromMillimeter(d.Page.MarginMm);
        ps.TopMargin = ps.BottomMargin = ps.LeftMargin = ps.RightMargin = m;
        ps.FooterDistance = Unit.FromMillimeter(Math.Max(4, d.Page.MarginMm / 2.0));
        var contentMm = w - 2 * d.Page.MarginMm;

        foreach (var el in d.Body)
        {
            switch (el)
            {
                case HeaderElement x: Header(section, x, contentMm); break;
                case TextElement x:
                {
                    var p = section.AddParagraph();
                    p.Format.Alignment = Align(x.Align);
                    p.Format.Font.Size = x.Size;
                    p.Format.SpaceAfter = Unit.FromPoint(6);
                    AddText(p, x.Text, x.Bold, x.Italic);
                    break;
                }
                case FieldsElement x: Fields(section, x, contentMm); break;
                case TableElement x: Table(section, x, contentMm); break;
                case TotalsElement x: Totals(section, x, contentMm); break;
                case ImageElement x:
                {
                    var p = section.AddParagraph();
                    p.Format.Alignment = Align(x.Align);
                    p.Format.SpaceAfter = Unit.FromPoint(6);
                    var img = p.AddImage("base64:" + Convert.ToBase64String(x.Data));
                    img.Width = Unit.FromMillimeter(Math.Min(x.WidthMm, contentMm));
                    img.LockAspectRatio = true;
                    break;
                }
                case SeparatorElement:
                {
                    var p = section.AddParagraph();
                    p.Format.Borders.Bottom.Width = Unit.FromPoint(0.75);
                    p.Format.Borders.Bottom.Color = Rule;
                    p.Format.SpaceAfter = Unit.FromPoint(8);
                    p.Format.Font.Size = 2;
                    break;
                }
                case PageBreakElement: section.AddPageBreak(); break;
                case SignaturesElement x: Signatures(section, x, contentMm); break;
            }
        }

        if (d.Footer is { } f)
        {
            var p = section.Footers.Primary.AddParagraph();
            p.Format.Font.Size = 7.5;
            p.Format.Font.Color = Muted;
            p.Format.Borders.Top.Width = Unit.FromPoint(0.5);
            p.Format.Borders.Top.Color = Rule;
            p.Format.Borders.DistanceFromTop = Unit.FromPoint(3);
            if (!string.IsNullOrWhiteSpace(f.Text)) AddText(p, f.Text, false, false);
            if (f.PageNumbers)
            {
                if (!string.IsNullOrWhiteSpace(f.Text)) p.AddTab();
                p.AddText("Página ");
                p.AddPageField();
                p.AddText(" de ");
                p.AddNumPagesField();
                p.Format.TabStops.AddTabStop(Unit.FromMillimeter(contentMm), TabAlignment.Right);
                if (string.IsNullOrWhiteSpace(f.Text)) p.Format.Alignment = ParagraphAlignment.Right;
            }
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void Header(Section s, HeaderElement x, double contentMm)
    {
        var t = s.AddTable();
        t.Borders.Width = 0;
        var logoMm = x.Logo is null ? 0 : Math.Min(45, contentMm * 0.3);
        if (logoMm > 0) t.AddColumn(Unit.FromMillimeter(logoMm + 4));
        t.AddColumn(Unit.FromMillimeter(contentMm - (logoMm > 0 ? logoMm + 4 : 0)));
        var row = t.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;
        var c = 0;
        if (x.Logo is { } logo)
        {
            var img = row.Cells[c++].AddParagraph().AddImage("base64:" + Convert.ToBase64String(logo));
            img.Width = Unit.FromMillimeter(logoMm);
            img.LockAspectRatio = true;
        }
        var cell = row.Cells[c];
        cell.Format.Alignment = ParagraphAlignment.Right;
        if (!string.IsNullOrWhiteSpace(x.CompanyName))
        {
            var p = cell.AddParagraph();
            p.Format.Font.Size = 11;
            AddText(p, x.CompanyName, true, false);
        }
        foreach (var line in x.CompanyLines)
        {
            var p = cell.AddParagraph();
            p.Format.Font.Size = 8;
            p.Format.Font.Color = Muted;
            AddText(p, line, false, false);
        }
        if (!string.IsNullOrWhiteSpace(x.Title))
        {
            var p = cell.AddParagraph();
            p.Format.Font.Size = 15;
            p.Format.SpaceBefore = Unit.FromPoint(6);
            AddText(p, x.Title, true, false);
        }
        if (!string.IsNullOrWhiteSpace(x.Subtitle))
        {
            var p = cell.AddParagraph();
            p.Format.Font.Size = 10;
            p.Format.Font.Color = Muted;
            AddText(p, x.Subtitle, false, false);
        }
        var after = s.AddParagraph();
        after.Format.SpaceAfter = Unit.FromPoint(6);
        after.Format.Borders.Bottom.Width = Unit.FromPoint(1);
        after.Format.Borders.Bottom.Color = Rule;
        after.Format.Font.Size = 2;
    }

    private static void Fields(Section s, FieldsElement x, double contentMm)
    {
        var t = s.AddTable();
        t.Borders.Width = 0;
        var per = Math.Clamp(x.PerRow, 1, 3);
        for (var i = 0; i < per; i++) t.AddColumn(Unit.FromMillimeter(contentMm / per));
        for (var i = 0; i < x.Items.Count; i += per)
        {
            var row = t.AddRow();
            row.TopPadding = Unit.FromPoint(2);
            row.BottomPadding = Unit.FromPoint(2);
            for (var j = 0; j < per && i + j < x.Items.Count; j++)
            {
                var (label, value) = x.Items[i + j];
                var l = row.Cells[j].AddParagraph();
                l.Format.Font.Size = 7.5;
                l.Format.Font.Color = Muted;
                l.Format.SpaceAfter = 0;
                AddText(l, label, false, false);
                AddText(row.Cells[j].AddParagraph(), value, false, false);
            }
        }
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static void Table(Section s, TableElement x, double contentMm)
    {
        var t = s.AddTable();
        t.Borders.Bottom.Width = Unit.FromPoint(0.5);
        t.Borders.Bottom.Color = Rule;
        t.Format.Font.Size = 8.5;
        var total = Math.Max(1, x.Columns.Sum(c => c.Width));
        foreach (var c in x.Columns) t.AddColumn(Unit.FromMillimeter(contentMm * c.Width / total));
        var head = t.AddRow();
        head.HeadingFormat = true; // se repite en cada hoja
        head.Shading.Color = HeadFill;
        head.TopPadding = head.BottomPadding = Unit.FromPoint(3);
        for (var i = 0; i < x.Columns.Count; i++)
        {
            var p = head.Cells[i].AddParagraph();
            p.Format.Alignment = Align(x.Columns[i].Align);
            AddText(p, x.Columns[i].Label, true, false);
        }
        foreach (var r in x.Rows)
        {
            var row = t.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2.5);
            for (var i = 0; i < x.Columns.Count && i < r.Count; i++)
            {
                var p = row.Cells[i].AddParagraph();
                p.Format.Alignment = Align(x.Columns[i].Align);
                AddText(p, r[i], false, false);
            }
        }
        if (x.More > 0)
        {
            var row = t.AddRow();
            row.Cells[0].MergeRight = x.Columns.Count - 1;
            var p = row.Cells[0].AddParagraph();
            p.Format.Font.Color = Muted;
            AddText(p, $"… y {x.More} partida(s) más que no caben en este documento.", false, true);
        }
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static void Totals(Section s, TotalsElement x, double contentMm)
    {
        var t = s.AddTable();
        t.Borders.Width = 0;
        var labelMm = Math.Min(50, contentMm * 0.35);
        var valueMm = Math.Min(40, contentMm * 0.3);
        t.AddColumn(Unit.FromMillimeter(contentMm - labelMm - valueMm));
        t.AddColumn(Unit.FromMillimeter(labelMm));
        t.AddColumn(Unit.FromMillimeter(valueMm));
        foreach (var (label, value, bold) in x.Rows)
        {
            var row = t.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(1.5);
            if (bold)
            {
                row.Cells[1].Borders.Top.Width = row.Cells[2].Borders.Top.Width = Unit.FromPoint(0.75);
                row.Cells[1].Borders.Top.Color = row.Cells[2].Borders.Top.Color = Ink;
            }
            var l = row.Cells[1].AddParagraph();
            l.Format.Alignment = ParagraphAlignment.Right;
            AddText(l, label, bold, false);
            var v = row.Cells[2].AddParagraph();
            v.Format.Alignment = ParagraphAlignment.Right;
            AddText(v, value, bold, false);
        }
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static void Signatures(Section s, SignaturesElement x, double contentMm)
    {
        var spacer = s.AddParagraph();
        spacer.Format.SpaceBefore = Unit.FromMillimeter(14);
        spacer.Format.Font.Size = 2;
        var t = s.AddTable();
        t.Borders.Width = 0;
        var n = Math.Max(1, x.Items.Count);
        var colMm = contentMm / n;
        for (var i = 0; i < n; i++) t.AddColumn(Unit.FromMillimeter(colMm));
        var row = t.AddRow();
        row.KeepWith = 0;
        for (var i = 0; i < x.Items.Count; i++)
        {
            var cell = row.Cells[i];
            cell.Format.Alignment = ParagraphAlignment.Center;
            cell.Format.LeftIndent = cell.Format.RightIndent = Unit.FromMillimeter(Math.Min(8, colMm * 0.1));
            var name = cell.AddParagraph();
            name.Format.Borders.Bottom.Width = Unit.FromPoint(0.75);
            name.Format.Borders.Bottom.Color = Ink;
            name.Format.SpaceAfter = Unit.FromPoint(2);
            AddText(name, string.IsNullOrWhiteSpace(x.Items[i].Name) ? " " : x.Items[i].Name!, false, false);
            var label = cell.AddParagraph();
            label.Format.Font.Size = 8;
            label.Format.Font.Color = Muted;
            AddText(label, x.Items[i].Label, false, false);
        }
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static ParagraphAlignment Align(string? a) => a switch
    {
        "center" => ParagraphAlignment.Center,
        "right" => ParagraphAlignment.Right,
        _ => ParagraphAlignment.Left,
    };

    /// <summary>Texto literal con saltos de línea; se quitan caracteres de control (no tienen glifo y rompen la línea).</summary>
    private static void AddText(Paragraph p, string text, bool bold, bool italic)
    {
        var lines = Clean(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.AddLineBreak();
            if (lines[i].Length == 0) continue;
            var f = p.AddFormattedText(lines[i]);
            f.Bold = bold;
            f.Italic = italic;
        }
    }

    internal static string Clean(string? text) =>
        new string((text ?? "").ReplaceLineEndings("\n").Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).Select(c => c == '\t' ? ' ' : c).ToArray());

    /// <summary>Lato normal y negrita del ensamblado; la cursiva la simula PDFsharp. Cualquier familia pedida usa Lato.</summary>
    private sealed class EmbeddedFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "Lato-Bold" : "Lato-Regular", false, italic);

        public byte[]? GetFont(string faceName)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Erp.Fonts.{faceName}.ttf");
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
