using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Security;
using Microsoft.AspNetCore.WebUtilities;

namespace Erp.Api.Mcp;

public sealed record McpConnectionDto(int Id, string Client, string RedirectHost, IReadOnlyList<string> Scopes, DateTime CreatedAt, DateTime? LastUsedAt);
public sealed record McpConnectionsDto(string ServerUrl, IReadOnlyList<McpConnectionDto> Connections);
public sealed record McpAuthorizeRequest(string? Query, bool Approve, string[]? Scopes);

/// <summary>
/// Spec 038: servidor de autorización del MCP (OAuth 2.1). Clientes públicos con registro dinámico (RFC 7591), código de
/// autorización con PKCE S256 obligatorio, tokens ligados al recurso <c>/mcp</c> (RFC 8707), rotación del <i>refresh token</i>
/// con detección de reuso y revocación (RFC 7009). La persona consiente desde su sesión (cookie + CSRF); el token resultante
/// solo sirve en <c>/mcp</c> (<see cref="BearerScope"/>).
/// </summary>
public sealed class McpOAuthService(Db db, McpOptions options, UserContextLoader loader, ILogger<McpOAuthService> log)
{
    public const int MaxRegistration = 8 * 1024;
    public const int MaxState = 500;
    public const int MaxName = 100;

    public static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];

    private void RequireEnabled()
    {
        if (!options.Enabled) throw new NotFound("Ruta no encontrada.");
    }

    // ---------- Metadatos (RFC 9728 y RFC 8414) ----------

    public object ProtectedResource(HttpContext http)
    {
        RequireEnabled();
        return new
        {
            resource = options.Resource(http),
            authorization_servers = new[] { options.Origin(http) },
            scopes_supported = McpScopes.All,
            bearer_methods_supported = new[] { "header" },
            resource_name = "ERP AI",
        };
    }

    public object AuthorizationServer(HttpContext http)
    {
        RequireEnabled();
        var origin = options.Origin(http);
        return new
        {
            issuer = origin,
            authorization_endpoint = origin + "/oauth/authorize",
            token_endpoint = origin + "/oauth/token",
            registration_endpoint = origin + "/oauth/register",
            revocation_endpoint = origin + "/oauth/revoke",
            response_types_supported = new[] { "code" },
            grant_types_supported = GrantTypes,
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            revocation_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = McpScopes.All,
            authorization_response_iss_parameter_supported = true,
        };
    }

    // ---------- Registro dinámico (RFC 7591) ----------

    private static IResult RegisterError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 400);

    public async Task<IResult> RegisterAsync(HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        if (http.Request.ContentLength > MaxRegistration) return RegisterError("invalid_client_metadata", "La petición es demasiado grande.");
        // Revisión adversarial F-05: lectura acotada (sin Content-Length, un cuerpo por trozos no se lee entero antes de medirlo).
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int n;
        while ((n = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxRegistration) return RegisterError("invalid_client_metadata", "La petición es demasiado grande.");
        }
        if (StrictJson.Parse(buffer.ToArray(), out _) is not JsonObject body) return RegisterError("invalid_client_metadata", "El cuerpo debe ser un objeto JSON.");

        static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        static string[]? Texts(JsonObject o, string key) =>
            o[key] is JsonArray a && a.All(x => x is JsonValue v && v.GetValueKind() == JsonValueKind.String) ? a.Select(x => x!.GetValue<string>()).ToArray() : null;

        var uris = Texts(body, "redirect_uris");
        if (uris is not { Length: > 0 } || uris.Length > RedirectUris.Max)
            return RegisterError("invalid_redirect_uri", $"Indica de 1 a {RedirectUris.Max} URL de retorno.");
        if (uris.Select(RedirectUris.Problem).FirstOrDefault(p => p is not null) is { } problem) return RegisterError("invalid_redirect_uri", problem);
        if (body["token_endpoint_auth_method"] is not null && Text(body, "token_endpoint_auth_method") != "none")
            return RegisterError("invalid_client_metadata", "Solo se registran clientes públicos (token_endpoint_auth_method = none) con PKCE.");
        var grants = body["grant_types"] is null ? GrantTypes : Texts(body, "grant_types");
        if (grants is null || grants.Length == 0 || grants.Any(g => !GrantTypes.Contains(g)) || !grants.Contains("authorization_code"))
            return RegisterError("invalid_client_metadata", "grant_types admite authorization_code y refresh_token.");
        var responses = body["response_types"] is null ? ["code"] : Texts(body, "response_types");
        if (responses is null || responses.Length == 0 || responses.Any(r => r != "code"))
            return RegisterError("invalid_client_metadata", "response_types solo admite code.");
        var name = (Text(body, "client_name") ?? "").Trim();
        if (name.Length == 0 && body["client_name"] is not null) return RegisterError("invalid_client_metadata", "client_name debe ser texto.");
        if (name.Length == 0) name = "Cliente MCP";
        if (name.Length > MaxName || !RedirectUris.SafeName(name))
            return RegisterError("invalid_client_metadata", $"client_name admite hasta {MaxName} caracteres, sin caracteres de control ni de formato.");

        var clientId = McpFormat.NewClientId();
        var uniqueUris = uris.Distinct(StringComparer.Ordinal).ToArray();
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        // Barrido: un cliente que nunca canjeó un código en 1 h no sirve (CLARIFY 4; revisión adversarial F-04: consentir sin
        // canjear ya no lo salva). Sus códigos se van con él. Luego, los topes por dirección y total.
        await cn.ExecuteAsync("""
            DECLARE @idle TABLE (Id int PRIMARY KEY);
            INSERT @idle SELECT c.Id FROM meta.McpClient c
             WHERE c.CreatedAt < DATEADD(hour, -1, SYSUTCDATETIME()) AND NOT EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id);
            DELETE meta.McpCode WHERE ClientId IN (SELECT Id FROM @idle);
            DELETE meta.McpClient WHERE Id IN (SELECT Id FROM @idle);
            """, transaction: tx);
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (await cn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM meta.McpClient c WITH (UPDLOCK, HOLDLOCK)
                 WHERE (c.CreatedIp = @ip OR (@ip IS NULL AND c.CreatedIp IS NULL)) AND NOT EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id)
                """, new { ip }, tx) >= options.PendingClientsPerAddress)
            return Results.Json(new { error = "temporarily_unavailable", error_description = "Demasiados clientes sin usar desde esta dirección: inténtalo en una hora." }, statusCode: 429);
        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WITH (UPDLOCK, HOLDLOCK)", transaction: tx) >= options.MaxClients)
        {
            log.LogWarning("Registro de cliente MCP rechazado: se alcanzó Mcp:MaxClients ({Max})", options.MaxClients);
            return Results.Json(new { error = "temporarily_unavailable", error_description = "No se admiten más clientes por ahora." }, statusCode: 503);
        }
        await cn.ExecuteAsync("""
            INSERT meta.McpClient (ClientId, Name, RedirectUris, CreatedIp) VALUES (@clientId, @name, @uris, @ip)
            """, new { clientId, name, uris = JsonSerializer.Serialize(uniqueUris), ip }, tx);
        await tx.CommitAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            client_id = clientId,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            client_name = name,
            redirect_uris = uniqueUris,
            grant_types = grants,
            response_types = responses,
            token_endpoint_auth_method = "none",
        }, statusCode: 201);
    }

    // ---------- Autorizar (RFC 6749 §4.1, RFC 7636, RFC 8707, RFC 9207) ----------

    private sealed record ClientRow(int Id, string ClientId, string Name, string RedirectUris);

    /// <summary>Lo que la pantalla de consentimiento muestra, o el error (al cliente, o a la persona si no se puede redirigir).</summary>
    private sealed record Authorization(ClientRow? Client, string? RedirectUri, string? State, string[] Scopes, string? Challenge, string Resource,
        string? NoRedirect, string? Error, string? ErrorDescription);

    private async Task<Authorization> ParseAsync(HttpContext http, string query, CancellationToken ct)
    {
        var resourceUrl = options.Resource(http);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> q;
        try { q = QueryHelpers.ParseQuery(query.Length > 4096 ? throw new FormatException() : query); }
        catch (FormatException) { return new(null, null, null, [], null, resourceUrl, "La petición de autorización no es válida.", null, null); }
        string? One(string key) => q.TryGetValue(key, out var v) && v.Count == 1 ? v.ToString() : null;
        bool Repeated(string key) => q.TryGetValue(key, out var v) && v.Count > 1;

        // Antes de validar el cliente y el retorno, ningún error se redirige (§4.1.2.1): podría ser un retorno abierto.
        var clientId = One("client_id");
        if (Repeated("client_id") || Repeated("redirect_uri") || !McpFormat.IsClientId(clientId))
            return new(null, null, null, [], null, resourceUrl, "El cliente que pide acceso no está registrado en este ERP.", null, null);
        await using var cn = await db.OpenAsync(ct);
        var client = await cn.QuerySingleOrDefaultAsync<ClientRow>("SELECT Id, ClientId, Name, RedirectUris FROM meta.McpClient WHERE ClientId = @clientId", new { clientId });
        if (client is null) return new(null, null, null, [], null, resourceUrl, "El cliente que pide acceso no está registrado en este ERP.", null, null);
        var redirect = One("redirect_uri");
        var registered = JsonSerializer.Deserialize<string[]>(client.RedirectUris) ?? [];
        if (redirect is null || !registered.Contains(redirect, StringComparer.Ordinal))
            return new(client, null, null, [], null, resourceUrl, "La dirección de retorno no es una de las que registró el cliente: no se le enviará nada.", null, null);

        var state = One("state");
        Authorization Fail(string error, string description) => new(client, redirect, state is { Length: <= MaxState } ? state : null, [], null, resourceUrl, null, error, description);
        if (q.Any(kv => kv.Value.Count > 1)) return Fail("invalid_request", "Un parámetro viene repetido.");
        if (state is { Length: > MaxState }) return Fail("invalid_request", $"state admite hasta {MaxState} caracteres.");
        if (One("response_type") != "code") return Fail("unsupported_response_type", "Solo se admite response_type=code.");
        var challenge = One("code_challenge");
        if (One("code_challenge_method") != "S256" || !McpFormat.IsChallenge(challenge))
            return Fail("invalid_request", "PKCE es obligatorio: code_challenge con code_challenge_method=S256.");
        if (McpScopes.Parse(One("scope")) is not { } scopes) return Fail("invalid_scope", "Los alcances admitidos son modelo y datos.");
        if (One("resource") is { } resource && !SameResource(resource, resourceUrl))
            return Fail("invalid_target", "El recurso pedido no es este servidor MCP.");
        return new(client, redirect, state, scopes, challenge, resourceUrl, null, null, null);
    }

    private static bool SameResource(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private string Redirect(HttpContext http, string uri, string? state, params (string Key, string? Value)[] pairs)
    {
        var all = pairs.Append(("state", state)).Append(("iss", options.Origin(http))).Where(p => p.Item2 is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2!)}");
        return uri + (uri.Contains('?') ? "&" : "?") + string.Join("&", all);
    }

    /// <summary>
    /// Revisión adversarial F-03: el error va al cliente, pero la pantalla no redirige sola (sería un redirector abierto con
    /// cualquier cliente registrado): muestra el motivo y el sitio, y la persona decide volver.
    /// </summary>
    private object ErrorRedirect(HttpContext http, Authorization a) => new
    {
        redirect = Redirect(http, a.RedirectUri!, a.State, ("error", a.Error), ("error_description", a.ErrorDescription)),
        error = a.Error, errorDescription = a.ErrorDescription, redirectHost = RedirectUris.Host(a.RedirectUri!),
    };

    /// <summary><c>GET /api/mcp/authorize</c>: lo que verá la persona (H2 CA1), o el error (H2 CA2).</summary>
    public async Task<object> CheckAsync(HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        var u = http.CurrentUser();
        var a = await ParseAsync(http, http.Request.QueryString.Value?.TrimStart('?') ?? "", ct);
        if (a.NoRedirect is { } message) throw new AppException(400, message);
        if (a.Error is not null) return ErrorRedirect(http, a);
        return new
        {
            client = new { name = a.Client!.Name, redirectHost = RedirectUris.Host(a.RedirectUri!), trusted = RedirectUris.Trusted(a.RedirectUri!, options.TrustedRedirectHosts) },
            user = new { u.Username, u.DisplayName },
            scopes = McpScopes.All.Select(s => new { name = s, required = s == McpScopes.Model, requested = a.Scopes.Contains(s) }),
        };
    }

    /// <summary><c>POST /api/mcp/authorize</c>: la decisión de la persona (H2 CA3). Devuelve a dónde ir.</summary>
    public async Task<object> DecideAsync(McpAuthorizeRequest req, HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        var u = http.CurrentUser();
        var a = await ParseAsync(http, (req.Query ?? "").TrimStart('?'), ct);
        if (a.NoRedirect is { } message) throw new AppException(400, message);
        if (a.Error is not null) return ErrorRedirect(http, a);
        if (!req.Approve) return new { redirect = Redirect(http, a.RedirectUri!, a.State, ("error", "access_denied"), ("error_description", "La persona no autorizó el acceso.")) };
        // Lo que la persona marcó, dentro de lo pedido: nunca más de lo que el cliente pidió.
        var chosen = McpScopes.Normalize((req.Scopes ?? []).Where(a.Scopes.Contains).Append(McpScopes.Model));
        var code = McpFormat.New(McpFormat.CodePrefix);
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("""
            INSERT meta.McpCode (CodeHash, ClientId, UserId, SecurityStamp, Scopes, RedirectUri, Challenge, Resource, ExpiresAt)
            VALUES (@hash, @client, @user, @stamp, @scopes, @redirect, @challenge, @resource, DATEADD(second, @seconds, SYSUTCDATETIME()));
            DELETE meta.McpCode WHERE ExpiresAt < DATEADD(day, -1, SYSUTCDATETIME()) AND GrantId IS NULL;
            """, new
        {
            hash = McpFormat.Hash(code), client = a.Client!.Id, user = u.Id, stamp = u.SecurityStamp, scopes = McpScopes.Join(chosen),
            redirect = a.RedirectUri, challenge = a.Challenge, resource = a.Resource, seconds = options.CodeSeconds,
        });
        return new { redirect = Redirect(http, a.RedirectUri!, a.State, ("code", code)) };
    }

    // ---------- Tokens ----------

    private sealed record CodeRow(long Id, int ClientId, int UserId, Guid SecurityStamp, string Scopes, string RedirectUri, string Challenge,
        string Resource, DateTime? UsedAt, int? GrantId, DateTime ExpiresAt);

    private sealed record GrantRow(int Id, int ClientId, int UserId, Guid SecurityStamp, string Scopes, string Resource, DateTime? RevokedAt);

    private static IResult Error(HttpContext http, int status, string error, string description) => OAuthTokenService.Error(http, status, error, description);

    /// <summary><c>POST /oauth/token</c> con <c>authorization_code</c> o <c>refresh_token</c> (el formulario ya leído y sin repetidos).</summary>
    public async Task<IResult> TokenAsync(HttpContext http, IFormCollection form, CancellationToken ct)
    {
        if (!options.Enabled) return Error(http, 400, "unsupported_grant_type", "Solo se admite grant_type=client_credentials.");
        if (http.Request.Headers.Authorization.Count > 0 || form.ContainsKey("client_secret"))
            return Error(http, 401, "invalid_client", "Los clientes MCP son públicos: no llevan secreto (usan PKCE).");
        var clientId = form["client_id"].ToString();
        if (!McpFormat.IsClientId(clientId)) return Error(http, 401, "invalid_client", "No se pudo autenticar el cliente.");
        await using var cn = await db.OpenAsync(ct);
        var client = await cn.QuerySingleOrDefaultAsync<int?>("SELECT Id FROM meta.McpClient WHERE ClientId = @clientId", new { clientId });
        if (client is null) return Error(http, 401, "invalid_client", "No se pudo autenticar el cliente.");
        if (form["resource"].ToString() is { Length: > 0 } resource && !SameResource(resource, options.Resource(http)))
            return Error(http, 400, "invalid_target", "El recurso pedido no es este servidor MCP.");
        return form["grant_type"].ToString() == "authorization_code"
            ? await ExchangeAsync(http, cn, client.Value, form, ct)
            : await RefreshAsync(http, cn, client.Value, form, ct);
    }

    private async Task<IResult> ExchangeAsync(HttpContext http, DbConnection cn, int client, IFormCollection form, CancellationToken ct)
    {
        var code = form["code"].ToString();
        var verifier = form["code_verifier"].ToString();
        var redirect = form["redirect_uri"].ToString();
        if (!McpFormat.Is(McpFormat.CodePrefix, code)) return Error(http, 400, "invalid_grant", "El código no es válido o ya venció.");
        if (verifier.Length == 0 || redirect.Length == 0) return Error(http, 400, "invalid_request", "Faltan code_verifier o redirect_uri.");
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Un canje por código (H3 CA2): la marca y la emisión van en la misma transacción.
        var row = await cn.QuerySingleOrDefaultAsync<CodeRow>("""
            UPDATE meta.McpCode SET UsedAt = COALESCE(UsedAt, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.ClientId, inserted.UserId, inserted.SecurityStamp, inserted.Scopes, inserted.RedirectUri, inserted.Challenge,
                   inserted.Resource, deleted.UsedAt, inserted.GrantId, inserted.ExpiresAt
             WHERE CodeHash = @hash
            """, new { hash = McpFormat.Hash(code) }, tx);
        if (row is null) { await tx.RollbackAsync(ct); return Error(http, 400, "invalid_grant", "El código no es válido o ya venció."); }
        if (row.UsedAt is not null)
        {
            // RFC 6749 §4.1.2: un código reusado revoca lo que emitió.
            if (row.GrantId is { } grant) await RevokeGrantAsync(cn, tx, http, grant, "reuse", Trail.Reuse, new { kind = "code" });
            await tx.CommitAsync(ct);
            return Error(http, 400, "invalid_grant", "El código ya se usó: la conexión que creó se revocó.");
        }
        // Un código que no cuadra queda gastado (se marcó arriba): quien lo robó no puede seguir probando verificadores.
        if (row.ClientId != client || row.ExpiresAt < DateTime.UtcNow || !string.Equals(row.RedirectUri, redirect, StringComparison.Ordinal)
            || !McpFormat.VerifyS256(verifier, row.Challenge))
        {
            await tx.CommitAsync(ct);
            return Error(http, 400, "invalid_grant", "El código no es válido o ya venció.");
        }
        var user = await loader.LoadAsync(row.UserId, row.SecurityStamp, ct);
        if (user is null || user.IsService)
        {
            await tx.CommitAsync(ct);
            return Error(http, 400, "invalid_grant", "La cuenta que autorizó cambió: vuelve a conectar.");
        }
        // Una conexión por persona y cliente (CLARIFY 5): la anterior se revoca.
        foreach (var previous in await cn.QueryAsync<int>(
                     // Revisión adversarial F-06: HOLDLOCK bloquea también el rango vacío; otro canje de la misma persona y cliente espera.
                     "SELECT Id FROM meta.McpGrant WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId AND ClientId = @client AND RevokedAt IS NULL", new { row.UserId, client }, tx))
            await RevokeGrantAsync(cn, tx, http, previous, "replaced", Trail.Revoke, new { reason = "replaced" }, user);
        var grantId = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.McpGrant (ClientId, UserId, SecurityStamp, Scopes, Resource) OUTPUT inserted.Id VALUES (@client, @UserId, @SecurityStamp, @Scopes, @Resource);
            """, new { client, row.UserId, row.SecurityStamp, row.Scopes, row.Resource }, tx);
        await cn.ExecuteAsync("""
            UPDATE meta.McpCode SET GrantId = @grantId WHERE Id = @Id;
            UPDATE meta.McpClient SET LastAuthorizedAt = SYSUTCDATETIME() WHERE Id = @client;
            """, new { grantId, row.Id, client }, tx);
        var tokens = await IssueAsync(cn, tx, grantId, ct);
        var clientName = await cn.ExecuteScalarAsync<string>("SELECT Name FROM meta.McpClient WHERE Id = @client", new { client }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.McpConnect, "McpGrant", grantId.ToString(),
            after: new { client = clientName, scopes = row.Scopes.Split(' ') }, userId: user.Id, username: user.Username);
        await tx.CommitAsync(ct);
        return Tokens(http, tokens, row.Scopes);
    }

    private async Task<IResult> RefreshAsync(HttpContext http, DbConnection cn, int client, IFormCollection form, CancellationToken ct)
    {
        var refresh = form["refresh_token"].ToString();
        if (!McpFormat.Is(McpFormat.RefreshPrefix, refresh)) return Error(http, 400, "invalid_grant", "El refresh token no es válido o ya venció.");
        await using var tx = await cn.BeginTransactionAsync(ct);
        var hash = McpFormat.Hash(refresh);
        var used = await cn.QuerySingleOrDefaultAsync<(int GrantId, DateTime? UsedAt, DateTime ExpiresAt)?>("""
            UPDATE meta.McpToken SET UsedAt = COALESCE(UsedAt, SYSUTCDATETIME()) OUTPUT inserted.GrantId, deleted.UsedAt, inserted.ExpiresAt
             WHERE TokenHash = @hash AND Kind = 'r'
            """, new { hash }, tx);
        if (used is null) { await tx.RollbackAsync(ct); return Error(http, 400, "invalid_grant", "El refresh token no es válido o ya venció."); }
        var grant = await cn.QuerySingleAsync<GrantRow>(
            "SELECT Id, ClientId, UserId, SecurityStamp, Scopes, Resource, RevokedAt FROM meta.McpGrant WITH (UPDLOCK) WHERE Id = @GrantId", new { used.Value.GrantId }, tx);
        if (grant.ClientId != client) { await tx.RollbackAsync(ct); return Error(http, 400, "invalid_grant", "El refresh token no es válido o ya venció."); }
        if (used.Value.UsedAt is not null)
        {
            // Detección de reuso (OAuth 2.1 §4.3.1): un refresh ya rotado vuelve; alguien más lo tiene. Se revoca la conexión.
            if (grant.RevokedAt is null) await RevokeGrantAsync(cn, tx, http, grant.Id, "reuse", Trail.Reuse, new { kind = "refresh" });
            await tx.CommitAsync(ct);
            return Error(http, 400, "invalid_grant", "El refresh token ya se usó: la conexión se revocó por seguridad.");
        }
        if (grant.RevokedAt is not null || used.Value.ExpiresAt < DateTime.UtcNow)
        {
            await tx.RollbackAsync(ct);
            return Error(http, 400, "invalid_grant", "El refresh token no es válido o ya venció.");
        }
        var user = await loader.LoadAsync(grant.UserId, grant.SecurityStamp, ct);
        if (user is null || user.IsService)
        {
            await RevokeGrantAsync(cn, tx, http, grant.Id, "stamp", Trail.None, null);
            await tx.CommitAsync(ct);
            return Error(http, 400, "invalid_grant", "La cuenta cambió (contraseña, roles o estado): vuelve a conectar.");
        }
        var tokens = await IssueAsync(cn, tx, grant.Id, ct);
        await tx.CommitAsync(ct);
        return Tokens(http, tokens, grant.Scopes);
    }

    private async Task<(string Access, string Refresh)> IssueAsync(DbConnection cn, DbTransaction tx, int grantId, CancellationToken ct)
    {
        var access = McpFormat.New(McpFormat.AccessPrefix);
        var refresh = McpFormat.New(McpFormat.RefreshPrefix);
        await cn.ExecuteAsync("""
            INSERT meta.McpToken (GrantId, Kind, TokenHash, ExpiresAt) VALUES
                (@grantId, 'a', @access, DATEADD(minute, @minutes, SYSUTCDATETIME())),
                (@grantId, 'r', @refresh, DATEADD(day, @days, SYSUTCDATETIME()));
            DELETE meta.McpToken WHERE GrantId = @grantId AND ExpiresAt < DATEADD(day, -1, SYSUTCDATETIME());
            """, new { grantId, access = McpFormat.Hash(access), refresh = McpFormat.Hash(refresh), minutes = options.AccessTokenMinutes, days = options.RefreshTokenDays }, tx);
        return (access, refresh);
    }

    private IResult Tokens(HttpContext http, (string Access, string Refresh) t, string scopes)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return Results.Json(new
        {
            access_token = t.Access, token_type = "Bearer", expires_in = options.AccessTokenMinutes * 60, refresh_token = t.Refresh,
            scope = scopes,
        });
    }

    private enum Trail { None, Revoke, Reuse }

    /// <param name="trail">Sin bitácora cuando la conexión ya no sirve porque cambió la cuenta (ese cambio ya está en la bitácora).</param>
    private static async Task RevokeGrantAsync(DbConnection cn, DbTransaction tx, HttpContext http, int grantId, string reason, Trail trail, object? detail,
        UserContext? by = null)
    {
        var owner = await cn.QuerySingleOrDefaultAsync<(int Id, string Username)?>("""
            UPDATE g SET RevokedAt = SYSUTCDATETIME(), RevokedReason = @reason OUTPUT u.Id, u.Username
              FROM meta.McpGrant g JOIN meta.[User] u ON u.Id = g.UserId WHERE g.Id = @grantId AND g.RevokedAt IS NULL
            """, new { grantId, reason }, tx);
        if (owner is null || trail == Trail.None) return;
        var (userId, username) = (by?.Id ?? owner.Value.Id, by?.Username ?? owner.Value.Username);
        if (trail == Trail.Reuse)
            await Audit.WriteAsync(cn, tx, http, AuditActions.McpReuse, "McpGrant", grantId.ToString(), after: detail ?? new { reason }, userId: userId, username: username);
        else
            await Audit.WriteAsync(cn, tx, http, AuditActions.McpRevoke, "McpGrant", grantId.ToString(), after: detail ?? new { reason }, userId: userId, username: username);
    }

    /// <summary><c>POST /oauth/revoke</c> (RFC 7009): siempre 200; revoca si el token es de ese cliente.</summary>
    public async Task<IResult> RevokeAsync(HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        // Revisión adversarial F-05: como /oauth/token, solo urlencoded y con sus límites.
        if (!http.Request.HasFormContentType || http.Request.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return Error(http, 400, "invalid_request", "La petición debe ser un formulario (application/x-www-form-urlencoded).");
        if (http.Request.ContentLength > OAuthTokenService.MaxBody) return Error(http, 400, "invalid_request", "La petición es demasiado grande.");
        IFormCollection form;
        try
        {
            http.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(http.Request, OAuthTokenService.Limits));
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Error(http, 400, "invalid_request", "La petición no tiene un formato válido.");
        }
        var token = form["token"].ToString();
        if (token.Length == 0) return Error(http, 400, "invalid_request", "Falta token.");
        var clientId = form["client_id"].ToString();
        if ((McpFormat.Is(McpFormat.AccessPrefix, token) || McpFormat.Is(McpFormat.RefreshPrefix, token)) && McpFormat.IsClientId(clientId))
        {
            await using var cn = await db.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            var grant = await cn.ExecuteScalarAsync<int?>("""
                SELECT t.GrantId FROM meta.McpToken t JOIN meta.McpGrant g ON g.Id = t.GrantId JOIN meta.McpClient c ON c.Id = g.ClientId
                 WHERE t.TokenHash = @hash AND c.ClientId = @clientId
                """, new { hash = McpFormat.Hash(token), clientId }, tx);
            if (grant is { } id) await RevokeGrantAsync(cn, tx, http, id, "client", Trail.Revoke, new { reason = "client" });
            await tx.CommitAsync(ct);
        }
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok();
    }

    // ---------- Conexiones de la persona (H6) ----------

    private sealed record ConnectionRow(int Id, string Client, string RedirectUris, string Scopes, DateTime CreatedAt, DateTime? LastUsedAt);

    public async Task<McpConnectionsDto> ConnectionsAsync(HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        var u = http.CurrentUser();
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<ConnectionRow>("""
            SELECT g.Id, c.Name AS Client, c.RedirectUris, g.Scopes, g.CreatedAt, g.LastUsedAt
              FROM meta.McpGrant g JOIN meta.McpClient c ON c.Id = g.ClientId
             WHERE g.UserId = @Id AND g.RevokedAt IS NULL
               AND EXISTS (SELECT 1 FROM meta.McpToken t WHERE t.GrantId = g.Id AND t.Kind = 'r' AND t.UsedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME())
             ORDER BY g.CreatedAt DESC
            """, new { u.Id });
        static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return new(options.Resource(http), rows.Select(r => new McpConnectionDto(r.Id, r.Client,
            string.Join(", ", (JsonSerializer.Deserialize<string[]>(r.RedirectUris) ?? []).Select(RedirectUris.Host).Distinct()),
            McpScopes.Normalize(r.Scopes.Split(' ')), Utc(r.CreatedAt), r.LastUsedAt is { } l ? Utc(l) : null)).ToList());
    }

    public async Task RevokeConnectionAsync(int id, HttpContext http, CancellationToken ct)
    {
        RequireEnabled();
        var u = http.CurrentUser();
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Solo las propias: la de otra persona es 404, como si no existiera (H6 CA2).
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.McpGrant WITH (UPDLOCK) WHERE Id = @id AND UserId = @uid AND RevokedAt IS NULL", new { id, uid = u.Id }, tx) is null)
            throw new NotFound("La conexión no existe o ya estaba desconectada.");
        await RevokeGrantAsync(cn, tx, http, id, "person", Trail.Revoke, new { reason = "person" }, u);
        await tx.CommitAsync(ct);
    }
}
