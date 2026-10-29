using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Assistant;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Studio;

public static class StudioStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    /// <summary>Entregó un plan válido contra el modelo real: la persona lo revisa y lo aplica.</summary>
    public const string Proposed = "proposed";
    /// <summary>Terminó sin plan (una respuesta, o el pedido no era un cambio al modelo).</summary>
    public const string Answered = "answered";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    /// <summary>Su trabajador dejó de renovar el lease (caída): no se repite (CLARIFY 4).</summary>
    public const string Interrupted = "interrupted";
    public const string Applied = "applied";
    public const string Discarded = "discarded";

    public static bool Active(string s) => s is Queued or Running;
}

/// <summary>Otro trabajador tomó la sesión (o se cerró): lo que este haga ya no se escribe.</summary>
public sealed class StudioLeaseLost : Exception;

/// <summary>
/// Sesión reclamada por un trabajador (lo que leyó al reclamarla). Spec 036: un turno de la conversación, con los contadores
/// acumulados de los turnos anteriores (los topes son de la conversación) y la marca de la copia aislada que dejó.
/// </summary>
public sealed record StudioClaim(int Id, int UserId, int? CompanyId, Guid SecurityStamp, string Prompt, string Scopes, Guid LeaseToken, DateTime StartedAt)
{
    public int Turn { get; init; } = 1;
    public DateTime TurnStartedAt { get; init; }
    public int Steps { get; init; }
    public int ToolCalls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CostMicros { get; init; }
    public Guid? SandboxToken { get; init; }

    public IReadOnlySet<string> ScopeSet => StudioService.ParseScopes(Scopes);
}

/// <summary>
/// Spec 033 (H2): el bucle agéntico de una sesión. Antes de cada llamada al modelo comprueba los topes y reserva el peor caso
/// del costo; cada paso y el cierre se escriben solo si el lease sigue siendo de este trabajador. Nunca aplica nada al
/// entorno real: la entrega queda como propuesta para la persona.
/// </summary>
public sealed class StudioAgent(Db db, StudioOptions options, AssistantOptions assistant, AssistantKeys keys, IStudioModel llm,
    StudioToolRunner tools, SandboxFactory sandboxes, UserContextLoader users, MetadataStore store, IServiceProvider services,
    StudioSignals signals, JsonSerializerOptions json, ILogger<StudioAgent> log)
{
    private sealed class Run(StudioClaim claim)
    {
        public StudioClaim Claim { get; } = claim;
        public int Seq;
        /// <summary>Spec 036: último mensaje del historial (meta.StudioMessage).</summary>
        public int MessageSeq;
        // Spec 036 (H2 CA1): los contadores siguen los de la conversación.
        public int Steps = claim.Steps;
        public int ToolCalls = claim.ToolCalls;
        public long Tokens = claim.InputTokens + claim.OutputTokens;
        public long Cost = claim.CostMicros;
        /// <summary>
        /// Spec 034 (A-05): lo reservado para la llamada en curso (0 al liquidarla). Si la sesión se corta a mitad de la
        /// llamada, el proveedor la cobra igual: el cierre lo suma al costo.
        /// </summary>
        public long Reserved;
        public long Input = claim.InputTokens;
        public long Output = claim.OutputTokens;
        /// <summary>Spec 036 (H5): el texto de la respuesta en curso, para el estado en vivo y para conservarlo si se detiene.</summary>
        public readonly System.Text.StringBuilder Partial = new();
        public long LiveAt;
        public Sandbox? Sandbox;
        /// <summary>Spec 034 (A-06): por qué no se pudo preparar la copia; las demás herramientas lo responden sin reintentar.</summary>
        public string? SandboxError;
        public UserContext? User;
        public string? LastText;
        /// <summary>Tokens de entrada y salida medidos en la llamada anterior y los caracteres que llevaba (F-02).</summary>
        public long LastTokens;
        public long LastChars;
    }

    private sealed record Outcome(string Status, string? ErrorCode, string? Summary = null, string? PlanJson = null, string? ReportJson = null);

    public async Task RunAsync(StudioClaim claim, CancellationToken stopping)
    {
        var run = new Run(claim);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        // Spec 036: los minutos son del turno.
        var remaining = claim.TurnStartedAt.AddMinutes(options.MaxMinutes) - DateTime.UtcNow;
        cts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var lost = false;
        var cancelled = false;
        // H5 CA4: «Detener» corta aquí mismo si el turno corre en este proceso (si no, al renovar el lease).
        using var registration = signals.Run(claim.Id, claim.LeaseToken, () => { cancelled = true; cts.Cancel(); });
        var renew = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.RenewSeconds), cts.Token);
                    bool? state;
                    // F-03: una falla pasajera de la base no detiene la renovación; si dura, el lease vence y otra vuelta lo ve.
                    try { state = await RenewAsync(claim); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning("No se pudo renovar el lease de la sesión de Studio {Id}: {Message}", claim.Id, ex.Message);
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
        catch (OperationCanceledException) when (lost) { log.LogWarning("La sesión de Studio {Id} la tomó otro trabajador", claim.Id); }
        catch (StudioLeaseLost) { log.LogWarning("La sesión de Studio {Id} ya no es de este trabajador", claim.Id); lost = true; }
        catch (OperationCanceledException) when (cancelled) { outcome = new(StudioStatus.Cancelled, "cancelled"); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { /* apagado: el lease vence y queda interrumpida */ }
        catch (OperationCanceledException) { outcome = new(StudioStatus.Failed, "limit_time", Summary: $"Se alcanzó el tiempo máximo de {options.MaxMinutes} minutos."); }
        // Revisión adversarial 034 (F-03): el proveedor respondió con error (clave, sobrecarga): esa llamada no se factura.
        catch (AssistantProviderException ex) { run.Reserved = 0; outcome = new(StudioStatus.Failed, ex.Code); }
        catch (Exception ex)
        {
            log.LogError(ex, "Error no controlado en la sesión de Studio {Id}", claim.Id);
            outcome = new(StudioStatus.Failed, "error");
        }
        finally
        {
            await cts.CancelAsync();
            if (run.Sandbox is { } sb)
            {
                await sb.DisposeAsync();
                // Spec 036 (H4 CA1): la copia se conserva para el turno siguiente; se borra si este trabajador ya no es dueño
                // o si el turno no se cierra aquí (apagado: el vencimiento la marca interrumpida).
                // Revisión adversarial 036 (F-03): solo si la copia sigue siendo la suya (marca), no la del turno siguiente.
                if (lost || outcome is null) await sandboxes.DropIfOwnedAsync(sb.Database, sb.Token);
            }
            try { await renew; }
            catch (Exception ex) { log.LogWarning("La renovación del lease de la sesión de Studio {Id} terminó con error: {Message}", claim.Id, ex.Message); }
        }
        if (outcome is not null && !lost)
        {
            try
            {
                // H5 CA4: lo que el modelo alcanzó a escribir antes de detenerlo queda como paso y en el historial.
                if (outcome.Status == StudioStatus.Cancelled && run.Partial.Length > 0)
                {
                    var text = run.Partial.ToString();
                    await AppendAsync(run, false, [new StudioText(text)], false);
                    await StepAsync(run, "text", null, null, new JsonObject { ["text"] = Cut(text, 8000), ["stopped"] = true }, "ok", Cut(text, 400), 0);
                }
                await FinishAsync(run, outcome);
            }
            catch (StudioLeaseLost) { log.LogWarning("La sesión de Studio {Id} cambió de dueño antes de cerrarse", claim.Id); }
        }
    }

    private async Task<Outcome> LoopAsync(Run run, CancellationToken ct)
    {
        var claim = run.Claim;
        var scopes = claim.ScopeSet;
        var credential = await keys.GetAsync(ct);
        if (credential is null) return new(StudioStatus.Failed, "not_configured");
        var specs = StudioTools.All.Where(t => scopes.Contains(t.Scope)).Select(t => new StudioToolSpec(t.Name, t.Description, t.InputSchema)).ToList();
        var system = StudioPrompt.System(scopes);
        var messages = await LoadAsync(run, ct);
        var context = new ToolContext
        {
            User = null!, Scopes = scopes, Services = services,
            SandboxAsync = token => SandboxOnceAsync(run, token),
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // H1 CA3: la identidad se recarga en cada paso: si la persona se desactiva, cambia su contraseña o deja de ser
            // modeladora, la sesión se detiene.
            run.User = await IdentityAsync(claim, ct);
            if (run.User is null) return new(StudioStatus.Failed, "identity", Summary: "La persona que pidió la sesión ya no tiene acceso.");
            context = new ToolContext { User = run.User, Scopes = scopes, Services = services, SandboxAsync = context.SandboxAsync };

            if (run.Steps >= options.MaxSteps) return await LimitAsync(run, "limit_steps", $"Se alcanzó el tope de {options.MaxSteps} pasos.");
            var request = new StudioRequest(system, messages, specs, options.MaxTokensPerCall);
            // F-02: entrada de la siguiente llamada = lo medido en la anterior (entrada y salida, que vuelve como historial)
            // más un token por cada carácter nuevo; el tope de tokens cuenta también la salida completa de esta llamada.
            var chars = EstimateChars(request);
            var estimate = run.LastTokens + Math.Max(0, chars - run.LastChars);
            if (run.Tokens + estimate + options.MaxTokensPerCall > options.MaxTokens)
                return await LimitAsync(run, "limit_tokens", $"La siguiente llamada podría rebasar el tope de {options.MaxTokens:N0} tokens de la sesión.");
            var reserve = options.ReserveMicros(estimate);
            if (run.Cost + reserve > options.MaxCostMicros)
                return await LimitAsync(run, "limit_cost", $"La siguiente llamada podría rebasar el costo máximo de la sesión ({options.MaxCostUsd} USD).");
            if (!await ReserveAsync(run, reserve, ct))
                return await LimitAsync(run, "limit_day", $"La siguiente llamada podría rebasar tu costo máximo del día ({options.CostPerDayUsd} USD).");

            run.Partial.Clear();
            await LiveAsync(run, new JsonObject { ["kind"] = "waiting" }, force: true);
            var reply = await llm.NextAsync(credential, request, delta => TextAsync(run, delta), ct);
            run.Steps++;
            run.Input += reply.InputTokens + reply.CacheReadTokens + reply.CacheWriteTokens;
            run.Output += reply.OutputTokens;
            run.Tokens = run.Input + run.Output;
            run.Cost += options.CostMicros(reply.InputTokens, reply.OutputTokens, reply.CacheReadTokens, reply.CacheWriteTokens);
            run.LastTokens = reply.InputTokens + reply.CacheReadTokens + reply.CacheWriteTokens + reply.OutputTokens;
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
                await StepAsync(run, "text", null, null, new JsonObject { ["text"] = Cut(text.Text, 8000) }, "ok", Cut(text.Text, 400), 0);
            }
            var uses = reply.Content.OfType<StudioToolUse>().ToList();
            if (uses.Count == 0)
            {
                if (stop == "max_tokens") return new(StudioStatus.Failed, "truncated");
                // Spec 036 (CLARIFY 7): en un chat, contestar es un final válido del turno.
                return new(StudioStatus.Answered, null, Summary: run.LastText is null ? null : Cut(run.LastText, 4000));
            }

            var results = new List<StudioBlock>();
            foreach (var use in uses)
            {
                if (use.Name == StudioTools.Deliver)
                {
                    var (done, result) = await DeliverAsync(run, use, ct);
                    if (done is not null)
                    {
                        // El historial queda cerrado: el uso de «entregar» tiene su resultado para el turno siguiente.
                        results.Add(new StudioToolResult(use.Id, StudioPrompt.Delivered, false, StudioTools.Deliver));
                        await AppendAsync(run, true, results, results.Any(IsData));
                        return done;
                    }
                    results.Add(result!);
                    continue;
                }
                if (++run.ToolCalls > options.MaxToolCalls)
                    return await LimitAsync(run, "limit_tools", $"Se alcanzó el tope de {options.MaxToolCalls} herramientas.");
                // También antes de cada herramienta: una respuesta del modelo puede traer varias y tardar.
                run.User = await IdentityAsync(claim, ct);
                if (run.User is null) return new(StudioStatus.Failed, "identity", Summary: "La persona que pidió la sesión ya no tiene acceso.");
                context = new ToolContext { User = run.User, Scopes = scopes, Services = services, SandboxAsync = context.SandboxAsync };
                await LiveAsync(run, new JsonObject { ["kind"] = "tool", ["tool"] = Cut(use.Name, 60), ["input"] = LiveInput(use.Input) }, force: true);
                var watch = Stopwatch.StartNew();
                var output = await tools.RunAsync(context, use.Name, use.Input, ct);
                watch.Stop();
                await StepAsync(run, "tool", Cut(use.Name, 60), Cut(use.Input.GetRawText(), 8000), output.Data, output.Status, output.Summary, (int)watch.ElapsedMilliseconds);
                // El atributo del delimitador solo lleva nombres del catálogo, nunca lo que escribió el modelo.
                var label = StudioTools.Find(use.Name)?.Name ?? "desconocida";
                results.Add(new StudioToolResult(use.Id, StudioPrompt.Untrusted(label, output.Data, options.MaxToolResultChars), output.IsError, label));
            }
            messages.Add(new(true, results));
            await AppendAsync(run, true, results, results.Any(IsData));
        }
    }

    /// <summary>
    /// H4 CA1: la entrega se compila contra el modelo real. Un plan que no compila vuelve al modelo como error de la
    /// herramienta (para que lo corrija); uno válido, o ninguno, cierra la sesión.
    /// </summary>
    private async Task<(Outcome? Done, StudioBlock? Result)> DeliverAsync(Run run, StudioToolUse use, CancellationToken ct)
    {
        string? error = null;
        ChangePlan? plan = null;
        string? planJson = null;
        string summary = "";
        JsonObject report = [];
        try
        {
            var input = JsonNode.Parse(use.Input.GetRawText()) as JsonObject ?? throw new ToolInputException("La entrada debe ser un objeto JSON.");
            summary = StudioTools.Req(input, "summary", 4000);
            report = new JsonObject
            {
                // F-04: una prueba puede llegar como texto suelto; no debe tumbar la entrega.
                ["tested"] = new JsonArray((input["tested"] as JsonArray ?? []).Take(50).Select(t => (JsonNode)(t is JsonObject o
                    ? new JsonObject
                    {
                        ["description"] = Cut(o["description"]?.ToString() ?? "", 500),
                        ["ok"] = o["ok"]?.GetValueKind() == JsonValueKind.True,
                        ["detail"] = o["detail"] is { } d ? Cut(d.ToString(), 1000) : null,
                    }
                    : new JsonObject { ["description"] = Cut(t?.ToString() ?? "", 500), ["ok"] = false })).ToArray()),
                ["failures"] = Texts(input["failures"]),
                ["risks"] = Texts(input["risks"]),
            };
            if (input["plan"] is JsonObject p)
            {
                if ((p["operations"] as JsonArray)?.Count is not > 0) throw new ToolInputException("El plan no tiene operaciones: omite «plan» si no propones cambios.");
                if ((p["operations"] as JsonArray)!.Count > StudioTools.MaxOperations) throw new ToolInputException($"El plan admite hasta {StudioTools.MaxOperations} operaciones.");
                try { plan = p.Deserialize<ChangePlan>(json); }
                catch (JsonException ex) { throw new ToolInputException("El plan no es un Plan de Cambios válido: " + Cut(ex.Message, 300)); }
                plan = plan! with { BaseHash = null, BaseVersions = null };
                var model = await store.GetAsync(ct);
                IReadOnlySet<string> roles;
                await using (var cn = await db.OpenAsync(ct)) roles = await PlanService.LoadRolesAsync(cn, null);
                var preview = PlanCompiler.Compile(plan, model, roles, await store.GetDashboardsAsync(ct), await store.GetAppsAsync(ct)).Preview;
                if (!preview.Valid)
                    error = "El plan no compila contra el modelo real; corrígelo y vuelve a entregar:\n- "
                            + string.Join("\n- ", preview.Errors.Take(30).Select(e => e.Operation >= 0 ? $"Operación {e.Operation + 1}: {e.Message}" : e.Message));
                else planJson = JsonSerializer.Serialize(plan, json);
            }
        }
        catch (ToolInputException ex) { error = ex.Message; }

        await StepAsync(run, "deliver", StudioTools.Deliver, Cut(use.Input.GetRawText(), 8000),
            error is null ? new JsonObject { ["summary"] = summary } : new JsonObject { ["error"] = error }, error is null ? "ok" : "error",
            Cut(error ?? summary, 400), 0);
        // F-05: los errores del compilador citan etiquetas del modelo (texto de otras personas): también van delimitados.
        if (error is not null)
            return (null, new StudioToolResult(use.Id, StudioPrompt.Untrusted(StudioTools.Deliver, new JsonObject { ["error"] = error }, options.MaxToolResultChars), true));
        return (new(planJson is null ? StudioStatus.Answered : StudioStatus.Proposed, null, summary, planJson, report.ToJsonString()), null);
    }

    private static JsonArray Texts(JsonNode? n) =>
        new((n as JsonArray ?? []).Take(50).Select(x => (JsonNode)JsonValue.Create(Cut(x?.ToString() ?? "", 1000))!).ToArray());

    private async Task<Outcome> LimitAsync(Run run, string code, string message)
    {
        await StepAsync(run, "limit", null, null, null, "error", message, 0);
        return new(StudioStatus.Failed, code, Summary: message);
    }

    private async Task<UserContext?> IdentityAsync(StudioClaim claim, CancellationToken ct)
    {
        var user = await users.LoadAsync(claim.UserId, claim.SecurityStamp, ct);
        if (user is null || user.IsService || !user.CanManageModel) return null;
        if (claim.CompanyId is { } cid)
            return user.Companies.FirstOrDefault(c => c.Id == cid) is { } company ? user.WithCompany(company) : null;
        return user;
    }

    /// <summary>
    /// Spec 034 (A-06): la copia se prepara una vez por sesión. Si falla, la falla queda en la corrida y las siguientes
    /// herramientas aisladas la responden sin crear otra base (cancelar no cuenta como falla: la sesión ya termina).
    /// </summary>
    private async Task<Sandbox> SandboxOnceAsync(Run run, CancellationToken ct)
    {
        if (run.Sandbox is { } ready) return ready;
        if (run.SandboxError is { } error) throw new SandboxUnavailable(error);
        try { return run.Sandbox = await CreateSandboxAsync(run, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (StudioLeaseLost) { throw; }
        catch (Exception ex)
        {
            run.SandboxError = (ex is SandboxUnavailable ? ex.Message : "No se pudo preparar la copia aislada.")
                               + " En esta sesión no se vuelve a intentar: entrega sin probar en la copia.";
            throw;
        }
    }

    private async Task<Sandbox> CreateSandboxAsync(Run run, CancellationToken ct)
    {
        if (!options.Sandbox) throw new SandboxUnavailable("El entorno aislado está deshabilitado en este servidor (Studio:Sandbox).");
        // Spec 036 (H4 CA1): la copia que dejó un turno anterior de esta conversación se reutiliza si su marca coincide.
        if (run.Claim.SandboxToken is { } previous && await sandboxes.OpenAsync(run.Claim.Id, previous, ct) is { } reopened)
            return reopened;
        // El nombre y la marca quedan en la sesión antes de crear la base: si el proceso cae, el barrido la encuentra.
        var token = Guid.NewGuid();
        await using (var cn = await db.OpenAsync(ct))
            if (await cn.ExecuteAsync("UPDATE meta.StudioSession SET SandboxDb = @name, SandboxToken = @token WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'",
                    new { name = sandboxes.NameFor(run.Claim.Id), token, run.Claim.Id, run.Claim.LeaseToken }) == 0)
                throw new StudioLeaseLost();
        var watch = Stopwatch.StartNew();
        var sandbox = await sandboxes.CreateAsync(run.Claim.Id, token, ct);
        await StepAsync(run, "tool", "aislado_preparar", null, new JsonObject { ["database"] = "copia aislada" }, "ok",
            $"Copia aislada lista en {watch.Elapsed.TotalSeconds:N1} s", (int)watch.ElapsedMilliseconds);
        return sandbox;
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

    // ---------- Escrituras con el lease ----------

    /// <summary>Renueva el lease. Null si ya no es de este trabajador; true si la persona pidió cancelar.</summary>
    private async Task<bool?> RenewAsync(StudioClaim claim)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        var rows = (await cn.QueryAsync<bool>("""
            UPDATE meta.StudioSession SET LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME())
            OUTPUT inserted.CancelRequested
             WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
            """, new { claim.Id, claim.LeaseToken, lease = options.LeaseSeconds })).ToList();
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// H2 CA2: reserva el peor caso de la siguiente llamada contra el costo del día de la persona, con un bloqueo por
    /// usuario (el mismo que al pedir una sesión). Devuelve false si no cabe.
    /// </summary>
    private async Task<bool> ReserveAsync(Run run, long reserve, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await StudioService.LockUserAsync(cn, tx, run.Claim.UserId);
        // Revisión adversarial 036 (F-01): las demás conversaciones con actividad en 24 horas, no solo las creadas.
        var (_, others) = await StudioService.TodayAsync(cn, tx, run.Claim.UserId, run.Claim.Id);
        if (others + run.Cost + reserve > options.CostPerDayMicros) return false;
        if (await cn.ExecuteAsync("""
                UPDATE meta.StudioSession SET ReservedMicros = @reserve WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
                """, new { reserve, run.Claim.Id, run.Claim.LeaseToken }, tx) == 0)
            throw new StudioLeaseLost();
        await tx.CommitAsync(ct);
        run.Reserved = reserve; // revisión adversarial 034 (F-03): solo lo que quedó guardado
        return true;
    }

    private async Task SettleAsync(Run run)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("""
                UPDATE meta.StudioSession SET Steps = @Steps, ToolCalls = @ToolCalls, InputTokens = @Input, OutputTokens = @Output,
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
        // H2 CA5: el paso solo se escribe si la sesión sigue siendo de este trabajador.
        // Spec 036: el paso lleva su turno y borra el estado en vivo en la misma transacción (el flujo no ve los dos a la vez).
        if (await cn.ExecuteAsync("""
                SET XACT_ABORT ON; BEGIN TRAN;
                INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs, Turn)
                SELECT @Id, @seq, @kind, @tool, @input, @output, @status, @summary, @ms, @Turn
                 WHERE EXISTS (SELECT 1 FROM meta.StudioSession WITH (UPDLOCK) WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running');
                UPDATE meta.StudioSession SET ToolCalls = @ToolCalls, LiveJson = NULL WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running';
                COMMIT;
                """, new
            {
                run.Claim.Id, run.Claim.LeaseToken, run.Claim.Turn, seq, kind, tool, input, ms, status, run.ToolCalls,
                output = output is null ? null : Cut(output.ToJsonString(), options.MaxToolResultChars),
                summary = Cut(summary, 400),
            }) == 0)
            throw new StudioLeaseLost();
        signals.Pulse(run.Claim.Id);
    }

    // ---------- Spec 036: historial y estado en vivo ----------

    /// <summary>
    /// El historial de la conversación, normalizado (CLARIFY 2), y los últimos números de paso y de mensaje. Una sesión sin
    /// historial (pedida antes de la 036 y aún en cola) empieza con su pedido, que también se guarda.
    /// </summary>
    private async Task<List<StudioMessage>> LoadAsync(Run run, CancellationToken ct)
    {
        List<(int Seq, bool FromUser, string ContentJson)> rows;
        await using (var cn = await db.OpenAsync(ct))
        {
            run.Seq = await cn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Seq), 0) FROM meta.StudioStep WHERE SessionId = @Id", new { run.Claim.Id });
            rows = (await cn.QueryAsync<(int, bool, string)>("SELECT Seq, FromUser, ContentJson FROM meta.StudioMessage WHERE SessionId = @Id ORDER BY Seq",
                new { run.Claim.Id })).ToList();
        }
        run.MessageSeq = rows.Count == 0 ? 0 : rows[^1].Seq;
        if (rows.Count == 0)
        {
            StudioBlock[] first = [new StudioText(StudioPrompt.Request(run.Claim.Prompt))];
            await AppendAsync(run, true, first, false);
            return [new(true, first)];
        }
        return StudioTranscript.Normalize(rows.Select(r => new StudioMessage(r.FromUser, StudioTranscript.Deserialize(r.ContentJson))));
    }

    /// <summary>Agrega un mensaje al historial, solo si el lease sigue siendo de este trabajador.</summary>
    private async Task AppendAsync(Run run, bool fromUser, IReadOnlyList<StudioBlock> blocks, bool hasData)
    {
        if (blocks.Count == 0) return;
        var seq = ++run.MessageSeq;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("""
                INSERT meta.StudioMessage (SessionId, Seq, FromUser, ContentJson, HasData)
                SELECT @Id, @seq, @fromUser, @content, @hasData
                 WHERE EXISTS (SELECT 1 FROM meta.StudioSession WITH (UPDLOCK) WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running')
                """, new { run.Claim.Id, run.Claim.LeaseToken, seq, fromUser, content = StudioTranscript.Serialize(blocks), hasData }) == 0)
            throw new StudioLeaseLost();
    }

    private static bool IsData(StudioBlock b) => b is StudioToolResult { Tool: { } t } && StudioTools.DataTools.Contains(t);

    private Task TextAsync(Run run, string delta)
    {
        run.Partial.Append(delta);
        // Revisión adversarial 036 (F-09): el texto se arma solo cuando toca escribirlo.
        if (Environment.TickCount64 - run.LiveAt < options.LiveMs) return Task.CompletedTask;
        return LiveAsync(run, new JsonObject { ["kind"] = "text", ["text"] = Cut(run.Partial.Length > 8000 ? run.Partial.ToString(0, 8000) : run.Partial.ToString(), 8000) }, force: false);
    }

    /// <summary>
    /// H5 CA1: el estado en vivo (esperando al modelo, texto parcial, herramienta en curso), a lo más cada
    /// <c>Studio:LiveMs</c> salvo los cambios de clase (<paramref name="force"/>). Con el lease, y avisa al flujo.
    /// </summary>
    private async Task LiveAsync(Run run, JsonObject live, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - run.LiveAt < options.LiveMs) return;
        run.LiveAt = now;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        if (await cn.ExecuteAsync("UPDATE meta.StudioSession SET LiveJson = @live WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'",
                new { live = live.ToJsonString(), run.Claim.Id, run.Claim.LeaseToken }) == 0)
            throw new StudioLeaseLost();
        signals.Pulse(run.Claim.Id);
    }

    private static JsonNode LiveInput(JsonElement input)
    {
        var raw = input.GetRawText();
        return raw.Length <= 8000 ? JsonNode.Parse(raw) ?? new JsonObject() : JsonValue.Create(Cut(raw, 8000))!;
    }

    /// <summary>H2 CA5: el cierre y su auditoría van en una transacción que exige el lease.</summary>
    private async Task FinishAsync(Run run, Outcome o)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        await using var tx = await cn.BeginTransactionAsync();
        // Spec 036 (H3 CA1): una entrega nueva reemplaza la propuesta vigente (PlanStatus 'proposed'); un turno sin plan no la
        // toca, y si contesta sin plan con una propuesta vigente, la conversación sigue mostrándola (estado y resumen).
        var n = await cn.ExecuteAsync("""
            UPDATE meta.StudioSession
               SET Status = CASE WHEN @PlanJson IS NULL AND @Status = 'answered' AND PlanStatus = 'proposed' THEN 'proposed' ELSE @Status END,
                   Summary = CASE WHEN @PlanJson IS NULL AND @Status = 'answered' AND PlanStatus = 'proposed' THEN Summary ELSE @Summary END,
                   ErrorCode = @ErrorCode,
                   PlanJson = CASE WHEN @PlanJson IS NULL THEN PlanJson ELSE @PlanJson END,
                   -- Revisión adversarial 036 (F-06): un turno sin plan (también uno fallido) no borra el informe de la propuesta vigente.
                   ReportJson = CASE WHEN @PlanJson IS NULL AND PlanStatus = 'proposed' THEN ReportJson ELSE @ReportJson END,
                   PlanStatus = CASE WHEN @PlanJson IS NULL THEN PlanStatus ELSE 'proposed' END,
                   Steps = @Steps, ToolCalls = @ToolCalls, InputTokens = @Input, OutputTokens = @Output,
                   CostMicros = @Cost, ReservedMicros = 0, FinishedAt = SYSUTCDATETIME(), LastActivityAt = SYSUTCDATETIME(),
                   LeaseToken = NULL, LeaseUntil = NULL, LiveJson = NULL
             WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
            """, new { o.Status, o.ErrorCode, o.Summary, o.PlanJson, o.ReportJson, run.Steps, run.ToolCalls, run.Input, run.Output,
                // A-05: la llamada cortada (cancelar, tiempo máximo, error del proveedor) se cobra como su reserva, el peor caso.
                Cost = run.Cost + run.Reserved, run.Claim.Id, run.Claim.LeaseToken }, tx);
        if (n == 0) throw new StudioLeaseLost();
        // Spec 035 (R-033-4) y 036 (H4 CA3): los registros que leyó el agente no se quedan al terminar el turno, ni en los
        // pasos ni en el historial que verá el modelo.
        await StudioTools.PurgeDataAsync(cn, tx, run.Claim.Id);
        await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.StudioFinish, entity: "studio", recordId: run.Claim.Id.ToString(),
            after: new { turn = run.Claim.Turn, status = o.Status, errorCode = o.ErrorCode, steps = run.Steps, toolCalls = run.ToolCalls, inputTokens = run.Input,
                outputTokens = run.Output, costUsd = (run.Cost + run.Reserved) / 1_000_000m, model = assistant.Model },
            userId: run.Claim.UserId, username: run.User?.Username ?? await cn.ExecuteScalarAsync<string>("SELECT Username FROM meta.[User] WHERE Id = @UserId", new { run.Claim.UserId }, tx));
        await tx.CommitAsync();
        signals.Pulse(run.Claim.Id);
    }

    internal static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
