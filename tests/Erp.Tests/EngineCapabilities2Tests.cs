using System.Text.Json;
using Erp.Api.Assistant;
using Erp.Api.Data;
using Erp.Api.Environments;
using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 023: merge en las listas, gravedad de las reglas y política de avisos (compilador y vista previa).</summary>
public class MergeAndWarningRulesTests
{
    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };

    private static ViewDef View(string name, string label) => new() { Name = name, Label = label };

    [Fact]
    public void H1_CA1_merge_reemplaza_en_su_lugar_y_agrega_al_final()
    {
        var merged = PlanCompiler.MergeByName([View("A", "a"), View("B", "b"), View("C", "c")], [View("b", "B nueva"), View("D", "d")], v => v.Name);
        Assert.Equal(["A", "b", "C", "D"], merged.Select(v => v.Name));
        Assert.Equal("B nueva", merged[1].Label);
        // Un nombre repetido en el plan se conserva repetido para que la validación lo reporte.
        Assert.Equal(["1", "2"], PlanCompiler.MergeByName([View("A", "a")], [View("A", "1"), View("A", "2")], v => v.Name).Select(v => v.Label));
    }

    [Fact]
    public void H1_CA1_setViews_reports_webhooks_y_schedules_con_merge_conservan_lo_demas()
    {
        var (model, boards, roles) = EngineCapabilitiesTests.Installed("compras.json", "inventario.json", "contabilidad.json", "costeo.json");
        Assert.Equal(["PolizasPendientes"], model["Recepcion"].Views!.Select(v => v.Name));
        var c = PlanCompiler.Compile(Plan(
            new SetViewsOp { Entity = "Recepcion", Merge = true, Views = [new ViewDef { Name = "Confirmadas", Label = "Confirmadas", State = "Confirmada" }] },
            new SetReportsOp { Entity = "OrdenCompra", Merge = true, Reports = [new ReportDef { Name = "Nuevo", Label = "Nuevo", Measures = [new ReportMeasure { Fn = "count" }] }] },
            new SetWebhooksOp { Entity = "Existencia", Merge = true, Webhooks = [] },
            new SetSchedulesOp { Entity = "PeriodoContable", Merge = true, Schedules = [] }), model, roles, boards);
        Assert.True(c.Preview.Valid, string.Join(" | ", c.Preview.Errors.Select(e => e.Message)));
        Assert.Equal(["PolizasPendientes", "Confirmadas"], c.ResultingModel["Recepcion"].Views!.Select(v => v.Name));
        Assert.Equal(model["OrdenCompra"].Reports!.Count + 1, c.ResultingModel["OrdenCompra"].Reports!.Count);
        Assert.Equal(model["Existencia"].Webhooks!.Select(w => w.Name), c.ResultingModel["Existencia"].Webhooks!.Select(w => w.Name));
        Assert.Equal(model["PeriodoContable"].Schedules!.Select(s => s.Name), c.ResultingModel["PeriodoContable"].Schedules!.Select(s => s.Name));
        Assert.Contains(c.Preview.Steps, s => s.Description.StartsWith("Agregar o actualizar 1 vista"));
        // Sin merge la vista previa del asistente dice qué quita; con merge, no.
        Assert.DoesNotContain(PlanWarnings.For(Plan(new SetViewsOp { Entity = "Recepcion", Merge = true, Views = [] }), model), w => w.Contains("Quita"));
        Assert.Contains(PlanWarnings.For(Plan(new SetViewsOp { Entity = "Recepcion", Views = [] }), model), w => w.Contains("Quita vistas"));
    }

    [Fact]
    public void H1_opciones_nuevas_omitidas_no_cambian_la_forma_canonica_de_los_paquetes()
    {
        Assert.DoesNotContain("merge", JsonSerializer.Serialize<Operation>(new SetViewsOp { Entity = "X", Views = [] }, PackageJson.Canonical));
        Assert.DoesNotContain("severity", JsonSerializer.Serialize(new BusinessRule { Name = "R", Assert = "TRUE", Message = "m" }, PackageJson.Canonical));
        Assert.DoesNotContain("onWarning", JsonSerializer.Serialize(new WorkflowTransition { Name = "T", Label = "T", To = "A" }, PackageJson.Canonical));
    }

    [Fact]
    public void H2_CA1_gravedad_y_politica_invalidas_se_rechazan()
    {
        var (model, boards, roles) = EngineCapabilitiesTests.Installed("compras.json");
        ChangePlan Rule(string severity) => Plan(new SetRulesOp
        {
            Entity = "Proveedor", Merge = true, Rules = [new BusinessRule { Name = "Prueba", Assert = "TRUE", Message = "m", Severity = severity }],
        });
        Assert.Contains(PlanCompiler.Compile(Rule("info"), model, roles, boards).Preview.Errors, e => e.Message.Contains("gravedad 'info'"));
        var okRule = PlanCompiler.Compile(Rule("warning"), model, roles, boards).Preview;
        Assert.True(okRule.Valid, string.Join(" | ", okRule.Errors.Select(e => e.Message)));
        var wf = model["OrdenCompra"].Workflow!;
        ChangePlan Policy(string policy) => Plan(new SetWorkflowOp
        {
            Entity = "OrdenCompra", Workflow = wf with { Transitions = [.. wf.Transitions.Select((t, i) => i == 0 ? t with { OnWarning = policy } : t)] },
        });
        Assert.Contains(PlanCompiler.Compile(Policy("ask"), model, roles, boards).Preview.Errors, e => e.Message.Contains("si hay avisos"));
        foreach (var ok in WarningPolicies.All)
        {
            var c = PlanCompiler.Compile(Policy(ok), model, roles, boards).Preview;
            Assert.True(c.Valid, ok + ": " + string.Join(" | ", c.Errors.Select(e => e.Message)));
        }
    }

    [Fact]
    public void H2_CA5_un_aviso_que_lee_un_campo_oculto_no_se_muestra()
    {
        var e = new EntityDef
        {
            Name = "Venta", Label = "Venta", LabelPlural = "Ventas",
            Fields = [new FieldDef { Name = "Monto", Label = "Monto", Type = "decimal" }, new FieldDef { Name = "Margen", Label = "Margen", Type = "decimal" }],
            Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Margen", Read = ["Gerente"] }] },
        };
        var model = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Venta"] = e };
        var margen = new BusinessRule { Name = "MargenBajo", Field = "Monto", Assert = "Margen > 10", Message = "Margen bajo.", Severity = "warning" };
        var monto = new BusinessRule { Name = "MontoAlto", Field = "Monto", When = "Monto > 0", Assert = "Monto < 100", Message = "Monto alto.", Severity = "warning" };
        List<RuleWarning> all = [new("Monto", margen.Message, e, margen), new("Monto", monto.Message, e, monto)];
        UserContext U(string role) => new() { Id = 1, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = [role] };
        Assert.Equal(["Monto alto."], RecordService.VisibleWarnings(all, model, U("Vendedor")).Select(w => w.Message));
        Assert.Equal(["Margen bajo.", "Monto alto."], RecordService.VisibleWarnings(all, model, U("Gerente")).Select(w => w.Message));
        Assert.Equal(new Dictionary<string, string[]> { ["Monto"] = ["Margen bajo.", "Monto alto."] }, RecordService.WarningMap(all));
    }

    [Fact]
    public void H2_regla_que_pasa_a_aviso_se_destaca_en_la_vista_previa()
    {
        var (model, _, _) = EngineCapabilitiesTests.Installed("compras.json", "inventario.json", "contabilidad.json", "costeo.json");
        var rule = model["Recepcion"].Rules!.Single(r => r.Name == "PolizaConCuentas");
        var w = PlanWarnings.For(Plan(new SetRulesOp { Entity = "Recepcion", Merge = true, Rules = [rule with { Severity = "warning" }] }), model);
        Assert.Contains(w, x => x.Contains("«PolizaConCuentas»") && x.Contains("pasa a aviso"));
        Assert.DoesNotContain(w, x => x.Contains("Quita"));
    }
}

/// <summary>Spec 023 (H3): rutas de hasta tres saltos en el lenguaje de expresiones.</summary>
public class ThreeLevelPathTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private sealed class Scope(Dictionary<string, object?> paths) : IExprScope
    {
        public object? Field(string name) => paths.GetValueOrDefault(name);
        public object? Related(string refField, string field) => paths.GetValueOrDefault($"{refField}.{field}");
        public object? RelatedPath(string refField, IReadOnlyList<string> path) => paths.GetValueOrDefault($"{refField}.{string.Join(".", path)}");
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
    }

    [Fact]
    public void H3_CA1_el_parser_acepta_hasta_cuatro_segmentos()
    {
        var p = Assert.IsType<PathRef>(ExprParser.Parse("Recepcion.OrdenCompra.Moneda.Clave"));
        Assert.Equal("Recepcion", p.Head);
        Assert.Equal(["OrdenCompra", "Moneda", "Clave"], p.Rest);
        Assert.Null(p.Single);
        Assert.Contains("máximo 3 niveles", Assert.Throws<ExprException>(() => ExprParser.Parse("A.B.C.D.E = 1")).Message);
        Assert.Throws<ExprException>(() => ExprParser.Parse("A.B."));
    }

    [Fact]
    public void H3_CA1_el_evaluador_lee_la_ruta_completa()
    {
        var s = new Scope(new() { ["Recepcion.OrdenCompra.Moneda.Clave"] = "USD", ["Producto.Nombre"] = "Tornillo" });
        Assert.Equal("USD", ExprEvaluator.Evaluate("Recepcion.OrdenCompra.Moneda.Clave", s, ExprEnv.System));
        Assert.Equal("Tornillo", ExprEvaluator.Evaluate("Producto.Nombre", s, ExprEnv.System));
        Assert.Null(ExprEvaluator.Evaluate("Recepcion.OrdenCompra.Proveedor.Nombre", s, ExprEnv.System));
    }

    private static IReadOnlyDictionary<string, EntityDef> Model() =>
        EngineCapabilitiesTests.Installed("compras.json", "inventario.json").Model;

    [Theory]
    [InlineData("Recepcion.OrdenCompra.Moneda.Clave = 'USD'")]
    [InlineData("Recepcion.OrdenCompra.Proveedor.RazonSocial <> ''")]
    [InlineData("PartidaOrden.Orden.Moneda.Clave = 'MXN'")]
    [InlineData("Recepcion.Almacen.Nombre <> ''")]
    public void H3_CA1_rutas_validas_contra_los_metadatos(string expr)
    {
        var m = Model();
        Assert.Empty(ExprAnalyzer.Analyze(expr, m["RecepcionPartida"], m, new HashSet<string>()).Errors);
    }

    [Theory]
    [InlineData("Recepcion.OrdenCompra.Nada.Clave = 1", "'Nada' no existe en OrdenCompra ('Recepcion.OrdenCompra.Nada.Clave')")]
    [InlineData("Recepcion.Folio.Clave = 1", "'Folio' de Recepcion no es una referencia")]
    [InlineData("Recepcion.OrdenCompra.Partidas.Cantidad = 1", "'Partidas' no existe en OrdenCompra")]
    [InlineData("Producto.Descripcion.X = 1", "'Descripcion' de Producto no es una referencia")]
    public void H3_CA1_rutas_invalidas_se_rechazan_con_el_salto(string expr, string message)
    {
        var m = Model();
        Assert.Contains(ExprAnalyzer.Analyze(expr, m["RecepcionPartida"], m, new HashSet<string>()).Errors, x => x.Contains(message));
    }

    [Fact]
    public void H3_CA1_SUM_sigue_leyendo_un_campo_de_la_partida()
    {
        var m = Model();
        Assert.Contains(ExprAnalyzer.Analyze("SUM(Partidas.Producto.Costo)", m["Recepcion"], m, new HashSet<string>()).Errors,
            x => x.Contains("SUM suma un campo de la partida"));
    }

    [Fact]
    public void H3_CA2_el_filtro_en_sql_anida_una_subconsulta_por_salto()
    {
        var m = Model();
        var bag = new RecordFilter.ParamBag("@rf");
        var sql = RecordFilter.TranslateRollup("Recepcion.OrdenCompra.Moneda.Clave = 'USD'", m["RecepcionPartida"], m, bag);
        Assert.True(sql.Contains("(SELECT r2.[Clave] FROM [app].[Moneda] r2 WHERE r2.[Id] = (SELECT r1.[Moneda] FROM [app].[OrdenCompra] r1 WHERE r1.[Id] = " +
                        "(SELECT r0.[OrdenCompra] FROM [app].[Recepcion] r0 WHERE r0.[Id] = t.[Recepcion] AND r0.[IsDeleted] = 0) " +
                        "AND r1.[IsDeleted] = 0) AND r2.[IsDeleted] = 0)"), sql);
        Assert.Equal("USD", bag.Items.Single().Value);
        // Las reglas de acceso siguen sin rutas.
        Assert.Throws<ExprException>(() => RecordFilter.Translate("Recepcion.OrdenCompra.Moneda.Clave = 'USD'", m["RecepcionPartida"],
            new RecordFilter.Who(1, null, []), new RecordFilter.ParamBag()));
    }

    [Fact]
    public void H3_CA3_un_campo_oculto_en_el_segundo_o_tercer_salto_cuenta_como_oculto()
    {
        var m = Model().ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        m["Moneda"] = m["Moneda"] with { Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Nombre", Read = ["Gerente"] }] } };
        var vendedor = new UserContext { Id = 7, Username = "v", DisplayName = "V", SecurityStamp = Guid.Empty, Roles = ["Vendedor"] };
        var gerente = new UserContext { Id = 8, Username = "g", DisplayName = "G", SecurityStamp = Guid.Empty, Roles = ["Gerente"] };
        bool Hidden(string expr, UserContext u) => RecordService.ReadsHidden(ExprParser.Parse(expr), m["RecepcionPartida"], m, u);
        // Tercer salto (el campo final).
        Assert.True(Hidden("Recepcion.OrdenCompra.Moneda.Nombre = 'x'", vendedor));
        Assert.False(Hidden("Recepcion.OrdenCompra.Moneda.Nombre = 'x'", gerente));
        Assert.False(Hidden("Recepcion.OrdenCompra.Moneda.Clave = 'x'", vendedor));
        // Segundo salto (el campo de referencia intermedio).
        m["OrdenCompra"] = m["OrdenCompra"] with { Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Moneda", Read = ["Gerente"] }] } };
        Assert.True(Hidden("Recepcion.OrdenCompra.Moneda.Clave = 'USD'", vendedor));
        Assert.False(Hidden("Recepcion.OrdenCompra.Proveedor.RazonSocial = 'x'", vendedor));
    }
}

/// <summary>Spec 023 (H3 CA4): pendiente 4 de la 022, recepciones sin moneda creadas antes de Inventario 1.5.0.</summary>
public class InventoryPathTests
{
    private sealed class Rates(Dictionary<(string, string), decimal> rates) : ICurrencyRates
    {
        public decimal? Rate(string from, string to, DateTime date) => rates.TryGetValue((from, to), out var r) ? r : null;
    }

    /// <summary>Partida de recepción en memoria: rutas de la partida, de la recepción y de la orden.</summary>
    private sealed class Line(Dictionary<string, object?> paths) : IExprScope
    {
        public object? Field(string name) => paths.GetValueOrDefault(name);
        public object? Related(string refField, string field) => paths.GetValueOrDefault($"{refField}.{field}");
        public object? RelatedPath(string refField, IReadOnlyList<string> path) => paths.GetValueOrDefault($"{refField}.{string.Join(".", path)}");
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
    }

    /// <summary>
    /// Regresión (spec 023, pendiente 4 de la 022): una recepción en borrador sin «Moneda» (anterior a Inventario 1.5.0) de una
    /// orden en dólares, con moneda base EUR, quedaba sin costo (la fórmula solo miraba Recepcion.Moneda y el respaldo de la
    /// orden es solo para base MXN). Con Inventario 1.7.0 toma la moneda de la orden por la ruta de tres saltos.
    /// </summary>
    [Fact]
    public void H3_CA4_recepcion_sin_moneda_usa_la_moneda_de_la_orden()
    {
        var (model, _, _) = EngineCapabilitiesTests.Installed("compras.json", "inventario.json");
        var formula = model["RecepcionPartida"].FindField("CostoUnitario")!.Formula!;
        var env = new ExprEnv(1, [], 1) { BaseCurrency = "EUR", Company = "E1", Rates = new Rates(new() { [("USD", "EUR")] = 0.9m }) };
        var line = new Line(new()
        {
            ["PartidaOrden.PrecioUnitario"] = 10m, ["PartidaOrden.DescuentoPct"] = 0m, ["PartidaOrden.CostoMXN"] = 180m,
            ["Recepcion.Moneda"] = null, ["Recepcion.Fecha"] = new DateTime(2026, 10, 1),
            ["Recepcion.OrdenCompra.Moneda.Clave"] = "USD",
        });
        Assert.Equal(9m, ExprEvaluator.Evaluate(formula, line, env));
        // Con la moneda capturada en la recepción, se usa esa (como en 1.6.1).
        line = new Line(new()
        {
            ["PartidaOrden.PrecioUnitario"] = 10m, ["PartidaOrden.DescuentoPct"] = 0m, ["Recepcion.Moneda"] = "USD",
            ["Recepcion.Fecha"] = new DateTime(2026, 10, 1), ["Recepcion.OrdenCompra.Moneda.Clave"] = "MXN",
        });
        Assert.Equal(9m, ExprEvaluator.Evaluate(formula, line, env));
        // Sin tipo de cambio ni respaldo, sigue vacío (1.6.1).
        Assert.Null(ExprEvaluator.Evaluate(formula, line, env with { Rates = new Rates([]) }));
    }
}
