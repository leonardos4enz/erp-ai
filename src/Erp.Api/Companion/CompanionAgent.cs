using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Assistant;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Companion;

/// <summary>Turno reclamado por un trabajador, con los contadores acumulados de la conversación (los topes son de ella).</summary>
public sealed record CompanionClaim(int Id, int UserId, int? CompanyId, Guid SecurityStamp, Guid LeaseToken, int Turn, DateTime TurnStartedAt,
    int Steps, int ToolCalls, long InputTokens, long OutputTokens, long CostMicros);

/// <summary>
/// Spec 046: un turno del Acompañante. El bucle es el de <see cref="StudioAgent"/> (036): topes y reserva del costo antes de
/// cada llamada, identidad recargada en cada paso, pasos e historial escritos solo con el lease, estado en vivo, «Detener»,
/// cierre con purga de lo leído en una transacción. Sin copia aislada ni Plan de Cambios: solo lee como la persona y propone
/// acciones que ella confirma (ADR-0010).
/// </summary>
public sealed class CompanionAgent(Db db, CompanionOptions options, AssistantOptions assistant, AssistantKeys keys, IStudioModel llm,
    CompanionToolRunner tools, UserContextLoader users, IServiceProvider services, CompanionSignals signals, ILogger<CompanionAgent> log)
{
    private sealed class Run(CompanionClaim claim)
    {
        public CompanionClaim Claim { get; } = claim;
        public int Seq;
        public int MessageSeq;
        public int Steps = claim.Steps;
        public int ToolCalls = claim.ToolCalls;
        public long Tokens = claim.InputTokens + claim.OutputTokens;
        public long Cost = claim.CostMicros;
        public long Reserved;
        public long Input = claim.InputTokens;
        public long Output = claim.OutputTokens;
        public readonly System.Text.StringBuilder Partial = new();
        public long LiveAt;
        public UserContext? User;
        public string? LastText;
        public long LastTokens;
        public long LastChars;
        public long LastInput;
    }

    private sealed record Outcome(string Status, string? ErrorCode, string? Summary = null);

    public async Task RunAsync(CompanionClaim claim, CancellationToken stopping)
    {
        var run = new Run(claim);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var remaining = claim.TurnStartedAt.AddMinutes(options.MaxMinutes) - DateTime.UtcNow;
        cts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var lost = false;
        var cancelled = false;
        using var registration = signals.Run(claim.Id, claim.LeaseToken, () => { cancelled = true; cts.Cancel(); });
        var renew = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.RenewSeconds), cts.Token);
                    bool? state;
                    try { state = await RenewAsync(claim); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning("No se pudo renovar el lease de la conversación del Acompañante {Id}: {Message}", claim.Id, ex.Message);
                        continue;
                    }
                    if (state is null) { lost = true; await cts.CancelAsync(); return; }
                    if (state == true) { cancelled = true; await cts.CancelAsync(); return; }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        Outcome? outcome = null;
        try
        {
            outcome = await LoopAsync(run, cts.Token);
        }
        catch (OperationCanceledException) when (lost) { log.LogWarning("La conversación del Acompañante {Id} la tomó otro trabajador", claim.Id); }
        catch (StudioLeaseLost) { log.LogWarning("La conversación del Acompañante {Id} ya no es de este trabajador", claim.Id); lost = true; }
        catch (OperationCanceledException) when (cancelled) { outcome = new(StudioStatus.Cancelled, "cancelled"); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { outcome = new(StudioStatus.Interrupted, "interrupted"); }
        catch (OperationCanceledException) { outcome = new(StudioStatus.Failed, "limit_time", $"Se alcanzó el tiempo máximo de {options.MaxMinutes} minutos por mensaje."); }
        catch (AssistantProviderException ex) { run.Reserved = 0; outcome = new(StudioStatus.Failed, ex.Code); }
        catch (Exception ex)
        {
            log.LogError(ex, "Error no controlado en la conversación del Acompañante {Id}", claim.Id);
            outcome = new(StudioStatus.Failed, "error");
        }
        finally
        {
            await cts.CancelAsync();
            try { await renew; }
            catch (Exception ex) { log.LogWarning("La renovación del lease del Acompañante {Id} terminó con error: {Message}", claim.Id, ex.Message); }
        }
        if (outcome is not null && !lost)
        {
            try
            {
                if (outcome.Status == StudioStatus.Cancelled && run.Partial.Length > 0)
                {
                    var text = run.Partial.ToString();
                    await AppendAsync(run, false, [new StudioText(text)], false);
                    await StepAsync(run, "text", null, null, new JsonObject { ["text"] = StudioAgent.Cut(text, 8000), ["stopped"] = true }, "ok", StudioAgent.Cut(text, 400), 0);
                }
                using var deadline = new CancellationTokenSource(stopping.IsCancellationRequested ? TimeSpan.FromSeconds(5) : Timeout.InfiniteTimeSpan);
                await FinishAsync(run, outcome, deadline.Token);
            }
            catch (StudioLeaseLost) { log.LogWarning("La conversación del Acompañante {Id} cambió de dueño antes de cerrarse", claim.Id); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { log.LogWarning("La conversación del Acompañante {Id} no alcanzó a cerrarse al apagar", claim.Id); }
        }
    }

    private async Task<Outcome> LoopAsync(Run run, CancellationToken ct)
    {
        var claim = run.Claim;
        // Spec 049 (auditoría 9, D-01): con el Acompañante apagado no se llama al proveedor, aunque el turno ya estuviera reclamado.
        if (!options.Enabled) return new(StudioStatus.Failed, "disabled");
        var credential = await keys.GetAsync(ct);
        if (credential is null) return new(StudioStatus.Failed, "not_configured");
        var messages = await LoadAsync(run, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // H1 CA4 (delegación): la identidad y la empresa se recargan en cada paso; si la persona se desactiva, cambia su
            // contraseña o pierde la empresa de la conversación, el turno se detiene.
            run.User = await IdentityAsync(claim, ct);
            if (run.User is null) return new(StudioStatus.Failed, "identity", "La persona ya no tiene acceso con la misma cuenta o empresa.");

            if (run.Steps >= options.MaxSteps) return await LimitAsync(run, "limit_steps", $"La conversación llegó al tope de {options.MaxSteps} pasos.");
            var request = new StudioRequest(CompanionPrompt.System, messages, CompanionTools.Specs, options.MaxTokensPerCall);
            var chars = EstimateChars(request);
            var estimate = run.LastTokens + Math.Max(0, chars - run.LastChars);
            if (run.Tokens + estimate + options.MaxTokensPerCall > options.MaxTokens)
                return await LimitAsync(run, "limit_tokens", "La conversación es muy larga: empieza una nueva.");
            // Spec 049 (auditoría 9, D-03): una sola llamada que no cabe en la ventana del modelo se detiene aquí, con su mensaje.
            if (StudioOptions.EstimateInput(chars, run.LastInput, run.LastChars) > options.MaxInputTokens)
                return await LimitAsync(run, "limit_tokens", StudioOptions.TooLongMessage);
            var reserve = options.ReserveMicros(estimate);
            if (run.Cost + reserve > options.MaxCostMicros)
                return await LimitAsync(run, "limit_cost", $"La conversación llegó a su costo máximo ({options.MaxCostUsd} USD): empieza una nueva.");
            if (!await ReserveAsync(run, reserve, ct))
                return await LimitAsync(run, "limit_day", $"Llegaste al costo máximo del Acompañante del día ({options.CostPerDayUsd} USD).");

            run.Partial.Clear();
            await LiveAsync(run, new JsonObject { ["kind"] = "waiting" }, force: true);
            var reply = await llm.NextAsync(credential, request, delta => TextAsync(run, delta), ct);
            run.Steps++;
            run.Input += reply.InputTokens + reply.CacheReadTokens + reply.CacheWriteTokens;
            run.Output += reply.OutputTokens;
            run.Tokens = run.Input + run.Output;
            run.Cost += options.CostMicros(reply.InputTokens, reply.OutputTokens, reply.CacheReadTokens, reply.CacheWriteTokens);
            run.LastTokens = reply.InputTokens + reply.CacheReadTokens + reply.CacheWriteTokens + reply.OutputTokens;
            run.LastInput = reply.InputTokens + reply.CacheReadTokens + reply.CacheWriteTokens;
            run.LastChars = chars;
            await SettleAsync(run);

            var stop = reply.StopReason?.ToLowerInvariant();
            if (stop == "refusal") return new(StudioStatus.Failed, "refusal");
            messages.Add(new(false, reply.Content));
            await AppendAsync(run, false, reply.Content, false);
            run.Partial.Clear();
            foreach (var text in reply.Content.OfType<StudioText>().Where(t => !string.IsNullOrWhiteSpace(t.Text)))
            {
                run.LastText = text.Text;
                await StepAsync(run, "text", null, null, new JsonObject { ["text"] = StudioAgent.Cut(text.Text, 8000) }, "ok", StudioAgent.Cut(text.Text, 400), 0);
            }
            var uses = reply.Content.OfType<StudioToolUse>().ToList();
            if (uses.Count == 0)
            {
                if (stop == "max_tokens") return new(StudioStatus.Failed, "truncated");
                return new(StudioStatus.Answered, null, run.LastText is null ? null : StudioAgent.Cut(run.LastText, 4000));
            }

            var results = new List<StudioBlock>();
            var asked = false;
            foreach (var use in uses)
            {
                if (++run.ToolCalls > options.MaxToolCalls)
                    return await LimitAsync(run, "limit_tools", $"La conversación llegó al tope de {options.MaxToolCalls} consultas.");
                run.User = await IdentityAsync(claim, ct);
                if (run.User is null) return new(StudioStatus.Failed, "identity", "La persona ya no tiene acceso con la misma cuenta o empresa.");
                await LiveAsync(run, new JsonObject { ["kind"] = "tool", ["tool"] = StudioAgent.Cut(use.Name, 60), ["input"] = LiveInput(use.Input) }, force: true);
                var watch = Stopwatch.StartNew();
                var output = await tools.RunAsync(new CompanionContext(run.User, services, claim.Id, claim.Turn, claim.LeaseToken, use.Id), use.Name, use.Input, ct);
                watch.Stop();
                // H4: una propuesta es un paso «action» con su id, para que el panel muestre la tarjeta de confirmación.
                var kind = use.Name == CompanionTools.Propose && !output.IsError ? "action" : "tool";
                asked |= kind == "action";
                await StepAsync(run, kind, StudioAgent.Cut(use.Name, 60), StudioAgent.Cut(use.Input.GetRawText(), 8000), output.Data, output.Status, output.Summary, (int)watch.ElapsedMilliseconds);
                var label = CompanionTools.Find(use.Name)?.Name ?? "desconocida";
                results.Add(new StudioToolResult(use.Id, StudioPrompt.Untrusted(label, output.Data, options.MaxToolResultChars), output.IsError, label));
            }
            messages.Add(new(true, results));
            await AppendAsync(run, true, results, results.Any(IsData));
            // Spec 048 (H1 CA1): como el permiso de una herramienta, una propuesta guardada detiene el turno hasta que la persona
            // decida; su respuesta vuelve como el resultado de esta llamada (LoadAsync) cuando el turno se reanude.
            if (asked) return new(CompanionService.Waiting, null, run.LastText is null ? null : StudioAgent.Cut(run.LastText, 4000));
        }
    }

    private async Task<Outcome> LimitAsync(Run run, string code, string message)
    {
        await StepAsync(run, "limit", null, null, null, "error", message, 0);
        return new(StudioStatus.Failed, code, message);
    }

    /// <summary>Activa, con el mismo sello, que no sea de servicio y en la empresa de la conversación (040 D-07).</summary>
    private async Task<UserContext?> IdentityAsync(CompanionClaim claim, CancellationToken ct)
    {
        var user = await users.LoadAsync(claim.UserId, claim.SecurityStamp, ct);
        if (user is null || user.IsService) return null;
        if (claim.CompanyId is { } cid)
            return user.Companies.FirstOrDefault(c => c.Id == cid) is { } company ? user.WithCompany(company) : null;
        return user;
    }

    private static int EstimateChars(StudioRequest r) =>
        r.System.Length + r.Tools.Sum(t => t.Name.Length + t.Description.Length + t.InputSchema.ToJsonString().Length)
        + r.Messages.Sum(m => m.Blocks.Sum(b => b switch
        {
            StudioText t => t.Text.Length,
            StudioToolUse u => u.Input.GetRawText().Length + u.Name.Length,
            StudioToolResult x => x.Content.Length,
            StudioThinking t => t.Thinking.Length,
            _ => 0,
        }));

    private static bool IsData(StudioBlock b) => b is StudioToolResult { Tool: { } t } && CompanionTools.DataTools.Contains(t);

    // ---------- Escrituras con el lease ----------

    private async Task<bool?> RenewAsync(CompanionClaim claim)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        var rows = (await cn.QueryAsync<bool>("""
            UPDATE meta.CompanionConversation SET LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME())
            OUTPUT inserted.CancelRequested
             WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
            """, new { claim.Id, claim.LeaseToken, lease = options.LeaseSeconds })).ToList();
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>H6 CA1: reserva el peor caso de la siguiente llamada contra el costo del día de la persona (bloqueo por persona).</summary>
    private async Task<bool> ReserveAsync(Run run, long reserve, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await CompanionService.LockUserAsync(cn, tx, run.Claim.UserId);
        var (_, others) = await CompanionService.TodayAsync(cn, tx, run.Claim.UserId, run.Claim.Id);
        if (others + run.Cost + reserve > options.CostPerDayMicros) return false;
        if (await cn.ExecuteAsync("""
                UPDATE meta.CompanionConversation SET ReservedMicros = @reserve WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
                """, new { reserve, run.Claim.Id, run.Claim.LeaseToken }, tx) == 0)
            throw new StudioLeaseLost();
        await tx.CommitAsync(ct);
        run.Reserved = reserve;
        return true;
    }

    private async Task SettleAsync(Run run)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("""
                UPDATE meta.CompanionConversation SET Steps = @Steps, ToolCalls = @ToolCalls, InputTokens = @Input, OutputTokens = @Output,
                       CostMicros = @Cost, ReservedMicros = 0
                 WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
                """, new { run.Steps, run.ToolCalls, run.Input, run.Output, run.Cost, run.Claim.Id, run.Claim.LeaseToken }) == 0)
            throw new StudioLeaseLost();
        run.Reserved = 0;
    }

    private async Task StepAsync(Run run, string kind, string? tool, string? input, JsonNode? output, string status, string summary, int ms)
    {
        var seq = ++run.Seq;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("""
                SET XACT_ABORT ON; BEGIN TRAN;
                INSERT meta.CompanionStep (ConversationId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs, Turn)
                SELECT @Id, @seq, @kind, @tool, @input, @output, @status, @summary, @ms, @Turn
                 WHERE EXISTS (SELECT 1 FROM meta.CompanionConversation WITH (UPDLOCK) WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running');
                UPDATE meta.CompanionConversation SET ToolCalls = @ToolCalls, LiveJson = NULL WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running';
                COMMIT;
                """, new
            {
                run.Claim.Id, run.Claim.LeaseToken, run.Claim.Turn, seq, kind, tool, input, ms, status, run.ToolCalls,
                output = output is null ? null : StudioAgent.Cut(output.ToJsonString(), options.MaxToolResultChars),
                summary = StudioAgent.Cut(summary, 400),
            }) == 0)
            throw new StudioLeaseLost();
        signals.Pulse(run.Claim.Id);
    }

    private async Task<List<StudioMessage>> LoadAsync(Run run, CancellationToken ct)
    {
        List<(int Seq, bool FromUser, string ContentJson)> rows;
        await using (var cn = await db.OpenAsync(ct))
        {
            run.Seq = await cn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Seq), 0) FROM meta.CompanionStep WHERE ConversationId = @Id", new { run.Claim.Id });
            rows = (await cn.QueryAsync<(int, bool, string)>("SELECT Seq, FromUser, ContentJson FROM meta.CompanionMessage WHERE ConversationId = @Id ORDER BY Seq",
                new { run.Claim.Id })).ToList();
        }
        run.MessageSeq = rows.Count == 0 ? 0 : rows[^1].Seq;
        var outcomes = await OutcomesAsync(run.Claim.Id, ct);
        // Spec 049 (auditoría 9, D-02): el resultado de una propuesta se aplica después de normalizar, así que también reemplaza
        // el «el turno se interrumpió» que agrega Normalize cuando el turno salió antes de guardar los resultados de la respuesta.
        return StudioTranscript.Normalize(rows.Select(r => new StudioMessage(r.FromUser, StudioTranscript.Deserialize(r.ContentJson))))
            .Select(m => new StudioMessage(m.FromUser, m.Blocks.Select(b => b is StudioToolResult x && outcomes.TryGetValue(x.ToolUseId, out var text)
                ? new StudioToolResult(x.ToolUseId, text, false, x.Tool ?? CompanionTools.Propose) : b).ToList())).ToList();
    }

    /// <summary>
    /// Spec 048 (H1 CA4, CLARIFY 3): el resultado de cada propuesta ya decidida, por la llamada del modelo que la hizo. Se lee de
    /// la acción al armar la conversación (no se copia al historial): vale en este turno y en los siguientes, y la purga de lo
    /// leído no lo toca.
    /// </summary>
    private async Task<Dictionary<string, string>> OutcomesAsync(int conversationId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<(string ToolUseId, int Id, string Status, string EntityName, long? RecordId, string? ResultJson, string? Error, string? Reason)>("""
            SELECT ToolUseId, Id, Status, EntityName, RecordId, ResultJson, Error, Reason FROM meta.CompanionAction
             WHERE ConversationId = @conversationId AND ToolUseId IS NOT NULL
            """, new { conversationId });
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in rows)
            if (CompanionActions.Outcome(a.Id, a.Status, a.EntityName, a.RecordId, a.ResultJson, a.Error, a.Reason, options.MaxToolResultChars) is { } text)
                result[a.ToolUseId] = text;
        return result;
    }

    private async Task AppendAsync(Run run, bool fromUser, IReadOnlyList<StudioBlock> blocks, bool hasData)
    {
        if (blocks.Count == 0) return;
        var seq = ++run.MessageSeq;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("""
                INSERT meta.CompanionMessage (ConversationId, Seq, FromUser, ContentJson, HasData)
                SELECT @Id, @seq, @fromUser, @content, @hasData
                 WHERE EXISTS (SELECT 1 FROM meta.CompanionConversation WITH (UPDLOCK) WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running')
                """, new { run.Claim.Id, run.Claim.LeaseToken, seq, fromUser, content = StudioTranscript.Serialize(blocks), hasData }) == 0)
            throw new StudioLeaseLost();
    }

    private Task TextAsync(Run run, string delta)
    {
        run.Partial.Append(delta);
        if (Environment.TickCount64 - run.LiveAt < options.LiveMs) return Task.CompletedTask;
        return LiveAsync(run, new JsonObject { ["kind"] = "text", ["text"] = StudioAgent.Cut(run.Partial.Length > 8000 ? run.Partial.ToString(0, 8000) : run.Partial.ToString(), 8000) }, force: false);
    }

    private async Task LiveAsync(Run run, JsonObject live, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - run.LiveAt < options.LiveMs) return;
        run.LiveAt = now;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("UPDATE meta.CompanionConversation SET LiveJson = @live WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'",
                new { live = live.ToJsonString(), run.Claim.Id, run.Claim.LeaseToken }) == 0)
            throw new StudioLeaseLost();
        signals.Pulse(run.Claim.Id);
    }

    private static JsonNode LiveInput(JsonElement input)
    {
        var raw = input.GetRawText();
        return raw.Length <= 8000 ? JsonNode.Parse(raw) ?? new JsonObject() : JsonValue.Create(StudioAgent.Cut(raw, 8000))!;
    }

    /// <summary>El cierre del turno, su purga y su auditoría van en una transacción que exige el lease.</summary>
    private async Task FinishAsync(Run run, Outcome o, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Revisión adversarial 048 (F-01): «Detener» mientras el turno se detenía a esperar gana: queda detenida, sin reanudarse.
        var status = await cn.ExecuteScalarAsync<string?>("""
            UPDATE meta.CompanionConversation
               SET Status = CASE WHEN @Status = 'waiting' AND CancelRequested = 1 THEN 'cancelled' ELSE @Status END,
                   ErrorCode = CASE WHEN @Status = 'waiting' AND CancelRequested = 1 THEN 'cancelled' ELSE @ErrorCode END, Summary = @Summary,
                   Steps = @Steps, ToolCalls = @ToolCalls, InputTokens = @Input, OutputTokens = @Output,
                   CostMicros = @Cost, ReservedMicros = 0, FinishedAt = SYSUTCDATETIME(), LastActivityAt = SYSUTCDATETIME(),
                   LeaseToken = NULL, LeaseUntil = NULL, LiveJson = NULL
            OUTPUT inserted.Status
             WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
            """, new { o.Status, o.ErrorCode, o.Summary, run.Steps, run.ToolCalls, run.Input, run.Output,
                Cost = run.Cost + run.Reserved, run.Claim.Id, run.Claim.LeaseToken }, tx) ?? throw new StudioLeaseLost();
        // Spec 048: si la persona ya decidió todo mientras el turno terminaba (vio la tarjeta por el flujo), sigue de una vez
        // (spec 049, D-01: no con el Acompañante apagado; el trabajador la cierra).
        if (status == CompanionService.Waiting) { if (options.Enabled) await CompanionActions.ResumeAsync(cn, tx, run.Claim.Id); }
        // Spec 049 (auditoría 9, D-02): un turno que no termina esperando no deja propuestas confirmables (detener, un tope, la
        // identidad o el tiempo en una herramienta posterior de la misma respuesta). Nunca se le preguntaron a la persona.
        else await CompanionService.DiscardPendingAsync(cn, tx, run.Claim.Id,
            status == StudioStatus.Cancelled ? CompanionService.StoppedError : CompanionService.EndedError);
        // H2 CA4: lo leído de registros no se queda al terminar el turno, ni en los pasos ni en el historial.
        await CompanionService.PurgeDataAsync(cn, tx, run.Claim.Id);
        await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.CompanionFinish, entity: "companion", recordId: run.Claim.Id.ToString(),
            after: new { turn = run.Claim.Turn, status = o.Status, errorCode = o.ErrorCode, steps = run.Steps, toolCalls = run.ToolCalls, inputTokens = run.Input,
                outputTokens = run.Output, costUsd = (run.Cost + run.Reserved) / 1_000_000m, model = assistant.Model },
            userId: run.Claim.UserId, username: run.User?.Username ?? await cn.ExecuteScalarAsync<string>("SELECT Username FROM meta.[User] WHERE Id = @UserId", new { run.Claim.UserId }, tx));
        await tx.CommitAsync(ct);
        signals.Pulse(run.Claim.Id);
    }
}
