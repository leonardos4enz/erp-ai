using System.Text.Json;
using Erp.Api.Metadata;

namespace Erp.Api.Assistant;

/// <summary>
/// Spec 011: la respuesta del LLM es entrada no confiable. Antes del compilador se exige el formato
/// <c>{ explanation, plan }</c>, tamaños máximos y que el plan sea un <see cref="ChangePlan"/> con operaciones conocidas.
/// </summary>
public static class ProposalParser
{
    public const int MaxResponseChars = 300_000;
    public const int MaxPlanChars = 256 * 1024;
    public const int MaxExplanationChars = 4000;

    /// <param name="Plan">Nulo si no hay plan (respuesta sin cambios) o si no se pudo interpretar.</param>
    /// <param name="Error">Por qué no es una propuesta válida; se reenvía al modelo para que la corrija.</param>
    public sealed record Result(string? Explanation, ChangePlan? Plan, string? PlanJson, string? Error)
    {
        public bool IsAnswer => Error is null && Plan is null;
    }

    public static Result Parse(string? text, JsonSerializerOptions json)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail(null, "La respuesta está vacía.");
        if (text.Length > MaxResponseChars) return Fail(null, "La respuesta excede el tamaño máximo.");

        JsonElement root;
        try { root = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 }).RootElement; }
        catch (JsonException) { return Fail(null, "La respuesta no es JSON válido."); }
        if (root.ValueKind != JsonValueKind.Object) return Fail(null, "La respuesta debe ser un objeto { explanation, plan }.");

        var explanation = root.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : null;
        if (explanation is { Length: > MaxExplanationChars }) explanation = explanation[..MaxExplanationChars];

        if (!root.TryGetProperty("plan", out var p) || p.ValueKind is JsonValueKind.Null) return new(explanation, null, null, null);
        // El esquema pide el plan como texto JSON; se acepta también el objeto por si el modelo lo anida.
        var planJson = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString()!.Trim(),
            JsonValueKind.Object => p.GetRawText(),
            _ => null,
        };
        if (planJson is null) return Fail(explanation, "«plan» debe ser el Plan de Cambios como texto JSON, o vacío.");
        if (planJson.Length == 0) return new(explanation, null, null, null);
        if (planJson.Length > MaxPlanChars) return Fail(explanation, $"El plan excede {MaxPlanChars / 1024} KB.");

        ChangePlan? plan;
        try { plan = JsonSerializer.Deserialize<ChangePlan>(planJson, json); }
        catch (JsonException ex) { return Fail(explanation, $"El plan no es un Plan de Cambios válido: {Describe(ex)}"); }
        catch (NotSupportedException) { return Fail(explanation, "El plan contiene una operación desconocida o sin «op»."); }
        if (plan is null) return Fail(explanation, "El plan está vacío.");
        if (plan.Operations is null || plan.Operations.Count == 0) return Fail(explanation, "El plan no tiene operaciones.");
        if (plan.Operations.Any(o => o is null)) return Fail(explanation, "El plan tiene operaciones vacías.");
        if (plan.Operations.Count > PlanCompiler.MaxOperations) return Fail(explanation, $"El plan excede {PlanCompiler.MaxOperations} operaciones.");
        if (string.IsNullOrWhiteSpace(plan.Description)) plan = plan with { Description = "Propuesta del asistente IA" };
        else if (plan.Description.Length > 400) plan = plan with { Description = plan.Description[..400] };
        return new(explanation, plan, JsonSerializer.Serialize(plan, json), null);
    }

    private static Result Fail(string? explanation, string error) => new(explanation, null, null, error);

    /// <summary>El mensaje de System.Text.Json es técnico pero seguro (no contiene datos del entorno) y ayuda al modelo a corregir.</summary>
    private static string Describe(JsonException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("type discriminator", StringComparison.OrdinalIgnoreCase) || msg.Contains("'op'", StringComparison.Ordinal))
            return "operación desconocida o sin «op».";
        return (ex.Path is { } path ? $"{path}: " : "") + (msg.Length > 300 ? msg[..300] : msg);
    }
}
