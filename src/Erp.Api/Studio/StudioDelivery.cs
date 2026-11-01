using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;

namespace Erp.Api.Studio;

/// <param name="Error">Por qué no se acepta la entrega (para que quien la mandó la corrija); null si se acepta.</param>
/// <param name="PlanJson">El plan normalizado (sin huella ni versiones) si trae uno y compila; null si no trae plan.</param>
public sealed record StudioDeliveryResult(string Summary, JsonObject Report, string? PlanJson, string? Error);

/// <summary>
/// Spec 033 (H4 CA1), movida aquí en la 044: lectura de una entrega (<c>entregar</c>) y compilación de su plan contra el modelo
/// real. La usan el agente de Studio y el servidor MCP (la entrada es no confiable en los dos casos).
/// </summary>
public static class StudioDelivery
{
    public const int MaxSummary = 4000;

    public static async Task<StudioDeliveryResult> ParseAsync(JsonElement raw, JsonSerializerOptions json, MetadataStore store, Db db, CancellationToken ct)
    {
        string? error = null;
        string? planJson = null;
        var summary = "";
        JsonObject report = [];
        try
        {
            var input = JsonNode.Parse(raw.GetRawText()) as JsonObject ?? throw new ToolInputException("La entrada debe ser un objeto JSON.");
            summary = StudioTools.Req(input, "summary", MaxSummary);
            report = new JsonObject
            {
                // F-04: una prueba puede llegar como texto suelto; no debe tumbar la entrega.
                ["tested"] = new JsonArray((input["tested"] as JsonArray ?? []).Take(50).Select(t => (JsonNode)(t is JsonObject o
                    ? new JsonObject
                    {
                        ["description"] = StudioAgent.Cut(o["description"]?.ToString() ?? "", 500),
                        ["ok"] = o["ok"]?.GetValueKind() == JsonValueKind.True,
                        ["detail"] = o["detail"] is { } d ? StudioAgent.Cut(d.ToString(), 1000) : null,
                    }
                    : new JsonObject { ["description"] = StudioAgent.Cut(t?.ToString() ?? "", 500), ["ok"] = false })).ToArray()),
                ["failures"] = Texts(input["failures"]),
                ["risks"] = Texts(input["risks"]),
            };
            if (input["plan"] is JsonObject p)
            {
                if ((p["operations"] as JsonArray)?.Count is not > 0) throw new ToolInputException("El plan no tiene operaciones: omite «plan» si no propones cambios.");
                if ((p["operations"] as JsonArray)!.Count > StudioTools.MaxOperations) throw new ToolInputException($"El plan admite hasta {StudioTools.MaxOperations} operaciones.");
                ChangePlan? plan;
                try { plan = p.Deserialize<ChangePlan>(json); }
                catch (JsonException ex) { throw new ToolInputException("El plan no es un Plan de Cambios válido: " + StudioAgent.Cut(ex.Message, 300)); }
                plan = plan! with { BaseHash = null, BaseVersions = null };
                var model = await store.GetAsync(ct);
                IReadOnlySet<string> roles;
                await using (var cn = await db.OpenAsync(ct)) roles = await PlanService.LoadRolesAsync(cn, null);
                var preview = PlanCompiler.Compile(plan, model, roles, await store.GetDashboardsAsync(ct), await store.GetAppsAsync(ct)).Preview;
                if (!preview.Valid)
                    error = "El plan no compila contra el modelo real; corrígelo y vuelve a entregar:\n- "
                            + string.Join("\n- ", preview.Errors.Take(30).Select(e => e.Operation >= 0 ? $"Operación {e.Operation + 1}: {e.Message}" : e.Message));
                else planJson = JsonSerializer.Serialize(plan, json);
            }
        }
        catch (ToolInputException ex) { error = ex.Message; }
        return new(summary, report, planJson, error);
    }

    private static JsonArray Texts(JsonNode? n) =>
        new((n as JsonArray ?? []).Take(50).Select(x => (JsonNode)JsonValue.Create(StudioAgent.Cut(x?.ToString() ?? "", 1000))!).ToArray());
}
