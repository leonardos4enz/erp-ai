using System.Net;

namespace Erp.Api.Assistant;

/// <summary>
/// Spec 011: configuración del asistente por entorno. La clave nunca vive en el repositorio (user-secrets o variable de
/// entorno <c>Assistant__AnthropicApiKey</c>) y nunca sale del servidor salvo hacia el proveedor.
/// </summary>
public sealed record AssistantOptions
{
    public const string DefaultModel = "claude-sonnet-5-5";

    public string? ApiKey { get; init; }
    /// <summary>Con una clave de organización del servidor: espacio de trabajo (cabecera <c>anthropic-workspace-id</c>).</summary>
    public string? WorkspaceId { get; init; }
    public string Model { get; init; } = DefaultModel;
    /// <summary>Solo pruebas: proveedor simulado. Fuera de loopback debe ser https (la clave no viaja en claro).</summary>
    public Uri? BaseUrl { get; init; }
    public int MaxPromptChars { get; init; } = 4000;
    public int RequestsPerHour { get; init; } = 20;
    public int MaxTokens { get; init; } = 16000;
    public int TimeoutSeconds { get; init; } = 120;
    public int RepairAttempts { get; init; } = 2;
    public int MaxContextChars { get; init; } = 200_000;
    /// <summary>Turnos previos de la conversación que se reenvían al modelo.</summary>
    public int MaxTurns { get; init; } = 10;

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    public static AssistantOptions From(IConfiguration config)
    {
        var s = config.GetSection("Assistant");
        var model = s["Model"]?.Trim();
        var o = new AssistantOptions
        {
            ApiKey = s["AnthropicApiKey"]?.Trim(),
            WorkspaceId = string.IsNullOrWhiteSpace(s["AnthropicWorkspaceId"]) ? null : s["AnthropicWorkspaceId"]!.Trim(),
            Model = string.IsNullOrEmpty(model) ? DefaultModel : model,
            BaseUrl = ParseBaseUrl(s["BaseUrl"]),
            MaxPromptChars = Clamp(s.GetValue("MaxPromptChars", 4000), 100, 20_000),
            RequestsPerHour = Clamp(s.GetValue("RequestsPerHour", 20), 1, 1000),
            MaxTokens = Clamp(s.GetValue("MaxTokens", 16000), 1000, 64_000),
            TimeoutSeconds = Clamp(s.GetValue("TimeoutSeconds", 120), 5, 600),
            RepairAttempts = Clamp(s.GetValue("RepairAttempts", 2), 0, 5),
            MaxContextChars = Clamp(s.GetValue("MaxContextChars", 200_000), 10_000, 2_000_000),
        };
        if (o.WorkspaceId is not null && !AssistantKeys.IsWorkspaceId(o.WorkspaceId))
            throw new InvalidOperationException("Assistant:AnthropicWorkspaceId no es un id de espacio de trabajo válido.");
        if (o.Model.Length > 100 || o.Model.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')))
            throw new InvalidOperationException("Assistant:Model no es un identificador de modelo válido.");
        return o;
    }

    /// <summary>Vacío = API pública. Se exige https salvo para un proveedor simulado en loopback (pruebas).</summary>
    internal static Uri? ParseBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Assistant:BaseUrl debe ser una URL http(s) absoluta.");
        if (uri.Scheme == "http" && !IsLoopback(uri))
            throw new InvalidOperationException("Assistant:BaseUrl debe usar https (http solo se permite en loopback, para pruebas).");
        return uri;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
}
