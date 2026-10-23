using Erp.Api.Expressions;

namespace Erp.Api.Metadata;

/// <summary>
/// Validación estática de automatizaciones (spec 005), como la de fórmulas: campos, destinos, transiciones,
/// expresiones en su ámbito y ciclos entre catálogos. Es pura para poder probarla sin base de datos.
/// </summary>
public static class AutomationRules
{
    public const int MaxAutomations = 20;
    public const int MaxActions = 20;

    /// <summary>Ámbito de una acción: el registro o, con <c>lines</c>, la partida.</summary>
    public static EntityDef? Scope(EntityDef e, AutomationAction a, IReadOnlyDictionary<string, EntityDef> model) =>
        a.Lines is null ? e
        : e.FindField(a.Lines) is { Type: FieldTypes.Lines, IsArchived: false, ChildEntity: { } c } && model.TryGetValue(c, out var child) ? child
        : null;

    /// <summary>Catálogo destino de una acción (para update/transition/rollup por referencia, el de <c>target</c>).</summary>
    public static EntityDef? Target(EntityDef scope, AutomationAction a, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (a.Type == AutomationActions.Create || (a.Type == AutomationActions.Rollup && a.Target is null))
            return model.TryGetValue(a.Entity ?? "", out var t) && !t.IsArchived ? t : null;
        return scope.FindField(a.Target ?? "") is { Type: FieldTypes.Reference, IsArchived: false, ReferenceEntity: { } r } &&
               model.TryGetValue(r, out var target) && !target.IsArchived ? target : null;
    }

    /// <summary>Documento que se guarda al modificar <paramref name="e"/> (una partida se guarda con su documento).</summary>
    public static EntityDef Root(EntityDef e, IReadOnlyDictionary<string, EntityDef> model) =>
        e.IsChild && model.TryGetValue(e.ParentEntity!, out var p) ? p : e;

    public static IEnumerable<string> Validate(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        foreach (var auto in e.Automations ?? [])
        {
            var where = $"{e.Name} › automatización {auto.Name}";
            void Check(string? expr, EntityDef scope, string what)
            {
                if (string.IsNullOrWhiteSpace(expr)) return;
                foreach (var err in ExprAnalyzer.Analyze(expr, scope, model, roles).Errors) msgs.Add($"{where} › {what}: {err}");
            }

            if (!AutomationEvents.All.Contains(auto.On ?? ""))
                msgs.Add($"{where}: evento '{auto.On}' no válido ({string.Join(", ", AutomationEvents.All)}).");
            if (auto.On == AutomationEvents.EnterState && e.Workflow?.FindState(auto.State) is null)
                msgs.Add($"{where}: el estado '{auto.State}' no existe en el proceso de {e.Name}.");
            if (auto.On == AutomationEvents.Transition && e.Workflow?.Transitions.Any(t => string.Equals(t.Name, auto.Transition, StringComparison.OrdinalIgnoreCase)) != true)
                msgs.Add($"{where}: la transición '{auto.Transition}' no existe en el proceso de {e.Name}.");
            Check(auto.When, e, "cuándo");
            if (auto.Actions.Count is 0 or > MaxActions) msgs.Add($"{where}: necesita de 1 a {MaxActions} acciones.");

            for (var i = 0; i < auto.Actions.Count && i < MaxActions; i++)
            {
                var a = auto.Actions[i];
                var at = $"acción {i + 1}";
                if (!AutomationActions.All.Contains(a.Type ?? "")) { msgs.Add($"{where} › {at}: tipo '{a.Type}' no válido ({string.Join(", ", AutomationActions.All)})."); continue; }
                var scope = Scope(e, a, model);
                if (scope is null) { msgs.Add($"{where} › {at}: '{a.Lines}' no es un campo de partidas de {e.Name}."); continue; }
                Check(a.When, scope, $"{at} (cuándo)");
                msgs.AddRange(ValidateAction(a, scope, model).Select(m => $"{where} › {at}: {m}"));
                foreach (var s in a.Set ?? []) Check(s.Value, scope, $"{at} › {s.Field}");
                Check(a.Filter, scope, $"{at} (filtro)");
            }
        }
        return msgs;
    }

    private static IEnumerable<string> ValidateAction(AutomationAction a, EntityDef scope, IReadOnlyDictionary<string, EntityDef> model)
    {
        var target = Target(scope, a, model);
        var byMatch = a.Type == AutomationActions.Rollup && a.Target is null;
        if (target is null)
        {
            yield return a.Type == AutomationActions.Create || byMatch
                ? $"el catálogo '{a.Entity}' no existe."
                : $"'{a.Target}' no es un campo de referencia de {scope.Name}.";
            yield break;
        }

        switch (a.Type)
        {
            case AutomationActions.Update:
                if (a.Set is not { Count: > 0 }) yield return "indica los campos a asignar (set).";
                foreach (var m in WritableFields(target, a.Set)) yield return m;
                break;

            case AutomationActions.Create:
                if (target.IsChild) { yield return $"{target.Name} es una partida: no se crea por separado."; break; }
                foreach (var m in WritableFields(target, a.Set)) yield return m;
                foreach (var m in MissingRequired(target, (a.Set ?? []).Select(s => s.Field))) yield return m;
                break;

            case AutomationActions.Transition:
                if (target.Workflow is null) { yield return $"{target.Name} no tiene proceso."; break; }
                if (!target.Workflow.Transitions.Any(t => string.Equals(t.Name, a.Transition, StringComparison.OrdinalIgnoreCase)))
                    yield return $"la transición '{a.Transition}' no existe en {target.Name}.";
                break;

            case AutomationActions.Rollup:
                if (target.FindField(a.Field ?? "") is not { IsArchived: false } tf || tf.ReadOnly || !ExprAnalyzer.IsNumeric(tf))
                    yield return $"'{a.Field}' debe ser un campo numérico capturable de {target.Name} (recibe la suma).";
                if (scope.FindField(a.Sum ?? "") is not { IsArchived: false } sf || !ExprAnalyzer.IsNumeric(sf))
                    yield return $"'{a.Sum}' debe ser un campo numérico de {scope.Name} (lo que se suma).";
                if (!byMatch)
                {
                    if (a.Match is { Count: > 0 } || a.CreateMissing) yield return "usa target o entity + match, no ambos.";
                    break;
                }
                if (target.IsChild) yield return $"{target.Name} es una partida: la suma por coincidencia necesita un catálogo raíz.";
                if (a.Match is not { Count: > 0 }) { yield return "indica cómo encontrar el destino (match)."; break; }
                foreach (var m in a.Match)
                {
                    var df = target.FindField(m.Field ?? "");
                    var of = scope.FindField(m.Source ?? "");
                    if (df is null || df.IsArchived || FieldTypes.IsVirtual(df.Type)) yield return $"'{m.Field}' no es un campo de {target.Name}.";
                    else if (of is null || of.IsArchived || FieldTypes.IsVirtual(of.Type)) yield return $"'{m.Source}' no es un campo de {scope.Name}.";
                    else if (df.Type != of.Type || !string.Equals(df.ReferenceEntity, of.ReferenceEntity, StringComparison.OrdinalIgnoreCase))
                        yield return $"'{m.Field}' y '{m.Source}' no son del mismo tipo.";
                }
                if (a.CreateMissing)
                    foreach (var m in MissingRequired(target, a.Match.Select(m => m.Field).Append(a.Field ?? ""))) yield return m;
                break;
        }
    }

    private static IEnumerable<string> WritableFields(EntityDef target, IReadOnlyList<FieldAssignment>? set)
    {
        foreach (var s in set ?? [])
            if (target.FindField(s.Field) is not { IsArchived: false } f || f.ReadOnly || f.Type == FieldTypes.Lines)
                yield return $"no se puede asignar '{s.Field}' en {target.Name}.";
    }

    private static IEnumerable<string> MissingRequired(EntityDef target, IEnumerable<string> provided)
    {
        var given = provided.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in target.ActiveFields.Where(f => f.Required && !f.ReadOnly && f.Type != FieldTypes.Lines && f.DefaultValue is null))
            if (!given.Contains(f.Name)) yield return $"falta asignar '{f.Name}', obligatorio en {target.Name}.";
    }

    /// <summary>Eventos que una acción dispara en el catálogo que guarda.</summary>
    public static IEnumerable<(EntityDef Entity, string Event)> Effects(EntityDef e, AutomationAction a, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (Scope(e, a, model) is not { } scope || Target(scope, a, model) is not { } target) yield break;
        var root = Root(target, model);
        switch (a.Type)
        {
            case AutomationActions.Update: yield return (root, AutomationEvents.Update); break;
            case AutomationActions.Create: yield return (target, AutomationEvents.Create); break;
            case AutomationActions.Transition:
                yield return (target, AutomationEvents.Transition);
                yield return (target, AutomationEvents.EnterState);
                break;
            case AutomationActions.Rollup:
                yield return (root, AutomationEvents.Update);
                if (a.CreateMissing) yield return (target, AutomationEvents.Create);
                break;
        }
    }

    /// <summary>Ciclo en el grafo catálogo → catálogo (solo aristas que disparan automatizaciones), o nulo.</summary>
    public static IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, EntityDef> model)
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in model.Values.Where(e => !e.IsArchived && e.Automations is { Count: > 0 }))
        {
            var to = edges[e.Name] = new(StringComparer.OrdinalIgnoreCase);
            foreach (var a in e.Automations!.SelectMany(x => x.Actions))
            foreach (var (target, ev) in Effects(e, a, model))
                if (target.Automations?.Any(x => x.On == ev) == true) to.Add(target.Name);
        }

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 visitando, 2 listo
        var path = new List<string>();
        foreach (var start in edges.Keys)
            if (Visit(start) is { } cycle) return cycle;
        return null;

        List<string>? Visit(string n)
        {
            if (state.TryGetValue(n, out var s))
                return s == 1 ? [.. path.Skip(path.IndexOf(n)), n] : null;
            state[n] = 1;
            path.Add(n);
            foreach (var next in edges.GetValueOrDefault(n) ?? [])
                if (Visit(next) is { } c) return c;
            path.RemoveAt(path.Count - 1);
            state[n] = 2;
            return null;
        }
    }
}
