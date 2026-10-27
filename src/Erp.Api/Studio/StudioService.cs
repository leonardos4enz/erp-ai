using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Assistant;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Studio;

public sealed record StudioStartBody(string? Prompt, IReadOnlyList<string>? Scopes);
public sealed record StudioApplyBody(string? ReviewHash);

public sealed record StudioLimits(int MaxSteps, int MaxToolCalls, long MaxTokens, int MaxMinutes, decimal MaxCostUsd, decimal CostPerDayUsd,
    int SessionsPerDay, int MaxPromptChars);

public sealed record StudioStatusDto(bool Configured, string? Model, bool SandboxAvailable, StudioLimits Limits, int SessionsToday,
    decimal CostTodayUsd, int? Active, bool DirectChanges);

public sealed record StudioSummaryDto(int Id, string Title, string Status, DateTime CreatedAt, DateTime? FinishedAt, int Steps, decimal CostUsd);

public sealed record StudioStepDto(int Seq, string Kind, string? Tool, string Status, string Summary, JsonNode? Input, JsonNode? Output, int DurationMs, DateTime At);

public sealed record StudioReport(IReadOnlyList<JsonNode?> Tested, IReadOnlyList<string> Failures, IReadOnlyList<string> Risks);

public sealed record StudioSessionDto
{
    public required int Id { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public required string Model { get; init; }
    public int Steps { get; init; }
    public int ToolCalls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? Summary { get; init; }
    public JsonNode? Report { get; init; }
    public ChangePlan? Plan { get; init; }
    public PlanPreview? Preview { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? ReviewHash { get; init; }
    public bool CanApply { get; init; }
    public string? BlockedReason { get; init; }
    public DateTime? AppliedAt { get; init; }
    public int? ChangeSetId { get; init; }
    public IReadOnlyList<StudioStepDto> Trail { get; init; } = [];
}

/// <summary>
/// Spec 033: sesiones de ERP AI Studio. Pedirlas, verlas, cancelarlas y aplicar su propuesta es de la persona dueña; el
/// trabajo lo hace <see cref="StudioAgent"/> en segundo plano. Aplicar es la misma vía que el modelador y el asistente:
/// huella de lo revisado, compuerta de cambio directo y <see cref="PlanService"/>.
/// </summary>
public sealed class StudioService(Db db, MetadataStore store, PlanService plans, StudioOptions options, AssistantOptions assistant,
    AssistantKeys keys, RouteDispatcher routes, SandboxFactory sandboxes, JsonSerializerOptions json, EnvironmentOptions environment,
    ILogger<StudioService> log)
{
    public static IReadOnlySet<string> ParseScopes(string? scopes) =>
        new HashSet<string>((scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(StudioScopes.Model), StringComparer.Ordinal);

    public static Task LockUserAsync(DbConnection cn, DbTransaction tx, int userId) => LockAsync(cn, tx, userId);

    private static async Task LockAsync(DbConnection cn, DbTransaction tx, int userId)
    {
        var locked = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, new { res = $"erp-studio:{userId}" }, tx);
        if (locked < 0) throw new Conflict("Hay otra operación de ERP AI Studio tuya en curso. Intenta de nuevo.");
    }

    public IReadOnlyList<StudioToolInfo> Tools() => StudioToolRunner.Describe(routes);

    public async Task<StudioStatusDto> StatusAsync(UserContext user, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var (sessions, cost) = await TodayAsync(cn, null, user.Id);
        var active = await cn.ExecuteScalarAsync<int?>("SELECT TOP 1 Id FROM meta.StudioSession WHERE UserId = @Id AND Status IN ('queued', 'running') ORDER BY Id DESC", new { user.Id });
        var configured = (await keys.InfoAsync(ct)).Source is not null;
        return new(configured, configured ? assistant.Model : null, options.Sandbox,
            new(options.MaxSteps, options.MaxToolCalls, options.MaxTokens, options.MaxMinutes, options.MaxCostUsd, options.CostPerDayUsd,
                options.SessionsPerDay, options.MaxPromptChars),
            sessions, cost / 1_000_000m, active, environment.DirectChanges);
    }

    private static async Task<(int Sessions, long CostMicros)> TodayAsync(DbConnection cn, DbTransaction? tx, int userId) =>
        await cn.QuerySingleAsync<(int, long)>("""
            SELECT COUNT(*), ISNULL(SUM(CostMicros + ReservedMicros), 0) FROM meta.StudioSession
             WHERE UserId = @userId AND CreatedAt > DATEADD(day, -1, SYSUTCDATETIME())
            """, new { userId }, tx);

    public async Task<StudioSessionDto> StartAsync(StudioStartBody body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (await keys.GetAsync(ct) is null)
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "ERP AI Studio usa la clave del asistente: un administrador debe guardarla en Configuración › Asistente IA.");
        var prompt = body.Prompt?.Trim() ?? "";
        if (prompt.Length == 0) throw Invalid("prompt", "Describe el desarrollo que necesitas.");
        if (prompt.Length > options.MaxPromptChars) throw Invalid("prompt", $"El pedido admite hasta {options.MaxPromptChars} caracteres.");
        var scopes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in body.Scopes ?? [])
        {
            if (s == StudioScopes.Model) continue;
            if (!StudioScopes.Optional.Contains(s)) throw Invalid("scopes", $"Alcance desconocido: usa {string.Join(" o ", StudioScopes.Optional)}.");
            scopes.Add(s);
        }
        if (scopes.Contains(StudioScopes.Sandbox) && !options.Sandbox)
            throw Invalid("scopes", "El entorno aislado no está habilitado en este servidor.");

        int id;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            // H2 CA2: una sesión activa por persona y los topes del día, reservados con un bloqueo por usuario.
            await LockAsync(cn, tx, user.Id);
            if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.StudioSession WHERE UserId = @Id AND Status IN ('queued', 'running')", new { user.Id }, tx) > 0)
                throw new Conflict("Ya tienes una sesión de ERP AI Studio en curso: espera a que termine o cancélala.");
            var (sessions, cost) = await TodayAsync(cn, tx, user.Id);
            if (sessions >= options.SessionsPerDay)
                throw new AppException(StatusCodes.Status429TooManyRequests, $"Alcanzaste el límite de {options.SessionsPerDay} sesiones de ERP AI Studio en 24 horas.");
            if (cost >= options.CostPerDayMicros)
                throw new AppException(StatusCodes.Status429TooManyRequests, $"Alcanzaste el costo máximo de {options.CostPerDayUsd} USD de ERP AI Studio en 24 horas.");
            id = await cn.ExecuteScalarAsync<int>("""
                INSERT meta.StudioSession (UserId, CompanyId, SecurityStamp, Prompt, Scopes, Status, Model)
                OUTPUT inserted.Id VALUES (@UserId, @CompanyId, @Stamp, @prompt, @Scopes, 'queued', @Model)
                """, new { UserId = user.Id, CompanyId = user.Company?.Id, Stamp = user.SecurityStamp, prompt, Scopes = string.Join(' ', scopes), assistant.Model }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.StudioStart, entity: "studio", recordId: id.ToString(),
                after: new { scopes = scopes.Prepend(StudioScopes.Model).ToArray(), model = assistant.Model });
            await tx.CommitAsync(ct);
        }
        return await GetAsync(id, null, user, ct);
    }

    public async Task<IReadOnlyList<StudioSummaryDto>> ListAsync(UserContext user, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<(int Id, string Prompt, string Status, DateTime CreatedAt, DateTime? FinishedAt, int Steps, long CostMicros)>("""
            SELECT TOP 30 Id, Prompt, Status, CreatedAt, FinishedAt, Steps, CostMicros FROM meta.StudioSession WHERE UserId = @Id ORDER BY Id DESC
            """, new { user.Id })).Select(r => new StudioSummaryDto(r.Id, r.Prompt.Length <= 80 ? r.Prompt : r.Prompt[..77].TrimEnd() + "…",
            r.Status, r.CreatedAt, r.FinishedAt, r.Steps, r.CostMicros / 1_000_000m)).ToList();
    }

    public async Task<StudioSessionDto> GetAsync(int id, int? after, UserContext user, CancellationToken ct)
    {
        Row row;
        List<StepRow> steps;
        await using (var cn = await db.OpenAsync(ct))
        {
            row = (await cn.QueryAsync<Row>(SelectRow + " WHERE Id = @id AND UserId = @UserId", new { id, UserId = user.Id })).SingleOrDefault()
                  ?? throw new NotFound("La sesión no existe.");
            steps = (await cn.QueryAsync<StepRow>("""
                SELECT Seq, Kind, Tool, Status, Summary, InputJson, OutputJson, DurationMs, CreatedAt FROM meta.StudioStep
                 WHERE SessionId = @id AND Seq > @after ORDER BY Seq
                """, new { id, after = after ?? 0 })).ToList();
        }
        return await ToDtoAsync(row, steps, ct);
    }

    public async Task<StudioSessionDto> CancelAsync(int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var status = await cn.ExecuteScalarAsync<string?>("SELECT Status FROM meta.StudioSession WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx) ?? throw new NotFound("La sesión no existe.");
            if (status == StudioStatus.Queued)
                await cn.ExecuteAsync("UPDATE meta.StudioSession SET Status = 'cancelled', ErrorCode = 'cancelled', FinishedAt = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
            else if (status == StudioStatus.Running)
                // El trabajador lo lee al renovar su lease y se detiene antes del siguiente paso (H2 CA3).
                await cn.ExecuteAsync("UPDATE meta.StudioSession SET CancelRequested = 1 WHERE Id = @id", new { id }, tx);
            else throw new Conflict("La sesión ya terminó.");
            await Audit.WriteAsync(cn, tx, http, AuditActions.StudioCancel, entity: "studio", recordId: id.ToString(), after: new { status });
            await tx.CommitAsync(ct);
        }
        return await GetAsync(id, null, user, ct);
    }

    public async Task<StudioSessionDto> DiscardAsync(int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var status = await cn.ExecuteScalarAsync<string?>("SELECT Status FROM meta.StudioSession WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId",
                new { id, UserId = user.Id }, tx) ?? throw new NotFound("La sesión no existe.");
            if (status != StudioStatus.Proposed) throw new Conflict("Solo se descarta una propuesta vigente.");
            await cn.ExecuteAsync("UPDATE meta.StudioSession SET Status = 'discarded' WHERE Id = @id", new { id }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.StudioDiscard, entity: "studio", recordId: id.ToString());
            await tx.CommitAsync(ct);
        }
        return await GetAsync(id, null, user, ct);
    }

    /// <summary>H4 CA2: la persona dueña aplica exactamente el plan guardado, con la huella de lo que revisó (012/021).</summary>
    public async Task<(int ChangeSetId, StudioSessionDto Session)> ApplyAsync(int id, StudioApplyBody? body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        environment.RequireDirectChange("una propuesta de ERP AI Studio");
        int changeSetId;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            var row = (await cn.QueryAsync<Row>(SelectRow + " WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND UserId = @UserId", new { id, UserId = user.Id }, tx))
                .SingleOrDefault() ?? throw new NotFound("La sesión no existe.");
            if (row.Status != StudioStatus.Proposed || row.PlanJson is null)
                throw new Conflict(row.Status switch
                {
                    StudioStatus.Applied => "Esta propuesta ya se aplicó.",
                    StudioStatus.Discarded => "Esta propuesta se descartó.",
                    _ => "Esta sesión no tiene una propuesta para aplicar.",
                });
            if (string.IsNullOrWhiteSpace(body?.ReviewHash))
                throw Invalid("reviewHash", "Falta la huella de la propuesta revisada: recarga la sesión y revísala de nuevo.");
            var plan = JsonSerializer.Deserialize<ChangePlan>(row.PlanJson, json)!;
            await PlanService.LockModelAsync(cn, tx);
            var model = await MetadataStore.LoadAsync(cn, tx);
            var boards = await MetadataStore.LoadDashboardsAsync(cn, tx);
            var apps = await MetadataStore.LoadAppsAsync(cn, tx);
            var compiled = PlanCompiler.Compile(plan, model, await PlanService.LoadRolesAsync(cn, tx), boards, apps);
            var baseHash = ModelVersion.Base(model, boards, compiled.TouchedEntities, compiled.TouchedDashboards, apps, compiled.TouchedApps);
            if (compiled.Preview.Valid && !string.Equals(body!.ReviewHash,
                    Assistant.ReviewHash.Of(row.PlanJson, compiled.Preview, PlanWarnings.For(plan, model), baseHash), StringComparison.OrdinalIgnoreCase))
                throw new Conflict("El modelo cambió desde que revisaste la propuesta: revisa la vista previa actualizada antes de aplicarla.");
            changeSetId = await plans.ApplyInTransactionAsync(cn, tx, plan, http);
            await cn.ExecuteAsync("UPDATE meta.StudioSession SET Status = 'applied', AppliedAt = SYSUTCDATETIME(), ChangeSetId = @changeSetId WHERE Id = @id",
                new { id, changeSetId }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.StudioApply, entity: "studio", recordId: id.ToString(),
                after: new { changeSetId, operations = plan.Operations.Count });
            await tx.CommitAsync(ct);
        }
        store.Invalidate();
        return (changeSetId, await GetAsync(id, null, user, ct));
    }

    // ---------- Trabajador ----------

    /// <summary>Sesiones cuyo trabajador dejó de renovar: terminan como interrumpidas (no se repiten) y su copia se borra.</summary>
    internal async Task<int> ExpireAsync(CancellationToken ct)
    {
        List<(int Id, int UserId, string? SandboxDb)> expired;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            expired = (await cn.QueryAsync<(int, int, string?)>("""
                UPDATE meta.StudioSession SET Status = 'interrupted', ErrorCode = 'interrupted', FinishedAt = SYSUTCDATETIME(),
                       LeaseToken = NULL, LeaseUntil = NULL, ReservedMicros = 0
                OUTPUT inserted.Id, inserted.UserId, inserted.SandboxDb
                 WHERE Status = 'running' AND LeaseUntil < SYSUTCDATETIME()
                """, transaction: tx)).ToList();
            foreach (var e in expired)
                await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.StudioFinish, entity: "studio", recordId: e.Id.ToString(),
                    after: new { status = StudioStatus.Interrupted }, userId: e.UserId,
                    username: await cn.ExecuteScalarAsync<string>("SELECT Username FROM meta.[User] WHERE Id = @UserId", new { e.UserId }, tx));
            await tx.CommitAsync(ct);
        }
        foreach (var e in expired)
        {
            log.LogWarning("La sesión de Studio {Id} quedó sin trabajador y se marcó interrumpida", e.Id);
            if (e.SandboxDb is not null) await sandboxes.DropAsync(e.SandboxDb);
        }
        return expired.Count;
    }

    internal async Task<StudioClaim?> ClaimAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<StudioClaim>("""
            WITH next AS (SELECT TOP 1 * FROM meta.StudioSession WITH (READPAST, UPDLOCK, ROWLOCK) WHERE Status = 'queued' ORDER BY Id)
            UPDATE next SET Status = 'running', LeaseToken = NEWID(), LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME()), StartedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.UserId, inserted.CompanyId, inserted.SecurityStamp, inserted.Prompt, inserted.Scopes, inserted.LeaseToken, inserted.StartedAt
            """, new { lease = options.LeaseSeconds })).SingleOrDefault();
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        return await sandboxes.SweepAsync(async () =>
        {
            await using var cn = await db.OpenAsync(ct);
            return (await cn.QueryAsync<int>("SELECT Id FROM meta.StudioSession WHERE Status = 'running'")).ToHashSet();
        }, ct);
    }

    // ---------- Lectura ----------

    private async Task<StudioSessionDto> ToDtoAsync(Row r, IReadOnlyList<StepRow> steps, CancellationToken ct)
    {
        ChangePlan? plan = null;
        if (r.PlanJson is not null)
        {
            try { plan = JsonSerializer.Deserialize<ChangePlan>(r.PlanJson, json); }
            catch (JsonException ex) { log.LogWarning("Sesión de Studio {Id} con plan ilegible: {Message}", r.Id, ex.Message); }
        }
        PlanPreview? preview = null;
        IReadOnlyList<string> warnings = [];
        string? reviewHash = null;
        if (plan is not null && r.Status == StudioStatus.Proposed)
        {
            // La propuesta vigente se recompila al leerla: si el modelo cambió, la vista previa (y la huella) lo reflejan.
            var model = await store.GetAsync(ct);
            var boards = await store.GetDashboardsAsync(ct);
            var apps = await store.GetAppsAsync(ct);
            IReadOnlySet<string> roles;
            await using (var cn = await db.OpenAsync(ct)) roles = await PlanService.LoadRolesAsync(cn, null);
            var compiled = PlanCompiler.Compile(plan, model, roles, boards, apps);
            preview = compiled.Preview;
            warnings = PlanWarnings.For(plan, model);
            if (preview.Valid)
                reviewHash = Assistant.ReviewHash.Of(r.PlanJson!, preview, warnings,
                    ModelVersion.Base(model, boards, compiled.TouchedEntities, compiled.TouchedDashboards, apps, compiled.TouchedApps));
        }
        else if (plan is not null) warnings = PlanWarnings.For(plan, await store.GetAsync(ct));
        return new StudioSessionDto
        {
            Id = r.Id, Prompt = r.Prompt, Scopes = ParseScopes(r.Scopes).Order(StringComparer.Ordinal).ToList(), Status = r.Status,
            Error = r.ErrorCode is null || r.Status is StudioStatus.Cancelled ? null : FailureMessage(r.ErrorCode, r.Summary),
            Model = r.Model, Steps = r.Steps, ToolCalls = r.ToolCalls, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens,
            CostUsd = r.CostMicros / 1_000_000m, CreatedAt = r.CreatedAt, StartedAt = r.StartedAt, FinishedAt = r.FinishedAt,
            Summary = r.Summary, Report = r.ReportJson is null ? null : JsonNode.Parse(r.ReportJson),
            Plan = plan, Preview = preview, Warnings = warnings, ReviewHash = reviewHash,
            CanApply = reviewHash is not null && environment.DirectChanges,
            BlockedReason = r.Status == StudioStatus.Proposed && !environment.DirectChanges
                ? "Este entorno exige la aprobación de otro administrador: la propuesta no se aplica aquí. Los cambios al modelo llegan como paquete desde Configuración › Entornos."
                : null,
            AppliedAt = r.AppliedAt, ChangeSetId = r.ChangeSetId,
            Trail = steps.Select(s => new StudioStepDto(s.Seq, s.Kind, s.Tool, s.Status, s.Summary, Parse(s.InputJson), Parse(s.OutputJson), s.DurationMs, s.CreatedAt)).ToList(),
        };
    }

    private static JsonNode? Parse(string? text)
    {
        if (text is null) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private static string FailureMessage(string code, string? summary) => code switch
    {
        "limit_steps" or "limit_tools" or "limit_tokens" or "limit_cost" or "limit_day" or "limit_time" => summary ?? "Se alcanzó un tope de la sesión.",
        "identity" => "La sesión se detuvo: tu cuenta cambió (contraseña, estado o permisos).",
        "interrupted" => "La sesión se interrumpió (el servidor se reinició o dejó de responder). Pídela de nuevo.",
        "not_configured" => "ERP AI Studio no tiene clave de Anthropic.",
        "auth" => "Anthropic rechazó la clave. Un administrador debe capturarla de nuevo en Configuración › Asistente IA.",
        "rate_limit" => "El proveedor está limitando las solicitudes. Intenta en unos minutos.",
        "timeout" or "network" => "El proveedor no respondió a tiempo.",
        "refusal" => "El modelo no pudo atender este pedido. Reformúlalo en términos del modelo del ERP.",
        "truncated" => "La respuesta del modelo quedó incompleta. Pide un desarrollo más acotado.",
        _ => "La sesión falló por un error inesperado.",
    };

    private static ValidationFailed Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] }, message);

    private const string SelectRow = """
        SELECT Id, Prompt, Scopes, Status, ErrorCode, Model, Steps, ToolCalls, InputTokens, OutputTokens, CostMicros, CreatedAt, StartedAt,
               FinishedAt, Summary, ReportJson, PlanJson, AppliedAt, ChangeSetId FROM meta.StudioSession
        """;

    private sealed record Row(int Id, string Prompt, string Scopes, string Status, string? ErrorCode, string Model, int Steps, int ToolCalls,
        long InputTokens, long OutputTokens, long CostMicros, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, string? Summary,
        string? ReportJson, string? PlanJson, DateTime? AppliedAt, int? ChangeSetId);

    private sealed record StepRow(int Seq, string Kind, string? Tool, string Status, string Summary, string? InputJson, string? OutputJson, int DurationMs, DateTime CreatedAt);
}

/// <summary>Spec 033 (H2 CA5): reclama sesiones con lease, marca las interrumpidas y barre las copias aisladas huérfanas.</summary>
public sealed class StudioDispatcher(IServiceScopeFactory scopes, StudioOptions options, ILogger<StudioDispatcher> log) : BackgroundService
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
                var svc = scope.ServiceProvider.GetRequiredService<StudioService>();
                await svc.ExpireAsync(stoppingToken);
                while (Volatile.Read(ref running) < options.Workers && await svc.ClaimAsync(stoppingToken) is { } claim)
                {
                    Interlocked.Increment(ref running);
                    runs.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await using var work = scopes.CreateAsyncScope();
                            await work.ServiceProvider.GetRequiredService<StudioAgent>().RunAsync(claim, stoppingToken);
                        }
                        catch (Exception ex) { log.LogError(ex, "Error en la sesión de Studio {Id}", claim.Id); }
                        finally { Interlocked.Decrement(ref running); }
                    }, CancellationToken.None));
                }
                runs.RemoveAll(t => t.IsCompleted);
                if (DateTime.UtcNow - lastSweep > TimeSpan.FromMinutes(10))
                {
                    await svc.SweepAsync(stoppingToken);
                    lastSweep = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Error en el trabajador de ERP AI Studio"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); } catch (OperationCanceledException) { break; }
        }
        await Task.WhenAll(runs);
    }
}
