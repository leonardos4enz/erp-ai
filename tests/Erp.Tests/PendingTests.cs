using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Environments;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>
/// Spec 014: pendientes menores. Sugerencias de Compras en la moneda del documento, suma del acumulado en SQL, folios de la
/// importación, campos locales que archiva un paquete y clave del bloqueo de un acumulado.
/// </summary>
public class PendingTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly DateTime Fecha = new(2026, 10, 1);

    // ---------------- H1: Compras 1.8.0 ----------------

    private sealed class Rates(Dictionary<(string, string), decimal> rates) : ICurrencyRates
    {
        public decimal? Rate(string from, string to, DateTime date) => rates.TryGetValue((from, to), out var r) ? r : null;
    }

    /// <summary>Partida en memoria: campos propios y de los registros relacionados (<c>Producto.X</c>, <c>Orden.X</c>).</summary>
    private sealed class Line(Dictionary<string, Dictionary<string, object?>> related) : IExprScope
    {
        public object? Field(string name) => null;
        public object? Related(string refField, string field) =>
            related.TryGetValue(refField, out var row) ? row.GetValueOrDefault(field) : null;
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
    }

    private static IReadOnlyDictionary<string, EntityDef> Compras()
    {
        IReadOnlyDictionary<string, EntityDef> model = new Dictionary<string, EntityDef>(Ci);
        var path = Path.Combine(ModulesFolder(), "compras.json");
        var p = JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(path), PackageJson.Read)!;
        var roles = p.AllRoles.Select(r => r.Name).Append("Administrador").ToHashSet(Ci);
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        foreach (var (_, plan) in p.FullChain)
        {
            var c = PlanCompiler.Compile(plan, model, roles, boards);
            Assert.True(c.Preview.Valid, string.Join(" | ", c.Preview.Errors.Select(e => e.Message)));
            (model, boards) = (c.ResultingModel, c.Dashboards!);
        }
        return model;
    }

    private static object? Suggest(string entity, string field, string baseCurrency, Dictionary<string, object?> producto,
        Dictionary<string, object?> documento, Dictionary<(string, string), decimal>? rates = null)
    {
        var e = Compras()[entity];
        var parent = e.ParentField!.Name;
        var env = new ExprEnv(1, [], 1) { BaseCurrency = baseCurrency, Company = "E1", Rates = new Rates(rates ?? []) };
        return ExprEvaluator.Evaluate(e.FindField(field)!.DefaultValue!, new Line(new() { ["Producto"] = producto, [parent] = documento }), env);
    }

    private static decimal Round2(object? v) => decimal.Round((decimal)v!, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void El_precio_sugerido_de_la_orden_esta_en_su_moneda()
    {
        // Empresa en MXN, costo último en MXN, orden en USD a 17.00: el precio se sugiere en dólares.
        var precio = Suggest("OrdenCompraPartida", "PrecioUnitario", "MXN",
            new() { ["CostoUltimo"] = 170m, ["MonedaCosto"] = "MXN" }, new() { ["TipoCambio"] = 17m, ["Fecha"] = Fecha });
        Assert.Equal(10m, Round2(precio));

        // Orden en la moneda base (tipo de cambio 1): el costo tal cual.
        Assert.Equal(18.5m, Round2(Suggest("OrdenCompraPartida", "PrecioUnitario", "MXN",
            new() { ["CostoUltimo"] = 18.5m, ["MonedaCosto"] = "MXN" }, new() { ["TipoCambio"] = 1m, ["Fecha"] = Fecha })));

        // Empresa en USD, costo registrado en MXN por otra empresa: se convierte a dólares con el tipo de la fecha.
        Assert.Equal(10m, Round2(Suggest("OrdenCompraPartida", "PrecioUnitario", "USD",
            new() { ["CostoUltimo"] = 200m, ["MonedaCosto"] = "MXN" }, new() { ["TipoCambio"] = 1m, ["Fecha"] = Fecha },
            new() { [("MXN", "USD")] = 0.05m })));
    }

    [Fact]
    public void Sin_tipo_de_cambio_o_sin_producto_no_se_sugiere_nada()
    {
        // Costo en MXN, empresa en USD y ningún tipo registrado: vacío, nunca el número en otra moneda.
        Assert.Null(Suggest("OrdenCompraPartida", "PrecioUnitario", "USD",
            new() { ["CostoUltimo"] = 200m, ["MonedaCosto"] = "MXN" }, new() { ["TipoCambio"] = 1m, ["Fecha"] = Fecha }));
        // Orden sin tipo de cambio (no hay tipo para su moneda).
        Assert.Null(Suggest("OrdenCompraPartida", "PrecioUnitario", "MXN",
            new() { ["CostoUltimo"] = 170m, ["MonedaCosto"] = "MXN" }, new() { ["TipoCambio"] = null, ["Fecha"] = Fecha }));
        // Partida aún sin producto: la aritmética trata el nulo como 0; la sugerencia no debe ser 0.
        Assert.Null(Suggest("OrdenCompraPartida", "PrecioUnitario", "MXN", [], new() { ["TipoCambio"] = 1m, ["Fecha"] = Fecha }));
        Assert.Null(Suggest("RequisicionPartida", "CostoEstimado", "MXN", [], new() { ["Fecha"] = Fecha }));
    }

    [Fact]
    public void El_costo_estimado_de_la_requisicion_esta_en_la_moneda_base()
    {
        Assert.Equal(10m, Round2(Suggest("RequisicionPartida", "CostoEstimado", "USD",
            new() { ["CostoUltimo"] = 200m, ["MonedaCosto"] = "MXN" }, new() { ["Fecha"] = Fecha }, new() { [("MXN", "USD")] = 0.05m })));
        // Sin moneda del costo (productos de antes de 1.7.0): se toma en la moneda base, como hasta ahora.
        Assert.Equal(32.8m, Round2(Suggest("RequisicionPartida", "CostoEstimado", "MXN", new() { ["CostoUltimo"] = 32.8m }, new() { ["Fecha"] = Fecha })));
    }

    [Fact]
    public void La_moneda_del_costo_se_captura_con_la_moneda_base_por_defecto()
    {
        var model = Compras();
        var moneda = model["Producto"].FindField("MonedaCosto")!;
        Assert.True(moneda.Editable);
        Assert.Equal("BASECURRENCY()", moneda.DefaultValue);
        Assert.Contains(model["Producto"].Rules!, r => r.Name == "MonedaCostoClave");
        Assert.Contains(model["Producto"].Rules!, r => r.Name == "IvaRango"); // setRules reemplaza: se conservan las anteriores
        // Decisión (CLARIFY): CostoMXN no se renombra; su etiqueta ya dice «moneda base».
        Assert.Equal("Costo unitario (moneda base)", model["OrdenCompraPartida"].FindField("CostoMXN")!.Label);
    }

    // ---------------- H2: acumulado en SQL ----------------

    private static IReadOnlyDictionary<string, EntityDef> Recepciones()
    {
        var plan = new ChangePlan { Operations =
        [
            new CreateEntityOp { Name = "Recep", Label = "Recepción", LabelPlural = "Recepciones", Fields =
                [new FieldDef { Name = "Nota", Label = "Nota", Type = FieldTypes.Text }] },
            new SetWorkflowOp { Entity = "Recep", Workflow = new Workflow
            {
                Initial = "Borrador",
                States = [new WorkflowState { Name = "Borrador", Label = "Borrador" }, new WorkflowState { Name = "Confirmada", Label = "Confirmada" }],
                Transitions = [new WorkflowTransition { Name = "confirmar", Label = "Confirmar", From = ["Borrador"], To = "Confirmada" }],
            } },
            new CreateEntityOp { Name = "Prod", Label = "Producto", LabelPlural = "Productos", Fields =
                [new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }, new FieldDef { Name = "Activo", Label = "Activo", Type = FieldTypes.Boolean }] },
            new CreateEntityOp { Name = "RecepPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
            [
                new FieldDef { Name = "Recep", Label = "Recepción", Type = FieldTypes.Parent, ReferenceEntity = "Recep" },
                new FieldDef { Name = "Prod", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Prod" },
                new FieldDef { Name = "Tipo", Label = "Tipo", Type = FieldTypes.Text },
                new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
            ] },
            new AddFieldOp { Entity = "Recep", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "RecepPartida" } },
        ] };
        var c = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(Ci), new HashSet<string>(Ci));
        Assert.True(c.Preview.Valid, string.Join(" | ", c.Preview.Errors.Select(e => e.Message)));
        return c.ResultingModel;
    }

    [Fact]
    public void El_filtro_del_acumulado_se_traduce_con_rutas_al_documento_y_la_semantica_de_nulos_del_evaluador()
    {
        var model = Recepciones();
        var source = model["RecepPartida"];
        var sum = source.FindField("Cantidad")!;

        Assert.Equal("", RecordService.SqlRollup(source, sum, null, model)!.Sql);

        // Ruta al documento: subconsulta al registro vigente, como RelatedLoader.Row; el literal va como parámetro.
        var p = RecordService.SqlRollup(source, sum, "Recep.Estado = 'Confirmada'", model)!;
        Assert.Contains("FROM [app].[Recep] r0 WHERE r0.[Id] = t.[Recep] AND r0.[IsDeleted] = 0", p.Sql);
        Assert.Contains("CASE WHEN", p.Sql); // «=» en dos valores: nulo = x es falso, también bajo NOT
        Assert.DoesNotContain("Confirmada", p.Sql);
        Assert.Contains(p.Parameters, x => (string)x.Value == "Confirmada" && x.Name.StartsWith("@rf"));

        var q = RecordService.SqlRollup(source, sum, "NOT (Tipo IN ('a''; DROP TABLE x --', 'b')) AND Prod.Activo = TRUE AND Cantidad > 0", model)!;
        Assert.DoesNotContain("DROP", q.Sql);
        Assert.Contains("[app].[Prod]", q.Sql);
        Assert.Equal(4, q.Parameters.Count);

        // Lo que depende de quién dispara el evento, rutas de dos niveles o un campo sin columna: en memoria (null).
        Assert.Null(RecordService.SqlRollup(source, sum, "Prod.Clave = CURRENTUNIT()", model));
        Assert.Null(RecordService.SqlRollup(source, sum, "HASROLE('Almacenista')", model));
        Assert.Null(RecordService.SqlRollup(source, sum, "Recep.Partidas.Cantidad > 0", model));
        Assert.Null(RecordService.SqlRollup(source, sum, "Tipo = NULL", model));
        Assert.Null(RecordService.SqlRollup(model["Recep"], model["Recep"].FindField("Partidas")!, null, model));
    }

    [Fact]
    public void La_clave_del_bloqueo_de_un_acumulado_es_la_misma_leida_de_la_base_o_capturada()
    {
        var model = Recepciones();
        var target = model["Prod"];
        Assert.Equal(RecordService.RollupLockKey(target, [5L, "Abc "]), RecordService.RollupLockKey(target, [5m, "ABC"]));
        Assert.Equal(RecordService.RollupLockKey(target, [2.50m]), RecordService.RollupLockKey(target, [2.5m]));
        Assert.NotEqual(RecordService.RollupLockKey(target, [5L, 6L]), RecordService.RollupLockKey(target, [56L]));
    }

    // ---------------- H3: folios de la importación ----------------

    [Fact]
    public void Folios_de_la_importacion_provisionales_o_de_un_bloque_y_devueltos_al_revertir_una_fila()
    {
        var e = new EntityDef { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields = [] };
        var otra = e with { Name = "Otra" };
        var folio = new FieldDef { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber, Prefix = "PED-", Padding = 4 };

        var preview = RecordService.FolioSource.ForPreview(e);
        Assert.Equal("PED-VP-1", preview.Take(e, folio));
        Assert.Equal("PED-VP-2", preview.Take(e, folio));
        Assert.Null(preview.Take(otra, folio)); // lo que creen sus automatizaciones usa la secuencia

        var block = new RecordService.FolioSource("Pedido", preview: false);
        block.Add("Folio", 41, 2);
        Assert.Equal("PED-0041", block.Take(e, folio));
        var mark = block.Mark();
        Assert.Equal("PED-0042", block.Take(e, folio));
        block.Restore(mark); // la fila se revirtió: su folio vuelve al bloque
        Assert.Equal("PED-0042", block.Take(e, folio));
        Assert.Null(block.Take(e, folio)); // agotado: la secuencia
        Assert.Equal([("Folio", 41L, 43L)], block.Blocks.ToList());
    }

    // ---------------- H4: campos locales ----------------

    [Fact]
    public void Aprobar_o_aplicar_un_paquete_que_archiva_campos_locales_exige_confirmarlo()
    {
        var none = new PackageDiffDto(true, [], [], [], [], [], "h", false, [], []);
        EnvironmentService.RequireArchiveConfirmed(none, confirmed: false);
        var local = none with { LocalArchives = ["«Producto» › Notas no viene en el paquete: se archiva (sus datos se conservan)."] };
        var ex = Assert.Throws<Conflict>(() => EnvironmentService.RequireArchiveConfirmed(local, confirmed: false));
        Assert.Contains("1 campo que solo existe", ex.Message);
        EnvironmentService.RequireArchiveConfirmed(local, confirmed: true);
    }

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }
}
