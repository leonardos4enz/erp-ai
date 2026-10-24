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
    public async Task<PlanPreview> PreviewAsync(ChangePlan plan, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = await LoadRolesAsync(cn, null);
        return PlanCompiler.Compile(plan, model, roles, boards).Preview;
    }

    public async Task<int> ApplyAsync(ChangePlan plan, HttpContext http, CancellationToken ct)
    {
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
        var roles = await LoadRolesAsync(cn, tx);
        var compiled = PlanCompiler.Compile(plan, model, extraRoles is null ? roles : roles.Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase), boards);
        if (!compiled.Preview.Valid)
            throw new ValidationFailed(compiled.Preview.Errors
                .GroupBy(e => e.Operation < 0 ? "plan" : $"operations[{e.Operation}]")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()), "El plan de cambios no es válido.");

        foreach (var statement in compiled.Sql)
            await cn.ExecuteAsync(statement, transaction: tx);

        foreach (var name in compiled.TouchedEntities)
            await UpsertAsync(cn, tx, compiled.ResultingModel[name]);
        foreach (var name in compiled.TouchedDashboards ?? new HashSet<string>())
            await UpsertDashboardAsync(cn, tx, compiled.Dashboards![name]);

        // Spec 005: una fórmula nueva o cambiada también se calcula en los registros existentes.
        var result = compiled.ResultingModel;
        var recompute = plan.Operations
            .Select(op => op switch
            {
                AddFieldOp a when string.Equals(a.Field.Type?.Trim(), FieldTypes.Formula, StringComparison.OrdinalIgnoreCase) => a.Entity,
                UpdateFieldOp u when u.Formula is not null => u.Entity,
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
            PlanJson = JsonSerializer.Serialize(plan, json),
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
        await using var cn = await db.OpenAsync(ct);
        var roles = (await LoadRolesAsync(cn, null)).Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new List<PlanError>();
        var steps = new List<PlanStep>();
        foreach (var (label, plan) in chain)
        {
            var compiled = PlanCompiler.Compile(plan, model, roles, boards);
            boards = compiled.Dashboards!;
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
        return PlanCompiler.Compile(plan, model, roles, boards);
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

    private static async Task UpsertAsync(DbConnection cn, DbTransaction tx, EntityDef e)
    {
        var entityId = await cn.ExecuteScalarAsync<int>("""
            MERGE meta.Entity AS t
            USING (SELECT @Name AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET Label = @Label, LabelPlural = @LabelPlural, Icon = @Icon,
                Description = @Description, DisplayField = @DisplayField, IsArchived = @IsArchived,
                WorkflowJson = @WorkflowJson, RulesJson = @RulesJson, GroupName = @Group, AutomationsJson = @AutomationsJson, SecurityJson = @SecurityJson,
                LayoutJson = @LayoutJson, ViewsJson = @ViewsJson, ReportsJson = @ReportsJson, WebhooksJson = @WebhooksJson, PerCompany = @PerCompany, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Label, LabelPlural, Icon, Description, DisplayField, IsArchived, WorkflowJson, RulesJson, GroupName, AutomationsJson, SecurityJson, LayoutJson, ViewsJson, ReportsJson, WebhooksJson, PerCompany)
                VALUES (@Name, @Label, @LabelPlural, @Icon, @Description, @DisplayField, @IsArchived, @WorkflowJson, @RulesJson, @Group, @AutomationsJson, @SecurityJson, @LayoutJson, @ViewsJson, @ReportsJson, @WebhooksJson, @PerCompany)
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
