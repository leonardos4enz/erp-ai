using System.Globalization;
using System.Text;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record ReportColumn(string Key, string Label, string Kind, string Type, int? Scale = null);
public sealed record ReportResult(string Entity, string Name, string Label, string? Description, string? Chart,
    IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, Dictionary<string, object?> Totals, bool Truncated);
public sealed record ReportSummary(string Entity, string EntityLabel, string? Group, string Name, string Label, string? Description, string? Chart);

/// <summary>
/// Spec 007: reportes con agrupaciones y totales, calculados en la base. Seguridad (spec 006): solo los registros que el
/// usuario ve (también en los catálogos relacionados) y nunca un campo oculto. Identificadores desde metadatos; valores
/// como parámetros; funciones, periodos y operadores de listas cerradas.
/// </summary>
public sealed class ReportService(Db db, MetadataStore store)
{
    public const int CommandTimeoutSeconds = 30;
    public const string NoValue = "(sin valor)";
    public const string NoAccess = "(sin acceso)";

    /// <summary>Reportes que el usuario puede consultar, en todos los catálogos.</summary>
    public async Task<IReadOnlyList<ReportSummary>> ListAsync(UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        return model.Values.Where(e => !e.IsArchived && e.Reports is { Count: > 0 })
            .SelectMany(e => e.Reports!.Where(r => Available(e, r, user, model)).Select(r =>
            {
                var root = Root(e, model);
                return new ReportSummary(e.Name, root.LabelPlural, root.Group, r.Name, r.Label, r.Description, r.Chart);
            }))
            .OrderBy(r => r.Group ?? "").ThenBy(r => r.Label, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<ReportResult> RunAsync(string entity, string report, IReadOnlyList<ViewFilter> filters, UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var (e, r) = Find(model, entity, report, user);
        return await RunAsync(e, r, filters, user, model, ct);
    }

    /// <summary>
    /// ¿El usuario puede consultar el reporte? Necesita leer el catálogo (una partida, su documento), tener uno de los
    /// roles y ver todos los campos que el reporte usa (agrupaciones, medidas y filtro fijo).
    /// </summary>
    public static bool Available(EntityDef e, ReportDef r, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (!user.Access(Root(e, model).Name).Read) return false;
        if (!user.IsSuperuser && r.Roles is not null && !user.HasRole(r.Roles)) return false;
        foreach (var path in (r.GroupBy ?? []).Select(g => g.Field).Concat(r.Measures.Select(m => m.Field).OfType<string>()))
            if (InterfaceRules.Resolve(e, path, model).Path is not { } p || !InterfaceRules.CanSee(p, e, user)) return false;
        if (!string.IsNullOrWhiteSpace(r.Filter))
        {
            try { if (RecordService.ReadsHidden(ExprParser.Parse(r.Filter), e, model, user)) return false; }
            catch (ExprException) { return false; }
        }
        return true;
    }

    internal static (EntityDef Entity, ReportDef Report) Find(IReadOnlyDictionary<string, EntityDef> model, string entity, string report, UserContext user)
    {
        // Inexistente o no disponible responden igual: no se revela qué reportes hay para otros roles.
        if (!model.TryGetValue(entity, out var e) || e.IsArchived ||
            e.Reports?.FirstOrDefault(x => string.Equals(x.Name, report, StringComparison.OrdinalIgnoreCase)) is not { } r ||
            !Available(e, r, user, model))
            throw new NotFound("El reporte no existe.");
        return (e, r);
    }

    private static EntityDef Root(EntityDef e, IReadOnlyDictionary<string, EntityDef> model) =>
        e.IsChild && model.TryGetValue(e.ParentEntity ?? "", out var p) ? p : e;

    // ======================= SQL =======================

    private sealed class Query(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user, SqlCommand cmd)
    {
        public readonly StringBuilder From = new($"{AppTable(e.Name)} t");
        private readonly Dictionary<string, string> _joins = new(StringComparer.OrdinalIgnoreCase);
        private int _n;

        /// <summary>
        /// Une el catálogo relacionado <paramref name="target"/> por la columna <paramref name="column"/> aplicando su
        /// propio filtro de lectura: si el usuario no ve el registro, la unión no lo trae («sin acceso»).
        /// </summary>
        public string Join(string column, EntityDef target)
        {
            var key = $"{column}>{target.Name}";
            if (_joins.TryGetValue(key, out var alias)) return alias;
            alias = $"j{_n++}";
            var where = "t.[IsDeleted] = 0";
            if (RecordFilter.ForRecordOrParent(target, model, user) is { } p)
            {
                var renamed = p.Prefixed($"{alias}_");
                renamed.AddTo(cmd.Parameters);
                where += " AND " + renamed.Sql;
            }
            From.Append($" LEFT JOIN (SELECT * FROM {AppTable(target.Name)} t WHERE {where}) {alias} ON {alias}.[Id] = {column}");
            _joins[key] = alias;
            return alias;
        }

        public string JoinUser(string column)
        {
            var key = $"{column}>user";
            if (_joins.TryGetValue(key, out var alias)) return alias;
            alias = $"u{_n++}";
            From.Append($" LEFT JOIN meta.[User] {alias} ON {alias}.[Id] = {column}");
            _joins[key] = alias;
            return alias;
        }

        /// <summary>Columna SQL del campo (propio o relacionado) y, si es relacionado, el alias de la unión.</summary>
        public (string Column, string? JoinAlias, string? ViaColumn) Column(FieldPath p)
        {
            if (p.Via is null) return ($"t.{Quote(p.Field.Name)}", null, null);
            var via = $"t.{Quote(p.Via.Name)}";
            var alias = Join(via, p.Owner);
            return ($"{alias}.{Quote(p.Field.Name)}", alias, via);
        }
    }

    private sealed record Group(string Key, string Select, IReadOnlyList<string> GroupBy, string Order, Func<object?[], object?> Value);

    public async Task<ReportResult> RunAsync(EntityDef e, ReportDef r, IReadOnlyList<ViewFilter> filters, UserContext user,
        IReadOnlyDictionary<string, EntityDef> model, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var q = new Query(e, model, user, cmd);
        var columns = new List<ReportColumn>();

        // ---- Agrupaciones ----
        var groups = new List<Group>();
        var ordinal = 0; // posición de la siguiente columna del SELECT
        var selects = new List<string>();
        foreach (var (g, gi) in (r.GroupBy ?? []).Select((g, i) => (g, i)))
        {
            var p = InterfaceRules.Resolve(e, g.Field, model).Path!;
            var (col, alias, via) = q.Column(p);
            var label = g.Label ?? (p.Via is null ? p.Field.Label : $"{p.Via.Label} · {p.Field.Label}");
            var key = $"g{gi}";
            var kind = InterfaceRules.KindOf(p.Field);
            // Relacionado no visible: la clave padre existe pero la unión no trajo el registro.
            var noAccess = alias is null ? "0" : $"CASE WHEN {via} IS NOT NULL AND {alias}.[Id] IS NULL THEN 1 ELSE 0 END";
            var baseOrd = ordinal;
            string[] parts;
            Func<object?[], object?> value;
            string order;
            switch (kind)
            {
                case ValueKind.Id when p.Field.Type == FieldTypes.User:
                {
                    var u = q.JoinUser(col);
                    parts = [col, $"{u}.[DisplayName]", noAccess];
                    value = v => Flag(v[2]) ? NoAccess : v[0] is null ? NoValue : v[1] ?? $"#{v[0]}";
                    order = $"{u}.[DisplayName]";
                    break;
                }
                case ValueKind.Id:
                {
                    var target = model[p.Field.ReferenceEntity!];
                    var j = q.Join(col, target);
                    var display = target.EffectiveDisplayField is { } d && !FieldAccess.DisplayHidden(target, user) // spec 029 (F-B)
                        ? $"CAST({j}.{Quote(d)} AS nvarchar(400))" : "CAST(NULL AS nvarchar(400))";
                    // Un registro referido que no se ve también es «sin acceso», aunque el principal sí se vea.
                    var hidden = $"CASE WHEN ({noAccess}) = 1 OR ({col} IS NOT NULL AND {j}.[Id] IS NULL) THEN 1 ELSE 0 END";
                    parts = [col, display, hidden];
                    value = v => Flag(v[2]) ? NoAccess : v[0] is null ? NoValue : v[1] ?? $"#{v[0]}";
                    order = display;
                    break;
                }
                case ValueKind.Date:
                {
                    var expr = g.Period switch
                    {
                        "year" => $"DATEFROMPARTS(YEAR({col}), 1, 1)",
                        "month" => $"DATEFROMPARTS(YEAR({col}), MONTH({col}), 1)",
                        _ => $"CAST({col} AS date)",
                    };
                    var fmt = g.Period switch { "year" => "yyyy", "month" => "yyyy-MM", _ => "yyyy-MM-dd" };
                    parts = [expr, noAccess];
                    value = v => Flag(v[1]) ? NoAccess : v[0] is DateTime dt ? dt.ToString(fmt, CultureInfo.InvariantCulture) : NoValue;
                    order = expr;
                    break;
                }
                case ValueKind.Boolean:
                    parts = [col, noAccess];
                    value = v => Flag(v[1]) ? NoAccess : v[0] is bool b ? (b ? "Sí" : "No") : NoValue;
                    order = col;
                    break;
                case ValueKind.Number:
                    parts = [col, noAccess];
                    value = v => Flag(v[1]) ? NoAccess : v[0];
                    order = col;
                    break;
                default:
                {
                    var states = p.Field.Type == FieldTypes.State ? p.Owner.Workflow : null;
                    parts = [col, noAccess];
                    value = v => Flag(v[1]) ? NoAccess : v[0] is null ? NoValue
                        : states?.FindState(v[0] as string)?.Label ?? Convert.ToString(v[0], CultureInfo.InvariantCulture);
                    order = col;
                    break;
                }
            }
            var idx = Enumerable.Range(baseOrd, parts.Length).ToArray();
            groups.Add(new Group(key, string.Join(", ", parts), parts, order, row => value(idx.Select(i => row[i]).ToArray())));
            selects.AddRange(parts);
            ordinal += parts.Length;
            columns.Add(new ReportColumn(key, label, "group", kind == ValueKind.Number ? "number" : "text"));
        }

        // ---- Medidas ----
        var measureStart = ordinal;
        var measureSql = new List<string>();
        foreach (var (m, mi) in r.Measures.Select((m, i) => (m, i)))
        {
            FieldPath? p = m.Field is null ? null : InterfaceRules.Resolve(e, m.Field, model).Path!;
            var col = p is null ? null : q.Column(p).Column;
            var sql = m.Fn switch
            {
                "count" => col is null ? "COUNT(*)" : $"COUNT({col})",
                "sum" => $"SUM({col})",
                "avg" => $"AVG(CAST({col} AS decimal(38, 6)))",
                "min" => $"MIN({col})",
                "max" => $"MAX({col})",
                _ => throw new AppException(400, "Medida no válida."),
            };
            measureSql.Add(sql);
            var fieldLabel = p is null ? null : p.Via is null ? p.Field.Label : $"{p.Via.Label} · {p.Field.Label}";
            var label = m.Label ?? m.Fn switch
            {
                "count" => fieldLabel is null ? "Registros" : $"Registros con {fieldLabel}",
                "sum" => fieldLabel!,
                "avg" => $"Promedio de {fieldLabel}",
                "min" => $"Mínimo de {fieldLabel}",
                _ => $"Máximo de {fieldLabel}",
            };
            var isDate = p is not null && InterfaceRules.KindOf(p.Field) == ValueKind.Date && m.Fn is "min" or "max";
            int? scale = m.Fn == "count" ? 0 : p?.Field.Scale ?? (p?.Field.Type == FieldTypes.Integer ? 0 : 2);
            if (m.Fn == "avg") scale = Math.Max(2, scale ?? 2);
            columns.Add(new ReportColumn($"m{mi}", label, "measure", isDate ? "date" : "number", isDate ? null : scale));
        }
        selects.AddRange(measureSql);

        // ---- Filtros: alcance del usuario, filtro fijo y filtros del usuario ----
        var where = new StringBuilder("t.[IsDeleted] = 0");
        if (RecordFilter.ForRecordOrParent(e, model, user) is { } scope)
        {
            var s = scope.Prefixed("base_");
            s.AddTo(cmd.Parameters);
            where.Append(" AND ").Append(s.Sql);
        }
        if (!string.IsNullOrWhiteSpace(r.Filter))
        {
            var bag = new RecordFilter.ParamBag("@rf");
            where.Append(" AND ").Append(RecordFilter.Translate(r.Filter, e, RecordFilter.Of(user), bag));
            foreach (var (n, v) in bag.Items) cmd.Parameters.Add(new SqlParameter(n, v));
        }
        if (FilterSql.Build(e, filters, user, cmd.Parameters, "uf") is { Length: > 0 } uf) where.Append(" AND ").Append(uf);

        var limit = r.Limit ?? InterfaceRules.DefaultRows;
        cmd.Parameters.Add(new SqlParameter("@take", limit + 1));
        var desc = string.Equals(r.Dir, "desc", StringComparison.OrdinalIgnoreCase);
        var asc = string.Equals(r.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        var orderBy = r.OrderBy == "measure"
            ? $"{measureSql[0]} {(asc ? "ASC" : "DESC")}"
            : string.Join(", ", groups.Select(g => $"{g.Order} {(desc ? "DESC" : "ASC")}"));

        var sb = new StringBuilder();
        if (groups.Count > 0)
            sb.Append($"SELECT TOP (@take) {string.Join(", ", selects)} FROM {q.From} WHERE {where} " +
                      $"GROUP BY {string.Join(", ", groups.SelectMany(g => g.GroupBy).Where(x => x != "0"))} ORDER BY {orderBy};");
        sb.Append($"SELECT {string.Join(", ", measureSql)} FROM {q.From} WHERE {where};");
        cmd.CommandText = sb.ToString();

        var rows = new List<Dictionary<string, object?>>();
        var totals = new Dictionary<string, object?>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (groups.Count > 0)
            {
                while (await reader.ReadAsync(ct))
                {
                    var raw = new object?[reader.FieldCount];
                    for (var i = 0; i < raw.Length; i++) raw[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    var row = new Dictionary<string, object?>();
                    foreach (var g in groups) row[g.Key] = Normalize(g.Value(raw));
                    for (var mi = 0; mi < r.Measures.Count; mi++) row[$"m{mi}"] = Measure(raw[measureStart + mi], columns[groups.Count + mi], r.Measures[mi]);
                    rows.Add(row);
                }
                await reader.NextResultAsync(ct);
            }
            if (await reader.ReadAsync(ct))
                for (var mi = 0; mi < r.Measures.Count; mi++)
                    totals[$"m{mi}"] = Measure(reader.IsDBNull(mi) ? null : reader.GetValue(mi), columns[groups.Count + mi], r.Measures[mi]);
        }
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new ReportResult(e.Name, r.Name, r.Label, r.Description, r.Chart, columns, rows, totals, truncated);
    }

    private static bool Flag(object? v) => v is int i ? i == 1 : v is not null && Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;

    private static object? Normalize(object? v) => v switch
    {
        long or int or short => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        _ => v,
    };

    /// <summary>
    /// Regresión (revisión exploratoria 007, F-04): una suma sin registros es 0 (un indicador «Monto comprado» vacío
    /// mostraba «—»). Promedio, mínimo y máximo sin datos sí quedan vacíos: no hay valor que mostrar.
    /// </summary>
    private static object? Measure(object? v, ReportColumn c, ReportMeasure m) => v switch
    {
        null => m.Fn is "count" or "sum" ? 0m : null,
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        _ => c.Scale is { } s ? decimal.Round(Convert.ToDecimal(v, CultureInfo.InvariantCulture), s, MidpointRounding.AwayFromZero) : v,
    };
}
