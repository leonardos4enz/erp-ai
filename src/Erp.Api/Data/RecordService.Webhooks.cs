using Dapper;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Data;

/// <summary>
/// Spec 008 · webhooks salientes. El evento se guarda en la bandeja de salida (<c>meta.WebhookDelivery</c>) dentro de la
/// transacción del cambio: si el cambio se revierte, no se envía nada. La carga se arma como la vería el rol de servicio
/// del webhook (seguridad 006); lo entrega <see cref="WebhookDispatcher"/>.
/// </summary>
public sealed partial class RecordService
{
    private async Task EnqueueWebhooksAsync(AutoCtx c, EntityDef e, long id, string ev, string? key)
    {
        var hooks = (e.Webhooks ?? []).Where(h => WebhookRules.Matches(h, ev, key)).ToList();
        if (hooks.Count == 0) return;
        // Solo los que tienen destino activo en este entorno.
        var active = (await c.Cn.QueryAsync<string>(
            "SELECT WebhookName FROM meta.WebhookEndpoint WHERE EntityName = @e AND IsActive = 1", new { e = e.Name }, c.Tx))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        hooks = hooks.Where(h => active.Contains(h.Name)).ToList();
        if (hooks.Count == 0) return;

        var draft = await LoadDraftAsync(c.Cn, c.Tx, e, c.Model, id, c.Ct);
        if (draft is null) return;
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        var state = draft.Header.GetValueOrDefault(Workflow.StateField) as string;
        foreach (var h in hooks)
        {
            if (h.When is not null && !Expressions.ExprEvaluator.Test(h.When, HeaderScope(draft, e, c.Model, loader), Env(c.User, draft, loader), whenNull: false))
                continue;
            // Spec 010: la carga se arma en la empresa del evento (la de quien lo disparó).
            var service = await ServiceRole.LoadAsync(c.Cn, c.Tx, h.Role, c.User.Company);
            // El rol debe poder leer el catálogo y el registro; si no, no sale nada.
            if (!service.Access(e.Name).Read) continue;
            if (RecordFilter.For(e, service, write: false) is { } scope && !await MatchesAsync(c.Cn, c.Tx, e, id, scope, c.Ct)) continue;
            var row = await LoadOutputAsync(c.Cn, c.Tx, e, c.Model, id, service, c.Ct);
            if (row is null) continue;
            var data = Trim(row, h.Fields);
            var deliveryId = Guid.NewGuid();
            var body = WebhookPayload.Serialize(deliveryId, h.Name, e.Name, ev, state, ev == AutomationEvents.Transition ? key : null, id, DateTime.UtcNow, data);
            await c.Cn.ExecuteAsync("""
                INSERT meta.WebhookDelivery (DeliveryId, EntityName, WebhookName, RecordId, Event, EventKey, PayloadJson, Status)
                VALUES (@deliveryId, @e, @n, @id, @ev, @key, @body, 'pending')
                """, new { deliveryId, e = e.Name, n = h.Name, id, ev, key, body }, c.Tx);
        }
    }

    /// <summary>Sin datos técnicos del formulario; con <c>fields</c>, solo el id y esos campos (con sus etiquetas).</summary>
    private static Dictionary<string, object?> Trim(Dictionary<string, object?> row, IReadOnlyList<string>? fields)
    {
        var keep = fields?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return row.Where(kv => !kv.Key.StartsWith('_') && (keep is null || kv.Key == "id" || keep.Contains(kv.Key) ||
                                                           kv.Key.EndsWith("__label", StringComparison.Ordinal) && keep.Contains(kv.Key[..^"__label".Length])))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
