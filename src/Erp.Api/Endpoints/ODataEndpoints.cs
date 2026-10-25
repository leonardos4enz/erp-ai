using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.OData;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

/// <summary>
/// Spec 018 (H3, H4): API OData v4 de solo lectura. Sesión de la app, clave de API o token con <c>odata.read</c>; dentro,
/// el rol del usuario decide catálogos, campos y registros (006, 010, 015).
/// </summary>
public static partial class ODataEndpoints
{
    public const int PageSize = 500;
    private const string JsonType = "application/json; odata.metadata=minimal; charset=utf-8";

    // Regresión (revisión exploratoria 018, F-02): un id fuera de rango es «el registro no existe», no «el conjunto no existe».
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_]*)(?:\((\d{1,40})\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    /// <summary>Formato de error de OData JSON (no ProblemDetails): los clientes OData lo leen así.</summary>
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new Dictionary<string, object> { ["error"] = new Dictionary<string, string> { ["code"] = code, ["message"] = message } },
            statusCode: status, contentType: JsonType);

    public static void MapOData(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/odata").RequireAuthorization();
        g.MapGet("/", (HttpContext http, MetadataStore store, CancellationToken ct) => Run(http, async () =>
        {
            var model = await store.GetAsync(ct);
            var root = Root(http);
            return Results.Json(new Dictionary<string, object>
            {
                ["@odata.context"] = $"{root}/$metadata",
                ["value"] = Csdl.EntitySets(model, http.CurrentUser())
                    .Select(e => new Dictionary<string, string> { ["name"] = e.Name, ["kind"] = "EntitySet", ["url"] = e.Name, ["title"] = e.LabelPlural }).ToList(),
            }, contentType: JsonType);
        })).Requires(Capability.Records);
        // OData:MaxPageSize (1..500) solo para que la suite E2E pruebe la paginación sin crear cientos de registros.
        var config = app.ServiceProvider.GetRequiredService<IConfiguration>();
        var pageSize = Math.Clamp(config.GetValue("OData:MaxPageSize", PageSize), 1, PageSize);
        // Spec 021 (H7 CA2): partidas expandidas por página; la suite lo baja para probarlo con pocos registros.
        var maxLines = Math.Clamp(config.GetValue("OData:MaxExpandedLines", ODataQuery.DefaultMaxExpandedLines), 1, ODataQuery.DefaultMaxExpandedLines);
        g.MapGet("/{segment}", (string segment, HttpContext http, MetadataStore store, RecordService records, CancellationToken ct) =>
            Run(http, () => SegmentAsync(segment, http, store, records, pageSize, maxLines, ct))).Requires(Capability.Records);
        g.MapMethods("/{**rest}", ["POST", "PUT", "PATCH", "DELETE", "MERGE"], (HttpContext http) =>
        {
            http.Response.Headers.Allow = "GET";
            return Error(405, "MethodNotAllowed", "La API OData es de solo lectura: usa GET.");
        }).Requires(Capability.Records).ReadsOnly();
    }

    private static string Root(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/odata";

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers["OData-Version"] = "4.0";
        try { return await action(); }
        catch (AppException ex)
        {
            var code = ex.Status switch { 400 => "BadRequest", 403 => "Forbidden", 404 => "NotFound", _ => "Error" };
            return Error(ex.Status, code, ex.Message);
        }
    }

    private static async Task<IResult> SegmentAsync(string segment, HttpContext http, MetadataStore store, RecordService records, int pageSize,
        int maxLines, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var model = await store.GetAsync(ct);
        if (segment == "$metadata")
            return Results.Text(Csdl.Write(model, user), "application/xml; charset=utf-8", Encoding.UTF8);

        var m = Segment().Match(segment);
        // Catálogo que no existe, que es partida o que el rol no lee: el mismo 404 (no revela cuál).
        if (!m.Success || !model.TryGetValue(m.Groups[1].Value, out var e) || !ODataModel.IsEntitySet(e, user))
            throw new NotFound("El conjunto de entidades no existe.");
        var q = ODataQuery.Parse(http.Request.Query) with { MaxExpandedLines = maxLines };
        var root = Root(http);

        if (m.Groups[2].Success)
        {
            if (q.Top is not null || q.Skip > 0 || q.Count || q.OrderBy.Count > 0 || q.Filter is not null)
                throw new ODataException("Al pedir un registro por id solo se admiten $select y $expand.");
            if (!long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new NotFound("El registro no existe.");
            var row = await records.ODataGetAsync(e, model, id, q, user, ct) ?? throw new NotFound("El registro no existe.");
            var single = new Dictionary<string, object?> { ["@odata.context"] = $"{root}/$metadata#{e.Name}/$entity" };
            foreach (var (k, v) in row) single[k] = v;
            return Results.Json(single, contentType: JsonType);
        }

        // Página = mín($top restante, 500); el resto, por @odata.nextLink.
        var take = Math.Min(q.Top ?? pageSize, pageSize);
        var page = take == 0 && !q.Count ? new ODataRows([], null, false) : await records.ODataListAsync(e, model, q, q.Skip, take, user, ct);
        var result = new Dictionary<string, object?> { ["@odata.context"] = $"{root}/$metadata#{e.Name}" };
        if (page.Count is { } count) result["@odata.count"] = count;
        result["value"] = page.Items;
        var remaining = q.Top is { } top ? top - page.Items.Count : (int?)null;
        if (page.More && remaining is not <= 0)
            result["@odata.nextLink"] = $"{root}/{e.Name}{NextQuery(http.Request.Query, q.Skip + page.Items.Count, remaining)}";
        return Results.Json(result, contentType: JsonType);
    }

    /// <summary>Las mismas opciones con <c>$skip</c> y <c>$top</c> recalculados.</summary>
    private static string NextQuery(IQueryCollection query, int skip, int? top)
    {
        var parts = query.Where(kv => !string.Equals(kv.Key, "$skip", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, "$top", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key).Replace("%24", "$")}={Uri.EscapeDataString(kv.Value.ToString())}").ToList();
        if (top is { } t) parts.Add($"$top={t}");
        parts.Add($"$skip={skip}");
        return "?" + string.Join('&', parts);
    }
}
