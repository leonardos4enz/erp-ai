using System.Globalization;
using System.Text.RegularExpressions;

namespace Erp.Api.OData;

public sealed record ODataOrder(string Property, bool Descending);

/// <summary>
/// Spec 018: opciones de consulta admitidas. Lo que no se admite responde 400 (nunca se ignora en silencio): un cliente
/// que pide <c>$search</c> y recibe todo creería que filtró.
/// </summary>
public sealed partial record ODataQuery(string? Filter, IReadOnlyList<string>? Select, IReadOnlyList<ODataOrder> OrderBy, int? Top, int Skip,
    bool Count, IReadOnlyList<string> Expand)
{
    public const int MaxOrderBy = 5;
    public const int MaxExpand = 10;
    public const int MaxSelect = 200;
    /// <summary>Spec 021 (H7 CA1): <c>OFFSET</c> profundo es costoso; para avanzar más, se filtra por Id.</summary>
    public const int MaxSkip = 100_000;
    /// <summary>Spec 021 (H7 CA2): partidas expandidas por página (lo fija el servidor con <c>OData:MaxExpandedLines</c>).</summary>
    public const int DefaultMaxExpandedLines = 5_000;
    public int MaxExpandedLines { get; init; } = DefaultMaxExpandedLines;

    public static readonly ODataQuery Empty = new(null, null, [], null, 0, false, []);

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { "$filter", "$select", "$orderby", "$top", "$skip", "$count", "$expand", "$format" };

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)(?:\s+(asc|desc))?$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderItem();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    public static ODataQuery Parse(IQueryCollection query)
    {
        string? Get(string key)
        {
            var hits = query.Where(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) return null;
            if (hits.Count > 1 || hits[0].Value.Count > 1) throw new ODataException($"La opción {key} viene repetida.");
            return hits[0].Value.ToString();
        }

        foreach (var key in query.Keys.Where(k => k.StartsWith('$')))
            if (!Supported.Contains(key)) throw new ODataException($"La opción {key} no se admite (solo $filter, $select, $orderby, $top, $skip, $count, $expand y $format).");

        if (Get("$format") is { } format && !(format == "json" || format.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
            throw new ODataException("Solo se admite $format=json.");

        var filter = Get("$filter");
        var select = Get("$select") is { } s ? Names(s, "$select", MaxSelect, allowStar: true) : null;
        var expand = Get("$expand") is { } x ? Names(x, "$expand", MaxExpand, allowStar: false) ?? [] : [];

        var order = new List<ODataOrder>();
        if (Get("$orderby") is { } o)
        {
            foreach (var item in Split(o, "$orderby"))
            {
                var m = OrderItem().Match(item);
                if (!m.Success) throw new ODataException(item.Contains('/')
                    ? "$orderby: las rutas a otros catálogos no se admiten."
                    : $"$orderby: «{item}» no es válido (propiedad seguida de asc o desc).");
                order.Add(new ODataOrder(m.Groups[1].Value, m.Groups[2].Value == "desc"));
            }
            if (order.Count > MaxOrderBy) throw new ODataException($"$orderby admite hasta {MaxOrderBy} propiedades.");
        }

        var count = Get("$count") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ODataException("$count es true o false."),
        };
        var skip = Number("$skip", Get("$skip")) ?? 0;
        if (skip > MaxSkip) throw new ODataException($"$skip admite hasta {MaxSkip:N0}: para avanzar más, filtra por Id (p. ej. $filter=Id gt …).");
        return new ODataQuery(filter, select, order, Number("$top", Get("$top")), skip, count, expand);
    }

    private static int? Number(string key, string? value)
    {
        if (value is null) return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
            : throw new ODataException($"{key} es un número entero no negativo.");
    }

    private static IEnumerable<string> Split(string value, string key)
    {
        var items = value.Split(',', StringSplitOptions.TrimEntries);
        if (items.Any(i => i.Length == 0)) throw new ODataException($"{key} tiene un elemento vacío.");
        return items;
    }

    /// <summary>Lista de nombres; <c>*</c> (solo en $select) = todas, devuelve null.</summary>
    private static List<string>? Names(string value, string key, int max, bool allowStar)
    {
        var items = Split(value, key).ToList();
        if (allowStar && items is ["*"]) return null;
        foreach (var i in items)
        {
            if (i.Contains('(')) throw new ODataException($"{key}: las opciones anidadas ({i}) no se admiten.");
            if (i.Contains('/')) throw new ODataException($"{key}: las rutas ({i}) no se admiten; la expansión es de un nivel.");
            if (!Name().IsMatch(i)) throw new ODataException($"{key}: «{i}» no es un nombre válido.");
        }
        if (items.Count > max) throw new ODataException($"{key} admite hasta {max} elementos.");
        return items.Distinct(StringComparer.Ordinal).ToList();
    }
}
