using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

namespace Erp.Api.Assistant;

/// <summary>Un mensaje de la conversación con el LLM: de la persona (o del servidor, con errores) o del asistente.</summary>
public sealed record AssistantTurn(bool FromUser, string Text);

public sealed record ModelReply(string? Text, string? StopReason, long InputTokens, long OutputTokens);

/// <summary>Falla del proveedor. <c>Code</c> se guarda y se audita; el detalle técnico solo va al log.</summary>
public sealed class AssistantProviderException(string code, Exception? inner = null) : Exception($"Falla del proveedor del asistente: {code}", inner)
{
    public string Code { get; } = code;
}

/// <summary>Spec 011: el LLM visto por el ERP. Recibe texto y devuelve texto: no tiene herramientas ni acceso al entorno.</summary>
public interface IAssistantModel
{
    Task<ModelReply> CompleteAsync(AssistantCredential credential, string contextJson, IReadOnlyList<AssistantTurn> messages, CancellationToken ct);

    /// <summary>Comprueba que el proveedor acepta la clave (consulta el modelo configurado; no genera texto).</summary>
    Task VerifyAsync(AssistantCredential credential, CancellationToken ct);
}

/// <summary>Claude por el SDK oficial de Anthropic, con salida estructurada <c>{ explanation, plan }</c>.</summary>
public sealed class AnthropicAssistantModel : IAssistantModel, IDisposable
{
    private readonly AssistantOptions options;
    private readonly ILogger<AnthropicAssistantModel> log;
    private readonly Lock gate = new();
    private (AssistantCredential Credential, IAnthropicClient Client)? current;

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            explanation = new { type = "string", description = "Explicación breve en español, texto plano." },
            plan = new { type = "string", description = "Plan de Cambios serializado como JSON, o cadena vacía si no hay cambios." },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "explanation", "plan" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public AnthropicAssistantModel(AssistantOptions options, ILogger<AnthropicAssistantModel> log)
    {
        this.options = options;
        this.log = log;
    }

    /// <summary>
    /// Un cliente por clave (la clave puede cambiar desde la app). Siempre con la clave explícita: así el SDK no toma
    /// credenciales del equipo (variables o perfiles).
    /// </summary>
    internal IAnthropicClient Client(AssistantCredential credential)
    {
        lock (gate)
        {
            if (current is { } c && c.Credential == credential) return c.Client;
            IAnthropicClient client = new AnthropicClient
            {
                ApiKey = credential.ApiKey,
                MaxRetries = 1,
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            };
            // Una clave de organización (no ligada a un espacio de trabajo) exige elegir uno en cada petición.
            if (credential.WorkspaceId is { } workspace)
                client = client.WithOptions(o => o with { ExtraHeaders = new Dictionary<string, string> { ["anthropic-workspace-id"] = workspace } });
            if (options.BaseUrl is not null) client = client.WithOptions(o => o with { BaseUrl = options.BaseUrl.ToString().TrimEnd('/') });
            current = (credential, client);
            return client;
        }
    }

    public async Task<ModelReply> CompleteAsync(AssistantCredential credential, string contextJson, IReadOnlyList<AssistantTurn> messages, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = new List<TextBlockParam>
            {
                // Las instrucciones son fijas: se cachean. El modelo del entorno cambia y va después.
                new() { Text = AssistantPrompt.Instructions, CacheControl = new CacheControlEphemeral() },
                new() { Text = AssistantPrompt.ModelBlock(contextJson) },
            },
            Messages = messages.Select(m => m.FromUser ? (MessageParam)new UserMessageParam { Content = m.Text } : new AssistantMessageParam { Content = m.Text }).ToList(),
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = Schema } },
        };
        try
        {
            var response = await Client(credential).Messages.Create(parameters, ct);
            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            return new ModelReply(text, response.StopReason?.ToString(), response.Usage.InputTokens, response.Usage.OutputTokens);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Fail(ex); }
    }

    public async Task VerifyAsync(AssistantCredential credential, CancellationToken ct)
    {
        try { await Client(credential).Models.Retrieve(options.Model, new ModelRetrieveParams(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Fail(ex); }
    }

    internal AssistantProviderException Fail(Exception ex)
    {
        var code = ex switch
        {
            AnthropicUnauthorizedException or AnthropicForbiddenException => "auth",
            AnthropicRateLimitException => "rate_limit",
            // Regresión F-06: una clave de organización sin espacio de trabajo responde 400 pidiendo esta cabecera.
            AnthropicBadRequestException when ex.Message.Contains("anthropic-workspace-id", StringComparison.OrdinalIgnoreCase) => "workspace",
            AnthropicNotFoundException => "model",
            AnthropicBadRequestException or AnthropicUnprocessableEntityException => "bad_request",
            Anthropic5xxException => "provider",
            AnthropicIOException or HttpRequestException => "network",
            OperationCanceledException or TimeoutException => "timeout",
            _ => "provider",
        };
        // Sin la excepción completa: solo tipo y mensaje (no contienen la clave), para no volcar peticiones al log.
        log.LogWarning("El proveedor del asistente falló ({Code}): {Type} {Message}", code, ex.GetType().Name, ex.Message);
        return new AssistantProviderException(code, ex);
    }

    public void Dispose()
    {
        if (current is { } c) (c.Client as IDisposable)?.Dispose();
    }
}
