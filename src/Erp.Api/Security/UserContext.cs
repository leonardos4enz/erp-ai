using Dapper;
using Erp.Api.Infrastructure;

namespace Erp.Api.Security;

public sealed record EntityAccess(bool Read, bool Create, bool Update, bool Delete)
{
    public static readonly EntityAccess None = new(false, false, false, false);
    public static readonly EntityAccess Full = new(true, true, true, true);
}

/// <summary>Identidad y permisos efectivos del usuario de la petición (unión de sus roles).</summary>
public sealed class UserContext
{
    public required int Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Spec 006: unidad (área o sucursal) para el alcance «de mi unidad».</summary>
    public string? Unit { get; init; }
    public required Guid SecurityStamp { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public bool IsSuperuser { get; init; }
    public bool CanManageModel { get; init; }
    public bool CanManageUsers { get; init; }
    public bool CanViewAudit { get; init; }
    /// <summary>Spec 008: cuenta de servicio de una clave de API.</summary>
    public bool IsService { get; init; }
    public IReadOnlyDictionary<string, EntityAccess> Entities { get; init; } = new Dictionary<string, EntityAccess>();

    public EntityAccess Access(string entity) =>
        IsSuperuser ? EntityAccess.Full
        : Entities.TryGetValue(entity, out var a) ? a : EntityAccess.None;

    public bool HasRole(IEnumerable<string>? roles) =>
        roles is not null && roles.Any(r => Roles.Contains(r, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Spec 008: usuario sintético con un solo rol de servicio (sin superusuario) para armar la carga de un webhook tal como
/// la vería ese rol. Id 0: no coincide con ningún dueño ni creador.
/// </summary>
public static class ServiceRole
{
    public static UserContext User(string role, IReadOnlyDictionary<string, EntityAccess> entities) => new()
    {
        Id = 0, Username = $"webhook:{role}", DisplayName = role, SecurityStamp = Guid.Empty,
        Roles = [role], Entities = entities,
    };

    public static async Task<UserContext> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx, string role)
    {
        var perms = await cn.QueryAsync<(string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete)>("""
            SELECT p.EntityName, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete
              FROM meta.EntityPermission p JOIN meta.Role r ON r.Id = p.RoleId WHERE r.Name = @role
            """, new { role }, tx);
        return User(role, perms.ToDictionary(p => p.EntityName, p => new EntityAccess(p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete),
            StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class UserContextLoader(Db db)
{
    /// <summary>
    /// Spec 008: cuenta de servicio de una clave de API (sin sello: la clave es la credencial). Null si está inactiva, no
    /// es de servicio o tiene un rol de administración (defensa en profundidad: la API ya lo impide al asignar roles).
    /// </summary>
    public async Task<UserContext?> LoadServiceAsync(int userId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        var stamp = await cn.ExecuteScalarAsync<Guid?>("SELECT SecurityStamp FROM meta.[User] WHERE Id = @userId AND IsActive = 1 AND IsService = 1", new { userId });
        if (stamp is null) return null;
        var ctx = await LoadAsync(userId, stamp.Value, ct);
        return ctx is null || ctx.IsSuperuser || ctx.CanManageModel || ctx.CanManageUsers ? null : ctx;
    }

    /// <summary>Devuelve null si el usuario no existe, está inactivo o su sello de seguridad cambió.</summary>
    public async Task<UserContext?> LoadAsync(int userId, Guid expectedStamp, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        using var multi = await cn.QueryMultipleAsync("""
            SELECT Id, Username, DisplayName, SecurityStamp, Unit, IsService FROM meta.[User] WHERE Id = @userId AND IsActive = 1;
            SELECT r.Name, r.IsSuperuser, r.CanManageModel, r.CanManageUsers, r.CanViewAudit
              FROM meta.UserRole ur JOIN meta.Role r ON r.Id = ur.RoleId WHERE ur.UserId = @userId;
            SELECT p.EntityName,
                   CAST(MAX(CAST(p.CanRead AS int)) AS bit) AS CanRead, CAST(MAX(CAST(p.CanCreate AS int)) AS bit) AS CanCreate,
                   CAST(MAX(CAST(p.CanUpdate AS int)) AS bit) AS CanUpdate, CAST(MAX(CAST(p.CanDelete AS int)) AS bit) AS CanDelete
              FROM meta.UserRole ur JOIN meta.EntityPermission p ON p.RoleId = ur.RoleId
             WHERE ur.UserId = @userId GROUP BY p.EntityName;
            """, new { userId });

        var user = await multi.ReadSingleOrDefaultAsync<UserRow>();
        if (user is null || user.SecurityStamp != expectedStamp) return null;
        var roles = (await multi.ReadAsync<RoleRow>()).ToList();
        var perms = await multi.ReadAsync<PermRow>();

        return new UserContext
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Unit = user.Unit,
            IsService = user.IsService,
            SecurityStamp = user.SecurityStamp,
            Roles = roles.Select(r => r.Name).ToList(),
            IsSuperuser = roles.Any(r => r.IsSuperuser),
            CanManageModel = roles.Any(r => r.IsSuperuser || r.CanManageModel),
            CanManageUsers = roles.Any(r => r.IsSuperuser || r.CanManageUsers),
            CanViewAudit = roles.Any(r => r.IsSuperuser || r.CanViewAudit),
            Entities = perms.ToDictionary(p => p.EntityName, p => new EntityAccess(p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed record UserRow(int Id, string Username, string DisplayName, Guid SecurityStamp, string? Unit, bool IsService);
    private sealed record RoleRow(string Name, bool IsSuperuser, bool CanManageModel, bool CanManageUsers, bool CanViewAudit);
    private sealed record PermRow(string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
}

public static class UserContextExtensions
{
    internal const string ItemKey = "erp.user";

    /// <summary>Usuario autenticado de la petición. Solo se usa en endpoints que exigen autenticación.</summary>
    public static UserContext CurrentUser(this HttpContext http) =>
        http.Items[ItemKey] as UserContext ?? throw new AppException(StatusCodes.Status401Unauthorized, "Sesión no válida.");
}
