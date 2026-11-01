using System.Text.Json.Nodes;
using Erp.Api.Data;
using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 041 · Reportes personales y exploración sobre SQL real: corre con el alcance, la empresa y los campos de quien pide
/// (también un reporte guardado cuando veía más), lo oculto responde como lo inexistente, solo el dueño ve y cambia su
/// reporte, la versión protege «abrir → guardar», el tope y el nombre único aguantan altas simultáneas y la bitácora va en
/// la misma transacción.
/// </summary>
public class PersonalReportsTests(PersonalReportsTests.Host host) : IClassFixture<PersonalReportsTests.Host>
{
    private const string Pwd = "frase larga de reportes 041";
    private const string Role = "IT41 Lector";
    private const string Lector = "it41-lector";
    private const string Tope = "it41-tope";
    private const string Pedido = "It41Pedido";

    public sealed class Host : ErpHost
    {
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
            var main = companies.First(c => c["isDefault"]!.GetValue<bool>()).Id();
            var other = (await admin.PostAsync("/api/admin/companies", new { code = "IT41-B", name = "Otra empresa 041", baseCurrency = "MXN" })).Body!.Id();
            var role = await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 041", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() });
            Assert.True(role.Status == 200, role.Text);
            await admin.ApplyPlanAsync(new { description = "Spec 041 · reportes personales", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 041", name = "It41Cliente", label = "Cliente 041", labelPlural = "Clientes 041", displayField = "Nombre", perCompany = true, fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = "Pruebas 041", name = Pedido, label = "Pedido 041", labelPlural = "Pedidos 041", fields = new object[] {
                    new { name = "Fecha", label = "Fecha", type = "date", required = true },
                    new { name = "Cliente", label = "Cliente", type = "reference", referenceEntity = "It41Cliente" },
                    new { name = "Zona", label = "Zona", type = "text", required = true },
                    new { name = "Region", label = "Región", type = "text" },
                    new { name = "Monto", label = "Monto", type = "decimal", required = true },
                    new { name = "Interno", label = "Interno", type = "text" } } },
                new { op = "createEntity", name = "It41Partida", label = "Partida 041", labelPlural = "Partidas 041", fields = new object[] {
                    new { name = "Pedido", label = "Pedido", type = "parent", referenceEntity = Pedido },
                    new { name = "Importe", label = "Importe", type = "decimal", required = true } } },
                new { op = "addField", entity = Pedido, field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "It41Partida" } },
                new { op = "createEntity", group = "Pruebas 041", name = "It41Secreto", label = "Secreto 041", labelPlural = "Secretos 041", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text" } } },
                new { op = "setSecurity", entity = Pedido, security = new {
                    fields = new[] { new { field = "Interno", read = new[] { "Administrador" } } },
                    records = new[] { new { roles = new[] { Role }, where = "Zona <> 'Privada'", access = "read" } } } },
                new { op = "setReports", entity = Pedido, reports = new object[] {
                    new { name = "PorZona", label = "Por zona", groupBy = new[] { new { field = "Zona" } }, measures = new object[] {
                        new { fn = "sum", field = "Monto", name = "Total" }, new { fn = "count", name = "Cuantos" },
                        new { fn = "calc", expr = "ROUND(Total / Cuantos, 2)", label = "Promedio" } }, chart = "bar", orderBy = "measure", dir = "desc" },
                    new { name = "Grandes", label = "Pedidos grandes", filter = "Monto >= 100", groupBy = new[] { new { field = "Zona" } },
                        measures = new[] { new { fn = "count" } } } } },
            } });
            var perms = new[] { Pedido, "It41Cliente" }
                .Select(entity => new { entity, canRead = true, canCreate = false, canUpdate = false, canDelete = false }).ToArray();
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{role.Body!.Id()}", new { name = Role, description = "Spec 041", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            foreach (var user in new[] { Lector, Tope })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = user, displayName = user, password = Pwd,
                    roleIds = new[] { role.Body!.Id() }, companyIds = new[] { main } });
                Assert.True(u.Status == 201, u.Text);
            }
            var a = (await admin.CreateAsync("It41Cliente", new { Nombre = "Cliente A" })).Id();
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = other })).Status);
            var b = (await admin.CreateAsync("It41Cliente", new { Nombre = "Cliente de otra empresa" })).Id();
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = main })).Status);
            await admin.CreateAsync(Pedido, new { Fecha = "2026-09-01", Cliente = a, Zona = "Norte", Region = "Uno", Monto = 100, Interno = "nota interna" });
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-01", Cliente = a, Zona = "Sur", Region = "Uno", Monto = 50 });
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-02", Cliente = a, Zona = "Sur", Region = "Dos", Monto = 30 });
            var otro = (await admin.CreateAsync(Pedido, new { Fecha = "2026-10-03", Zona = "Norte", Region = "Dos", Monto = 7 })).Id();
            // El ERP no deja apuntar a un cliente de otra empresa; un dato así llega por importación antigua: se prepara por SQL.
            await ExecAsync($"UPDATE [app].[{Pedido}] SET [Cliente] = {b} WHERE [Id] = {otro}");
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-04", Cliente = a, Zona = "Privada", Monto = 1000 });
        }
    }

    private Task<Client> LectorAsync() => host.LoginAsync(Lector, Pwd);

    private static object Def(string group, string entity = Pedido, object? filters = null, string measureField = "Monto") => new
    {
        entity, groupBy = new[] { new { field = group } }, measures = new[] { new { fn = "sum", field = measureField } }, filters,
    };

    private static List<string> Rows(JsonNode r) => r.Arr("rows").Nodes()
        .Select(row => $"{row.Str("g0")}={Norm(row["m0"])}").ToList();

    private static string Norm(JsonNode? n) => n is null ? "null"
        : n.GetValue<decimal>().ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonNode> ExploreAsync(Client c, object definition, string? label = null)
    {
        var res = await c.PostAsync("/api/my-reports/explore", new { label, definition });
        Assert.True(res.Status == 200, res.ToString());
        return res.Body!;
    }

    [Fact]
    public async Task Explorar_respeta_alcance_y_empresa()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        Assert.Equal(["Norte=107", "Privada=1000", "Sur=80"], Rows(await ExploreAsync(admin, Def("Zona"))));
        // El lector no ve el pedido de la zona Privada (alcance por registro).
        var l = await ExploreAsync(lector, Def("Zona"));
        Assert.Equal(["Norte=107", "Sur=80"], Rows(l));
        Assert.Equal(187m, l["totals"]!.Dec("m0"));
        Assert.Equal(PersonalReports.DefaultLabel, l.Str("label"));
        // El cliente de otra empresa es «(sin acceso)» para los dos en la empresa principal; nunca su nombre.
        var byClient = await ExploreAsync(lector, Def("Cliente"));
        Assert.Equal([$"{ReportService.NoAccess}=7", "Cliente A=180"], Rows(byClient).Order().ToList());
        Assert.DoesNotContain("otra empresa", byClient.Json());
        Assert.DoesNotContain("otra empresa", (await ExploreAsync(admin, Def("Cliente.Nombre"))).Json());
    }

    [Fact]
    public async Task Explorar_rechaza_lo_que_no_valida_y_lo_oculto_responde_como_lo_inexistente()
    {
        using var lector = await LectorAsync();
        var hidden = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("Interno") });
        var missing = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("Nada") });
        Assert.Equal(400, hidden.Status);
        Assert.Equal(400, missing.Status);
        Assert.Equal(hidden.Text.Replace("Interno", "X"), missing.Text.Replace("Nada", "X").Replace(missing["traceId"]?.GetValue<string>() ?? "-", hidden["traceId"]?.GetValue<string>() ?? "-"));
        Assert.DoesNotContain("nota interna", hidden.Text);
        // Filtrar por un campo oculto tampoco.
        var filter = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("Zona", filters: new[] { new { field = "Interno", op = "contains", value = "nota" } }) });
        Assert.Equal(400, filter.Status);
        Assert.Contains("no existe o no puedes ver", filter.Text);
        // Un catálogo que no lee responde igual que uno que no existe.
        var secret = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("Nombre", "It41Secreto") });
        var none = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("Nombre", "It41NoExiste") });
        Assert.Equal(404, secret.Status);
        Assert.Equal(404, none.Status);
        Assert.Equal(secret["title"]!.GetValue<string>(), none["title"]!.GetValue<string>());
        // Lo que el modelo no admite, con el mensaje que dice qué cambiar.
        var pie = await lector.PostAsync("/api/my-reports/explore", new { definition = new { entity = Pedido, chart = "pie",
            groupBy = new[] { new { field = "Zona" } }, measures = new[] { new { fn = "sum", field = "Monto" }, new { fn = "count", field = (string?)null } },
            chartOptions = new { measures = new[] { 0, 1 } } } });
        Assert.Equal(400, pie.Status);
        Assert.Contains("Un pastel o una dona muestra una sola medida.", pie.Text);
    }

    [Fact]
    public async Task Fuentes_no_ofrecen_ocultos_ni_catalogos_que_no_lee()
    {
        using var lector = await LectorAsync();
        using var admin = await host.AdminAsync();
        var sources = (await lector.GetAsync("/api/my-reports/sources")).Array.Nodes().Select(s => s.Str("entity")).ToList();
        Assert.Contains(Pedido, sources);
        Assert.DoesNotContain("It41Secreto", sources);
        var fields = (await lector.GetAsync($"/api/my-reports/sources/{Pedido}")).Body!.Arr("fields").Nodes().Select(f => f.Str("path")).ToList();
        Assert.Contains("Zona", fields);
        Assert.Contains("Cliente.Nombre", fields);
        Assert.DoesNotContain("Interno", fields);
        Assert.Contains("Interno", (await admin.GetAsync($"/api/my-reports/sources/{Pedido}")).Body!.Arr("fields").Nodes().Select(f => f.Str("path")));
        Assert.Equal(404, (await lector.GetAsync("/api/my-reports/sources/It41Secreto")).Status);
    }

    [Fact]
    public async Task Explorar_da_lo_mismo_que_el_reporte_del_modelo_y_avisa_el_filtro_fijo()
    {
        using var lector = await LectorAsync();
        var model = (await lector.GetAsync($"/api/reports/{Pedido}/PorZona")).Body!;
        Assert.False(model["fixedFilter"]!.GetValue<bool>());
        var def = model["definition"]!;
        Assert.Equal(Pedido, def.Str("entity"));
        Assert.Null(def["roles"]);
        var explored = await ExploreAsync(lector, def, "Por zona");
        Assert.Equal(model["rows"]!.Json(), explored["rows"]!.Json());
        Assert.Equal(model["totals"]!.Json(), explored["totals"]!.Json());
        Assert.Equal(model["columns"]!.Json(), explored["columns"]!.Json());
        // Con filtro fijo se dice (y no se copia).
        var grandes = (await lector.GetAsync($"/api/reports/{Pedido}/Grandes")).Body!;
        Assert.True(grandes["fixedFilter"]!.GetValue<bool>());
        Assert.Null(grandes["definition"]!["filters"]);
        Assert.DoesNotContain("Monto >= 100", grandes.Json());
    }

    [Fact]
    public async Task Exportar_da_lo_mismo_que_explorar()
    {
        using var lector = await LectorAsync();
        var def = Def("Zona");
        var csv = await lector.PostAsync("/api/my-reports/explore/export", new { label = "Mis zonas", definition = def, format = "csv" });
        Assert.Equal(200, csv.Status);
        Assert.Contains($"{Pedido}-{PersonalReports.ReportName}-", csv.Headers["Content-Disposition"]);
        var lines = csv.Text.Trim().Split("\r\n");
        Assert.Equal(["Norte,107", "Sur,80"], lines.Skip(1).Take(2).Select(x => x.Replace(".00", "")).ToArray());
        Assert.DoesNotContain("Privada", csv.Text);
        var xlsx = await lector.PostAsync("/api/my-reports/explore/export", new { definition = def, format = "xlsx" });
        Assert.Equal(200, xlsx.Status);
        Assert.Equal(400, (await lector.PostAsync("/api/my-reports/explore/export", new { definition = def, format = "pdf" })).Status);
        Assert.Equal(400, (await lector.PostAsync("/api/my-reports/explore/export", new { definition = Def("Interno"), format = "csv" })).Status);
    }

    private static async Task<JsonNode> CreateAsync(Client c, string label, object definition)
    {
        var res = await c.PostAsync("/api/my-reports", new { label, definition });
        Assert.True(res.Status == 201, res.ToString());
        return res.Body!;
    }

    [Fact]
    public async Task Solo_el_dueno_ve_y_cambia_su_reporte()
    {
        using var lector = await LectorAsync();
        using var admin = await host.AdminAsync();
        var mine = await CreateAsync(lector, "Del lector " + Guid.NewGuid().ToString("N")[..6], Def("Zona"));
        var id = mine.Id();
        Assert.Equal(1, mine["version"]!.GetValue<int>());
        Assert.Contains((await lector.GetAsync("/api/my-reports")).Array.Nodes(), r => r.Id() == id && r["available"]!.GetValue<bool>());
        var own = await lector.GetAsync($"/api/my-reports/{id}");
        Assert.Equal(200, own.Status);
        Assert.Equal("Zona", own["definition"]!["groupBy"]![0]!.Str("field"));
        // Otro usuario (aunque sea el superusuario) recibe lo mismo que con un id que no existe.
        var notFound = await admin.GetAsync("/api/my-reports/999999");
        foreach (var res in new[] {
            await admin.GetAsync($"/api/my-reports/{id}"),
            await admin.PutAsync($"/api/my-reports/{id}", new { label = "Robado", definition = Def("Zona"), version = 1 }),
            await admin.DeleteAsync($"/api/my-reports/{id}?version=1") })
        {
            Assert.Equal(404, res.Status);
            Assert.Equal(notFound["title"]!.GetValue<string>(), res["title"]!.GetValue<string>());
        }
        Assert.DoesNotContain((await admin.GetAsync("/api/my-reports")).Array.Nodes(), r => r.Id() == id);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT Version FROM meta.PersonalReport WHERE Id = {id}"));
    }

    [Fact]
    public async Task Guardar_con_version_vieja_da_409()
    {
        using var lector = await LectorAsync();
        var label = "Versiones " + Guid.NewGuid().ToString("N")[..6];
        var id = (await CreateAsync(lector, label, Def("Zona"))).Id();
        var first = await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = Def("Region"), version = 1 });
        Assert.Equal(200, first.Status);
        Assert.Equal(2, first["version"]!.GetValue<int>());
        // La otra pestaña todavía tenía la versión 1: no pisa el cambio.
        var stale = await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = Def("Zona"), version = 1 });
        Assert.Equal(409, stale.Status);
        Assert.Equal(409, (await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = Def("Zona") })).Status);
        Assert.Equal("Region", (await lector.GetAsync($"/api/my-reports/{id}")).Body!["definition"]!["groupBy"]![0]!.Str("field"));
        Assert.Equal(409, (await lector.DeleteAsync($"/api/my-reports/{id}?version=1")).Status);
        Assert.Equal(409, (await lector.DeleteAsync($"/api/my-reports/{id}")).Status);
        Assert.Equal(204, (await lector.DeleteAsync($"/api/my-reports/{id}?version=2")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/my-reports/{id}")).Status);
    }

    [Fact]
    public async Task Diez_altas_simultaneas_no_pasan_el_tope()
    {
        using var tope = await host.LoginAsync(Tope, Pwd);
        var existing = (await tope.GetAsync("/api/my-reports")).Array.Count;
        for (var i = existing; i < PersonalReportService.MaxPerUser - 5; i++) await CreateAsync(tope, $"Tope {i}", Def("Zona"));
        var results = await Concurrency.Concurrently(10, i => tope.PostAsync("/api/my-reports", new { label = $"Simultánea {i}", definition = Def("Zona") }));
        Invariant.Statuses(results, 201, 201, 201, 201, 201, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains($"Máximo {PersonalReportService.MaxPerUser}", r.Text));
        var count = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.PersonalReport r JOIN meta.[User] u ON u.Id = r.UserId WHERE u.Username = '{Tope}'");
        Invariant.AtMost(count, PersonalReportService.MaxPerUser, "reportes personales de una persona");
        Assert.Equal(PersonalReportService.MaxPerUser, count);
    }

    [Fact]
    public async Task Nombre_unico_con_altas_simultaneas()
    {
        using var lector = await LectorAsync();
        var label = "Mismo nombre " + Guid.NewGuid().ToString("N")[..6];
        var results = await Concurrency.Concurrently(10, _ => lector.PostAsync("/api/my-reports", new { label, definition = Def("Zona") }));
        Invariant.Statuses(results, 201, 400, 400, 400, 400, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains("Ya tienes un reporte con ese nombre", r.Text));
        // Renombrar otro al mismo nombre tampoco.
        var other = await CreateAsync(lector, label + " (2)", Def("Zona"));
        var rename = await lector.PutAsync($"/api/my-reports/{other.Id()}", new { label, definition = Def("Zona"), version = 1 });
        Assert.Equal(400, rename.Status);
    }

    [Fact]
    public async Task Guardado_deja_de_correr_si_se_oculta_un_campo()
    {
        using var lector = await LectorAsync();
        using var admin = await host.AdminAsync();
        var id = (await CreateAsync(lector, "Por región " + Guid.NewGuid().ToString("N")[..6], Def("Region"))).Id();
        // Región pasa a ser solo del administrador: el reporte guardado cuando la veía deja de correr.
        await admin.ApplyPlanAsync(new { description = "Spec 041 · ocultar región", operations = new object[] {
            new { op = "setSecurity", entity = Pedido, security = new {
                fields = new[] { new { field = "Interno", read = new[] { "Administrador" } }, new { field = "Region", read = new[] { "Administrador" } } },
                records = new[] { new { roles = new[] { Role }, where = "Zona <> 'Privada'", access = "read" } } } } } });
        try
        {
            var saved = (await lector.GetAsync($"/api/my-reports/{id}")).Body!;
            Assert.False(saved["available"]!.GetValue<bool>());
            Assert.Contains("'Region' no existe o no puedes verlo.", saved.Arr("problems").Json());
            Assert.Contains((await lector.GetAsync("/api/my-reports")).Array.Nodes(), r => r.Id() == id && !r["available"]!.GetValue<bool>());
            var run = await lector.PostAsync("/api/my-reports/explore", new { definition = saved["definition"] });
            Assert.Equal(400, run.Status);
            Assert.DoesNotContain("Uno", run.Text);
            // Guardarlo así tampoco; con otra agrupación, sí.
            Assert.Equal(400, (await lector.PutAsync($"/api/my-reports/{id}", new { label = "Por región", definition = saved["definition"], version = 1 })).Status);
            Assert.Equal(200, (await lector.PutAsync($"/api/my-reports/{id}", new { label = "Ya por zona", definition = Def("Zona"), version = 1 })).Status);
        }
        finally
        {
            await admin.ApplyPlanAsync(new { description = "Spec 041 · región visible", operations = new object[] {
                new { op = "setSecurity", entity = Pedido, security = new {
                    fields = new[] { new { field = "Interno", read = new[] { "Administrador" } } },
                    records = new[] { new { roles = new[] { Role }, where = "Zona <> 'Privada'", access = "read" } } } } } });
        }
    }

    [Fact]
    public async Task Alta_y_bitacora_en_la_misma_transaccion()
    {
        using var lector = await LectorAsync();
        using var admin = await host.AdminAsync();
        var label = "Bitácora " + Guid.NewGuid().ToString("N")[..6];
        var id = (await CreateAsync(lector, label, Def("Zona"))).Id();
        var audit = (await admin.GetAsync("/api/audit", new { entity = Pedido, pageSize = 200 })).Items.Nodes()
            .Where(x => x.Str("action") == "report.personal" && x.Str("recordId") == id.ToString()).ToList();
        Assert.Single(audit);
        Assert.Contains(label, audit[0].Json());
        // Si la bitácora falla, el reporte tampoco queda (una transacción).
        var failed = "Sin auditoria " + Guid.NewGuid().ToString("N")[..6];
        await using (await host.FailWhenAsync("meta.AuditLog", $"Action = 'report.personal' AND AfterJson LIKE N'%{failed}%'"))
        {
            var res = await lector.PostAsync("/api/my-reports", new { label = failed, definition = Def("Zona") });
            Assert.Equal(500, res.Status);
            var del = await lector.DeleteAsync($"/api/my-reports/{id}?version=1");
            Assert.Equal(204, del.Status);
        }
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.PersonalReport WHERE Label = N'{failed}'"));
        Assert.Contains((await admin.GetAsync("/api/audit", new { entity = Pedido, pageSize = 200 })).Items.Nodes(),
            x => x.Str("action") == "report.personal" && x.Str("recordId") == id.ToString() && x["after"] is null);
    }

    [Fact]
    public async Task Bitacora_no_lleva_la_definicion_y_va_bajo_el_documento()
    {
        // Regresión (revisión adversarial 041, F-B): un auditor que lee el catálogo veía la definición (rutas y valores de los
        // filtros) del reporte personal de otro. Ahora solo el nombre y la huella.
        using var lector = await LectorAsync();
        using var admin = await host.AdminAsync();
        var label = "Huella " + Guid.NewGuid().ToString("N")[..6];
        var id = (await CreateAsync(lector, label, Def("Zona", filters: new[] { new { field = "Zona", op = "eq", value = "valor-del-filtro-041" } }))).Id();
        var entry = (await admin.GetAsync("/api/audit", new { entity = Pedido, pageSize = 200 })).Items.Nodes()
            .Single(x => x.Str("action") == "report.personal" && x.Str("recordId") == id.ToString());
        Assert.DoesNotContain("valor-del-filtro-041", entry.Json());
        Assert.Matches("^[0-9a-f]{64}$", entry["after"]!.Str("definitionSha256"));
        Assert.Equal(label, entry["after"]!.Str("label"));
        // Regresión (F-D): el de una partida se registra bajo su documento (la bitácora filtra por catálogo raíz).
        var line = (await CreateAsync(lector, "Partidas " + Guid.NewGuid().ToString("N")[..6],
            new { entity = "It41Partida", measures = new[] { new { fn = "sum", field = "Importe" } } })).Id();
        Assert.Contains((await admin.GetAsync("/api/audit", new { entity = Pedido, pageSize = 200 })).Items.Nodes(),
            x => x.Str("action") == "report.personal" && x.Str("recordId") == line.ToString());
    }

    [Fact]
    public async Task Un_reporte_guardado_no_cambia_de_catalogo()
    {
        // Regresión (revisión adversarial 041, F-C): cambiar de catálogo dejaba la definición del anterior en la bitácora del nuevo.
        using var lector = await LectorAsync();
        var label = "Catálogo fijo " + Guid.NewGuid().ToString("N")[..6];
        var id = (await CreateAsync(lector, label, Def("Zona"))).Id();
        var moved = await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = new { entity = "It41Cliente", groupBy = new[] { new { field = "Nombre" } }, measures = new[] { new { fn = "count" } } }, version = 1 });
        Assert.Equal(400, moved.Status);
        Assert.Contains("no cambia de catálogo", moved.Text);
        Assert.Equal(Pedido, await host.ScalarAsync<string>($"SELECT EntityName FROM meta.PersonalReport WHERE Id = {id}"));
    }

    [Fact]
    public async Task Version_vieja_da_409_antes_que_la_validacion()
    {
        // Regresión (revisión adversarial 041, F-F): con una versión vieja y una definición que ya no valía respondía 400.
        using var lector = await LectorAsync();
        var label = "Versión primero " + Guid.NewGuid().ToString("N")[..6];
        var id = (await CreateAsync(lector, label, Def("Zona"))).Id();
        Assert.Equal(200, (await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = Def("Region"), version = 1 })).Status);
        Assert.Equal(409, (await lector.PutAsync($"/api/my-reports/{id}", new { label, definition = Def("Interno"), version = 1 })).Status);
    }
}
