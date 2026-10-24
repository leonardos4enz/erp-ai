using System.Text;
using Erp.Api.Metadata;
using Erp.Api.OData;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record ODataRows(IReadOnlyList<Dictionary<string, object?>> Items, int? Count, bool More);

/// <summary>
/// Spec 018: lectura por OData con las mismas piezas que la API de datos (<see cref="SelectClause"/>, <see cref="Format"/>,
/// <see cref="RecordFilter"/> y <see cref="FieldAccess"/>): no es un segundo camino de lectura con reglas propias.
/// </summary>
public sealed partial class RecordService
{
    /// <summary>Página de <paramref name="take"/> filas desde <paramref name="skip"/>; <c>More</c> si hay más.</summary>
    public async Task<ODataRows> ODataListAsync(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, ODataQuery q, int skip, int take,
        UserContext user, CancellationToken ct)
    {
        var (rows, count, more) = await ODataQueryAsync(e, model, q, null, skip, take, user, ct);
        return new ODataRows(rows, count, more);
    }

    public async Task<Dictionary<string, object?>?> ODataGetAsync(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, long id, ODataQuery q,
        UserContext user, CancellationToken ct)
    {
        var (rows, _, _) = await ODataQueryAsync(e, model, q with { Count = false }, id, 0, 1, user, ct);
        return rows.FirstOrDefault();
    }

    private async Task<(List<Dictionary<string, object?>> Rows, int? Count, bool More)> ODataQueryAsync(EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, ODataQuery q, long? id, int skip, int take, UserContext user, CancellationToken ct)
    {
        var props = ODataModel.Properties(e, user);
        var byName = props.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var navs = ODataModel.Navigations(e, model, user).ToDictionary(n => n.Name, StringComparer.Ordinal);

        // Todo nombre se resuelve contra lo que el usuario ve: lo oculto y lo inexistente dan el mismo error.
        foreach (var s in q.Select ?? [])
            if (!byName.ContainsKey(s))
                throw new ODataException(navs.ContainsKey(s) ? $"$select: {s} es una navegación; inclúyela con $expand." : $"$select: la propiedad '{s}' no existe en {e.Name}.");
        var expand = q.Expand.Select(x => navs.GetValueOrDefault(x) ?? throw new ODataException($"$expand: '{x}' no es una navegación de {e.Name}.")).ToList();
        var order = new List<string>();
        foreach (var o in q.OrderBy)
        {
            var p = byName.GetValueOrDefault(o.Property) ?? throw new ODataException($"$orderby: la propiedad '{o.Property}' no existe en {e.Name}.");
            if (!p.Sortable) throw new ODataException($"$orderby: {p.Name} es un texto largo y no se ordena.");
            order.Add($"{p.Column} {(o.Descending ? "DESC" : "ASC")}");
        }
        // Orden estable para paginar: el id al final, en el sentido del último criterio.
        order.Add($"t.[Id] {(q.OrderBy.LastOrDefault()?.Descending == true ? "DESC" : "ASC")}");

        var (select, from, labelParams) = SelectClause(e, model, user);
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        foreach (var (name, value) in labelParams) cmd.Parameters.Add(new SqlParameter(name, value));
        var where = new StringBuilder("t.[IsDeleted] = 0");
        // Spec 006/010/015: el mismo filtro de alcance y empresa que la API de datos, también en el conteo.
        if (RecordFilter.For(e, user, write: false) is { } scope)
        {
            scope.AddTo(cmd.Parameters);
            where.Append(" AND ").Append(scope.Sql);
        }
        if (id is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@odid", id));
            where.Append(" AND t.[Id] = @odid");
        }
        if (q.Filter is { } filter)
            where.Append(" AND ").Append(ODataFilter.ToSql(ODataFilter.Parse(filter), byName.GetValueOrDefault, cmd.Parameters, e));

        cmd.Parameters.Add(new SqlParameter("@skip", skip));
        cmd.Parameters.Add(new SqlParameter("@take", take + 1));
        cmd.CommandText = $"""
            {(q.Count ? $"SELECT COUNT(*) FROM {AppTable(e.Name)} t WHERE {where};" : "")}
            SELECT {select} FROM {from} WHERE {where} ORDER BY {string.Join(", ", order)} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;

        int? count = null;
        var raws = new List<Values>();
        var rows = new List<Dictionary<string, object?>>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (q.Count)
            {
                await reader.ReadAsync(ct);
                count = reader.GetInt32(0);
                await reader.NextResultAsync(ct);
            }
            while (await reader.ReadAsync(ct))
            {
                var raw = ReadRaw(reader);
                var row = Format(e, raw);
                FieldAccess.Strip(e, row, user, model);
                raws.Add(raw);
                rows.Add(ToOData(props, row));
            }
        }
        var more = rows.Count > take;
        if (more) { rows.RemoveAt(take); raws.RemoveAt(take); }

        foreach (var n in expand)
        {
            if (n.Collection) await ExpandLinesAsync(cn, n, model, rows, user, ct);
            else await ExpandReferenceAsync(cn, n, model, rows, raws, user, ct);
        }
        if (q.Select is { } selected)
        {
            var keep = selected.Append("Id").Concat(expand.Select(n => n.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var r in rows)
                foreach (var k in r.Keys.Where(k => !keep.Contains(k)).ToList()) r.Remove(k);
        }
        return (rows, count, more);
    }

    private static Dictionary<string, object?> ToOData(IReadOnlyList<ODataProperty> props, Dictionary<string, object?> row)
    {
        var o = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in props)
            o[p.Name] = p.Name switch
            {
                "Id" when p.Field is null => row.GetValueOrDefault("id"),
                "CreatedAt" when p.Field is null => row.GetValueOrDefault("createdAt"),
                "UpdatedAt" when p.Field is null => row.GetValueOrDefault("updatedAt"),
                _ => row.GetValueOrDefault(p.Field!.Name),
            };
        return o;
    }

    /// <summary>
    /// Spec 018 (H4 CA4): el registro referido con el alcance de lectura del destino (empresa incluida) y sus campos
    /// visibles; si el usuario no lo alcanza, <c>null</c>.
    /// </summary>
    private static async Task ExpandReferenceAsync(SqlConnection cn, ODataNavigation n, IReadOnlyDictionary<string, EntityDef> model,
        List<Dictionary<string, object?>> rows, List<Values> raws, UserContext user, CancellationToken ct)
    {
        var target = n.Target;
        var ids = raws.Select(r => r.GetValueOrDefault(n.Field.Name)).OfType<long>().Distinct().ToList();
        var found = new Dictionary<long, Dictionary<string, object?>>();
        if (ids.Count > 0)
        {
            var props = ODataModel.Properties(target, user);
            var (select, from, labelParams) = SelectClause(target, model, user);
            await using var cmd = cn.CreateCommand();
            foreach (var (name, value) in labelParams) cmd.Parameters.Add(new SqlParameter(name, value));
            var where = new StringBuilder($"t.[IsDeleted] = 0 AND t.[Id] IN ({string.Join(", ", ids.Select((v, i) => Param(cmd, $"@ex{i}", v)))})");
            if (RecordFilter.For(target, user, write: false) is { } scope)
            {
                scope.AddTo(cmd.Parameters);
                where.Append(" AND ").Append(scope.Sql);
            }
            cmd.CommandText = $"SELECT {select} FROM {from} WHERE {where}";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = Format(target, ReadRaw(reader));
                FieldAccess.Strip(target, row, user, model);
                found[Convert.ToInt64(row["id"])] = ToOData(props, row);
            }
        }
        for (var i = 0; i < rows.Count; i++)
            rows[i][n.Name] = raws[i].GetValueOrDefault(n.Field.Name) is long v ? found.GetValueOrDefault(v) : null;
    }

    /// <summary>Las partidas del documento (los documentos ya pasaron su alcance) con los campos visibles de la partida.</summary>
    private static async Task ExpandLinesAsync(SqlConnection cn, ODataNavigation n, IReadOnlyDictionary<string, EntityDef> model,
        List<Dictionary<string, object?>> rows, UserContext user, CancellationToken ct)
    {
        var child = n.Target;
        var parent = child.ParentField!.Name;
        var byParent = rows.ToDictionary(r => Convert.ToInt64(r["Id"]), _ => new List<Dictionary<string, object?>>());
        if (byParent.Count > 0)
        {
            var props = ODataModel.Properties(child, user);
            var (select, from, labelParams) = SelectClause(child, model, user);
            await using var cmd = cn.CreateCommand();
            foreach (var (name, value) in labelParams) cmd.Parameters.Add(new SqlParameter(name, value));
            var ids = string.Join(", ", byParent.Keys.Select((v, i) => Param(cmd, $"@ex{i}", v)));
            cmd.CommandText = $"SELECT {select} FROM {from} WHERE t.[IsDeleted] = 0 AND t.{Quote(parent)} IN ({ids}) ORDER BY t.[Id]";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var raw = ReadRaw(reader);
                if (raw.GetValueOrDefault(parent) is not long owner || !byParent.TryGetValue(owner, out var list)) continue;
                var row = Format(child, raw);
                FieldAccess.Strip(child, row, user, model);
                list.Add(ToOData(props, row));
            }
        }
        foreach (var r in rows) r[n.Name] = byParent[Convert.ToInt64(r["Id"])];
    }

    private static string Param(SqlCommand cmd, string name, long value)
    {
        cmd.Parameters.Add(new SqlParameter(name, value));
        return name;
    }
}
