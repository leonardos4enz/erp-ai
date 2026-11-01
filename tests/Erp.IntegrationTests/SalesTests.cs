using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 043 · Módulo de Ventas sobre SQL real: la capacidad <c>createLines[].from</c>, el flujo cotización → pedido →
/// embarque → factura → cobro con sus pólizas, el límite de crédito, la concurrencia (surtir y cobrar a la vez) y la
/// seguridad por rol, registro y campo. La clase instala Compras, Inventario, Contabilidad, Costeo y Ventas en su base.
/// </summary>
public class SalesTests(SalesTests.Host host) : IClassFixture<SalesTests.Host>
{
    private const string Pwd = "frase larga de ventas 2043";
    private const string Vendedor = "it43-vendedor";
    private const string Vendedor2 = "it43-vendedor2";
    private const string Gerente = "it43-gerente";
    private const string Surtidor = "it43-surtidor";
    private const string Cobranza = "it43-cobranza";
    private const string Contador = "it43-contador";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];
        public readonly Dictionary<string, int> Cuentas = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            foreach (var m in new[] { "compras", "inventario", "contabilidad", "costeo", "ventas" }) await admin.InstallModuleAsync(m);
            Ids["main"] = await ScalarAsync<int>("SELECT Id FROM meta.Company WHERE IsDefault = 1");
            Ids["unidad"] = (await admin.GetAsync("/api/data/UnidadMedida", new { search = "PZA" })).Items.Nodes().First().Id();
            Ids["mxn"] = (await admin.GetAsync("/api/data/Moneda", new { search = "MXN" })).Items.Nodes().First().Id();
            Ids["almacen"] = (await admin.GetAsync("/api/data/Almacen/lookup", new { search = "Almacén general" })).Array.Nodes().First().Id();
            foreach (var c in (await admin.GetAsync("/api/data/CuentaContable", new { pageSize = 200 })).Items.Nodes())
                Cuentas[c.Str("Codigo")!] = c.Id();
            Cuentas["1151"] = (await admin.CreateAsync("CuentaContable", new { Codigo = "1151", Nombre = "Clientes sucursal", Tipo = "Activo" })).Id();

            await CuentasAsync(admin, Ids["almacen"], clientes: "1150");
            Ids["sucursal"] = (await admin.CreateAsync("Almacen", new { Clave = "SUC43", Nombre = "Sucursal 043" })).Id();
            await CuentasAsync(admin, Ids["sucursal"], clientes: "1151");
            Ids["sinCuentas"] = (await admin.CreateAsync("Almacen", new { Clave = "SC43", Nombre = "Sin cuentas 043" })).Id();
            var year = DateTime.Today.Year;
            await admin.CreateAsync("PeriodoContable", new { Nombre = $"Ejercicio {year}", Inicio = $"{year}-01-01", Fin = $"{year}-12-31" });

            async Task<int> Producto(string clave, decimal precio, string metodo = "Promedio") =>
                (await admin.CreateAsync("Producto", new { Clave = clave, Descripcion = $"Producto {clave}", Unidad = Ids["unidad"], PrecioVenta = precio, MonedaPrecio = "MXN", MetodoCosteo = metodo })).Id();
            Ids["a"] = await Producto("P43-A", 100);
            Ids["fifo"] = await Producto("P43-FIFO", 50, "FIFO");
            Ids["poco"] = await Producto("P43-POCO", 10);
            foreach (var almacen in new[] { Ids["almacen"], Ids["sucursal"], Ids["sinCuentas"] })
                await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = almacen, Producto = Ids["a"], Cantidad = 1000, CostoUnitario = 60 });
            await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = Ids["almacen"], Producto = Ids["fifo"], Cantidad = 10, CostoUnitario = 20 });
            await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = Ids["almacen"], Producto = Ids["fifo"], Cantidad = 10, CostoUnitario = 30 });
            await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = Ids["almacen"], Producto = Ids["poco"], Cantidad = 2, CostoUnitario = 5 });

            foreach (var (user, role) in new[] { (Vendedor, "Vendedor"), (Vendedor2, "Vendedor"), (Gerente, "Gerente de ventas"), (Surtidor, "Surtidor"),
                         (Cobranza, "Facturación y cobranza"), (Contador, "Contador") })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = user, displayName = user, password = Pwd,
                    roleIds = new[] { await admin.RoleIdAsync(role) }, companyIds = new[] { Ids["main"] } });
                Assert.True(u.Status == 201, u.Text);
                Ids[user] = u.Body!.Id();
            }
        }

        private async Task CuentasAsync(Client admin, int almacen, string clientes)
        {
            var row = await admin.RecordAsync("Almacen", almacen);
            var res = await admin.PutAsync($"/api/data/Almacen/{almacen}", new
            {
                CuentaInventario = Cuentas["1200"], CuentaEntradas = Cuentas["2150"], CuentaCostoVentas = Cuentas["5100"], CuentaVentas = Cuentas["4100"],
                CuentaClientes = Cuentas[clientes], CuentaIvaTrasladado = Cuentas["2160"], _rowVersion = row.Str("_rowVersion"),
            });
            Assert.True(res.Status == 200, res.Text);
        }
    }

    private int Id(string key) => host.Ids[key];
    private Task<Client> As(string user) => host.LoginAsync(user, Pwd);

    private async Task<JsonNode> ClienteAsync(Client c, string rfc, object? extra = null)
    {
        var data = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra ?? new { }))!.AsObject();
        data["RFC"] = rfc; data["RazonSocial"] = $"Cliente {rfc}"; data["Moneda"] = Id("mxn");
        var res = await c.SendAsync(HttpMethod.Post, "/api/data/Cliente", new StringContent(data.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.True(res.Status == 201, res.Text);
        return res.Body!;
    }

    private static async Task<JsonNode> Ok(Task<Res> call, int status = 200)
    {
        var res = await call;
        Assert.True(res.Status == status, res.Text);
        return res.Body!;
    }

    /// <summary>Pedido confirmado (sin pasar por cotización) del vendedor.</summary>
    private async Task<JsonNode> PedidoAsync(Client vendedor, int cliente, int almacen, params (int Producto, decimal Cantidad, decimal Precio)[] lineas)
    {
        var pedido = await vendedor.CreateAsync("Pedido", new { Cliente = cliente, Almacen = almacen,
            Partidas = lineas.Select(l => new { Producto = l.Producto, Cantidad = l.Cantidad, PrecioUnitario = l.Precio }).ToArray() });
        return await Ok(vendedor.TransitionAsync("Pedido", pedido, "confirmar"));
    }

    private static JsonArray Lines(JsonNode doc, string field = "Partidas") => doc.Arr(field);

    private async Task<JsonNode> EmbarqueAsync(Client surtidor, JsonNode pedido, int almacen, params (int Linea, decimal Cantidad)[] lineas)
    {
        var partidas = Lines(pedido).Nodes().ToList();
        return await surtidor.CreateAsync("Embarque", new { Pedido = pedido.Id(), Almacen = almacen,
            Partidas = lineas.Select(l => new { PartidaPedido = partidas[l.Linea].Id(), Cantidad = l.Cantidad }).ToArray() });
    }

    private async Task<JsonNode> FacturaAsync(Client cobranza, JsonNode pedido, params (int Linea, decimal Cantidad)[] lineas)
    {
        var partidas = Lines(pedido).Nodes().ToList();
        return await cobranza.CreateAsync("Factura", new { Pedido = pedido.Id(),
            Partidas = lineas.Select(l => new { PartidaPedido = partidas[l.Linea].Id(), Cantidad = l.Cantidad }).ToArray() });
    }

    private async Task<JsonNode> PolizaAsync(string referencia)
    {
        using var contador = await As(Contador);
        var list = (await contador.GetAsync("/api/data/Poliza", new { search = referencia, pageSize = 50 })).Items.Nodes().Where(p => p.Str("Referencia") == referencia).ToList();
        Assert.True(list.Count == 1, $"pólizas de {referencia}: {list.Count}");
        return await contador.RecordAsync("Poliza", list[0].Id());
    }

    private static List<string> Asientos(JsonNode poliza, IReadOnlyDictionary<int, string> codigos) => Lines(poliza).Nodes()
        .Select(p => $"{codigos[p["Cuenta"]!.GetValue<int>()]}:{p.Dec("Cargo"):0.##}/{p.Dec("Abono"):0.##}").ToList();

    private Dictionary<int, string> Codigos => host.Cuentas.ToDictionary(kv => kv.Value, kv => kv.Key);

    // ---------------- H1: partidas que se copian del origen (motor) ----------------

    [Fact]
    public async Task H1_CA1_create_con_from_copia_cada_partida_en_su_orden_con_when_y_rutas_al_documento()
    {
        using var admin = await host.AdminAsync();
        await admin.ApplyPlanAsync(new { description = "Spec 043 · from", operations = new object[] {
            new { op = "createEntity", group = "Pruebas 043", name = "It43Destino", label = "Destino 043", labelPlural = "Destinos 043", fields = new object[] {
                new { name = "Origen", label = "Origen", type = "text" } } },
            new { op = "createEntity", group = "Pruebas 043", name = "It43DestinoPartida", label = "Partida destino", labelPlural = "Partidas destino", fields = new object[] {
                new { name = "Destino", label = "Destino", type = "parent", referenceEntity = "It43Destino" },
                new { name = "Texto", label = "Texto", type = "text" },
                new { name = "Doble", label = "Doble", type = "decimal" } } },
            new { op = "addField", entity = "It43Destino", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "It43DestinoPartida" } },
            new { op = "createEntity", group = "Pruebas 043", name = "It43Origen", label = "Origen 043", labelPlural = "Orígenes 043", fields = new object[] {
                new { name = "Nombre", label = "Nombre", type = "text", required = true },
                new { name = "Almacen", label = "Almacén", type = "reference", referenceEntity = "Almacen" } } },
            new { op = "createEntity", group = "Pruebas 043", name = "It43OrigenPartida", label = "Partida origen", labelPlural = "Partidas origen", fields = new object[] {
                new { name = "Origen", label = "Origen", type = "parent", referenceEntity = "It43Origen" },
                new { name = "Clave", label = "Clave", type = "text", required = true },
                new { name = "Cantidad", label = "Cantidad", type = "decimal", required = true } } },
            new { op = "addField", entity = "It43Origen", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "It43OrigenPartida" } },
            new { op = "setAutomations", entity = "It43Origen", automations = new[] { new { name = "Copiar", label = "Copiar", on = "create", actions = new object[] {
                new { type = "create", entity = "It43Destino", set = new[] { new { field = "Origen", value = "Nombre" } }, createLines = new object[] {
                    new { field = "Partidas", set = new[] { new { field = "Texto", value = "'encabezado ' & Nombre" } } },
                    // Ruta de tres saltos desde la partida: su documento › almacén › cuenta de clientes › código.
                    new { field = "Partidas", from = "Partidas", when = "Cantidad > 0", set = new[] {
                        new { field = "Texto", value = "Clave & ' de ' & Origen.Nombre & ' · ' & Origen.Almacen.CuentaClientes.Codigo" },
                        new { field = "Doble", value = "Cantidad * 2" } } } } } } } } },
        } });
        await admin.CreateAsync("It43Origen", new { Nombre = "O1", Almacen = Id("sucursal"), Partidas = new object[] {
            new { Clave = "c", Cantidad = 3 }, new { Clave = "a", Cantidad = 0 }, new { Clave = "b", Cantidad = 1.5 } } });

        var destino = (await admin.GetAsync("/api/data/It43Destino", new { pageSize = 10 })).Items.Nodes().Single(d => d.Str("Origen") == "O1");
        var lines = Lines(await admin.RecordAsync("It43Destino", destino.Id())).Nodes().Select(l => $"{l.Str("Texto")}={l.Dec("Doble"):0.##}").ToList();
        Assert.Equal(["encabezado O1=", "c de O1 · 1151=6", "b de O1 · 1151=3"], lines);
    }

    [Fact]
    public async Task H1_CA2_el_compilador_rechaza_from_que_no_es_partida_con_lines_y_campos_de_otro_ambito()
    {
        using var admin = await host.AdminAsync();
        async Task<string> Errors(object createLine, string? lines = null)
        {
            var action = new JsonObject { ["type"] = "create", ["entity"] = "Pedido",
                ["set"] = JsonNode.Parse("""[{"field":"Cliente","value":"Cliente"},{"field":"Almacen","value":"Almacen"},{"field":"Moneda","value":"Moneda"},{"field":"Vendedor","value":"Vendedor"}]"""),
                ["createLines"] = new JsonArray(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(createLine))) };
            if (lines is not null) action["lines"] = lines;
            var plan = new JsonObject { ["description"] = "x", ["operations"] = new JsonArray(new JsonObject { ["op"] = "setAutomations", ["entity"] = "Cotizacion", ["merge"] = true,
                ["automations"] = new JsonArray(new JsonObject { ["name"] = "X43", ["label"] = "X", ["on"] = "create", ["actions"] = new JsonArray(action) }) }) };
            var res = await admin.SendAsync(HttpMethod.Post, "/api/model/plan/preview", new StringContent(plan.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.True(res.Status is 200 or 400, res.Text);
            return res.Text;
        }
        Assert.Contains("'Cliente' no es un campo de partidas de Cotizacion", await Errors(new { field = "Partidas", from = "Cliente", set = new[] { new { field = "Cantidad", value = "1" } } }));
        Assert.Contains("'from' no se combina con 'lines'", await Errors(new { field = "Partidas", from = "Partidas", set = new[] { new { field = "Cantidad", value = "1" } } }, lines: "Partidas"));
        // En la partida de origen no hay «Vigencia» (es del encabezado): se nombra por el padre.
        Assert.Contains("Vigencia", await Errors(new { field = "Partidas", from = "Partidas", set = new[] { new { field = "Cantidad", value = "IF(Vigencia > TODAY(), 1, 2)" } } }));
        Assert.Contains("\"valid\":true", await Errors(new { field = "Partidas", from = "Partidas", set = new[] { new { field = "Cantidad", value = "IF(Cotizacion.Vigencia > TODAY(), Cantidad, 2)" }, new { field = "Producto", value = "Producto" } } }));
    }

    // ---------------- H3 · H4 · H5: el flujo completo ----------------

    [Fact]
    public async Task H3_H4_H5_cotizacion_pedido_embarques_factura_y_cobros_con_sus_polizas_y_saldos()
    {
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        var cliente = await ClienteAsync(vendedor, "FLU430101AAA", new { LimiteCredito = (decimal?)null });

        // H3: cotizar con el precio sugerido (H2 CA2), enviar y aceptar.
        var cot = await vendedor.CreateAsync("Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new object[] {
            new { Producto = Id("a"), Cantidad = 10, DescuentoPct = 10 }, new { Producto = Id("fifo"), Cantidad = 15 } } });
        Assert.Equal([100m, 50m], Lines(cot).Nodes().Select(l => l.Dec("PrecioUnitario")!.Value));
        Assert.Equal(1914m, cot.Dec("Total")); // (1000 − 100) × 1.16 + 750 × 1.16
        cot = await Ok(vendedor.TransitionAsync("Cotizacion", cot, "enviar"));
        cot = await Ok(vendedor.TransitionAsync("Cotizacion", cot, "aceptar"));
        var folioPedido = cot.Str("Pedido");
        Assert.StartsWith("PED-", folioPedido);
        var pedidoRow = (await vendedor.GetAsync("/api/data/Pedido", new { search = folioPedido })).Items.Nodes().Single();
        var pedido = await vendedor.RecordAsync("Pedido", pedidoRow.Id());
        Assert.Equal(cot.Str("Folio"), pedido.Str("Cotizacion"));
        Assert.Equal("Borrador", pedido.Str("Estado"));
        Assert.Equal(["10=100=10", "15=50=0"], Lines(pedido).Nodes().Select(l => $"{l.Dec("Cantidad"):0.##}={l.Dec("PrecioUnitario"):0.##}={l.Dec("DescuentoPct"):0.##}"));
        pedido = await Ok(vendedor.TransitionAsync("Pedido", pedido, "confirmar"));

        // H4: primer embarque parcial (FIFO: 10 × 20 + 2 × 30) y luego el resto.
        var emb = await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 4), (1, 12));
        emb = await Ok(surtidor.TransitionAsync("Embarque", emb, "confirmar"));
        Assert.Null(emb["CostoTotal"]); // H4 CA4: el surtidor no ve costos
        using var gerente = await As(Gerente);
        var embG = await gerente.RecordAsync("Embarque", emb.Id());
        Assert.Equal(240m + 260m, embG.Dec("CostoTotal"));
        pedido = await vendedor.RecordAsync("Pedido", pedido.Id());
        Assert.Equal("SurtidoParcial", pedido.Str("Estado"));
        Assert.Equal(["5100:240/0", "5100:260/0", "1200:0/500"], Asientos(await PolizaAsync(emb.Str("Folio")!), Codigos));

        var emb2 = await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 6), (1, 3));
        await Ok(surtidor.TransitionAsync("Embarque", emb2, "confirmar"));
        pedido = await vendedor.RecordAsync("Pedido", pedido.Id());
        Assert.Equal("Surtido", pedido.Str("Estado"));

        // H5: no se factura más de lo surtido; se factura todo y la póliza de ventas cuadra.
        var excede = await cobranza.PostAsync("/api/data/Factura", new { Pedido = pedido.Id(), Partidas = new[] { new { PartidaPedido = Lines(pedido)[0]!.Id(), Cantidad = 11 } } });
        Assert.True(excede.Status == 400 && excede.Text.Contains("excede lo surtido"), excede.Text);
        var fac = await FacturaAsync(cobranza, pedido, (0, 10), (1, 15));
        Assert.Equal(1914m, fac.Dec("Total"));
        fac = await Ok(cobranza.TransitionAsync("Factura", fac, "emitir"));
        Assert.Equal(["1150:1914/0", "4100:0/1650", "2160:0/264"], Asientos(await PolizaAsync(fac.Str("Folio")!), Codigos));
        Assert.Equal(1914m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("Saldo"));
        Assert.Equal("Facturado", (await vendedor.RecordAsync("Pedido", pedido.Id())).Str("Estado"));

        // Cobro parcial y luego el resto: la factura pasa a pagada parcial y pagada; el saldo del cliente llega a cero.
        var cobro = await cobranza.CreateAsync("Cobro", new { Cliente = cliente.Id(), CuentaBanco = host.Cuentas["1100"], Aplicaciones = new[] { new { Factura = fac.Id(), Importe = 450 } } });
        cobro = await Ok(cobranza.TransitionAsync("Cobro", cobro, "aplicar"));
        Assert.Equal("PagadaParcial", (await cobranza.RecordAsync("Factura", fac.Id())).Str("Estado"));
        Assert.Equal(1464m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("Saldo"));
        Assert.Equal(["1100:450/0", "1150:0/450"], Asientos(await PolizaAsync(cobro.Str("Folio")!), Codigos));
        var resto = await cobranza.CreateAsync("Cobro", new { Cliente = cliente.Id(), CuentaBanco = host.Cuentas["1100"], Aplicaciones = new[] { new { Factura = fac.Id() } } });
        Assert.Equal(1464m, Lines(resto, "Aplicaciones")[0]!.Dec("Importe")); // sugiere el saldo
        await Ok(cobranza.TransitionAsync("Cobro", resto, "aplicar"));
        Assert.Equal("Pagada", (await cobranza.RecordAsync("Factura", fac.Id())).Str("Estado"));
        Assert.Equal(0m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("Saldo"));
    }

    [Fact]
    public async Task H5_CA3_un_cobro_de_dos_facturas_de_almacenes_distintos_abona_a_la_cuenta_de_clientes_de_cada_uno()
    {
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        var cliente = await ClienteAsync(vendedor, "DOS430101AAA");
        var facturas = new List<JsonNode>();
        foreach (var almacen in new[] { Id("almacen"), Id("sucursal") })
        {
            var pedido = await PedidoAsync(vendedor, cliente.Id(), almacen, (Id("a"), 1, 100));
            await Ok(surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, pedido, almacen, (0, 1)), "confirmar"));
            pedido = await vendedor.RecordAsync("Pedido", pedido.Id());
            facturas.Add(await Ok(cobranza.TransitionAsync("Factura", await FacturaAsync(cobranza, pedido, (0, 1)), "emitir")));
        }
        var cobro = await cobranza.CreateAsync("Cobro", new { Cliente = cliente.Id(), CuentaBanco = host.Cuentas["1100"],
            Aplicaciones = facturas.Select(f => new { Factura = f.Id(), Importe = 116 }).ToArray() });
        cobro = await Ok(cobranza.TransitionAsync("Cobro", cobro, "aplicar"));
        Assert.Equal(["1100:232/0", "1150:0/116", "1151:0/116"], Asientos(await PolizaAsync(cobro.Str("Folio")!), Codigos));
    }

    // ---------------- H2: crédito, precio y descuento ----------------

    [Fact]
    public async Task H2_CA3_sobre_el_limite_el_vendedor_no_confirma_y_el_gerente_confirma_aceptando_el_aviso()
    {
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "LIM430101AAA");
        // H6 CA2: el límite lo captura el gerente de ventas.
        var conLimite = await vendedor.PutAsync($"/api/data/Cliente/{cliente.Id()}", new { LimiteCredito = 500, _rowVersion = cliente.Str("_rowVersion") });
        var row = await gerente.RecordAsync("Cliente", cliente.Id());
        Assert.Null(row.Dec("LimiteCredito"));
        Assert.True(conLimite.Status == 200, conLimite.Text); // el campo que no edita se ignora, como cualquier campo de solo lectura
        await Ok(gerente.PutAsync($"/api/data/Cliente/{cliente.Id()}", new { LimiteCredito = 500, _rowVersion = row.Str("_rowVersion") }));

        var pedido = await vendedor.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 5 } } });
        Assert.Contains("límite de crédito", pedido.Json()); // _warnings: el aviso se ve al guardar
        var negado = await vendedor.TransitionAsync("Pedido", pedido, "confirmar");
        Assert.True(negado.Status == 400 && negado.Text.Contains("Solo el gerente de ventas"), negado.Text);

        var pregunta = await gerente.TransitionAsync("Pedido", pedido, "confirmar");
        Assert.True(pregunta.Status == 428, pregunta.Text);
        var aviso = pregunta.Body!["errors"]!.AsObject().First().Value!.AsArray()[0]!.GetValue<string>();
        var ok = await gerente.PostAsync($"/api/data/Pedido/{pedido.Id()}/transition", new { transition = "confirmar", _rowVersion = pedido.Str("_rowVersion"), acceptWarnings = new[] { aviso } });
        Assert.True(ok.Status == 200, ok.Text);

        // Sin límite no avisa.
        var libre = await ClienteAsync(vendedor, "SIN430101AAA");
        var p2 = await vendedor.CreateAsync("Pedido", new { Cliente = libre.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 50 } } });
        Assert.DoesNotContain("límite de crédito", p2.Json());
        Assert.Equal(200, (await vendedor.TransitionAsync("Pedido", p2, "confirmar")).Status);
    }

    [Fact]
    public async Task H3_CA3_un_descuento_mayor_a_15_solo_lo_captura_el_gerente()
    {
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "DES430101AAA");
        var res = await vendedor.PostAsync("/api/data/Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, DescuentoPct = 20 } } });
        Assert.True(res.Status == 400 && res.Text.Contains("mayor a 15 %"), res.Text);
        await gerente.CreateAsync("Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, DescuentoPct = 20 } } });
    }

    // ---------------- H4: existencia, cuentas y concurrencia ----------------

    [Fact]
    public async Task H4_CA2_sin_cuentas_en_el_almacen_o_sin_existencia_no_se_confirma_el_embarque()
    {
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        var cliente = await ClienteAsync(vendedor, "CUE430101AAA");
        var p1 = await PedidoAsync(vendedor, cliente.Id(), Id("sinCuentas"), (Id("a"), 1, 100));
        var r1 = await surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, p1, Id("sinCuentas"), (0, 1)), "confirmar");
        Assert.True(r1.Status == 400 && r1.Text.Contains("cuenta de costo de ventas"), r1.Text);

        var p2 = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("poco"), 5, 10));
        var r2 = await surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, p2, Id("almacen"), (0, 5)), "confirmar");
        Assert.True(r2.Status == 400 && r2.Text.Contains("existencias suficientes"), r2.Text);
    }

    [Fact]
    public async Task H4_CA3_diez_embarques_del_mismo_pedido_a_la_vez_no_surten_de_mas()
    {
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "CON430101AAA");
        var pedido = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("a"), 10, 100));
        var embarques = new List<JsonNode>();
        for (var i = 0; i < 10; i++) embarques.Add(await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 4)));
        var antes = await ExistenciaAsync(gerente, Id("almacen"), Id("a"));

        var results = await Concurrently(10, i => surtidor.TransitionAsync("Embarque", embarques[i], "confirmar"));
        // 4 + 4 ≤ 10; un tercero excedería lo pendiente (cada confirmación espera el pedido bloqueado y lo lee al día).
        Invariant.Statuses(results, 200, 200, 400, 400, 400, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains("excede lo pendiente de surtir", r.Text));
        var linea = Lines(await gerente.RecordAsync("Pedido", pedido.Id()))[0]!;
        Invariant.AtMost(linea.Dec("CantidadSurtida")!.Value, 10, "surtido");
        Assert.Equal(8m, linea.Dec("CantidadSurtida"));
        Assert.Equal(antes - 8, await ExistenciaAsync(gerente, Id("almacen"), Id("a")));
        Assert.Equal(480m, linea.Dec("CostoSurtido"));
    }

    private static async Task<decimal> ExistenciaAsync(Client c, int almacen, int producto) =>
        (await c.GetAsync("/api/data/Existencia", new { pageSize = 200 })).Items.Nodes()
            .Single(x => x["Almacen"]?.GetValue<int>() == almacen && x["Producto"]?.GetValue<int>() == producto).Dec("Cantidad")!.Value;

    // ---------------- H5: concurrencia de cobros ----------------

    [Fact]
    public async Task H5_CA4_diez_cobros_de_la_misma_factura_a_la_vez_no_cobran_de_mas_y_el_saldo_cuadra()
    {
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        var cliente = await ClienteAsync(vendedor, "COB430101AAA");
        var pedido = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("a"), 10, 100));
        await Ok(surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 10)), "confirmar"));
        pedido = await vendedor.RecordAsync("Pedido", pedido.Id());
        var fac = await Ok(cobranza.TransitionAsync("Factura", await FacturaAsync(cobranza, pedido, (0, 10)), "emitir"));
        var cobros = new List<JsonNode>();
        for (var i = 0; i < 10; i++)
            cobros.Add(await cobranza.CreateAsync("Cobro", new { Cliente = cliente.Id(), CuentaBanco = host.Cuentas["1100"], Aplicaciones = new[] { new { Factura = fac.Id(), Importe = 500 } } }));

        var results = await Concurrently(10, i => cobranza.TransitionAsync("Cobro", cobros[i], "aplicar"));
        // 500 + 500 ≤ 1160; un tercero excedería el saldo (la factura se bloquea y se lee al día).
        Invariant.Statuses(results, 200, 200, 400, 400, 400, 400, 400, 400, 400, 400);
        Assert.All(results.Where(r => r.Status == 400), r => Assert.Contains("excede el saldo", r.Text));
        fac = await cobranza.RecordAsync("Factura", fac.Id());
        Invariant.AtMost(fac.Dec("Cobrado")!.Value, fac.Dec("Total")!.Value, "cobrado");
        Assert.Equal(160m, fac.Dec("Saldo"));
        Assert.Equal(160m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("Saldo"));
    }

    [Fact]
    public async Task H3_CA2_aceptar_la_misma_cotizacion_dos_veces_a_la_vez_crea_un_solo_pedido()
    {
        using var vendedor = await As(Vendedor);
        var cliente = await ClienteAsync(vendedor, "ACE430101AAA");
        var cot = await vendedor.CreateAsync("Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1 } } });
        cot = await Ok(vendedor.TransitionAsync("Cotizacion", cot, "enviar"));
        var results = await Concurrently(2, _ => vendedor.TransitionAsync("Cotizacion", cot, "aceptar"));
        Assert.Equal(1, results.Count(r => r.Status == 200));
        var pedidos = (await vendedor.GetAsync("/api/data/Pedido", new { pageSize = 200 })).Items.Nodes().Count(p => p.Str("Cotizacion") == cot.Str("Folio"));
        Invariant.ExactlyOnce(pedidos, "pedido de la cotización");
    }

    // ---------------- H6: seguridad, reportes y módulo ----------------

    [Fact]
    public async Task H6_CA2_cada_vendedor_ve_lo_suyo_y_los_costos_solo_quien_los_ve()
    {
        using var admin = await host.AdminAsync();
        using var vendedor = await As(Vendedor);
        using var otro = await As(Vendedor2);
        using var gerente = await As(Gerente);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        var cliente = await ClienteAsync(vendedor, "PRO430101AAA");
        var pedido = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("poco"), 1, 10));
        await Ok(surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 1)), "confirmar"));
        Assert.Equal(404, (await otro.GetAsync($"/api/data/Cliente/{cliente.Id()}")).Status);
        Assert.Equal(404, (await otro.GetAsync($"/api/data/Pedido/{pedido.Id()}")).Status);
        Assert.DoesNotContain((await otro.GetAsync("/api/data/Pedido", new { pageSize = 200 })).Items.Nodes(), p => p.Id() == pedido.Id());
        Assert.Equal(200, (await gerente.GetAsync($"/api/data/Pedido/{pedido.Id()}")).Status);

        // El vendedor no ve el costo de lo surtido; el gerente sí, también en el margen (039: el reporte con el campo oculto no
        // existe para quien no lo ve, y «Productos más vendidos» sí).
        Assert.Null(Lines(await vendedor.RecordAsync("Pedido", pedido.Id()))[0]!["CostoSurtido"]);
        Assert.Equal(5m, Lines(await gerente.RecordAsync("Pedido", pedido.Id()))[0]!.Dec("CostoSurtido"));
        static string? Fila(Res r, string producto) => r.Body!.Arr("rows").Nodes().FirstOrDefault(x => x.Str("g0")!.Contains(producto)) is { } row
            ? string.Join("=", row.AsObject().Where(kv => kv.Key != "g0").OrderBy(kv => kv.Key).Select(kv => $"{kv.Value?.GetValue<decimal>():0.##}")) : null;
        var margen = await gerente.GetAsync("/api/reports/PedidoPartida/MargenPorProducto");
        Assert.True(margen.Status == 200, margen.Text);
        Assert.Equal("10=5=5=50", Fila(margen, "P43-POCO"));
        foreach (var sin in new[] { vendedor, cobranza })
        {
            Assert.Equal(404, (await sin.GetAsync("/api/reports/PedidoPartida/MargenPorProducto")).Status);
            var vendidos = await sin.GetAsync("/api/reports/PedidoPartida/ProductosMasVendidos");
            Assert.True(vendidos.Status == 200, vendidos.Text);
            Assert.Equal("1=10", Fila(vendidos, "P43-POCO"));
        }
        Assert.Null(Fila(await otro.GetAsync("/api/reports/PedidoPartida/ProductosMasVendidos"), "P43-POCO")); // solo sus pedidos

        // Nadie fuera de la empresa los ve, tampoco el superusuario con la otra empresa activa.
        var otra = (await admin.PostAsync("/api/admin/companies", new { code = "IT43-B", name = "Otra empresa 043", baseCurrency = "MXN" })).Body!.Id();
        Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = otra })).Status);
        Assert.Equal(404, (await admin.GetAsync($"/api/data/Pedido/{pedido.Id()}")).Status);
        Assert.Equal(404, (await admin.GetAsync($"/api/data/Cliente/{cliente.Id()}")).Status);
        Assert.Equal(200, (await admin.PostAsync("/api/auth/company", new { companyId = Id("main") })).Status);
    }

    [Fact]
    public async Task H6_CA1_el_modulo_va_entre_compras_e_inventario_y_la_tarea_llega_en_pausa()
    {
        using var admin = await host.AdminAsync();
        var apps = (await admin.GetAsync("/api/model/apps")).Text;
        Assert.True(apps.IndexOf("\"Compras\"", StringComparison.Ordinal) < apps.IndexOf("\"Ventas\"", StringComparison.Ordinal), apps);
        Assert.True(apps.IndexOf("\"Ventas\"", StringComparison.Ordinal) < apps.IndexOf("\"Inventario\"", StringComparison.Ordinal), apps);
        var schedules = await admin.GetAsync("/api/admin/automations/schedules");
        Assert.Contains("VencerCotizaciones", schedules.Text);
    }

    // ---------------- Regresiones (revisión adversarial 043) ----------------

    [Fact]
    public async Task Regresion_FA_bajar_el_precio_cuenta_como_descuento_y_solo_lo_autoriza_el_gerente()
    {
        // Regresión (revisión adversarial 043, F-A): con precio 50 y 0 % de descuento el vendedor daba 50 % sin el gerente.
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "FAA430101AAA");
        var bajo = await vendedor.PostAsync("/api/data/Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 50 } } });
        Assert.True(bajo.Status == 400 && bajo.Text.Contains("85 % del precio de lista"), bajo.Text);
        // 90 × (1 − 5 %) = 85.50 ≥ 85: dentro del 15 %.
        await vendedor.CreateAsync("Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 90, DescuentoPct = 5 } } });
        var pedido = await vendedor.PostAsync("/api/data/Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 50 } } });
        Assert.Equal(400, pedido.Status);
        await gerente.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 50 } } });
    }

    [Fact]
    public async Task Regresion_FB_los_pedidos_abiertos_cuentan_en_el_credito_tambien_uno_tras_otro_y_a_la_vez()
    {
        // Regresión (revisión adversarial 043, F-B): solo contaban las facturas; N pedidos de 900 pasaban un límite de 1 000.
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "FBB430101AAA");
        var row = await gerente.RecordAsync("Cliente", cliente.Id());
        await Ok(gerente.PutAsync($"/api/data/Cliente/{cliente.Id()}", new { LimiteCredito = 1000, _rowVersion = row.Str("_rowVersion") }));

        var primero = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("a"), 6, 100)); // 696
        Assert.Equal(696m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("PedidosAbiertos"));
        var segundo = await vendedor.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 3, PrecioUnitario = 100 } } });
        var negado = await vendedor.TransitionAsync("Pedido", segundo, "confirmar"); // 696 + 348 > 1 000
        Assert.True(negado.Status == 400 && negado.Text.Contains("pedidos abiertos"), negado.Text);

        // Al cancelar el primero deja de contar.
        await Ok(vendedor.PostAsync($"/api/data/Pedido/{primero.Id()}/transition", new { transition = "cancelar", comment = "IT 043", _rowVersion = primero.Str("_rowVersion") }));
        Assert.Equal(0m, (await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("PedidosAbiertos"));

        // A la vez: cada uno cabe solo (348 + 348 + 348 > 1 000), pero juntos solo dos.
        var pedidos = new List<JsonNode> { await vendedor.RecordAsync("Pedido", segundo.Id()) };
        for (var i = 0; i < 2; i++)
            pedidos.Add(await vendedor.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Partidas = new[] { new { Producto = Id("a"), Cantidad = 3, PrecioUnitario = 100 } } }));
        var results = await Concurrently(3, i => vendedor.TransitionAsync("Pedido", pedidos[i], "confirmar"));
        Invariant.Statuses(results, 200, 200, 400);
        Invariant.AtMost((await vendedor.RecordAsync("Cliente", cliente.Id())).Dec("PedidosAbiertos")!.Value, 1000, "pedidos abiertos");
    }

    [Fact]
    public async Task Regresion_FC_el_tipo_de_cambio_solo_lo_cambia_el_gerente()
    {
        // Regresión (revisión adversarial 043, F-C): el vendedor capturaba un tipo de cambio arbitrario (p. ej. para quedar
        // bajo el límite de crédito en moneda extranjera).
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var usd = (await vendedor.GetAsync("/api/data/Moneda", new { search = "USD" })).Items.Nodes().First().Id();
        using var admin = await host.AdminAsync();
        Assert.Equal(200, (await admin.PutAsync("/api/admin/exchange-rates", new { from = "USD", to = "MXN", date = DateTime.Now.ToString("yyyy-MM-dd"), rate = 18 })).Status);
        var cliente = await ClienteAsync(vendedor, "FCC430101AAA");
        var pedido = await vendedor.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Moneda = usd, TipoCambio = 0.0001,
            Partidas = new[] { new { Producto = Id("a"), Cantidad = 1 } } });
        Assert.Equal(18m, pedido.Dec("TipoCambio"));
        var g = await gerente.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Moneda = usd, TipoCambio = 17.5,
            Partidas = new[] { new { Producto = Id("a"), Cantidad = 1 } } });
        Assert.Equal(17.5m, g.Dec("TipoCambio"));
    }

    [Fact]
    public async Task Regresion_FE_el_descuento_que_capturo_el_gerente_lo_acepta_el_gerente()
    {
        // Revisión adversarial 043 (F-E): la regla vale al crear el pedido con quien acepta. Se mantiene a propósito (R-043-4):
        // el vendedor no convierte en pedido un descuento que no puede autorizar; el mensaje lo dice.
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(vendedor, "FEE430101AAA");
        var cot = await gerente.CreateAsync("Cotizacion", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Vendedor = host.Ids[Vendedor],
            Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, DescuentoPct = 20 } } });
        cot = await Ok(vendedor.TransitionAsync("Cotizacion", await vendedor.RecordAsync("Cotizacion", cot.Id()), "enviar"));
        var negado = await vendedor.TransitionAsync("Cotizacion", cot, "aceptar");
        Assert.True(negado.Status == 400 && negado.Text.Contains("pídele que capture o acepte"), negado.Text);
        await Ok(gerente.TransitionAsync("Cotizacion", cot, "aceptar"));
    }

    [Fact]
    public async Task Regresion_FG_una_aplicacion_que_redondea_a_cero_en_moneda_base_no_deja_partida_vacia_en_la_poliza()
    {
        // Regresión (revisión adversarial 043, F-G): 0.01 EUR × 0.4 = 0.00 dejaba una partida sin cargo ni abono y el libro
        // rechazaba el cobro completo.
        using var admin = await host.AdminAsync();
        using var vendedor = await As(Vendedor);
        using var gerente = await As(Gerente);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        Assert.Equal(200, (await admin.PutAsync("/api/admin/exchange-rates", new { from = "EUR", to = "MXN", date = DateTime.Now.ToString("yyyy-MM-dd"), rate = 0.4 })).Status);
        var eur = (await vendedor.GetAsync("/api/data/Moneda", new { search = "EUR" })).Items.Nodes().First().Id();
        var cliente = await ClienteAsync(vendedor, "FGG430101AAA");
        var pedido = await gerente.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"), Moneda = eur, Vendedor = host.Ids[Vendedor],
            Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 100 } } });
        pedido = await Ok(gerente.TransitionAsync("Pedido", pedido, "confirmar"));
        await Ok(surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 1)), "confirmar"));
        pedido = await gerente.RecordAsync("Pedido", pedido.Id());
        var fac = await Ok(cobranza.TransitionAsync("Factura", await FacturaAsync(cobranza, pedido, (0, 1)), "emitir"));
        var cobro = await cobranza.CreateAsync("Cobro", new { Cliente = cliente.Id(), Moneda = eur, CuentaBanco = host.Cuentas["1100"],
            Aplicaciones = new object[] { new { Factura = fac.Id(), Importe = 0.01 }, new { Factura = fac.Id(), Importe = 100 } } });
        cobro = await Ok(cobranza.TransitionAsync("Cobro", cobro, "aplicar"));
        Assert.Equal(["1100:40/0", "1150:0/40"], Asientos(await PolizaAsync(cobro.Str("Folio")!), Codigos));
    }

    [Fact]
    public async Task Regresion_FH_la_factura_es_del_almacen_y_vendedor_del_pedido_y_el_banco_es_una_cuenta_de_activo()
    {
        // Regresión (revisión adversarial 043, F-H): facturación elegía otro almacén (otras cuentas), otro vendedor (otro dueño)
        // o la cuenta de ventas como banco.
        using var vendedor = await As(Vendedor);
        using var surtidor = await As(Surtidor);
        using var cobranza = await As(Cobranza);
        var cliente = await ClienteAsync(vendedor, "FHH430101AAA");
        var pedido = await PedidoAsync(vendedor, cliente.Id(), Id("almacen"), (Id("a"), 1, 100));
        await Ok(surtidor.TransitionAsync("Embarque", await EmbarqueAsync(surtidor, pedido, Id("almacen"), (0, 1)), "confirmar"));
        pedido = await vendedor.RecordAsync("Pedido", pedido.Id());
        var linea = new[] { new { PartidaPedido = Lines(pedido)[0]!.Id(), Cantidad = 1 } };
        var otroAlmacen = await cobranza.PostAsync("/api/data/Factura", new { Pedido = pedido.Id(), Almacen = Id("sucursal"), Partidas = linea });
        Assert.True(otroAlmacen.Status == 400 && otroAlmacen.Text.Contains("almacén del pedido"), otroAlmacen.Text);
        var otroVendedor = await cobranza.PostAsync("/api/data/Factura", new { Pedido = pedido.Id(), Vendedor = host.Ids[Vendedor2], Partidas = linea });
        Assert.True(otroVendedor.Status == 400 && otroVendedor.Text.Contains("vendedor del pedido"), otroVendedor.Text);
        var fac = await Ok(cobranza.TransitionAsync("Factura", await cobranza.CreateAsync("Factura", new { Pedido = pedido.Id(), Partidas = linea }), "emitir"));
        foreach (var cuenta in new[] { "4100", "1150" })
        {
            var r = await cobranza.PostAsync("/api/data/Cobro", new { Cliente = cliente.Id(), CuentaBanco = host.Cuentas[cuenta], Aplicaciones = new[] { new { Factura = fac.Id(), Importe = 1 } } });
            Assert.True(r.Status == 400, $"{cuenta}: {r.Text}");
        }
    }

    [Fact]
    public async Task Regresion_exploratoria_F01_F02_cliente_generico_en_pesos_y_sin_tipo_de_cambio_un_mensaje_que_dice_que_hacer()
    {
        using var admin = await host.AdminAsync();
        using var vendedor = await As(Vendedor);
        // Regresión (revisión exploratoria 043, F-01): «Público en general» llegaba sin moneda y el documento la pedía a mano.
        var generico = (await vendedor.GetAsync("/api/data/Cliente", new { search = "Público" })).Items.Nodes().Single();
        Assert.Equal("MXN", generico.Str("Moneda__label"));
        // Regresión (revisión exploratoria 043, F-02): sin tipo de cambio registrado el vendedor veía «Tipo de cambio es
        // obligatorio» en un campo que no puede capturar.
        var gbp = (await admin.CreateAsync("Moneda", new { Clave = "GBP", Nombre = "Libra esterlina" })).Id();
        var res = await vendedor.PostAsync("/api/data/Cotizacion", new { Cliente = generico.Id(), Almacen = Id("almacen"), Moneda = gbp,
            Partidas = new[] { new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 100 } } });
        Assert.True(res.Status == 400 && res.Text.Contains("Tipos de cambio"), res.Text);
        Assert.DoesNotContain("es obligatorio", res.Text);
    }

    [Fact]
    public async Task Regresion_FF_un_embarque_de_500_partidas_no_se_confirma_con_un_error_claro()
    {
        // Regresión (revisión adversarial 043, F-F): la póliza de 500 + 1 partidas pasaba el tope y el error no lo explicaba.
        using var gerente = await As(Gerente);
        var cliente = await ClienteAsync(gerente, "FFF430101AAA");
        var pedido = await gerente.CreateAsync("Pedido", new { Cliente = cliente.Id(), Almacen = Id("almacen"),
            Partidas = Enumerable.Range(0, 500).Select(_ => new { Producto = Id("a"), Cantidad = 1, PrecioUnitario = 100 }).ToArray() });
        pedido = await Ok(gerente.TransitionAsync("Pedido", pedido, "confirmar"));
        var emb = await gerente.CreateAsync("Embarque", new { Pedido = pedido.Id(), Almacen = Id("almacen"),
            Partidas = Lines(pedido).Nodes().Select(l => new { PartidaPedido = l.Id(), Cantidad = 1 }).ToArray() });
        var r = await gerente.TransitionAsync("Embarque", emb, "confirmar");
        Assert.True(r.Status == 400 && r.Text.Contains("de 1 a 499 partidas"), r.Text);
    }
}
