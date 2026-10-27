namespace Erp.Api.Studio;

/// <summary>
/// Spec 033: topes y trabajador de ERP AI Studio, por entorno (<c>Studio:*</c>). El modelo y la clave son los del asistente
/// de la 011 (<c>Assistant:Model</c>, <c>AssistantKeys</c>).
/// </summary>
public sealed record StudioOptions
{
    public int MaxPromptChars { get; init; } = 4000;
    /// <summary>Llamadas al modelo por sesión.</summary>
    public int MaxSteps { get; init; } = 30;
    public int MaxToolCalls { get; init; } = 80;
    /// <summary>Tokens de entrada más salida por sesión.</summary>
    public long MaxTokens { get; init; } = 1_500_000;
    public int MaxMinutes { get; init; } = 20;
    public decimal MaxCostUsd { get; init; } = 5m;
    public decimal CostPerDayUsd { get; init; } = 20m;
    public int SessionsPerDay { get; init; } = 10;
    /// <summary>Precio por millón de tokens (por omisión, el público de Claude Sonnet 5.5).</summary>
    public decimal InputUsdPerMTok { get; init; } = 2m;
    public decimal OutputUsdPerMTok { get; init; } = 10m;
    /// <summary>Tope de salida de cada llamada al modelo.</summary>
    public int MaxTokensPerCall { get; init; } = 16000;
    public int MaxToolResultChars { get; init; } = 20_000;
    /// <summary>Habilita el alcance «aislado» (exige permiso para crear bases en el servidor).</summary>
    public bool Sandbox { get; init; } = true;
    public int Workers { get; init; } = 2;
    public int PollSeconds { get; init; } = 2;
    public int LeaseSeconds { get; init; } = 120;
    public int RenewSeconds { get; init; } = 10;

    public long MaxCostMicros => Micros(MaxCostUsd);
    public long CostPerDayMicros => Micros(CostPerDayUsd);

    public static long Micros(decimal usd) => (long)Math.Round(usd * 1_000_000m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Costo en microdólares. La lectura de caché cuesta el 10 % de la entrada y la escritura el 125 % (tarifas de
    /// Anthropic); <paramref name="input"/> son los tokens de entrada sin caché.
    /// </summary>
    public long CostMicros(long input, long output, long cacheRead = 0, long cacheWrite = 0) =>
        (long)Math.Ceiling((input + cacheRead * 0.1m + cacheWrite * 1.25m) * InputUsdPerMTok + output * OutputUsdPerMTok);

    /// <summary>
    /// Peor caso de la siguiente llamada, reservado antes de hacerla (revisión adversarial F-02): toda la entrada escrita en
    /// caché (125 %) y la salida completa. <paramref name="inputTokens"/> lo estima el agente con los tokens medidos de la
    /// llamada anterior más un token por cada carácter nuevo (el peor caso: CJK, símbolos).
    /// </summary>
    public long ReserveMicros(long inputTokens) => CostMicros(0, MaxTokensPerCall, cacheWrite: inputTokens);

    public static StudioOptions From(IConfiguration config)
    {
        var s = config.GetSection("Studio");
        return new StudioOptions
        {
            // F-06: la columna meta.StudioSession.Prompt es de 4 000.
            MaxPromptChars = Math.Clamp(s.GetValue("MaxPromptChars", 4000), 100, 4000),
            MaxSteps = Math.Clamp(s.GetValue("MaxSteps", 30), 1, 200),
            MaxToolCalls = Math.Clamp(s.GetValue("MaxToolCalls", 80), 1, 1000),
            MaxTokens = Math.Clamp(s.GetValue("MaxTokens", 1_500_000L), 1000L, 50_000_000L),
            MaxMinutes = Math.Clamp(s.GetValue("MaxMinutes", 20), 1, 240),
            MaxCostUsd = Math.Clamp(s.GetValue("MaxCostUsd", 5m), 0.01m, 1000m),
            CostPerDayUsd = Math.Clamp(s.GetValue("CostPerDayUsd", 20m), 0.01m, 10_000m),
            SessionsPerDay = Math.Clamp(s.GetValue("SessionsPerDay", 10), 1, 1000),
            InputUsdPerMTok = Math.Clamp(s.GetValue("InputUsdPerMTok", 2m), 0m, 1000m),
            OutputUsdPerMTok = Math.Clamp(s.GetValue("OutputUsdPerMTok", 10m), 0m, 1000m),
            MaxTokensPerCall = Math.Clamp(s.GetValue("MaxTokensPerCall", 16000), 1000, 64_000),
            MaxToolResultChars = Math.Clamp(s.GetValue("MaxToolResultChars", 20_000), 1000, 200_000),
            Sandbox = s.GetValue("Sandbox", true),
            Workers = Math.Clamp(s.GetValue("Workers", 2), 1, 16),
            PollSeconds = Math.Clamp(s.GetValue("PollSeconds", 2), 1, 60),
            LeaseSeconds = Math.Clamp(s.GetValue("LeaseSeconds", 120), 5, 3600),
            RenewSeconds = Math.Clamp(s.GetValue("RenewSeconds", 10), 1, 600),
        };
    }
}
