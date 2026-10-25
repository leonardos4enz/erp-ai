using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 012 · Endurecimiento (antes en <c>web/e2e/endurecimiento.spec.ts</c>, movida en la 030). Cada prueba es un hallazgo
/// de la auditoría de solo lectura (2026-10-23). H3 (sobre-recepción, 012-3) falla con el código anterior a la 012
/// (<c>cd81760</c>): <c>npm --prefix web run it:regresion -- cd81760 Hardening012Tests.H3</c>.
/// </summary>
public class Hardening012Tests(Hardening012Tests.Host host) : IClassFixture<Hardening012Tests.Host>
{
    public const string Pwd = "frase larga de endurecimiento 2026";
    public const string Secret = "S3CRETO-012-precio";
    private const string Group = "Pruebas 012";

    public sealed class Host : ErpHost
    {
        public int MainCompany, OtherCompany;
        public readonly Dictionary<string, JsonNode> Rec = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
            MainCompany = companies.First(c => c["isDefault"]!.GetValue<bool>()).Id();
            OtherCompany = (await admin.PostAsync("/api/admin/companies", new { code = "E2E-EH2", name = "Otra empresa 012", baseCurrency = "MXN" })).Body!.Id();

            foreach (var name in new[] { "Eh Editor", "Eh Auditor" })
            {
                var res = await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 012", canManageModel = false, canManageUsers = false,
                    canViewAudit = name == "Eh Auditor", permissions = Array.Empty<object>() });
                Assert.True(res.Status == 200, res.Text);
            }
            await admin.ApplyPlanAsync(new { description = "Spec 012 · catálogos", operations = new object[] {
                new { op = "createEntity", group = Group, name = "EhTarea", label = "Tarea 012", labelPlural = "Tareas 012", displayField = "Titulo", perCompany = true, fields = new object[] {
                    new { name = "Titulo", label = "Título", type = "text", required = true },
                    new { name = "Publica", label = "Pública", type = "boolean", defaultValue = "FALSE" },
                    new { name = "Secreto", label = "Secreto", type = "text" },
                    new { name = "Motivo", label = "Motivo", type = "text" } } },
                new { op = "setWorkflow", entity = "EhTarea", workflow = new {
                    initial = "Abierta",
                    states = new object[] { new { name = "Abierta", label = "Abierta" }, new { name = "Revisada", label = "Revisada" }, new { name = "Cerrada", label = "Cerrada", editable = false } },
                    transitions = new object[] {
                        new { name = "cerrar", label = "Cerrar", from = new[] { "Abierta", "Revisada" }, to = "Cerrada", requiredFields = new[] { "Motivo" } },
                        new { name = "revisar", label = "Revisar", from = new[] { "Abierta" }, to = "Revisada", roles = new[] { "Eh Editor" } } } } },
                new { op = "setSecurity", entity = "EhTarea", security = new {
                    fields = new object[] { new { field = "Secreto", read = new[] { "Eh Editor" } } },
                    records = new object[] {
                        new { roles = new[] { "Eh Editor" }, scope = "own" },
                        new { roles = new[] { "Eh Editor" }, access = "read" },
                        new { roles = new[] { "Eh Auditor" }, where = "Publica", access = "read" } } } },
                new { op = "createEntity", group = Group, name = "EhPrivado", label = "Privado 012", labelPlural = "Privados 012", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "EhCliente", label = "Cliente 012", labelPlural = "Clientes 012", displayField = "Nombre", perCompany = true, fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "EhPedido", label = "Pedido 012", labelPlural = "Pedidos 012", displayField = "Clave", fields = new object[] {
                    new { name = "Clave", label = "Clave", type = "text", required = true },
                    new { name = "Cliente", label = "Cliente", type = "reference", referenceEntity = "EhCliente" },
                    new { name = "Tarea", label = "Tarea", type = "reference", referenceEntity = "EhTarea" } } },
            } });

            static object Perm(string entity, bool write) => new { entity, canRead = true, canCreate = write, canUpdate = write, canDelete = false };
            var roles = (await admin.GetAsync("/api/admin/roles")).Array.Nodes().ToList();
            foreach (var (name, permissions) in new[] {
                ("Eh Editor", new[] { Perm("EhTarea", true), Perm("EhPedido", true), Perm("EhCliente", true) }),
                ("Eh Auditor", new[] { Perm("EhTarea", false), Perm("EhPedido", false) }) })
            {
                var r = roles.First(x => x.Str("name") == name);
                var res = await admin.PutAsync($"/api/admin/roles/{r.Id()}", new { name, description = r.Str("description"), canManageModel = false,
                    canManageUsers = false, canViewAudit = name == "Eh Auditor", permissions });
                Assert.True(res.Status == 200, res.Text);
            }
            foreach (var (user, role) in new[] { ("e2e-eh-ana", "Eh Editor"), ("e2e-eh-beto", "Eh Editor"), ("e2e-eh-auditor", "Eh Auditor") })
            {
                var res = await admin.PostAsync("/api/admin/users", new { username = user, displayName = user, password = Pwd,
                    roleIds = new[] { await admin.RoleIdAsync(role) }, companyIds = new[] { MainCompany } });
                Assert.True(res.Status == 201, res.Text);
            }

            using var ana = await LoginAsync("e2e-eh-ana", Pwd);
            Rec["publica"] = await ana.CreateAsync("EhTarea", new { Titulo = "Tarea pública 012", Publica = true, Secreto = Secret });
            Rec["privada"] = await ana.CreateAsync("EhTarea", new { Titulo = "Tarea privada 012", Publica = false, Secreto = Secret });
            Rec["nada"] = await admin.CreateAsync("EhPrivado", new { Nombre = "Registro sin permiso 012" });
            await SwitchAsync(admin, OtherCompany);
            Rec["otra"] = await admin.CreateAsync("EhTarea", new { Titulo = "Tarea de otra empresa 012", Publica = true });
            Rec["clienteOtra"] = await admin.CreateAsync("EhCliente", new { Nombre = "Cliente de otra empresa 012" });
            await SwitchAsync(admin, MainCompany);
            Rec["clienteMain"] = await admin.CreateAsync("EhCliente", new { Nombre = "Cliente principal 012" });

            await PrepareReceptionsAsync(admin);
        }

        /// <summary>H3: orden con partidas, recepción con regla «no exceder lo pendiente» y acumulado al confirmar.</summary>
        private static Task PrepareReceptionsAsync(Client admin) => admin.ApplyPlanAsync(new { description = "Spec 012 · recepciones", operations = new object[] {
            new { op = "createEntity", group = Group, name = "EhOrden", label = "Orden 012", labelPlural = "Órdenes 012", displayField = "Clave", fields = new object[] {
                new { name = "Clave", label = "Clave", type = "text", required = true } } },
            new { op = "createEntity", name = "EhOrdenPartida", label = "Partida 012", labelPlural = "Partidas 012", displayField = "Concepto", fields = new object[] {
                new { name = "Orden", label = "Orden", type = "parent", referenceEntity = "EhOrden" },
                new { name = "Concepto", label = "Concepto", type = "text", required = true },
                new { name = "Cantidad", label = "Cantidad", type = "decimal", required = true },
                new { name = "Recibida", label = "Recibida", type = "decimal", editable = false, defaultValue = "0" },
                new { name = "Pendiente", label = "Pendiente", type = "formula", resultType = "decimal", formula = "Cantidad - Recibida" } } },
            new { op = "addField", entity = "EhOrden", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "EhOrdenPartida" } },
            new { op = "setWorkflow", entity = "EhOrden", workflow = new {
                initial = "Abierta", states = new object[] { new { name = "Abierta", label = "Abierta" }, new { name = "Cancelada", label = "Cancelada", editable = false } },
                transitions = new object[] { new { name = "cancelar", label = "Cancelar", from = new[] { "Abierta" }, to = "Cancelada" } } } },
            new { op = "createEntity", group = Group, name = "EhRecepcion", label = "Recepción 012", labelPlural = "Recepciones 012", fields = new object[] {
                new { name = "Orden", label = "Orden", type = "reference", referenceEntity = "EhOrden", required = true } } },
            new { op = "createEntity", name = "EhRecepcionPartida", label = "Partida de recepción 012", labelPlural = "Partidas de recepción 012", fields = new object[] {
                new { name = "Recepcion", label = "Recepción", type = "parent", referenceEntity = "EhRecepcion" },
                new { name = "PartidaOrden", label = "Partida de la orden", type = "reference", referenceEntity = "EhOrdenPartida", required = true },
                new { name = "Cantidad", label = "Cantidad", type = "decimal", required = true } } },
            new { op = "addField", entity = "EhRecepcion", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "EhRecepcionPartida" } },
            new { op = "setRules", entity = "EhRecepcionPartida", rules = new object[] {
                new { name = "NoExceder", field = "Cantidad", assert = "Cantidad <= PartidaOrden.Pendiente", message = "Excede lo pendiente de la orden." } } },
            new { op = "setWorkflow", entity = "EhRecepcion", workflow = new {
                initial = "Borrador", states = new object[] { new { name = "Borrador", label = "Borrador" }, new { name = "Confirmada", label = "Confirmada", editable = false } },
                transitions = new object[] { new { name = "confirmar", label = "Confirmar", from = new[] { "Borrador" }, to = "Confirmada", when = "Orden.Estado = 'Abierta'",
                    whenMessage = "La orden ya no está abierta." } } } },
            new { op = "setAutomations", entity = "EhRecepcion", automations = new object[] {
                new { name = "Acumular", label = "Acumular lo recibido", on = "enterState", state = "Confirmada", actions = new object[] {
                    new { type = "rollup", lines = "Partidas", target = "PartidaOrden", field = "Recibida", sum = "Cantidad", filter = "Recepcion.Estado = 'Confirmada'" } } } } },
        } });

    }

    internal static async Task SwitchAsync(Client api, int companyId) =>
        Assert.Equal(200, (await api.PostAsync("/api/auth/company", new { companyId })).Status);

    private Task<Client> As(string who) => who == "admin" ? host.AdminAsync() : host.LoginAsync(who, Pwd);

    [Fact]
    public async Task H1_CA1_CA2_CA3_la_bitacora_respeta_catalogo_empresa_alcance_por_registro_y_campos_ocultos()
    {
        using var aud = await As("e2e-eh-auditor");
        async Task<JsonNode> Audit(string? entity = null) => (await aud.GetAsync("/api/audit", new { pageSize = 200, entity })).Body!;

        var tareas = await Audit("EhTarea");
        var items = tareas.Arr("items").Nodes().ToList();
        var ids = items.Select(i => i.Str("recordId")).ToList();
        Assert.Contains(host.Rec["publica"].Id().ToString(), ids);
        // Regresión (auditoría 012, hallazgo 1): antes llegaban la tarea no pública, la de otra empresa y el campo oculto.
        Assert.DoesNotContain(host.Rec["privada"].Id().ToString(), ids);
        Assert.DoesNotContain(host.Rec["otra"].Id().ToString(), ids);
        Assert.DoesNotContain(Secret, tareas.Json());
        Assert.Equal(items.Count, tareas["total"]!.GetValue<int>());
        var created = items.First(i => i.Str("action") == "create" && i.Str("recordId") == host.Rec["publica"].Id().ToString());
        Assert.Equal("Tarea pública 012", created["after"]!.Str("Titulo"));
        Assert.Null(created["after"]!["Secreto"]);

        Assert.Equal(0, (await Audit("EhPrivado"))["total"]!.GetValue<int>());
        var all = await Audit();
        Assert.DoesNotContain("Registro sin permiso 012", all.Json());
        Assert.DoesNotContain(Secret, all.Json());
        Assert.Contains(all.Arr("items").Nodes(), i => i.Str("action") == "login");

        // El superusuario ve todo, como antes (H1 CA4).
        using var admin = await As("admin");
        var full = (await admin.GetAsync("/api/audit", new { entity = "EhTarea", pageSize = 200 })).Body!;
        Assert.Contains(Secret, full.Json());
        var fullIds = full.Arr("items").Nodes().Select(i => i.Str("recordId")).ToList();
        Assert.Contains(host.Rec["privada"].Id().ToString(), fullIds);
        Assert.Contains(host.Rec["otra"].Id().ToString(), fullIds);
    }

    [Fact]
    public async Task H2_CA1_CA2_una_transicion_sin_roles_exige_poder_modificar_ese_registro_con_roles_basta_verlo()
    {
        using var ana = await As("e2e-eh-ana");
        using var beto = await As("e2e-eh-beto");
        var tarea = await ana.CreateAsync("EhTarea", new { Titulo = "Tarea de Ana 012" });
        var seen = await beto.RecordAsync("EhTarea", tarea.Id());
        Assert.Equal("Tarea de Ana 012", seen.Str("Titulo"));
        var cerrar = seen["_workflow"]!.Arr("transitions").Nodes().First(t => t.Str("name") == "cerrar");
        Assert.False(cerrar["allowed"]!.GetValue<bool>());
        Assert.Contains("no puede modificarlo", cerrar.Str("reason"));

        // Regresión (auditoría 012, hallazgo 2): Beto solo edita sus tareas y aun así cerraba la de Ana capturando «Motivo».
        var denied = await beto.TransitionAsync("EhTarea", seen, "cerrar", new { Motivo = "cerrada por otro" });
        Assert.Equal(403, denied.Status);
        Assert.Equal("Abierta", (await ana.RecordAsync("EhTarea", tarea.Id())).Str("Estado"));

        var revisada = await beto.TransitionAsync("EhTarea", seen, "revisar");
        Assert.True(revisada.Status == 200, revisada.Text);
        Assert.Equal("Revisada", revisada.Body!.Str("Estado"));
        var propia = await ana.TransitionAsync("EhTarea", revisada.Body!, "cerrar", new { Motivo = "listo" });
        Assert.True(propia.Status == 200, propia.Text);
    }

    [Fact]
    public async Task H6_CA1_CA2_la_etiqueta_de_una_referencia_fuera_de_alcance_no_revela_el_nombre()
    {
        using var admin = await As("admin");
        await SwitchAsync(admin, host.OtherCompany);
        var pedido = await admin.CreateAsync("EhPedido", new { Clave = "PED-012", Cliente = host.Rec["clienteOtra"].Id() });
        Assert.Equal("Cliente de otra empresa 012", pedido.Str("Cliente__label"));
        await SwitchAsync(admin, host.MainCompany);
        var privado = await admin.CreateAsync("EhPedido", new { Clave = "PED-012-B", Tarea = host.Rec["privada"].Id() });
        var publico = await admin.CreateAsync("EhPedido", new { Clave = "PED-012-C", Tarea = host.Rec["publica"].Id() });

        // Regresión (auditoría 012, hallazgo 6): desde la empresa principal se leía el nombre del cliente de otra empresa.
        var fromMain = await admin.RecordAsync("EhPedido", pedido.Id());
        Assert.Equal(host.Rec["clienteOtra"].Id(), fromMain["Cliente"]!.GetValue<int>());
        Assert.Equal("(sin acceso)", fromMain.Str("Cliente__label"));
        var listed = (await admin.GetAsync("/api/data/EhPedido", new { pageSize = 200 })).Items.Nodes().First(r => r.Id() == pedido.Id());
        Assert.Equal("(sin acceso)", listed.Str("Cliente__label"));
        var computedRes = await admin.PostAsync("/api/data/EhPedido/compute", new { values = new { Clave = "X", Cliente = host.Rec["clienteOtra"].Id() } });
        Assert.True(computedRes.Status == 200, computedRes.Text);
        var computed = computedRes.Body!;
        Assert.DoesNotContain("Cliente de otra empresa 012", computed["values"]!.Json());
        var kept = (await admin.PostAsync("/api/data/EhPedido/compute", new { id = pedido.Id(), values = new { Clave = "X" } })).Body!;
        Assert.Equal("(sin acceso)", kept["values"]!.Str("Cliente__label"));

        using var aud = await As("e2e-eh-auditor");
        var audView = (await aud.GetAsync("/api/data/EhPedido", new { pageSize = 200 })).Items.Nodes().ToList();
        Assert.Equal("(sin acceso)", audView.First(r => r.Id() == privado.Id()).Str("Tarea__label"));
        Assert.Equal("Tarea pública 012", audView.First(r => r.Id() == publico.Id()).Str("Tarea__label"));
        Assert.Equal("(sin acceso)", audView.First(r => r.Id() == pedido.Id()).Str("Cliente__label"));

        // CA2: el buscador ya filtraba (empresa y alcance); se deja como prueba.
        var lookup = (await aud.GetAsync("/api/data/EhTarea/lookup")).Array.Nodes().Select(l => l.Str("label")).ToList();
        Assert.Equal(["Tarea pública 012"], lookup);
        var clientes = (await admin.GetAsync("/api/data/EhCliente/lookup")).Array.Nodes().Select(l => l.Str("label")).ToList();
        Assert.Equal(["Cliente principal 012"], clientes);
    }

    [Fact]
    public async Task H7_CA2_no_se_cambia_la_moneda_base_de_una_empresa_con_registros_por_empresa()
    {
        using var admin = await As("admin");
        Task<Res> Put(string baseCurrency) => admin.PutAsync($"/api/admin/companies/{host.OtherCompany}", new { name = "Otra empresa 012", baseCurrency, isActive = true });
        // Regresión (auditoría 012, menor): con tareas y clientes registrados en MXN se podía pasar a USD.
        var res = await Put("USD");
        Assert.Equal(409, res.Status);
        Assert.Contains("moneda base", res.Text);
        Assert.Equal(200, (await Put("MXN")).Status);
    }

    // ---------------- H3: concurrencia (012-3) ----------------

    private static async Task<(JsonNode orden, List<JsonNode> recs)> OrderWithReceptionAsync(Client admin, decimal qty, params decimal[] receptions)
    {
        var orden = await admin.CreateAsync("EhOrden", new { Clave = $"OC-{Guid.NewGuid():N}"[..12], Partidas = new[] { new { Concepto = "Tornillos", Cantidad = qty } } });
        var partida = orden.Arr("Partidas").Nodes().First();
        var recs = new List<JsonNode>();
        foreach (var q in receptions)
            recs.Add(await admin.CreateAsync("EhRecepcion", new { Orden = orden.Id(), Partidas = new[] { new { PartidaOrden = partida.Id(), Cantidad = q } } }));
        return (orden, recs);
    }

    [Fact]
    public async Task H3_CA1_confirmaciones_simultaneas_contra_la_misma_partida_no_reciben_mas_de_lo_pedido()
    {
        using var admin = await As("admin");
        var (orden, recs) = await OrderWithReceptionAsync(admin, 10, Enumerable.Repeat(10m, 10).ToArray());
        // Regresión (auditoría 012, hallazgo 3): todas validaban «pendiente 10» antes de que la primera bloqueara la orden.
        var results = await Concurrently(recs.Count, i => admin.TransitionAsync("EhRecepcion", recs[i], "confirmar"));
        var partida = (await admin.RecordAsync("EhOrden", orden.Id())).Arr("Partidas").Nodes().First();
        Invariant.AtMost(partida.Dec("Recibida") ?? 0, partida.Dec("Cantidad") ?? 0, "recibido de la partida");
        Invariant.Statuses(results, [200, .. Enumerable.Repeat(400, 9)]);
        Assert.Equal(10m, partida.Dec("Recibida"));
        Assert.Equal(0m, partida.Dec("Pendiente"));
    }

    [Fact]
    public async Task H3_CA3_cancelar_la_orden_mientras_se_confirma_una_recepcion_gana_una_sola()
    {
        using var admin = await As("admin");
        for (var round = 0; round < 6; round++)
        {
            var (orden, recs) = await OrderWithReceptionAsync(admin, 5, 5);
            var both = await Concurrently(2, i => i == 0 ? admin.TransitionAsync("EhRecepcion", recs[0], "confirmar") : admin.TransitionAsync("EhOrden", orden, "cancelar"));
            // Regresión (auditoría 012, hallazgo 3): la recepción se confirmaba y acumulaba sobre una orden recién cancelada.
            Assert.True(both.Count(r => r.Status == 200) == 1, $"ronda {round}: {both[0]} / {both[1]}");
            var final = await admin.RecordAsync("EhOrden", orden.Id());
            var recibida = final.Arr("Partidas").Nodes().First().Dec("Recibida");
            Assert.Equal(final.Str("Estado") == "Cancelada" ? 0m : 5m, recibida);
        }
    }

    [Fact]
    public async Task H3_CA2_H7_CA1_Compras_protege_lo_recibido_y_guarda_la_moneda_del_costo_ultimo()
    {
        using var admin = await As("admin");
        await admin.InstallModuleAsync("compras");
        var partida = (await admin.GetAsync("/api/model/entities/OrdenCompraPartida")).Body!;
        Assert.Equal(["CantidadPositiva", "PrecioNoNegativo", "DescuentoRango", "IvaRango", "RecibidoNoExcede"],
            partida.Arr("rules").Nodes().Select(r => r.Str("name")).ToList());
        var producto = (await admin.GetAsync("/api/model/entities/Producto")).Body!;
        Assert.Contains("MonedaCosto", producto.Arr("fields").Nodes().Select(f => f.Str("name")));
        var recepcion = (await admin.GetAsync("/api/model/entities/Recepcion")).Body!;
        var acumular = recepcion.Arr("automations").Nodes().First(a => a.Str("name") == "AcumularRecibido");
        Assert.Contains("BASECURRENCY()", acumular.Json());
    }
    // ---------------- H4: segunda instancia «producción» sin AllowDirectChanges ----------------

    private Task<ErpHost.Factory> ProdAsync() => host.SiblingAsync(s =>
    {
        s["Environments:AllowDirectChanges"] = "false";
        s["Environments:Name"] = "Producción IT (sin atajos)";
        s.Remove("Security:TokenRequestsPerMinute"); // el límite por defecto del servidor de tokens (30 por minuto)
    });

    [Fact]
    public async Task H4_CA1_CA2_con_aprobacion_el_modelo_no_cambia_por_plan_modulo_ni_asistente()
    {
        var prod = await ProdAsync();
        using var admin = await prod.LoginAsync("admin", host.AdminPassword);
        var plan = new { description = "Atajo", operations = new[] { new { op = "addField", entity = "EhPedido", field = new { name = "Atajo", label = "Atajo", type = "text" } } } };
        Assert.Equal(200, (await admin.PostAsync("/api/model/plan/preview", plan)).Status);
        // Regresión (auditoría 012, hallazgo 4): cada uno de estos caminos aplicaba sin la aprobación de otro administrador.
        foreach (var (url, body) in new (string, object?)[] {
            ("/api/model/plan/apply", plan),
            ("/api/admin/modules/compras/upgrade", null),
            ("/api/admin/modules/inventario/install", null),
            ("/api/assistant/requests/999999/apply", new { reviewHash = "x" }) })
        {
            var res = await admin.PostAsync(url, body);
            Assert.True(res.Status == 409, $"{url}: {res}");
            Assert.Contains("Entornos", res.Text);
        }
        Assert.DoesNotContain("Atajo", (await admin.GetAsync("/api/model/entities/EhPedido"))["fields"].Json());
        Assert.False((await admin.GetAsync("/api/assistant"))["directChanges"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Spec018_H2_CA5_el_servidor_de_tokens_limita_las_peticiones_por_direccion()
    {
        var prod = await ProdAsync();
        using var anon = prod.Anonymous(csrfHeader: false);
        var statuses = new List<int>();
        for (var i = 0; i < 32; i++)
            statuses.Add((await anon.PostFormAsync("/oauth/token", new Dictionary<string, string> {
                ["grant_type"] = "client_credentials", ["client_id"] = "erpcl_00000000000000", ["client_secret"] = "erpcs_" + new string('a', 43) })).Status);
        Assert.All(statuses.Take(30), s => Assert.Equal(401, s));
        Assert.Equal([429, 429], statuses.Skip(30));
    }
}
