using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Endpoints;

public static class ModelEndpoints
{
    public static void MapModel(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/model").RequireAuthorization();

        g.MapGet("/entities", async (HttpContext http, MetadataStore store, bool? includeArchived, bool? includeChildren, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var model = await store.GetAsync(ct);
            return model.Values
                .Where(e => (includeArchived == true && u.CanManageModel) || !e.IsArchived)
                // Las partidas no van en el menú: solo se piden para pintar el formulario de su documento.
                .Where(e => includeChildren == true || !e.IsChild)
                // Los modeladores ven todo el modelo; el resto, solo lo que puede leer o capturar.
                .Where(e => u.CanManageModel || CanSee(u, e))
                .OrderBy(e => e.LabelPlural)
                .Select(e => ForUser(e, u))
                .ToList();
        });

        g.MapGet("/entities/{name}", async (string name, HttpContext http, MetadataStore store, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var e = await store.GetActiveAsync(name, ct);
            if (!u.CanManageModel && !CanSee(u, e)) throw new Forbidden();
            return ForUser(e, u);
        });

        g.MapPost("/plan/preview", async (ChangePlan plan, HttpContext http, PlanService plans, CancellationToken ct) =>
        {
            RequireModeler(http);
            return await plans.PreviewAsync(plan, ct);
        });

        g.MapPost("/plan/apply", async (ChangePlan plan, HttpContext http, PlanService plans, CancellationToken ct) =>
        {
            RequireModeler(http);
            return new { changeSetId = await plans.ApplyAsync(plan, http, ct) };
        });

        g.MapGet("/changes", async (HttpContext http, Db db) =>
        {
            RequireModeler(http);
            await using var cn = await db.OpenAsync();
            return await cn.QueryAsync<ChangeRow>("""
                SELECT TOP 200 c.Id, c.Description, c.AppliedAt, u.DisplayName AS AppliedBy, c.OperationCount, c.SqlText AS Sql
                  FROM meta.ChangeSet c JOIN meta.[User] u ON u.Id = c.AppliedBy ORDER BY c.Id DESC
                """);
        });
    }

    private sealed record ChangeRow(int Id, string Description, DateTime AppliedAt, string AppliedBy, int OperationCount, string? Sql);

    private static void RequireModeler(HttpContext http)
    {
        if (!http.CurrentUser().CanManageModel) throw new Forbidden();
    }

    /// <summary>Las partidas heredan el permiso de su documento.</summary>
    private static bool CanSee(UserContext u, EntityDef e)
    {
        var a = u.Access(e.IsChild ? e.ParentEntity! : e.Name);
        return a.Read || a.Create;
    }

    /// <summary>Oculta los campos archivados a quien no modela.</summary>
    private static EntityDef ForUser(EntityDef e, UserContext u) =>
        u.CanManageModel ? e : e with { Fields = e.ActiveFields.ToList() };
}
