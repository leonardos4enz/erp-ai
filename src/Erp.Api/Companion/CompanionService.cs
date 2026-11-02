using System.Data.Common;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Assistant;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Companion;

/// <summary>H2 CA3: lo que la persona está viendo (lo manda el panel; el servidor lo revalida con su alcance).</summary>
public sealed record CompanionScreen(string? Entity, long? Id);
public sealed record CompanionMessageBody(string? Text, CompanionScreen? Context = null);

public sealed record CompanionLimits(int MaxPromptChars, int MaxTurns, int MaxPendingActions, int ConversationsPerDay, decimal CostPerDayUsd);

public sealed record CompanionStatusDto(bool Available, string? Reason, CompanionLimits Limits, int ConversationsToday, decimal CostTodayUsd, int? ActiveId);

public sealed record CompanionSummaryDto(int Id, string Title, string Status, int Turns, DateTime CreatedAt, DateTime UpdatedAt, int PendingActions);

public sealed record CompanionConversationDto
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public int Turns { get; init; }
    public int Steps { get; init; }
    public decimal CostUsd { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public bool CanSend { get; init; }
    public string? SendBlockedReason { get; init; }
    public IReadOnlyList<StudioStepDto> Trail { get; init; } = [];
    public IReadOnlyList<CompanionActionDto> Actions { get; init; } = [];
}

/// <summary>
/// Spec 046: las conversaciones del Acompañante. Cualquier persona activa que no sea cuenta de servicio; cada conversación es
/// de su dueña (404 a otra) y de la empresa en que empezó. El trabajo lo hace <see cref="CompanionAgent"/> en segundo plano.
/// </summary>
public sealed class CompanionService(Db db, MetadataStore store, CompanionOptions options, AssistantOptions assistant, AssistantKeys keys,
    CompanionActions actions, CompanionSignals signals, ILogger<CompanionService> log)
{
    /// <summary>
    /// Revisión adversarial 046 (F-01): borrar una conversación borra lo que se dijo y se leyó, pero su fila queda con este
    /// estado (sin título ni texto) para que siga contando en los topes del día; la retención la borra del todo.
    /// </summary>
    public const string Deleted = "deleted";

    public static async Task LockUserAsync(DbConnection cn, DbTransaction tx, int userId)
    {
        var locked = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, new { res = $"erp-companion:{userId}" }, tx);
        if (locked < 0) throw new Conflict("Hay otra operación tuya con el Acompañante en curso. Intenta de nuevo.");
    }

    /// <summary>Conversaciones empezadas en 24 horas y el costo de las que tuvieron actividad en 24 horas (como Studio, 036 F-01).</summary>
    internal static async Task<(int Conversations, long CostMicros)> TodayAsync(DbConnection cn, DbTransaction? tx, int userId, int? except = null) =>
        await cn.QuerySingleAsync<(int, long)>("""
            SELECT ISNULL(SUM(CASE WHEN CreatedAt > DATEADD(day, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CostMicros + ReservedMicros), 0) FROM meta.CompanionConversation
             WHERE UserId = @userId AND (@except IS NULL OR Id <> @except)
               AND (CreatedAt > DATEADD(day, -1, SYSUTCDATETIME()) OR LastActivityAt > DATEADD(day, -1, SYSUTCDATETIME()))
            """, new { userId, except }, tx);

    /// <summary>H1 CA4: por qué el Acompañante no está disponible en este entorno (null si lo está).</summary>
    private async Task<string?> UnavailableAsync(CancellationToken ct) =>
        !options.Enabled ? "El Acompañante está apagado en este entorno."
        : await keys.GetAsync(ct) is null ? "El Acompañante no está disponible: un administrador debe guardar la clave de Anthropic en Configuración › Asistente IA."
        : null;

    public async Task<CompanionStatusDto> StatusAsync(UserContext user, CancellationToken ct)
    {
        NotService(user);
        var reason = await UnavailableAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var (conversations, cost) = await TodayAsync(cn, null, user.Id);
        var active = await cn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM meta.CompanionConversation WHERE UserId = @Id AND Status IN ('queued', 'running') ORDER BY Id DESC", new { user.Id });
        return new(reason is null, reason, new(options.MaxPromptChars, options.MaxTurns, options.MaxPendingActions, options.ConversationsPerDay, options.CostPerDayUsd),
            conversations, cost / 1_000_000m, active);
    }

    public async Task<CompanionConversationDto> StartAsync(CompanionMessageBody body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        NotService(user);
        if (await UnavailableAsync(ct) is { } reason) throw new AppException(StatusCodes.Status503ServiceUnavailable, reason);
        var text = Text(body);
        var screen = await ScreenAsync(body.Context, user, ct);
        int id;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            await LockUserAsync(cn, tx, user.Id);
            if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.CompanionConversation WHERE UserId = @Id AND Status IN ('queued', 'running')", new { user.Id }, tx) > 0)
                throw new Conflict(OtherActive);
            var (conversations, cost) = await TodayAsync(cn, tx, user.Id);
            if (conversations >= options.ConversationsPerDay)
                throw new AppException(StatusCodes.Status429TooManyRequests, $"Llegaste al límite de {options.ConversationsPerDay} conversaciones con el Acompañante en 24 horas.");
            if (cost >= options.CostPerDayMicros)
                throw new AppException(StatusCodes.Status429TooManyRequests, $"Llegaste al costo máximo del Acompañante del día ({options.CostPerDayUsd} USD).");
            id = await cn.ExecuteScalarAsync<int>("""
                INSERT meta.CompanionConversation (UserId, CompanyId, SecurityStamp, Title, Status, Model)
                OUTPUT inserted.Id VALUES (@UserId, @CompanyId, @Stamp, @title, 'queued', @Model)
                """, new { UserId = user.Id, CompanyId = user.Company?.Id, Stamp = user.SecurityStamp, title = StudioAgent.Cut(text, 80), assistant.Model }, tx);
            await UserMessageAsync(cn, tx, id, 1, text, screen);
            await Audit.WriteAsync(cn, tx, http, AuditActions.CompanionMessage, entity: "companion", recordId: id.ToString(),
                after: new { turn = 1, length = text.Length, screen = screen is not null, model = assistant.Model });
            await tx.CommitAsync(ct);
        }
        return await GetAsync(id, null, user, ct);
    }

    public async Task<CompanionConversationDto> SendAsync(int id, CompanionMessageBody body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        NotService(user);
        if (await UnavailableAsync(ct) is { } reason) throw new AppException(StatusCodes.Status503ServiceUnavailable, reason);
        var text = Text(body);
        var screen = await ScreenAsync(body.Context, user, ct);
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            await LockUserAsync(cn, tx, user.Id);
            var c = await cn.QuerySingleOrDefaultAsync<Row>(SelectRow + " WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId AND Status <> 'deleted'", new { id, UserId = user.Id }, tx)
                    ?? throw new NotFound("La conversación no existe.");
            if (Blocked(c, user) is { } blocked) throw new Conflict(blocked);
            if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.CompanionConversation WHERE UserId = @UserId AND Id <> @id AND Status IN ('queued', 'running')",
                    new { id, UserId = user.Id }, tx) > 0)
                throw new Conflict(OtherActive);
            var limit = Limit(c);
            if (limit is null && (await TodayAsync(cn, tx, user.Id)).CostMicros >= options.CostPerDayMicros)
                limit = $"Llegaste al costo máximo del Acompañante del día ({options.CostPerDayUsd} USD).";
            if (limit is not null) throw new AppException(StatusCodes.Status429TooManyRequests, limit);
            var turn = c.Turns + 1;
            await cn.ExecuteAsync("""
                UPDATE meta.CompanionConversation SET Status = 'queued', Turns = @turn, SecurityStamp = @Stamp, ErrorCode = NULL,
                       CancelRequested = 0, LastActivityAt = SYSUTCDATETIME()
                 WHERE Id = @id
                """, new { id, turn, Stamp = user.SecurityStamp }, tx);
            await UserMessageAsync(cn, tx, id, turn, text, screen);
            await Audit.WriteAsync(cn, tx, http, AuditActions.CompanionMessage, entity: "companion", recordId: id.ToString(),
                after: new { turn, length = text.Length, screen = screen is not null });
            await tx.CommitAsync(ct);
        }
        signals.Pulse(id);
        return await GetAsync(id, int.MaxValue, user, ct);
    }

    public async Task<IReadOnlyList<CompanionSummaryDto>> ListAsync(UserContext user, CancellationToken ct)
    {
        NotService(user);
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<(int Id, string Title, string Status, int Turns, DateTime CreatedAt, DateTime LastActivityAt, int Pending)>("""
            SELECT TOP 50 c.Id, c.Title, c.Status, c.Turns, c.CreatedAt, c.LastActivityAt,
                   (SELECT COUNT(*) FROM meta.CompanionAction a WHERE a.ConversationId = c.Id AND a.Status = 'proposed')
              FROM meta.CompanionConversation c WHERE c.UserId = @Id AND c.Status <> 'deleted' ORDER BY c.LastActivityAt DESC, c.Id DESC
            """, new { user.Id })).Select(r => new CompanionSummaryDto(r.Id, r.Title, r.Status, r.Turns, Utc(r.CreatedAt), Utc(r.LastActivityAt), r.Pending)).ToList();
    }

    public async Task<CompanionConversationDto> GetAsync(int id, int? after, UserContext user, CancellationToken ct)
    {
        NotService(user);
        Row row;
        List<StudioService.StepRow> steps;
        await using (var cn = await db.OpenAsync(ct))
        {
            row = await cn.QuerySingleOrDefaultAsync<Row>(SelectRow + " WHERE Id = @id AND UserId = @UserId AND Status <> 'deleted'", new { id, UserId = user.Id })
                  ?? throw new NotFound("La conversación no existe.");
            // Revisión adversarial 046 (F-07): quien ya no está en la empresa de la conversación no vuelve a leer lo que se dijo ahí.
            if (!InCompany(row.CompanyId, user)) throw new NotFound("La conversación no existe.");
            steps = (await cn.QueryAsync<StudioService.StepRow>("""
                SELECT Seq, Kind, Tool, Status, Summary, InputJson, OutputJson, DurationMs, CreatedAt, Turn FROM meta.CompanionStep
                 WHERE ConversationId = @id AND Seq > @after ORDER BY Seq
                """, new { id, after = after ?? 0 })).ToList();
        }
        var blocked = Blocked(row, user) ?? Limit(row);
        return new CompanionConversationDto
        {
            Id = row.Id, Title = row.Title, Status = row.Status, Turns = row.Turns, Steps = row.Steps, CostUsd = row.CostMicros / 1_000_000m,
            Error = row.ErrorCode is null || row.Status == StudioStatus.Cancelled ? null : FailureMessage(row.ErrorCode, row.Summary),
            CreatedAt = Utc(row.CreatedAt), UpdatedAt = Utc(row.LastActivityAt), CanSend = blocked is null, SendBlockedReason = blocked,
            Trail = steps.Select(StudioService.ToStep).ToList(), Actions = await actions.ListAsync(id, user.Id, ct),
        };
    }

    public async Task<CompanionConversationDto> CancelAsync(int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        NotService(user);
        Guid? stopLease;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var (status, lease) = (await cn.QueryAsync<(string, Guid?)>("SELECT Status, LeaseToken FROM meta.CompanionConversation WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId AND Status <> 'deleted'",
                new { id, UserId = user.Id }, tx)).SingleOrDefault();
            if (status is null) throw new NotFound("La conversación no existe.");
            stopLease = lease;
            if (status == StudioStatus.Queued)
                await cn.ExecuteAsync("UPDATE meta.CompanionConversation SET Status = 'cancelled', ErrorCode = 'cancelled', FinishedAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
            else if (status == StudioStatus.Running)
                await cn.ExecuteAsync("UPDATE meta.CompanionConversation SET CancelRequested = 1 WHERE Id = @id", new { id }, tx);
            await tx.CommitAsync(ct);
        }
        if (stopLease is { } l) signals.Stop(id, l);
        signals.Pulse(id);
        return await GetAsync(id, null, user, ct);
    }

    /// <summary>H6 CA3: la persona borra una conversación suya con sus pasos, mensajes y acciones (la bitácora se queda).</summary>
    public async Task DeleteAsync(int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        NotService(user);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var status = await cn.ExecuteScalarAsync<string?>("SELECT Status FROM meta.CompanionConversation WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId AND Status <> 'deleted'",
            new { id, UserId = user.Id }, tx) ?? throw new NotFound("La conversación no existe.");
        if (StudioStatus.Active(status)) throw new Conflict(Working);
        // Una acción que se está ejecutando termina antes de poder borrar la conversación (su resultado se guarda en ella). El
        // bloqueo de sus filas ordena esto con confirmar: o confirmar ya la marcó (409 aquí) o espera y ya no la encuentra (404).
        if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.CompanionAction WITH (UPDLOCK, HOLDLOCK) WHERE ConversationId = @id AND Status = 'applying'", new { id }, tx) > 0)
            throw new Conflict("Una acción de esta conversación se está aplicando: espera a que termine.");
        await cn.ExecuteAsync("""
            UPDATE meta.CompanionConversation SET Status = 'deleted', Title = N'(borrada)', Summary = NULL, ErrorCode = NULL, LiveJson = NULL,
                   LastActivityAt = SYSUTCDATETIME() WHERE Id = @id;
            DELETE FROM meta.CompanionMessage WHERE ConversationId = @id;
            DELETE FROM meta.CompanionStep WHERE ConversationId = @id;
            UPDATE meta.CompanionAction SET Status = CASE WHEN Status = 'proposed' THEN 'discarded' ELSE Status END,
                   DecidedAt = ISNULL(DecidedAt, SYSUTCDATETIME()), BodyJson = N'{}', PreviewJson = NULL, ResultJson = NULL
             WHERE ConversationId = @id;
            """, new { id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.CompanionDelete, entity: "companion", recordId: id.ToString());
        await tx.CommitAsync(ct);
    }

    // ---------- Trabajador ----------

    internal async Task<CompanionClaim?> ClaimAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<CompanionClaim>("""
            WITH next AS (SELECT TOP 1 * FROM meta.CompanionConversation WITH (READPAST, UPDLOCK, ROWLOCK) WHERE Status = 'queued' ORDER BY Id)
            UPDATE next SET Status = 'running', LeaseToken = NEWID(), LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME()),
                   TurnStartedAt = SYSUTCDATETIME(), CancelRequested = 0, ErrorCode = NULL, LiveJson = NULL, LastActivityAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.UserId, inserted.CompanyId, inserted.SecurityStamp, inserted.LeaseToken, inserted.Turns AS Turn,
                   inserted.TurnStartedAt, inserted.Steps, inserted.ToolCalls, inserted.InputTokens, inserted.OutputTokens, inserted.CostMicros
            """, new { lease = options.LeaseSeconds })).SingleOrDefault();
    }

    /// <summary>
    /// Turnos cuyo trabajador dejó de renovar: interrumpidos (no se repiten), con lo reservado cobrado y lo leído purgado; uno
    /// por uno, en su transacción (040 D-03).
    /// </summary>
    internal async Task<int> ExpireAsync(CancellationToken ct)
    {
        List<int> candidates;
        await using (var cn = await db.OpenAsync(ct))
            candidates = (await cn.QueryAsync<int>("SELECT Id FROM meta.CompanionConversation WHERE Status = 'running' AND LeaseUntil < SYSUTCDATETIME() ORDER BY Id")).ToList();
        var count = 0;
        foreach (var id in candidates)
        {
            try
            {
                await using var cn = await db.OpenAsync(ct);
                await using var tx = await cn.BeginTransactionAsync(ct);
                var userId = await cn.ExecuteScalarAsync<int?>("""
                    UPDATE meta.CompanionConversation SET Status = 'interrupted', ErrorCode = 'interrupted', FinishedAt = SYSUTCDATETIME(),
                           LeaseToken = NULL, LeaseUntil = NULL, CostMicros = CostMicros + ReservedMicros, ReservedMicros = 0,
                           LiveJson = NULL, LastActivityAt = SYSUTCDATETIME()
                    OUTPUT inserted.UserId
                     WHERE Id = @id AND Status = 'running' AND LeaseUntil < SYSUTCDATETIME()
                    """, new { id }, tx);
                if (userId is null) continue;
                await PurgeDataAsync(cn, tx, id);
                await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.CompanionFinish, entity: "companion", recordId: id.ToString(),
                    after: new { status = StudioStatus.Interrupted }, userId: userId,
                    username: await cn.ExecuteScalarAsync<string>("SELECT Username FROM meta.[User] WHERE Id = @userId", new { userId }, tx));
                await tx.CommitAsync(ct);
                count++;
                log.LogWarning("La conversación del Acompañante {Id} quedó sin trabajador y se marcó interrumpida", id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogError(ex, "No se pudo cerrar la conversación del Acompañante {Id}; se reintenta en la siguiente vuelta", id); }
        }
        return count;
    }

    /// <summary>H6 CA3: conversaciones sin actividad en <c>Companion:RetentionDays</c>, por lotes (cascada a lo suyo).</summary>
    internal async Task<int> RetainAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return await cn.ExecuteAsync("""
            DELETE TOP (200) FROM meta.CompanionConversation
             WHERE LastActivityAt < DATEADD(day, -@days, SYSUTCDATETIME()) AND Status NOT IN ('queued', 'running')
               AND NOT EXISTS (SELECT 1 FROM meta.CompanionAction a WHERE a.ConversationId = meta.CompanionConversation.Id AND a.Status = 'applying')
            """, new { days = options.RetentionDays });
    }

    /// <summary>
    /// H2 CA4: lo que devolvieron las herramientas de datos, en pasos e historial. De una propuesta queda solo su número (la
    /// tarjeta la arma el panel con la acción, que guarda su vista previa mientras está pendiente).
    /// </summary>
    internal static async Task PurgeDataAsync(DbConnection cn, DbTransaction tx, int conversationId)
    {
        await cn.ExecuteAsync("""
            UPDATE meta.CompanionStep
               SET OutputJson = CASE WHEN Kind = 'action' THEN CONCAT('{"accion":', TRY_CAST(JSON_VALUE(OutputJson, '$.accion') AS int), '}') ELSE NULL END,
                   -- F-08: lo que el modelo quiso proponer (valores tomados de registros), también si la propuesta falló.
                   InputJson = CASE WHEN Tool = 'proponer_accion' THEN NULL ELSE InputJson END
             WHERE ConversationId = @conversationId AND Tool IN @DataTools AND OutputJson IS NOT NULL
            """, new { conversationId, CompanionTools.DataTools }, tx);
        foreach (var (id, content) in await cn.QueryAsync<(long, string)>(
                     "SELECT Id, ContentJson FROM meta.CompanionMessage WHERE ConversationId = @conversationId AND HasData = 1", new { conversationId }, tx))
            await cn.ExecuteAsync("UPDATE meta.CompanionMessage SET ContentJson = @content, HasData = 0 WHERE Id = @id",
                new { id, content = CompanionTools.PurgeContent(content) }, tx);
    }

    // ---------- Ayudas ----------

    private const string OtherActive = "Tienes otra conversación con el Acompañante en curso: espera a que termine o detenla.";
    private const string Working = "El Acompañante está respondiendo en esta conversación: espera a que termine o detenlo.";
    private const string OtherCompanyMessage = "Esta conversación es de otra empresa: vuelve a esa empresa o empieza una conversación nueva.";

    internal static bool InCompany(int? companyId, UserContext user) => companyId is null || user.Companies.Any(c => c.Id == companyId);

    private static void NotService(UserContext user)
    {
        if (user.IsService) throw new Forbidden("El Acompañante es para personas, no para cuentas de servicio.");
    }

    private string Text(CompanionMessageBody body)
    {
        var text = body.Text?.Trim() ?? "";
        if (text.Length == 0) throw Invalid("text", "Escribe tu mensaje.");
        if (text.Length > options.MaxPromptChars) throw Invalid("text", $"El mensaje admite hasta {options.MaxPromptChars} caracteres.");
        return text;
    }

    /// <summary>
    /// H2 CA3: el contexto de la pantalla solo si la persona ve el catálogo (como <c>/api/model/entities/{name}</c>). Uno que no
    /// existe, que no ve o con un nombre inválido se ignora sin decir por qué: la respuesta no revela qué catálogos existen.
    /// </summary>
    private async Task<string?> ScreenAsync(CompanionScreen? screen, UserContext user, CancellationToken ct)
    {
        if (screen?.Entity is not { Length: > 0 and <= 100 } name) return null;
        var model = await store.GetAsync(ct);
        if (!model.TryGetValue(name, out var e) || e.IsArchived || e.IsChild) return null;
        var access = user.Access(e.Name);
        if (!user.CanManageModel && !access.Read && !access.Create) return null;
        return CompanionPrompt.Screen(e.Name, e.Label, screen.Id is > 0 ? screen.Id : null);
    }

    private static async Task UserMessageAsync(DbConnection cn, DbTransaction tx, int id, int turn, string text, string? screen)
    {
        await cn.ExecuteAsync("""
            INSERT meta.CompanionStep (ConversationId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs, Turn)
            SELECT @id, ISNULL(MAX(Seq), 0) + 1, 'user', NULL, NULL, @output, 'ok', @summary, 0, @turn FROM meta.CompanionStep WITH (UPDLOCK, HOLDLOCK) WHERE ConversationId = @id;
            INSERT meta.CompanionMessage (ConversationId, Seq, FromUser, ContentJson, HasData)
            SELECT @id, ISNULL(MAX(Seq), 0) + 1, 1, @content, 0 FROM meta.CompanionMessage WITH (UPDLOCK, HOLDLOCK) WHERE ConversationId = @id;
            """, new
        {
            id, turn, output = new JsonObject { ["text"] = text }.ToJsonString(), summary = StudioAgent.Cut(text, 400),
            content = StudioTranscript.Serialize([new StudioText(CompanionPrompt.Request(text, screen))]),
        }, tx);
    }

    private const string SelectRow = """
        SELECT Id, Title, Status, ErrorCode, Summary, Turns, Steps, ToolCalls, InputTokens, OutputTokens, CostMicros, CompanyId, CreatedAt, LastActivityAt
          FROM meta.CompanionConversation
        """;

    private sealed record Row(int Id, string Title, string Status, string? ErrorCode, string? Summary, int Turns, int Steps, int ToolCalls,
        long InputTokens, long OutputTokens, long CostMicros, int? CompanyId, DateTime CreatedAt, DateTime LastActivityAt);

    /// <summary>Lo que impide seguir la conversación por su estado o su empresa (H6 CA2).</summary>
    private static string? Blocked(Row r, UserContext user) =>
        StudioStatus.Active(r.Status) ? Working
        : r.CompanyId != user.Company?.Id ? OtherCompanyMessage
        : null;

    private string? Limit(Row r) =>
        r.Turns >= options.MaxTurns ? $"Esta conversación llegó al tope de {options.MaxTurns} mensajes: empieza una nueva."
        : r.Steps >= options.MaxSteps ? $"Esta conversación llegó al tope de {options.MaxSteps} pasos: empieza una nueva."
        : r.ToolCalls >= options.MaxToolCalls ? $"Esta conversación llegó al tope de {options.MaxToolCalls} consultas: empieza una nueva."
        : r.InputTokens + r.OutputTokens + options.MaxTokensPerCall > options.MaxTokens ? "Esta conversación es muy larga: empieza una nueva."
        : r.CostMicros >= options.MaxCostMicros ? $"Esta conversación llegó a su costo máximo ({options.MaxCostUsd} USD): empieza una nueva."
        : null;

    internal static string FailureMessage(string code, string? summary) => code switch
    {
        "limit_steps" or "limit_tools" or "limit_tokens" or "limit_cost" or "limit_day" or "limit_time" => summary ?? "Se alcanzó un tope de la conversación.",
        "identity" => "La conversación se detuvo: tu cuenta cambió (contraseña, estado o empresa).",
        "interrupted" => "La respuesta se interrumpió (el servidor se reinició o dejó de responder). Vuelve a preguntar.",
        "not_configured" => "El Acompañante no tiene clave de Anthropic.",
        "auth" => "Anthropic rechazó la clave. Un administrador debe capturarla de nuevo en Configuración › Asistente IA.",
        "rate_limit" => "El servicio está limitando las solicitudes. Intenta en unos minutos.",
        "timeout" or "network" => "El servicio no respondió a tiempo. Intenta de nuevo.",
        "refusal" => "El Acompañante no pudo atender este mensaje. Reformúlalo.",
        "truncated" => "La respuesta quedó incompleta. Pide algo más acotado.",
        _ => "La respuesta falló por un error inesperado.",
    };

    private static ValidationFailed Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] }, message);

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}

/// <summary>Spec 046: reclama turnos con lease, vence los interrumpidos y barre propuestas viejas y conversaciones sin actividad.</summary>
public sealed class CompanionDispatcher(IServiceScopeFactory scopes, CompanionOptions options, ILogger<CompanionDispatcher> log) : BackgroundService
{
    private int running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runs = new List<Task>();
        var lastSweep = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<CompanionService>();
                try
                {
                    await svc.ExpireAsync(stoppingToken);
                    if (DateTime.UtcNow - lastSweep > TimeSpan.FromSeconds(Math.Min(60, options.ApplyingSeconds)))
                    {
                        await scope.ServiceProvider.GetRequiredService<CompanionActions>().SweepAsync(stoppingToken);
                        await svc.RetainAsync(stoppingToken);
                        lastSweep = DateTime.UtcNow;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Error al vencer o barrer conversaciones del Acompañante");
                }
                while (Volatile.Read(ref running) < options.Workers && await svc.ClaimAsync(stoppingToken) is { } claim)
                {
                    Interlocked.Increment(ref running);
                    runs.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await using var work = scopes.CreateAsyncScope();
                            await work.ServiceProvider.GetRequiredService<CompanionAgent>().RunAsync(claim, stoppingToken);
                        }
                        catch (Exception ex) { log.LogError(ex, "Error en la conversación del Acompañante {Id}", claim.Id); }
                        finally { Interlocked.Decrement(ref running); }
                    }, CancellationToken.None));
                }
                runs.RemoveAll(t => t.IsCompleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Error en el trabajador del Acompañante"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); } catch (OperationCanceledException) { break; }
        }
        await Task.WhenAll(runs);
    }
}
