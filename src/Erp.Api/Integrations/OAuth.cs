using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Erp.Api.Integrations;

/// <summary>Spec 018: alcances de un cliente OAuth. Limitan superficies; dentro de ellas manda el rol.</summary>
public static class OAuthScopes
{
    public const string ODataRead = "odata.read";
    public const string DataRead = "data.read";
    public const string DataWrite = "data.write";
    public const string Claim = "scope";

    /// <summary>En este orden se guardan y se responden.</summary>
    public static readonly IReadOnlyList<string> All = [ODataRead, DataRead, DataWrite];

    /// <summary>Lista separada por espacios (RFC 6749 §3.3); null si trae un alcance desconocido o está vacía.</summary>
    public static string[]? Parse(string? value)
    {
        var parts = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => !All.Contains(p, StringComparer.Ordinal))) return null;
        return Normalize(parts);
    }

    public static string[] Normalize(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.Ordinal);
        return All.Where(set.Contains).ToArray();
    }

    public static string Join(IEnumerable<string> scopes) => string.Join(' ', Normalize(scopes));

    /// <summary>
    /// Alcance que exige la ruta para un token: <c>""</c> = cualquier token; null = ningún token llega ahí. Solo lectura
    /// por <c>/odata</c>; la API de datos separa lectura (incluido <c>/compute</c>, que no escribe) de escritura.
    /// </summary>
    public static string? Required(PathString path, string method)
    {
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (path.StartsWithSegments("/api/auth/me")) return read ? "" : null;
        if (path.StartsWithSegments("/odata")) return ODataRead; // otro método responde 405 en el propio endpoint
        if (path.StartsWithSegments("/api/reports")) return read ? DataRead : null;
        if (path.StartsWithSegments("/api/data", out var rest))
        {
            if (read) return DataRead;
            var compute = HttpMethods.IsPost(method) && rest.Value is { } r && r.EndsWith("/compute", StringComparison.OrdinalIgnoreCase)
                          && r.Count(c => c == '/') == 2;
            return compute ? DataRead : DataWrite;
        }
        return null;
    }
}

/// <summary>Spec 018: formato de client_id, secreto y token. 256 bits aleatorios; en la base, solo su SHA-256.</summary>
public static partial class OAuthFormat
{
    public const string ClientPrefix = "erpcl_";
    public const string SecretPrefix = "erpcs_";
    public const string TokenPrefix = "erpat_";

    [GeneratedRegex("^erpcl_[0-9a-f]{14}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientPattern();

    [GeneratedRegex("^erpcs_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("^erpat_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static string NewClientId() => ClientPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(7));
    public static string NewSecret() => SecretPrefix + WebhookSignature.Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string NewToken() => TokenPrefix + WebhookSignature.Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool IsClientId(string? s) => s is not null && ClientPattern().IsMatch(s);
    public static bool IsSecret(string? s) => s is not null && SecretPattern().IsMatch(s);
    public static bool IsToken(string? s) => s is not null && TokenPattern().IsMatch(s);

    /// <summary>¿La cabecera trae un token OAuth (y no una clave de API)?</summary>
    public static bool IsBearerToken(HttpRequest r) =>
        r.Headers.Authorization.ToString() is var h && h.StartsWith("Bearer " + TokenPrefix, StringComparison.OrdinalIgnoreCase);

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool Matches(string value, byte[]? hash) =>
        hash is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Hash(value), hash);
}

/// <summary>
/// Spec 018: autenticación con <c>Authorization: Bearer erpat_…</c>. Token vigente, cliente sin revocar y cuenta de servicio
/// activa; los alcances efectivos son los del token que el cliente conserva hoy (quitar un alcance aplica ya).
/// </summary>
public sealed class OAuthTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    Db db, UserContextLoader loader) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OAuth";

    private sealed record TokenRow(string TokenScopes, string ClientScopes, int UserId);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var fail = AuthenticateResult.Fail("Token no válido.");
        if (!OAuthFormat.IsToken(token)) return fail;
        await using var cn = await db.OpenAsync(Context.RequestAborted);
        // Búsqueda por el hash (índice único): el tiempo no depende de cuánto del token sea correcto.
        var row = await cn.QuerySingleOrDefaultAsync<TokenRow>("""
            SELECT t.Scopes AS TokenScopes, c.Scopes AS ClientScopes, c.UserId
              FROM meta.OAuthToken t JOIN meta.OAuthClient c ON c.Id = t.ClientId
             WHERE t.TokenHash = @hash AND t.RevokedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME() AND c.RevokedAt IS NULL
            """, new { hash = OAuthFormat.Hash(token) });
        if (row is null) return fail;
        var scopes = OAuthScopes.Normalize(row.TokenScopes.Split(' ').Intersect(row.ClientScopes.Split(' '), StringComparer.Ordinal));
        var user = await loader.LoadServiceAsync(row.UserId, Context.RequestAborted);
        if (user is null) return fail;
        Context.Items[UserContextExtensions.ItemKey] = user;
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username),
            new Claim(OAuthScopes.Claim, string.Join(' ', scopes)),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }
}

public sealed record OAuthClientDto(int Id, string Name, string ClientId, IReadOnlyList<int> RoleIds, string Username, IReadOnlyList<string> Scopes,
    int TokenMinutes, DateTime CreatedAt, string CreatedBy, DateTime? RotatedAt, DateTime? PreviousExpiresAt, DateTime? RevokedAt,
    DateTime? LastTokenAt, int ActiveTokens, string Status);
public sealed record SaveOAuthClientRequest(string? Name, int[]? RoleIds, string[]? Scopes, int? TokenMinutes);
public sealed record RotateOAuthClientRequest(int GraceHours);

/// <summary>Spec 018: alta, cambio, rotación y revocación de clientes OAuth (solo superusuario; auditado sin el secreto).</summary>
public sealed class OAuthClientService(Db db, PasswordService passwords, SecurityApproval gate)
{
    public const int MinTokenMinutes = 5;
    public const int MaxTokenMinutes = 60;
    public const int DefaultTokenMinutes = 15;

    private static void RequireAdmin(UserContext u)
    {
        if (!u.IsSuperuser) throw new Forbidden("Solo un administrador puede administrar clientes OAuth.");
    }

    private sealed record Row(int Id, string Name, string ClientId, string Username, string Scopes, int TokenMinutes, DateTime CreatedAt,
        string CreatedBy, DateTime? RotatedAt, DateTime? PreviousExpiresAt, DateTime? RevokedAt, DateTime? LastTokenAt, int ActiveTokens, int UserId);

    public async Task<IReadOnlyList<OAuthClientDto>> ListAsync(UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        return await LoadAsync(cn, null, null);
    }

    private static async Task<List<OAuthClientDto>> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx, int? id)
    {
        var rows = await cn.QueryAsync<Row>("""
            SELECT c.Id, c.Name, c.ClientId, u.Username, c.Scopes, c.TokenMinutes, c.CreatedAt, b.DisplayName AS CreatedBy, c.RotatedAt,
                   c.PreviousExpiresAt, c.RevokedAt, c.LastTokenAt,
                   (SELECT COUNT(*) FROM meta.OAuthToken t WHERE t.ClientId = c.Id AND t.RevokedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME()) AS ActiveTokens,
                   c.UserId
              FROM meta.OAuthClient c JOIN meta.[User] u ON u.Id = c.UserId JOIN meta.[User] b ON b.Id = c.CreatedBy
             WHERE @id IS NULL OR c.Id = @id ORDER BY c.Id DESC
            """, new { id }, tx);
        var roles = (await cn.QueryAsync<(int UserId, int RoleId)>(
            "SELECT ur.UserId, ur.RoleId FROM meta.UserRole ur JOIN meta.OAuthClient c ON c.UserId = ur.UserId", transaction: tx)).ToLookup(x => x.UserId, x => x.RoleId);
        var now = DateTime.UtcNow;
        static DateTime? U(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
        return rows.Select(r => new OAuthClientDto(r.Id, r.Name, r.ClientId.Trim(), roles[r.UserId].Order().ToList(), r.Username,
            OAuthScopes.Normalize(r.Scopes.Split(' ')), r.TokenMinutes, DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc), r.CreatedBy,
            U(r.RotatedAt), r.PreviousExpiresAt > now ? U(r.PreviousExpiresAt) : null, U(r.RevokedAt), U(r.LastTokenAt),
            r.RevokedAt is null ? r.ActiveTokens : 0, r.RevokedAt is not null ? "revoked" : "active")).ToList();
    }

    private static (string? Name, string[] Scopes, int Minutes) Valid(SaveOAuthClientRequest req, Dictionary<string, string[]> errors)
    {
        var name = ApiKeyService.ValidName(req.Name, errors);
        // Regresión (revisión exploratoria 018, F-01): sin roles se avisa junto con los demás errores, no en una segunda vuelta.
        if (req.RoleIds is not { Length: > 0 }) errors["roleIds"] = ["Elige al menos un rol: define qué puede hacer el servicio."];
        var scopes = req.Scopes is { Length: > 0 } s && s.All(x => OAuthScopes.All.Contains(x, StringComparer.Ordinal)) ? OAuthScopes.Normalize(s) : null;
        if (scopes is null) errors["scopes"] = ["Elige al menos un alcance válido (odata.read, data.read o data.write)."];
        var minutes = req.TokenMinutes ?? DefaultTokenMinutes;
        if (minutes is < MinTokenMinutes or > MaxTokenMinutes) errors["tokenMinutes"] = [$"La vida del token va de {MinTokenMinutes} a {MaxTokenMinutes} minutos."];
        return (name, scopes ?? [], minutes);
    }

    /// <summary>El cliente ya generado, como se guarda si espera aprobación: solo el SHA-256 del secreto y la empresa de quien lo pidió.</summary>
    public sealed record OAuthClientPayload(string Name, int[] RoleIds, string[] Scopes, int TokenMinutes, string ClientId, string SecretHash, int? CompanyId);

    public sealed record OAuthClientSnapshot(IReadOnlyList<int> RoleIds, IReadOnlyList<string> Scopes, string Status);

    public async Task<IResult> CreateAsync(SaveOAuthClientRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var errors = new Dictionary<string, string[]>();
        var (name, scopes, minutes) = Valid(req, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);
        var (clientId, secret) = (OAuthFormat.NewClientId(), OAuthFormat.NewSecret());
        var payload = new OAuthClientPayload(name!, (req.RoleIds ?? []).Distinct().ToArray(), scopes, minutes, clientId,
            Convert.ToBase64String(OAuthFormat.Hash(secret)), u.Company?.Id);
        return await CreateCoreAsync(payload, secret, http, null, ct);
    }

    internal async Task<IResult> CreateCoreAsync(OAuthClientPayload p, string? secret, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var roleIds = await ApiKeyService.ValidRolesAsync(cn, tx, p.RoleIds, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);

        // Spec 015 (H4): un cliente nuevo es una credencial persistente; espera aprobación. El secreto solo lo recibe quien
        // lo pidió y el cliente no existe (no emite tokens) hasta que se aprueba.
        if (gate.MustWait(approving, expands: true))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.OAuthClientCreate, null, $"Alta del cliente OAuth {p.Name}", p, null);
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change, secret);
        }

        // Cuenta de servicio, como la de una clave de API (008): no inicia sesión y su contraseña aleatoria nunca se conoce.
        var userId = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.[User] (Username, DisplayName, PasswordHash, IsService) OUTPUT inserted.Id VALUES (@username, @name, @hash, 1)
            """, new { username = $"svc.{p.ClientId}", name = p.Name[..Math.Min(p.Name.Length, 150)], hash = passwords.Hash(WebhookSignature.NewSecret()) }, tx);
        foreach (var r in roleIds)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @r)", new { userId, r }, tx);
        // Spec 010: el cliente trabaja en la empresa actual de quien lo crea (o de quien lo solicitó).
        if (p.CompanyId is { } company && await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.Company WHERE Id = @company", new { company }, tx) is not null)
            await cn.ExecuteAsync("""
                INSERT meta.UserCompany (UserId, CompanyId) VALUES (@userId, @company);
                UPDATE meta.[User] SET CompanyId = @company WHERE Id = @userId;
                """, new { userId, company }, tx);
        var scopes = OAuthScopes.Join(p.Scopes);
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.OAuthClient (Name, ClientId, SecretHash, Scopes, TokenMinutes, UserId, CreatedBy)
            OUTPUT inserted.Id VALUES (@Name, @ClientId, @hash, @scopes, @TokenMinutes, @userId, @uid)
            """, new { p.Name, p.ClientId, hash = Convert.FromBase64String(p.SecretHash), scopes, p.TokenMinutes, userId, uid = u.Id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.OAuthClientChange, "OAuthClient", id.ToString(),
            after: new { action = "create", p.Name, p.ClientId, roleIds, scopes = p.Scopes, p.TokenMinutes, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return Results.Ok(secret is null ? new { client = dto } : new { client = dto, secret });
    }

    public Task<IResult> UpdateAsync(int id, SaveOAuthClientRequest req, HttpContext http, CancellationToken ct) => UpdateCoreAsync(id, req, http, null, ct);

    internal async Task<IResult> UpdateCoreAsync(int id, SaveOAuthClientRequest req, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var before = (await LoadAsync(cn, tx, id)).SingleOrDefault() ?? throw new NotFound("El cliente no existe.");
        var snapshot = new OAuthClientSnapshot(before.RoleIds, before.Scopes, before.Status);
        approving?.Verify(snapshot);
        if (before.Status == "revoked") throw new Conflict("El cliente está revocado.");
        var errors = new Dictionary<string, string[]>();
        var (name, scopes, minutes) = Valid(req, errors);
        var roleIds = await ApiKeyService.ValidRolesAsync(cn, tx, req.RoleIds, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);
        // Spec 015 (H4): ganar roles o alcances espera aprobación; quitarlos, renombrar o cambiar la vida del token, no.
        if (gate.MustWait(approving, SecurityGrants.OAuthClient(before.RoleIds, before.Scopes, roleIds, scopes)))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.OAuthClientUpdate, id, $"Roles y alcances del cliente OAuth {before.Name}",
                new SaveOAuthClientRequest(name, roleIds.Order().ToArray(), scopes, minutes), snapshot);
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change);
        }
        var userId = await cn.ExecuteScalarAsync<int>("""
            UPDATE meta.OAuthClient SET Name = @name, Scopes = @scopes, TokenMinutes = @minutes OUTPUT inserted.UserId WHERE Id = @id
            """, new { id, name, scopes = OAuthScopes.Join(scopes), minutes }, tx);
        await cn.ExecuteAsync("UPDATE meta.[User] SET DisplayName = @name WHERE Id = @userId", new { userId, name = name![..Math.Min(name!.Length, 150)] }, tx);
        await cn.ExecuteAsync("DELETE meta.UserRole WHERE UserId = @userId", new { userId }, tx);
        foreach (var r in roleIds)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @r)", new { userId, r }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.OAuthClientChange, "OAuthClient", id.ToString(),
            before: new { before.Name, before.RoleIds, before.Scopes, before.TokenMinutes },
            after: new { action = "update", name, roleIds, scopes, tokenMinutes = minutes, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return Results.Ok(dto);
    }

    /// <summary>Secreto nuevo. Sin gracia, el anterior y los tokens emitidos dejan de servir ya (se supone filtrado).</summary>
    public async Task<object> RotateAsync(int id, RotateOAuthClientRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        if (req.GraceHours is not (0 or 24)) throw new ValidationFailed(new Dictionary<string, string[]> { ["graceHours"] = ["El periodo de gracia es 0 o 24 horas."] });
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var current = (await LoadAsync(cn, tx, id)).SingleOrDefault() ?? throw new NotFound("El cliente no existe.");
        if (current.Status != "active") throw new Conflict("Solo se rota el secreto de un cliente vigente.");
        var secret = OAuthFormat.NewSecret();
        var revoked = await cn.ExecuteScalarAsync<int>("""
            UPDATE meta.OAuthClient SET PreviousSecretHash = CASE WHEN @grace > 0 THEN SecretHash END,
                   PreviousExpiresAt = CASE WHEN @grace > 0 THEN DATEADD(hour, @grace, SYSUTCDATETIME()) END,
                   SecretHash = @hash, RotatedAt = SYSUTCDATETIME()
             WHERE Id = @id;
            IF @grace = 0
                UPDATE meta.OAuthToken SET RevokedAt = SYSUTCDATETIME() WHERE ClientId = @id AND RevokedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();
            SELECT CASE WHEN @grace = 0 THEN @@ROWCOUNT ELSE 0 END;
            """, new { id, grace = req.GraceHours, hash = OAuthFormat.Hash(secret) }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.OAuthClientChange, "OAuthClient", id.ToString(),
            after: new { action = "rotate", req.GraceHours, revokedTokens = revoked });
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return new { client = dto, secret };
    }

    public async Task RevokeAsync(int id, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var userId = await cn.ExecuteScalarAsync<int?>("""
            UPDATE meta.OAuthClient SET RevokedAt = SYSUTCDATETIME(), PreviousSecretHash = NULL, PreviousExpiresAt = NULL
            OUTPUT inserted.UserId WHERE Id = @id AND RevokedAt IS NULL
            """, new { id }, tx) ?? throw new NotFound("El cliente no existe o ya estaba revocado.");
        await cn.ExecuteAsync("""
            UPDATE meta.OAuthToken SET RevokedAt = SYSUTCDATETIME() WHERE ClientId = @id AND RevokedAt IS NULL;
            UPDATE meta.[User] SET IsActive = 0, SecurityStamp = NEWID() WHERE Id = @userId;
            """, new { id, userId }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.OAuthClientChange, "OAuthClient", id.ToString(), after: new { action = "revoke" });
        await tx.CommitAsync(ct);
    }
}

/// <summary>
/// Spec 018 (H2): <c>POST /oauth/token</c> con <c>grant_type=client_credentials</c> (RFC 6749 §4.4). Errores en el formato de
/// §5.2; nunca dice si el cliente existe, está revocado o el secreto es otro.
/// </summary>
public sealed class OAuthTokenService(Db db, UserContextLoader loader, ILogger<OAuthTokenService> log)
{
    public const int MaxBody = 4096;

    internal static readonly FormOptions Limits = new()
    {
        BufferBodyLengthLimit = MaxBody, MultipartBodyLengthLimit = MaxBody, ValueCountLimit = 10, KeyLengthLimit = 64, ValueLengthLimit = 1024,
    };

    private sealed record ClientRow(int Id, int UserId, byte[] SecretHash, byte[]? PreviousSecretHash, DateTime? PreviousExpiresAt, string Scopes,
        int TokenMinutes, DateTime? RevokedAt);

    public static IResult Error(HttpContext http, int status, string error, string description)
    {
        NoStore(http);
        return Results.Json(new { error, error_description = description }, statusCode: status);
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }

    public async Task<IResult> IssueAsync(HttpContext http, CancellationToken ct)
    {
        var req = http.Request;
        if (!req.HasFormContentType || req.HasFormContentType && req.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return Error(http, 400, "invalid_request", "La petición debe ser un formulario (application/x-www-form-urlencoded).");
        if (req.ContentLength > MaxBody) return Error(http, 400, "invalid_request", "La petición es demasiado grande.");
        IFormCollection form;
        try
        {
            http.Features.Set<IFormFeature>(new FormFeature(req, Limits));
            form = await req.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Error(http, 400, "invalid_request", "La petición no tiene un formato válido.");
        }
        // RFC 6749 §3.2: un parámetro no se repite.
        if (form.Any(kv => kv.Value.Count > 1)) return Error(http, 400, "invalid_request", "Un parámetro viene repetido.");
        var grant = form["grant_type"].ToString();
        if (grant.Length == 0) return Error(http, 400, "invalid_request", "Falta grant_type.");
        // Spec 038: código de autorización y renovación son del servidor MCP (clientes públicos de personas, con PKCE).
        if (Mcp.McpOAuthService.GrantTypes.Contains(grant))
            return await http.RequestServices.GetRequiredService<Mcp.McpOAuthService>().TokenAsync(http, form, ct);
        if (grant != "client_credentials")
            return Error(http, 400, "unsupported_grant_type", "Se admiten client_credentials, authorization_code y refresh_token.");

        // Credenciales: Basic (preferida) o en el cuerpo; nunca las dos (§2.3.1).
        string? clientId, secret;
        var basic = req.Headers.Authorization.ToString();
        var usedBasic = basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
        if (usedBasic)
        {
            if (form.ContainsKey("client_secret")) return Error(http, 400, "invalid_request", "Las credenciales van en Authorization o en el cuerpo, no en ambos.");
            (clientId, secret) = ParseBasic(basic["Basic ".Length..].Trim());
            if (form.TryGetValue("client_id", out var bodyId) && bodyId.ToString() != clientId)
                return Error(http, 400, "invalid_request", "client_id no coincide con el de Authorization.");
        }
        else
        {
            clientId = form["client_id"].ToString();
            secret = form["client_secret"].ToString();
        }

        await using var cn = await db.OpenAsync(ct);
        var client = OAuthFormat.IsClientId(clientId) && OAuthFormat.IsSecret(secret)
            ? await cn.QuerySingleOrDefaultAsync<ClientRow>("""
                SELECT Id, UserId, SecretHash, PreviousSecretHash, PreviousExpiresAt, Scopes, TokenMinutes, RevokedAt
                  FROM meta.OAuthClient WHERE ClientId = @clientId
                """, new { clientId })
            : null;
        var now = DateTime.UtcNow;
        var service = client is { RevokedAt: null } &&
                      (OAuthFormat.Matches(secret!, client.SecretHash) || client.PreviousExpiresAt > now && OAuthFormat.Matches(secret!, client.PreviousSecretHash))
            ? await loader.LoadServiceAsync(client.UserId, ct) : null;
        if (service is null)
        {
            if (OAuthFormat.IsClientId(clientId)) log.LogWarning("Cliente OAuth rechazado ({ClientId})", clientId);
            if (usedBasic) http.Response.Headers.WWWAuthenticate = "Basic realm=\"erp\"";
            return Error(http, 401, "invalid_client", "No se pudo autenticar el cliente.");
        }

        var granted = client!.Scopes.Split(' ');
        string[] scopes;
        if (form.TryGetValue("scope", out var asked))
        {
            if (OAuthScopes.Parse(asked.ToString()) is not { } wanted || wanted.Except(granted, StringComparer.Ordinal).Any())
                return Error(http, 400, "invalid_scope", "El alcance pedido no es válido o el cliente no lo tiene.");
            scopes = wanted;
        }
        else scopes = OAuthScopes.Normalize(granted);

        var token = OAuthFormat.NewToken();
        var scope = string.Join(' ', scopes);
        await cn.ExecuteAsync("""
            INSERT meta.OAuthToken (ClientId, TokenHash, Scopes, ExpiresAt) VALUES (@Id, @hash, @scope, DATEADD(minute, @TokenMinutes, SYSUTCDATETIME()));
            UPDATE meta.OAuthClient SET LastTokenAt = SYSUTCDATETIME() WHERE Id = @Id;
            DELETE meta.OAuthToken WHERE ClientId = @Id AND ExpiresAt < DATEADD(day, -1, SYSUTCDATETIME());
            """, new { client.Id, hash = OAuthFormat.Hash(token), scope, client.TokenMinutes });
        // Spec 021 (H8 CA2): cada token emitido queda en la auditoría a nombre del cliente (nunca el token).
        await Audit.WriteAsync(cn, null, http, AuditActions.OAuthToken, "OAuthClient", client.Id.ToString(),
            after: new { clientId, scopes, expiresInMinutes = client.TokenMinutes }, userId: service.Id, username: service.Username);
        NoStore(http);
        return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = client.TokenMinutes * 60, scope });
    }

    /// <summary><c>base64(client_id:client_secret)</c>; (null, null) si no se puede leer.</summary>
    internal static (string? Id, string? Secret) ParseBasic(string value)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var i = text.IndexOf(':');
            // RFC 6749 §2.3.1: id y secreto van codificados como formulario antes de Basic.
            return i <= 0 ? (null, null) : (Uri.UnescapeDataString(text[..i].Replace('+', ' ')), Uri.UnescapeDataString(text[(i + 1)..].Replace('+', ' ')));
        }
        catch (FormatException) { return (null, null); }
    }
}
