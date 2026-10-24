using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Api.Expressions;
using Erp.Api.Security;

namespace Erp.Api.Metadata;

/// <summary>Spec 016: validación estática de las tareas programadas de un catálogo (ver plan 016).</summary>
public static partial class ScheduleRules
{
    public const int MaxSchedules = 10;
    public const int MaxLabel = 100;
    public const int MaxDescription = 300;

    public static IEnumerable<string> Validate(EntityDef e, IReadOnlyList<ScheduleDef> schedules, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        if (e.IsChild) { msgs.Add("es una partida: las tareas programadas se declaran en su documento (usa \"lines\")."); return msgs; }
        if (schedules.Count > MaxSchedules) msgs.Add($"máximo {MaxSchedules} tareas programadas por catálogo.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in schedules)
        {
            var where = $"tarea '{s.Name}'";
            if (!Identifiers.IsValid(s.Name, out var err)) msgs.Add($"{where}: {err}");
            else if (!names.Add(s.Name)) msgs.Add($"{where}: el nombre está repetido.");
            if (string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > MaxLabel) msgs.Add($"{where}: la etiqueta es obligatoria (máx. {MaxLabel} caracteres).");
            if (s.Description?.Length > MaxDescription) msgs.Add($"{where}: la descripción admite máximo {MaxDescription} caracteres.");
            msgs.AddRange(ValidateCalendar(s).Select(m => $"{where}: {m}"));

            if (string.IsNullOrWhiteSpace(s.Where)) msgs.Add($"{where}: indica qué registros procesa (where).");
            else
            {
                var analysis = ExprAnalyzer.Analyze(s.Where, e, model, roles).Errors;
                foreach (var m in analysis) msgs.Add($"{where} (where): {m}");
                // Sin SQL habría que leer el catálogo completo en memoria en cada ocurrencia.
                if (analysis.Count == 0 && TranslateError(s.Where, e, model) is { } bad)
                    msgs.Add($"{where} (where): la consulta debe poder resolverse en la base: {bad}");
            }

            if (string.IsNullOrWhiteSpace(s.Role) || !roles.Contains(s.Role)) msgs.Add($"{where}: el rol '{s.Role}' no existe.");
            if (s.BatchSize is < 1 or > ScheduleDef.MaxBatchSize) msgs.Add($"{where}: el tamaño de lote va de 1 a {ScheduleDef.MaxBatchSize}.");
            msgs.AddRange(AutomationRules.ValidateActions(e, s.Actions, model, roles, where));
        }
        return msgs;
    }

    private static string? TranslateError(string where, EntityDef e, IReadOnlyDictionary<string, EntityDef> model)
    {
        try { RecordFilter.TranslateRollup(where, e, model, new RecordFilter.ParamBag("@rf")); return null; }
        catch (ExprException ex) { return ex.Message; }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):([0-5]\d)$")]
    private static partial Regex TimeOfDay();

    public static bool TryParseAt(string? at, out int hour, out int minute)
    {
        hour = minute = 0;
        if (at is null || TimeOfDay().Match(at) is not { Success: true } m) return false;
        hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        minute = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static IEnumerable<string> ValidateCalendar(ScheduleDef s)
    {
        if (!ScheduleFrequencies.All.Contains(s.Every ?? ""))
        {
            yield return $"frecuencia '{s.Every}' no válida ({string.Join(", ", ScheduleFrequencies.All)}).";
            yield break;
        }
        // Un dato de otra frecuencia casi siempre es un error de captura («every: day» con «minute: 30»).
        var extra = new List<string>();
        if (s.Interval is not null && s.Every != ScheduleFrequencies.Minutes) extra.Add("interval");
        if (s.Minute is not null && s.Every != ScheduleFrequencies.Hour) extra.Add("minute");
        if (s.At is not null && s.Every is ScheduleFrequencies.Minutes or ScheduleFrequencies.Hour) extra.Add("at");
        if (s.Weekdays is not null && s.Every != ScheduleFrequencies.Week) extra.Add("weekdays");
        if (s.Day is not null && s.Every != ScheduleFrequencies.Month) extra.Add("day");
        foreach (var x in extra) yield return $"'{x}' no aplica a la frecuencia '{s.Every}'.";

        switch (s.Every)
        {
            case ScheduleFrequencies.Minutes:
                if (s.Interval is not { } n || n < 5 || n > 60 || 60 % n != 0)
                    yield return "interval debe ser 5, 10, 12, 15, 20, 30 o 60 minutos.";
                break;
            case ScheduleFrequencies.Hour:
                if (s.Minute is < 0 or > 59) yield return "minute va de 0 a 59.";
                break;
            default:
                if (!TryParseAt(s.At, out _, out _)) yield return "at debe ser una hora 'HH:mm' (00:00 a 23:59).";
                if (s.Every == ScheduleFrequencies.Week &&
                    (s.Weekdays is not { Count: > 0 } days || days.Any(d => d is < 1 or > 7) || days.Distinct().Count() != days.Count))
                    yield return "weekdays: indica los días sin repetir, de 1 (lunes) a 7 (domingo).";
                if (s.Every == ScheduleFrequencies.Month && s.Day is not (>= 1 and <= 31))
                    yield return "day va de 1 a 31 (si el mes no tiene ese día, se usa el último).";
                break;
        }
    }
}

/// <summary>
/// Spec 016: cuándo toca la siguiente ocurrencia de una tarea, en la hora local del entorno (la misma de <c>TODAY()</c>).
/// Es pura para probarla sin reloj ni base de datos.
/// </summary>
public static class ScheduleCalendar
{
    private static readonly string[] Weekdays = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    /// <summary>1 = lunes … 7 = domingo.</summary>
    public static int IsoWeekday(DateTime d) => ((int)d.DayOfWeek + 6) % 7 + 1;

    /// <summary>Primera ocurrencia estrictamente posterior a <paramref name="afterUtc"/>, en UTC. Las perdidas no se acumulan.</summary>
    public static DateTime Next(ScheduleDef s, DateTime afterUtc, TimeZoneInfo zone)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone), DateTimeKind.Unspecified);
        for (var guard = 0; guard < 10; guard++)
        {
            var candidate = NextLocal(s, local);
            // Una hora que no existe (cambio a horario de verano) se recorre a la primera válida.
            while (zone.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
            var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            if (utc > afterUtc) return utc;
            local = candidate; // hora ambigua (regreso al horario normal) ya pasada en UTC: la siguiente
        }
        throw new InvalidOperationException("No se pudo calcular la siguiente ocurrencia.");
    }

    private static DateTime NextLocal(ScheduleDef s, DateTime local)
    {
        var minuteStart = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
        ScheduleRules.TryParseAt(s.At, out var h, out var m);
        var at = new TimeSpan(h, m, 0);
        switch (s.Every)
        {
            case ScheduleFrequencies.Minutes:
            {
                var interval = s.Interval ?? 60;
                var since = (int)(minuteStart - local.Date).TotalMinutes;
                return local.Date.AddMinutes((since / interval + 1) * interval);
            }
            case ScheduleFrequencies.Hour:
            {
                var c = local.Date.AddHours(local.Hour).AddMinutes(s.Minute ?? 0);
                return c > local ? c : c.AddHours(1);
            }
            case ScheduleFrequencies.Day:
            {
                var c = local.Date + at;
                return c > local ? c : c.AddDays(1);
            }
            case ScheduleFrequencies.Week:
            {
                var days = (s.Weekdays ?? [1]).ToHashSet();
                for (var d = 0; d <= 7; d++)
                {
                    var c = local.Date.AddDays(d) + at;
                    if (c > local && days.Contains(IsoWeekday(c))) return c;
                }
                break;
            }
            case ScheduleFrequencies.Month:
            {
                var first = new DateTime(local.Year, local.Month, 1);
                for (var k = 0; k <= 12; k++)
                {
                    var month = first.AddMonths(k);
                    var day = Math.Min(s.Day ?? 1, DateTime.DaysInMonth(month.Year, month.Month));
                    var c = month.AddDays(day - 1) + at;
                    if (c > local) return c;
                }
                break;
            }
        }
        throw new InvalidOperationException($"Frecuencia no válida: {s.Every}.");
    }

    /// <summary>El calendario en palabras, para la pantalla del administrador.</summary>
    public static string Describe(ScheduleDef s)
    {
        string At() => s.At ?? "00:00";
        return s.Every switch
        {
            ScheduleFrequencies.Minutes => s.Interval == 60 ? "Cada hora" : $"Cada {s.Interval} minutos",
            ScheduleFrequencies.Hour => $"Cada hora, al minuto {(s.Minute ?? 0):00}",
            ScheduleFrequencies.Day => $"Diaria a las {At()}",
            ScheduleFrequencies.Week => $"Semanal: {Join((s.Weekdays ?? []).Order().Select(d => Weekdays[Math.Clamp(d, 1, 7) - 1]).ToList())} a las {At()}",
            ScheduleFrequencies.Month => s.Day >= 29
                ? $"Mensual: día {s.Day} a las {At()} (o el último día del mes)"
                : $"Mensual: día {s.Day} a las {At()}",
            _ => s.Every,
        };
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : $"{string.Join(", ", items.Take(items.Count - 1))} y {items[^1]}";

    /// <summary>Forma canónica del calendario: si cambia (un plan lo editó), se recalcula la siguiente ocurrencia.</summary>
    public static string Key(ScheduleDef s) =>
        JsonSerializer.Serialize(new { s.Every, s.Interval, s.Minute, s.At, Weekdays = s.Weekdays?.Order().ToList(), s.Day });
}
