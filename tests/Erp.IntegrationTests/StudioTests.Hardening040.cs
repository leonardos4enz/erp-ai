using System.Text.Json.Nodes;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 040 · Endurecimiento 6 (auditoría 7), H2 y H3 en Studio: una sesión que no se puede vencer no atora la cola, el
/// apagado cierra el turno y purga lo leído, una conversación con <c>datos</c> no cambia de empresa y detener un seguimiento
/// conserva el resumen de la propuesta vigente.
/// </summary>
public partial class StudioTests
{
    private Task<int> RunningSessionAsync(int uid, string prompt, string scopes = "", int leaseSeconds = -30) => host.ScalarAsync<int>($"""
        INSERT meta.StudioSession (UserId, SecurityStamp, Prompt, Scopes, Status, Model, LeaseToken, LeaseUntil, StartedAt, TurnStartedAt)
        OUTPUT inserted.Id SELECT {uid}, SecurityStamp, N'{prompt}', N'{scopes}', 'running', N'prueba', NEWID(),
               DATEADD(second, {leaseSeconds}, SYSUTCDATETIME()), SYSUTCDATETIME(), SYSUTCDATETIME()
          FROM meta.[User] WHERE Id = {uid}
        """);

    // ---------- H2 ----------

    [Fact]
    public async Task H040_H2_CA1_una_sesion_que_no_se_puede_vencer_no_detiene_a_las_demas()
    {
        (await NewModelerAsync("st-h40-a")).Dispose();
        (await NewModelerAsync("st-h40-b")).Dispose();
        using var c = await NewModelerAsync("st-h40-c");
        var poisoned = await RunningSessionAsync(host.Users["st-h40-a"], "envenenada", "datos");
        await host.ExecAsync($"""
            INSERT meta.StudioMessage (SessionId, Seq, FromUser, ContentJson, HasData) VALUES ({poisoned}, 1, 1, N'ilegible CANARIO-h40', 1);
            INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary) VALUES ({poisoned}, 1, 'tool', 'leer_registros', NULL, N'CANARIO-h40', 'ok', N'Leyó');
            """);
        // Su cierre normal falla (la bitácora la rechaza) mientras dure la falla.
        var fault = await host.FailWhenAsync("meta.AuditLog", $"Action = 'studio.finish' AND RecordId = '{poisoned}'");
        try
        {
            var healthy = await RunningSessionAsync(host.Users["st-h40-b"], "sana");
            host.Llm.Scripts["h40-cola"] = _ => Task.FromResult(Reply.Text("Atendido."));
            // Regresión (auditoría 7, D-03): las vencidas se cerraban en un solo lote y antes de reclamar; con una que fallaba,
            // ninguna pasaba a interrumpida y ningún turno nuevo se atendía.
            await WaitForAsync(() => host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {healthy}"), s => s == "interrupted", "la sana vencida");
            var s = await WaitAsync(c, (await StartAsync(c, "h40-cola", "Hola")).Id());
            Assert.True(s.Str("status") == "answered", s.Json());
            // Revisión adversarial 040 (F-08): la que falla siempre no se queda «en curso» para siempre: se cierra sin bitácora y
            // sin leer su historial, y lo leído con «datos» se retira igual.
            await WaitForAsync(() => host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {poisoned}"), s => s == "interrupted", "la envenenada cerrada");
            Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {poisoned} AND (HasData = 1 OR ContentJson LIKE N'%CANARIO-h40%')"));
            Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {poisoned} AND OutputJson IS NOT NULL"));
            Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'studio.finish' AND RecordId = '{poisoned}'"));
        }
        finally { await fault.DisposeAsync(); }
    }

    [Fact]
    public async Task H040_H2_CA3_al_apagar_el_turno_se_cierra_interrumpido_y_purga_lo_leido()
    {
        (await NewModelerAsync("st-h40-d")).Dispose();
        var uid = host.Users["st-h40-d"];
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["h40-apagado"] = async t =>
        {
            if (t.Index == 0) return Reply.Tool("leer_registros", new { entity = "StTarea" });
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, t.Ct); // el servidor se apaga mientras el modelo responde
            return Reply.Text("Tarde.");
        };
        // Un turno que corre en este proceso (con lease vigente: el trabajador de la API no lo toca) y que se detiene con el
        // token de apagado del servidor.
        var prompt = "[guion:h40-apagado] Lee las tareas";
        var id = await RunningSessionAsync(uid, prompt, "datos", leaseSeconds: 600);
        await host.ExecAsync($"""
            INSERT meta.StudioMessage (SessionId, Seq, FromUser, ContentJson, HasData)
            VALUES ({id}, 1, 1, N'{StudioTranscript.Serialize([new StudioText(StudioPrompt.Request(prompt))]).Replace("'", "''")}', 0)
            """);
        var lease = await host.ScalarAsync<Guid>($"SELECT LeaseToken FROM meta.StudioSession WHERE Id = {id}");
        var stamp = await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.[User] WHERE Id = {uid}");
        var claim = new StudioClaim(id, uid, null, stamp, prompt, "datos", lease, DateTime.UtcNow) { TurnStartedAt = DateTime.UtcNow };
        using var stopping = new CancellationTokenSource();
        Task run;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            run = scope.ServiceProvider.GetRequiredService<StudioAgent>().RunAsync(claim, stopping.Token);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // Lo leído está en la base mientras el turno corre (control).
            Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Tool = 'leer_registros' AND OutputJson IS NOT NULL"));
            Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id} AND HasData = 1"));
            await stopping.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        // Regresión (auditoría 7, D-05): al apagar, el turno salía sin cerrar ni purgar; si el servidor no volvía, lo leído se
        // quedaba en la base (el vencimiento nunca llegaba).
        Assert.Equal("interrupted", await host.ScalarAsync<string>($"SELECT Status FROM meta.StudioSession WHERE Id = {id}"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioStep WHERE SessionId = {id} AND Tool = 'leer_registros' AND OutputJson IS NOT NULL"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioMessage WHERE SessionId = {id} AND HasData = 1"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'studio.finish' AND RecordId = '{id}'"));
    }

    // ---------- H3 ----------

    [Fact]
    public async Task H040_H3_CA1_con_datos_otra_empresa_no_sigue_la_conversacion()
    {
        using var admin = await host.AdminAsync();
        var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
        var main = companies.First(x => x["isDefault"]!.GetValue<bool>()).Id();
        var other = companies.FirstOrDefault(x => x.Str("code") == "ST40-B")?.Id()
                    ?? (await admin.PostAsync("/api/admin/companies", new { code = "ST40-B", name = "Otra empresa 040", baseCurrency = "MXN" })).Body!.Id();
        if (!host.Users.ContainsKey("st-h40-e"))
        {
            var res = await admin.PostAsync("/api/admin/users", new { username = "st-h40-e", displayName = "st-h40-e", password = Pwd,
                roleIds = new[] { await admin.RoleIdAsync("Studio Modelador") }, companyIds = new[] { main, other } });
            Assert.True(res.Status == 201, res.Text);
            host.Users["st-h40-e"] = res.Body!.Id();
        }
        using var eva = await host.LoginAsync("st-h40-e", Pwd);
        Assert.Equal(200, (await eva.PostAsync("/api/auth/company", new { companyId = main })).Status);
        host.Llm.Scripts["h40-empresa"] = _ => Task.FromResult(Reply.Text("Listo."));
        var withData = (await WaitAsync(eva, (await StartAsync(eva, "h40-empresa", "Con datos", "datos")).Id())).Id();
        var withoutData = (await WaitAsync(eva, (await StartAsync(eva, "h40-empresa", "Sin datos")).Id())).Id();

        Assert.Equal(200, (await eva.PostAsync("/api/auth/company", new { companyId = other })).Status);
        // Regresión (auditoría 7, D-07): el mensaje copiaba la empresa nueva y lo que el modelo escribió citando registros de la
        // primera volvía al proveedor en un turno de la segunda.
        var res2 = await SendAsync(eva, withData, "Sigue");
        Assert.Equal(409, res2.Status);
        Assert.Contains("otra empresa", res2.Text);
        var s = (await eva.GetAsync($"/api/studio/sessions/{withData}")).Body!;
        Assert.False(s["canSend"]!.GetValue<bool>());
        Assert.Contains("otra empresa", s.Str("sendBlockedReason"));
        // Sin «datos», la conversación sigue y toma la empresa actual (036 H1 CA4).
        Assert.Equal("answered", (await SendOkAsync(eva, withoutData, "Sigue")).Str("status"));
        // De vuelta en su empresa, sigue.
        Assert.Equal(200, (await eva.PostAsync("/api/auth/company", new { companyId = main })).Status);
        Assert.Equal("answered", (await SendOkAsync(eva, withData, "Sigue")).Str("status"));
    }

    [Fact]
    public async Task H040_H3_CA2_detener_un_seguimiento_conserva_el_resumen_de_la_propuesta()
    {
        using var fer = await NewModelerAsync("st-h40-f");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Llm.Scripts["h40-resumen"] = async t =>
        {
            if (!LastAsk(t).Contains("lento")) return Reply.Tool(StudioTools.Deliver, new { summary = "Campo H40", plan = FieldPlan("Resumen040") });
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, t.Ct);
            return Reply.Text("Tarde.");
        };
        var id = (await WaitAsync(fer, (await StartAsync(fer, "h40-resumen", "Agrega un campo")).Id())).Id();
        Assert.Equal(202, (await SendAsync(fer, id, "Piénsalo lento")).Status);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(200, (await fer.PostAsync($"/api/studio/sessions/{id}/cancel")).Status);
        var s = await WaitAsync(fer, id);
        Assert.Equal("cancelled", s.Str("status"));
        Assert.Equal("proposed", s.Str("planStatus"));
        // Regresión (auditoría 7, D-08): el resumen solo se conservaba si el turno contestaba.
        Assert.Equal("Campo H40", s.Str("summary"));
        Assert.NotNull(s.Str("reviewHash"));
    }
}
