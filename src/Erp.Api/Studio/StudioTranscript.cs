using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Api.Studio;

/// <summary>
/// Spec 036: el historial exacto de una conversación (lo que se manda al modelo), guardado en <c>meta.StudioMessage</c>.
/// No se rearma desde los pasos: los pasos se recortan y no guardan ids de herramienta ni firmas de razonamiento (CLARIFY 2).
/// </summary>
public static class StudioTranscript
{
    /// <summary>Lo que ve el modelo en lugar de los registros que leyó un turno ya cerrado (H4 CA3).</summary>
    public const string Purged = "(Salida borrada al terminar el turno: tenía registros del entorno. Vuelve a leerlos si los necesitas.)";

    // Revisión adversarial 036 (F-02): idempotente por igualdad exacta con el aviso envuelto; un registro que contenga la
    // frase del aviso no se salta la purga.
    private static string Wrapped(string tool) => StudioPrompt.Untrusted(tool, JsonValue.Create(Purged), int.MaxValue);

    public const string Interrupted = "(Sin resultado: el turno se interrumpió antes de ejecutar esta herramienta.)";

    public static string Serialize(IReadOnlyList<StudioBlock> blocks) =>
        new JsonArray(blocks.Select(b => (JsonNode)(b switch
        {
            StudioText t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
            StudioToolUse u => new JsonObject { ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = JsonNode.Parse(u.Input.GetRawText()) },
            StudioToolResult r => new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = r.ToolUseId, ["tool"] = r.Tool, ["content"] = r.Content, ["is_error"] = r.IsError,
            },
            StudioThinking t => new JsonObject { ["type"] = "thinking", ["thinking"] = t.Thinking, ["signature"] = t.Signature },
            StudioRedactedThinking r => new JsonObject { ["type"] = "redacted_thinking", ["data"] = r.Data },
            _ => throw new InvalidOperationException("Bloque desconocido."),
        })).ToArray()).ToJsonString();

    public static IReadOnlyList<StudioBlock> Deserialize(string json)
    {
        var blocks = new List<StudioBlock>();
        foreach (var n in JsonNode.Parse(json) as JsonArray ?? [])
        {
            if (n is not JsonObject o) continue;
            switch (o["type"]?.GetValue<string>())
            {
                case "text": blocks.Add(new StudioText(o["text"]?.GetValue<string>() ?? "")); break;
                case "tool_use":
                    blocks.Add(new StudioToolUse(o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(),
                        JsonSerializer.SerializeToElement(o["input"] ?? new JsonObject())));
                    break;
                case "tool_result":
                    blocks.Add(new StudioToolResult(o["tool_use_id"]!.GetValue<string>(), o["content"]?.GetValue<string>() ?? "",
                        o["is_error"]?.GetValue<bool>() ?? false, o["tool"]?.GetValue<string>()));
                    break;
                case "thinking": blocks.Add(new StudioThinking(o["thinking"]?.GetValue<string>() ?? "", o["signature"]?.GetValue<string>() ?? "")); break;
                case "redacted_thinking": blocks.Add(new StudioRedactedThinking(o["data"]?.GetValue<string>() ?? "")); break;
            }
        }
        return blocks;
    }

    /// <summary>
    /// Deja el historial como lo exige la API (CLARIFY 2): une los mensajes seguidos del mismo rol (un turno que falló tras
    /// los resultados deja un mensaje de la persona antes del siguiente), y a cada uso de herramienta sin resultado (turno
    /// interrumpido) le agrega un resultado de error al principio del mensaje que sigue. Mensajes vacíos se quitan.
    /// </summary>
    public static List<StudioMessage> Normalize(IEnumerable<StudioMessage> messages)
    {
        var merged = new List<StudioMessage>();
        foreach (var m in messages.Where(m => m.Blocks.Count > 0))
        {
            if (merged.Count > 0 && merged[^1].FromUser == m.FromUser)
                merged[^1] = new(m.FromUser, [.. merged[^1].Blocks, .. m.Blocks]);
            else merged.Add(m);
        }
        // La conversación siempre empieza con la persona.
        while (merged.Count > 0 && !merged[0].FromUser) merged.RemoveAt(0);

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].FromUser) continue;
            var uses = merged[i].Blocks.OfType<StudioToolUse>().Select(u => u.Id).ToList();
            if (uses.Count == 0) continue;
            var next = i + 1 < merged.Count ? merged[i + 1] : null;
            var answered = next?.Blocks.OfType<StudioToolResult>().Select(r => r.ToolUseId).ToHashSet() ?? [];
            var missing = uses.Where(id => !answered.Contains(id))
                .Select(id => (StudioBlock)new StudioToolResult(id, Interrupted, true)).ToList();
            if (missing.Count == 0) continue;
            // Los resultados van primero en el mensaje que sigue al uso; el resto (el texto nuevo) después.
            var rest = next?.Blocks ?? [];
            var results = rest.OfType<StudioToolResult>().Where(r => uses.Contains(r.ToolUseId)).Cast<StudioBlock>();
            var fixedNext = new StudioMessage(true, [.. results, .. missing, .. rest.Where(b => b is not StudioToolResult r || !uses.Contains(r.ToolUseId))]);
            if (next is null) merged.Add(fixedNext);
            else merged[i + 1] = fixedNext;
        }
        return merged;
    }

    /// <summary>
    /// H4 CA3: reemplaza el contenido de los resultados de las herramientas de <paramref name="dataTools"/> por un aviso.
    /// Devuelve null si el mensaje no tenía ninguno.
    /// </summary>
    public static string? Purge(string contentJson, IReadOnlyCollection<string> dataTools)
    {
        var blocks = Deserialize(contentJson);
        var changed = false;
        var purged = blocks.Select(b =>
        {
            if (b is StudioToolResult r && r.Tool is { } tool && dataTools.Contains(tool) && r.Content != Wrapped(tool))
            {
                changed = true;
                return new StudioToolResult(r.ToolUseId, Wrapped(tool), false, tool);
            }
            return b;
        }).ToList();
        return changed ? Serialize(purged) : null;
    }
}
