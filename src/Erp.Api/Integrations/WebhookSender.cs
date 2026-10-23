using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Erp.Api.Integrations;

/// <summary>Spec 008: firma de cada envío (HMAC-SHA256 sobre <c>"{timestamp}.{cuerpo}"</c>).</summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-ERP-Signature";
    public const string TimestampHeader = "X-ERP-Timestamp";
    public const string DeliveryHeader = "X-ERP-Delivery";
    public const string EventHeader = "X-ERP-Event";

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>Lo que debe hacer el receptor: firma correcta (en tiempo constante) y marca de tiempo reciente.</summary>
    public static bool Verify(string secret, string? timestamp, string body, string? signature, DateTimeOffset now, TimeSpan tolerance)
    {
        if (!long.TryParse(timestamp, out var ts) || signature is null) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > tolerance.TotalSeconds) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, ts, body)), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>Secreto nuevo: 256 bits aleatorios.</summary>
    public static string NewSecret() => "whsec_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Destino y secreto cifrados con Data Protection: nunca en metadatos, planes, auditoría ni repositorio.</summary>
public sealed class WebhookSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _p = provider.CreateProtector("Erp.Webhooks.v1");
    public string Protect(string value) => _p.Protect(value);
    public string Unprotect(string value) => _p.Unprotect(value);
}

public sealed record SendResult(bool Ok, int? Status, string? Error);

/// <summary>
/// Envía una entrega firmada. El cliente HTTP no sigue redirecciones ni usa proxy, y su <c>ConnectCallback</c> revisa
/// cada IP resuelta contra <see cref="WebhookUrlPolicy"/> justo antes de conectar (SSRF y DNS rebinding).
/// </summary>
public sealed class WebhookSender : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly WebhookUrlPolicy _policy;

    public WebhookSender(WebhookUrlPolicy policy)
    {
        _policy = policy;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = IPAddress.TryParse(ctx.DnsEndPoint.Host, out var literal)
                    ? [literal] : await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => !_policy.IsBlocked(a)).ToArray();
                if (allowed.Length == 0 || allowed.Length != addresses.Length)
                    throw new BlockedDestinationException();
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ErpAi-Webhooks/1");
    }

    public async Task<SendResult> SendAsync(string url, string secret, Guid deliveryId, string ev, string body, CancellationToken ct)
    {
        if (_policy.Check(url) is { } bad) return new(false, null, bad);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add(WebhookSignature.EventHeader, ev);
        req.Headers.Add(WebhookSignature.DeliveryHeader, deliveryId.ToString());
        req.Headers.Add(WebhookSignature.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, ts, body));
        try
        {
            // Solo importan los encabezados: el cuerpo de la respuesta no se lee ni se guarda.
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            return code is >= 200 and < 300 ? new(true, code, null)
                : new(false, code, code is >= 300 and < 400 ? "El destino respondió con una redirección (no se siguen)." : $"El destino respondió {code}.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedDestinationException || ex is { InnerException.InnerException: BlockedDestinationException })
        {
            return new(false, null, "El destino resuelve a una dirección interna; no se permite.");
        }
        catch (HttpRequestException) { return new(false, null, "No se pudo conectar con el destino."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, $"El destino no respondió en {Timeout.TotalSeconds:0} s."); }
    }

    public void Dispose() => _http.Dispose();

    private sealed class BlockedDestinationException() : Exception("Destino bloqueado.");
}
