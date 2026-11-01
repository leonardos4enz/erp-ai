using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 041 · Reportes personales: la definición se valida contra quien la corre. Lo que no ve responde igual que lo que no
/// existe (catálogo, campo, salto, filtro); los campos que se ofrecen siguen la misma regla; los topes de la 039/040 valen.
/// </summary>
public class PersonalReportsTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendedor", "Gerente" };

    private static IReadOnlyDictionary<string, EntityDef> Model()
    {
        var plan = new ChangePlan
        {
            Operations =
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
                ] },
                new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields =
                [
                    new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date },
                    new FieldDef { Name = "Cliente", Label = "Cliente", Type = FieldTypes.Text },
                    new FieldDef { Name = "Margen", Label = "Margen", Type = FieldTypes.Decimal },
                ] },
                new CreateEntityOp { Name = "PedidoPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
                [
                    new FieldDef { Name = "Pedido", Label = "Pedido", Type = FieldTypes.Parent, ReferenceEntity = "Pedido" },
                    new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Producto" },
                    new FieldDef { Name = "Importe", Label = "Importe", Type = FieldTypes.Decimal },
                ] },
                new AddFieldOp { Entity = "Pedido", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "PedidoPartida" } },
                // Producto.Categoria y Pedido.Margen, solo para Gerente.
                new SetSecurityOp { Entity = "Producto", Security = new() { Fields = [new() { Field = "Categoria", Read = ["Gerente"] }] } },
                new SetSecurityOp { Entity = "Pedido", Security = new() { Fields = [new() { Field = "Margen", Read = ["Gerente"] }] } },
            ],
        };
        var r = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        Assert.True(r.Preview.Valid, string.Join(" | ", r.Preview.Errors.Select(e => e.Message)));
        return r.ResultingModel;
    }

    private static UserContext User(string role, params string[] reads) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = [role],
        Entities = reads.ToDictionary(x => x, _ => new EntityAccess(true, false, false, false), StringComparer.OrdinalIgnoreCase),
    };

    private static readonly UserContext Vendedor = User("Vendedor", "Pedido", "Producto", "Categoria");
    private static readonly UserContext GerenteSinCategorias = User("Gerente", "Pedido", "Producto");

    private static PersonalReportDef Def(string entity, params string[] groups) => new()
    {
        Entity = entity,
        GroupBy = groups.Select(g => new ReportGroup { Field = g, Period = g.EndsWith("Fecha") ? "month" : null }).ToList(),
        Measures = [new() { Fn = "sum", Field = "Importe", Name = "Ventas" }, new() { Fn = "count" }],
    };

    private static string[] Messages(Action check)
    {
        var ex = Assert.ThrowsAny<AppException>(check);
        return ex.Errors?.SelectMany(x => x.Value).ToArray() ?? [ex.Message];
    }

    [Fact]
    public void Definicion_valida_da_el_reporte_listo_para_correr()
    {
        var m = Model();
        var d = Def("PedidoPartida", "Pedido.Fecha", "Producto.Nombre") with { Chart = "column", Filters = [new() { Field = "Importe", Op = "gt", Value = JsonSerializer.SerializeToElement(10) }] };
        var (e, r) = PersonalReports.Check(d, "Mis ventas", Vendedor, m);
        Assert.Equal("PedidoPartida", e.Name);
        Assert.Equal("Mis ventas", r.Label);
        Assert.Null(r.Roles);
        Assert.Null(r.Filter);
        Assert.Equal(2, r.GroupBy!.Count);
        Assert.Equal(PersonalReports.DefaultLabel, PersonalReports.Check(d, null, Vendedor, m).Report.Label);
    }

    [Fact]
    public void Check_oculto_igual_que_inexistente()
    {
        var m = Model();
        // Un salto oculto (Producto.Categoria es de Gerente), un campo oculto y uno que no existe: el mismo texto.
        var hidden = Messages(() => PersonalReports.Check(Def("PedidoPartida", "Producto.Categoria.Nombre"), null, Vendedor, m));
        var hiddenField = Messages(() => PersonalReports.Check(Def("PedidoPartida", "Pedido.Margen"), null, Vendedor, m));
        var missing = Messages(() => PersonalReports.Check(Def("PedidoPartida", "Producto.Nada"), null, Vendedor, m));
        Assert.Equal(["'Producto.Categoria.Nombre' no existe o no puedes verlo."], hidden);
        Assert.Equal(["'Pedido.Margen' no existe o no puedes verlo."], hiddenField);
        Assert.Equal(["'Producto.Nada' no existe o no puedes verlo."], missing);
        // También en una medida.
        var measure = Def("PedidoPartida", "Producto") with { Measures = [new() { Fn = "sum", Field = "Pedido.Margen" }] };
        Assert.Equal(["'Pedido.Margen' no existe o no puedes verlo."], Messages(() => PersonalReports.Check(measure, null, Vendedor, m)));
        // El Gerente sí lo ve.
        PersonalReports.Check(Def("PedidoPartida", "Pedido.Margen"), null, User("Gerente", "Pedido", "Producto", "Categoria"), m);
    }

    [Fact]
    public void Catalogo_que_no_lee_responde_como_inexistente()
    {
        var m = Model();
        var sinPedidos = User("Vendedor", "Producto");
        // Las partidas se leen con su documento: sin leer Pedido no hay partidas.
        var a = Assert.Throws<NotFound>(() => PersonalReports.Check(Def("PedidoPartida", "Producto"), null, sinPedidos, m));
        var b = Assert.Throws<NotFound>(() => PersonalReports.Check(Def("NoExiste", "Producto"), null, sinPedidos, m));
        var c = Assert.Throws<NotFound>(() => PersonalReports.Check(Def("Pedido", "Cliente"), null, sinPedidos, m));
        Assert.Equal(a.Message, b.Message);
        Assert.Equal(a.Message, c.Message);
        Assert.Throws<NotFound>(() => PersonalReports.Fields("PedidoPartida", sinPedidos, m));
        Assert.DoesNotContain(PersonalReports.Sources(sinPedidos, m), s => s.Entity is "Pedido" or "PedidoPartida");
    }

    [Fact]
    public void Salto_por_un_catalogo_que_no_lee_solo_llega_a_su_campo_para_mostrar()
    {
        var m = Model();
        // El Gerente ve Producto.Categoria pero no lee Categoría: su nombre sí (lo da el buscador), su costo no.
        PersonalReports.Check(Def("PedidoPartida", "Producto.Categoria.Nombre"), null, GerenteSinCategorias, m);
        var cost = Def("PedidoPartida", "Producto") with { Measures = [new() { Fn = "sum", Field = "Producto.Categoria.Costo" }] };
        Assert.Equal(["'Producto.Categoria.Costo' no existe o no puedes verlo."], Messages(() => PersonalReports.Check(cost, null, GerenteSinCategorias, m)));
    }

    [Fact]
    public void Filtro_por_un_campo_oculto_igual_que_inexistente()
    {
        var m = Model();
        var v = JsonSerializer.SerializeToElement(1);
        var hidden = Def("Pedido", "Cliente") with { Measures = [new() { Fn = "count" }], Filters = [new() { Field = "Margen", Op = "gt", Value = v }] };
        var missing = hidden with { Filters = [new() { Field = "Nada", Op = "gt", Value = v }] };
        Assert.Equal(["El filtro usa 'Margen', que no existe o no puedes ver."], Messages(() => PersonalReports.Check(hidden, null, Vendedor, m)));
        Assert.Equal(["El filtro usa 'Nada', que no existe o no puedes ver."], Messages(() => PersonalReports.Check(missing, null, Vendedor, m)));
        // Un operador que no aplica sí se explica (el campo se ve).
        var badOp = hidden with { Filters = [new() { Field = "Cliente", Op = "gt", Value = v }] };
        Assert.Contains(Messages(() => PersonalReports.Check(badOp, null, Vendedor, m)), x => x.Contains("no aplica al campo 'Cliente'"));
    }

    [Fact]
    public void Lo_que_el_modelo_rechaza_tambien_se_rechaza_sin_el_prefijo_del_reporte()
    {
        var m = Model();
        var pie = Def("PedidoPartida", "Producto", "Pedido.Fecha") with { Chart = "pie" };
        Assert.Contains("Un pastel o una dona necesita exactamente una agrupación.", Messages(() => PersonalReports.Check(pie, null, Vendedor, m)));
        var calc = Def("PedidoPartida", "Producto") with { Measures = [new() { Fn = "sum", Field = "Importe", Name = "Ventas" }, new() { Fn = "calc", Expr = "Ventas / Nada" }] };
        Assert.Contains(Messages(() => PersonalReports.Check(calc, null, Vendedor, m)), x => x.Contains("'Nada' no es el nombre de una medida"));
        Assert.Contains(Messages(() => PersonalReports.Check(Def("PedidoPartida", "Producto") with { Limit = 5000 }, null, Vendedor, m)),
            x => x.StartsWith("«limit» va de 1 a 1000", StringComparison.Ordinal));
        Assert.Contains(Messages(() => PersonalReports.Check(Def("PedidoPartida") with { Measures = [] }, null, Vendedor, m)), x => x.Contains("Indica de 1 a 6 medidas"));
        Assert.Contains(Messages(() => PersonalReports.Check(Def("PedidoPartida", "Producto"), new string('x', 101), Vendedor, m)), x => x.Contains("excede 100"));
        Assert.Equal(["Falta la definición del reporte."], Messages(() => PersonalReports.Check(null, null, Vendedor, m)));
    }

    [Fact]
    public void Check_con_elementos_nulos_da_400()
    {
        // Regresión (revisión adversarial 041, F-E): `groupBy: [null]` daba NullReferenceException (500).
        var m = Model();
        foreach (var d in new[] {
            Def("PedidoPartida") with { GroupBy = [null!] },
            Def("PedidoPartida") with { Measures = [null!] },
            Def("PedidoPartida") with { Filters = [null!] } })
            Assert.Equal(["Hay una agrupación, una medida o un filtro vacío."], Messages(() => PersonalReports.Check(d, null, Vendedor, m)));
    }

    [Fact]
    public void Check_rechaza_mas_de_16_uniones()
    {
        // Ocho referencias a ocho catálogos que llegan a Hoja, y Hoja a Tipo: 2 agrupaciones por una referencia a dos saltos
        // (R7.Hoja.Tipo: 3 uniones cada una) y 6 medidas a dos saltos (2 cada una) = 18 > 16.
        var ops = new List<Operation>
        {
            new CreateEntityOp { Name = "Tipo", Label = "Tipo", LabelPlural = "Tipos", DisplayField = "Nombre", Fields = [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }] },
            new CreateEntityOp { Name = "Hoja", Label = "Hoja", LabelPlural = "Hojas", DisplayField = "Nombre", Fields =
                [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }, new FieldDef { Name = "Valor", Label = "Valor", Type = FieldTypes.Decimal },
                 new FieldDef { Name = "Tipo", Label = "Tipo", Type = FieldTypes.Reference, ReferenceEntity = "Tipo" }] },
        };
        for (var i = 1; i <= 8; i++)
            ops.Add(new CreateEntityOp { Name = $"Medio{i}", Label = $"Medio {i}", LabelPlural = $"Medios {i}", DisplayField = "Nombre", Fields =
                [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }, new FieldDef { Name = "Hoja", Label = "Hoja", Type = FieldTypes.Reference, ReferenceEntity = "Hoja" }] });
        ops.Add(new CreateEntityOp { Name = "Centro", Label = "Centro", LabelPlural = "Centros", Fields =
            [.. Enumerable.Range(1, 8).Select(i => new FieldDef { Name = $"R{i}", Label = $"R{i}", Type = FieldTypes.Reference, ReferenceEntity = $"Medio{i}" })] });
        var compiled = PlanCompiler.Compile(new ChangePlan { Operations = ops }, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        Assert.True(compiled.Preview.Valid, string.Join(" | ", compiled.Preview.Errors.Select(e => e.Message)));
        var su = new UserContext { Id = 1, Username = "su", DisplayName = "SU", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };
        var d = new PersonalReportDef
        {
            Entity = "Centro",
            GroupBy = [new() { Field = "R7.Hoja.Tipo" }, new() { Field = "R8.Hoja.Tipo" }],
            Measures = [.. Enumerable.Range(1, 6).Select(i => new ReportMeasure { Fn = "sum", Field = $"R{i}.Hoja.Valor" })],
        };
        Assert.Contains(Messages(() => PersonalReports.Check(d, null, su, compiled.ResultingModel)), x => x.Contains("el máximo es 16"));
        // Con una medida menos (16 uniones) pasa.
        PersonalReports.Check(d with { Measures = d.Measures!.Take(5).ToList() }, null, su, compiled.ResultingModel);
    }

    [Fact]
    public void Definicion_demasiado_grande_se_rechaza()
    {
        var m = Model();
        var big = JsonSerializer.SerializeToElement(Enumerable.Range(0, 50).Select(i => new string('x', 30) + i).ToArray());
        var d = Def("Pedido", "Cliente") with { Measures = [new() { Fn = "count" }], Filters = [.. Enumerable.Range(0, 20).Select(_ => new ViewFilter { Field = "Cliente", Op = "in", Value = big })] };
        Assert.Equal([$"La definición excede {PersonalReports.MaxDefinition} caracteres."], Messages(() => PersonalReports.Check(d, null, Vendedor, m)));
    }

    [Fact]
    public void Campos_que_se_ofrecen_son_los_que_puede_usar()
    {
        var m = Model();
        var v = PersonalReports.Fields("PedidoPartida", Vendedor, m).Fields.Select(f => f.Path).ToList();
        Assert.Contains("Importe", v);
        Assert.Contains("Producto", v);
        Assert.Contains("Producto.Nombre", v);
        Assert.Contains("Pedido.Fecha", v);
        Assert.DoesNotContain("Pedido", v); // el documento es un salto, no un campo para agrupar
        Assert.DoesNotContain(v, p => p.StartsWith("Producto.Categoria", StringComparison.Ordinal));
        Assert.DoesNotContain("Pedido.Margen", v);
        Assert.All(v, p => Assert.True(PersonalReports.CanUse(m["PedidoPartida"], p, Vendedor, m), p));
        // Sin leer Categoría no se baja por ella (el Gerente agrupa por Producto.Categoria, que ya muestra su nombre).
        var g = PersonalReports.Fields("PedidoPartida", GerenteSinCategorias, m).Fields.Select(f => f.Path).ToList();
        Assert.Contains("Producto.Categoria", g);
        Assert.Contains("Pedido.Margen", g);
        Assert.DoesNotContain("Producto.Categoria.Costo", g);
        var kinds = PersonalReports.Fields("PedidoPartida", Vendedor, m).Fields.ToDictionary(f => f.Path, f => f.Kind);
        Assert.Equal("number", kinds["Importe"]);
        Assert.Equal("date", kinds["Pedido.Fecha"]);
        Assert.Equal("id", kinds["Producto"]);
    }

    [Fact]
    public void Fuentes_son_los_catalogos_que_lee_y_sus_partidas()
    {
        var m = Model();
        var s = PersonalReports.Sources(User("Vendedor", "Pedido"), m);
        Assert.Equal(["Pedido", "PedidoPartida"], s.Select(x => x.Entity).Order().ToArray());
        Assert.Equal("Pedido", s.Single(x => x.Entity == "PedidoPartida").Parent);
    }

    [Theory]
    [InlineData("GET", "/api/my-reports")]
    [InlineData("GET", "/api/my-reports/1")]
    [InlineData("GET", "/api/my-reports/sources/Pedido")]
    [InlineData("POST", "/api/my-reports/explore")]
    [InlineData("POST", "/api/my-reports/explore/export")]
    [InlineData("PUT", "/api/my-reports/1")]
    public void Credenciales_no_llegan_a_mis_reportes(string method, string path)
    {
        // Solo con sesión: ni una clave de API, ni un token OAuth con todos sus alcances, ni un token MCP.
        var all = $"{Erp.Api.Integrations.OAuthScopes.DataRead} {Erp.Api.Integrations.OAuthScopes.DataWrite} {Erp.Api.Integrations.OAuthScopes.ODataRead}";
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Integrations.ApiKeyHandler.SchemeName), path, method));
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Integrations.OAuthTokenHandler.SchemeName, all), path, method));
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Mcp.McpTokenHandler.SchemeName), path, method));
        Assert.Null(BearerScope.Check(null, path, method));
    }

    [Fact]
    public void Explorar_un_reporte_del_modelo_no_copia_roles_ni_filtro()
    {
        var m = Model();
        var r = new ReportDef { Name = "X", Label = "X", Roles = ["Gerente"], Filter = "Importe > 0", GroupBy = [new() { Field = "Producto" }],
            Measures = [new() { Fn = "count" }], Chart = "bar", Limit = 10 };
        var d = PersonalReports.FromReport(m["PedidoPartida"], r);
        Assert.Equal("PedidoPartida", d.Entity);
        Assert.Equal("bar", d.Chart);
        Assert.Equal(10, d.Limit);
        Assert.Null(d.Filters);
        var back = PersonalReports.ToReport(d, "Y");
        Assert.Null(back.Roles);
        Assert.Null(back.Filter);
    }
}
