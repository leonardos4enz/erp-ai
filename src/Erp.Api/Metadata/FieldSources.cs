using System.Text.Json.Serialization;

namespace Erp.Api.Metadata;

/// <summary>
/// Spec 048 (solo salida, <c>FieldDef.FilledBy</c>): algo del modelo que le asigna valor a un campo. <c>Kind</c> es
/// <see cref="FieldSources.Automation"/> (de este catálogo o de otro; <c>Action</c> dice cómo: update, create, capture, rollup,
/// cost) o <see cref="FieldSources.Transition"/> (el <c>set</c> de una transición de este catálogo).
/// </summary>
public sealed record FieldSource(string Kind, string Entity, string EntityLabel, string Name, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? On = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StateLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransitionLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null);

/// <summary>
/// Spec 048 (H3): de dónde sale el valor de cada campo de un catálogo, leído de los metadatos: las automatizaciones (del mismo
/// catálogo o de otros que la persona puede ver) cuyas acciones lo asignan, y las transiciones con <c>set</c>. Pura, como
/// <see cref="AutomationRules"/>: usa sus mismos ámbito y destino, así que dice lo que el motor hace.
/// </summary>
public static class FieldSources
{
    public const string Automation = "automation";
    public const string Transition = "transition";

    /// <param name="canSee">
    /// Si la persona puede ver el catálogo de una automatización (las de uno que no ve no aparecen), y las partidas sobre las que
    /// actúa (spec 049: una partida, solo si ve su campo de partidas).
    /// </param>
    public static IReadOnlyDictionary<string, IReadOnlyList<FieldSource>> For(EntityDef target, IReadOnlyDictionary<string, EntityDef> model, Func<EntityDef, bool> canSee)
    {
        var map = new Dictionary<string, List<FieldSource>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? field, FieldSource source)
        {
            if (field is null || target.FindField(field) is not { } f) return;
            if (!map.TryGetValue(f.Name, out var list)) map[f.Name] = list = [];
            if (!list.Contains(source)) list.Add(source);
        }
        bool Is(EntityDef? e) => e is not null && string.Equals(e.Name, target.Name, StringComparison.OrdinalIgnoreCase);

        foreach (var t in target.Workflow?.Transitions ?? [])
            foreach (var s in t.Set ?? [])
                Add(s.Field, new(Transition, target.Name, target.Label, t.Name, t.Label));

        foreach (var source in model.Values.Where(e => !e.IsArchived && e.Automations is { Count: > 0 }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Is(source) && !canSee(source)) continue;
            foreach (var auto in source.Automations!)
            {
                var state = auto.On == AutomationEvents.EnterState ? source.Workflow?.FindState(auto.State)?.Label ?? auto.State : null;
                var transition = auto.On == AutomationEvents.Transition
                    ? source.Workflow?.Transitions.FirstOrDefault(t => string.Equals(t.Name, auto.Transition, StringComparison.OrdinalIgnoreCase))?.Label ?? auto.Transition
                    : null;
                FieldSource Src(string action) => new(Automation, source.Name, source.Label, auto.Name, auto.Label, auto.On, state, transition, action);
                foreach (var a in auto.Actions)
                {
                    if (AutomationRules.Scope(source, a, model) is not { } scope) continue;
                    // Spec 049 (auditoría 9, O-03): una acción sobre las partidas del documento solo se nombra a quien ve esas partidas.
                    if (scope != source && !Is(scope) && !canSee(scope)) continue;
                    var dest = AutomationRules.Target(scope, a, model);
                    switch (a.Type)
                    {
                        case AutomationActions.Update:
                            if (Is(dest)) foreach (var s in a.Set ?? []) Add(s.Field, Src(a.Type));
                            break;
                        case AutomationActions.Create:
                            if (Is(dest)) foreach (var s in a.Set ?? []) Add(s.Field, Src(a.Type));
                            if (Is(scope)) foreach (var s in a.Capture ?? []) Add(s.Field, Src("capture"));
                            foreach (var lt in a.CreateLines ?? [])
                                if (dest?.FindField(lt.Field) is { ChildEntity: { } child } && string.Equals(child, target.Name, StringComparison.OrdinalIgnoreCase))
                                    foreach (var s in lt.Set) Add(s.Field, Src(a.Type));
                            break;
                        case AutomationActions.Rollup:
                            if (Is(dest)) Add(a.Field, Src(a.Type));
                            break;
                        case AutomationActions.Cost:
                            if (Is(dest)) { Add(a.Field, Src(a.Type)); Add(a.QuantityField, Src(a.Type)); }
                            if (Is(scope)) { Add(a.CostField, Src(a.Type)); Add(a.UnitCostField, Src(a.Type)); }
                            break;
                    }
                }
            }
        }
        return map.ToDictionary(p => p.Key, p => (IReadOnlyList<FieldSource>)p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
