using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Erp.Api.Integrations;

namespace Erp.Api.Mcp;

/// <summary>Spec 038: configuración del servidor MCP (<c>Mcp:*</c>, por entorno).</summary>
public sealed class McpOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>Origen público (<c>https://erp.ejemplo.com</c>); sin él, el de la petición (R-021-5: detrás de un proxy, fijarlo).</summary>
    public string? PublicUrl { get; init; }
    public int AccessTokenMinutes { get; init; } = 60;
    public int RefreshTokenDays { get; init; } = 30;
    public int CodeSeconds { get; init; } = 120;
    /// <summary>Cubeta de fichas por persona para <c>tools/call</c>: llamadas seguidas y recarga por minuto.</summary>
    public int Burst { get; init; } = 60;
    public int CallsPerMinute { get; init; } = 120;
    public int MaxResultBytes { get; init; } = 256 * 1024;
    public int MaxClients { get; init; } = 1000;
    /// <summary>Revisión adversarial F-04: clientes registrados sin canjear todavía por dirección (los demás esperan al barrido).</summary>
    public int PendingClientsPerAddress { get; init; } = 20;
    public IReadOnlyList<string> TrustedRedirectHosts { get; init; } = ["claude.ai", "claude.com"];
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public static McpOptions From(IConfiguration config)
    {
        var s = config.GetSection("Mcp");
        static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
        var trusted = s.GetSection("TrustedRedirectHosts").Get<string[]>();
        return new McpOptions
        {
            Enabled = s.GetValue("Enabled", true),
            PublicUrl = s["PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : null,
            AccessTokenMinutes = Clamp(s.GetValue("AccessTokenMinutes", 60), 5, 24 * 60),
            RefreshTokenDays = Clamp(s.GetValue("RefreshTokenDays", 30), 1, 365),
            CodeSeconds = Clamp(s.GetValue("CodeSeconds", 120), 10, 600),
            Burst = Clamp(s.GetValue("Burst", 60), 1, 10_000),
            CallsPerMinute = Clamp(s.GetValue("CallsPerMinute", 120), 1, 100_000),
            MaxResultBytes = Clamp(s.GetValue("MaxResultBytes", 256 * 1024), 1024, 4 * 1024 * 1024),
            MaxClients = Clamp(s.GetValue("MaxClients", 1000), 1, 1_000_000),
            PendingClientsPerAddress = Clamp(s.GetValue("PendingClientsPerAddress", 20), 1, 100_000),
            TrustedRedirectHosts = trusted is { Length: > 0 } ? trusted : ["claude.ai", "claude.com"],
            AllowedOrigins = s.GetSection("AllowedOrigins").Get<string[]>() ?? [],
        };
    }

    /// <summary>El origen público: el configurado o el de la petición.</summary>
    public string Origin(HttpContext http) => PublicUrl ?? $"{http.Request.Scheme}://{http.Request.Host}";

    /// <summary>El recurso protegido (RFC 8707/9728): la URL del servidor MCP.</summary>
    public string Resource(HttpContext http) => Origin(http) + "/mcp";
}

/// <summary>Spec 038: alcances que una persona consiente a su cliente MCP (los de Studio, sin la copia aislada: CLARIFY 2).</summary>
public static class McpScopes
{
    public const string Model = Studio.StudioScopes.Model;
    public const string Data = Studio.StudioScopes.Data;

    /// <summary>En este orden se guardan y se responden.</summary>
    public static readonly IReadOnlyList<string> All = [Model, Data];

    /// <summary>
    /// Lista separada por espacios; null si trae uno desconocido (p. ej. <c>aislado</c>). Vacía o ausente = <c>modelo</c>.
    /// <c>modelo</c> va siempre: sin él no hay herramientas.
    /// </summary>
    public static string[]? Parse(string? value)
    {
        var parts = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => !All.Contains(p, StringComparer.Ordinal))) return null;
        return Normalize(parts.Append(Model));
    }

    public static string[] Normalize(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.Ordinal);
        return All.Where(set.Contains).ToArray();
    }

    public static string Join(IEnumerable<string> scopes) => string.Join(' ', Normalize(scopes));
}

/// <summary>Spec 038: identificadores y secretos del flujo. 256 bits aleatorios; en la base, solo su SHA-256.</summary>
public static partial class McpFormat
{
    public const string ClientPrefix = "erpmcp_";
    public const string CodePrefix = "erpmc_";
    public const string AccessPrefix = "erpmt_";
    public const string RefreshPrefix = "erpmr_";

    [GeneratedRegex("^erpmcp_[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientPattern();

    [GeneratedRegex("^erpm[ctr]_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    /// <summary>RFC 7636 §4.1: 43 a 128 caracteres «unreserved».</summary>
    [GeneratedRegex("^[A-Za-z0-9._~-]{43,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex VerifierPattern();

    /// <summary>El reto S256 es el SHA-256 en base64url sin relleno: 43 caracteres.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengePattern();

    public static string NewClientId() => ClientPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public static string New(string prefix) => prefix + WebhookSignature.Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool IsClientId(string? s) => s is not null && ClientPattern().IsMatch(s);
    public static bool Is(string prefix, string? s) => s is not null && s.StartsWith(prefix, StringComparison.Ordinal) && SecretPattern().IsMatch(s);
    public static bool IsVerifier(string? s) => s is not null && VerifierPattern().IsMatch(s);
    public static bool IsChallenge(string? s) => s is not null && ChallengePattern().IsMatch(s);

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>RFC 7636 §4.6: <c>BASE64URL(SHA256(ASCII(code_verifier))) == code_challenge</c>, en tiempo constante.</summary>
    public static bool VerifyS256(string verifier, string challenge) =>
        IsVerifier(verifier) && IsChallenge(challenge)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Challenge(verifier)), Encoding.ASCII.GetBytes(challenge));

    public static string Challenge(string verifier) => WebhookSignature.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>¿La cabecera trae un token de acceso MCP?</summary>
    public static bool IsBearer(HttpRequest r) =>
        r.Headers.Authorization.ToString() is var h && h.StartsWith("Bearer " + AccessPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Revisión exploratoria 038 (F-01): JSON de un cuerpo, con UTF-8 estricto (si no, una cadena inválida estalla al leerse: 500).</summary>
public static class StrictJson
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>El nodo, o null si no es UTF-8 válido o no es JSON.</summary>
    public static System.Text.Json.Nodes.JsonNode? Parse(byte[] bytes, out bool valid)
    {
        valid = false;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Utf8.GetString(bytes));
            valid = true;
            return node;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>
/// Spec 038 (H1 CA3): URL de retorno de un cliente registrado. https absoluta, o http de <i>loopback</i> (RFC 8252 §7.3, apps
/// nativas como Claude Code); sin fragmento ni credenciales. Al autorizar se compara exacta contra las registradas.
/// </summary>
public static class RedirectUris
{
    public const int Max = 5;
    public const int MaxLength = 500;

    /// <summary>null si es válida; si no, por qué.</summary>
    public static string? Problem(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxLength) return $"Cada URL de retorno es obligatoria y de hasta {MaxLength} caracteres.";
        if (uri.Any(char.IsControl) || uri.Contains(' ')) return "La URL de retorno no admite espacios ni caracteres de control.";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return "La URL de retorno debe ser absoluta.";
        if (u.Fragment.Length > 0 || uri.Contains('#')) return "La URL de retorno no admite fragmento (#).";
        if (u.UserInfo.Length > 0) return "La URL de retorno no admite usuario ni contraseña.";
        if (u.Scheme == Uri.UriSchemeHttps) return null;
        if (u.Scheme == Uri.UriSchemeHttp && IsLoopback(u)) return null;
        return "La URL de retorno debe ser https (o http solo hacia este equipo: localhost o 127.0.0.1).";
    }

    public static bool IsLoopback(Uri u) =>
        u.IsLoopback && (u.Host == "localhost" || IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>El sitio que se le muestra a la persona: el host (con puerto si no es el de siempre).</summary>
    /// Revisión adversarial F-09: el host en ASCII (punycode), para que un homógrafo (<c>сlaude.ai</c> con «с» cirílica) no pase por el real.
    public static string Host(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.IdnHost : $"{u.IdnHost}:{u.Port}") : uri;

    /// <summary>Revisión adversarial F-09: un nombre visible sin caracteres de control ni de formato (bidi, ancho cero).</summary>
    public static bool SafeName(string name) =>
        !name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format);

    /// <summary>¿Un sitio conocido? Los de la lista (o sus subdominios) y el propio equipo.</summary>
    public static bool Trusted(string uri, IReadOnlyList<string> hosts) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && (IsLoopback(u) || hosts.Any(h => u.Scheme == Uri.UriSchemeHttps
            && (string.Equals(u.IdnHost, h, StringComparison.OrdinalIgnoreCase) || u.IdnHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))));
}
