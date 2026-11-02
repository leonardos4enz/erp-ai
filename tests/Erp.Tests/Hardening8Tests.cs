using Erp.Api.Assistant;
using Erp.Api.Companion;
using Erp.Api.Studio;
using Microsoft.Extensions.Configuration;

namespace Erp.Tests;

/// <summary>
/// Spec 049 · Endurecimiento 8 (auditoría 9, D-03): el tope de entrada de una sola llamada y el «prompt is too long» del
/// proveedor con su propio código.
/// </summary>
public class Hardening8Tests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void H3_CA1_la_entrada_se_estima_con_la_medicion_del_turno_o_por_caracteres()
    {
        // Sin medición (primera llamada del turno): un token por cada tres caracteres.
        Assert.Equal(10_000, StudioOptions.EstimateInput(30_000, 0, 0));
        // Con medición: la entrada medida más un token por cada dos caracteres nuevos (los resultados de la respuesta).
        Assert.Equal(12_000 + 50_000, StudioOptions.EstimateInput(130_000, 12_000, 30_000));
        Assert.Equal(12_000, StudioOptions.EstimateInput(20_000, 12_000, 30_000));
    }

    [Fact]
    public void H3_CA1_MaxInputTokens_por_omision_y_acotado()
    {
        Assert.Equal(180_000, CompanionOptions.From(Config()).MaxInputTokens);
        Assert.Equal(180_000, StudioOptions.From(Config()).MaxInputTokens);
        Assert.Equal(1000, CompanionOptions.From(Config(("Companion:MaxInputTokens", "5"))).MaxInputTokens);
        Assert.Equal(2_000_000, StudioOptions.From(Config(("Studio:MaxInputTokens", "999999999"))).MaxInputTokens);
    }

    [Fact]
    public void H3_CA2_prompt_is_too_long_se_reconoce()
    {
        // El mensaje del 400 de Anthropic cuando la entrada rebasa la ventana del modelo.
        Assert.True(AnthropicAssistantModel.TooLong("invalid_request_error: prompt is too long: 215339 tokens > 200000 maximum"));
        Assert.True(AnthropicAssistantModel.TooLong("Prompt Is Too Long"));
        Assert.False(AnthropicAssistantModel.TooLong("messages: text content blocks must be non-empty"));
        Assert.False(AnthropicAssistantModel.TooLong(null));
        Assert.Equal(StudioOptions.TooLongMessage, CompanionService.FailureMessage("too_long", null));
        Assert.Equal(StudioOptions.TooLongMessage, StudioService.FailureMessage("too_long", null));
    }
}
