using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Erp.Api.Approvals;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <param name="RowVersion">Versión del documento que vio la persona.</param>
/// <param name="ApprovalVersion">Versión de la autorización que vio (<c>_approval.version</c>).</param>
public sealed record DecisionRequest(string? Decision, string? Comment,
    [property: System.Text.Json.Serialization.JsonPropertyName("_rowVersion")] string? RowVersion, string? ApprovalVersion);

public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Reset = "reset";
}

public static class ApprovalDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Skip = "skip";
}

/// <summary>
/// Spec 053: el motor de la matriz de autorización. Entrar al estado elige la regla y la congela; salir por otra transición cancela;
/// un cambio de monto o de condición reinicia; decidir avanza por niveles y, al final, ejecuta la transición de salida. Toda
/// escritura a corridas y a la lista de autorizadores toma el candado <see cref="LockResource"/> después de los bloqueos del
/// documento, y la lista solo la escribe el motor (<see cref="SaveMultiAsync"/> la salta).
/// </summary>
public sealed partial class RecordService
{
    public const string LockResource = "erp:approval";

    private sealed record RunRow(long Id, int RuleId, string RuleJson, decimal? Amount, byte[] ConditionsHash, int? CurrentLevel,
        string Status, byte[] RowVer, int StartedBy, DateTime StartedAt, DateTime? EndedAt, int? CreatorId);

    private sealed record DecisionRow(long Id, int Level, int ProfileId, int? UserId, string? UserName, string Decision, string? Comment,
        bool Superseded, DateTime At, byte[]? Fingerprint);

    /// <summary>¿<paramref name="f"/> es la lista de autorizadores de <paramref name="e"/>? (solo la escribe el motor).</summary>
    private static bool IsApprovers(EntityDef e, FieldDef f) =>
        e.Workflow?.Approval is { } a && string.Equals(a.Approvers, f.Name, StringComparison.OrdinalIgnoreCase);

    internal static Task LockApprovalsAsync(SqlConnection cn, SqlTransaction tx, CancellationToken ct) =>
        cn.ExecuteAsync(new CommandDefinition("""
            DECLARE @r int;
            EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
            IF @r < 0 THROW 51053, 'No se obtuvo el candado de autorizaciones.', 1;
            """, new { res = LockResource }, tx, cancellationToken: ct));

    private static async Task<RunRow?> PendingRunAsync(SqlConnection cn, SqlTransaction? tx, EntityDef e, long id, bool lockRow, CancellationToken ct) =>
        await cn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition($"""
            SELECT Id, RuleId, RuleJson, Amount, ConditionsHash, CurrentLevel, Status, RowVer, StartedBy, StartedAt, EndedAt, CreatorId
              FROM meta.ApprovalRun {(lockRow ? "WITH (UPDLOCK, HOLDLOCK)" : "")}
             WHERE EntityName = @e AND RecordId = @id AND Status = 'pending'
            """, new { e = e.Name, id }, tx, cancellationToken: ct));

    /// <summary>
    /// Personas que pueden autorizar ahora el nivel de <paramref name="profileId"/>: activas en el perfil (al momento), no de servicio
    /// ni equipo, con acceso a la empresa, sin el creador ni quien ya decidió en esta corrida. Sin <paramref name="run"/>, todas las
    /// personas activas del perfil (para saber si el nivel está vacío).
    /// </summary>
    private static async Task<List<(int Id, string Name)>> ProfilePeopleAsync(SqlConnection cn, SqlTransaction? tx, int profileId, int companyId,
        long? run, int? creator, CancellationToken ct, int? starter = null) =>
        (await cn.QueryAsync<(int, string)>(new CommandDefinition("""
            SELECT u.Id, u.DisplayName
              FROM meta.ApprovalProfileMember m
              JOIN meta.ApprovalProfile p ON p.Id = m.ProfileId
              JOIN meta.[User] u ON u.Id = m.UserId
             WHERE m.ProfileId = @profileId AND p.IsActive = 1 AND p.CompanyId = @companyId
               AND u.IsActive = 1 AND u.IsTeam = 0 AND u.IsService = 0
               AND (EXISTS (SELECT 1 FROM meta.UserCompany uc WHERE uc.UserId = u.Id AND uc.CompanyId = @companyId)
                    OR EXISTS (SELECT 1 FROM meta.UserRole ur JOIN meta.Role r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id AND r.IsSuperuser = 1))
               AND (@creator IS NULL OR u.Id <> @creator)
               AND (@starter IS NULL OR u.Id <> @starter)
               AND (@run IS NULL OR NOT EXISTS (SELECT 1 FROM meta.ApprovalDecision d WHERE d.RunId = @run AND d.UserId = u.Id))
             ORDER BY u.DisplayName
            """, new { profileId, companyId, run, creator, starter }, tx, cancellationToken: ct))).ToList();

    /// <summary>
    /// Revisión adversarial 053 (F-01): un nivel está vacío —y se salta al llegar— solo por configuración de la matriz (el perfil
    /// está inactivo o no tiene personas), que pasa por la compuerta de seguridad. Desactivar a una persona, quitarle la empresa o el
    /// rol no vacía el nivel: el documento espera.
    /// </summary>
    private static async Task<bool> ProfileConfiguredAsync(SqlConnection cn, SqlTransaction tx, int profileId, CancellationToken ct) =>
        await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT TOP 1 1 FROM meta.ApprovalProfile p JOIN meta.ApprovalProfileMember m ON m.ProfileId = p.Id
             WHERE p.Id = @profileId AND p.IsActive = 1
            """, new { profileId }, tx, cancellationToken: ct)) is not null;

    private static FrozenRule Frozen(RunRow run) => JsonSerializer.Deserialize<FrozenRule>(run.RuleJson, ApprovalMatrix.Json)!;

    /// <summary>Reemplaza la lista de autorizadores del documento (solo el motor, con el candado tomado).</summary>
    private static async Task SetApproversAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, long id, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var table = JoinTable(e.Name, e.Workflow!.Approval!.Approvers);
        await cn.ExecuteAsync(new CommandDefinition($"""
            DELETE FROM {table} WHERE [OwnerId] = @id AND [ValueId] NOT IN (SELECT CAST([value] AS bigint) FROM OPENJSON(@ids));
            INSERT INTO {table} ([OwnerId], [ValueId])
            SELECT @id, CAST(v.[value] AS bigint) FROM OPENJSON(@ids) v
             WHERE NOT EXISTS (SELECT 1 FROM {table} x WHERE x.[OwnerId] = @id AND x.[ValueId] = CAST(v.[value] AS bigint));
            """, new { id, ids = JsonSerializer.Serialize(ids) }, tx, cancellationToken: ct));
    }

    /// <summary>Recalcula la lista de autorizadores de una corrida pendiente (tras avanzar o cambiar las personas de un perfil).</summary>
    internal static async Task RefreshApproversAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, long recordId, CancellationToken ct)
    {
        var run = await PendingRunAsync(cn, tx, e, recordId, lockRow: false, ct);
        var people = new List<(int Id, string Name)>();
        if (run?.CurrentLevel is { } level)
        {
            // Revisión adversarial 053 (F-07): sin leer la fila del documento bajo el candado (otra transacción puede tenerla en X y
            // esperar el candado): la empresa, el creador y quien envió viven en la corrida.
            var company = await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CompanyId FROM meta.ApprovalRun WHERE Id = @id",
                new { id = run.Id }, tx, cancellationToken: ct));
            var frozen = Frozen(run).Levels.First(l => l.Level == level);
            people = await ProfilePeopleAsync(cn, tx, frozen.ProfileId, company, run.Id, run.CreatorId, ct, run.StartedBy);
        }
        await SetApproversAsync(cn, tx, e, recordId, people.Select(p => p.Id).ToList(), ct);
    }

    /// <summary>
    /// Spec 053: gancho de <see cref="ApplyTransitionAsync"/> tras escribir el encabezado. Entrar al estado empieza la corrida (falla
    /// cerrado); salir por una transición que no es la de autorizar o rechazar la cancela; de un estado de autorización al mismo,
    /// solo revisa si hay que reiniciar.
    /// </summary>
    private async Task ApprovalOnTransitionAsync(AutoCtx c, EntityDef e, long id, DocDraft draft, string? from, WorkflowTransition t)
    {
        if (e.Workflow?.Approval is not { } a) return;
        var leaving = string.Equals(from, a.State, StringComparison.OrdinalIgnoreCase);
        var entering = string.Equals(t.To, a.State, StringComparison.OrdinalIgnoreCase);
        if (leaving && entering) { await ApprovalOnChangeAsync(c, e, id, draft); return; }
        if (!leaving && !entering) return;
        await LockApprovalsAsync(c.Cn, c.Tx, c.Ct);
        if (leaving)
        {
            if (await PendingRunAsync(c.Cn, c.Tx, e, id, lockRow: true, c.Ct) is { } run)
                await CloseRunAsync(c, e, id, run, ApprovalStatus.Cancelled, AuditActions.ApprovalCancel, new { transition = t.Name });
            await SetApproversAsync(c.Cn, c.Tx, e, id, [], c.Ct);
            return;
        }
        await StartRunAsync(c, e, id, draft, a);
    }

    /// <summary>
    /// Spec 053 (H5 CA2): tras guardar el encabezado (edición, automatización o transición al mismo estado), si la autorización está
    /// en curso y cambió el monto o un campo de condición, se reinicia con la matriz vigente (y falla cerrado si ya no aplica ninguna).
    /// </summary>
    private async Task ApprovalOnChangeAsync(AutoCtx c, EntityDef e, long id, DocDraft draft)
    {
        if (e.Workflow?.Approval is not { } a
            || !string.Equals(draft.Header.GetValueOrDefault(Workflow.StateField) as string, a.State, StringComparison.OrdinalIgnoreCase)) return;
        await LockApprovalsAsync(c.Cn, c.Tx, c.Ct);
        if (await PendingRunAsync(c.Cn, c.Tx, e, id, lockRow: true, c.Ct) is not { } run) return;
        if (ApprovalMatrix.ConditionsHash(a, draft.Header).AsSpan().SequenceEqual(run.ConditionsHash)) return;
        await c.Cn.ExecuteAsync(new CommandDefinition("UPDATE meta.ApprovalDecision SET Superseded = 1 WHERE RunId = @id",
            new { id = run.Id }, c.Tx, cancellationToken: c.Ct));
        // Revisión adversarial 053 (F-09): sin el monto ni las condiciones (campos que quizá el auditor no ve).
        await CloseRunAsync(c, e, id, run, ApprovalStatus.Reset, AuditActions.ApprovalReset, new { reason = "conditions" });
        await StartRunAsync(c, e, id, draft, a);
    }

    /// <summary>Revisión adversarial 053 (F-11): borrar un documento en espera cancela su autorización y vacía la lista.</summary>
    private async Task ApprovalOnDeleteAsync(AutoCtx c, EntityDef e, long id)
    {
        if (e.Workflow?.Approval is null) return;
        await LockApprovalsAsync(c.Cn, c.Tx, c.Ct);
        if (await PendingRunAsync(c.Cn, c.Tx, e, id, lockRow: true, c.Ct) is not { } run) return;
        await CloseRunAsync(c, e, id, run, ApprovalStatus.Cancelled, AuditActions.ApprovalCancel, new { deleted = true });
        await SetApproversAsync(c.Cn, c.Tx, e, id, [], c.Ct);
    }

    private static async Task CloseRunAsync(AutoCtx c, EntityDef e, long id, RunRow run, string status, string auditAction, object? detail)
    {
        await c.Cn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ApprovalRun SET Status = @status, EndedAt = SYSUTCDATETIME() WHERE Id = @id AND Status = 'pending'",
            new { status, id = run.Id }, c.Tx, cancellationToken: c.Ct));
        await Audit.WriteAsync(c.Cn, c.Tx, c.Http, auditAction, e.Name, id.ToString(CultureInfo.InvariantCulture),
            after: new { run = run.Id, rule = Frozen(run).Name, level = run.CurrentLevel, detail });
    }

    /// <summary>Elige la regla, la congela, salta lo que no aplica y deja la corrida en el primer nivel con personas (H4 CA1–CA2).</summary>
    private async Task StartRunAsync(AutoCtx c, EntityDef e, long id, DocDraft draft, WorkflowApproval a)
    {
        var (cn, tx, ct) = (c.Cn, c.Tx, c.Ct);
        var company = draft.CompanyId ?? throw new AppException(400, "El documento no tiene empresa: la matriz de autorización es por empresa.");
        var rules = await ApprovalConfig.RulesAsync(cn, tx, company, e.Name, activeOnly: true, ct);
        var profiles = (await ApprovalConfig.ProfilesAsync(cn, tx, company, ct)).ToDictionary(p => p.Id);
        var amount = ApprovalMatrix.AmountOf(a, draft.Header);
        // Revisión adversarial 053 (F-10, F-14): un monto negativo o fuera de la precisión de la corrida no entra (falla cerrado).
        if (amount is < 0 or >= 1_000_000_000_000_000m)
            throw new AppException(400, "El monto para autorizar debe ser positivo y menor a mil billones.");
        if (amount is { } am) amount = decimal.Round(am, 4);
        // Revisión adversarial 053 (F-05): una corrida pendiente que quedó huérfana (el proceso cambió) se cancela antes de empezar otra.
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ApprovalRun SET Status = 'cancelled', EndedAt = SYSUTCDATETIME() WHERE EntityName = @e AND RecordId = @id AND Status = 'pending'",
            new { e = e.Name, id }, tx, cancellationToken: ct));
        var rule = ApprovalMatrix.Pick(rules, ApprovalMatrix.ConditionValues(a, draft.Header), amount, DateTime.Today)
                   ?? throw new AppException(400, $"Ninguna regla de autorización aplica a este documento de {e.LabelPlural.ToLowerInvariant()}: pide al administrador que la configure.");
        var frozen = ApprovalMatrix.Freeze(rule, profiles, amount, a.Amount is not null);
        var runId = await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT meta.ApprovalRun (EntityName, RecordId, CompanyId, RuleId, RuleJson, Amount, ConditionsHash, CurrentLevel, Status, StartedBy, CreatorId)
            OUTPUT inserted.Id VALUES (@e, @id, @company, @rule, @json, @amount, @hash, NULL, 'pending', @uid, @creator)
            """, new { e = e.Name, id, company, rule = rule.Id, creator = draft.CreatorId, json = JsonSerializer.Serialize(frozen, ApprovalMatrix.Json), amount,
                hash = ApprovalMatrix.ConditionsHash(a, draft.Header), uid = c.User.Id }, tx, cancellationToken: ct));
        foreach (var l in frozen.Levels.Where(l => l.Skipped is not null))
            await InsertDecisionAsync(cn, tx, runId, l, null, ApprovalDecisions.Skip, "range", null, ct);
        var level = await NextLevelAsync(cn, tx, runId, frozen, company, after: 0, ct);
        if (level is null)
            throw new AppException(400, $"La regla de autorización «{rule.Name}» no tiene a nadie que autorice este documento: pide al administrador que revise sus perfiles.");
        await cn.ExecuteAsync(new CommandDefinition("UPDATE meta.ApprovalRun SET CurrentLevel = @level WHERE Id = @runId",
            new { level, runId }, tx, cancellationToken: ct));
        await Audit.WriteAsync(cn, tx, c.Http, AuditActions.ApprovalStart, e.Name, id.ToString(CultureInfo.InvariantCulture),
            after: new { run = runId, rule = rule.Name, ruleId = rule.Id, level, levels = frozen.Levels.Select(l => new { l.Level, l.Profile, l.Skipped }) });
        await RefreshApproversAsync(cn, tx, e, id, ct);
    }

    /// <summary>
    /// El siguiente nivel después de <paramref name="after"/> que tiene personas activas en su perfil; los vacíos se saltan al llegar
    /// a ellos (decisión «skip», «empty»). Null si no queda ninguno.
    /// </summary>
    private static async Task<int?> NextLevelAsync(SqlConnection cn, SqlTransaction tx, long runId, FrozenRule frozen, int company, int after, CancellationToken ct)
    {
        foreach (var l in frozen.Levels.Where(l => l.Level > after && l.Skipped is null))
        {
            if (await ProfileConfiguredAsync(cn, tx, l.ProfileId, ct)) return l.Level;
            await InsertDecisionAsync(cn, tx, runId, l, null, ApprovalDecisions.Skip, "empty", null, ct);
        }
        return null;
    }

    private static Task InsertDecisionAsync(SqlConnection cn, SqlTransaction tx, long runId, FrozenLevel l, int? userId, string decision,
        string? comment, byte[]? fingerprint, CancellationToken ct) =>
        cn.ExecuteAsync(new CommandDefinition("""
            INSERT meta.ApprovalDecision (RunId, Level, ProfileId, UserId, Decision, Comment, Fingerprint)
            VALUES (@runId, @Level, @ProfileId, @userId, @decision, @comment, @fingerprint)
            """, new { runId, l.Level, l.ProfileId, userId, decision, comment, fingerprint }, tx, cancellationToken: ct));

    /// <summary>
    /// Spec 053 (H3): autorizar o rechazar el nivel en curso. Bloquea el documento y la corrida, exige las versiones que vio la persona
    /// (huella), su turno (perfil al momento, no creador, no decidió otro nivel, ve el registro) y, en el último nivel o al rechazar,
    /// ejecuta la transición de salida con la persona como actor. Todo en una transacción.
    /// </summary>
    public async Task<Dictionary<string, object?>> DecideAsync(string entityName, long id, DecisionRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        var a = e.Workflow?.Approval ?? throw new AppException(400, "Este catálogo no se autoriza por matriz.");
        if (req.Decision is not (ApprovalDecisions.Approve or ApprovalDecisions.Reject))
            throw new AppException(400, "La decisión debe ser «approve» o «reject».");
        var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        if (comment?.Length > ApprovalMatrix.MaxComment)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = [$"El comentario admite máximo {ApprovalMatrix.MaxComment} caracteres."] });
        if (req.Decision == ApprovalDecisions.Reject && comment is null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = ["Escribe el motivo del rechazo."] });
        var rowVersion = ReadRowVersion(req.RowVersion);
        var approvalVersion = ReadRowVersion(req.ApprovalVersion);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        // Nadie aprueba lo que no ve (006): 404 igual que si no existiera.
        await EnsureAccessAsync(cn, tx, e, model, id, user, write: false, ct);
        var current = await cn.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            $"SELECT [RowVer] FROM {AppTable(e.Name)} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id AND [IsDeleted] = 0", new { id }, tx, cancellationToken: ct))
            ?? throw new NotFound("El registro no existe.");
        var draft = (await LoadDraftAsync(cn, tx, e, model, id, ct))!;
        if (!string.Equals(draft.Header.GetValueOrDefault(Workflow.StateField) as string, a.State, StringComparison.OrdinalIgnoreCase))
            throw new Conflict("El documento ya no está pendiente de autorización. Recarga para ver su estado.");
        if (!current.AsSpan().SequenceEqual(rowVersion))
            throw new Conflict("El documento cambió mientras lo revisabas. Recarga para ver los cambios.");
        // Revisión adversarial 053 (F-07): lo que tocarán las automatizaciones de la salida se bloquea antes del candado (como 012 H3).
        var exit = e.Workflow!.FindTransition(req.Decision == ApprovalDecisions.Approve ? a.Approve : a.Reject)!;
        if (await LockAutomationTargetsAsync(cn, tx, e, model, draft, exit, ct)) draft = (await LoadDraftAsync(cn, tx, e, model, id, ct))!;
        await LockApprovalsAsync(cn, tx, ct);
        var run = await PendingRunAsync(cn, tx, e, id, lockRow: true, ct)
                  ?? throw new Conflict("El documento ya no está pendiente de autorización. Recarga para ver su estado.");
        if (!run.RowVer.AsSpan().SequenceEqual(approvalVersion))
            throw new Conflict("La autorización avanzó mientras la revisabas. Recarga para ver en qué nivel va.");
        var frozen = Frozen(run);
        var level = frozen.Levels.First(l => l.Level == run.CurrentLevel);
        var company = draft.CompanyId!.Value;
        var can = await ProfilePeopleAsync(cn, tx, level.ProfileId, company, run.Id, run.CreatorId, ct, run.StartedBy);
        if (can.All(p => p.Id != user.Id))
            throw new Forbidden("No te toca autorizar este documento: " + await WhyNotAsync(cn, tx, run, level, company, draft, user, ct));

        var before = Snapshot(draft);
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            rowVersion = Convert.ToBase64String(current), approvalVersion = Convert.ToBase64String(run.RowVer), record = before,
        }, ApprovalMatrix.Json)));
        await InsertDecisionAsync(cn, tx, run.Id, level, user.Id, req.Decision, comment, fingerprint, ct);
        var c = new AutoCtx(cn, tx, model, user, http, 0, [], ct);
        var auditAction = req.Decision == ApprovalDecisions.Approve ? AuditActions.ApprovalApprove : AuditActions.ApprovalReject;
        await Audit.WriteAsync(cn, tx, http, auditAction, e.Name, id.ToString(CultureInfo.InvariantCulture),
            after: new { run = run.Id, rule = frozen.Name, level = level.Level, profile = level.Profile, comment, fingerprint = Convert.ToHexStringLower(fingerprint) });

        var next = req.Decision == ApprovalDecisions.Approve
            ? await NextLevelAsync(cn, tx, run.Id, frozen, company, after: level.Level, ct)
            : null;
        if (next is not null)
        {
            await cn.ExecuteAsync(new CommandDefinition("UPDATE meta.ApprovalRun SET CurrentLevel = @next WHERE Id = @runId",
                new { next, runId = run.Id }, tx, cancellationToken: ct));
            await RefreshApproversAsync(cn, tx, e, id, ct);
        }
        else
        {
            var done = req.Decision == ApprovalDecisions.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            await cn.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.ApprovalRun SET Status = @done, EndedAt = SYSUTCDATETIME() WHERE Id = @runId",
                new { done, runId = run.Id }, tx, cancellationToken: ct));
            await ApplyTransitionAsync(c, e, id, draft, before, exit, comment, rowVersion: null);
        }
        // Revisión adversarial 053 (F-13): quien decidió y ya no le toca deja de ver el registro: solo sabe que se guardó.
        if (RecordFilter.For(e, user, write: false, model) is { } scope && !await MatchesAsync(cn, tx, e, id, scope, ct))
        {
            await tx.CommitAsync(ct);
            return new Dictionary<string, object?> { ["id"] = id, ["_gone"] = true };
        }
        var row = (await LoadOutputAsync(cn, tx, e, model, id, user, ct, warnings: true))!;
        await tx.CommitAsync(ct);
        return row;
    }

    /// <summary>Por qué no le toca (para el 403 y el panel): lo creó, ya decidió, o no está en el perfil del nivel en curso.</summary>
    private static async Task<string> WhyNotAsync(SqlConnection cn, SqlTransaction? tx, RunRow run, FrozenLevel level, int company, DocDraft draft,
        UserContext user, CancellationToken ct)
    {
        var inProfile = (await ProfilePeopleAsync(cn, tx, level.ProfileId, company, null, null, ct)).Any(p => p.Id == user.Id);
        if (inProfile && run.CreatorId == user.Id) return "lo creaste tú (control interno).";
        if (inProfile && run.StartedBy == user.Id) return "tú lo mandaste a autorizar (control interno).";
        var decided = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 Level FROM meta.ApprovalDecision WHERE RunId = @id AND UserId = @uid", new { id = run.Id, uid = user.Id }, tx, cancellationToken: ct));
        if (decided is { } l) return $"ya decidiste el nivel {l} de esta autorización.";
        return $"el nivel {level.Level} lo autoriza el perfil «{level.Profile}» y no estás en él.";
    }

    /// <summary>Spec 053 (H3 CA1): el panel del registro: la última corrida, sus niveles, decisiones, quién puede ahora y si me toca.</summary>
    private static async Task<object?> ApprovalOutputAsync(SqlConnection cn, SqlTransaction? tx, EntityDef e, long id, DocDraft draft, UserContext user, CancellationToken ct)
    {
        if (e.Workflow?.Approval is null) return null;
        var run = await cn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition("""
            SELECT TOP 1 Id, RuleId, RuleJson, Amount, ConditionsHash, CurrentLevel, Status, RowVer, StartedBy, StartedAt, EndedAt, CreatorId
              FROM meta.ApprovalRun WHERE EntityName = @e AND RecordId = @id ORDER BY Id DESC
            """, new { e = e.Name, id }, tx, cancellationToken: ct));
        if (run is null) return null;
        var frozen = Frozen(run);
        var decisions = (await cn.QueryAsync<DecisionRow>(new CommandDefinition("""
            SELECT d.Id, d.Level, d.ProfileId, d.UserId, u.DisplayName AS UserName, d.Decision, d.Comment, d.Superseded, d.At, d.Fingerprint
              FROM meta.ApprovalDecision d LEFT JOIN meta.[User] u ON u.Id = d.UserId
             WHERE d.RunId = @id ORDER BY d.Id
            """, new { id = run.Id }, tx, cancellationToken: ct))).ToList();
        var pending = run.Status == ApprovalStatus.Pending;
        var current = pending ? frozen.Levels.FirstOrDefault(l => l.Level == run.CurrentLevel) : null;
        var approvers = current is null || draft.CompanyId is not { } company ? []
            : await ProfilePeopleAsync(cn, tx, current.ProfileId, company, run.Id, run.CreatorId, ct, run.StartedBy);
        string? waiting = null;
        if (current is not null && approvers.Count == 0)
            waiting = "Nadie puede autorizar este nivel: las personas del perfil crearon o enviaron el documento, ya decidieron otro nivel o están inactivas. Pide al administrador que agregue a alguien.";
        static DateTime U(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return new
        {
            status = run.Status,
            rule = frozen.Name,
            level = run.CurrentLevel,
            version = Convert.ToBase64String(run.RowVer),
            startedAt = U(run.StartedAt),
            endedAt = run.EndedAt is { } end ? U(end) : (DateTime?)null,
            levels = frozen.Levels.Select(l =>
            {
                var mine = decisions.Where(d => d.Level == l.Level).ToList();
                var skip = mine.FirstOrDefault(d => d.Decision == ApprovalDecisions.Skip);
                var status = skip is not null ? "skipped"
                    : mine.Any(d => d.Decision == ApprovalDecisions.Reject) ? "rejected"
                    : mine.Any(d => d.Decision == ApprovalDecisions.Approve) ? "approved"
                    : current?.Level == l.Level ? "current" : "pending";
                return new
                {
                    level = l.Level, profile = l.Profile, status,
                    reason = skip?.Comment switch
                    {
                        "range" => "El monto no está en el rango del perfil.",
                        "empty" => "El perfil no tenía personas al llegar a este nivel.",
                        _ => null,
                    },
                    decisions = mine.Where(d => d.Decision != ApprovalDecisions.Skip).Select(d => new
                    {
                        user = d.UserName, decision = d.Decision, comment = d.Comment, at = U(d.At),
                        fingerprint = d.Fingerprint is null ? null : Convert.ToHexStringLower(d.Fingerprint),
                    }),
                };
            }),
            approvers = approvers.Select(p => p.Name),
            canDecide = approvers.Any(p => p.Id == user.Id),
            waitingReason = waiting,
        };
    }
}
