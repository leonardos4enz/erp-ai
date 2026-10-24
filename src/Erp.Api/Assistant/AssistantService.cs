using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Assistant;

public static class ProposalStatus
{
    public const string Proposed = "proposed";
    public const string Invalid = "invalid";
    public const string Answer = "answer";
    public const string Failed = "failed";
    public const string Applied = "applied";
    public const string Discarded = "discarded";
    public const string Superseded = "superseded";
    /// <summary>Spec 012: lugar reservado mientras se espera al proveedor (cuenta para el límite por hora).</summary>
    public const string Pending = "pending";
}

public sealed record AssistantRequestBody(string? Prompt, int? ConversationId);

/// <summary>Spec 012: huella de la vista previa que la persona revisó (<see cref="Proposal.ReviewHash"/>).</summary>
public sealed record AssistantApplyBody(string? ReviewHash);

/// <param name="KeySource"><c>server</c> (user-secrets o variable de entorno), <c>app</c> (guardada por un administrador) o nulo.</param>
/// <param name="CanConfigure">Quien consulta puede poner o quitar la clave desde la app (superusuario, sin clave del servidor).</param>
public sealed record AssistantStatus(bool Configured, string? Model, int MaxPromptChars, int RequestsPerHour, int Remaining,
    string? KeySource, DateTime? KeyUpdatedAt, string? KeyUpdatedBy, string? WorkspaceId, bool CanConfigure, bool DirectChanges = true);

public sealed record AssistantKeyBody(string? ApiKey, string? WorkspaceId = null);

public sealed record ConversationSummary(int Id, string Title, int Turns, DateTime UpdatedAt, string LastStatus);

public sealed record Conversation(int Id, IReadOnlyList<Proposal> Turns);

public sealed record Proposal
{
    public required int Id { get; init; }
    public required int ConversationId { get; init; }
    public required string Prompt { get; init; }
    public required string Status { get; init; }
    public string? Explanation { get; init; }
    public ChangePlan? Plan { get; init; }
    public PlanPreview? Preview { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Errores que no son de una operación del plan (p. ej. la respuesta no era un Plan de Cambios).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    public int Attempts { get; init; }
    public required string Model { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? AppliedAt { get; init; }
    public int? ChangeSetId { get; init; }
    public string? Error { get; init; }
    public bool CanApply { get; init; }
    /// <summary>Spec 012: huella de lo revisado (plan, pasos y advertencias); aplicar la exige.</summary>
    public string? ReviewHash { get; init; }
    /// <summary>Spec 012: por qué no se puede aplicar aunque sea válida (entorno con aprobación).</summary>
    public string? BlockedReason { get; init; }
}

/// <summary>
/// Spec 011: el asistente propone Planes de Cambios; nunca aplica nada por su cuenta. Aplicar es una acción de la persona,
/// con sus permisos, sobre el plan guardado en el servidor y por el mismo <see cref="PlanService"/> del modelador.
/// </summary>
public sealed class AssistantService(Db db, MetadataStore store, PlanService plans, AssistantOptions options, IAssistantModel llm,
    AssistantKeys keys, JsonSerializerOptions json, ILogger<AssistantService> log, Environments.EnvironmentOptions environment)
{
    public async Task<AssistantStatus> StatusAsync(UserContext user, CancellationToken ct)
    {
        Require(user);
        await using var cn = await db.OpenAsync(ct);
        var used = await UsedLastHourAsync(cn, user.Id);
        var key = await keys.InfoAsync(ct);
        var configured = key.Source is not null;
        return new(configured, configured ? options.Model : null, options.MaxPromptChars, options.RequestsPerHour,
            Math.Max(0, options.RequestsPerHour - used), key.Source, key.UpdatedAt, key.UpdatedBy, key.WorkspaceId,
            user.IsSuperuser && key.Source != AssistantKeyInfo.Server, environment.DirectChanges);
    }

    /// <summary>
    /// Guarda la clave desde la app (superusuario). Antes se prueba con el proveedor: una clave rechazada no se guarda.
    /// La clave no vuelve al navegador ni a la auditoría.
    /// </summary>
    public async Task<AssistantStatus> SetKeyAsync(AssistantKeyBody body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireKeyAdmin(user);
        var key = body.ApiKey?.Trim();
        if (!AssistantKeys.IsWellFormed(key))
            throw Invalid("apiKey", $"Pega la clave completa de Anthropic (entre {AssistantKeys.MinLength} y {AssistantKeys.MaxLength} caracteres, sin espacios).");
        var workspace = string.IsNullOrWhiteSpace(body.WorkspaceId) ? null : body.WorkspaceId.Trim();
        if (workspace is not null && !AssistantKeys.IsWorkspaceId(workspace))
            throw Invalid("workspaceId", "El ID del espacio de trabajo solo lleva letras, números, guiones y guiones bajos (p. ej. wrkspc_01AbC…).");
        var credential = new AssistantCredential(key!, workspace);
        try { await llm.VerifyAsync(credential, ct); }
        catch (AssistantProviderException ex) when (ex.Code == "auth")
        {
            throw Invalid("apiKey", "Anthropic rechazó la clave. Revisa que esté completa y activa en console.anthropic.com.");
        }
        catch (AssistantProviderException ex) when (ex.Code == "workspace")
        {
            throw Invalid("workspaceId", workspace is null
                ? "Esta clave es de la organización y no está ligada a un espacio de trabajo: escribe el ID del espacio de trabajo (console.anthropic.com › Settings › Workspaces) o crea la clave dentro de un espacio de trabajo."
                : "Anthropic no aceptó ese espacio de trabajo para esta clave. Revisa el ID en console.anthropic.com › Settings › Workspaces.");
        }
        catch (AssistantProviderException ex) when (ex.Code == "model")
        {
            throw Invalid("apiKey", $"La clave funciona, pero no tiene acceso al modelo {options.Model}.");
        }
        catch (AssistantProviderException ex) when (ex.Code == "bad_request")
        {
            throw Invalid("apiKey", "Anthropic rechazó la verificación de la clave. Revisa la clave y el espacio de trabajo.");
        }
        catch (AssistantProviderException)
        {
            throw new AppException(StatusCodes.Status502BadGateway, "No se pudo comprobar la clave con Anthropic. Intenta de nuevo en unos minutos.");
        }
        await SaveKeyAsync(http, credential, "set", ct);
        return await StatusAsync(user, ct);
    }

    public async Task<AssistantStatus> ClearKeyAsync(HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireKeyAdmin(user);
        await SaveKeyAsync(http, null, "clear", ct);
        return await StatusAsync(user, ct);
    }

    private async Task SaveKeyAsync(HttpContext http, AssistantCredential? credential, string action, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await keys.SaveAsync(cn, tx, credential, http.CurrentUser().Id);
        await Audit.WriteAsync(cn, tx, http, AuditActions.AssistantKey, entity: "assistant", after: new { action, workspaceId = credential?.WorkspaceId });
        await tx.CommitAsync(ct);
        keys.Invalidate();
    }

    private void RequireKeyAdmin(UserContext user)
    {
        if (!user.IsSuperuser || user.IsService) throw new Forbidden("Solo un administrador configura la clave del asistente.");
        if (options.Configured)
            throw new Conflict("La clave está definida en la configuración del servidor (user-secrets o variable de entorno) y no se cambia desde la app.");
    }

    public async Task<IReadOnlyList<ConversationSummary>> ConversationsAsync(UserContext user, CancellationToken ct)
    {
        Require(user);
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<(int ConversationId, string Prompt, int Turns, DateTime UpdatedAt, string LastStatus)>("""
            SELECT TOP 30 c.ConversationId, f.Prompt, c.Turns, c.UpdatedAt, l.Status AS LastStatus
              FROM (SELECT ConversationId, COUNT(*) AS Turns, MAX(CreatedAt) AS UpdatedAt, MAX(Id) AS LastId
                      FROM meta.AssistantRequest WHERE UserId = @UserId GROUP BY ConversationId) c
              JOIN meta.AssistantRequest f ON f.Id = c.ConversationId
              JOIN meta.AssistantRequest l ON l.Id = c.LastId
             ORDER BY c.UpdatedAt DESC
            """, new { UserId = user.Id });
        return rows.Select(r => new ConversationSummary(r.ConversationId, Title(r.Prompt), r.Turns, r.UpdatedAt, r.LastStatus)).ToList();
    }

    public async Task<Conversation> ConversationAsync(int id, UserContext user, CancellationToken ct)
    {
        Require(user);
        await using var cn = await db.OpenAsync(ct);
        var rows = await LoadTurnsAsync(cn, null, id, user.Id);
        if (rows.Count == 0) throw new NotFound("La conversación no existe.");
        var ctx = await CompileContextAsync(ct);
        return new(id, rows.Select(r => ToProposal(r, ctx)).ToList());
    }

    public async Task<Proposal> RequestAsync(AssistantRequestBody body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        Require(user);
        var credential = await keys.GetAsync(ct)
            ?? throw new AppException(StatusCodes.Status503ServiceUnavailable, "El asistente no está configurado: un administrador debe guardar la clave de Anthropic.");
        var prompt = body.Prompt?.Trim() ?? "";
        if (prompt.Length == 0) throw Invalid("prompt", "Describe el cambio que necesitas.");
        if (prompt.Length > options.MaxPromptChars) throw Invalid("prompt", $"La solicitud admite hasta {options.MaxPromptChars} caracteres.");

        List<Row> history = [];
        int id;
        // Spec 012 (H5 CA1): el lugar se reserva antes de llamar al proveedor, con un bloqueo por usuario. Antes se contaba
        // aquí y se guardaba al final: varias solicitudes simultáneas pasaban el conteo y rebasaban el límite.
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var locked = await cn.ExecuteScalarAsync<int>("""
                DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
                """, new { res = $"erp-assistant:{user.Id}" }, tx);
            if (locked < 0) throw new Conflict("Hay otra solicitud tuya en curso. Intenta de nuevo.");
            if (await UsedLastHourAsync(cn, user.Id, tx) >= options.RequestsPerHour)
                throw new AppException(StatusCodes.Status429TooManyRequests,
                    $"Alcanzaste el límite de {options.RequestsPerHour} solicitudes por hora al asistente. Intenta más tarde.");
            if (body.ConversationId is { } cid)
            {
                history = await LoadTurnsAsync(cn, tx, cid, user.Id);
                if (history.Count == 0) throw new NotFound("La conversación no existe.");
                if (history.Count >= options.MaxTurns)
                    throw Invalid("conversationId", $"La conversación llegó a {options.MaxTurns} solicitudes: empieza una nueva.");
            }
            id = await cn.ExecuteScalarAsync<int>("""
                INSERT meta.AssistantRequest (ConversationId, UserId, Prompt, Status, Model, Attempts, InputTokens, OutputTokens, ContextHash)
                OUTPUT inserted.Id VALUES (ISNULL(@ConversationId, 0), @UserId, @Prompt, 'pending', @Model, 0, 0, 0, REPLICATE('0', 64))
                """, new { body.ConversationId, UserId = user.Id, Prompt = prompt, Model = options.Model }, tx);
            if (body.ConversationId is null)
                await cn.ExecuteAsync("UPDATE meta.AssistantRequest SET ConversationId = Id WHERE Id = @id", new { id }, tx);
            await tx.CommitAsync(ct);
        }

        // Solo metadatos: modelo, tableros y nombres de roles (ver AssistantContext).
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        IReadOnlySet<string> roles;
        await using (var cn = await db.OpenAsync(ct)) roles = await PlanService.LoadRolesAsync(cn, null);
        var context = AssistantContext.Build(model, boards, roles, options.MaxContextChars);

        var messages = new List<AssistantTurn>();
        foreach (var turn in history.Where(t => t.ResponseJson is not null))
        {
            messages.Add(new(true, turn.Prompt));
            messages.Add(new(false, turn.ResponseJson!));
        }
        messages.Add(new(true, prompt));

        var outcome = new Outcome();
        try
        {
            await RunAsync(credential, messages, context.Json, model, roles, boards, outcome, ct);
        }
        catch (AssistantProviderException ex)
        {
            outcome.Status = ProposalStatus.Failed;
            outcome.ErrorCode = ex.Code;
        }
        catch (Exception)
        {
            // Nunca queda «pending»: una falla inesperada (o la cancelación) deja la solicitud como fallida.
            outcome.Status = ProposalStatus.Failed;
            outcome.ErrorCode ??= "error";
            outcome.Plan = null;
            outcome.PlanJson = null;
            await SaveAsync(http, user, id, body.ConversationId, context.Hash, outcome, CancellationToken.None);
            throw;
        }

        await SaveAsync(http, user, id, body.ConversationId, context.Hash, outcome, ct);
        if (outcome.Status == ProposalStatus.Failed && outcome.ErrorCode is not ("refusal" or "truncated"))
            throw new AppException(StatusCodes.Status502BadGateway, FailureMessage(outcome.ErrorCode));
        await using (var cn = await db.OpenAsync(ct))
        {
            var row = (await cn.QueryAsync<Row>(SelectRow + " WHERE Id = @id", new { id })).Single();
            return ToProposal(row, (model, roles, boards));
        }
    }

    /// <summary>Llama al modelo y, si el plan no se interpreta o no compila, le devuelve los errores para que lo corrija.</summary>
    private async Task RunAsync(AssistantCredential credential, List<AssistantTurn> messages, string context, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlySet<string> roles, IReadOnlyDictionary<string, DashboardDef> boards, Outcome outcome, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var reply = await llm.CompleteAsync(credential, context, messages, ct);
            outcome.Attempts = attempt + 1;
            outcome.InputTokens += reply.InputTokens;
            outcome.OutputTokens += reply.OutputTokens;
            if (string.Equals(reply.StopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                outcome.Status = ProposalStatus.Failed; outcome.ErrorCode = "refusal"; return;
            }
            if (string.Equals(reply.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                outcome.Status = ProposalStatus.Failed; outcome.ErrorCode = "truncated"; return;
            }

            var parsed = ProposalParser.Parse(reply.Text, json);
            outcome.Explanation = parsed.Explanation;
            outcome.ResponseJson = reply.Text is { Length: <= ProposalParser.MaxResponseChars } ? reply.Text : null;
            List<string> errors;
            if (parsed.Error is not null)
            {
                outcome.Plan = null;
                outcome.PlanJson = null;
                errors = [parsed.Error];
                outcome.Status = ProposalStatus.Invalid;
            }
            else if (parsed.IsAnswer)
            {
                outcome.Status = ProposalStatus.Answer;
                outcome.Plan = null;
                outcome.PlanJson = null;
                outcome.Errors = [];
                return;
            }
            else
            {
                outcome.Plan = parsed.Plan;
                outcome.PlanJson = parsed.PlanJson;
                var preview = PlanCompiler.Compile(parsed.Plan!, model, roles, boards).Preview;
                if (preview.Valid)
                {
                    outcome.Status = ProposalStatus.Proposed;
                    outcome.Errors = [];
                    return;
                }
                outcome.Status = ProposalStatus.Invalid;
                errors = preview.Errors.Select(e => e.Operation >= 0 ? $"Operación {e.Operation + 1}: {e.Message}" : e.Message).ToList();
            }
            outcome.Errors = errors;
            if (attempt >= options.RepairAttempts || reply.Text is null) return;
            messages.Add(new(false, reply.Text));
            messages.Add(new(true, AssistantPrompt.RepairMessage(errors)));
        }
    }

    /// <summary>Completa la fila reservada con el resultado.</summary>
    private async Task SaveAsync(HttpContext http, UserContext user, int id, int? conversationId, string contextHash, Outcome o, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Al refinar, la propuesta anterior deja de ser aplicable: solo vale el último plan de la conversación. Una respuesta
        // sin plan (una pregunta, algo que no es metadato) o una falla no la reemplaza (regresión F-03).
        if (conversationId is { } cid && o.Plan is not null)
            await cn.ExecuteAsync("""
                UPDATE meta.AssistantRequest SET Status = 'superseded'
                 WHERE ConversationId = @cid AND UserId = @UserId AND Id < @id AND Status IN ('proposed', 'invalid')
                """, new { cid, UserId = user.Id, id }, tx);
        await cn.ExecuteAsync("""
            UPDATE meta.AssistantRequest SET Status = @Status, Explanation = @Explanation, PlanJson = @PlanJson, ErrorsJson = @ErrorsJson,
                   ResponseJson = @ResponseJson, ErrorCode = @ErrorCode, Attempts = @Attempts, InputTokens = @InputTokens,
                   OutputTokens = @OutputTokens, ContextHash = @ContextHash
             WHERE Id = @id
            """, new
        {
            id, o.Status, o.Explanation, o.PlanJson,
            ErrorsJson = o.Errors.Count == 0 ? null : JsonSerializer.Serialize(o.Errors), o.ResponseJson, o.ErrorCode,
            o.Attempts, InputTokens = Tokens(o.InputTokens), OutputTokens = Tokens(o.OutputTokens), ContextHash = contextHash,
        }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.AssistantPropose, entity: "assistant", recordId: id.ToString(), after: new
        {
            conversationId = conversationId ?? id, status = o.Status, errorCode = o.ErrorCode, model = options.Model, attempts = o.Attempts,
            inputTokens = o.InputTokens, outputTokens = o.OutputTokens, contextHash, operations = o.Plan?.Operations.Count,
        });
        await tx.CommitAsync(ct);
    }

    public async Task<(int ChangeSetId, Proposal Proposal)> ApplyAsync(int id, AssistantApplyBody? body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        Require(user);
        environment.RequireDirectChange("una propuesta del asistente"); // spec 012 (H4)
        int changeSetId;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var row = (await cn.QueryAsync<Row>(SelectRow + " WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx)).SingleOrDefault() ?? throw new NotFound("La propuesta no existe.");
            if (row.Status != ProposalStatus.Proposed || row.PlanJson is null)
                throw new Conflict(row.Status switch
                {
                    ProposalStatus.Applied => "Esta propuesta ya se aplicó.",
                    ProposalStatus.Superseded => "Esta propuesta fue reemplazada por una más reciente de la conversación.",
                    ProposalStatus.Discarded => "Esta propuesta se descartó.",
                    _ => "Esta propuesta no se puede aplicar.",
                });
            if (string.IsNullOrWhiteSpace(body?.ReviewHash))
                throw Invalid("reviewHash", "Falta la huella de la propuesta revisada: recarga la conversación y revísala de nuevo.");
            // Se aplica exactamente lo guardado; PlanService lo recompila contra el modelo dentro de la transacción.
            var plan = JsonSerializer.Deserialize<ChangePlan>(row.PlanJson, json)!;
            // Spec 012 (H5 CA2): lo que se aplica debe ser lo que la persona revisó. Se compila contra el modelo de la
            // transacción bajo el mismo bloqueo que el aplicar (reentrante) y se compara la huella.
            await PlanService.LockModelAsync(cn, tx);
            var model = await MetadataStore.LoadAsync(cn, tx);
            var boards = await MetadataStore.LoadDashboardsAsync(cn, tx);
            var compiled = PlanCompiler.Compile(plan, model, await PlanService.LoadRolesAsync(cn, tx), boards);
            var preview = compiled.Preview;
            // Spec 021 (H2 CA3): la huella incluye lo que el plan toca tal como estaba (un setLayout compila igual aunque
            // otra persona haya cambiado el layout entre medio).
            var baseHash = ModelVersion.Base(model, boards, compiled.TouchedEntities, compiled.TouchedDashboards);
            if (preview.Valid && !string.Equals(body!.ReviewHash, ReviewHash.Of(row.PlanJson, preview, PlanWarnings.For(plan, model), baseHash), StringComparison.OrdinalIgnoreCase))
                throw new Conflict("El modelo cambió desde que revisaste la propuesta: revisa la vista previa actualizada antes de aplicarla.");
            changeSetId = await plans.ApplyInTransactionAsync(cn, tx, plan, http);
            await cn.ExecuteAsync("""
                UPDATE meta.AssistantRequest SET Status = 'applied', AppliedAt = SYSUTCDATETIME(), ChangeSetId = @changeSetId WHERE Id = @id
                """, new { id, changeSetId }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.AssistantApply, entity: "assistant", recordId: id.ToString(),
                after: new { conversationId = row.ConversationId, changeSetId, operations = plan.Operations.Count });
            await tx.CommitAsync(ct);
        }
        store.Invalidate();
        return (changeSetId, await ProposalAsync(id, user, ct));
    }

    public async Task<Proposal> DiscardAsync(int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        Require(user);
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var status = await cn.ExecuteScalarAsync<string?>(
                "SELECT Status FROM meta.AssistantRequest WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx) ?? throw new NotFound("La propuesta no existe.");
            if (status is not (ProposalStatus.Proposed or ProposalStatus.Invalid))
                throw new Conflict("Solo se puede descartar una propuesta vigente.");
            await cn.ExecuteAsync("UPDATE meta.AssistantRequest SET Status = 'discarded' WHERE Id = @id", new { id }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.AssistantDiscard, entity: "assistant", recordId: id.ToString());
            await tx.CommitAsync(ct);
        }
        return await ProposalAsync(id, user, ct);
    }

    private async Task<Proposal> ProposalAsync(int id, UserContext user, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var row = (await cn.QueryAsync<Row>(SelectRow + " WHERE Id = @id AND UserId = @UserId", new { id, UserId = user.Id })).Single();
        return ToProposal(row, await CompileContextAsync(ct));
    }

    private async Task<(IReadOnlyDictionary<string, EntityDef> Model, IReadOnlySet<string> Roles, IReadOnlyDictionary<string, DashboardDef> Boards)>
        CompileContextAsync(CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        return (model, await PlanService.LoadRolesAsync(cn, null), boards);
    }

    /// <summary>Las propuestas vigentes se recompilan al leerlas: si el modelo cambió, la vista previa lo refleja.</summary>
    private Proposal ToProposal(Row r,
        (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlySet<string> Roles, IReadOnlyDictionary<string, DashboardDef> Boards) ctx)
    {
        ChangePlan? plan = null;
        if (r.PlanJson is not null)
        {
            try { plan = JsonSerializer.Deserialize<ChangePlan>(r.PlanJson, json); }
            catch (JsonException ex) { log.LogWarning("Propuesta {Id} con plan ilegible: {Message}", r.Id, ex.Message); }
        }
        var live = r.Status is ProposalStatus.Proposed or ProposalStatus.Invalid;
        var compiled = plan is not null && live ? PlanCompiler.Compile(plan, ctx.Model, ctx.Roles, ctx.Boards) : null;
        var preview = compiled?.Preview;
        var errors = r.ErrorsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(r.ErrorsJson) ?? [];
        IReadOnlyList<string> warnings = plan is null ? [] : PlanWarnings.For(plan, ctx.Model);
        var reviewable = r.Status == ProposalStatus.Proposed && preview?.Valid == true;
        return new Proposal
        {
            Id = r.Id, ConversationId = r.ConversationId, Prompt = r.Prompt, Status = r.Status, Explanation = r.Explanation,
            Plan = plan, Preview = preview, Warnings = warnings,
            // Con plan, los errores del compilador ya vienen en la vista previa; sin plan, se muestran los de interpretación.
            Errors = plan is null ? errors : [],
            Attempts = r.Attempts, Model = r.Model, CreatedAt = r.CreatedAt, AppliedAt = r.AppliedAt, ChangeSetId = r.ChangeSetId,
            Error = r.Status == ProposalStatus.Failed ? FailureMessage(r.ErrorCode) : null,
            CanApply = reviewable && environment.DirectChanges,
            ReviewHash = reviewable ? ReviewHash.Of(r.PlanJson!, preview!, warnings,
                ModelVersion.Base(ctx.Model, ctx.Boards, compiled!.TouchedEntities, compiled.TouchedDashboards)) : null,
            BlockedReason = r.Status == ProposalStatus.Proposed && !environment.DirectChanges
                ? "Este entorno exige la aprobación de otro administrador: la propuesta no se aplica aquí. Los cambios al modelo llegan como paquete desde Configuración › Entornos."
                : null,
        };
    }

    private static string FailureMessage(string? code) => code switch
    {
        "auth" => "Anthropic rechazó la clave del asistente. Un administrador debe capturarla de nuevo en Configuración › Asistente IA.",
        "rate_limit" => "El proveedor del asistente está limitando las solicitudes. Intenta en unos minutos.",
        "timeout" or "network" => "El asistente no respondió a tiempo. Intenta de nuevo.",
        "refusal" => "El asistente no pudo atender esta solicitud. Reformúlala en términos del modelo del ERP.",
        "workspace" => "La clave del asistente necesita un espacio de trabajo de Anthropic. Un administrador debe indicarlo en Configuración › Asistente IA.",
        "model" => "La clave del asistente no tiene acceso al modelo configurado.",
        "truncated" => "La respuesta del asistente quedó incompleta. Pide un cambio más acotado.",
        _ => "El asistente no está disponible en este momento. Intenta más tarde.",
    };

    private static void Require(UserContext user)
    {
        if (!(user.CanManageModel || user.IsSuperuser) || user.IsService) throw new Forbidden();
    }

    private static ValidationFailed Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] }, message);

    private static string Title(string prompt) => prompt.Length <= 80 ? prompt : prompt[..77].TrimEnd() + "…";

    private static int Tokens(long n) => (int)Math.Min(n, int.MaxValue);

    private static Task<int> UsedLastHourAsync(System.Data.Common.DbConnection cn, int userId, System.Data.Common.DbTransaction? tx = null) =>
        cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.AssistantRequest WHERE UserId = @userId AND CreatedAt > DATEADD(hour, -1, SYSUTCDATETIME())",
            new { userId }, tx);

    private static async Task<List<Row>> LoadTurnsAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction? tx, int conversationId, int userId) =>
        (await cn.QueryAsync<Row>(SelectRow + " WHERE ConversationId = @conversationId AND UserId = @userId AND Status <> 'pending' ORDER BY Id",
            new { conversationId, userId }, tx)).ToList();

    private const string SelectRow = """
        SELECT Id, ConversationId, Prompt, Status, Explanation, PlanJson, ErrorsJson, ResponseJson, ErrorCode, Model, Attempts,
               CreatedAt, AppliedAt, ChangeSetId FROM meta.AssistantRequest
        """;

    private sealed record Row(int Id, int ConversationId, string Prompt, string Status, string? Explanation, string? PlanJson,
        string? ErrorsJson, string? ResponseJson, string? ErrorCode, string Model, int Attempts, DateTime CreatedAt, DateTime? AppliedAt,
        int? ChangeSetId);

    private sealed class Outcome
    {
        public string Status { get; set; } = ProposalStatus.Failed;
        public string? Explanation { get; set; }
        public ChangePlan? Plan { get; set; }
        public string? PlanJson { get; set; }
        public string? ResponseJson { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = [];
        public string? ErrorCode { get; set; }
        public int Attempts { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }
}
