using System.Globalization;
using System.Text.Json;
using Dapper;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Automations;

/// <summary>Spec 016: configuración por entorno de la bandeja de automatizaciones (<c>Automations:*</c>).</summary>
public sealed record AutomationOptions(int PollSeconds, IReadOnlyList<int> RetryDelaysSeconds, int RetentionDays)
{
    public const int BatchSize = 10;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public static AutomationOptions From(IConfiguration c) => new(
        Math.Clamp(c.GetValue("Automations:PollSeconds", 2), 1, 300),
        (c["Automations:RetryDelaysSeconds"] ?? "30,120,600,3600").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Math.Clamp(int.Parse(s, CultureInfo.InvariantCulture), 1, 86_400)).Take(10).ToList(),
        Math.Clamp(c.GetValue("Automations:RetentionDays", 30), 1, 365));

    /// <summary>Intentos totales de un trabajo asíncrono: el primero más uno por cada espera.</summary>
    public int MaxAttempts => RetryDelaysSeconds.Count + 1;
}

public static class RunKinds
{
    public const string Async = "async";
    public const string Schedule = "schedule";
}

public static class RunStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Pending, Running, Succeeded, Partial, Failed, Canceled };
}

public sealed record LastRunDto(long Id, string Status, DateTime? FinishedAt, int Processed, int Succeeded, int Failed, bool More, string? Error);
public sealed record ScheduleSummary(string Entity, string EntityLabel, string Name, string Label, string? Description, string Calendar,
    string Role, int BatchSize, bool Active, DateTime? NextRunAt, LastRunDto? LastRun);
public sealed record SetScheduleRequest(bool Active);
public sealed record AutomationRunDto(long Id, string Kind, string Entity, string EntityLabel, string Name, string Label, long? RecordId,
    string? Event, string? EventKey, string RunAs, bool Manual, string Status, int Attempts, int Processed, int Succeeded, int Failed,
    int Skipped, bool More, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, DateTime? NextAttemptAt, string? LastError);
public sealed record AutomationRunDetail(AutomationRunDto Run, IReadOnlyList<RunError> Errors);

/// <summary>
/// Spec 016: tareas programadas (activar por entorno, ocurrencias) y la bandeja <c>meta.AutomationRun</c> (reclamo con
/// *lease*, ejecución, reintentos y bitácora). La administración es solo del superusuario, como las integraciones.
/// </summary>
public sealed class AutomationService(Db db, MetadataStore store, RecordService records, UserContextLoader users,
    AutomationOptions options, ILogger<AutomationService> log)
{
    /// <summary>Hora local del entorno: la misma que usa <c>TODAY()</c>.</summary>
    public static TimeZoneInfo Zone => TimeZoneInfo.Local;

    private static void RequireAdmin(UserContext u)
    {
        if (!u.IsSuperuser) throw new Forbidden("Solo un administrador puede administrar las automatizaciones.");
    }

    private async Task<(EntityDef Entity, ScheduleDef Schedule)> FindAsync(string entity, string name, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entity, ct);
        var s = e.Schedules?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFound("La tarea programada no existe.");
        return (e, s);
    }

    // ======================= Administración =======================

    private sealed record StateRow(string EntityName, string ScheduleName, bool IsActive, DateTime? NextRunAt, string? CalendarJson, long? LastRunId);
    private sealed record LastRow(long Id, string EntityName, string Name, string Status, DateTime? FinishedAt, int Processed, int Succeeded, int Failed, bool More, string? LastError);

    public async Task<IReadOnlyList<ScheduleSummary>> ListSchedulesAsync(UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var states = (await cn.QueryAsync<StateRow>("SELECT EntityName, ScheduleName, IsActive, NextRunAt, CalendarJson, LastRunId FROM meta.ScheduleState"))
            .ToDictionary(x => Key(x.EntityName, x.ScheduleName));
        var last = (await cn.QueryAsync<LastRow>("""
            SELECT r.Id, r.EntityName, r.Name, r.Status, r.FinishedAt, r.Processed, r.Succeeded, r.Failed, r.More, r.LastError FROM meta.AutomationRun r
             WHERE r.Id IN (SELECT MAX(Id) FROM meta.AutomationRun WHERE Kind = 'schedule' GROUP BY EntityName, Name)
            """)).ToDictionary(x => Key(x.EntityName, x.Name));
        return model.Values.Where(e => !e.IsArchived && e.Schedules is { Count: > 0 })
            .OrderBy(e => e.Label, StringComparer.CurrentCulture)
            .SelectMany(e => e.Schedules!.Select(s => Summary(e, s, states.GetValueOrDefault(Key(e.Name, s.Name)), last.GetValueOrDefault(Key(e.Name, s.Name)))))
            .ToList();
    }

    private static ScheduleSummary Summary(EntityDef e, ScheduleDef s, StateRow? state, LastRow? last) =>
        new(e.Name, e.Label, s.Name, s.Label, s.Description, ScheduleCalendar.Describe(s), s.Role, s.EffectiveBatchSize,
            state?.IsActive == true, state is { IsActive: true, NextRunAt: { } n } ? Utc(n) : null,
            last is null ? null : new LastRunDto(last.Id, last.Status, last.FinishedAt is { } f ? Utc(f) : null, last.Processed, last.Succeeded, last.Failed, last.More, last.LastError));

    private static (string, string) Key(string entity, string name) => (entity.ToLowerInvariant(), name.ToLowerInvariant());

    /// <summary>Activar calcula la siguiente ocurrencia desde ahora; pausar la quita.</summary>
    public async Task<ScheduleSummary> SetActiveAsync(string entity, string name, SetScheduleRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, s) = await FindAsync(entity, name, ct);
        DateTime? next = req.Active ? ScheduleCalendar.Next(s, DateTime.UtcNow, Zone) : null;
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var before = await cn.QuerySingleOrDefaultAsync<bool?>(
            "SELECT IsActive FROM meta.ScheduleState WITH (UPDLOCK, HOLDLOCK) WHERE EntityName = @e AND ScheduleName = @n", new { e = e.Name, n = s.Name }, tx);
        await cn.ExecuteAsync("""
            MERGE meta.ScheduleState AS t USING (SELECT @e AS EntityName, @n AS ScheduleName) AS src
               ON t.EntityName = src.EntityName AND t.ScheduleName = src.ScheduleName
            WHEN MATCHED THEN UPDATE SET IsActive = @active, NextRunAt = @next, CalendarJson = @calendar, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @uid
            WHEN NOT MATCHED THEN INSERT (EntityName, ScheduleName, IsActive, NextRunAt, CalendarJson, UpdatedBy) VALUES (@e, @n, @active, @next, @calendar, @uid);
            """, new { e = e.Name, n = s.Name, active = req.Active, next, calendar = ScheduleCalendar.Key(s), uid = u.Id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ScheduleChange, e.Name, s.Name,
            before: before is null ? null : new { active = before }, after: new { action = req.Active ? "activate" : "pause", active = req.Active, nextRunAt = next });
        await tx.CommitAsync(ct);
        return (await ListSchedulesAsync(u, ct)).First(x => x.Entity == e.Name && x.Name == s.Name);
    }

    /// <summary>Encola una ocurrencia ahora (también si está pausada). 409 si ya hay una pendiente o en curso.</summary>
    public async Task<long> RunNowAsync(string entity, string name, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, s) = await FindAsync(entity, name, ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await InsertOccurrenceAsync(cn, tx, e, s, manual: true)
                 ?? throw new Conflict("Ya hay una ejecución de esta tarea pendiente o en curso: espera a que termine.");
        await Audit.WriteAsync(cn, tx, http, AuditActions.ScheduleChange, e.Name, s.Name, after: new { action = "run", run = id });
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Una ocurrencia pendiente, salvo que la tarea ya tenga una pendiente o en curso (no se enciman).</summary>
    private static Task<long?> InsertOccurrenceAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx, EntityDef e, ScheduleDef s, bool manual) =>
        cn.ExecuteScalarAsync<long?>("""
            INSERT meta.AutomationRun (Kind, EntityName, Name, RunAsRole, RunAsName, Manual, Status)
            OUTPUT inserted.Id
            SELECT 'schedule', @e, @n, @role, @runAs, @manual, 'pending'
             WHERE NOT EXISTS (SELECT 1 FROM meta.AutomationRun WITH (UPDLOCK, HOLDLOCK)
                                WHERE Kind = 'schedule' AND EntityName = @e AND Name = @n AND Status IN ('pending', 'running'))
            """, new { e = e.Name, n = s.Name, role = s.Role, runAs = ScheduleAccount.RunAs(e.Name, s.Name), manual }, tx);

    private sealed record RunRow(long Id, string Kind, string EntityName, string Name, long? RecordId, string? Event, string? EventKey,
        int? TriggeredBy, string? TriggeredByName, string? RunAsRole, string RunAsName, bool Manual, string Status, int Attempts, int Processed,
        int Succeeded, int Failed, int Skipped, bool More, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, DateTime NextAttemptAt,
        string? LastError, string? ErrorsJson);

    private const string RunSelect = """
        SELECT r.Id, r.Kind, r.EntityName, r.Name, r.RecordId, r.Event, r.EventKey, r.TriggeredBy, u.DisplayName AS TriggeredByName,
               r.RunAsRole, r.RunAsName, r.Manual, r.Status, r.Attempts, r.Processed, r.Succeeded, r.Failed, r.Skipped, r.More,
               r.CreatedAt, r.StartedAt, r.FinishedAt, r.NextAttemptAt, r.LastError, r.ErrorsJson
          FROM meta.AutomationRun r LEFT JOIN meta.[User] u ON u.Id = r.TriggeredBy
        """;

    public async Task<IReadOnlyList<AutomationRunDto>> RunsAsync(string? kind, string? status, string? entity, string? name, UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<RunRow>($"""
            {RunSelect.Replace("SELECT r.Id", "SELECT TOP 100 r.Id")}
             WHERE (@kind IS NULL OR r.Kind = @kind) AND (@status IS NULL OR r.Status = @status)
               AND (@entity IS NULL OR r.EntityName = @entity) AND (@name IS NULL OR r.Name = @name)
             ORDER BY r.Id DESC
            """, new { kind, status, entity, name });
        return rows.Select(r => Dto(r, model)).ToList();
    }

    public async Task<AutomationRunDetail> RunAsync(long id, UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var r = await cn.QuerySingleOrDefaultAsync<RunRow>($"{RunSelect} WHERE r.Id = @id", new { id }) ?? throw new NotFound("La ejecución no existe.");
        var errors = r.ErrorsJson is null ? [] : JsonSerializer.Deserialize<List<RunError>>(r.ErrorsJson, MetadataStore.Json) ?? [];
        return new(Dto(r, model), errors);
    }

    private static AutomationRunDto Dto(RunRow r, IReadOnlyDictionary<string, EntityDef> model)
    {
        var e = model.GetValueOrDefault(r.EntityName);
        var label = r.Kind == RunKinds.Schedule
            ? e?.Schedules?.FirstOrDefault(s => string.Equals(s.Name, r.Name, StringComparison.OrdinalIgnoreCase))?.Label
            : e?.Automations?.FirstOrDefault(a => string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase))?.Label;
        // A nombre de quién: la persona (o la clave) que disparó el evento; en una tarea, la tarea.
        var runAs = r.RunAsRole is not null
            ? $"Tarea programada ({r.RunAsRole})"
            : r.TriggeredByName ?? r.RunAsName;
        return new(r.Id, r.Kind, r.EntityName, e?.Label ?? r.EntityName, r.Name, label ?? r.Name, r.RecordId, r.Event, r.EventKey, runAs, r.Manual,
            r.Status, r.Attempts, r.Processed, r.Succeeded, r.Failed, r.Skipped, r.More, Utc(r.CreatedAt),
            r.StartedAt is { } s ? Utc(s) : null, r.FinishedAt is { } f ? Utc(f) : null,
            r.Status == RunStatus.Pending ? Utc(r.NextAttemptAt) : null, r.LastError);
    }

    /// <summary>Un trabajo asíncrono fallido o cancelado vuelve a la bandeja con sus intentos desde cero.</summary>
    public async Task RetryAsync(long id, HttpContext http, CancellationToken ct)
    {
        RequireAdmin(http.CurrentUser());
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<RetryRow>(
            "SELECT EntityName, Name FROM meta.AutomationRun WITH (UPDLOCK) WHERE Id = @id AND Kind = 'async' AND Status IN ('failed', 'canceled')",
            new { id }, tx) ?? throw new NotFound("La ejecución no existe o no se puede reintentar.");
        await cn.ExecuteAsync("""
            UPDATE meta.AutomationRun SET Status = 'pending', Attempts = 0, NextAttemptAt = SYSUTCDATETIME(), LeaseUntil = NULL,
                   LastError = NULL, StartedAt = NULL, FinishedAt = NULL
             WHERE Id = @id
            """, new { id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.AutomationRetry, row.EntityName, row.Name, after: new { run = id });
        await tx.CommitAsync(ct);
    }

    private sealed record RetryRow(string EntityName, string Name);

    // ======================= Segundo plano =======================

    /// <summary>Encola las ocurrencias que tocan y ejecuta un lote de la bandeja. Devuelve cuántas ejecuciones tomó.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await QueueDueSchedulesAsync(ct);
        List<Claimed> batch;
        await using (var cn = await db.OpenAsync(ct))
        {
            batch = (await cn.QueryAsync<Claimed>($"""
                UPDATE TOP ({AutomationOptions.BatchSize}) r
                   SET Status = 'running', Attempts = Attempts + 1, StartedAt = SYSUTCDATETIME(), LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME())
                OUTPUT inserted.Id, inserted.Kind, inserted.EntityName, inserted.Name, inserted.RecordId, inserted.TriggeredBy, inserted.RunAsRole,
                       inserted.RunAsName, inserted.CompanyId, inserted.Generation, inserted.Attempts
                  FROM meta.AutomationRun r WITH (ROWLOCK, READPAST, UPDLOCK)
                 WHERE (r.Status = 'pending' AND r.NextAttemptAt <= SYSUTCDATETIME())
                    OR (r.Status = 'running' AND r.LeaseUntil < SYSUTCDATETIME())
                """, new { lease = (int)AutomationOptions.Lease.TotalSeconds })).ToList();
        }
        if (batch.Count == 0) return 0;
        await Task.WhenAll(batch.Select(r => r.Kind == RunKinds.Schedule ? RunScheduleAsync(r, ct) : RunAsyncJobAsync(r, ct)));
        return batch.Count;
    }

    private sealed record Claimed(long Id, string Kind, string EntityName, string Name, long? RecordId, int? TriggeredBy, string? RunAsRole,
        string RunAsName, int? CompanyId, int Generation, int Attempts);

    /// <summary>
    /// Por cada tarea activa: si un plan cambió su calendario, se reagenda sin correr; si ya tocaba, se avanza la siguiente
    /// ocurrencia (solo una instancia lo logra: el UPDATE vuelve a leer la fila bloqueada) y se encola una ejecución. Las
    /// perdidas no se acumulan.
    /// </summary>
    private async Task QueueDueSchedulesAsync(CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var states = (await cn.QueryAsync<StateRow>(
            "SELECT EntityName, ScheduleName, IsActive, NextRunAt, CalendarJson, LastRunId FROM meta.ScheduleState WHERE IsActive = 1")).ToList();
        var now = DateTime.UtcNow;
        foreach (var st in states)
        {
            if (!model.TryGetValue(st.EntityName, out var e) || e.IsArchived ||
                e.Schedules?.FirstOrDefault(s => string.Equals(s.Name, st.ScheduleName, StringComparison.OrdinalIgnoreCase)) is not { } s)
                continue;
            if (ScheduleRules.ValidateCalendar(s).Any()) continue;
            var key = ScheduleCalendar.Key(s);
            var next = ScheduleCalendar.Next(s, now, Zone);
            if (st.CalendarJson != key || st.NextRunAt is null)
            {
                await cn.ExecuteAsync("""
                    UPDATE meta.ScheduleState SET NextRunAt = @next, CalendarJson = @key
                     WHERE EntityName = @EntityName AND ScheduleName = @ScheduleName AND IsActive = 1
                    """, new { next, key, st.EntityName, st.ScheduleName });
                continue;
            }
            if (st.NextRunAt > now) continue;

            await using var tx = await cn.BeginTransactionAsync(ct);
            var won = await cn.ExecuteAsync("""
                UPDATE meta.ScheduleState SET NextRunAt = @next
                 WHERE EntityName = @EntityName AND ScheduleName = @ScheduleName AND IsActive = 1 AND NextRunAt <= SYSUTCDATETIME()
                """, new { next, st.EntityName, st.ScheduleName }, tx);
            if (won == 1) await InsertOccurrenceAsync(cn, tx, e, s, manual: false);
            await tx.CommitAsync(ct);
        }
    }

    private async Task RunAsyncJobAsync(Claimed r, CancellationToken ct)
    {
        try
        {
            var user = await IdentityAsync(r, ct);
            await records.RunQueuedAsync(r.EntityName, r.Name, r.RecordId ?? 0, user, r.Generation, ct);
            await FinishAsync(r.Id, RunStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* el lease vence y otra vuelta lo retoma */ }
        catch (RunCanceled ex) { await FinishAsync(r.Id, RunStatus.Canceled, ex.Message); }
        catch (Exception ex)
        {
            var transient = BackgroundErrors.IsTransient(ex);
            if (ex is not AppException && !transient) log.LogError(ex, "Error al ejecutar la automatización asíncrona {Entity}.{Name} ({Id})", r.EntityName, r.Name, r.Id);
            var message = BackgroundErrors.Message(ex);
            if (transient && r.Attempts < options.MaxAttempts)
            {
                await using var cn = await db.OpenAsync(CancellationToken.None);
                await cn.ExecuteAsync("""
                    UPDATE meta.AutomationRun SET Status = 'pending', LeaseUntil = NULL, LastError = @message,
                           NextAttemptAt = DATEADD(second, @delay, SYSUTCDATETIME())
                     WHERE Id = @Id
                    """, new { r.Id, message = Truncate(message), delay = options.RetryDelaysSeconds[r.Attempts - 1] });
            }
            else await FinishAsync(r.Id, RunStatus.Failed, message);
        }
    }

    /// <summary>
    /// Quien disparó el evento, recargado (roles, estado y empresa actuales); en una cadena que empezó en una tarea, la
    /// identidad de la tarea. Si ya no está activo o perdió la empresa del evento, el trabajo se cancela.
    /// </summary>
    private async Task<UserContext> IdentityAsync(Claimed r, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        CompanyInfo? company = null;
        if (r.CompanyId is { } cid)
            company = (await cn.QueryAsync<(int Id, string Code, string Name, string BaseCurrency)>(
                    "SELECT Id, Code, Name, BaseCurrency FROM meta.Company WHERE Id = @cid AND IsActive = 1", new { cid }))
                .Select(c => new CompanyInfo(c.Id, c.Code, c.Name, c.BaseCurrency.Trim())).FirstOrDefault()
                ?? throw new RunCanceled("La empresa del evento ya no está activa.");
        if (r.RunAsRole is not null) return await ScheduleAccount.LoadAsync(cn, null, r.RunAsRole, company, r.RunAsName);

        var stamp = r.TriggeredBy is { } uid
            ? await cn.ExecuteScalarAsync<Guid?>("SELECT SecurityStamp FROM meta.[User] WHERE Id = @uid AND IsActive = 1 AND IsTeam = 0", new { uid })
            : null;
        var user = stamp is null ? null : await users.LoadAsync(r.TriggeredBy!.Value, stamp.Value, ct);
        if (user is null) throw new RunCanceled("Quien disparó el evento ya no está activo.");
        if (company is null) return user;
        if (!user.Companies.Any(c => c.Id == company.Id)) throw new RunCanceled("Quien disparó el evento ya no tiene acceso a su empresa.");
        return user.WithCompany(company);
    }

    private async Task RunScheduleAsync(Claimed r, CancellationToken ct)
    {
        try
        {
            var outcome = await records.RunScheduleAsync(r.EntityName, r.Name, r.RunAsName, o => ProgressAsync(r.Id, o),
                ex => log.LogError(ex, "Error en la tarea programada {Entity}.{Name} ({Id})", r.EntityName, r.Name, r.Id), ct);
            var status = outcome.Failed == 0 ? RunStatus.Succeeded : outcome.Succeeded > 0 ? RunStatus.Partial : RunStatus.Failed;
            var summary = outcome.Failed == 0 ? null : $"{outcome.Failed} registro(s) con error: {outcome.Errors.FirstOrDefault()?.Message}";
            await using var cn = await db.OpenAsync(CancellationToken.None);
            await cn.ExecuteAsync("""
                UPDATE meta.AutomationRun SET Status = @status, LeaseUntil = NULL, FinishedAt = SYSUTCDATETIME(), Processed = @Processed,
                       Succeeded = @Succeeded, Failed = @Failed, Skipped = @Skipped, More = @More, LastError = @summary, ErrorsJson = @errors
                 WHERE Id = @Id;
                UPDATE meta.ScheduleState SET LastRunId = @Id WHERE EntityName = @EntityName AND ScheduleName = @Name;
                """, new
            {
                r.Id, r.EntityName, r.Name, status, outcome.Processed, outcome.Succeeded, outcome.Failed, outcome.Skipped, outcome.More,
                summary = Truncate(summary), errors = outcome.Errors.Count == 0 ? null : JsonSerializer.Serialize(outcome.Errors, MetadataStore.Json),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (RunCanceled ex) { await FinishAsync(r.Id, RunStatus.Canceled, ex.Message); }
        catch (Exception ex)
        {
            // Una tarea no se reintenta sola: la siguiente ocurrencia toma lo que siga cumpliendo la consulta.
            if (ex is not AppException) log.LogError(ex, "Error en la tarea programada {Entity}.{Name} ({Id})", r.EntityName, r.Name, r.Id);
            await FinishAsync(r.Id, RunStatus.Failed, BackgroundErrors.Message(ex));
        }
    }

    /// <summary>El avance se ve en la bitácora y renueva el *lease*: una tarea larga no se le pasa a otra instancia.</summary>
    private async Task ProgressAsync(long id, ScheduleOutcome o)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        await cn.ExecuteAsync("""
            UPDATE meta.AutomationRun SET Processed = @Processed, Succeeded = @Succeeded, Failed = @Failed, Skipped = @Skipped,
                   LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME())
             WHERE Id = @id
            """, new { id, o.Processed, o.Succeeded, o.Failed, o.Skipped, lease = (int)AutomationOptions.Lease.TotalSeconds });
    }

    private async Task FinishAsync(long id, string status, string? error)
    {
        await using var cn = await db.OpenAsync(CancellationToken.None);
        await cn.ExecuteAsync("""
            UPDATE meta.AutomationRun SET Status = @status, LeaseUntil = NULL, FinishedAt = SYSUTCDATETIME(), LastError = @error WHERE Id = @id
            """, new { id, status, error = Truncate(error) });
    }

    /// <summary>La bitácora lleva ids de registros y mensajes del negocio: se borra al vencer la retención.</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("""
            DELETE meta.AutomationRun WHERE Status IN ('succeeded', 'partial', 'failed', 'canceled')
               AND CreatedAt < DATEADD(day, -@days, SYSUTCDATETIME())
            """, new { days = options.RetentionDays });
    }

    private static string? Truncate(string? s) => s is { Length: > 500 } ? s[..500] : s;
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}

/// <summary>Spec 016: ocurrencias y bandeja en segundo plano, cada <c>Automations:PollSeconds</c>.</summary>
public sealed class AutomationDispatcher(IServiceScopeFactory scopes, AutomationOptions options, ILogger<AutomationDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var took = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<AutomationService>();
                took = await svc.DispatchBatchAsync(stoppingToken);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(1))
                {
                    await svc.PurgeAsync(stoppingToken);
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Error en la bandeja de automatizaciones"); }
            if (took < AutomationOptions.BatchSize)
                try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
