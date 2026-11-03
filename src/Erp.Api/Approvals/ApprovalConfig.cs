using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Approvals;

/// <param name="CompanyId">La empresa de quien pidió el cambio (la fija el servidor; viaja en la solicitud de la 015).</param>
public sealed record ApprovalProfileRequest(string? Name, decimal? MinAmount = null, decimal? MaxAmount = null, bool IsActive = true,
    int[]? MemberIds = null, string? Version = null, int? CompanyId = null);

/// <param name="Conditions">Por campo de condición, los valores admitidos (ids, opciones o sí/no); vacío = cualquiera.</param>
public sealed record ApprovalRuleRequest(string? Entity, string? Name, int Priority = 1, bool IsActive = true, DateTime? ValidFrom = null,
    DateTime? ValidTo = null, decimal? MinAmount = null, decimal? MaxAmount = null, Dictionary<string, JsonElement[]>? Conditions = null,
    int[]? Levels = null, string? Version = null, int? CompanyId = null);

public sealed record ApprovalProfileSnapshot(string Name, decimal? MinAmount, decimal? MaxAmount, bool IsActive, IReadOnlyList<int> MemberIds);

public sealed record ApprovalRuleSnapshot(string Entity, string Name, int Priority, bool IsActive, DateTime? ValidFrom, DateTime? ValidTo,
    decimal? MinAmount, decimal? MaxAmount, IReadOnlyDictionary<string, IReadOnlyList<string>> Conditions, IReadOnlyList<int> Levels);

/// <summary>
/// Spec 053 (H2): la matriz de la empresa actual del administrador. Todo cambio pasa por la compuerta de seguridad de la 015
/// (casi cualquier cambio puede aflojar el control: quitar personas, angostar un rango o desactivar un perfil saltan niveles);
/// nadie cambia su propia pertenencia a un perfil. Los cambios de un perfil recalculan, en la misma transacción y con el candado
/// del motor, la lista de autorizadores de los documentos que esperan un nivel de ese perfil.
/// </summary>
public sealed class ApprovalConfig(Db db, MetadataStore store, SecurityApproval gate)
{
    private sealed record ProfileRow(int Id, string Name, decimal? MinAmount, decimal? MaxAmount, bool IsActive, byte[] RowVer);

    private sealed record RuleRow(int Id, string EntityName, string Name, int Priority, bool IsActive, DateTime? ValidFrom, DateTime? ValidTo,
        decimal? MinAmount, decimal? MaxAmount, string ConditionsJson, string LevelsJson, byte[] RowVer);

    public static async Task<List<ApprovalProfileDef>> ProfilesAsync(DbConnection cn, DbTransaction? tx, int company, CancellationToken ct) =>
        (await cn.QueryAsync<ApprovalProfileDef>(new CommandDefinition(
            "SELECT Id, Name, MinAmount, MaxAmount, IsActive FROM meta.ApprovalProfile WHERE CompanyId = @company",
            new { company }, tx, cancellationToken: ct))).ToList();

    public static async Task<List<ApprovalRuleDef>> RulesAsync(DbConnection cn, DbTransaction? tx, int company, string entity, bool activeOnly, CancellationToken ct) =>
        (await cn.QueryAsync<RuleRow>(new CommandDefinition("""
            SELECT Id, EntityName, Name, Priority, IsActive, ValidFrom, ValidTo, MinAmount, MaxAmount, ConditionsJson, LevelsJson, RowVer
              FROM meta.ApprovalRule WHERE CompanyId = @company AND EntityName = @entity AND (@activeOnly = 0 OR IsActive = 1)
            """, new { company, entity, activeOnly }, tx, cancellationToken: ct))).Select(ToDef).ToList();

    private static ApprovalRuleDef ToDef(RuleRow r) => new(r.Id, r.Name, r.Priority, r.ValidFrom, r.ValidTo, r.MinAmount, r.MaxAmount,
        JsonSerializer.Deserialize<Dictionary<string, List<string>>>(r.ConditionsJson, ApprovalMatrix.Json)!
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
        JsonSerializer.Deserialize<List<int>>(r.LevelsJson, ApprovalMatrix.Json)!);

    private static int CompanyOf(UserContext user) =>
        user.Company?.Id ?? throw new Forbidden("No tienes una empresa asignada: la matriz de autorización es por empresa.");

    private static void RequireUserAdmin(UserContext user)
    {
        if (!user.CanManageUsers) throw new Forbidden();
    }

    private static string Version(byte[] rv) => Convert.ToBase64String(rv);

    // ---------------- Lectura ----------------

    public async Task<object> GetAsync(UserContext user, CancellationToken ct)
    {
        RequireUserAdmin(user);
        var company = CompanyOf(user);
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var profiles = (await cn.QueryAsync<ProfileRow>(new CommandDefinition(
            "SELECT Id, Name, MinAmount, MaxAmount, IsActive, RowVer FROM meta.ApprovalProfile WHERE CompanyId = @company ORDER BY Name",
            new { company }, cancellationToken: ct))).ToList();
        var members = (await cn.QueryAsync<(int ProfileId, int Id, string Label)>(new CommandDefinition("""
            SELECT m.ProfileId, u.Id, u.DisplayName FROM meta.ApprovalProfileMember m JOIN meta.ApprovalProfile p ON p.Id = m.ProfileId
              JOIN meta.[User] u ON u.Id = m.UserId WHERE p.CompanyId = @company ORDER BY u.DisplayName
            """, new { company }, cancellationToken: ct))).ToLookup(m => m.ProfileId);
        var rules = (await cn.QueryAsync<RuleRow>(new CommandDefinition("""
            SELECT Id, EntityName, Name, Priority, IsActive, ValidFrom, ValidTo, MinAmount, MaxAmount, ConditionsJson, LevelsJson, RowVer
              FROM meta.ApprovalRule WHERE CompanyId = @company ORDER BY EntityName, Priority, Name
            """, new { company }, cancellationToken: ct))).ToList();
        return new
        {
            company = user.Company!.Name,
            entities = model.Values.Where(e => !e.IsArchived && e.Workflow?.Approval is not null).OrderBy(e => e.LabelPlural)
                .Select(e => new
                {
                    name = e.Name, label = e.LabelPlural,
                    amount = e.Workflow!.Approval!.Amount is { } a ? e.FindField(a)?.Label : null,
                    conditions = (e.Workflow.Approval.Conditions ?? []).Select(e.FindField).OfType<FieldDef>().Select(f => new
                    {
                        field = f.Name, label = f.Label, type = f.Type, options = f.Options, referenceEntity = f.ReferenceEntity,
                    }),
                }),
            profiles = profiles.Select(p => new
            {
                id = p.Id, name = p.Name, minAmount = p.MinAmount, maxAmount = p.MaxAmount, isActive = p.IsActive,
                members = members[p.Id].Select(m => new { id = m.Id, label = m.Label }), version = Version(p.RowVer),
            }),
            rules = rules.Select(r =>
            {
                var d = ToDef(r);
                return new
                {
                    id = r.Id, entity = r.EntityName, name = r.Name, priority = r.Priority, isActive = r.IsActive,
                    validFrom = r.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    validTo = r.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    minAmount = r.MinAmount, maxAmount = r.MaxAmount, conditions = d.Conditions, levels = d.Levels, version = Version(r.RowVer),
                };
            }),
        };
    }

    // ---------------- Perfiles ----------------

    private static ApprovalProfileSnapshot Snapshot(ProfileRow p, IEnumerable<int> members) =>
        new(p.Name, p.MinAmount, p.MaxAmount, p.IsActive, members.Order().ToList());

    public async Task<IResult> SaveProfileAsync(int? id, ApprovalProfileRequest req, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireUserAdmin(user);
        var company = approving is not null ? req.CompanyId ?? throw new Conflict("La solicitud no dice de qué empresa es.") : CompanyOf(user);
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > ApprovalMatrix.MaxName) errors["name"] = [$"El nombre es obligatorio (máx. {ApprovalMatrix.MaxName})."];
        CheckRange(req.MinAmount, req.MaxAmount, errors);
        var memberIds = (req.MemberIds ?? []).Distinct().Order().ToArray();
        if (memberIds.Length > ApprovalMatrix.MaxMembers) errors["memberIds"] = [$"Un perfil tiene máximo {ApprovalMatrix.MaxMembers} personas."];
        if (errors.Count > 0) throw new ValidationFailed(errors);
        req = req with { Name = name, MemberIds = memberIds, CompanyId = company, IsActive = id is null || req.IsActive };

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        // Spec 053: el candado del motor primero: lo que se recalcula abajo no se cruza con una decisión a medias.
        await RecordService.LockApprovalsAsync(cn, tx, ct);
        ProfileRow? before = null;
        int[] beforeMembers = [];
        if (id is not null)
        {
            before = await cn.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
                "SELECT Id, Name, MinAmount, MaxAmount, IsActive, RowVer FROM meta.ApprovalProfile WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id AND CompanyId = @company",
                new { id, company }, tx, cancellationToken: ct)) ?? throw new NotFound("El perfil no existe.");
            beforeMembers = (await cn.QueryAsync<int>(new CommandDefinition("SELECT UserId FROM meta.ApprovalProfileMember WHERE ProfileId = @id",
                new { id }, tx, cancellationToken: ct))).Order().ToArray();
            approving?.Verify(Snapshot(before, beforeMembers));
            if (req.Version is null || Version(before.RowVer) != req.Version)
                throw new Conflict("Otro administrador cambió este perfil. Recarga para ver los cambios.");
        }
        else if (await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM meta.ApprovalProfile WHERE CompanyId = @company",
                     new { company }, tx, cancellationToken: ct)) >= ApprovalMatrix.MaxProfiles)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = [$"La empresa tiene el máximo de {ApprovalMatrix.MaxProfiles} perfiles."] });
        if (await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM meta.ApprovalProfile WHERE CompanyId = @company AND Name = @name AND (@id IS NULL OR Id <> @id)",
                new { company, name, id }, tx, cancellationToken: ct)) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe un perfil con ese nombre en esta empresa."] });
        // Nadie (tampoco el superusuario) se agrega ni se quita a sí mismo: el beneficiario no se aprueba (021-1).
        var changed = memberIds.Except(beforeMembers).Concat(beforeMembers.Except(memberIds)).ToHashSet();
        if (changed.Contains(user.Id))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["memberIds"] = ["Nadie se agrega ni se quita a sí mismo de un perfil de autorización: pídeselo a otro administrador."] });
        // Personas activas (ni equipos ni cuentas de servicio) con acceso a la empresa.
        var added = memberIds.Except(beforeMembers).ToArray();
        if (added.Length > 0)
        {
            var valid = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM meta.[User] u WHERE u.Id IN @added AND u.IsActive = 1 AND u.IsTeam = 0 AND u.IsService = 0
                   AND (EXISTS (SELECT 1 FROM meta.UserCompany uc WHERE uc.UserId = u.Id AND uc.CompanyId = @company)
                        OR EXISTS (SELECT 1 FROM meta.UserRole ur JOIN meta.Role r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id AND r.IsSuperuser = 1))
                """, new { added, company }, tx, cancellationToken: ct));
            if (valid != added.Length)
                throw new ValidationFailed(new Dictionary<string, string[]> { ["memberIds"] = ["Hay personas inactivas, que no son usuarios o sin acceso a esta empresa."] });
        }

        if (gate.MustWait(approving, expands: true))
        {
            var change = await gate.RequestAsync(cn, tx, http, id is null ? SecurityChangeKinds.ApprovalProfileCreate : SecurityChangeKinds.ApprovalProfileUpdate, id,
                id is null ? $"Alta del perfil de autorización {name}" : $"Cambios al perfil de autorización {before!.Name}", req,
                before is null ? null : Snapshot(before, beforeMembers));
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change);
        }

        var profileId = id ?? await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT meta.ApprovalProfile (CompanyId, Name, MinAmount, MaxAmount, IsActive, CreatedBy)
            OUTPUT inserted.Id VALUES (@company, @name, @MinAmount, @MaxAmount, 1, @uid)
            """, new { company, name, req.MinAmount, req.MaxAmount, uid = user.Id }, tx, cancellationToken: ct));
        if (id is not null)
            await cn.ExecuteAsync(new CommandDefinition("""
                UPDATE meta.ApprovalProfile SET Name = @name, MinAmount = @MinAmount, MaxAmount = @MaxAmount, IsActive = @IsActive,
                       UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @uid WHERE Id = @id
                """, new { id, name, req.MinAmount, req.MaxAmount, req.IsActive, uid = user.Id }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition("DELETE meta.ApprovalProfileMember WHERE ProfileId = @profileId AND UserId NOT IN @memberIds",
            new { profileId, memberIds = memberIds.Length == 0 ? [0] : memberIds }, tx, cancellationToken: ct));
        foreach (var uid in added)
            await cn.ExecuteAsync(new CommandDefinition("INSERT meta.ApprovalProfileMember (ProfileId, UserId, AddedBy) VALUES (@profileId, @uid, @by)",
                new { profileId, uid, by = user.Id }, tx, cancellationToken: ct));
        var saved = await cn.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            "SELECT Id, Name, MinAmount, MaxAmount, IsActive, RowVer FROM meta.ApprovalProfile WHERE Id = @profileId", new { profileId }, tx, cancellationToken: ct));
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApprovalProfileChange, "ApprovalProfile", profileId.ToString(CultureInfo.InvariantCulture),
            before is null ? null : Snapshot(before, beforeMembers),
            approving is null ? Snapshot(saved, memberIds) : new { profile = Snapshot(saved, memberIds), securityChange = approving.Id });
        await RefreshPendingAsync(cn, tx, company, profileId, ct);
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync(ct);
        var dto = new { id = profileId, name, minAmount = saved.MinAmount, maxAmount = saved.MaxAmount, isActive = saved.IsActive, memberIds, version = Version(saved.RowVer) };
        return id is null ? Results.Created($"/api/admin/approvals/profiles/{profileId}", dto) : Results.Ok(dto);
    }

    /// <summary>Recalcula la lista de autorizadores de los documentos que esperan un nivel de este perfil (personas vivas).</summary>
    private async Task RefreshPendingAsync(SqlConnection cn, SqlTransaction tx, int company, int profileId, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var runs = await cn.QueryAsync<(string EntityName, long RecordId, string RuleJson, int? CurrentLevel)>(new CommandDefinition(
            "SELECT EntityName, RecordId, RuleJson, CurrentLevel FROM meta.ApprovalRun WHERE Status = 'pending' AND CompanyId = @company",
            new { company }, tx, cancellationToken: ct));
        foreach (var r in runs)
        {
            if (!model.TryGetValue(r.EntityName, out var e) || e.Workflow?.Approval is null) continue;
            var frozen = JsonSerializer.Deserialize<FrozenRule>(r.RuleJson, ApprovalMatrix.Json)!;
            if (frozen.Levels.FirstOrDefault(l => l.Level == r.CurrentLevel)?.ProfileId != profileId) continue;
            await RecordService.RefreshApproversAsync(cn, tx, e, r.RecordId, ct);
        }
    }

    // ---------------- Reglas ----------------

    private static ApprovalRuleSnapshot Snapshot(RuleRow r)
    {
        var d = ToDef(r);
        return new(r.EntityName, r.Name, r.Priority, r.IsActive, r.ValidFrom, r.ValidTo, r.MinAmount, r.MaxAmount,
            new SortedDictionary<string, IReadOnlyList<string>>(d.Conditions.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.OrdinalIgnoreCase), d.Levels);
    }

    public async Task<IResult> SaveRuleAsync(int? id, ApprovalRuleRequest req, HttpContext http, SecurityChangeApproval? approving, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireUserAdmin(user);
        var company = approving is not null ? req.CompanyId ?? throw new Conflict("La solicitud no dice de qué empresa es.") : CompanyOf(user);
        var model = await store.GetAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > ApprovalMatrix.MaxName) errors["name"] = [$"El nombre es obligatorio (máx. {ApprovalMatrix.MaxName})."];
        var e = model.GetValueOrDefault(req.Entity ?? "");
        if (e is not { IsArchived: false, Workflow.Approval: not null }) errors["entity"] = ["El catálogo no se autoriza por matriz."];
        if (req.Priority is < 1 or > 999) errors["priority"] = ["La prioridad va de 1 (la primera) a 999."];
        if (req.ValidFrom is { } f && req.ValidTo is { } t && f.Date > t.Date) errors["validTo"] = ["La vigencia termina antes de empezar."];
        CheckRange(req.MinAmount, req.MaxAmount, errors);
        var levels = req.Levels ?? [];
        if (levels.Length is 0 or > ApprovalMatrix.MaxLevels) errors["levels"] = [$"Una regla tiene de 1 a {ApprovalMatrix.MaxLevels} niveles."];
        var conditions = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (e?.Workflow?.Approval is { } a)
            foreach (var (field, raw) in req.Conditions ?? [])
            {
                var cf = (a.Conditions ?? []).Contains(field, StringComparer.OrdinalIgnoreCase) ? e.FindField(field) : null;
                if (cf is null) { errors[$"conditions.{field}"] = ["No es un campo de condición de este catálogo."]; continue; }
                var values = raw.Select(v => CanonValue(cf, v)).ToList();
                if (values.Count > ApprovalMatrix.MaxValues) errors[$"conditions.{cf.Name}"] = [$"Máximo {ApprovalMatrix.MaxValues} valores."];
                else if (values.Any(v => v is null)) errors[$"conditions.{cf.Name}"] = [$"Hay un valor que no es válido para {cf.Label}."];
                else if (values.Count > 0) conditions[cf.Name] = values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => v!).ToList();
            }
        if (errors.Count > 0) throw new ValidationFailed(errors);
        req = req with { Name = name, Entity = e!.Name, Levels = levels, CompanyId = company,
            Conditions = conditions.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => JsonSerializer.SerializeToElement(v)).ToArray()) };

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        RuleRow? before = null;
        if (id is not null)
        {
            before = await cn.QuerySingleOrDefaultAsync<RuleRow>(new CommandDefinition("""
                SELECT Id, EntityName, Name, Priority, IsActive, ValidFrom, ValidTo, MinAmount, MaxAmount, ConditionsJson, LevelsJson, RowVer
                  FROM meta.ApprovalRule WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id AND CompanyId = @company
                """, new { id, company }, tx, cancellationToken: ct)) ?? throw new NotFound("La regla no existe.");
            approving?.Verify(Snapshot(before));
            if (req.Version is null || Version(before.RowVer) != req.Version)
                throw new Conflict("Otro administrador cambió esta regla. Recarga para ver los cambios.");
            if (!string.Equals(before.EntityName, e.Name, StringComparison.OrdinalIgnoreCase))
                throw new ValidationFailed(new Dictionary<string, string[]> { ["entity"] = ["Una regla no cambia de catálogo: crea otra."] });
        }
        else if (await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM meta.ApprovalRule WHERE CompanyId = @company",
                     new { company }, tx, cancellationToken: ct)) >= ApprovalMatrix.MaxRules)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = [$"La empresa tiene el máximo de {ApprovalMatrix.MaxRules} reglas."] });
        if (await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM meta.ApprovalRule WHERE CompanyId = @company AND EntityName = @entity AND Name = @name AND (@id IS NULL OR Id <> @id)",
                new { company, entity = e.Name, name, id }, tx, cancellationToken: ct)) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Ya existe una regla con ese nombre para este catálogo."] });
        var known = (await cn.QueryAsync<int>(new CommandDefinition("SELECT Id FROM meta.ApprovalProfile WHERE CompanyId = @company AND Id IN @levels",
            new { company, levels }, tx, cancellationToken: ct))).ToHashSet();
        if (levels.Any(l => !known.Contains(l)))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["levels"] = ["Hay niveles con un perfil que no existe en esta empresa."] });

        if (gate.MustWait(approving, expands: true))
        {
            var change = await gate.RequestAsync(cn, tx, http, id is null ? SecurityChangeKinds.ApprovalRuleCreate : SecurityChangeKinds.ApprovalRuleUpdate, id,
                id is null ? $"Alta de la regla de autorización {name} ({e.LabelPlural})" : $"Cambios a la regla de autorización {before!.Name} ({e.LabelPlural})",
                req, before is null ? null : Snapshot(before));
            await tx.CommitAsync(ct);
            return SecurityApproval.Accepted(change);
        }

        var conditionsJson = JsonSerializer.Serialize(conditions, ApprovalMatrix.Json);
        var levelsJson = JsonSerializer.Serialize(levels, ApprovalMatrix.Json);
        var p = new { id, company, entity = e.Name, name, req.Priority, IsActive = id is null || req.IsActive, ValidFrom = req.ValidFrom?.Date,
            ValidTo = req.ValidTo?.Date, req.MinAmount, req.MaxAmount, conditionsJson, levelsJson, uid = user.Id };
        var ruleId = id ?? await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT meta.ApprovalRule (CompanyId, EntityName, Name, Priority, IsActive, ValidFrom, ValidTo, MinAmount, MaxAmount, ConditionsJson, LevelsJson, CreatedBy)
            OUTPUT inserted.Id VALUES (@company, @entity, @name, @Priority, @IsActive, @ValidFrom, @ValidTo, @MinAmount, @MaxAmount, @conditionsJson, @levelsJson, @uid)
            """, p, tx, cancellationToken: ct));
        if (id is not null)
            await cn.ExecuteAsync(new CommandDefinition("""
                UPDATE meta.ApprovalRule SET Name = @name, Priority = @Priority, IsActive = @IsActive, ValidFrom = @ValidFrom, ValidTo = @ValidTo,
                       MinAmount = @MinAmount, MaxAmount = @MaxAmount, ConditionsJson = @conditionsJson, LevelsJson = @levelsJson,
                       UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @uid WHERE Id = @id
                """, p, tx, cancellationToken: ct));
        var saved = await cn.QuerySingleAsync<RuleRow>(new CommandDefinition("""
            SELECT Id, EntityName, Name, Priority, IsActive, ValidFrom, ValidTo, MinAmount, MaxAmount, ConditionsJson, LevelsJson, RowVer
              FROM meta.ApprovalRule WHERE Id = @ruleId
            """, new { ruleId }, tx, cancellationToken: ct));
        await Audit.WriteAsync(cn, tx, http, AuditActions.ApprovalRuleChange, "ApprovalRule", ruleId.ToString(CultureInfo.InvariantCulture),
            before is null ? null : Snapshot(before),
            approving is null ? Snapshot(saved) : new { rule = Snapshot(saved), securityChange = approving.Id });
        if (approving is not null) await SecurityApproval.MarkAppliedAsync(cn, tx, approving, http);
        await tx.CommitAsync(ct);
        var dto = new { id = ruleId, entity = e.Name, name, version = Version(saved.RowVer) };
        return id is null ? Results.Created($"/api/admin/approvals/rules/{ruleId}", dto) : Results.Ok(dto);
    }

    private static void CheckRange(decimal? min, decimal? max, Dictionary<string, string[]> errors)
    {
        if (min < 0 || max < 0) errors["minAmount"] = ["Los montos no son negativos."];
        else if (min is { } a && max is { } b && a > b) errors["maxAmount"] = ["El monto máximo es menor que el mínimo."];
    }

    /// <summary>Un valor admitido de una condición, en forma canónica según el tipo del campo (null si no es válido).</summary>
    private static string? CanonValue(FieldDef f, JsonElement v) => f.Type switch
    {
        FieldTypes.Boolean => v.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null },
        FieldTypes.Choice => v.ValueKind == JsonValueKind.String && (f.Options ?? []).Contains(v.GetString()!, StringComparer.Ordinal) ? v.GetString() : null,
        FieldTypes.Reference or FieldTypes.User => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0
                ? s.ToString(CultureInfo.InvariantCulture) : null,
        _ => null,
    };
}
