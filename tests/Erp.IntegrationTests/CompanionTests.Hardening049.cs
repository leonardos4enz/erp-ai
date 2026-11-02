using System.Text.Json.Nodes;
using Erp.Api.Assistant;
using Erp.Api.Companion;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 049 · Endurecimiento 8 (auditoría 9) en el Acompañante: una propuesta no sobrevive a un turno que termina sin
/// preguntarle a la persona y su resultado vuelve al modelo (D-02), una sola llamada que no cabe en la ventana del modelo se
/// detiene con un mensaje claro (D-03), decidir reanuda con el sello de ahora (O-01), una partida oculta no se nombra en la
/// procedencia ni en la búsqueda (O-03) y detener y borrar piden la empresa de la conversación (O-04). El interruptor apagado
/// (D-01) está en <see cref="CompanionApagadoTests"/>.
/// </summary>
public partial class CompanionTests
{
    // Los textos van literales (no las constantes del motor) para que las pruebas compilen contra el código anterior.
    private const string EndedError049 = "El turno terminó antes de preguntarle a la persona (un tope, un cambio de su cuenta o una interrupción).";
    private const string TooLong049 = "Lo que el modelo tendría que leer de una vez ya no cabe: pide algo más acotado o empieza una conversación nueva.";

    /// <summary>
    /// Spec 049 (O-03): la nómina tiene partidas que solo ve el administrador; una automatización del documento sobre sus
    /// partidas llena el último pago del empleado (las partidas no declaran automatizaciones propias).
    /// </summary>
    private static readonly object NominaPlan = new
    {
        description = "Spec 049 · nómina", operations = new object[] {
            new { op = "createEntity", group = Group, name = "CoEmpleado", label = "Empleado 049", labelPlural = "Empleados 049", displayField = "Nombre", fields = new object[] {
                new { name = "Nombre", label = "Nombre", type = "text", required = true },
                new { name = "UltimoPago", label = "Último pago", type = "decimal", precision = 18, scale = 2, editable = false },
                new { name = "Sueldo", label = "Sueldo", type = "decimal", precision = 18, scale = 2 } } },
            // O-02: una regla y una condición sobre el sueldo, que solo ve el administrador; otra regla sobre un campo visible.
            new { op = "setSecurity", entity = "CoEmpleado", security = new {
                fields = new object[] { new { field = "Sueldo", read = new[] { "Administrador" } } } } },
            new { op = "setRules", entity = "CoEmpleado", rules = new object[] {
                new { name = "TopePago", field = "Sueldo", assert = "ISBLANK(Sueldo) OR Sueldo <= 90417", message = "El pago rebasa el tope autorizado." },
                new { name = "NombreValido", field = "Nombre", assert = "Nombre <> 'Prohibido'", message = "Ese nombre no se admite." } } },
            new { op = "setWorkflow", entity = "CoEmpleado", workflow = new {
                initial = "Activo",
                states = new object[] { new { name = "Activo", label = "Activo" }, new { name = "Baja", label = "Baja" } },
                transitions = new object[] { new { name = "baja", label = "Dar de baja", from = new[] { "Activo" }, to = "Baja",
                    when = "Sueldo < 50417", whenMessage = "Solo se da de baja con autorización." } } } },
            new { op = "createEntity", group = Group, name = "CoNomina", label = "Nómina 049", labelPlural = "Nóminas 049", displayField = "Nombre", fields = new object[] {
                new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
            new { op = "createEntity", name = "CoNominaPartida", label = "Partida secreta de nómina", labelPlural = "Partidas secretas de nómina", fields = new object[] {
                new { name = "Nomina", label = "Nómina", type = "parent", referenceEntity = "CoNomina" },
                new { name = "Empleado", label = "Empleado", type = "reference", referenceEntity = "CoEmpleado", required = true },
                new { name = "Monto", label = "Monto", type = "decimal", precision = 18, scale = 2, required = true } } },
            new { op = "addField", entity = "CoNomina", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "CoNominaPartida" } },
            new { op = "setSecurity", entity = "CoNomina", security = new {
                fields = new object[] { new { field = "Partidas", read = new[] { "Administrador" } } } } },
            new { op = "setAutomations", entity = "CoNomina", automations = new object[] {
                new { name = "recalcular", label = "Recalcular nómina oculta (pago secreto)", on = "create", actions = new object[] {
                    new { type = "update", lines = "Partidas", target = "Empleado", set = new object[] { new { field = "UltimoPago", value = "Monto" } } } } } } },
        },
    };

    // ---------- D-02: la propuesta de un turno que no llega a «esperando» ----------

    [Fact]
    public async Task H049_H2_CA1_un_tope_a_media_respuesta_descarta_la_propuesta_y_su_resultado_vuelve_al_modelo()
    {
        using var admin = await host.AdminAsync();
        var pedido = await PedidoAsync(admin, "Pedido 049 tope");
        using var kai = await host.LoginAsync("co-kai", Pwd);
        var nombre = "Tope " + Guid.NewGuid().ToString("N")[..6];
        IReadOnlyList<string>? resultados = null;
        host.Llm.Scripts["d02-tope"] = Script(_ => Reply.Text("Hola."),
            _ => Reply.Tools(null, (CompanionTools.Propose, CrearGato(nombre)), ("ver_registro", new { catalogo = "CoPedido", id = pedido.Id() })),
            t => { resultados = t.Results.Select(r => r.Content).ToList(); return Reply.Text("Entendido."); });
        var cid = (await WaitAsync(kai, (await StartAsync(kai, "d02-tope", "Hola")).Id())).Id();
        // Determinista (informe de la auditoría 9): a una consulta del tope, la respuesta trae la propuesta y otra consulta.
        var max = host.Services.GetRequiredService<CompanionOptions>().MaxToolCalls;
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET ToolCalls = {max - 1} WHERE Id = {cid}");
        Assert.Equal(202, (await kai.PostAsync($"/api/companion/conversations/{cid}/messages", new { text = "Registra a mi gato y revisa el pedido" })).Status);
        var c = await WaitAsync(kai, cid);
        Assert.Equal("failed", c.Str("status"));
        var action = Assert.Single(c.Arr("actions").Nodes());
        // Regresión (auditoría 9, D-02): la propuesta seguía confirmable y confirmarla no reanudaba nada.
        Assert.Equal("discarded", action.Str("status"));
        Assert.Equal(EndedError049, action.Str("error"));
        Assert.Equal(409, (await kai.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") })).Status);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.CoGato WHERE Nombre = N'{nombre}'"));

        // En el mensaje siguiente el modelo sabe qué pasó con su propuesta (no «el turno se interrumpió»).
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET ToolCalls = 0 WHERE Id = {cid}");
        Assert.Equal(202, (await kai.PostAsync($"/api/companion/conversations/{cid}/messages", new { text = "¿Qué pasó?" })).Status);
        Assert.Equal("answered", (await WaitAsync(kai, cid)).Str("status"));
        Assert.Contains("\"resultado\":\"detenida\"", resultados![0]);
        Assert.Contains("No se le preguntó a la persona", resultados[0]);
        Assert.Equal(StudioTranscript.Interrupted, resultados[1]);
    }

    [Fact]
    public async Task H049_H2_CA2_detener_durante_otra_consulta_de_la_misma_respuesta_descarta_la_propuesta()
    {
        using var admin = await host.AdminAsync();
        var pedido = await PedidoAsync(admin, "Pedido 049 detener");
        using var leo = await host.LoginAsync("co-leo", Pwd);
        var nombre = "Detenido " + Guid.NewGuid().ToString("N")[..6];
        var ver = ("ver_registro", (object?)new { catalogo = "CoPedido", id = pedido.Id() });
        host.Llm.Scripts["d02-detener"] = Script(_ => Reply.Tools(null, (CompanionTools.Propose, CrearGato(nombre)), ver, ver));
        int cid;
        // Otra conexión retiene el pedido: la primera lectura queda detenida después de guardar la propuesta.
        await using (await host.HoldAsync($"UPDATE app.CoPedido SET Monto = Monto WHERE Id = {pedido.Id()}"))
        {
            cid = (await StartAsync(leo, "d02-detener", "Registra a mi gato y revisa el pedido")).Id();
            await WaitForAsync(() => host.ScalarAsync<int>("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()"),
                n => n >= 1, "la lectura del pedido detenida");
            Assert.Equal("proposed", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE ConversationId = {cid}"));
            Assert.Equal(200, (await leo.PostAsync($"/api/companion/conversations/{cid}/cancel")).Status);
        }
        var c = await WaitAsync(leo, cid);
        Assert.Equal("cancelled", c.Str("status"));
        var action = Assert.Single(c.Arr("actions").Nodes());
        // Regresión (auditoría 9, D-02; 048 H1 CA6): detener es «no» a lo pendiente también a media respuesta.
        Assert.Equal("discarded", action.Str("status"));
        Assert.Equal("La persona detuvo la conversación.", action.Str("error"));
        Assert.Equal(409, (await leo.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") })).Status);
    }

    [Fact]
    public async Task H049_H2_CA3_un_turno_sin_trabajador_descarta_lo_que_propuso()
    {
        using var max = await host.LoginAsync("co-max", Pwd);
        var (cid, action) = await ProposeAsync(max, "d02-caida", CrearGato("Caída " + Guid.NewGuid().ToString("N")[..6]));
        // El proceso cayó con el turno corriendo después de guardar la propuesta: su lease vence.
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET Status = 'running', LeaseToken = NEWID(), LeaseUntil = DATEADD(second, -30, SYSUTCDATETIME()) WHERE Id = {cid}");
        await WaitForAsync(() => StatusAsync(cid), s => s == "interrupted", "turno interrumpido por el barrido");
        // Regresión (auditoría 9, D-02): ExpireAsync dejaba la propuesta confirmable.
        Assert.Equal("discarded", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE Id = {action.Id()}"));
        Assert.Equal(409, (await max.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") })).Status);
    }

    // ---------- D-03: una sola llamada que no cabe ----------

    [Fact]
    public async Task H049_H3_CA1_una_llamada_que_no_cabe_se_detiene_antes_del_proveedor()
    {
        using var admin = await host.AdminAsync();
        var grande = await PedidoAsync(admin, "Pedido 049 grande", notas: string.Concat(Enumerable.Repeat("Nota larga del pedido. ", 850)));
        using var nora = await host.LoginAsync("co-nora", Pwd);
        var lecturas = Enumerable.Range(0, 8).Select(_ => ("ver_registro", (object?)new { catalogo = "CoPedido", id = grande.Id() })).ToArray();
        host.Llm.Scripts["d03-grande"] = Script(_ => Reply.Tools(null, lecturas), _ => Reply.Text("No debería llegar aquí."));
        var c = await WaitAsync(nora, (await StartAsync(nora, "d03-grande", "Revisa ese pedido a fondo")).Id());
        // Regresión (auditoría 9, D-03): la llamada viajaba y el «prompt is too long» del proveedor se decía «error inesperado».
        Assert.Equal("failed", c.Str("status"));
        Assert.Equal(TooLong049, c.Str("error"));
        Assert.Equal(1, host.Llm.Calls("d03-grande"));
        Assert.Contains(Trail(c), s => s.Str("kind") == "limit" && s.Str("summary") == TooLong049);
    }

    [Fact]
    public async Task H049_H3_CA2_el_prompt_is_too_long_del_proveedor_tiene_su_mensaje()
    {
        using var nora = await host.LoginAsync("co-nora", Pwd);
        host.Llm.Scripts["d03-proveedor"] = _ => throw new AssistantProviderException("too_long");
        var c = await WaitAsync(nora, (await StartAsync(nora, "d03-proveedor", "Hola")).Id());
        Assert.Equal("failed", c.Str("status"));
        Assert.Equal(TooLong049, c.Str("error"));
    }

    // ---------- O-01: decidir reanuda con el sello de ahora ----------

    [Fact]
    public async Task H049_H4_CA1_tras_cambiar_la_contrasena_decidir_reanuda_con_la_sesion_nueva()
    {
        using var admin = await host.AdminAsync();
        string? resultado = null;
        var nombre = "Sello " + Guid.NewGuid().ToString("N")[..6];
        host.Llm.Scripts["o01"] = Script(_ => Reply.Tool(CompanionTools.Propose, CrearGato(nombre)),
            t => { resultado = ResultOf(t); return Reply.Text("Listo."); });
        int cid;
        JsonNode action;
        using (var olga = await host.LoginAsync("co-olga", Pwd))
        {
            cid = (await WaitAsync(olga, (await StartAsync(olga, "o01", "Registra a mi gato")).Id())).Id();
            action = (await olga.GetAsync($"/api/companion/conversations/{cid}")).Body!.Arr("actions").Nodes().Single();
        }
        // Cambia su contraseña (cambia su sello) y vuelve a entrar.
        const string nueva = "otra frase larga del acompañante 2049";
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Users["co-olga"]}", new { displayName = "co-olga", isActive = true, password = nueva,
            roleIds = new[] { await admin.RoleIdAsync("Comp Usuario"), await admin.RoleIdAsync("Comp Nomina") } })).Status);
        try
        {
            using var again = await host.LoginAsync("co-olga", nueva);
            Assert.Equal(200, (await again.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") })).Status);
            // Regresión (auditoría 9, O-01): el turno reanudado se detenía por identidad con el sello del turno.
            var c = await WaitResumedAsync(again, cid);
            Assert.Equal("answered", c.Str("status"));
            Assert.Contains("\"resultado\":\"aplicada\"", resultado);
        }
        finally
        {
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Users["co-olga"]}", new { displayName = "co-olga", isActive = true, password = Pwd,
                roleIds = new[] { await admin.RoleIdAsync("Comp Usuario"), await admin.RoleIdAsync("Comp Nomina") } })).Status);
        }
    }

    // ---------- O-03: una partida oculta no se nombra ----------

    [Fact]
    public async Task H049_H5_CA1_la_procedencia_y_la_busqueda_no_nombran_partidas_ocultas()
    {
        using var olga = await host.LoginAsync("co-olga", Pwd);
        var def = await olga.GetAsync("/api/model/entities/CoEmpleado");
        Assert.Equal(200, def.Status);
        var pago = def.Body!.Arr("fields").Nodes().Single(f => f.Str("name") == "UltimoPago");
        // Regresión (auditoría 9, O-03): bastaba leer la nómina para ver la automatización de su partida oculta.
        Assert.Null(pago["filledBy"]);
        Assert.DoesNotContain("Pago secreto", def.Text);
        Assert.DoesNotContain("Recalcular nómina oculta", def.Text);
        Assert.DoesNotContain("Partida secreta", def.Text);
        // Quien ve las partidas (el administrador) la recibe.
        using var admin = await host.AdminAsync();
        var adminPago = (await admin.GetAsync("/api/model/entities/CoEmpleado")).Body!.Arr("fields").Nodes().Single(f => f.Str("name") == "UltimoPago");
        Assert.Equal("Recalcular nómina oculta (pago secreto)", Assert.Single(adminPago.Arr("filledBy").Nodes()).Str("label"));

        string? encontrado = null;
        host.Llm.Scripts["o03"] = Script(_ => Reply.Tool("buscar_en_el_sistema", new { buscar = "nómina oculta" }),
            t => { encontrado = ResultOf(t); return Reply.Text("Nada."); });
        await WaitAsync(olga, (await StartAsync(olga, "o03", "¿Qué recalcula la nómina?")).Id());
        Assert.DoesNotContain("Recalcular", encontrado);
        Assert.DoesNotContain("Partida secreta", encontrado);
    }

    [Fact]
    public async Task H049_H5_CA2_la_ruta_del_modelo_no_entrega_lo_de_partidas_ocultas()
    {
        using var olga = await host.LoginAsync("co-olga", Pwd);
        // Revisión adversarial 049 (F-01): la ruta entregaba la automatización cruda (su campo de partidas y lo que asigna).
        var nomina = await olga.GetAsync("/api/model/entities/CoNomina");
        Assert.Equal(200, nomina.Status);
        Assert.DoesNotContain("Recalcular nómina oculta", nomina.Text);
        Assert.DoesNotContain("Partidas", nomina.Text);
        var lista = await olga.GetAsync("/api/model/entities?includeChildren=true");
        Assert.DoesNotContain("Recalcular nómina oculta", lista.Text);
        // Revisión adversarial 049 (F-02): la definición de la partida tampoco se entrega a quien no ve su campo.
        Assert.DoesNotContain("CoNominaPartida", lista.Text);
        Assert.Equal(403, (await olga.GetAsync("/api/model/entities/CoNominaPartida")).Status);
        using var admin = await host.AdminAsync();
        Assert.Equal(200, (await admin.GetAsync("/api/model/entities/CoNominaPartida")).Status);
        Assert.Contains("Recalcular nómina oculta", (await admin.GetAsync("/api/model/entities/CoNomina")).Text);
    }

    // ---------- Revisión adversarial 049 ----------

    [Fact]
    public async Task Regresion_049_F03_una_propuesta_caducada_no_se_confirma_ni_reanuda_desde_otra_empresa()
    {
        using var admin = await host.AdminAsync();
        var (main, other) = await TwoCompaniesAsync(admin);
        var res = await admin.PostAsync("/api/admin/users", new { username = "co-f03b", displayName = "co-f03b", password = Pwd,
            roleIds = new[] { await admin.RoleIdAsync("Comp Usuario") }, companyIds = new[] { main, other } });
        Assert.True(res.Status == 201, res.Text);
        using var f03 = await host.LoginAsync("co-f03b", Pwd);
        Assert.Equal(200, (await f03.PostAsync("/api/auth/company", new { companyId = main })).Status);
        var (cid, action) = await ProposeAsync(f03, "f03b", CrearGato("F03 " + Guid.NewGuid().ToString("N")[..6]));
        await host.ExecAsync($"UPDATE meta.CompanionAction SET CreatedAt = DATEADD(hour, -25, SYSUTCDATETIME()) WHERE Id = {action.Id()}");
        Assert.Equal(200, (await f03.PostAsync("/api/auth/company", new { companyId = other })).Status);
        var wrong = await f03.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") });
        Assert.Equal(409, wrong.Status);
        Assert.Contains("otra empresa", wrong.Text);
        Assert.Equal("proposed", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE Id = {action.Id()}"));
        Assert.Equal("waiting", await StatusAsync(cid));
        Assert.Equal(200, (await f03.PostAsync("/api/auth/company", new { companyId = main })).Status);
        Assert.Equal(200, (await f03.PostAsync($"/api/companion/conversations/{cid}/cancel")).Status);
    }

    [Fact]
    public async Task Regresion_049_F04_una_propuesta_que_quedo_de_un_turno_cerrado_no_se_aplica()
    {
        using var hugo = await host.LoginAsync("co-hugo", Pwd);
        var nombre = "Huérfana " + Guid.NewGuid().ToString("N")[..6];
        var (cid, action) = await ProposeAsync(hugo, "f04", CrearGato(nombre));
        // Como la dejaba un turno cerrado antes de esta spec (D-02): la conversación terminó y la propuesta siguió pendiente.
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET Status = 'failed', ErrorCode = 'limit_tools' WHERE Id = {cid}");
        Assert.Equal(409, (await hugo.PostAsync($"/api/companion/actions/{action.Id()}/confirm", new { hash = action.Str("hash") })).Status);
        Assert.Equal("discarded", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE Id = {action.Id()}"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.CoGato WHERE Nombre = N'{nombre}'"));
    }

    [Fact]
    public async Task Regresion_049_F05_una_conversacion_sin_empresa_se_detiene_y_borra()
    {
        using var max = await host.LoginAsync("co-max", Pwd);
        var (cid, _) = await ProposeAsync(max, "f05b", CrearGato("Sin empresa " + Guid.NewGuid().ToString("N")[..6]));
        await host.ExecAsync($"UPDATE meta.CompanionConversation SET CompanyId = NULL WHERE Id = {cid}");
        Assert.Equal(200, (await max.PostAsync($"/api/companion/conversations/{cid}/cancel")).Status);
        Assert.Equal(204, (await max.DeleteAsync($"/api/companion/conversations/{cid}")).Status);
    }

    private static async Task<(int Main, int Other)> TwoCompaniesAsync(Client admin)
    {
        var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
        var main = companies.First(x => x["isDefault"]!.GetValue<bool>()).Id();
        var other = companies.FirstOrDefault(x => x.Str("code") == "CO46-C")?.Id()
                    ?? (await admin.PostAsync("/api/admin/companies", new { code = "CO46-C", name = "Empresa C 046", baseCurrency = "MXN" })).Body!.Id();
        return (main, other);
    }

    // ---------- O-02: reglas y condiciones sobre campos ocultos ----------

    [Fact]
    public async Task H049_H8_CA1_una_regla_o_condicion_sobre_un_campo_oculto_llega_sin_su_expresion()
    {
        using var olga = await host.LoginAsync("co-olga", Pwd);
        var res = await olga.GetAsync("/api/model/entities/CoEmpleado");
        Assert.Equal(200, res.Status);
        var rules = res.Body!.Arr("rules").Nodes().ToDictionary(r => r.Str("name")!);
        // Regresión (auditoría 9, O-02): la regla nombraba el campo oculto y su umbral.
        Assert.Null(rules["TopePago"]!["assert"]);
        Assert.Null(rules["TopePago"]!["field"]);
        Assert.Equal("El pago rebasa el tope autorizado.", rules["TopePago"]!.Str("message"));
        Assert.Equal("Nombre <> 'Prohibido'", rules["NombreValido"]!.Str("assert"));
        var baja = res.Body!["workflow"]!.Arr("transitions").Nodes().Single();
        Assert.Null(baja["when"]);
        Assert.Equal("Solo se da de baja con autorización.", baja.Str("whenMessage"));
        Assert.DoesNotContain("Sueldo", res.Text);
        Assert.DoesNotContain("90417", res.Text);
        Assert.DoesNotContain("50417", (await olga.GetAsync("/api/model/entities")).Text);
        // Quien modela (el administrador) recibe las expresiones.
        using var admin = await host.AdminAsync();
        var full = await admin.GetAsync("/api/model/entities/CoEmpleado");
        Assert.Contains("90417", full.Text);
        Assert.Contains("50417", full.Text);
        // Tampoco llegan al proveedor por «cómo funciona».
        string? leido = null;
        host.Llm.Scripts["o02"] = Script(_ => Reply.Tool("como_funciona", new { catalogo = "CoEmpleado" }),
            t => { leido = ResultOf(t); return Reply.Text("Listo."); });
        await WaitAsync(olga, (await StartAsync(olga, "o02", "¿Cómo funciona Empleados?")).Id());
        Assert.Contains("El pago rebasa el tope autorizado.", leido);
        Assert.DoesNotContain("90417", leido);
        Assert.DoesNotContain("50417", leido);
        Assert.DoesNotContain("90417", host.Llm.Sent("o02"));
    }

    // ---------- O-04: detener y borrar desde la empresa de la conversación ----------

    [Fact]
    public async Task H049_H6_CA1_detener_y_borrar_piden_la_empresa_de_la_conversacion()
    {
        using var admin = await host.AdminAsync();
        var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
        var main = companies.First(x => x["isDefault"]!.GetValue<bool>()).Id();
        var other = companies.FirstOrDefault(x => x.Str("code") == "CO46-C")?.Id()
                    ?? (await admin.PostAsync("/api/admin/companies", new { code = "CO46-C", name = "Empresa C 046", baseCurrency = "MXN" })).Body!.Id();
        var role = await admin.RoleIdAsync("Comp Usuario");
        var res = await admin.PostAsync("/api/admin/users", new { username = "co-o04", displayName = "co-o04", password = Pwd, roleIds = new[] { role }, companyIds = new[] { main, other } });
        Assert.True(res.Status == 201, res.Text);
        var userId = res.Body!.Id();
        using var o04 = await host.LoginAsync("co-o04", Pwd);
        Assert.Equal(200, (await o04.PostAsync("/api/auth/company", new { companyId = main })).Status);
        var (cid, action) = await ProposeAsync(o04, "o04", CrearGato("O04 " + Guid.NewGuid().ToString("N")[..6]));
        // Regresión (auditoría 9, O-04): desde otra empresa se detenía (y se descartaba lo pendiente) y se borraba.
        Assert.Equal(200, (await o04.PostAsync("/api/auth/company", new { companyId = other })).Status);
        var stop = await o04.PostAsync($"/api/companion/conversations/{cid}/cancel");
        Assert.Equal(409, stop.Status);
        Assert.Contains("otra empresa", stop.Text);
        Assert.Equal(409, (await o04.DeleteAsync($"/api/companion/conversations/{cid}")).Status);
        Assert.Equal("waiting", await StatusAsync(cid));
        Assert.Equal("proposed", await host.ScalarAsync<string>($"SELECT Status FROM meta.CompanionAction WHERE Id = {action.Id()}"));
        // Sacada de la empresa de la conversación, ni la encuentra.
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{userId}", new { displayName = "co-o04", isActive = true, roleIds = new[] { role }, companyIds = new[] { other } })).Status);
        using var again = await host.LoginAsync("co-o04", Pwd);
        Assert.Equal(404, (await again.PostAsync($"/api/companion/conversations/{cid}/cancel")).Status);
        Assert.Equal(404, (await again.DeleteAsync($"/api/companion/conversations/{cid}")).Status);
        // De vuelta en su empresa, sí la detiene.
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{userId}", new { displayName = "co-o04", isActive = true, roleIds = new[] { role }, companyIds = new[] { main, other } })).Status);
        using var back = await host.LoginAsync("co-o04", Pwd);
        Assert.Equal(200, (await back.PostAsync("/api/auth/company", new { companyId = main })).Status);
        Assert.Equal(200, (await back.PostAsync($"/api/companion/conversations/{cid}/cancel")).Status);
        Assert.Equal(204, (await back.DeleteAsync($"/api/companion/conversations/{cid}")).Status);
    }
}
