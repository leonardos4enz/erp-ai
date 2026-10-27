using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;

namespace Erp.Api.Security;

/// <summary>Spec 015 (H4): tipos de cambio de seguridad que pueden esperar aprobación.</summary>
public static class SecurityChangeKinds
{
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string RoleCreate = "role.create";
    public const string RoleUpdate = "role.update";
    public const string TeamCreate = "team.create";
    public const string TeamUpdate = "team.update";
    public const string UnitMove = "unit.move";
    public const string ApiKeyCreate = "apikey.create";
    public const string ApiKeyUpdate = "apikey.update";

    public const string OAuthClientCreate = "oauthclient.create";
    public const string OAuthClientUpdate = "oauthclient.update";

    /// <summary>Las claves de API (spec 008) y los clientes OAuth (spec 018) los administra solo el superusuario; sus solicitudes también.</summary>
    public static bool IsIntegration(string kind) =>
        kind.StartsWith("apikey.", StringComparison.Ordinal) || kind.StartsWith("oauthclient.", StringComparison.Ordinal);
}

/// <summary>Lo que decide el acceso de un usuario (sin datos que cambian solos, como el último inicio de sesión).</summary>
public sealed record UserAccessSnapshot(string DisplayName, bool IsActive, IReadOnlyList<int> RoleIds, string? Unit, IReadOnlyList<int> CompanyIds);

public sealed record TeamSnapshot(string Name, string? Unit, bool IsActive, IReadOnlyList<int> MemberIds);

public sealed record RoleSnapshot(string Name, bool CanManageModel, bool CanManageUsers, bool CanViewAudit,
    IReadOnlyList<PermissionSnapshot> Permissions, IReadOnlyList<int> InheritsRoleIds);

public sealed record PermissionSnapshot(string Entity, bool Read, bool Create, bool Update, bool Delete);

/// <summary>
/// Spec 021 (H1): capacidades administrativas de un rol o de varios. Solo se otorga o quita lo que uno mismo tiene: un
/// administrador de usuarios no reparte superusuario, ni modelo ni auditoría si no los tiene.
/// </summary>
public sealed record AdminCapabilities(bool Superuser, bool Model, bool Users, bool Audit)
{
    public static readonly AdminCapabilities None = new(false, false, false, false);

    public bool Any => Superuser || Model || Users || Audit;

    public AdminCapabilities Or(AdminCapabilities o) => new(Superuser || o.Superuser, Model || o.Model, Users || o.Users, Audit || o.Audit);

    /// <summary>Lo primero que le falta a <paramref name="actor"/> para repartir estas capacidades (null = nada).</summary>
    public string? MissingFor(UserContext actor) =>
        actor.IsSuperuser ? null
        : Superuser ? "administrador del sistema"
        : Model && !actor.CanManageModel ? "administrar el modelo"
        : Users && !actor.CanManageUsers ? "administrar usuarios"
        : Audit && !actor.CanViewAudit ? "ver la auditoría"
        : null;
}

/// <summary>
/// Spec 015 (H4 CA1–CA2): ¿el cambio amplía acceso? Ampliar espera aprobación; reducir (quitar, desactivar, revocar) nunca.
/// Funciones puras para probarlas una por una.
/// </summary>
public static class SecurityGrants
{
    private static bool Adds(IEnumerable<int> before, IEnumerable<int> after) => after.Except(before).Any();

    private static bool NewUnit(string? before, string? after) => after is not null && !string.Equals(before, after, StringComparison.Ordinal);

    /// <param name="passwordForOther">Pone contraseña a otra persona: permite entrar como ella.</param>
    public static bool User(UserAccessSnapshot? before, UserAccessSnapshot after, bool passwordForOther) =>
        before is null || passwordForOther || Adds(before.RoleIds, after.RoleIds) || Adds(before.CompanyIds, after.CompanyIds)
        || NewUnit(before.Unit, after.Unit) || (!before.IsActive && after.IsActive);

    public static bool Role(RoleSnapshot? before, RoleSnapshot after)
    {
        if (before is null)
            return after.CanManageModel || after.CanManageUsers || after.CanViewAudit || after.InheritsRoleIds.Count > 0
                   || after.Permissions.Any(p => p.Read || p.Create || p.Update || p.Delete);
        // Renombrar cambia qué políticas lo nombran (campos, registros, transiciones): puede ganar accesos.
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal)) return true;
        if ((!before.CanManageModel && after.CanManageModel) || (!before.CanManageUsers && after.CanManageUsers) || (!before.CanViewAudit && after.CanViewAudit))
            return true;
        if (Adds(before.InheritsRoleIds, after.InheritsRoleIds)) return true;
        var old = before.Permissions.ToDictionary(p => p.Entity, StringComparer.OrdinalIgnoreCase);
        return after.Permissions.Any(p => !old.TryGetValue(p.Entity, out var o)
            ? p.Read || p.Create || p.Update || p.Delete
            : (p.Read && !o.Read) || (p.Create && !o.Create) || (p.Update && !o.Update) || (p.Delete && !o.Delete));
    }

    public static bool Team(TeamSnapshot? before, TeamSnapshot after) =>
        before is null ? after.MemberIds.Count > 0
        : Adds(before.MemberIds, after.MemberIds) || NewUnit(before.Unit, after.Unit) || (!before.IsActive && after.IsActive);

    /// <summary>Mover bajo otra unidad amplía lo que ven sus nuevos superiores; llevarla a la raíz solo reduce.</summary>
    public static bool UnitMove(int? beforeParent, int? afterParent) => afterParent is not null && afterParent != beforeParent;

    public static bool ApiKeyRoles(IEnumerable<int>? before, IEnumerable<int> after) => before is null || Adds(before, after);

    /// <summary>Spec 018: un cliente OAuth amplía acceso si gana roles o alcances (la vida del token no da acceso nuevo).</summary>
    public static bool OAuthClient(IEnumerable<int>? beforeRoles, IEnumerable<string>? beforeScopes, IEnumerable<int> roles, IEnumerable<string> scopes) =>
        beforeRoles is null || beforeScopes is null || Adds(beforeRoles, roles) || scopes.Except(beforeScopes, StringComparer.Ordinal).Any();
}

public sealed record SecurityChangeDto(int Id, string Kind, int? TargetId, string Summary, string Status, JsonElement? Before,
    JsonElement After, string Hash, string RequestedBy, DateTime RequestedAt, string? ReviewedBy, DateTime? ReviewedAt,
    string? ReviewComment, bool CanApprove, bool CanReject);

public sealed record ReviewSecurityChangeRequest(string? Hash, string? Comment);

/// <summary>
/// Aprobación en curso que se pasa a la operación: la operación compara, dentro de su transacción, lo que decide el acceso
/// del destino con lo que se guardó al solicitar (si cambió, 409).
/// </summary>
public sealed record SecurityChangeApproval(int Id, string Kind, int? TargetId, string Payload, string? BeforeJson, string? Comment)
{
    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, SecurityApproval.JsonOptions)!;

    public void Verify(object? current)
    {
        if (SecurityApproval.Json(current) != (BeforeJson ?? "null"))
            throw new Conflict("Lo que se solicitó cambiar ya cambió desde la solicitud: recházala y vuelve a solicitarla.");
    }
}

/// <summary>
/// Spec 015 (H4): con <c>Environments:RequireApproval</c> y <c>Environments:RequireSecurityApproval</c>, los cambios que
/// amplían acceso quedan como solicitud y los aplica la aprobación de otro administrador, con la huella del antes y la carga.
/// </summary>
public sealed class SecurityApproval(Db db, EnvironmentOptions env)
{
    public const string Pending = "pending";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
    public const int MaxComment = 500;
    /// <summary>Spec 021 (H8 CA1): una solicitud en <c>applying</c> más de 10 minutos quedó atorada (el proceso cayó).</summary>
    private const string Stuck = "(c.Status = 'applying' AND (c.ClaimedAt IS NULL OR c.ClaimedAt < DATEADD(minute, -10, SYSUTCDATETIME())))";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    public bool Required => env.SecurityChangesNeedApproval;

    /// <summary>¿Espera aprobación? Nunca dentro de la propia aprobación, ni si el cambio solo reduce acceso.</summary>
    public bool MustWait(SecurityChangeApproval? approving, bool expands) => Required && approving is null && expands;

    public static string Hash(string kind, int? targetId, string payload, string? before) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{targetId}\n{payload}\n{before ?? "null"}")));

    /// <summary>Guarda la solicitud en la transacción de quien la pide y la audita (sin aplicar nada).</summary>
    public async Task<SecurityChangeDto> RequestAsync(DbConnection cn, DbTransaction tx, HttpContext http, string kind, int? targetId,
        string summary, object payload, object? before)
    {
        var payloadJson = Json(payload);
        var beforeJson = before is null ? null : Json(before);
        var hash = Hash(kind, targetId, payloadJson, beforeJson);
        var user = http.CurrentUser();
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.SecurityChange (Kind, TargetId, Summary, PayloadJson, BeforeJson, Hash, Status, RequestedBy)
            OUTPUT inserted.Id VALUES (@kind, @targetId, @summary, @payloadJson, @beforeJson, @hash, @Pending, @uid)
            """, new { kind, targetId, summary = summary[..Math.Min(summary.Length, 400)], payloadJson, beforeJson, hash, Pending, uid = user.Id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.SecurityRequest, "SecurityChange", id.ToString(),
            before: before, after: new { kind, targetId, summary, change = Redacted(payloadJson), hash });
        return (await LoadAsync(cn, tx, user, id, null)).Single();
    }

    public async Task<object> ListAsync(UserContext user, string? status, CancellationToken ct)
    {
        if (!user.CanManageUsers) throw new Forbidden();
        await using var cn = await db.OpenAsync(ct);
        return new { required = Required, items = await LoadAsync(cn, null, user, null, status is "all" ? null : status ?? Pending) };
    }

    private sealed record Row(int Id, string Kind, int? TargetId, string Summary, string Status, string PayloadJson, string? BeforeJson,
        string Hash, int RequestedById, string RequestedBy, DateTime RequestedAt, string? ReviewedBy, DateTime? ReviewedAt, string? ReviewComment);

    private static async Task<List<SecurityChangeDto>> LoadAsync(DbConnection cn, DbTransaction? tx, UserContext user, int? id, string? status)
    {
        // Una solicitud atorada en 'applying' se muestra (y se trata) como pendiente.
        var rows = await cn.QueryAsync<Row>($"""
            SELECT TOP 200 c.Id, c.Kind, c.TargetId, c.Summary, CASE WHEN {Stuck} THEN 'pending' ELSE c.Status END AS Status,
                   c.PayloadJson, c.BeforeJson, c.Hash, c.RequestedBy AS RequestedById,
                   r.Username AS RequestedBy, c.RequestedAt, v.Username AS ReviewedBy, c.ReviewedAt, c.ReviewComment
              FROM meta.SecurityChange c JOIN meta.[User] r ON r.Id = c.RequestedBy LEFT JOIN meta.[User] v ON v.Id = c.ReviewedBy
             WHERE (@id IS NULL OR c.Id = @id) AND (@status IS NULL OR c.Status = @status OR (@status = 'pending' AND {Stuck}))
             ORDER BY c.Id DESC
            """, new { id, status }, tx);
        var roles = await LoadRoleCapabilitiesAsync(cn, tx);
        var units = await LoadUnitsAsync(cn, tx);
        static DateTime U(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return rows.Where(r => id is not null || user.IsSuperuser || !SecurityChangeKinds.IsIntegration(r.Kind))
            .Select(r => new SecurityChangeDto(r.Id, r.Kind, r.TargetId, r.Summary, r.Status,
                r.BeforeJson is null ? null : JsonDocument.Parse(r.BeforeJson).RootElement.Clone(),
                JsonSerializer.SerializeToElement(Redacted(r.PayloadJson)), r.Hash, r.RequestedBy, U(r.RequestedAt), r.ReviewedBy,
                r.ReviewedAt is { } at ? U(at) : null, r.ReviewComment,
                CanApprove: r.Status == Pending && CanReview(user, r.Kind) && r.RequestedById != user.Id
                            && WhyNotApprover(user, r.Kind, r.TargetId, r.PayloadJson, r.BeforeJson, roles, units) is null,
                CanReject: r.Status == Pending && (r.RequestedById == user.Id || CanReview(user, r.Kind)))).ToList();
    }

    private static async Task<IReadOnlyDictionary<int, (string Name, AdminCapabilities Caps)>> LoadRoleCapabilitiesAsync(DbConnection cn, DbTransaction? tx) =>
        (await cn.QueryAsync<(int Id, string Name, bool IsSuperuser, bool CanManageModel, bool CanManageUsers, bool CanViewAudit)>(
            "SELECT Id, Name, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit FROM meta.Role", transaction: tx))
        .ToDictionary(r => r.Id, r => (r.Name, new AdminCapabilities(r.IsSuperuser, r.CanManageModel, r.CanManageUsers, r.CanViewAudit)));

    private static JsonElement? Prop(string? json, string property)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var v) ? v.Clone() : null;
    }

    private static IReadOnlyList<int> Ints(string? json, string property) =>
        Prop(json, property) is { ValueKind: JsonValueKind.Array } v
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : [];

    private static bool Flag(string? json, string property) => Prop(json, property) is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// Spec 021 (H1 CA4–CA5): por qué <paramref name="user"/> no puede aprobar este cambio aunque revise solicitudes (null =
    /// puede): le faltan capacidades que el cambio otorga o quita, o es su beneficiario.
    /// </summary>
    /// <remarks>
    /// Spec 029 (H6 CA1, auditoría 4 D-03): sin caso por omisión. Cada tipo de cambio declara su regla; uno nuevo sin regla no
    /// lo aprueba nadie (<see cref="NoRule"/>) y la prueba de tipos lo detecta.
    /// </remarks>
    internal static string? WhyNotApprover(UserContext user, string kind, int? targetId, string payload, string? before,
        IReadOnlyDictionary<int, (string Name, AdminCapabilities Caps)> roles, IReadOnlyList<UnitPath>? units = null)
    {
        AdminCapabilities needed;
        switch (kind)
        {
            case SecurityChangeKinds.UserCreate or SecurityChangeKinds.UserUpdate:
                if (kind == SecurityChangeKinds.UserUpdate && targetId == user.Id) return "El cambio es a tu propio usuario: lo aprueba otro administrador.";
                // Spec 031 (H3, auditoría 5 A-02): pasar a alguien (con sus registros) a la unidad propia o a una que cuelga de ella.
                if (kind == SecurityChangeKinds.UserUpdate && GainsMember(user, Str(before, "unit"), Str(payload, "unit"), units ?? []))
                    return "El cambio pasa al usuario a tu unidad (o a una que depende de ella) y te deja ver sus registros: lo aprueba otro administrador.";
                var b = Ints(before, "roleIds");
                var a = Ints(payload, "roleIds");
                // Spec 034 (A-01): contraseña, activación, unidad o empresas de quien tiene capacidades que el revisor no tiene.
                if (kind == SecurityChangeKinds.UserUpdate && ActsOnUser(payload, before)
                    && b.Select(id => roles.TryGetValue(id, out var r) ? r.Caps : AdminCapabilities.None)
                        .Aggregate(AdminCapabilities.None, (x, y) => x.Or(y)).MissingFor(user) is { } over)
                    return $"El cambio toca la contraseña, la activación, la unidad, las empresas o los roles de alguien con capacidades que no tienes ({over}): lo aprueba quien las tenga.";
                needed = a.Except(b).Concat(b.Except(a)).Select(id => roles.TryGetValue(id, out var r) ? r.Caps : AdminCapabilities.None)
                    .Aggregate(AdminCapabilities.None, (x, y) => x.Or(y));
                break;
            case SecurityChangeKinds.RoleCreate or SecurityChangeKinds.RoleUpdate:
                var target = targetId is { } tid && roles.TryGetValue(tid, out var tr) ? tr : (Name: (string?)null, Caps: AdminCapabilities.None);
                if (target.Name is not null && user.Roles.Contains(target.Name, StringComparer.OrdinalIgnoreCase))
                    return "Tienes este rol: el cambio lo aprueba otro administrador.";
                needed = new AdminCapabilities(target.Caps.Superuser, Flag(payload, "canManageModel"), Flag(payload, "canManageUsers"), Flag(payload, "canViewAudit"))
                    .Or(new AdminCapabilities(false, Flag(before, "canManageModel"), Flag(before, "canManageUsers"), Flag(before, "canViewAudit")));
                break;
            case SecurityChangeKinds.TeamCreate or SecurityChangeKinds.TeamUpdate:
                if (Ints(payload, "memberIds").Contains(user.Id) && !Ints(before, "memberIds").Contains(user.Id))
                    return "El cambio te agrega al equipo: lo aprueba otro administrador.";
                // Spec 031 (revisión adversarial F-C): reactivar un equipo del que eres miembro te devuelve sus registros.
                if (kind == SecurityChangeKinds.TeamUpdate && Ints(before, "memberIds").Contains(user.Id)
                    && Prop(before, "isActive") is { ValueKind: JsonValueKind.False } && Flag(payload, "isActive"))
                    return "El cambio reactiva un equipo del que eres miembro: lo aprueba otro administrador.";
                // Spec 031 (H3): un equipo es dueño de registros (015); al crearlo aún no tiene ninguno.
                return kind == SecurityChangeKinds.TeamUpdate && GainsMember(user, Str(before, "unit"), Str(payload, "unit"), units ?? [])
                    ? "El cambio pasa al equipo a tu unidad (o a una que depende de ella) y te deja ver sus registros: lo aprueba otro administrador." : null;
            case SecurityChangeKinds.UnitMove:
                // Con «unitTree», quien tiene la unidad nueva superior (o una de más arriba) gana lo que ve la unidad movida.
                return GainsUnit(user, Int(before, "parentId"), Int(payload, "parentId"), units ?? [])
                    ? "Mover la unidad bajo la tuya te deja ver sus registros: lo aprueba otro administrador." : null;
            case SecurityChangeKinds.ApiKeyCreate or SecurityChangeKinds.ApiKeyUpdate
                or SecurityChangeKinds.OAuthClientCreate or SecurityChangeKinds.OAuthClientUpdate:
                // Las revisa solo el superusuario (CanReview), que ya tiene todo; la cuenta de servicio no es de quien aprueba.
                return null;
            default:
                return NoRule;
        }
        return needed.MissingFor(user) is { } missing ? $"El cambio otorga o quita capacidades que no tienes ({missing}): lo aprueba quien las tenga." : null;
    }

    internal const string NoRule = "Este tipo de cambio no tiene regla de aprobación: no se puede aprobar.";

    /// <summary>
    /// Spec 034: ¿la solicitud de <c>user.update</c> cambia la contraseña, la activación, la unidad, las empresas o los roles? (La misma
    /// regla que <c>AdminEndpoints.RequireActOnAsync</c>, sobre el JSON guardado: <c>UserPayload</c> y su foto de antes.)
    /// </summary>
    internal static bool ActsOnUser(string payload, string? before) =>
        Prop(payload, "passwordHash") is { ValueKind: JsonValueKind.String }
        || Prop(payload, "isActive")?.ValueKind != Prop(before, "isActive")?.ValueKind
        || !string.Equals(Str(payload, "unit"), Str(before, "unit"), StringComparison.Ordinal)
        || Prop(payload, "companyIds") is { ValueKind: JsonValueKind.Array }
           && !Ints(payload, "companyIds").Distinct().Order().SequenceEqual(Ints(before, "companyIds").Order())
        || !Ints(payload, "roleIds").Distinct().Order().SequenceEqual(Ints(before, "roleIds").Order()); // F-01

    /// <summary>Spec 029: ruta materializada de cada unidad (la de la 015, «/1/4/»).</summary>
    public sealed record UnitPath(int Id, string Name, string Path);

    private static int? Int(string? json, string property) =>
        Prop(json, property) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    /// <summary>¿La unidad de <paramref name="user"/> queda por encima de la movida sin haberlo estado antes?</summary>
    internal static bool GainsUnit(UserContext user, int? oldParent, int? newParent, IReadOnlyList<UnitPath> units)
    {
        if (string.IsNullOrWhiteSpace(user.Unit) || newParent is null) return false;
        var mine = units.FirstOrDefault(u => string.Equals(u.Name, user.Unit, StringComparison.OrdinalIgnoreCase));
        if (mine is null) return false;
        bool Under(int? parent) => parent is { } id && units.FirstOrDefault(u => u.Id == id) is { } p && p.Path.StartsWith(mine.Path, StringComparison.Ordinal);
        return Under(newParent) && !Under(oldParent);
    }

    private static string? Str(string? json, string property) =>
        Prop(json, property) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>
    /// Spec 031 (H3): ¿el usuario o equipo pasa de <paramref name="oldUnit"/> a la unidad de <paramref name="user"/> o a una que
    /// cuelga de ella, sin haber estado antes? Con alcance «unit» o «unitTree», quien aprueba ganaría ver sus registros (la
    /// regla de <c>unit.move</c> de la 029 aplicada al principal en lugar de a la unidad). Al crear un usuario aún no tiene
    /// registros: no aplica.
    /// </summary>
    internal static bool GainsMember(UserContext user, string? oldUnit, string? newUnit, IReadOnlyList<UnitPath> units)
    {
        if (string.IsNullOrWhiteSpace(user.Unit) || newUnit is null) return false;
        var mine = units.FirstOrDefault(u => string.Equals(u.Name, user.Unit, StringComparison.OrdinalIgnoreCase));
        if (mine is null) return false;
        bool Under(string? name) => name is not null && units.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
                                    && p.Path.StartsWith(mine.Path, StringComparison.Ordinal);
        bool Mine(string? name) => string.Equals(name, mine.Name, StringComparison.OrdinalIgnoreCase);
        // Revisión adversarial F-B: con alcance «unit» (la unidad exacta) quien revisa no veía a alguien de una unidad hija;
        // pasarlo a la suya también le da sus registros.
        return Under(newUnit) && (!Under(oldUnit) || Mine(newUnit) && !Mine(oldUnit));
    }

    private static async Task<IReadOnlyList<UnitPath>> LoadUnitsAsync(DbConnection cn, DbTransaction? tx) =>
        (await cn.QueryAsync<UnitPath>("SELECT Id, Name, Path FROM meta.BusinessUnit", transaction: tx)).ToList();

    private static bool CanReview(UserContext user, string kind) =>
        SecurityChangeKinds.IsIntegration(kind) ? user.IsSuperuser : user.CanManageUsers;

    /// <summary>La carga sin hashes: quien revisa ve que cambia la contraseña o que hay un secreto, no su hash.</summary>
    private static Dictionary<string, object?> Redacted(string payloadJson)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson, JsonOptions) ?? [];
        var result = new Dictionary<string, object?>();
        foreach (var (k, v) in map)
        {
            if (k == "passwordHash") { result["passwordChanged"] = v.ValueKind == JsonValueKind.String; continue; }
            if (k == "secretHash") continue;
            result[k] = v;
        }
        return result;
    }

    /// <summary>
    /// Reclama la solicitud para aplicarla (<c>pending → applying</c>, atómico): dos aprobaciones simultáneas no la aplican
    /// dos veces. Comprueba quién aprueba y la huella.
    /// </summary>
    public async Task<SecurityChangeApproval> ClaimAsync(int id, ReviewSecurityChangeRequest req, UserContext user, CancellationToken ct)
    {
        if (req.Comment?.Length > MaxComment) throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = [$"Máximo {MaxComment} caracteres."] });
        await using var cn = await db.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<Row>("""
            SELECT c.Id, c.Kind, c.TargetId, c.Summary, c.Status, c.PayloadJson, c.BeforeJson, c.Hash, c.RequestedBy AS RequestedById,
                   CAST(N'' AS nvarchar(100)) AS RequestedBy, c.RequestedAt, CAST(NULL AS nvarchar(100)) AS ReviewedBy, c.ReviewedAt, c.ReviewComment
              FROM meta.SecurityChange c WHERE c.Id = @id
            """, new { id }) ?? throw new NotFound("La solicitud no existe.");
        if (!CanReview(user, row.Kind)) throw new Forbidden(SecurityChangeKinds.IsIntegration(row.Kind)
            ? "Una solicitud de clave de API o de cliente OAuth la aprueba otro administrador del sistema." : "No puedes aprobar cambios de seguridad.");
        if (row.RequestedById == user.Id) throw new Forbidden("La debe aprobar un administrador distinto de quien la solicitó.");
        if (WhyNotApprover(user, row.Kind, row.TargetId, row.PayloadJson, row.BeforeJson, await LoadRoleCapabilitiesAsync(cn, null),
                await LoadUnitsAsync(cn, null)) is { } why)
            throw new Forbidden(why);
        if (!string.Equals(req.Hash, row.Hash, StringComparison.OrdinalIgnoreCase))
            throw new Conflict("La solicitud no coincide con la que revisaste: vuelve a revisarla.");
        var claimed = await cn.ExecuteAsync(
            $"UPDATE c SET Status = @Applying, ClaimedAt = SYSUTCDATETIME() FROM meta.SecurityChange c WHERE c.Id = @id AND (c.Status = @Pending OR {Stuck})",
            new { id, Applying, Pending });
        if (claimed == 0) throw new Conflict("La solicitud ya no está pendiente.");
        return new SecurityChangeApproval(row.Id, row.Kind, row.TargetId, row.PayloadJson, row.BeforeJson, Clean(req.Comment));
    }

    /// <summary>Si la operación falló, la solicitud vuelve a quedar pendiente (se puede rechazar o reintentar).</summary>
    public async Task ReleaseAsync(int id)
    {
        await using var cn = await db.OpenAsync();
        await cn.ExecuteAsync("UPDATE meta.SecurityChange SET Status = @Pending WHERE Id = @id AND Status = @Applying", new { id, Pending, Applying });
    }

    /// <summary>La operación aprobada marca la solicitud como aplicada en su propia transacción: o quedan ambas, o ninguna.</summary>
    public static async Task MarkAppliedAsync(DbConnection cn, DbTransaction tx, SecurityChangeApproval approving, HttpContext http)
    {
        var requestedBy = await cn.ExecuteScalarAsync<int?>("""
            UPDATE meta.SecurityChange SET Status = @Applied, ReviewedBy = @uid, ReviewedAt = SYSUTCDATETIME(), ReviewComment = @comment
            OUTPUT inserted.RequestedBy WHERE Id = @id AND Status = @Applying
            """, new { id = approving.Id, Applied, Applying, uid = http.CurrentUser().Id, comment = approving.Comment }, tx)
            ?? throw new Conflict("La solicitud ya no está pendiente.");
        await Audit.WriteAsync(cn, tx, http, AuditActions.SecurityApprove, "SecurityChange", approving.Id.ToString(),
            after: new { approving.Kind, approving.TargetId, requestedBy, comment = approving.Comment });
    }

    public async Task<SecurityChangeDto> GetAsync(int id, UserContext user, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return (await LoadAsync(cn, null, user, id, null)).SingleOrDefault() ?? throw new NotFound("La solicitud no existe.");
    }

    public async Task<SecurityChangeDto> RejectAsync(int id, ReviewSecurityChangeRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (!user.CanManageUsers) throw new Forbidden();
        if (req.Comment?.Length > MaxComment) throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = [$"Máximo {MaxComment} caracteres."] });
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var row = (await LoadAsync(cn, tx, user, id, null)).SingleOrDefault() ?? throw new NotFound("La solicitud no existe.");
        if (!row.CanReject) throw row.Status != Pending ? new Conflict("La solicitud ya no está pendiente.") : new Forbidden();
        await cn.ExecuteAsync($"""
            UPDATE c SET Status = @Rejected, ReviewedBy = @uid, ReviewedAt = SYSUTCDATETIME(), ReviewComment = @comment
              FROM meta.SecurityChange c WHERE c.Id = @id AND (c.Status = @Pending OR {Stuck})
            """, new { id, Rejected, Pending, uid = user.Id, comment = Clean(req.Comment) }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.SecurityReject, "SecurityChange", id.ToString(),
            after: new { row.Kind, row.Summary, comment = Clean(req.Comment) });
        var dto = (await LoadAsync(cn, tx, user, id, null)).Single();
        await tx.CommitAsync(ct);
        return dto;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Respuesta de una operación que quedó como solicitud.</summary>
    public static IResult Accepted(SecurityChangeDto change, string? secret = null) =>
        Results.Json(secret is null ? new { securityChange = change } : new { securityChange = change, secret }, JsonOptions, statusCode: 202);
}
