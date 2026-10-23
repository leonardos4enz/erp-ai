using System.Globalization;
using System.Text.Json;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>
/// Spec 007: filtros por campo de vistas y reportes (<c>Campo op valor</c>, combinados con AND). El campo sale de los
/// metadatos (lista blanca, citado), el operador de una lista cerrada y el valor siempre va como parámetro.
/// </summary>
public static class FilterSql
{
    public const int MaxFilters = 20;
    public const int MaxInItems = 50;
    public const string Me = "$me";
    public const string Today = "$today";

    private static readonly Dictionary<ValueKind, HashSet<string>> OpsByKind = new()
    {
        [ValueKind.Text] = ["eq", "ne", "contains", "in", "blank", "notBlank"],
        [ValueKind.LongText] = ["contains", "blank", "notBlank"],
        [ValueKind.Number] = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "blank", "notBlank"],
        [ValueKind.Date] = ["eq", "ne", "gt", "gte", "lt", "lte", "blank", "notBlank"],
        [ValueKind.Boolean] = ["eq", "ne", "blank", "notBlank"],
        [ValueKind.Id] = ["eq", "ne", "in", "blank", "notBlank"],
    };

    /// <summary>Lee el parámetro <c>filters</c> (lista JSON). Inválido → 400.</summary>
    public static IReadOnlyList<ViewFilter> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<ViewFilter>>(json, MetadataStore.Json) ?? [];
            if (list.Count > MaxFilters) throw new AppException(400, $"Máximo {MaxFilters} filtros.");
            return list;
        }
        catch (JsonException) { throw new AppException(400, "Los filtros no son válidos."); }
    }

    /// <summary>Validación sin usuario (vista previa del plan y vistas personales). Devuelve el error o null.</summary>
    public static string? Validate(EntityDef e, ViewFilter filter)
    {
        var f = e.FindField(filter.Field ?? "");
        if (f is null || f.IsArchived || FieldTypes.IsVirtual(f.Type)) return $"el filtro usa '{filter.Field}', que no es un campo de {e.Name}.";
        if (filter.Label?.Length > 200) return $"la etiqueta del filtro de '{f.Name}' es demasiado larga.";
        var kind = InterfaceRules.KindOf(f);
        if (!OpsByKind[kind].Contains(filter.Op ?? "")) return $"el operador '{filter.Op}' no aplica al campo '{f.Name}'.";
        try { Values(e, f, filter, userId: 0); return null; }
        catch (FilterValueError ex) { return $"filtro de '{f.Name}': {ex.Message}"; }
    }

    /// <summary>
    /// Condición SQL (sobre el alias <c>t</c>) de los filtros para el usuario; cadena vacía si no hay. Un filtro sobre un
    /// campo que el usuario no ve se rechaza (400): filtrar por él permitiría deducir su valor (spec 006).
    /// </summary>
    public static string Build(EntityDef e, IReadOnlyList<ViewFilter>? filters, UserContext user, SqlParameterCollection ps, string prefix)
    {
        if (filters is null || filters.Count == 0) return "";
        if (filters.Count > MaxFilters) throw new AppException(400, $"Máximo {MaxFilters} filtros.");
        var parts = new List<string>();
        var n = 0;
        string Add(object v)
        {
            var name = $"@{prefix}{n++}";
            ps.Add(new SqlParameter(name, v));
            return name;
        }
        foreach (var filter in filters)
        {
            if (Validate(e, filter) is { } err) throw new AppException(400, $"Filtro inválido: {err}");
            var f = e.FindField(filter.Field)!;
            if (!FieldAccess.CanSee(e, f, user)) throw new AppException(400, "Filtro inválido: no se puede filtrar por un campo que no ves.");
            var col = $"t.{Quote(f.Name)}";
            var values = Values(e, f, filter, user.Id);
            var textual = InterfaceRules.KindOf(f) is ValueKind.Text or ValueKind.LongText;
            var blank = textual ? $"({col} IS NULL OR LTRIM(RTRIM({col})) = N'')" : $"{col} IS NULL";
            parts.Add(filter.Op switch
            {
                "eq" => $"{col} = {Add(values[0])}",
                "ne" => $"({col} <> {Add(values[0])} OR {col} IS NULL)",
                "gt" => $"{col} > {Add(values[0])}",
                "gte" => $"{col} >= {Add(values[0])}",
                "lt" => $"{col} < {Add(values[0])}",
                "lte" => $"{col} <= {Add(values[0])}",
                "contains" => $"{col} LIKE {Add("%" + EscapeLike((string)values[0]) + "%")} ESCAPE '\\'",
                "in" => $"{col} IN ({string.Join(", ", values.Select(Add))})",
                "blank" => blank,
                "notBlank" => $"NOT {blank}",
                _ => throw new AppException(400, "Filtro inválido."),
            });
        }
        return string.Join(" AND ", parts);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>Mensaje propio para el usuario (las excepciones de conversión de .NET se reemplazan por uno genérico).</summary>
    private sealed class FilterValueError(string message) : FormatException(message);

    /// <summary>Valores convertidos al tipo del campo. Lanza <see cref="FilterValueError"/> con un mensaje legible.</summary>
    private static List<object> Values(EntityDef e, FieldDef f, ViewFilter filter, int userId)
    {
        if (FilterOps.NoValue.Contains(filter.Op)) return [];
        if (filter.Value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new FilterValueError("indica un valor.");
        if (filter.Op == "in")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is 0 or > MaxInItems)
                throw new FilterValueError($"«in» necesita una lista de 1 a {MaxInItems} valores.");
            return v.EnumerateArray().Select(x => One(e, f, x, userId)).ToList();
        }
        return [One(e, f, v, userId)];
    }

    private static object One(EntityDef e, FieldDef f, JsonElement v, int userId)
    {
        var s = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
        try
        {
            switch (InterfaceRules.KindOf(f))
            {
                case ValueKind.Number:
                    return s is not null ? decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture) : v.GetDecimal();
                case ValueKind.Boolean:
                    return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new FilterValueError("debe ser true o false.");
                case ValueKind.Date:
                    if (s == Today) return DateTime.Today;
                    if (s is null) throw new FilterValueError("la fecha va como texto (aaaa-mm-dd) o $today.");
                    return f.Type == FieldTypes.DateTime || (f.Type == FieldTypes.Formula && s.Length > 10)
                        ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                        : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
                case ValueKind.Id:
                    if (s == Me)
                        return f.Type == FieldTypes.User ? userId : throw new FilterValueError("$me solo aplica a campos de tipo usuario.");
                    return s is not null ? long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) : v.GetInt64();
                default:
                    if (s is null) throw new FilterValueError("debe ser texto.");
                    if (s.Length is 0 or > 400) throw new FilterValueError("el texto va de 1 a 400 caracteres.");
                    if (f.Type == FieldTypes.Choice && f.Options?.Contains(s) != true) throw new FilterValueError($"'{s}' no es una opción de {f.Label}.");
                    if (f.Type == FieldTypes.State && e.Workflow?.FindState(s) is null) throw new FilterValueError($"'{s}' no es un estado del proceso.");
                    return s;
            }
        }
        catch (Exception ex) when (ex is not FilterValueError && ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new FilterValueError($"'{v}' no es un valor válido para {f.Label}.");
        }
    }
}
