using System.Text.Json.Serialization;

namespace Erp.Api.Metadata;

// ---------- Spec 027: plantillas de impresión (PDF) ----------
// El modelo de la plantilla no conoce al motor de PDF (ADR-0007): describe bloques que fluyen de arriba abajo; el servidor
// los resuelve contra un registro (con su seguridad) y un renderizador los dibuja.

public static class PrintBlockTypes
{
    public const string Header = "header";
    public const string Text = "text";
    public const string Fields = "fields";
    public const string Table = "table";
    public const string Totals = "totals";
    public const string Image = "image";
    public const string Separator = "separator";
    public const string Signatures = "signatures";
    public const string PageBreak = "pageBreak";
    public const string Footer = "footer";
    public static readonly IReadOnlySet<string> All = new HashSet<string>
        { Header, Text, Fields, Table, Totals, Image, Separator, Signatures, PageBreak, Footer };
}

public static class PrintFormats
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Money = "money";
    public const string Percent = "percent";
    public const string Date = "date";
    public const string DateLong = "dateLong";
    public const string DateTime = "datetime";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Text, Number, Integer, Money, Percent, Date, DateLong, DateTime };
}

public static class PrintAlign
{
    public static readonly IReadOnlySet<string> All = new HashSet<string> { "left", "center", "right" };
}

/// <summary>Página: <c>letter</c> o <c>a4</c>, <c>portrait</c> o <c>landscape</c>, margen en milímetros.</summary>
public sealed record PrintPage
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Orientation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Margin { get; init; }

    public const int DefaultMargin = 15;
    [JsonIgnore] public bool IsA4 => Size == "a4";
    [JsonIgnore] public bool IsLandscape => Orientation == "landscape";
    [JsonIgnore] public int EffectiveMargin => Margin ?? DefaultMargin;
}

/// <summary>
/// Un valor impreso: <c>Value</c> es una expresión (002) en el ámbito del bloque (el registro, o la partida en una tabla).
/// <c>Format</c> omitido = según el tipo del campo. <c>Currency</c>: expresión con la clave de moneda (formato moneda);
/// omitida, la moneda base de la empresa.
/// </summary>
public sealed record PrintValue
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Decimals { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
    /// <summary>Tabla: ancho relativo (1 a 10).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Align { get; init; }
    /// <summary>Totales: renglón resaltado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bold { get; init; }
}

/// <summary>Firma: etiqueta bajo la línea («Autorizó») y nombre opcional (texto con <c>{{ expresión }}</c>).</summary>
public sealed record PrintSignature
{
    public string Label { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

/// <summary>
/// Bloque de la plantilla. Un solo registro para todos los tipos (forma simple para el JSON, la IA y el editor); cada tipo
/// usa sus propiedades y el compilador rechaza las que no le corresponden. Los textos (<c>Text</c>, <c>Title</c>,
/// <c>Subtitle</c>) admiten <c>{{ expresión }}</c>.
/// </summary>
public sealed record PrintBlock
{
    public string Type { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VisibleWhen { get; init; }
    // header
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Logo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Company { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; init; }
    // text, footer
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bold { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Italic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Align { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PageNumbers { get; init; }
    // fields, totals
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PrintValue>? Items { get; init; }
    /// <summary>Campos con etiqueta: cuántos por renglón (1 a 3).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerRow { get; init; }
    // table
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lines { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PrintValue>? Columns { get; init; }
    // image
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }
    // signatures
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PrintSignature>? Signatures { get; init; }
}

public sealed record PrintTemplate
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
    /// <summary>Roles que la usan; omitido = todos los que leen el catálogo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Roles { get; init; }
    /// <summary>Claves de empresa (010) en que aplica; omitido = todas.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Companies { get; init; }
    /// <summary>Condición sobre el registro: la plantilla solo aplica si da verdadero.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? When { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Default { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrintPage? Page { get; init; }
    public IReadOnlyList<PrintBlock> Blocks { get; init; } = [];
}

/// <summary>Spec 027. Reemplaza las plantillas de impresión del catálogo; con <c>Merge</c> agrega o reemplaza por nombre (023).</summary>
public sealed record SetPrintTemplatesOp : Operation
{
    public required string Entity { get; init; }
    public IReadOnlyList<PrintTemplate> Templates { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merge { get; init; }
}
