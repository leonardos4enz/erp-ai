using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Tests;

/// <summary>
/// Spec 050: campos de varios valores. La tabla de unión (nombres validados y citados), la validación del plan, las expresiones
/// que todavía no los leen, los filtros «contiene» con <c>EXISTS</c> parametrizado y la celda de importación.
/// </summary>
public class MultiValueTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrador", "Compras" };
    private static readonly Dictionary<string, EntityDef> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static FieldDef Text(string name) => new() { Name = name, Label = name, Type = FieldTypes.Text };

    private static CompiledPlan Compile(params Operation[] ops) => PlanCompiler.Compile(new ChangePlan
    {
        Operations =
        [
            new CreateEntityOp { Name = "Categoria", Label = "Categoría", LabelPlural = "Categorías", Fields = [Text("Nombre")] },
            new CreateEntityOp
            {
                Name = "Proveedor", Label = "Proveedor", LabelPlural = "Proveedores", Fields =
                [
                    Text("Nombre"),
                    new() { Name = "Categorias", Label = "Categorías", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria" },
                    new() { Name = "Responsables", Label = "Responsables", Type = FieldTypes.MultiUser },
                ],
            },
            .. ops,
        ],
    }, Empty, Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    [Fact]
    public void La_tabla_de_union_cita_cada_parte_y_rechaza_un_identificador_invalido()
    {
        var f = new FieldDef { Name = "Categorias", Label = "Categorías", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria" };
        var sql = string.Join("\n", DdlGenerator.CreateJoinTable("Proveedor", f));
        Assert.Contains("CREATE TABLE [app].[Proveedor__Categorias]", sql);
        Assert.Contains("[OwnerId] bigint NOT NULL CONSTRAINT [FKO_Proveedor_Categorias] REFERENCES [app].[Proveedor] ([Id])", sql);
        Assert.Contains("[ValueId] bigint NOT NULL CONSTRAINT [FK_Proveedor_Categorias] REFERENCES [app].[Categoria] ([Id])", sql);
        Assert.Contains("PRIMARY KEY ([OwnerId], [ValueId])", sql);
        Assert.Contains("CREATE INDEX [IXM_Proveedor_Categorias] ON [app].[Proveedor__Categorias] ([ValueId], [OwnerId])", sql);

        // Personas: int y llave a meta.User.
        var users = string.Join("\n", DdlGenerator.CreateJoinTable("Proveedor", new FieldDef { Name = "Responsables", Label = "R", Type = FieldTypes.MultiUser }));
        Assert.Contains("[ValueId] int NOT NULL CONSTRAINT [FK_Proveedor_Responsables] REFERENCES [meta].[User] ([Id])", users);

        // Defensa en profundidad: un nombre que no pasa la lista blanca nunca llega al SQL.
        Assert.Throws<InvalidOperationException>(() => Identifiers.JoinTable("Proveedor]; DROP TABLE x;--", "Categorias"));
        Assert.Throws<InvalidOperationException>(() => Identifiers.JoinTable("Proveedor", "a b"));
        Assert.Equal("[app].[Proveedor__Categorias]", Identifiers.JoinTable("Proveedor", "Categorias"));
    }

    [Fact]
    public void Crear_el_catalogo_crea_su_tabla_de_union_y_agregar_el_campo_tambien()
    {
        var result = Compile(new AddFieldOp { Entity = "Categoria", Field = new() { Name = "Encargados", Label = "Encargados", Type = FieldTypes.MultiUser } });
        Assert.True(result.Preview.Valid, Errors(result));
        var sql = string.Join("\n", result.Sql);
        Assert.Contains("CREATE TABLE [app].[Proveedor__Categorias]", sql);
        Assert.Contains("CREATE TABLE [app].[Proveedor__Responsables]", sql);
        Assert.Contains("CREATE TABLE [app].[Categoria__Encargados]", sql);
        // Sin columna en la tabla del catálogo.
        Assert.DoesNotContain("[Categorias] bigint", sql);
        var prov = result.ResultingModel["Proveedor"];
        Assert.Equal(["Categorias", "Responsables"], prov.MultiFields.Select(f => f.Name));
        Assert.DoesNotContain(prov.StoredFields, f => FieldTypes.IsMulti(f.Type));
        Assert.Equal("Categoria", prov.FindField("Categorias")!.ReferenceEntity);
        Assert.Null(prov.FindField("Responsables")!.ReferenceEntity);
    }

    [Fact]
    public void El_plan_rechaza_lo_que_no_tiene_sentido_en_un_campo_de_varios_valores()
    {
        var r = Compile(
            new AddFieldOp { Entity = "Proveedor", Field = new() { Name = "Otras", Label = "Otras", Type = FieldTypes.MultiReference } },
            new AddFieldOp { Entity = "Proveedor", Field = new() { Name = "Nada", Label = "Nada", Type = FieldTypes.MultiReference, ReferenceEntity = "NoExiste" } },
            new AddFieldOp { Entity = "Proveedor", Field = new() { Name = "Unicas", Label = "Únicas", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria", Unique = true } },
            new AddFieldOp { Entity = "Proveedor", Field = new() { Name = "Defecto", Label = "Defecto", Type = FieldTypes.MultiUser, DefaultValue = "CURRENTUSER()" } });
        var e = Errors(r);
        Assert.Contains("debe indicar el catálogo", e);
        Assert.Contains("'NoExiste' no existe", e);
        Assert.Contains("no admite valores únicos", e);
        Assert.Contains("'Defecto' tiene varios valores y no admite valor por defecto", e);
    }

    [Fact]
    public void Ni_en_una_partida_ni_apuntando_a_una_partida()
    {
        var r = Compile(
            new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields = [Text("Folio")] },
            new CreateEntityOp
            {
                Name = "PedidoPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
                [
                    new() { Name = "Pedido", Label = "Pedido", Type = FieldTypes.Parent, ReferenceEntity = "Pedido" },
                    new() { Name = "Etiquetas", Label = "Etiquetas", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria" },
                ],
            },
            new AddFieldOp { Entity = "Pedido", Field = new() { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "PedidoPartida" } },
            new AddFieldOp { Entity = "Categoria", Field = new() { Name = "Lineas", Label = "Líneas", Type = FieldTypes.MultiReference, ReferenceEntity = "PedidoPartida" } });
        var e = Errors(r);
        Assert.Contains("PedidoPartida.Etiquetas: PedidoPartida es una partida", e);
        Assert.Contains("Categoria.Lineas: 'PedidoPartida' es una partida", e);
    }

    [Theory]
    [InlineData("Categorias")]
    [InlineData("ISBLANK(Responsables)")]
    [InlineData("Nombre & Categorias")]
    public void Una_expresion_no_lee_el_campo_todavia(string expr)
    {
        var model = Compile().ResultingModel;
        var errors = ExprAnalyzer.Analyze(expr, model["Proveedor"], model, Roles).Errors;
        Assert.Contains(errors, m => m.Contains("tiene varios valores") && m.Contains("051"));
    }

    [Fact]
    public void Tampoco_como_ultimo_salto_de_una_ruta()
    {
        var model = Compile(new CreateEntityOp
        {
            Name = "Orden", Label = "Orden", LabelPlural = "Órdenes",
            Fields = [Text("Folio"), new() { Name = "Proveedor", Label = "Proveedor", Type = FieldTypes.Reference, ReferenceEntity = "Proveedor" }],
        }).ResultingModel;
        var errors = ExprAnalyzer.Analyze("Proveedor.Categorias", model["Orden"], model, Roles).Errors;
        Assert.Contains(errors, m => m.Contains("'Proveedor.Categorias' tiene varios valores"));
        // Una regla con el campo se rechaza al validar el plan.
        var r = Compile(new SetRulesOp { Entity = "Proveedor", Rules = [new BusinessRule { Name = "Regla", Field = "Nombre", Assert = "NOT ISBLANK(Categorias)", Message = "m" }] });
        Assert.Contains("tiene varios valores", Errors(r));
        // Un reporte no agrupa por él (todavía).
        Assert.Contains("tiene varios valores", InterfaceRules.Resolve(model["Proveedor"], "Categorias", model).Error);
    }

    [Fact]
    public void Una_automatizacion_ni_una_transicion_lo_asignan_con_una_expresion()
    {
        var r = Compile(
            new CreateEntityOp { Name = "Visita", Label = "Visita", LabelPlural = "Visitas", Fields = [Text("Nota")] },
            new SetAutomationsOp
            {
                Entity = "Visita", Automations =
                [
                    new Automation { Name = "Marcar", Label = "Marcar", On = AutomationEvents.Create, Actions =
                        [new() { Type = AutomationActions.Create, Entity = "Proveedor", Set = [new("Nombre", "Nota"), new("Categorias", "1")] }] },
                ],
            },
            new SetWorkflowOp
            {
                Entity = "Proveedor", Workflow = new Workflow
                {
                    Initial = "Alta", States = [new() { Name = "Alta", Label = "Alta" }, new() { Name = "Baja", Label = "Baja" }],
                    Transitions = [new() { Name = "baja", Label = "Dar de baja", From = ["Alta"], To = "Baja", Set = [new("Responsables", "CURRENTUSER()")] }],
                },
            });
        var e = Errors(r);
        Assert.Contains("'Categorias' tiene varios valores y no se asigna", e);
        Assert.Contains("'Responsables' tiene varios valores y no se asigna", e);
    }

    // Regresión (revisión adversarial 050, F-03): una transición podía pedir el campo, que su acción no sabe capturar.
    [Fact]
    public void Una_transicion_no_pide_capturar_un_campo_de_varios_valores()
    {
        var r = Compile(new SetWorkflowOp
        {
            Entity = "Proveedor", Workflow = new Workflow
            {
                Initial = "Alta", States = [new() { Name = "Alta", Label = "Alta" }, new() { Name = "Baja", Label = "Baja" }],
                Transitions = [new() { Name = "baja", Label = "Dar de baja", From = ["Alta"], To = "Baja", RequiredFields = ["Categorias"] }],
            },
        });
        Assert.Contains("'Categorias' tiene varios valores y no se captura en una acción", Errors(r));
    }

    // Regresión (revisión adversarial 050, F-05): un catálogo que termina en «_» (Pedido_ + Tags = Pedido___Tags) se partía en el
    // primer «__» y su tabla de unión salía huérfana en el explorador.
    [Fact]
    public void El_explorador_reconoce_la_tabla_de_union_de_un_catalogo_que_termina_en_guion_bajo()
    {
        var model = PlanCompiler.Compile(new ChangePlan
        {
            Operations =
            [
                new CreateEntityOp { Name = "Categoria", Label = "Categoría", LabelPlural = "Categorías", Fields = [Text("Nombre")] },
                new CreateEntityOp { Name = "Pedido_", Label = "Pedido", LabelPlural = "Pedidos", Fields =
                    [Text("Folio"), new() { Name = "Tags", Label = "Tags", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria" }] },
            ],
        }, Empty, Roles).ResultingModel;
        static Erp.Api.Explorer.CatColumn Col(string n, string t) => new(n, t, false, false, null, null, null);
        var join = new Erp.Api.Explorer.CatTable("app", "Pedido___Tags", [Col("OwnerId", "bigint"), Col("ValueId", "bigint")], [], []);
        var db = Erp.Api.Explorer.PhysicalModel.Build(Erp.Api.Explorer.DbCatalog.App, [join], model,
            new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(db.Drift, d => d.Kind == Erp.Api.Explorer.DriftKinds.OrphanTable && d.Table == "Pedido___Tags");
    }

    [Fact]
    public void Una_plantilla_si_imprime_el_campo_propio()
    {
        var model = Compile().ResultingModel;
        Assert.True(ExprAnalyzer.IsOwnMulti("Categorias", model["Proveedor"]));
        Assert.False(ExprAnalyzer.IsOwnMulti("Nombre", model["Proveedor"]));
        Assert.False(ExprAnalyzer.IsOwnMulti("Categorias & ''", model["Proveedor"]));
    }

    private static UserContext User(params string[] roles) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles,
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase) { ["Proveedor"] = new(true, true, true, true) },
    };

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    [Fact]
    public void Los_filtros_usan_exists_sobre_la_tabla_de_union_con_parametros()
    {
        var e = Compile().ResultingModel["Proveedor"];
        var cmd = new SqlCommand();
        var sql = FilterSql.Build(e, [
            new() { Field = "Categorias", Op = "contains", Value = J(3) },
            new() { Field = "Categorias", Op = "in", Value = J(new[] { 4, 5 }) },
            new() { Field = "Responsables", Op = "contains", Value = J("$me") },
            new() { Field = "Responsables", Op = "blank" },
            new() { Field = "Categorias", Op = "notBlank" },
        ], User("Compras"), cmd.Parameters, "f");
        const string cat = "SELECT 1 FROM [app].[Proveedor__Categorias] j WHERE j.[OwnerId] = t.[Id]";
        const string res = "SELECT 1 FROM [app].[Proveedor__Responsables] j WHERE j.[OwnerId] = t.[Id]";
        Assert.Equal($"EXISTS ({cat} AND j.[ValueId] = @f0) AND EXISTS ({cat} AND j.[ValueId] IN (@f1, @f2)) AND " +
                     $"EXISTS ({res} AND j.[ValueId] = @f3) AND NOT EXISTS ({res}) AND EXISTS ({cat})", sql);
        Assert.Equal(3L, cmd.Parameters[0].Value);
        Assert.Equal(7, cmd.Parameters[3].Value); // $me = quien consulta
    }

    [Theory]
    [InlineData("Categorias", "eq", "no aplica")]
    [InlineData("Categorias", "contains", "$me solo aplica")]
    public void Un_filtro_invalido_se_explica(string field, string op, string message)
    {
        var e = Compile().ResultingModel["Proveedor"];
        var filter = new ViewFilter { Field = field, Op = op, Value = J(message == "$me solo aplica" ? "$me" : "3") };
        Assert.Contains(message, FilterSql.Validate(e, filter));
    }

    [Fact]
    public void Un_campo_obligatorio_vacio_no_cuenta_como_lleno()
    {
        // La regla de obligatorio del motor considera vacía una lista sin elementos (IsBlank).
        var isBlank = typeof(RecordService).GetMethod("IsBlank", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)isBlank.Invoke(null, [new List<long>()])!);
        Assert.False((bool)isBlank.Invoke(null, [new List<long> { 1 }])!);
    }
}
