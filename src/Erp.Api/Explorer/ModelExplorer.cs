using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Explorer;

public sealed record RelField(string Name, string Label, string Type, string? ResultType, string? Target, bool Required, bool Unique,
    bool Formula, bool Autonumber, bool HasDefault, bool State);
public sealed record RelNode(string Name, string Label, string LabelPlural, string Icon, string App, bool IsChild, bool HasWorkflow,
    bool PerCompany, IReadOnlyList<RelField> Fields);
/// <param name="Kind"><c>reference</c>, <c>lines</c> (documento → partida), <c>parent</c> (partida → documento) o <c>user</c>.</param>
public sealed record RelEdge(string From, string Field, string To, string Kind);
public sealed record RelGraph(IReadOnlyList<AppRef> Apps, IReadOnlyList<RelNode> Nodes, IReadOnlyList<RelEdge> Edges);

public sealed record FlowNode(string Name, string Label, string App);
/// <param name="Mode"><c>sync</c>, <c>async</c> o <c>schedule</c>.</param>
/// <param name="Triggers">El destino tiene automatizaciones del evento que la acción provoca.</param>
/// <param name="Cycle">La arista forma parte de un ciclo de automatizaciones.</param>
public sealed record FlowEdge(string From, string To, string Owner, string Name, string Label, string Trigger, string Mode, string Action,
    string Detail, bool Triggers, bool Cycle);
public sealed record FlowGraph(IReadOnlyList<AppRef> Apps, IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges);

public sealed record RoleInfo(int Id, string Name, string? Description, bool IsSuperuser);
public sealed record RoleSummary(string Name, string? Description, bool IsSuperuser, IReadOnlyList<string> Inherits);
public sealed record ScopeGrant(string Access, string Scope, string? Where);
public sealed record RoleAccess(string Role, string Entity, bool Read, bool Create, bool Update, bool Delete, IReadOnlyList<string> InheritedFrom,
    IReadOnlyList<ScopeGrant> Scopes, IReadOnlyList<string> Hidden, IReadOnlyList<string> ReadOnly);
public sealed record SecurityEntity(string Name, string Label, string App, string AppLabel);
public sealed record SecurityMatrix(IReadOnlyList<RoleSummary> Roles, IReadOnlyList<SecurityEntity> Entities, IReadOnlyList<RoleAccess> Cells);

public sealed record NavField(string Name, string Label, string Type);
public sealed record NavSection(string Name, string Label, string? Tab, IReadOnlyList<NavField> Fields);
public sealed record NavEntity(string Name, string Label, string LabelPlural, string Icon, bool HasWorkflow, bool Layout,
    IReadOnlyList<NavSection> Sections, IReadOnlyList<string> Lines);
public sealed record NavApp(string Name, string Label, string Icon, IReadOnlyList<NavEntity> Entities);

/// <summary>Spec 025: vistas del modelo lógico. Puras, como <see cref="PlanCompiler"/>: reciben el modelo y no tocan la base.</summary>
public static class ModelExplorer
{
    public const string UserNode = "$user";
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    // ---------- H2: relaciones ----------

    public static RelGraph Relations(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, AppDef> apps,
        string? app = null, string? q = null)
    {
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var entities = model.Values.Where(e => !e.IsArchived)
            .Where(e => app is null || Ci.Equals(PhysicalModel.AppOf(e, model), app))
            .Where(e => q is null || Has(e.Name, q) || Has(e.Label, q) || Has(e.LabelPlural, q) || e.ActiveFields.Any(f => Has(f.Name, q) || Has(f.Label, q)))
            .OrderBy(e => e.Name, Ci).ToList();
        var nodes = entities.Select(e => new RelNode(e.Name, e.Label, e.LabelPlural, e.Icon, PhysicalModel.AppOf(e, model), e.IsChild,
            e.Workflow is not null, e.PerCompany,
            e.ActiveFields.Select(f => new RelField(f.Name, f.Label, f.Type, f.ResultType,
                f.Type is FieldTypes.Reference or FieldTypes.Parent ? f.ReferenceEntity : f.Type == FieldTypes.Lines ? f.ChildEntity : null,
                f.Required, f.Unique, f.Type == FieldTypes.Formula, f.Type == FieldTypes.Autonumber,
                !string.IsNullOrWhiteSpace(f.DefaultValue), f.Type == FieldTypes.State)).ToList())).ToList();
        var edges = new List<RelEdge>();
        foreach (var e in entities)
            foreach (var f in e.ActiveFields)
            {
                var (to, kind) = f.Type switch
                {
                    FieldTypes.Reference => (f.ReferenceEntity, "reference"),
                    FieldTypes.Parent => (f.ReferenceEntity, "parent"),
                    FieldTypes.Lines => (f.ChildEntity, "lines"),
                    FieldTypes.User => (UserNode, "user"),
                    _ => (null, null),
                };
                if (to is not null && kind is not null && (to == UserNode || model.ContainsKey(to)))
                    edges.Add(new(e.Name, f.Name, to == UserNode ? to : model[to].Name, kind));
            }
        return new(PhysicalModel.AppRefs(model, apps), nodes, edges);
    }

    private static bool Has(string? s, string q) =>
        s is not null && System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(s, q,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    // ---------- H5: flujo de automatizaciones ----------

    /// <param name="apps">Para la leyenda y los colores: los mismos módulos y en el mismo orden que en relaciones.</param>
    public static FlowGraph Flow(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, AppDef>? apps = null)
    {
        var raw = new List<(FlowEdge Edge, string? Effect)>();
        foreach (var e in model.Values.Where(e => !e.IsArchived).OrderBy(e => e.Name, Ci))
        {
            foreach (var auto in e.Automations ?? [])
                raw.AddRange(Edges(e, auto.Actions, model, auto.Name, auto.Label, Trigger(e, auto), auto.IsAsync ? AutomationModes.Async : AutomationModes.Sync));
            foreach (var s in e.Schedules ?? [])
                raw.AddRange(Edges(e, s.Actions, model, s.Name, s.Label, Frequency(s), "schedule"));
        }

        // Ciclos: componentes fuertes sobre las aristas que disparan automatizaciones (las tareas no las dispara un evento).
        var graph = raw.Where(x => x.Edge.Triggers && x.Edge.Mode != "schedule" && x.Effect is not null)
            .GroupBy(x => x.Edge.From, Ci).ToDictionary(g => g.Key, g => g.Select(x => x.Effect!).Distinct(Ci).ToList(), Ci);
        var component = StrongComponents(graph);
        bool InCycle(string from, string to) =>
            component.TryGetValue(from, out var a) && component.TryGetValue(to, out var b) && a == b &&
            (component.Values.Count(c => c == a) > 1 || graph.GetValueOrDefault(from)?.Contains(to, Ci) == true);

        var edges = raw.Select(x => x.Edge with
        {
            Cycle = x.Edge.Triggers && x.Edge.Mode != "schedule" && x.Effect is not null && InCycle(x.Edge.From, x.Effect),
        }).ToList();
        var names = edges.SelectMany(x => new[] { x.From, x.To }).Distinct(Ci);
        var nodes = names.Where(model.ContainsKey).Select(n => model[n]).OrderBy(n => n.Name, Ci)
            .Select(n => new FlowNode(n.Name, n.LabelPlural, PhysicalModel.AppOf(n, model))).ToList();
        return new(PhysicalModel.AppRefs(model, apps ?? new Dictionary<string, AppDef>()), nodes, edges);
    }

    private static IEnumerable<(FlowEdge, string?)> Edges(EntityDef e, IReadOnlyList<AutomationAction> actions, IReadOnlyDictionary<string, EntityDef> model,
        string name, string label, string trigger, string mode)
    {
        foreach (var a in actions)
        {
            if (AutomationRules.Scope(e, a, model) is not { } scope || AutomationRules.Target(scope, a, model) is not { } target) continue;
            var effects = AutomationRules.Effects(e, a, model).ToList();
            var firing = effects.FirstOrDefault(x => x.Entity.Automations?.Any(au => au.On == x.Event) == true);
            yield return (new FlowEdge(e.Name, target.Name, e.Name, name, label, trigger, mode, a.Type, Detail(a, target),
                firing.Entity is not null, false), firing.Entity?.Name);
            // Spec 022: capture escribe en el registro que disparó la acción.
            if (a.Capture is { Count: > 0 })
                yield return (new FlowEdge(target.Name, AutomationRules.Root(scope, model).Name, e.Name, name, label, trigger, mode, "capture",
                    $"devuelve {string.Join(", ", a.Capture.Select(c => c.Field))}", false, false), null);
        }
    }

    private static string Detail(AutomationAction a, EntityDef target) => a.Type switch
    {
        AutomationActions.Update => $"asigna {string.Join(", ", (a.Set ?? []).Select(s => s.Field))}",
        AutomationActions.Transition => $"ejecuta «{target.Workflow?.Transitions.FirstOrDefault(t => Ci.Equals(t.Name, a.Transition))?.Label ?? a.Transition}»",
        AutomationActions.Create => $"crea {target.Label}{(a.CreateLines is { Count: > 0 } ? " con partidas" : "")}{(a.Transition is null ? "" : $" y ejecuta {a.Transition}")}",
        AutomationActions.Rollup => $"suma {a.Sum} en {a.Field}",
        AutomationActions.Cost => $"valúa con capas en {a.Field}",
        _ => a.Type,
    };

    private static string Trigger(EntityDef e, Automation a) => a.On switch
    {
        AutomationEvents.Create => "Al crear",
        AutomationEvents.Update => "Al editar",
        AutomationEvents.EnterState => $"Al entrar a {e.Workflow?.FindState(a.State)?.Label ?? a.State}",
        AutomationEvents.Transition => $"Al ejecutar {e.Workflow?.Transitions.FirstOrDefault(t => Ci.Equals(t.Name, a.Transition))?.Label ?? a.Transition}",
        _ => a.On,
    };

    private static string Frequency(ScheduleDef s) => s.Every switch
    {
        ScheduleFrequencies.Minutes => $"Cada {s.Interval ?? 1} minutos",
        ScheduleFrequencies.Hour => "Cada hora",
        ScheduleFrequencies.Day => $"Cada día{(s.At is null ? "" : $" a las {s.At}")}",
        ScheduleFrequencies.Week => $"Cada semana{(s.At is null ? "" : $" a las {s.At}")}",
        ScheduleFrequencies.Month => $"Cada mes{(s.Day is null ? "" : $" el día {s.Day}")}",
        _ => s.Every,
    };

    /// <summary>Tarjan: número de componente fuerte de cada nodo.</summary>
    private static Dictionary<string, int> StrongComponents(Dictionary<string, List<string>> graph)
    {
        var index = new Dictionary<string, int>(Ci);
        var low = new Dictionary<string, int>(Ci);
        var onStack = new HashSet<string>(Ci);
        var stack = new Stack<string>();
        var comp = new Dictionary<string, int>(Ci);
        var counter = 0;
        var nodes = graph.Keys.Concat(graph.Values.SelectMany(v => v)).Distinct(Ci).ToList();
        foreach (var n in nodes) if (!index.ContainsKey(n)) Visit(n);
        return comp;

        void Visit(string v)
        {
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in graph.GetValueOrDefault(v) ?? [])
            {
                if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            }
            if (low[v] != index[v]) return;
            string x;
            do { x = stack.Pop(); onStack.Remove(x); comp[x] = index[v]; } while (!Ci.Equals(x, v));
        }
    }

    // ---------- H6: seguridad ----------

    public static SecurityMatrix Security(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyList<RoleInfo> roles,
        IReadOnlyList<(int RoleId, PermRow Perm)> perms, IReadOnlyList<(int RoleId, int ParentRoleId)> inherit,
        IReadOnlyDictionary<string, AppDef>? apps = null)
    {
        var byId = roles.ToDictionary(r => r.Id);
        var permsByRole = perms.ToLookup(p => p.RoleId, p => p.Perm);
        var entities = model.Values.Where(e => !e.IsArchived && !e.IsChild).OrderBy(e => e.LabelPlural, StringComparer.Create(new("es-MX"), true)).ToList();
        var summaries = roles.OrderBy(r => r.Name, StringComparer.Create(new("es-MX"), true)).Select(r => new RoleSummary(r.Name, r.Description, r.IsSuperuser,
            inherit.Where(x => x.RoleId == r.Id).Select(x => byId.GetValueOrDefault(x.ParentRoleId)?.Name).OfType<string>().ToList())).ToList();
        var cells = new List<RoleAccess>();
        foreach (var r in roles)
        {
            var closure = RoleGraph.Expand([r.Id], inherit).Where(byId.ContainsKey).ToList();
            var names = closure.Select(id => byId[id].Name).ToList();
            var superuser = closure.Any(id => byId[id].IsSuperuser);
            foreach (var e in entities)
            {
                var direct = permsByRole[r.Id].FirstOrDefault(p => Ci.Equals(p.EntityName, e.Name));
                var all = closure.SelectMany(id => permsByRole[id].Where(p => Ci.Equals(p.EntityName, e.Name)).Select(p => (id, p))).ToList();
                bool Any(Func<PermRow, bool> f) => superuser || all.Any(x => f(x.p));
                var inherited = all.Where(x => x.id != r.Id && Adds(x.p, direct)).Select(x => byId[x.id].Name).Distinct(Ci).ToList();
                var sec = e.Security;
                bool Applies(IReadOnlyList<string>? list) => list is null || list.Any(n => names.Contains(n, Ci));
                var scopes = superuser ? new List<ScopeGrant>() : (sec?.Records ?? []).Where(g => Applies(g.Roles))
                    .Select(g => new ScopeGrant(g.Writes ? "write" : "read", g.EffectiveScope, g.Where)).ToList();
                var hidden = superuser ? new List<string>() : (sec?.Fields ?? []).Where(f => f.Read is not null && !Applies(f.Read)).Select(f => f.Field).ToList();
                var readOnly = superuser ? new List<string>() : (sec?.Fields ?? []).Where(f => !hidden.Contains(f.Field, Ci) && f.Edit is not null && !Applies(f.Edit))
                    .Select(f => f.Field).ToList();
                cells.Add(new(r.Name, e.Name, Any(p => p.CanRead), Any(p => p.CanCreate), Any(p => p.CanUpdate), Any(p => p.CanDelete),
                    inherited, scopes, hidden, readOnly));
            }
        }
        var appLabels = PhysicalModel.AppRefs(model, apps ?? new Dictionary<string, AppDef>()).ToDictionary(a => a.Name, a => a.Label, Ci);
        return new(summaries, entities.Select(e =>
        {
            var app = PhysicalModel.AppOf(e, model);
            return new SecurityEntity(e.Name, e.LabelPlural, app, appLabels.GetValueOrDefault(app) ?? app);
        }).ToList(), cells);
    }

    private static bool Adds(PermRow p, PermRow? direct) =>
        (p.CanRead && direct?.CanRead != true) || (p.CanCreate && direct?.CanCreate != true) ||
        (p.CanUpdate && direct?.CanUpdate != true) || (p.CanDelete && direct?.CanDelete != true);

    // ---------- H6: navegación ----------

    public static IReadOnlyList<NavApp> Navigation(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, AppDef> apps) =>
        Apps.Effective(model, apps, _ => true).Select(a => new NavApp(a.Name, a.Label, a.Icon,
            a.Entities.Select(n => model[n]).Select(e => new NavEntity(e.Name, e.Label, e.LabelPlural, e.Icon, e.Workflow is not null,
                e.Layout is { Sections.Count: > 0 }, Sections(e),
                e.ActiveFields.Where(f => f.Type == FieldTypes.Lines && f.ChildEntity is not null).Select(f => f.ChildEntity!).ToList())).ToList())).ToList();

    private static IReadOnlyList<NavSection> Sections(EntityDef e)
    {
        NavField F(FieldDef f) => new(f.Name, f.Label, f.Type);
        if (e.Layout is not { Sections.Count: > 0 } layout)
            return [new("Formulario", "Formulario (en el orden de los campos)", null, e.ActiveFields.Select(F).ToList())];
        return layout.Sections.Select(s => new NavSection(s.Name, s.Label, s.Tab,
            s.Fields.Select(i => e.FindField(i.Field)).OfType<FieldDef>().Where(f => !f.IsArchived).Select(F).ToList())).ToList();
    }

    // ---------- H3: ficha ----------

    public sealed record PackageRef(string Id, string Name, bool Created, IReadOnlyList<string> Versions, string? InstalledVersion);

    /// <summary>Paquetes de <c>modules/</c> cuya cadena (base y actualizaciones) crea o modifica el catálogo.</summary>
    public static IReadOnlyList<PackageRef> Packages(string entity, IReadOnlyList<ModulePackage> packages, IReadOnlyDictionary<string, string> installed)
    {
        var list = new List<PackageRef>();
        foreach (var p in packages)
        {
            var versions = p.FullChain.Where(c => c.Plan.Operations.Any(o => Ci.Equals(EntityOf(o), entity)))
                .Select(c => c.Label == "base" ? "base" : c.Label.TrimStart('v')).ToList();
            if (versions.Count == 0) continue;
            var created = p.FullChain.Any(c => c.Plan.Operations.OfType<CreateEntityOp>().Any(o => Ci.Equals(o.Name, entity)));
            list.Add(new(p.Id, p.Name, created, versions, installed.GetValueOrDefault(p.Id)));
        }
        return list.OrderByDescending(p => p.Created).ThenBy(p => p.Name).ToList();
    }

    private static string? EntityOf(Operation o) => o switch
    {
        CreateEntityOp x => x.Name,
        UpdateEntityOp x => x.Entity,
        ArchiveEntityOp x => x.Entity,
        RestoreEntityOp x => x.Entity,
        AddFieldOp x => x.Entity,
        UpdateFieldOp x => x.Entity,
        ArchiveFieldOp x => x.Entity,
        RestoreFieldOp x => x.Entity,
        SetWorkflowOp x => x.Entity,
        SetRulesOp x => x.Entity,
        SetAutomationsOp x => x.Entity,
        SetSecurityOp x => x.Entity,
        SetLayoutOp x => x.Entity,
        SetViewsOp x => x.Entity,
        SetReportsOp x => x.Entity,
        SetWebhooksOp x => x.Entity,
        SetSchedulesOp x => x.Entity,
        SetLedgerOp x => x.Entity,
        SetPrintTemplatesOp x => x.Entity,
        _ => null,
    };
}
