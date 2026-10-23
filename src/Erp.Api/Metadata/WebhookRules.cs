using Erp.Api.Expressions;
using Erp.Api.Security;

namespace Erp.Api.Metadata;

/// <summary>Spec 008: validación estática de los webhooks de un catálogo (ver plan 008).</summary>
public static class WebhookRules
{
    public const int MaxWebhooks = 10;
    public const int MaxEvents = 8;
    public const int MaxLabel = 100;
    public const int MaxDescription = 300;

    public static IEnumerable<string> Validate(EntityDef e, IReadOnlyList<WebhookDef> hooks, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (e.IsChild) { msgs.Add("es una partida: los webhooks se declaran en su documento."); return msgs; }
        if (hooks.Count > MaxWebhooks) msgs.Add($"máximo {MaxWebhooks} webhooks por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hooks)
        {
            var where = $"webhook '{h.Name}'";
            if (!Identifiers.IsValid(h.Name, out var err)) msgs.Add($"{where}: {err}");
            else if (!names.Add(h.Name)) msgs.Add($"{where}: el nombre está repetido.");
            if (string.IsNullOrWhiteSpace(h.Label) || h.Label.Length > MaxLabel) msgs.Add($"{where}: la etiqueta es obligatoria (máx. {MaxLabel} caracteres).");
            if (h.Description?.Length > MaxDescription) msgs.Add($"{where}: la descripción admite máximo {MaxDescription} caracteres.");

            if (h.Events.Count is 0 or > MaxEvents) msgs.Add($"{where}: declara de 1 a {MaxEvents} eventos.");
            foreach (var ev in h.Events) msgs.AddRange(ValidateEvent(e, ev).Select(m => $"{where}: {m}"));

            if (!string.IsNullOrWhiteSpace(h.When))
                foreach (var m in ExprAnalyzer.Analyze(h.When, e, model, roles).Errors) msgs.Add($"{where} (when): {m}");

            if (string.IsNullOrWhiteSpace(h.Role) || !roles.Contains(h.Role)) { msgs.Add($"{where}: el rol '{h.Role}' no existe."); continue; }
            // La carga se arma como la vería el rol: pedir un campo que no ve sería una fuga (o un webhook que miente).
            var service = ServiceRole.User(h.Role, new Dictionary<string, EntityAccess>());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in h.Fields ?? [])
            {
                var f = e.FindField(name ?? "");
                if (f is null || f.IsArchived) { msgs.Add($"{where}: '{name}' no es un campo de {e.Name}."); continue; }
                if (!seen.Add(f.Name)) msgs.Add($"{where}: el campo '{f.Name}' está repetido.");
                if (!FieldAccess.CanSee(e, f, service)) msgs.Add($"{where}: el rol '{h.Role}' no puede ver '{f.Label}' (seguridad del catálogo).");
            }
        }
        return msgs;
    }

    private static IEnumerable<string> ValidateEvent(EntityDef e, WebhookEvent ev)
    {
        if (!AutomationEvents.All.Contains(ev.On ?? "")) { yield return $"el evento '{ev.On}' no existe (create, update, enterState, transition)."; yield break; }
        switch (ev.On)
        {
            case AutomationEvents.EnterState:
                if (e.Workflow?.FindState(ev.State) is null) yield return $"«al entrar a un estado» exige un estado del proceso ('{ev.State}' no existe).";
                if (ev.Transition is not null) yield return "«al entrar a un estado» no admite transición.";
                break;
            case AutomationEvents.Transition:
                if (e.Workflow?.Transitions.Any(t => string.Equals(t.Name, ev.Transition, StringComparison.OrdinalIgnoreCase)) != true)
                    yield return $"«al ejecutar una transición» exige una transición del proceso ('{ev.Transition}' no existe).";
                if (ev.State is not null) yield return "«al ejecutar una transición» no admite estado.";
                break;
            default:
                if (ev.State is not null || ev.Transition is not null) yield return $"el evento '{ev.On}' no admite estado ni transición.";
                break;
        }
    }

    /// <summary>¿El evento ocurrido (con su estado o transición) dispara el webhook?</summary>
    public static bool Matches(WebhookDef h, string ev, string? key) => h.Events.Any(x => x.On == ev && ev switch
    {
        AutomationEvents.EnterState => string.Equals(x.State, key, StringComparison.OrdinalIgnoreCase),
        AutomationEvents.Transition => string.Equals(x.Transition, key, StringComparison.OrdinalIgnoreCase),
        _ => true,
    });
}
