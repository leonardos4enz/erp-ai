using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Mcp;

/// <param name="Structure">Del cuerpo de la API (ya formado por la herramienta) y la entrada, al objeto que publica <c>structuredContent</c>.</param>
public sealed record McpTool(StudioTool Tool, string Title, JsonObject OutputSchema, Func<JsonNode?, JsonObject, JsonObject> Structure)
{
    public string Name => Tool.Name;
    /// <summary>Spec 044: el alcance MCP que la habilita (el de Studio, salvo <c>entregar</c>: <c>proponer</c>).</summary>
    public string Scope { get; init; } = Tool.Scope;
    public string Description { get; init; } = Tool.Description;
    public bool ReadOnly { get; init; } = true;
    public bool Idempotent { get; init; } = true;
    public bool Destructive { get; init; }
    /// <summary>Spec 044 (H3 CA1): una escritura que no es idempotente exige clave.</summary>
    public bool NeedsKey => !Idempotent;
    public bool UsesSandbox => Tool.Target == ToolTarget.Sandbox;

    /// <summary>El de la herramienta de Studio; con clave de idempotencia (obligatoria) si la necesita.</summary>
    public JsonObject InputSchema
    {
        get
        {
            var schema = (JsonObject)Tool.InputSchema.DeepClone();
            if (!NeedsKey) return schema;
            schema["properties"]![McpIdempotency.Field] = new JsonObject
            {
                ["type"] = "string", ["minLength"] = 8, ["maxLength"] = 100, ["pattern"] = "^[A-Za-z0-9._:-]{8,100}$", ["description"] = McpIdempotency.Description,
            };
            ((JsonArray)schema["required"]!).Add(McpIdempotency.Field);
            return schema;
        }
    }
}

/// <summary>
/// Spec 038 (H4, H5): el catálogo de Studio (033) tal como se publica por MCP. Las herramientas del entorno real son todas de
/// lectura (el despachador niega escrituras). Spec 044: con <c>aislado</c>, las de la copia aislada de la conexión y
/// <c>aislado_descartar</c>; con <c>proponer</c>, <c>entregar</c> (una propuesta que la persona aplica en Studio). Cada una declara su
/// esquema de salida y cómo se forma su <c>structuredContent</c> (siempre un objeto: un arreglo de la API va en <c>items</c>),
/// para que un cliente en <i>code mode</i> genere tipos fiables (H5 CA1).
/// </summary>
public static class McpTools
{

    public const string DiscardName = "aislado_descartar";

    /// <summary>Spec 044 (H2 CA3): solo del MCP (Studio suelta su copia al terminar la conversación).</summary>
    private static readonly StudioTool Discard = new(DiscardName,
        "Borra la copia aislada de esta conexión; la siguiente herramienta aislada prepara otra desde el modelo actual. Úsala al terminar de probar.",
        StudioScopes.Sandbox, ToolTarget.Server,
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false },
        null, null, false, null);

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

        // Spec 044 (H2): la copia aislada de la conexión. Las que escriben exigen clave (H3).
        Sbx("aislado_aplicar_plan", "Aplicar un plan en la copia aislada", Obj(["applied", "operations"], ("applied", Bool()), ("operations", Int())), write: true),
        Sbx("aislado_crear", "Crear un registro de prueba en la copia", Obj(["id"], ("id", Int())), write: true),
        Sbx("aislado_actualizar", "Cambiar un registro de la copia", Obj(["id"], ("id", Int())), write: true),
        Sbx("aislado_transicion", "Ejecutar una transición en la copia", Obj(["id"], ("id", Int())), write: true),
        Sbx("aislado_leer", "Leer un registro de la copia", Obj(["id"], ("id", Int()))),
        Sbx("aislado_listar", "Listar registros de la copia", Obj(["items"], ("items", Arr(Obj(["id"], ("id", Int())))), ("total", Int()))),
        Sbx("aislado_procesar_asincronas", "Procesar las automatizaciones asíncronas de la copia", Obj(["processed", "runs"],
            ("processed", Int()), ("runs", Arr(Obj(["id", "status"], ("id", Int()), ("status", Str()))))), write: true),
        new(Discard, "Descartar la copia aislada", Obj(["discarded"], ("discarded", Bool())), (body, _) => AsObject(body))
            { ReadOnly = false, Idempotent = true, Destructive = true },
        // Spec 044 (H4): la entrega como propuesta que la persona revisa en Studio.
        new(StudioTools.Find(StudioTools.Deliver)!, "Entregar una propuesta", Obj(["proposalId", "status", "url"],
                ("proposalId", Int(1)), ("status", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("proposed", "answered") }), ("url", Str())),
            (body, _) => AsObject(body))
        {
            Scope = McpScopes.Propose, ReadOnly = false, Idempotent = false,
            Description = "Entrega tu trabajo a la persona como propuesta, que ella revisa y aplica (o descarta) en ERP AI Studio: el Plan de Cambios " +
                          "(omítelo si no propones cambios), un resumen, lo que probaste con su resultado, lo que falló y los riesgos. No aplica nada. " +
                          "El plan se compila contra el modelo real: si no compila, recibes los errores para corregirlo.",
        },
    ];


    /// <summary>Las herramientas que esta persona, con estos alcances, puede usar: las que <c>tools/list</c> le da (H4 CA1).</summary>
    public static IEnumerable<McpTool> For(UserContext user, IReadOnlySet<string> scopes, RouteDispatcher routes) =>
        All.Where(t => scopes.Contains(t.Scope)
                       && t.Tool.Capability(routes) is { } required && required.AnyOf.Any(c => EndpointCapabilities.Has(c, user)));

    public static McpTool? Find(string? name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>La definición de <c>tools/list</c>.</summary>
    public static JsonObject Describe(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema,
        ["outputSchema"] = t.OutputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title, ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = t.Destructive, ["idempotentHint"] = t.Idempotent, ["openWorldHint"] = false,
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

    private static McpTool Sbx(string name, string title, JsonObject schema, bool write = false) =>
        new(StudioTools.Find(name) is { Target: ToolTarget.Sandbox } t ? t : throw new InvalidOperationException($"La herramienta {name} no es de la copia aislada."),
            title, schema, (body, _) => AsObject(body))
        { ReadOnly = !write, Idempotent = !write };

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
