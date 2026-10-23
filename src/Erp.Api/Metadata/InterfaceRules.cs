using Erp.Api.Data;
using Erp.Api.Expressions;
using Erp.Api.Security;

namespace Erp.Api.Metadata;

/// <summary>Spec 007: clase de valor de un campo para filtros, agrupaciones y medidas.</summary>
public enum ValueKind { Text, LongText, Number, Date, Boolean, Id }

/// <summary>Campo de un reporte: propio (<c>Campo</c>) o de un catálogo relacionado a un nivel (<c>Relacion.Campo</c>).</summary>
public sealed record FieldPath(FieldDef Field, EntityDef Owner, FieldDef? Via)
{
    public bool IsRelated => Via is not null;
}

/// <summary>Spec 007: validación estática de layouts, vistas compartidas, reportes y tableros (ver plan 007).</summary>
public static class InterfaceRules
{
    public const int MaxSections = 30;
    public const int MaxViews = 30;
    public const int MaxReports = 30;
    public const int MaxGroups = 2;
    public const int MaxMeasures = 6;
    public const int MaxRows = 1000;
    public const int DefaultRows = 500;
    public const int MaxWidgets = 20;
    public const int MaxLabel = 100;
    public const int MaxDescription = 300;

    public static readonly IReadOnlySet<string> Periods = new HashSet<string> { "day", "month", "year" };
    public static readonly IReadOnlySet<string> MeasureFns = new HashSet<string> { "count", "sum", "avg", "min", "max" };
    public static readonly IReadOnlySet<string> Charts = new HashSet<string> { "bar", "line", "pie" };
    public static readonly IReadOnlySet<string> WidgetTypes = new HashSet<string> { "kpi", "chart", "table" };

    public static ValueKind KindOf(FieldDef f) => (f.Type == FieldTypes.Formula ? f.ResultType : f.Type) switch
    {
        FieldTypes.LongText => ValueKind.LongText,
        FieldTypes.Integer or FieldTypes.Decimal => ValueKind.Number,
        FieldTypes.Date or FieldTypes.DateTime => ValueKind.Date,
        FieldTypes.Boolean => ValueKind.Boolean,
        FieldTypes.Reference or FieldTypes.Parent or FieldTypes.User => ValueKind.Id,
        _ => ValueKind.Text,
    };

    /// <summary>Resuelve <c>Campo</c> o <c>Relacion.Campo</c> a columnas con tabla; devuelve el error para el modelador.</summary>
    public static (FieldPath? Path, string? Error) Resolve(EntityDef e, string? path, IReadOnlyDictionary<string, EntityDef> model)
    {
        var parts = (path ?? "").Split('.');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace)) return (null, $"'{path}' no es un campo (usa Campo o Relacion.Campo).");
        var head = e.FindField(parts[0]);
        if (head is null || head.IsArchived || FieldTypes.IsVirtual(head.Type)) return (null, $"'{parts[0]}' no es un campo de {e.Name}.");
        if (parts.Length == 1) return (new FieldPath(head, e, null), null);
        if (head.Type is not (FieldTypes.Reference or FieldTypes.Parent) || !model.TryGetValue(head.ReferenceEntity ?? "", out var target) || target.IsArchived)
            return (null, $"'{path}': solo se puede llegar a otro catálogo por una referencia o el documento padre.");
        var tail = target.FindField(parts[1]);
        if (tail is null || tail.IsArchived || FieldTypes.IsVirtual(tail.Type)) return (null, $"'{parts[1]}' no es un campo de {target.Name}.");
        return (new FieldPath(tail, target, head), null);
    }

    /// <summary>¿El usuario ve el campo y, si es relacionado, también la referencia que lleva a él?</summary>
    public static bool CanSee(FieldPath p, EntityDef e, UserContext u) =>
        FieldAccess.CanSee(p.Owner, p.Field, u) && (p.Via is null || FieldAccess.CanSee(e, p.Via, u));

    private static void CheckRoles(IReadOnlyList<string>? list, string where, IReadOnlySet<string> roles, List<string> msgs)
    {
        if (list is { Count: 0 }) msgs.Add($"{where}: indica los roles u omite «roles» para todos.");
        foreach (var r in list ?? [])
            if (!roles.Contains(r)) msgs.Add($"{where}: el rol '{r}' no existe.");
    }

    private static void CheckLabel(string? label, string where, List<string> msgs, int max = MaxLabel)
    {
        if (string.IsNullOrWhiteSpace(label)) msgs.Add($"{where}: la etiqueta es obligatoria.");
        else if (label.Length > max) msgs.Add($"{where}: la etiqueta excede {max} caracteres.");
    }

    private static void CheckName(string? name, string where, HashSet<string> seen, List<string> msgs)
    {
        if (!Identifiers.IsValid(name, out var err)) msgs.Add($"{where}: {err}");
        else if (!seen.Add(name!)) msgs.Add($"{where}: el nombre '{name}' está repetido.");
    }

    // ---------------- Layout ----------------

    public static IEnumerable<string> ValidateLayout(EntityDef e, Layout l, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (e.IsChild) { msgs.Add("es una partida: se muestra como tabla dentro de su documento y no admite layout."); return msgs; }
        if (l.Sections.Count is 0 or > MaxSections) msgs.Add($"el layout necesita de 1 a {MaxSections} secciones.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void CheckCondition(string? expr, string where)
        {
            if (string.IsNullOrWhiteSpace(expr)) return;
            if (expr.Length > 1000) { msgs.Add($"{where}: la condición excede 1000 caracteres."); return; }
            foreach (var err in ExprAnalyzer.Analyze(expr, e, model, roles).Errors) msgs.Add($"{where} (visibleWhen): {err}");
        }
        foreach (var s in l.Sections)
        {
            var where = $"sección '{s.Name}'";
            CheckName(s.Name, where, names, msgs);
            CheckLabel(s.Label, where, msgs);
            if (s.Description?.Length > MaxDescription) msgs.Add($"{where}: la descripción excede {MaxDescription} caracteres.");
            if (s.Tab is { } tab && (string.IsNullOrWhiteSpace(tab) || tab.Length > 60)) msgs.Add($"{where}: la pestaña necesita una etiqueta de 1 a 60 caracteres.");
            if (s.Columns is not (null or 1 or 2)) msgs.Add($"{where}: «columns» es 1 o 2.");
            if (s.Fields.Count == 0) msgs.Add($"{where}: indica al menos un campo.");
            CheckCondition(s.VisibleWhen, where);
            foreach (var item in s.Fields)
            {
                var f = e.FindField(item.Field ?? "");
                if (f is null || f.IsArchived) { msgs.Add($"{where}: el campo '{item.Field}' no existe en {e.Name}."); continue; }
                if (f.Type is FieldTypes.Lines or FieldTypes.Parent or FieldTypes.State)
                    msgs.Add($"{where}: '{f.Name}' no se coloca en el layout (las partidas y el estado tienen su propio lugar).");
                if (!placed.Add(f.Name)) msgs.Add($"{where}: el campo '{f.Name}' ya está en otra sección.");
                CheckCondition(item.VisibleWhen, $"{where} › {f.Name}");
            }
        }
        // Las pestañas no se intercalan: así el orden de las secciones es el orden en pantalla.
        var tabs = l.Sections.Select(s => s.Tab ?? "").ToList();
        for (var i = 1; i < tabs.Count; i++)
            if (tabs[i] != tabs[i - 1] && tabs.Take(i - 1).Contains(tabs[i]))
                msgs.Add($"la pestaña '{tabs[i]}' aparece separada: coloca juntas sus secciones.");
        if (tabs.Any(t => t.Length > 0) && tabs.Any(t => t.Length == 0))
            msgs.Add("si una sección tiene pestaña, todas deben tenerla.");
        return msgs;
    }

    // ---------------- Vistas compartidas ----------------

    public static IEnumerable<string> ValidateViews(EntityDef e, IReadOnlyList<ViewDef> views, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (e.IsChild) { msgs.Add("es una partida: sus registros se consultan desde el documento."); return msgs; }
        if (views.Count > MaxViews) msgs.Add($"máximo {MaxViews} vistas por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in views)
        {
            var where = $"vista '{v.Name}'";
            CheckName(v.Name, where, names, msgs);
            CheckLabel(v.Label, where, msgs);
            CheckRoles(v.Roles, where, roles, msgs);
            msgs.AddRange(ValidateQuery(e, v.Columns, v.Filters, v.Sort, v.Dir, v.State).Select(m => $"{where}: {m}"));
        }
        return msgs;
    }

    /// <summary>Columnas, filtros, orden y estado de una vista (compartida o personal).</summary>
    public static IEnumerable<string> ValidateQuery(EntityDef e, IReadOnlyList<string>? columns, IReadOnlyList<ViewFilter>? filters,
        string? sort, string? dir, string? state)
    {
        if (columns is { Count: > MaxColumns }) yield return $"máximo {MaxColumns} columnas.";
        foreach (var c in columns ?? [])
            if (e.FindField(c) is not { IsArchived: false } f || f.Type is FieldTypes.Lines or FieldTypes.Parent)
                yield return $"la columna '{c}' no es un campo de {e.Name}.";
        if (filters is { Count: > FilterSql.MaxFilters }) yield return $"máximo {FilterSql.MaxFilters} filtros.";
        foreach (var f in filters ?? [])
            if (FilterSql.Validate(e, f) is { } err) yield return err;
        if (sort is not null && !SortFields.Contains(sort) && !IsSortable(e.FindField(sort)))
            yield return $"no se puede ordenar por '{sort}'.";
        if (dir is not (null or "asc" or "desc")) yield return "«dir» es asc o desc.";
        if (state is not null && e.Workflow?.FindState(state) is null) yield return $"el estado '{state}' no existe en el proceso.";
    }

    public const int MaxColumns = 30;

    /// <summary>Mismo criterio que el orden del listado (spec 001): columna propia que no sea texto largo.</summary>
    public static bool IsSortable(FieldDef? f) =>
        f is { IsArchived: false } && !FieldTypes.IsVirtual(f.Type) && f.Type is not (FieldTypes.LongText or FieldTypes.Parent);
    private static readonly HashSet<string> SortFields = new(StringComparer.OrdinalIgnoreCase) { "id", "createdAt", "updatedAt" };

    // ---------------- Reportes ----------------

    public static IEnumerable<string> ValidateReports(EntityDef e, IReadOnlyList<ReportDef> reports, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (reports.Count > MaxReports) msgs.Add($"máximo {MaxReports} reportes por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reports)
        {
            var where = $"reporte '{r.Name}'";
            CheckName(r.Name, where, names, msgs);
            CheckLabel(r.Label, where, msgs);
            if (r.Description?.Length > MaxDescription) msgs.Add($"{where}: la descripción excede {MaxDescription} caracteres.");
            CheckRoles(r.Roles, where, roles, msgs);
            var groups = r.GroupBy ?? [];
            if (groups.Count > MaxGroups) msgs.Add($"{where}: máximo {MaxGroups} agrupaciones.");
            foreach (var g in groups)
            {
                var (p, err) = Resolve(e, g.Field, model);
                if (p is null) { msgs.Add($"{where}: {err}"); continue; }
                var kind = KindOf(p.Field);
                if (kind == ValueKind.LongText) msgs.Add($"{where}: no se puede agrupar por un texto largo ('{g.Field}').");
                if (kind == ValueKind.Date && g.Period is null) msgs.Add($"{where}: agrupar por la fecha '{g.Field}' requiere «period» (day, month o year).");
                if (g.Period is not null && (kind != ValueKind.Date || !Periods.Contains(g.Period)))
                    msgs.Add($"{where}: «period» solo aplica a fechas y es day, month o year.");
                if (g.Label?.Length > MaxLabel) msgs.Add($"{where}: la etiqueta de '{g.Field}' es demasiado larga.");
            }
            if (r.Measures.Count is 0 or > MaxMeasures) msgs.Add($"{where}: indica de 1 a {MaxMeasures} medidas.");
            foreach (var m in r.Measures)
            {
                if (!MeasureFns.Contains(m.Fn ?? "")) { msgs.Add($"{where}: la medida '{m.Fn}' no existe (count, sum, avg, min, max)."); continue; }
                if (m.Label?.Length > MaxLabel) msgs.Add($"{where}: la etiqueta de una medida es demasiado larga.");
                if (m.Field is null)
                {
                    if (m.Fn != "count") msgs.Add($"{where}: «{m.Fn}» necesita un campo.");
                    continue;
                }
                var (p, err) = Resolve(e, m.Field, model);
                if (p is null) { msgs.Add($"{where}: {err}"); continue; }
                var kind = KindOf(p.Field);
                if (m.Fn is "sum" or "avg" && kind != ValueKind.Number) msgs.Add($"{where}: «{m.Fn}» solo aplica a números ('{m.Field}').");
                if (m.Fn is "min" or "max" && kind is not (ValueKind.Number or ValueKind.Date)) msgs.Add($"{where}: «{m.Fn}» solo aplica a números o fechas ('{m.Field}').");
            }
            if (!string.IsNullOrWhiteSpace(r.Filter) && RecordFilter.Check(r.Filter, e, roles) is { } ferr) msgs.Add($"{where} (filter): {ferr}");
            if (r.OrderBy is not (null or "group" or "measure")) msgs.Add($"{where}: «orderBy» es group o measure.");
            if (r.Dir is not (null or "asc" or "desc")) msgs.Add($"{where}: «dir» es asc o desc.");
            if (r.Limit is < 1 or > MaxRows) msgs.Add($"{where}: «limit» va de 1 a {MaxRows}.");
            if (r.Chart is not null && (!Charts.Contains(r.Chart) || groups.Count == 0)) msgs.Add($"{where}: «chart» es bar, line o pie y requiere una agrupación.");
        }
        return msgs;
    }

    // ---------------- Tableros ----------------

    public static IEnumerable<string> ValidateDashboard(DashboardDef d, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        CheckLabel(d.Label, "tablero", msgs);
        if (d.Description?.Length > MaxDescription) msgs.Add($"la descripción excede {MaxDescription} caracteres.");
        CheckRoles(d.Roles, "tablero", roles, msgs);
        if (d.Widgets.Count is 0 or > MaxWidgets) msgs.Add($"el tablero necesita de 1 a {MaxWidgets} tarjetas.");
        for (var i = 0; i < d.Widgets.Count; i++)
        {
            var w = d.Widgets[i];
            var where = $"tarjeta {i + 1}";
            if (!WidgetTypes.Contains(w.Type ?? "")) { msgs.Add($"{where}: el tipo '{w.Type}' no existe (kpi, chart, table)."); continue; }
            if (w.Label?.Length > MaxLabel) msgs.Add($"{where}: la etiqueta es demasiado larga.");
            if (!model.TryGetValue(w.Entity ?? "", out var e) || e.IsArchived) { msgs.Add($"{where}: el catálogo '{w.Entity}' no existe."); continue; }
            var r = e.Reports?.FirstOrDefault(x => string.Equals(x.Name, w.Report, StringComparison.OrdinalIgnoreCase));
            if (r is null) { msgs.Add($"{where}: el reporte '{w.Report}' no existe en {e.Name}."); continue; }
            var grouped = r.GroupBy is { Count: > 0 };
            if (w.Type == "kpi" && grouped) msgs.Add($"{where}: un indicador usa un reporte sin agrupación ('{r.Name}' agrupa).");
            if (w.Type == "chart" && !grouped) msgs.Add($"{where}: una gráfica usa un reporte con agrupación.");
            if (w.Measure is { } m && (m < 0 || m >= r.Measures.Count)) msgs.Add($"{where}: la medida {m} no existe en '{r.Name}'.");
        }
        return msgs;
    }
}
