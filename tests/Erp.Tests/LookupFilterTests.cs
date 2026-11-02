using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Tests;

/// <summary>
/// Spec 052: filtro compuesto del buscador. La validación del plan (forma corta, operadores por tipo, constantes, orígenes de
/// hasta tres saltos y tipos compatibles), el SQL (solo nombres de los metadatos, valores como parámetros, <c>EXISTS</c> sobre la
/// tabla de unión, origen vacío → nada), los campos ocultos que lo vuelven inusable y la forma JSON.
/// </summary>
public class LookupFilterTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrador", "Capturista" };
    private static readonly Dictionary<string, EntityDef> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static FieldDef F(string name, string type, string? target = null) => new() { Name = name, Label = name, Type = type, ReferenceEntity = target };
    private static LookupCondition C(string field, string op, string? source = null, object? value = null) => new()
    {
        Field = field, Op = op, Source = source, Value = value is null ? null : JsonSerializer.SerializeToElement(value),
    };

    /// <summary>Categoría ← Proveedor (varias categorías) ← Orden (con partidas) ← Partida; el filtro va en el campo indicado.</summary>
    private static CompiledPlan Compile(LookupFilter? proveedor = null, LookupFilter? producto = null) => PlanCompiler.Compile(new ChangePlan
    {
        Operations =
        [
            new CreateEntityOp { Name = "Categoria", Label = "Categoría", LabelPlural = "Categorías", Fields = [F("Nombre", FieldTypes.Text)] },
            new CreateEntityOp
            {
                Name = "Proveedor", Label = "Proveedor", LabelPlural = "Proveedores", Fields =
                [
                    F("Nombre", FieldTypes.Text), F("Activo", FieldTypes.Boolean), F("Calificacion", FieldTypes.Integer),
                    F("Categorias", FieldTypes.MultiReference, "Categoria"), F("Comprador", FieldTypes.User), F("Alta", FieldTypes.DateTime),
                    new() { Name = "Tipo", Label = "Tipo", Type = FieldTypes.Choice, Options = ["Nacional", "Extranjero"] },
                ],
            },
            new CreateEntityOp
            {
                Name = "Producto", Label = "Producto", LabelPlural = "Productos", Fields =
                    [F("Nombre", FieldTypes.Text), F("Activo", FieldTypes.Boolean), F("Categoria", FieldTypes.Reference, "Categoria")],
            },
            new CreateEntityOp
            {
                Name = "Orden", Label = "Orden", LabelPlural = "Órdenes", Fields =
                [
                    F("Nota", FieldTypes.Text), F("Nivel", FieldTypes.Integer), F("Fecha", FieldTypes.Date), F("Categoria", FieldTypes.Reference, "Categoria"),
                    F("Categorias", FieldTypes.MultiReference, "Categoria"),
                    F("Proveedor", FieldTypes.Reference, "Proveedor") with { LookupFilter = proveedor },
                ],
            },
            new CreateEntityOp
            {
                Name = "Partida", Label = "Partida", LabelPlural = "Partidas", Fields =
                [
                    F("Orden", FieldTypes.Parent, "Orden"), F("Cantidad", FieldTypes.Decimal),
                    F("Producto", FieldTypes.Reference, "Producto") with { LookupFilter = producto },
                ],
            },
            new AddFieldOp { Entity = "Orden", Field = new() { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "Partida" } },
        ],
    }, Empty, Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    [Fact]
    public void La_forma_corta_de_la_005_es_una_condicion_eq_y_sigue_valiendo()
    {
        Assert.Empty(Errors(Compile(producto: new LookupFilter("Categoria", "Orden.Categoria"))));
        var c = Assert.Single(LookupFilters.Of(new LookupFilter("Categoria", "Orden.Categoria")));
        Assert.Equal(("Categoria", "eq", "Orden.Categoria"), (c.Field, c.Op, c.Source));
        // Corta y condiciones juntas: la corta primero.
        var both = LookupFilters.Of(new LookupFilter("Categoria", "Orden.Categoria") { Conditions = [C("Activo", "eq", value: true)] });
        Assert.Equal(["Categoria", "Activo"], both.Select(x => x.Field));
        Assert.True(LookupFilters.IsEmpty(new LookupFilter()));
        Assert.True(LookupFilters.IsEmpty(new LookupFilter("", "") { Conditions = [] }));
    }

    [Fact]
    public void Constantes_contiene_y_es_uno_de_validos()
    {
        var p = Compile(
            proveedor: new LookupFilter
            {
                Conditions =
                [
                    C("Activo", "eq", value: true), C("Categorias", "contains", source: "Categoria"), C("Categorias", "in", source: "Categorias"),
                    C("Tipo", "in", value: new[] { "Nacional" }), C("Calificacion", "gte", source: "Nivel"), C("Comprador", "eq", value: "$me"),
                    C("Categorias", "contains", value: 7), C("Nombre", "notBlank"),
                ],
            },
            producto: new LookupFilter { Conditions = [C("Activo", "eq", value: true), C("Categoria", "in", source: "Orden.Proveedor.Categorias")] });
        Assert.Empty(Errors(p));
    }

    [Theory]
    [InlineData("NoExiste", "eq", null, true, "no es un campo de Proveedor")]
    [InlineData("Activo", "gt", null, true, "el operador 'gt' no aplica")]
    [InlineData("Activo", "eq", null, "sí", "debe ser true o false")]
    [InlineData("Tipo", "eq", null, "Marciano", "no es una opción")]
    [InlineData("Activo", "eq", "Nivel", true, "uno de los dos")]
    [InlineData("Activo", "eq", null, null, "uno de los dos")]
    [InlineData("Nombre", "blank", "Nota", null, "no lleva valor ni origen")]
    [InlineData("Calificacion", "eq", "Nota", null, "no se compara")]
    [InlineData("Categorias", "contains", "Nivel", null, "no se compara")]
    [InlineData("Categorias", "eq", "Categoria", null, "el operador 'eq' no aplica")]
    [InlineData("Calificacion", "eq", "Proveedor.Comprador.Nombre", null, "no es una referencia")]
    [InlineData("Calificacion", "eq", "NoExiste", null, "no es un campo de Orden")]
    [InlineData("Calificacion", "eq", "Partidas", null, "no tiene valor que comparar")]
    [InlineData("Alta", "gte", "Fecha", null, "no se compara")] // revisión adversarial 052 (F-07): fecha con fecha y hora
    public void Errores_del_plan_con_mensaje_para_el_modelador(string field, string op, string? source, object? value, string expected)
    {
        var p = Compile(proveedor: new LookupFilter { Conditions = [C(field, op, source, value)] });
        Assert.Contains("Orden.Proveedor (filtro del buscador)", Errors(p));
        Assert.Contains(expected, Errors(p));
    }

    [Fact]
    public void Mas_de_diez_condiciones_o_cuatro_saltos_es_error_del_plan()
    {
        var many = Compile(proveedor: new LookupFilter { Conditions = Enumerable.Range(0, 11).Select(_ => C("Activo", "eq", value: true)).ToList() });
        Assert.Contains("máximo 10 condiciones", Errors(many));
        var deep = Compile(producto: new LookupFilter { Conditions = [C("Categoria", "eq", source: "Orden.Proveedor.Comprador.Id")] });
        Assert.Contains("hasta tres saltos", Errors(deep));
        var updated = Compile(producto: new LookupFilter { Conditions = [C("Categoria", "in", source: "Orden.Proveedor.Categorias")] });
        Assert.Empty(Errors(updated));
    }

    [Fact]
    public void Una_partida_empieza_el_origen_en_su_campo_padre()
    {
        Assert.Empty(Errors(Compile(producto: new LookupFilter { Conditions = [C("Categoria", "eq", source: "Orden.Categoria")] })));
        Assert.Contains("no es un campo de Partida", Errors(Compile(producto: new LookupFilter { Conditions = [C("Categoria", "eq", source: "Categoria")] })));
    }

    private static (EntityDef Orden, EntityDef Proveedor, IReadOnlyDictionary<string, EntityDef> Model) Model(LookupFilter lf)
    {
        var m = Compile(proveedor: lf).ResultingModel;
        return (m["Orden"], m["Proveedor"], m);
    }

    private static RelatedLoader Loader(IReadOnlyDictionary<string, EntityDef> m) => new(new SqlConnection(), null, m);

    [Fact]
    public void El_SQL_usa_solo_nombres_de_los_metadatos_y_valores_como_parametros()
    {
        var lf = new LookupFilter
        {
            Conditions =
            [
                C("Activo", "eq", value: true), C("Categorias", "contains", source: "Categoria"), C("Nombre", "contains", source: "Nota"),
                C("Categorias", "in", source: "Categorias"),
            ],
        };
        var (orden, proveedor, m) = Model(lf);
        var own = new Values { ["Categoria"] = 5L, ["Nota"] = "x'; DROP TABLE meta.[User]; --", ["Categorias"] = new List<long> { 5, 6 } };
        using var cmd = new SqlCommand();
        var sql = LookupFilters.Build(orden, proveedor, lf, own, null, m, Loader(m), 1, cmd.Parameters)!;
        Assert.Equal("t.[Activo] = @lf0 AND EXISTS (SELECT 1 FROM [app].[Proveedor__Categorias] j WHERE j.[OwnerId] = t.[Id] AND j.[ValueId] = @lf1)"
            + " AND t.[Nombre] LIKE @lf2 ESCAPE '\\' AND EXISTS (SELECT 1 FROM [app].[Proveedor__Categorias] j WHERE j.[OwnerId] = t.[Id] AND j.[ValueId] IN (@lf3, @lf4))", sql);
        Assert.DoesNotContain("DROP", sql);
        Assert.Equal([true, 5L, "%x'; DROP TABLE meta.\\[User]; --%", 5L, 6L], cmd.Parameters.Cast<SqlParameter>().Select(p => p.Value));
    }

    [Fact]
    public void Origen_vacio_no_ofrece_nada()
    {
        var lf = new LookupFilter { Conditions = [C("Activo", "eq", value: true), C("Categorias", "contains", source: "Categoria")] };
        var (orden, proveedor, m) = Model(lf);
        using var cmd = new SqlCommand();
        Assert.Null(LookupFilters.Build(orden, proveedor, lf, new Values(), null, m, Loader(m), 1, cmd.Parameters));
        var list = new LookupFilter { Conditions = [C("Categorias", "in", source: "Categorias")] };
        (orden, proveedor, m) = Model(list);
        Assert.Null(LookupFilters.Build(orden, proveedor, list, new Values { ["Categorias"] = new List<long>() }, null, m, Loader(m), 1, cmd.Parameters));
        // Sin condiciones con origen, no hace falta nada del borrador.
        var constant = new LookupFilter { Conditions = [C("Activo", "eq", value: true)] };
        (orden, proveedor, m) = Model(constant);
        Assert.Equal("t.[Activo] = @lf0", LookupFilters.Build(orden, proveedor, constant, new Values(), null, m, Loader(m), 1, cmd.Parameters));
    }

    [Fact]
    public void Me_es_quien_captura()
    {
        var lf = new LookupFilter { Conditions = [C("Comprador", "eq", value: "$me")] };
        var (orden, proveedor, m) = Model(lf);
        using var cmd = new SqlCommand();
        Assert.Equal("t.[Comprador] = @lf0", LookupFilters.Build(orden, proveedor, lf, new Values(), null, m, Loader(m), 42, cmd.Parameters));
        Assert.Equal(42, Convert.ToInt32(cmd.Parameters[0].Value));
    }

    [Fact]
    public void Un_origen_o_destino_oculto_vuelve_inusable_el_filtro_y_una_constante_no()
    {
        var capturista = new UserContext { Id = 9, Username = "c", DisplayName = "C", SecurityStamp = Guid.Empty, Roles = ["Capturista"] };
        var hidden = new FieldSecurity { Field = "Calificacion", Read = ["Administrador"] };
        IReadOnlyDictionary<string, EntityDef> With(LookupFilter lf, FieldSecurity rule, string entity)
        {
            var m = new Dictionary<string, EntityDef>(Compile(proveedor: lf).ResultingModel, StringComparer.OrdinalIgnoreCase);
            m[entity] = m[entity] with { Security = new SecurityPolicy { Fields = [rule] } };
            return m;
        }
        var bySource = new LookupFilter { Conditions = [C("Calificacion", "eq", source: "Nivel")] };
        var m1 = With(bySource, hidden, "Proveedor");
        Assert.True(LookupFilters.HiddenFor(m1["Orden"], m1["Proveedor"], bySource, m1, capturista));
        var byConstant = new LookupFilter { Conditions = [C("Calificacion", "gte", value: 3)] };
        var m2 = With(byConstant, hidden, "Proveedor");
        Assert.False(LookupFilters.HiddenFor(m2["Orden"], m2["Proveedor"], byConstant, m2, capturista));
        // El origen propio oculto también: el filtro diría su valor.
        var m3 = With(bySource, new FieldSecurity { Field = "Nivel", Read = ["Administrador"] }, "Orden");
        Assert.True(LookupFilters.HiddenFor(m3["Orden"], m3["Proveedor"], bySource, m3, capturista));
    }

    [Fact]
    public void La_forma_JSON_no_escribe_lo_que_no_tiene_y_se_lee_igual()
    {
        var lf = new LookupFilter { Conditions = [C("Activo", "eq", value: true), C("Categorias", "contains", source: "Categoria")] };
        var json = JsonSerializer.Serialize(lf, MetadataStore.Json);
        Assert.Equal("""{"field":null,"source":null,"conditions":[{"field":"Activo","op":"eq","value":true},{"field":"Categorias","op":"contains","source":"Categoria"}]}""", json);
        var back = JsonSerializer.Deserialize<LookupFilter>(json, MetadataStore.Json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, MetadataStore.Json));
        var old = JsonSerializer.Deserialize<LookupFilter>("""{"field":"Orden","source":"Recepcion.Orden"}""", MetadataStore.Json)!;
        Assert.Equal(("Orden", "Recepcion.Orden", null), (old.Field, old.Source, old.Conditions));
    }
}
