using System.Data.Common;
using System.Text.Json;
using Dapper;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Metadata;

public sealed class PlanService(Db db, MetadataStore store, JsonSerializerOptions json)
{
    /// <summary>
    /// Vista previa del modelador. Spec 021: agrega los catálogos que cambiaron desde que el editor los abrió, los estados
    /// que se quitarían con registros, y la huella de lo revisado (<see cref="PlanPreview.BaseHash"/>).
    /// </summary>
    public async Task<PlanPreview> PreviewAsync(ChangePlan plan, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        var apps = await store.GetAppsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = await LoadRolesAsync(cn, null);
        var compiled = PlanCompiler.Compile(plan, model, roles, boards, apps);
        var errors = compiled.Preview.Errors
            .Concat(ModelVersion.Stale(model, plan.BaseVersions).Select(StaleError))
            .Concat(await RemovedStatesInUseAsync(cn, null, plan, model)).ToList();
        return compiled.Preview with
        {
            Valid = errors.Count == 0, Errors = errors,
            BaseHash = ModelVersion.Base(model, boards, compiled.TouchedEntities, compiled.TouchedDashboards, apps, compiled.TouchedApps),
        };
    }

    private static PlanError StaleError(string label) =>
        new(-1, $"«{label}» cambió desde que lo abriste (otra persona aplicó un cambio): vuelve a abrirlo para no borrar su cambio.");

    /// <summary>Spec 021 (H2 CA2): el modelador aplica solo con la huella de la vista previa que revisó.</summary>
    public async Task<int> ApplyAsync(ChangePlan plan, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.BaseHash))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["baseHash"] = ["Falta la huella de la vista previa: revisa los cambios antes de aplicarlos."] });
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var changeSetId = await ApplyInTransactionAsync(cn, tx, plan, http);
        await tx.CommitAsync(ct);
        store.Invalidate();
        return changeSetId;
    }

    /// <summary>
    /// Aplica el plan dentro de una transacción ajena (p. ej. la instalación de un módulo).
    /// Quien llama es responsable de confirmar y de invalidar la caché del modelo.
    /// </summary>
    internal async Task<int> ApplyInTransactionAsync(DbConnection cn, DbTransaction tx, ChangePlan plan, HttpContext http,
        IReadOnlySet<string>? extraRoles = null)
    {
        var user = http.CurrentUser();

        await LockModelAsync(cn, tx);

        // Se recompila contra el modelo leído dentro de la transacción, no contra la caché.
        var model = await MetadataStore.LoadAsync(cn, tx);
        var boards = await MetadataStore.LoadDashboardsAsync(cn, tx);
        var apps = await MetadataStore.LoadAppsAsync(cn, tx);
        var roles = await LoadRolesAsync(cn, tx);
        var compiled = PlanCompiler.Compile(plan, model, extraRoles is null ? roles : roles.Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase), boards, apps);
        // Spec 021 (H8 CA3): un estado que desaparece no puede tener registros (quedarían en un estado inexistente).
        var errors = compiled.Preview.Errors.Concat(await RemovedStatesInUseAsync(cn, tx, plan, model)).ToList();
        if (errors.Count > 0)
            throw new ValidationFailed(errors
                .GroupBy(e => e.Operation < 0 ? "plan" : $"operations[{e.Operation}]")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()), "El plan de cambios no es válido.");
        // Spec 021 (H2): bajo el candado del modelo, lo que el editor abrió y lo que se revisó siguen siendo lo actual.
        if (ModelVersion.Stale(model, plan.BaseVersions).FirstOrDefault() is { } stale)
            throw new Conflict(StaleError(stale).Message);
        if (plan.BaseHash is not null &&
            !string.Equals(plan.BaseHash, ModelVersion.Base(model, boards, compiled.TouchedEntities, compiled.TouchedDashboards, apps, compiled.TouchedApps), StringComparison.OrdinalIgnoreCase))
            throw new Conflict("El modelo cambió desde la vista previa (otra persona aplicó un cambio a lo mismo): vuelve a revisar los cambios.");

        foreach (var statement in compiled.Sql)
            await cn.ExecuteAsync(statement, transaction: tx);

        foreach (var name in compiled.TouchedEntities)
            await UpsertAsync(cn, tx, compiled.ResultingModel[name]);
        foreach (var name in compiled.TouchedDashboards ?? new HashSet<string>())
            await UpsertDashboardAsync(cn, tx, compiled.Dashboards![name]);
        foreach (var name in compiled.TouchedApps ?? new HashSet<string>())
            await UpsertAppAsync(cn, tx, compiled.Apps![name]);
        // Spec 021 (H4 CA2): una tarea activa cuyo contenido cambia, se quita o pierde su catálogo vuelve a la pausa.
        await Automations.ScheduleStates.PauseChangedAsync(cn, tx, model, compiled.ResultingModel, http);

        // Spec 005: una fórmula nueva o cambiada también se calcula en los registros existentes.
        var result = compiled.ResultingModel;
        var recompute = plan.Operations
            .Select(op => op switch
            {
                AddFieldOp a when string.Equals(a.Field.Type?.Trim(), FieldTypes.Formula, StringComparison.OrdinalIgnoreCase) => a.Entity,
                UpdateFieldOp u when u.Formula is not null => u.Entity,
                // Spec 017: una fórmula reactivada no se calculó mientras estuvo archivada.
                RestoreFieldOp r when model.TryGetValue(r.Entity, out var re) && re.FindField(r.Field) is { Type: FieldTypes.Formula } => r.Entity,
                _ => null,
            })
            .OfType<string>()
            .Where(result.ContainsKey)
            .Select(n => AutomationRules.Root(result[n], result).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in recompute)
            await RecordService.RecomputeFormulasAsync((SqlConnection)cn, (SqlTransaction)tx, result[root], result, CancellationToken.None);

        var changeSetId = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.ChangeSet (Description, PlanJson, SqlText, OperationCount, AppliedBy)
            OUTPUT inserted.Id VALUES (@Description, @PlanJson, @SqlText, @Count, @UserId)
            """, new
        {
            Description = string.IsNullOrWhiteSpace(plan.Description) ? "Cambio de modelo" : plan.Description.Trim(),
            PlanJson = JsonSerializer.Serialize(plan with { BaseVersions = null, BaseHash = null }, json),
            SqlText = string.Join("\n\n", compiled.Sql),
            Count = plan.Operations.Count,
            UserId = user.Id,
        }, tx);

        // La columna admite 100 caracteres; con muchos catálogos el detalle completo queda en el JSON del plan.
        var touched = string.Join(",", compiled.TouchedEntities);
        if (touched.Length > 100) touched = $"{compiled.TouchedEntities.Count} catálogos";
        await Audit.WriteAsync(cn, tx, http, AuditActions.ModelChange, entity: touched, recordId: changeSetId.ToString(), after: plan);
        return changeSetId;
    }

    /// <summary>
    /// Vista previa de varios planes en secuencia (base + actualizaciones de un módulo): cada uno se
    /// compila sobre el modelo que deja el anterior, igual que al aplicarlos.
    /// </summary>
    internal async Task<PlanPreview> PreviewChainAsync(IReadOnlyList<(string Label, ChangePlan Plan)> chain,
        IReadOnlySet<string> extraRoles, CancellationToken ct)
    {
        IReadOnlyDictionary<string, EntityDef> model = await store.GetAsync(ct);
        IReadOnlyDictionary<string, DashboardDef> boards = await store.GetDashboardsAsync(ct);
        IReadOnlyDictionary<string, AppDef> apps = await store.GetAppsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = (await LoadRolesAsync(cn, null)).Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new List<PlanError>();
        var steps = new List<PlanStep>();
        foreach (var (label, plan) in chain)
        {
            var compiled = PlanCompiler.Compile(plan, model, roles, boards, apps);
            (boards, apps) = (compiled.Dashboards!, compiled.Apps!);
            var prefix = chain.Count > 1 ? $"[{label}] " : "";
            errors.AddRange(compiled.Preview.Errors.Select(e => e with { Message = prefix + e.Message }));
            steps.AddRange(compiled.Preview.Steps.Select(s => s with { Description = prefix + s.Description }));
            model = compiled.ResultingModel;
        }
        return new PlanPreview(errors.Count == 0, errors, steps);
    }

    /// <summary>Compila contra el modelo actual con roles adicionales que aún no existen (vista previa de módulos).</summary>
    internal async Task<CompiledPlan> CompileWithRolesAsync(ChangePlan plan, IReadOnlySet<string> extraRoles, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = (await LoadRolesAsync(cn, null)).Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PlanCompiler.Compile(plan, model, roles, boards, await store.GetAppsAsync(ct));
    }

    /// <summary>
    /// Spec 021 (H8 CA3): estados que un <see cref="SetWorkflowOp"/> quita y en los que hay registros. El nombre de la tabla
    /// viene del modelo validado; los estados van como parámetros.
    /// </summary>
    private static async Task<IReadOnlyList<PlanError>> RemovedStatesInUseAsync(DbConnection cn, DbTransaction? tx, ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef> model)
    {
        var errors = new List<PlanError>();
        for (var i = 0; i < plan.Operations.Count; i++)
        {
            if (plan.Operations[i] is not SetWorkflowOp op || !model.TryGetValue(op.Entity, out var e) || e.IsArchived || e.Workflow is null) continue;
            var kept = op.Workflow.States.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = e.Workflow.States.Select(s => s.Name).Where(n => !kept.Contains(n)).ToArray();
            if (removed.Length == 0) continue;
            var inUse = await cn.QueryAsync<(string State, int Count)>(
                $"SELECT {Identifiers.Quote(Workflow.StateField)}, COUNT(*) FROM {Identifiers.AppTable(e.Name)} WHERE [IsDeleted] = 0 AND {Identifiers.Quote(Workflow.StateField)} IN @removed GROUP BY {Identifiers.Quote(Workflow.StateField)}",
                new { removed }, tx);
            foreach (var (state, count) in inUse)
                errors.Add(new(i, $"No se puede quitar el estado «{state}» de «{e.Label}»: {count} registro(s) están en el estado «{state}». " +
                                  "Muévelos a otro estado antes de quitarlo, o consérvalo."));
        }
        return errors;
    }

    /// <summary>Serializa los cambios de modelo: dos modeladores no pueden aplicar planes a la vez (reentrante en la transacción).</summary>
    internal static async Task LockModelAsync(DbConnection cn, DbTransaction tx)
    {
        var lockResult = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'erp-model', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, transaction: tx);
        if (lockResult < 0) throw new Conflict("Otro cambio de modelo está en curso. Intenta de nuevo.");
    }

    internal static async Task<IReadOnlySet<string>> LoadRolesAsync(DbConnection cn, DbTransaction? tx) =>
        (await cn.QueryAsync<string>("SELECT Name FROM meta.Role", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Task UpsertDashboardAsync(DbConnection cn, DbTransaction tx, DashboardDef d) =>
        cn.ExecuteAsync("""
            MERGE meta.Dashboard AS t USING (SELECT @Name AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET Label = @Label, DefinitionJson = @Json, IsArchived = @IsArchived, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Label, DefinitionJson, IsArchived) VALUES (@Name, @Label, @Json, @IsArchived);
            """, new { d.Name, d.Label, Json = JsonSerializer.Serialize(d with { IsArchived = false }, MetadataStore.Json), d.IsArchived }, tx);

    private static Task UpsertAppAsync(DbConnection cn, DbTransaction tx, AppDef a) =>
        cn.ExecuteAsync("""
            MERGE meta.App AS t USING (SELECT @Name AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET Label = @Label, DefinitionJson = @Json, IsArchived = @IsArchived, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Label, DefinitionJson, IsArchived) VALUES (@Name, @Label, @Json, @IsArchived);
            """, new { a.Name, a.Label, Json = JsonSerializer.Serialize(a with { IsArchived = false }, MetadataStore.Json), a.IsArchived }, tx);

    private static async Task UpsertAsync(DbConnection cn, DbTransaction tx, EntityDef e)
    {
        var entityId = await cn.ExecuteScalarAsync<int>("""
            MERGE meta.Entity AS t
            USING (SELECT @Name AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET Label = @Label, LabelPlural = @LabelPlural, Icon = @Icon,
                Description = @Description, DisplayField = @DisplayField, IsArchived = @IsArchived,
                WorkflowJson = @WorkflowJson, RulesJson = @RulesJson, GroupName = @Group, AutomationsJson = @AutomationsJson, SecurityJson = @SecurityJson,
                LayoutJson = @LayoutJson, ViewsJson = @ViewsJson, ReportsJson = @ReportsJson, WebhooksJson = @WebhooksJson, SchedulesJson = @SchedulesJson, LedgerJson = @LedgerJson, PerCompany = @PerCompany, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Label, LabelPlural, Icon, Description, DisplayField, IsArchived, WorkflowJson, RulesJson, GroupName, AutomationsJson, SecurityJson, LayoutJson, ViewsJson, ReportsJson, WebhooksJson, SchedulesJson, LedgerJson, PerCompany)
                VALUES (@Name, @Label, @LabelPlural, @Icon, @Description, @DisplayField, @IsArchived, @WorkflowJson, @RulesJson, @Group, @AutomationsJson, @SecurityJson, @LayoutJson, @ViewsJson, @ReportsJson, @WebhooksJson, @SchedulesJson, @LedgerJson, @PerCompany)
            OUTPUT inserted.Id;
            """, new
        {
            e.Name, e.Label, e.LabelPlural, e.Icon, e.Description, e.DisplayField, e.IsArchived, e.Group, e.PerCompany,
            WorkflowJson = e.Workflow is null ? null : JsonSerializer.Serialize(e.Workflow, MetadataStore.Json),
            RulesJson = e.Rules is null ? null : JsonSerializer.Serialize(e.Rules, MetadataStore.Json),
            AutomationsJson = e.Automations is null ? null : JsonSerializer.Serialize(e.Automations, MetadataStore.Json),
            SecurityJson = e.Security is null ? null : JsonSerializer.Serialize(e.Security, MetadataStore.Json),
            LayoutJson = e.Layout is null ? null : JsonSerializer.Serialize(e.Layout, MetadataStore.Json),
            ViewsJson = e.Views is null ? null : JsonSerializer.Serialize(e.Views, MetadataStore.Json),
            ReportsJson = e.Reports is null ? null : JsonSerializer.Serialize(e.Reports, MetadataStore.Json),
            WebhooksJson = e.Webhooks is null ? null : JsonSerializer.Serialize(e.Webhooks, MetadataStore.Json),
            SchedulesJson = e.Schedules is null ? null : JsonSerializer.Serialize(e.Schedules, MetadataStore.Json),
            LedgerJson = e.Ledger is null ? null : JsonSerializer.Serialize(e.Ledger, MetadataStore.Json),
        }, tx);

        foreach (var f in e.Fields)
        {
            await cn.ExecuteAsync("""
                MERGE meta.Field AS t
                USING (SELECT @EntityId AS EntityId, @Name AS Name) AS s ON t.EntityId = s.EntityId AND t.Name = s.Name
                WHEN MATCHED THEN UPDATE SET Label = @Label, IsRequired = @Required, ShowInList = @ShowInList,
                    OptionsJson = @Options, RequiredForRolesJson = @Roles, DisplayOrder = @DisplayOrder, IsArchived = @IsArchived,
                    DefaultValue = @DefaultValue, Formula = @Formula, IsEditable = @Editable, LookupFilterJson = @LookupFilter
                WHEN NOT MATCHED THEN INSERT (EntityId, Name, Label, Type, IsRequired, IsUnique, MaxLength, [Precision], Scale,
                    OptionsJson, ReferenceEntity, DisplayOrder, ShowInList, RequiredForRolesJson, IsArchived,
                    DefaultValue, Formula, ResultType, Prefix, Padding, ChildEntity, IsEditable, LookupFilterJson)
                    VALUES (@EntityId, @Name, @Label, @Type, @Required, @Unique, @MaxLength, @Precision, @Scale,
                    @Options, @ReferenceEntity, @DisplayOrder, @ShowInList, @Roles, @IsArchived,
                    @DefaultValue, @Formula, @ResultType, @Prefix, @Padding, @ChildEntity, @Editable, @LookupFilter);
                """, new
            {
                EntityId = entityId, f.Name, f.Label, f.Type, f.Required, f.Unique, f.MaxLength, f.Precision, f.Scale,
                Options = f.Options is null ? null : JsonSerializer.Serialize(f.Options),
                f.ReferenceEntity, f.DisplayOrder, f.ShowInList,
                Roles = f.RequiredForRoles is null ? null : JsonSerializer.Serialize(f.RequiredForRoles),
                f.IsArchived, f.DefaultValue, f.Formula, f.ResultType, f.Prefix, f.Padding, f.ChildEntity, f.Editable,
                LookupFilter = f.LookupFilter is null ? null : JsonSerializer.Serialize(f.LookupFilter, MetadataStore.Json),
            }, tx);
        }
    }
}
