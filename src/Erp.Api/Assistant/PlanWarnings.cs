using Erp.Api.Metadata;

namespace Erp.Api.Assistant;

/// <summary>
/// Spec 011 (H2 CA2): lo que merece la atención de quien confirma una propuesta del asistente. No bloquea: el compilador
/// decide si el plan es válido; esto solo destaca cambios destructivos, de seguridad o que salen del registro, y lo que
/// una operación set* quita sin decirlo (reglas, vistas, reportes, automatizaciones o webhooks que el plan no repite).
/// </summary>
public static class PlanWarnings
{
    public static IReadOnlyList<string> For(ChangePlan plan, IReadOnlyDictionary<string, EntityDef> model)
    {
        string Label(string entity) => model.TryGetValue(entity, out var e) ? e.Label : entity;
        var list = new List<string>();
        // Las operaciones set* reemplazan la lista completa: lo que el catálogo tenía y el plan no repite, se quita.
        void Removed<T>(string entity, IEnumerable<T>? before, IEnumerable<T> after, Func<T, string> name, string what)
        {
            var kept = after.Select(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = (before ?? []).Select(name).Where(n => !kept.Contains(n)).ToList();
            if (gone.Count > 0) list.Add($"Quita {what} de «{Label(entity)}»: {string.Join(", ", gone)}.");
        }
        EntityDef? Current(string entity) => model.TryGetValue(entity, out var e) ? e : null;
        foreach (var op in plan.Operations)
        {
            switch (op)
            {
                case ArchiveEntityOp a:
                    list.Add($"Archiva el catálogo «{Label(a.Entity)}»: deja de mostrarse (sus datos se conservan).");
                    break;
                case ArchiveFieldOp a:
                    list.Add($"Archiva el campo «{a.Field}» de «{Label(a.Entity)}» (sus datos se conservan).");
                    break;
                case RestoreEntityOp r:
                    list.Add($"Reactiva el catálogo «{Label(r.Entity)}»: vuelve a mostrarse con los registros que conservó.");
                    break;
                case RestoreFieldOp r:
                    list.Add($"Reactiva el campo «{r.Field}» de «{Label(r.Entity)}»: vuelve a verse con los datos que conservó.");
                    break;
                case ArchiveDashboardOp a:
                    list.Add($"Archiva el tablero «{a.Name}».");
                    break;
                case ArchiveAppOp a:
                    list.Add($"Archiva el módulo «{a.Name}»: sus catálogos siguen en el menú bajo un módulo con su nombre, sin ícono ni descripción.");
                    break;
                case SetSecurityOp s:
                    list.Add(s.Security is null
                        ? $"Quita la seguridad por campo y por registro de «{Label(s.Entity)}»."
                        : $"Cambia quién ve y modifica campos y registros de «{Label(s.Entity)}».");
                    break;
                case SetWorkflowOp w when w.Workflow.Transitions.Any(t => t.Roles is { Count: > 0 }):
                    var roles = w.Workflow.Transitions.SelectMany(t => t.Roles ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
                    list.Add($"El proceso de «{Label(w.Entity)}» limita transiciones a los roles: {string.Join(", ", roles)}.");
                    break;
                case SetAutomationsOp a:
                    if (!a.Merge) Removed(a.Entity, Current(a.Entity)?.Automations, a.Automations, x => x.Name, "automatizaciones existentes");
                    if (a.Automations.Count > 0)
                        list.Add($"Las automatizaciones de «{Label(a.Entity)}» modifican o crean otros registros al ocurrir un evento.");
                    break;
                case SetSchedulesOp s:
                    if (!s.Merge) Removed(s.Entity, Current(s.Entity)?.Schedules, s.Schedules, x => x.Name, "tareas programadas existentes");
                    if (s.Schedules.Count > 0)
                        list.Add($"Las tareas programadas de «{Label(s.Entity)}» modifican registros solas, con su rol de servicio, cuando el administrador las activa.");
                    break;
                case SetWebhooksOp w:
                    if (!w.Merge) Removed(w.Entity, Current(w.Entity)?.Webhooks, w.Webhooks, x => x.Name, "webhooks existentes");
                    if (w.Webhooks.Count > 0)
                        list.Add($"Los webhooks de «{Label(w.Entity)}» envían datos a sistemas externos (el destino se configura en Integraciones).");
                    break;
                case SetRulesOp r:
                    // Regresión (spec 023): con merge las reglas que el plan no nombra se conservan.
                    if (!r.Merge) Removed(r.Entity, Current(r.Entity)?.Rules, r.Rules, x => x.Name, "reglas existentes");
                    // Spec 023: una regla que pasa de error a aviso relaja un control.
                    foreach (var rule in r.Rules.Where(x => x.IsWarning))
                        if (Current(r.Entity)?.Rules?.FirstOrDefault(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase)) is { IsWarning: false })
                            list.Add($"La regla «{rule.Name}» de «{Label(r.Entity)}» pasa a aviso: ya no impide guardar ni ejecutar acciones.");
                    break;
                case SetViewsOp v:
                    if (!v.Merge) Removed(v.Entity, Current(v.Entity)?.Views, v.Views, x => x.Name, "vistas existentes");
                    break;
                case SetReportsOp r:
                    if (!r.Merge) Removed(r.Entity, Current(r.Entity)?.Reports, r.Reports, x => x.Name, "reportes existentes");
                    break;
                case SetLayoutOp l when l.Layout is null && Current(l.Entity)?.Layout is not null:
                    list.Add($"Quita el layout de «{Label(l.Entity)}»: el formulario vuelve a mostrar los campos en orden.");
                    break;
                case UpdateEntityOp u when u.PerCompany == true:
                    list.Add($"«{Label(u.Entity)}» pasa a un registro por empresa: no se puede revertir.");
                    break;
            }
        }
        return list.Distinct().ToList();
    }

    /// <summary>
    /// Spec 021 (H5): solo los avisos de lo que cambia quién ve o modifica (seguridad por campo y registro, proceso con roles)
    /// o lo que actúa solo sobre los datos (automatizaciones, webhooks, tareas). Lo usa la reversión de un paquete.
    /// </summary>
    public static IReadOnlyList<string> Security(ChangePlan plan, IReadOnlyDictionary<string, EntityDef> model) =>
        For(plan with { Operations = plan.Operations.Where(op => op is SetSecurityOp or SetWorkflowOp or SetAutomationsOp or SetWebhooksOp or SetSchedulesOp).ToList() }, model);
}
