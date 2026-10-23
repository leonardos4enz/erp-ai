using System.Data.Common;
using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Metadata;

public sealed class PlanService(Db db, MetadataStore store, JsonSerializerOptions json)
{
    public async Task<PlanPreview> PreviewAsync(ChangePlan plan, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = await LoadRolesAsync(cn, null);
        return PlanCompiler.Compile(plan, model, roles).Preview;
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

        // Serializa los cambios de modelo: dos modeladores no pueden aplicar planes a la vez.
        var lockResult = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'erp-model', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, transaction: tx);
        if (lockResult < 0) throw new Conflict("Otro cambio de modelo está en curso. Intenta de nuevo.");

        // Se recompila contra el modelo leído dentro de la transacción, no contra la caché.
        var model = await MetadataStore.LoadAsync(cn, tx);
        var roles = await LoadRolesAsync(cn, tx);
        var compiled = PlanCompiler.Compile(plan, model, extraRoles is null ? roles : roles.Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (!compiled.Preview.Valid)
            throw new ValidationFailed(compiled.Preview.Errors
                .GroupBy(e => e.Operation < 0 ? "plan" : $"operations[{e.Operation}]")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()), "El plan de cambios no es válido.");

        foreach (var statement in compiled.Sql)
            await cn.ExecuteAsync(statement, transaction: tx);

        foreach (var name in compiled.TouchedEntities)
            await UpsertAsync(cn, tx, compiled.ResultingModel[name]);

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

    /// <summary>Compila contra el modelo actual con roles adicionales que aún no existen (vista previa de módulos).</summary>
    internal async Task<CompiledPlan> CompileWithRolesAsync(ChangePlan plan, IReadOnlySet<string> extraRoles, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var roles = (await LoadRolesAsync(cn, null)).Union(extraRoles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PlanCompiler.Compile(plan, model, roles);
    }

    internal static async Task<IReadOnlySet<string>> LoadRolesAsync(DbConnection cn, DbTransaction? tx) =>
        (await cn.QueryAsync<string>("SELECT Name FROM meta.Role", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task UpsertAsync(DbConnection cn, DbTransaction tx, EntityDef e)
    {
        var entityId = await cn.ExecuteScalarAsync<int>("""
            MERGE meta.Entity AS t
            USING (SELECT @Name AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET Label = @Label, LabelPlural = @LabelPlural, Icon = @Icon,
                Description = @Description, DisplayField = @DisplayField, IsArchived = @IsArchived,
                WorkflowJson = @WorkflowJson, RulesJson = @RulesJson, GroupName = @Group, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, Label, LabelPlural, Icon, Description, DisplayField, IsArchived, WorkflowJson, RulesJson, GroupName)
                VALUES (@Name, @Label, @LabelPlural, @Icon, @Description, @DisplayField, @IsArchived, @WorkflowJson, @RulesJson, @Group)
            OUTPUT inserted.Id;
            """, new
        {
            e.Name, e.Label, e.LabelPlural, e.Icon, e.Description, e.DisplayField, e.IsArchived, e.Group,
            WorkflowJson = e.Workflow is null ? null : JsonSerializer.Serialize(e.Workflow, MetadataStore.Json),
            RulesJson = e.Rules is null ? null : JsonSerializer.Serialize(e.Rules, MetadataStore.Json),
        }, tx);

        foreach (var f in e.Fields)
        {
            await cn.ExecuteAsync("""
                MERGE meta.Field AS t
                USING (SELECT @EntityId AS EntityId, @Name AS Name) AS s ON t.EntityId = s.EntityId AND t.Name = s.Name
                WHEN MATCHED THEN UPDATE SET Label = @Label, IsRequired = @Required, ShowInList = @ShowInList,
                    OptionsJson = @Options, RequiredForRolesJson = @Roles, DisplayOrder = @DisplayOrder, IsArchived = @IsArchived,
                    DefaultValue = @DefaultValue, Formula = @Formula
                WHEN NOT MATCHED THEN INSERT (EntityId, Name, Label, Type, IsRequired, IsUnique, MaxLength, [Precision], Scale,
                    OptionsJson, ReferenceEntity, DisplayOrder, ShowInList, RequiredForRolesJson, IsArchived,
                    DefaultValue, Formula, ResultType, Prefix, Padding, ChildEntity)
                    VALUES (@EntityId, @Name, @Label, @Type, @Required, @Unique, @MaxLength, @Precision, @Scale,
                    @Options, @ReferenceEntity, @DisplayOrder, @ShowInList, @Roles, @IsArchived,
                    @DefaultValue, @Formula, @ResultType, @Prefix, @Padding, @ChildEntity);
                """, new
            {
                EntityId = entityId, f.Name, f.Label, f.Type, f.Required, f.Unique, f.MaxLength, f.Precision, f.Scale,
                Options = f.Options is null ? null : JsonSerializer.Serialize(f.Options),
                f.ReferenceEntity, f.DisplayOrder, f.ShowInList,
                Roles = f.RequiredForRoles is null ? null : JsonSerializer.Serialize(f.RequiredForRoles),
                f.IsArchived, f.DefaultValue, f.Formula, f.ResultType, f.Prefix, f.Padding, f.ChildEntity,
            }, tx);
        }
    }
}
