using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

public static partial class AdminEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9._-]{3,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public sealed record UserDto(int Id, string Username, string DisplayName, bool IsActive, DateTime? LastLoginAt,
        DateTime? LockoutEnd, IReadOnlyList<int> RoleIds, string? Unit, IReadOnlyList<int> CompanyIds);
    /// <param name="CompanyIds">Spec 010: omitido = la empresa predeterminada.</param>
    public sealed record CreateUserRequest(string? Username, string? DisplayName, string? Password, int[]? RoleIds, string? Unit = null, int[]? CompanyIds = null);
    /// <param name="CompanyIds">Spec 010: omitido = sin cambio.</param>
    public sealed record UpdateUserRequest(string? DisplayName, bool IsActive, int[]? RoleIds, string? Password, string? Unit = null, int[]? CompanyIds = null);

    /// <summary>
    /// Spec 015: la petición ya validada, como se guarda si espera aprobación. La contraseña va solo como hash PBKDF2.
    /// <c>CompanyIds</c> null = sin cambio (en un alta, la empresa predeterminada).
    /// </summary>
    public sealed record UserPayload(string? Username, string DisplayName, int[] RoleIds, string? Unit, int[]? CompanyIds, bool IsActive, string? PasswordHash);

    public const int MaxUnit = 100;

    public sealed record EntityPermissionDto(string Entity, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
    /// <param name="InheritsRoleIds">Spec 015: roles de los que hereda.</param>
    public sealed record RoleDto(int Id, string Name, string? Description, bool IsSystem, bool IsSuperuser,
        bool CanManageModel, bool CanManageUsers, bool CanViewAudit, IReadOnlyList<EntityPermissionDto> Permissions, IReadOnlyList<int> InheritsRoleIds);
    public sealed record RoleRequest(string? Name, string? Description, bool CanManageModel, bool CanManageUsers,
        bool CanViewAudit, EntityPermissionDto[]? Permissions, int[]? InheritsRoleIds = null);

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization();

        g.MapGet("/users", async (Db db) =>
        {
            await using var cn = await db.OpenAsync();
            return await LoadUsersAsync(cn);
        }).Requires(Capability.UserAdmin);
        g.MapPost("/users", CreateUser).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        g.MapPut("/users/{id:int}", UpdateUser).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        MapOrg(g);

        // Los modeladores también leen los roles para configurar reglas "obligatorio para rol".
        g.MapGet("/roles", async (HttpContext http, Db db) =>
        {
            await using var cn = await db.OpenAsync();
            return await LoadRolesAsync(cn);
        }).Requires(Capability.UserAdmin, Capability.Modeler);
        // Módulos (spec 002 H7): solo superusuario, porque instalan roles y permisos.
        g.MapGet("/modules", async (ModuleService modules) => await modules.ListAsync()).Requires(Capability.Superuser);
        g.MapPost("/modules/{id}/preview", async (string id, ModuleService modules, CancellationToken ct) => await modules.PreviewAsync(id, ct))
            .Requires(Capability.Superuser).ReadsOnly();
        // Spec 029 (H6 CA2): aplicar exige la huella de la vista previa (el cuerpo es opcional para responder 400 con su motivo).
        g.MapPost("/modules/{id}/install", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
            new { changeSetId = await modules.InstallAsync(id, await ModuleApply.ReadAsync(http, ct), http, ct) })
            .Requires(Capability.Superuser).Writes(Effect.Model, Gates.DirectChange).AppliesReviewed("baseHash");
        g.MapPost("/modules/{id}/upgrade/preview", async (string id, ModuleService modules, CancellationToken ct) => await modules.UpgradePreviewAsync(id, ct))
            .Requires(Capability.Superuser).ReadsOnly();
        g.MapPost("/modules/{id}/upgrade", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
            new { changeSetId = await modules.UpgradeAsync(id, await ModuleApply.ReadAsync(http, ct), http, ct) })
            .Requires(Capability.Superuser).Writes(Effect.Model, Gates.DirectChange).AppliesReviewed("baseHash");

        g.MapPost("/roles", (RoleRequest req, HttpContext http, Db db, MetadataStore store, SecurityApproval gate, CancellationToken ct) =>
            SaveRole(null, req, http, db, store, gate, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
        g.MapPut("/roles/{id:int}", (int id, RoleRequest req, HttpContext http, Db db, MetadataStore store, SecurityApproval gate, CancellationToken ct) =>
            SaveRole(id, req, http, db, store, gate, null, ct)).Requires(Capability.UserAdmin).Writes(Effect.Security, Gates.SecurityApproval);
    }

    /// <summary>
    /// La capacidad de la ruta la exige su metadato (spec 029). Las operaciones que también reaplica una aprobación
    /// (<see cref="SecurityChangeEndpoints"/>) la vuelven a comprobar con quien aprueba.
    /// </summary>
    private static void RequireUserAdmin(HttpContext http)
    {
        if (!http.CurrentUser().CanManageUsers) throw new Forbidden();
    }

    // ---------------- Usuarios ----------------

    private static async Task<IResult> CreateUser(CreateUserRequest req, HttpContext http, Db db, PasswordService passwords, SecurityApproval gate)
    {
        var errors = new Dictionary<string, string[]>();
        var username = req.Username?.Trim() ?? "";
        if (!UsernamePattern().IsMatch(username)) errors["username"] = ["Usa de 3 a 100 caracteres: letras, números, '.', '_' o '-'."];
        var displayName = req.DisplayName?.Trim() ?? "";
        if (displayName.Length is 0 or > 150) errors["displayName"] = ["El nombre es obligatorio (máx. 150)."];
        if (!PasswordService.MeetsPolicy(req.Password, out var pwdError, username)) errors["password"] = [pwdError];
        var unit = NormalizeUnit(req.Unit, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);
        var payload = new UserPayload(username, displayName, (req.RoleIds ?? []).Distinct().ToArray(), unit, req.CompanyIds?.Distinct().ToArray(),
            true, passwords.Hash(req.Password!));
        return await CreateUserCore(payload, http, db, gate, null);
    }

    internal static async Task<IResult> CreateUserCore(UserPayload p, HttpContext http, Db db, SecurityApproval gate, SecurityChangeApproval? approving)
    {
        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var username = p.Username!;
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.[User] WHERE Username = @username", new { username }, tx) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["username"] = ["Ese usuario ya existe."] });
        await RequireUnitAsync(cn, tx, p.Unit);
        await RequireRolesAsync(cn, tx, p.RoleIds);
        await RequireGrantableRolesAsync(cn, tx, http.CurrentUser(), [], p.RoleIds);
        var companyIds = p.CompanyIds ?? (await cn.QueryAsync<int>("SELECT Id FROM meta.Company WHERE IsDefault = 1", transaction: tx)).ToArray();
        await RequireCompaniesAsync(cn, tx, companyIds);

        // Spec 015 (H4): un alta siempre amplía acceso.
        if (gate.MustWait(approving, expands: true))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.UserCreate, null, $"Alta del usuario {username}",
                p with { CompanyIds = companyIds }, null);
            await tx.CommitAsync();
            return SecurityApproval.Accepted(change);
        }

        var id = await cn.ExecuteScalarAsync<int>(
            "INSERT meta.[User] (Username, DisplayName, PasswordHash, Unit) OUTPUT inserted.Id VALUES (@username, @DisplayName, @PasswordHash, @Unit)",
            new { username, p.DisplayName, p.PasswordHash, p.Unit }, tx);
        await SetRolesAsync(cn, tx, id, p.RoleIds);
        await SetCompaniesAsync(cn, tx, id, companyIds);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UserChange, "Usuario", id.ToString(),
            after: new { username, displayName = p.DisplayName, roleIds = p.RoleIds, unit = p.Unit, companyIds, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync();
        return Results.Created($"/api/admin/users/{id}", (await LoadUsersAsync(cn)).First(u => u.Id == id));
    }

    private static async Task<IResult> UpdateUser(int id, UpdateUserRequest req, HttpContext http, Db db, PasswordService passwords, SecurityApproval gate)
    {
        var me = http.CurrentUser();
        var errors = new Dictionary<string, string[]>();
        var displayName = req.DisplayName?.Trim() ?? "";
        if (displayName.Length is 0 or > 150) errors["displayName"] = ["El nombre es obligatorio (máx. 150)."];
        if (id == me.Id && !req.IsActive) errors["isActive"] = ["No puedes desactivar tu propia cuenta."];
        var unit = NormalizeUnit(req.Unit, errors);

        await using (var cn = await db.OpenAsync())
        {
            var before = (await LoadUsersAsync(cn)).FirstOrDefault(u => u.Id == id) ?? throw new NotFound("El usuario no existe.");
            if (!string.IsNullOrEmpty(req.Password) && !PasswordService.MeetsPolicy(req.Password, out var pwdError, before.Username))
                errors["password"] = [pwdError];
        }
        if (errors.Count > 0) throw new ValidationFailed(errors);
        var payload = new UserPayload(null, displayName, (req.RoleIds ?? []).Distinct().ToArray(), unit, req.CompanyIds?.Distinct().ToArray(),
            req.IsActive, string.IsNullOrEmpty(req.Password) ? null : passwords.Hash(req.Password));
        return await UpdateUserCore(id, payload, http, db, gate, null);
    }

    private static UserAccessSnapshot Snapshot(UserDto u) =>
        new(u.DisplayName, u.IsActive, u.RoleIds.Order().ToList(), u.Unit, u.CompanyIds.Order().ToList());

    internal static async Task<IResult> UpdateUserCore(int id, UserPayload p, HttpContext http, Db db, SecurityApproval gate, SecurityChangeApproval? approving)
    {
        var me = http.CurrentUser();
        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var before = (await LoadUsersAsync(cn, tx)).FirstOrDefault(u => u.Id == id) ?? throw new NotFound("El usuario no existe.");
        var snapshot = Snapshot(before);
        approving?.Verify(snapshot);
        await RequireUnitAsync(cn, tx, p.Unit);
        await RequireRolesAsync(cn, tx, p.RoleIds);
        await RequireGrantableRolesAsync(cn, tx, me, before.RoleIds, p.RoleIds);
        var companyIds = p.CompanyIds ?? before.CompanyIds.ToArray();
        await RequireCompaniesAsync(cn, tx, companyIds);
        await RequireActOnAsync(cn, tx, me, before, p, companyIds);

        var after = new UserAccessSnapshot(p.DisplayName, p.IsActive, p.RoleIds.Order().ToList(), p.Unit, companyIds.Order().ToList());
        // Spec 015 (H4): agregar roles o empresas, cambiar la unidad, reactivar o poner contraseña a otro espera aprobación.
        var requester = approving is null ? me.Id : (int?)null;
        if (gate.MustWait(approving, SecurityGrants.User(snapshot, after, p.PasswordHash is not null && id != requester)))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.UserUpdate, id, $"Cambios al usuario {before.Username}", p, snapshot);
            await tx.CommitAsync();
            return SecurityApproval.Accepted(change);
        }

        var rolesChanged = !before.RoleIds.Order().SequenceEqual(p.RoleIds.Order());
        var companiesChanged = !before.CompanyIds.Order().SequenceEqual(companyIds.Order());
        // Cambiar contraseña, roles, unidad, empresas o desactivar invalida las sesiones abiertas (nuevo SecurityStamp):
        // la unidad (spec 006) y las empresas (spec 010) deciden qué registros ve.
        var revoke = p.PasswordHash is not null || rolesChanged || companiesChanged || (before.IsActive && !p.IsActive) || before.Unit != p.Unit;
        await cn.ExecuteAsync("""
            UPDATE meta.[User] SET DisplayName = @DisplayName, IsActive = @IsActive, Unit = @Unit,
                PasswordHash = COALESCE(@PasswordHash, PasswordHash),
                FailedAttempts = CASE WHEN @PasswordHash IS NULL THEN FailedAttempts ELSE 0 END,
                LockoutEnd = CASE WHEN @PasswordHash IS NULL THEN LockoutEnd ELSE NULL END,
                SecurityStamp = CASE WHEN @revoke = 1 THEN NEWID() ELSE SecurityStamp END
             WHERE Id = @id
            """, new { id, p.DisplayName, p.IsActive, revoke, p.Unit, p.PasswordHash }, tx);
        await SetRolesAsync(cn, tx, id, p.RoleIds);
        if (companiesChanged) await SetCompaniesAsync(cn, tx, id, companyIds);
        await EnsureSuperuserRemainsAsync(cn, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UserChange, "Usuario", id.ToString(),
            before: new { before.DisplayName, before.IsActive, before.RoleIds, before.Unit, before.CompanyIds },
            after: new { p.DisplayName, p.IsActive, p.RoleIds, p.Unit, CompanyIds = companyIds, PasswordChanged = p.PasswordHash is not null,
                securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync();
        return Results.Ok((await LoadUsersAsync(cn)).First(u => u.Id == id));
    }

    private static string? NormalizeUnit(string? unit, Dictionary<string, string[]> errors)
    {
        var u = unit?.Trim();
        if (u?.Length > MaxUnit) errors["unit"] = [$"La unidad admite máximo {MaxUnit} caracteres."];
        return string.IsNullOrEmpty(u) ? null : u;
    }

    /// <summary>Spec 015: la unidad de un usuario o equipo es una de las dadas de alta en el árbol.</summary>
    private static async Task RequireUnitAsync(DbConnection cn, DbTransaction tx, string? unit)
    {
        if (unit is not null && await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.BusinessUnit WHERE Name = @unit", new { unit }, tx) is null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["unit"] = ["La unidad no existe: dala de alta en Seguridad › Unidades."] });
    }

    private static async Task RequireRolesAsync(DbConnection cn, DbTransaction tx, int[] ids)
    {
        var existing = (await cn.QueryAsync<int>("SELECT Id FROM meta.Role WHERE Id IN @ids", new { ids }, tx)).Count();
        if (existing != ids.Length) throw new ValidationFailed(new Dictionary<string, string[]> { ["roleIds"] = ["Hay roles que no existen."] });
    }

    /// <summary>
    /// Spec 021 (H1 CA1–CA2): los roles que se agregan o se quitan solo dan capacidades que quien hace el cambio tiene.
    /// Corre al solicitar (con quien solicita) y al aprobar (con quien aprueba).
    /// </summary>
    private static async Task RequireGrantableRolesAsync(DbConnection cn, DbTransaction tx, UserContext actor, IEnumerable<int> before, IEnumerable<int> after)
    {
        var b = before.ToHashSet();
        var a = after.ToHashSet();
        var changed = a.Except(b).Concat(b.Except(a)).ToArray();
        if (changed.Length == 0 || actor.IsSuperuser) return;
        foreach (var r in await LoadCapabilitiesAsync(cn, tx, changed))
            if (r.Caps.MissingFor(actor) is { } missing)
                throw new Forbidden(r.Caps.Superuser
                    ? $"Solo un administrador del sistema puede otorgar o quitar el rol «{r.Name}»."
                    : $"No puedes otorgar ni quitar el rol «{r.Name}»: da una capacidad que no tienes ({missing}).");
    }

    /// <summary>
    /// Spec 034 (H1, auditoría 6 A-01): nadie cambia la contraseña, la activación, la unidad ni las empresas de quien tiene
    /// capacidades que él no tiene; con superusuario solo actúa otro superusuario. Es la regla de 021-1 (los roles) aplicada a
    /// la persona: sin ella, quien administra usuarios entraba como el administrador poniéndole contraseña. Corre al
    /// solicitar (con quien solicita) y al aprobar (con quien aprueba); el nombre visible no da ni quita acceso.
    /// </summary>
    private static async Task RequireActOnAsync(DbConnection cn, DbTransaction tx, UserContext actor, UserDto target, UserPayload p, int[] companyIds)
    {
        // Revisión adversarial 034 (F-01): cambiar roles también, aunque el rol no dé capacidades: renueva el sello y cierra
        // todas las sesiones del destino (repetido, deja fuera al administrador) o le quita sus roles de datos.
        var acts = p.PasswordHash is not null || p.IsActive != target.IsActive || !string.Equals(p.Unit, target.Unit, StringComparison.Ordinal)
                   || !target.CompanyIds.Order().SequenceEqual(companyIds.Distinct().Order())
                   || !target.RoleIds.Order().SequenceEqual(p.RoleIds.Distinct().Order());
        if (!acts || actor.IsSuperuser) return;
        var caps = (await LoadCapabilitiesAsync(cn, tx, target.RoleIds.ToArray())).Aggregate(AdminCapabilities.None, (all, r) => all.Or(r.Caps));
        if (caps.MissingFor(actor) is { } missing)
            throw new Forbidden(caps.Superuser
                ? $"Solo un administrador del sistema puede cambiar la contraseña, la activación, la unidad, las empresas o los roles de «{target.Username}»."
                : $"No puedes cambiar la contraseña, la activación, la unidad, las empresas ni los roles de «{target.Username}»: tiene una capacidad que no tienes ({missing}).");
    }

    internal static async Task<IReadOnlyList<(int Id, string Name, AdminCapabilities Caps)>> LoadCapabilitiesAsync(DbConnection cn, DbTransaction? tx, int[] ids) =>
        ids.Length == 0 ? [] : (await cn.QueryAsync<(int Id, string Name, bool IsSuperuser, bool CanManageModel, bool CanManageUsers, bool CanViewAudit)>(
                "SELECT Id, Name, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit FROM meta.Role WHERE Id IN @ids", new { ids }, tx))
            .Select(r => (r.Id, r.Name, new AdminCapabilities(r.IsSuperuser, r.CanManageModel, r.CanManageUsers, r.CanViewAudit))).ToList();

    private static async Task RequireCompaniesAsync(DbConnection cn, DbTransaction tx, int[] ids)
    {
        var existing = (await cn.QueryAsync<int>("SELECT Id FROM meta.Company WHERE Id IN @ids", new { ids }, tx)).Count();
        if (existing != ids.Length) throw new ValidationFailed(new Dictionary<string, string[]> { ["companyIds"] = ["Hay empresas que no existen."] });
    }

    private static async Task SetRolesAsync(DbConnection cn, DbTransaction tx, int userId, int[] roleIds)
    {
        var ids = roleIds.Distinct().ToArray();
        await RequireRolesAsync(cn, tx, ids);
        await cn.ExecuteAsync("DELETE meta.UserRole WHERE UserId = @userId", new { userId }, tx);
        foreach (var roleId in ids)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @roleId)", new { userId, roleId }, tx);
    }

    /// <summary>Spec 010: empresas que puede elegir. Si su empresa actual ya no está entre ellas, se olvida.</summary>
    private static async Task SetCompaniesAsync(DbConnection cn, DbTransaction tx, int userId, IEnumerable<int> companyIds)
    {
        var ids = companyIds.Distinct().ToArray();
        await RequireCompaniesAsync(cn, tx, ids);
        await cn.ExecuteAsync("DELETE meta.UserCompany WHERE UserId = @userId", new { userId }, tx);
        foreach (var companyId in ids)
            await cn.ExecuteAsync("INSERT meta.UserCompany (UserId, CompanyId) VALUES (@userId, @companyId)", new { userId, companyId }, tx);
        await cn.ExecuteAsync("UPDATE meta.[User] SET CompanyId = NULL WHERE Id = @userId AND CompanyId NOT IN (SELECT CompanyId FROM meta.UserCompany WHERE UserId = @userId)",
            new { userId }, tx);
    }

    private static async Task EnsureSuperuserRemainsAsync(DbConnection cn, DbTransaction tx)
    {
        var count = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT u.Id) FROM meta.[User] u JOIN meta.UserRole ur ON ur.UserId = u.Id
              JOIN meta.Role r ON r.Id = ur.RoleId WHERE u.IsActive = 1 AND r.IsSuperuser = 1
            """, transaction: tx);
        if (count == 0) throw new Conflict("Debe quedar al menos un administrador activo.");
    }

    private static async Task<IReadOnlyList<UserDto>> LoadUsersAsync(DbConnection cn, DbTransaction? tx = null)
    {
        var users = await cn.QueryAsync<UserRow>(
            "SELECT Id, Username, DisplayName, IsActive, LastLoginAt, LockoutEnd, Unit FROM meta.[User] WHERE IsService = 0 AND IsTeam = 0 ORDER BY Username",
            transaction: tx);
        var roles = (await cn.QueryAsync<(int UserId, int RoleId)>("SELECT UserId, RoleId FROM meta.UserRole", transaction: tx)).ToLookup(x => x.UserId, x => x.RoleId);
        var companies = (await cn.QueryAsync<(int UserId, int CompanyId)>("SELECT UserId, CompanyId FROM meta.UserCompany", transaction: tx)).ToLookup(x => x.UserId, x => x.CompanyId);
        return users.Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.IsActive, u.LastLoginAt,
            u.LockoutEnd > DateTime.UtcNow ? u.LockoutEnd : null, roles[u.Id].Order().ToList(), u.Unit, companies[u.Id].Order().ToList())).ToList();
    }

    // ---------------- Roles ----------------

    private static RoleSnapshot Snapshot(RoleDto r) => new(r.Name, r.CanManageModel, r.CanManageUsers, r.CanViewAudit,
        r.Permissions.OrderBy(x => x.Entity, StringComparer.OrdinalIgnoreCase).Select(x => new PermissionSnapshot(x.Entity, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)).ToList(),
        r.InheritsRoleIds.Order().ToList());

    internal static async Task<IResult> SaveRole(int? id, RoleRequest req, HttpContext http, Db db, MetadataStore store, SecurityApproval gate,
        SecurityChangeApproval? approving, CancellationToken ct)
    {
        RequireUserAdmin(http);
        var name = req.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (name.Length is 0 or > 100) errors["name"] = ["El nombre es obligatorio (máx. 100)."];
        if (req.Description?.Length > 300) errors["description"] = ["Máximo 300 caracteres."];
        var model = await store.GetAsync(ct);
        var perms = (req.Permissions ?? []).DistinctBy(x => x.Entity, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.CanRead || x.CanCreate || x.CanUpdate || x.CanDelete).ToList();
        if (perms.Any(p => !model.ContainsKey(p.Entity))) errors["permissions"] = ["Hay catálogos que no existen."];
        var parents = (req.InheritsRoleIds ?? []).Distinct().Order().ToArray();
        if (parents.Length > RoleGraph.MaxParents) errors["inheritsRoleIds"] = [$"Un rol hereda de máximo {RoleGraph.MaxParents} roles."];
        if (errors.Count > 0) throw new ValidationFailed(errors);
        // La carga que se guarda si espera aprobación: nombres de catálogo canónicos y sin permisos vacíos.
        req = req with { Name = name, Description = req.Description?.Trim(), InheritsRoleIds = parents,
            Permissions = perms.Select(p => p with { Entity = model[p.Entity].Name }).OrderBy(p => p.Entity, StringComparer.OrdinalIgnoreCase).ToArray() };

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var all = await LoadRolesAsync(cn, tx);
        RoleDto? before = null;
        if (id is not null)
        {
            before = all.FirstOrDefault(r => r.Id == id) ?? throw new NotFound("El rol no existe.");
            approving?.Verify(Snapshot(before));
            if (before.IsSystem && !string.Equals(before.Name, name, StringComparison.Ordinal))
                throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Los roles del sistema no se pueden renombrar."] });
        }
        if (all.Any(r => r.Id != id && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe un rol con ese nombre."] });

        // El rol superusuario no se puede degradar desde la aplicación.
        var superuser = before?.IsSuperuser == true;
        var (canModel, canUsers, canAudit) = superuser ? (true, true, true) : (req.CanManageModel, req.CanManageUsers, req.CanViewAudit);
        // Spec 021 (H1 CA3): un rol no gana ni pierde capacidades que quien lo edita no tiene; el superusuario, solo otro superusuario.
        var actor = http.CurrentUser();
        var touched = new AdminCapabilities(superuser, canModel, canUsers, canAudit)
            .Or(before is null ? AdminCapabilities.None : new AdminCapabilities(before.IsSuperuser, before.CanManageModel, before.CanManageUsers, before.CanViewAudit));
        if (touched.MissingFor(actor) is { } missing)
            throw new Forbidden(superuser ? "Solo un administrador del sistema puede cambiar el rol superusuario."
                : $"No puedes dar ni quitar a un rol una capacidad que no tienes ({missing}).");
        CheckInheritance(id, parents, canModel || canUsers || canAudit || superuser, all);

        var after = new RoleSnapshot(name, canModel, canUsers, canAudit,
            req.Permissions!.Select(x => new PermissionSnapshot(x.Entity, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)).ToList(), parents);
        // Spec 015 (H4): renombrar o agregar permisos, capacidades o herencia espera aprobación.
        if (gate.MustWait(approving, SecurityGrants.Role(before is null ? null : Snapshot(before), after)))
        {
            var change = await gate.RequestAsync(cn, tx, http, id is null ? SecurityChangeKinds.RoleCreate : SecurityChangeKinds.RoleUpdate, id,
                id is null ? $"Alta del rol {name}" : $"Cambios al rol {before!.Name}", req, before is null ? null : Snapshot(before));
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change);
        }

        var args = new { id, name, description = req.Description, req.CanManageModel, req.CanManageUsers, req.CanViewAudit };
        var roleId = id ?? await cn.ExecuteScalarAsync<int>("""
            INSERT meta.Role (Name, Description, CanManageModel, CanManageUsers, CanViewAudit)
            OUTPUT inserted.Id VALUES (@name, @description, @CanManageModel, @CanManageUsers, @CanViewAudit)
            """, args, tx);
        if (id is not null)
            await cn.ExecuteAsync("""
                UPDATE meta.Role SET Name = @name, Description = @description,
                    CanManageModel = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanManageModel END,
                    CanManageUsers = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanManageUsers END,
                    CanViewAudit = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanViewAudit END
                 WHERE Id = @id
                """, args, tx);

        await cn.ExecuteAsync("DELETE meta.EntityPermission WHERE RoleId = @roleId", new { roleId }, tx);
        foreach (var perm in req.Permissions!)
            await cn.ExecuteAsync("""
                INSERT meta.EntityPermission (RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete)
                VALUES (@roleId, @Entity, @CanRead, @CanCreate, @CanUpdate, @CanDelete)
                """, new { roleId, perm.Entity, perm.CanRead, perm.CanCreate, perm.CanUpdate, perm.CanDelete }, tx);
        await cn.ExecuteAsync("DELETE meta.RoleInherit WHERE RoleId = @roleId", new { roleId }, tx);
        foreach (var parent in parents)
            await cn.ExecuteAsync("INSERT meta.RoleInherit (RoleId, ParentRoleId) VALUES (@roleId, @parent)", new { roleId, parent }, tx);

        var saved = (await LoadRolesAsync(cn, tx)).First(r => r.Id == roleId);
        await Audit.WriteAsync(cn, tx, http, AuditActions.RoleChange, "Rol", roleId.ToString(), before,
            approving is null ? saved : new { role = saved, securityChange = approving.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync(ct);
        return Results.Ok(saved);
    }

    /// <summary>
    /// Spec 015 (H3): los roles de los que se hereda existen, no tienen capacidades administrativas (no se heredan), y la
    /// herencia no forma ciclos ni pasa de <see cref="RoleGraph.MaxDepth"/> niveles. Un rol del que otros heredan no puede ganar
    /// capacidades.
    /// </summary>
    private static void CheckInheritance(int? id, int[] parents, bool administrative, IReadOnlyList<RoleDto> all)
    {
        var errors = new List<string>();
        var byId = all.ToDictionary(r => r.Id);
        foreach (var parent in parents)
        {
            if (parent == id) errors.Add("Un rol no puede heredar de sí mismo.");
            else if (!byId.TryGetValue(parent, out var r)) errors.Add("Hay roles que no existen.");
            else if (r.IsSuperuser || r.CanManageModel || r.CanManageUsers || r.CanViewAudit)
                errors.Add($"No se hereda de «{r.Name}»: tiene capacidades administrativas, que se asignan directamente.");
        }
        if (id is not null && administrative && all.Any(r => r.InheritsRoleIds.Contains(id.Value)))
            errors.Add("Otros roles heredan de este: no puede tener capacidades administrativas.");
        if (errors.Count == 0)
        {
            const int draft = 0; // un rol nuevo aún no tiene id
            var self = id ?? draft;
            var edges = all.Where(r => r.Id != self).SelectMany(r => r.InheritsRoleIds.Select(pid => (r.Id, pid)))
                .Concat(parents.Select(pid => (self, pid))).ToList();
            var depth = RoleGraph.MaxDepthOf(edges);
            if (depth is null) errors.Add("La herencia formaría un ciclo.");
            else if (depth > RoleGraph.MaxDepth) errors.Add($"La herencia admite máximo {RoleGraph.MaxDepth} niveles.");
        }
        if (errors.Count > 0) throw new ValidationFailed(new Dictionary<string, string[]> { ["inheritsRoleIds"] = errors.Distinct().ToArray() });
    }

    private static async Task<IReadOnlyList<RoleDto>> LoadRolesAsync(DbConnection cn, DbTransaction? tx = null)
    {
        var roles = await cn.QueryAsync<RoleRow>(
            "SELECT Id, Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit FROM meta.Role ORDER BY Name",
            transaction: tx);
        var perms = (await cn.QueryAsync<PermRow>(
            "SELECT RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete FROM meta.EntityPermission", transaction: tx))
            .ToLookup(x => x.RoleId);
        var parents = (await cn.QueryAsync<(int RoleId, int ParentRoleId)>("SELECT RoleId, ParentRoleId FROM meta.RoleInherit", transaction: tx))
            .ToLookup(x => x.RoleId, x => x.ParentRoleId);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsSuperuser, r.CanManageModel,
            r.CanManageUsers, r.CanViewAudit,
            perms[r.Id].OrderBy(x => x.EntityName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new EntityPermissionDto(x.EntityName, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)).ToList(),
            parents[r.Id].Order().ToList())).ToList();
    }

    private sealed record UserRow(int Id, string Username, string DisplayName, bool IsActive, DateTime? LastLoginAt, DateTime? LockoutEnd, string? Unit);
    private sealed record RoleRow(int Id, string Name, string? Description, bool IsSystem, bool IsSuperuser,
        bool CanManageModel, bool CanManageUsers, bool CanViewAudit);
    private sealed record PermRow(int RoleId, string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
}

public static class AuditEndpoints
{
    public sealed record AuditItem(long Id, DateTime At, string? User, string Action, string? EntityName, string? RecordId,
        JsonElement? Before, JsonElement? After, string? IpAddress);

    public static void MapAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (string? entity, string? action, int? page, int? pageSize, HttpContext http, Db db, MetadataStore store) =>
        {
            var user = http.CurrentUser();
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = (Math.Max(1, page ?? 1) - 1) * size;
            var model = await store.GetAsync();
            var args = new DynamicParameters(new { entity = Empty(entity), action = Empty(action), skip, size });
            var where = "WHERE (@entity IS NULL OR a.EntityName = @entity) AND (@action IS NULL OR a.Action = @action)";
            // Spec 012 (H1): filas y conteo con la seguridad del dato (catálogo, empresa y permisos por registro).
            if (AuditFilter.Build(model, user) is { } scope)
            {
                where += " AND " + scope.Sql;
                foreach (var (name, value) in scope.Parameters) args.Add(name, value);
            }
            await using var cn = await db.OpenAsync();
            var total = await cn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog a {where}", args);
            var rows = await cn.QueryAsync<Row>($"""
                SELECT a.Id, a.At, a.Username, a.Action, a.EntityName, a.RecordId, a.BeforeJson, a.AfterJson, a.IpAddress
                  FROM meta.AuditLog a {where} ORDER BY a.Id DESC OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY
                """, args);
            return new
            {
                items = rows.Select(r => new AuditItem(r.Id, DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.Username, r.Action,
                    r.EntityName, r.RecordId,
                    AuditFilter.Redact(r.Action, r.EntityName, Parse(r.BeforeJson), user, model),
                    AuditFilter.Redact(r.Action, r.EntityName, Parse(r.AfterJson), user, model), r.IpAddress)),
                total,
            };
        }).Requires(Capability.Audit);
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static JsonElement? Parse(string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private sealed record Row(long Id, DateTime At, string? Username, string Action, string? EntityName, string? RecordId,
        string? BeforeJson, string? AfterJson, string? IpAddress);
}
