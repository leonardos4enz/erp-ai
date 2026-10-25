using Erp.Api.Expressions;
using Erp.Api.Security;

namespace Erp.Api.Metadata;

/// <summary>Spec 027: texto con campos insertados (<c>Hola {{ Proveedor.Contacto }}</c>).</summary>
public static class TemplateText
{
    public const int MaxExpressions = 40;

    /// <summary>Partes del texto: literal (<c>IsExpr</c> falso) o expresión. Error si una llave queda sin cerrar.</summary>
    public static (IReadOnlyList<(bool IsExpr, string Text)> Parts, string? Error) Parse(string? text)
    {
        var parts = new List<(bool, string)>();
        var s = text ?? "";
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { parts.Add((false, s[i..])); break; }
            if (open > i) parts.Add((false, s[i..open]));
            var close = s.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) return (parts, "una llave «{{» no se cierra con «}}».");
            var expr = s[(open + 2)..close].Trim();
            if (expr.Length == 0) return (parts, "hay un «{{ }}» vacío: escribe el campo o la expresión entre las llaves.");
            parts.Add((true, expr));
            i = close + 2;
        }
        return parts.Count(p => p.Item1) > MaxExpressions
            ? (parts, $"un texto admite hasta {MaxExpressions} campos insertados.")
            : (parts, null);
    }
}

/// <summary>Spec 027: validación estática de las plantillas de impresión (ver plan 027 §2).</summary>
public static class PrintRules
{
    public const int MaxTemplates = 30;
    public const int MaxBlocks = 60;
    public const int MaxItems = 30;
    public const int MaxColumns = 12;
    public const int MaxTotals = 12;
    public const int MaxSignatures = 4;
    public const int MaxText = 4000;
    public const int MaxTitle = 300;
    public const int MaxExpr = 1000;
    public const int MaxCompanies = 50;
    public const int MinMargin = 5, MaxMargin = 40;
    public const int MinImageWidth = 5, MaxImageWidth = 190;

    public static IEnumerable<string> Validate(EntityDef e, IReadOnlyList<PrintTemplate> templates, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (e.IsChild) { msgs.Add("es una partida: se imprime dentro de su documento y no admite plantillas."); return msgs; }
        if (templates.Count > MaxTemplates) msgs.Add($"máximo {MaxTemplates} plantillas por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates)
        {
            var where = $"plantilla '{t.Name}'";
            if (!Identifiers.IsValid(t.Name, out var err)) msgs.Add($"{where}: {err}");
            else if (!names.Add(t.Name)) msgs.Add($"{where}: el nombre '{t.Name}' está repetido.");
            if (string.IsNullOrWhiteSpace(t.Label)) msgs.Add($"{where}: la etiqueta es obligatoria.");
            else if (t.Label.Length > InterfaceRules.MaxLabel) msgs.Add($"{where}: la etiqueta excede {InterfaceRules.MaxLabel} caracteres.");
            if (t.Description?.Length > InterfaceRules.MaxDescription) msgs.Add($"{where}: la descripción excede {InterfaceRules.MaxDescription} caracteres.");
            if (t.Roles is { Count: 0 }) msgs.Add($"{where}: indica los roles u omite «roles» para todos.");
            foreach (var r in t.Roles ?? []) if (!roles.Contains(r)) msgs.Add($"{where}: el rol '{r}' no existe.");
            if (t.Companies is { Count: 0 }) msgs.Add($"{where}: indica las empresas u omite «companies» para todas.");
            if (t.Companies?.Count > MaxCompanies) msgs.Add($"{where}: máximo {MaxCompanies} empresas.");
            foreach (var c in t.Companies ?? [])
                if (!Companies.IsCode(c)) msgs.Add($"{where}: '{c}' no es una clave de empresa (2 a 20 mayúsculas, números, '_' o '-').");
            // Dos predeterminadas para la misma empresa (o para todas) no dicen cuál usar.
            if (t.Default)
                foreach (var key in t.Companies ?? ["*"])
                    if (!defaults.Add(key))
                        msgs.Add($"{where}: ya hay otra plantilla predeterminada para {(key == "*" ? "todas las empresas" : $"la empresa {key}")}.");
            Expr(t.When, e, $"{where} (cuándo aplica)");
            if (t.Page is { } p)
            {
                if (p.Size is not (null or "letter" or "a4")) msgs.Add($"{where}: el tamaño de página es «letter» (carta) o «a4».");
                if (p.Orientation is not (null or "portrait" or "landscape")) msgs.Add($"{where}: la orientación es «portrait» (vertical) o «landscape» (horizontal).");
                if (p.Margin is < MinMargin or > MaxMargin) msgs.Add($"{where}: el margen va de {MinMargin} a {MaxMargin} mm.");
            }
            if (t.Blocks.Count is 0 or > MaxBlocks) msgs.Add($"{where}: la plantilla necesita de 1 a {MaxBlocks} bloques.");
            if (t.Blocks.Count(b => b.Type == PrintBlockTypes.Footer) > 1) msgs.Add($"{where}: solo puede tener un pie de página.");
            for (var i = 0; i < t.Blocks.Count && i < MaxBlocks; i++) Block(t.Blocks[i], $"{where} › bloque {i + 1}");
        }
        return msgs;

        void Expr(string? expr, EntityDef scope, string where)
        {
            if (string.IsNullOrWhiteSpace(expr)) return;
            if (expr.Length > MaxExpr) { msgs.Add($"{where}: la expresión excede {MaxExpr} caracteres."); return; }
            foreach (var m in ExprAnalyzer.Analyze(expr, scope, model, roles).Errors) msgs.Add($"{where}: {m}");
        }

        void Text(string? text, EntityDef scope, string where, int max)
        {
            if (text is null) return;
            if (text.Length > max) { msgs.Add($"{where}: el texto excede {max} caracteres."); return; }
            var (parts, error) = TemplateText.Parse(text);
            if (error is not null) { msgs.Add($"{where}: {error}"); return; }
            foreach (var (_, expr) in parts.Where(x => x.IsExpr)) Expr(expr, scope, where);
        }

        void Value(PrintValue v, EntityDef scope, string where, bool table)
        {
            if (string.IsNullOrWhiteSpace(v.Label) && !table) msgs.Add($"{where}: la etiqueta es obligatoria.");
            if (v.Label.Length > InterfaceRules.MaxLabel) msgs.Add($"{where}: la etiqueta excede {InterfaceRules.MaxLabel} caracteres.");
            if (string.IsNullOrWhiteSpace(v.Value)) msgs.Add($"{where}: indica el campo o la expresión que se imprime.");
            else Expr(v.Value, scope, where);
            if (v.Format is { } f && !PrintFormats.All.Contains(f)) msgs.Add($"{where}: el formato '{f}' no existe ({string.Join(", ", PrintFormats.All)}).");
            if (v.Decimals is < 0 or > 6) msgs.Add($"{where}: los decimales van de 0 a 6.");
            if (v.Currency is not null)
            {
                if (v.Format != PrintFormats.Money) msgs.Add($"{where}: la moneda solo se usa con el formato «money».");
                Expr(v.Currency, scope, $"{where} (moneda)");
            }
            if (v.Width is < 1 or > 10) msgs.Add($"{where}: el ancho relativo va de 1 a 10.");
            if (v.Align is { } a && !PrintAlign.All.Contains(a)) msgs.Add($"{where}: la alineación es left, center o right.");
        }

        void Block(PrintBlock b, string where)
        {
            if (!PrintBlockTypes.All.Contains(b.Type)) { msgs.Add($"{where}: el tipo '{b.Type}' no existe ({string.Join(", ", PrintBlockTypes.All)})."); return; }
            where = $"{where} ({b.Type})";
            Expr(b.VisibleWhen, e, $"{where} (mostrar cuando)");
            if (b.Type == PrintBlockTypes.Footer && b.VisibleWhen is not null) msgs.Add($"{where}: el pie se imprime en todas las hojas; no admite condición.");
            if (b.Align is { } align && !PrintAlign.All.Contains(align)) msgs.Add($"{where}: la alineación es left, center o right.");
            if (b.Size is < 6 or > 28) msgs.Add($"{where}: el tamaño de letra va de 6 a 28 puntos.");
            switch (b.Type)
            {
                case PrintBlockTypes.Header:
                    Text(b.Title, e, $"{where} (título)", MaxTitle);
                    Text(b.Subtitle, e, $"{where} (subtítulo)", MaxTitle);
                    if (!b.Logo && !b.Company && string.IsNullOrWhiteSpace(b.Title)) msgs.Add($"{where}: muestra el logo, la empresa o un título.");
                    break;
                case PrintBlockTypes.Text:
                    if (string.IsNullOrWhiteSpace(b.Text)) msgs.Add($"{where}: escribe el texto.");
                    Text(b.Text, e, where, MaxText);
                    break;
                case PrintBlockTypes.Footer:
                    Text(b.Text, e, where, MaxTitle);
                    if (string.IsNullOrWhiteSpace(b.Text) && !b.PageNumbers) msgs.Add($"{where}: escribe un texto o muestra el número de página.");
                    break;
                case PrintBlockTypes.Fields:
                case PrintBlockTypes.Totals:
                {
                    var max = b.Type == PrintBlockTypes.Fields ? MaxItems : MaxTotals;
                    if (b.Items is not { Count: > 0 } items || items.Count > max) { msgs.Add($"{where}: indica de 1 a {max} valores."); break; }
                    if (b.PerRow is not (null or 1 or 2 or 3)) msgs.Add($"{where}: de 1 a 3 campos por renglón.");
                    for (var i = 0; i < items.Count; i++) Value(items[i], e, $"{where} › «{items[i].Label}»", table: false);
                    break;
                }
                case PrintBlockTypes.Table:
                {
                    var lines = e.FindField(b.Lines ?? "");
                    if (lines is null || lines.IsArchived || lines.Type != FieldTypes.Lines)
                    { msgs.Add($"{where}: '{b.Lines}' no es un campo de partidas de {e.Name}."); break; }
                    if (!model.TryGetValue(lines.ChildEntity ?? "", out var child) || child.IsArchived) break; // lo reporta el campo
                    if (b.Columns is not { Count: > 0 } cols || cols.Count > MaxColumns) { msgs.Add($"{where}: indica de 1 a {MaxColumns} columnas."); break; }
                    for (var i = 0; i < cols.Count; i++) Value(cols[i], child, $"{where} › columna {i + 1}", table: true);
                    break;
                }
                case PrintBlockTypes.Image:
                    if (!Identifiers.IsValid(b.File, out _)) msgs.Add($"{where}: indica la clave de la imagen (p. ej. SelloCalidad).");
                    if (b.Width is < MinImageWidth or > MaxImageWidth) msgs.Add($"{where}: el ancho va de {MinImageWidth} a {MaxImageWidth} mm.");
                    break;
                case PrintBlockTypes.Signatures:
                    if (b.Signatures is not { Count: > 0 } sigs || sigs.Count > MaxSignatures) { msgs.Add($"{where}: indica de 1 a {MaxSignatures} firmas."); break; }
                    foreach (var s in sigs)
                    {
                        if (string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > InterfaceRules.MaxLabel)
                            msgs.Add($"{where}: cada firma necesita una etiqueta de 1 a {InterfaceRules.MaxLabel} caracteres.");
                        Text(s.Name, e, $"{where} › «{s.Label}»", MaxTitle);
                    }
                    break;
            }
        }
    }
}
