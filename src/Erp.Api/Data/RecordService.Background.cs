using Dapper;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>Spec 016: el trabajo ya no aplica (registro, automatización o identidad); se cancela con el motivo.</summary>
public sealed class RunCanceled(string reason) : Exception(reason);

/// <summary>Spec 021 (H3 CA2): otra instancia retomó el trabajo (el lease venció); lo hecho aquí se revierte y no se registra.</summary>
public sealed class LeaseLost() : Exception("Otra instancia retomó el trabajo.");

public sealed record RunError(long RecordId, string Message);

/// <summary>Resultado de una ocurrencia de tarea programada (o de lo que lleva, para mostrar el avance).</summary>
public sealed record ScheduleOutcome(int Processed, int Succeeded, int Failed, int Skipped, bool More, IReadOnlyList<RunError> Errors);

/// <summary>Spec 016: qué errores de un trabajo en segundo plano vale la pena repetir, y cómo se cuentan.</summary>
public static class BackgroundErrors
{
    /// <summary>Interbloqueo (1205), bloqueo vencido (1222), tiempo agotado (-2) u otra operación sobre el mismo registro (409).</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        SqlException sql => sql.Number is 1205 or 1222 or -2,
        AutomationFailed af => af.Transient,
        Conflict => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>Mensaje para la bitácora: el del motor si es del negocio; uno genérico si no (el detalle va al registro del servidor).</summary>
    public static string Message(Exception ex) => ex switch
    {
        ValidationFailed v when v.Errors is { Count: > 0 } errs => string.Join(" ", errs.Values.SelectMany(x => x).Distinct()),
        AppException a => a.Message,
        RunCanceled c => c.Message,
        ExprException x => $"La consulta o una expresión no se pudo evaluar: {x.Message}",
        _ when IsTransient(ex) => "Otra operación estaba usando el mismo registro.",
        _ => "Error inesperado; el detalle quedó en el registro del servidor.",
    };
}

/// <summary>
/// Spec 016 · ejecución en segundo plano: trabajos asíncronos (una automatización con <c>mode: async</c>) y ocurrencias de
/// tareas programadas. Usa el mismo núcleo que las automatizaciones de la 005; quien llama (<c>AutomationService</c>)
/// decide la identidad, los reintentos y la bitácora.
/// </summary>
public sealed partial class RecordService
{
    public const int MaxAsyncGenerations = 5;
    public const int MaxRunErrors = 50;

    /// <summary>La auditoría toma al autor de la petición: en segundo plano se arma una con la identidad del trabajo.</summary>
    internal static HttpContext BackgroundHttp(UserContext user)
    {
        var http = new DefaultHttpContext();
        http.Items[UserContextExtensions.ItemKey] = user;
        return http;
    }

    private static async Task EnqueueAutomationAsync(AutoCtx c, EntityDef e, long id, Automation auto, string ev, string? key)
    {
        var generation = c.Generation + 1;
        if (generation > MaxAsyncGenerations)
            throw new AutomationFailed(auto.Label, $"las automatizaciones asíncronas se encadenaron más de {MaxAsyncGenerations} veces " +
                                                   $"({string.Join(" › ", [.. c.Chain, $"{e.Label}: {auto.Label}"])}). Revisa si hay un ciclo.");
        await c.Cn.ExecuteAsync("""
            INSERT meta.AutomationRun (Kind, EntityName, Name, RecordId, Event, EventKey, TriggeredBy, RunAsRole, RunAsName, CompanyId, Generation, Status)
            VALUES ('async', @entity, @name, @id, @ev, @key, @uid, @role, @runAs, @company, @generation, 'pending')
            """, new
        {
            entity = e.Name, name = auto.Name, id, ev, key, uid = c.User.Id, role = c.User.ServiceRoleName,
            runAs = c.User.Username, company = c.User.Company?.Id, generation,
        }, c.Tx);
    }

    /// <summary>
    /// Corre un trabajo asíncrono en su propia transacción, con los datos actuales. Lanza <see cref="RunCanceled"/> si ya no
    /// aplica. Spec 021 (H3 CA1): <paramref name="beforeCommit"/> marca el éxito en la misma transacción (o lanza
    /// <see cref="LeaseLost"/>): los efectos y el estado se guardan juntos.
    /// </summary>
    internal async Task RunQueuedAsync(string entityName, string name, long id, UserContext user, int generation,
        Func<SqlConnection, SqlTransaction, Task> beforeCommit, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        if (!model.TryGetValue(entityName, out var e) || e.IsArchived) throw new RunCanceled("El catálogo ya no existe.");
        var auto = e.Automations?.FirstOrDefault(a => a.IsAsync && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new RunCanceled("La automatización ya no existe o ya no es asíncrona.");
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        if (await LoadDraftAsync(cn, tx, e, model, id, ct) is null) throw new RunCanceled("El registro ya no existe.");
        var c = new AutoCtx(cn, tx, model, user, BackgroundHttp(user), 1, [$"{e.Label}: {auto.Label}"], ct, Generation: generation);
        await RunActionsAsync(c, e, id, auto);
        await beforeCommit(cn, tx);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Una ocurrencia de tarea: selecciona hasta <c>batchSize</c> registros (consulta + alcance del rol, por empresa en un
    /// catálogo por empresa) y procesa cada uno en su propia transacción. <paramref name="progress"/> recibe el avance.
    /// </summary>
    internal async Task<ScheduleOutcome> RunScheduleAsync(string entityName, string name, string runAs,
        Func<ScheduleOutcome, Task> progress, Action<Exception> unexpected, Func<SqlConnection, SqlTransaction, Task> beforeCommit, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        if (!model.TryGetValue(entityName, out var e) || e.IsArchived) throw new RunCanceled("El catálogo ya no existe.");
        var s = e.Schedules?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new RunCanceled("La tarea ya no existe.");
        var auto = new Automation { Name = s.Name, Label = s.Label, On = "schedule", Actions = s.Actions };

        await using var cn = await db.OpenAsync(ct);
        var budget = s.EffectiveBatchSize;
        int ok = 0, failed = 0, skipped = 0;
        var more = false;
        var errors = new List<RunError>();
        ScheduleOutcome Outcome() => new(ok + failed + skipped, ok, failed, skipped, more, errors);

        foreach (var company in await ScheduleCompaniesAsync(cn, e))
        {
            var svc = await ScheduleAccount.LoadAsync(cn, null, s.Role, company, runAs);
            if (!svc.Access(e.Name).Read)
                throw new AppException(StatusCodes.Status403Forbidden,
                    $"El rol «{s.Role}» no puede leer {e.LabelPlural.ToLowerInvariant()}: dale permiso de lectura o cambia el rol de la tarea.");
            var (sql, pars) = ScheduleQuery(e, s, model, svc, Math.Max(budget, 0) + 1, lockId: null);
            var ids = (await cn.QueryAsync<long>(sql, Params(pars))).ToList();
            if (ids.Count > budget) more = true;
            foreach (var id in ids.Take(budget))
            {
                ct.ThrowIfCancellationRequested();
                budget--;
                switch (await RunScheduledRecordAsync(cn, e, s, auto, model, svc, id, unexpected, beforeCommit, ct))
                {
                    case null: ok++; break;
                    case "": skipped++; break;
                    case var message:
                        failed++;
                        if (errors.Count < MaxRunErrors) errors.Add(new(id, message));
                        break;
                }
                await progress(Outcome());
            }
        }
        return Outcome();
    }

    /// <summary>null = correcto; "" = ya no cumple la consulta o salió del alcance; otro = el error.</summary>
    private async Task<string?> RunScheduledRecordAsync(SqlConnection cn, EntityDef e, ScheduleDef s, Automation auto,
        IReadOnlyDictionary<string, EntityDef> model, UserContext svc, long id, Action<Exception> unexpected,
        Func<SqlConnection, SqlTransaction, Task> beforeCommit, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            // Se vuelve a comprobar con el registro bloqueado: otra ejecución o una persona pudo cambiarlo desde la selección.
            var (sql, pars) = ScheduleQuery(e, s, model, svc, 1, lockId: id);
            if (await cn.ExecuteScalarAsync<long?>(sql, Params(pars), tx) is null)
            {
                await tx.RollbackAsync(ct);
                return "";
            }
            var c = new AutoCtx(cn, tx, model, svc, BackgroundHttp(svc), 1, [$"{e.Label}: {s.Label}"], ct);
            await RunActionsAsync(c, e, id, auto);
            // Spec 021 (H3 CA3): cada registro confirma solo si la ocurrencia sigue siendo de esta instancia.
            await beforeCommit(cn, tx);
            await tx.CommitAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LeaseLost)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
            if (ex is not AppException && !BackgroundErrors.IsTransient(ex)) unexpected(ex);
            return BackgroundErrors.Message(ex);
        }
    }

    /// <summary>
    /// Consulta de una tarea: la condición <c>where</c> (traductor de acumulados, parámetros <c>@rf…</c>) y el alcance de
    /// lectura del rol de servicio (006/010, parámetros <c>@sec…</c>). Con <paramref name="lockId"/>, solo ese registro y bloqueado.
    /// </summary>
    internal static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) ScheduleQuery(EntityDef e, ScheduleDef s,
        IReadOnlyDictionary<string, EntityDef> model, UserContext svc, int top, long? lockId)
    {
        var bag = new RecordFilter.ParamBag("@rf");
        var where = RecordFilter.TranslateRollup(s.Where, e, model, bag);
        var scope = RecordFilter.For(e, svc, write: false);
        var pars = new List<(string, object)>([.. bag.Items, .. scope?.Parameters ?? []]);
        var filter = $"t.[IsDeleted] = 0 AND {where}{(scope is null ? "" : $" AND {scope.Sql}")}";
        if (lockId is { } id)
        {
            pars.Add(("@id", id));
            return ($"SELECT t.[Id] FROM {AppTable(e.Name)} t WITH (UPDLOCK, HOLDLOCK) WHERE t.[Id] = @id AND {filter}", pars);
        }
        return ($"SELECT TOP ({Math.Clamp(top, 1, ScheduleDef.MaxBatchSize + 1)}) t.[Id] FROM {AppTable(e.Name)} t WHERE {filter} ORDER BY t.[Id]", pars);
    }

    private static DynamicParameters Params(IEnumerable<(string Name, object Value)> pars)
    {
        var p = new DynamicParameters();
        foreach (var (name, value) in pars) p.Add(name, value == DBNull.Value ? null : value);
        return p;
    }

    /// <summary>Spec 010: en un catálogo por empresa, cada empresa activa con su contexto; si no, la predeterminada.</summary>
    private static async Task<IReadOnlyList<CompanyInfo?>> ScheduleCompaniesAsync(SqlConnection cn, EntityDef e)
    {
        var list = (await cn.QueryAsync<(int Id, string Code, string Name, string BaseCurrency)>(
                "SELECT Id, Code, Name, BaseCurrency FROM meta.Company WHERE IsActive = 1 ORDER BY IsDefault DESC, Name"))
            .Select(r => (CompanyInfo?)new CompanyInfo(r.Id, r.Code, r.Name, r.BaseCurrency.Trim())).ToList();
        return e.PerCompany ? list : [list.FirstOrDefault()];
    }
}
