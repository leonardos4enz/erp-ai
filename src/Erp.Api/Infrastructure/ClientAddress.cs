using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Erp.Api.Infrastructure;

/// <summary>
/// Spec 040 (auditoría 7, D-01 y D-02): de qué dirección viene una petición, para los límites por dirección. Una IPv6 se
/// agrupa por su red /64 (un equipo tiene 2^64 direcciones en su red; contar por dirección no limita nada) y una IPv4 mapeada
/// en IPv6 cuenta como la IPv4. La bitácora sigue guardando la dirección completa.
/// </summary>
public static class ClientAddress
{
    public const int DefaultIpv6Prefix = 64;

    /// <summary>
    /// Revisión adversarial 040 (F-05): el prefijo con que se agrupa una IPv6 (<c>Security:Ipv6PrefixLength</c>, 48–128, 64 por
    /// omisión). Un despliegue que vea a muchos clientes legítimos salir del mismo /64 (p. ej. las renovaciones de Claude.ai)
    /// lo sube.
    /// </summary>
    public static int Ipv6Prefix(IConfiguration config) => Math.Clamp(config.GetValue("Security:Ipv6PrefixLength", DefaultIpv6Prefix), 48, 128);

    /// <summary>La clave de partición: la IPv4, o el prefijo de la IPv6 (<c>2001:db8:1:2::/64</c>); null sin dirección.</summary>
    public static string? Key(IPAddress? ip, int prefix = DefaultIpv6Prefix)
    {
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        for (var bit = Math.Clamp(prefix, 0, 128); bit < 128; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return $"{new IPAddress(bytes)}/{prefix}";
    }

    /// <summary>La partición de un límite: la persona si la hay; si no, la clave de su dirección.</summary>
    public static string Partition(HttpContext http, int prefix, bool byUser = true) =>
        (byUser ? http.User.Identity?.Name : null) ?? Key(http.Connection.RemoteIpAddress, prefix) ?? "unknown";

    /// <summary>
    /// Spec 040 (H1 CA5, R-021-5 y R-018-4): proxies de confianza (<c>Security:ForwardedProxies</c>, IP o red CIDR). Solo de
    /// ellos se aceptan <c>X-Forwarded-For</c> y <c>X-Forwarded-Proto</c>; null sin la opción (no se confía en ninguna cabecera).
    /// Un valor mal escrito detiene el arranque. Revisión adversarial 040 (F-06): se toma un solo salto por omisión
    /// (<c>Security:ForwardedLimit</c>, 1–10): con una red listada, un cliente de esa red que mandara su propio
    /// <c>X-Forwarded-For</c> elegiría su dirección; listar las IP exactas de los proxies.
    /// </summary>
    public static ForwardedHeadersOptions? ForwardedOptions(IConfiguration config)
    {
        var entries = config.GetSection("Security:ForwardedProxies").Get<string[]>() ?? [];
        if (entries.Length == 0) return null;
        var o = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // De derecha a izquierda, a lo más este número de saltos y solo mientras quien entrega sea un proxy listado.
            ForwardLimit = Math.Clamp(config.GetValue("Security:ForwardedLimit", 1), 1, 10),
        };
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var raw in entries.Select(e => e.Trim()).Where(e => e.Length > 0))
        {
            if (raw.Contains('/') && System.Net.IPNetwork.TryParse(raw, out var network)) o.KnownIPNetworks.Add(network);
            else if (!raw.Contains('/') && IPAddress.TryParse(raw, out var ip)) o.KnownProxies.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
            else throw new InvalidOperationException($"Security:ForwardedProxies: «{raw}» no es una dirección IP ni una red CIDR (p. ej. 10.0.0.5 o 10.0.0.0/8).");
        }
        return o;
    }
}
