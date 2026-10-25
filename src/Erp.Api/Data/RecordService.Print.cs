using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Expressions;
using Erp.Api.Files;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Printing;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Data;

public sealed record PrintChoice(string Name, string Label, string? Description, bool Default);

public sealed record PrintResult(byte[] Pdf, string FileName, string Template, IReadOnlyList<string> Warnings);

/// <summary>Spec 027 (H5 CA4): límites del generador (por proceso).</summary>
public static class PrintLimits
{
    public const int MaxLines = 500;
    public const int MaxImages = 10;
    public const int MaxConcurrent = 4;
    /// <summary>Spec 029 (H5 CA2): megapíxeles de imagen por documento (decodificadas ocupan ~4 bytes por píxel).</summary>
    public const long MaxDocumentPixels = 12_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    internal static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    /// <summary>
    /// Spec 029 (H5 CA1, auditoría 4 D-02): el lugar del semáforo se libera cuando termina el render real. Si el cliente
    /// cancela o se vence el tiempo, la respuesta sale pero el lugar sigue ocupado hasta que el render acaba: nunca hay más
    /// de <see cref="MaxConcurrent"/> a la vez.
    /// </summary>
    internal static async Task<byte[]> RenderAsync(Func<byte[]> render, SemaphoreSlim gate, TimeSpan wait, TimeSpan timeout, CancellationToken ct)
    {
        if (!await gate.WaitAsync(wait, ct))
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "El servidor está generando otros PDF: inténtalo de nuevo en unos segundos.");
        Task<byte[]> work;
        try
        {
            // Sin token: una vez tomado el lugar, el render siempre corre y siempre lo libera.
            work = Task.Run(() =>
            {
                try { return render(); }
                finally { gate.Release(); }
            }, CancellationToken.None);
        }
        catch
        {
            gate.Release();
            throw;
        }
        try
        {
            return await work.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "El PDF tardó demasiado en generarse: reduce las partidas o las imágenes de la plantilla.");
        }
    }
}

/// <summary>Un salto de la ruta llega a un registro fuera del alcance de lectura del usuario.</summary>
internal sealed class PrintNoAccess : Exception;

/// <summary>Spec 027: lo que el PDF lee de la base (detrás de una interfaz para probar la seguridad sin base).</summary>
internal interface IPrintData
{
    /// <summary>¿El registro está al alcance de lectura del usuario (006/010/015)?</summary>
    bool Visible(string entity, long id);
    Values? Row(string entity, long id);
    Values? User(int id);
}

internal sealed class LoaderPrintData(RelatedLoader loader, UserContext user) : IPrintData
{
    public bool Visible(string entity, long id) => loader.Visible(entity, id, user);
    public Values? Row(string entity, long id) => loader.Row(entity, id);
    public Values? User(int id) => loader.User(id);
}

public sealed partial class RecordService
{
    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeFileChars();

    /// <summary>H5 CA1: plantillas que aplican al registro y al usuario (rol, empresa actual y condición); la predeterminada primero.</summary>
    public async Task<IReadOnlyList<PrintChoice>> PrintChoicesAsync(string entityName, long id, UserContext user, CancellationToken ct)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        await using var cn = await db.OpenAsync(ct);
        await EnsureAccessAsync(cn, null, e, id, user, write: false, ct);
        if (e.PrintTemplates is not { Count: > 0 }) return [];
        var draft = await LoadDraftAsync(cn, null, e, model, id, ct) ?? throw new NotFound("El registro no existe.");
        var loader = new RelatedLoader(cn, null, model);
        var ctx = new PrintContext(new LoaderPrintData(loader, user), user, model);
        var scope = PrintHeaderScope(draft, e, ctx);
        var env = Env(user, draft, loader);
        return Applicable(e, user, scope, env, ctx).OrderByDescending(t => t.Default)
            .Select(t => new PrintChoice(t.Name, t.Label, t.Description, t.Default)).ToList();
    }

    /// <summary>
    /// H5: PDF del registro con la plantilla pedida (o la predeterminada que aplique). Con <paramref name="draftTemplate"/>
    /// (vista previa del modelador) se usa esa plantilla aunque no aplique: el modelador ve el aviso.
    /// </summary>
    public async Task<PrintResult> PrintAsync(string entityName, long id, string? templateName, UserContext user, CancellationToken ct,
        PrintTemplate? draftTemplate = null)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        await using var cn = await db.OpenAsync(ct);
        await EnsureAccessAsync(cn, null, e, id, user, write: false, ct);
        var draft = await LoadDraftAsync(cn, null, e, model, id, ct) ?? throw new NotFound("El registro no existe.");
        var loader = new RelatedLoader(cn, null, model);
        var ctx = new PrintContext(new LoaderPrintData(loader, user), user, model);
        var scope = PrintHeaderScope(draft, e, ctx);
        var env = Env(user, draft, loader);

        PrintTemplate template;
        if (draftTemplate is not null)
        {
            template = draftTemplate;
            if (!Applies(template, user, scope, env, ctx))
                ctx.Warnings.Add("Esta plantilla no aplica a este registro o a tu usuario (rol, empresa o «cuándo aplica»): en el formulario no se ofrecería.");
        }
        else
        {
            var candidates = Applicable(e, user, scope, env, ctx).ToList();
            template = (templateName is { Length: > 0 }
                           ? candidates.FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase))
                           : candidates.FirstOrDefault(t => t.Default) ?? candidates.FirstOrDefault())
                       ?? throw new NotFound(templateName is { Length: > 0 } ? "La plantilla no existe o no aplica a este registro." : "Este registro no tiene una plantilla de impresión que te aplique.");
        }

        var company = await PrintCompanyAsync(cn, e.PerCompany ? draft.CompanyId : user.Company?.Id, ct);
        var imageKeys = template.Blocks.Where(b => b.Type == PrintBlockTypes.Image && b.File is not null).Select(b => b.File!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var logoCompany = template.Blocks.Any(b => b.Logo) ? (e.PerCompany ? draft.CompanyId : user.Company?.Id) : null;
        if (await FileStore.PixelsAsync(cn, imageKeys, logoCompany, ct) is var pixels && pixels > PrintLimits.MaxDocumentPixels)
            throw new AppException(StatusCodes.Status422UnprocessableEntity,
                $"Las imágenes de la plantilla suman {pixels / 1_000_000.0:0.#} megapíxeles; el máximo por documento es {PrintLimits.MaxDocumentPixels / 1_000_000}. Usa imágenes más pequeñas.");
        var images = await FileStore.ImagesByKeyAsync(cn, imageKeys, ct);
        var doc = ResolvePrint(e, template, draft, scope, env, ctx, company, images);

        var pdf = await PrintLimits.RenderAsync(() => PdfRenderer.Render(doc), PrintLimits.Gate, TimeSpan.FromSeconds(2), PrintLimits.Timeout, ct);

        var folio = e.EffectiveDisplayField is { } df ? Convert.ToString(draft.Header.GetValueOrDefault(df), PrintFormat.Culture) : null;
        var safe = UnsafeFileChars().Replace(string.IsNullOrWhiteSpace(folio) ? id.ToString(PrintFormat.Culture) : folio, "-").Trim('-');
        return new PrintResult(pdf, $"{e.Name}-{(safe.Length == 0 ? id.ToString(PrintFormat.Culture) : safe[..Math.Min(safe.Length, 60)])}.pdf",
            template.Name, ctx.Warnings.Distinct().ToList());
    }

    // ---------------- Elegir la plantilla ----------------

    private static IEnumerable<PrintTemplate> Applicable(EntityDef e, UserContext user, PrintScope scope, ExprEnv env, PrintContext ctx) =>
        (e.PrintTemplates ?? []).Where(t => Applies(t, user, scope, env, ctx));

    private static bool Applies(PrintTemplate t, UserContext user, PrintScope scope, ExprEnv env, PrintContext ctx)
    {
        if (!user.IsSuperuser && t.Roles is not null && !user.HasRole(t.Roles)) return false;
        if (t.Companies is { } codes && !codes.Contains(user.Company?.Code ?? "", StringComparer.OrdinalIgnoreCase)) return false;
        return ctx.Condition(t.When, scope, env);
    }

    // ---------------- Resolver los bloques ----------------

    internal sealed record PrintCompany(string Code, string Name, string BaseCurrency, string? TaxId, string? Address, byte[]? Logo);

    private static async Task<PrintCompany?> PrintCompanyAsync(SqlConnection cn, int? companyId, CancellationToken ct)
    {
        if (companyId is null) return null;
        var row = await cn.QueryFirstOrDefaultAsync<(string Code, string Name, string BaseCurrency, string? TaxId, string? Address, byte[]? Logo)?>(
            new CommandDefinition("""
                SELECT c.Code, c.Name, c.BaseCurrency, c.TaxId, c.Address, f.Content
                  FROM meta.Company c LEFT JOIN meta.[File] f ON f.Id = c.LogoFileId AND f.IsArchived = 0
                 WHERE c.Id = @companyId
                """, new { companyId }, cancellationToken: ct));
        return row is { } r ? new PrintCompany(r.Code, r.Name, r.BaseCurrency.Trim(), r.TaxId, r.Address, r.Logo) : null;
    }

    internal static PrintScope PrintHeaderScope(DocDraft d, EntityDef e, PrintContext ctx)
    {
        var lines = new Dictionary<string, IReadOnlyList<IExprScope>>(StringComparer.OrdinalIgnoreCase);
        var scope = new PrintScope(e, d.Header, ctx, lines);
        foreach (var (field, list) in d.Lines)
        {
            if (e.FindField(field) is not { ChildEntity: { } childName } || !ctx.Model.TryGetValue(childName, out var child)) continue;
            lines[field] = list.Select(l => (IExprScope)new PrintScope(child, l.Values, ctx, parent: scope)).ToList();
        }
        return scope;
    }

    internal static PrintDocument ResolvePrint(EntityDef e, PrintTemplate t, DocDraft draft, PrintScope scope, ExprEnv env, PrintContext ctx,
        PrintCompany? company, IReadOnlyDictionary<string, byte[]> images)
    {
        var body = new List<PrintElement>();
        PrintFooter? footer = null;
        var imageCount = company?.Logo is null ? 0 : 1;
        var baseCurrency = company?.BaseCurrency ?? env.BaseCurrency;
        string Text(string? text, PrintScope s) => ctx.Text(text, s, env);
        string Value(PrintValue v, PrintScope s) => ctx.Value(v, s, env, baseCurrency);

        foreach (var b in t.Blocks.Take(PrintRules.MaxBlocks))
        {
            if (b.Type == PrintBlockTypes.Footer)
            {
                footer = new PrintFooter(Text(b.Text, scope), b.PageNumbers);
                continue;
            }
            if (!ctx.Condition(b.VisibleWhen, scope, env)) continue;
            switch (b.Type)
            {
                case PrintBlockTypes.Header:
                {
                    byte[]? logo = null;
                    if (b.Logo)
                    {
                        if (company?.Logo is null) ctx.Warnings.Add(company is null ? "El encabezado pide el logo, pero el registro no tiene empresa." : $"La empresa {company.Name} no tiene logo: súbelo en Configuración › Empresas.");
                        else logo = company.Logo;
                    }
                    var lines = new List<string>();
                    if (b.Company && company is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(company.TaxId)) lines.Add($"RFC: {company.TaxId}");
                        if (!string.IsNullOrWhiteSpace(company.Address)) lines.AddRange(company.Address.Split('\n'));
                    }
                    body.Add(new HeaderElement(logo, b.Company ? company?.Name : null, lines, Text(b.Title, scope), Text(b.Subtitle, scope)));
                    break;
                }
                case PrintBlockTypes.Text:
                    body.Add(new TextElement(Text(b.Text, scope), b.Size ?? 9, b.Bold, b.Italic, b.Align ?? "left"));
                    break;
                // Regresión (revisión exploratoria 027, F-01): un valor vacío dejaba la etiqueta sola; ahora dice «—».
                case PrintBlockTypes.Fields:
                    body.Add(new FieldsElement(b.PerRow ?? 2, (b.Items ?? []).Select(v => (v.Label, OrDash(Value(v, scope)))).ToList()));
                    break;
                case PrintBlockTypes.Totals:
                    body.Add(new TotalsElement((b.Items ?? []).Select(v => (v.Label, OrDash(Value(v, scope)), v.Bold)).ToList()));
                    break;
                case PrintBlockTypes.Table:
                {
                    if (e.FindField(b.Lines ?? "") is not { ChildEntity: { } childName } || !ctx.Model.TryGetValue(childName, out var child)) break;
                    // Decisión 4: una columna que lee un campo oculto no se imprime.
                    var columns = (b.Columns ?? []).Where(c => !ctx.Hidden(c.Value, child)).ToList();
                    if ((b.Columns?.Count ?? 0) > columns.Count) ctx.Warnings.Add($"La tabla omite {(b.Columns!.Count - columns.Count)} columna(s) con campos que tu rol no ve.");
                    if (columns.Count == 0) break;
                    var all = scope.Lines(b.Lines!).Cast<PrintScope>().ToList();
                    var rows = all.Take(PrintLimits.MaxLines).Select(line => (IReadOnlyList<string>)columns.Select(c => Value(c, line)).ToList()).ToList();
                    body.Add(new TableElement(
                        columns.Select(c => new TableColumn(c.Label, c.Width ?? 2, c.Align ?? (IsNumber(c, child, ctx) ? "right" : "left"))).ToList(),
                        rows, Math.Max(0, all.Count - PrintLimits.MaxLines)));
                    break;
                }
                case PrintBlockTypes.Image:
                    if (b.File is null || !images.TryGetValue(b.File, out var data))
                        ctx.Warnings.Add($"La imagen «{b.File}» no existe en este entorno: súbela en Configuración › Modelo › Imágenes.");
                    else if (++imageCount > PrintLimits.MaxImages)
                        ctx.Warnings.Add($"Un documento lleva hasta {PrintLimits.MaxImages} imágenes; «{b.File}» no se imprimió.");
                    else body.Add(new ImageElement(data, b.Width ?? 40, b.Align ?? "left"));
                    break;
                case PrintBlockTypes.Separator: body.Add(new SeparatorElement()); break;
                case PrintBlockTypes.PageBreak: body.Add(new PageBreakElement()); break;
                case PrintBlockTypes.Signatures:
                    body.Add(new SignaturesElement((b.Signatures ?? []).Select(s => (s.Label, s.Name is null ? null : Text(s.Name, scope))).ToList()));
                    break;
            }
        }
        var display = e.EffectiveDisplayField is { } df ? Convert.ToString(draft.Header.GetValueOrDefault(df), PrintFormat.Culture) : null;
        var p = t.Page;
        return new PrintDocument($"{t.Label}{(string.IsNullOrWhiteSpace(display) ? "" : $" {display}")}",
            new PrintPageSpec(p?.IsA4 == true, p?.IsLandscape == true, p?.EffectiveMargin ?? PrintPage.DefaultMargin), body, footer);
    }

    private static string OrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static bool IsNumber(PrintValue v, EntityDef scope, PrintContext ctx) =>
        v.Format is PrintFormats.Number or PrintFormats.Integer or PrintFormats.Money or PrintFormats.Percent ||
        (v.Format is null && ctx.FinalField(v.Value, scope).Field is { } f && ExprAnalyzer.IsNumeric(f));
}

/// <summary>
/// Spec 027: todo lo que se evalúa para un PDF pasa por aquí. Cada expresión se revisa antes contra la seguridad por campo
/// (incluidos los saltos de una ruta, 023) y cada salto a otro registro, contra su alcance de lectura (006/010/015).
/// </summary>
internal sealed class PrintContext(IPrintData data, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
{
    public const string NoAccess = ReportService.NoAccess;
    public IPrintData Data => data;
    public UserContext User => user;
    public IReadOnlyDictionary<string, EntityDef> Model => model;
    public List<string> Warnings { get; } = [];
    private readonly Dictionary<(string, long), bool> _visible = new();

    public bool Visible(string entity, long id)
    {
        if (_visible.TryGetValue((entity, id), out var v)) return v;
        return _visible[(entity, id)] = data.Visible(entity, id);
    }

    private Node? Parse(string expr)
    {
        try { return ExprParser.Parse(expr); }
        catch (ExprException ex) { Warnings.Add($"No se pudo leer «{expr}»: {ex.Message}"); return null; }
    }

    /// <summary>¿La expresión lee un campo que el usuario no ve? (también si no se puede leer: no se imprime).</summary>
    public bool Hidden(string expr, EntityDef scope) => Parse(expr) is not { } n || RecordService.ReadsHidden(n, scope, model, user);

    /// <summary>Condición de visibilidad o de aplicación: vacía = sí; desconocida, con un campo oculto o fuera de alcance = no.</summary>
    public bool Condition(string? expr, PrintScope scope, ExprEnv env)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;
        if (Hidden(expr, scope.Entity)) return false;
        try { return ExprEvaluator.Test(expr, scope, env, whenNull: false); }
        catch (PrintNoAccess) { return false; }
        catch (ExprException ex) { Warnings.Add($"No se pudo evaluar «{expr}»: {ex.Message}"); return false; }
    }

    /// <summary>Texto con <c>{{ expresión }}</c>: cada campo insertado se resuelve con su formato automático.</summary>
    public string Text(string? text, PrintScope scope, ExprEnv env)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var (parts, error) = TemplateText.Parse(text);
        if (error is not null) { Warnings.Add($"Texto de la plantilla: {error}"); return text; }
        return string.Concat(parts.Select(p => p.IsExpr ? Value(new PrintValue { Value = p.Text }, scope, env, env.BaseCurrency) : p.Text));
    }

    public string Value(PrintValue v, PrintScope scope, ExprEnv env, string? baseCurrency)
    {
        if (string.IsNullOrWhiteSpace(v.Value)) return "";
        var node = Parse(v.Value);
        if (node is null) return "";
        if (RecordService.ReadsHidden(node, scope.Entity, model, user)) return NoAccess;
        var (field, owner) = FinalField(node, scope.Entity);
        try
        {
            object? value;
            if (node is PathRef p && field is not null && Display(field) is { } extra)
            {
                // Una referencia o un usuario se imprimen con su etiqueta: un salto más hasta el campo para mostrar.
                if (extra.Hidden) return NoAccess;
                value = p.IsOwn && field.Type == FieldTypes.User
                    ? scope.RelatedPath(p.Head, [extra.Field])
                    : scope.RelatedPath(p.Head, [.. p.Rest, extra.Field]);
            }
            else value = ExprEvaluator.Evaluate(v.Value, scope, env);
            if (field?.Type == FieldTypes.State && owner?.Workflow?.FindState(value as string) is { } st) return st.Label;
            string? currency = null;
            if (v.Format == PrintFormats.Money)
                currency = v.Currency is { Length: > 0 } c
                    ? (Hidden(c, scope.Entity) ? null : Convert.ToString(ExprEvaluator.Evaluate(c, scope, env), PrintFormat.Culture))
                    : baseCurrency;
            return PrintFormat.Format(value, Display(field) is null ? field : null, v.Format, v.Decimals, currency);
        }
        catch (PrintNoAccess) { return NoAccess; }
        catch (ExprException ex) { Warnings.Add($"No se pudo calcular «{v.Value}»: {ex.Message}"); return ""; }
    }

    /// <summary>Campo de la etiqueta de una referencia (el campo para mostrar del destino) o de un usuario (su nombre).</summary>
    private (string Field, bool Hidden)? Display(FieldDef? f)
    {
        if (f is null) return null;
        if (f.Type == FieldTypes.User) return ("Nombre", false);
        if (f.Type != FieldTypes.Reference || !model.TryGetValue(f.ReferenceEntity ?? "", out var target) || target.EffectiveDisplayField is not { } df) return null;
        return (df, target.FindField(df) is { } dff && !FieldAccess.CanSee(target, dff, user));
    }

    public (FieldDef? Field, EntityDef? Owner) FinalField(string expr, EntityDef scope) =>
        Parse(expr) is { } n ? FinalField(n, scope) : (null, null);

    /// <summary>Si la expresión es solo un campo (o una ruta), el campo al que llega y su catálogo.</summary>
    public (FieldDef? Field, EntityDef? Owner) FinalField(Node n, EntityDef scope)
    {
        if (n is not PathRef p) return (null, null);
        var owner = scope;
        var f = scope.FindField(p.Head);
        foreach (var seg in p.Rest)
        {
            if (f is null || f.Type == FieldTypes.User || !model.TryGetValue(f.ReferenceEntity ?? "", out var target)) return (null, null);
            owner = target;
            f = target.FindField(seg);
        }
        return (f, owner);
    }
}

/// <summary>
/// Spec 027: ámbito de evaluación de un PDF. Como <see cref="DocScope"/>, pero cada salto a otro registro exige que esté
/// al alcance de lectura del usuario; si no, <see cref="PrintNoAccess"/> («(sin acceso)» en el documento).
/// </summary>
internal sealed class PrintScope(EntityDef entity, Values values, PrintContext ctx,
    IReadOnlyDictionary<string, IReadOnlyList<IExprScope>>? lines = null, PrintScope? parent = null) : IExprScope
{
    public EntityDef Entity => entity;

    public object? Field(string name) => values.GetValueOrDefault(name);

    public object? Related(string refField, string field) => RelatedPath(refField, [field]);

    public object? RelatedPath(string refField, IReadOnlyList<string> path)
    {
        var f = entity.FindField(refField);
        if (f is null || path.Count == 0) return null;
        // Desde una partida, el padre es el documento que se imprime (ya revisado).
        if (f.Type == FieldTypes.Parent && parent is not null)
            return path.Count == 1 ? parent.Field(path[0]) : parent.RelatedPath(path[0], path.Skip(1).ToList());
        var raw = values.GetValueOrDefault(f.Name);
        if (raw is null) return null;
        if (f.Type == FieldTypes.User)
            return path.Count == 1 ? ctx.Data.User(Convert.ToInt32(raw))?.GetValueOrDefault(path[0]) : null;
        if (f.Type is not (FieldTypes.Reference or FieldTypes.Parent) || f.ReferenceEntity is null) return null;
        var (target, id) = (f.ReferenceEntity, Convert.ToInt64(raw));
        for (var i = 0; ; i++)
        {
            if (!ctx.Visible(target, id)) throw new PrintNoAccess();
            var row = ctx.Data.Row(target, id);
            if (row is null || !ctx.Model.TryGetValue(target, out var te) || te.FindField(path[i]) is not { } hop) return null;
            var value = row.GetValueOrDefault(hop.Name);
            if (i == path.Count - 1) return value;
            if (value is null) return null;
            if (hop.Type == FieldTypes.User)
                return i == path.Count - 2 ? ctx.Data.User(Convert.ToInt32(value))?.GetValueOrDefault(path[i + 1]) : null;
            if (hop.Type is not (FieldTypes.Reference or FieldTypes.Parent) || hop.ReferenceEntity is null) return null;
            (target, id) = (hop.ReferenceEntity, Convert.ToInt64(value));
        }
    }

    public IReadOnlyList<IExprScope> Lines(string linesField) =>
        lines is not null && lines.TryGetValue(linesField, out var l) ? l : [];
}
