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

    public const int MaxUnit = 100;

    public sealed record EntityPermissionDto(string Entity, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
    public sealed record RoleDto(int Id, string Name, string? Description, bool IsSystem, bool IsSuperuser,
        bool CanManageModel, bool CanManageUsers, bool CanViewAudit, IReadOnlyList<EntityPermissionDto> Permissions);
    public sealed record RoleRequest(string? Name, string? Description, bool CanManageModel, bool CanManageUsers,
        bool CanViewAudit, EntityPermissionDto[]? Permissions);

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization();

        g.MapGet("/users", async (HttpContext http, Db db) =>
        {
            RequireUserAdmin(http);
            await using var cn = await db.OpenAsync();
            return await LoadUsersAsync(cn);
        });
        g.MapPost("/users", CreateUser);
        g.MapPut("/users/{id:int}", UpdateUser);
        // Spec 006: unidades en uso, como sugerencias al asignar la unidad de un usuario.
        g.MapGet("/units", async (HttpContext http, Db db) =>
        {
            RequireUserAdmin(http);
            await using var cn = await db.OpenAsync();
            return await cn.QueryAsync<string>("SELECT DISTINCT Unit FROM meta.[User] WHERE Unit IS NOT NULL ORDER BY Unit");
        });

        // Los modeladores también leen los roles para configurar reglas "obligatorio para rol".
        g.MapGet("/roles", async (HttpContext http, Db db) =>
        {
            var u = http.CurrentUser();
            if (!u.CanManageUsers && !u.CanManageModel) throw new Forbidden();
            await using var cn = await db.OpenAsync();
            return await LoadRolesAsync(cn);
        });
        // Módulos (spec 002 H7): solo superusuario, porque instalan roles y permisos.
        g.MapGet("/modules", async (HttpContext http, ModuleService modules) =>
        {
            if (!http.CurrentUser().IsSuperuser) throw new Forbidden();
            return await modules.ListAsync();
        });
        g.MapPost("/modules/{id}/preview", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
        {
            if (!http.CurrentUser().IsSuperuser) throw new Forbidden();
            return await modules.PreviewAsync(id, ct);
        });
        g.MapPost("/modules/{id}/install", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
            new { changeSetId = await modules.InstallAsync(id, http, ct) });
        g.MapPost("/modules/{id}/upgrade/preview", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
        {
            if (!http.CurrentUser().IsSuperuser) throw new Forbidden();
            return await modules.UpgradePreviewAsync(id, ct);
        });
        g.MapPost("/modules/{id}/upgrade", async (string id, HttpContext http, ModuleService modules, CancellationToken ct) =>
            new { changeSetId = await modules.UpgradeAsync(id, http, ct) });

        g.MapPost("/roles", (RoleRequest req, HttpContext http, Db db, MetadataStore store, CancellationToken ct) => SaveRole(null, req, http, db, store, ct));
        g.MapPut("/roles/{id:int}", (int id, RoleRequest req, HttpContext http, Db db, MetadataStore store, CancellationToken ct) => SaveRole(id, req, http, db, store, ct));
    }

    private static void RequireUserAdmin(HttpContext http)
    {
        if (!http.CurrentUser().CanManageUsers) throw new Forbidden();
    }

    // ---------------- Usuarios ----------------

    private static async Task<IResult> CreateUser(CreateUserRequest req, HttpContext http, Db db, PasswordService passwords)
    {
        RequireUserAdmin(http);
        var errors = new Dictionary<string, string[]>();
        var username = req.Username?.Trim() ?? "";
        if (!UsernamePattern().IsMatch(username)) errors["username"] = ["Usa de 3 a 100 caracteres: letras, números, '.', '_' o '-'."];
        var displayName = req.DisplayName?.Trim() ?? "";
        if (displayName.Length is 0 or > 150) errors["displayName"] = ["El nombre es obligatorio (máx. 150)."];
        if (!PasswordService.MeetsPolicy(req.Password, out var pwdError, username)) errors["password"] = [pwdError];
        var unit = NormalizeUnit(req.Unit, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.[User] WHERE Username = @username", new { username }, tx) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["username"] = ["Ese usuario ya existe."] });

        var id = await cn.ExecuteScalarAsync<int>(
            "INSERT meta.[User] (Username, DisplayName, PasswordHash, Unit) OUTPUT inserted.Id VALUES (@username, @displayName, @hash, @unit)",
            new { username, displayName, hash = passwords.Hash(req.Password!), unit }, tx);
        await SetRolesAsync(cn, tx, id, req.RoleIds ?? []);
        var companyIds = req.CompanyIds ?? await cn.QueryAsync<int>("SELECT Id FROM meta.Company WHERE IsDefault = 1", transaction: tx);
        await SetCompaniesAsync(cn, tx, id, companyIds);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UserChange, "Usuario", id.ToString(),
            after: new { username, displayName, roleIds = req.RoleIds, unit, companyIds });
        await tx.CommitAsync();
        return Results.Created($"/api/admin/users/{id}", (await LoadUsersAsync(cn)).First(u => u.Id == id));
    }

    private static async Task<UserDto> UpdateUser(int id, UpdateUserRequest req, HttpContext http, Db db, PasswordService passwords)
    {
        RequireUserAdmin(http);
        var me = http.CurrentUser();
        var errors = new Dictionary<string, string[]>();
        var displayName = req.DisplayName?.Trim() ?? "";
        if (displayName.Length is 0 or > 150) errors["displayName"] = ["El nombre es obligatorio (máx. 150)."];
        if (id == me.Id && !req.IsActive) errors["isActive"] = ["No puedes desactivar tu propia cuenta."];
        var unit = NormalizeUnit(req.Unit, errors);

        await using var cn = await db.OpenAsync();
        var before = (await LoadUsersAsync(cn)).FirstOrDefault(u => u.Id == id) ?? throw new NotFound("El usuario no existe.");
        if (!string.IsNullOrEmpty(req.Password) && !PasswordService.MeetsPolicy(req.Password, out var pwdError, before.Username))
            errors["password"] = [pwdError];
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var tx = await cn.BeginTransactionAsync();
        var roleIds = req.RoleIds ?? [];
        var rolesChanged = !before.RoleIds.Order().SequenceEqual(roleIds.Distinct().Order());
        var companyIds = req.CompanyIds ?? before.CompanyIds;
        var companiesChanged = !before.CompanyIds.Order().SequenceEqual(companyIds.Distinct().Order());
        // Cambiar contraseña, roles, unidad, empresas o desactivar invalida las sesiones abiertas (nuevo SecurityStamp):
        // la unidad (spec 006) y las empresas (spec 010) deciden qué registros ve.
        var revoke = !string.IsNullOrEmpty(req.Password) || rolesChanged || companiesChanged || (before.IsActive && !req.IsActive) || before.Unit != unit;
        await cn.ExecuteAsync("""
            UPDATE meta.[User] SET DisplayName = @displayName, IsActive = @IsActive, Unit = @unit,
                PasswordHash = COALESCE(@hash, PasswordHash),
                FailedAttempts = CASE WHEN @hash IS NULL THEN FailedAttempts ELSE 0 END,
                LockoutEnd = CASE WHEN @hash IS NULL THEN LockoutEnd ELSE NULL END,
                SecurityStamp = CASE WHEN @revoke = 1 THEN NEWID() ELSE SecurityStamp END
             WHERE Id = @id
            """, new
        {
            id, displayName, req.IsActive, revoke, unit,
            hash = string.IsNullOrEmpty(req.Password) ? null : passwords.Hash(req.Password),
        }, tx);
        await SetRolesAsync(cn, tx, id, roleIds);
        if (companiesChanged) await SetCompaniesAsync(cn, tx, id, companyIds);
        await EnsureSuperuserRemainsAsync(cn, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UserChange, "Usuario", id.ToString(),
            before: new { before.DisplayName, before.IsActive, before.RoleIds, before.Unit, before.CompanyIds },
            after: new { DisplayName = displayName, req.IsActive, RoleIds = roleIds, Unit = unit, CompanyIds = companyIds, PasswordChanged = !string.IsNullOrEmpty(req.Password) });
        await tx.CommitAsync();
        return (await LoadUsersAsync(cn)).First(u => u.Id == id);
    }

    private static string? NormalizeUnit(string? unit, Dictionary<string, string[]> errors)
    {
        var u = unit?.Trim();
        if (u?.Length > MaxUnit) errors["unit"] = [$"La unidad admite máximo {MaxUnit} caracteres."];
        return string.IsNullOrEmpty(u) ? null : u;
    }

    private static async Task SetRolesAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, int userId, int[] roleIds)
    {
        var ids = roleIds.Distinct().ToArray();
        var existing = (await cn.QueryAsync<int>("SELECT Id FROM meta.Role WHERE Id IN @ids", new { ids }, tx)).Count();
        if (existing != ids.Length) throw new ValidationFailed(new Dictionary<string, string[]> { ["roleIds"] = ["Hay roles que no existen."] });
        await cn.ExecuteAsync("DELETE meta.UserRole WHERE UserId = @userId", new { userId }, tx);
        foreach (var roleId in ids)
            await cn.ExecuteAsync("INSERT meta.UserRole (UserId, RoleId) VALUES (@userId, @roleId)", new { userId, roleId }, tx);
    }

    /// <summary>Spec 010: empresas que puede elegir. Si su empresa actual ya no está entre ellas, se olvida.</summary>
    private static async Task SetCompaniesAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, int userId, IEnumerable<int> companyIds)
    {
        var ids = companyIds.Distinct().ToArray();
        var existing = (await cn.QueryAsync<int>("SELECT Id FROM meta.Company WHERE Id IN @ids", new { ids }, tx)).Count();
        if (existing != ids.Length) throw new ValidationFailed(new Dictionary<string, string[]> { ["companyIds"] = ["Hay empresas que no existen."] });
        await cn.ExecuteAsync("DELETE meta.UserCompany WHERE UserId = @userId", new { userId }, tx);
        foreach (var companyId in ids)
            await cn.ExecuteAsync("INSERT meta.UserCompany (UserId, CompanyId) VALUES (@userId, @companyId)", new { userId, companyId }, tx);
        await cn.ExecuteAsync("UPDATE meta.[User] SET CompanyId = NULL WHERE Id = @userId AND CompanyId NOT IN (SELECT CompanyId FROM meta.UserCompany WHERE UserId = @userId)",
            new { userId }, tx);
    }

    private static async Task EnsureSuperuserRemainsAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx)
    {
        var count = await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT u.Id) FROM meta.[User] u JOIN meta.UserRole ur ON ur.UserId = u.Id
              JOIN meta.Role r ON r.Id = ur.RoleId WHERE u.IsActive = 1 AND r.IsSuperuser = 1
            """, transaction: tx);
        if (count == 0) throw new Conflict("Debe quedar al menos un administrador activo.");
    }

    private static async Task<IReadOnlyList<UserDto>> LoadUsersAsync(System.Data.Common.DbConnection cn)
    {
        var users = await cn.QueryAsync<UserRow>("SELECT Id, Username, DisplayName, IsActive, LastLoginAt, LockoutEnd, Unit FROM meta.[User] WHERE IsService = 0 ORDER BY Username");
        var roles = (await cn.QueryAsync<(int UserId, int RoleId)>("SELECT UserId, RoleId FROM meta.UserRole")).ToLookup(x => x.UserId, x => x.RoleId);
        var companies = (await cn.QueryAsync<(int UserId, int CompanyId)>("SELECT UserId, CompanyId FROM meta.UserCompany")).ToLookup(x => x.UserId, x => x.CompanyId);
        return users.Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.IsActive, u.LastLoginAt,
            u.LockoutEnd > DateTime.UtcNow ? u.LockoutEnd : null, roles[u.Id].ToList(), u.Unit, companies[u.Id].Order().ToList())).ToList();
    }

    // ---------------- Roles ----------------

    private static async Task<RoleDto> SaveRole(int? id, RoleRequest req, HttpContext http, Db db, MetadataStore store, CancellationToken ct)
    {
        RequireUserAdmin(http);
        var name = req.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (name.Length is 0 or > 100) errors["name"] = ["El nombre es obligatorio (máx. 100)."];
        if (req.Description?.Length > 300) errors["description"] = ["Máximo 300 caracteres."];
        var model = await store.GetAsync(ct);
        var perms = req.Permissions ?? [];
        if (perms.Any(p => !model.ContainsKey(p.Entity))) errors["permissions"] = ["Hay catálogos que no existen."];
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        RoleDto? before = null;
        if (id is not null)
        {
            before = (await LoadRolesAsync(cn, tx)).FirstOrDefault(r => r.Id == id) ?? throw new NotFound("El rol no existe.");
            if (before.IsSystem && !string.Equals(before.Name, name, StringComparison.Ordinal))
                throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Los roles del sistema no se pueden renombrar."] });
        }
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.Role WHERE Name = @name AND Id <> ISNULL(@id, 0)", new { name, id }, tx) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe un rol con ese nombre."] });

        // El rol superusuario no se puede degradar desde la aplicación.
        var p = new { id, name, description = req.Description?.Trim(), req.CanManageModel, req.CanManageUsers, req.CanViewAudit };
        var roleId = id ?? await cn.ExecuteScalarAsync<int>("""
            INSERT meta.Role (Name, Description, CanManageModel, CanManageUsers, CanViewAudit)
            OUTPUT inserted.Id VALUES (@name, @description, @CanManageModel, @CanManageUsers, @CanViewAudit)
            """, p, tx);
        if (id is not null)
            await cn.ExecuteAsync("""
                UPDATE meta.Role SET Name = @name, Description = @description,
                    CanManageModel = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanManageModel END,
                    CanManageUsers = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanManageUsers END,
                    CanViewAudit = CASE WHEN IsSuperuser = 1 THEN 1 ELSE @CanViewAudit END
                 WHERE Id = @id
                """, p, tx);

        await cn.ExecuteAsync("DELETE meta.EntityPermission WHERE RoleId = @roleId", new { roleId }, tx);
        foreach (var perm in perms.DistinctBy(x => x.Entity, StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.CanRead || x.CanCreate || x.CanUpdate || x.CanDelete))
            await cn.ExecuteAsync("""
                INSERT meta.EntityPermission (RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete)
                VALUES (@roleId, @Entity, @CanRead, @CanCreate, @CanUpdate, @CanDelete)
                """, new { roleId, Entity = model[perm.Entity].Name, perm.CanRead, perm.CanCreate, perm.CanUpdate, perm.CanDelete }, tx);

        var after = (await LoadRolesAsync(cn, tx)).First(r => r.Id == roleId);
        await Audit.WriteAsync(cn, tx, http, AuditActions.RoleChange, "Rol", roleId.ToString(), before, after);
        await tx.CommitAsync(ct);
        return after;
    }

    private static async Task<IReadOnlyList<RoleDto>> LoadRolesAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx = null)
    {
        var roles = await cn.QueryAsync<RoleRow>(
            "SELECT Id, Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit FROM meta.Role ORDER BY Name",
            transaction: tx);
        var perms = (await cn.QueryAsync<PermRow>(
            "SELECT RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete FROM meta.EntityPermission", transaction: tx))
            .ToLookup(x => x.RoleId);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsSuperuser, r.CanManageModel,
            r.CanManageUsers, r.CanViewAudit,
            perms[r.Id].Select(x => new EntityPermissionDto(x.EntityName, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)).ToList())).ToList();
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
            if (!user.CanViewAudit) throw new Forbidden();
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
        }).RequireAuthorization();
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static JsonElement? Parse(string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private sealed record Row(long Id, DateTime At, string? Username, string Action, string? EntityName, string? RecordId,
        string? BeforeJson, string? AfterJson, string? IpAddress);
}
