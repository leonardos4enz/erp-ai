using System.Text;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;
using static Erp.IntegrationTests.Harness.Invariant;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 053 · matriz de autorización sobre SQL real: elegir regla y congelarla, niveles saltados por rango o vacíos, falla cerrado,
/// segregación (creador, dos niveles, superusuario, propia pertenencia), personas vivas, reinicio por cambio, cancelar al salir,
/// todos los caminos (transiciones de salida, plan, formulario, importación), visibilidad por la 051, huella y versiones, diez a la
/// vez, falla parcial y la compuerta de seguridad.
/// </summary>
public class ApprovalTests(ApprovalTests.Host host) : IClassFixture<ApprovalTests.Host>
{
    private const string Pwd = "frase larga de autorización 053";
    private const string E = "AzSolicitud";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];
        public ErpHost.Factory Sec = null!;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            // Los roles existen antes del plan (lo nombran la seguridad y las transiciones); sus permisos, después.
            foreach (var name in new[] { "Az Solicitante", "Az Autorizador" })
                Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 053", canManageModel = false,
                    canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            await admin.ApplyPlanAsync(Plan());
            async Task<int> Role(string name, bool users, params object[] permissions)
            {
                var body = new { name, description = "Spec 053", canManageModel = false, canManageUsers = users, canViewAudit = false, permissions };
                var existing = (await admin.GetAsync("/api/admin/roles")).Array.Nodes().FirstOrDefault(r => r.Str("name") == name);
                var res = existing is null ? await admin.PostAsync("/api/admin/roles", body) : await admin.PutAsync($"/api/admin/roles/{existing.Id()}", body);
                Assert.True(res.Status is 200 or 201, res.Text);
                return await admin.RoleIdAsync(name);
            }
            var solicitante = await Role("Az Solicitante", false,
                new { entity = E, canRead = true, canCreate = true, canUpdate = true, canDelete = false },
                new { entity = "AzDepto", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                new { entity = "AzAjuste", canRead = true, canCreate = true, canUpdate = false, canDelete = false });
            var autorizador = await Role("Az Autorizador", false,
                new { entity = E, canRead = true, canCreate = true, canUpdate = true, canDelete = false },
                new { entity = "AzDepto", canRead = true, canCreate = false, canUpdate = false, canDelete = false });
            Ids["rolAut"] = autorizador;
            var ciego = await Role("Az Ciego", false);
            var ua = await Role("Az Usuarios", true);
            var users = new List<(string Key, int Role)> { ("sol", solicitante), ("a1", autorizador), ("a2", autorizador), ("a3", autorizador),
                ("ciego", ciego), ("ua", ua), ("ub", ua) };
            users.AddRange(Enumerable.Range(1, 10).Select(i => ($"m{i:00}", autorizador)));
            foreach (var (key, role) in users)
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = $"it53-{key}", displayName = $"Persona {key}", password = Pwd, roleIds = new[] { role } });
                Assert.True(u.Status == 201, u.Text);
                Ids[key] = u.Body!.Id();
            }
            foreach (var d in new[] { "general", "ti", "vacio", "solo", "masivo", "ciego", "vivo", "carrera", "congelada", "unico", "starter" })
                Ids[d] = (await admin.CreateAsync("AzDepto", new { Nombre = d })).Id();

            async Task<int> Profile(string name, decimal? min, params string[] members)
            {
                var res = await admin.PostAsync("/api/admin/approvals/profiles", new { name, minAmount = min, memberIds = members.Select(m => Ids[m]).ToArray() });
                Assert.True(res.Status == 201, res.Text);
                return res.Body!.Id();
            }
            Ids["pJefes"] = await Profile("Jefes", null, "a1", "a2");
            Ids["pGerentes"] = await Profile("Gerentes", null, "a2", "a3");
            Ids["pDirectores"] = await Profile("Directores", 100_000m, "a3");
            Ids["pVacio"] = await Profile("Vacío", null);
            Ids["pSoloA1"] = await Profile("Solo A1", null, "a1");
            Ids["pMasivo"] = await Profile("Masivo", null, Enumerable.Range(1, 10).Select(i => $"m{i:00}").ToArray());
            Ids["pCiegos"] = await Profile("Ciegos", null, "ciego");
            Ids["pVivos"] = await Profile("Vivos", null, "a1");
            Ids["pCarrera"] = await Profile("Carrera", null, "a1");
            Ids["pUnico"] = await Profile("Único", null, "a3");

            async Task<int> Rule(string name, int priority, object conditions, decimal? max, params string[] levels)
            {
                var res = await admin.PostAsync("/api/admin/approvals/rules", new { entity = E, name, priority, conditions, maxAmount = max,
                    levels = levels.Select(l => Ids[l]).ToArray() });
                Assert.True(res.Status == 201, res.Text);
                return res.Body!.Id();
            }
            Ids["rGeneral"] = await Rule("General", 5, new { }, 1_000_000m, "pJefes", "pVacio", "pGerentes", "pDirectores");
            Ids["rTi"] = await Rule("Servicios TI", 1, new { Departamento = new[] { Ids["ti"] }, Tipo = new[] { "Servicios" } }, null, "pGerentes");
            Ids["rVacio"] = await Rule("Solo vacío", 2, new { Departamento = new[] { Ids["vacio"] } }, null, "pVacio");
            Ids["rSolo"] = await Rule("Solo A1", 2, new { Departamento = new[] { Ids["solo"] } }, null, "pSoloA1");
            Ids["rMasivo"] = await Rule("Masivo", 2, new { Departamento = new[] { Ids["masivo"] } }, null, "pMasivo", "pGerentes");
            Ids["rCiego"] = await Rule("Ciega", 2, new { Departamento = new[] { Ids["ciego"] } }, null, "pCiegos", "pGerentes");
            Ids["rVivo"] = await Rule("Viva", 2, new { Departamento = new[] { Ids["vivo"] } }, null, "pVivos", "pGerentes");
            Ids["rCarrera"] = await Rule("Carrera", 2, new { Departamento = new[] { Ids["carrera"] } }, null, "pCarrera", "pGerentes");
            Ids["rUnico"] = await Rule("Única", 2, new { Departamento = new[] { Ids["unico"] } }, null, "pJefes", "pUnico");
            Ids["rCongelada"] = await Rule("Congelada", 2, new { Departamento = new[] { Ids["congelada"] } }, null, "pJefes");

            Sec = await SiblingAsync(s =>
            {
                s["Environments:Name"] = "Producción IT 053";
                s["Environments:RequireSecurityApproval"] = "true";
                s["Automations:PollSeconds"] = "300";
            });
        }

        private static object Plan() => new
        {
            description = "IT 053: matriz de autorización",
            operations = new object[]
            {
                new { op = "createEntity", name = "AzDepto", label = "Depto 053", labelPlural = "Deptos 053", displayField = "Nombre",
                    fields = new object[] { new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", name = E, label = "Solicitud 053", labelPlural = "Solicitudes 053", displayField = "Folio", perCompany = true,
                    fields = new object[]
                    {
                        new { name = "Folio", label = "Folio", type = "autonumber", prefix = "AZ-", padding = 4 },
                        new { name = "Concepto", label = "Concepto", type = "text" },
                        new { name = "Departamento", label = "Departamento", type = "reference", referenceEntity = "AzDepto", required = true },
                        new { name = "Tipo", label = "Tipo", type = "choice", options = new[] { "Bienes", "Servicios" } },
                        new { name = "Monto", label = "Monto", type = "decimal", precision = 18, scale = 2 },
                        new { name = "Nota", label = "Nota", type = "text" },
                        new { name = "Autorizadores", label = "Autorizadores", type = "multiuser", editable = false },
                    } },
                new { op = "setWorkflow", entity = E, workflow = new
                {
                    initial = "Borrador",
                    states = new object[]
                    {
                        new { name = "Borrador", label = "Borrador", editable = true },
                        new { name = "PorAutorizar", label = "Por autorizar", editable = false },
                        new { name = "Autorizada", label = "Autorizada", editable = false },
                        new { name = "Rechazada", label = "Rechazada", editable = false },
                    },
                    transitions = new object[]
                    {
                        new { name = "enviar", label = "Enviar", from = new[] { "Borrador" }, to = "PorAutorizar" },
                        new { name = "autorizada", label = "Autorizada", from = new[] { "PorAutorizar" }, to = "Autorizada", system = true },
                        new { name = "rechazada", label = "Rechazada", from = new[] { "PorAutorizar" }, to = "Rechazada", system = true },
                        new { name = "devolver", label = "Devolver", from = new[] { "PorAutorizar" }, to = "Borrador", roles = new[] { "Az Solicitante", "Az Autorizador" } },
                        new { name = "comentar", label = "Comentar", from = new[] { "PorAutorizar" }, to = "PorAutorizar", roles = new[] { "Az Solicitante" },
                            set = new[] { new { field = "Nota", value = "'comentada'" } } },
                    },
                    approval = new { state = "PorAutorizar", approve = "autorizada", reject = "rechazada", amount = "Monto",
                        conditions = new[] { "Departamento", "Tipo" }, approvers = "Autorizadores" },
                } },
                new { op = "setSecurity", entity = E, security = new
                {
                    records = new object[]
                    {
                        new { roles = new[] { "Az Solicitante", "Az Autorizador" }, scope = "own" },
                        new { roles = new[] { "Az Autorizador" }, where = "CURRENTUSER() IN Autorizadores", access = "read" },
                    },
                } },
                new { op = "createEntity", name = "AzBitacora", label = "Bitácora 053", labelPlural = "Bitácoras 053",
                    fields = new object[]
                    {
                        new { name = "Solicitud", label = "Solicitud", type = "reference", referenceEntity = E },
                        new { name = "Nota", label = "Nota", type = "text" },
                    } },
                new { op = "setAutomations", entity = E, automations = new object[]
                {
                    new { name = "Registrar", label = "Registrar la autorización", on = "transition", transition = "autorizada", actions = new object[]
                    {
                        new { type = "create", entity = "AzBitacora", set = new[] { new { field = "Nota", value = "Folio" } } },
                    } },
                } },
                new { op = "createEntity", name = "AzAjuste", label = "Ajuste 053", labelPlural = "Ajustes 053",
                    fields = new object[]
                    {
                        new { name = "Solicitud", label = "Solicitud", type = "reference", referenceEntity = E, required = true },
                        new { name = "Nuevo", label = "Nuevo monto", type = "decimal", precision = 18, scale = 2 },
                    } },
                new { op = "setAutomations", entity = "AzAjuste", automations = new object[]
                {
                    new { name = "Ajustar", label = "Ajustar el monto", on = "create", actions = new object[]
                    {
                        new { type = "update", target = "Solicitud", set = new[] { new { field = "Monto", value = "Nuevo" } } },
                    } },
                } },
            },
        };
    }

    private int Id(string key) => host.Ids[key];
    private Task<Client> As(string who) => who == "admin" ? host.AdminAsync() : host.LoginAsync($"it53-{who}", Pwd);

    /// <summary>Crea la solicitud como <paramref name="who"/> y la manda a autorizar (exige 200).</summary>
    private async Task<int> SentAsync(string who, string depto, string tipo = "Bienes", decimal monto = 500m)
    {
        using var api = await As(who);
        var row = await api.CreateAsync(E, new { Concepto = $"{depto} {tipo} {monto}", Departamento = Id(depto), Tipo = tipo, Monto = monto });
        var sent = await api.TransitionAsync(E, row, "enviar");
        Assert.True(sent.Status == 200, sent.Text);
        return row.Id();
    }

    private async Task<JsonNode> SeenByAdminAsync(int id)
    {
        using var admin = await As("admin");
        return await admin.RecordAsync(E, id);
    }

    private static async Task<Res> DecideAsync(Client api, int id, JsonNode seen, string decision = "approve", string? comment = null) =>
        await api.PostAsync($"/api/data/{E}/{id}/approval", new { decision, comment, _rowVersion = seen.Str("_rowVersion"),
            approvalVersion = seen["_approval"]?.Str("version") });

    /// <summary>Decide con lo que ve la propia persona (o, si no lo ve, con lo que ve el administrador).</summary>
    private async Task<Res> DecideAsAsync(string who, int id, string decision = "approve", string? comment = null)
    {
        using var api = await As(who);
        var mine = await api.GetAsync($"/api/data/{E}/{id}");
        return await DecideAsync(api, id, mine.Status == 200 ? mine.Body! : await SeenByAdminAsync(id), decision, comment);
    }

    private async Task<string> StateAsync(int id) => (await host.ScalarAsync<string>($"SELECT Estado FROM app.{E} WHERE Id = {id}"))!;

    private async Task<int[]> ApproversAsync(int id) =>
        ((await host.ScalarAsync<string>($"SELECT STRING_AGG(CAST(ValueId AS varchar(20)), ',') FROM app.[{E}__Autorizadores] WHERE OwnerId = {id}")) ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToArray();

    private int[] People(params string[] keys) => keys.Select(Id).Order().ToArray();

    // ---------------- H4: elegir, saltar, falla cerrado ----------------

    [Fact]
    public async Task Flujo_de_niveles_salta_el_vacio_al_llegar_y_el_de_rango_al_empezar()
    {
        var id = await SentAsync("sol", "general");
        Assert.Equal(People("a1", "a2"), await ApproversAsync(id));
        var first = await SeenByAdminAsync(id);
        Assert.Equal("General", first["_approval"]!.Str("rule"));
        Assert.Equal(1, first["_approval"]!["level"]!.GetValue<int>());

        Assert.Equal(200, (await DecideAsAsync("a1", id, comment: "Visto bueno")).Status);
        Assert.Equal(People("a2", "a3"), await ApproversAsync(id));
        var res = await DecideAsAsync("a2", id);
        Assert.True(res.Status == 200, res.Text);
        Assert.Equal("Autorizada", await StateAsync(id));
        Assert.Empty(await ApproversAsync(id));
        // F-13: a2 ya no lo ve: la respuesta solo dice que se guardó (sin campos).
        Assert.True(res.Body!["_gone"]!.GetValue<bool>(), res.Text);
        Assert.Null(res.Body!["Concepto"]);
        var seen = await SeenByAdminAsync(id);
        var levels = seen["_approval"]!.Arr("levels").Nodes().Select(l => l.Str("status")).ToArray();
        Assert.Equal(["approved", "skipped", "approved", "skipped"], levels);
        Assert.Equal("approved", seen["_approval"]!.Str("status"));
        // La automatización de la transición de salida corrió con la persona como actor.
        Assert.Equal(Id("a2"), await host.ScalarAsync<int>($"SELECT b.CreatedBy FROM app.AzBitacora b JOIN app.{E} s ON s.Folio = b.Nota WHERE s.Id = {id}"));
    }

    [Fact]
    public async Task Gana_la_regla_de_menor_prioridad_y_un_rango_de_perfil_incluye_al_director_desde_su_minimo()
    {
        var ti = await SentAsync("sol", "ti", "Servicios");
        Assert.Equal("Servicios TI", (await SeenByAdminAsync(ti))["_approval"]!.Str("rule"));
        Assert.Equal(People("a2", "a3"), await ApproversAsync(ti));

        var big = await SentAsync("sol", "general", monto: 250_000m);
        Assert.Equal(200, (await DecideAsAsync("a1", big)).Status);
        Assert.Equal(200, (await DecideAsAsync("a2", big)).Status);
        // El director (desde 100 000) es el último nivel y a3 no decidió antes.
        Assert.Equal(People("a3"), await ApproversAsync(big));
        Assert.Equal(200, (await DecideAsAsync("a3", big)).Status);
        Assert.Equal("Autorizada", await StateAsync(big));
    }

    [Fact]
    public async Task Sin_regla_o_con_todos_los_niveles_vacios_falla_cerrado()
    {
        using var sol = await As("sol");
        foreach (var (depto, monto, expected) in new[] { ("general", 2_000_000m, "Ninguna regla"), ("vacio", 10m, "no tiene a nadie") })
        {
            var row = await sol.CreateAsync(E, new { Concepto = "Cerrado", Departamento = Id(depto), Tipo = "Bienes", Monto = monto });
            var sent = await sol.TransitionAsync(E, row, "enviar");
            Assert.True(sent.Status == 400, sent.Text);
            Assert.Contains(expected, sent.Text);
            Assert.Equal("Borrador", await StateAsync(row.Id()));
            Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalRun WHERE EntityName = '{E}' AND RecordId = {row.Id()}"));
        }
    }

    // ---------------- Segregación ----------------

    [Fact]
    public async Task El_creador_no_autoriza_y_nadie_autoriza_dos_niveles()
    {
        var id = await SentAsync("a1", "general");
        Assert.Equal(People("a2"), await ApproversAsync(id));
        var own = await DecideAsAsync("a1", id);
        Assert.True(own.Status == 403, own.Text);
        Assert.Contains("lo creaste", own.Text);
        Assert.Equal(200, (await DecideAsAsync("a2", id)).Status);
        // Nivel 3 (Gerentes: a2 y a3): a2 ya decidió el 1.
        Assert.Equal(People("a3"), await ApproversAsync(id));
        var twice = await DecideAsAsync("a2", id);
        Assert.True(twice.Status is 403 or 404, twice.Text);
        Assert.Equal(200, (await DecideAsAsync("a3", id)).Status);
        Assert.Equal("Autorizada", await StateAsync(id));
    }

    [Fact]
    public async Task El_superusuario_fuera_del_perfil_no_autoriza()
    {
        var id = await SentAsync("sol", "general");
        var res = await DecideAsAsync("admin", id);
        Assert.True(res.Status == 403, res.Text);
        Assert.Contains("no estás en él", res.Text);
        Assert.Equal("PorAutorizar", await StateAsync(id));
    }

    [Fact]
    public async Task Un_nivel_que_solo_tiene_al_creador_espera_y_no_se_salta()
    {
        var id = await SentAsync("a1", "solo");
        Assert.Equal("PorAutorizar", await StateAsync(id));
        Assert.Empty(await ApproversAsync(id));
        var seen = await SeenByAdminAsync(id);
        Assert.Equal("current", seen["_approval"]!.Arr("levels").Nodes().First().Str("status"));
        Assert.Contains("Nadie puede autorizar", seen["_approval"]!.Str("waitingReason"));
        Assert.Equal(403, (await DecideAsAsync("a1", id)).Status);
    }

    [Fact]
    public async Task Nadie_cambia_su_propia_pertenencia()
    {
        using var admin = await As("admin");
        var adminId = await host.ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = 'admin'");
        var self = await admin.PostAsync("/api/admin/approvals/profiles", new { name = "Yo mismo", memberIds = new[] { adminId } });
        Assert.True(self.Status == 400, self.Text);
        Assert.Contains("a sí mismo", self.Text);
        using var ua = await As("ua");
        var mine = await ua.PostAsync("/api/admin/approvals/profiles", new { name = "Del UA", memberIds = new[] { Id("ua"), Id("a1") } });
        Assert.True(mine.Status == 400, mine.Text);
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.ApprovalProfile WHERE Name IN (N'Yo mismo', N'Del UA')"));
    }

    [Fact]
    public async Task Sin_administrar_usuarios_la_matriz_responde_403()
    {
        using var sol = await As("sol");
        Assert.Equal(403, (await sol.GetAsync("/api/admin/approvals")).Status);
        Assert.Equal(403, (await sol.PostAsync("/api/admin/approvals/profiles", new { name = "No", memberIds = new[] { Id("a1") } })).Status);
        Assert.Equal(403, (await sol.PostAsync("/api/admin/approvals/rules", new { entity = E, name = "No", levels = new[] { Id("pJefes") } })).Status);
        using var ua = await As("ua");
        var config = await ua.GetAsync("/api/admin/approvals");
        Assert.True(config.Status == 200, config.Text);
        Assert.Contains(config.Body!.Arr("entities").Nodes(), x => x.Str("name") == E);
    }

    [Fact]
    public async Task La_matriz_de_otra_empresa_no_se_ve_ni_se_cambia()
    {
        var other = await host.ScalarAsync<int>("""
            IF NOT EXISTS (SELECT 1 FROM meta.Company WHERE Code = 'IT53') INSERT meta.Company (Code, Name, BaseCurrency, IsDefault) VALUES ('IT53', N'Otra 053', 'MXN', 0);
            DECLARE @c int = (SELECT Id FROM meta.Company WHERE Code = 'IT53');
            IF NOT EXISTS (SELECT 1 FROM meta.ApprovalProfile WHERE CompanyId = @c) INSERT meta.ApprovalProfile (CompanyId, Name, CreatedBy) VALUES (@c, N'Ajeno', 1);
            SELECT Id FROM meta.ApprovalProfile WHERE CompanyId = @c AND Name = N'Ajeno';
            """);
        using var admin = await As("admin");
        var config = await admin.GetAsync("/api/admin/approvals");
        Assert.DoesNotContain(config.Body!.Arr("profiles").Nodes(), p => p.Id() == other);
        var put = await admin.PutAsync($"/api/admin/approvals/profiles/{other}", new { name = "Robado", memberIds = new[] { Id("a1") }, version = "AAAAAAAAAAA=" });
        Assert.Equal(404, put.Status);
        var rule = await admin.PostAsync("/api/admin/approvals/rules", new { entity = E, name = "Con ajeno", levels = new[] { other } });
        Assert.True(rule.Status == 400, rule.Text);
    }

    // ---------------- H4 CA4 y personas vivas ----------------

    [Fact]
    public async Task Solo_quien_le_toca_ve_el_pendiente_en_todos_los_caminos()
    {
        var id = await SentAsync("sol", "general");
        var folio = (await SeenByAdminAsync(id)).Str("Folio")!;
        using var a3 = await As("a3");
        async Task AssertSees(bool sees)
        {
            Assert.Equal(sees ? 200 : 404, (await a3.GetAsync($"/api/data/{E}/{id}")).Status);
            Assert.Equal(sees, (await a3.GetAsync($"/api/data/{E}", new { pageSize = 200 })).Text.Contains(folio));
            Assert.Equal(sees, (await a3.GetAsync($"/api/data/{E}/lookup", new { search = folio })).Text.Contains(folio));
            Assert.Equal(sees, (await a3.GetAsync($"/api/data/{E}/export", new { format = "csv", columns = "Folio" })).Text.Contains(folio));
            Assert.Equal(sees, (await a3.GetAsync($"/odata/{E}")).Text.Contains(folio));
            Assert.Equal(sees ? 200 : 404, (await a3.GetAsync($"/api/data/{E}/{id}/print")).Status);
        }
        await AssertSees(false);
        Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
        await AssertSees(true);
        Assert.Equal(200, (await DecideAsAsync("a2", id)).Status);
        // Autorizada: ya no le toca a nadie.
        await AssertSees(false);
    }

    [Fact]
    public async Task Dar_de_baja_quita_de_la_lista_al_momento_y_dar_de_alta_agrega()
    {
        var id = await SentAsync("sol", "vivo");
        Assert.Equal(People("a1"), await ApproversAsync(id));
        using var admin = await As("admin");
        var config = (await admin.GetAsync("/api/admin/approvals")).Body!;
        var vivos = config.Arr("profiles").Nodes().First(p => p.Id() == Id("pVivos"));
        var down = await admin.PutAsync($"/api/admin/approvals/profiles/{Id("pVivos")}", new { name = "Vivos", isActive = true, memberIds = new[] { Id("a2") }, version = vivos.Str("version") });
        Assert.True(down.Status == 200, down.Text);
        Assert.Equal(People("a2"), await ApproversAsync(id));
        using var a1 = await As("a1");
        Assert.Equal(404, (await a1.GetAsync($"/api/data/{E}/{id}")).Status);
        Assert.Equal(404, (await DecideAsAsync("a1", id)).Status);
        // Una baja nunca hace avanzar el documento.
        Assert.Equal("PorAutorizar", await StateAsync(id));
        Assert.Equal(200, (await DecideAsAsync("a2", id)).Status);
    }

    [Fact]
    public async Task Las_reglas_se_congelan_al_empezar()
    {
        var id = await SentAsync("sol", "congelada");
        using var admin = await As("admin");
        var rule = (await admin.GetAsync("/api/admin/approvals")).Body!.Arr("rules").Nodes().First(r => r.Id() == Id("rCongelada"));
        var put = await admin.PutAsync($"/api/admin/approvals/rules/{Id("rCongelada")}", new { entity = E, name = "Congelada", priority = 2, isActive = true,
            conditions = new { Departamento = new[] { Id("congelada") } }, levels = new[] { Id("pGerentes") }, version = rule.Str("version") });
        Assert.True(put.Status == 200, put.Text);
        Assert.Equal(People("a1", "a2"), await ApproversAsync(id));
        Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
        Assert.Equal("Autorizada", await StateAsync(id));
        // El siguiente documento ya usa la regla nueva.
        var next = await SentAsync("sol", "congelada");
        Assert.Equal(People("a2", "a3"), await ApproversAsync(next));
    }

    // ---------------- H5: reinicio y cancelación ----------------

    [Fact]
    public async Task Cambiar_el_monto_reinicia_la_autorizacion_y_la_edicion_directa_no_se_permite()
    {
        var id = await SentAsync("sol", "general");
        Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
        Assert.Equal(People("a2", "a3"), await ApproversAsync(id));
        using var sol = await As("sol");
        // F-02: el estado que se autoriza no es editable: lo autorizado es lo que se vio.
        var row = await sol.RecordAsync(E, id);
        var put = await sol.PutAsync($"/api/data/{E}/{id}", new { Monto = 900m, Concepto = "Otro proveedor", _rowVersion = row.Str("_rowVersion") });
        Assert.True(put.Status == 409, put.Text);
        // Por automatización: el monto pasa del máximo de toda regla → falla cerrado, nada escrito.
        var tooBig = await sol.PostAsync("/api/data/AzAjuste", new { Solicitud = id, Nuevo = 5_000_000m });
        Assert.True(tooBig.Status == 400, tooBig.Text);
        Assert.Equal(500m, await host.ScalarAsync<decimal>($"SELECT Monto FROM app.{E} WHERE Id = {id}"));
        Assert.Equal(201, (await sol.PostAsync("/api/data/AzAjuste", new { Solicitud = id, Nuevo = 950m })).Status);
        Assert.Equal(People("a1", "a2"), await ApproversAsync(id));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalRun WHERE EntityName = '{E}' AND RecordId = {id} AND Status = 'reset'"));
        Assert.Equal(1, await host.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM meta.ApprovalDecision d JOIN meta.ApprovalRun r ON r.Id = d.RunId
             WHERE r.EntityName = '{E}' AND r.RecordId = {id} AND r.Status = 'reset' AND d.Superseded = 1 AND d.Decision = 'approve'
            """));
        // F-09: la bitácora del reinicio no lleva el monto.
        var reset = await host.ScalarAsync<string>($"SELECT TOP 1 AfterJson FROM meta.AuditLog WHERE Action = 'approval.reset' AND RecordId = '{id}' ORDER BY Id DESC");
        Assert.DoesNotContain("950", reset);
        // Una transición al mismo estado que no cambia monto ni condiciones no reinicia.
        Assert.Equal(200, (await sol.TransitionAsync(E, await sol.RecordAsync(E, id), "comentar")).Status);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalRun WHERE EntityName = '{E}' AND RecordId = {id} AND Status = 'reset'"));
    }

    [Fact]
    public async Task Salir_por_otra_transicion_cancela_y_una_al_mismo_estado_no_reinicia()
    {
        var id = await SentAsync("sol", "general");
        Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
        using var sol = await As("sol");
        var same = await sol.TransitionAsync(E, await sol.RecordAsync(E, id), "comentar");
        Assert.True(same.Status == 200, same.Text);
        Assert.Equal(3, same.Body!["_approval"]!["level"]!.GetValue<int>());
        var back = await sol.TransitionAsync(E, await sol.RecordAsync(E, id), "devolver");
        Assert.True(back.Status == 200, back.Text);
        Assert.Equal("Borrador", await StateAsync(id));
        Assert.Empty(await ApproversAsync(id));
        Assert.Equal("cancelled", await host.ScalarAsync<string>($"SELECT TOP 1 Status FROM meta.ApprovalRun WHERE EntityName = '{E}' AND RecordId = {id} ORDER BY Id DESC"));
        // Volver a enviar empieza otra autorización desde el nivel 1.
        Assert.Equal(200, (await sol.TransitionAsync(E, await sol.RecordAsync(E, id), "enviar")).Status);
        Assert.Equal(People("a1", "a2"), await ApproversAsync(id));
    }

    // ---------------- Caminos alternos ----------------

    [Fact]
    public async Task Nadie_ejecuta_approve_ni_reject_por_la_ruta_de_transiciones()
    {
        var id = await SentAsync("sol", "general");
        foreach (var who in new[] { "admin", "a1", "sol" })
        {
            using var api = await As(who);
            var seen = await SeenByAdminAsync(id);
            foreach (var t in new[] { "autorizada", "rechazada" })
            {
                var res = await api.TransitionAsync(E, seen, t);
                Assert.True(res.Status is 400 or 403 or 404, $"{who} {t}: {res}");
            }
        }
        Assert.Equal("PorAutorizar", await StateAsync(id));
    }

    [Fact]
    public async Task El_plan_rechaza_atajos_a_la_matriz()
    {
        using var admin = await As("admin");
        async Task Invalid(object op, string expected)
        {
            var res = await admin.PostAsync("/api/model/plan/preview", new { description = "Atajo", operations = new[] { op } });
            Assert.True(res.Status == 200, res.Text);
            Assert.False(res["valid"]!.GetValue<bool>(), res.Text);
            Assert.Contains(expected, res.Text);
        }
        await Invalid(new { op = "setAutomations", entity = "AzAjuste", automations = new object[]
        {
            new { name = "Atajo", label = "Atajo", on = "create", actions = new object[] { new { type = "transition", target = "Solicitud", transition = "autorizada" } } },
        } }, "solo lo hace la matriz");
        await Invalid(new { op = "setAutomations", entity = "AzAjuste", automations = new object[]
        {
            new { name = "Atajo", label = "Atajo", on = "create", actions = new object[]
                { new { type = "update", target = "Solicitud", set = new[] { new { field = "Autorizadores", value = "CURRENTUSER()", mode = "add" } } } } },
        } }, "la mantiene la matriz");
        await Invalid(new { op = "updateField", entity = E, field = "Autorizadores", editable = true }, "no editable");
    }

    [Fact]
    public async Task Formulario_api_e_importacion_no_escriben_autorizadores()
    {
        using var sol = await As("sol");
        var row = await sol.CreateAsync(E, new { Concepto = "Sin atajo", Departamento = Id("general"), Tipo = "Bienes", Monto = 10m, Autorizadores = new[] { Id("sol") } });
        Assert.Empty(await ApproversAsync(row.Id()));
        var put = await sol.PutAsync($"/api/data/{E}/{row.Id()}", new { Autorizadores = new[] { Id("sol") }, _rowVersion = row.Str("_rowVersion") });
        Assert.Equal(200, put.Status);
        Assert.Empty(await ApproversAsync(row.Id()));
        using var admin = await As("admin");
        var csv = Encoding.UTF8.GetBytes($"Concepto;Departamento;Tipo;Monto;Autorizadores;Estado\nImportada 053;general;Bienes;10;Persona sol;Autorizada\n");
        var dry = await admin.PostBytesAsync($"/api/data/{E}/import?dryRun=true", csv);
        Assert.True(dry.Status == 200, dry.Text);
        Assert.Contains("Lo calcula o asigna el sistema; se ignora.", dry.Text);
        var run = await admin.PostBytesAsync($"/api/data/{E}/import?dryRun=false", csv);
        Assert.True(run.Status == 200, run.Text);
        var imported = await host.ScalarAsync<int>($"SELECT Id FROM app.{E} WHERE Concepto = N'Importada 053'");
        Assert.True(imported > 0, run.Text);
        Assert.Equal("Borrador", await StateAsync(imported));
        Assert.Empty(await ApproversAsync(imported));
    }

    [Fact]
    public async Task Una_persona_del_perfil_que_no_ve_el_registro_no_autoriza()
    {
        var id = await SentAsync("sol", "ciego");
        Assert.Equal(People("ciego"), await ApproversAsync(id));
        var res = await DecideAsAsync("ciego", id);
        Assert.Equal(403, res.Status);
        Assert.Equal("PorAutorizar", await StateAsync(id));
    }

    // ---------------- Huella, concurrencia y fallas parciales ----------------

    [Fact]
    public async Task Una_decision_con_la_version_vieja_es_409_y_rechazar_pide_motivo()
    {
        var id = await SentAsync("sol", "general");
        using var a2 = await As("a2");
        var old = await a2.RecordAsync(E, id);
        Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
        // a2 vio el nivel 1 (también está en Gerentes): esa decisión no cuenta para el nivel 3.
        var stale = await DecideAsync(a2, id, old);
        Assert.True(stale.Status == 409, stale.Text);
        var noReason = await DecideAsync(a2, id, await a2.RecordAsync(E, id), "reject");
        Assert.True(noReason.Status == 400, noReason.Text);
        var reject = await DecideAsync(a2, id, await a2.RecordAsync(E, id), "reject", "Fuera de presupuesto");
        Assert.True(reject.Status == 200, reject.Text);
        Assert.Equal("Rechazada", await StateAsync(id));
        Assert.Equal("Fuera de presupuesto", await host.ScalarAsync<string>($"SELECT TOP 1 Comment FROM meta.WorkflowHistory WHERE EntityName = '{E}' AND RecordId = {id} ORDER BY Id DESC"));
    }

    [Fact]
    public async Task La_bitacora_nombra_regla_nivel_persona_y_huella()
    {
        var id = await SentAsync("sol", "general");
        Assert.Equal(200, (await DecideAsAsync("a1", id, comment: "Revisado")).Status);
        var after = await host.ScalarAsync<string>($"SELECT TOP 1 AfterJson FROM meta.AuditLog WHERE Action = 'approval.approve' AND RecordId = '{id}' ORDER BY Id DESC");
        Assert.Contains("General", after);
        Assert.Contains("Jefes", after);
        Assert.Contains("Revisado", after);
        Assert.Matches("\"fingerprint\":\"[0-9a-f]{64}\"", after);
        Assert.Equal(Id("a1"), await host.ScalarAsync<int>($"SELECT TOP 1 UserId FROM meta.AuditLog WHERE Action = 'approval.approve' AND RecordId = '{id}' ORDER BY Id DESC"));
        Assert.Equal(32, await host.ScalarAsync<int>($"SELECT TOP 1 DATALENGTH(d.Fingerprint) FROM meta.ApprovalDecision d JOIN meta.ApprovalRun r ON r.Id = d.RunId WHERE r.RecordId = {id} AND d.Decision = 'approve'"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'approval.start' AND RecordId = '{id}'"));
    }

    [Fact]
    public async Task Diez_personas_del_mismo_nivel_a_la_vez_cuenta_una()
    {
        var id = await SentAsync("sol", "masivo");
        var clients = new List<Client>();
        for (var i = 1; i <= 10; i++) clients.Add(await As($"m{i:00}"));
        var seen = await Task.WhenAll(clients.Select(c => c.RecordAsync(E, id)));
        var results = await Concurrently(10, i => DecideAsync(clients[i], id, seen[i]));
        foreach (var c in clients) c.Dispose();
        // Quien ya no tiene turno tampoco ve el documento: 404 (no es oráculo); quien lo veía, 409.
        Assert.All(results, r => Assert.True(r.Status is 200 or 404 or 409, r.ToString()));
        ExactlyOnce(results.Count(r => r.Status == 200), "decisión del nivel 1");
        ExactlyOnce(await host.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM meta.ApprovalDecision d JOIN meta.ApprovalRun r ON r.Id = d.RunId
             WHERE r.EntityName = '{E}' AND r.RecordId = {id} AND d.Level = 1 AND d.Decision = 'approve'
            """), "decisión guardada del nivel 1");
        Assert.Equal(People("a2", "a3"), await ApproversAsync(id));
    }

    [Fact]
    public async Task Autorizar_mientras_se_quita_a_la_persona_da_un_orden_serial()
    {
        var id = await SentAsync("sol", "carrera");
        using var admin = await As("admin");
        var carrera = (await admin.GetAsync("/api/admin/approvals")).Body!.Arr("profiles").Nodes().First(p => p.Id() == Id("pCarrera"));
        var members = carrera.Arr("members").Nodes().Select(m => m.Id()).ToArray();
        var who = members.Contains(Id("a2")) ? "a2" : "a1";
        Res decide, remove;
        await using (var hold = await host.HoldAsync($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'erp:approval', @LockMode = 'Exclusive', @LockOwner = 'Transaction';"))
        {
            using var person = await As(who);
            var seen = await person.RecordAsync(E, id);
            var d = DecideAsync(person, id, seen);
            var r = admin.PutAsync($"/api/admin/approvals/profiles/{Id("pCarrera")}", new { name = "Carrera", isActive = true,
                memberIds = members.Where(m => m != Id(who)).Append(Id(who == "a1" ? "a2" : "a1")).ToArray(), version = carrera.Str("version") });
            await hold.WaitUntilBlockingAsync();
            await hold.DisposeAsync();
            decide = await d;
            remove = await r;
        }
        Assert.True(remove.Status == 200, remove.Text);
        var decided = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalDecision d JOIN meta.ApprovalRun r ON r.Id = d.RunId WHERE r.RecordId = {id} AND r.EntityName = '{E}' AND d.UserId = {Id(who)}");
        if (decide.Status == 200) Assert.Equal(1, decided);
        else { Assert.True(decide.Status is 403 or 404, decide.Text); Assert.Equal(0, decided); }
    }

    [Fact]
    public async Task Si_falla_la_automatizacion_de_approve_no_queda_la_decision()
    {
        var id = await SentAsync("sol", "ti", "Servicios");
        var folio = await host.ScalarAsync<string>($"SELECT Folio FROM app.{E} WHERE Id = {id}");
        await using (await host.FailWhenAsync("app.AzBitacora", $"Nota = N'{folio}'"))
        {
            var res = await DecideAsAsync("a2", id);
            Assert.True(res.Status >= 400, res.Text);
        }
        Assert.Equal("PorAutorizar", await StateAsync(id));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalDecision d JOIN meta.ApprovalRun r ON r.Id = d.RunId WHERE r.RecordId = {id} AND r.EntityName = '{E}' AND d.Decision = 'approve'"));
        Assert.Equal(People("a2", "a3"), await ApproversAsync(id));
        Assert.Equal(200, (await DecideAsAsync("a2", id)).Status);
        Assert.Equal("Autorizada", await StateAsync(id));
    }

    // ---------------- Compuerta de seguridad (015) ----------------

    [Fact]
    public async Task Con_aprobacion_de_seguridad_la_matriz_espera_a_otro_administrador_que_no_sea_miembro_agregado()
    {
        using var ua = await host.Sec.LoginAsync("it53-ua", Pwd);
        var req = await ua.PostAsync("/api/admin/approvals/profiles", new { name = "Por aprobar 053", memberIds = new[] { Id("a1"), Id("ub") } });
        Assert.True(req.Status == 202, req.Text);
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.ApprovalProfile WHERE Name = N'Por aprobar 053'"));
        var change = req.Body!["securityChange"]!;
        // ub es una persona que el cambio agrega: no lo aprueba.
        using var ub = await host.Sec.LoginAsync("it53-ub", Pwd);
        var mine = await ub.PostAsync($"/api/admin/security-changes/{change.Id()}/approve", new { hash = change.Str("hash"), comment = "IT 053" });
        Assert.True(mine.Status == 403, mine.Text);
        using var admin = await host.Sec.LoginAsync("admin", host.AdminPassword);
        var ok = await admin.PostAsync($"/api/admin/security-changes/{change.Id()}/approve", new { hash = change.Str("hash"), comment = "IT 053" });
        Assert.True(ok.Status == 200, ok.Text);
        Assert.Equal(2, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.ApprovalProfileMember m JOIN meta.ApprovalProfile p ON p.Id = m.ProfileId WHERE p.Name = N'Por aprobar 053'"));
    }

    // ---------------- Revisión adversarial 053 ----------------

    [Fact]
    public async Task F01_desactivar_a_la_unica_persona_del_perfil_no_salta_el_nivel()
    {
        var id = await SentAsync("sol", "unico");
        using var admin = await As("admin");
        async Task Active(bool on) => Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{Id("a3")}",
            new { displayName = "Persona a3", isActive = on, roleIds = new[] { Id("rolAut") } })).Status);
        await Active(false);
        try
        {
            Assert.Equal(200, (await DecideAsAsync("a1", id)).Status);
            // El nivel 2 (Único: solo a3, inactiva) espera: no se salta ni autoriza el documento.
            Assert.Equal("PorAutorizar", await StateAsync(id));
            var seen = await SeenByAdminAsync(id);
            Assert.Equal(2, seen["_approval"]!["level"]!.GetValue<int>());
            Assert.Contains("inactivas", seen["_approval"]!.Str("waitingReason"));
            Assert.Empty(await ApproversAsync(id));
        }
        finally { await Active(true); }
    }

    [Fact]
    public async Task F03_F04_el_plan_rechaza_otra_entrada_al_estado_autorizado_y_condiciones_en_la_salida()
    {
        using var admin = await As("admin");
        var wf = (await admin.GetAsync($"/api/model/entities/{E}")).Body!["workflow"]!.DeepClone().AsObject();
        async Task Invalid(JsonObject w, string expected)
        {
            var res = await admin.PostAsync("/api/model/plan/preview", new { description = "Atajo", operations = new object[] { new { op = "setWorkflow", entity = E, workflow = w } } });
            Assert.True(res.Status == 200, res.Text);
            Assert.False(res["valid"]!.GetValue<bool>(), res.Text);
            Assert.Contains(expected, res.Text);
        }
        var forzar = wf.DeepClone().AsObject();
        forzar["transitions"]!.AsArray().Add(new JsonObject { ["name"] = "forzar", ["label"] = "Forzar", ["from"] = new JsonArray("Borrador"), ["to"] = "Autorizada" });
        await Invalid(forzar, "solo se llega autorizando");
        var cuando = wf.DeepClone().AsObject();
        cuando["transitions"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "autorizada")!["when"] = "Monto > 0";
        await Invalid(cuando, "la decide la matriz");
        var editable = wf.DeepClone().AsObject();
        editable["states"]!.AsArray().First(st => st!["name"]!.GetValue<string>() == "PorAutorizar")!["editable"] = true;
        await Invalid(editable, "no editable");
    }

    [Fact]
    public async Task F05_una_corrida_huerfana_no_impide_volver_a_empezar()
    {
        using var sol = await As("sol");
        var row = await sol.CreateAsync(E, new { Concepto = "Huérfana", Departamento = Id("general"), Tipo = "Bienes", Monto = 10m });
        const string vieja = """{"ruleId":1,"name":"Vieja","levels":[]}""";
        await host.ExecAsync($"""
            INSERT meta.ApprovalRun (EntityName, RecordId, CompanyId, RuleId, RuleJson, ConditionsHash, CurrentLevel, Status, StartedBy)
            SELECT '{E}', {row.Id()}, CompanyId, {Id("rGeneral")}, N'{vieja}', 0x00, NULL, 'pending', {Id("sol")}
              FROM app.{E} WHERE Id = {row.Id()}
            """);
        var sent = await sol.TransitionAsync(E, row, "enviar");
        Assert.True(sent.Status == 200, sent.Text);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.ApprovalRun WHERE EntityName = '{E}' AND RecordId = {row.Id()} AND Status = 'pending'"));
        Assert.Equal(People("a1", "a2"), await ApproversAsync(row.Id()));
    }

    [Fact]
    public async Task F06_quien_lo_manda_a_autorizar_no_lo_autoriza()
    {
        // Otro administrador (ua) pone al superusuario en un perfil; el superusuario envía un documento de sol y no lo autoriza.
        using var ua = await As("ua");
        var adminId = await host.ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = 'admin'");
        var p = await ua.PostAsync("/api/admin/approvals/profiles", new { name = "Con el admin", memberIds = new[] { adminId, Id("a3") } });
        Assert.True(p.Status == 201, p.Text);
        using var admin = await As("admin");
        var r = await admin.PostAsync("/api/admin/approvals/rules", new { entity = E, name = "Envío", priority = 2,
            conditions = new { Departamento = new[] { Id("starter") } }, levels = new[] { p.Body!.Id() } });
        Assert.True(r.Status == 201, r.Text);
        using var sol = await As("sol");
        var row = await sol.CreateAsync(E, new { Concepto = "La envía otro", Departamento = Id("starter"), Tipo = "Bienes", Monto = 10m });
        Assert.Equal(200, (await admin.TransitionAsync(E, await admin.RecordAsync(E, row.Id()), "enviar")).Status);
        Assert.Equal(People("a3"), await ApproversAsync(row.Id()));
        var own = await DecideAsAsync("admin", row.Id());
        Assert.True(own.Status == 403, own.Text);
        Assert.Contains("lo mandaste", own.Text);
    }
}
