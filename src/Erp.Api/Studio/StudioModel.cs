using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using Erp.Api.Assistant;

namespace Erp.Api.Studio;

/// <summary>Un bloque de un mensaje del bucle agéntico (independiente del SDK, para el proveedor simulado de las pruebas).</summary>
public abstract record StudioBlock;
public sealed record StudioText(string Text) : StudioBlock;
public sealed record StudioToolUse(string Id, string Name, JsonElement Input) : StudioBlock;
/// <summary>Spec 036: <paramref name="Tool"/> es el nombre del catálogo (para purgar lo leído); no viaja al proveedor.</summary>
public sealed record StudioToolResult(string ToolUseId, string Content, bool IsError, string? Tool = null) : StudioBlock;
/// <summary>El razonamiento del modelo se devuelve tal cual en el turno siguiente (la firma no se toca).</summary>
public sealed record StudioThinking(string Thinking, string Signature) : StudioBlock;
public sealed record StudioRedactedThinking(string Data) : StudioBlock;

public sealed record StudioMessage(bool FromUser, IReadOnlyList<StudioBlock> Blocks);
public sealed record StudioToolSpec(string Name, string Description, JsonObject InputSchema);
public sealed record StudioRequest(string System, IReadOnlyList<StudioMessage> Messages, IReadOnlyList<StudioToolSpec> Tools, int MaxTokens);
public sealed record StudioReply(IReadOnlyList<StudioBlock> Content, string? StopReason, long InputTokens, long OutputTokens,
    long CacheReadTokens = 0, long CacheWriteTokens = 0);

/// <summary>
/// Spec 033: el modelo de lenguaje del bucle de ERP AI Studio. Recibe texto y la lista de herramientas, y responde con texto
/// y pedidos de herramienta: no ejecuta nada ni tiene credenciales del ERP (las herramientas las corre el servidor).
/// </summary>
public interface IStudioModel
{
    /// <summary>
    /// Spec 036: <paramref name="onText"/> recibe el texto a medida que el modelo lo escribe (para el estado en vivo); la
    /// respuesta completa es la que cuenta.
    /// </summary>
    Task<StudioReply> NextAsync(AssistantCredential credential, StudioRequest request, Func<string, Task>? onText, CancellationToken ct);
}

/// <summary>Claude por el SDK oficial (tool use, bucle manual), con el cliente y la clave del asistente de la 011.</summary>
public sealed class AnthropicStudioModel(AnthropicAssistantModel provider, AssistantOptions options) : IStudioModel
{
    public async Task<StudioReply> NextAsync(AssistantCredential credential, StudioRequest request, Func<string, Task>? onText, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = request.MaxTokens,
            // Las herramientas y las instrucciones no cambian durante la sesión: se cachean (van antes de los mensajes).
            System = new List<TextBlockParam> { new() { Text = request.System, CacheControl = new CacheControlEphemeral() } },
            Tools = request.Tools.Select(t => (ToolUnion)new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new()
                {
                    Properties = (t.InputSchema["properties"] as JsonObject ?? []).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                    Required = (t.InputSchema["required"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList(),
                },
            }).ToList(),
            Messages = request.Messages.Select(ToParam).ToList(),
        };
        try
        {
            // Spec 036: en streaming, para el texto en vivo; el agregador del SDK arma el mensaje completo.
            var response = await global::Anthropic.SseAggregatorExtensions.Aggregate(Tap(provider.Client(credential).Messages.CreateStreaming(parameters, ct), onText));
            var content = new List<StudioBlock>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text)) content.Add(new StudioText(text.Text));
                else if (block.TryPickThinking(out var thinking)) content.Add(new StudioThinking(thinking.Thinking, thinking.Signature));
                else if (block.TryPickRedactedThinking(out var redacted)) content.Add(new StudioRedactedThinking(redacted.Data));
                else if (block.TryPickToolUse(out var use)) content.Add(new StudioToolUse(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            }
            var u = response.Usage;
            // Spec 037 (regresión): el motivo es un enum envuelto del SDK; su `ToString()` no es el valor de la API.
            return new StudioReply(content, response.StopReason?.Raw(), u.InputTokens, u.OutputTokens,
                u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Revisión adversarial 036 (F-04): lo que falla al escribir el estado en vivo (lease perdido, base) no es del proveedor.
        catch (StudioLeaseLost) { throw; }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException) { throw; }
        catch (Exception ex) { throw provider.Fail(ex); }
    }

    private static async IAsyncEnumerable<RawMessageStreamEvent> Tap(IAsyncEnumerable<RawMessageStreamEvent> events, Func<string, Task>? onText)
    {
        await foreach (var e in events)
        {
            if (onText is not null && e.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text) && text.Text.Length > 0)
                await onText(text.Text);
            yield return e;
        }
    }

    private static MessageParam ToParam(StudioMessage m)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var b in m.Blocks)
            switch (b)
            {
                case StudioText t: blocks.Add(new TextBlockParam { Text = t.Text }); break;
                case StudioThinking t: blocks.Add(new ThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature }); break;
                case StudioRedactedThinking r: blocks.Add(new RedactedThinkingBlockParam { Data = r.Data }); break;
                case StudioToolUse u:
                    blocks.Add(new ToolUseBlockParam
                    {
                        ID = u.Id, Name = u.Name,
                        Input = u.Input.ValueKind == JsonValueKind.Object
                            ? u.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                            : new Dictionary<string, JsonElement>(),
                    });
                    break;
                case StudioToolResult r: blocks.Add(new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError }); break;
            }
        return m.FromUser ? new UserMessageParam { Content = blocks } : new AssistantMessageParam { Content = blocks };
    }
}
