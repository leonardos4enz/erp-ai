using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record ImportColumn(string Column, string? Field, string? Label, string Status, string? Message);
public sealed record ImportError(int Row, string? Column, string? Field, string Message);
public sealed record ImportResult(bool DryRun, string Format, int Total, int Imported, bool Valid,
    IReadOnlyList<ImportColumn> Columns, IReadOnlyList<ImportError> Errors, bool TruncatedErrors);

/// <summary>
/// Spec 008 · importación masiva y exportación de listados. Cada fila importada pasa por el mismo alta que la captura
/// (<see cref="CreateInTransactionAsync"/>): valores por defecto, fórmulas, reglas, seguridad 006, automatizaciones,
/// webhooks y auditoría. Todo o nada: la vista previa y una importación con errores se revierten completas.
/// </summary>
public sealed partial class RecordService
{
    public const int MaxImportErrors = 200;
    public const int MaxExport = 5000;

    public async Task<ImportResult> ImportAsync(string entityName, byte[] file, bool dryRun, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Create);
        RequireHumanWritable(e);
        var table = ImportReader.Read(file);
        var writer = new Writer(user, e.Workflow?.Initial);
        var (columns, map) = MapColumns(e, table.Headers, writer, table.Rows);

        var errors = new List<ImportError>();
        var truncated = false;
        void Add(ImportError err)
        {
            if (errors.Count < MaxImportErrors) errors.Add(err); else truncated = true;
        }
        var colOf = map.ToDictionary(m => m.Value.Name, m => columns[m.Key].Column, StringComparer.OrdinalIgnoreCase);
        var structural = columns.Any(c => c.Status == "error");
        var imported = 0;

        // Spec 014 (H3): la vista previa no toca la secuencia de folios; la importación reserva su bloque de una vez, en una
        // transacción corta, y las altas de otras personas no esperan a que termine.
        var folios = structural || !e.ActiveFields.Any(f => f.Type == FieldTypes.Autonumber) ? null
            : dryRun ? FolioSource.ForPreview(e) : await ReserveFoliosAsync(e, table.Rows.Count, ct);
        var committed = false;
        try
        {
            await using var cn = await db.OpenAsync(ct);
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            if (!structural)
            {
                var refs = new Dictionary<(string, string), (long? Id, string? Error)>();
                foreach (var (rowNumber, cells) in table.Rows)
                {
                    var body = new JsonObject();
                    var rowOk = true;
                    foreach (var (index, f) in map)
                    {
                        var raw = index < cells.Count ? cells[index] : null;
                        if (string.IsNullOrWhiteSpace(raw)) continue; // vacía: aplica el valor por defecto
                        var (node, error) = await CellAsync(cn, tx, f, raw.Trim(), model, user, refs, ct);
                        if (error is not null) { Add(new(rowNumber, columns[index].Column, f.Name, error)); rowOk = false; }
                        else body[f.Name] = node;
                    }
                    if (!rowOk) continue;

                    tx.Save("fila");
                    var mark = folios?.Mark();
                    try
                    {
                        using var doc = JsonDocument.Parse(body.ToJsonString());
                        var id = await CreateInTransactionAsync(cn, tx, e, model, doc.RootElement, user, http, ct, folios);
                        if (RecordFilter.For(e, user, write: true) is { } scope && !await MatchesAsync(cn, tx, e, id, scope, ct))
                            throw new Forbidden("Quedaría fuera de los registros que tu rol puede modificar.");
                        imported++;
                    }
                    catch (ValidationFailed vf)
                    {
                        tx.Rollback("fila");
                        folios?.Restore(mark!);
                        foreach (var (key, messages) in vf.Errors ?? new Dictionary<string, string[]>())
                        foreach (var m in messages)
                            Add(new(rowNumber, colOf.GetValueOrDefault(key), e.FindField(key)?.Name, m));
                    }
                    catch (AppException ex)
                    {
                        tx.Rollback("fila");
                        folios?.Restore(mark!);
                        Add(new(rowNumber, null, null, ex.Message));
                    }
                    catch (ExprException ex)
                    {
                        tx.Rollback("fila");
                        folios?.Restore(mark!);
                        Add(new(rowNumber, null, null, $"Error al calcular: {ex.Message}"));
                    }
                }
            }

            var valid = !structural && errors.Count == 0 && !truncated;
            if (!valid || dryRun)
            {
                await tx.RollbackAsync(ct);
                var result = new ImportResult(dryRun, table.Format, table.Rows.Count, 0, valid, columns, errors, truncated);
                if (!dryRun)
                    throw new ValidationFailed(
                        (structural ? columns.Where(c => c.Status == "error").Select(c => ($"Columna «{c.Column}»", c.Message!)) : [])
                            .Concat(errors.Select(x => ($"Fila {x.Row}{(x.Column is null ? "" : $" › {x.Column}")}", x.Message)))
                            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray()),
                        "El archivo tiene errores; no se importó ningún registro.");
                return result;
            }
            await Audit.WriteAsync(cn, tx, http, AuditActions.Import, e.Name, null,
                after: new { table.Format, rows = table.Rows.Count, imported, columns = columns.Where(c => c.Status == "ok").Select(c => c.Field) });
            await tx.CommitAsync(ct);
            committed = true;
            return new ImportResult(false, table.Format, table.Rows.Count, imported, true, columns, [], false);
        }
        finally
        {
            if (!committed && folios is { IsPreview: false }) await ReleaseFoliosAsync(folios, CancellationToken.None);
        }
    }

    /// <summary>Spec 014 (H3): reserva <paramref name="count"/> folios por campo en una transacción propia y corta.</summary>
    private async Task<FolioSource> ReserveFoliosAsync(EntityDef e, int count, CancellationToken ct)
    {
        var folios = new FolioSource(e.Name, preview: false);
        if (count == 0) return folios;
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        foreach (var f in e.ActiveFields.Where(f => f.Type == FieldTypes.Autonumber))
        {
            await using var cmd = new SqlCommand("""
                DECLARE @v bigint;
                UPDATE meta.Sequence WITH (UPDLOCK, HOLDLOCK) SET @v = NextValue, NextValue = NextValue + @n
                 WHERE EntityName = @e AND FieldName = @f;
                IF @v IS NULL BEGIN INSERT meta.Sequence (EntityName, FieldName, NextValue) VALUES (@e, @f, 1 + @n); SET @v = 1; END
                SELECT @v;
                """, cn, tx);
            cmd.Parameters.Add(new SqlParameter("@e", e.Name));
            cmd.Parameters.Add(new SqlParameter("@f", f.Name));
            cmd.Parameters.Add(new SqlParameter("@n", (long)count));
            folios.Add(f.Name, (long)(await cmd.ExecuteScalarAsync(ct))!, count);
        }
        await tx.CommitAsync(ct);
        return folios;
    }

    /// <summary>
    /// Devuelve el bloque de una importación que no se guardó, solo si nadie reservó folios después (comparar y cambiar);
    /// si alguien lo hizo, queda un hueco en la numeración (spec 014, H3 CA3).
    /// </summary>
    private async Task ReleaseFoliosAsync(FolioSource folios, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        foreach (var (field, start, end) in folios.Blocks)
        {
            await using var cmd = new SqlCommand(
                "UPDATE meta.Sequence SET NextValue = @start WHERE EntityName = @e AND FieldName = @f AND NextValue = @end", cn);
            cmd.Parameters.Add(new SqlParameter("@e", folios.Entity));
            cmd.Parameters.Add(new SqlParameter("@f", field));
            cmd.Parameters.Add(new SqlParameter("@start", start));
            cmd.Parameters.Add(new SqlParameter("@end", end));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

/// <summary>
    /// Spec 014 (H3): de dónde salen los folios de una importación. En la vista previa son provisionales (no se guardan ni
    /// tocan la secuencia); en la importación, de un bloque reservado de antemano. Solo para el catálogo importado: lo que
    /// creen sus automatizaciones usa la secuencia como siempre.
    /// </summary>
    internal sealed class FolioSource(string entity, bool preview)
    {
        private readonly Dictionary<string, (long Next, long End)> _blocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _start = new(StringComparer.OrdinalIgnoreCase);
        private long _previews;

        public string Entity => entity;
        public bool IsPreview => preview;
        public static FolioSource ForPreview(EntityDef e) => new(e.Name, preview: true);

        public IEnumerable<(string Field, long Start, long End)> Blocks => _blocks.Select(b => (b.Key, _start[b.Key], b.Value.End));

        public void Add(string field, long start, long count)
        {
            _start[field] = start;
            _blocks[field] = (start, start + count);
        }

        public static string Format(FieldDef f, long n) =>
            (f.Prefix ?? "") + n.ToString(CultureInfo.InvariantCulture).PadLeft(f.Padding ?? 6, '0');

        /// <summary>Folio para <paramref name="f"/>, o null si no aplica o se agotó el bloque (entonces, la secuencia).</summary>
        public string? Take(EntityDef e, FieldDef f)
        {
            if (!string.Equals(e.Name, entity, StringComparison.OrdinalIgnoreCase)) return null;
            if (preview)
            {
                var p = $"{f.Prefix}VP-{++_previews}";
                return p.Length <= 30 ? p : p[^30..];
            }
            if (!_blocks.TryGetValue(f.Name, out var b) || b.Next >= b.End) return null;
            _blocks[f.Name] = (b.Next + 1, b.End);
            return Format(f, b.Next);
        }

        /// <summary>Una fila que se revierte a su punto de guardado devuelve sus folios al bloque.</summary>
        public object Mark() => (_blocks.ToDictionary(b => b.Key, b => b.Value, StringComparer.OrdinalIgnoreCase), _previews);

        public void Restore(object mark)
        {
            var (blocks, previews) = ((Dictionary<string, (long, long)>, long))mark;
            foreach (var (k, v) in blocks) _blocks[k] = v;
            _previews = previews;
        }
    }

    /// <summary>Encabezado → campo por nombre o etiqueta (sin mayúsculas ni acentos), con la seguridad del usuario.</summary>
    internal static (List<ImportColumn> Columns, Dictionary<int, FieldDef> Map) MapColumns(EntityDef e, IReadOnlyList<string> headers,
        Func<FieldDef, bool> canSee, Func<FieldDef, bool> canEdit, IReadOnlyList<(int Row, IReadOnlyList<string?> Cells)> rows)
    {
        var columns = new List<ImportColumn>();
        var map = new Dictionary<int, FieldDef>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (h.Length == 0)
            {
                // Columna sin encabezado: si tampoco tiene datos (p. ej. separador final), no existe.
                if (rows.All(r => i >= r.Cells.Count || string.IsNullOrWhiteSpace(r.Cells[i]))) { columns.Add(new("", null, null, "ignored", "Sin encabezado ni datos.")); continue; }
                columns.Add(new($"Columna {i + 1}", null, null, "error", "La columna tiene datos pero no tiene encabezado."));
                continue;
            }
            var key = Normalize(h);
            var f = e.ActiveFields.FirstOrDefault(x => Normalize(x.Name) == key) ?? e.ActiveFields.FirstOrDefault(x => Normalize(x.Label) == key);
            // Un campo oculto para el usuario se trata igual que uno inexistente: no revela que existe.
            if (f is null || f.Type == FieldTypes.Parent || !canSee(f))
                columns.Add(new(h, null, null, "error", "No corresponde a ningún campo (usa el nombre o la etiqueta del campo)."));
            else if (!used.Add(f.Name))
                columns.Add(new(h, f.Name, f.Label, "error", $"«{f.Label}» está repetida."));
            else if (f.Type == FieldTypes.Lines)
                columns.Add(new(h, f.Name, f.Label, "error", "Las partidas no se importan; captúralas en el documento."));
            else if (f.ClientLocked)
                columns.Add(new(h, f.Name, f.Label, "ignored", "Lo calcula o asigna el sistema; se ignora."));
            else if (!canEdit(f))
                columns.Add(new(h, f.Name, f.Label, "ignored", "Tu rol no lo captura al crear; se ignora."));
            else
            {
                map[i] = f;
                columns.Add(new(h, f.Name, f.Label, "ok", null));
            }
        }
        return (columns, map);
    }

    private static (List<ImportColumn>, Dictionary<int, FieldDef>) MapColumns(EntityDef e, IReadOnlyList<string> headers, Writer writer,
        IReadOnlyList<(int Row, IReadOnlyList<string?> Cells)> rows) =>
        MapColumns(e, headers, f => FieldAccess.CanSee(e, f, writer.User), f => writer.CanEdit(e, f), rows);

    internal static string Normalize(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy/MM/dd"];
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
        "dd/MM/yyyy HH:mm", "d/M/yyyy H:mm", "dd/MM/yyyy HH:mm:ss"];

    /// <summary>
    /// Texto de una celda → valor JSON que entiende <see cref="ConvertValue"/>. Las referencias se buscan por el campo
    /// para mostrar con el filtro de lectura del catálogo referido (spec 006): un registro que no se ve «no existe».
    /// </summary>
    private static async Task<(JsonNode? Node, string? Error)> CellAsync(SqlConnection cn, SqlTransaction tx, FieldDef f, string raw,
        IReadOnlyDictionary<string, EntityDef> model, UserContext user, Dictionary<(string, string), (long? Id, string? Error)> cache, CancellationToken ct)
    {
        switch (f.Type)
        {
            case FieldTypes.Reference:
            {
                var key = (f.ReferenceEntity!, raw.ToLowerInvariant());
                if (!cache.TryGetValue(key, out var hit))
                    cache[key] = hit = await FindReferenceAsync(cn, tx, model[f.ReferenceEntity!], raw, model, user, ct);
                return hit.Id is { } id ? (JsonValue.Create(id), null) : (null, hit.Error);
            }
            case FieldTypes.User:
            {
                var key = ("$user", raw.ToLowerInvariant());
                if (!cache.TryGetValue(key, out var hit))
                {
                    await using var cmd = new SqlCommand(
                        "SELECT TOP 2 Id FROM meta.[User] WHERE IsActive = 1 AND IsService = 0 AND (Username = @v OR DisplayName = @v)", cn, tx);
                    cmd.Parameters.Add(new SqlParameter("@v", raw));
                    var ids = new List<long>();
                    await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
                    cache[key] = hit = ids.Count switch
                    {
                        0 => (null, $"No existe el usuario «{raw}»."),
                        1 => (ids[0], null),
                        _ => (null, $"Hay varios usuarios «{raw}»; usa el nombre de usuario."),
                    };
                }
                return hit.Id is { } id ? (JsonValue.Create(id), null) : (null, hit.Error);
            }
            default:
                return CellValue(f, raw);
        }
    }

    /// <summary>Conversión pura de una celda (sin referencias). <c>internal</c> para pruebas.</summary>
    internal static (JsonNode? Node, string? Error) CellValue(FieldDef f, string raw)
    {
        var invalid = $"«{raw}» no es un valor válido para {f.Label}.";
        switch (f.Type)
        {
            case FieldTypes.Text:
            case FieldTypes.LongText:
                // La exportación antepone «'» a lo que una hoja de cálculo ejecutaría; aquí se quita.
                if (raw.Length > 1 && raw[0] == '\'' && raw[1] is '=' or '+' or '-' or '@') raw = raw[1..];
                return (JsonValue.Create(raw), null);
            case FieldTypes.Integer:
            case FieldTypes.Decimal:
            {
                // Formato es-MX: punto decimal; comas y espacios de miles y el símbolo de moneda se quitan.
                var s = raw.Replace("$", "").Replace(",", "").Replace(" ", "").Replace(" ", "");
                if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                        CultureInfo.InvariantCulture, out var d)) return (null, invalid);
                if (f.Type == FieldTypes.Integer && d != decimal.Truncate(d)) return (null, $"{f.Label} debe ser un número entero.");
                return (JsonValue.Create(f.Type == FieldTypes.Integer ? decimal.Truncate(d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture)), null);
            }
            case FieldTypes.Boolean:
                return Normalize(raw) switch
                {
                    "si" or "s" or "yes" or "y" or "true" or "verdadero" or "1" or "x" => (JsonValue.Create(true), null),
                    "no" or "n" or "false" or "falso" or "0" => (JsonValue.Create(false), null),
                    _ => (null, invalid),
                };
            case FieldTypes.Date:
            {
                if (DateTime.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || TryExcelDate(raw, out d))
                    return (JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null);
                return (null, $"«{raw}» no es una fecha válida para {f.Label} (usa aaaa-mm-dd o dd/mm/aaaa).");
            }
            case FieldTypes.DateTime:
            {
                if (DateTime.TryParseExact(raw, DateTimeFormats.Concat(DateFormats).ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
                    DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) || TryExcelDate(raw, out d))
                    return (JsonValue.Create(d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + (d.Kind == DateTimeKind.Utc ? "Z" : "")), null);
                return (null, $"«{raw}» no es una fecha y hora válida para {f.Label}.");
            }
            case FieldTypes.Choice:
            {
                var key = Normalize(raw);
                var option = f.Options?.FirstOrDefault(o => Normalize(o) == key);
                return option is null ? (null, $"«{raw}» no es una opción de {f.Label} ({string.Join(", ", f.Options ?? [])}).") : (JsonValue.Create(option), null);
            }
            default:
                return (null, invalid);
        }
    }

    /// <summary>Fecha de Excel (número de serie, días desde 1899-12-30).</summary>
    private static bool TryExcelDate(string raw, out DateTime d)
    {
        d = default;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || serial < 1 || serial > 2_958_465) return false;
        d = DateTime.FromOADate(serial);
        return true;
    }

    private static async Task<(long? Id, string? Error)> FindReferenceAsync(SqlConnection cn, SqlTransaction tx, EntityDef target, string raw,
        IReadOnlyDictionary<string, EntityDef> model, UserContext user, CancellationToken ct)
    {
        var missing = $"No existe «{raw}» en {target.LabelPlural}.";
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        var where = "t.[IsDeleted] = 0";
        if (target.EffectiveDisplayField is { } display)
        {
            where += $" AND t.{Quote(display)} = @v";
            cmd.Parameters.Add(new SqlParameter("@v", raw));
        }
        else if (long.TryParse(raw.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var refId))
        {
            where += " AND t.[Id] = @v";
            cmd.Parameters.Add(new SqlParameter("@v", refId));
        }
        else return (null, missing);
        if (RecordFilter.ForRecordOrParent(target, model, user) is { } scope)
        {
            scope.AddTo(cmd.Parameters);
            where += " AND " + scope.Sql;
        }
        cmd.CommandText = $"SELECT TOP 2 t.[Id] FROM {AppTable(target.Name)} t WHERE {where}";
        var ids = new List<long>();
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        return ids.Count switch
        {
            0 => (null, missing),
            1 => (ids[0], null),
            _ => (null, $"Hay varios «{raw}» en {target.LabelPlural}; no se sabe cuál elegir."),
        };
    }

    // ======================= Exportación de listados =======================

    /// <summary>
    /// El listado tal como lo ve el usuario (alcance, búsqueda, filtros, estado, orden) con las columnas pedidas que puede
    /// ver, listo para <see cref="ReportExport"/>. Solo columnas de campos: el archivo se puede volver a importar.
    /// </summary>
    public async Task<ReportResult> ExportAsync(string entityName, ListQuery q, IReadOnlyList<string>? columns, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        var visible = e.StoredFields.Where(f => f.Type != FieldTypes.Parent && FieldAccess.CanSee(e, f, user)).ToList();
        var chosen = columns is { Count: > 0 }
            ? columns.Select(c => visible.FirstOrDefault(f => string.Equals(f.Name, c, StringComparison.OrdinalIgnoreCase))).OfType<FieldDef>().DistinctBy(f => f.Name).ToList()
            : visible.Where(f => f.ShowInList).ToList();
        if (chosen.Count == 0) chosen = visible;

        var list = await ListAsync(e.Name, q with { Page = 1, PageSize = MaxExport }, user, ct, MaxExport);
        var cols = chosen.Select(f => new ReportColumn(f.Name, f.Label, "field",
            InterfaceRules.KindOf(f) switch { ValueKind.Number => "number", _ => "text" }, f.Scale)).ToList();
        var rows = list.Items.Select(item => chosen.ToDictionary(f => f.Name, f => ExportCell(e, f, item))).ToList();

        await using (var cn = await db.OpenAsync(ct))
            await Audit.WriteAsync(cn, null, http, AuditActions.Export, e.Name, null,
                after: new { rows = rows.Count, total = list.Total, columns = chosen.Select(f => f.Name), q.Search, q.State, filters = q.Filters?.Count ?? 0 });
        return new ReportResult(e.Name, "listado", e.LabelPlural, null, null, cols, rows, [], list.Total > rows.Count);
    }

    private static object? ExportCell(EntityDef e, FieldDef f, Dictionary<string, object?> item)
    {
        var v = item.GetValueOrDefault(f.Name);
        if (v is null) return null;
        var type = f.Type == FieldTypes.Formula ? f.ResultType : f.Type;
        return type switch
        {
            FieldTypes.Reference or FieldTypes.User => item.GetValueOrDefault(f.Name + "__label") ?? v,
            FieldTypes.Boolean => v is true ? "Sí" : "No",
            FieldTypes.State => e.Workflow?.FindState(v as string)?.Label ?? v,
            FieldTypes.Integer or FieldTypes.Decimal => v is long or int or decimal ? v : Convert.ToDecimal(v, CultureInfo.InvariantCulture),
            _ => v,
        };
    }
}
