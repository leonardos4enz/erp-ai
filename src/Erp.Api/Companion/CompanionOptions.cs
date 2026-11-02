using Erp.Api.Studio;

namespace Erp.Api.Companion;

/// <summary>
/// Spec 046: topes, cola y retención del Acompañante, por entorno (<c>Companion:*</c>). El modelo y la clave son los del
/// asistente de la 011 (<c>Assistant:Model</c>, <c>AssistantKeys</c>): la persona no los elige. El precio por millón de
/// tokens es el de Studio (mismo modelo).
/// </summary>
public sealed record CompanionOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxPromptChars { get; init; } = 2000;
    /// <summary>Mensajes de la persona por conversación.</summary>
    public int MaxTurns { get; init; } = 40;
    /// <summary>Llamadas al modelo por conversación.</summary>
    public int MaxSteps { get; init; } = 80;
    public int MaxToolCalls { get; init; } = 160;
    public long MaxTokens { get; init; } = 1_500_000;
    public decimal MaxCostUsd { get; init; } = 2m;
    /// <summary>Minutos de un turno.</summary>
    public int MaxMinutes { get; init; } = 5;
    public int ConversationsPerDay { get; init; } = 30;
    public decimal CostPerDayUsd { get; init; } = 2m;
    /// <summary>H5 CA2: acciones propuestas sin decidir por conversación, y propuestas por persona en 24 horas.</summary>
    public int MaxPendingActions { get; init; } = 5;
    public int ActionsPerDay { get; init; } = 50;
    /// <summary>H4 CA3: una propuesta sin decidir caduca a estas horas.</summary>
    public int ActionHours { get; init; } = 24;
    /// <summary>
    /// Fallas parciales: una acción que lleva este tiempo «aplicándose» (el proceso cayó entre ejecutarla y guardar el
    /// resultado) pasa a «sin confirmar si se aplicó»; nunca se repite sola.
    /// </summary>
    public int ApplyingSeconds { get; init; } = 120;
    /// <summary>H6 CA3: una conversación sin actividad en estos días se borra.</summary>
    public int RetentionDays { get; init; } = 90;
    public int MaxTokensPerCall { get; init; } = 8000;
    public int MaxToolResultChars { get; init; } = 20_000;
    public int Workers { get; init; } = 2;
    public int PollSeconds { get; init; } = 2;
    public int LeaseSeconds { get; init; } = 120;
    public int RenewSeconds { get; init; } = 10;
    public int LiveMs { get; init; } = 250;
    public int StreamSeconds { get; init; } = 300;
    public int StreamPollMs { get; init; } = 1000;
    public int MaxStreamsPerUser { get; init; } = 3;
    public int IdentitySeconds { get; init; } = 5;
    public decimal InputUsdPerMTok { get; init; } = 2m;
    public decimal OutputUsdPerMTok { get; init; } = 10m;

    public long MaxCostMicros => StudioOptions.Micros(MaxCostUsd);
    public long CostPerDayMicros => StudioOptions.Micros(CostPerDayUsd);

    /// <summary>Como Studio (<see cref="StudioOptions.CostMicros"/>): la caché se lee al 10 % y se escribe al 125 %.</summary>
    public long CostMicros(long input, long output, long cacheRead = 0, long cacheWrite = 0) =>
        (long)Math.Ceiling((input + cacheRead * 0.1m + cacheWrite * 1.25m) * InputUsdPerMTok + output * OutputUsdPerMTok);

    /// <summary>Peor caso de la siguiente llamada: toda la entrada escrita en caché y la salida completa (Studio F-02).</summary>
    public long ReserveMicros(long inputTokens) => CostMicros(0, MaxTokensPerCall, cacheWrite: inputTokens);

    public static CompanionOptions From(IConfiguration config)
    {
        var s = config.GetSection("Companion");
        var studio = config.GetSection("Studio");
        var lease = Math.Clamp(s.GetValue("LeaseSeconds", 120), 5, 3600);
        return new CompanionOptions
        {
            Enabled = s.GetValue("Enabled", true),
            // El título de la conversación (200) y el resumen (4 000) se recortan; el mensaje va completo en el historial.
            MaxPromptChars = Math.Clamp(s.GetValue("MaxPromptChars", 2000), 100, 4000),
            MaxTurns = Math.Clamp(s.GetValue("MaxTurns", 40), 1, 500),
            MaxSteps = Math.Clamp(s.GetValue("MaxSteps", 80), 1, 1000),
            MaxToolCalls = Math.Clamp(s.GetValue("MaxToolCalls", 160), 1, 2000),
            MaxTokens = Math.Clamp(s.GetValue("MaxTokens", 1_500_000L), 1000L, 50_000_000L),
            MaxCostUsd = Math.Clamp(s.GetValue("MaxCostUsd", 2m), 0.01m, 1000m),
            MaxMinutes = Math.Clamp(s.GetValue("MaxMinutes", 5), 1, 60),
            ConversationsPerDay = Math.Clamp(s.GetValue("ConversationsPerDay", 30), 1, 1000),
            CostPerDayUsd = Math.Clamp(s.GetValue("CostPerDayUsd", 2m), 0.01m, 10_000m),
            MaxPendingActions = Math.Clamp(s.GetValue("MaxPendingActions", 5), 1, 50),
            ActionsPerDay = Math.Clamp(s.GetValue("ActionsPerDay", 50), 1, 1000),
            ActionHours = Math.Clamp(s.GetValue("ActionHours", 24), 1, 24 * 30),
            ApplyingSeconds = Math.Clamp(s.GetValue("ApplyingSeconds", 120), 5, 3600),
            RetentionDays = Math.Clamp(s.GetValue("RetentionDays", 90), 1, 3650),
            MaxTokensPerCall = Math.Clamp(s.GetValue("MaxTokensPerCall", 8000), 1000, 64_000),
            MaxToolResultChars = Math.Clamp(s.GetValue("MaxToolResultChars", 20_000), 1000, 200_000),
            Workers = Math.Clamp(s.GetValue("Workers", 2), 1, 16),
            PollSeconds = Math.Clamp(s.GetValue("PollSeconds", 2), 1, 60),
            LeaseSeconds = lease,
            // Como Studio (040 D-04): renovar más seguido que un tercio del lease.
            RenewSeconds = Math.Min(Math.Clamp(s.GetValue("RenewSeconds", 10), 1, 600), Math.Max(1, lease / 3)),
            LiveMs = Math.Clamp(s.GetValue("LiveMs", 250), 50, 5000),
            StreamSeconds = Math.Clamp(s.GetValue("StreamSeconds", 300), 5, 3600),
            StreamPollMs = Math.Clamp(s.GetValue("StreamPollMs", 1000), 100, 10_000),
            MaxStreamsPerUser = Math.Clamp(s.GetValue("MaxStreamsPerUser", 3), 1, 20),
            IdentitySeconds = Math.Clamp(s.GetValue("IdentitySeconds", 5), 1, 300),
            InputUsdPerMTok = Math.Clamp(studio.GetValue("InputUsdPerMTok", 2m), 0m, 1000m),
            OutputUsdPerMTok = Math.Clamp(studio.GetValue("OutputUsdPerMTok", 10m), 0m, 1000m),
        };
    }
}

/// <summary>Spec 046: las señales de Studio (despertar el flujo, detener el turno, contar flujos) para las conversaciones del Acompañante.</summary>
public sealed class CompanionSignals : StudioSignals;
