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
    public IReadOnlyDictionary<string, EntityAccess> Entities { get; init; } = new Dictionary<string, EntityAccess>();

    public EntityAccess Access(string entity) =>
        IsSuperuser ? EntityAccess.Full
        : Entities.TryGetValue(entity, out var a) ? a : EntityAccess.None;

    public bool HasRole(IEnumerable<string>? roles) =>
        roles is not null && roles.Any(r => Roles.Contains(r, StringComparer.OrdinalIgnoreCase));
}

public sealed class UserContextLoader(Db db)
{
    /// <summary>Devuelve null si el usuario no existe, está inactivo o su sello de seguridad cambió.</summary>
    public async Task<UserContext?> LoadAsync(int userId, Guid expectedStamp, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        using var multi = await cn.QueryMultipleAsync("""
            SELECT Id, Username, DisplayName, SecurityStamp, Unit FROM meta.[User] WHERE Id = @userId AND IsActive = 1;
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

    private sealed record UserRow(int Id, string Username, string DisplayName, Guid SecurityStamp, string? Unit);
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
