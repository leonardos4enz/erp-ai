using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 014 · Pendientes menores y riesgos residuales (las pruebas sin UI de <c>web/e2e/pendientes.spec.ts</c>, movidas en
/// la 030). La clase instala Compras e Inventario en su propia base: ya no depende de que otro archivo corra antes.
/// </summary>
[Collection(SinParalelo.Nombre)] // revisión adversarial 030 (F-G): mide tiempos; no compite con las demás clases
public class PendingTests(PendingTests.Host host) : IClassFixture<PendingTests.Host>
{
    private const string Group = "Pruebas 014";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            await admin.InstallModuleAsync("compras");
            await admin.InstallModuleAsync("inventario");
            Ids["company"] = await ScalarAsync<int>("SELECT Id FROM meta.Company WHERE IsDefault = 1");
            Ids["unidad"] = (await admin.GetAsync("/api/data/UnidadMedida", new { search = "PZA" })).Items.Nodes().First().Id();
            Ids["almacen"] = await LookupAsync(admin, "Almacen", "Almacén general");
            Ids["proveedorUsd"] = await LookupAsync(admin, "Proveedor", "Industrial Supply");
            Ids["departamento"] = await LookupAsync(admin, "Departamento", "Almacén");
            async Task<int> Producto(string clave, object extra)
            {
                var data = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject();
                data["Clave"] = clave; data["Descripcion"] = $"Producto {clave}"; data["Unidad"] = Ids["unidad"];
                var res = await admin.SendAsync(HttpMethod.Post, "/api/data/Producto", new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json"));
                Assert.True(res.Status == 201, res.Text);
                return res.Body!.Id();
            }
            Ids["costoMxn"] = await Producto("P14-MXN", new { CostoUltimo = 170, MonedaCosto = "MXN" });
            Ids["costoUsd"] = await Producto("P14-USD", new { CostoUltimo = 10, MonedaCosto = "USD" });
            Ids["muchos"] = await Producto("P14-MUCHOS", new { });
            Ids["stock"] = await Producto("P14-STOCK", new { });
            Assert.Equal(200, (await admin.PutAsync("/api/admin/exchange-rates", new { from = "USD", to = "MXN", date = DateTime.Now.ToString("yyyy-MM-dd"), rate = 17 })).Status);

            // Sin automatizaciones en el ticket: lo que se mide es solo el folio.
            await admin.ApplyPlanAsync(new { description = "Spec 014 · importación", operations = new object[] {
                new { op = "createEntity", group = Group, name = "P14Ticket", label = "Ticket 014", labelPlural = "Tickets 014", displayField = "Folio", fields = new object[] {
                    new { name = "Folio", label = "Folio", type = "autonumber", prefix = "T14-", padding = 6 },
                    new { name = "Nombre", label = "Nombre", type = "text", required = true, unique = true },
                    new { name = "Grupo", label = "Grupo", type = "text", required = true } } },
                // Acumulado por «match» en un catálogo sin folio: nada serializa las altas antes de acumular.
                new { op = "createEntity", group = Group, name = "P14Cuenta", label = "Cuenta 014", labelPlural = "Cuentas 014", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "P14Saldo", label = "Saldo 014", labelPlural = "Saldos 014", fields = new object[] {
                    new { name = "Cuenta", label = "Cuenta", type = "reference", referenceEntity = "P14Cuenta", editable = false },
                    new { name = "Saldo", label = "Saldo", type = "decimal", editable = false, defaultValue = "0" } } },
                new { op = "setRules", entity = "P14Saldo", rules = new[] { new { name = "SinNegativos", field = "Saldo", assert = "Saldo >= 0", message = "Saldo insuficiente." } } },
                new { op = "createEntity", group = Group, name = "P14Mov", label = "Movimiento 014", labelPlural = "Movimientos 014", fields = new object[] {
                    new { name = "Cuenta", label = "Cuenta", type = "reference", referenceEntity = "P14Cuenta", required = true },
                    new { name = "Importe", label = "Importe", type = "decimal", required = true } } },
                new { op = "setAutomations", entity = "P14Mov", automations = new[] { new { name = "Saldo", label = "Actualizar el saldo", on = "create", actions = new[] {
                    new { type = "rollup", entity = "P14Saldo", match = new[] { new { field = "Cuenta", source = "Cuenta" } }, createMissing = true, field = "Saldo", sum = "Importe" } } } } },
            } });
            Ids["cuenta"] = (await admin.CreateAsync("P14Cuenta", new { Nombre = "Caja 014" })).Id();
        }
    }

    /// <summary>Id por el buscador (lo que ve la persona en el campo de referencia), con la empresa actual.</summary>
    private static async Task<int> LookupAsync(Client admin, string entity, string search)
    {
        var items = (await admin.GetAsync($"/api/data/{entity}/lookup", new { search })).Array;
        Assert.True(items.Count > 0, $"{entity} «{search}»");
        return items[0]!.Id();
    }

    private int Id(string key) => host.Ids[key];

    private static async Task<JsonNode> ExistenciaAsync(Client admin, int producto) =>
        (await admin.GetAsync("/api/data/Existencia", new { filters = $"[{{\"field\":\"Producto\",\"op\":\"eq\",\"value\":{producto}}}]" })).Items.Nodes().First();

    [Fact]
    public async Task H1_CA1_CA2_CA4_el_precio_sugerido_esta_en_la_moneda_de_la_orden_y_el_costo_estimado_en_la_moneda_base()
    {
        using var admin = await host.AdminAsync();
        // Regresión (auditoría 014): la orden en dólares recibía el costo en pesos (170) y la requisición el costo en dólares (10).
        var oc = await admin.CreateAsync("OrdenCompra", new { Proveedor = Id("proveedorUsd"), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("costoMxn"), Cantidad = 2 } } });
        Assert.Equal("USD", oc.Str("Moneda__label"));
        Assert.Equal(17m, oc.Dec("TipoCambio"));
        var partida = oc.Arr("Partidas").Nodes().First();
        Assert.Equal(10m, partida.Dec("PrecioUnitario"));
        Assert.Equal(170m, partida.Dec("CostoMXN"));

        var req = await admin.CreateAsync("Requisicion", new { Departamento = Id("departamento"), Almacen = Id("almacen"), Justificacion = "IT 014",
            Partidas = new[] { new { Producto = Id("costoUsd"), Cantidad = 1 }, new { Producto = Id("costoMxn"), Cantidad = 1 } } });
        Assert.Equal([170m, 170m], req.Arr("Partidas").Nodes().Select(p => p.Dec("CostoEstimado")));

        Assert.Equal("MXN", (await admin.RecordAsync("Producto", Id("muchos"))).Str("MonedaCosto"));
        var usd = await admin.RecordAsync("Producto", Id("costoUsd"));
        var mala = await admin.PutAsync($"/api/data/Producto/{Id("costoUsd")}", new { MonedaCosto = "US", _rowVersion = usd.Str("_rowVersion") });
        Assert.Equal(400, mala.Status);
        Assert.Contains("clave de 3 letras", mala.Body.Json());
    }

    [Fact]
    public async Task H1_CA3_sin_tipo_de_cambio_no_se_sugiere_un_precio_en_otra_moneda()
    {
        using var admin = await host.AdminAsync();
        var eur = await LookupAsync(admin, "Moneda", "EUR");
        var res = await admin.PostAsync("/api/data/OrdenCompra/compute", new { values = new { Proveedor = Id("proveedorUsd"), Almacen = Id("almacen"), Moneda = eur,
            Partidas = new object[] { new { Producto = Id("costoMxn"), Cantidad = 1 }, new { Cantidad = 1 } } } });
        Assert.True(res.Status == 200, res.Text);
        var values = res["values"]!;
        Assert.Null(values.Dec("TipoCambio"));
        Assert.Equal([null, null], values.Arr("Partidas").Nodes().Select(p => p.Dec("PrecioUnitario"))); // antes: 170 (pesos) y nada
    }

    [Fact]
    public async Task H2_CA1_el_acumulado_suma_en_la_base_aunque_haya_mas_de_10_000_movimientos()
    {
        using var admin = await host.AdminAsync();
        // Regresión (auditoría 014): pasado el tope en memoria, todo movimiento nuevo del producto fallaba con 400.
        // 10 001 entradas previas se insertan directo (preparación); el movimiento que se prueba va por la API.
        await host.ExecAsync($"""
            INSERT app.MovimientoInventario (Folio, Fecha, Tipo, Almacen, Producto, Cantidad, Efecto, Estado, CompanyId, CreatedBy)
            SELECT 'B14-' + CAST(n AS varchar(10)), CAST(GETDATE() AS date), N'Entrada', {Id("almacen")}, {Id("muchos")}, 1, 1, N'Aplicado', {Id("company")},
                   (SELECT Id FROM meta.[User] WHERE Username = N'admin')
              FROM (SELECT TOP 10001 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x
            """);
        await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = Id("almacen"), Producto = Id("muchos"), Cantidad = 5 });
        Assert.Equal(10_006m, (await ExistenciaAsync(admin, Id("muchos"))).Dec("Cantidad"));
    }

    [Fact]
    public async Task H6_CA1_salidas_simultaneas_del_mismo_producto_y_almacen_pasan_las_que_caben_el_resto_400()
    {
        using var admin = await host.AdminAsync();
        await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = Id("almacen"), Producto = Id("stock"), Cantidad = 10 });
        var results = await Concurrently(10, _ => admin.PostAsync("/api/data/MovimientoInventario", new { Tipo = "Salida", Almacen = Id("almacen"), Producto = Id("stock"), Cantidad = 3 }));
        Invariant.Statuses(results, 201, 201, 201, 400, 400, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains("No hay existencias suficientes", r["title"]!.GetValue<string>()));
        var cantidad = (await ExistenciaAsync(admin, Id("stock"))).Dec("Cantidad") ?? -1;
        Invariant.NonNegative(cantidad, "existencia");
        Assert.Equal(1m, cantidad);
    }

    [Fact]
    public async Task H6_CA1_cargos_simultaneos_a_la_misma_cuenta_en_un_catalogo_sin_folio_pasan_los_que_caben_sin_esperas()
    {
        // Regresión (riesgo residual 012, 6a): sin folio nada serializaba las altas; con el código anterior esperaban 10 s al
        // bloqueo por clave y se rechazaba un cargo que cabía.
        using var admin = await host.AdminAsync();
        var watch = Stopwatch.StartNew();
        await admin.CreateAsync("P14Mov", new { Cuenta = Id("cuenta"), Importe = 10 });
        var results = await Concurrently(10, _ => admin.PostAsync("/api/data/P14Mov", new { Cuenta = Id("cuenta"), Importe = -3 }));
        Invariant.Statuses(results, 201, 201, 201, 400, 400, 400, 400, 400, 400, 400);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"tardó {watch.ElapsedMilliseconds} ms");
        var saldo = (await admin.GetAsync("/api/data/P14Saldo")).Items.Nodes().First(s => s["Cuenta"]?.GetValue<int>() == Id("cuenta")).Dec("Saldo") ?? -1;
        Invariant.NonNegative(saldo, "saldo");
        Assert.Equal(1m, saldo);
    }

    // ---------------- H3: folios de la importación ----------------

    private static byte[] Csv(int rows, string grupo, string extra = "") =>
        Encoding.UTF8.GetBytes(string.Join("\n", new[] { "Nombre;Grupo" }.Concat(Enumerable.Range(1, rows).Select(i => $"{grupo} {i};{grupo}")).Concat(extra.Length > 0 ? [extra] : [])));

    private static Task<Res> ImportAsync(Client admin, byte[] file, bool dryRun) => admin.PostBytesAsync($"/api/data/P14Ticket/import?dryRun={(dryRun ? "true" : "false")}", file);

    private static long Folio(JsonNode r) => long.Parse(r.Str("Folio")!.Replace("T14-", ""));

    /// <summary>
    /// Otra conexión retiene (sin confirmar) el nombre único de la primera fila: la importación queda detenida justo después
    /// de tomar su primer folio, que es donde el código anterior retenía la secuencia. Mientras, otra persona da de alta un
    /// ticket. Devuelve si el alta terminó antes que la importación.
    /// </summary>
    private async Task<(JsonNode ticket, bool createdFirst, JsonNode result)> CreateWhileImportingAsync(Client admin, string grupo, bool dryRun)
    {
        // Revisión adversarial 030 (F-H): se suelta aunque algo falle a mitad (si no, la siguiente prueba se atora en EXT-014).
        await using var held = await host.HoldAsync($"INSERT app.P14Ticket (Folio, Nombre, Grupo, CreatedBy) VALUES (N'EXT-014', N'{grupo} 1', N'Externo', 1)");
        var watch = Stopwatch.StartNew();
        long importDone = 0;
        var importing = ImportAsync(admin, Csv(200, grupo), dryRun).ContinueWith(t => { importDone = watch.ElapsedMilliseconds; return t.Result; });
        await held.WaitUntilBlockingAsync(); // la importación ya tomó su bloque y está detenida en el nombre retenido
        var ticket = await admin.CreateAsync("P14Ticket", new { Nombre = $"Alta durante {grupo}", Grupo = "Captura" });
        var createDone = watch.ElapsedMilliseconds;
        await Task.Delay(2000);
        await held.DisposeAsync();
        var res = await importing;
        Assert.True(res.Status == 200, res.Text);
        Assert.True(importDone > 1500, $"la importación debía esperar a la otra conexión (terminó a los {importDone} ms)");
        return (ticket, createDone < importDone, res.Body!);
    }

    [Fact]
    public async Task H3_CA1_la_vista_previa_no_toca_la_secuencia_ni_detiene_las_altas()
    {
        // Regresión (diferido de la 012): el alta esperaba a que la vista previa liberara el folio.
        using var admin = await host.AdminAsync();
        var before = await admin.CreateAsync("P14Ticket", new { Nombre = "Antes", Grupo = "Captura" });
        var (ticket, createdFirst, result) = await CreateWhileImportingAsync(admin, "Vista", dryRun: true);
        Assert.True(result["dryRun"]!.GetValue<bool>());
        Assert.True(result["valid"]!.GetValue<bool>());
        Assert.Equal(0, result["imported"]!.GetValue<int>());
        Assert.True(createdFirst, "el alta no espera a la vista previa");
        Assert.Equal(Folio(before) + 1, Folio(ticket)); // la vista previa no consumió folios
    }

    [Fact]
    public async Task H3_CA2_CA3_la_importacion_reserva_su_bloque_no_detiene_altas_y_devuelve_el_bloque_si_falla()
    {
        using var admin = await host.AdminAsync();
        var (ticket, createdFirst, result) = await CreateWhileImportingAsync(admin, "Importados", dryRun: false);
        Assert.Equal(200, result["imported"]!.GetValue<int>());
        Assert.True(createdFirst, "el alta no espera a la importación");
        var folios = (await host.ScalarAsync<string>("""
            SELECT STRING_AGG(REPLACE(Folio, 'T14-', ''), ',') FROM app.P14Ticket WHERE Grupo = N'Importados' AND IsDeleted = 0
            """))!.Split(',').Select(long.Parse).ToList();
        Assert.Equal(200, folios.Count);
        Invariant.Unique(folios, "folios de la importación", consecutive: true);
        Assert.Equal(folios.Max() + 1, Folio(ticket)); // el alta simultánea recibe el siguiente al bloque

        // Una importación con error devuelve su bloque: el siguiente folio no deja hueco.
        Assert.Equal(400, (await ImportAsync(admin, Csv(20, "Fallidos", ";SinNombre"), dryRun: false)).Status);
        var next = await admin.CreateAsync("P14Ticket", new { Nombre = "Después del error", Grupo = "Captura" });
        Assert.Equal(Folio(ticket) + 1, Folio(next));
    }
}
