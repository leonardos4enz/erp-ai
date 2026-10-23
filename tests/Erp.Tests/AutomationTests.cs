using Erp.Api.Data;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 005: validación estática de automatizaciones, buscador filtrado y transiciones del sistema.</summary>
public class AutomationCompilerTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Almacenista" };

    private static FieldDef F(string name, string type) => new() { Name = name, Label = name, Type = type };

    /// <summary>Orden con partidas (cantidad, recibido, pendiente) y proceso; recepción con partidas ligadas a la orden.</summary>
    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Producto", Label = "Producto", LabelPlural = "Productos", Fields =
            [F("Clave", FieldTypes.Text), new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Decimal }] },
        new CreateEntityOp { Name = "Orden", Label = "Orden", LabelPlural = "Órdenes", Fields = [F("Folio", FieldTypes.Autonumber)] },
        new CreateEntityOp { Name = "OrdenPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "Orden" },
            new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto" },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Recibida", Label = "Recibida", Type = FieldTypes.Decimal, Editable = false },
            new FieldDef { Name = "Pendiente", Label = "Pendiente", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "Cantidad - Recibida" },
        ] },
        new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "OrdenPartida" } },
        new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Pendiente", Label = "Pendiente", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "SUM(Partidas.Pendiente)" } },
        new SetWorkflowOp { Entity = "Orden", Workflow = new()
        {
            Initial = "Enviada",
            States = [new() { Name = "Enviada", Label = "Enviada" }, new() { Name = "Recibida", Label = "Recibida", Editable = false }],
            Transitions = [new() { Name = "recibir", Label = "Recibir", From = ["Enviada"], To = "Recibida", System = true }],
        } },
        new CreateEntityOp { Name = "Recepcion", Label = "Recepción", LabelPlural = "Recepciones", Fields =
            [F("Folio", FieldTypes.Autonumber), new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Reference, ReferenceEntity = "Orden" }] },
        new CreateEntityOp { Name = "RecepcionPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Recepcion", Label = "Recepción", Type = FieldTypes.Parent, ReferenceEntity = "Recepcion" },
            new FieldDef { Name = "PartidaOrden", Label = "Partida de la orden", Type = FieldTypes.Reference, ReferenceEntity = "OrdenPartida",
                LookupFilter = new("Orden", "Recepcion.Orden") },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
        ] },
        new AddFieldOp { Entity = "Recepcion", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "RecepcionPartida" } },
        new SetWorkflowOp { Entity = "Recepcion", Workflow = new()
        {
            Initial = "Borrador",
            States = [new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "Confirmada", Label = "Confirmada", Editable = false }],
            Transitions = [new() { Name = "confirmar", Label = "Confirmar", From = ["Borrador"], To = "Confirmada", Roles = ["Almacenista"] }],
        } },
    ];

    private static Automation Receive(params AutomationAction[] actions) => new()
    {
        Name = "Acumular", Label = "Acumular recibido", On = AutomationEvents.EnterState, State = "Confirmada", Actions = actions,
    };

    private static readonly AutomationAction Rollup = new()
    {
        Type = AutomationActions.Rollup, Lines = "Partidas", Target = "PartidaOrden", Field = "Recibida", Sum = "Cantidad",
        Filter = "Recepcion.Estado = 'Confirmada'",
    };

    private static readonly AutomationAction Close = new()
    {
        Type = AutomationActions.Transition, Target = "Orden", Transition = "recibir", When = "Orden.Pendiente <= 0",
    };

    private static CompiledPlan Compile(IEnumerable<Operation> ops, IReadOnlyDictionary<string, EntityDef>? model = null) =>
        PlanCompiler.Compile(new ChangePlan { Operations = ops.ToList() },
            model ?? new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    [Fact]
    public void Receiving_flow_compiles_rollup_and_system_transition()
    {
        var result = Compile([.. Base(), new SetAutomationsOp { Entity = "Recepcion", Automations = [Receive(Rollup, Close)] }]);
        Assert.True(result.Preview.Valid, Errors(result));
        var recepcion = result.ResultingModel["Recepcion"];
        Assert.Equal("Acumular", Assert.Single(recepcion.Automations!).Name);
        Assert.True(result.ResultingModel["Orden"].Workflow!.Transitions[0].System);
        Assert.Equal("Recepcion.Orden", result.ResultingModel["RecepcionPartida"].FindField("PartidaOrden")!.LookupFilter!.Source);
    }

    [Theory]
    [InlineData("rollup-field-not-numeric")]
    [InlineData("rollup-field-is-formula")]
    [InlineData("unknown-transition")]
    [InlineData("unknown-state")]
    [InlineData("bad-expression")]
    [InlineData("target-not-reference")]
    [InlineData("unknown-lines")]
    [InlineData("create-missing-required")]
    [InlineData("update-computed-field")]
    public void Invalid_automations_are_rejected(string kind)
    {
        var auto = kind switch
        {
            "rollup-field-not-numeric" => Receive(Rollup with { Field = "Producto" }),
            "rollup-field-is-formula" => Receive(Rollup with { Field = "Pendiente" }),
            "unknown-transition" => Receive(Close with { Transition = "cerrar" }),
            "unknown-state" => Receive(Rollup) with { State = "Inventado" },
            "bad-expression" => Receive(Close with { When = "Orden.NoExiste > 0" }),
            "target-not-reference" => Receive(Close with { Target = "Folio" }),
            "unknown-lines" => Receive(Rollup with { Lines = "Detalle" }),
            "create-missing-required" => Receive(new AutomationAction { Type = AutomationActions.Create, Entity = "Bitacora", Set = [new("Nota", "'x'")] }),
            "update-computed-field" => Receive(new AutomationAction { Type = AutomationActions.Update, Lines = "Partidas", Target = "PartidaOrden", Set = [new("Pendiente", "0")] }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var bitacora = new CreateEntityOp { Name = "Bitacora", Label = "Bitácora", LabelPlural = "Bitácoras", Fields =
            [F("Nota", FieldTypes.Text), new FieldDef { Name = "Obligatorio", Label = "Obligatorio", Type = FieldTypes.Text, Required = true }] };
        var result = Compile([.. Base(), bitacora, new SetAutomationsOp { Entity = "Recepcion", Automations = [auto] }]);
        Assert.False(result.Preview.Valid);
    }

    [Fact]
    public void Automations_cannot_be_declared_on_lines() =>
        Assert.False(Compile([.. Base(), new SetAutomationsOp { Entity = "RecepcionPartida", Automations = [Receive(Rollup)] }]).Preview.Valid);

    [Fact]
    public void Cycles_between_catalogs_are_rejected()
    {
        // Recepción → (editar) Orden, y Orden al editarse → (editar) la recepción: ciclo.
        var ordenRef = new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "UltimaRecepcion", Label = "Última", Type = FieldTypes.Reference, ReferenceEntity = "Recepcion" } };
        var nota = new AddFieldOp { Entity = "Recepcion", Field = F("Nota", FieldTypes.Text) };
        var toOrder = new Automation { Name = "HaciaOrden", Label = "Hacia la orden", On = AutomationEvents.Update, Actions =
            [new() { Type = AutomationActions.Update, Target = "Orden", Set = [new("UltimaRecepcion", "1")] }] };
        var toReception = new Automation { Name = "HaciaRecepcion", Label = "Hacia la recepción", On = AutomationEvents.Update, Actions =
            [new() { Type = AutomationActions.Update, Target = "UltimaRecepcion", Set = [new("Nota", "'x'")] }] };
        var result = Compile([.. Base(), ordenRef, nota,
            new SetAutomationsOp { Entity = "Recepcion", Automations = [toOrder] },
            new SetAutomationsOp { Entity = "Orden", Automations = [toReception] }]);
        Assert.False(result.Preview.Valid);
        Assert.Contains("Ciclo de automatizaciones", Errors(result));

        // Sin la automatización de regreso no hay ciclo.
        Assert.True(Compile([.. Base(), ordenRef, nota, new SetAutomationsOp { Entity = "Recepcion", Automations = [toOrder] }]).Preview.Valid);
    }

    [Fact]
    public void Merge_keeps_other_automations_and_replaces_by_name()
    {
        var first = Compile([.. Base(), new SetAutomationsOp { Entity = "Recepcion", Automations = [Receive(Rollup)] }]);
        Assert.True(first.Preview.Valid, Errors(first));
        var other = Receive(Close) with { Name = "Cerrar", Label = "Cerrar orden" };

        var merged = Compile([new SetAutomationsOp { Entity = "Recepcion", Merge = true, Automations = [other] }], first.ResultingModel);
        Assert.Equal(["Acumular", "Cerrar"], merged.ResultingModel["Recepcion"].Automations!.Select(a => a.Name));

        var replaced = Compile([new SetAutomationsOp { Entity = "Recepcion", Automations = [other] }], first.ResultingModel);
        Assert.Equal(["Cerrar"], replaced.ResultingModel["Recepcion"].Automations!.Select(a => a.Name));
    }

    [Fact]
    public void Changing_a_target_revalidates_automations_of_other_catalogs()
    {
        var first = Compile([.. Base(), new SetAutomationsOp { Entity = "Recepcion", Automations = [Receive(Rollup)] }]);
        // Archivar el campo que recibe la suma rompería la automatización de Recepción, aunque el plan no la toque.
        var broken = Compile([new ArchiveFieldOp { Entity = "OrdenPartida", Field = "Recibida" }], first.ResultingModel);
        Assert.False(broken.Preview.Valid);
    }

    [Fact]
    public void Rollup_by_match_validates_keys_and_required_fields_when_creating()
    {
        var stock = new CreateEntityOp { Name = "Existencia", Label = "Existencia", LabelPlural = "Existencias", Fields =
        [
            new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto", Editable = false },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal, Editable = false },
        ] };
        var move = new CreateEntityOp { Name = "Movimiento", Label = "Movimiento", LabelPlural = "Movimientos", Fields =
        [
            new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto" },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
        ] };
        AutomationAction Match(string source) => new()
        {
            Type = AutomationActions.Rollup, Entity = "Existencia", Match = [new("Producto", source)], CreateMissing = true,
            Field = "Cantidad", Sum = "Cantidad",
        };
        Automation Auto(AutomationAction a) => new() { Name = "Stock", Label = "Stock", On = AutomationEvents.Create, Actions = [a] };

        var ok = Compile([.. Base(), stock, move, new SetAutomationsOp { Entity = "Movimiento", Automations = [Auto(Match("Producto"))] }]);
        Assert.True(ok.Preview.Valid, Errors(ok));
        // Clave de distinto tipo (producto contra cantidad).
        Assert.False(Compile([.. Base(), stock, move, new SetAutomationsOp { Entity = "Movimiento", Automations = [Auto(Match("Cantidad"))] }]).Preview.Valid);
    }

    [Fact]
    public void Lookup_filter_must_point_to_existing_fields()
    {
        var ops = Base();
        var i = ops.FindIndex(o => o is CreateEntityOp { Name: "RecepcionPartida" });
        var rp = (CreateEntityOp)ops[i];
        ops[i] = rp with { Fields = rp.Fields.Select(f => f.Name == "PartidaOrden" ? f with { LookupFilter = new("Orden", "Recepcion.NoExiste") } : f).ToList() };
        Assert.False(Compile(ops).Preview.Valid);
        ops[i] = rp with { Fields = rp.Fields.Select(f => f.Name == "PartidaOrden" ? f with { LookupFilter = new("NoExiste", "Recepcion.Orden") } : f).ToList() };
        Assert.False(Compile(ops).Preview.Valid);
    }

    [Fact]
    public void Text_formula_can_be_the_display_field()
    {
        var ok = Compile([.. Base(),
            new AddFieldOp { Entity = "OrdenPartida", Field = new FieldDef { Name = "Descripcion", Label = "Descripción", Type = FieldTypes.Formula, ResultType = "text", Formula = "Producto.Clave & ' · pendiente ' & Pendiente" } },
            new UpdateEntityOp { Entity = "OrdenPartida", DisplayField = "Descripcion" }]);
        Assert.True(ok.Preview.Valid, Errors(ok));
        Assert.False(Compile([.. Base(), new UpdateEntityOp { Entity = "OrdenPartida", DisplayField = "Pendiente" }]).Preview.Valid);
    }
}

public class DecimalScaleTests
{
    private static readonly FieldDef Pendiente = new() { Name = "Pendiente", Label = "Pendiente", Type = FieldTypes.Formula, ResultType = "decimal", Scale = 2 };

    // Regresión (spec 005): el texto de una fórmula dependía de si el decimal venía de memoria o de la base.
    [Fact]
    public void Coerced_decimals_keep_the_field_scale_like_the_database()
    {
        Assert.Equal("10.00", ((decimal)RecordService.Coerce(Pendiente, 10m - 0m)!).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2.50", ((decimal)RecordService.Coerce(Pendiente, 2.5m)!).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1.24", ((decimal)RecordService.Coerce(Pendiente, 1.235m)!).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
