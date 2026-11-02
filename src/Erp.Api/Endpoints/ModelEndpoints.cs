using Dapper;
using Erp.Api.Environments;
using Erp.Api.Expressions;
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
                // Spec 049 (revisión adversarial F-02): una partida, solo a quien ve su campo de partidas.
                .Where(e => u.CanManageModel || SeesCatalog(u, e, model))
                .OrderBy(e => e.LabelPlural)
                .Select(e => ForUser(e, u, model))
                .ToList();
        }).Requires(Capability.Authenticated);

        g.MapGet("/entities/{name}", async (string name, HttpContext http, MetadataStore store, CancellationToken ct) =>
        {
            var u = http.CurrentUser();
            var e = await store.GetActiveAsync(name, ct);
            var model = await store.GetAsync(ct);
            if (!u.CanManageModel && !SeesCatalog(u, e, model)) throw new Forbidden(); // spec 049 (F-02)
            // Spec 048 (H3 CA1): de dónde sale cada campo, solo con automatizaciones de catálogos que la persona ve (la regla de la lista).
            // Spec 049 (auditoría 9, O-03): una partida, solo si además ve su campo de partidas (045 D-01).
            var sources = FieldSources.For(e, model, s => u.CanManageModel || SeesCatalog(u, s, model));
            var shaped = ForUser(e, u, model);
            return shaped with { Fields = shaped.Fields.Select(f => sources.TryGetValue(f.Name, out var by) ? f with { FilledBy = by } : f).ToList() };
        }).Requires(Capability.Authenticated);

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
        }).Requires(Capability.Authenticated);

        g.MapPost("/plan/preview", async (ChangePlan plan, HttpContext http, PlanService plans, CancellationToken ct) =>
        {
            return await plans.PreviewAsync(plan, ct);
        }).Requires(Capability.Modeler).ReadsOnly();

        g.MapPost("/plan/apply", async (ChangePlan plan, HttpContext http, PlanService plans, CancellationToken ct) =>
            new { changeSetId = await plans.ApplyAsync(plan, http, ct) }).Requires(Capability.Modeler).Writes(Effect.Model, Gates.DirectChange).AppliesReviewed("baseHash");

        g.MapGet("/changes", async (HttpContext http, Db db) =>
        {
            await using var cn = await db.OpenAsync();
            return await cn.QueryAsync<ChangeRow>("""
                SELECT TOP 200 c.Id, c.Description, c.AppliedAt, u.DisplayName AS AppliedBy, c.OperationCount, c.SqlText AS Sql
                  FROM meta.ChangeSet c JOIN meta.[User] u ON u.Id = c.AppliedBy ORDER BY c.Id DESC
                """);
        }).Requires(Capability.Modeler);

        // Spec 026 (H2): de qué paquete viene cada parte del modelo (se reproduce de los paquetes del servidor instalados).
        g.MapGet("/provenance", async (HttpContext http, Db db, MetadataStore store, ModuleService modules, CancellationToken ct) =>
        {
            var model = await store.GetAsync(ct);
            await using var cn = await db.OpenAsync(ct);
            var installed = (await cn.QueryAsync<(string Id, string Version)>("SELECT Id, Version FROM meta.Module"))
                .ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase);
            var roles = await PlanService.LoadRolesAsync(cn, null);
            var rep = Provenance.ReproduceCached(model, modules.Packages(), installed, roles);
            return new { entities = Provenance.Compute(model, rep) };
        }).Requires(Capability.Modeler);

        // Spec 026 (H3 CA3): valida una expresión con el mismo analizador que el compilador, en el ámbito de un catálogo.
        // `fields` son los del borrador (campos nuevos aún sin aplicar); nada se guarda.
        g.MapPost("/expressions/check", async (ExpressionCheck req, HttpContext http, Db db, MetadataStore store, CancellationToken ct) =>
        {
            if (!Identifiers.IsValid(req.Entity, out _)) throw new AppException(400, "Indica un catálogo válido.");
            if ((req.Expression?.Length ?? 0) > MaxExpression) throw new AppException(400, $"La expresión excede {MaxExpression} caracteres.");
            if ((req.Fields?.Count ?? 0) > PlanCompiler.MaxFieldsPerEntity) throw new AppException(400, "Demasiados campos.");
            if (string.IsNullOrWhiteSpace(req.Expression)) return new { errors = Array.Empty<string>() };
            var model = await store.GetAsync(ct);
            var scope = model.GetValueOrDefault(req.Entity!) ?? new EntityDef { Name = req.Entity!, Label = req.Entity!, LabelPlural = req.Entity! };
            if (req.Fields is { } draft)
                scope = scope with
                {
                    Fields = draft.Where(f => Identifiers.IsValid(f.Name, out _) && (FieldTypes.All.Contains(f.Type) || f.Type == FieldTypes.State))
                        .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                };
            var working = new Dictionary<string, EntityDef>(model, StringComparer.OrdinalIgnoreCase) { [scope.Name] = scope };
            await using var cn = await db.OpenAsync(ct);
            var roles = await PlanService.LoadRolesAsync(cn, null);
            return new { errors = ExprAnalyzer.Analyze(req.Expression, scope, working, roles).Errors.ToArray() };
        }).Requires(Capability.Modeler).ReadsOnly();

        g.MapGet("/expressions/functions", (HttpContext http) =>
        {
            return ExprAnalyzer.FunctionList;
        }).Requires(Capability.Modeler);
    }

    private const int MaxExpression = 2000;
    private sealed record ExpressionCheck(string? Entity, string? Expression, IReadOnlyList<FieldDef>? Fields);

    private sealed record ChangeRow(int Id, string Description, DateTime AppliedAt, string AppliedBy, int OperationCount, string? Sql);

    /// <summary>Las partidas heredan el permiso de su documento.</summary>
    private static bool CanSee(UserContext u, EntityDef e)
    {
        var a = u.Access(e.IsChild ? e.ParentEntity! : e.Name);
        return a.Read || a.Create;
    }

    /// <summary>
    /// Spec 049 (O-03): quien solo captura el documento (sin leerlo) ve sus partidas en el formulario si ve su campo de partidas;
    /// <see cref="FieldAccess.SeesLines"/> exige leer la raíz, así que aquí basta con verlo.
    /// </summary>
    private static bool SeesLinesToCreate(EntityDef e, UserContext u, IReadOnlyDictionary<string, EntityDef> model) =>
        model.TryGetValue(e.ParentEntity ?? "", out var root) && u.Access(root.Name).Create
        && root.ActiveFields.Any(f => f.Type == FieldTypes.Lines && string.Equals(f.ChildEntity, e.Name, StringComparison.OrdinalIgnoreCase)
            && FieldAccess.CanSee(root, f, u));

    /// <summary>
    /// Spec 049 (O-03, revisión adversarial F-02): ve el catálogo y, si es una partida, también su campo de partidas (045 D-01):
    /// la regla de la definición, la lista, la procedencia y la búsqueda.
    /// </summary>
    private static bool SeesCatalog(UserContext u, EntityDef e, IReadOnlyDictionary<string, EntityDef> model) =>
        CanSee(u, e) && (!e.IsChild || FieldAccess.SeesLines(e, u, model) || SeesLinesToCreate(e, u, model));

    /// <summary>
    /// Oculta los campos archivados a quien no modela. Spec 006: cada campo lleva el acceso del usuario por rol
    /// (<c>access</c>) y los estados en que se captura; quien no modela no recibe los campos ocultos ni la política.
    /// </summary>
    private static EntityDef ForUser(EntityDef e, UserContext u, IReadOnlyDictionary<string, EntityDef> model)
    {
        // Spec 049 (auditoría 9, O-02; decisión del dueño, 2026-11-02): quien no modela no recibe una expresión que nombra algo
        // que no ve. La regla, la transición o el valor se quedan con su mensaje o etiqueta, sin el texto de la expresión.
        string? Visible(string? expression) => u.CanManageModel || !DerivedAccess.NamesHidden(e, expression, model, u) ? expression : null;
        bool Hidden(string? field) => field is not null && e.FindField(field) is { } f && !FieldAccess.CanSee(e, f, u);
        var fields = (u.CanManageModel ? e.Fields : e.ActiveFields)
            .Select(f => f with
            {
                FilledBy = null, // revisión adversarial 048 (F-07): solo salida; la calcula la ruta de un catálogo
                Access = FieldAccess.ByRole(e, f, u), EditableIn = e.Security?.For(f.Name)?.EditableIn,
                AcceptsTeams = e.Security?.AcceptsTeams(f.Name) == true ? true : null, // spec 015
                DefaultValue = Visible(f.DefaultValue),
            })
            .Where(f => u.CanManageModel || f.Access != FieldAccessLevels.Hidden)
            .ToList();
        // Spec 007: vistas y reportes se consultan por /api/views y /api/reports, ya filtrados para el usuario.
        return e with
        {
            Rules = u.CanManageModel ? e.Rules : e.Rules?.Select(r => (Visible(r.Assert) is null || Visible(r.When) != r.When
                ? r with { Assert = null!, When = null } : r) with { Field = Hidden(r.Field) ? null : r.Field }).ToList(),
            Workflow = u.CanManageModel || e.Workflow is null ? e.Workflow
                : e.Workflow with
                {
                    Transitions = e.Workflow.Transitions.Select(t => t with
                    {
                        When = Visible(t.When), RequiredFields = t.RequiredFields?.Where(f => !Hidden(f)).ToList(),
                        Set = t.Set?.Where(s => !Hidden(s.Field)).Select(s => Visible(s.Value) is null ? s with { Value = "" } : s).ToList(),
                    }).ToList(),
                },
            // Las acciones se evalúan en otros ámbitos (partidas, destino, registro creado) y nadie que no modele usa sus
            // expresiones (la procedencia y la búsqueda leen solo tipos, catálogos y campos): se mandan sin ellas.
            // Revisión adversarial 049 (F-01): sin las acciones sobre partidas cuyo campo no ve, ni la automatización que solo tenía esas.
            Automations = u.CanManageModel ? e.Automations : e.Automations?
                .Where(a => a.Actions.Count == 0 || a.Actions.Any(x => !Hidden(x.Lines)))
                .Select(a => a with { When = Visible(a.When), Actions = a.Actions.Where(x => !Hidden(x.Lines)).Select(WithoutExpressions).ToList() }).ToList(),
            // Spec 021 (H2 CA1): el editor manda esta versión con su plan; si otra persona cambió el catálogo, se rechaza.
            Version = u.CanManageModel ? ModelVersion.Of(e) : null,
            Fields = fields,
            Security = u.CanManageModel ? e.Security : null,
            Views = u.CanManageModel ? e.Views : null,
            Reports = u.CanManageModel ? e.Reports : null,
            Webhooks = u.CanManageModel ? e.Webhooks : null,
            Schedules = u.CanManageModel ? e.Schedules : null,
            // Spec 027: los usuarios eligen entre las plantillas que les aplican (/api/data/{e}/{id}/print).
            PrintTemplates = u.CanManageModel ? e.PrintTemplates : null,
        };
    }

    /// <summary>Spec 049 (O-02): una acción de automatización con sus campos y destinos, sin el texto de sus expresiones.</summary>
    private static AutomationAction WithoutExpressions(AutomationAction a) => a with
    {
        When = null, Filter = null, Sum = null, Quantity = null, UnitCost = null, FallbackCost = null, TotalCost = null,
        Set = a.Set?.Select(s => s with { Value = "" }).ToList(),
        Capture = a.Capture?.Select(s => s with { Value = "" }).ToList(),
        Match = a.Match?.Select(m => m with { Source = "" }).ToList(),
        CreateLines = a.CreateLines?.Select(l => l with { When = null, Set = l.Set.Select(s => s with { Value = "" }).ToList() }).ToList(),
    };
}
