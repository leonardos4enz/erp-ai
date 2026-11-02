using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Companion;

/// <summary>Lo que necesita una herramienta del Acompañante: la persona (recargada), el proveedor de la app y la conversación.</summary>
public sealed record CompanionContext(UserContext User, IServiceProvider Services, int ConversationId, int Turn, Guid Lease);

/// <param name="Routes">Rutas del inventario que usa (todas de lectura); la capacidad es la de cada ruta.</param>
/// <param name="Data">Devuelve registros: su salida se borra al terminar el turno (H2 CA4).</param>
public sealed record CompanionTool(string Name, string Description, JsonObject InputSchema, bool Data, IReadOnlyList<(string Method, string Route)> Routes,
    Func<CompanionToolRunner, CompanionContext, JsonObject, CancellationToken, Task<ToolOutput>> Run);

/// <summary>
/// Spec 046 (H2, H3): catálogo del Acompañante. Cada herramienta es una o varias rutas de la API de **lectura** ejecutadas por
/// <see cref="RouteDispatcher"/> con la identidad de la persona (ADR-0008, ADR-0010): ve lo que ella ve. La única que no es una
/// ruta, <see cref="Propose"/>, no escribe: guarda una acción que la persona confirma (<see cref="CompanionActions"/>).
/// </summary>
public static class CompanionTools
{
    public const string Propose = "proponer_accion";
    public const int RecordsPage = 20;
    public const int MaxPageSize = 50;
    /// <summary>H3 CA2: catálogos que revisa «mis pendientes» y muestras por estado.</summary>
    public const int PendingEntities = 15;
    public const int PendingSamples = 5;

    private const string Entities = "/api/model/entities";
    private const string Entity = "/api/model/entities/{name}";
    private const string List = "/api/data/{entity}/";
    private const string Record = "/api/data/{entity}/{id:long}";
    private const string Me = "/api/auth/me";

    public static readonly IReadOnlyList<CompanionTool> All =
    [
        Tool("mis_modulos", "Los módulos del sistema que la persona puede usar, con sus catálogos.", Schema(), false, [("GET", "/api/model/apps")],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", "/api/model/apps"), null, ct)),
        Tool("mis_catalogos", "Los catálogos y documentos que la persona puede ver o capturar (nombre interno, etiqueta, módulo y si tiene proceso).",
            Schema(), false, [("GET", Entities)],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", Entities), ShapeEntities, ct)),
        Tool("como_funciona", "Cómo funciona un catálogo para la persona: sus campos (obligatorios, calculados con su fórmula, de solo lectura), su proceso (estados y transiciones con quién puede y su condición) y sus reglas con su mensaje.",
            Schema(("catalogo", "string", "Nombre interno del catálogo (de mis_catalogos)", true)), false, [("GET", Entity)],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", Entity, V(("name", StudioTools.Req(i, "catalogo")))), ShapeEntity, ct)),
        Tool("buscar_registros", $"Registros de un catálogo como los ve la persona ({RecordsPage} por página, hasta {MaxPageSize}); se puede buscar por texto y filtrar por estado (nombre interno del estado).",
            Schema(("catalogo", "string", "Nombre interno del catálogo", true), ("buscar", "string", "Texto a buscar", false),
                ("estado", "string", "Nombre interno del estado", false), ("pagina", "integer", "Página (1…)", false), ("porPagina", "integer", $"Registros por página (1…{MaxPageSize})", false)),
            true, [("GET", List)],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", List, V(("entity", StudioTools.Req(i, "catalogo"))),
                Q(("pageSize", PageSize(i)), ("page", Page(i)), ("search", StudioTools.Opt(i, "buscar")), ("state", StudioTools.Opt(i, "estado", 100)))), ShapeList, ct)),
        Tool("ver_registro", "Un registro como lo ve la persona: sus valores, su estado, las transiciones que puede ejecutar (o el motivo por el que no) y si lo puede modificar.",
            Schema(("catalogo", "string", "Nombre interno del catálogo", true), ("id", "integer", "Número del registro", true)), true, [("GET", Record)],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", Record, V(("entity", StudioTools.Req(i, "catalogo")), ("id", Id(i)))), ShapeRecord, ct)),
        Tool("historial_registro", "Los cambios de estado de un registro: quién, cuándo, de qué estado a cuál y su comentario.",
            Schema(("catalogo", "string", "Nombre interno del catálogo", true), ("id", "integer", "Número del registro", true)), true, [("GET", Record + "/history")],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", Record + "/history", V(("entity", StudioTools.Req(i, "catalogo")), ("id", Id(i)))), null, ct)),
        Tool("buscar_folio", "Busca documentos por su folio en todos los catálogos que la persona ve.",
            Schema(("folio", "string", "Folio o parte de él", true)), true, [("GET", "/api/search")],
            (r, c, i, ct) => r.GetAsync(c, new RouteCall("GET", "/api/search", Query: Q(("q", StudioTools.Req(i, "folio", 100)))), null, ct)),
        Tool("por_que_no_puedo", "Explica qué le impide a la persona una acción: su permiso sobre el catálogo, el estado del registro, las transiciones con su motivo y si el registro está fuera de lo que puede modificar.",
            Schema(("catalogo", "string", "Nombre interno del catálogo", true), ("id", "integer", "Número del registro (opcional)", false)), true,
            [("GET", Me), ("GET", Entity), ("GET", Record)], (r, c, i, ct) => r.WhyNotAsync(c, i, ct)),
        Tool("mis_pendientes", $"Lo que le toca a la persona: por catálogo con proceso, los registros en estados desde donde ella puede ejecutar una transición (hasta {PendingEntities} catálogos, {PendingSamples} ejemplos por estado).",
            Schema(), true, [("GET", Me), ("GET", Entities), ("GET", List)], (r, c, i, ct) => r.PendingAsync(c, ct)),
        Tool(Propose, "Propone a la persona crear un registro, cambiar campos de uno o ejecutar una transición. No hace el cambio: la persona ve la vista previa y lo confirma o lo descarta en el panel. Úsala solo cuando la persona te pida hacer algo.",
            Schema(("tipo", "string", "crear, editar o transicion", true), ("catalogo", "string", "Nombre interno del catálogo", true),
                ("id", "integer", "Número del registro (editar y transicion)", false), ("valores", "object", "Valores por nombre interno de campo (crear y editar; en transicion, los que pide)", false),
                ("transicion", "string", "Nombre interno de la transición", false), ("comentario", "string", "Comentario de la transición", false),
                ("explicacion", "string", "Qué hará y por qué, en palabras de la persona", true)),
            true, [], (r, c, i, ct) => r.ProposeAsync(c, i, ct)),
    ];

    public static CompanionTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>H2 CA4: las que devuelven registros; su salida se purga de pasos e historial al cerrar el turno.</summary>
    public static readonly IReadOnlyList<string> DataTools = All.Where(t => t.Data).Select(t => t.Name).ToList();

    public static IReadOnlyList<StudioToolSpec> Specs { get; } = All.Select(t => new StudioToolSpec(t.Name, t.Description, t.InputSchema)).ToList();

    /// <summary>Spec 040 (D-03), como Studio: un mensaje sin lo leído; si no se puede leer, un aviso en su lugar.</summary>
    public static string PurgeContent(string content)
    {
        try { return StudioTranscript.Purge(content, DataTools) ?? content; }
        catch (Exception) { return StudioTranscript.Serialize([new StudioText(StudioTools.Unreadable)]); }
    }

    // ---------- Definición ----------

    private static CompanionTool Tool(string name, string description, JsonObject schema, bool data, IReadOnlyList<(string, string)> routes,
        Func<CompanionToolRunner, CompanionContext, JsonObject, CancellationToken, Task<ToolOutput>> run) => new(name, description, schema, data, routes, run);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
            properties[p.Name] = p.Type == "integer"
                ? new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = p.Description }
                : new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    internal static string Id(JsonObject i) =>
        long.TryParse(StudioTools.Req(i, "id", 20), out var id) && id > 0 ? id.ToString() : throw new ToolInputException("«id» debe ser un número entero positivo.");

    private static string PageSize(JsonObject i) =>
        StudioTools.Opt(i, "porPagina", 4) is { } p
            ? int.TryParse(p, out var n) && n is > 0 and <= MaxPageSize ? n.ToString() : throw new ToolInputException($"«porPagina» va de 1 a {MaxPageSize}.")
            : RecordsPage.ToString();

    private static string? Page(JsonObject i) =>
        StudioTools.Opt(i, "pagina", 6) is { } p ? (int.TryParse(p, out var n) && n is > 0 and <= 10_000 ? n.ToString() : throw new ToolInputException("«pagina» no es válida.")) : null;

    internal static Dictionary<string, string?> Q(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
    internal static Dictionary<string, string?> V(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    // ---------- Formas (etiquetas para hablar en palabras de la persona) ----------

    internal static JsonNode? ShapeEntities(JsonNode? body) => body is JsonArray a
        ? new JsonArray(a.OfType<JsonObject>().Where(e => e["parentEntity"] is null).Select(e => (JsonNode)new JsonObject
        {
            ["nombre"] = e["name"]?.DeepClone(), ["etiqueta"] = e["labelPlural"]?.DeepClone(), ["modulo"] = e["group"]?.DeepClone(),
            ["tieneProceso"] = e["workflow"] is JsonObject, ["porEmpresa"] = e["perCompany"]?.DeepClone(),
        }).ToArray())
        : body;

    internal static JsonNode? ShapeEntity(JsonNode? body)
    {
        if (body is not JsonObject e) return body;
        var states = (e["workflow"]?["states"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(s => s["name"]?.GetValue<string>() ?? "", s => s["label"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);
        string Label(JsonNode? n) => n?.GetValue<string>() is { } s && states.TryGetValue(s, out var l) ? l : n?.GetValue<string>() ?? "";
        var shaped = new JsonObject
        {
            ["nombre"] = e["name"]?.DeepClone(), ["etiqueta"] = e["label"]?.DeepClone(), ["etiquetaPlural"] = e["labelPlural"]?.DeepClone(),
            ["descripcion"] = e["description"]?.DeepClone(),
            ["campos"] = new JsonArray((e["fields"] as JsonArray ?? []).OfType<JsonObject>().Where(f => f["isArchived"]?.GetValue<bool>() != true).Select(f => (JsonNode)Compact(new JsonObject
            {
                ["nombre"] = f["name"]?.DeepClone(), ["etiqueta"] = f["label"]?.DeepClone(), ["tipo"] = f["type"]?.DeepClone(),
                ["obligatorio"] = f["required"]?.GetValue<bool>() == true ? true : null, ["unico"] = f["unique"]?.GetValue<bool>() == true ? true : null,
                ["formula"] = f["formula"]?.DeepClone(), ["opciones"] = f["options"]?.DeepClone(), ["referenciaA"] = f["referenceEntity"]?.DeepClone(),
                ["partidasDe"] = f["childEntity"]?.DeepClone(), ["acceso"] = f["access"]?.DeepClone(), ["editableEn"] = f["editableIn"]?.DeepClone(),
                ["obligatorioPara"] = f["requiredForRoles"]?.DeepClone(), ["valorInicial"] = f["defaultValue"]?.DeepClone(),
            })).ToArray()),
            ["reglas"] = new JsonArray((e["rules"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (JsonNode)Compact(new JsonObject
            {
                ["mensaje"] = r["message"]?.DeepClone(), ["debeCumplirse"] = r["assert"]?.DeepClone(), ["cuando"] = r["when"]?.DeepClone(),
                ["campo"] = r["field"]?.DeepClone(), ["soloAviso"] = r["severity"]?.GetValue<string>() == "warning" ? true : null,
            })).ToArray()),
        };
        if (e["workflow"] is JsonObject wf)
            shaped["proceso"] = new JsonObject
            {
                ["estadoInicial"] = Label(wf["initial"]),
                ["estados"] = new JsonArray((wf["states"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                {
                    ["nombre"] = s["name"]?.DeepClone(), ["etiqueta"] = s["label"]?.DeepClone(), ["seEditaEnEste"] = s["editable"]?.DeepClone() ?? true,
                }).ToArray()),
                ["transiciones"] = new JsonArray((wf["transitions"] as JsonArray ?? []).OfType<JsonObject>().Where(t => t["system"]?.GetValue<bool>() != true)
                    .Select(t => (JsonNode)Compact(new JsonObject
                    {
                        ["nombre"] = t["name"]?.DeepClone(), ["etiqueta"] = t["label"]?.DeepClone(),
                        ["desde"] = new JsonArray((t["from"] as JsonArray ?? []).Select(f => (JsonNode)JsonValue.Create(Label(f))!).ToArray()),
                        ["hacia"] = Label(t["to"]), ["roles"] = t["roles"]?.DeepClone(), ["condicion"] = t["when"]?.DeepClone(),
                        ["mensajeSiNoSeCumple"] = t["whenMessage"]?.DeepClone(), ["pideComentario"] = t["requireComment"]?.GetValue<bool>() == true ? true : null,
                        ["camposObligatorios"] = t["requiredFields"]?.DeepClone(),
                    })).ToArray()),
            };
        return shaped;
    }

    /// <summary>La lista sin lo que no sirve para explicar (versión, acceso por campo): registros, total y cuántos hay por estado.</summary>
    internal static JsonNode? ShapeList(JsonNode? body) => body is JsonObject o
        ? new JsonObject
        {
            ["total"] = o["total"]?.DeepClone(),
            ["registros"] = new JsonArray((o["items"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (JsonNode)StripRecord(r)).ToArray()),
            ["porEstado"] = o["stateCounts"] is JsonArray sc
                ? new JsonArray(sc.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                {
                    ["estado"] = s["state"]?.DeepClone(), ["etiqueta"] = s["label"]?.DeepClone(), ["cuantos"] = s["count"]?.DeepClone(),
                }).ToArray())
                : null,
        }
        : body;

    internal static JsonNode? ShapeRecord(JsonNode? body) => body is JsonObject o ? StripRecord(o) : body;

    private static JsonObject StripRecord(JsonObject r)
    {
        var copy = (JsonObject)r.DeepClone();
        foreach (var k in new[] { "_rowVersion", "_layout" }) copy.Remove(k);
        return copy;
    }

    private static JsonObject Compact(JsonObject o)
    {
        foreach (var k in o.Where(p => p.Value is null).Select(p => p.Key).ToList()) o.Remove(k);
        return o;
    }
}

/// <summary>
/// Spec 046: ejecuta las herramientas del Acompañante. Valida la entrada (la manda el modelo: no confiable) y convierte cualquier
/// falla en un mensaje seguro, como <see cref="StudioToolRunner"/>. Las rutas se despachan sin escribir.
/// </summary>
public sealed class CompanionToolRunner(RouteDispatcher routes, CompanionActions actions, ILogger<CompanionToolRunner> log)
{
    public async Task<ToolOutput> RunAsync(CompanionContext ctx, string name, JsonElement input, CancellationToken ct)
    {
        var tool = CompanionTools.Find(name);
        if (tool is null) return ToolOutput.Fail($"La herramienta «{ToolOutput.Trim(name)}» no existe.");
        if (input.ValueKind != JsonValueKind.Object) return ToolOutput.Fail("La entrada debe ser un objeto JSON.");
        try
        {
            return await tool.Run(this, ctx, (JsonObject)JsonNode.Parse(input.GetRawText())!, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ToolInputException ex) { return ToolOutput.Fail(ex.Message); }
        catch (AppException ex) { return ToolOutput.Fail(ex.Message, ex.Status == 403 ? "denied" : "error"); }
        catch (StudioLeaseLost) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Error no controlado en la herramienta del Acompañante {Tool}", name);
            return ToolOutput.Fail("Ocurrió un error inesperado.");
        }
    }

    /// <summary>Una ruta de lectura como la persona (el despachador rechaza toda ruta con efecto: ADR-0010).</summary>
    internal Task<RouteResult> ReadAsync(CompanionContext c, RouteCall call, CancellationToken ct) =>
        routes.SendAsync(c.Services, c.User, call, allowWrites: false, ct);

    internal async Task<ToolOutput> GetAsync(CompanionContext c, RouteCall call, Func<JsonNode?, JsonNode?>? shape, CancellationToken ct) =>
        StudioToolRunner.Output(await ReadAsync(c, call, ct), shape);

    internal Task<ToolOutput> ProposeAsync(CompanionContext c, JsonObject i, CancellationToken ct) => actions.ProposeAsync(this, c, i, ct);

    /// <summary>
    /// H3 CA1: el permiso de la persona sobre el catálogo (de <c>/api/auth/me</c>), cómo funciona el proceso y, con un registro,
    /// su estado y cada transición con su motivo. Todo por las rutas de siempre: un catálogo o registro que no ve da su 403/404.
    /// </summary>
    internal async Task<ToolOutput> WhyNotAsync(CompanionContext c, JsonObject i, CancellationToken ct)
    {
        var entity = StudioTools.Req(i, "catalogo");
        var id = i["id"] is null ? null : CompanionTools.Id(i);
        var def = await ReadAsync(c, new RouteCall("GET", "/api/model/entities/{name}", CompanionTools.V(("name", entity))), ct);
        if (!def.Ok) return StudioToolRunner.Output(def, null);
        var me = await ReadAsync(c, new RouteCall("GET", "/api/auth/me"), ct);
        var access = me.Body?["entities"]?[entity];
        var data = new JsonObject
        {
            ["catalogo"] = def.Body?["label"]?.DeepClone(),
            ["tusPermisos"] = new JsonObject
            {
                ["ver"] = access?["read"]?.GetValue<bool>() == true, ["crear"] = access?["create"]?.GetValue<bool>() == true,
                ["editar"] = access?["update"]?.GetValue<bool>() == true, ["eliminar"] = access?["delete"]?.GetValue<bool>() == true,
            },
            ["tusRoles"] = me.Body?["roles"]?.DeepClone(),
            ["proceso"] = (CompanionTools.ShapeEntity(def.Body) as JsonObject)?["proceso"]?.DeepClone(),
        };
        if (id is not null)
        {
            var rec = await ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/{id:long}", CompanionTools.V(("entity", entity), ("id", id))), ct);
            if (!rec.Ok) return StudioToolRunner.Output(rec, null);
            var wf = rec.Body?["_workflow"];
            data["registro"] = new JsonObject
            {
                ["id"] = rec.Body?["id"]?.DeepClone(),
                ["estado"] = wf?["stateLabel"]?.DeepClone(),
                ["seEditaEnEsteEstado"] = wf?["editable"]?.DeepClone(),
                ["puedesModificarlo"] = rec.Body?["_access"]?["update"]?.DeepClone(),
                ["puedesEliminarlo"] = rec.Body?["_access"]?["delete"]?.DeepClone(),
                ["motivoSiNo"] = rec.Body?["_access"]?["reason"]?.DeepClone(),
                ["transicionesDesdeAqui"] = new JsonArray((wf?["transitions"] as JsonArray ?? []).OfType<JsonObject>().Select(t => (JsonNode)new JsonObject
                {
                    ["nombre"] = t["name"]?.DeepClone(), ["etiqueta"] = t["label"]?.DeepClone(), ["puedes"] = t["allowed"]?.DeepClone(),
                    ["motivo"] = t["reason"]?.DeepClone(), ["pideComentario"] = t["requireComment"]?.DeepClone(),
                    ["camposObligatorios"] = t["requiredFields"]?.DeepClone(),
                }).ToArray()),
            };
        }
        return new("ok", data, id is null ? "permisos y proceso" : "permisos, estado y transiciones");
    }

    /// <summary>
    /// H3 CA2: catálogos con proceso que la persona ve; de cada uno, los estados desde donde puede ejecutar una transición (por
    /// rol, como la pantalla) y cuántos registros suyos hay en ellos (la lista ya trae el conteo por estado con su alcance); los
    /// ejemplos, con tope. Las condiciones de cada registro se revisan con <c>ver_registro</c>.
    /// </summary>
    internal async Task<ToolOutput> PendingAsync(CompanionContext c, CancellationToken ct)
    {
        var me = await ReadAsync(c, new RouteCall("GET", "/api/auth/me"), ct);
        if (!me.Ok) return StudioToolRunner.Output(me, null);
        var roles = (me.Body?["roles"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var superuser = me.Body?["isSuperuser"]?.GetValue<bool>() == true;
        var defs = await ReadAsync(c, new RouteCall("GET", "/api/model/entities"), ct);
        if (!defs.Ok) return StudioToolRunner.Output(defs, null);

        var result = new JsonArray();
        var samples = 0;
        foreach (var e in (defs.Body as JsonArray ?? []).OfType<JsonObject>())
        {
            if (result.Count >= CompanionTools.PendingEntities) break;
            if (e["workflow"] is not JsonObject wf || e["parentEntity"] is not null) continue;
            var name = e["name"]!.GetValue<string>();
            var access = me.Body?["entities"]?[name];
            if (access?["read"]?.GetValue<bool>() != true || access["update"]?.GetValue<bool>() != true) continue;
            var states = (wf["transitions"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(t => t["system"]?.GetValue<bool>() != true)
                .Where(t => superuser || t["roles"] is not JsonArray { Count: > 0 } rs || rs.Any(r => r is not null && roles.Contains(r.GetValue<string>())))
                .SelectMany(t => (t["from"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (states.Count == 0) continue;
            var counts = await ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/", CompanionTools.V(("entity", name)), CompanionTools.Q(("pageSize", "1"))), ct);
            if (!counts.Ok) continue;
            var pending = new JsonArray();
            foreach (var s in (counts.Body?["stateCounts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var state = s["state"]?.GetValue<string>();
                var count = s["count"]?.GetValue<int>() ?? 0;
                if (state is null || count == 0 || !states.Contains(state)) continue;
                var item = new JsonObject { ["estado"] = state, ["etiqueta"] = s["label"]?.DeepClone(), ["cuantos"] = count };
                if (samples < CompanionTools.PendingEntities)
                {
                    samples++;
                    var list = await ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/", CompanionTools.V(("entity", name)),
                        CompanionTools.Q(("pageSize", CompanionTools.PendingSamples.ToString()), ("state", state))), ct);
                    if (list.Ok) item["ejemplos"] = (CompanionTools.ShapeList(list.Body) as JsonObject)?["registros"]?.DeepClone();
                }
                pending.Add(item);
            }
            if (pending.Count > 0)
                result.Add(new JsonObject { ["catalogo"] = name, ["etiqueta"] = e["labelPlural"]?.DeepClone(), ["pendientes"] = pending });
        }
        return new("ok", new JsonObject { ["catalogos"] = result }, $"{result.Count} catálogos con pendientes");
    }
}
