using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Environments;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 020: capas de costo (promedio y FIFO), libro de partida doble, create con partidas y los módulos.</summary>
public class AccountingTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);

    // ---------------- CostMath (H2) ----------------

    private static CostLayerState L(long id, decimal q, decimal v) => new(id, q, v);

    [Fact]
    public void Promedio_funde_las_capas_y_la_salida_toma_la_parte_proporcional()
    {
        var e1 = CostMath.Apply([], CostMethods.Average, 10m, 10m, null, 2);
        Assert.Equal(100m, e1.Cost);
        var open = e1.Layers.Select((l, i) => l with { Id = i + 1 }).ToList();
        var e2 = CostMath.Apply(open, CostMethods.Average, 10m, 13m, null, 2);
        Assert.Equal(130m, e2.Cost);
        // Una sola capa abierta con 20 piezas y 230 de valor (11.50 de promedio).
        Assert.Single(e2.Layers, l => l.Quantity > 0);
        Assert.Equal(230m, e2.Remaining);

        var merged = new List<CostLayerState> { L(3, 20m, 230m) };
        var s1 = CostMath.Apply(merged, CostMethods.Average, -3m, null, null, 2);
        Assert.Equal(-34.5m, s1.Cost);
        Assert.Equal(11.5m, s1.UnitCost);
        Assert.Equal(195.5m, s1.Remaining);

        // Sale todo: se lleva el valor completo, sin residuo de redondeo.
        var s2 = CostMath.Apply([L(3, 17m, 195.5m)], CostMethods.Average, -17m, null, null, 2);
        Assert.Equal(-195.5m, s2.Cost);
        Assert.Equal(0m, s2.Remaining);
    }

    [Fact]
    public void FIFO_consume_de_la_capa_mas_antigua_a_la_mas_nueva()
    {
        var layers = new List<CostLayerState> { L(1, 10m, 100m), L(2, 10m, 130m) };
        var s = CostMath.Apply(layers, CostMethods.Fifo, -12m, null, null, 2);
        Assert.Equal(-126m, s.Cost); // 10 × 10 + 2 × 13
        Assert.Equal(10.5m, s.UnitCost);
        Assert.Equal([0m, 8m], s.Layers.Select(l => l.Quantity));
        Assert.Equal(104m, s.Remaining);

        var e = CostMath.Apply(layers, CostMethods.Fifo, 5m, 20m, null, 2);
        Assert.Equal(3, e.Layers.Count); // capa nueva al final, las anteriores intactas
        Assert.Equal(new CostLayerState(null, 5m, 100m), e.Layers[^1]);
        Assert.Equal(10.6m, CostMath.PeekUnitCost(layers, 12.5m)); // (100 + 2.5 × 13) / 12.5
    }

    [Fact]
    public void Cada_consumo_se_redondea_una_vez_y_se_descuenta_exacto()
    {
        // 3 piezas por 10.00: cada pieza vale 3.333…; tres salidas de una pieza dejan la capa en cero exacto.
        var open = new List<CostLayerState> { L(1, 3m, 10m) };
        decimal total = 0;
        for (var i = 0; i < 3; i++)
        {
            var r = CostMath.Apply(open, CostMethods.Fifo, -1m, null, null, 2);
            total += r.Cost;
            open = r.Layers.ToList();
        }
        Assert.Equal(-10m, total);
        Assert.Equal(0m, open.Single().Value);
    }

    [Fact]
    public void Entrada_sin_costo_va_al_promedio_y_la_salida_sin_capas_al_respaldo()
    {
        var sinCosto = CostMath.Apply([L(1, 4m, 50m)], CostMethods.Average, 4m, null, null, 2);
        Assert.Equal(50m, sinCosto.Cost); // 12.50 vigente
        Assert.Equal(0m, CostMath.Apply([], CostMethods.Fifo, 2m, null, null, 2).Cost);

        var faltante = CostMath.Apply([L(1, 2m, 20m)], CostMethods.Fifo, -5m, null, 9m, 2);
        Assert.Equal(-47m, faltante.Cost); // 2 de capas (20) + 3 al respaldo (27)
        Assert.All(faltante.Layers, l => Assert.True(l.Quantity >= 0));
        Assert.Equal(-20m, CostMath.Apply([L(1, 2m, 20m)], CostMethods.Fifo, -5m, null, null, 2).Cost);
        Assert.Null(CostMath.PeekUnitCost([], 3m));
    }

    [Fact]
    public void Costo_negativo_o_metodo_desconocido_se_rechazan()
    {
        Assert.Throws<AppException>(() => CostMath.Apply([], CostMethods.Fifo, 1m, -1m, null, 2));
        Assert.Throws<AppException>(() => CostMath.Apply([], "lifo", 1m, 1m, null, 2));
    }

    [Fact]
    public void La_llave_de_las_capas_distingue_empresa_y_normaliza_valores()
    {
        var book = new EntityDef { Name = "Existencia", Label = "Existencia", LabelPlural = "Existencias", PerCompany = true };
        Assert.Equal(CostMath.KeyHash(book, 1, [5L, 7L]), CostMath.KeyHash(book, 1, [5m, 7.0m]));
        Assert.NotEqual(CostMath.KeyHash(book, 1, [5L, 7L]), CostMath.KeyHash(book, 2, [5L, 7L]));
        Assert.NotEqual(CostMath.KeyHash(book, 1, [5L, 7L]), CostMath.KeyHash(book, 1, [7L, 5L]));
    }

    // ---------------- Módulos (H4, H5) ----------------

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    private static ModulePackage Package(string file) =>
        JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), PackageJson.Read)!;

    private static string Errors(CompiledPlan c) => string.Join(" | ", c.Preview.Errors.Select(e => $"#{e.Operation} {e.Message}"));

    private sealed record Installed(IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards, HashSet<string> Roles);

    /// <summary>Instala los módulos en orden, con todas sus actualizaciones (lo que recibe una base nueva).</summary>
    private static Installed Install(params string[] files)
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        var roles = new HashSet<string>(["Administrador"], Ci);
        foreach (var file in files)
        {
            var p = Package(file);
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, $"{file}: {Errors(c)}");
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        return new(model, boards, roles);
    }

    private static Installed All() => Install("compras.json", "inventario.json", "contabilidad.json", "costeo.json");

    [Fact]
    public void Contabilidad_y_Costeo_compilan_sobre_Compras_e_Inventario()
    {
        var m = All().Model;
        Assert.Equal(["contabilidad", "inventario"], Package("costeo.json").Requires!.Order(Ci));
        Assert.All(["CuentaContable", "PeriodoContable", "Poliza"], n => Assert.True(m[n].PerCompany));

        var ledger = m["Poliza"].Ledger!;
        Assert.Equal(["Aplicada"], ledger.States);
        Assert.Equal("PeriodoContable", ledger.Period!.Entity);

        var valuar = m["MovimientoInventario"].Automations!.Single(a => a.Name == "Valuar").Actions.Single();
        Assert.Equal(AutomationActions.Cost, valuar.Type);
        Assert.Equal(["ActualizarExistencia", "Valuar"], m["MovimientoInventario"].Automations!.Select(a => a.Name));
        // La póliza de la recepción va después de la entrada al almacén (las automatizaciones corren en orden).
        Assert.Equal(["AcumularRecibido", "EntradaAlmacen", "PolizaEntrada", "MarcarPolizaPendiente"], m["Recepcion"].Automations!.Select(a => a.Name));
        Assert.Null(AutomationRules.FindCycle(m));
        // La tarea de cierre usa el rol de servicio del módulo.
        Assert.Equal("Cierre contable", m["PeriodoContable"].Schedules!.Single().Role);
    }

    [Fact]
    public void Inventario_1_5_llega_como_actualizacion_sobre_1_4()
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        var roles = new HashSet<string>(["Administrador"], Ci);
        var compras = Package("compras.json");
        var inventario = Package("inventario.json");
        roles.UnionWith(compras.AllRoles.Select(r => r.Name));
        roles.UnionWith(inventario.AllRoles.Select(r => r.Name));
        foreach (var (_, plan) in compras.FullChain.Concat(inventario.FullChain.Where(x => x.Label is not ("v1.5.0" or "v1.6.0" or "v1.6.1"))))
        {
            var c = PlanCompiler.Compile(plan, model, roles, boards);
            Assert.True(c.Preview.Valid, Errors(c));
            (model, boards) = (c.ResultingModel, c.Dashboards!);
        }
        Assert.Null(model["MovimientoInventario"].FindField("CostoUnitario"));

        var pending = inventario.PendingAfter("1.4.0");
        Assert.Equal(["1.5.0", "1.6.0", "1.6.1"], pending.Select(u => u.Version)); // 1.6.x: spec 022
        var up = PlanCompiler.Compile(pending[0].Plan, model, roles, boards);
        Assert.True(up.Preview.Valid, Errors(up));
        var after = up.ResultingModel;
        Assert.NotNull(after["MovimientoInventario"].FindField("CostoUnitario"));
        Assert.Contains("CONVERT(", after["RecepcionPartida"].FindField("CostoUnitario")!.Formula);
        // Las reglas anteriores del movimiento se conservan (setRules reemplaza la lista).
        Assert.Equal(["CantidadNoCero", "SoloAjusteNegativo", "AjusteSupervisor", "TraspasoConDocumento", "CostoNoNegativo"],
            after["MovimientoInventario"].Rules!.Select(r => r.Name));
        // El traspaso crea primero la entrada (valuada con UNITCOST sobre el origen) y después la salida.
        var mover = after["Traspaso"].Automations!.Single(a => a.Name == "MoverMercancia").Actions;
        Assert.Contains("UNITCOST('Existencia'", mover[0].Set!.Single(s => s.Field == "CostoUnitario").Value);
        Assert.Equal("'Traspaso salida'", mover[1].Set!.Single(s => s.Field == "Tipo").Value);
    }

    [Fact]
    public void Costeo_sin_Contabilidad_no_compila()
    {
        var baseModel = Install("compras.json", "inventario.json");
        var c = PlanCompiler.Compile(Package("costeo.json").Plan, baseModel.Model, baseModel.Roles, baseModel.Boards);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains("CuentaContable") || e.Message.Contains("Poliza"));
    }

    // ---------------- Libro (H1 CA5) ----------------

    private static CompiledPlan Compile(Installed i, params Operation[] ops) =>
        PlanCompiler.Compile(new ChangePlan { Description = "prueba", Operations = ops }, i.Model, i.Roles, i.Boards);

    private static LedgerDef Ledger => new()
    {
        Lines = "Partidas", Debit = "Cargo", Credit = "Abono", Date = "Fecha", States = ["Aplicada"],
        Period = new() { Entity = "PeriodoContable", Start = "Inicio", End = "Fin", Open = "Estado = 'Abierto'" },
    };

    [Theory]
    [InlineData("lines", "Concepto", "no es un campo de partidas")]
    [InlineData("debit", "Concepto", "debe ser un campo numérico")]
    [InlineData("credit", "Cargo", "deben ser campos distintos")]
    [InlineData("date", "Concepto", "debe ser un campo de fecha")]
    [InlineData("states", "Contabilizada", "no existe en el proceso")]
    [InlineData("period", "Inexistente", "no existe")]
    [InlineData("open", "Estado = ", "periodo abierto")]
    [InlineData("start", "Nombre", "debe ser un campo de fecha")]
    public void Un_libro_mal_declarado_se_rechaza_en_la_vista_previa(string what, string value, string expected)
    {
        var l = Ledger;
        l = what switch
        {
            "lines" => l with { Lines = value },
            "debit" => l with { Debit = value },
            "credit" => l with { Credit = value },
            "date" => l with { Date = value },
            "states" => l with { States = [value] },
            "period" => l with { Period = l.Period! with { Entity = value } },
            "open" => l with { Period = l.Period! with { Open = value } },
            _ => l with { Period = l.Period! with { Start = value } },
        };
        var c = Compile(All(), new SetLedgerOp { Entity = "Poliza", Ledger = l });
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains("libro") && e.Message.Contains(expected));
    }

    [Fact]
    public void Archivar_un_campo_del_libro_o_compartir_el_libro_con_periodos_por_empresa_se_rechaza()
    {
        var all = All();
        var archive = Compile(all, new ArchiveFieldOp { Entity = "PolizaPartida", Field = "Abono" });
        Assert.Contains(archive.Preview.Errors, e => e.Message.Contains("libro"));

        // Un catálogo compartido (Producto no es por empresa) no puede usar periodos por empresa.
        var ops = new Operation[]
        {
            new CreateEntityOp { Name = "Diario", Label = "Diario", LabelPlural = "Diarios", Fields =
                [new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date }] },
            new CreateEntityOp { Name = "DiarioPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
            [
                new FieldDef { Name = "Diario", Label = "Diario", Type = FieldTypes.Parent, ReferenceEntity = "Diario" },
                new FieldDef { Name = "Cargo", Label = "Cargo", Type = FieldTypes.Decimal },
                new FieldDef { Name = "Abono", Label = "Abono", Type = FieldTypes.Decimal },
            ] },
            new AddFieldOp { Entity = "Diario", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "DiarioPartida" } },
            new SetLedgerOp { Entity = "Diario", Ledger = Ledger with { States = null } },
        };
        var shared = Compile(all, ops);
        Assert.Contains(shared.Preview.Errors, e => e.Message.Contains("compartido"));
        // Sin periodos (y sin proceso: todo contabilizado) el mismo libro es válido.
        ops[^1] = new SetLedgerOp { Entity = "Diario", Ledger = Ledger with { States = null, Period = null } };
        Assert.True(Compile(all, ops).Preview.Valid, Errors(Compile(all, ops)));
    }

    // ---------------- Acción cost y create con partidas (H2 CA6, H3 CA3) ----------------

    private static Operation WithValuation(Func<AutomationAction, AutomationAction> change)
    {
        var all = All();
        var auto = all.Model["MovimientoInventario"].Automations!.Single(a => a.Name == "Valuar");
        return new SetAutomationsOp { Entity = "MovimientoInventario", Merge = true, Automations = [auto with { Actions = [change(auto.Actions[0])] }] };
    }

    [Theory]
    [InlineData("method", "método 'lifo' no válido")]
    [InlineData("costField", "debe ser un campo numérico capturable")]
    [InlineData("quantity", "la cantidad, con signo")]
    [InlineData("match", "no es un campo de Existencia")]
    [InlineData("lines", "no es un campo de partidas")]
    [InlineData("field", "recibe el valor de las capas")]
    [InlineData("unitCost", "costo unitario")]
    public void Una_valuacion_mal_declarada_se_rechaza(string what, string expected)
    {
        var op = WithValuation(a => what switch
        {
            "method" => a with { Method = "'lifo'" },
            "costField" => a with { CostField = "Efecto" },
            "quantity" => a with { Quantity = "Tipo" },
            "match" => a with { Match = [new("Bodega", "Almacen")] },
            "lines" => a with { Lines = "Partidas" },
            "field" => a with { Field = "CostoPromedio" },
            _ => a with { UnitCost = "Inexistente * 2" },
        });
        var c = Compile(All(), op);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(expected));
    }

    [Fact]
    public void Una_poliza_automatica_mal_declarada_se_rechaza()
    {
        var all = All();
        var auto = all.Model["Recepcion"].Automations!.Single(a => a.Name == "PolizaEntrada");
        Operation With(AutomationAction a) => new SetAutomationsOp { Entity = "Recepcion", Merge = true, Automations = [auto with { Actions = [a] }] };
        var create = auto.Actions[0];

        var noLines = Compile(all, With(create with { CreateLines = [create.CreateLines![0] with { Field = "Concepto" }] }));
        Assert.Contains(noLines.Preview.Errors, e => e.Message.Contains("no es un campo de partidas de Poliza"));

        var noAccount = Compile(all, With(create with { CreateLines = [create.CreateLines![0] with { Set = [new("Cargo", "CostoTotal")] }] }));
        Assert.Contains(noAccount.Preview.Errors, e => e.Message.Contains("falta asignar 'Cuenta'"));

        var badTransition = Compile(all, With(create with { Transition = "contabilizar" }));
        Assert.Contains(badTransition.Preview.Errors, e => e.Message.Contains("la transición 'contabilizar' no existe en Poliza"));

        var badExpr = Compile(all, With(create with { CreateLines = [create.CreateLines![0] with { When = "Inexistente > 0" }] }));
        Assert.Contains(badExpr.Preview.Errors, e => e.Message.Contains("partida 1 (cuándo)"));
    }

    [Fact]
    public void Crear_con_transicion_cuenta_como_arista_hacia_las_automatizaciones_del_destino()
    {
        var m = All().Model;
        var action = m["Recepcion"].Automations!.Single(a => a.Name == "PolizaEntrada").Actions[0];
        var effects = AutomationRules.Effects(m["Recepcion"], action, m).Select(x => (x.Entity.Name, x.Event)).ToList();
        Assert.Contains(("Poliza", AutomationEvents.Create), effects);
        Assert.Contains(("Poliza", AutomationEvents.EnterState), effects);
    }

    // ---------------- UNITCOST (H2 CA4) ----------------

    [Fact]
    public void UNITCOST_exige_un_catalogo_existente_entre_comillas()
    {
        var m = All().Model;
        var traspaso = m["TraspasoPartida"];
        Assert.Empty(ExprAnalyzer.Analyze("UNITCOST('Existencia', Cantidad, Traspaso.AlmacenOrigen, Producto)", traspaso, m, new HashSet<string>()).Errors);
        Assert.Contains(ExprAnalyzer.Analyze("UNITCOST('Bodega', Cantidad, Producto)", traspaso, m, new HashSet<string>()).Errors, e => e.Contains("UNITCOST"));
        Assert.Contains(ExprAnalyzer.Analyze("UNITCOST(Producto, Cantidad, Producto)", traspaso, m, new HashSet<string>()).Errors, e => e.Contains("UNITCOST"));
        Assert.NotEmpty(ExprAnalyzer.Analyze("UNITCOST('Existencia', Cantidad)", traspaso, m, new HashSet<string>()).Errors);
    }

    private sealed class Costs(decimal? value) : ICostLayers
    {
        public (string Book, decimal Quantity, IReadOnlyList<object?> Key, string? Company)? Last { get; private set; }
        public decimal? UnitCost(string book, decimal quantity, IReadOnlyList<object?> key, string? company)
        {
            Last = (book, quantity, key, company);
            return value;
        }
    }

    private sealed class Scope(Dictionary<string, object?> values) : IExprScope
    {
        public object? Field(string name) => values.GetValueOrDefault(name);
        public object? Related(string refField, string field) => null;
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
    }

    [Fact]
    public void UNITCOST_pasa_la_llave_y_la_empresa_del_contexto()
    {
        var costs = new Costs(12.5m);
        var env = new ExprEnv(1, [], 1) { Company = "E1", Costs = costs };
        var v = ExprEvaluator.Evaluate("UNITCOST('Existencia', Cantidad, Almacen, Producto)",
            new Scope(new() { ["Cantidad"] = 4m, ["Almacen"] = 3L, ["Producto"] = 9L }), env);
        Assert.Equal(12.5m, v);
        Assert.Equal("Existencia", costs.Last!.Value.Book);
        Assert.Equal(4m, costs.Last.Value.Quantity);
        Assert.Equal([3m, 9m], costs.Last.Value.Key);
        Assert.Equal("E1", costs.Last.Value.Company);
        // Sin capas disponibles (fuera de la base) es desconocido, no cero.
        Assert.Null(ExprEvaluator.Evaluate("UNITCOST('Existencia', 1, 1)", new Scope([]), new ExprEnv(1, [], 1)));
    }

    // ---------------- Paquetes (009) ----------------

    [Fact]
    public void El_libro_viaja_en_el_diff_de_entornos()
    {
        var all = All();
        var target = new Dictionary<string, EntityDef>(all.Model, Ci) { ["Poliza"] = all.Model["Poliza"] with { Ledger = null } };
        var diff = ModelDiff.Compute(all.Model, all.Boards, target, all.Boards, "prueba");
        Assert.Contains(diff.Plan.Operations, o => o is SetLedgerOp { Entity: "Poliza", Ledger: not null });
    }
}
