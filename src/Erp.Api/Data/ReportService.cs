using System.Globalization;
using System.Text;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <param name="Period">Spec 039: periodo de una agrupación por fecha (day, week, month, quarter, year).</param>
/// <param name="Fn">Spec 039: función de una medida (la gráfica rellena con 0 los periodos sin registros de un conteo o una suma).</param>
public sealed record ReportColumn(string Key, string Label, string Kind, string Type, int? Scale = null, string? Period = null, string? Fn = null);
/// <param name="ChartOptions">Spec 039: cómo se dibuja la gráfica, tal como se declaró.</param>
/// <param name="Comparison">Spec 039 (H4): último periodo y el anterior, solo para un indicador sobre un reporte por periodo.</param>
public sealed record ReportResult(string Entity, string Name, string Label, string? Description, string? Chart,
    IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, Dictionary<string, object?> Totals, bool Truncated,
    ReportChartOptions? ChartOptions = null, ReportComparison? Comparison = null);
public sealed record ReportSummary(string Entity, string EntityLabel, string? Group, string Name, string Label, string? Description, string? Chart);
public sealed record ReportPeriodValues(string Label, Dictionary<string, object?> Values);
/// <param name="Previous">El periodo inmediato anterior del calendario (sin registros: conteo y suma en 0); null si no hay ninguno.</param>
public sealed record ReportComparison(ReportPeriodValues Current, ReportPeriodValues? Previous);

/// <summary>
/// Spec 007: reportes con agrupaciones y totales, calculados en la base. Seguridad (spec 006): solo los registros que el
/// usuario ve (también en los catálogos relacionados, en cada salto: spec 039) y nunca un campo oculto. Identificadores desde
/// metadatos; valores como parámetros; funciones, periodos y operadores de listas cerradas. Las medidas calculadas (039) se
/// evalúan en memoria sobre las otras medidas: nunca llegan a SQL.
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
    /// roles y ver todos los campos que el reporte usa (agrupaciones, medidas y filtro fijo), y cada referencia del camino.
    /// </summary>
    public static bool Available(EntityDef e, ReportDef r, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (!user.Access(Root(e, model).Name).Read) return false;
        if (!user.IsSuperuser && r.Roles is not null && !user.HasRole(r.Roles)) return false;
        foreach (var path in (r.GroupBy ?? []).Select(g => g.Field).Concat(r.Measures.Select(m => m.Field).OfType<string>()))
            if (InterfaceRules.Resolve(e, path, model).Path is not { } p || !InterfaceRules.CanSee(p, user) || !CanReadPath(p, user, model)) return false;
        if (!string.IsNullOrWhiteSpace(r.Filter))
        {
            try { if (RecordService.ReadsHidden(ExprParser.Parse(r.Filter), e, model, user)) return false; }
            catch (ExprException) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Regresión (revisión adversarial 039, F-A): cada salto llega a un catálogo que el usuario debe poder leer. Sin ese
    /// permiso solo se ve lo que ofrece el buscador de una referencia: el campo para mostrar del último catálogo del camino.
    /// </summary>
    private static bool CanReadPath(FieldPath p, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        for (var i = 0; i < p.Hops.Count; i++)
        {
            var to = p.Hops[i].To;
            if (user.Access(Root(to, model).Name).Read) continue;
            var display = i == p.Hops.Count - 1 && string.Equals(p.Field.Name, to.EffectiveDisplayField, StringComparison.OrdinalIgnoreCase)
                && !FieldAccess.DisplayHidden(to, user);
            if (!display) return false;
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

    // ======================= Periodos (spec 039) =======================

    /// <summary>Expresión SQL del inicio del periodo de <paramref name="col"/> (lista cerrada; la semana empieza en lunes sin depender de DATEFIRST).</summary>
    public static string PeriodSql(string col, string? period) => period switch
    {
        "year" => $"DATEFROMPARTS(YEAR({col}), 1, 1)",
        "quarter" => $"DATEFROMPARTS(YEAR({col}), (DATEPART(quarter, {col}) - 1) * 3 + 1, 1)",
        "month" => $"DATEFROMPARTS(YEAR({col}), MONTH({col}), 1)",
        "week" => $"DATEADD(day, -((DATEPART(weekday, {col}) + @@DATEFIRST + 5) % 7), CAST({col} AS date))",
        _ => $"CAST({col} AS date)",
    };

    /// <summary>Etiqueta del periodo que empieza en <paramref name="start"/>: 2026, 2026-T1, 2026-05, 2026-S05 (ISO) o 2026-05-04.</summary>
    public static string PeriodLabel(DateTime start, string? period) => period switch
    {
        "year" => start.ToString("yyyy", CultureInfo.InvariantCulture),
        "quarter" => $"{start.Year:0000}-T{(start.Month - 1) / 3 + 1}",
        "month" => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        "week" => $"{ISOWeek.GetYear(start):0000}-S{ISOWeek.GetWeekOfYear(start):00}",
        _ => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Inicio del periodo inmediato anterior al que empieza en <paramref name="start"/>; null antes del año 1 (regresión,
    /// revisión adversarial 039 F-C: un registro fechado en el año 1 daba 500 en todo el tablero).
    /// </summary>
    public static DateTime? PreviousPeriod(DateTime start, string? period)
    {
        try
        {
            return period switch
            {
                "year" => start.AddYears(-1),
                "quarter" => start.AddMonths(-3),
                "month" => start.AddMonths(-1),
                "week" => start.AddDays(-7),
                _ => start.AddDays(-1),
            };
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ======================= SQL =======================

    private sealed class Query(IReadOnlyDictionary<string, EntityDef> model, UserContext user, SqlCommand cmd, EntityDef e)
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

        /// <summary>
        /// Columna SQL del campo (propio o relacionado) y, si es relacionado, la condición «sin acceso»: en algún salto la
        /// llave existe y la unión (filtrada con el alcance del catálogo destino) no trajo el registro (spec 039: en cada salto).
        /// </summary>
        public (string Column, string NoAccess) Column(FieldPath p)
        {
            var from = "t";
            var hidden = new List<string>();
            foreach (var h in p.Hops)
            {
                var via = $"{from}.{Quote(h.Via.Name)}";
                var alias = Join(via, h.To);
                hidden.Add($"({via} IS NOT NULL AND {alias}.[Id] IS NULL)");
                from = alias;
            }
            var noAccess = hidden.Count == 0 ? "0" : $"CASE WHEN {string.Join(" OR ", hidden)} THEN 1 ELSE 0 END";
            return ($"{from}.{Quote(p.Field.Name)}", noAccess);
        }
    }

    private sealed record Group(string Key, IReadOnlyList<string> GroupBy, string Order, Func<object?[], object?> Value);

    /// <param name="compare">Spec 039 (H4): calcula la comparación aunque el reporte no sea un indicador (tarjeta <c>kpi</c> de un tablero).</param>
    public async Task<ReportResult> RunAsync(EntityDef e, ReportDef r, IReadOnlyList<ViewFilter> filters, UserContext user,
        IReadOnlyDictionary<string, EntityDef> model, CancellationToken ct, bool compare = false)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var q = new Query(model, user, cmd, e);
        var columns = new List<ReportColumn>();

        // ---- Agrupaciones ----
        var groups = new List<Group>();
        var ordinal = 0; // posición de la siguiente columna del SELECT
        var selects = new List<string>();
        string? periodExpr = null; // spec 039: inicio del periodo de la primera agrupación por fecha (para la comparación)
        foreach (var (g, gi) in (r.GroupBy ?? []).Select((g, i) => (g, i)))
        {
            var p = InterfaceRules.Resolve(e, g.Field, model).Path!;
            var (col, noAccess) = q.Column(p);
            var label = g.Label ?? p.Label;
            var key = $"g{gi}";
            var kind = InterfaceRules.KindOf(p.Field);
            var baseOrd = ordinal;
            string[] parts;
            Func<object?[], object?> value;
            string order;
            string? period = null;
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
                    var displayHidden = FieldAccess.DisplayHidden(target, user); // spec 029 (F-B)
                    var display = target.EffectiveDisplayField is { } d && !displayHidden
                        ? $"CAST({j}.{Quote(d)} AS nvarchar(400))" : "CAST(NULL AS nvarchar(400))";
                    // Un registro referido que no se ve también es «sin acceso», aunque el principal sí se vea. Spec 040 (auditoría 7,
                    // D-10): y uno cuyo campo para mostrar está oculto para quien ve, como en el listado (`__label`), en una sola fila.
                    var hidden = displayHidden
                        ? $"CASE WHEN ({noAccess}) = 1 OR {col} IS NOT NULL THEN 1 ELSE 0 END"
                        : $"CASE WHEN ({noAccess}) = 1 OR ({col} IS NOT NULL AND {j}.[Id] IS NULL) THEN 1 ELSE 0 END";
                    // Regresión (revisión adversarial 039, F-B): sin acceso, ni el id ni el nombre agrupan: todos los registros
                    // que no se ven van en una sola fila (antes, una por registro oculto: se sabía cuántos eran y cuánto sumaba cada uno).
                    var shown = $"CASE WHEN {hidden} = 1 THEN NULL ELSE {display} END";
                    parts = [$"CASE WHEN {hidden} = 1 THEN NULL ELSE {col} END", shown, hidden];
                    value = v => Flag(v[2]) ? NoAccess : v[0] is null ? NoValue : v[1] ?? $"#{v[0]}";
                    order = shown;
                    break;
                }
                case ValueKind.Date:
                {
                    var expr = PeriodSql(col, g.Period);
                    period = g.Period ?? "day";
                    periodExpr ??= expr;
                    var pp = period;
                    parts = [expr, noAccess];
                    value = v => Flag(v[1]) ? NoAccess : v[0] is DateTime dt ? PeriodLabel(dt, pp) : NoValue;
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
            groups.Add(new Group(key, parts, order, row => value(idx.Select(i => row[i]).ToArray())));
            selects.AddRange(parts);
            ordinal += parts.Length;
            columns.Add(new ReportColumn(key, label, "group", kind == ValueKind.Number ? "number" : "text", Period: period));
        }

        // ---- Medidas (las calculadas no van a SQL: spec 039) ----
        var measureStart = ordinal;
        var measureSql = new List<string>();
        var sqlIndex = new int[r.Measures.Count]; // posición de la medida entre las de SQL; -1 si es calculada
        foreach (var (m, mi) in r.Measures.Select((m, i) => (m, i)))
        {
            if (m.Fn == "calc")
            {
                sqlIndex[mi] = -1;
                columns.Add(new ReportColumn($"m{mi}", m.Label ?? m.Name ?? "Calculada", "measure", "number", m.Scale ?? 2, Fn: "calc"));
                continue;
            }
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
            sqlIndex[mi] = measureSql.Count;
            measureSql.Add(sql);
            var fieldLabel = p?.Label;
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
            columns.Add(new ReportColumn($"m{mi}", label, "measure", isDate ? "date" : "number", isDate ? null : scale, Fn: m.Fn));
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

        // Spec 039 (H4): el último periodo con datos y el anterior, con el mismo WHERE (alcance, filtro fijo y del usuario).
        var comparing = (compare || r.Chart == "kpi") && periodExpr is not null && InterfaceRules.IsComparable(e, r, model);

        var sb = new StringBuilder();
        if (groups.Count > 0)
            sb.Append($"SELECT TOP (@take) {string.Join(", ", selects)} FROM {q.From} WHERE {where} " +
                      $"GROUP BY {string.Join(", ", groups.SelectMany(g => g.GroupBy).Where(x => x != "0"))} ORDER BY {orderBy};");
        sb.Append($"SELECT {string.Join(", ", measureSql)} FROM {q.From} WHERE {where};");
        if (comparing)
            sb.Append($"SELECT TOP (2) {periodExpr}, {string.Join(", ", measureSql)} FROM {q.From} WHERE {where} AND {periodExpr} IS NOT NULL " +
                      $"GROUP BY {periodExpr} ORDER BY {periodExpr} DESC;");
        cmd.CommandText = sb.ToString();

        var rows = new List<Dictionary<string, object?>>();
        var totals = new Dictionary<string, object?>();
        var periods = new List<(DateTime Start, Dictionary<string, object?> Values)>();
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
                    Measures(row, k => raw[measureStart + k], columns, groups.Count, r, sqlIndex);
                    rows.Add(row);
                }
                await reader.NextResultAsync(ct);
            }
            if (await reader.ReadAsync(ct))
                Measures(totals, k => reader.IsDBNull(k) ? null : reader.GetValue(k), columns, groups.Count, r, sqlIndex);
            if (comparing && await reader.NextResultAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(0)) continue;
                    var values = new Dictionary<string, object?>();
                    Measures(values, k => reader.IsDBNull(k + 1) ? null : reader.GetValue(k + 1), columns, groups.Count, r, sqlIndex);
                    periods.Add((reader.GetDateTime(0), values));
                }
        }
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new ReportResult(e.Name, r.Name, r.Label, r.Description, r.Chart, columns, rows, totals, truncated, r.ChartOptions,
            comparing ? Comparison(periods, r.GroupBy![0].Period, columns, groups.Count, r, sqlIndex) : null);
    }

    /// <summary>
    /// Spec 039 (H4 CA2): el anterior es el periodo inmediato del calendario; si no tuvo registros, conteo y suma valen 0 y
    /// las demás quedan vacías (las calculadas se evalúan sobre esos valores).
    /// </summary>
    private static ReportComparison? Comparison(List<(DateTime Start, Dictionary<string, object?> Values)> periods, string? period,
        List<ReportColumn> columns, int groups, ReportDef r, int[] sqlIndex)
    {
        if (periods.Count == 0) return null;
        var (start, values) = periods[0];
        if (PreviousPeriod(start, period) is not { } prevStart) return new ReportComparison(new(PeriodLabel(start, period), values), null);
        Dictionary<string, object?> prev;
        if (periods.Count > 1 && periods[1].Start == prevStart) prev = periods[1].Values;
        else
        {
            prev = [];
            Measures(prev, _ => null, columns, groups, r, sqlIndex);
        }
        return new ReportComparison(new(PeriodLabel(start, period), values), new(PeriodLabel(prevStart, period), prev));
    }

    /// <summary>Valores de las medidas de una fila (o del total): primero las de SQL y luego las calculadas sobre ellas.</summary>
    private static void Measures(Dictionary<string, object?> into, Func<int, object?> sqlValue, List<ReportColumn> columns, int groups,
        ReportDef r, int[] sqlIndex)
    {
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var mi = 0; mi < r.Measures.Count; mi++)
        {
            if (sqlIndex[mi] < 0) continue;
            var raw = sqlValue(sqlIndex[mi]);
            var v = Measure(raw, columns[groups + mi], r.Measures[mi]);
            into[$"m{mi}"] = v;
            // Regresión (revisión adversarial 039, F-E): la calculada usa el valor sin redondear (un promedio de 93.333…, no
            // 93.33) y solo su resultado se redondea; sin registros, conteo y suma valen 0 como en la tabla.
            if (r.Measures[mi].Name is { } n)
                named[n] = raw is null ? v : raw is DateTime ? null : Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        }
        for (var mi = 0; mi < r.Measures.Count; mi++)
            if (sqlIndex[mi] < 0) into[$"m{mi}"] = Calc(r.Measures[mi], named);
    }

    /// <summary>Spec 039 (H5): la expresión sobre las otras medidas; un vacío o dividir entre cero dejan vacía la medida.</summary>
    public static decimal? Calc(ReportMeasure m, IReadOnlyDictionary<string, object?> named) => ReportCalc.Evaluate(m.Expr!, named, m.Scale ?? 2);

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
