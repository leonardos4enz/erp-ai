using System.Net;
using Erp.Api.Infrastructure;
using Erp.Api.Mcp;
using Microsoft.Extensions.Configuration;

namespace Erp.Tests;

/// <summary>
/// Spec 045 · Endurecimiento 7 (auditoría 8): el <i>lease</i> de una clave MCP frente a su retención (D-10) y los proxies de
/// confianza escritos como texto (O-02).
/// </summary>
public class Hardening7Tests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void H5_CA3_WriteLeaseSeconds_no_pasa_de_la_mitad_de_IdempotencyHours()
    {
        static int Lease(string hours, string lease) =>
            McpOptions.From(Config(("Mcp:IdempotencyHours", hours), ("Mcp:WriteLeaseSeconds", lease))).WriteLeaseSeconds;
        // Regresión (auditoría 8, D-10): se acotaban por separado y una clave en curso duraba más que su retención.
        Assert.Equal(1800, Lease("1", "7200"));
        Assert.Equal(300, Lease("1", "300"));
        Assert.Equal(24 * 3600, Lease("720", "999999"));
        Assert.Equal(300, McpOptions.From(Config()).WriteLeaseSeconds);
    }

    [Fact]
    public void H6_CA3_ForwardedProxies_escrito_como_texto_se_lee_como_lista()
    {
        // Regresión (auditoría 8, O-02): «Security__ForwardedProxies=10.0.0.5» (sin «__0») se ignoraba sin aviso.
        var o = ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies", "10.0.0.5, 10.0.0.6;192.168.0.0/16")))!;
        Assert.Equal([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6")], o.KnownProxies);
        Assert.Single(o.KnownIPNetworks);
        // Mal escrito, igual detiene el arranque.
        Assert.Throws<InvalidOperationException>(() => ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies", "proxy.interno"))));
        Assert.Null(ClientAddress.ForwardedOptions(Config(("Security:ForwardedProxies", " "))));
    }
}
