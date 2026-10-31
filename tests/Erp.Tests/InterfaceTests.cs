using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Tests;

/// <summary>Spec 007: layouts, vistas, reportes y tableros (validación, filtros SQL, seguridad y exportación).</summary>
public class InterfaceTests
{
    private static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendedor", "Gerente" };

    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Cliente", Label = "Cliente", LabelPlural = "Clientes", Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Region", Label = "Región", Type = FieldTypes.Choice, Options = ["Norte", "Sur"] },
        ] },
        new CreateEntityOp { Name = "Venta", Label = "Venta", LabelPlural = "Ventas", Fields =
        [
            new FieldDef { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber },
            new FieldDef { Name = "Cliente", Label = "Cliente", Type = FieldTypes.Reference, ReferenceEntity = "Cliente" },
            new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date },
            new FieldDef { Name = "Monto", Label = "Monto", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Margen", Label = "Margen", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Vendedor", Label = "Vendedor", Type = FieldTypes.User },
            new FieldDef { Name = "Notas", Label = "Notas", Type = FieldTypes.LongText },
            new FieldDef { Name = "Urgente", Label = "Urgente", Type = FieldTypes.Boolean },
        ] },
        new CreateEntityOp { Name = "VentaPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Venta", Label = "Venta", Type = FieldTypes.Parent, ReferenceEntity = "Venta" },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
        ] },
        new AddFieldOp { Entity = "Venta", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "VentaPartida" } },
        new SetWorkflowOp { Entity = "Venta", Workflow = new()
        {
            Initial = "Abierta",
            States = [new() { Name = "Abierta", Label = "Abierta" }, new() { Name = "Cerrada", Label = "Cerrada" }],
            Transitions = [new() { Name = "cerrar", Label = "Cerrar", From = ["Abierta"], To = "Cerrada" }],
        } },
        new SetSecurityOp { Entity = "Venta", Security = new() { Fields = [new() { Field = "Margen", Read = ["Gerente"] }] } },
    ];

    private static CompiledPlan Compile(params Operation[] extra) =>
        PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), .. extra] },
            new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    private static UserContext User(params string[] roles) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles,
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase)
        {
            ["Venta"] = new(true, true, true, true), ["Cliente"] = new(true, false, false, false),
        },
    };

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    // ---------------- Layout ----------------

    [Fact]
    public void Layout_valido_con_pestanas_y_condiciones()
    {
        var r = Compile(new SetLayoutOp { Entity = "Venta", Layout = new()
        {
            Sections =
            [
                new() { Name = "General", Label = "General", Tab = "Datos", Fields = [new() { Field = "Cliente" }, new() { Field = "Fecha" }] },
                new() { Name = "Importes", Label = "Importes", Tab = "Datos", VisibleWhen = "Cliente.Region = 'Norte'", Fields = [new() { Field = "Monto", VisibleWhen = "Urgente" }] },
                new() { Name = "Extra", Label = "Extra", Tab = "Notas", Columns = 1, Fields = [new() { Field = "Notas", Wide = true }] },
            ],
        } });
        Assert.True(r.Preview.Valid, Errors(r));
        Assert.Contains("3 sección(es) en 2 pestaña(s)", r.Preview.Steps.Last().Description);
    }

    [Theory]
    [InlineData("NoExiste", "no existe")]
    [InlineData("Partidas", "no se coloca en el layout")]
    [InlineData("Estado", "no se coloca en el layout")]
    public void Layout_rechaza_campos_invalidos(string field, string message)
    {
        var r = Compile(new SetLayoutOp { Entity = "Venta", Layout = new() { Sections = [new() { Name = "A", Label = "A", Fields = [new() { Field = field }] }] } });
        Assert.False(r.Preview.Valid);
        Assert.Contains(message, Errors(r));
    }

    [Fact]
    public void Layout_rechaza_repetidos_condiciones_invalidas_pestanas_separadas_y_partidas()
    {
        var r = Compile(new SetLayoutOp { Entity = "Venta", Layout = new()
        {
            Sections =
            [
                new() { Name = "A", Label = "A", Tab = "Uno", Fields = [new() { Field = "Monto" }] },
                new() { Name = "B", Label = "B", Tab = "Dos", VisibleWhen = "Inventado > 1", Fields = [new() { Field = "Monto" }] },
                new() { Name = "C", Label = "C", Tab = "Uno", Columns = 3, Fields = [new() { Field = "Fecha" }] },
            ],
        } }, new SetLayoutOp { Entity = "VentaPartida", Layout = new() { Sections = [new() { Name = "A", Label = "A", Fields = [new() { Field = "Cantidad" }] }] } });
        var e = Errors(r);
        Assert.Contains("ya está en otra sección", e);
        Assert.Contains("Inventado", e);
        Assert.Contains("aparece separada", e);
        Assert.Contains("«columns» es 1 o 2", e);
        Assert.Contains("es una partida", e);
    }

    [Fact]
    public void Archivar_un_campo_lo_quita_del_layout_y_de_las_columnas()
    {
        var r = Compile(
            new SetLayoutOp { Entity = "Venta", Layout = new() { Sections = [new() { Name = "SecA", Label = "A", Fields = [new() { Field = "Notas" }] }, new() { Name = "SecB", Label = "B", Fields = [new() { Field = "Monto" }, new() { Field = "Fecha" }] }] } },
            new SetViewsOp { Entity = "Venta", Views = [new() { Name = "Vista", Label = "V", Columns = ["Monto", "Notas"] }] },
            new ArchiveFieldOp { Entity = "Venta", Field = "Notas" });
        Assert.True(r.Preview.Valid, Errors(r));
        var venta = r.ResultingModel["Venta"];
        Assert.Equal(["SecB"], venta.Layout!.Sections.Select(s => s.Name));
        Assert.Equal(["Monto"], venta.Views![0].Columns!);
    }

    [Fact]
    public void Layout_acepta_el_campo_como_texto_u_objeto_en_json()
    {
        var l = JsonSerializer.Deserialize<Layout>("""{ "sections": [{ "name": "A", "label": "A", "fields": ["Monto", { "field": "Fecha", "visibleWhen": "Urgente", "wide": true }] }] }""", MetadataStore.Json)!;
        Assert.Equal("Monto", l.Sections[0].Fields[0].Field);
        Assert.Equal("Urgente", l.Sections[0].Fields[1].VisibleWhen);
        Assert.True(l.Sections[0].Fields[1].Wide);
        // Al guardar, el campo simple vuelve a ser texto.
        Assert.Contains("\"Monto\"", JsonSerializer.Serialize(l, MetadataStore.Json));
    }

    // ---------------- Vistas y filtros ----------------

    [Fact]
    public void Vistas_validan_filtros_columnas_orden_estado_y_roles()
    {
        var r = Compile(new SetViewsOp { Entity = "Venta", Views =
        [
            new() { Name = "Bien", Label = "Mis abiertas", Filters = [new() { Field = "Vendedor", Op = "eq", Value = J("$me") }], State = "Abierta", Sort = "Fecha" },
            new() { Name = "Mal", Label = "Mal", Roles = ["Nadie"], Columns = ["Partidas"], Sort = "Notas", State = "Perdida",
                Filters = [new() { Field = "Notas", Op = "gt", Value = J("x") }, new() { Field = "Monto", Op = "eq", Value = J("mucho") }, new() { Field = "Region", Op = "eq", Value = J("x") }] },
        ] });
        var e = Errors(r);
        Assert.DoesNotContain("'Bien'", e);
        Assert.Contains("el rol 'Nadie' no existe", e);
        Assert.Contains("la columna 'Partidas'", e);
        Assert.Contains("no se puede ordenar por 'Notas'", e);
        Assert.Contains("el estado 'Perdida' no existe", e);
        Assert.Contains("el operador 'gt' no aplica", e);
        Assert.Contains("no es un valor válido para Monto", e);
        Assert.Contains("'Region', que no es un campo de Venta", e);
    }

    private static EntityDef Venta() => Compile().ResultingModel["Venta"];

    [Fact]
    public void Filtros_se_traducen_a_sql_con_parametros()
    {
        var cmd = new SqlCommand();
        var sql = FilterSql.Build(Venta(), [
            new() { Field = "Vendedor", Op = "eq", Value = J("$me") },
            new() { Field = "Folio", Op = "contains", Value = J("'; DROP TABLE x; --%") },
            new() { Field = "Estado", Op = "in", Value = J(new[] { "Abierta", "Cerrada" }) },
            new() { Field = "Fecha", Op = "gte", Value = J("$today") },
            new() { Field = "Notas", Op = "blank" },
            new() { Field = "Urgente", Op = "eq", Value = J(true) },
        ], User("Gerente"), cmd.Parameters, "f");
        Assert.Equal("t.[Vendedor] = @f0 AND t.[Folio] LIKE @f1 ESCAPE '\\' AND t.[Estado] IN (@f2, @f3) AND t.[Fecha] >= @f4 " +
                     "AND (t.[Notas] IS NULL OR LTRIM(RTRIM(t.[Notas])) = N'') AND t.[Urgente] = @f5", sql);
        Assert.Equal(7, cmd.Parameters[0].Value);
        Assert.Equal("%''; DROP TABLE x; --\\%%".Replace("''", "'"), cmd.Parameters[1].Value);
        Assert.Equal(DateTime.Today, cmd.Parameters[4].Value);
        Assert.DoesNotContain("DROP", sql);
    }

    [Fact]
    public void Filtrar_por_un_campo_oculto_responde_400()
    {
        var ex = Assert.Throws<AppException>(() =>
            FilterSql.Build(Venta(), [new() { Field = "Margen", Op = "gt", Value = J(30) }], User("Vendedor"), new SqlCommand().Parameters, "f"));
        Assert.Equal(400, ex.Status);
        // El gerente sí lo ve y sí puede filtrar.
        Assert.Equal("t.[Margen] > @f0", FilterSql.Build(Venta(), [new() { Field = "Margen", Op = "gt", Value = J(30) }], User("Gerente"), new SqlCommand().Parameters, "f"));
    }

    [Theory]
    [InlineData("""[{ "field": "Monto", "op": "eq" }]""", "indica un valor")]
    [InlineData("""[{ "field": "Monto", "op": "drop" }]""", "operador")]
    [InlineData("""[{ "field": "Estado", "op": "eq", "value": "Inventado" }]""", "no es un estado")]
    [InlineData("""[{ "field": "Cliente", "op": "eq", "value": "$me" }]""", "$me solo aplica")]
    public void Filtros_invalidos_responden_400(string json, string message)
    {
        var ex = Assert.Throws<AppException>(() => FilterSql.Build(Venta(), FilterSql.Parse(json), User("Gerente"), new SqlCommand().Parameters, "f"));
        Assert.Equal(400, ex.Status);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Parametro_filters_invalido_responde_400() =>
        Assert.Equal(400, Assert.Throws<AppException>(() => FilterSql.Parse("{no es json")).Status);

    // ---------------- Reportes y tableros ----------------

    private static ReportDef Report(string name, params ReportGroup[] groups) => new()
    {
        Name = name, Label = name, GroupBy = groups, Measures = [new() { Fn = "count" }, new() { Fn = "sum", Field = "Monto" }],
    };

    [Fact]
    public void Reportes_validos_con_relaciones_periodos_y_partidas()
    {
        var r = Compile(
            new SetReportsOp { Entity = "Venta", Reports =
            [
                Report("PorRegion", new ReportGroup { Field = "Cliente.Region" }) with { Chart = "pie", Filter = "Estado = 'Cerrada'" },
                Report("PorMes", new ReportGroup { Field = "Fecha", Period = "month" }, new ReportGroup { Field = "Vendedor" }) with { OrderBy = "measure", Limit = 10 },
                Report("Total") with { Measures = [new() { Fn = "avg", Field = "Monto" }, new() { Fn = "max", Field = "Fecha" }] },
            ] },
            new SetReportsOp { Entity = "VentaPartida", Reports = [new() { Name = "PorCliente", Label = "Por cliente", GroupBy = [new() { Field = "Venta.Cliente" }], Measures = [new() { Fn = "sum", Field = "Cantidad" }] }] });
        Assert.True(r.Preview.Valid, Errors(r));
    }

    [Fact]
    public void Reportes_rechazan_definiciones_invalidas()
    {
        var r = Compile(new SetReportsOp { Entity = "Venta", Reports =
        [
            new() { Name = "A", Label = "A", GroupBy = [new() { Field = "Fecha" }], Measures = [new() { Fn = "sum", Field = "Notas" }] },
            new() { Name = "B", Label = "B", GroupBy = [new() { Field = "Monto", Period = "month" }, new() { Field = "Cliente.Nada" }, new() { Field = "Folio" }], Measures = [] },
            new() { Name = "C", Label = "C", Measures = [new() { Fn = "median", Field = "Monto" }, new() { Fn = "min", Field = "Urgente" }], Chart = "bar", Limit = 5000, Filter = "Cliente.Region = 'Norte'" },
            new() { Name = "D", Label = "D", GroupBy = [new() { Field = "Fecha.Algo" }], Measures = [new() { Fn = "sum" }] },
        ] });
        var e = Errors(r);
        Assert.Contains("requiere «period»", e);
        Assert.Contains("«sum» solo aplica a números", e);
        Assert.Contains("«period» solo aplica a fechas", e);
        Assert.Contains("'Nada' no es un campo de Cliente", e);
        Assert.Contains("máximo 2 agrupaciones", e);
        Assert.Contains("indica de 1 a 6 medidas", e);
        Assert.Contains("'median' no existe", e);
        Assert.Contains("«min» solo aplica a números o fechas", e);
        Assert.Contains("la gráfica «bar» necesita una agrupación", e); // spec 039: el mensaje nombra el tipo
        Assert.Contains("«limit» va de 1 a 1000", e);
        Assert.Contains("(filter)", e);
        Assert.Contains("solo se puede llegar a otro catálogo por una referencia", e);
        Assert.Contains("«sum» necesita un campo", e);
    }

    [Fact]
    public void Tableros_se_validan_contra_los_reportes_y_se_archivan()
    {
        var reports = new SetReportsOp { Entity = "Venta", Reports = [Report("Total"), Report("PorCliente", new ReportGroup { Field = "Cliente" })] };
        var ok = Compile(reports, new SetDashboardOp { Dashboard = new()
        {
            Name = "Ventas", Label = "Ventas", Roles = ["Gerente"],
            Widgets = [new() { Type = "kpi", Entity = "Venta", Report = "Total", Measure = 1 }, new() { Type = "chart", Entity = "Venta", Report = "PorCliente" }],
        } });
        Assert.True(ok.Preview.Valid, Errors(ok));
        Assert.Contains("Ventas", ok.TouchedDashboards!);
        Assert.Equal("layout-dashboard", ok.Dashboards!["ventas"].Icon);

        var bad = Compile(reports, new SetDashboardOp { Dashboard = new()
        {
            Name = "Malo", Label = "Malo", Roles = ["Nadie"],
            Widgets = [new() { Type = "kpi", Entity = "Venta", Report = "PorCliente" }, new() { Type = "chart", Entity = "Venta", Report = "Total" },
                       new() { Type = "kpi", Entity = "Venta", Report = "Total", Measure = 9 }, new() { Type = "mapa", Entity = "Venta", Report = "Total" },
                       new() { Type = "table", Entity = "Venta", Report = "NoExiste" }, new() { Type = "table", Entity = "Nada", Report = "Total" }],
        } });
        var e = Errors(bad);
        Assert.Contains("el rol 'Nadie' no existe", e);
        Assert.Contains("un indicador usa un reporte sin agrupación", e);
        Assert.Contains("una gráfica usa un reporte con agrupación", e);
        Assert.Contains("la medida 9 no existe", e);
        Assert.Contains("el tipo 'mapa' no existe", e);
        Assert.Contains("el reporte 'NoExiste' no existe", e);
        Assert.Contains("el catálogo 'Nada' no existe", e);

        // Quitar un reporte que usa un tablero rompe el tablero: la vista previa lo detecta.
        var model = ok.ResultingModel;
        var broken = PlanCompiler.Compile(new ChangePlan { Operations = [new SetReportsOp { Entity = "Venta", Reports = [Report("Total")] }] }, model, Roles, ok.Dashboards);
        Assert.Contains("el reporte 'PorCliente' no existe", Errors(broken));

        var archived = PlanCompiler.Compile(new ChangePlan { Operations = [new ArchiveDashboardOp { Name = "Ventas" }] }, model, Roles, ok.Dashboards);
        Assert.True(archived.Preview.Valid, Errors(archived));
        Assert.True(archived.Dashboards!["Ventas"].IsArchived);
        Assert.False(PlanCompiler.Compile(new ChangePlan { Operations = [new ArchiveDashboardOp { Name = "Nada" }] }, model, Roles, ok.Dashboards).Preview.Valid);
    }

    [Fact]
    public void Reporte_no_disponible_si_usa_un_campo_oculto_o_no_tiene_el_rol()
    {
        var r = Compile(new SetReportsOp { Entity = "Venta", Reports =
        [
            Report("Monto"), Report("Margen") with { Measures = [new() { Fn = "sum", Field = "Margen" }] },
            Report("FiltroOculto") with { Filter = "Margen > 10" }, Report("SoloGerente") with { Roles = ["Gerente"] },
        ] });
        Assert.True(r.Preview.Valid, Errors(r));
        var m = r.ResultingModel;
        var e = m["Venta"];
        bool Can(string report, UserContext u) => ReportService.Available(e, e.Reports!.First(x => x.Name == report), u, m);
        Assert.True(Can("Monto", User("Vendedor")));
        Assert.False(Can("Margen", User("Vendedor")));
        Assert.False(Can("FiltroOculto", User("Vendedor")));
        Assert.False(Can("SoloGerente", User("Vendedor")));
        Assert.True(Can("Margen", User("Gerente")));
        Assert.True(Can("SoloGerente", User("Gerente")));
        // Sin permiso de lectura del catálogo, ningún reporte.
        Assert.False(Can("Monto", new UserContext { Id = 7, Username = "x", DisplayName = "x", SecurityStamp = Guid.Empty, Roles = ["Vendedor"] }));
    }

    [Fact]
    public void Condicion_que_lee_un_campo_oculto_se_detecta()
    {
        var m = Compile().ResultingModel;
        var e = m["Venta"];
        Assert.True(RecordService.ReadsHidden(ExprParser.Parse("IF(Margen > 30, TRUE, FALSE)"), e, m, User("Vendedor")));
        Assert.False(RecordService.ReadsHidden(ExprParser.Parse("Margen > 30"), e, m, User("Gerente")));
        Assert.False(RecordService.ReadsHidden(ExprParser.Parse("Cliente.Region = 'Norte' AND Monto > 1"), e, m, User("Vendedor")));
    }

    // Regresión (spec 007, F-01): el traductor de la 006 solo aceptaba campos sí/no como condición, no fórmulas sí/no.
    [Fact]
    public void Formula_si_no_es_condicion_en_filtros_fijos()
    {
        var r = Compile(new AddFieldOp { Entity = "Venta", Field = new FieldDef { Name = "Grande", Label = "Grande", Type = FieldTypes.Formula, ResultType = FieldTypes.Boolean, Formula = "Monto > 1000" } },
            new SetReportsOp { Entity = "Venta", Reports = [Report("Grandes") with { Filter = "Grande AND NOT Urgente" }] });
        Assert.True(r.Preview.Valid, Errors(r));
        var bag = new RecordFilter.ParamBag();
        Assert.Equal("(t.[Grande] = 1 AND (NOT t.[Urgente] = 1))", RecordFilter.Translate("Grande AND NOT Urgente", r.ResultingModel["Venta"], new RecordFilter.Who(1, null, []), bag));
    }

    [Fact]
    public void Prefijo_de_parametros_no_confunde_sec1_con_sec10()
    {
        var p = new SqlPredicate("t.a = @sec1 OR t.b = @sec10", [("@sec1", 1), ("@sec10", 10)]);
        var r = p.Prefixed("j0_");
        Assert.Equal("t.a = @j0_1 OR t.b = @j0_10", r.Sql);
        Assert.Equal(["@j0_1", "@j0_10"], r.Parameters.Select(x => x.Name));
    }

    // ---------------- Exportación ----------------

    private static ReportResult Result() => new("Venta", "PorCliente", "Ventas: por cliente", null, null,
        [new("g0", "Cliente", "group", "text"), new("m0", "Monto", "measure", "number", 2)],
        [new() { ["g0"] = "=HYPERLINK(\"http://x\")", ["m0"] = 10.5m }, new() { ["g0"] = "Pérez, Ñandú", ["m0"] = -3m }],
        new() { ["m0"] = 7.5m }, false);

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+52 81", "'+52 81")]
    [InlineData("-x", "'-x")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("normal", "normal")]
    [InlineData("a,b", "\"a,b\"")]
    public void Csv_neutraliza_formulas(string value, string expected) => Assert.Equal(expected, ReportExport.CsvCell(value));

    [Fact]
    public void Csv_con_bom_encabezados_filas_y_total()
    {
        var bytes = ReportExport.Csv(Result());
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("Cliente,Monto\r\n\"'=HYPERLINK(\"\"http://x\"\")\",10.5\r\n\"Pérez, Ñandú\",-3\r\nTotal,7.5\r\n", text);
    }

    [Fact]
    public void Xlsx_es_un_libro_valido_sin_formulas()
    {
        using var zip = new ZipArchive(new MemoryStream(ReportExport.Xlsx(Result())));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = reader.ReadToEnd();
        System.Xml.Linq.XDocument.Parse(sheet); // XML bien formado
        Assert.DoesNotContain("<f>", sheet);
        Assert.Contains("=HYPERLINK(&quot;http://x&quot;)", sheet); // texto, no fórmula
        Assert.Contains("<v>10.5</v>", sheet);
        Assert.Contains("Pérez, Ñandú", sheet);
        using var wb = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("name=\"Ventas por cliente\"", wb.ReadToEnd()); // sin caracteres prohibidos en el nombre de la hoja
    }

    // ---------------- Paquetes reales ----------------

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "modules")) && File.Exists(Path.Combine(d.FullName, "modules", "compras.json")))
                return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    [Fact]
    public void Compras_e_Inventario_compilan_con_todas_sus_actualizaciones()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        IReadOnlyDictionary<string, EntityDef> model = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(StringComparer.OrdinalIgnoreCase);
        // Spec 008: los roles se acumulan como en la base real (los webhooks de Compras usan su rol de servicio aunque el plan sea de Inventario).
        var roles = new HashSet<string>(["Administrador"], StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "compras.json", "inventario.json" })
        {
            var p = JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), json)!;
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (label, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, $"{file} {label}: {Errors(c)}");
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        Assert.Equal(["Compras", "Inventario"], boards.Keys.Order());
        Assert.NotNull(model["OrdenCompra"].Layout);
        Assert.Equal("OrdenAutorizada", model["OrdenCompra"].Webhooks!.Single().Name);
        Assert.Equal("ExistenciaActualizada", model["Existencia"].Webhooks!.Single().Name);
    }
}
