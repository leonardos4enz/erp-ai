namespace Erp.Api.Printing;

// Spec 027: documento ya resuelto contra un registro (textos finales, seguridad aplicada). Es lo único que ve el
// renderizador: así el modelo de la plantilla y la seguridad no dependen del motor de PDF (ADR-0007).

public sealed record PrintPageSpec(bool A4, bool Landscape, int MarginMm);

public abstract record PrintElement;

public sealed record HeaderElement(byte[]? Logo, string? CompanyName, IReadOnlyList<string> CompanyLines, string? Title, string? Subtitle) : PrintElement;

public sealed record TextElement(string Text, int Size, bool Bold, bool Italic, string Align) : PrintElement;

public sealed record FieldsElement(int PerRow, IReadOnlyList<(string Label, string Value)> Items) : PrintElement;

public sealed record TableColumn(string Label, int Width, string Align);

/// <param name="More">Partidas que no se imprimieron por el límite (0 = todas).</param>
public sealed record TableElement(IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int More) : PrintElement;

public sealed record TotalsElement(IReadOnlyList<(string Label, string Value, bool Bold)> Rows) : PrintElement;

public sealed record ImageElement(byte[] Data, int WidthMm, string Align) : PrintElement;

public sealed record SeparatorElement : PrintElement;

public sealed record PageBreakElement : PrintElement;

public sealed record SignaturesElement(IReadOnlyList<(string Label, string? Name)> Items) : PrintElement;

public sealed record PrintFooter(string? Text, bool PageNumbers);

public sealed record PrintDocument(string Title, PrintPageSpec Page, IReadOnlyList<PrintElement> Body, PrintFooter? Footer);
