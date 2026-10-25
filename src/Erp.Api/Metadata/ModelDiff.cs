using System.Text.Json;

namespace Erp.Api.Metadata;

/// <summary>
/// Plan mínimo para llevar un entorno al estado de un paquete, más lo que el plan no puede expresar. Spec 014:
/// <c>LocalArchives</c> son los campos que solo existen en el destino y el plan archivaría (requieren confirmación aparte).
/// </summary>
public sealed record ModelDiffResult(ChangePlan Plan, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings, IReadOnlySet<string> Created)
{
    public IReadOnlyList<string> LocalArchives { get; init; } = [];
}

/// <summary>
/// Spec 017 (reversión). <c>ArchiveEntities</c>/<c>ArchiveDashboards</c>: alcance en el que lo que no está en lo deseado se
/// archiva. <c>Applied</c>: el modelo justo después de aplicar el paquete; un campo del destino que lo deseado no trae solo
/// es «local» (requiere confirmación, 014) si tampoco estaba ahí.
/// </summary>
public sealed record DiffOptions(IReadOnlySet<string> ArchiveEntities, IReadOnlySet<string> ArchiveDashboards,
    IReadOnlyDictionary<string, EntityDef>? Applied = null, IReadOnlySet<string>? ArchiveApps = null);

/// <summary>
/// Spec 017: lo que el paquete podía tocar (sus catálogos con sus partidas y sus tableros) tal como estaba en un momento.
/// <c>Entities</c>/<c>Dashboards</c> son el alcance; las definiciones, solo las que existían. Spec 024: <c>Apps</c>/<c>AppDefs</c>,
/// los módulos de navegación (nulos en las instantáneas anteriores a la 024: sin módulos).
/// </summary>
public sealed record ModelSnapshot(IReadOnlyList<string> Entities, IReadOnlyList<string> Dashboards,
    IReadOnlyList<EntityDef> EntityDefs, IReadOnlyList<DashboardDef> DashboardDefs,
    IReadOnlyList<string>? Apps = null, IReadOnlyList<AppDef>? AppDefs = null)
{
    public static ModelSnapshot Take(IReadOnlySet<string> entities, IReadOnlySet<string> dashboards,
        IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlySet<string>? apps = null, IReadOnlyDictionary<string, AppDef>? appDefs = null) =>
        new(entities.Order(StringComparer.OrdinalIgnoreCase).ToList(), dashboards.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            model.Values.Where(e => entities.Contains(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            boards.Values.Where(d => dashboards.Contains(d.Name)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            apps?.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            apps is null ? null : (appDefs ?? new Dictionary<string, AppDef>()).Values.Where(a => apps.Contains(a.Name)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public IReadOnlyDictionary<string, EntityDef> EntityMap() => EntityDefs.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, DashboardDef> DashboardMap() => DashboardDefs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, AppDef> AppMap() => (AppDefs ?? []).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Spec 009: compara estados de modelo (no historiales). Es puro, como <see cref="PlanCompiler"/>: el resultado es un
/// <see cref="ChangePlan"/> que pasa por el compilador igual que cualquier cambio del modelador.
/// </summary>
public static class ModelDiff
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// <paramref name="desired"/> son solo los catálogos del paquete (el paquete manda sobre ellos); los demás catálogos
    /// y tableros del destino no se tocan.
    /// </summary>
    public static ModelDiffResult Compute(
        IReadOnlyDictionary<string, EntityDef> desired,
        IReadOnlyDictionary<string, DashboardDef> desiredBoards,
        IReadOnlyDictionary<string, EntityDef> target,
        IReadOnlyDictionary<string, DashboardDef> targetBoards,
        string description, DiffOptions? options = null,
        IReadOnlyDictionary<string, AppDef>? desiredApps = null, IReadOnlyDictionary<string, AppDef>? targetApps = null)
    {
        var conflicts = new List<string>();
        var warnings = new List<string>();
        var creates = new List<Operation>();
        var adds = new List<Operation>();
        var updates = new List<Operation>();
        var workflows = new List<Operation>();
        var sets = new List<Operation>();
        var archives = new List<Operation>();
        var boards = new List<Operation>();
        var source = options is null ? "el paquete" : "la versión anterior";
        var restoreEntities = new List<Operation>();
        var restoreFields = new List<Operation>();
        var created = new HashSet<string>(Ci);
        var localArchives = new List<string>();

        // 1) Catálogos nuevos: el documento antes que su partida; una referencia a un catálogo del paquete que aún no
        //    existe (ciclo A ↔ B) se agrega después con addField.
        var fresh = desired.Values.Where(d => !d.IsArchived && !target.ContainsKey(d.Name)).OrderBy(d => d.Name, Ci).ToList();
        var pending = fresh.Select(d => d.Name).ToHashSet(Ci);
        foreach (var d in OrderForCreation(fresh))
        {
            pending.Remove(d.Name);
            var fields = d.ActiveFields.Where(f => f.Type != FieldTypes.State).ToList();
            bool Later(FieldDef f) => f.Type == FieldTypes.Reference && pending.Contains(f.ReferenceEntity ?? "");
            creates.Add(new CreateEntityOp
            {
                Name = d.Name, Label = d.Label, LabelPlural = d.LabelPlural, Icon = d.Icon, Description = d.Description,
                DisplayField = d.DisplayField, Group = d.Group, PerCompany = d.PerCompany,
                Fields = fields.Where(f => !Later(f)).Select(Clean).ToList(),
            });
            adds.AddRange(fields.Where(Later).Select(f => new AddFieldOp { Entity = d.Name, Field = Clean(f) }));
            if (d.Workflow is not null) workflows.Add(new SetWorkflowOp { Entity = d.Name, Workflow = d.Workflow });
            Sets(d, null, sets);
            created.Add(d.Name);
        }

        // 2) Catálogos existentes.
        foreach (var d in desired.Values.Where(d => !d.IsArchived && target.ContainsKey(d.Name)).OrderBy(d => d.Name, Ci))
        {
            var t = target[d.Name];
            // Spec 017: un catálogo archivado se reactiva (su tabla conservó los registros) y se compara como activo.
            if (t.IsArchived)
            {
                restoreEntities.Add(new RestoreEntityOp { Entity = t.Name });
                t = t with { IsArchived = false };
            }
            if (t.IsChild != d.IsChild || !Ci.Equals(t.ParentEntity ?? "", d.ParentEntity ?? ""))
            {
                conflicts.Add($"«{d.Label}» es {(d.IsChild ? $"partida de {d.ParentEntity}" : "un catálogo")} en {source} y " +
                              $"{(t.IsChild ? $"partida de {t.ParentEntity}" : "un catálogo")} en este entorno.");
                continue;
            }
            // Spec 010: pasar a un registro por empresa es un cambio; volver a compartido no tiene operación.
            if (t.PerCompany && !d.PerCompany)
                conflicts.Add($"«{d.Label}» es por empresa en este entorno y compartido en {source}: un catálogo por empresa no vuelve a ser compartido.");
            if (EntityChange(d, t, warnings) is { } ue) updates.Add(ue);

            foreach (var df in d.ActiveFields.Where(f => f.Type != FieldTypes.State))
            {
                var tf = t.FindField(df.Name);
                var where = $"«{d.Label}» › {df.Label}";
                if (tf is null) { adds.Add(new AddFieldOp { Entity = t.Name, Field = Clean(df) }); continue; }
                if (StructureChange(df, tf) is { } why) { conflicts.Add($"{where}: {why}. No se puede cambiar la estructura de un campo existente."); continue; }
                // Spec 017: un campo archivado se reactiva con los datos que conservó su columna.
                if (tf.IsArchived)
                {
                    restoreFields.Add(new RestoreFieldOp { Entity = t.Name, Field = tf.Name });
                    tf = tf with { IsArchived = false };
                }
                if (FieldChange(t.Name, df, tf) is { } uf) updates.Add(uf);
            }
            foreach (var tf in t.ActiveFields.Where(f => f.Type != FieldTypes.State))
            {
                var df = d.FindField(tf.Name);
                if (df is { IsArchived: false }) continue;
                if (tf.Type == FieldTypes.Parent) continue; // ya se reportó como conflicto de partida
                archives.Add(new ArchiveFieldOp { Entity = t.Name, Field = tf.Name });
                if (df is not null) continue;
                if (options?.Applied is not { } applied)
                    localArchives.Add($"«{t.Label}» › {tf.Label} no viene en el paquete: se archiva (sus datos se conservan).");
                else if (applied.GetValueOrDefault(t.Name)?.FindField(tf.Name) is null)
                    localArchives.Add($"«{t.Label}» › {tf.Label} se agregó en este entorno después del paquete: se archiva (sus datos se conservan).");
            }

            if (!JsonEq(d.Workflow, t.Workflow))
            {
                if (d.Workflow is null) conflicts.Add($"«{d.Label}»: {source} no trae el proceso que tiene este entorno, y un proceso no se puede quitar.");
                else workflows.Add(new SetWorkflowOp { Entity = t.Name, Workflow = d.Workflow });
            }
            Sets(d, t, sets);
        }

        // 3) Tableros del paquete (los demás no se tocan).
        foreach (var db in desiredBoards.Values.Where(b => !b.IsArchived).OrderBy(b => b.Name, Ci))
        {
            targetBoards.TryGetValue(db.Name, out var tb);
            var wanted = db with { Name = tb?.Name ?? db.Name, IsArchived = false };
            if (tb is null || tb.IsArchived || !JsonEq(wanted, tb)) boards.Add(new SetDashboardOp { Dashboard = wanted });
        }

        // Spec 024: módulos de navegación del paquete (los demás no se tocan).
        var apps = new List<Operation>();
        targetApps ??= new Dictionary<string, AppDef>();
        foreach (var da in (desiredApps ?? new Dictionary<string, AppDef>()).Values.Where(a => !a.IsArchived).OrderBy(a => a.Name, Ci))
        {
            targetApps.TryGetValue(da.Name, out var ta);
            var wanted = da with { Name = ta?.Name ?? da.Name, IsArchived = false };
            if (ta is null || ta.IsArchived || !JsonEq(wanted, ta)) apps.Add(new SetAppOp { App = wanted });
        }
        if (options?.ArchiveApps is { } archiveApps)
            foreach (var n in archiveApps.Order(Ci))
                if (targetApps.TryGetValue(n, out var ta) && !ta.IsArchived && !(desiredApps?.TryGetValue(n, out var dap) == true && !dap.IsArchived))
                    apps.Add(new ArchiveAppOp { Name = ta.Name });

        // Spec 017 (reversión): lo que existe en el alcance y no en lo deseado se archiva; nunca se borra.
        var archiveEntities = new List<Operation>();
        if (options is not null)
        {
            var gone = options.ArchiveEntities
                .Where(n => target.TryGetValue(n, out var t) && !t.IsArchived && !(desired.TryGetValue(n, out var d) && !d.IsArchived))
                .Select(n => target[n]).ToList();
            var goneNames = gone.Select(e => e.Name).ToHashSet(Ci);
            // El campo de partidas del documento primero: si no, documento y partida se referencian y ninguno se puede archivar.
            foreach (var e in gone.OrderBy(e => e.Name, Ci))
                foreach (var f in e.ActiveFields.Where(f => f.Type == FieldTypes.Lines && goneNames.Contains(f.ChildEntity ?? "")))
                    archiveEntities.Add(new ArchiveFieldOp { Entity = e.Name, Field = f.Name });
            archiveEntities.AddRange(OrderForArchive(gone).Select(e => new ArchiveEntityOp { Entity = e.Name }));
            foreach (var n in options.ArchiveDashboards.Order(Ci))
                if (targetBoards.TryGetValue(n, out var tb) && !tb.IsArchived && !(desiredBoards.TryGetValue(n, out var dbd) && !dbd.IsArchived))
                    boards.Add(new ArchiveDashboardOp { Name = tb.Name });
        }

        var plan = new ChangePlan
        {
            Description = description,
            Operations = [.. restoreEntities, .. creates, .. restoreFields, .. adds, .. updates, .. workflows, .. sets, .. archives,
                .. archiveEntities, .. boards, .. apps],
        };
        return new ModelDiffResult(plan, conflicts, warnings, created) { LocalArchives = localArchives };
    }

    /// <summary>
    /// Exportar = el diff de los elegidos contra «el mismo modelo sin ellos»: el plan que los crea, con las referencias a
    /// catálogos que no viajan resueltas contra el resto del modelo.
    /// </summary>
    public static ModelDiffResult Export(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlySet<string> entities, IReadOnlySet<string> dashboards, string description, IReadOnlyDictionary<string, AppDef>? apps = null)
    {
        var desired = model.Where(kv => entities.Contains(kv.Key) && !kv.Value.IsArchived).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        var rest = model.Where(kv => !entities.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        var desiredBoards = boards.Where(kv => dashboards.Contains(kv.Key) && !kv.Value.IsArchived).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        var restBoards = boards.Where(kv => !dashboards.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        // Spec 024: el módulo declarado de cada catálogo exportado viaja con él.
        var appNames = desired.Values.Select(Apps.Of).ToHashSet(Ci);
        var allApps = apps ?? new Dictionary<string, AppDef>();
        var desiredApps = allApps.Where(kv => appNames.Contains(kv.Key) && !kv.Value.IsArchived).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        var restApps = allApps.Where(kv => !desiredApps.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        return Compute(desired, desiredBoards, rest, restBoards, description, null, desiredApps, restApps);
    }

    /// <summary>
    /// Spec 017: el diff inverso. Lo deseado es la instantánea <paramref name="before"/>; lo que el paquete creó se archiva, lo
    /// que cambió vuelve y lo que archivó se reactiva. <paramref name="after"/> distingue lo que cambió aquí después de aplicar.
    /// </summary>
    public static ModelDiffResult Revert(ModelSnapshot before, ModelSnapshot after, IReadOnlyDictionary<string, EntityDef> current,
        IReadOnlyDictionary<string, DashboardDef> currentBoards, string description, IReadOnlyDictionary<string, AppDef>? currentApps = null)
    {
        var applied = after.EntityMap();
        var appliedBoards = after.DashboardMap();
        var scope = before.Entities.Concat(after.Entities).ToHashSet(Ci);
        var scopeBoards = before.Dashboards.Concat(after.Dashboards).ToHashSet(Ci);
        var scopeApps = (before.Apps ?? []).Concat(after.Apps ?? []).ToHashSet(Ci);
        var diff = Compute(before.EntityMap(), before.DashboardMap(), current, currentBoards, description,
            new DiffOptions(scope, scopeBoards, applied, scopeApps), before.AppMap(), currentApps);

        // Deriva: el modelador cambió algo del alcance después de aplicar; revertir también lo deshace.
        var warnings = new List<string>(diff.Warnings);
        foreach (var n in scope.Order(Ci))
        {
            var now = current.GetValueOrDefault(n);
            var then = applied.GetValueOrDefault(n);
            if ((now is not null || then is not null) && !JsonEq(now, then))
                warnings.Add($"«{(now ?? then)!.LabelPlural}» cambió en este entorno después de aplicar el paquete: revertir también deshace ese cambio.");
        }
        foreach (var n in scopeBoards.Order(Ci))
        {
            var now = currentBoards.GetValueOrDefault(n);
            var then = appliedBoards.GetValueOrDefault(n);
            if ((now is not null || then is not null) && !JsonEq(now, then))
                warnings.Add($"El tablero «{(now ?? then)!.Label}» cambió en este entorno después de aplicar el paquete: revertir también deshace ese cambio.");
        }
        var archived = diff.Plan.Operations.OfType<ArchiveEntityOp>().Select(o => current[o.Entity].LabelPlural).ToList();
        if (archived.Count > 0)
            warnings.Add($"Se archivan los catálogos que creó el paquete ({string.Join(", ", archived)}): dejan de verse y sus registros se conservan.");
        return diff with { Warnings = warnings };
    }

    /// <summary>Quien referencia antes que el referido (la partida antes que su documento): nadie activo apunta a un archivado.</summary>
    private static IEnumerable<EntityDef> OrderForArchive(List<EntityDef> gone)
    {
        var remaining = gone.OrderBy(e => e.Name, Ci).ToList();
        bool Referenced(EntityDef e) => remaining.Any(o => !Ci.Equals(o.Name, e.Name) &&
            o.ActiveFields.Any(f => f.Type is FieldTypes.Reference or FieldTypes.Parent && Ci.Equals(f.ReferenceEntity, e.Name)));
        while (remaining.Count > 0)
        {
            // En un ciclo (A ↔ B) no hay orden posible: el compilador lo reporta y la reversión queda bloqueada.
            var next = remaining.FirstOrDefault(e => !Referenced(e)) ?? remaining[0];
            remaining.Remove(next);
            yield return next;
        }
    }

    /// <summary>Catálogos y tableros que crea un paquete (su alcance).</summary>
    public static (IReadOnlySet<string> Entities, IReadOnlySet<string> Dashboards) Scope(ModulePackage p)
    {
        var ops = p.FullChain.SelectMany(c => c.Plan.Operations).ToList();
        return (ops.OfType<CreateEntityOp>().Select(o => o.Name).ToHashSet(Ci),
                ops.OfType<SetDashboardOp>().Select(o => o.Dashboard?.Name ?? "").ToHashSet(Ci));
    }

    /// <summary>Spec 024: módulos de navegación que declara un paquete.</summary>
    public static IReadOnlySet<string> ScopeApps(ModulePackage p) =>
        p.FullChain.SelectMany(c => c.Plan.Operations).OfType<SetAppOp>().Select(o => o.App?.Name?.Trim() ?? "").Where(n => n.Length > 0).ToHashSet(Ci);

    /// <summary>
    /// Spec 023: catálogos que el paquete modifica sin crearlos (un campo, una regla o una automatización en un catálogo de
    /// otro módulo). Entran al estado deseado y a la instantánea de la reversión; no a los permisos del paquete.
    /// </summary>
    public static IReadOnlySet<string> Extended(ModulePackage p)
    {
        var created = Scope(p).Entities;
        return p.FullChain.SelectMany(c => c.Plan.Operations).Select(EntityOf).OfType<string>()
            .Where(e => !created.Contains(e)).ToHashSet(Ci);
    }

    /// <summary>Spec 023: lo que la instantánea de la 017 guarda de un paquete: lo que crea y lo que extiende.</summary>
    public static (IReadOnlySet<string> Entities, IReadOnlySet<string> Dashboards) SnapshotScope(ModulePackage p)
    {
        var (entities, dashboards) = Scope(p);
        return (entities.Union(Extended(p), Ci).ToHashSet(Ci), dashboards);
    }

    /// <summary>Catálogo existente que toca una operación (null si crea uno o no toca ninguno).</summary>
    private static string? EntityOf(Operation op) => op switch
    {
        UpdateEntityOp o => o.Entity, ArchiveEntityOp o => o.Entity, RestoreEntityOp o => o.Entity,
        AddFieldOp o => o.Entity, UpdateFieldOp o => o.Entity, ArchiveFieldOp o => o.Entity, RestoreFieldOp o => o.Entity,
        SetWorkflowOp o => o.Entity, SetRulesOp o => o.Entity, SetAutomationsOp o => o.Entity, SetSecurityOp o => o.Entity,
        SetLayoutOp o => o.Entity, SetViewsOp o => o.Entity, SetReportsOp o => o.Entity, SetWebhooksOp o => o.Entity,
        SetSchedulesOp o => o.Entity, SetLedgerOp o => o.Entity,
        _ => null,
    };

    /// <summary>
    /// Estado deseado: el plan del paquete compilado sobre el destino sin los catálogos y tableros que el paquete crea.
    /// Los errores (p. ej. un catálogo del que depende y que este entorno no tiene) invalidan el paquete aquí. Spec 023:
    /// incluye los catálogos que el paquete extiende, con su estado completo (lo del destino más lo del paquete).
    /// </summary>
    public static (IReadOnlyDictionary<string, EntityDef> Entities, IReadOnlyDictionary<string, DashboardDef> Dashboards, IReadOnlyList<PlanError> Errors,
        IReadOnlyList<PlanStep> Steps) Desired(ModulePackage p, IReadOnlyDictionary<string, EntityDef> target, IReadOnlyDictionary<string, DashboardDef> targetBoards, IReadOnlySet<string> roles)
    {
        var d = DesiredState(p, target, targetBoards, roles, null);
        return (d.Entities, d.Dashboards, d.Errors, d.Steps);
    }

    /// <summary>Spec 024: el estado deseado con los módulos de navegación que declara el paquete.</summary>
    public sealed record DesiredResult(IReadOnlyDictionary<string, EntityDef> Entities, IReadOnlyDictionary<string, DashboardDef> Dashboards,
        IReadOnlyDictionary<string, AppDef> Apps, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps);

    public static DesiredResult DesiredState(ModulePackage p, IReadOnlyDictionary<string, EntityDef> target,
        IReadOnlyDictionary<string, DashboardDef> targetBoards, IReadOnlySet<string> roles, IReadOnlyDictionary<string, AppDef>? targetApps)
    {
        var (entities, dashboards) = Scope(p);
        var appScope = ScopeApps(p);
        IReadOnlyDictionary<string, AppDef> apps = (targetApps ?? new Dictionary<string, AppDef>())
            .Where(kv => !appScope.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        IReadOnlyDictionary<string, EntityDef> model = target.Where(kv => !entities.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        IReadOnlyDictionary<string, DashboardDef> boards = targetBoards.Where(kv => !dashboards.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci);
        var errors = new List<PlanError>();
        var steps = new List<PlanStep>();
        var chain = p.FullChain;
        foreach (var (label, plan) in chain)
        {
            var c = PlanCompiler.Compile(plan, model, roles, boards, apps);
            var prefix = chain.Count > 1 ? $"[{label}] " : "";
            errors.AddRange(c.Preview.Errors.Select(e => e with { Message = prefix + e.Message }));
            steps.AddRange(c.Preview.Steps.Select(s => s with { Description = prefix + s.Description }));
            (model, boards, apps) = (c.ResultingModel, c.Dashboards!, c.Apps!);
        }
        var extended = Extended(p);
        return new(model.Where(kv => entities.Contains(kv.Key) || extended.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci),
                boards.Where(kv => dashboards.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci),
                apps.Where(kv => appScope.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci),
                errors, steps);
    }

    // ---------------- Detalle ----------------

    /// <summary>Primero los que no dependen de otro catálogo nuevo; la partida siempre después de su documento.</summary>
    private static IEnumerable<EntityDef> OrderForCreation(List<EntityDef> fresh)
    {
        var remaining = new List<EntityDef>(fresh);
        var names = fresh.Select(d => d.Name).ToHashSet(Ci);
        var done = new HashSet<string>(Ci);
        bool Ready(string? target, EntityDef self) => target is null || !names.Contains(target) || done.Contains(target) || Ci.Equals(target, self.Name);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(d => Ready(d.ParentEntity, d) &&
                           d.ActiveFields.Where(f => f.Type == FieldTypes.Reference).All(f => Ready(f.ReferenceEntity, d)))
                       ?? remaining.FirstOrDefault(d => Ready(d.ParentEntity, d))
                       ?? remaining[0];
            remaining.Remove(next);
            done.Add(next.Name);
            yield return next;
        }
    }

    /// <summary>Solo lo declarado: el acceso por rol (spec 006) es salida, no metadato.</summary>
    private static FieldDef Clean(FieldDef f) => f with { Access = null, EditableIn = null, IsArchived = false };

    private static void Sets(EntityDef d, EntityDef? t, List<Operation> sets)
    {
        var name = t?.Name ?? d.Name;
        if (!JsonEq(Norm(d.Rules), Norm(t?.Rules))) sets.Add(new SetRulesOp { Entity = name, Rules = d.Rules ?? [] });
        if (!JsonEq(d.Security, t?.Security)) sets.Add(new SetSecurityOp { Entity = name, Security = d.Security });
        if (!JsonEq(d.Layout, t?.Layout)) sets.Add(new SetLayoutOp { Entity = name, Layout = d.Layout });
        if (!JsonEq(Norm(d.Views), Norm(t?.Views))) sets.Add(new SetViewsOp { Entity = name, Views = d.Views ?? [] });
        if (!JsonEq(Norm(d.Reports), Norm(t?.Reports))) sets.Add(new SetReportsOp { Entity = name, Reports = d.Reports ?? [] });
        if (!JsonEq(Norm(d.Automations), Norm(t?.Automations))) sets.Add(new SetAutomationsOp { Entity = name, Automations = d.Automations ?? [] });
        if (!JsonEq(Norm(d.Webhooks), Norm(t?.Webhooks))) sets.Add(new SetWebhooksOp { Entity = name, Webhooks = d.Webhooks ?? [] });
        // Spec 016: si una tarea corre es decisión de cada entorno (meta.ScheduleState): solo viaja la definición.
        if (!JsonEq(Norm(d.Schedules), Norm(t?.Schedules))) sets.Add(new SetSchedulesOp { Entity = name, Schedules = d.Schedules ?? [] });
        if (!JsonEq(d.Ledger, t?.Ledger)) sets.Add(new SetLedgerOp { Entity = name, Ledger = d.Ledger });
    }

    private static UpdateEntityOp? EntityChange(EntityDef d, EntityDef t, List<string> warnings)
    {
        var displayField = !Ci.Equals(d.DisplayField ?? "", t.DisplayField ?? "") && d.DisplayField is not null ? d.DisplayField : null;
        if (d.DisplayField is null && t.DisplayField is not null)
            warnings.Add($"«{t.Label}»: el paquete no indica campo para mostrar; se conserva «{t.DisplayField}».");
        var op = new UpdateEntityOp
        {
            Entity = t.Name,
            Label = d.Label != t.Label ? d.Label : null,
            LabelPlural = d.LabelPlural != t.LabelPlural ? d.LabelPlural : null,
            Icon = d.Icon != t.Icon ? d.Icon : null,
            Description = !StrEq(d.Description, t.Description) ? d.Description ?? "" : null,
            DisplayField = displayField,
            Group = !StrEq(d.Group, t.Group) ? d.Group ?? "" : null,
            PerCompany = d.PerCompany && !t.PerCompany ? true : null,
        };
        return op is { Label: null, LabelPlural: null, Icon: null, Description: null, DisplayField: null, Group: null, PerCompany: null } ? null : op;
    }

    /// <summary>Lo que <c>updateField</c> no puede cambiar.</summary>
    private static string? StructureChange(FieldDef d, FieldDef t)
    {
        if (d.Type != t.Type) return $"el tipo cambia de «{t.Type}» a «{d.Type}»";
        if (!Ci.Equals(d.ReferenceEntity ?? "", t.ReferenceEntity ?? "")) return $"el catálogo referido cambia de «{t.ReferenceEntity}» a «{d.ReferenceEntity}»";
        if (!Ci.Equals(d.ChildEntity ?? "", t.ChildEntity ?? "")) return "el catálogo de partidas cambia";
        if (d.ResultType != t.ResultType) return $"el tipo de resultado cambia de «{t.ResultType}» a «{d.ResultType}»";
        if (d.MaxLength != t.MaxLength) return $"la longitud cambia de {t.MaxLength} a {d.MaxLength}";
        if (d.Precision != t.Precision || d.Scale != t.Scale) return "la precisión o la escala cambian";
        if (d.Unique != t.Unique) return d.Unique ? "pasa a ser único" : "deja de ser único";
        if (!StrEq(d.Prefix, t.Prefix) || d.Padding != t.Padding) return "el prefijo o los dígitos del folio cambian";
        return null;
    }

    private static UpdateFieldOp? FieldChange(string entity, FieldDef d, FieldDef t)
    {
        var op = new UpdateFieldOp
        {
            Entity = entity,
            Field = t.Name,
            Label = d.Label != t.Label ? d.Label : null,
            Required = d.Required != t.Required ? d.Required : null,
            ShowInList = d.ShowInList != t.ShowInList ? d.ShowInList : null,
            Options = d.Type == FieldTypes.Choice && !(d.Options ?? []).SequenceEqual(t.Options ?? []) ? d.Options ?? [] : null,
            RequiredForRoles = !(d.RequiredForRoles ?? []).Order(Ci).SequenceEqual((t.RequiredForRoles ?? []).Order(Ci), Ci) ? d.RequiredForRoles ?? [] : null,
            DisplayOrder = d.DisplayOrder != t.DisplayOrder ? d.DisplayOrder : null,
            DefaultValue = !StrEq(d.DefaultValue, t.DefaultValue) ? d.DefaultValue ?? "" : null,
            Formula = d.Type == FieldTypes.Formula && d.Formula != t.Formula ? d.Formula : null,
            Editable = d.Editable != t.Editable ? d.Editable : null,
            LookupFilter = d.Type == FieldTypes.Reference && !JsonEq(d.LookupFilter, t.LookupFilter) ? d.LookupFilter ?? new LookupFilter("", "") : null,
        };
        return op is { Label: null, Required: null, ShowInList: null, Options: null, RequiredForRoles: null, DisplayOrder: null,
            DefaultValue: null, Formula: null, Editable: null, LookupFilter: null } ? null : op;
    }

    private static IReadOnlyList<T>? Norm<T>(IReadOnlyList<T>? list) => list is { Count: > 0 } ? list : null;

    private static bool StrEq(string? a, string? b) => (string.IsNullOrEmpty(a) ? "" : a) == (string.IsNullOrEmpty(b) ? "" : b);

    private static bool JsonEq<T>(T? a, T? b) =>
        JsonSerializer.Serialize(a, MetadataStore.Json) == JsonSerializer.Serialize(b, MetadataStore.Json);
}
