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

/// <summary>Sesión reclamada por un trabajador (lo que leyó al reclamarla).</summary>
public sealed record StudioClaim(int Id, int UserId, int? CompanyId, Guid SecurityStamp, string Prompt, string Scopes, Guid LeaseToken, DateTime StartedAt)
{
    public IReadOnlySet<string> ScopeSet => StudioService.ParseScopes(Scopes);
}

/// <summary>
/// Spec 033 (H2): el bucle agéntico de una sesión. Antes de cada llamada al modelo comprueba los topes y reserva el peor caso
/// del costo; cada paso y el cierre se escriben solo si el lease sigue siendo de este trabajador. Nunca aplica nada al
/// entorno real: la entrega queda como propuesta para la persona.
/// </summary>
public sealed class StudioAgent(Db db, StudioOptions options, AssistantOptions assistant, AssistantKeys keys, IStudioModel llm,
    StudioToolRunner tools, SandboxFactory sandboxes, UserContextLoader users, MetadataStore store, IServiceProvider services,
    JsonSerializerOptions json, ILogger<StudioAgent> log)
{
    private sealed class Run(StudioClaim claim)
    {
        public StudioClaim Claim { get; } = claim;
        public int Seq;
        public int Steps;
        public int ToolCalls;
        public long Tokens;
        public long Cost;
        public long Input;
        public long Output;
        public Sandbox? Sandbox;
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
        var remaining = claim.StartedAt.AddMinutes(options.MaxMinutes) - DateTime.UtcNow;
        cts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var lost = false;
        var cancelled = false;
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
        catch (AssistantProviderException ex) { outcome = new(StudioStatus.Failed, ex.Code); }
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
                await sandboxes.DropAsync(sb.Database);
            }
            try { await renew; }
            catch (Exception ex) { log.LogWarning("La renovación del lease de la sesión de Studio {Id} terminó con error: {Message}", claim.Id, ex.Message); }
        }
        if (outcome is not null && !lost)
        {
            try { await FinishAsync(run, outcome); }
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
        var messages = new List<StudioMessage> { new(true, [new StudioText(StudioPrompt.Request(claim.Prompt))]) };
        var nudged = false;
        var context = new ToolContext
        {
            User = null!, Scopes = scopes, Services = services,
            SandboxAsync = async token => run.Sandbox ??= await CreateSandboxAsync(run, token),
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

            var reply = await llm.NextAsync(credential, request, ct);
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
            foreach (var text in reply.Content.OfType<StudioText>().Where(t => !string.IsNullOrWhiteSpace(t.Text)))
            {
                run.LastText = text.Text;
                await StepAsync(run, "text", null, null, new JsonObject { ["text"] = Cut(text.Text, 8000) }, "ok", Cut(text.Text, 400), 0);
            }
            var uses = reply.Content.OfType<StudioToolUse>().ToList();
            if (uses.Count == 0)
            {
                if (stop == "max_tokens") return new(StudioStatus.Failed, "truncated");
                if (nudged) return new(StudioStatus.Answered, null, Summary: run.LastText is null ? null : Cut(run.LastText, 4000));
                nudged = true;
                messages.Add(new(true, [new StudioText(StudioPrompt.Nudge)]));
                continue;
            }

            var results = new List<StudioBlock>();
            foreach (var use in uses)
            {
                if (use.Name == StudioTools.Deliver)
                {
                    var (done, result) = await DeliverAsync(run, use, ct);
                    if (done is not null) return done;
                    results.Add(result!);
                    continue;
                }
                if (++run.ToolCalls > options.MaxToolCalls)
                    return await LimitAsync(run, "limit_tools", $"Se alcanzó el tope de {options.MaxToolCalls} herramientas.");
                // También antes de cada herramienta: una respuesta del modelo puede traer varias y tardar.
                run.User = await IdentityAsync(claim, ct);
                if (run.User is null) return new(StudioStatus.Failed, "identity", Summary: "La persona que pidió la sesión ya no tiene acceso.");
                context = new ToolContext { User = run.User, Scopes = scopes, Services = services, SandboxAsync = context.SandboxAsync };
                var watch = Stopwatch.StartNew();
                var output = await tools.RunAsync(context, use.Name, use.Input, ct);
                watch.Stop();
                await StepAsync(run, "tool", Cut(use.Name, 60), Cut(use.Input.GetRawText(), 8000), output.Data, output.Status, output.Summary, (int)watch.ElapsedMilliseconds);
                // El atributo del delimitador solo lleva nombres del catálogo, nunca lo que escribió el modelo.
                var label = StudioTools.Find(use.Name)?.Name ?? "desconocida";
                results.Add(new StudioToolResult(use.Id, StudioPrompt.Untrusted(label, output.Data, options.MaxToolResultChars), output.IsError));
            }
            messages.Add(new(true, results));
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

    private async Task<Sandbox> CreateSandboxAsync(Run run, CancellationToken ct)
    {
        if (!options.Sandbox) throw new SandboxUnavailable("El entorno aislado está deshabilitado en este servidor (Studio:Sandbox).");
        // El nombre queda en la sesión antes de crear la base: si el proceso cae, el barrido la encuentra.
        await using (var cn = await db.OpenAsync(ct))
            if (await cn.ExecuteAsync("UPDATE meta.StudioSession SET SandboxDb = @name WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'",
                    new { name = sandboxes.NameFor(run.Claim.Id), run.Claim.Id, run.Claim.LeaseToken }) == 0)
                throw new StudioLeaseLost();
        var watch = Stopwatch.StartNew();
        var sandbox = await sandboxes.CreateAsync(run.Claim.Id, ct);
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
        var others = await cn.ExecuteScalarAsync<long>("""
            SELECT ISNULL(SUM(CostMicros + ReservedMicros), 0) FROM meta.StudioSession
             WHERE UserId = @UserId AND Id <> @Id AND CreatedAt > DATEADD(day, -1, SYSUTCDATETIME())
            """, new { run.Claim.UserId, run.Claim.Id }, tx);
        if (others + run.Cost + reserve > options.CostPerDayMicros) return false;
        if (await cn.ExecuteAsync("""
                UPDATE meta.StudioSession SET ReservedMicros = @reserve WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
                """, new { reserve, run.Claim.Id, run.Claim.LeaseToken }, tx) == 0)
            throw new StudioLeaseLost();
        await tx.CommitAsync(ct);
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
    }

    private async Task StepAsync(Run run, string kind, string? tool, string? input, JsonNode? output, string status, string summary, int ms)
    {
        var seq = ++run.Seq;
        await using var cn = await db.OpenAsync(CancellationToken.None);
        // H2 CA5: el paso solo se escribe si la sesión sigue siendo de este trabajador.
        if (await cn.ExecuteAsync("""
                INSERT meta.StudioStep (SessionId, Seq, Kind, Tool, InputJson, OutputJson, Status, Summary, DurationMs)
                SELECT @Id, @seq, @kind, @tool, @input, @output, @status, @summary, @ms
                 WHERE EXISTS (SELECT 1 FROM meta.StudioSession WITH (UPDLOCK) WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running');
                UPDATE meta.StudioSession SET ToolCalls = @ToolCalls WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running';
                """, new
            {
                run.Claim.Id, run.Claim.LeaseToken, seq, kind, tool, input, ms, status, run.ToolCalls,
                output = output is null ? null : Cut(output.ToJsonString(), options.MaxToolResultChars),
                summary = Cut(summary, 400),
            }) == 0)
            throw new StudioLeaseLost();
    }

    /// <summary>H2 CA5: el cierre y su auditoría van en una transacción que exige el lease.</summary>
    private async Task FinishAsync(Run run, Outcome o)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        await using var tx = await cn.BeginTransactionAsync();
        var n = await cn.ExecuteAsync("""
            UPDATE meta.StudioSession SET Status = @Status, ErrorCode = @ErrorCode, Summary = @Summary, PlanJson = @PlanJson,
                   ReportJson = @ReportJson, Steps = @Steps, ToolCalls = @ToolCalls, InputTokens = @Input, OutputTokens = @Output,
                   CostMicros = @Cost, ReservedMicros = 0, FinishedAt = SYSUTCDATETIME(), LeaseToken = NULL, LeaseUntil = NULL
             WHERE Id = @Id AND LeaseToken = @LeaseToken AND Status = 'running'
            """, new { o.Status, o.ErrorCode, o.Summary, o.PlanJson, o.ReportJson, run.Steps, run.ToolCalls, run.Input, run.Output, run.Cost,
                run.Claim.Id, run.Claim.LeaseToken }, tx);
        if (n == 0) throw new StudioLeaseLost();
        await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.StudioFinish, entity: "studio", recordId: run.Claim.Id.ToString(),
            after: new { status = o.Status, errorCode = o.ErrorCode, steps = run.Steps, toolCalls = run.ToolCalls, inputTokens = run.Input,
                outputTokens = run.Output, costUsd = run.Cost / 1_000_000m, model = assistant.Model },
            userId: run.Claim.UserId, username: run.User?.Username ?? await cn.ExecuteScalarAsync<string>("SELECT Username FROM meta.[User] WHERE Id = @UserId", new { run.Claim.UserId }, tx));
        await tx.CommitAsync();
    }

    internal static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
