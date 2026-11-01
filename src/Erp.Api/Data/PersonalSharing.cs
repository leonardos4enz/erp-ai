using System.Data.Common;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Data;

/// <param name="Roles">Nombres de los roles; vacío deja de compartir.</param>
public sealed record ShareRequest(IReadOnlyList<string?>? Roles, int? Version);
public sealed record ShareResult(int Id, int Version, IReadOnlyList<string> Roles);

/// <summary>
/// Spec 042: reparto por rol de reportes y tableros personales. Quien comparte solo elige roles que tiene (asignados o
/// heredados; el superusuario, cualquiera), y el reparto se revalida en cada lectura: cuenta mientras el dueño esté activo y
/// siga teniendo el rol (021-1: nadie reparte lo que no tiene). Lo compartido no da permisos: corre con quien lo ve.
/// </summary>
public static class PersonalSharing
{
    public const int MaxRoles = 20;

    /// <summary>
    /// Condición de candidatos «compartido con quien lee»: uno de sus roles (<c>@viewerRoles</c>, nombres efectivos) está en el
    /// reparto y el dueño está activo. Que el dueño siga teniendo ese rol lo comprueba <see cref="RevalidateAsync"/>.
    /// <paramref name="shareTable"/>, <paramref name="key"/> y <paramref name="alias"/> son constantes del código, nunca de la
    /// petición.
    /// </summary>
    public static string Visible(string shareTable, string key, string alias) => $"""
        EXISTS (SELECT 1 FROM {shareTable} s JOIN meta.Role vr ON vr.Id = s.RoleId
                 WHERE s.{key} = {alias}.Id AND vr.Name IN @viewerRoles
                   AND EXISTS (SELECT 1 FROM meta.[User] o WHERE o.Id = {alias}.UserId AND o.IsActive = 1))
        """;

    /// <summary>
    /// De los candidatos (<see cref="Visible"/>), los que siguen compartidos con quien lee: el dueño es superusuario o tiene hoy
    /// (asignado o heredado) alguno de los roles del reparto que también tiene quien lee (021-1: nadie reparte lo que no tiene).
    /// Regresión (revisión adversarial 042, F-A): antes, una CTE recursiva sin deduplicar recorría la herencia de todas las
    /// personas en cada lectura (en rombo, 10^5 caminos por rol); ahora solo la de los dueños candidatos, con
    /// <see cref="RoleGraph.Expand"/> (deduplica y tolera ciclos).
    /// </summary>
    public static async Task<HashSet<int>> RevalidateAsync(string shareTable, string key, IReadOnlyCollection<(int Id, int OwnerId)> items,
        IReadOnlyList<string> viewerRoles, DbConnection cn, DbTransaction? tx = null)
    {
        if (items.Count == 0) return [];
        var ids = items.Select(i => i.Id).Distinct().ToList();
        var owners = items.Select(i => i.OwnerId).Distinct().ToList();
        var shares = (await cn.QueryAsync<(int ItemId, int RoleId)>($"""
            SELECT s.{key}, s.RoleId FROM {shareTable} s JOIN meta.Role vr ON vr.Id = s.RoleId WHERE s.{key} IN @ids AND vr.Name IN @viewerRoles
            """, new { ids, viewerRoles }, tx)).ToLookup(x => x.ItemId, x => x.RoleId);
        var direct = (await cn.QueryAsync<(int UserId, int RoleId, bool IsSuperuser)>("""
            SELECT ur.UserId, ur.RoleId, r.IsSuperuser FROM meta.UserRole ur JOIN meta.Role r ON r.Id = ur.RoleId WHERE ur.UserId IN @owners
            """, new { owners }, tx)).ToList();
        var edges = (await cn.QueryAsync<(int RoleId, int ParentRoleId)>("SELECT RoleId, ParentRoleId FROM meta.RoleInherit", transaction: tx)).ToList();
        // null = superusuario (reparte con cualquier rol).
        var effective = owners.ToDictionary(o => o, o => direct.Any(d => d.UserId == o && d.IsSuperuser)
            ? null : RoleGraph.Expand(direct.Where(d => d.UserId == o).Select(d => d.RoleId), edges));
        return items.Where(i => effective[i.OwnerId] is not { } roles || shares[i.Id].Any(roles.Contains)).Select(i => i.Id).ToHashSet();
    }

    /// <summary>Roles con los que la persona puede compartir: los suyos (el superusuario, todos).</summary>
    public static async Task<IReadOnlyList<string>> ShareableAsync(UserContext user, DbConnection cn, DbTransaction? tx = null)
    {
        if (!user.IsSuperuser) return user.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture).ToList();
        return (await cn.QueryAsync<string>("SELECT Name FROM meta.Role ORDER BY Name", transaction: tx)).ToList();
    }

    /// <summary>
    /// Los ids de los roles pedidos. Uno inexistente y uno que la persona no tiene responden igual: no es un oráculo de
    /// nombres de roles.
    /// </summary>
    public static async Task<IReadOnlyList<(int Id, string Name)>> ResolveAsync(IReadOnlyList<string?>? names, UserContext user,
        DbConnection cn, DbTransaction tx)
    {
        if (names is null) throw Invalid("Falta la lista de roles (vacía para dejar de compartir).");
        if (names.Any(n => string.IsNullOrWhiteSpace(n))) throw Invalid("Hay un rol vacío.");
        var wanted = names.Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count > MaxRoles) throw Invalid($"Se comparte con {MaxRoles} roles como máximo.");
        if (wanted.Count == 0) return [];
        var rows = (await cn.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM meta.Role WHERE Name IN @wanted", new { wanted }, tx)).ToList();
        var result = new List<(int, string)>();
        foreach (var name in wanted)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row.Name is null || (!user.IsSuperuser && !user.Roles.Contains(row.Name, StringComparer.OrdinalIgnoreCase)))
                throw Invalid($"El rol '{name}' no existe o no lo tienes.");
            result.Add(row);
        }
        return result;
    }

    /// <summary>Reemplaza el reparto de un elemento (dentro de la transacción de quien lo cambia).</summary>
    public static async Task ReplaceAsync(string shareTable, string key, int id, IReadOnlyList<(int Id, string Name)> roles,
        DbConnection cn, DbTransaction tx)
    {
        await cn.ExecuteAsync($"DELETE FROM {shareTable} WHERE {key} = @id", new { id }, tx);
        foreach (var (roleId, _) in roles)
            await cn.ExecuteAsync($"INSERT {shareTable} ({key}, RoleId) VALUES (@id, @roleId)", new { id, roleId }, tx);
    }

    /// <summary>Nombres de los roles con los que está compartido (los que el dueño eligió, tal como están hoy).</summary>
    public static async Task<IReadOnlyList<string>> RolesOfAsync(string shareTable, string key, int id, DbConnection cn, DbTransaction? tx = null) =>
        (await cn.QueryAsync<string>(
            $"SELECT r.Name FROM {shareTable} s JOIN meta.Role r ON r.Id = s.RoleId WHERE s.{key} = @id ORDER BY r.Name", new { id }, tx)).ToList();

    private static ValidationFailed Invalid(string msg) => new(new Dictionary<string, string[]> { ["roles"] = [msg] });
}
