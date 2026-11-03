namespace Erp.Api.Metadata;

/// <summary>
/// Spec 053: validación estática de la autorización por matriz (<see cref="WorkflowApproval"/>). Corre en la pasada global del
/// plan porque los atajos pueden venir de otro catálogo (una automatización o una tarea que ejecuta la transición de salida, o que
/// asigna la lista de autorizadores). Es pura para probarla sin base de datos.
/// </summary>
public static class ApprovalRules
{
    /// <summary>Tipos de campo que sirven de condición de una regla (se comparan por igualdad con una lista de valores).</summary>
    public static readonly IReadOnlySet<string> ConditionTypes = new HashSet<string>
        { FieldTypes.Reference, FieldTypes.Choice, FieldTypes.Boolean, FieldTypes.User };

    /// <summary>Errores de la declaración del catálogo <paramref name="e"/> (vacío si no declara autorización).</summary>
    public static IEnumerable<string> Validate(EntityDef e, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (e.IsArchived || e.Workflow?.Approval is not { } a) yield break;
        var w = e.Workflow;
        var where = $"{e.Name} › autorización";
        if (e.IsChild) { yield return $"{where}: {e.Name} es una partida; la autorización se declara en su documento."; yield break; }
        if (!e.PerCompany) yield return $"{where}: la matriz es por empresa; declara el catálogo por empresa (perCompany).";
        if (w.FindState(a.State) is not { } st) yield return $"{where}: el estado '{a.State}' no existe.";
        else if (string.Equals(w.Initial, a.State, StringComparison.OrdinalIgnoreCase))
            yield return $"{where}: el estado '{a.State}' es el inicial; un documento entra a autorización por una transición.";
        // Revisión adversarial 053 (F-02): lo que se autoriza no cambia mientras se autoriza.
        else if (st.Editable) yield return $"{where}: el estado '{a.State}' debe ser no editable (editable: false): lo autorizado es lo que se vio.";
        foreach (var (name, what) in new[] { (a.Approve, "autorizar"), (a.Reject, "rechazar") })
        {
            if (w.FindTransition(name) is not { } t) { yield return $"{where}: la transición para {what} '{name}' no existe."; continue; }
            if (!t.System) yield return $"{where}: '{t.Name}' ({what}) debe ser del sistema (system: true): solo la ejecuta la matriz.";
            if (!t.From.Contains(a.State, StringComparer.OrdinalIgnoreCase) || t.From.Count != 1)
                yield return $"{where}: '{t.Name}' ({what}) debe salir solo de '{a.State}'.";
            if (string.Equals(t.To, a.State, StringComparison.OrdinalIgnoreCase))
                yield return $"{where}: '{t.Name}' ({what}) debe llevar a otro estado.";
            // Revisión adversarial 053 (F-04): la matriz decide; la transición no agrega condiciones ni campos que no se piden.
            if (t.When is not null || t.RequiredFields is { Count: > 0 })
                yield return $"{where}: '{t.Name}' ({what}) no lleva «cuándo» ni campos obligatorios: la decide la matriz.";
        }
        // Revisión adversarial 053 (F-03): al estado autorizado solo se llega por la matriz.
        if (w.FindTransition(a.Approve) is { } ap)
            foreach (var t in w.Transitions.Where(t => !string.Equals(t.Name, ap.Name, StringComparison.OrdinalIgnoreCase)
                                                       && string.Equals(t.To, ap.To, StringComparison.OrdinalIgnoreCase)))
                yield return $"{where}: '{t.Name}' también lleva a '{ap.To}'; a ese estado solo se llega autorizando por la matriz.";
        if (string.Equals(a.Approve, a.Reject, StringComparison.OrdinalIgnoreCase))
            yield return $"{where}: autorizar y rechazar son transiciones distintas.";
        if (a.Amount is not null && (e.FindField(a.Amount) is not { IsArchived: false } amount || !Expressions.ExprAnalyzer.IsNumeric(amount)))
            yield return $"{where}: el monto '{a.Amount}' debe ser un campo numérico de {e.Name}.";
        var conditions = a.Conditions ?? [];
        if (conditions.Count > WorkflowApproval.MaxConditions) yield return $"{where}: máximo {WorkflowApproval.MaxConditions} campos de condición.";
        if (conditions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != conditions.Count) yield return $"{where}: un campo de condición está repetido.";
        foreach (var c in conditions)
            if (e.FindField(c) is not { IsArchived: false } cf || !ConditionTypes.Contains(cf.Type))
                yield return $"{where}: la condición '{c}' debe ser un campo de {e.Name} de tipo referencia, opción, sí/no o persona.";
        if (e.FindField(a.Approvers) is not { IsArchived: false, Type: FieldTypes.MultiUser, Editable: false })
            yield return $"{where}: los autorizadores '{a.Approvers}' deben ser un campo de varias personas no editable (editable: false).";
        else
            foreach (var t in w.Transitions)
                if (t.Set?.Any(s => string.Equals(s.Field, a.Approvers, StringComparison.OrdinalIgnoreCase)) == true)
                    yield return $"{where}: la transición '{t.Name}' asigna '{a.Approvers}'; esa lista la mantiene la matriz.";

        // Atajos desde cualquier catálogo: ejecutar la salida o escribir la lista con la autoridad del motor.
        foreach (var src in model.Values.Where(x => !x.IsArchived))
        {
            var actions = (src.Automations ?? []).SelectMany(x => x.Actions.Select(act => (act, $"la automatización {src.Name}.{x.Name}")))
                .Concat((src.Schedules ?? []).SelectMany(x => x.Actions.Select(act => (act, $"la tarea {src.Name}.{x.Name}"))));
            foreach (var (act, who) in actions)
            {
                if (AutomationRules.Scope(src, act, model) is not { } scope || AutomationRules.Target(scope, act, model) is not { } target) continue;
                if (!string.Equals(target.Name, e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (act.Transition is not null && a.IsExit(act.Transition))
                    yield return $"{where}: {who} ejecuta '{act.Transition}'; autorizar o rechazar solo lo hace la matriz.";
                if (act.Set?.Any(s => string.Equals(s.Field, a.Approvers, StringComparison.OrdinalIgnoreCase)) == true)
                    yield return $"{where}: {who} asigna '{a.Approvers}'; esa lista la mantiene la matriz.";
            }
        }
    }
}
