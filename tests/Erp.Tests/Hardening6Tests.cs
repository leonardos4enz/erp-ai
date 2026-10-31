using System.Net;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Studio;
using Microsoft.Extensions.Configuration;

namespace Erp.Tests;

/// <summary>
/// Spec 040 · Endurecimiento 6 (auditoría 7): la clave de dirección de los límites (una IPv6 por su /64), los proxies de
/// confianza, <c>RenewSeconds</c> frente a <c>LeaseSeconds</c>, la purga de un historial ilegible y el tope de uniones de un
/// reporte contado como lo reutiliza el motor.
/// </summary>
public class Hardening6Tests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ---------- H1 ----------

    [Fact]
    public void H1_CA4_la_clave_de_una_IPv6_es_su_red_64()
    {
        Assert.Equal("2001:db8:1:2::/64", ClientAddress.Key(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        // Dos equipos (o dos direcciones del mismo) en la misma /64 comparten la clave; otra /64 no.
        Assert.Equal(ClientAddress.Key(IPAddress.Parse("2001:db8:1:2::1")), ClientAddress.Key(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ClientAddress.Key(IPAddress.Parse("2001:db8:1:2::1")), ClientAddress.Key(IPAddress.Parse("2001:db8:1:3::1")));
        // IPv4, IPv4 mapeada en IPv6 (la misma) y sin dirección.
        Assert.Equal("10.1.2.3", ClientAddress.Key(IPAddress.Parse("10.1.2.3")));
        Assert.Equal("10.1.2.3", ClientAddress.Key(IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.Null(ClientAddress.Key((IPAddress?)null));
        // Revisión adversarial 040 (F-05): el prefijo se configura (Security:Ipv6PrefixLength, 48–128).
        Assert.Equal("2001:db8:1:200::/56", ClientAddress.Key(IPAddress.Parse("2001:db8:1:2ff:1::1"), 56));
        Assert.Equal("2001:db8:1:2::1/128", ClientAddress.Key(IPAddress.Parse("2001:db8:1:2::1"), 128));
        Assert.Equal(64, ClientAddress.Ipv6Prefix(Config()));
        Assert.Equal(48, ClientAddress.Ipv6Prefix(Config(("Security:Ipv6PrefixLength", "8"))));
        // Cabe en meta.McpClient.CreatedIp (varchar(45)), también con /128.
        Assert.True(ClientAddress.Key(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), 128)!.Length <= 45);
    }

    [Fact]
    public void H1_CA5_los_proxies_de_confianza_son_explicitos_y_uno_mal_escrito_detiene_el_arranque()
    {
        Assert.Null(ClientAddress.ForwardedOptions(Config()));
        var o = ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies:0", "10.0.0.5"), ("Security:ForwardedProxies:1", "192.168.0.0/16")))!;
        Assert.Equal([IPAddress.Parse("10.0.0.5")], o.KnownProxies);
        Assert.Single(o.KnownIPNetworks);
        // Sin el loopback que ASP.NET trae por omisión: solo lo que se lista.
        Assert.DoesNotContain(IPAddress.IPv6Loopback, o.KnownProxies);
        Assert.Equal(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto, o.ForwardedHeaders);
        Assert.Equal(1, o.ForwardLimit); // revisión adversarial 040 (F-06): un salto por omisión
        Assert.Equal(2, ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies:0", "10.0.0.5"), ("Security:ForwardedLimit", "2")))!.ForwardLimit);
        var ex = Assert.Throws<InvalidOperationException>(() => ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies:0", "proxy.interno"))));
        Assert.Contains("proxy.interno", ex.Message);
    }

    // ---------- H2 ----------

    [Fact]
    public void H2_CA2_RenewSeconds_no_pasa_de_un_tercio_del_lease()
    {
        static int Renew(string lease, string renew) =>
            StudioOptions.From(Config(("Studio:LeaseSeconds", lease), ("Studio:RenewSeconds", renew))).RenewSeconds;
        // Regresión (auditoría 7, D-04): con lease 5 y renovación 10, cada turno perdía su lease.
        Assert.Equal(1, Renew("5", "10"));
        Assert.Equal(10, Renew("30", "20"));
        Assert.Equal(10, Renew("120", "10")); // los valores por omisión no cambian
        Assert.Equal(40, Renew("120", "600"));
    }

    [Fact]
    public void H2_CA1_una_salida_ilegible_se_purga_con_un_aviso()
    {
        // Lo que leyó una herramienta de «datos» se retira; lo demás se conserva.
        var content = StudioTranscript.Serialize([new StudioToolResult("toolu_1", "CANARIO-dato", false, "leer_registros"), new StudioText("hola")]);
        var purged = StudioTools.PurgeContent(content);
        Assert.DoesNotContain("CANARIO-dato", purged);
        Assert.Contains("hola", purged);
        // Regresión (auditoría 7, D-03): un JSON que no se puede leer hacía fallar la purga (y con ella el vencimiento de todas
        // las sesiones); ahora se retira entero y queda un aviso.
        foreach (var bad in new[] { "{no es json CANARIO", "[{\"type\":\"tool_use\",\"input\":\"CANARIO\"}]", "[{\"type\":\"text\",\"text\":42}]" })
        {
            var p = StudioTools.PurgeContent(bad);
            Assert.DoesNotContain("CANARIO", p);
            Assert.Contains(StudioTools.Unreadable, p);
        }
    }

    // ---------- H4 ----------

    private static List<Operation> JoinModel() =>
    [
        new CreateEntityOp { Name = "JnCategoria", Label = "Categoría", LabelPlural = "Categorías", DisplayField = "Nombre", Fields =
            [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true }] },
        new CreateEntityOp { Name = "JnDestino", Label = "Destino", LabelPlural = "Destinos", DisplayField = "Nombre", Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Cat", Label = "Categoría", Type = FieldTypes.Reference, ReferenceEntity = "JnCategoria" },
            new FieldDef { Name = "Monto", Label = "Monto", Type = FieldTypes.Decimal },
        ] },
        new CreateEntityOp { Name = "JnMedio", Label = "Medio", LabelPlural = "Medios", DisplayField = "Nombre", Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Dest", Label = "Destino", Type = FieldTypes.Reference, ReferenceEntity = "JnDestino" },
        ] },
        new CreateEntityOp { Name = "JnBase", Label = "Base", LabelPlural = "Bases", Fields =
            [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, Required = true },
             .. new[] { "G1", "G2", "R1", "R2", "R3", "R4", "R5", "R6" }.Select(n => new FieldDef { Name = n, Label = n, Type = FieldTypes.Reference, ReferenceEntity = "JnMedio" })] },
    ];

    private static ReportDef Report(string[] groups, string[] measures) => new()
    {
        Name = "Uniones", Label = "Uniones", GroupBy = groups.Select(g => new ReportGroup { Field = g }).ToList(),
        Measures = measures.Select(m => new ReportMeasure { Fn = "sum", Field = m }).ToList(),
    };

    private static string Compile(ReportDef r) => string.Join(" | ",
        PlanCompiler.Compile(new ChangePlan { Operations = [.. JoinModel(), new SetReportsOp { Entity = "JnBase", Reports = [r] }] },
            new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Preview.Errors.Select(e => e.Message));

    private static readonly string[] SixMeasures = ["R1.Dest.Monto", "R2.Dest.Monto", "R3.Dest.Monto", "R4.Dest.Monto", "R5.Dest.Monto", "R6.Dest.Monto"];

    [Fact]
    public void H4_CA1_un_reporte_con_mas_de_16_uniones_no_compila()
    {
        // 6 medidas a dos saltos (12 uniones) y 2 agrupaciones por una referencia a dos saltos (3 cada una: 18).
        // Regresión (auditoría 7, D-09): el plan 039 prometía ≤ 16 y no había tope.
        var errors = Compile(Report(["G1.Dest.Cat", "G2.Dest.Cat"], SixMeasures));
        Assert.Contains("usa 18 uniones a otros catálogos; el máximo es 16", errors);
        // 16 justas: las agrupaciones por un campo de texto a dos saltos no unen el catálogo final.
        Assert.DoesNotContain("uniones", Compile(Report(["G1.Dest.Nombre", "G2.Dest.Nombre"], SixMeasures)));
    }

    [Fact]
    public void H4_CA1_las_uniones_se_cuentan_como_las_reutiliza_el_motor()
    {
        var model = PlanCompiler.Compile(new ChangePlan { Operations = JoinModel() }, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ResultingModel;
        var e = model["JnBase"];
        Assert.Equal(0, InterfaceRules.JoinCount(e, Report([], []), model) + InterfaceRules.JoinCount(e, new ReportDef { Name = "C", Label = "C", Measures = [new() { Fn = "count" }] }, model));
        // Agrupar por R1 (une JnMedio) y medir R1.Dest.Monto (R1 y R1.D): R1 se comparte.
        Assert.Equal(2, InterfaceRules.JoinCount(e, Report(["R1"], ["R1.Dest.Monto"]), model));
        // La misma ruta dos veces no suma.
        Assert.Equal(2, InterfaceRules.JoinCount(e, Report(["R1.Dest"], ["R1.Dest.Monto", "R1.Dest.Monto"]), model));
        // Agrupar por un texto propio no une nada.
        Assert.Equal(0, InterfaceRules.JoinCount(e, Report(["Nombre"], []), model));
    }
}
