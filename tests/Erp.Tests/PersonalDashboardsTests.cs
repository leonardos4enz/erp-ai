using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Extensions.Configuration;

namespace Erp.Tests;

/// <summary>
/// Spec 042 · Tableros personales: las tarjetas se validan contra quien las ve (fuente única, reporte que ve, tipo compatible,
/// límites), las corridas costosas a la vez tienen tope por persona y ninguna credencial llega a lo personal.
/// </summary>
public class PersonalDashboardsTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendedor", "Gerente" };

    private static IReadOnlyDictionary<string, EntityDef> Model()
    {
        var plan = new ChangePlan
        {
            Operations =
            [
                new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields =
                [
                    new FieldDef { Name = "Fecha", Label = "Fecha", Type = FieldTypes.Date },
                    new FieldDef { Name = "Zona", Label = "Zona", Type = FieldTypes.Text },
                    new FieldDef { Name = "Monto", Label = "Monto", Type = FieldTypes.Decimal },
                ] },
                new SetReportsOp { Entity = "Pedido", Reports =
                [
                    new ReportDef { Name = "Total", Label = "Total", Measures = [new() { Fn = "sum", Field = "Monto" }] },
                    new ReportDef { Name = "PorZona", Label = "Por zona", GroupBy = [new() { Field = "Zona" }], Measures = [new() { Fn = "count" }] },
                    new ReportDef { Name = "PorMes", Label = "Por mes", GroupBy = [new() { Field = "Fecha", Period = "month" }], Measures = [new() { Fn = "count" }] },
                    new ReportDef { Name = "Gerencia", Label = "Gerencia", Roles = ["Gerente"], Measures = [new() { Fn = "count" }] },
                ] },
            ],
        };
        var r = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        Assert.True(r.Preview.Valid, string.Join(" | ", r.Preview.Errors.Select(e => e.Message)));
        return r.ResultingModel;
    }

    private static UserContext User(int id, string role) => new()
    {
        Id = id, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = [role],
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase) { ["Pedido"] = new(true, false, false, false) },
    };

    private static readonly IReadOnlyDictionary<int, CardReport> NoReports = new Dictionary<int, CardReport>();

    private static List<string> Validate(params PersonalWidget?[] widgets) =>
        PersonalDashboards.Validate(widgets, User(1, "Vendedor"), Model(), NoReports);

    [Fact]
    public void Tarjetas_validas_del_modelo()
    {
        Assert.Empty(Validate(
            new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Total" },
            new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "PorMes" }, // por fecha con periodo: se compara
            new PersonalWidget { Type = "chart", Entity = "pedido", Report = "porzona" },
            new PersonalWidget { Type = "table", Entity = "Pedido", Report = "Total" }));
    }

    [Fact]
    public void Tarjetas_fuera_de_limite_se_rechazan()
    {
        Assert.Contains("El tablero necesita de 1 a 20 tarjetas.", Validate());
        Assert.Contains("El tablero necesita de 1 a 20 tarjetas.",
            Validate(Enumerable.Range(0, 21).Select(_ => new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Total" }).ToArray()));
        Assert.Equal(["Tarjeta 1: está vacía."], Validate((PersonalWidget?)null));
        Assert.Equal(["Tarjeta 1: el tipo 'mapa' no existe (kpi, chart, table)."], Validate(new PersonalWidget { Type = "mapa", Entity = "Pedido", Report = "Total" }));
        // Una sola fuente: ni las dos ni ninguna, ni un catálogo sin reporte.
        Assert.Equal(["Tarjeta 1: elige un reporte (del modelo o uno tuyo)."],
            Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Total", PersonalReport = 3 }));
        Assert.Equal(["Tarjeta 1: elige un reporte (del modelo o uno tuyo)."], Validate(new PersonalWidget { Type = "kpi" }));
        Assert.Equal(["Tarjeta 1: elige un reporte (del modelo o uno tuyo)."], Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido" }));
        Assert.Contains("Tarjeta 1: el título excede 100 caracteres.",
            Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Total", Label = new string('x', 101) }));
    }

    [Fact]
    public void Tarjeta_con_tipo_que_no_va_con_el_reporte()
    {
        Assert.Equal(["Tarjeta 1: una gráfica usa un reporte con agrupación."], Validate(new PersonalWidget { Type = "chart", Entity = "Pedido", Report = "Total" }));
        Assert.Equal(["Tarjeta 1: un indicador usa un reporte sin agrupación o agrupado solo por una fecha con periodo."],
            Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "PorZona" }));
    }

    [Fact]
    public void Reporte_que_no_ve_responde_igual_que_uno_que_no_existe()
    {
        var hidden = Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Gerencia" });
        var missing = Validate(new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "NoExiste" });
        var catalog = Validate(new PersonalWidget { Type = "kpi", Entity = "Secreto", Report = "Total" });
        Assert.Equal(["Tarjeta 1: el reporte no existe o no puedes verlo."], hidden);
        Assert.Equal(hidden, missing);
        Assert.Equal(hidden, catalog);
        // Un reporte personal que no es del dueño del tablero (no está entre los suyos) tampoco corre.
        Assert.Equal(["Tarjeta 1: el reporte personal ya no existe."], Validate(new PersonalWidget { Type = "kpi", PersonalReport = 99 }));
        // El gerente sí ve el suyo.
        Assert.Empty(PersonalDashboards.Validate([new PersonalWidget { Type = "kpi", Entity = "Pedido", Report = "Gerencia" }],
            User(1, "Gerente"), Model(), NoReports));
    }

    [Fact]
    public void Tarjeta_personal_corre_con_quien_la_ve()
    {
        var reports = new Dictionary<int, CardReport>
        {
            [5] = new(5, "Mis zonas", new PersonalReportDef { Entity = "Pedido", GroupBy = [new() { Field = "Zona" }], Measures = [new() { Fn = "count" }] }),
        };
        var problems = new List<string>();
        var ok = PersonalDashboards.Resolve(new PersonalWidget { Type = "chart", PersonalReport = 5 }, User(1, "Vendedor"), Model(), reports, problems);
        Assert.NotNull(ok);
        Assert.Equal("Mis zonas", ok.Value.R.Label);
        // Quien no lee el catálogo no lo corre (y no se le dice cuál es).
        var sinPedidos = new UserContext { Id = 2, Username = "x", DisplayName = "X", SecurityStamp = Guid.Empty, Roles = ["Vendedor"] };
        Assert.Null(PersonalDashboards.Resolve(new PersonalWidget { Type = "chart", PersonalReport = 5 }, sinPedidos, Model(), reports, problems));
        Assert.Equal(["El catálogo del reporte ya no existe o ya no lo puedes consultar."], problems);
    }

    [Fact]
    public void CostlyGate_tope_por_persona_y_libera()
    {
        var gate = new CostlyGate(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:CostlyConcurrentPerUser"] = "2" }).Build());
        var ana = User(1, "Vendedor");
        var beto = User(2, "Vendedor");
        var a1 = gate.Enter(ana);
        var a2 = gate.Enter(ana);
        var ex = Assert.Throws<AppException>(() => gate.Enter(ana));
        Assert.Equal(429, ex.Status);
        Assert.Equal(2, gate.Running(ana.Id)); // el rechazo no cuenta
        using (gate.Enter(beto)) Assert.Equal(1, gate.Running(beto.Id)); // por persona
        Assert.Equal(0, gate.Running(beto.Id));
        a1.Dispose();
        a1.Dispose(); // soltar dos veces no libera de más
        Assert.Equal(1, gate.Running(ana.Id));
        using (gate.Enter(ana)) Assert.Equal(2, gate.Running(ana.Id));
        a2.Dispose();
        Assert.Equal(0, gate.Running(ana.Id));
        // Por omisión, 4.
        Assert.Equal(4, new CostlyGate(new ConfigurationBuilder().Build()).Limit);
    }

    [Fact]
    public async Task CostlyGate_aguanta_muchas_entradas_a_la_vez()
    {
        var gate = new CostlyGate(new ConfigurationBuilder().Build());
        var ana = User(1, "Vendedor");
        var admitted = 0;
        var max = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            try
            {
                using var lease = gate.Enter(ana);
                var now = Interlocked.Increment(ref admitted);
                InterlockedMax(ref max, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref admitted);
            }
            catch (AppException ex) when (ex.Status == 429) { }
        })));
        Assert.True(max <= gate.Limit, $"hubo {max} corridas a la vez con tope {gate.Limit}");
        Assert.Equal(0, gate.Running(ana.Id));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Theory]
    [InlineData("GET", "/api/my-dashboards")]
    [InlineData("GET", "/api/my-dashboards/1")]
    [InlineData("POST", "/api/my-dashboards")]
    [InlineData("PUT", "/api/my-dashboards/1")]
    [InlineData("PUT", "/api/my-dashboards/1/share")]
    [InlineData("DELETE", "/api/my-dashboards/1")]
    [InlineData("GET", "/api/my-reports/shared")]
    [InlineData("PUT", "/api/my-reports/1/share")]
    public void Credenciales_no_llegan_a_mis_tableros(string method, string path)
    {
        // Solo con sesión: ni una clave de API, ni un token OAuth con todos sus alcances, ni un token MCP.
        var all = $"{Erp.Api.Integrations.OAuthScopes.DataRead} {Erp.Api.Integrations.OAuthScopes.DataWrite} {Erp.Api.Integrations.OAuthScopes.ODataRead}";
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Integrations.ApiKeyHandler.SchemeName), path, method));
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Integrations.OAuthTokenHandler.SchemeName, all), path, method));
        Assert.NotNull(BearerScope.Check(new BearerCredential(Erp.Api.Mcp.McpTokenHandler.SchemeName), path, method));
        Assert.Null(BearerScope.Check(null, path, method));
    }
}
