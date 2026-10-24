using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Erp.Api.Integrations;

/// <summary>Spec 008: formato y verificación de claves de API (<c>erp_&lt;id público&gt;_&lt;secreto&gt;</c>).</summary>
public static partial class ApiKeyFormat
{
    public const string Prefix = "erp_";

    [GeneratedRegex("^erp_([0-9a-f]{12})_([A-Za-z0-9_-]{43})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static (string PublicId, string Key) New()
    {
        var publicId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        return (publicId, $"{Prefix}{publicId}_{WebhookSignature.Base64Url(RandomNumberGenerator.GetBytes(32))}");
    }

    /// <summary>Id público si la clave tiene el formato correcto; null si no.</summary>
    public static string? PublicIdOf(string? key) => key is not null && Pattern().Match(key) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>256 bits aleatorios: SHA-256 basta (no hay diccionario que probar); un hash lento solo costaría en cada petición.</summary>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static bool Matches(string key, byte[]? hash) =>
        hash is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Hash(key), hash);
}

/// <summary>
/// Spec 008: autenticación con <c>Authorization: Bearer erp_…</c>. Carga la cuenta de servicio con sus roles; cualquier
/// falla responde 401 sin decir por qué (formato, clave, vencida, revocada o cuenta inactiva).
/// </summary>
public sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    Db db, UserContextLoader loader) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    /// <summary>Rutas que una clave puede usar; el resto (administración, modelo, módulos, auditoría) responde 403.</summary>
    public static readonly string[] AllowedPaths = ["/api/data", "/api/reports", "/api/auth/me"];

    public static bool IsBearer(HttpRequest r) => r.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    private sealed record KeyRow(int Id, int UserId, byte[] SecretHash, byte[]? PreviousSecretHash, DateTime? PreviousExpiresAt,
        DateTime? ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var fail = AuthenticateResult.Fail("Clave de API no válida.");
        if (ApiKeyFormat.PublicIdOf(key) is not { } publicId) return fail;

        await using var cn = await db.OpenAsync(Context.RequestAborted);
        var row = await cn.QuerySingleOrDefaultAsync<KeyRow>("""
            SELECT Id, UserId, SecretHash, PreviousSecretHash, PreviousExpiresAt, ExpiresAt, RevokedAt, LastUsedAt
              FROM meta.ApiKey WHERE PublicId = @publicId
            """, new { publicId });
        var now = DateTime.UtcNow;
        var ok = row is not null && row.RevokedAt is null && (row.ExpiresAt is null || row.ExpiresAt > now) &&
                 (ApiKeyFormat.Matches(key, row.SecretHash) ||
                  row.PreviousExpiresAt > now && ApiKeyFormat.Matches(key, row.PreviousSecretHash));
        if (!ok) { Logger.LogWarning("Clave de API rechazada ({PublicId})", publicId); return fail; }

        var user = await loader.LoadServiceAsync(row!.UserId, Context.RequestAborted);
        if (user is null) return fail;
        // Último uso, como máximo una vez por minuto (no una escritura por petición).
        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
            await cn.ExecuteAsync("UPDATE meta.ApiKey SET LastUsedAt = SYSUTCDATETIME() WHERE Id = @Id", new { row.Id });

        Context.Items[UserContextExtensions.ItemKey] = user;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Task.CompletedTask;
    }
}

public sealed record ApiKeyDto(int Id, string Name, string Prefix, IReadOnlyList<int> RoleIds, string Username, DateTime CreatedAt,
    string CreatedBy, DateTime? ExpiresAt, DateTime? LastUsedAt, DateTime? RotatedAt, DateTime? PreviousExpiresAt, DateTime? RevokedAt, string Status);
public sealed record CreateApiKeyRequest(string? Name, int[]? RoleIds, int? ExpiresInDays);
public sealed record UpdateApiKeyRequest(string? Name, int[]? RoleIds);
public sealed record RotateApiKeyRequest(int GraceHours);

/// <summary>Spec 008: alta, cambio, rotación y revocación de claves (solo superusuario; auditado sin la clave).</summary>
public sealed class ApiKeyService(Db db, PasswordService passwords, SecurityApproval gate)
{
    public const int MaxName = 100;
    public const int MaxExpiresInDays = 3650;

    private static void RequireAdmin(UserContext u)
    {
        if (!u.IsSuperuser) throw new Forbidden("Solo un administrador puede administrar claves de API.");
    }

    private sealed record Row(int Id, string Name, string PublicId, string Username, DateTime CreatedAt, string CreatedBy, DateTime? ExpiresAt,
        DateTime? LastUsedAt, DateTime? RotatedAt, DateTime? PreviousExpiresAt, DateTime? RevokedAt, int UserId);

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        return await LoadAsync(cn, null, null);
    }

    private static async Task<List<ApiKeyDto>> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx, int? id)
    {
        var rows = await cn.QueryAsync<Row>("""
            SELECT k.Id, k.Name, k.PublicId, u.Username, k.CreatedAt, c.DisplayName AS CreatedBy, k.ExpiresAt, k.LastUsedAt, k.RotatedAt,
                   k.PreviousExpiresAt, k.RevokedAt, k.UserId
              FROM meta.ApiKey k JOIN meta.[User] u ON u.Id = k.UserId JOIN meta.[User] c ON c.Id = k.CreatedBy
             WHERE @id IS NULL OR k.Id = @id ORDER BY k.Id DESC
            """, new { id }, tx);
        var roles = (await cn.QueryAsync<(int UserId, int RoleId)>(
            "SELECT ur.UserId, ur.RoleId FROM meta.UserRole ur JOIN meta.ApiKey k ON k.UserId = ur.UserId", transaction: tx)).ToLookup(x => x.UserId, x => x.RoleId);
        var now = DateTime.UtcNow;
        static DateTime? U(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
        return rows.Select(r => new ApiKeyDto(r.Id, r.Name, $"{ApiKeyFormat.Prefix}{r.PublicId}", roles[r.UserId].ToList(), r.Username,
            DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc), r.CreatedBy, U(r.ExpiresAt), U(r.LastUsedAt), U(r.RotatedAt),
            r.PreviousExpiresAt > now ? U(r.PreviousExpiresAt) : null, U(r.RevokedAt),
            r.RevokedAt is not null ? "revoked" : r.ExpiresAt is { } e && e <= now ? "expired" : "active")).ToList();
    }

    /// <summary>Roles válidos para un servicio: existentes y sin superusuario, modelador ni administración de usuarios.</summary>
    private static async Task<int[]> ValidRolesAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, int[]? roleIds,
        Dictionary<string, string[]> errors)
    {
        var ids = (roleIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) { errors["roleIds"] = ["Elige al menos un rol: define qué puede hacer el servicio."]; return ids; }
        var roles = (await cn.QueryAsync<(int Id, bool Admin)>(
            "SELECT Id, CAST(CASE WHEN IsSuperuser = 1 OR CanManageModel = 1 OR CanManageUsers = 1 THEN 1 ELSE 0 END AS bit) FROM meta.Role WHERE Id IN @ids",
            new { ids }, tx)).ToList();
        if (roles.Count != ids.Length) errors["roleIds"] = ["Alguno de los roles no existe."];
        else if (roles.Any(r => r.Admin)) errors["roleIds"] = ["Una clave de API no puede tener roles de administración (todo, modelo o usuarios)."];
        return ids;
    }

    private static string? ValidName(string? name, Dictionary<string, string[]> errors)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || n.Length > MaxName) errors["name"] = [$"Escribe un nombre de máximo {MaxName} caracteres."];
        return n;
    }

    /// <summary>
    /// Spec 015: la clave ya generada, como se guarda si espera aprobación: solo el SHA-256 del secreto (que recibe quien
    /// la pidió) y la empresa de quien la pidió.
    /// </summary>
    public sealed record ApiKeyPayload(string Name, int[] RoleIds, int? ExpiresInDays, string PublicId, string SecretHash, int? CompanyId);

    public sealed record ApiKeySnapshot(IReadOnlyList<int> RoleIds, string Status);

    public async Task<IResult> CreateAsync(CreateApiKeyRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var errors = new Dictionary<string, string[]>();
        var name = ValidName(req.Name, errors);
        if (req.ExpiresInDays is < 1 or > MaxExpiresInDays) errors["expiresInDays"] = [$"El vencimiento va de 1 a {MaxExpiresInDays} días."];
        if (errors.Count > 0) throw new ValidationFailed(errors);
        var (publicId, key) = ApiKeyFormat.New();
        var payload = new ApiKeyPayload(name!, (req.RoleIds ?? []).Distinct().ToArray(), req.ExpiresInDays, publicId,
            Convert.ToBase64String(ApiKeyFormat.Hash(key)), u.Company?.Id);
        return await CreateCoreAsync(payload, key, http, null, ct);
    }

    internal async Task<IResult> CreateCoreAsync(ApiKeyPayload p, string? secret, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var roleIds = await ValidRolesAsync(cn, tx, p.RoleIds, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);

        // Spec 015 (H4): una clave nueva es una credencial persistente; espera aprobación. El secreto se generó al solicitar
        // y solo lo recibe quien la pidió; la clave no existe (no autentica) hasta que se aprueba.
        if (gate.MustWait(approving, expands: true))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.ApiKeyCreate, null, $"Alta de la clave de API {p.Name}", p, null);
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change, secret);
        }

        // Cuenta de servicio: no inicia sesión (login la rechaza) y su contraseña aleatoria nunca se conoce.
        var name = p.Name;
        var userId = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.[User] (Username, DisplayName, PasswordHash, IsService) OUTPUT inserted.Id VALUES (@username, @name, @hash, 1)
            """, new { username = $"svc.{p.PublicId}", name = name[..Math.Min(name.Length, 150)], hash = passwords.Hash(WebhookSignature.NewSecret()) }, tx);
        foreach (var r in roleIds)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @r)", new { userId, r }, tx);
        // Spec 010: la clave trabaja en la empresa actual de quien la crea (o de quien la solicitó).
        if (p.CompanyId is { } company && await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.Company WHERE Id = @company", new { company }, tx) is not null)
            await cn.ExecuteAsync("""
                INSERT meta.UserCompany (UserId, CompanyId) VALUES (@userId, @company);
                UPDATE meta.[User] SET CompanyId = @company WHERE Id = @userId;
                """, new { userId, company }, tx);
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.ApiKey (Name, PublicId, SecretHash, UserId, CreatedBy, ExpiresAt)
            OUTPUT inserted.Id VALUES (@name, @PublicId, @hash, @userId, @uid, @expires)
            """, new { name, p.PublicId, hash = Convert.FromBase64String(p.SecretHash), userId, uid = u.Id,
                   expires = p.ExpiresInDays is { } d ? DateTime.UtcNow.AddDays(d) : (DateTime?)null }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApiKeyChange, "ApiKey", id.ToString(),
            after: new { action = "create", name, prefix = ApiKeyFormat.Prefix + p.PublicId, roleIds, p.ExpiresInDays, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return Results.Ok(secret is null ? new { key = dto } : new { key = dto, secret });
    }

    public Task<IResult> UpdateAsync(int id, UpdateApiKeyRequest req, HttpContext http, CancellationToken ct) => UpdateCoreAsync(id, req, http, null, ct);

    internal async Task<IResult> UpdateCoreAsync(int id, UpdateApiKeyRequest req, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var before = (await LoadAsync(cn, tx, id)).SingleOrDefault() ?? throw new NotFound("La clave no existe.");
        var snapshot = new ApiKeySnapshot(before.RoleIds.Order().ToList(), before.Status);
        approving?.Verify(snapshot);
        if (before.Status == "revoked") throw new Conflict("La clave está revocada.");
        var errors = new Dictionary<string, string[]>();
        var name = ValidName(req.Name, errors);
        var roleIds = await ValidRolesAsync(cn, tx, req.RoleIds, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);
        // Spec 015 (H4): agregar roles a una clave espera aprobación; quitarlos o renombrarla, no.
        if (gate.MustWait(approving, SecurityGrants.ApiKeyRoles(before.RoleIds, roleIds)))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.ApiKeyUpdate, id, $"Roles de la clave de API {before.Name}",
                new UpdateApiKeyRequest(name, roleIds.Order().ToArray()), snapshot);
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change);
        }
        var userId = await cn.ExecuteScalarAsync<int>("UPDATE meta.ApiKey SET Name = @name OUTPUT inserted.UserId WHERE Id = @id", new { id, name }, tx);
        await cn.ExecuteAsync("UPDATE meta.[User] SET DisplayName = @name WHERE Id = @userId", new { userId, name = name![..Math.Min(name!.Length, 150)] }, tx);
        await cn.ExecuteAsync("DELETE meta.UserRole WHERE UserId = @userId", new { userId }, tx);
        foreach (var r in roleIds)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @r)", new { userId, r }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApiKeyChange, "ApiKey", id.ToString(),
            before: new { before.Name, before.RoleIds }, after: new { action = "update", name, roleIds, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return Results.Ok(dto);
    }

    /// <summary>Clave nueva; la anterior sirve <paramref name="req"/>.GraceHours más (0 = deja de servir ya).</summary>
    public async Task<object> RotateAsync(int id, RotateApiKeyRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        if (req.GraceHours is not (0 or 24)) throw new ValidationFailed(new Dictionary<string, string[]> { ["graceHours"] = ["El periodo de gracia es 0 o 24 horas."] });
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var current = (await LoadAsync(cn, tx, id)).SingleOrDefault() ?? throw new NotFound("La clave no existe.");
        if (current.Status != "active") throw new Conflict("Solo se rota una clave vigente.");
        var publicId = current.Prefix[ApiKeyFormat.Prefix.Length..];
        var key = $"{ApiKeyFormat.Prefix}{publicId}_{WebhookSignature.Base64Url(RandomNumberGenerator.GetBytes(32))}";
        await cn.ExecuteAsync("""
            UPDATE meta.ApiKey SET PreviousSecretHash = CASE WHEN @grace > 0 THEN SecretHash END,
                   PreviousExpiresAt = CASE WHEN @grace > 0 THEN DATEADD(hour, @grace, SYSUTCDATETIME()) END,
                   SecretHash = @hash, RotatedAt = SYSUTCDATETIME()
             WHERE Id = @id
            """, new { id, grace = req.GraceHours, hash = ApiKeyFormat.Hash(key) }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApiKeyChange, "ApiKey", id.ToString(), after: new { action = "rotate", req.GraceHours });
        var dto = (await LoadAsync(cn, tx, id)).Single();
        await tx.CommitAsync(ct);
        return new { key = dto, secret = key };
    }

    public async Task RevokeAsync(int id, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var userId = await cn.ExecuteScalarAsync<int?>("""
            UPDATE meta.ApiKey SET RevokedAt = SYSUTCDATETIME(), PreviousSecretHash = NULL, PreviousExpiresAt = NULL
            OUTPUT inserted.UserId WHERE Id = @id AND RevokedAt IS NULL
            """, new { id }, tx) ?? throw new NotFound("La clave no existe o ya estaba revocada.");
        await cn.ExecuteAsync("UPDATE meta.[User] SET IsActive = 0, SecurityStamp = NEWID() WHERE Id = @userId", new { userId }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApiKeyChange, "ApiKey", id.ToString(), after: new { action = "revoke" });
        await tx.CommitAsync(ct);
    }
}
