using System.Text.Json.Nodes;
using Erp.Api.Companion;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 049 · Endurecimiento 8 (auditoría 9, D-01): <c>Companion:Enabled=false</c> apaga también lo que ya estaba en marcha. La
/// API arranca encendida (una conversación queda esperando a la persona y otra en cola) y se reinicia apagada, como el dueño
/// que la apaga por un incidente de inyección o de costo: nada vuelve a llamar al proveedor ni a escribir. Clase propia porque
/// reinicia la instancia principal.
/// </summary>
public sealed class CompanionApagadoTests(CompanionApagadoTests.Host host) : IClassFixture<CompanionApagadoTests.Host>
{
    private const string Pwd = "frase larga del acompañante apagado 2049";

    public sealed class Host : ErpHost
    {
        public readonly FakeStudioModel Llm = new();

        protected override void Configure(IDictionary<string, string?> s)
        {
            s["Assistant:AnthropicApiKey"] = "sk-ant-prueba-integracion-no-es-real-0000";
            s["Companion:PollSeconds"] = "1";
            s["Companion:RenewSeconds"] = "1";
            s["Companion:LeaseSeconds"] = "4";
            s["Companion:ApplyingSeconds"] = "5";
        }

        protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IStudioModel>(Llm);

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            await admin.ApplyPlanAsync(new { description = "Spec 049 · apagado", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 049", name = "ApGato", label = "Gato 049", labelPlural = "Gatos 049", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } } } });
            Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = "Ap Usuario", description = "Spec 049", canManageModel = false,
                canManageUsers = false, canViewAudit = false,
                permissions = new[] { new { entity = "ApGato", canRead = true, canCreate = true, canUpdate = true, canDelete = false } } })).Status);
            var role = await admin.RoleIdAsync("Ap Usuario");
            foreach (var u in new[] { "ap-ana", "ap-beto" })
                Assert.Equal(201, (await admin.PostAsync("/api/admin/users", new { username = u, displayName = u, password = Pwd, roleIds = new[] { role } })).Status);
        }
    }

    private static Func<FakeStudioModel.Turn, Task<StudioReply>> Script(params Func<FakeStudioModel.Turn, StudioReply>[] steps) => t =>
        Task.FromResult(t.Index < steps.Length ? steps[t.Index](t) : Reply.Text("Listo."));

    private static Task<JsonNode> WaitAsync(Client api, int id, params string[] statuses) =>
        WaitForAsync(async () => (await api.GetAsync($"/api/companion/conversations/{id}")).Body!,
            c => statuses.Contains(c.Str("status")), $"conversación {id} en {string.Join("/", statuses)}", seconds: 60);

    [Fact]
    public async Task H049_H1_apagado_no_reclama_no_reanuda_y_decidir_responde_503()
    {
        var nombre = "Apagado " + Guid.NewGuid().ToString("N")[..6];
        host.Llm.Scripts["ap-espera"] = Script(_ => Reply.Tool(CompanionTools.Propose, new { tipo = "crear", catalogo = "ApGato", valores = new { Nombre = nombre }, explicacion = "Lo registro" }));
        host.Llm.Scripts["ap-cola"] = Script(_ => Reply.Text("Hola."));
        int waiting, queued, action;
        string hash;
        using (var ana = await host.LoginAsync("ap-ana", Pwd))
        {
            waiting = (await WaitAsync(ana, (await ana.PostAsync("/api/companion/conversations", new { text = "[guion:ap-espera] Registra a mi gato" })).Body!.Id(), "waiting")).Id();
            var a = (await ana.GetAsync($"/api/companion/conversations/{waiting}")).Body!.Arr("actions").Nodes().Single();
            (action, hash) = (a.Id(), a.Str("hash")!);
        }
        using (var beto = await host.LoginAsync("ap-beto", Pwd))
            queued = (await WaitAsync(beto, (await beto.PostAsync("/api/companion/conversations", new { text = "[guion:ap-cola] Hola" })).Body!.Id(), "answered")).Id();
        Assert.Equal(1, host.Llm.Calls("ap-espera"));
        Assert.Equal(1, host.Llm.Calls("ap-cola"));

        // El dueño apaga el Acompañante y reinicia. Un mensaje de beto quedó en cola justo antes (el estado que deja SendAsync).
        await host.RestartAsync(s => s["Companion:Enabled"] = "false");
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET Status = 'queued', Turns = Turns + 1 WHERE Id = {queued}");

        using var ana2 = await host.LoginAsync("ap-ana", Pwd);
        // Regresión (auditoría 9, D-01): confirmar escribía y reanudaba el turno; descartar con motivo llamaba al modelo.
        var confirm = await ana2.PostAsync($"/api/companion/actions/{action}/confirm", new { hash });
        Assert.Equal(503, confirm.Status);
        Assert.Contains("apagado", confirm.Text);
        var discard = await ana2.PostAsync($"/api/companion/actions/{action}/discard", new { reason = "Mejor llámalo Michi" });
        Assert.Equal(503, discard.Status);
        Assert.Equal(503, (await ana2.PostAsync($"/api/companion/conversations/{waiting}/messages", new { text = "No" })).Status);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.ApGato WHERE Nombre = N'{nombre}'"));

        // El trabajador no reclama la cola ni reanuda la espera: las cierra, con lo propuesto descartado.
        var closed = await WaitAsync(ana2, waiting, "failed");
        Assert.Contains("se apagó", closed.Str("error"));
        Assert.Equal("discarded", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE Id = {action}"));
        Assert.Equal("failed:disabled", await WaitForAsync(() => host.ScalarAsync<string>($"SELECT Status + ':' + ISNULL(ErrorCode, '') FROM meta.CompanionConversation WHERE Id = {queued}"),
            s => s != "queued:", "la cola se cierra"));
        // Ni el barrido (que reanuda lo que espera sin pendientes) ni la cola vuelven a llamar al proveedor.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, host.Llm.Calls("ap-espera"));
        Assert.Equal(1, host.Llm.Calls("ap-cola"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'companion.finish' AND RecordId = '{waiting}' AND AfterJson LIKE '%disabled%'"));
    }
}
