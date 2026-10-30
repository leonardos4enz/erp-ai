using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Mcp;

/// <param name="Structure">Del cuerpo de la API (ya formado por la herramienta) y la entrada, al objeto que publica <c>structuredContent</c>.</param>
public sealed record McpTool(StudioTool Tool, string Title, JsonObject OutputSchema, Func<JsonNode?, JsonObject, JsonObject> Structure)
{
    public string Name => Tool.Name;
}

/// <summary>
/// Spec 038 (H4, H5): el catálogo de Studio (033) tal como se publica por MCP. Solo las herramientas del entorno real (todas de
/// lectura: el despachador niega escrituras); la copia aislada y <c>entregar</c> no se exponen (CLARIFY 2). Cada una declara su
/// esquema de salida y cómo se forma su <c>structuredContent</c> (siempre un objeto: un arreglo de la API va en <c>items</c>),
/// para que un cliente en <i>code mode</i> genere tipos fiables (H5 CA1).
/// </summary>
public static class McpTools
{

    public static readonly IReadOnlyList<McpTool> All =
    [
        Def("listar_catalogos", "Catálogos del modelo", Items(Obj(["name", "label", "fields"],
            ("name", Str()), ("label", Str()), ("group", Nullable("string")), ("parentEntity", Nullable("string")),
            ("perCompany", Nullable("boolean")), ("fields", Int())))),
        Def("ver_catalogo", "Definición de un catálogo", Obj(["entity", "table", "tableExists"],
            ("entity", Obj(["name", "label", "fields"], ("name", Str()), ("label", Str()), ("fields", Arr(Obj([]))))),
            ("app", Obj([])), ("table", Str()), ("tableExists", Bool()), ("packages", Arr(Obj([]))), ("dashboards", Arr(Obj([]))),
            ("referencedBy", Arr(Edge())), ("roles", Arr(Obj(["role", "entity"], ("role", Str()), ("entity", Str())))))),
        Def("relaciones", "Relaciones entre catálogos", Obj(["nodes", "edges"],
            ("apps", Arr(Obj([]))), ("nodes", Arr(Obj(["name"], ("name", Str())))), ("edges", Arr(Edge())))),
        Def("base_fisica", "Base de datos física", Obj(["schema", "tables"],
            ("schema", Str()), ("tables", Arr(Obj(["schema", "name", "columns"], ("schema", Str()), ("name", Str()),
                ("columns", Arr(Obj(["name", "type", "nullable"], ("name", Str()), ("type", Str()), ("nullable", Bool()))))))),
            ("external", Arr(Obj([]))), ("drift", Arr(Obj([]))), ("apps", Arr(Obj([]))))),
        Def("donde_se_usa", "Dónde se usa", Items(Obj(["entity", "kind", "location"],
            ("entity", Str()), ("kind", Str()), ("location", Str()), ("expression", Nullable("string")), ("target", Obj([])), ("tab", Str())))),
        Def("automatizaciones", "Flujo de automatizaciones", Obj(["nodes", "edges"],
            ("apps", Arr(Obj([]))), ("nodes", Arr(Obj(["name"], ("name", Str())))),
            ("edges", Arr(Obj(["from", "to", "name", "trigger", "mode", "action"], ("from", Str()), ("to", Str()), ("name", Str()),
                ("trigger", Str()), ("mode", Str()), ("action", Str())))))),
        Def("seguridad", "Matriz de seguridad", Obj(["roles", "entities", "cells"],
            ("roles", Arr(Obj(["name"], ("name", Str())))), ("entities", Arr(Obj(["name"], ("name", Str())))),
            ("cells", Arr(Obj(["role", "entity", "read", "create", "update", "delete"], ("role", Str()), ("entity", Str()),
                ("read", Bool()), ("create", Bool()), ("update", Bool()), ("delete", Bool())))))),
        Def("inventario_rutas", "Inventario de rutas de la API", Items(Obj(["method", "route", "capability"],
            ("method", Str()), ("route", Str()), ("capability", Str()), ("effect", Nullable("string")), ("gate", Nullable("string")),
            ("costly", Nullable("string"))))),
        Def("riesgos", "Registro de riesgos", Items(Obj(["id", "text", "severity", "decision", "state"],
            ("id", Str()), ("text", Str()), ("severity", Str()), ("decision", Str()), ("state", Str())))),
        Def("funciones_expresion", "Funciones del lenguaje de expresiones", Items(Obj(["name", "min", "max"],
            ("name", Str()), ("min", Int()), ("max", Int())))),
        Def("validar_expresion", "Validar una expresión", Obj(["errors"], ("errors", Arr(new JsonObject())))),
        Def("compilar_plan", "Compilar un Plan de Cambios", Obj(["valid", "steps", "warnings"],
            ("valid", Bool()), ("errors", new JsonObject { ["type"] = new JsonArray("array", "null") }),
            ("steps", Arr(Obj([], ("operation", new JsonObject()), ("description", Nullable("string"))))), ("warnings", Arr(new JsonObject())))),
        new(Studio("leer_registros"), "Leer registros", Obj(["items", "total", "page", "pageSize", "nextPage"],
                ("items", Arr(Obj(["id"], ("id", Int())))), ("total", Int()), ("page", Int(1)), ("pageSize", Int(1)),
                ("nextPage", new JsonObject { ["type"] = new JsonArray("integer", "null") })),
            Page),
        new(Studio("leer_registro"), "Leer un registro", Obj(["id"], ("id", Int())), (body, _) => AsObject(body)),
    ];

    /// <summary>Las herramientas que esta persona, con estos alcances, puede usar: las que <c>tools/list</c> le da (H4 CA1).</summary>
    public static IEnumerable<McpTool> For(UserContext user, IReadOnlySet<string> scopes, RouteDispatcher routes) =>
        All.Where(t => scopes.Contains(t.Tool.Scope)
                       && t.Tool.Capability(routes) is { } required && required.AnyOf.Any(c => EndpointCapabilities.Has(c, user)));

    public static McpTool? Find(string? name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>La definición de <c>tools/list</c>.</summary>
    public static JsonObject Describe(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Tool.Description,
        ["inputSchema"] = t.Tool.InputSchema.DeepClone(),
        ["outputSchema"] = t.OutputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title, ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false,
        },
    };

    // ---------- Salida estructurada ----------

    private static McpTool Def(string name, string title, JsonObject schema) =>
        new(Studio(name), title, schema, (body, _) => AsObject(body));

    /// <summary>Un arreglo va en <c>items</c> (MCP exige un objeto); un objeto, tal cual.</summary>
    internal static JsonObject AsObject(JsonNode? body) => body switch
    {
        JsonObject o => (JsonObject)o.DeepClone(),
        JsonArray a => new JsonObject { ["items"] = a.DeepClone() },
        _ => new JsonObject { ["value"] = body?.DeepClone() },
    };

    /// <summary>H5 CA2: página explícita y la siguiente (nula en la última).</summary>
    private static JsonObject Page(JsonNode? body, JsonObject input)
    {
        var items = body?["items"] as JsonArray ?? [];
        var total = body?["total"]?.GetValue<int>() ?? items.Count;
        var page = int.TryParse(StudioTools.Opt(input, "page", 6), out var p) && p > 0 ? p : 1;
        var size = int.TryParse(StudioTools.Opt(input, "pageSize", 4), out var s) && s > 0 ? s : StudioTools.RecordsPage;
        return new JsonObject
        {
            ["items"] = items.DeepClone(), ["total"] = total, ["page"] = page, ["pageSize"] = size,
            ["nextPage"] = (long)page * size < total ? page + 1 : null,
        };
    }

    private static StudioTool Studio(string name) =>
        StudioTools.Find(name) is { Target: ToolTarget.Real } t ? t : throw new InvalidOperationException($"La herramienta {name} no es del entorno real.");

    // ---------- Esquemas (JSON Schema 2020-12, el subconjunto que usa MCP) ----------

    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };
    private static JsonObject Int(int? minimum = null) => minimum is { } m ? new() { ["type"] = "integer", ["minimum"] = m } : new() { ["type"] = "integer" };
    private static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray(type, "null") };
    private static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Items(JsonObject item) => Obj(["items"], ("items", Arr(item)));
    private static JsonObject Edge() => Obj(["from", "to", "kind"], ("from", Str()), ("field", Str()), ("to", Str()), ("kind", Str()));

    private static JsonObject Obj(string[] required, params (string Name, JsonObject Schema)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in props) properties[name] = schema;
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return o;
    }
}
