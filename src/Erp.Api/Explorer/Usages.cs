using System.Runtime.CompilerServices;
using Erp.Api.Expressions;
using Erp.Api.Metadata;

namespace Erp.Api.Explorer;

public sealed record UsageTarget(string Entity, string? Field);

/// <param name="Entity">Catálogo donde está la mención (el que se abre en el modelador).</param>
/// <param name="Tab">Pestaña del editor del catálogo: <c>catalogo</c>, <c>formulario</c>, <c>proceso</c> o <c>impresion</c> (027).</param>
public sealed record Usage(string Entity, string Kind, string Location, string? Expression, UsageTarget Target, string Tab);

public static class UsageKinds
{
    public const string Formula = "formula";
    public const string Default = "default";
    public const string Rule = "rule";
    public const string Transition = "transition";
    public const string Layout = "layout";
    public const string Automation = "automation";
    public const string Schedule = "schedule";
    public const string Webhook = "webhook";
    public const string Access = "access";
    public const string View = "view";
    public const string Report = "report";
    public const string Dashboard = "dashboard";
    public const string Lookup = "lookup";
    public const string Ledger = "ledger";
    public const string Display = "display";
    public const string Reference = "reference";
    /// <summary>Spec 027: plantilla de impresión.</summary>
    public const string Print = "print";
}

/// <summary>
/// Spec 025 (H4): todo lo que menciona cada catálogo y cada campo. Recorre cada sitio del modelo que tiene una expresión o
/// un nombre de campo, en el mismo ámbito que usa el compilador (el registro, la partida con <c>lines</c>, el registro creado
/// en <c>capture</c>, el catálogo de periodos del libro), y analiza las expresiones con <see cref="ExprParser"/>: una ruta
/// <c>A.B.C</c> menciona un campo por salto y <c>SUM(P.X)</c> la partida y su campo. Genérico, sin código por módulo.
/// </summary>
public sealed class UsageIndex
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, EntityDef>, UsageIndex> Cache = new();

    private readonly IReadOnlyDictionary<string, EntityDef> _model;
    private readonly List<Usage> _all = [];
    private readonly HashSet<(string, string, string, string?, string, string?)> _seen = [];
    private readonly IReadOnlyDictionary<string, DashboardDef> _dashboards;

    public IReadOnlyList<Usage> All => _all;

    private UsageIndex(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> dashboards)
    {
        _model = model;
        _dashboards = dashboards;
        foreach (var e in model.Values.Where(e => !e.IsArchived).OrderBy(e => e.Name, Ci)) Collect(e);
        foreach (var d in dashboards.Values.Where(d => !d.IsArchived).OrderBy(d => d.Name, Ci))
            foreach (var w in d.Widgets)
                if (model.TryGetValue(w.Entity, out var we) && !we.IsArchived)
                    Add(we.Name, UsageKinds.Dashboard, $"Tablero «{d.Label}» · tarjeta «{w.Label ?? w.Report}» (reporte {w.Report})", null,
                        new(we.Name, null), "catalogo");
    }

    /// <summary>
    /// Índice del modelo, en caché mientras el modelo sea la misma instancia (<see cref="MetadataStore"/> entrega una nueva
    /// al cambiar o caducar) y con los mismos tableros.
    /// </summary>
    public static UsageIndex For(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> dashboards)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(model, out var cached) && ReferenceEquals(cached._dashboards, dashboards)) return cached;
            var index = new UsageIndex(model, dashboards);
            Cache.AddOrUpdate(model, index);
            return index;
        }
    }

    public IReadOnlyList<Usage> Of(string entity, string? field = null) =>
        _all.Where(u => Ci.Equals(u.Target.Entity, entity) && (field is null || Ci.Equals(u.Target.Field, field))).ToList();

    private void Add(string owner, string kind, string location, string? expression, UsageTarget target, string tab)
    {
        if (_seen.Add((owner, kind, location, expression, target.Entity.ToLowerInvariant(), target.Field?.ToLowerInvariant())))
            _all.Add(new(owner, kind, location, expression, target, tab));
    }

    private sealed record Site(EntityDef Owner, string Kind, string Location, string Tab);

    /// <summary>Nombre de campo suelto, resuelto en <paramref name="scope"/>.</summary>
    private void Name(Site s, EntityDef? scope, string? field)
    {
        if (scope is null || string.IsNullOrWhiteSpace(field)) return;
        // Un nombre con punto (p. ej. el origen de un lookupFilter) es una ruta.
        if (field.Contains('.')) { Expr(s, scope, field); return; }
        if (scope.FindField(field) is { } f) Add(s.Owner.Name, s.Kind, s.Location, null, new(scope.Name, f.Name), s.Tab);
    }

    private void Entity(Site s, string? entity)
    {
        if (entity is not null && _model.TryGetValue(entity, out var t))
            Add(s.Owner.Name, s.Kind, s.Location, null, new(t.Name, null), s.Tab);
    }

    private void Expr(Site s, EntityDef? scope, string? source)
    {
        if (scope is null || string.IsNullOrWhiteSpace(source)) return;
        Node root;
        try { root = ExprParser.Parse(source); }
        catch (ExprException) { return; } // el compilador no deja entrar expresiones inválidas; aquí no se reportan
        var targets = new List<UsageTarget>();
        Walk(root, scope, targets);
        foreach (var t in targets) Add(s.Owner.Name, s.Kind, s.Location, source, t, s.Tab);
    }

    private void Walk(Node n, EntityDef scope, List<UsageTarget> found)
    {
        switch (n)
        {
            case Unary u: Walk(u.Operand, scope, found); break;
            case Binary b: Walk(b.Left, scope, found); Walk(b.Right, scope, found); break;
            case InList i: Walk(i.Value, scope, found); foreach (var x in i.Items) Walk(x, scope, found); break;
            case PathRef p: Path(p, scope, found); break;
            case Call { Name: "SUM" or "COUNT", Args: [PathRef p] }:
                if (scope.FindField(p.Head) is not { } lines) break;
                found.Add(new(scope.Name, lines.Name));
                if (p.Single is { } name && _model.TryGetValue(lines.ChildEntity ?? "", out var child) && child.FindField(name) is { } cf)
                    found.Add(new(child.Name, cf.Name));
                break;
            case Call c:
                if (c is { Name: "UNITCOST", Args: [Literal { Value: string book }, ..] } && _model.TryGetValue(book, out var be))
                    found.Add(new(be.Name, null));
                foreach (var a in c.Args) Walk(a, scope, found);
                break;
        }
    }

    /// <summary>Spec 023: un campo por salto de la ruta (hasta tres saltos), siguiendo referencias y padres.</summary>
    private void Path(PathRef p, EntityDef scope, List<UsageTarget> found)
    {
        if (scope.FindField(p.Head) is not { } hop) return;
        found.Add(new(scope.Name, hop.Name));
        foreach (var seg in p.Rest)
        {
            if (hop.Type is not (FieldTypes.Reference or FieldTypes.Parent) || !_model.TryGetValue(hop.ReferenceEntity ?? "", out var target)) return;
            if (target.FindField(seg) is not { } next) return;
            found.Add(new(target.Name, next.Name));
            hop = next;
        }
    }

    private void Collect(EntityDef e)
    {
        Site S(string kind, string location, string tab = "catalogo") => new(e, kind, location, tab);

        Name(S(UsageKinds.Display, "Campo que representa al registro"), e, e.DisplayField);

        foreach (var f in e.ActiveFields)
        {
            Expr(S(UsageKinds.Formula, $"Fórmula de {f.Label}"), e, f.Formula);
            Expr(S(UsageKinds.Default, $"Valor por defecto de {f.Label}"), e, f.DefaultValue);
            if (f.Type is FieldTypes.Reference or FieldTypes.Parent)
                Entity(S(UsageKinds.Reference, f.Type == FieldTypes.Parent ? $"Documento de la partida ({f.Label})" : $"Referencia {f.Label}"), f.ReferenceEntity);
            if (f.Type == FieldTypes.Lines)
                Entity(S(UsageKinds.Reference, $"Partidas {f.Label}"), f.ChildEntity);
            if (f.LookupFilter is { } lf && _model.TryGetValue(f.ReferenceEntity ?? "", out var lookupTarget))
            {
                var site = S(UsageKinds.Lookup, $"Filtro del buscador de {f.Label}");
                Name(site, lookupTarget, lf.Field);
                Name(site, e, lf.Source);
            }
        }

        foreach (var r in e.Rules ?? [])
        {
            var what = r.IsWarning ? "Aviso" : "Regla";
            Name(S(UsageKinds.Rule, $"{what} «{r.Name}» (campo señalado)"), e, r.Field);
            Expr(S(UsageKinds.Rule, $"{what} «{r.Name}» (condición)"), e, r.When);
            Expr(S(UsageKinds.Rule, $"{what} «{r.Name}»"), e, r.Assert);
        }

        foreach (var t in e.Workflow?.Transitions ?? [])
        {
            var at = $"Transición «{t.Label}»";
            Expr(S(UsageKinds.Transition, $"{at} (condición)", "proceso"), e, t.When);
            foreach (var rf in t.RequiredFields ?? []) Name(S(UsageKinds.Transition, $"{at} (campo obligatorio)", "proceso"), e, rf);
            foreach (var a in t.Set ?? [])
            {
                var site = S(UsageKinds.Transition, $"{at} › asigna {a.Field}", "proceso");
                Name(site, e, a.Field);
                Expr(site, e, a.Value);
            }
        }

        foreach (var sec in e.Layout?.Sections ?? [])
        {
            var at = $"Sección «{sec.Label}» del formulario";
            Expr(S(UsageKinds.Layout, $"{at} (visible cuando)", "formulario"), e, sec.VisibleWhen);
            foreach (var item in sec.Fields)
            {
                Name(S(UsageKinds.Layout, at, "formulario"), e, item.Field);
                Expr(S(UsageKinds.Layout, $"{at} › {item.Field} (visible cuando)", "formulario"), e, item.VisibleWhen);
            }
        }

        foreach (var auto in e.Automations ?? [])
        {
            var at = $"Automatización «{auto.Label}»";
            Expr(S(UsageKinds.Automation, $"{at} (condición)"), e, auto.When);
            Actions(e, auto.Actions, UsageKinds.Automation, at);
        }

        foreach (var sch in e.Schedules ?? [])
        {
            var at = $"Tarea «{sch.Label}»";
            Expr(S(UsageKinds.Schedule, $"{at} (consulta)"), e, sch.Where);
            Actions(e, sch.Actions, UsageKinds.Schedule, at);
        }

        foreach (var h in e.Webhooks ?? [])
        {
            var at = $"Webhook «{h.Label}»";
            Expr(S(UsageKinds.Webhook, $"{at} (condición)"), e, h.When);
            foreach (var f in h.Fields ?? []) Name(S(UsageKinds.Webhook, $"{at} (campos enviados)"), e, f);
        }

        if (e.Security is { } sp)
        {
            Name(S(UsageKinds.Access, "Seguridad: campo dueño"), e, sp.Owner);
            foreach (var fs in sp.Fields ?? []) Name(S(UsageKinds.Access, "Seguridad por campo"), e, fs.Field);
            foreach (var (g, i) in (sp.Records ?? []).Select((g, i) => (g, i + 1)))
                Expr(S(UsageKinds.Access, $"Regla de acceso {i}{(g.Roles is { Count: > 0 } r ? $" ({string.Join(", ", r)})" : "")}"), e, g.Where);
        }

        foreach (var v in e.Views ?? [])
        {
            var site = S(UsageKinds.View, $"Vista «{v.Label}»");
            foreach (var c in v.Columns ?? []) Name(site, e, c);
            foreach (var f in v.Filters ?? []) Name(site, e, f.Field);
            Name(site, e, v.Sort);
        }

        foreach (var r in e.Reports ?? [])
        {
            var at = $"Reporte «{r.Label}»";
            foreach (var g in r.GroupBy ?? []) Name(S(UsageKinds.Report, $"{at} (agrupa)"), e, g.Field);
            foreach (var m in r.Measures) Name(S(UsageKinds.Report, $"{at} (medida)"), e, m.Field);
            Expr(S(UsageKinds.Report, $"{at} (filtro)"), e, r.Filter);
        }

        // Spec 027: plantillas de impresión (las columnas de una tabla, en el ámbito de la partida).
        foreach (var t in e.PrintTemplates ?? [])
        {
            var at = $"Plantilla «{t.Label}»";
            Expr(S(UsageKinds.Print, $"{at} (cuándo aplica)", "impresion"), e, t.When);
            for (var i = 0; i < t.Blocks.Count; i++)
            {
                var b = t.Blocks[i];
                var site = S(UsageKinds.Print, $"{at} › bloque {i + 1} ({b.Type})", "impresion");
                Expr(site, e, b.VisibleWhen);
                foreach (var text in new[] { b.Title, b.Subtitle, b.Text }.Concat((b.Signatures ?? []).Select(s => s.Name)))
                    foreach (var (_, expr) in TemplateText.Parse(text).Parts.Where(p => p.IsExpr)) Expr(site, e, expr);
                foreach (var v in b.Items ?? [])
                {
                    Expr(site, e, v.Value);
                    Expr(site, e, v.Currency);
                }
                if (b.Type == PrintBlockTypes.Table)
                {
                    Name(site, e, b.Lines);
                    var child = e.FindField(b.Lines ?? "") is { ChildEntity: { } cn } && _model.TryGetValue(cn, out var ce) ? ce : null;
                    foreach (var c in b.Columns ?? [])
                    {
                        Expr(site, child, c.Value);
                        Expr(site, child, c.Currency);
                    }
                }
            }
        }

        if (e.Ledger is { } l)
        {
            var site = S(UsageKinds.Ledger, "Libro contable");
            Name(site, e, l.Lines);
            Name(site, e, l.Date);
            if (e.FindField(l.Lines) is { ChildEntity: { } ce } && _model.TryGetValue(ce, out var lineEntity))
            {
                Name(site, lineEntity, l.Debit);
                Name(site, lineEntity, l.Credit);
            }
            if (l.Period is { } p && _model.TryGetValue(p.Entity, out var pe))
            {
                var ps = S(UsageKinds.Ledger, "Libro contable (periodos)");
                Entity(ps, pe.Name);
                Name(ps, pe, p.Start);
                Name(ps, pe, p.End);
                Expr(S(UsageKinds.Ledger, "Libro contable (periodo abierto)"), pe, p.Open);
            }
        }
    }

    /// <summary>Acciones de una automatización o tarea: el ámbito y el destino son los del compilador (<see cref="AutomationRules"/>).</summary>
    private void Actions(EntityDef e, IReadOnlyList<AutomationAction> actions, string kind, string prefix)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var site = new Site(e, kind, $"{prefix} › acción {i + 1} ({ActionLabel(a.Type)})", "catalogo");
            var scope = AutomationRules.Scope(e, a, _model);
            if (scope is null) continue;
            Name(site, e, a.Lines);
            Expr(site, scope, a.When);
            Name(site, scope, a.Target);
            var target = AutomationRules.Target(scope, a, _model);
            if (target is not null) Entity(site, target.Name);
            foreach (var s in a.Set ?? [])
            {
                Name(site, target ?? scope, s.Field);
                Expr(site, scope, s.Value);
            }
            foreach (var m in a.Match ?? [])
            {
                Name(site, target, m.Field);
                Name(site, scope, m.Source);
            }
            Name(site, target, a.Field);
            Name(site, scope, a.Sum);
            Expr(site, scope, a.Filter);
            foreach (var lt in a.CreateLines ?? [])
            {
                Name(site, target, lt.Field);
                var child = target?.FindField(lt.Field) is { ChildEntity: { } c } && _model.TryGetValue(c, out var ce) ? ce : null;
                Expr(site, scope, lt.When);
                foreach (var s in lt.Set)
                {
                    Name(site, child, s.Field);
                    Expr(site, scope, s.Value);
                }
            }
            foreach (var x in new[] { a.Method, a.UnitCost, a.FallbackCost, a.TotalCost }) Expr(site, scope, x);
            foreach (var x in new[] { a.Quantity, a.CostField, a.UnitCostField }) Name(site, scope, x);
            Name(site, target, a.QuantityField);
            foreach (var s in a.Capture ?? [])
            {
                Name(site, scope, s.Field);
                Expr(site, target, s.Value);
            }
        }
    }

    public static string ActionLabel(string? type) => type switch
    {
        AutomationActions.Update => "actualiza",
        AutomationActions.Transition => "transiciona",
        AutomationActions.Create => "crea",
        AutomationActions.Rollup => "acumula",
        AutomationActions.Cost => "valúa",
        _ => type ?? "",
    };
}
