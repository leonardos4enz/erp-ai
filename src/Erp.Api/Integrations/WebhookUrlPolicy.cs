using System.Net;
using System.Net.Sockets;

namespace Erp.Api.Integrations;

/// <summary>
/// Spec 008 · SSRF: qué destinos admite un webhook. La URL se revisa al configurarla y cada IP se revisa otra vez al
/// conectar (<see cref="WebhookSender"/>), porque el DNS puede cambiar entre una y otra (DNS rebinding).
/// </summary>
/// <param name="allowedInternal">
/// IPs internas permitidas de forma explícita (<c>Webhooks:AllowedInternalAddresses</c>). Solo para la suite E2E, que
/// recibe en 127.0.0.1; cualquier otra dirección interna sigue bloqueada.
/// </param>
public sealed class WebhookUrlPolicy(bool allowHttp = false, IReadOnlyCollection<IPAddress>? allowedInternal = null)
{
    public const int MaxLength = 2000;

    /// <summary>Null si la URL es aceptable; si no, el motivo (seguro para mostrarse).</summary>
    public string? Check(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return $"Escribe una URL de máximo {MaxLength} caracteres.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "La URL no es válida.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) return "La URL debe usar https.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "La URL no puede llevar usuario ni contraseña.";
        if (!string.IsNullOrEmpty(uri.Fragment)) return "La URL no puede llevar fragmento (#).";
        if (string.IsNullOrEmpty(uri.Host)) return "La URL no tiene servidor.";
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IsBlocked(ip))
            return "La URL apunta a una dirección interna; no se permite.";
        if (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "La URL apunta a una dirección interna; no se permite.";
        return null;
    }

    /// <summary>Lo que se muestra en pantalla y en la auditoría: esquema, servidor y puerto (la ruta puede llevar un token).</summary>
    public static string Mask(string url)
    {
        var uri = new Uri(url.Trim());
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.IdnHost}{port}{(uri.AbsolutePath.Length > 1 || uri.Query.Length > 0 ? "/…" : "")}";
    }

    public bool IsBlocked(IPAddress ip) =>
        IsInternal(ip) && allowedInternal?.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) != true;

    /// <summary>Direcciones que no son de Internet: loopback, privadas, enlace local (metadatos de nube), CGNAT, multicast, reservadas.</summary>
    public static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            // ::a.b.c.d (compatible con IPv4, obsoleto) se evalúa como IPv4.
            if (b6.Take(12).All(x => x == 0) && !(b6[12] == 0 && b6[13] == 0 && b6[14] == 0 && b6[15] <= 1))
                return IsInternal(new IPAddress(b6[12..]));
            return ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                   ip.IsIPv6Multicast || (b6[0] & 0xFE) == 0xFC /* fc00::/7 */ ||
                   (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x0d && b6[3] == 0xb8) /* 2001:db8::/32 documentación */ ||
                   (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x00 && b6[3] == 0x00) /* 2001::/32 Teredo (spec 014) */ ||
                   (b6[0] == 0x00 && b6[1] == 0x64 && b6[2] == 0xff && b6[3] == 0x9b) /* 64:ff9b::/96 NAT64 */ ||
                   (b6[0] == 0x20 && b6[1] == 0x02) /* 2002::/16 6to4 */;
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] >= 64 && b[1] <= 127,
            169 => b[1] == 254,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => (b[1] == 168) || (b[1] == 0 && (b[2] == 0 || b[2] == 2)),
            198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100),
            203 => b[1] == 0 && b[2] == 113,
            >= 224 => true, // multicast, reservadas y broadcast
            _ => false,
        };
    }
}
