using Erp.Api.Data;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 039 · Reportes 2: validación de gráficas, paleta, medidas calculadas, periodos y rutas de dos saltos; disponibilidad
/// con un campo oculto en un salto; etiquetas de semana ISO y periodo anterior.
/// </summary>
public class Reports039Tests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendedor", "Gerente" };

    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Categoria", Label = "Categoría", LabelPlural = "Categorías", DisplayField = "Nombre", Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Decimal },
        ] },
        new CreateEntityOp { Name = "Producto", Label = "Producto", LabelPlural = "Productos", DisplayField = "Nombre", Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Categoria", Label = "Categoría", Type = FieldTypes.Reference, ReferenceEntity = "Categoria" },
            new FieldDef { Name = "Interno", Label = "Interno", Type = FieldTypes.Text },
        ] },
        new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields =
        [
            new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date },
            new FieldDef { Name = "Cliente", Label = "Cliente", Type = FieldTypes.Text },
        ] },
        new CreateEntityOp { Name = "PedidoPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Pedido", Label = "Pedido", Type = FieldTypes.Parent, ReferenceEntity = "Pedido" },
            new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto" },
            new FieldDef { Name = "Importe", Label = "Importe", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Decimal },
        ] },
        new AddFieldOp { Entity = "Pedido", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "PedidoPartida" } },
        new SetSecurityOp { Entity = "Producto", Security = new() { Fields = [new() { Field = "Categoria", Read = ["Gerente"] }] } },
    ];

    private static CompiledPlan Compile(params ReportDef[] reports) =>
        PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), new SetReportsOp { Entity = "PedidoPartida", Reports = reports }] },
            new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    private static ReportDef R(string name, params ReportGroup[] groups) => new()
    {
        Name = name, Label = name, GroupBy = groups,
        Measures = [new() { Fn = "sum", Field = "Importe", Name = "Ventas" }, new() { Fn = "sum", Field = "Costo", Name = "Costo" }],
    };

    private static ReportGroup G(string field, string? period = null) => new() { Field = field, Period = period };

    [Fact]
    public void Graficas_validas_de_todos_los_tipos()
    {
        var r = Compile(
            R("Columnas", G("Pedido.Fecha", "month"), G("Producto.Categoria.Nombre")) with { Chart = "column", ChartOptions = new() { Stacked = true, Colors = ["azul", "lima"] } },
            R("Barras", G("Producto")) with { Chart = "bar", ChartOptions = new() { Measures = [0, 1], Stacked = true, Values = true } },
            R("Area", G("Pedido.Fecha", "week")) with { Chart = "area", ChartOptions = new() { Measures = [0, 1] } },
            R("Linea", G("Pedido.Fecha", "quarter")) with { Chart = "line", ChartOptions = new() { Legend = false } },
            R("Dona", G("Producto.Categoria.Nombre")) with { Chart = "donut" },
            R("Pastel", G("Pedido.Cliente")) with { Chart = "pie", ChartOptions = new() { Measures = [1] } },
            R("Combinada", G("Pedido.Fecha", "month")) with
            {
                Chart = "combo", ChartOptions = new() { Measures = [0, 1], Lines = [1] },
                Measures = [.. R("x").Measures, new() { Fn = "calc", Expr = "ROUND((Ventas - Costo) / Ventas * 100, 1)", Label = "Margen %", Scale = 1 }],
            },
            R("Indicador") with { Chart = "kpi" },
            R("IndicadorMes", G("Pedido.Fecha", "month")) with { Chart = "kpi" });
        Assert.True(r.Preview.Valid, Errors(r));
    }

    [Fact]
    public void Combinaciones_sin_sentido_se_rechazan_con_un_mensaje_claro()
    {
        var r = Compile(
            R("PastelDos", G("Producto")) with { Chart = "pie", ChartOptions = new() { Measures = [0, 1] } },
            R("PastelDosGrupos", G("Producto"), G("Pedido.Cliente")) with { Chart = "donut" },
            R("ComboUna", G("Producto")) with { Chart = "combo" },
            R("ApiladaUna", G("Producto")) with { Chart = "column", ChartOptions = new() { Stacked = true } },
            R("LineaApilada", G("Producto")) with { Chart = "line", ChartOptions = new() { Measures = [0, 1], Stacked = true } },
            R("DosGruposDosMedidas", G("Producto"), G("Pedido.Cliente")) with { Chart = "column", ChartOptions = new() { Measures = [0, 1] } },
            R("SinGrupo") with { Chart = "column" },
            R("IndicadorPorProducto", G("Producto")) with { Chart = "kpi" },
            R("LineasFuera", G("Producto")) with { Chart = "bar", ChartOptions = new() { Lines = [0] } },
            R("MedidaInexistente", G("Producto")) with { Chart = "bar", ChartOptions = new() { Measures = [9] } },
            R("SinTipo", G("Producto")) with { ChartOptions = new() { Values = true } },
            R("Mapa", G("Producto")) with { Chart = "mapa" });
        var e = Errors(r);
        Assert.Contains("un pastel o una dona muestra una sola medida", e);
        Assert.Contains("un pastel o una dona necesita exactamente una agrupación", e);
        Assert.Contains("la combinada necesita al menos dos medidas", e);
        Assert.Contains("apilar es de columnas, barras o área con más de una serie", e);
        Assert.Contains("con dos agrupaciones, la segunda da las series: elige una sola medida", e);
        Assert.Contains("la gráfica «column» necesita una agrupación", e);
        Assert.Contains("un indicador usa un reporte sin agrupación o agrupado solo por una fecha con periodo", e);
        Assert.Contains("«lines» es solo de la gráfica combinada", e);
        Assert.Contains("la medida 9 de la gráfica no existe", e);
        Assert.Contains("«chartOptions» necesita «chart»", e);
        Assert.Contains("«chart» es column, bar, line, area, pie, donut, combo o kpi", e);
    }

    [Fact]
    public void Paleta_y_periodos_cerrados()
    {
        var r = Compile(
            R("Color", G("Producto")) with { Chart = "bar", ChartOptions = new() { Colors = ["azul", "#ff0000", "red;background:url(x)"] } },
            R("Periodo", G("Pedido.Fecha", "decade")),
            R("FechaMedida", G("Producto")) with { Chart = "bar", Measures = [new() { Fn = "max", Field = "Pedido.Fecha" }] });
        var e = Errors(r);
        Assert.Contains("el color '#ff0000' no es de la paleta", e);
        Assert.Contains("el color 'red;background:url(x)' no es de la paleta", e);
        Assert.Contains("«period» solo aplica a fechas y es day, week, month, quarter o year", e);
        Assert.Contains("la medida 0 de la gráfica no es un número", e);
        Assert.Equal(12, InterfaceRules.Palette.Count);
    }

    [Fact]
    public void Calculada_rechaza_campos_funciones_y_encadenadas()
    {
        ReportDef Calc(string name, string expr, params ReportMeasure[] more) => R(name, G("Producto")) with
        {
            Measures = [.. R("x").Measures, new() { Fn = "calc", Expr = expr, Name = "Calc" + name }, .. more],
        };
        var r = Compile(
            Calc("Campo", "Importe / Ventas"),
            Calc("Ruta", "Producto.Interno"),
            Calc("Usuario", "IF(HASROLE('Gerente'), Ventas, 0)"),
            Calc("Hoy", "Ventas + DAYS(TODAY(), TODAY())"),
            Calc("Texto", "Ventas & 'x'"),
            Calc("Encadenada", "Ventas", new ReportMeasure { Fn = "calc", Expr = "CalcEncadenada * 2" }),
            Calc("Booleana", "Ventas > Costo"),
            Calc("Larga", string.Join(" + ", Enumerable.Repeat("Ventas", 120))),
            Calc("Aridad", "ROUND(Ventas, 1, 2)"),
            R("ConCampo", G("Producto")) with { Measures = [new() { Fn = "sum", Field = "Importe", Name = "V" }, new() { Fn = "calc", Field = "Importe", Expr = "V" }] },
            R("ExprEnSuma", G("Producto")) with { Measures = [new() { Fn = "sum", Field = "Importe", Expr = "1", Scale = 2 }] },
            R("SoloCalculadas", G("Producto")) with { Measures = [new() { Fn = "calc", Expr = "1 + 1" }] },
            R("OrdenCalculada", G("Producto")) with { OrderBy = "measure", Measures = [new() { Fn = "calc", Expr = "Ventas" }, .. R("x").Measures] },
            R("NombreRepetido", G("Producto")) with { Measures = [new() { Fn = "sum", Field = "Importe", Name = "Uno" }, new() { Fn = "count", Name = "uno" }] },
            R("SinNombre", G("Producto")) with { Measures = [new() { Fn = "sum", Field = "Importe" }, new() { Fn = "calc", Expr = "Importe * 2" }] });
        var e = Errors(r);
        Assert.Contains("'Importe' no es el nombre de una medida no calculada del reporte", e);
        Assert.Contains("'Producto.Interno' no es el nombre de una medida", e);
        Assert.Contains("la función HASROLE no se usa en medidas calculadas", e);
        Assert.Contains("la función DAYS no se usa en medidas calculadas", e);
        Assert.Contains("«&» une textos", e);
        Assert.Contains("'CalcEncadenada' no es el nombre de una medida no calculada", e);
        Assert.Contains("la expresión debe dar un número", e);
        Assert.Contains("la expresión excede 500 caracteres", e);
        Assert.Contains("ROUND recibe de 1 a 2 argumentos", e);
        Assert.Contains("una medida calculada no lleva campo", e);
        Assert.Contains("«expr» y «scale» son solo de una medida calculada", e);
        Assert.Contains("agrega al menos una que no lo sea", e);
        Assert.Contains("la primera no puede ser calculada", e);
        Assert.Contains("el nombre 'uno' está repetido", e);
        Assert.Contains("dale un nombre a las medidas que uses", e);
    }

    [Fact]
    public void Calculada_en_memoria_con_vacios_y_division_entre_cero()
    {
        var m = new ReportMeasure { Fn = "calc", Expr = "ROUND((Ventas - Costo) / Ventas * 100, 1)", Scale = 1 };
        Dictionary<string, object?> V(object? ventas, object? costo) => new(StringComparer.OrdinalIgnoreCase) { ["Ventas"] = ventas, ["Costo"] = costo };
        Assert.Equal(25.0m, ReportService.Calc(m, V(200m, 150m)));
        Assert.Null(ReportService.Calc(m, V(0m, 0m))); // dividir entre cero da vacío
        Assert.Null(ReportService.Calc(m, V(null, 5m))); // un vacío se propaga (como en SQL), no vale 0 como en las fórmulas
        Assert.Equal(-5m, ReportService.Calc(new ReportMeasure { Fn = "calc", Expr = "COALESCE(Ventas, 0) - Costo" }, V(null, 5m)));
        Assert.Equal(33.33m, ReportService.Calc(new ReportMeasure { Fn = "calc", Expr = "Ventas / Costo" }, V(100m, 3m)));
        var guarded = new ReportMeasure { Fn = "calc", Expr = "IF(ISBLANK(Costo), NULL, Ventas - Costo)", Scale = 0 };
        Assert.Null(ReportService.Calc(guarded, V(10m, null)));
        Assert.Equal(4m, ReportService.Calc(guarded, V(10.4m, 6m)));
    }

    [Fact]
    public void Rutas_de_tres_saltos_y_expresiones_largas_se_rechazan()
    {
        var model = Compile().ResultingModel;
        var e = model["PedidoPartida"];
        Assert.NotNull(InterfaceRules.Resolve(e, "Producto.Categoria.Nombre", model).Path);
        var parent = InterfaceRules.Resolve(e, "Pedido.Fecha", model).Path!;
        Assert.Single(parent.Hops);
        Assert.Equal("Pedido · Fecha", parent.Label);
        var two = InterfaceRules.Resolve(e, "Producto.Categoria.Nombre", model).Path!;
        Assert.Equal(["Producto", "Categoria"], two.Hops.Select(h => h.Via.Name));
        Assert.Equal("Categoria", two.Owner.Name);
        Assert.Contains("no es un campo (usa Campo, Relacion.Campo o Relacion.Relacion.Campo)",
            InterfaceRules.Resolve(e, "Producto.Categoria.Nombre.Otro", model).Error);
        Assert.Contains("solo se puede llegar a otro catálogo",
            InterfaceRules.Resolve(e, "Importe.Algo.Mas", model).Error);
        Assert.Contains("'Nada' no es un campo de Categoria", InterfaceRules.Resolve(e, "Producto.Categoria.Nada", model).Error);
        Assert.Contains("la expresión excede 500", InterfaceRules.CheckCalc(new string('1', 501), new HashSet<string>()));
    }

    private static UserContext User(params string[] roles) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles,
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pedido"] = new(true, true, true, true), ["Producto"] = new(true, false, false, false), ["Categoria"] = new(true, false, false, false),
        },
    };

    [Fact]
    public void Disponible_exige_ver_cada_salto()
    {
        // Producto.Categoria es solo para Gerente: un reporte que pasa por ese campo (aunque el final, Categoria.Nombre,
        // sea visible) no está disponible para Vendedor.
        var r = Compile(R("PorCategoria", G("Producto.Categoria.Nombre")), R("PorProducto", G("Producto.Nombre")),
            R("MideCategoria", G("Producto")) with { Measures = [new() { Fn = "sum", Field = "Producto.Categoria.Costo" }] });
        Assert.True(r.Preview.Valid, Errors(r));
        var m = r.ResultingModel;
        var e = m["PedidoPartida"];
        bool Can(string report, UserContext u) => ReportService.Available(e, e.Reports!.First(x => x.Name == report), u, m);
        Assert.False(Can("PorCategoria", User("Vendedor")));
        Assert.False(Can("MideCategoria", User("Vendedor")));
        Assert.True(Can("PorProducto", User("Vendedor")));
        Assert.True(Can("PorCategoria", User("Gerente")));
    }

    [Fact]
    public void Tarjeta_kpi_acepta_un_reporte_por_periodo()
    {
        var reports = new SetReportsOp { Entity = "PedidoPartida", Reports = [R("PorMes", G("Pedido.Fecha", "month")), R("PorProducto", G("Producto"))] };
        var r = PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), reports, new SetDashboardOp { Dashboard = new()
        {
            Name = "Ventas", Label = "Ventas",
            Widgets = [new() { Type = "kpi", Entity = "PedidoPartida", Report = "PorMes" }, new() { Type = "kpi", Entity = "PedidoPartida", Report = "PorProducto" }],
        } }] }, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        var e = Errors(r);
        Assert.DoesNotContain("tarjeta 1", e);
        Assert.Contains("tarjeta 2: un indicador usa un reporte sin agrupación o agrupado solo por una fecha con periodo", e);
    }

    [Theory]
    [InlineData("2026-01-05", "week", "2026-S02")]
    [InlineData("2025-12-29", "week", "2026-S01")] // el lunes de la semana 1 de 2026 cae en 2025
    [InlineData("2026-12-28", "week", "2026-S53")] // 2026 tiene 53 semanas: el 2027-01-01 es de la 2026-S53
    [InlineData("2026-10-01", "quarter", "2026-T4")]
    [InlineData("2026-01-01", "quarter", "2026-T1")]
    [InlineData("2026-03-01", "month", "2026-03")]
    [InlineData("2026-03-04", "day", "2026-03-04")]
    [InlineData("2026-01-01", "year", "2026")]
    public void Semana_iso_en_el_cambio_de_anio(string start, string period, string label) =>
        Assert.Equal(label, ReportService.PeriodLabel(DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture), period));

    [Theory]
    [InlineData("2026-01-05", "week", "2025-12-29")]
    [InlineData("2026-01-01", "quarter", "2025-10-01")]
    [InlineData("2026-03-01", "month", "2026-02-01")]
    [InlineData("2026-03-01", "day", "2026-02-28")]
    [InlineData("2026-01-01", "year", "2025-01-01")]
    public void Periodo_anterior(string start, string period, string previous)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(DateTime.Parse(previous, inv), ReportService.PreviousPeriod(DateTime.Parse(start, inv), period));
    }

    [Fact]
    public void Paquete_con_reporte_invalido_no_compila()
    {
        // Paquetes, reversión y asistente aplican planes por el mismo compilador: una opción fuera de lista no entra por ahí.
        var r = Compile(R("Color", G("Producto")) with { Chart = "bar", ChartOptions = new() { Colors = ["fucsia"] } });
        Assert.False(r.Preview.Valid);
        Assert.Contains("no es de la paleta", Errors(r));
    }

    // Regresión (revisión adversarial 039, F-C): un registro del año 1 hacía fallar el periodo anterior (500 en el tablero).
    [Fact]
    public void F_C_el_periodo_anterior_al_anio_1_no_existe()
    {
        Assert.Null(ReportService.PreviousPeriod(DateTime.MinValue, "month"));
        Assert.Null(ReportService.PreviousPeriod(DateTime.MinValue, "week"));
        Assert.Null(ReportService.PreviousPeriod(DateTime.MinValue, "day"));
    }
}
