using Erp.Api.Expressions;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Ámbito en memoria para probar el evaluador sin base de datos.</summary>
file sealed class FakeScope(Dictionary<string, object?> values, Dictionary<string, Dictionary<string, object?>>? related = null,
    Dictionary<string, List<FakeScope>>? lines = null) : IExprScope
{
    public object? Field(string name) => values.GetValueOrDefault(name);
    public object? Related(string refField, string field) => related?.GetValueOrDefault(refField)?.GetValueOrDefault(field);
    public IReadOnlyList<IExprScope> Lines(string linesField) => lines?.GetValueOrDefault(linesField) ?? [];
}

public class ExpressionEvaluatorTests
{
    private static readonly ExprEnv Env = new(UserId: 7, Roles: ["Comprador"], CreatorId: 7);

    private static object? Eval(string expr, Dictionary<string, object?>? values = null) =>
        ExprEvaluator.Evaluate(expr, new FakeScope(values ?? []), Env);

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("-5 + 2", -3)]
    [InlineData("ROUND(10 / 3, 2)", 3.33)]
    [InlineData("MAX(3, 9) - MIN(3, 9)", 6)]
    public void Arithmetic_is_decimal_exact(string expr, double expected) =>
        Assert.Equal((decimal)expected, Eval(expr));

    [Fact]
    public void Typical_line_formula()
    {
        var v = new Dictionary<string, object?> { ["Cantidad"] = 3L, ["Precio"] = 19.99m, ["Descuento"] = 10m };
        Assert.Equal(53.973m, Eval("Cantidad * Precio * (1 - Descuento / 100)", v));
    }

    [Fact]
    public void Null_counts_as_zero_in_arithmetic_and_division_by_zero_is_null()
    {
        Assert.Equal(5m, Eval("Precio + 5", new() { ["Precio"] = null }));
        Assert.Null(Eval("10 / 0"));
    }

    [Fact]
    public void Comparisons_and_three_valued_logic()
    {
        Assert.Equal(true, Eval("Total > 50000 AND Moneda = 'mxn'", new() { ["Total"] = 60000m, ["Moneda"] = "MXN" }));
        Assert.Equal(true, Eval("Estado IN ('Borrador', 'PorAutorizar')", new() { ["Estado"] = "PorAutorizar" }));
        Assert.Equal(true, Eval("NULL = NULL"));
        Assert.Equal(false, Eval("FALSE AND (1 / 0 > 1)"));
    }

    // Regresión (smoke test 003): "LimiteCredito >= 0" bloqueaba proveedores sin límite capturado.
    [Fact]
    public void Ordering_against_null_is_unknown_so_rules_on_optional_fields_pass()
    {
        var scope = new FakeScope(new() { ["LimiteCredito"] = null });
        Assert.Null(ExprEvaluator.Evaluate("LimiteCredito >= 0", scope, Env));
        Assert.True(ExprEvaluator.Test("LimiteCredito >= 0", scope, Env, whenNull: true));   // regla: no se viola
        Assert.False(ExprEvaluator.Test("LimiteCredito >= 0", scope, Env, whenNull: false)); // condición: no habilita
        Assert.Equal(false, Eval("NULL > 1 AND FALSE"));
        Assert.Equal(true, Eval("NULL > 1 OR TRUE"));
    }

    [Fact]
    public void Dates()
    {
        var v = new Dictionary<string, object?> { ["Fecha"] = new DateTime(2026, 1, 10), ["Entrega"] = new DateTime(2026, 1, 25) };
        Assert.Equal(15m, Eval("DAYS(Fecha, Entrega)", v));
        Assert.Equal(true, Eval("Entrega >= Fecha", v));
        Assert.Equal(new DateTime(2026, 1, 17), Eval("ADDDAYS(Fecha, 7)", v));
    }

    [Fact]
    public void Security_functions()
    {
        Assert.Equal(true, Eval("HASROLE('comprador')"));
        Assert.Equal(false, Eval("HASROLE('Director')"));
        Assert.Equal(true, Eval("ISCREATOR()"));
        Assert.Equal(7m, Eval("CURRENTUSER()"));
    }

    [Fact]
    public void Aggregates_over_lines_and_related_fields()
    {
        var lines = new List<FakeScope>
        {
            new(new() { ["Importe"] = 100m }), new(new() { ["Importe"] = 250.5m }), new(new() { ["Importe"] = null }),
        };
        var scope = new FakeScope(new() { ["Proveedor"] = 1L },
            related: new() { ["Proveedor"] = new() { ["Activo"] = true } },
            lines: new() { ["Partidas"] = lines });
        Assert.Equal(350.5m, ExprEvaluator.Evaluate("SUM(Partidas.Importe)", scope, Env));
        Assert.Equal(3m, ExprEvaluator.Evaluate("COUNT(Partidas)", scope, Env));
        Assert.Equal(true, ExprEvaluator.Evaluate("Proveedor.Activo = TRUE", scope, Env));
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("'sin cerrar")]
    [InlineData("Campo; DROP TABLE x")]
    [InlineData("System.IO.File.Delete('x')")]
    // Spec 023: una ruta admite hasta 4 segmentos (a.b.c.d); con 5 es error de sintaxis.
    [InlineData("a.b.c.d.e")]
    [InlineData("$x")]
    public void Rejects_invalid_or_malicious_syntax(string expr) =>
        Assert.Throws<ExprException>(() => ExprParser.Parse(expr));

    [Fact]
    public void Rejects_oversized_and_deeply_nested_expressions()
    {
        Assert.Throws<ExprException>(() => ExprParser.Parse(new string('1', ExprParser.MaxLength + 1)));
        Assert.Throws<ExprException>(() => ExprParser.Parse(new string('(', 200) + "1" + new string(')', 200)));
    }

    [Fact]
    public void Unknown_functions_cannot_be_invoked() =>
        Assert.Throws<ExprException>(() => Eval("EXEC('x')"));
}

public class ExpressionAnalyzerTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Director" };

    private static readonly EntityDef Producto = new()
    {
        Name = "Producto", Label = "Producto", LabelPlural = "Productos",
        Fields = [new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Decimal }],
    };
    private static readonly EntityDef Partida = new()
    {
        Name = "Partida", Label = "Partida", LabelPlural = "Partidas",
        Fields =
        [
            new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "Orden" },
            new FieldDef { Name = "Importe", Label = "Importe", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Nota", Label = "Nota", Type = FieldTypes.Text },
        ],
    };
    private static readonly EntityDef Orden = new()
    {
        Name = "Orden", Label = "Orden", LabelPlural = "Órdenes",
        Fields =
        [
            new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto" },
            new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "Partida" },
            new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date },
        ],
    };
    private static readonly Dictionary<string, EntityDef> Model = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Producto"] = Producto, ["Partida"] = Partida, ["Orden"] = Orden,
    };

    private static IReadOnlyList<string> Errors(string expr) => ExprAnalyzer.Analyze(expr, Orden, Model, Roles).Errors;

    [Theory]
    [InlineData("SUM(Partidas.Importe) * 1.16")]
    [InlineData("COUNT(Partidas) > 0")]
    [InlineData("Producto.Costo")]
    [InlineData("Fecha >= TODAY() OR HASROLE('Director')")]
    public void Valid_expressions(string expr) => Assert.Empty(Errors(expr));

    [Theory]
    [InlineData("NoExiste + 1")]
    [InlineData("Producto.NoExiste")]
    [InlineData("SUM(Partidas.Nota)")]       // no numérico
    [InlineData("Partidas")]                  // partidas sin agregado
    [InlineData("SUM(Fecha)")]               // no son partidas
    [InlineData("HASROLE('Inventado')")]
    [InlineData("ROUND()")]
    [InlineData("FOO(1)")]
    public void Invalid_expressions_are_reported(string expr) => Assert.NotEmpty(Errors(expr));

    [Fact]
    public void Formula_cycles_are_detected()
    {
        var e = Orden with
        {
            Fields =
            [
                new FieldDef { Name = "A", Label = "A", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "B + 1" },
                new FieldDef { Name = "B", Label = "B", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "A * 2" },
            ],
        };
        Assert.Throws<ExprException>(() => FormulaGraph.Order(e));
    }

    [Fact]
    public void Formulas_are_ordered_by_dependency()
    {
        var e = Orden with
        {
            Fields =
            [
                new FieldDef { Name = "Total", Label = "Total", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "Subtotal + Iva" },
                new FieldDef { Name = "Iva", Label = "IVA", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "Subtotal * 0.16" },
                new FieldDef { Name = "Subtotal", Label = "Subtotal", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "SUM(Partidas.Importe)" },
                new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "Partida" },
            ],
        };
        Assert.Equal(["Subtotal", "Iva", "Total"], FormulaGraph.Order(e).Select(f => f.Name));
    }
}

public class WorkflowCompilerTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gerente", "Director" };

    private static CompiledPlan Compile(params Operation[] ops) =>
        PlanCompiler.Compile(new ChangePlan { Operations = ops }, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static CreateEntityOp Orden() => new()
    {
        Name = "Orden", Label = "Orden", LabelPlural = "Órdenes",
        Fields =
        [
            new FieldDef { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber, Prefix = "OC-" },
            new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date, DefaultValue = "TODAY()" },
        ],
    };

    private static Workflow Flow(string to = "Autorizada", string? when = null, string[]? roles = null) => new()
    {
        Initial = "Borrador",
        States = [new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "Autorizada", Label = "Autorizada", Color = "success", Editable = false }],
        Transitions = [new() { Name = "autorizar", Label = "Autorizar", From = ["Borrador"], To = to, Roles = roles ?? ["Gerente"], When = when }],
    };

    [Fact]
    public void Document_with_lines_formulas_and_workflow_compiles()
    {
        var result = Compile(
            Orden(),
            new CreateEntityOp { Name = "Partida", Label = "Partida", LabelPlural = "Partidas", Fields =
            [
                new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "Orden" },
                new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
                new FieldDef { Name = "Precio", Label = "Precio", Type = FieldTypes.Decimal },
                new FieldDef { Name = "Importe", Label = "Importe", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "Cantidad * Precio" },
            ] },
            new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "Partida" } },
            new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Total", Label = "Total", Type = FieldTypes.Formula, ResultType = "decimal", Formula = "SUM(Partidas.Importe)" } },
            new SetWorkflowOp { Entity = "Orden", Workflow = Flow(when: "Total <= 50000 OR HASROLE('Director')") },
            new SetRulesOp { Entity = "Orden", Rules = [new() { Name = "ConPartidas", Message = "Agrega partidas", Assert = "COUNT(Partidas) > 0", When = "Estado <> 'Borrador'" }] });

        Assert.True(result.Preview.Valid, string.Join("\n", result.Preview.Errors.Select(e => e.Message)));
        var orden = result.ResultingModel["Orden"];
        Assert.Equal(FieldTypes.State, orden.FindField("Estado")!.Type);
        Assert.Contains(result.Sql, s => s.Contains("UPDATE [app].[Orden] SET [Estado] = N'Borrador'"));
        Assert.Contains(result.Sql, s => s.Contains("CREATE UNIQUE INDEX [UX_Orden_Folio]"));
        Assert.DoesNotContain(result.Sql, s => s.Contains("[Partidas]")); // las partidas no tienen columna
        Assert.True(result.ResultingModel["Partida"].IsChild);
    }

    [Fact]
    public void Lines_without_matching_parent_field_are_rejected()
    {
        var result = Compile(Orden(),
            new CreateEntityOp { Name = "Suelta", Label = "Suelta", LabelPlural = "Sueltas", Fields = [new FieldDef { Name = "Nota", Label = "Nota", Type = FieldTypes.Text }] },
            new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "Suelta" } });
        Assert.False(result.Preview.Valid);
    }

    [Fact]
    public void Workflow_with_unknown_state_role_or_expression_is_rejected()
    {
        Assert.False(Compile(Orden(), new SetWorkflowOp { Entity = "Orden", Workflow = Flow(to: "NoExiste") }).Preview.Valid);
        Assert.False(Compile(Orden(), new SetWorkflowOp { Entity = "Orden", Workflow = Flow(roles: ["Hacker"]) }).Preview.Valid);
        Assert.False(Compile(Orden(), new SetWorkflowOp { Entity = "Orden", Workflow = Flow(when: "Inventado > 1") }).Preview.Valid);
    }

    [Fact]
    public void State_field_cannot_be_archived_and_formula_values_cannot_be_defaulted()
    {
        Assert.False(Compile(Orden(), new SetWorkflowOp { Entity = "Orden", Workflow = Flow() },
            new ArchiveFieldOp { Entity = "Orden", Field = "Estado" }).Preview.Valid);
        Assert.False(Compile(Orden(), new UpdateFieldOp { Entity = "Orden", Field = "Folio", DefaultValue = "'X'" }).Preview.Valid);
    }

    // Spec 004 · campos asignados por el sistema
    [Fact]
    public void Non_editable_required_field_needs_a_default()
    {
        var withoutDefault = Compile(Orden(), new AddFieldOp { Entity = "Orden", Field = new FieldDef
            { Name = "Solicitante", Label = "Solicitante", Type = FieldTypes.User, Required = true, Editable = false } });
        Assert.False(withoutDefault.Preview.Valid);

        var withDefault = Compile(Orden(), new AddFieldOp { Entity = "Orden", Field = new FieldDef
            { Name = "Solicitante", Label = "Solicitante", Type = FieldTypes.User, Required = true, Editable = false, DefaultValue = "CURRENTUSER()" } });
        Assert.True(withDefault.Preview.Valid);
        Assert.True(withDefault.ResultingModel["Orden"].FindField("Solicitante")!.ClientLocked);
    }

    [Fact]
    public void Update_field_can_lock_a_field_and_transitions_cannot_ask_users_to_capture_it()
    {
        var locked = Compile(Orden(), new UpdateFieldOp { Entity = "Orden", Field = "Fecha", Editable = false });
        Assert.True(locked.Preview.Valid);
        Assert.False(locked.ResultingModel["Orden"].FindField("Fecha")!.Editable);

        var bad = Compile(Orden(), new UpdateFieldOp { Entity = "Orden", Field = "Fecha", Editable = false },
            new SetWorkflowOp { Entity = "Orden", Workflow = Flow() with
            {
                Transitions = [new() { Name = "autorizar", Label = "Autorizar", From = ["Borrador"], To = "Autorizada", RequiredFields = ["Fecha"] }],
            } });
        Assert.False(bad.Preview.Valid);

        // Pero una transición sí puede asignarlo (p. ej. "Autorizado por").
        var set = Compile(Orden(), new UpdateFieldOp { Entity = "Orden", Field = "Fecha", Editable = false },
            new SetWorkflowOp { Entity = "Orden", Workflow = Flow() with
            {
                Transitions = [new() { Name = "autorizar", Label = "Autorizar", From = ["Borrador"], To = "Autorizada", Set = [new("Fecha", "TODAY()")] }],
            } });
        Assert.True(set.Preview.Valid, string.Join("; ", set.Preview.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Default_value_expressions_are_validated() =>
        Assert.False(Compile(Orden(), new AddFieldOp { Entity = "Orden", Field = new FieldDef
            { Name = "Entrega", Label = "Entrega", Type = FieldTypes.Date, DefaultValue = "ADDDAYS(NoExiste, 7)" } }).Preview.Valid);
}
