using System.Text.Json;
using Erp.Api.Environments;
using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 010: catálogos por empresa (compilador, DDL, filtro, diff) y funciones de empresa y moneda.</summary>
public class CompanyTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(["Administrador", "Usuario", "Solicitante"], Ci);
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);

    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };

    private static CompiledPlan Compile(IReadOnlyDictionary<string, EntityDef> model, params Operation[] ops) =>
        PlanCompiler.Compile(Plan(ops), model, Roles);

    private static string Errors(CompiledPlan c) => string.Join(" | ", c.Preview.Errors.Select(e => e.Message));

    private static IReadOnlyDictionary<string, EntityDef> Valid(IReadOnlyDictionary<string, EntityDef> model, params Operation[] ops)
    {
        var c = Compile(model, ops);
        Assert.True(c.Preview.Valid, Errors(c));
        return c.ResultingModel;
    }

    private static CreateEntityOp Almacen(bool perCompany = false) => new()
    {
        Name = "Almacen", Label = "Almacén", LabelPlural = "Almacenes", PerCompany = perCompany,
        Fields =
        [
            new() { Name = "Folio", Label = "Folio", Type = "autonumber", Prefix = "AL-" },
            new() { Name = "Clave", Label = "Clave", Type = "text", Required = true, Unique = true },
            new() { Name = "Nombre", Label = "Nombre", Type = "text" },
        ],
    };

    private static CreateEntityOp Orden(bool perCompany) => new()
    {
        Name = "Orden", Label = "Orden", LabelPlural = "Órdenes", PerCompany = perCompany,
        Fields =
        [
            new() { Name = "Total", Label = "Total", Type = "decimal" },
            new() { Name = "Moneda", Label = "Moneda", Type = "text", MaxLength = 3 },
            new() { Name = "Partidas", Label = "Partidas", Type = "lines", ChildEntity = "OrdenPartida" },
        ],
    };

    private static CreateEntityOp Partida(params FieldDef[] extra) => new()
    {
        Name = "OrdenPartida", Label = "Partida", LabelPlural = "Partidas",
        Fields = [new() { Name = "Orden", Label = "Orden", Type = "parent", ReferenceEntity = "Orden" }, new() { Name = "Importe", Label = "Importe", Type = "decimal" }, .. extra],
    };

    // ---------------- Compilador y DDL ----------------

    [Fact]
    public void Crear_por_empresa_agrega_la_columna_de_empresa_y_los_unicos_son_por_empresa()
    {
        var c = Compile(Empty, Almacen(perCompany: true));
        Assert.True(c.Preview.Valid, Errors(c));
        Assert.True(c.ResultingModel["Almacen"].PerCompany);
        var sql = string.Join("\n", c.Sql);
        Assert.Contains("[CompanyId] int NOT NULL CONSTRAINT [FK_Almacen_CompanyId] REFERENCES [meta].[Company] ([Id])", sql);
        Assert.Contains("CREATE INDEX [IX_Almacen_CompanyId] ON [app].[Almacen] ([CompanyId])", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UX_Almacen_Clave] ON [app].[Almacen] ([CompanyId], [Clave])", sql);
        // El folio sigue siendo único en todo el entorno.
        Assert.Contains("CREATE UNIQUE INDEX [UX_Almacen_Folio] ON [app].[Almacen] ([Folio])", sql);
        Assert.Contains("un registro por empresa", c.Preview.Steps[0].Description);

        var shared = string.Join("\n", Compile(Empty, Almacen()).Sql);
        Assert.DoesNotContain("CompanyId", shared);
        Assert.Contains("CREATE UNIQUE INDEX [UX_Almacen_Clave] ON [app].[Almacen] ([Clave])", shared);
    }

    [Fact]
    public void Pasar_a_por_empresa_asigna_la_predeterminada_y_rehace_los_unicos()
    {
        var model = Valid(Empty, Almacen(), new AddFieldOp { Entity = "Almacen", Field = new() { Name = "Viejo", Label = "Viejo", Type = "text", Unique = true } },
            new ArchiveFieldOp { Entity = "Almacen", Field = "Viejo" });
        var c = Compile(model, new UpdateEntityOp { Entity = "Almacen", PerCompany = true });
        Assert.True(c.Preview.Valid, Errors(c));
        Assert.True(c.ResultingModel["Almacen"].PerCompany);
        Assert.Equal(new[]
        {
            "ALTER TABLE [app].[Almacen] ADD [CompanyId] int NULL;",
            "UPDATE [app].[Almacen] SET [CompanyId] = (SELECT TOP 1 [Id] FROM [meta].[Company] WHERE [IsDefault] = 1 ORDER BY [Id]) WHERE [CompanyId] IS NULL;",
            "ALTER TABLE [app].[Almacen] ALTER COLUMN [CompanyId] int NOT NULL;",
            "ALTER TABLE [app].[Almacen] ADD CONSTRAINT [FK_Almacen_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [meta].[Company] ([Id]);",
            "CREATE INDEX [IX_Almacen_CompanyId] ON [app].[Almacen] ([CompanyId]) WHERE [IsDeleted] = 0;",
            "DROP INDEX [UX_Almacen_Clave] ON [app].[Almacen];",
            "CREATE UNIQUE INDEX [UX_Almacen_Clave] ON [app].[Almacen] ([CompanyId], [Clave]) WHERE [Clave] IS NOT NULL AND [IsDeleted] = 0;",
        }, c.Sql); // el folio y el campo archivado conservan su índice
        Assert.Contains("empresa predeterminada", c.Preview.Steps[0].Description);

        // Repetirlo no hace nada; un campo único nuevo ya nace por empresa.
        var after = c.ResultingModel;
        Assert.Empty(Compile(after, new UpdateEntityOp { Entity = "Almacen", PerCompany = true }).Sql);
        var add = Compile(after, new AddFieldOp { Entity = "Almacen", Field = new() { Name = "Codigo", Label = "Código", Type = "text", Unique = true } });
        Assert.Contains("([CompanyId], [Codigo])", string.Join("\n", add.Sql));
    }

    [Fact]
    public void Volver_a_compartido_o_declarar_una_partida_por_empresa_se_rechaza()
    {
        var model = Valid(Empty, Almacen(perCompany: true));
        var back = Compile(model, new UpdateEntityOp { Entity = "Almacen", PerCompany = false });
        Assert.False(back.Preview.Valid);
        Assert.Contains("no puede volver a ser compartido", Errors(back));

        var docs = Valid(Empty, Orden(perCompany: true), Partida());
        var line = Compile(docs, new UpdateEntityOp { Entity = "OrdenPartida", PerCompany = true });
        Assert.Contains("sigue la empresa de su documento", Errors(line));
        var created = Compile(Empty, Orden(perCompany: true), Partida() with { PerCompany = true });
        Assert.Contains("sigue la empresa de su documento", Errors(created));
    }

    [Fact]
    public void CompanyId_es_columna_del_sistema()
    {
        var c = Compile(Empty, new CreateEntityOp
        {
            Name = "Cosa", Label = "Cosa", LabelPlural = "Cosas", Fields = [new() { Name = "CompanyId", Label = "Empresa", Type = "integer" }],
        });
        Assert.Contains("columna del sistema", Errors(c));
    }

    [Fact]
    public void Una_formula_guardada_de_un_catalogo_compartido_no_usa_la_empresa()
    {
        FieldDef Base(string formula) => new() { Name = "Base", Label = "Base", Type = "formula", ResultType = "text", Formula = formula };
        var shared = Compile(Empty, Orden(perCompany: false) with { Fields = [.. Orden(false).Fields, Base("BASECURRENCY()")] }, Partida());
        Assert.Contains("BASECURRENCY() solo se usa en fórmulas de catálogos por empresa", Errors(shared));
        var line = Compile(Empty, Orden(perCompany: false), Partida(Base("CURRENTCOMPANY() & 'x'")));
        Assert.Contains("CURRENTCOMPANY() solo se usa", Errors(line));

        // Por empresa sí (también sus partidas); RATE y CONVERT no dependen de la empresa; un valor por defecto se evalúa al capturar.
        Valid(Empty, Orden(perCompany: true) with { Fields = [.. Orden(true).Fields, Base("BASECURRENCY()")] }, Partida(Base("CURRENTCOMPANY()")));
        Valid(Empty, Orden(perCompany: false) with
        {
            Fields =
            [
                .. Orden(false).Fields,
                new() { Name = "Pesos", Label = "Pesos", Type = "formula", ResultType = "decimal", Formula = "CONVERT(Total, Moneda, 'MXN')" },
                new() { Name = "Destino", Label = "Destino", Type = "text", MaxLength = 3, DefaultValue = "BASECURRENCY()" },
            ],
        }, Partida());
    }

    // ---------------- Filtro por empresa ----------------

    private static readonly CompanyInfo Norte = new(7, "NORTE", "Norte", "USD");

    private static UserContext User(CompanyInfo? company, bool superuser = false, params string[] roles) => new()
    {
        Id = 5, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles, IsSuperuser = superuser,
        Company = company, Companies = company is null ? [] : [company],
    };

    [Fact]
    public void El_filtro_de_empresa_aplica_tambien_al_superusuario_y_se_combina_con_la_seguridad_por_registro()
    {
        var model = Valid(Empty, Orden(perCompany: true), Partida(), Almacen());
        var e = model["Orden"];

        var admin = RecordFilter.For(e, User(Norte, superuser: true), write: false)!;
        Assert.Equal("t.[CompanyId] = @sec0", admin.Sql);
        Assert.Equal(7, admin.Parameters.Single().Value);
        // Sin empresa no hay registros (el id 0 no existe).
        Assert.Equal(0, RecordFilter.For(e, User(null), write: false)!.Parameters.Single().Value);
        // Un catálogo compartido no se filtra.
        Assert.Null(RecordFilter.For(model["Almacen"], User(Norte), write: false));

        var secured = e with { Security = new SecurityPolicy { Records = [new RecordGrant { Roles = ["Solicitante"], Scope = "own" }] } };
        var p = RecordFilter.For(secured, User(Norte, false, "Solicitante"), write: true)!;
        Assert.Equal("(t.[CompanyId] = @sec0 AND (t.[CreatedBy] = @sec1))", p.Sql);
        Assert.Equal(new object[] { 7, 5 }, p.Parameters.Select(x => x.Value));
        Assert.Equal("t.[CompanyId] = @sec0", RecordFilter.For(secured, User(Norte, superuser: true), write: true)!.Sql);
        Assert.Equal("(t.[CompanyId] = @sec0 AND 1 = 0)", RecordFilter.For(secured, User(Norte), write: false)!.Sql);

        // La partida hereda la empresa de su documento.
        var line = RecordFilter.ForRecordOrParent(model["OrdenPartida"], model, User(Norte))!;
        Assert.Equal("t.[Orden] IN (SELECT t.[Id] FROM [app].[Orden] t WHERE t.[IsDeleted] = 0 AND t.[CompanyId] = @sec0)", line.Sql);
        // Los reportes combinan varios filtros renombrando los parámetros.
        Assert.Equal("t.[CompanyId] = @r10", admin.Prefixed("r1").Sql);
    }

    // ---------------- Funciones de empresa y moneda ----------------

    private sealed class Scope(Dictionary<string, object?> values) : IExprScope
    {
        public object? Field(string name) => values.GetValueOrDefault(name);
        public object? Related(string refField, string field) => null;
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
    }

    private sealed class Rates : ICurrencyRates
    {
        public List<(string From, string To, DateTime Date)> Asked { get; } = [];
        public decimal? Rate(string from, string to, DateTime date)
        {
            Asked.Add((from, to, date));
            return (from, to) switch { ("USD", "MXN") => 18.25m, ("EUR", "MXN") => null, _ => null };
        }
    }

    private static object? Eval(string expr, ExprEnv env, Dictionary<string, object?>? values = null) =>
        ExprEvaluator.Evaluate(expr, new Scope(values ?? []), env);

    [Fact]
    public void Funciones_de_empresa_tipo_de_cambio_y_conversion()
    {
        var rates = new Rates();
        var env = new ExprEnv(1, [], null) { Company = "NORTE", BaseCurrency = "MXN", Rates = rates };
        var fecha = new DateTime(2026, 10, 1);
        var values = new Dictionary<string, object?> { ["Moneda"] = "usd", ["Total"] = 100m, ["Fecha"] = fecha };

        Assert.Equal("MXN", Eval("BASECURRENCY()", env));
        Assert.Equal("NORTE", Eval("CURRENTCOMPANY()", env));
        Assert.Equal(18.25m, Eval("RATE(Moneda, BASECURRENCY(), Fecha)", env, values));
        Assert.Equal(("USD", "MXN", fecha), rates.Asked.Last()); // códigos en mayúsculas, fecha del registro
        Assert.Equal(1825m, Eval("CONVERT(Total, Moneda, 'MXN', Fecha)", env, values));
        Eval("RATE('USD', 'MXN')", env);
        Assert.Equal(DateTime.Today, rates.Asked.Last().Date); // sin fecha: la de hoy

        // Misma moneda = 1 sin consultar; sin tipo registrado, sin moneda, sin fecha o sin importe: desconocido (nulo).
        var asked = rates.Asked.Count;
        Assert.Equal(1m, Eval("RATE('MXN', BASECURRENCY())", env));
        Assert.Equal(asked, rates.Asked.Count);
        Assert.Null(Eval("RATE('EUR', 'MXN')", env));
        Assert.Null(Eval("CONVERT(Total, 'EUR', 'MXN')", env, values));
        Assert.Null(Eval("RATE(Nada, 'MXN')", env, values));
        Assert.Null(Eval("RATE('USD', 'MXN', Nada)", env, values));
        Assert.Null(Eval("CONVERT(Nada, 'USD', 'MXN')", env, values));
        // Una regla sobre un tipo desconocido no se viola (lógica de tres valores).
        Assert.True(ExprEvaluator.Test("RATE('EUR', 'MXN') > 0", new Scope(values), env, whenNull: true));

        // Sin empresa en el contexto: moneda base y empresa vacías.
        Assert.Null(Eval("BASECURRENCY()", ExprEnv.System));
        Assert.Null(Eval("RATE('USD', 'MXN')", ExprEnv.System));
    }

    [Fact]
    public void El_analizador_conoce_las_funciones_nuevas()
    {
        var e = Valid(Empty, Orden(perCompany: true), Partida())["Orden"];
        var model = new Dictionary<string, EntityDef>(Ci) { ["Orden"] = e };
        Assert.Empty(ExprAnalyzer.Analyze("CONVERT(Total, Moneda, BASECURRENCY(), TODAY()) + RATE(Moneda, 'MXN')", e, model, Roles).Errors);
        Assert.Contains("RATE recibe 2 a 3 argumento(s).", ExprAnalyzer.Analyze("RATE('USD')", e, model, Roles).Errors);
        Assert.Contains("CONVERT recibe 3 a 4 argumento(s).", ExprAnalyzer.Analyze("CONVERT(Total, 'USD')", e, model, Roles).Errors);
        Assert.Contains("BASECURRENCY recibe 0 argumento(s).", ExprAnalyzer.Analyze("BASECURRENCY(1)", e, model, Roles).Errors);
        Assert.Equal(new HashSet<string> { "IF", "BASECURRENCY", "ISBLANK" }, ExprAnalyzer.UsedFunctions("IF(ISBLANK(Moneda), BASECURRENCY(), Moneda)"));
        Assert.Empty(ExprAnalyzer.UsedFunctions("(("));
    }

    // ---------------- Diff de paquetes (009) ----------------

    [Fact]
    public void El_diff_lleva_por_empresa_y_no_vuelve_a_compartido()
    {
        var shared = Valid(Empty, Almacen());
        var perCompany = Valid(Empty, Almacen(perCompany: true));
        var none = new Dictionary<string, DashboardDef>(Ci);

        var fresh = ModelDiff.Compute(perCompany, none, Empty, none, "p");
        Assert.True(((CreateEntityOp)fresh.Plan.Operations.Single()).PerCompany);

        var change = ModelDiff.Compute(perCompany, none, shared, none, "p");
        Assert.Empty(change.Conflicts);
        Assert.Equal(true, Assert.IsType<UpdateEntityOp>(change.Plan.Operations.Single()).PerCompany);
        Assert.True(Valid(shared, [.. change.Plan.Operations])["Almacen"].PerCompany);

        var back = ModelDiff.Compute(shared, none, perCompany, none, "p");
        Assert.Contains(back.Conflicts, c => c.Contains("no vuelve a ser compartido"));
        Assert.Empty(ModelDiff.Compute(perCompany, none, perCompany, none, "p").Plan.Operations);
    }

    // ---------------- Compras 1.6.0 e Inventario 1.4.0 ----------------

    [Fact]
    public void Compras_e_Inventario_separan_las_operaciones_por_empresa_y_convierten_a_la_moneda_base()
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        var roles = new HashSet<string>(Roles, Ci);
        var folder = ModulesFolder();
        foreach (var file in new[] { "compras.json", "inventario.json" })
        {
            var p = JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(folder, file)), PackageJson.Read)!;
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, Errors(c));
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }

        string[] perCompany = ["Almacen", "Requisicion", "OrdenCompra", "Recepcion", "Existencia", "MovimientoInventario", "Traspaso"];
        Assert.Equal(perCompany.Order(Ci), model.Values.Where(e => e.PerCompany).Select(e => e.Name).Order(Ci));
        Assert.All(["Proveedor", "Producto", "Moneda", "UnidadMedida", "CondicionPago", "CategoriaProducto", "Departamento"],
            n => Assert.False(model[n].PerCompany));

        var oc = model["OrdenCompra"];
        Assert.Equal("RATE(Moneda.Clave, BASECURRENCY(), Fecha)", oc.FindField("TipoCambio")!.DefaultValue);
        Assert.Equal("Total en moneda base", oc.FindField("TotalMXN")!.Label);
        Assert.Contains(oc.Rules!, r => r.When == "Moneda.Clave = BASECURRENCY()");
        var json = JsonSerializer.Serialize(new { oc.Rules, oc.Layout, oc.Reports, boards }, MetadataStore.Json);
        Assert.DoesNotContain("'MXN'", json);
        Assert.DoesNotContain("Total MXN", json);
    }

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }
}
