using System.Text.Json.Nodes;
using Erp.Api.Data;
using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 042 · Tableros personales y compartir sobre SQL real: solo se comparte con roles que se tienen y el reparto se
/// revalida al leer; lo compartido corre con el alcance, la empresa y los campos de quien lo ve; quien lo ve no recibe lo que
/// no puede ver; nadie cambia lo de otro; versión, topes con altas simultáneas, tope de corridas a la vez por persona y
/// bitácora en la misma transacción.
/// </summary>
public class PersonalDashboardsTests(PersonalDashboardsTests.Host host) : IClassFixture<PersonalDashboardsTests.Host>
{
    private const string Pwd = "frase larga de tableros 042";
    private const string Ventas = "IT42 Ventas";
    private const string Equipo = "IT42 Equipo";
    private const string Jefe = "IT42 Jefe";
    private const string Ana = "it42-ana";     // Ventas + Equipo, empresa principal: la dueña
    private const string Beto = "it42-beto";   // Equipo, solo la otra empresa, sin la zona Privada ni el campo Interno
    private const string Caro = "it42-caro";   // Ventas
    private const string Jaime = "it42-jaime"; // Jefe (hereda Equipo)
    private const string Tope = "it42-tope";   // Equipo
    private const string Pedido = "It42Pedido";

    public sealed class Host : ErpHost
    {
        public int AnaId { get; private set; }
        public int VentasId { get; private set; }
        public int EquipoId { get; private set; }
        public int Main { get; private set; }
        public int ClienteA { get; private set; }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
            Main = companies.First(c => c["isDefault"]!.GetValue<bool>()).Id();
            var other = (await admin.PostAsync("/api/admin/companies", new { code = "IT42-B", name = "Otra empresa 042", baseCurrency = "MXN" })).Body!.Id();
            async Task<int> RoleAsync(string name, int[]? inherits = null)
            {
                var r = await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 042", canManageModel = false, canManageUsers = false,
                    canViewAudit = false, permissions = Array.Empty<object>(), inheritsRoleIds = inherits ?? [] });
                Assert.True(r.Status == 200, r.Text);
                return r.Body!.Id();
            }
            VentasId = await RoleAsync(Ventas);
            EquipoId = await RoleAsync(Equipo);
            var jefe = await RoleAsync(Jefe, [EquipoId]);
            await RoleAsync("IT42 Ajeno");
            await admin.ApplyPlanAsync(new { description = "Spec 042 · tableros personales", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 042", name = "It42Cliente", label = "Cliente 042", labelPlural = "Clientes 042", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Ciudad", label = "Ciudad", type = "text" } } },
                new { op = "createEntity", group = "Pruebas 042", name = Pedido, label = "Pedido 042", labelPlural = "Pedidos 042", perCompany = true, fields = new object[] {
                    new { name = "Fecha", label = "Fecha", type = "date", required = true },
                    new { name = "Cliente", label = "Cliente", type = "reference", referenceEntity = "It42Cliente" },
                    new { name = "Zona", label = "Zona", type = "text", required = true },
                    new { name = "Monto", label = "Monto", type = "decimal", required = true },
                    new { name = "Interno", label = "Interno", type = "text" } } },
                new { op = "setSecurity", entity = Pedido, security = new {
                    fields = new[] { new { field = "Interno", read = new[] { "Administrador", Ventas } } },
                    records = new object[] {
                        new { roles = new[] { "Administrador", Ventas }, access = "read" },
                        new { roles = new[] { Equipo }, where = "Zona <> 'Privada'", access = "read" } } } },
                new { op = "setReports", entity = Pedido, reports = new object[] {
                    new { name = "PorZona", label = "Por zona", groupBy = new[] { new { field = "Zona" } }, measures = new[] { new { fn = "sum", field = "Monto" } }, chart = "bar" },
                    new { name = "Gerencia", label = "Gerencia", roles = new[] { Ventas }, measures = new[] { new { fn = "count" } } } } },
            } });
            async Task GrantAsync(int roleId, string name, params string[] entities) =>
                Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{roleId}", new { name, description = "Spec 042", canManageModel = false,
                    canManageUsers = false, canViewAudit = false, inheritsRoleIds = roleId == jefe ? new[] { EquipoId } : [],
                    permissions = entities.Select(entity => new { entity, canRead = true, canCreate = false, canUpdate = false, canDelete = false }).ToArray() })).Status);
            await GrantAsync(VentasId, Ventas, Pedido, "It42Cliente");
            await GrantAsync(EquipoId, Equipo, Pedido);
            foreach (var (user, roles, cos) in new[] {
                (Ana, new[] { VentasId, EquipoId }, new[] { Main }), (Beto, new[] { EquipoId }, new[] { other }), (Caro, new[] { VentasId }, new[] { Main }),
                (Jaime, new[] { jefe }, new[] { Main }), (Tope, new[] { EquipoId }, new[] { Main }) })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = user, displayName = user, password = Pwd, roleIds = roles, companyIds = cos });
                Assert.True(u.Status == 201, u.Text);
                if (user == Ana) AnaId = u.Body!.Id();
            }
            ClienteA = (await admin.CreateAsync("It42Cliente", new { Nombre = "Cliente A" })).Id();
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-01", Cliente = ClienteA, Zona = "Norte", Monto = 100, Interno = "nota" });
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-02", Zona = "Sur", Monto = 50 });
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-03", Zona = "Privada", Monto = 1000 });
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = other })).Status);
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-04", Zona = "Norte", Monto = 7 });
            await admin.CreateAsync(Pedido, new { Fecha = "2026-10-05", Zona = "Privada", Monto = 3 });
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = Main })).Status);
        }
    }

    private Task<Client> As(string user) => host.LoginAsync(user, Pwd);

    private static object Def(string group, object? filters = null) => new
    {
        entity = Pedido, groupBy = new[] { new { field = group } }, measures = new[] { new { fn = "sum", field = "Monto" } }, chart = "bar", filters,
    };

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<JsonNode> CreateReportAsync(Client c, string label, object definition)
    {
        var res = await c.PostAsync("/api/my-reports", new { label, definition });
        Assert.True(res.Status == 201, res.ToString());
        return res.Body!;
    }

    private static async Task<JsonNode> CreateBoardAsync(Client c, string label, params object[] widgets)
    {
        var res = await c.PostAsync("/api/my-dashboards", new { label, widgets });
        Assert.True(res.Status == 201, res.ToString());
        return res.Body!;
    }

    private static async Task<int> ShareAsync(Client c, string kind, JsonNode item, params string[] roles)
    {
        var res = await c.PutAsync($"/api/{kind}/{item.Id()}/share", new { roles, version = item["version"]!.GetValue<int>() });
        Assert.True(res.Status == 200, res.ToString());
        return res.Body!["version"]!.GetValue<int>();
    }

    private static async Task<List<string>> ExploreRowsAsync(Client c, JsonNode definition)
    {
        var res = await c.PostAsync("/api/my-reports/explore", new { definition });
        Assert.True(res.Status == 200, res.ToString());
        return Rows(res.Body!);
    }

    private static List<string> Rows(JsonNode r) => r.Arr("rows").Nodes()
        .Select(row => $"{row.Str("g0")}={row["m0"]!.GetValue<decimal>():0.##}").ToList();

    private static IEnumerable<int> Ids(Res list) => list.Array.Nodes().Select(x => x.Id());

    private async Task SetRolesAsync(string username, int[] roleIds, bool active = true)
    {
        using var admin = await host.AdminAsync();
        var user = (await admin.GetAsync("/api/admin/users")).Array.Nodes().Single(u => u.Str("username") == username);
        var res = await admin.PutAsync($"/api/admin/users/{user.Id()}", new { displayName = username, isActive = active, roleIds,
            companyIds = new[] { host.Main } });
        Assert.True(res.Status == 200, res.ToString());
    }

    [Fact]
    public async Task Solo_comparte_con_roles_que_tiene()
    {
        using var beto = await As(Beto);
        var mine = await CreateReportAsync(beto, Unique("De Beto"), Def("Zona"));
        var other = await beto.PutAsync($"/api/my-reports/{mine.Id()}/share", new { roles = new[] { Ventas }, version = 1 });
        var missing = await beto.PutAsync($"/api/my-reports/{mine.Id()}/share", new { roles = new[] { "IT42 No existe" }, version = 1 });
        Assert.Equal(400, other.Status);
        Assert.Equal(400, missing.Status);
        // Un rol ajeno y uno inexistente responden igual (cambia solo el nombre que se pidió).
        Assert.Contains($"El rol '{Ventas}' no existe o no lo tienes.", other.Text);
        Assert.Contains("El rol 'IT42 No existe' no existe o no lo tienes.", missing.Text);
        Assert.Equal(400, (await beto.PutAsync($"/api/my-reports/{mine.Id()}/share", new { roles = new string?[] { null }, version = 1 })).Status);
        Assert.Equal(400, (await beto.PutAsync($"/api/my-reports/{mine.Id()}/share", new { version = 1 })).Status);
        // Nada cambió: ni versión ni reparto.
        var get = (await beto.GetAsync($"/api/my-reports/{mine.Id()}")).Body!;
        Assert.Equal(1, get["version"]!.GetValue<int>());
        Assert.Empty(get.Arr("roles"));
        Assert.Equal([Equipo], get.Arr("shareableRoles").Nodes().Select(n => n.GetValue<string>()));

        // Un rol heredado cuenta: Jaime (Jefe hereda Equipo) comparte con Equipo y Beto lo ve; Jaime ve lo compartido con Equipo.
        using var jaime = await As(Jaime);
        Assert.Contains(Equipo, (await jaime.GetAsync("/api/auth/me")).Body!.Arr("roles").Nodes().Select(n => n.GetValue<string>()));
        var heredado = await CreateReportAsync(jaime, Unique("De Jaime"), Def("Zona"));
        await ShareAsync(jaime, "my-reports", heredado, Equipo);
        Assert.Contains(heredado.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
        await ShareAsync(beto, "my-reports", mine, Equipo);
        Assert.Contains(mine.Id(), Ids(await jaime.GetAsync("/api/my-reports/shared")));
        // Lo propio no aparece en «compartidos conmigo».
        Assert.DoesNotContain(mine.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
        Assert.True((await beto.GetAsync("/api/my-reports")).Array.Nodes().Single(r => r.Id() == mine.Id())["shared"]!.GetValue<bool>());

        // El superusuario puede compartir con cualquier rol, aunque no lo tenga.
        using var admin = await host.AdminAsync();
        var suyo = await CreateReportAsync(admin, Unique("Del admin"), Def("Zona"));
        await ShareAsync(admin, "my-reports", suyo, Ventas);
        using var caro = await As(Caro);
        Assert.Contains(suyo.Id(), Ids(await caro.GetAsync("/api/my-reports/shared")));
    }

    [Fact]
    public async Task Compartido_corre_con_quien_lo_ve()
    {
        using var ana = await As(Ana);
        using var beto = await As(Beto);
        var report = await CreateReportAsync(ana, Unique("Zonas de Ana"), Def("Zona"));
        Assert.Equal(404, (await beto.GetAsync($"/api/my-reports/{report.Id()}")).Status); // aún no compartido
        await ShareAsync(ana, "my-reports", report, Equipo);

        var shared = (await beto.GetAsync("/api/my-reports/shared")).Array.Nodes().Single(r => r.Id() == report.Id());
        Assert.Equal(Ana, shared.Str("owner"));
        Assert.True(shared["available"]!.GetValue<bool>());
        var opened = (await beto.GetAsync($"/api/my-reports/{report.Id()}")).Body!;
        Assert.False(opened["owned"]!.GetValue<bool>());
        Assert.Null(opened["roles"]);
        Assert.Null(opened["shareableRoles"]);
        // Ana ve las tres zonas de su empresa; Beto, con la misma definición, solo lo de su empresa y sin la zona Privada.
        Assert.Equal(["Norte=100", "Privada=1000", "Sur=50"], await ExploreRowsAsync(ana, opened["definition"]!));
        Assert.Equal(["Norte=7"], await ExploreRowsAsync(beto, opened["definition"]!));
    }

    [Fact]
    public async Task Compartido_no_entrega_lo_que_quien_lo_ve_no_puede_ver()
    {
        using var ana = await As(Ana);
        using var beto = await As(Beto);
        // Un campo que Beto no ve (Interno) y un catálogo que no lee (Cliente.Ciudad; su campo para mostrar sí, como en el buscador).
        var hidden = await CreateReportAsync(ana, Unique("Por interno"), Def("Interno"));
        var hop = await CreateReportAsync(ana, Unique("Por cliente"), Def("Cliente.Ciudad"));
        await ShareAsync(ana, "my-reports", hidden, Equipo);
        await ShareAsync(ana, "my-reports", hop, Equipo);
        foreach (var r in new[] { hidden, hop })
        {
            var res = await beto.GetAsync($"/api/my-reports/{r.Id()}");
            Assert.Equal(200, res.Status);
            Assert.Null(res.Body!["definition"]);
            Assert.False(res.Body!["available"]!.GetValue<bool>());
            Assert.Equal([PersonalReportService.NotForYou], res.Body!.Arr("problems").Nodes().Select(n => n.GetValue<string>()));
            Assert.DoesNotContain("Interno", res.Text);
            Assert.DoesNotContain("Ciudad", res.Text);
            var listed = (await beto.GetAsync("/api/my-reports/shared")).Array.Nodes().Single(x => x.Id() == r.Id());
            Assert.False(listed["available"]!.GetValue<bool>());
            Assert.Null(listed["entity"]);
            Assert.Null(listed["entityLabel"]);
        }
        // La etiqueta del valor de un filtro (el nombre de un registro que Beto no lee) no le llega; el valor sí.
        var filtered = await CreateReportAsync(ana, Unique("Con filtro"),
            Def("Zona", new[] { new { field = "Cliente", op = "eq", value = (object)host.ClienteA, label = "Cliente A" } }));
        Assert.Contains("Cliente A", (await ana.GetAsync($"/api/my-reports/{filtered.Id()}")).Text);
        await ShareAsync(ana, "my-reports", filtered, Equipo);
        var seen = await beto.GetAsync($"/api/my-reports/{filtered.Id()}");
        Assert.DoesNotContain("Cliente A", seen.Text);
        Assert.Null(seen.Body!["definition"]!.Arr("filters")[0]!["label"]);
        Assert.Equal(host.ClienteA, seen.Body!["definition"]!.Arr("filters")[0]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reparto_se_revalida_si_el_dueno_pierde_el_rol()
    {
        using var ana = await As(Ana);
        var report = await CreateReportAsync(ana, Unique("Revalidado"), Def("Zona"));
        await ShareAsync(ana, "my-reports", report, Equipo);
        var board = await CreateBoardAsync(ana, Unique("Tablero revalidado"), new { type = "chart", entity = Pedido, report = "PorZona" });
        await ShareAsync(ana, "my-dashboards", board, Equipo);
        using (var beto = await As(Beto))
        {
            Assert.Contains(report.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
            Assert.Contains(board.Id(), (await beto.GetAsync("/api/my-dashboards")).Body!.Arr("shared").Nodes().Select(x => x.Id()));
        }
        try
        {
            // Ana deja de tener Equipo: lo que repartió por él deja de verse sin que nadie lo edite.
            await SetRolesAsync(Ana, [host.VentasId]);
            using (var beto = await As(Beto))
            {
                Assert.DoesNotContain(report.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
                Assert.Equal(404, (await beto.GetAsync($"/api/my-reports/{report.Id()}")).Status);
                Assert.DoesNotContain(board.Id(), (await beto.GetAsync("/api/my-dashboards")).Body!.Arr("shared").Nodes().Select(x => x.Id()));
                Assert.Equal(404, (await beto.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
            }
            // Vuelve a tenerlo: el reparto vuelve a contar. Inactiva: deja de contar.
            await SetRolesAsync(Ana, [host.VentasId, host.EquipoId]);
            using (var beto = await As(Beto)) Assert.Contains(report.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
            await SetRolesAsync(Ana, [host.VentasId, host.EquipoId], active: false);
            using (var beto = await As(Beto))
            {
                Assert.DoesNotContain(report.Id(), Ids(await beto.GetAsync("/api/my-reports/shared")));
                Assert.Equal(404, (await beto.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
            }
        }
        finally
        {
            await SetRolesAsync(Ana, [host.VentasId, host.EquipoId]);
        }
        using (var beto = await As(Beto)) Assert.Equal(200, (await beto.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
    }

    [Fact]
    public async Task Reparto_con_herencia_en_rombo_se_revalida_a_tiempo()
    {
        // Revisión adversarial 042 (F-A): la revalidación recorría con una CTE recursiva, sin deduplicar, la herencia de TODAS las
        // personas en cada lectura (en rombo, 10^5 caminos por rol asignado). Ahora se expande solo la de los dueños candidatos,
        // deduplicada (RoleGraph.Expand). Con la CTE anterior esta prueba también pasa (SQL Server la resolvió en menos de 1 s con
        // 8 personas): protege que la herencia en rombo siga contando y su tiempo, no reproduce la lentitud (R-042-1).
        const string prefix = "IT42 Rombo";
        await host.ExecAsync($"""
            IF NOT EXISTS (SELECT 1 FROM meta.Role WHERE Name LIKE N'{prefix}%')
            BEGIN
                DECLARE @n int = 0, @k int;
                WHILE @n < 6 BEGIN SET @k = 0; WHILE @k < 10 BEGIN
                    INSERT meta.Role (Name, Description) VALUES (CONCAT(N'{prefix} ', @n, N'.', @k), N'Spec 042'); SET @k += 1; END; SET @n += 1; END;
                INSERT meta.RoleInherit (RoleId, ParentRoleId)
                SELECT c.Id, p.Id FROM meta.Role c JOIN meta.Role p
                  ON p.Name LIKE N'{prefix} %' AND c.Name LIKE N'{prefix} %'
                 AND CAST(SUBSTRING(p.Name, LEN(N'{prefix}') + 2, 1) AS int) = CAST(SUBSTRING(c.Name, LEN(N'{prefix}') + 2, 1) AS int) + 1;
            END
            """);
        var top = await host.ScalarAsync<int>($"SELECT Id FROM meta.Role WHERE Name = N'{prefix} 0.0'");
        var leaf = $"{prefix} 5.0";
        var leafId = await host.ScalarAsync<int>($"SELECT Id FROM meta.Role WHERE Name = N'{leaf}'");
        using var admin = await host.AdminAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        // Varias personas con el rol de arriba (cada una hereda los 60 roles por 10^5 caminos) y una con la hoja.
        var owners = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var name = $"it42-rombo-{suffix}-{i}";
            Assert.Equal(201, (await admin.PostAsync("/api/admin/users", new { username = name, displayName = name, password = Pwd,
                roleIds = new[] { top, host.EquipoId }, companyIds = new[] { host.Main } })).Status);
            owners.Add(name);
        }
        var viewer = $"it42-hoja-{suffix}";
        Assert.Equal(201, (await admin.PostAsync("/api/admin/users", new { username = viewer, displayName = viewer, password = Pwd,
            roleIds = new[] { leafId, host.EquipoId }, companyIds = new[] { host.Main } })).Status);
        var ids = new List<int>();
        foreach (var o in owners.Take(3))
        {
            using var c = await As(o);
            var r = await CreateReportAsync(c, Unique("Rombo"), Def("Zona"));
            await ShareAsync(c, "my-reports", r, leaf); // heredado a 5 niveles: lo tiene
            ids.Add(r.Id());
        }
        using var v = await As(viewer);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var shared = await v.GetAsync("/api/my-reports/shared");
        watch.Stop();
        Assert.Equal(200, shared.Status);
        Assert.Subset(Ids(shared).ToHashSet(), ids.ToHashSet());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"la lista de compartidos tardó {watch.Elapsed}");
        // Y la revalidación sigue la herencia: sin el rol de arriba, el dueño ya no tiene la hoja.
        await SetRolesAsync(owners[0], [host.EquipoId]);
        Assert.DoesNotContain(ids[0], Ids(await v.GetAsync("/api/my-reports/shared")));
        Assert.Contains(ids[1], Ids(await v.GetAsync("/api/my-reports/shared")));
    }

    [Fact]
    public async Task Nadie_cambia_ni_comparte_lo_de_otro()
    {
        using var ana = await As(Ana);
        using var beto = await As(Beto);
        using var caro = await As(Caro);
        var report = await CreateReportAsync(ana, Unique("Ajeno"), Def("Zona"));
        var board = await CreateBoardAsync(ana, Unique("Ajeno"), new { type = "chart", entity = Pedido, report = "PorZona" });
        var v = await ShareAsync(ana, "my-reports", report, Equipo);
        var bv = await ShareAsync(ana, "my-dashboards", board, Equipo);
        // Beto lo ve compartido, pero no lo cambia, no lo borra ni lo comparte: 404 igual que lo inexistente.
        foreach (var (kind, id, version, body) in new[] {
            ("my-reports", report.Id(), v, (object)new { label = "Mío", definition = Def("Zona"), version = v }),
            ("my-dashboards", board.Id(), bv, new { label = "Mío", widgets = new[] { new { type = "chart", entity = Pedido, report = "PorZona" } }, version = bv }) })
        {
            Assert.Equal(200, (await beto.GetAsync($"/api/{kind}/{id}")).Status);
            Assert.Equal(404, (await beto.PutAsync($"/api/{kind}/{id}", body)).Status);
            Assert.Equal(404, (await beto.PutAsync($"/api/{kind}/{id}/share", new { roles = new[] { Equipo }, version })).Status);
            Assert.Equal(404, (await beto.DeleteAsync($"/api/{kind}/{id}?version={version}")).Status);
            // Caro (sin Equipo) ni lo ve.
            Assert.Equal(404, (await caro.GetAsync($"/api/{kind}/{id}")).Status);
            Assert.Equal(404, (await caro.GetAsync($"/api/{kind}/999999")).Status);
        }
        Assert.Equal(v, (await ana.GetAsync($"/api/my-reports/{report.Id()}")).Body!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Tablero_compartido_corre_cada_tarjeta_con_quien_lo_ve()
    {
        using var ana = await As(Ana);
        using var beto = await As(Beto);
        var zonas = await CreateReportAsync(ana, Unique("Zonas"), Def("Zona"));
        var interno = await CreateReportAsync(ana, Unique("Interno"), Def("Interno"));
        var compartido = await CreateReportAsync(ana, Unique("Compartido aparte"), Def("Zona"));
        await ShareAsync(ana, "my-reports", compartido, Equipo);
        var board = await CreateBoardAsync(ana, Unique("Tablero de Ana"),
            new { type = "chart", entity = Pedido, report = "PorZona" },
            new { type = "chart", personalReport = zonas.Id(), label = "Mis zonas" },
            new { type = "table", personalReport = interno.Id() },
            new { type = "kpi", entity = Pedido, report = "Gerencia" },
            new { type = "table", personalReport = compartido.Id() });
        var own = (await ana.GetAsync($"/api/my-dashboards/{board.Id()}")).Body!;
        Assert.True(own["owned"]!.GetValue<bool>());
        Assert.Equal(5, own.Arr("cards").Count);
        Assert.All(own.Arr("cards").Nodes(), c => Assert.True(c["available"]!.GetValue<bool>()));
        Assert.Equal(["Norte=100", "Privada=1000", "Sur=50"], Rows(own.Arr("cards")[1]!["result"]!));
        Assert.Equal($"/explore/{zonas.Id()}", own.Arr("cards")[1]!.Str("link"));
        Assert.Equal(5, own.Arr("widgets").Count);

        await ShareAsync(ana, "my-dashboards", (await ana.GetAsync($"/api/my-dashboards/{board.Id()}")).Body!, Equipo);
        var seen = await beto.GetAsync($"/api/my-dashboards/{board.Id()}");
        Assert.Equal(200, seen.Status);
        var b = seen.Body!;
        Assert.False(b["owned"]!.GetValue<bool>());
        Assert.Null(b["widgets"]); // la definición solo al dueño
        Assert.Null(b["roles"]);
        // Sin la tarjeta de Interno (campo oculto) ni la de Gerencia (reporte de otro rol): no se nombran.
        var cards = b.Arr("cards").Nodes().ToList();
        Assert.Equal(["Por zona", "Mis zonas", compartido.Str("label")], cards.Select(c => c.Str("label")));
        Assert.All(cards, c => Assert.Equal(["Norte=7"], Rows(c["result"]!))); // su empresa, sin la zona Privada
        // Solo enlaza a lo que puede abrir: el del modelo y el reporte compartido aparte; no el que va solo con el tablero.
        Assert.Equal($"/reports/{Pedido}/PorZona", cards[0].Str("link"));
        Assert.Null(cards[1]["link"]);
        Assert.Null(cards[1]["personalReport"]);
        Assert.Equal($"/explore/{compartido.Id()}", cards[2].Str("link"));
        Assert.DoesNotContain("Interno", seen.Text);
        Assert.DoesNotContain("Gerencia", seen.Text);
        // Y el reporte que va solo con el tablero no se abre por su id.
        Assert.Equal(404, (await beto.GetAsync($"/api/my-reports/{zonas.Id()}")).Status);
    }

    [Fact]
    public async Task Tablero_valida_tarjetas_y_marca_las_que_no_corren()
    {
        using var ana = await As(Ana);
        using var beto = await As(Beto);
        var bad = await ana.PostAsync("/api/my-dashboards", new { label = Unique("Malo"), widgets = new object[] {
            new { type = "chart", entity = Pedido, report = "Gerencia" },
            new { type = "kpi", entity = Pedido, report = "NoExiste" } } });
        Assert.Equal(400, bad.Status);
        Assert.Contains("Tarjeta 1: una gráfica usa un reporte con agrupación.", bad.Text);
        Assert.Contains("Tarjeta 2: el reporte no existe o no puedes verlo.", bad.Text);
        // Un reporte personal de otra persona no es una fuente (aunque esté compartido con ella).
        var deBeto = await CreateReportAsync(beto, Unique("De Beto"), Def("Zona"));
        await ShareAsync(beto, "my-reports", deBeto, Equipo);
        var ajeno = await ana.PostAsync("/api/my-dashboards", new { label = Unique("Ajeno"), widgets = new[] { new { type = "chart", personalReport = deBeto.Id() } } });
        Assert.Equal(400, ajeno.Status);
        Assert.Contains("Tarjeta 1: el reporte personal ya no existe.", ajeno.Text);
        Assert.Equal(400, (await ana.PostAsync("/api/my-dashboards", new { label = "", widgets = new[] { new { type = "chart", entity = Pedido, report = "PorZona" } } })).Status);
        Assert.Equal(400, (await ana.PostAsync("/api/my-dashboards", new { label = Unique("Sin"), widgets = Array.Empty<object>() })).Status);

        // Una tarjeta que dejó de correr para su dueña se marca con el motivo; el resto sigue.
        var temp = await CreateReportAsync(ana, Unique("Temporal"), Def("Zona"));
        var board = await CreateBoardAsync(ana, Unique("Con baja"),
            new { type = "chart", personalReport = temp.Id() }, new { type = "chart", entity = Pedido, report = "PorZona" });
        Assert.Equal(204, (await ana.DeleteAsync($"/api/my-reports/{temp.Id()}?version=1")).Status);
        var cards = (await ana.GetAsync($"/api/my-dashboards/{board.Id()}")).Body!.Arr("cards").Nodes().ToList();
        Assert.False(cards[0]["available"]!.GetValue<bool>());
        Assert.Equal(["El reporte personal ya no existe."], cards[0].Arr("problems").Nodes().Select(n => n.GetValue<string>()));
        Assert.Null(cards[0]["result"]);
        Assert.True(cards[1]["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Tablero_con_version_vieja_da_409()
    {
        using var ana = await As(Ana);
        var label = Unique("Versionado");
        var board = await CreateBoardAsync(ana, label, new { type = "chart", entity = Pedido, report = "PorZona" });
        var widgets = new[] { new { type = "table", entity = Pedido, report = "PorZona" } };
        Assert.Equal(200, (await ana.PutAsync($"/api/my-dashboards/{board.Id()}", new { label, widgets, version = 1 })).Status);
        Assert.Equal(409, (await ana.PutAsync($"/api/my-dashboards/{board.Id()}", new { label, widgets, version = 1 })).Status);
        Assert.Equal(409, (await ana.PutAsync($"/api/my-dashboards/{board.Id()}", new { label, widgets })).Status);
        // La versión se compara antes de validar: una versión vieja con tarjetas inválidas es 409, no 400.
        Assert.Equal(409, (await ana.PutAsync($"/api/my-dashboards/{board.Id()}", new { label, widgets = Array.Empty<object>(), version = 1 })).Status);
        Assert.Equal(409, (await ana.DeleteAsync($"/api/my-dashboards/{board.Id()}?version=1")).Status);
        Assert.Equal(204, (await ana.DeleteAsync($"/api/my-dashboards/{board.Id()}?version=2")).Status);
        Assert.Equal(404, (await ana.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
    }

    [Fact]
    public async Task Compartir_con_version_vieja_da_409()
    {
        using var ana = await As(Ana);
        var report = await CreateReportAsync(ana, Unique("Para compartir"), Def("Zona"));
        Assert.Equal(200, (await ana.PutAsync($"/api/my-reports/{report.Id()}/share", new { roles = new[] { Equipo }, version = 1 })).Status);
        // Compartir sube la versión: guardar con la que se abrió antes (otra pestaña) da 409, y compartir otra vez también.
        Assert.Equal(409, (await ana.PutAsync($"/api/my-reports/{report.Id()}", new { label = report.Str("label"), definition = Def("Zona"), version = 1 })).Status);
        Assert.Equal(409, (await ana.PutAsync($"/api/my-reports/{report.Id()}/share", new { roles = Array.Empty<string>(), version = 1 })).Status);
        var both = await Concurrency.Concurrently(5, _ => ana.PutAsync($"/api/my-reports/{report.Id()}/share", new { roles = Array.Empty<string>(), version = 2 }));
        Invariant.Statuses(both, 200, 409, 409, 409, 409);
        var now = (await ana.GetAsync($"/api/my-reports/{report.Id()}")).Body!;
        Assert.Equal(3, now["version"]!.GetValue<int>());
        Assert.Empty(now.Arr("roles"));
    }

    [Fact]
    public async Task Veinte_altas_simultaneas_no_pasan_el_tope_de_tableros()
    {
        using var tope = await As(Tope);
        var widgets = new[] { new { type = "chart", entity = Pedido, report = "PorZona" } };
        var existing = (await tope.GetAsync("/api/my-dashboards")).Body!.Arr("mine").Count;
        for (var i = existing; i < PersonalDashboards.MaxPerUser - 5; i++) await CreateBoardAsync(tope, $"Tope {i}", widgets);
        var results = await Concurrency.Concurrently(10, i => tope.PostAsync("/api/my-dashboards", new { label = $"Simultáneo {i}", widgets }));
        Invariant.Statuses(results, 201, 201, 201, 201, 201, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains($"Máximo {PersonalDashboards.MaxPerUser}", r.Text));
        var count = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.PersonalDashboard d JOIN meta.[User] u ON u.Id = d.UserId WHERE u.Username = '{Tope}'");
        Assert.Equal(PersonalDashboards.MaxPerUser, count);

        // Nombre único con altas simultáneas (otra persona, sin tope cerca).
        using var caro = await As(Caro);
        var label = Unique("Mismo nombre");
        var same = await Concurrency.Concurrently(10, _ => caro.PostAsync("/api/my-dashboards", new { label, widgets }));
        Invariant.Statuses(same, 201, 400, 400, 400, 400, 400, 400, 400, 400, 400);
        Assert.All(same.Where(r => r.Status == 400), r => Assert.Contains("Ya tienes un tablero con ese nombre", r.Text));
    }

    [Fact]
    public async Task Corridas_simultaneas_por_persona_tienen_tope()
    {
        using var caro = await As(Caro);
        var board = await CreateBoardAsync(caro, Unique("Costoso"), new { type = "chart", entity = Pedido, report = "PorZona" });
        Task<Res>[] running;
        // Otra conexión retiene las filas del catálogo: cada corrida queda detenida dentro del tope.
        await using (var held = await host.HoldAsync($"UPDATE app.[{Pedido}] SET Monto = Monto"))
        {
            running = Enumerable.Range(0, 4).Select(_ => caro.PostAsync("/api/my-reports/explore", new { definition = Def("Zona") })).ToArray();
            await Concurrency.WaitForAsync(() => host.ScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()"), n => n >= 4, "cuatro corridas detenidas");
            // La quinta, por cualquiera de las rutas costosas, responde 429 sin esperar.
            var fifth = await caro.PostAsync("/api/my-reports/explore", new { definition = Def("Zona") });
            Assert.Equal(429, fifth.Status);
            Assert.Contains("demasiadas consultas en curso", fifth.Text);
            Assert.Equal(429, (await caro.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
            Assert.Equal(429, (await caro.PostAsync("/api/my-reports/explore/export", new { definition = Def("Zona"), format = "csv" })).Status);
        }
        Assert.All(await Task.WhenAll(running), r => Assert.Equal(200, r.Status));
        // Al terminar se sueltan: vuelve a correr.
        Assert.Equal(200, (await caro.GetAsync($"/api/my-dashboards/{board.Id()}")).Status);
        Assert.Equal(200, (await caro.PostAsync("/api/my-reports/explore", new { definition = Def("Zona") })).Status);
    }

    [Fact]
    public async Task Bitacora_de_tableros_sin_definicion_y_en_la_transaccion()
    {
        using var ana = await As(Ana);
        using var admin = await host.AdminAsync();
        var label = Unique("Auditado");
        var board = await CreateBoardAsync(ana, label, new { type = "chart", entity = Pedido, report = "PorZona", label = "título-secreto-042" });
        await ShareAsync(ana, "my-dashboards", board, Equipo);
        var entries = (await admin.GetAsync("/api/audit", new { entity = "PersonalDashboard", pageSize = 200 })).Items.Nodes()
            .Where(x => x.Str("action") == "dashboard.personal" && x.Str("recordId") == board.Id().ToString()).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.DoesNotContain("título-secreto-042", e.Json()));
        var shared = entries.Single(e => e["before"] is not null);
        Assert.Equal([Equipo], shared["after"]!.Arr("roles").Nodes().Select(n => n.GetValue<string>()));
        Assert.Matches("^[0-9a-f]{64}$", shared["after"]!.Str("definitionSha256"));
        // El reparto de un reporte va en su bitácora, con los roles.
        var report = await CreateReportAsync(ana, Unique("Auditado"), Def("Zona"));
        await ShareAsync(ana, "my-reports", report, Equipo);
        Assert.Contains((await admin.GetAsync("/api/audit", new { entity = Pedido, pageSize = 200 })).Items.Nodes(),
            x => x.Str("action") == "report.personal" && x.Str("recordId") == report.Id().ToString() && x["after"]?["roles"] is JsonArray { Count: 1 });

        // Si la bitácora falla, ni el tablero ni el reparto quedan (una transacción).
        var failed = Unique("Sin auditoria");
        await using (await host.FailWhenAsync("meta.AuditLog", $"Action = 'dashboard.personal' AND AfterJson LIKE N'%{failed}%'"))
            Assert.Equal(500, (await ana.PostAsync("/api/my-dashboards", new { label = failed, widgets = new[] { new { type = "chart", entity = Pedido, report = "PorZona" } } })).Status);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.PersonalDashboard WHERE Label = N'{failed}'"));
        var other = await CreateBoardAsync(ana, Unique("Reparto fallido"), new { type = "chart", entity = Pedido, report = "PorZona" });
        await using (await host.FailWhenAsync("meta.AuditLog", $"Action = 'dashboard.personal' AND RecordId = N'{other.Id()}' AND BeforeJson IS NOT NULL"))
            Assert.Equal(500, (await ana.PutAsync($"/api/my-dashboards/{other.Id()}/share", new { roles = new[] { Equipo }, version = 1 })).Status);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.PersonalDashboardShare WHERE DashboardId = {other.Id()}"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT Version FROM meta.PersonalDashboard WHERE Id = {other.Id()}"));
    }
}
