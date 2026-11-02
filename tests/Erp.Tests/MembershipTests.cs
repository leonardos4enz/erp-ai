using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 051: «pertenece a». El parser (<c>X IN Campo</c> = <c>CONTAINS</c>), el analizador (ruta a un campo de varios valores,
/// valor compatible), el evaluador en memoria (dos valores), la traducción a <c>EXISTS</c> parametrizado de las reglas de acceso y
/// los acumulados, y la validación de <c>mode</c> en las asignaciones.
/// </summary>
public class MembershipTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrador", "Solicitante" };
    private static readonly Dictionary<string, EntityDef> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static FieldDef Text(string name) => new() { Name = name, Label = name, Type = FieldTypes.Text };
    private static FieldDef Ref(string name, string target) => new() { Name = name, Label = name, Type = FieldTypes.Reference, ReferenceEntity = target };

    /// <summary>Centro (responsables y categorías) ← Requisición ← Orden ← Recepción (tres saltos hasta la lista).</summary>
    private static CompiledPlan Compile(params Operation[] ops) => PlanCompiler.Compile(new ChangePlan
    {
        Operations =
        [
            new CreateEntityOp { Name = "Categoria", Label = "Categoría", LabelPlural = "Categorías", Fields = [Text("Nombre")] },
            new CreateEntityOp
            {
                Name = "Centro", Label = "Centro", LabelPlural = "Centros", Fields =
                [
                    Text("Nombre"),
                    new() { Name = "Responsables", Label = "Responsables", Type = FieldTypes.MultiUser },
                    new() { Name = "Categorias", Label = "Categorías", Type = FieldTypes.MultiReference, ReferenceEntity = "Categoria" },
                ],
            },
            new CreateEntityOp
            {
                Name = "Requisicion", Label = "Requisición", LabelPlural = "Requisiciones", Fields =
                [
                    Text("Nota"), Ref("Centro", "Centro"), Ref("Categoria", "Categoria"),
                    new() { Name = "Solicitante", Label = "Solicitante", Type = FieldTypes.User },
                    new() { Name = "Seguidores", Label = "Seguidores", Type = FieldTypes.MultiUser },
                ],
            },
            new CreateEntityOp { Name = "Orden", Label = "Orden", LabelPlural = "Órdenes", Fields = [Text("Folio"), Ref("Requisicion", "Requisicion")] },
            new CreateEntityOp { Name = "Recepcion", Label = "Recepción", LabelPlural = "Recepciones", Fields = [Text("Folio"), Ref("Orden", "Orden")] },
            .. ops,
        ],
    }, Empty, Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));
    private static IReadOnlyDictionary<string, EntityDef> Model() => Compile().ResultingModel;
    private static IReadOnlyList<string> Analyze(string expr, string entity = "Requisicion")
    {
        var m = Model();
        return ExprAnalyzer.Analyze(expr, m[entity], m, Roles).Errors;
    }

    // ---------------- Lenguaje ----------------

    [Fact]
    public void X_IN_Campo_es_CONTAINS_y_la_lista_entre_parentesis_sigue_igual()
    {
        var node = ExprParser.Parse("CURRENTUSER() IN Centro.Responsables");
        var call = Assert.IsType<Call>(node);
        Assert.Equal("CONTAINS", call.Name);
        Assert.Equal("Centro.Responsables", Assert.IsType<PathRef>(call.Args[0]).ToString());
        Assert.Equal("CURRENTUSER", Assert.IsType<Call>(call.Args[1]).Name);
        var same = Assert.IsType<Call>(ExprParser.Parse("CONTAINS(Centro.Responsables, CURRENTUSER())"));
        Assert.Equal(("CONTAINS", "Centro.Responsables"), (same.Name, same.Args[0].ToString()));
        Assert.IsType<InList>(ExprParser.Parse("Nota IN ('a', 'b')"));
        Assert.Throws<ExprException>(() => ExprParser.Parse("Nota IN 5"));
        // NOT y AND alrededor, como cualquier comparación.
        Assert.IsType<Unary>(ExprParser.Parse("NOT CURRENTUSER() IN Seguidores"));
        Assert.IsType<Binary>(ExprParser.Parse("CURRENTUSER() IN Seguidores AND Nota = 'x'"));
    }

    [Theory]
    [InlineData("CURRENTUSER() IN Seguidores", "Requisicion")]
    [InlineData("Solicitante IN Centro.Responsables", "Requisicion")]
    [InlineData("Categoria IN Centro.Categorias", "Requisicion")]
    [InlineData("CONTAINS(Requisicion.Centro.Responsables, CURRENTUSER())", "Orden")]
    [InlineData("NOT (CURRENTUSER() IN Orden.Requisicion.Centro.Responsables)", "Recepcion")]
    [InlineData("5 IN Seguidores", "Requisicion")]
    public void Pertenencia_valida_propia_o_hasta_tres_saltos(string expr, string entity)
    {
        Assert.Empty(Analyze(expr, entity));
    }

    [Theory]
    [InlineData("CURRENTUSER() IN Nota", "no tiene varios valores")]
    [InlineData("CURRENTUSER() IN Centro.Nombre", "no tiene varios valores")]
    [InlineData("Nota IN Seguidores", "X debe ser CURRENTUSER(), un campo de personas o un número")]
    [InlineData("CURRENTUSER() IN Centro.Categorias", "X debe ser un campo que apunte a Categoria o un número")]
    [InlineData("Centro IN Centro.Categorias", "X debe ser un campo que apunte a Categoria")]
    [InlineData("CONTAINS('Seguidores', CURRENTUSER())", "CONTAINS necesita un campo de varios valores")]
    [InlineData("CURRENTUSER() IN Nota.Responsables", "no es una referencia")]
    [InlineData("CURRENTUSER() IN Centro.NoExiste", "'NoExiste' no existe en Centro")]
    public void CONTAINS_sobre_algo_que_no_es_una_lista_o_con_un_valor_incompatible_es_error(string expr, string message)
    {
        Assert.Contains(Analyze(expr), m => m.Contains(message));
    }

    [Fact]
    public void Cuatro_saltos_es_error_del_parser()
    {
        Assert.Throws<ExprException>(() => ExprParser.Parse("CURRENTUSER() IN A.B.C.D.Responsables"));
    }

    private sealed class FakeScope(Dictionary<string, IReadOnlyCollection<long>?> lists) : IExprScope
    {
        public object? Field(string name) => name == "Vacio" ? null : 7m;
        public object? Related(string refField, string field) => null;
        public IReadOnlyList<IExprScope> Lines(string linesField) => [];
        public IReadOnlyCollection<long>? Members(string field, IReadOnlyList<string> path) =>
            lists.GetValueOrDefault(path.Count == 0 ? field : $"{field}.{string.Join(".", path)}");
    }

    [Fact]
    public void Pertenencia_con_lista_vacia_valor_nulo_y_NOT_tiene_dos_valores()
    {
        var scope = new FakeScope(new() { ["Seguidores"] = [7, 9], ["Vacia"] = [], ["Centro.Responsables"] = [], ["Zona.Encargados"] = null });
        var me = new ExprEnv(7, [], null);
        var other = new ExprEnv(8, [], null);
        Assert.True(ExprEvaluator.Test("CURRENTUSER() IN Seguidores", scope, me, whenNull: false));
        Assert.False(ExprEvaluator.Test("CURRENTUSER() IN Seguidores", scope, other, whenNull: true));
        Assert.True(ExprEvaluator.Test("Solicitante IN Seguidores", scope, other, whenNull: false));   // el campo vale 7
        Assert.False(ExprEvaluator.Test("Vacio IN Seguidores", scope, me, whenNull: true));          // valor nulo: falso, no desconocido
        Assert.False(ExprEvaluator.Test("CURRENTUSER() IN Vacia", scope, me, whenNull: true));
        Assert.False(ExprEvaluator.Test("CURRENTUSER() IN Centro.Responsables", scope, me, whenNull: true)); // ruta rota
        Assert.True(ExprEvaluator.Test("NOT CURRENTUSER() IN Centro.Responsables", scope, me, whenNull: false)); // NOT decide
        Assert.True(ExprEvaluator.Test("9 IN Seguidores", scope, me, whenNull: false));
        // Regresión (revisión adversarial 051, F-01): una lista fuera del alcance de quien mira es desconocida, también bajo NOT
        // (antes daba falso y «NOT … IN …» se volvía verdadero: deletableWhen dejaba eliminar).
        Assert.Null(ExprEvaluator.Evaluate("CURRENTUSER() IN Zona.Encargados", scope, me));
        Assert.Null(ExprEvaluator.Evaluate("NOT CURRENTUSER() IN Zona.Encargados", scope, me));
    }

    // ---------------- Traducción a SQL ----------------

    private static UserContext User(params string[] roles) => new()
    {
        Id = 42, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles,
    };

    private static (EntityDef Entity, IReadOnlyDictionary<string, EntityDef> Model) Secured(string where, string entity = "Requisicion")
    {
        var r = Compile(new SetSecurityOp
        {
            Entity = entity, Security = new SecurityPolicy { Records = [new() { Roles = ["Solicitante"], Where = where }] },
        });
        Assert.True(r.Preview.Valid, Errors(r));
        return (r.ResultingModel[entity], r.ResultingModel);
    }

    [Fact]
    public void La_pertenencia_se_traduce_a_EXISTS_con_la_tabla_de_union_y_el_valor_como_parametro()
    {
        var (e, model) = Secured("CURRENTUSER() IN Centro.Responsables");
        var p = RecordFilter.For(e, User("Solicitante"), write: false, model)!;
        Assert.Equal(
            "(EXISTS (SELECT 1 FROM [app].[Centro__Responsables] j1 WHERE j1.[OwnerId] = " +
            "(SELECT o0.[Id] FROM [app].[Centro] o0 WHERE o0.[Id] = t.[Centro] AND o0.[IsDeleted] = 0) AND j1.[ValueId] = @sec0))", p.Sql);
        Assert.Equal(("@sec0", (object)42), p.Parameters.Single());

        // Propia: la llave es el registro.
        var (own, m2) = Secured("CURRENTUSER() IN Seguidores");
        Assert.Equal("(EXISTS (SELECT 1 FROM [app].[Requisicion__Seguidores] j0 WHERE j0.[OwnerId] = t.[Id] AND j0.[ValueId] = @sec0))",
            RecordFilter.For(own, User("Solicitante"), write: false, m2)!.Sql);

        // Tres saltos: subconsultas por Id hasta el dueño, vigente en cada salto.
        var (rec, m3) = Secured("CURRENTUSER() IN Orden.Requisicion.Centro.Responsables", "Recepcion");
        var sql = RecordFilter.For(rec, User("Solicitante"), write: true, m3)!.Sql;
        Assert.Contains("FROM [app].[Centro__Responsables]", sql);
        Assert.Contains("FROM [app].[Orden] r0 WHERE r0.[Id] = t.[Orden] AND r0.[IsDeleted] = 0", sql);
        Assert.Contains("FROM [app].[Requisicion] r1 WHERE r1.[Id] = ", sql);
        Assert.Contains("FROM [app].[Centro] o2 WHERE o2.[Id] = ", sql);

        // Una columna propia como valor, sin parámetro.
        var (col, m4) = Secured("Solicitante IN Centro.Responsables");
        Assert.Contains("j1.[ValueId] = t.[Solicitante]", RecordFilter.For(col, User("Solicitante"), write: false, m4)!.Sql);
    }

    [Fact]
    public void Sin_el_modelo_una_ruta_no_se_traduce_y_falla_cerrado()
    {
        var (e, _) = Secured("CURRENTUSER() IN Centro.Responsables");
        Assert.Throws<ExprException>(() => RecordFilter.For(e, User("Solicitante"), write: false));
        // HasGrants no traduce: no necesita el modelo.
        Assert.True(RecordFilter.HasGrants(e, User("Solicitante")));
    }

    [Theory]
    [InlineData("Centro.Nombre = 'Norte'", "solo usa campos del propio catálogo")]
    [InlineData("Nota IN Centro.Responsables", "X debe ser CURRENTUSER(), un campo o un número")]
    [InlineData("Centro IN Centro.Categorias", "X debe ser CURRENTUSER(), un campo o un número")]
    [InlineData("CURRENTUSER() IN Centro.Nombre", "no tiene varios valores")]
    [InlineData("CURRENTUSER() IN Centro.Categorias", "X debe ser")]
    [InlineData("'x' IN Seguidores", "X debe ser")]
    public void Una_regla_de_acceso_rechaza_lo_que_no_es_una_pertenencia_valida(string where, string message)
    {
        var r = Compile(new SetSecurityOp { Entity = "Requisicion", Security = new SecurityPolicy { Records = [new() { Roles = ["Solicitante"], Where = where }] } });
        Assert.Contains(message, Errors(r));
    }

    [Fact]
    public void En_un_acumulado_la_pertenencia_se_traduce_pero_CURRENTUSER_no()
    {
        var model = Model();
        var bag = new RecordFilter.ParamBag("@rf");
        var sql = RecordFilter.TranslateRollup("Solicitante IN Centro.Responsables AND 3 IN Seguidores", model["Requisicion"], model, bag);
        Assert.Contains("[app].[Requisicion__Seguidores]", sql);
        Assert.Contains("= @rf0", sql);
        Assert.Equal(3L, bag.Items.Single().Value);
        Assert.Throws<ExprException>(() => RecordFilter.TranslateRollup("CURRENTUSER() IN Seguidores", model["Requisicion"], model, new RecordFilter.ParamBag("@rf")));
    }

    [Fact]
    public void Archivar_la_lista_que_usa_una_regla_de_acceso_es_error_del_plan()
    {
        var r = Compile(
            new SetSecurityOp { Entity = "Requisicion", Security = new SecurityPolicy { Records = [new() { Roles = ["Solicitante"], Where = "CURRENTUSER() IN Centro.Responsables" }] } },
            new ArchiveFieldOp { Entity = "Centro", Field = "Responsables" });
        Assert.Contains("seguridad", Errors(r));
    }

    // ---------------- Asignaciones ----------------

    private static Workflow Flow(params FieldAssignment[] set) => new()
    {
        Initial = "Borrador", States = [new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "Enviada", Label = "Enviada" }],
        Transitions = [new() { Name = "enviar", Label = "Enviar", From = ["Borrador"], To = "Enviada", Set = set }],
    };

    [Fact]
    public void Una_transicion_agrega_o_quita_con_mode_y_un_campo_simple_no_lo_admite()
    {
        Assert.True(Compile(new SetWorkflowOp { Entity = "Requisicion", Workflow = Flow(new FieldAssignment("Seguidores", "CURRENTUSER()", AssignModes.Add)) }).Preview.Valid);
        Assert.True(Compile(new SetWorkflowOp { Entity = "Requisicion", Workflow = Flow(new FieldAssignment("Seguidores", "Solicitante", AssignModes.Remove)) }).Preview.Valid);
        Assert.Contains("«mode» solo aplica a un campo de varios valores; 'Nota' no lo es",
            Errors(Compile(new SetWorkflowOp { Entity = "Requisicion", Workflow = Flow(new FieldAssignment("Nota", "'x'", AssignModes.Add)) })));
        Assert.Contains("indica si se agrega o se quita",
            Errors(Compile(new SetWorkflowOp { Entity = "Requisicion", Workflow = Flow(new FieldAssignment("Seguidores", "CURRENTUSER()", "replace")) })));
    }

    [Fact]
    public void Una_automatizacion_agrega_con_mode_y_la_captura_no_lo_admite()
    {
        Automation Auto(AutomationAction a) => new() { Name = "Seguir", Label = "Seguir", On = AutomationEvents.Create, Actions = [a] };
        var ok = Compile(new SetAutomationsOp
        {
            Entity = "Orden", Automations = [Auto(new() { Type = AutomationActions.Update, Target = "Requisicion", Set = [new("Seguidores", "CURRENTUSER()", AssignModes.Add)] })],
        });
        Assert.True(ok.Preview.Valid, Errors(ok));
        var bad = Compile(new SetAutomationsOp
        {
            Entity = "Orden", Automations =
            [
                Auto(new()
                {
                    Type = AutomationActions.Create, Entity = "Requisicion", Set = [new("Nota", "Folio")],
                    Capture = [new("Folio", "Nota", AssignModes.Add)],
                }),
            ],
        });
        Assert.Contains("captura: 'Folio' no se captura", Errors(bad));
    }

    [Fact]
    public void El_modo_no_cambia_la_forma_canonica_de_una_asignacion_sin_el()
    {
        // Paquetes firmados (009): «mode» se omite cuando no está.
        Assert.Equal("""{"Field":"Nota","Value":"Folio"}""", System.Text.Json.JsonSerializer.Serialize(new FieldAssignment("Nota", "Folio")));
        Assert.Contains("\"Mode\":\"add\"", System.Text.Json.JsonSerializer.Serialize(new FieldAssignment("Seguidores", "CURRENTUSER()", "add")));
    }
}
