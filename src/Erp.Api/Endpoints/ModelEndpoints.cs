using Dapper;
using Erp.Api.Environments;
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

        // Spec 024: módulos de navegación con al menos un catálogo que el usuario puede leer (filtrado aquí, no en el cliente).
        // `all` (solo modeladores): también los declarados sin catálogos o archivados, para el modelador.
        g.MapGet("/apps", async (HttpContext http, MetadataStore store, bool? all, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var (model, apps) = (await store.GetAsync(ct), await store.GetAppsAsync(ct));
            if (all == true)
            {
                if (!u.CanManageModel) throw new Forbidden();
                return Apps.All(model, apps);
            }
            return Apps.Effective(model, apps, e => u.IsSuperuser || u.Access(e.Name).Read);
        });

        g.MapPost("/plan/preview", async (ChangePlan plan, HttpContext http, PlanService plans, CancellationToken ct) =>
        {
            RequireModeler(http);
            return await plans.PreviewAsync(plan, ct);
        });

        g.MapPost("/plan/apply", async (ChangePlan plan, HttpContext http, PlanService plans, EnvironmentOptions env, CancellationToken ct) =>
        {
            RequireModeler(http);
            env.RequireDirectChange("un plan del modelador");
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

    /// <summary>
    /// Oculta los campos archivados a quien no modela. Spec 006: cada campo lleva el acceso del usuario por rol
    /// (<c>access</c>) y los estados en que se captura; quien no modela no recibe los campos ocultos ni la política.
    /// </summary>
    private static EntityDef ForUser(EntityDef e, UserContext u)
    {
        var fields = (u.CanManageModel ? e.Fields : e.ActiveFields)
            .Select(f => f with
            {
                Access = FieldAccess.ByRole(e, f, u), EditableIn = e.Security?.For(f.Name)?.EditableIn,
                AcceptsTeams = e.Security?.AcceptsTeams(f.Name) == true ? true : null, // spec 015
            })
            .Where(f => u.CanManageModel || f.Access != FieldAccessLevels.Hidden)
            .ToList();
        // Spec 007: vistas y reportes se consultan por /api/views y /api/reports, ya filtrados para el usuario.
        return e with
        {
            // Spec 021 (H2 CA1): el editor manda esta versión con su plan; si otra persona cambió el catálogo, se rechaza.
            Version = u.CanManageModel ? ModelVersion.Of(e) : null,
            Fields = fields,
            Security = u.CanManageModel ? e.Security : null,
            Views = u.CanManageModel ? e.Views : null,
            Reports = u.CanManageModel ? e.Reports : null,
            Webhooks = u.CanManageModel ? e.Webhooks : null,
            Schedules = u.CanManageModel ? e.Schedules : null,
        };
    }
}
