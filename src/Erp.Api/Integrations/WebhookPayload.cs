using System.Text.Json;

namespace Erp.Api.Integrations;

/// <summary>Spec 008: cuerpo de una entrega. Se fija al encolar; cada reintento envía exactamente el mismo texto.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(Guid deliveryId, string webhook, string entity, string ev, string? state, string? transition,
        long? recordId, DateTime occurredAt, IReadOnlyDictionary<string, object?> data) =>
        JsonSerializer.Serialize(new
        {
            id = deliveryId,
            webhook,
            entity,
            @event = ev,
            state,
            transition,
            recordId,
            occurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc),
            data,
        }, Json);
}
