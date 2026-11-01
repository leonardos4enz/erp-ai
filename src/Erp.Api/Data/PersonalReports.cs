using System.Text.Json;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Data;

/// <summary>
/// Spec 041: definición de un reporte personal o explorado. La misma forma que <see cref="ReportDef"/> (agrupaciones, medidas,
/// gráfica, orden, límite) con filtros estructurados (<see cref="ViewFilter"/>, como las vistas) y sin roles ni filtro de
/// expresión.
/// </summary>
public sealed record PersonalReportDef
{
    public string? Entity { get; init; }
    public IReadOnlyList<ReportGroup>? GroupBy { get; init; }
    public IReadOnlyList<ReportMeasure>? Measures { get; init; }
    public IReadOnlyList<ViewFilter>? Filters { get; init; }
    public string? OrderBy { get; init; }
    public string? Dir { get; init; }
    public int? Limit { get; init; }
    public string? Chart { get; init; }
    public ReportChartOptions? ChartOptions { get; init; }
}

/// <param name="Parent">Si es una partida, la etiqueta de su documento.</param>
public sealed record ReportSource(string Entity, string Label, string? Group, string? Parent);
/// <param name="Kind">text, longtext, number, date, boolean o id (lo que admite agrupar y medir).</param>
public sealed record ReportSourceField(string Path, string Label, string Type, string Kind);
public sealed record ReportSourceFields(string Entity, string Label, IReadOnlyList<ReportSourceField> Fields);

/// <summary>
/// Spec 041: valida una definición personal **contra quien la corre**, en cada corrida (lo que dejó de ver deja de correr).
/// Lo que no ve responde igual que lo que no existe: ni el catálogo ni un campo son un oráculo.
/// </summary>
public static class PersonalReports
{
    public const string ReportName = "Personal";
    public const string DefaultLabel = "Exploración";
    public const int MaxDefinition = 20_000;
    public const int MaxSourceFields = 2_000;
    private static readonly IReadOnlySet<string> NoRoles = new HashSet<string>();

    public static ReportDef ToReport(PersonalReportDef d, string? label) => new()
    {
        Name = ReportName,
        Label = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label.Trim(),
        GroupBy = d.GroupBy,
        Measures = d.Measures ?? [],
        OrderBy = d.OrderBy,
        Dir = d.Dir,
        Limit = d.Limit,
        Chart = d.Chart,
        ChartOptions = d.ChartOptions,
    };

    /// <summary>La definición de un reporte del modelo para explorarlo (sin roles ni filtro fijo: no se copian).</summary>
    public static PersonalReportDef FromReport(EntityDef e, ReportDef r) => new()
    {
        Entity = e.Name, GroupBy = r.GroupBy, Measures = r.Measures, OrderBy = r.OrderBy, Dir = r.Dir, Limit = r.Limit,
        Chart = r.Chart, ChartOptions = r.ChartOptions,
    };

    /// <summary>El catálogo, si existe, está activo y el usuario lee su raíz (una partida, su documento); si no, 404 igual.</summary>
    public static EntityDef Catalog(string? entity, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (!model.TryGetValue(entity ?? "", out var e) || e.IsArchived || Root(e, model) is not { IsArchived: false } root
            || !user.Access(root.Name).Read)
            throw new NotFound("El catálogo no existe.");
        return e;
    }

    /// <summary>
    /// ¿El usuario puede agrupar o medir por la ruta? Resuelve, ve el campo y cada referencia del camino, y lee cada catálogo
    /// al que llega (salvo el campo para mostrar del último: lo que ofrece el buscador). La misma regla que
    /// <see cref="ReportService.Available"/>.
    /// </summary>
    public static bool CanUse(EntityDef e, string? path, UserContext user, IReadOnlyDictionary<string, EntityDef> model) =>
        InterfaceRules.Resolve(e, path, model).Path is { } p && InterfaceRules.CanSee(p, user) && ReportService.CanReadPath(p, user, model);

    /// <summary>Valida la definición para el usuario; devuelve el catálogo y el reporte listos para <see cref="ReportService"/>.</summary>
    public static (EntityDef Entity, ReportDef Report) Check(PersonalReportDef? d, string? label, UserContext user,
        IReadOnlyDictionary<string, EntityDef> model)
    {
        if (d is null) throw Invalid(["Falta la definición del reporte."]);
        // Regresión (revisión adversarial 041, F-E): un elemento nulo en una lista daba 500 (NullReferenceException).
        if (d.GroupBy?.Any(g => g?.Field is null) == true || d.Measures?.Any(m => m?.Fn is null) == true
            || d.Filters?.Any(f => f?.Field is null || f.Op is null) == true)
            throw Invalid(["Hay una agrupación, una medida o un filtro vacío."]);
        var e = Catalog(d.Entity, user, model);
        var r = ToReport(d, label);
        var msgs = new List<string>();
        if (JsonSerializer.Serialize(d, MetadataStore.Json).Length > MaxDefinition)
            throw Invalid([$"La definición excede {MaxDefinition} caracteres."]);
        if (label?.Trim().Length > InterfaceRules.MaxLabel) msgs.Add($"el nombre excede {InterfaceRules.MaxLabel} caracteres.");
        // Primero lo que no ve o no existe, con el mismo texto: la validación del modelo distinguiría «no existe» de «oculto».
        var paths = (r.GroupBy ?? []).Select(g => g.Field)
            .Concat(r.Measures.Where(m => m.Fn != "calc").Select(m => m.Field).OfType<string>());
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!CanUse(e, path, user, model)) msgs.Add($"'{path}' no existe o no puedes verlo.");
        foreach (var f in d.Filters ?? [])
            if (e.FindField(f.Field ?? "") is not { IsArchived: false } fd || FieldTypes.IsVirtual(fd.Type) || !FieldAccess.CanSee(e, fd, user))
                msgs.Add($"el filtro usa '{f.Field}', que no existe o no puedes ver.");
        if (msgs.Count == 0)
        {
            var prefix = $"reporte '{ReportName}': ";
            msgs.AddRange(InterfaceRules.ValidateReports(e, [r], model, NoRoles)
                .Select(m => m.StartsWith(prefix, StringComparison.Ordinal) ? m[prefix.Length..] : m));
            msgs.AddRange(InterfaceRules.ValidateQuery(e, null, d.Filters, null, null, null));
        }
        if (msgs.Count > 0) throw Invalid(msgs.Distinct().Select(m => char.ToUpperInvariant(m[0]) + m[1..]));
        return (e, r);
    }

    /// <summary>Catálogos sobre los que el usuario puede armar un reporte: los que lee y sus partidas.</summary>
    public static IReadOnlyList<ReportSource> Sources(UserContext user, IReadOnlyDictionary<string, EntityDef> model) =>
        model.Values.Where(e => !e.IsArchived && Root(e, model) is { IsArchived: false } root && user.Access(root.Name).Read)
            .Select(e => new ReportSource(e.Name, e.LabelPlural, Root(e, model)!.Group,
                e.IsChild ? Root(e, model)!.Label : null))
            .OrderBy(s => s.Group ?? "").ThenBy(s => s.Parent ?? s.Label, StringComparer.CurrentCulture).ThenBy(s => s.Parent is not null)
            .ToList();

    /// <summary>
    /// Campos para agrupar y medir: propios y a uno o dos saltos (por referencia o por el documento), solo los que el usuario
    /// puede usar (<see cref="CanUse"/>). No se baja por una referencia que no ve ni por un catálogo que no lee.
    /// </summary>
    public static ReportSourceFields Fields(string entity, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        var e = Catalog(entity, user, model);
        var list = new List<ReportSourceField>();
        void Walk(EntityDef current, string prefix, string labelPrefix, int depth)
        {
            foreach (var f in current.Fields)
            {
                if (list.Count >= MaxSourceFields) return;
                if (f.IsArchived || FieldTypes.IsVirtual(f.Type)) continue;
                var path = prefix + f.Name;
                if (!CanUse(e, path, user, model)) continue;
                var label = labelPrefix + f.Label;
                if (f.Type != FieldTypes.Parent)
                    list.Add(new ReportSourceField(path, label, f.Type == FieldTypes.Formula ? f.ResultType ?? FieldTypes.Text : f.Type,
                        InterfaceRules.KindOf(f).ToString().ToLowerInvariant()));
                if (depth < InterfaceRules.MaxHops && f.Type is FieldTypes.Reference or FieldTypes.Parent
                    && model.TryGetValue(f.ReferenceEntity ?? "", out var next) && !next.IsArchived
                    && user.Access(Root(next, model)?.Name ?? next.Name).Read)
                    Walk(next, path + ".", label + " › ", depth + 1);
            }
        }
        Walk(e, "", "", 0);
        return new ReportSourceFields(e.Name, e.LabelPlural, list);
    }

    private static EntityDef? Root(EntityDef e, IReadOnlyDictionary<string, EntityDef> model) =>
        e.IsChild ? model.GetValueOrDefault(e.ParentEntity ?? "") : e;

    private static ValidationFailed Invalid(IEnumerable<string> msgs) =>
        new(new Dictionary<string, string[]> { ["report"] = msgs.ToArray() }, "El reporte no es válido.");
}
