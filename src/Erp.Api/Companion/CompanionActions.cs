using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Companion;

public static class CompanionActionStatus
{
    public const string Proposed = "proposed";
    /// <summary>Confirmada y despachándose: si el proceso cae aquí, el barrido la pasa a <see cref="Unknown"/>.</summary>
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Discarded = "discarded";
    public const string Expired = "expired";
    /// <summary>Fallas parciales: no sabemos si se aplicó. Nunca se repite sola (ADR-0010).</summary>
    public const string Unknown = "unknown";
}

public sealed record CompanionConfirmBody(string? Hash, IReadOnlyList<string>? AcceptWarnings = null);

/// <summary>Spec 048 (H1 CA2): «No» o «No, y dile qué cambiar» (el motivo vuelve al modelo como palabras de la persona).</summary>
public sealed record CompanionDiscardBody(string? Reason = null);

public sealed record CompanionActionDto(int Id, string Kind, string Entity, string EntityLabel, long? RecordId, JsonNode? Preview, string Hash,
    string Status, string? Error, JsonNode? Result, DateTime CreatedAt, DateTime? DecidedAt, string? Reason = null);

/// <summary>
/// Spec 046 (H4, ADR-0010): las acciones que el Acompañante propone y la persona confirma. Proponer no escribe nada: lee el
/// registro como la persona, arma la vista previa con etiquetas y guarda el cuerpo exacto con su huella. Confirmar lo ejecuta
/// una vez, por la ruta de datos de siempre y con la identidad, la empresa y los permisos de quien confirma en ese momento.
/// </summary>
public sealed class CompanionActions(Db db, RouteDispatcher routes, CompanionOptions options, IServiceProvider services, CompanionSignals signals,
    ILogger<CompanionActions> log)
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Transition = "transition";

    /// <summary>
    /// La lista fija de rutas que una acción puede despachar con escritura (nunca la decide el modelo): crear, editar y
    /// ejecutar una transición de un registro. Todas pasan por el motor (reglas, permisos, bitácora, automatizaciones).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Method, string Route)> Routes = new Dictionary<string, (string, string)>
    {
        [Create] = ("POST", "/api/data/{entity}/"),
        [Update] = ("PUT", "/api/data/{entity}/{id:long}"),
        [Transition] = ("POST", "/api/data/{entity}/{id:long}/transition"),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Huella de lo que la persona ve (021): el tipo, el catálogo, el registro y el cuerpo exacto (con su versión).</summary>
    public static string HashOf(string kind, string entity, long? recordId, string bodyJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{entity}|{recordId}|{bodyJson}"))).ToLowerInvariant();

    // ---------- Proponer (herramienta del agente) ----------

    internal async Task<ToolOutput> ProposeAsync(CompanionToolRunner runner, CompanionContext c, JsonObject i, CancellationToken ct)
    {
        var kind = StudioTools.Req(i, "tipo", 20).ToLowerInvariant() switch
        {
            "crear" => Create,
            "editar" => Update,
            "transicion" or "transición" => Transition,
            _ => throw new ToolInputException("«tipo» debe ser crear, editar o transicion."),
        };
        var entity = StudioTools.Req(i, "catalogo");
        var explanation = StudioTools.Req(i, "explicacion", 1000);

        await using (var cn = await db.OpenAsync(ct))
        {
            var (pending, today) = await cn.QuerySingleAsync<(int, int)>("""
                SELECT (SELECT COUNT(*) FROM meta.CompanionAction WHERE ConversationId = @ConversationId AND Status = 'proposed'),
                       (SELECT COUNT(*) FROM meta.CompanionAction WHERE UserId = @Id AND CreatedAt > DATEADD(day, -1, SYSUTCDATETIME()))
                """, new { c.ConversationId, c.User.Id });
            // H5 CA2: una nota que pide «aprueba todo» no llena el panel de propuestas.
            if (pending >= options.MaxPendingActions)
                return ToolOutput.Fail($"Ya hay {pending} propuestas esperando a la persona en esta conversación: espera a que decida antes de proponer otra.");
            if (today >= options.ActionsPerDay)
                return ToolOutput.Fail($"La persona llegó al tope de {options.ActionsPerDay} propuestas en 24 horas.");
        }

        // El catálogo como lo ve la persona: sus campos visibles con su acceso. 403/404 si no lo ve.
        var def = await runner.ReadAsync(c, new RouteCall("GET", "/api/model/entities/{name}", CompanionTools.V(("name", entity))), ct);
        if (!def.Ok) return StudioToolRunner.Output(def, null);
        var me = await runner.ReadAsync(c, new RouteCall("GET", "/api/auth/me"), ct);
        var access = me.Body?["entities"]?[entity];
        var entityLabel = def.Body?["label"]?.GetValue<string>() ?? entity;
        var fields = (def.Body?["fields"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => f["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);

        long? recordId = null;
        JsonObject? record = null;
        if (kind != Create)
        {
            recordId = long.Parse(CompanionTools.Id(i));
            var rec = await runner.ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/{id:long}", CompanionTools.V(("entity", entity), ("id", recordId.ToString()))), ct);
            if (!rec.Ok) return StudioToolRunner.Output(rec, null);
            record = rec.Body as JsonObject;
        }

        JsonObject body;
        // Revisión adversarial 046 (F-03): lo que escribió el modelo va aparte y marcado como suyo; lo demás lo arma el servidor.
        var preview = new JsonObject { ["tipo"] = kind, ["catalogo"] = entityLabel, ["registro"] = recordId, ["segunElAcompanante"] = explanation };
        switch (kind)
        {
            case Create:
                if (access?["create"]?.GetValue<bool>() != true) return ToolOutput.Fail("La persona no puede crear registros en este catálogo.", "denied");
                body = Values(i, fields, required: true);
                if (await ReferencesAsync(runner, c, body, fields, ct) is { } bad) return bad;
                preview["cambios"] = Changes(body, fields, null);
                break;
            case Update:
                if (record?["_access"]?["update"]?.GetValue<bool>() != true)
                    return ToolOutput.Fail(record?["_access"]?["reason"]?.GetValue<string>() ?? "La persona no puede modificar este registro.", "denied");
                body = Values(i, fields, required: true);
                if (await ReferencesAsync(runner, c, body, fields, ct) is { } bad2) return bad2;
                preview["cambios"] = Changes(body, fields, record);
                body["_rowVersion"] = record["_rowVersion"]?.DeepClone();
                break;
            default:
                var name = StudioTools.Req(i, "transicion", 100);
                var t = (record?["_workflow"]?["transitions"] as JsonArray ?? []).OfType<JsonObject>()
                    .FirstOrDefault(x => string.Equals(x["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                if (t is null) return ToolOutput.Fail($"Desde el estado actual no existe la transición «{ToolOutput.Trim(name)}». Revisa ver_registro.");
                if (t["allowed"]?.GetValue<bool>() != true)
                    return ToolOutput.Fail($"La persona no puede ejecutar «{t["label"]}» ahora: {t["reason"]?.GetValue<string>() ?? "no está permitido."}", "denied");
                var values = i["valores"] is null ? null : Values(i, fields, required: false);
                if (values is not null && await ReferencesAsync(runner, c, values, fields, ct) is { } bad3) return bad3;
                body = new JsonObject
                {
                    ["transition"] = t["name"]!.GetValue<string>(), ["comment"] = StudioTools.Opt(i, "comentario", 1000),
                    ["values"] = values, ["_rowVersion"] = record!["_rowVersion"]?.DeepClone(),
                };
                var states = (def.Body?["workflow"]?["states"] as JsonArray ?? []).OfType<JsonObject>();
                preview["transicion"] = t["label"]?.DeepClone();
                preview["desde"] = record["_workflow"]?["stateLabel"]?.DeepClone();
                preview["hacia"] = states.FirstOrDefault(s => string.Equals(s["name"]?.GetValue<string>(), t["to"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))?["label"]?.DeepClone()
                                   ?? t["to"]?.DeepClone();
                preview["comentario"] = body["comment"]?.DeepClone();
                if (values is not null) preview["cambios"] = Changes(values, fields, record);
                break;
        }

        var bodyJson = body.ToJsonString();
        var hash = HashOf(kind, entity, recordId, bodyJson);
        int? inserted;
        // Revisión adversarial 046 (F-06): solo si el turno sigue siendo de este trabajador (no tras «Detener» ni tras la purga).
        await using (var cn = await db.OpenAsync(ct))
            inserted = await cn.ExecuteScalarAsync<int?>("""
                INSERT meta.CompanionAction (ConversationId, UserId, CompanyId, Turn, Kind, EntityName, RecordId, BodyJson, PreviewJson, Hash, Status, ToolUseId)
                OUTPUT inserted.Id
                SELECT @ConversationId, @UserId, @CompanyId, @Turn, @kind, @entity, @recordId, @bodyJson, @preview, @hash, 'proposed', @ToolUseId
                 WHERE EXISTS (SELECT 1 FROM meta.CompanionConversation WITH (UPDLOCK) WHERE Id = @ConversationId AND LeaseToken = @Lease AND Status = 'running')
                """, new { c.ConversationId, UserId = c.User.Id, CompanyId = c.User.Company?.Id, c.Turn, kind, entity = def.Body?["name"]?.GetValue<string>() ?? entity,
                    recordId, bodyJson, preview = preview.ToJsonString(), hash, c.Lease, c.ToolUseId });
        var id = inserted ?? throw new StudioLeaseLost();
        var data = new JsonObject
        {
            ["accion"] = id, ["estado"] = "esperando a la persona", ["vistaPrevia"] = preview,
            ["nota"] = "No está hecho: la persona ve la vista previa en el panel y decide. Su respuesta te llegará como el resultado de esta llamada.",
        };
        return new("ok", data, $"propuesta {id}: {kind} en {entityLabel}");
    }

    /// <summary>
    /// Los valores que propone el modelo, solo sobre campos que la persona ve y puede capturar (no ocultos, de solo lectura,
    /// calculados ni partidas): uno que no cumple se rechaza con la lista de los que sí.
    /// </summary>
    private static JsonObject Values(JsonObject i, Dictionary<string, JsonObject> fields, bool required)
    {
        if (i["valores"] is not JsonObject values || values.Count == 0)
            return required ? throw new ToolInputException("Falta «valores» (por nombre interno de campo).") : [];
        var editable = fields.Values.Where(Editable).Select(f => f["name"]!.GetValue<string>()).ToList();
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (!fields.TryGetValue(key, out var f) || !Editable(f))
                throw new ToolInputException($"No se puede proponer «{ToolOutput.Trim(key)}»{(f is null ? "" : $": {WhyNotEditable(f)}")}. Campos que la persona puede capturar: {string.Join(", ", editable)}.");
            result[f["name"]!.GetValue<string>()] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// Spec 048 (H4 CA1): por qué un campo que la persona ve no se captura y, si lo llena el sistema, quién (de <c>filledBy</c>
    /// de la ruta del modelo: solo automatizaciones de catálogos que ella ve). Para que el modelo lo explique sin suponer.
    /// </summary>
    internal static string WhyNotEditable(JsonObject f)
    {
        var label = f["label"]?.GetValue<string>() ?? f["name"]?.GetValue<string>();
        var type = f["type"]?.GetValue<string>();
        if (f["formula"] is not null || type == FieldTypes.Formula) return $"«{label}» se calcula solo con su fórmula";
        if (type == FieldTypes.Autonumber) return $"«{label}» es un folio que asigna el sistema";
        if (type == FieldTypes.Lines) return $"«{label}» son partidas: no se proponen desde aquí";
        if (type == FieldTypes.State) return $"«{label}» es el estado: cambia con una transición del proceso";
        if (f["editable"]?.GetValue<bool>() == false)
            return CompanionTools.FilledByText(f) is { } who ? $"«{label}» lo llena el sistema, no la persona: {who}" : $"«{label}» lo llena el sistema (no se captura)";
        if (f["access"]?.GetValue<string>() == FieldAccessLevels.Read) return $"la persona solo puede ver «{label}», no capturarlo";
        return $"«{label}» no se captura";
    }

    /// <summary>
    /// Revisión adversarial 046 (F-03): una referencia propuesta se muestra con la etiqueta que la persona ve de ese registro (no
    /// con el número que escribió el modelo); si no lo ve o no existe, no hay propuesta. La etiqueta queda en la vista previa.
    /// </summary>
    private static async Task<ToolOutput?> ReferencesAsync(CompanionToolRunner runner, CompanionContext c, JsonObject values,
        Dictionary<string, JsonObject> fields, CancellationToken ct)
    {
        foreach (var (key, value) in values.ToList())
        {
            // Spec 050: varios registros, cada uno con la misma regla (la persona lo ve; si no, no hay propuesta).
            if (fields[key]["type"]?.GetValue<string>() == FieldTypes.MultiReference && value is not null)
            {
                var mtarget = fields[key]["referenceEntity"]?.GetValue<string>();
                if (value is not JsonArray items || items.Count > FieldTypes.MaxMultiValues || mtarget is null)
                    return ToolOutput.Fail($"«{key}» debe ser una lista de números de registros de {mtarget}.");
                var mdef = await runner.ReadAsync(c, new RouteCall("GET", "/api/model/entities/{name}", CompanionTools.V(("name", mtarget))), ct);
                if (!mdef.Ok) return ToolOutput.Fail($"La persona no tiene acceso a «{fields[key]["label"]}».", "denied");
                var mdisplay = mdef.Body?["displayField"]?.GetValue<string>();
                var shown = new List<string>();
                foreach (var item in items)
                {
                    if (item is null || item.GetValueKind() != JsonValueKind.Number || !item.AsValue().TryGetValue<long>(out var id) || id <= 0)
                        return ToolOutput.Fail($"«{key}» debe ser una lista de números de registros de {mtarget}.");
                    var mrec = await runner.ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/{id:long}", CompanionTools.V(("entity", mtarget), ("id", id.ToString()))), ct);
                    if (!mrec.Ok) return ToolOutput.Fail($"La persona no tiene acceso al registro {id} de «{fields[key]["label"]}» (o no existe).", "denied");
                    var mlabel = mdisplay is null ? null : mrec.Body?[mdisplay]?.ToString();
                    shown.Add($"{(string.IsNullOrWhiteSpace(mlabel) ? "registro" : mlabel)} (n.º {id})");
                }
                fields[key] = (JsonObject)fields[key].DeepClone();
                fields[key]["__shown"] = shown.Count == 0 ? "(ninguno)" : string.Join(", ", shown);
                continue;
            }
            if (fields[key]["type"]?.GetValue<string>() != FieldTypes.Reference || value is null) continue;
            var target = fields[key]["referenceEntity"]?.GetValue<string>();
            if (target is null || value.GetValueKind() != JsonValueKind.Number || !value.AsValue().TryGetValue<long>(out var rid) || rid <= 0)
                return ToolOutput.Fail($"«{key}» debe ser el número de un registro de {target}.");
            var def = await runner.ReadAsync(c, new RouteCall("GET", "/api/model/entities/{name}", CompanionTools.V(("name", target))), ct);
            var rec = def.Ok ? await runner.ReadAsync(c, new RouteCall("GET", "/api/data/{entity}/{id:long}", CompanionTools.V(("entity", target), ("id", rid.ToString()))), ct) : def;
            if (!rec.Ok) return ToolOutput.Fail($"La persona no tiene acceso al registro {rid} de «{fields[key]["label"]}» (o no existe).", "denied");
            var display = def.Body?["displayField"]?.GetValue<string>();
            var label = display is null ? null : rec.Body?[display]?.ToString();
            fields[key] = (JsonObject)fields[key].DeepClone();
            fields[key]["__shown"] = $"{(string.IsNullOrWhiteSpace(label) ? "registro" : label)} (n.º {rid})";
        }
        return null;
    }

    private static bool Editable(JsonObject f) =>
        f["access"]?.GetValue<string>() is not (FieldAccessLevels.Read or FieldAccessLevels.Hidden)
        && f["formula"] is null && f["editable"]?.GetValue<bool>() != false
        && f["type"]?.GetValue<string>() is not (FieldTypes.Lines or FieldTypes.Formula or FieldTypes.Autonumber or FieldTypes.Parent or FieldTypes.State);

    /// <summary>Antes → después por campo, con su etiqueta; lo de antes es como lo ve la persona (las referencias, por su etiqueta).</summary>
    private static JsonArray Changes(JsonObject values, Dictionary<string, JsonObject> fields, JsonObject? record) =>
        new(values.Select(p => (JsonNode)new JsonObject
        {
            ["campo"] = fields[p.Key]["label"]?.DeepClone(),
            ["antes"] = record is null ? null : (record[p.Key + "__label"] ?? record[p.Key])?.DeepClone(),
            ["despues"] = fields[p.Key]["__shown"]?.DeepClone() ?? p.Value?.DeepClone(),
        }).ToArray());

    // ---------- Confirmar y descartar (la persona) ----------

    private sealed record Row(int Id, int ConversationId, int UserId, int? CompanyId, string Kind, string EntityName, long? RecordId,
        string BodyJson, string? PreviewJson, string Hash, string Status, string? ResultJson, string? Error, DateTime CreatedAt, DateTime? DecidedAt,
        string? ToolUseId, string? Reason);

    private const string Columns = "Id, ConversationId, UserId, CompanyId, Kind, EntityName, RecordId, BodyJson, PreviewJson, Hash, Status, ResultJson, Error, CreatedAt, DecidedAt, ToolUseId, Reason";

    /// <summary>
    /// H4 CA2/CA3: dueña, pendiente, sin caducar, con la huella y en la empresa de la acción; <c>proposed → applying</c> bajo
    /// bloqueo y en su transacción (diez a la vez: una pasa). Después se despacha la ruta, sin cancelar si la persona cierra la
    /// pestaña, y se guarda el resultado. Un aviso por aceptar (428) la deja pendiente con sus mensajes.
    /// </summary>
    public async Task<(CompanionActionDto Action, JsonNode? Record)> ConfirmAsync(int id, CompanionConfirmBody? body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (user.IsService) throw new Forbidden();
        Enabled();
        Row row;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            // Revisión adversarial 046 (F-02): primero la conversación, como al borrarla (mismo orden de bloqueos): o el borrado ya
            // pasó y aquí no se encuentra, o espera a que esta acción quede «aplicándose» y entonces responde 409.
            var conversation = await cn.ExecuteScalarAsync<int?>("SELECT ConversationId FROM meta.CompanionAction WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx) ?? throw new NotFound("La propuesta no existe.");
            var conversationStatus = await cn.ExecuteScalarAsync<string?>("SELECT Status FROM meta.CompanionConversation WITH (UPDLOCK, HOLDLOCK) WHERE Id = @conversation",
                new { conversation }, tx);
            if (conversationStatus is null or CompanionService.Deleted) throw new NotFound("La propuesta no existe.");
            row = await cn.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM meta.CompanionAction WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                      new { id, UserId = user.Id }, tx) ?? throw new NotFound("La propuesta no existe.");
            if (row.Status != CompanionActionStatus.Proposed) throw new Conflict(Decided(row.Status));
            // Revisión adversarial 049 (F-03): la empresa antes que la caducidad, que también reanuda el turno.
            if (row.CompanyId != user.Company?.Id)
                throw new Conflict("La propuesta es de otra empresa: cámbiate a ella para confirmarla.");
            // Revisión adversarial 049 (F-04): solo se confirma lo de un turno que corre o espera a la persona; una propuesta que
            // quedó de un turno ya cerrado (de antes de la 049, D-02) se descarta sin aplicarse.
            if (conversationStatus is not (StudioStatus.Running or CompanionService.Waiting))
            {
                await CompanionService.DiscardPendingAsync(cn, tx, conversation, CompanionService.EndedError);
                await tx.CommitAsync(ct);
                throw new Conflict(Decided(CompanionActionStatus.Discarded));
            }
            if (row.CreatedAt < DateTime.UtcNow.AddHours(-options.ActionHours))
            {
                await cn.ExecuteAsync("UPDATE meta.CompanionAction SET Status = 'expired', DecidedAt = SYSUTCDATETIME(), PreviewJson = NULL WHERE Id = @id", new { id }, tx);
                var resumedExpired = await ResumeAsync(cn, tx, conversation, user.SecurityStamp);
                await tx.CommitAsync(ct);
                if (resumedExpired) signals.Pulse(conversation);
                throw new Conflict(Decided(CompanionActionStatus.Expired));
            }
            if (!string.Equals(body?.Hash, row.Hash, StringComparison.Ordinal))
                throw new Conflict("La propuesta no es la que revisaste: vuelve a abrirla antes de confirmar.");
            await cn.ExecuteAsync("UPDATE meta.CompanionAction SET Status = 'applying', DecidedAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
            await tx.CommitAsync(ct);
        }

        var (method, route) = Routes[row.Kind];
        var payload = JsonNode.Parse(row.BodyJson)!.AsObject();
        if (row.Kind == Transition && body?.AcceptWarnings is { Count: > 0 } accepted)
            payload["acceptWarnings"] = new JsonArray(accepted.Take(50).Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
        var values = new Dictionary<string, string?> { ["entity"] = row.EntityName };
        if (row.RecordId is { } rid) values["id"] = rid.ToString();
        RouteResult result;
        try
        {
            result = await routes.SendAsync(services, user, new RouteCall(method, route, values, Body: payload), allowWrites: true, CancellationToken.None,
                BearerCredential.Of(http.User), origin: Audit.CompanionOrigin);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "No se pudo ejecutar la acción {Id} del Acompañante", id);
            result = new RouteResult(500, null, "Ocurrió un error inesperado.");
        }

        var (status, error) = result.Ok ? (CompanionActionStatus.Applied, (string?)null)
            : result.Status is StatusCodes.Status428PreconditionRequired or StatusCodes.Status429TooManyRequests ? (CompanionActionStatus.Proposed, null)
            : (CompanionActionStatus.Failed, Detail(result));
        var resultJson = result.Ok ? new JsonObject { ["recordId"] = (result.Body?["id"] ?? JsonValue.Create(row.RecordId))?.DeepClone() }.ToJsonString() : null;
        var resumed = false;
        await using (var cn = await db.OpenAsync(CancellationToken.None))
        await using (var tx = await cn.BeginTransactionAsync(CancellationToken.None))
        {
            // Spec 048: el mismo orden de bloqueos que detener, borrar y descartar (conversación y luego acción).
            await cn.ExecuteAsync("SELECT Status FROM meta.CompanionConversation WITH (UPDLOCK, HOLDLOCK) WHERE Id = @ConversationId", new { row.ConversationId }, tx);
            var updated = await cn.ExecuteAsync("""
                UPDATE meta.CompanionAction
                   SET Status = @status, Error = @error, ResultJson = @resultJson,
                       DecidedAt = CASE WHEN @status = 'proposed' THEN NULL ELSE DecidedAt END,
                       -- La vista previa tenía valores del registro: al decidirse ya no hace falta (H2 CA4).
                       PreviewJson = CASE WHEN @status = 'proposed' THEN PreviewJson ELSE NULL END
                 -- Si el despacho tardó más que Companion:ApplyingSeconds, el barrido ya la marcó «sin confirmar»: el resultado real gana.
                 WHERE Id = @id AND Status IN ('applying', 'unknown')
                """, new { id, status, error = error is null ? null : StudioAgent.Cut(error, 1000), resultJson }, tx);
            // F-05: si otra cosa la cambió mientras se aplicaba, queda en el log (con el barrido corregido no debería pasar).
            if (updated == 0) log.LogWarning("La acción {Id} del Acompañante cambió de estado mientras se aplicaba ({Status})", id, status);
            if (status != CompanionActionStatus.Proposed)
                await Audit.WriteAsync(cn, tx, http, AuditActions.CompanionAction, entity: row.EntityName, recordId: (result.Body?["id"] ?? JsonValue.Create(row.RecordId))?.ToString(),
                    after: new { action = id, conversation = row.ConversationId, kind = row.Kind, status, error = error is null ? null : StudioAgent.Cut(error, 300) });
            // Spec 048 (H1 CA4, H2 CA3): el resultado y la reanudación del turno, en la misma transacción.
            if (updated > 0 && status != CompanionActionStatus.Proposed) resumed = await ResumeAsync(cn, tx, row.ConversationId, user.SecurityStamp);
            await tx.CommitAsync(CancellationToken.None);
        }
        if (resumed) signals.Pulse(row.ConversationId);
        if (result.Status == StatusCodes.Status428PreconditionRequired)
            throw new AppException(StatusCodes.Status428PreconditionRequired, result.Error ?? "La acción tiene avisos: revísalos y confirma para continuar.", result.Errors);
        var dto = await GetAsync(id, user.Id, CancellationToken.None) ?? throw new NotFound("La propuesta no existe.");
        if (!result.Ok) throw new AppException(result.Status is >= 400 and < 500 ? result.Status : 400, error ?? "La acción no se pudo aplicar.", result.Errors);
        return (dto, result.Body);
    }

    /// <summary>
    /// H4 CA2 y spec 048 (H1 CA2): «No» o «No, y dile qué cambiar». El motivo es de la persona: vuelve al modelo en un bloque
    /// <c>&lt;pedido&gt;</c>. Si era la última pendiente de una conversación que esperaba, el turno se vuelve a encolar.
    /// </summary>
    public async Task<CompanionActionDto> DiscardAsync(int id, CompanionDiscardBody? body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        Enabled();
        var reason = body?.Reason?.Trim() is { Length: > 0 } r ? r : null;
        if (reason is not null && reason.Length > options.MaxPromptChars)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["reason"] = [$"El motivo admite hasta {options.MaxPromptChars} caracteres."] },
                $"El motivo admite hasta {options.MaxPromptChars} caracteres.");
        int conversation;
        bool resumed;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            // Spec 048: conversación y luego acción, como confirmar (046 F-02) y detener.
            conversation = await cn.ExecuteScalarAsync<int?>("SELECT ConversationId FROM meta.CompanionAction WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx) ?? throw new NotFound("La propuesta no existe.");
            await cn.ExecuteAsync("SELECT Status FROM meta.CompanionConversation WITH (UPDLOCK, HOLDLOCK) WHERE Id = @conversation", new { conversation }, tx);
            var row = await cn.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM meta.CompanionAction WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                          new { id, UserId = user.Id }, tx) ?? throw new NotFound("La propuesta no existe.");
            if (row.Status != CompanionActionStatus.Proposed) throw new Conflict(Decided(row.Status));
            // Revisión adversarial 048 (F-05): como confirmar, desde su empresa (decidir reanuda el turno de esa conversación).
            if (row.CompanyId != user.Company?.Id) throw new Conflict("La propuesta es de otra empresa: cámbiate a ella para responderla.");
            await cn.ExecuteAsync("UPDATE meta.CompanionAction SET Status = 'discarded', Reason = @reason, DecidedAt = SYSUTCDATETIME(), PreviewJson = NULL WHERE Id = @id",
                new { id, reason }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.CompanionAction, entity: row.EntityName, recordId: row.RecordId?.ToString(),
                after: new { action = id, conversation = row.ConversationId, kind = row.Kind, status = CompanionActionStatus.Discarded, reasonLength = reason?.Length ?? 0 });
            resumed = await ResumeAsync(cn, tx, conversation, user.SecurityStamp);
            await tx.CommitAsync(ct);
        }
        if (resumed) signals.Pulse(conversation);
        return (await GetAsync(id, user.Id, ct))!;
    }

    /// <summary>
    /// Spec 048 (H1 CA4): la conversación que esperaba a la persona vuelve a la cola cuando ya no tiene propuestas pendientes ni
    /// aplicándose. Va en la transacción de la decisión (con la conversación bloqueada): se encola una sola vez.
    /// Spec 049 (auditoría 9, O-01): si la reanuda una decisión de la persona, con su sello de ahora (como enviar y responder):
    /// tras cambiar su contraseña y volver a entrar, el turno reanudado no se detiene por identidad. Sin persona (el cierre del
    /// turno), se queda el sello guardado.
    /// </summary>
    internal static async Task<bool> ResumeAsync(DbConnection cn, DbTransaction tx, int conversationId, Guid? stamp = null) =>
        await cn.ExecuteAsync("""
            UPDATE meta.CompanionConversation SET Status = 'queued', SecurityStamp = ISNULL(@stamp, SecurityStamp), LastActivityAt = SYSUTCDATETIME()
             WHERE Id = @conversationId AND Status = 'waiting'
               AND NOT EXISTS (SELECT 1 FROM meta.CompanionAction WHERE ConversationId = @conversationId AND Status IN ('proposed', 'applying'))
            """, new { conversationId, stamp }, tx) > 0;

    /// <summary>Spec 049 (auditoría 9, D-01): apagado, decidir no reanuda nada ni escribe: 503, como empezar o enviar.</summary>
    private void Enabled()
    {
        if (!options.Enabled) throw new AppException(StatusCodes.Status503ServiceUnavailable, CompanionService.Off);
    }

    /// <summary>
    /// Spec 048 (H1 CA4): lo que vuelve al modelo como resultado de su <c>proponer_accion</c>. Lo arma el servidor con palabras
    /// fijas dentro de los datos no confiables (el error de una regla lo escribió un modelador); solo el motivo de la persona va
    /// como <c>&lt;pedido&gt;</c>. Null mientras la acción sigue pendiente.
    /// </summary>
    internal static string? Outcome(int id, string status, string entity, long? recordId, string? resultJson, string? error, string? reason, int maxChars)
    {
        var data = new JsonObject { ["accion"] = id, ["catalogo"] = entity };
        switch (status)
        {
            case CompanionActionStatus.Applied:
                data["resultado"] = "aplicada";
                data["registro"] = (resultJson is null ? null : JsonNode.Parse(resultJson)?["recordId"]?.DeepClone()) ?? recordId;
                data["nota"] = "La persona dijo que sí y el sistema lo aplicó: ya está hecho. Las automatizaciones del catálogo ya corrieron; si necesitas ver el resultado, consulta el registro.";
                break;
            case CompanionActionStatus.Discarded when error is not null:
                data["resultado"] = "detenida";
                // Spec 049 (D-02): también cuando el turno terminó antes de preguntarle (un tope, su cuenta o una interrupción).
                data["nota"] = error == CompanionService.StoppedError
                    ? "La persona detuvo la conversación sin decidir: no se hizo nada."
                    : $"No se le preguntó a la persona: {error} No se hizo nada; si todavía hace falta, vuelve a proponerla.";
                break;
            case CompanionActionStatus.Discarded:
                data["resultado"] = "rechazada";
                data["nota"] = reason is null
                    ? "La persona dijo que no: no se hizo nada. No la vuelvas a proponer igual; pregúntale qué quiere."
                    : "La persona dijo que no: no se hizo nada. Justo después, en su pedido, te dice qué cambiar.";
                break;
            case CompanionActionStatus.Failed:
                data["resultado"] = "no se aplicó";
                data["error"] = error;
                data["nota"] = "La persona dijo que sí, pero el sistema la rechazó: no se hizo nada. Explícale el motivo.";
                break;
            case CompanionActionStatus.Expired:
                data["resultado"] = "caducó";
                data["nota"] = "La persona no la decidió a tiempo: no se hizo nada.";
                break;
            case CompanionActionStatus.Unknown:
                data["resultado"] = "sin confirmar si se aplicó";
                data["error"] = error;
                data["nota"] = "No se sabe si se aplicó (el servidor se detuvo mientras la ejecutaba). Consulta el registro antes de decir nada.";
                break;
            default:
                return null;
        }
        var text = StudioPrompt.Untrusted(CompanionTools.Propose, data, maxChars);
        return status == CompanionActionStatus.Discarded && error is null && reason is not null ? text + "\n" + CompanionPrompt.Request(reason, null) : text;
    }

    public async Task<CompanionActionDto?> GetAsync(int id, int userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM meta.CompanionAction WHERE Id = @id AND UserId = @userId", new { id, userId });
        return row is null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<CompanionActionDto>> ListAsync(int conversationId, int userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<Row>($"SELECT {Columns} FROM meta.CompanionAction WHERE ConversationId = @conversationId AND UserId = @userId ORDER BY Id",
            new { conversationId, userId })).Select(ToDto).ToList();
    }

    /// <summary>
    /// Barrido: las propuestas viejas caducan y las que se quedaron «aplicándose» (el proceso cayó entre despachar y guardar)
    /// pasan a «sin confirmar si se aplicó» sin volver a ejecutarse.
    /// </summary>
    public async Task<(int Expired, int Unknown)> SweepAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var expired = await cn.ExecuteAsync("""
            UPDATE meta.CompanionAction SET Status = 'expired', DecidedAt = SYSUTCDATETIME(), PreviewJson = NULL
             WHERE Status = 'proposed' AND CreatedAt < DATEADD(hour, -@hours, SYSUTCDATETIME())
            """, new { hours = options.ActionHours });
        var unknown = await cn.ExecuteAsync("""
            UPDATE meta.CompanionAction SET Status = 'unknown', PreviewJson = NULL,
                   Error = N'No sabemos si se aplicó: el servidor se detuvo mientras la ejecutaba. Revisa el registro antes de repetirla.'
             WHERE Status = 'applying' AND DecidedAt < DATEADD(second, -@seconds, SYSUTCDATETIME())
            """, new { seconds = options.ApplyingSeconds });
        // Spec 048 (H2 CA3): las conversaciones que esperaban y ya no tienen nada pendiente (caducó, quedó sin confirmar, o el
        // proceso cayó entre decidir y reanudar) siguen su turno con ese resultado. Spec 049 (D-01): no con el Acompañante apagado.
        if (!options.Enabled) return (expired, unknown);
        var resumed = (await cn.QueryAsync<int>("""
            UPDATE c SET Status = 'queued', LastActivityAt = SYSUTCDATETIME()
            OUTPUT inserted.Id
              FROM meta.CompanionConversation c WITH (UPDLOCK, ROWLOCK)
             WHERE c.Status = 'waiting'
               AND NOT EXISTS (SELECT 1 FROM meta.CompanionAction a WHERE a.ConversationId = c.Id AND a.Status IN ('proposed', 'applying'))
            """)).ToList();
        foreach (var id in resumed) signals.Pulse(id);
        return (expired, unknown);
    }

    private static CompanionActionDto ToDto(Row r)
    {
        var preview = r.PreviewJson is null ? null : JsonNode.Parse(r.PreviewJson);
        var label = preview?["catalogo"]?.GetValue<string>() ?? r.EntityName;
        return new(r.Id, r.Kind, r.EntityName, label, r.RecordId, preview, r.Hash, r.Status, r.Error,
            r.ResultJson is null ? null : JsonNode.Parse(r.ResultJson), DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
            r.DecidedAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null, r.Reason);
    }

    private static string Decided(string status) => status switch
    {
        CompanionActionStatus.Applied => "Esta propuesta ya se aplicó.",
        CompanionActionStatus.Applying => "Esta propuesta se está aplicando.",
        CompanionActionStatus.Discarded => "Esta propuesta se descartó.",
        CompanionActionStatus.Expired => "Esta propuesta caducó: pídele al Acompañante una nueva.",
        CompanionActionStatus.Failed => "Esta propuesta ya se intentó y no se aplicó.",
        _ => "Esta propuesta ya no está pendiente.",
    };

    private static string Detail(RouteResult r) =>
        r.Errors is { Count: > 0 } ? $"{r.Error} {string.Join(" ", r.Errors.SelectMany(e => e.Value))}" : r.Error ?? "La acción no se pudo aplicar.";
}
