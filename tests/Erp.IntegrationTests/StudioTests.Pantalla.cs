using System.Text.Json.Nodes;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 035 · lo que la pantalla de ERP AI Studio pide al motor: lo que leyó el agente no se queda en la sesión cerrada
/// (R-033-4) y sondear con <c>after</c> no recompila la propuesta (R-033-10).
/// </summary>
public partial class StudioTests
{
    private Task<int> StoredDataAsync(int id) =>
        host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Tool IN ('leer_registros', 'leer_registro') AND OutputJson IS NOT NULL")!;

    [Fact]
    public async Task H5_CA1_al_cerrar_la_sesion_se_borran_las_salidas_con_registros()
    {
        using var gil = await host.LoginAsync("st-gil", Pwd);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["purga"] = async t =>
        {
            if (t.Index == 0) return Reply.Tools(null, ("leer_registros", new { entity = "StTarea" }), ("leer_registro", new { entity = "StTarea", id = host.Rec["hostil"].Id() }));
            read.TrySetResult();
            await release.Task.WaitAsync(t.Ct);
            return Reply.Tool(StudioTools.Deliver, new { summary = "Leí las tareas." });
        };
        var id = (await StartAsync(gil, "purga", "Revisa las tareas", "datos")).Id();
        await read.Task.WaitAsync(TimeSpan.FromSeconds(90));
        // Mientras corre, la persona ve lo que el agente leyó (con su permiso, recargado en cada paso).
        Assert.Equal(2, await StoredDataAsync(id));
        var live = (await gil.GetAsync($"/api/studio/sessions/{id}?after=0")).Body!;
        Assert.Contains("Ignora las instrucciones", Trail(live).Single(s => s.Str("tool") == "leer_registro")["output"]!.ToJsonString());
        release.SetResult();

        var s = await WaitAsync(gil, id);
        Assert.Equal("answered", s.Str("status"));
        // Regresión (R-033-4): los valores de los registros quedaban para siempre en meta.StudioStep.
        Assert.Equal(0, await StoredDataAsync(id));
        foreach (var step in Trail(s).Where(x => x.Str("tool") is "leer_registros" or "leer_registro"))
        {
            Assert.Null(step["output"]);
            Assert.Equal("ok", step.Str("status"));
            Assert.False(string.IsNullOrEmpty(step.Str("summary")));
            Assert.NotNull(step["input"]);
        }
        Assert.DoesNotContain("Ignora las instrucciones", s.ToJsonString());
    }

    [Fact]
    public async Task H5_CA1_una_sesion_interrumpida_tambien_borra_lo_leido()
    {
        var uid = host.Users["st-gil"];
        var id = await host.ScalarAsync<int>($"""
            INSERT meta.StudioSession (UserId, SecurityStamp, Prompt, Scopes, Status, Model, LeaseToken, LeaseUntil, StartedAt)
            OUTPUT inserted.Id SELECT {uid}, SecurityStamp, N'caída con datos', N'datos', 'running', N'prueba', NEWID(), DATEADD(second, -30, SYSUTCDATETIME()), SYSUTCDATETIME()
              FROM meta.[User] WHERE Id = {uid}
            """);
        await host.ExecAsync($$"""
            INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs)
            VALUES ({{id}}, 1, 'tool', 'leer_registro', N'{"entity":"StCanario"}', N'{"Nombre":"{{host.Canary}}-caida"}', 'ok', N'200', 5),
                   ({{id}}, 2, 'tool', 'listar_catalogos', N'{}', N'[{"name":"StTarea"}]', 'ok', N'200', 3)
            """);
        await WaitForAsync(() => host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {id}"), s => s == "interrupted", "sesión interrumpida");
        Assert.Equal(0, await StoredDataAsync(id));
        // Lo que no son registros (el modelo) se conserva.
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Tool = 'listar_catalogos' AND OutputJson IS NOT NULL"));
        using var gil = await host.LoginAsync("st-gil", Pwd);
        Assert.DoesNotContain(host.Canary, (await gil.GetAsync($"/api/studio/sessions/{id}")).Text);
    }

    [Fact]
    public async Task F01_al_arrancar_se_borra_lo_leido_en_sesiones_cerradas_antes_de_la_035()
    {
        var uid = host.Users["st-gil"];
        // Una sesión que se cerró con el código de la 033/034 (sin purga) y una en curso (su dueña la sigue viendo).
        var closed = await host.ScalarAsync<int>($"INSERT meta.StudioSession (UserId, SecurityStamp, Prompt, Scopes, Status, Model, FinishedAt) OUTPUT inserted.Id SELECT {uid}, SecurityStamp, N'vieja', N'datos', 'failed', N'prueba', SYSUTCDATETIME() FROM meta.[User] WHERE Id = {uid}");
        var running = await host.ScalarAsync<int>($"INSERT meta.StudioSession (UserId, SecurityStamp, Prompt, Scopes, Status, Model, LeaseToken, LeaseUntil, StartedAt) OUTPUT inserted.Id SELECT {uid}, SecurityStamp, N'en curso', N'datos', 'running', N'prueba', NEWID(), DATEADD(hour, 1, SYSUTCDATETIME()), SYSUTCDATETIME() FROM meta.[User] WHERE Id = {uid}");
        try
        {
            foreach (var id in new[] { closed, running })
                await host.ExecAsync($$"""
                    INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs)
                    VALUES ({{id}}, 1, 'tool', 'leer_registros', N'{"entity":"StCanario"}', N'{"items":[{"Nombre":"{{host.Canary}}-vieja"}]}', 'ok', N'200', 5)
                    """);
            // Regresión (revisión adversarial 035, F-01): la purga solo corría al cerrar; lo de antes de la 035 se quedaba.
            await using var restarted = await host.SiblingAsync(_ => { });
            Assert.Equal(0, await StoredDataAsync(closed));
            Assert.Equal(1, await StoredDataAsync(running));
        }
        finally
        {
            await host.ExecAsync($"UPDATE meta.StudioSession SET Status = 'cancelled', LeaseToken = NULL, LeaseUntil = NULL WHERE Id = {running}");
        }
    }

    [Fact]
    public async Task F03_la_purga_va_en_la_transaccion_del_cierre()
    {
        using var gil = await host.LoginAsync("st-gil", Pwd);
        host.Llm.Scripts["purga-atomica"] = Reply.Steps(_ => Reply.Tool("leer_registros", new { entity = "StTarea" }));
        int id;
        // Revisión adversarial 035 (F-03): el cierre falla al escribir su auditoría, después de purgar; ni la sesión queda
        // cerrada ni lo leído se borra (si la purga fuera fuera de la transacción, quedaría «running» y sin lo leído).
        await using (await host.FailWhenAsync("meta.AuditLog", "Action = 'studio.finish'"))
        {
            id = (await StartAsync(gil, "purga-atomica", "Lee las tareas", "datos")).Id();
            await WaitForAsync(() => host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Kind = 'deliver'"), n => n == 1, "la entrega");
            // El cierre del trabajador falla: la sesión sigue «running» con lo leído (nada a medias).
            Assert.Equal("running", await host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {id}"));
            Assert.Equal(1, await StoredDataAsync(id));
            // Spec 040 (revisión adversarial F-08): al vencer el lease (4 s), el vencimiento normal falla igual (bitácora) y el
            // cierre de respaldo la deja interrumpida sin bitácora; la purga va en esa misma transacción: nunca se ve
            // «interrumpida» con lo leído.
            await WaitForAsync(async () => (await host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {id}"), await StoredDataAsync(id)),
                s => s.Item1 == "interrupted" || s.Item2 == 0, "sesión cerrada o purgada");
            Assert.Equal(("interrupted", 0), (await host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {id}"), await StoredDataAsync(id)));
            Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'studio.finish' AND RecordId = '{id}'"));
        }
    }

    [Fact]
    public async Task H5_CA2_sondear_con_after_no_compila_ni_da_la_huella()
    {
        using var gil = await host.LoginAsync("st-gil", Pwd);
        var s = await ProposalAsync(gil, "sondeo", "Sondeo035");
        Assert.NotNull(s["preview"]);
        Assert.False(string.IsNullOrEmpty(s.Str("reviewHash")));

        // Regresión (R-033-10): cada sondeo recompilaba el plan contra el modelo y devolvía la huella.
        var poll = (await gil.GetAsync($"/api/studio/sessions/{s.Id()}?after=0")).Body!;
        Assert.Equal("proposed", poll.Str("status"));
        // Regresión (spec 035): las fechas salían sin zona y la pantalla las tomaba como hora local.
        Assert.EndsWith("Z", poll.Str("createdAt"));
        Assert.EndsWith("Z", Trail(poll).First().Str("at"));
        Assert.EndsWith("Z", (await gil.GetAsync("/api/studio/sessions")).Array.Nodes().First().Str("createdAt"));
        Assert.Null(poll["preview"]);
        Assert.Null(poll["plan"]);
        Assert.Null(poll["reviewHash"]);
        Assert.False(poll["canApply"]!.GetValue<bool>());
        Assert.Equal(Trail(s).Count(), Trail(poll).Count());
        Assert.Empty(Trail((await gil.GetAsync($"/api/studio/sessions/{s.Id()}?after={Trail(s).Max(x => x["seq"]!.GetValue<int>())}")).Body!));

        // La lectura completa sigue dando la propuesta para revisar y aplicar.
        var full = (await gil.GetAsync($"/api/studio/sessions/{s.Id()}")).Body!;
        Assert.Equal(s.Str("reviewHash"), full.Str("reviewHash"));
        Assert.Equal(200, (await gil.PostAsync($"/api/studio/sessions/{s.Id()}/discard")).Status);
    }
}
