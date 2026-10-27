using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Assistant;
using Erp.Api.Automations;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Studio;

/// <summary>Spec 033 (H1 CA4): alcances de una sesión (y, en la 035, de un token MCP).</summary>
public static class StudioScopes
{
    /// <summary>Leer el modelo, el explorador, el inventario y compilar: siempre.</summary>
    public const string Model = "modelo";
    /// <summary>Leer registros con el alcance de la persona: opcional, los registros viajan al proveedor.</summary>
    public const string Data = "datos";
    /// <summary>Probar en la copia aislada.</summary>
    public const string Sandbox = "aislado";

    public static readonly IReadOnlyList<string> Optional = [Data, Sandbox];
}

public enum ToolTarget { Real, Sandbox, Server }

/// <summary>Lo que necesita una herramienta para correr: la persona (recargada), sus alcances y los destinos.</summary>
public sealed class ToolContext
{
    public required UserContext User { get; init; }
    public required IReadOnlySet<string> Scopes { get; init; }
    /// <summary>Proveedor de la aplicación (entorno real).</summary>
    public required IServiceProvider Services { get; init; }
    /// <summary>La copia aislada de la sesión; se crea al primer uso.</summary>
    public required Func<CancellationToken, Task<Sandbox>> SandboxAsync { get; init; }
}

/// <param name="Status"><c>ok</c>, <c>error</c> o <c>denied</c> (la API o los alcances lo negaron).</param>
public sealed record ToolOutput(string Status, JsonNode? Data, string Summary)
{
    public bool IsError => Status != "ok";
    public static ToolOutput Fail(string message, string status = "error") => new(status, new JsonObject { ["error"] = message }, Trim(message));
    internal static string Trim(string s) => s.Length <= 300 ? s : s[..297] + "…";
}

/// <summary>Entrada inválida de una herramienta (la manda el modelo: no confiable).</summary>
public sealed class ToolInputException(string message) : Exception(message);

/// <param name="Route">Ruta del inventario que usa (las del destino real y aislado); la capacidad es la de esa ruta.</param>
public sealed record StudioTool(string Name, string Description, string Scope, ToolTarget Target, JsonObject InputSchema,
    string? Method, string? Route, bool Costly, Func<StudioToolRunner, ToolContext, JsonObject, CancellationToken, Task<ToolOutput>>? Run);

/// <summary>Lo que se publica del catálogo (<c>GET /api/studio/tools</c>; el servidor MCP de la 035 dará lo mismo).</summary>
public sealed record StudioToolInfo(string Name, string Description, string Scope, string Target, string? Method, string? Route,
    string Capability, bool Costly, JsonObject InputSchema);

/// <summary>
/// Spec 033 (H1): catálogo de herramientas del agente. Cada una es una ruta de la API (o varias, en la copia aislada): la
/// capacidad no se declara aquí, es la de la ruta (029). Las del entorno real solo usan rutas que no escriben
/// (<c>StudioCoreTests</c>), y ninguna toca usuarios, roles, claves, paquetes ni aplicar.
/// </summary>
public static class StudioTools
{
    public const string Deliver = "entregar";
    public const int RecordsPage = 20;
    /// <summary>Como el asistente de la 011 (<c>ProposalParser</c>).</summary>
    public const int MaxOperations = 100;

    public static readonly IReadOnlyList<StudioTool> All =
    [
        Real("listar_catalogos", "Lista los catálogos del modelo (nombre, etiqueta, módulo, si es partida y cuántos campos tiene).",
            Schema(), "GET", "/api/model/entities", (r, c, i) => new(Query: Q(("includeChildren", "true"))), ShapeEntities),
        Real("ver_catalogo", "Definición completa de un catálogo: campos, proceso, reglas, automatizaciones, seguridad, layout, vistas, reportes, quién lo referencia y permisos por rol.",
            Schema(("entity", "string", "Nombre del catálogo", true)), "GET", "/api/explorer/entities/{name}", (r, c, i) => new(RouteValues: V(("name", Req(i, "entity"))))),
        Real("relaciones", "Grafo de referencias y partidas entre catálogos; filtra por módulo o texto.",
            Schema(("app", "string", "Módulo de navegación", false), ("q", "string", "Texto a buscar", false)), "GET", "/api/explorer/relations",
            (r, c, i) => new(Query: Q(("app", Opt(i, "app")), ("q", Opt(i, "q"))))),
        Real("base_fisica", "Tablas y columnas físicas del esquema app (tipos, nulos, índices, llaves).",
            Schema(("app", "string", "Módulo de navegación", false), ("q", "string", "Texto a buscar", false)), "GET", "/api/explorer/database",
            (r, c, i) => new(Query: Q(("schema", "app"), ("app", Opt(i, "app")), ("q", Opt(i, "q"))))),
        Real("donde_se_usa", "Dónde se usa un catálogo o un campo: fórmulas, reglas, procesos, automatizaciones, vistas, reportes, tableros.",
            Schema(("entity", "string", "Catálogo", true), ("field", "string", "Campo (opcional)", false)), "GET", "/api/explorer/usages",
            (r, c, i) => new(Query: Q(("entity", Req(i, "entity")), ("field", Opt(i, "field"))))),
        Real("automatizaciones", "Flujo de automatizaciones entre catálogos (qué evento dispara qué acción en qué catálogo).",
            Schema(), "GET", "/api/explorer/automations", (r, c, i) => new()),
        Real("seguridad", "Matriz de seguridad: roles, permisos por catálogo, herencia, seguridad por campo y por registro.",
            Schema(), "GET", "/api/explorer/security", (r, c, i) => new()),
        Real("inventario_rutas", "Rutas de la API con la capacidad que exige cada una, su efecto, su compuerta y si es costosa.",
            Schema(("q", "string", "Texto a buscar en la ruta", false)), "GET", "/api/explorer/routes", (r, c, i) => new(Query: Q(("q", Opt(i, "q"))))),
        Real("riesgos", "Registro de riesgos conocidos del sistema (id, riesgo, severidad, decisión y estado).",
            Schema(("q", "string", "Texto a buscar", false)), "GET", "/api/explorer/risks", (r, c, i) => new(Query: Q(("q", Opt(i, "q"))))),
        Real("funciones_expresion", "Funciones del lenguaje de expresiones con su firma y descripción.",
            Schema(), "GET", "/api/model/expressions/functions", (r, c, i) => new()),
        Real("validar_expresion", "Valida una expresión (fórmula, regla, condición) en el ámbito de un catálogo, con campos de borrador opcionales.",
            Schema(("entity", "string", "Catálogo", true), ("expression", "string", "Expresión", true), ("fields", "array", "Campos nuevos aún sin aplicar (Field[])", false)),
            "POST", "/api/model/expressions/check", (r, c, i) => new(Body: new JsonObject
            {
                ["entity"] = Req(i, "entity"), ["expression"] = Req(i, "expression", 2000), ["fields"] = i["fields"]?.DeepClone(),
            })),
        new("compilar_plan", "Compila un Plan de Cambios contra el modelo real sin aplicarlo: errores por operación, pasos y avisos de seguridad.",
            "modelo", ToolTarget.Real, Schema(("plan", "object", "Plan de Cambios { description, operations }", true)), "POST", "/api/model/plan/preview", false,
            (r, c, i, ct) => r.CompileAsync(c, PlanOf(i), ct)),
        Real("leer_registros", $"Registros de un catálogo, como los ve la persona ({RecordsPage} por página).",
            Schema(("entity", "string", "Catálogo", true), ("search", "string", "Texto a buscar", false), ("page", "integer", "Página (1…)", false)),
            "GET", "/api/data/{entity}/", (r, c, i) => new(RouteValues: V(("entity", Req(i, "entity"))),
                Query: Q(("pageSize", RecordsPage.ToString()), ("page", Page(i)), ("search", Opt(i, "search")))), scope: StudioScopes.Data),
        Real("leer_registro", "Un registro de un catálogo, como lo ve la persona.",
            Schema(("entity", "string", "Catálogo", true), ("id", "integer", "Id del registro", true)), "GET", "/api/data/{entity}/{id:long}",
            (r, c, i) => new(RouteValues: V(("entity", Req(i, "entity")), ("id", Id(i)))), scope: StudioScopes.Data),

        new("aislado_aplicar_plan", "Aplica un Plan de Cambios en la copia aislada (base de prueba con el modelo del entorno y sin datos reales). La primera herramienta aislada prepara la copia.",
            StudioScopes.Sandbox, ToolTarget.Sandbox, Schema(("plan", "object", "Plan de Cambios", true)), "POST", "/api/model/plan/apply", true,
            (r, c, i, ct) => r.SandboxApplyAsync(c, PlanOf(i), ct)),
        Sbx("aislado_crear", "Crea un registro de ejemplo en la copia aislada; devuelve el registro o los errores de validación.",
            Schema(("entity", "string", "Catálogo", true), ("values", "object", "Valores por campo", true)), "POST", "/api/data/{entity}/",
            (r, c, i) => new(RouteValues: V(("entity", Req(i, "entity"))), Body: Obj(i, "values"))),
        new("aislado_actualizar", "Cambia campos de un registro de la copia aislada.", StudioScopes.Sandbox, ToolTarget.Sandbox,
            Schema(("entity", "string", "Catálogo", true), ("id", "integer", "Id", true), ("values", "object", "Valores por campo", true)),
            "PUT", "/api/data/{entity}/{id:long}", false, (r, c, i, ct) => r.SandboxUpdateAsync(c, Req(i, "entity"), Id(i), Obj(i, "values"), ct)),
        new("aislado_transicion", "Ejecuta una transición del proceso sobre un registro de la copia aislada (con sus reglas, avisos y automatizaciones).",
            StudioScopes.Sandbox, ToolTarget.Sandbox,
            Schema(("entity", "string", "Catálogo", true), ("id", "integer", "Id", true), ("transition", "string", "Nombre de la transición", true),
                ("values", "object", "Valores que pide la transición", false), ("comment", "string", "Comentario", false),
                ("acceptWarnings", "array", "Nombres de avisos que se confirman", false)),
            "POST", "/api/data/{entity}/{id:long}/transition", false, (r, c, i, ct) => r.SandboxTransitionAsync(c, i, ct)),
        Sbx("aislado_leer", "Lee un registro de la copia aislada (campos calculados, estado y avisos).",
            Schema(("entity", "string", "Catálogo", true), ("id", "integer", "Id", true)), "GET", "/api/data/{entity}/{id:long}",
            (r, c, i) => new(RouteValues: V(("entity", Req(i, "entity")), ("id", Id(i))))),
        Sbx("aislado_listar", "Lista registros de la copia aislada.",
            Schema(("entity", "string", "Catálogo", true), ("search", "string", "Texto a buscar", false)), "GET", "/api/data/{entity}/",
            (r, c, i) => new(RouteValues: V(("entity", Req(i, "entity"))), Query: Q(("pageSize", RecordsPage.ToString()), ("search", Opt(i, "search"))))),
        new("aislado_procesar_asincronas", "Procesa las automatizaciones asíncronas pendientes de la copia aislada y devuelve cómo terminó cada una.",
            StudioScopes.Sandbox, ToolTarget.Sandbox, Schema(), null, null, false, (r, c, i, ct) => r.SandboxDrainAsync(c, ct)),

        new(Deliver, "Termina el trabajo y entrega la propuesta para que la persona la revise y la aplique: el Plan de Cambios (o nada si no hay cambios), un resumen, lo que probaste con su resultado, lo que falló y los riesgos que ves.",
            StudioScopes.Model, ToolTarget.Server, Schema(("summary", "string", "Resumen en español, texto plano", true), ("plan", "object", "Plan de Cambios final (omítelo si no propones cambios)", false),
                ("tested", "array", "Pruebas: [{ description, ok, detail? }]", false), ("failures", "array", "Lo que falló o quedó sin resolver (texto)", false),
                ("risks", "array", "Riesgos que la persona debe revisar (texto)", false)),
            null, null, false, null),
    ];

    public static StudioTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    // ---------- Definición ----------

    private static StudioTool Real(string name, string description, JsonObject schema, string method, string route,
        Func<StudioToolRunner, ToolContext, JsonObject, RouteCall> call, Func<JsonNode?, JsonNode?>? shape = null, string scope = StudioScopes.Model) =>
        new(name, description, scope, ToolTarget.Real, schema, method, route, false,
            (r, c, i, ct) => r.RealAsync(c, call(r, c, i) with { Method = method, Route = route }, shape, ct));

    private static StudioTool Sbx(string name, string description, JsonObject schema, string method, string route,
        Func<StudioToolRunner, ToolContext, JsonObject, RouteCall> call) =>
        new(name, description, StudioScopes.Sandbox, ToolTarget.Sandbox, schema, method, route, false,
            (r, c, i, ct) => r.SandboxAsync(c, call(r, c, i) with { Method = method, Route = route }, ct));

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
            properties[p.Name] = p.Type == "array"
                ? new JsonObject { ["type"] = "array", ["description"] = p.Description }
                : new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
        };
    }

    // ---------- Entrada (no confiable) ----------

    internal static string Req(JsonObject i, string name, int max = 200) =>
        Opt(i, name, max) ?? throw new ToolInputException($"Falta «{name}».");

    internal static string? Opt(JsonObject i, string name, int max = 200)
    {
        if (i[name] is not JsonValue v) return null;
        var s = v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToString(),
            _ => throw new ToolInputException($"«{name}» debe ser texto."),
        };
        if (s.Length > max) throw new ToolInputException($"«{name}» admite hasta {max} caracteres.");
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    internal static string Id(JsonObject i) =>
        long.TryParse(Req(i, "id", 20), out var id) && id > 0 ? id.ToString() : throw new ToolInputException("«id» debe ser un número entero positivo.");

    private static string? Page(JsonObject i) =>
        Opt(i, "page", 6) is { } p ? (int.TryParse(p, out var n) && n is > 0 and <= 10_000 ? n.ToString() : throw new ToolInputException("«page» no es válida.")) : null;

    internal static JsonObject Obj(JsonObject i, string name) =>
        i[name] as JsonObject is { } o ? (JsonObject)o.DeepClone() : throw new ToolInputException($"«{name}» debe ser un objeto.");

    internal static JsonObject PlanOf(JsonObject i)
    {
        var plan = Obj(i, "plan");
        if (plan["operations"] is not JsonArray) throw new ToolInputException("El plan debe tener «operations».");
        return plan;
    }

    private static Dictionary<string, string?> Q(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
    private static Dictionary<string, string?> V(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static JsonNode? ShapeEntities(JsonNode? body) => body is JsonArray a
        ? new JsonArray(a.Where(e => e is JsonObject).Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e!["name"]?.DeepClone(), ["label"] = e["label"]?.DeepClone(), ["group"] = e["group"]?.DeepClone(),
            ["parentEntity"] = e["parentEntity"]?.DeepClone(), ["perCompany"] = e["perCompany"]?.DeepClone(),
            ["fields"] = (e["fields"] as JsonArray)?.Count ?? 0,
        }).ToArray())
        : body;
}

/// <summary>
/// Spec 033: ejecuta herramientas. Comprueba el alcance de la sesión, valida la entrada y convierte cualquier falla en un
/// mensaje seguro (los de la API; una excepción inesperada queda en el log y llega como mensaje genérico, H1 CA5).
/// </summary>
public sealed class StudioToolRunner(RouteDispatcher routes, SandboxFactory sandboxes, MetadataStore store, JsonSerializerOptions json,
    ILogger<StudioToolRunner> log)
{
    public static IReadOnlyList<StudioToolInfo> Describe(RouteDispatcher routes) => StudioTools.All.Select(t =>
    {
        var capability = t.Target == ToolTarget.Real && t.Route is not null && routes.Find(t.Method!, t.Route) is { } e
            ? e.Metadata.GetMetadata<CapabilityMetadata>()?.ToString() ?? "—"
            : CapabilityMetadata.Name(Capability.Modeler);
        return new StudioToolInfo(t.Name, t.Description, t.Scope, t.Target switch { ToolTarget.Real => "real", ToolTarget.Sandbox => "aislado", _ => "servidor" },
            t.Method, t.Route, capability, t.Costly, (JsonObject)t.InputSchema.DeepClone());
    }).ToList();

    public async Task<ToolOutput> RunAsync(ToolContext ctx, string name, JsonElement input, CancellationToken ct)
    {
        var tool = StudioTools.Find(name);
        if (tool?.Run is null) return ToolOutput.Fail($"La herramienta «{ToolOutput.Trim(name)}» no existe.");
        if (!ctx.Scopes.Contains(tool.Scope))
            return ToolOutput.Fail($"La herramienta «{name}» necesita el alcance «{tool.Scope}», que esta sesión no tiene.", "denied");
        if (input.ValueKind != JsonValueKind.Object) return ToolOutput.Fail("La entrada debe ser un objeto JSON.");
        try
        {
            return await tool.Run(this, ctx, (JsonObject)JsonNode.Parse(input.GetRawText())!, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ToolInputException ex) { return ToolOutput.Fail(ex.Message); }
        catch (SandboxUnavailable ex) { return ToolOutput.Fail(ex.Message); }
        catch (AppException ex) { return ToolOutput.Fail(ex.Message, ex.Status == 403 ? "denied" : "error"); }
        catch (Exception ex)
        {
            log.LogError(ex, "Error no controlado en la herramienta {Tool}", name);
            return ToolOutput.Fail("Ocurrió un error inesperado.");
        }
    }

    internal async Task<ToolOutput> RealAsync(ToolContext c, RouteCall call, Func<JsonNode?, JsonNode?>? shape, CancellationToken ct) =>
        Output(await routes.SendAsync(c.Services, c.User, call, allowWrites: false, ct), shape);

    internal async Task<ToolOutput> SandboxAsync(ToolContext c, RouteCall call, CancellationToken ct)
    {
        var sb = await c.SandboxAsync(ct);
        return Output(await routes.SendAsync(sb.Services, sb.Admin, call, allowWrites: true, ct), null);
    }

    internal static ToolOutput Output(RouteResult r, Func<JsonNode?, JsonNode?>? shape)
    {
        if (r.Ok) return new("ok", shape is null ? r.Body : shape(r.Body), $"{r.Status}");
        var data = new JsonObject { ["status"] = r.Status, ["error"] = r.Error };
        if (r.Errors is { Count: > 0 }) data["errors"] = JsonSerializer.SerializeToNode(r.Errors);
        var detail = r.Errors is { Count: > 0 } ? r.Error + " " + string.Join(" ", r.Errors.SelectMany(e => e.Value)) : r.Error;
        return new(r.Status == 403 ? "denied" : "error", data, ToolOutput.Trim($"{r.Status}: {detail}"));
    }

    /// <summary>Vista previa en el modelo real más los avisos de seguridad de la 011 (<see cref="PlanWarnings"/>).</summary>
    internal async Task<ToolOutput> CompileAsync(ToolContext c, JsonObject plan, CancellationToken ct)
    {
        var r = await routes.SendAsync(c.Services, c.User, new RouteCall("POST", "/api/model/plan/preview", Body: plan), allowWrites: false, ct);
        if (!r.Ok) return Output(r, null);
        var warnings = new JsonArray();
        try
        {
            if (plan.Deserialize<ChangePlan>(json) is { } typed)
                foreach (var w in PlanWarnings.For(typed, await store.GetAsync(ct))) warnings.Add(w);
        }
        catch (JsonException) { /* el compilador ya informó el error del plan */ }
        var valid = r.Body?["valid"]?.GetValue<bool>() == true;
        var data = new JsonObject
        {
            ["valid"] = valid,
            ["errors"] = r.Body?["errors"]?.DeepClone(),
            ["steps"] = new JsonArray((r.Body?["steps"] as JsonArray ?? []).Select(s => (JsonNode)new JsonObject
            {
                ["operation"] = s?["operation"]?.DeepClone(), ["description"] = s?["description"]?.DeepClone(),
            }).ToArray()),
            ["warnings"] = warnings,
        };
        return new("ok", data, valid ? $"válido · {warnings.Count} avisos" : $"{(r.Body?["errors"] as JsonArray)?.Count ?? 0} errores");
    }

    internal async Task<ToolOutput> SandboxApplyAsync(ToolContext c, JsonObject plan, CancellationToken ct)
    {
        var sb = await c.SandboxAsync(ct);
        var error = await sandboxes.ApplyAsync(sb.Services, sb.Admin, plan, ct);
        return error is null
            ? new("ok", new JsonObject { ["applied"] = true, ["operations"] = (plan["operations"] as JsonArray)?.Count ?? 0 }, "aplicado en la copia")
            : ToolOutput.Fail(error);
    }

    internal async Task<ToolOutput> SandboxUpdateAsync(ToolContext c, string entity, string id, JsonObject values, CancellationToken ct)
    {
        var sb = await c.SandboxAsync(ct);
        var route = V(entity, id);
        var current = await routes.SendAsync(sb.Services, sb.Admin, new RouteCall("GET", "/api/data/{entity}/{id:long}", route), true, ct);
        if (!current.Ok) return Output(current, null);
        values["_rowVersion"] = current.Body?["_rowVersion"]?.DeepClone();
        return Output(await routes.SendAsync(sb.Services, sb.Admin, new RouteCall("PUT", "/api/data/{entity}/{id:long}", route, Body: values), true, ct), null);
    }

    internal async Task<ToolOutput> SandboxTransitionAsync(ToolContext c, JsonObject i, CancellationToken ct)
    {
        var sb = await c.SandboxAsync(ct);
        var route = V(StudioTools.Req(i, "entity"), StudioTools.Id(i));
        var current = await routes.SendAsync(sb.Services, sb.Admin, new RouteCall("GET", "/api/data/{entity}/{id:long}", route), true, ct);
        if (!current.Ok) return Output(current, null);
        var body = new JsonObject
        {
            ["transition"] = StudioTools.Req(i, "transition"), ["comment"] = StudioTools.Opt(i, "comment", 2000),
            ["values"] = i["values"] as JsonObject is { } v ? v.DeepClone() : null,
            ["_rowVersion"] = current.Body?["_rowVersion"]?.DeepClone(),
            ["acceptWarnings"] = i["acceptWarnings"] as JsonArray is { } a ? a.DeepClone() : null,
        };
        return Output(await routes.SendAsync(sb.Services, sb.Admin, new RouteCall("POST", "/api/data/{entity}/{id:long}/transition", route, Body: body), true, ct), null);
    }

    internal async Task<ToolOutput> SandboxDrainAsync(ToolContext c, CancellationToken ct)
    {
        var sb = await c.SandboxAsync(ct);
        var processed = 0;
        await using (var scope = sb.Services.CreateAsyncScope())
        {
            var automations = scope.ServiceProvider.GetRequiredService<AutomationService>();
            for (var round = 0; round < 10; round++)
            {
                var n = await automations.DispatchBatchAsync(ct);
                processed += n;
                if (n == 0) break;
            }
        }
        await using var cn = await sb.Services.GetRequiredService<Db>().OpenAsync(ct);
        var runs = (await cn.QueryAsync<(long Id, string Kind, string EntityName, string Name, long? RecordId, string Status, string? LastError)>(
            "SELECT TOP 50 Id, Kind, EntityName, Name, RecordId, Status, LastError FROM meta.AutomationRun ORDER BY Id DESC")).ToList();
        var data = new JsonObject
        {
            ["processed"] = processed,
            ["runs"] = new JsonArray(runs.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["kind"] = x.Kind, ["entity"] = x.EntityName, ["automation"] = x.Name, ["recordId"] = x.RecordId,
                ["status"] = x.Status, ["error"] = x.LastError,
            }).ToArray()),
        };
        return new("ok", data, $"{processed} procesadas");
    }

    private static Dictionary<string, string?> V(string entity, string id) => new() { ["entity"] = entity, ["id"] = id };
}
