using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Erp.Api.Mcp;

/// <summary>
/// Spec 038 (H3 CA4): autenticación de <c>/mcp</c> con <c>Authorization: Bearer erpmt_…</c>. Token de acceso vigente, conexión
/// sin revocar, ligada a este recurso y persona activa con el mismo sello que cuando consintió (cambiar contraseña, roles o
/// desactivarla mata la conexión). La persona se recarga en cada petición, como la cookie.
/// </summary>
public sealed class McpTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    Db db, UserContextLoader loader, McpOptions mcp) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Mcp";
    /// <summary>La conexión (<c>meta.McpGrant</c>) del token.</summary>
    public const string GrantClaim = "mcp.grant";

    private sealed record TokenRow(int GrantId, int UserId, Guid SecurityStamp, string Scopes, string Resource, DateTime? LastUsedAt);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!McpFormat.IsBearer(Request)) return AuthenticateResult.NoResult();
        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var fail = AuthenticateResult.Fail("Token no válido.");
        if (!McpFormat.Is(McpFormat.AccessPrefix, token)) return fail;
        await using var cn = await db.OpenAsync(Context.RequestAborted);
        var row = await cn.QuerySingleOrDefaultAsync<TokenRow>("""
            SELECT g.Id AS GrantId, g.UserId, g.SecurityStamp, g.Scopes, g.Resource, g.LastUsedAt
              FROM meta.McpToken t JOIN meta.McpGrant g ON g.Id = t.GrantId
             WHERE t.TokenHash = @hash AND t.Kind = 'a' AND t.ExpiresAt > SYSUTCDATETIME() AND g.RevokedAt IS NULL
            """, new { hash = McpFormat.Hash(token) });
        // RFC 8707: el token es de este recurso (otra URL pública = otra audiencia).
        if (row is null || !string.Equals(row.Resource.TrimEnd('/'), mcp.Resource(Context).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return fail;
        var user = await loader.LoadAsync(row.UserId, row.SecurityStamp, Context.RequestAborted);
        if (user is null || user.IsService) return fail;
        if (row.LastUsedAt is null || row.LastUsedAt < DateTime.UtcNow.AddMinutes(-1))
            await cn.ExecuteAsync("UPDATE meta.McpGrant SET LastUsedAt = SYSUTCDATETIME() WHERE Id = @GrantId", new { row.GrantId });
        Context.Items[UserContextExtensions.ItemKey] = user;
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username),
            new Claim(Integrations.OAuthScopes.Claim, McpScopes.Join(row.Scopes.Split(' '))), new Claim(GrantClaim, row.GrantId.ToString()),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>H1 CA1: el 401 dice dónde están los metadatos del recurso (RFC 9728 §5.1).</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        var metadata = mcp.Origin(Context) + "/.well-known/oauth-protected-resource/mcp";
        Response.Headers.WWWAuthenticate = McpFormat.IsBearer(Request)
            ? $"Bearer resource_metadata=\"{metadata}\", error=\"invalid_token\""
            : $"Bearer resource_metadata=\"{metadata}\"";
        return Task.CompletedTask;
    }
}

/// <summary>
/// Spec 038 (H5 CA3): cubeta de fichas por persona para <c>tools/call</c>, pensada para un cliente en <i>code mode</i> que
/// llama herramientas en bucle: <c>Mcp:Burst</c> seguidas y <c>Mcp:CallsPerMinute</c> de recarga. Se suma a los límites del
/// despachador (034), que siguen contando cada ruta.
/// </summary>
public sealed class McpLimits(McpOptions options) : IDisposable
{
    private readonly PartitionedRateLimiter<int> _limiter = PartitionedRateLimiter.Create<int, int>(user =>
        RateLimitPartition.GetTokenBucketLimiter(user, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.Burst,
            // Una ficha cada 60/n segundos (hasta 10 por segundo); más arriba, n/60 por segundo.
            TokensPerPeriod = options.CallsPerMinute <= 600 ? 1 : options.CallsPerMinute / 60,
            ReplenishmentPeriod = options.CallsPerMinute <= 600 ? TimeSpan.FromSeconds(60.0 / options.CallsPerMinute) : TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    /// <summary>null si hay ficha; si no, en cuántos segundos habrá (≥ 1).</summary>
    public int? TryAcquire(int user)
    {
        using var lease = _limiter.AttemptAcquire(user);
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : Math.Max(1, (int)Math.Ceiling(60.0 / options.CallsPerMinute));
    }

    public void Dispose() => _limiter.Dispose();
}
