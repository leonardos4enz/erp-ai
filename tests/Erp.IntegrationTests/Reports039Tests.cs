using System.Text;
using System.Text.Json.Nodes;
using Erp.Api.Data;
using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 039 · Reportes 2 sobre SQL real: agrupar a dos saltos con el alcance (registro y empresa) de cada catálogo del camino,
/// campo oculto en el camino, medidas calculadas en filas y total, semana y trimestre, comparación del indicador con el
/// alcance de quien lo ve, y la exportación y el tablero iguales a la pantalla.
/// </summary>
public class Reports039Tests(Reports039Tests.Host host) : IClassFixture<Reports039Tests.Host>
{
    private const string Pwd = "frase larga de reportes 039";
    private const string Group = "Pruebas 039";
    private const string Role = "IT39 Lector";
    private const string Lector = "it39-lector";
    private const string Partida = "It39Partida";
    private const string SinCategorias = "it39-sin-categorias";
    private const string RoleSinCategorias = "IT39 Sin categorías";

    public sealed class Host : ErpHost
    {
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var companies = (await admin.GetAsync("/api/admin/companies")).Array.Nodes().ToList();
            var main = companies.First(c => c["isDefault"]!.GetValue<bool>()).Id();
            var other = (await admin.PostAsync("/api/admin/companies", new { code = "IT39-B", name = "Otra empresa 039", baseCurrency = "MXN" })).Body!.Id();
            var role = await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 039", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() });
            Assert.True(role.Status == 200, role.Text);

            var month = new { field = "Pedido.Fecha", period = "month" };
            await admin.ApplyPlanAsync(new { description = "Spec 039 · reportes a dos saltos", operations = new object[] {
                new { op = "createEntity", group = Group, name = "It39Categoria", label = "Categoría 039", labelPlural = "Categorías 039", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Publica", label = "Pública", type = "boolean" } } },
                new { op = "createEntity", group = Group, name = "It39Marca", label = "Marca 039", labelPlural = "Marcas 039", displayField = "Nombre", perCompany = true, fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "It39Producto", label = "Producto 039", labelPlural = "Productos 039", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Activo", label = "Activo", type = "boolean" },
                    new { name = "Categoria", label = "Categoría", type = "reference", referenceEntity = "It39Categoria" },
                    new { name = "CategoriaInterna", label = "Categoría interna", type = "reference", referenceEntity = "It39Categoria" },
                    new { name = "Marca", label = "Marca", type = "reference", referenceEntity = "It39Marca" } } },
                new { op = "createEntity", group = Group, name = "It39Pedido", label = "Pedido 039", labelPlural = "Pedidos 039", fields = new object[] {
                    new { name = "Fecha", label = "Fecha", type = "date", required = true },
                    new { name = "Cliente", label = "Cliente", type = "text", required = true } } },
                new { op = "createEntity", name = Partida, label = "Partida 039", labelPlural = "Partidas 039", fields = new object[] {
                    new { name = "Pedido", label = "Pedido", type = "parent", referenceEntity = "It39Pedido" },
                    new { name = "Producto", label = "Producto", type = "reference", referenceEntity = "It39Producto", required = true },
                    new { name = "Importe", label = "Importe", type = "decimal", required = true },
                    new { name = "Costo", label = "Costo", type = "decimal", required = true } } },
                new { op = "addField", entity = "It39Pedido", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = Partida } },
                new { op = "setSecurity", entity = "It39Categoria", security = new { records = new[] { new { roles = new[] { Role }, where = "Publica", access = "read" } } } },
                new { op = "setSecurity", entity = "It39Producto", security = new {
                    fields = new[] { new { field = "CategoriaInterna", read = new[] { "Administrador" } } },
                    records = new[] { new { roles = new[] { Role }, where = "Activo", access = "read" } } } },
                new { op = "setSecurity", entity = "It39Pedido", security = new { records = new[] { new { roles = new[] { Role }, where = "Cliente <> 'Privado'", access = "read" } } } },
                new { op = "setReports", entity = Partida, reports = new object[] {
                    new { name = "PorCategoria", label = "Por categoría", groupBy = new[] { new { field = "Producto.Categoria.Nombre" } },
                        measures = new[] { new { fn = "sum", field = "Importe" } }, chart = "column" },
                    new { name = "PorMarca", label = "Por marca", groupBy = new[] { new { field = "Producto.Marca.Nombre" } }, measures = new[] { new { fn = "sum", field = "Importe" } } },
                    new { name = "Interna", label = "Por categoría interna", groupBy = new[] { new { field = "Producto.CategoriaInterna.Nombre" } }, measures = new[] { new { fn = "count" } } },
                    new { name = "Margen", label = "Margen por mes", groupBy = new[] { month }, measures = new object[] {
                        new { fn = "sum", field = "Importe", name = "Ventas" }, new { fn = "sum", field = "Costo", name = "Costo" },
                        new { fn = "calc", expr = "ROUND((Ventas - Costo) / Ventas * 100, 1)", label = "Margen %", scale = 1 } },
                        chart = "combo", chartOptions = new { measures = new[] { 0, 1 }, lines = new[] { 1 }, colors = new[] { "azul", "naranja" } } },
                    new { name = "Semanas", label = "Por semana", groupBy = new[] { new { field = "Pedido.Fecha", period = "week" } }, measures = new[] { new { fn = "count" } } },
                    new { name = "Trimestres", label = "Por trimestre", groupBy = new[] { new { field = "Pedido.Fecha", period = "quarter" } }, measures = new[] { new { fn = "sum", field = "Importe" } } },
                    new { name = "KpiMes", label = "Ventas del mes", groupBy = new[] { month }, measures = new[] { new { fn = "sum", field = "Importe" } }, chart = "kpi" },
                    new { name = "Mensual", label = "Ventas por mes", groupBy = new[] { month }, measures = new[] { new { fn = "sum", field = "Importe" } }, chart = "line" },
                    new { name = "CategoriaPublica", label = "Por categoría pública", groupBy = new[] { new { field = "Producto.Categoria.Publica" } }, measures = new[] { new { fn = "count" } } },
                    new { name = "Promedio", label = "Promedio por mes", groupBy = new[] { month }, measures = new object[] {
                        new { fn = "avg", field = "Importe", name = "Prom" }, new { fn = "calc", expr = "Prom * 3", label = "Triple" } } } } },
                new { op = "setReports", entity = "It39Producto", reports = new object[] {
                    new { name = "PorCategoria", label = "Productos por categoría", groupBy = new[] { new { field = "Categoria" } }, measures = new[] { new { fn = "count" } } } } },
                new { op = "setDashboard", dashboard = new { name = "It39Tablero", label = "Tablero 039", widgets = new object[] {
                    new { type = "kpi", entity = Partida, report = "Mensual" },
                    new { type = "chart", entity = Partida, report = "PorCategoria" },
                    new { type = "table", entity = Partida, report = "Margen" } } } },
            } });
            var perms = new[] { "It39Pedido", "It39Producto", "It39Categoria", "It39Marca" }
                .Select(entity => new { entity, canRead = true, canCreate = false, canUpdate = false, canDelete = false }).ToArray();
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{role.Body!.Id()}", new { name = Role, description = "Spec 039", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            var u = await admin.PostAsync("/api/admin/users", new { username = Lector, displayName = "Lector 039", password = Pwd,
                roleIds = new[] { role.Body!.Id() }, companyIds = new[] { main } });
            Assert.True(u.Status == 201, u.Text);
            // Revisión adversarial F-A: un rol que lee pedidos y productos pero no categorías.
            var sin = await admin.PostAsync("/api/admin/roles", new { name = RoleSinCategorias, description = "Spec 039 F-A", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = perms.Where(x => x.entity != "It39Categoria").ToArray() });
            Assert.True(sin.Status == 200, sin.Text);
            var u2 = await admin.PostAsync("/api/admin/users", new { username = SinCategorias, displayName = "Sin categorías 039", password = Pwd,
                roleIds = new[] { sin.Body!.Id() }, companyIds = new[] { main } });
            Assert.True(u2.Status == 201, u2.Text);

            var visible = (await admin.CreateAsync("It39Categoria", new { Nombre = "Visible", Publica = true })).Id();
            var secreta = (await admin.CreateAsync("It39Categoria", new { Nombre = "Secreta", Publica = false })).Id();
            var secreta2 = (await admin.CreateAsync("It39Categoria", new { Nombre = "Secreta dos", Publica = false })).Id();
            var marcaA = (await admin.CreateAsync("It39Marca", new { Nombre = "Marca A" })).Id();
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = other })).Status);
            var marcaOtra = (await admin.CreateAsync("It39Marca", new { Nombre = "Marca de otra empresa" })).Id();
            Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = main })).Status);
            var p1 = (await admin.CreateAsync("It39Producto", new { Nombre = "P1", Activo = true, Categoria = visible, CategoriaInterna = secreta, Marca = marcaA })).Id();
            var p2 = (await admin.CreateAsync("It39Producto", new { Nombre = "P2", Activo = true, Categoria = secreta })).Id();
            // El ERP no deja apuntar a un registro de otra empresa; un dato así llega por importación antigua o un cambio de
            // empresa del catálogo: se prepara por SQL.
            await ExecAsync($"UPDATE [app].[It39Producto] SET [Marca] = {marcaOtra} WHERE [Id] = {p2}");
            var p3 = (await admin.CreateAsync("It39Producto", new { Nombre = "P3 inactivo", Activo = false, Categoria = visible, Marca = marcaA })).Id();
            await admin.CreateAsync("It39Producto", new { Nombre = "P5 sin ventas", Activo = true, Categoria = secreta2 });
            async Task Pedido(string fecha, string cliente, params object[] partidas) =>
                await admin.CreateAsync("It39Pedido", new { Fecha = fecha, Cliente = cliente, Partidas = partidas });
            await Pedido("2026-07-01", "Cero", new { Producto = p1, Importe = 0, Costo = 0 });
            await Pedido("2026-08-10", "Uno", new { Producto = p1, Importe = 100, Costo = 60 });
            await Pedido("2026-10-05", "Dos", new { Producto = p1, Importe = 200, Costo = 150 }, new { Producto = p2, Importe = 50, Costo = 10 });
            await Pedido("2026-10-12", "Tres", new { Producto = p3, Importe = 30, Costo = 0 });
            await Pedido("2026-10-20", "Privado", new { Producto = p1, Importe = 1000, Costo = 500 });
        }
    }

    private Task<Client> LectorAsync() => host.LoginAsync(Lector, Pwd);

    private static async Task<JsonNode> RunAsync(Client c, string report)
    {
        var res = await c.GetAsync($"/api/reports/{Partida}/{report}");
        Assert.True(res.Status == 200, $"{report}: {res}");
        return res.Body!;
    }

    /// <summary>Filas como «grupo=medida0=medida1…» (números sin ceros de escala) para comparar de un vistazo.</summary>
    private static List<string> Rows(JsonNode r) => r.Arr("rows").Nodes()
        .Select(row => string.Join("=", row.AsObject().OrderBy(kv => kv.Key).Select(kv => kv.Value switch
        {
            null => "null",
            var v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => Norm(v.GetValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            var v => v.GetValue<string>(),
        }))).ToList();

    private static string Norm(string number) =>
        decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Dos_saltos_respetan_el_alcance_del_intermedio_y_del_final()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        Assert.Equal(["Secreta=50", "Visible=1330"], Rows(await RunAsync(admin, "PorCategoria")));
        // El lector no ve la categoría Secreta (final) ni el producto inactivo (intermedio): los dos son «(sin acceso)»,
        // nunca el nombre del registro que no ve. El pedido «Privado» no está en su alcance.
        var r = await RunAsync(lector, "PorCategoria");
        Assert.Equal([$"{ReportService.NoAccess}=80", "Visible=300"], Rows(r));
        Assert.DoesNotContain("Secreta", r.Json());
        Assert.Equal("Producto · Categoría · Nombre", r.Arr("columns")[0]!.Str("label"));
    }

    [Fact]
    public async Task Dos_saltos_con_catalogo_por_empresa()
    {
        // La marca de la otra empresa no se ve ni siquiera para el superusuario en la empresa principal (010).
        using var admin = await host.AdminAsync();
        var r = await RunAsync(admin, "PorMarca");
        Assert.Equal([$"{ReportService.NoAccess}=50", "Marca A=1330"], Rows(r));
        Assert.DoesNotContain("otra empresa", r.Json());
    }

    [Fact]
    public async Task Campo_oculto_en_el_camino_da_404()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        Assert.Equal(200, (await admin.GetAsync($"/api/reports/{Partida}/Interna")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/reports/{Partida}/Interna")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/reports/{Partida}/Interna/export", new { format = "csv" })).Status);
        Assert.DoesNotContain("Interna", (await lector.GetAsync("/api/reports")).Text);
    }

    [Fact]
    public async Task Calculadas_en_filas_y_total()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        var a = await RunAsync(admin, "Margen");
        // Julio: ventas 0 → margen vacío (no 0 %); octubre del administrador incluye el pedido Privado.
        Assert.Equal(["2026-07=0=0=null", "2026-08=100=60=40", "2026-10=1280=660=48.4"], Rows(a));
        Assert.Equal(47.8m, a["totals"]!.Dec("m2")); // margen del total: (1380 − 720) / 1380, no el promedio de los meses
        var l = await RunAsync(lector, "Margen");
        Assert.Equal(["2026-07=0=0=null", "2026-08=100=60=40", "2026-10=280=160=42.9"], Rows(l));
        Assert.Equal(42.1m, l["totals"]!.Dec("m2"));
        Assert.Equal("Margen %", l.Arr("columns")[3]!.Str("label"));
        Assert.Equal(1, l.Arr("columns")[3]!["scale"]!.GetValue<int>());
        Assert.Equal("month", l.Arr("columns")[0]!.Str("period"));
        Assert.Equal("[1]", l["chartOptions"]!["lines"]!.Json());
    }

    [Fact]
    public async Task Semana_y_trimestre_ordenan_por_fecha()
    {
        using var admin = await host.AdminAsync();
        // 2026-10-20 es martes: semana del lunes 19 (S43).
        Assert.Equal(["2026-S27=1", "2026-S33=1", "2026-S41=2", "2026-S42=1", "2026-S43=1"], Rows(await RunAsync(admin, "Semanas")));
        Assert.Equal(["2026-T3=100", "2026-T4=1280"], Rows(await RunAsync(admin, "Trimestres")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Semana_no_depende_de_datefirst(int datefirst)
    {
        // El domingo 2026-10-11 es de la semana del lunes 2026-10-05; el lunes 2026-10-12 empieza la siguiente.
        var sunday = await host.ScalarAsync<DateTime>($"SET DATEFIRST {datefirst}; SELECT {ReportService.PeriodSql("CAST('2026-10-11' AS date)", "week")}");
        var monday = await host.ScalarAsync<DateTime>($"SET DATEFIRST {datefirst}; SELECT {ReportService.PeriodSql("CAST('2026-10-12' AS date)", "week")}");
        Assert.Equal(new DateTime(2026, 10, 5), sunday);
        Assert.Equal(new DateTime(2026, 10, 12), monday);
    }

    [Fact]
    public async Task Comparacion_con_el_alcance_del_usuario()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        var a = (await RunAsync(admin, "KpiMes"))["comparison"]!;
        Assert.Equal("2026-10", a["current"]!.Str("label"));
        Assert.Equal(1280m, a["current"]!["values"]!.Dec("m0"));
        // Septiembre no tuvo registros: el anterior es septiembre en 0, no agosto.
        Assert.Equal("2026-09", a["previous"]!.Str("label"));
        Assert.Equal(0m, a["previous"]!["values"]!.Dec("m0"));
        var l = (await RunAsync(lector, "KpiMes"))["comparison"]!;
        Assert.Equal(280m, l["current"]!["values"]!.Dec("m0")); // sin el pedido Privado
        // Un reporte que no es indicador no calcula la comparación.
        Assert.Null((await RunAsync(lector, "Mensual"))["comparison"]);
    }

    [Fact]
    public async Task Exportacion_y_tablero_iguales_a_la_pantalla()
    {
        using var lector = await LectorAsync();
        var screen = await RunAsync(lector, "PorCategoria");
        var csv = await lector.GetAsync($"/api/reports/{Partida}/PorCategoria/export", new { format = "csv" });
        Assert.Equal(200, csv.Status);
        var lines = csv.Text.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToList();
        Assert.Contains("Producto · Categoría · Nombre", lines[0]);
        var data = lines.Skip(1).Select(l => l.Split(',')).Select(c => $"{c[0].Trim('"')}={Norm(c[^1].Trim('"'))}").ToList();
        Assert.Equal([$"{ReportService.NoAccess}=80", "Visible=300", "Total=380"], data);
        Assert.DoesNotContain("Secreta", csv.Text);

        var board = await lector.GetAsync("/api/dashboards/It39Tablero");
        Assert.Equal(200, board.Status);
        var widgets = board.Body!.Arr("widgets").Nodes().ToList();
        Assert.Equal(Rows(screen), Rows(widgets.First(w => w.Str("report") == "PorCategoria")["result"]!));
        Assert.Equal(Rows(await RunAsync(lector, "Margen")), Rows(widgets.First(w => w.Str("report") == "Margen")["result"]!));
        // La tarjeta kpi de un reporte por mes trae la comparación con el alcance del lector.
        var kpi = widgets.First(w => w.Str("type") == "kpi")["result"]!["comparison"]!;
        Assert.Equal(280m, kpi["current"]!["values"]!.Dec("m0"));
    }

    // Regresión (revisión adversarial 039, F-A): las uniones no pedían leer el catálogo destino; quien no lee categorías
    // agrupaba por un dato de la categoría (solo el campo para mostrar, el que da el buscador, sigue disponible).
    [Fact]
    public async Task F_A_sin_leer_el_catalogo_del_salto_solo_su_campo_para_mostrar()
    {
        using var sin = await host.LoginAsync(SinCategorias, Pwd);
        using var lector = await LectorAsync();
        Assert.Equal(404, (await sin.GetAsync($"/api/reports/{Partida}/CategoriaPublica")).Status);
        Assert.Equal(200, (await lector.GetAsync($"/api/reports/{Partida}/CategoriaPublica")).Status);
        Assert.Equal(200, (await sin.GetAsync($"/api/reports/{Partida}/PorCategoria")).Status); // Nombre es el campo para mostrar
        Assert.DoesNotContain("CategoriaPublica", (await sin.GetAsync("/api/reports")).Text);
    }

    // Regresión (revisión adversarial 039, F-B): agrupar por una referencia daba una fila «(sin acceso)» por cada registro
    // oculto (el GROUP BY llevaba su id): se sabía cuántos había y cuánto sumaba cada uno.
    [Fact]
    public async Task F_B_los_registros_ocultos_de_una_referencia_van_en_una_sola_fila()
    {
        using var lector = await LectorAsync();
        var r = await lector.GetAsync("/api/reports/It39Producto/PorCategoria");
        Assert.True(r.Status == 200, r.Text);
        Assert.Equal([$"{ReportService.NoAccess}=2", "Visible=1"], Rows(r.Body!));
    }

    // Regresión (revisión adversarial 039, F-E): la calculada usaba el promedio ya redondeado (93.33 × 3 = 279.99).
    [Fact]
    public async Task F_E_la_calculada_usa_los_valores_sin_redondear()
    {
        using var lector = await LectorAsync();
        var rows = Rows(await RunAsync(lector, "Promedio"));
        Assert.Contains("2026-10=93.33=280", rows);
    }
}
