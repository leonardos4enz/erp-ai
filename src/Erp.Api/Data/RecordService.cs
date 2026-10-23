using System.Globalization;
using System.Text;
using System.Text.Json;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record ListQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Sort = null, string? Dir = null);
public sealed record ListResult(IReadOnlyList<Dictionary<string, object?>> Items, int Total);
public sealed record LookupItem(long Id, string Label);

/// <summary>
/// CRUD genérico sobre las tablas <c>app.*</c>. Reglas de seguridad:
/// identificadores solo desde <see cref="EntityDef"/> (nunca desde la petición), valores siempre parametrizados.
/// </summary>
public sealed class RecordService(Db db, MetadataStore store)
{
    public const int MaxPageSize = 200;
    public const int MaxSearchLength = 100;
    public const int MaxLongText = 1_000_000;

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "_rowVersion", "createdAt", "createdBy", "updatedAt", "updatedBy",
    };

    // ---------------- Lectura ----------------

    public async Task<ListResult> ListAsync(string entityName, ListQuery q, UserContext user, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        var model = await store.GetAsync(ct);

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var (select, from) = SelectClause(e, model);
        var where = new StringBuilder("t.[IsDeleted] = 0");

        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var searchable = e.ActiveFields.Where(f => FieldTypes.Searchable.Contains(f.Type)).ToList();
            if (searchable.Count > 0)
            {
                var term = q.Search.Trim();
                if (term.Length > MaxSearchLength) term = term[..MaxSearchLength];
                cmd.Parameters.Add(new SqlParameter("@search", "%" + EscapeLike(term) + "%"));
                where.Append(" AND (").Append(string.Join(" OR ",
                    searchable.Select(f => $"t.{Quote(f.Name)} LIKE @search ESCAPE '\\'"))).Append(')');
            }
        }

        var orderBy = ResolveSort(e, q.Sort, q.Dir);
        cmd.Parameters.Add(new SqlParameter("@skip", (page - 1) * size));
        cmd.Parameters.Add(new SqlParameter("@take", size));
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM {AppTable(e.Name)} t WHERE {where};
            SELECT {select} FROM {from} WHERE {where} ORDER BY {orderBy} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var total = reader.GetInt32(0);
        await reader.NextResultAsync(ct);
        var items = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader, e));
        return new ListResult(items, total);
    }

    public async Task<Dictionary<string, object?>> GetAsync(string entityName, long id, UserContext user, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        await using var cn = await db.OpenAsync(ct);
        return await LoadRowAsync(cn, null, e, id, ct) ?? throw new NotFound("El registro no existe.");
    }

    public async Task<IReadOnlyList<LookupItem>> LookupAsync(string entityName, string? search, UserContext user, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entityName, ct);
        var model = await store.GetAsync(ct);
        // Se permite consultar opciones si puede leer el catálogo o capturar en uno que lo referencia.
        var allowed = user.Access(e.Name).Read || model.Values.Any(o => !o.IsArchived &&
            o.ActiveFields.Any(f => string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase)) &&
            (user.Access(o.Name).Create || user.Access(o.Name).Update));
        Require(allowed);

        var label = e.EffectiveDisplayField is { } d ? $"t.{Quote(d)}" : "CAST(t.[Id] AS nvarchar(20))";
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        var where = "t.[IsDeleted] = 0";
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > MaxSearchLength) term = term[..MaxSearchLength];
            cmd.Parameters.Add(new SqlParameter("@search", "%" + EscapeLike(term) + "%"));
            where += $" AND {label} LIKE @search ESCAPE '\\'";
        }
        cmd.CommandText = $"SELECT TOP 20 t.[Id], {label} FROM {AppTable(e.Name)} t WHERE {where} ORDER BY 2";
        var list = new List<LookupItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new LookupItem(reader.GetInt64(0), reader.IsDBNull(1) ? $"#{reader.GetInt64(0)}" : reader.GetString(1)));
        return list;
    }

    // ---------------- Escritura ----------------

    public async Task<Dictionary<string, object?>> CreateAsync(string entityName, JsonElement body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var e = await store.GetActiveAsync(entityName, ct);
        Require(user.Access(e.Name).Create);

        await using var cn = await db.OpenAsync(ct);
        var values = await ParseBodyAsync(cn, e, body, ct);
        ValidateRequired(e, user, f => values.TryGetValue(f.Name, out var v) ? v : null);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        var cols = new List<string>();
        var pars = new List<string>();
        var i = 0;
        foreach (var (name, value) in values)
        {
            cols.Add(Quote(e.FindField(name)!.Name));
            pars.Add($"@p{i}");
            cmd.Parameters.Add(new SqlParameter($"@p{i++}", value ?? DBNull.Value));
        }
        cols.Add("[CreatedBy]");
        pars.Add("@uid");
        cmd.Parameters.Add(new SqlParameter("@uid", user.Id));
        cmd.CommandText = $"INSERT INTO {AppTable(e.Name)} ({string.Join(", ", cols)}) OUTPUT inserted.[Id] VALUES ({string.Join(", ", pars)})";

        var id = (long)(await ExecuteMappingUniqueAsync(e, () => cmd.ExecuteScalarAsync(ct)))!;
        var row = (await LoadRowAsync(cn, tx, e, id, ct))!;
        await Audit.WriteAsync(cn, tx, http, AuditActions.Create, e.Name, id.ToString(), after: DataOnly(row));
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<Dictionary<string, object?>> UpdateAsync(string entityName, long id, JsonElement body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var e = await store.GetActiveAsync(entityName, ct);
        Require(user.Access(e.Name).Update);

        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("_rowVersion", out var rvEl) || rvEl.ValueKind != JsonValueKind.String)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["_rowVersion"] = ["Falta la versión del registro."] });
        byte[] rowVersion;
        try { rowVersion = Convert.FromBase64String(rvEl.GetString()!); }
        catch (FormatException) { throw new ValidationFailed(new Dictionary<string, string[]> { ["_rowVersion"] = ["Versión inválida."] }); }

        await using var cn = await db.OpenAsync(ct);
        var before = await LoadRowAsync(cn, null, e, id, ct) ?? throw new NotFound("El registro no existe.");
        var values = await ParseBodyAsync(cn, e, body, ct);
        ValidateRequired(e, user, f => values.TryGetValue(f.Name, out var v) ? v : before.GetValueOrDefault(f.Name));
        if (values.Count == 0) return before;

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        var sets = new List<string>();
        var i = 0;
        foreach (var (name, value) in values)
        {
            sets.Add($"{Quote(e.FindField(name)!.Name)} = @p{i}");
            cmd.Parameters.Add(new SqlParameter($"@p{i++}", value ?? DBNull.Value));
        }
        cmd.Parameters.Add(new SqlParameter("@uid", user.Id));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@rv", System.Data.SqlDbType.Timestamp) { Value = rowVersion });
        cmd.CommandText = $"""
            UPDATE {AppTable(e.Name)} SET {string.Join(", ", sets)}, [UpdatedAt] = SYSUTCDATETIME(), [UpdatedBy] = @uid
             WHERE [Id] = @id AND [RowVer] = @rv AND [IsDeleted] = 0
            """;
        var affected = (int)(await ExecuteMappingUniqueAsync(e, async () => (object?)await cmd.ExecuteNonQueryAsync(ct)))!;
        if (affected == 0)
            throw new Conflict("Otro usuario modificó este registro. Recarga para ver los cambios.");

        var after = (await LoadRowAsync(cn, tx, e, id, ct))!;
        await Audit.WriteAsync(cn, tx, http, AuditActions.Update, e.Name, id.ToString(), DataOnly(before), DataOnly(after));
        await tx.CommitAsync(ct);
        return after;
    }

    public async Task DeleteAsync(string entityName, long id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var e = await store.GetActiveAsync(entityName, ct);
        Require(user.Access(e.Name).Delete);
        var model = await store.GetAsync(ct);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var before = await LoadRowAsync(cn, tx, e, id, ct) ?? throw new NotFound("El registro no existe.");

        // Integridad: no se elimina un registro que siga en uso por registros activos.
        foreach (var other in model.Values)
        foreach (var f in other.Fields.Where(f => string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase)))
        {
            await using var check = new SqlCommand(
                $"SELECT TOP 1 1 FROM {AppTable(other.Name)} WHERE {Quote(f.Name)} = @id AND [IsDeleted] = 0", cn, tx);
            check.Parameters.Add(new SqlParameter("@id", id));
            if (await check.ExecuteScalarAsync(ct) is not null)
                throw new Conflict($"No se puede eliminar: está en uso en {other.LabelPlural} ({f.Label}).");
        }

        await using var cmd = new SqlCommand(
            $"UPDATE {AppTable(e.Name)} SET [IsDeleted] = 1, [UpdatedAt] = SYSUTCDATETIME(), [UpdatedBy] = @uid WHERE [Id] = @id AND [IsDeleted] = 0",
            cn, tx);
        cmd.Parameters.Add(new SqlParameter("@uid", user.Id));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
        await Audit.WriteAsync(cn, tx, http, AuditActions.Delete, e.Name, id.ToString(), before: DataOnly(before));
        await tx.CommitAsync(ct);
    }

    // ---------------- Internos ----------------

    private static void Require(bool allowed)
    {
        if (!allowed) throw new Forbidden();
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static (string Select, string From) SelectClause(EntityDef e, IReadOnlyDictionary<string, EntityDef> model)
    {
        var cols = new List<string> { "t.[Id]" };
        var from = new StringBuilder($"{AppTable(e.Name)} t");
        var j = 0;
        foreach (var f in e.ActiveFields)
        {
            cols.Add($"t.{Quote(f.Name)}");
            if (f.Type != FieldTypes.Reference || !model.TryGetValue(f.ReferenceEntity!, out var target)) continue;
            var alias = $"r{j++}";
            var label = target.EffectiveDisplayField is { } d ? $"{alias}.{Quote(d)}" : $"CAST({alias}.[Id] AS nvarchar(20))";
            cols.Add($"{label} AS [{Quote(f.Name)[1..^1]}__label]");
            from.Append($" LEFT JOIN {AppTable(target.Name)} {alias} ON {alias}.[Id] = t.{Quote(f.Name)}");
        }
        cols.AddRange(["t.[CreatedAt]", "cu.[DisplayName] AS [CreatedByName]", "t.[UpdatedAt]", "uu.[DisplayName] AS [UpdatedByName]", "t.[RowVer]"]);
        from.Append(" LEFT JOIN meta.[User] cu ON cu.[Id] = t.[CreatedBy] LEFT JOIN meta.[User] uu ON uu.[Id] = t.[UpdatedBy]");
        return (string.Join(", ", cols), from.ToString());
    }

    private static string ResolveSort(EntityDef e, string? sort, string? dir)
    {
        var direction = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        if (string.IsNullOrEmpty(sort)) return "t.[Id] DESC";
        var column = sort.ToLowerInvariant() switch
        {
            "id" => "[Id]",
            "createdat" => "[CreatedAt]",
            "updatedat" => "[UpdatedAt]",
            _ => e.ActiveFields.FirstOrDefault(f => f.Type != FieldTypes.LongText &&
                     string.Equals(f.Name, sort, StringComparison.OrdinalIgnoreCase)) is { } f ? Quote(f.Name) : null,
        };
        return column is null ? "t.[Id] DESC" : $"t.{column} {direction}, t.[Id] {direction}";
    }

    private async Task<Dictionary<string, object?>?> LoadRowAsync(SqlConnection cn, SqlTransaction? tx, EntityDef e, long id, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var (select, from) = SelectClause(e, model);
        await using var cmd = new SqlCommand($"SELECT {select} FROM {from} WHERE t.[Id] = @id AND t.[IsDeleted] = 0", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader, e) : null;
    }

    private static Dictionary<string, object?> ReadRow(SqlDataReader r, EntityDef e)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < r.FieldCount; i++)
        {
            var name = r.GetName(i);
            var value = r.IsDBNull(i) ? null : r.GetValue(i);
            switch (name)
            {
                case "Id": row["id"] = value; break;
                case "RowVer": row["_rowVersion"] = Convert.ToBase64String((byte[])value!); break;
                case "CreatedAt": row["createdAt"] = AsUtc(value); break;
                case "UpdatedAt": row["updatedAt"] = AsUtc(value); break;
                case "CreatedByName": row["createdBy"] = value; break;
                case "UpdatedByName": row["updatedBy"] = value; break;
                default:
                    var f = e.FindField(name);
                    row[name] = (f?.Type, value) switch
                    {
                        (_, null) => null,
                        (FieldTypes.Date, DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        (FieldTypes.DateTime, DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        _ => value,
                    };
                    break;
            }
        }
        return row;
    }

    private static object? AsUtc(object? v) => v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : v;

    private static Dictionary<string, object?> DataOnly(Dictionary<string, object?> row) =>
        row.Where(kv => !kv.Key.StartsWith('_') && kv.Key is not ("createdAt" or "createdBy" or "updatedAt" or "updatedBy"))
           .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static void ValidateRequired(EntityDef e, UserContext user, Func<FieldDef, object?> valueOf)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var f in e.ActiveFields)
        {
            var required = f.Required || user.HasRole(f.RequiredForRoles);
            var v = valueOf(f);
            if (required && (v is null || v is string s && string.IsNullOrWhiteSpace(s)))
                errors[f.Name] = [$"{f.Label} es obligatorio."];
        }
        if (errors.Count > 0) throw new ValidationFailed(errors);
    }

    /// <summary>Convierte el JSON recibido a valores tipados según metadatos. Rechaza campos desconocidos.</summary>
    private static async Task<Dictionary<string, object?>> ParseBodyAsync(SqlConnection cn, EntityDef e, JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new AppException(400, "Se esperaba un objeto JSON.");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string[]>();

        foreach (var prop in body.EnumerateObject())
        {
            if (IgnoredKeys.Contains(prop.Name) || prop.Name.EndsWith("__label", StringComparison.Ordinal)) continue;
            var f = e.FindField(prop.Name);
            if (f is null || f.IsArchived) { errors[prop.Name] = ["Campo desconocido."]; continue; }
            try
            {
                var value = ConvertValue(f, prop.Value);
                if (value is long refId && f.Type == FieldTypes.Reference)
                {
                    await using var check = new SqlCommand($"SELECT 1 FROM {AppTable(f.ReferenceEntity!)} WHERE [Id] = @id AND [IsDeleted] = 0", cn);
                    check.Parameters.Add(new SqlParameter("@id", refId));
                    if (await check.ExecuteScalarAsync(ct) is null) throw new FieldError("El registro seleccionado no existe.");
                }
                values[f.Name] = value;
            }
            catch (Exception ex) when (ex is FieldError or FormatException or InvalidOperationException or OverflowException)
            {
                errors[f.Name] = [ex is FieldError ? ex.Message : $"Valor inválido para {f.Label}."];
            }
        }
        if (errors.Count > 0) throw new ValidationFailed(errors);
        return values;
    }

    internal static object? ConvertValue(FieldDef f, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;
        switch (f.Type)
        {
            case FieldTypes.Text:
            case FieldTypes.LongText:
            {
                var s = v.GetString()!.Trim();
                if (s.Length == 0) return null;
                var max = f.Type == FieldTypes.Text ? f.MaxLength ?? FieldTypes.DefaultTextLength : MaxLongText;
                if (s.Length > max) throw new FieldError($"{f.Label} admite máximo {max} caracteres.");
                return s;
            }
            case FieldTypes.Integer:
                return v.ValueKind == JsonValueKind.String
                    ? long.Parse(v.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : v.GetInt64();
            case FieldTypes.Decimal:
            {
                var d = v.ValueKind == JsonValueKind.String
                    ? decimal.Parse(v.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
                    : v.GetDecimal();
                int p = f.Precision ?? 18, s = f.Scale ?? 2;
                if (d.Scale > s && decimal.Round(d, s) != d) throw new FieldError($"{f.Label} admite máximo {s} decimales.");
                if (Math.Abs(decimal.Truncate(d)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length > p - s)
                    throw new FieldError($"{f.Label} es demasiado grande.");
                return decimal.Round(d, s);
            }
            case FieldTypes.Boolean:
                return v.GetBoolean();
            case FieldTypes.Date:
                return DateOnly.ParseExact(v.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
            case FieldTypes.DateTime:
                // Se almacena en UTC; el cliente convierte a la zona del usuario.
                return DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            case FieldTypes.Choice:
            {
                var s = v.GetString()!.Trim();
                if (s.Length == 0) return null;
                if (f.Options?.Contains(s) != true) throw new FieldError($"'{s}' no es una opción válida de {f.Label}.");
                return s;
            }
            case FieldTypes.Reference:
                return v.ValueKind == JsonValueKind.String
                    ? long.Parse(v.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : v.GetInt64();
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Error de validación con mensaje pensado para el usuario (los de .NET no se exponen).</summary>
    private sealed class FieldError(string message) : Exception(message);

    /// <summary>Traduce violaciones de índice único a un error de validación sobre el campo correspondiente.</summary>
    private static async Task<object?> ExecuteMappingUniqueAsync(EntityDef e, Func<Task<object?>> action)
    {
        try { return await action(); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var field = e.Fields.FirstOrDefault(f => f.Unique && ex.Message.Contains($"UX_{e.Name}_{f.Name}", StringComparison.OrdinalIgnoreCase));
            throw new ValidationFailed(new Dictionary<string, string[]>
            {
                [field?.Name ?? "registro"] = [$"Ya existe un registro con ese valor{(field is null ? "" : $" de {field.Label}")}."],
            });
        }
    }
}
