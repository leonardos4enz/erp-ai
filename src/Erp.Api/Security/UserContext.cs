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
    /// <summary>Spec 010: empresa en la que trabaja (null si no tiene ninguna activa asignada).</summary>
    public CompanyInfo? Company { get; init; }
    /// <summary>Spec 010: empresas activas que puede elegir (el superusuario, todas); la predeterminada primero.</summary>
    public IReadOnlyList<CompanyInfo> Companies { get; init; } = [];
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
    public static UserContext User(string role, IReadOnlyDictionary<string, EntityAccess> entities, CompanyInfo? company = null) => new()
    {
        Id = 0, Username = $"webhook:{role}", DisplayName = role, SecurityStamp = Guid.Empty,
        Roles = [role], Entities = entities, Company = company, Companies = company is null ? [] : [company],
    };

    /// <param name="company">Spec 010: la empresa del evento (la de quien lo disparó).</param>
    public static async Task<UserContext> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx, string role,
        CompanyInfo? company = null)
    {
        // Spec 015: el rol de servicio también recibe lo que hereda.
        var direct = await cn.QueryAsync<int>("SELECT Id FROM meta.Role WHERE Name = @role", new { role }, tx);
        var graph = await RoleGraph.LoadAsync(cn, tx);
        var ids = RoleGraph.Expand(direct, graph.Edges).ToArray();
        var perms = await cn.QueryAsync<PermRow>(
            "SELECT p.EntityName, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete FROM meta.EntityPermission p WHERE p.RoleId IN @ids",
            new { ids }, tx);
        var inherited = ids.Select(graph.Name).OfType<string>().Where(n => !string.Equals(n, role, StringComparison.OrdinalIgnoreCase));
        var user = User(role, RoleGraph.Union(perms), company);
        return new UserContext
        {
            Id = user.Id, Username = user.Username, DisplayName = user.DisplayName, SecurityStamp = user.SecurityStamp,
            Roles = [role, .. inherited], Entities = user.Entities, Company = user.Company, Companies = user.Companies,
        };
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
            SELECT Id, Username, DisplayName, SecurityStamp, Unit, IsService, CompanyId FROM meta.[User] WHERE Id = @userId AND IsActive = 1 AND IsTeam = 0;
            SELECT r.Id, r.Name, r.IsSuperuser, r.CanManageModel, r.CanManageUsers, r.CanViewAudit
              FROM meta.UserRole ur JOIN meta.Role r ON r.Id = ur.RoleId WHERE ur.UserId = @userId;
            SELECT RoleId, ParentRoleId FROM meta.RoleInherit;
            SELECT Id, Name FROM meta.Role;
            SELECT p.RoleId, p.EntityName, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete FROM meta.EntityPermission p
             WHERE p.RoleId IN (SELECT ur.RoleId FROM meta.UserRole ur WHERE ur.UserId = @userId)
                OR EXISTS (SELECT 1 FROM meta.RoleInherit ri WHERE ri.ParentRoleId = p.RoleId);
            SELECT c.Id, c.Code, c.Name, c.BaseCurrency,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM meta.UserCompany uc WHERE uc.UserId = @userId AND uc.CompanyId = c.Id) THEN 1 ELSE 0 END AS bit) AS Assigned
              FROM meta.Company c WHERE c.IsActive = 1 ORDER BY c.IsDefault DESC, c.Name;
            """, new { userId });

        var user = await multi.ReadSingleOrDefaultAsync<UserRow>();
        if (user is null || user.SecurityStamp != expectedStamp) return null;
        var roles = (await multi.ReadAsync<RoleRow>()).ToList();
        var graph = new RoleGraph((await multi.ReadAsync<(int RoleId, int ParentRoleId)>()).ToList(),
            (await multi.ReadAsync<(int Id, string Name)>()).ToDictionary(r => r.Id, r => r.Name));
        // Spec 015: roles efectivos = los asignados y aquellos de los que heredan. Las capacidades administrativas solo
        // vienen de los asignados (un rol del que se hereda no puede tenerlas).
        var effective = RoleGraph.Expand(roles.Select(r => r.Id), graph.Edges);
        var perms = RoleGraph.Union((await multi.ReadAsync<(int RoleId, string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete)>())
            .Where(p => effective.Contains(p.RoleId)).Select(p => new PermRow(p.EntityName, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete)));
        var direct = roles.Select(r => r.Id).ToHashSet();
        var inherited = effective.Where(id => !direct.Contains(id)).Select(graph.Name).OfType<string>().Order(StringComparer.Ordinal);
        var superuser = roles.Any(r => r.IsSuperuser);
        // Spec 010: la empresa elegida solo vale mientras siga activa y asignada (el superusuario puede usar cualquiera activa).
        var companies = (await multi.ReadAsync<CompanyRow>()).Where(c => superuser || c.Assigned)
            .Select(c => new CompanyInfo(c.Id, c.Code, c.Name, c.BaseCurrency.Trim())).ToList();

        return new UserContext
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Unit = user.Unit,
            IsService = user.IsService,
            SecurityStamp = user.SecurityStamp,
            Roles = [.. roles.Select(r => r.Name), .. inherited],
            IsSuperuser = superuser,
            Companies = companies,
            Company = companies.FirstOrDefault(c => c.Id == user.CompanyId) ?? companies.FirstOrDefault(),
            CanManageModel = roles.Any(r => r.IsSuperuser || r.CanManageModel),
            CanManageUsers = roles.Any(r => r.IsSuperuser || r.CanManageUsers),
            CanViewAudit = roles.Any(r => r.IsSuperuser || r.CanViewAudit),
            Entities = perms,
        };
    }

    private sealed record UserRow(int Id, string Username, string DisplayName, Guid SecurityStamp, string? Unit, bool IsService, int? CompanyId);
    private sealed record CompanyRow(int Id, string Code, string Name, string BaseCurrency, bool Assigned);
    private sealed record RoleRow(int Id, string Name, bool IsSuperuser, bool CanManageModel, bool CanManageUsers, bool CanViewAudit);
}

public sealed record PermRow(string EntityName, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);

/// <summary>
/// Spec 015: herencia de roles. <c>Edges</c> = (rol, rol del que hereda). El cierre transitivo da los roles efectivos; la
/// validación al guardar impide ciclos, pero el recorrido tolera uno (no repite un rol ya visitado).
/// </summary>
public sealed record RoleGraph(IReadOnlyList<(int RoleId, int ParentRoleId)> Edges, IReadOnlyDictionary<int, string> Names)
{
    public const int MaxParents = 10;
    public const int MaxDepth = 5;

    public string? Name(int id) => Names.GetValueOrDefault(id);

    public static async Task<RoleGraph> LoadAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx)
    {
        var edges = (await cn.QueryAsync<(int RoleId, int ParentRoleId)>("SELECT RoleId, ParentRoleId FROM meta.RoleInherit", transaction: tx)).ToList();
        var names = (await cn.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM meta.Role", transaction: tx)).ToDictionary(r => r.Id, r => r.Name);
        return new(edges, names);
    }

    /// <summary>Los roles dados y todos aquellos de los que heredan, a cualquier profundidad.</summary>
    public static HashSet<int> Expand(IEnumerable<int> direct, IReadOnlyList<(int RoleId, int ParentRoleId)> edges)
    {
        var parents = edges.ToLookup(x => x.RoleId, x => x.ParentRoleId);
        var seen = new HashSet<int>();
        var pending = new Stack<int>(direct);
        while (pending.TryPop(out var id))
            if (seen.Add(id))
                foreach (var p in parents[id]) pending.Push(p);
        return seen;
    }

    /// <summary>Niveles de herencia por encima de <paramref name="role"/> (0 si no hereda); null si hay un ciclo.</summary>
    public static int? Depth(int role, IReadOnlyList<(int RoleId, int ParentRoleId)> edges)
    {
        var parents = edges.ToLookup(x => x.RoleId, x => x.ParentRoleId);
        int? Walk(int id, HashSet<int> path)
        {
            if (!path.Add(id)) return null;
            var max = 0;
            foreach (var p in parents[id])
            {
                if (Walk(p, path) is not { } d) return null;
                max = Math.Max(max, d + 1);
            }
            path.Remove(id);
            return max;
        }
        return Walk(role, []);
    }

    /// <summary>Profundidad máxima de toda la herencia (los roles que heredan de este también cuentan); null si hay ciclo.</summary>
    public static int? MaxDepthOf(IReadOnlyList<(int RoleId, int ParentRoleId)> edges)
    {
        var max = 0;
        foreach (var role in edges.Select(e => e.RoleId).Distinct())
        {
            if (Depth(role, edges) is not { } d) return null;
            max = Math.Max(max, d);
        }
        return max;
    }

    /// <summary>Unión de permisos por catálogo (lo más permisivo de los roles).</summary>
    public static IReadOnlyDictionary<string, EntityAccess> Union(IEnumerable<PermRow> perms) =>
        perms.GroupBy(p => p.EntityName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => new EntityAccess(g.Any(p => p.CanRead), g.Any(p => p.CanCreate), g.Any(p => p.CanUpdate), g.Any(p => p.CanDelete)),
            StringComparer.OrdinalIgnoreCase);
}

public static class UserContextExtensions
{
    internal const string ItemKey = "erp.user";

    /// <summary>Usuario autenticado de la petición. Solo se usa en endpoints que exigen autenticación.</summary>
    public static UserContext CurrentUser(this HttpContext http) =>
        http.Items[ItemKey] as UserContext ?? throw new AppException(StatusCodes.Status401Unauthorized, "Sesión no válida.");
}
