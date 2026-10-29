using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Studio;
using Microsoft.AspNetCore.Http;

namespace Erp.Tests;

/// <summary>
/// Spec 036: el historial exacto de una conversación de Studio (CLARIFY 2) y el formato de los eventos SSE. Sin base.
/// </summary>
public class StudioChatTests
{
    private static StudioToolUse Use(string id, string name = "listar_catalogos") => new(id, name, JsonSerializer.SerializeToElement(new { entity = "StTarea" }));
    private static StudioMessage User(params StudioBlock[] b) => new(true, b);
    private static StudioMessage Assistant(params StudioBlock[] b) => new(false, b);

    [Fact]
    public void Serializar_y_leer_conserva_ids_firmas_y_herramienta()
    {
        StudioBlock[] blocks =
        [
            new StudioThinking("pienso", "firma-1"), new StudioRedactedThinking("oculto"), new StudioText("Plan <x>"),
            Use("toolu_1"), new StudioToolResult("toolu_1", "<datos_no_confiables>…</datos_no_confiables>", true, "leer_registros"),
        ];
        var back = StudioTranscript.Deserialize(StudioTranscript.Serialize(blocks));
        Assert.Equal(blocks.Length, back.Count);
        Assert.Equal(new StudioThinking("pienso", "firma-1"), back[0]);
        Assert.Equal(new StudioRedactedThinking("oculto"), back[1]);
        Assert.Equal(new StudioText("Plan <x>"), back[2]);
        var use = Assert.IsType<StudioToolUse>(back[3]);
        Assert.Equal(("toolu_1", "listar_catalogos", "StTarea"), (use.Id, use.Name, use.Input.GetProperty("entity").GetString()));
        Assert.Equal(new StudioToolResult("toolu_1", "<datos_no_confiables>…</datos_no_confiables>", true, "leer_registros"), back[4]);
    }

    [Fact]
    public void Normalize_une_mensajes_seguidos_del_mismo_rol()
    {
        // Un turno que falló tras los resultados deja un mensaje de la persona antes del pedido siguiente.
        var msgs = StudioTranscript.Normalize([
            User(new StudioText("<pedido>uno</pedido>")),
            Assistant(Use("a")),
            User(new StudioToolResult("a", "r", false)),
            User(new StudioText("<pedido>dos</pedido>")),
        ]);
        Assert.Equal([true, false, true], msgs.Select(m => m.FromUser));
        Assert.IsType<StudioToolResult>(msgs[2].Blocks[0]);
        Assert.Equal(new StudioText("<pedido>dos</pedido>"), msgs[2].Blocks[1]);
    }

    [Fact]
    public void Normalize_cierra_los_usos_de_herramienta_sin_resultado()
    {
        // Turno interrumpido: el modelo pidió dos herramientas y solo una tiene resultado; después llega el pedido nuevo.
        var msgs = StudioTranscript.Normalize([
            User(new StudioText("uno")),
            Assistant(new StudioText("voy"), Use("a"), Use("b")),
            User(new StudioText("dos"), new StudioToolResult("a", "r", false)),
        ]);
        Assert.Equal(3, msgs.Count);
        var next = msgs[2].Blocks;
        // Los resultados van primero (la API lo exige) y el texto después.
        Assert.Equal("a", Assert.IsType<StudioToolResult>(next[0]).ToolUseId);
        var missing = Assert.IsType<StudioToolResult>(next[1]);
        Assert.Equal(("b", true, StudioTranscript.Interrupted), (missing.ToolUseId, missing.IsError, missing.Content));
        Assert.Equal(new StudioText("dos"), next[2]);
    }

    [Fact]
    public void Normalize_agrega_resultados_si_el_historial_termina_en_un_uso()
    {
        var msgs = StudioTranscript.Normalize([User(new StudioText("uno")), Assistant(Use("a"))]);
        Assert.Equal(3, msgs.Count);
        Assert.Equal("a", Assert.IsType<StudioToolResult>(Assert.Single(msgs[2].Blocks)).ToolUseId);
    }

    [Fact]
    public void Normalize_empieza_con_la_persona_y_quita_vacios()
    {
        var msgs = StudioTranscript.Normalize([Assistant(new StudioText("huérfano")), User(), User(new StudioText("hola"))]);
        Assert.Equal(new StudioText("hola"), Assert.Single(Assert.Single(msgs).Blocks));
    }

    [Fact]
    public void Purge_reemplaza_solo_los_resultados_de_datos()
    {
        var json = StudioTranscript.Serialize([
            new StudioToolResult("a", "<datos_no_confiables herramienta=\"leer_registro\">{\"Secreto\":\"CANARIO\"}</datos_no_confiables>", false, "leer_registro"),
            new StudioToolResult("b", "<datos_no_confiables herramienta=\"ver_catalogo\">{\"name\":\"StTarea\"}</datos_no_confiables>", false, "ver_catalogo"),
        ]);
        var purged = StudioTranscript.Purge(json, StudioTools.DataTools);
        Assert.NotNull(purged);
        Assert.DoesNotContain("CANARIO", purged);
        var blocks = StudioTranscript.Deserialize(purged!).Cast<StudioToolResult>().ToList();
        Assert.StartsWith("<datos_no_confiables herramienta=\"leer_registro\">", blocks[0].Content);
        Assert.Contains("Salida borrada al terminar el turno", blocks[0].Content);
        Assert.Equal("a", blocks[0].ToolUseId);
        Assert.Contains("StTarea", blocks[1].Content);
        // Idempotente: purgar otra vez no cambia nada.
        Assert.Null(StudioTranscript.Purge(purged!, StudioTools.DataTools));
        Assert.Null(StudioTranscript.Purge(StudioTranscript.Serialize([new StudioText("hola")]), StudioTools.DataTools));
    }

    [Fact]
    public async Task Un_evento_SSE_no_se_parte_aunque_los_datos_traigan_saltos()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        var data = new JsonObject { ["text"] = "línea 1\nlínea 2\r\nevent: falso" }.ToJsonString();
        await StudioStream.WriteAsync(http.Response, "live", data, "7", CancellationToken.None);
        var text = Encoding.UTF8.GetString(body.ToArray());
        var lines = text.Split('\n');
        Assert.Equal(["id: 7", "event: live"], lines.Take(2));
        Assert.StartsWith("data: {", lines[2]);
        Assert.Equal(["", ""], lines.Skip(3));
        Assert.Equal("línea 1\nlínea 2\r\nevent: falso", JsonNode.Parse(lines[2][6..])!["text"]!.GetValue<string>());
    }
    [Fact]
    public void F02_un_registro_con_la_frase_del_aviso_tambien_se_purga()
    {
        // Regresión (revisión adversarial 036, F-02): un resultado que ya «contenía» el aviso se saltaba la purga.
        var json = StudioTranscript.Serialize([new StudioToolResult("a",
            "<datos_no_confiables herramienta=\"leer_registro\">{\"Nota\":\"Salida borrada al terminar el turno\",\"Secreto\":\"CANARIO\"}</datos_no_confiables>", false, "leer_registro")]);
        var purged = StudioTranscript.Purge(json, StudioTools.DataTools);
        Assert.NotNull(purged);
        Assert.DoesNotContain("CANARIO", purged);
    }

    [Fact]
    public void F08_detener_solo_corta_el_turno_de_ese_lease()
    {
        // Regresión (revisión adversarial 036, F-08): un «detener» tardío cortaba el turno siguiente de la misma sesión.
        var signals = new StudioSignals();
        var stopped = 0;
        var lease = Guid.NewGuid();
        using (signals.Run(7, lease, () => stopped++))
        {
            Assert.False(signals.Stop(7, Guid.NewGuid()));
            Assert.Equal(0, stopped);
            Assert.True(signals.Stop(7, lease));
            Assert.Equal(1, stopped);
        }
        Assert.False(signals.Stop(7, lease));
    }
}

