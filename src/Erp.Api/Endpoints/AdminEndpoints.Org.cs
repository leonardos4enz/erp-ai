using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>Spec 015: unidades de negocio en árbol y equipos (dueños de registros). Requieren administrar usuarios.</summary>
public static partial class AdminEndpoints
{
    public const int MaxUnitDepth = 10;
    public const int MaxTeamMembers = 500;

    public sealed record BusinessUnitDto(int Id, string Name, int? ParentId, int Depth, int Users, int Teams);
    public sealed record CreateUnitRequest(string? Name, int? ParentId);
    public sealed record MoveUnitRequest(int? ParentId);
    public sealed record UnitSnapshot(int? ParentId);

    public sealed record TeamDto(int Id, string Name, string? Unit, bool IsActive, IReadOnlyList<int> MemberIds);
    public sealed record TeamRequest(string? Name, string? Unit, bool IsActive = true, int[]? MemberIds = null);

    private static void MapOrg(RouteGroupBuilder g)
    {
        g.MapGet("/units", async (HttpContext http, Db db) =>
        {
            RequireUserAdmin(http);
            await using var cn = await db.OpenAsync();
            return await LoadUnitsAsync(cn, null);
        });
        g.MapPost("/units", CreateUnit);
        g.MapPut("/units/{id:int}", (int id, MoveUnitRequest req, HttpContext http, Db db, SecurityApproval gate) => MoveUnit(id, req, http, db, gate, null));

        g.MapGet("/teams", async (HttpContext http, Db db) =>
        {
            RequireUserAdmin(http);
            await using var cn = await db.OpenAsync();
            return await LoadTeamsAsync(cn, null);
        });
        g.MapPost("/teams", (TeamRequest req, HttpContext http, Db db, PasswordService passwords, SecurityApproval gate) =>
            SaveTeam(null, req, http, db, passwords, gate, null));
        g.MapPut("/teams/{id:int}", (int id, TeamRequest req, HttpContext http, Db db, PasswordService passwords, SecurityApproval gate) =>
            SaveTeam(id, req, http, db, passwords, gate, null));
    }

    // ---------------- Unidades ----------------

    private sealed record UnitRow(int Id, string Name, int? ParentId, string Path, int Users, int Teams);

    private static async Task<List<UnitRow>> UnitRowsAsync(DbConnection cn, DbTransaction? tx) =>
        (await cn.QueryAsync<UnitRow>("""
            SELECT b.Id, b.Name, b.ParentId, b.Path,
                   (SELECT COUNT(*) FROM meta.[User] u WHERE u.Unit = b.Name AND u.IsTeam = 0 AND u.IsService = 0) AS Users,
                   (SELECT COUNT(*) FROM meta.[User] u WHERE u.Unit = b.Name AND u.IsTeam = 1) AS Teams
              FROM meta.BusinessUnit b
            """, transaction: tx)).ToList();

    private static int Depth(string path) => path.Count(c => c == '/') - 2;

    /// <summary>En orden de árbol: cada unidad seguida de las que cuelgan de ella, hermanas por nombre.</summary>
    private static async Task<IReadOnlyList<BusinessUnitDto>> LoadUnitsAsync(DbConnection cn, DbTransaction? tx)
    {
        var rows = await UnitRowsAsync(cn, tx);
        var children = rows.ToLookup(r => r.ParentId);
        var result = new List<BusinessUnitDto>();
        void Add(int? parent)
        {
            foreach (var r in children[parent].OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                result.Add(new BusinessUnitDto(r.Id, r.Name, r.ParentId, Depth(r.Path), r.Users, r.Teams));
                Add(r.Id);
            }
        }
        Add(null);
        return result;
    }

    private static async Task<IResult> CreateUnit(CreateUnitRequest req, HttpContext http, Db db)
    {
        RequireUserAdmin(http);
        var errors = new Dictionary<string, string[]>();
        var name = NormalizeUnit(req.Name, errors);
        if (name is null) errors["name"] = ["Escribe el nombre de la unidad."];
        else if (errors.Remove("unit", out var e)) errors["name"] = e;
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var rows = await UnitRowsAsync(cn, tx);
        if (rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe una unidad con ese nombre."] });
        var parent = req.ParentId is { } pid ? rows.FirstOrDefault(r => r.Id == pid)
            ?? throw new ValidationFailed(new Dictionary<string, string[]> { ["parentId"] = ["La unidad superior no existe."] }) : null;
        if (parent is not null && Depth(parent.Path) + 1 >= MaxUnitDepth)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["parentId"] = [$"El árbol admite máximo {MaxUnitDepth} niveles."] });

        // Dar de alta una unidad no amplía el acceso de nadie: nadie pertenece a ella todavía.
        var id = await cn.ExecuteScalarAsync<int>("INSERT meta.BusinessUnit (Name, ParentId, Path) OUTPUT inserted.Id VALUES (@name, @ParentId, N'')",
            new { name, req.ParentId }, tx);
        await cn.ExecuteAsync("UPDATE meta.BusinessUnit SET Path = @path WHERE Id = @id", new { id, path = $"{parent?.Path ?? "/"}{id}/" }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UnitChange, "Unidad", id.ToString(), after: new { name, parentId = req.ParentId });
        var dto = (await LoadUnitsAsync(cn, tx)).First(u => u.Id == id);
        await tx.CommitAsync();
        return Results.Created($"/api/admin/units/{id}", dto);
    }

    internal static async Task<IResult> MoveUnit(int id, MoveUnitRequest req, HttpContext http, Db db, SecurityApproval gate, SecurityChangeApproval? approving)
    {
        RequireUserAdmin(http);
        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        // Bloquea el árbol: dos movimientos simultáneos no pueden formar un ciclo entre ambos.
        await cn.ExecuteAsync("SELECT COUNT(*) FROM meta.BusinessUnit WITH (TABLOCKX, HOLDLOCK)", transaction: tx);
        var rows = await UnitRowsAsync(cn, tx);
        var unit = rows.FirstOrDefault(r => r.Id == id) ?? throw new NotFound("La unidad no existe.");
        approving?.Verify(new UnitSnapshot(unit.ParentId));
        UnitRow? parent = null;
        if (req.ParentId is { } pid)
        {
            parent = rows.FirstOrDefault(r => r.Id == pid)
                ?? throw new ValidationFailed(new Dictionary<string, string[]> { ["parentId"] = ["La unidad superior no existe."] });
            if (parent.Path.StartsWith(unit.Path, StringComparison.Ordinal))
                throw new ValidationFailed(new Dictionary<string, string[]> { ["parentId"] = ["Una unidad no puede quedar debajo de sí misma ni de las que cuelgan de ella."] });
            var deepest = rows.Where(r => r.Path.StartsWith(unit.Path, StringComparison.Ordinal)).Max(r => Depth(r.Path)) - Depth(unit.Path);
            if (Depth(parent.Path) + 1 + deepest >= MaxUnitDepth)
                throw new ValidationFailed(new Dictionary<string, string[]> { ["parentId"] = [$"El árbol admite máximo {MaxUnitDepth} niveles."] });
        }
        if (unit.ParentId == req.ParentId) return Results.Ok((await LoadUnitsAsync(cn, tx)).First(u => u.Id == id));

        // Spec 015 (H4): moverla bajo otra amplía lo que ven sus nuevos superiores.
        if (gate.MustWait(approving, SecurityGrants.UnitMove(unit.ParentId, req.ParentId)))
        {
            var change = await gate.RequestAsync(cn, tx, http, SecurityChangeKinds.UnitMove, id,
                $"Mover la unidad {unit.Name} bajo {parent?.Name ?? "la raíz"}", req, new UnitSnapshot(unit.ParentId));
            await tx.CommitAsync();
            return SecurityApproval.Accepted(change);
        }

        var newPath = $"{parent?.Path ?? "/"}{id}/";
        // Las rutas solo tienen dígitos y '/': no hay comodines de LIKE que escapar.
        await cn.ExecuteAsync("""
            UPDATE meta.BusinessUnit SET Path = @newPath + SUBSTRING(Path, LEN(@oldPath) + 1, 900) WHERE Path LIKE @oldPath + N'%';
            UPDATE meta.BusinessUnit SET ParentId = @ParentId WHERE Id = @id;
            """, new { id, req.ParentId, newPath, oldPath = unit.Path }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.UnitChange, "Unidad", id.ToString(),
            before: new { unit.Name, unit.ParentId }, after: new { unit.Name, req.ParentId, securityChange = approving?.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        var dto = (await LoadUnitsAsync(cn, tx)).First(u => u.Id == id);
        await tx.CommitAsync();
        return Results.Ok(dto);
    }

    // ---------------- Equipos ----------------

    private static async Task<IReadOnlyList<TeamDto>> LoadTeamsAsync(DbConnection cn, DbTransaction? tx)
    {
        var teams = await cn.QueryAsync<(int Id, string Name, string? Unit, bool IsActive)>(
            "SELECT Id, DisplayName, Unit, IsActive FROM meta.[User] WHERE IsTeam = 1 ORDER BY DisplayName", transaction: tx);
        var members = (await cn.QueryAsync<(int TeamId, int UserId)>("SELECT TeamId, UserId FROM meta.TeamMember", transaction: tx))
            .ToLookup(m => m.TeamId, m => m.UserId);
        return teams.Select(t => new TeamDto(t.Id, t.Name, t.Unit, t.IsActive, members[t.Id].Order().ToList())).ToList();
    }

    private static TeamSnapshot Snapshot(TeamDto t) => new(t.Name, t.Unit, t.IsActive, t.MemberIds);

    internal static async Task<IResult> SaveTeam(int? id, TeamRequest req, HttpContext http, Db db, PasswordService passwords, SecurityApproval gate,
        SecurityChangeApproval? approving)
    {
        RequireUserAdmin(http);
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 150) errors["name"] = ["El nombre es obligatorio (máx. 150)."];
        var unit = NormalizeUnit(req.Unit, errors);
        var memberIds = (req.MemberIds ?? []).Distinct().Order().ToArray();
        if (memberIds.Length > MaxTeamMembers) errors["memberIds"] = [$"Un equipo tiene máximo {MaxTeamMembers} miembros."];
        if (errors.Count > 0) throw new ValidationFailed(errors);
        req = new TeamRequest(name, unit, id is null || req.IsActive, memberIds);

        await using var cn = await db.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var all = await LoadTeamsAsync(cn, tx);
        TeamDto? before = null;
        if (id is not null)
        {
            before = all.FirstOrDefault(t => t.Id == id) ?? throw new NotFound("El equipo no existe.");
            approving?.Verify(Snapshot(before));
        }
        if (all.Any(t => t.Id != id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe un equipo con ese nombre."] });
        await RequireUnitAsync(cn, tx, unit);
        // Miembros: personas (ni equipos ni cuentas de servicio). Un equipo no anida equipos.
        var people = (await cn.QueryAsync<int>("SELECT Id FROM meta.[User] WHERE Id IN @memberIds AND IsTeam = 0 AND IsService = 0", new { memberIds }, tx)).Count();
        if (people != memberIds.Length) throw new ValidationFailed(new Dictionary<string, string[]> { ["memberIds"] = ["Hay miembros que no son usuarios."] });

        var after = new TeamSnapshot(name, unit, req.IsActive, memberIds);
        // Spec 015 (H4): agregar miembros, cambiar la unidad o reactivar espera aprobación.
        if (gate.MustWait(approving, SecurityGrants.Team(before is null ? null : Snapshot(before), after)))
        {
            var change = await gate.RequestAsync(cn, tx, http, id is null ? SecurityChangeKinds.TeamCreate : SecurityChangeKinds.TeamUpdate, id,
                id is null ? $"Alta del equipo {name}" : $"Cambios al equipo {before!.Name}", req, before is null ? null : Snapshot(before));
            await tx.CommitAsync();
            return SecurityApproval.Accepted(change);
        }

        // Un equipo es un principal sin sesión (como las cuentas de servicio): su contraseña aleatoria nunca se conoce y
        // el inicio de sesión y el contexto de usuario rechazan IsTeam = 1.
        var teamId = id ?? await cn.ExecuteScalarAsync<int>("""
            INSERT meta.[User] (Username, DisplayName, PasswordHash, Unit, IsTeam) OUTPUT inserted.Id VALUES (@username, @name, @hash, @unit, 1)
            """, new { username = $"team.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}", name, unit,
                       hash = passwords.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) }, tx);
        if (id is not null)
            await cn.ExecuteAsync("UPDATE meta.[User] SET DisplayName = @name, Unit = @unit, IsActive = @IsActive WHERE Id = @id AND IsTeam = 1",
                new { id, name, unit, req.IsActive }, tx);
        await cn.ExecuteAsync("DELETE meta.TeamMember WHERE TeamId = @teamId", new { teamId }, tx);
        foreach (var userId in memberIds)
            await cn.ExecuteAsync("INSERT meta.TeamMember (TeamId, UserId) VALUES (@teamId, @userId)", new { teamId, userId }, tx);
        var saved = (await LoadTeamsAsync(cn, tx)).First(t => t.Id == teamId);
        await Audit.WriteAsync(cn, tx, http, AuditActions.TeamChange, "Equipo", teamId.ToString(), before,
            approving is null ? saved : new { team = saved, securityChange = approving.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync();
        return id is null ? Results.Created($"/api/admin/teams/{teamId}", saved) : Results.Ok(saved);
    }
}
