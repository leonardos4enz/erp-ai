namespace Erp.Api.Metadata;

/// <summary>Spec 024: un módulo de navegación tal como lo ve un usuario.</summary>
public sealed record AppSummary(string Name, string Label, string Icon, string? Description, int Order, bool Declared,
    IReadOnlyList<string> Entities)
{
    /// <summary>Solo en la lista completa del modelador.</summary>
    public bool? IsArchived { get; init; }
}

/// <summary>
/// Spec 024: módulos efectivos. Es puro, como <see cref="PlanCompiler"/>: agrupa los catálogos por <c>group</c> (sin
/// distinguir mayúsculas), toma la declaración no archivada del mismo nombre o arma una implícita, y omite los módulos sin
/// catálogos visibles. Los catálogos sin grupo forman «General».
/// </summary>
public static class Apps
{
    public const string General = "General";
    public const string DefaultIcon = "layout-grid";
    public const string ImplicitIcon = "folder";
    public const int DefaultOrder = 100;
    private const string GeneralDescription = "Catálogos que no pertenecen a otro módulo.";
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Nombre del módulo de un catálogo.</summary>
    public static string Of(EntityDef e) => string.IsNullOrWhiteSpace(e.Group) ? General : e.Group.Trim();

    public static IReadOnlyList<AppSummary> Effective(IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlyDictionary<string, AppDef> apps, Func<EntityDef, bool> visible)
    {
        var groups = model.Values
            .Where(e => !e.IsArchived && !e.IsChild && visible(e))
            .GroupBy(Of, Ci);
        return groups.Select(g => Summary(g.Key, apps.TryGetValue(g.Key, out var d) && !d.IsArchived ? d : null,
                // Documentos con proceso primero, después por nombre (el orden del menú).
                g.OrderBy(e => e.Workflow is null).ThenBy(e => e.LabelPlural, StringComparer.Create(new("es-MX"), true)).Select(e => e.Name).ToList()))
            .OrderBy(a => a.Order)
            .ThenBy(a => !a.Declared && Ci.Equals(a.Name, General)) // General implícito al final de su orden
            .ThenBy(a => a.Label, StringComparer.Create(new("es-MX"), true))
            .ToList();
    }

    /// <summary>Spec 024 (modelador): todos los declarados (también archivados) y los implícitos de todo el modelo.</summary>
    public static IReadOnlyList<AppSummary> All(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, AppDef> apps)
    {
        var effective = Effective(model, apps, _ => true).ToList();
        foreach (var d in apps.Values.Where(d => !effective.Any(a => a.Declared && Ci.Equals(a.Name, d.Name))))
        {
            // Un declarado sin catálogos o archivado (sus catálogos aparecen como implícito, si los hay).
            var s = Summary(d.Name, d, []) with { IsArchived = d.IsArchived };
            effective.Add(s);
        }
        return effective.Select(a => a.IsArchived is null ? a with { IsArchived = false } : a).ToList();
    }

    private static AppSummary Summary(string name, AppDef? d, IReadOnlyList<string> entities) => d is not null
        ? new(d.Name, d.Label, d.Icon ?? DefaultIcon, d.Description, d.Order ?? DefaultOrder, true, entities)
        : Ci.Equals(name, General)
            ? new(General, General, DefaultIcon, GeneralDescription, int.MaxValue, false, entities)
            : new(name, name, ImplicitIcon, null, DefaultOrder, false, entities);
}
