using System.Runtime.CompilerServices;
using Erp.Api.Expressions;
using Erp.Api.Metadata;

namespace Erp.Api.Security;

/// <summary>
/// Spec 029 (H3 CA2, H4 CA1; auditoría 4 D-01 y D-05, R-006-1): una fórmula guardada es tan visible como lo que lee.
/// Al cargar el modelo se calcula, para cada fórmula, de qué campos depende (propios, de sus partidas o de registros
/// relacionados, transitivamente a través de otras fórmulas) y qué catálogos recorre por referencia. <see cref="FieldAccess"/>
/// la oculta a quien no ve uno de esos campos o tiene restringido por registro uno de esos catálogos; como todo pasa por
/// <see cref="FieldAccess"/>, la regla vale en salida, orden, filtros, reportes, exportación, OData, bitácora e impresión.
/// </summary>
public static class DerivedAccess
{
    /// <param name="Reads">Campos (no fórmula) que la fórmula lee, con su catálogo.</param>
    /// <param name="Crosses">Catálogos raíz (el documento, si es una partida) a los que llega por una referencia.</param>
    /// <param name="CrossesCompany">Llega desde un catálogo compartido a uno por empresa: el registro puede ser de otra.</param>
    internal sealed record Info(IReadOnlyList<(EntityDef Entity, FieldDef Field)> Reads, IReadOnlyList<EntityDef> Crosses, bool CrossesCompany);

    // Ligada a la instancia del campo del modelo cargado: un modelo nuevo trae instancias nuevas (sin estado global que limpiar).
    private static readonly ConditionalWeakTable<FieldDef, Info> Table = new();
    private static readonly ConditionalWeakTable<UserContext, Dictionary<string, bool>> Restricted = new();

    /// <summary>Calcula las dependencias de todas las fórmulas del modelo (lo llama <see cref="MetadataStore"/> al cargarlo).</summary>
    public static void Annotate(IReadOnlyDictionary<string, EntityDef> model)
    {
        foreach (var e in model.Values)
        foreach (var f in e.Fields.Where(f => f.Type == FieldTypes.Formula && f.Formula is not null))
            Table.AddOrUpdate(f, Compute(e, f, model));
    }

    internal static Info? For(FieldDef f) => Table.TryGetValue(f, out var i) ? i : null;

    /// <summary>
    /// Spec 031 (revisión adversarial F-A): lo que <c>UNITCOST('libro', …)</c> revela del catálogo de saldo: los campos donde las
    /// acciones <c>cost</c> guardan el valor y la cantidad costeada de sus capas (en Costeo, <c>Existencia.Valor</c> y
    /// <c>CantidadCosteada</c>). Quien no ve uno de ellos tampoco recibe el costo unitario de las capas.
    /// </summary>
    public static IEnumerable<(EntityDef Entity, FieldDef Field)> CostBookFields(string book, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (!model.TryGetValue(book, out var b)) yield break;
        foreach (var a in model.Values.SelectMany(e => e.Automations ?? []).SelectMany(x => x.Actions)
                     .Where(a => a.Type == AutomationActions.Cost && string.Equals(a.Entity, b.Name, StringComparison.OrdinalIgnoreCase)))
            foreach (var name in new[] { a.Field, a.QuantityField })
                if (name is not null && b.FindField(name) is { } f) yield return (b, f);
    }

    /// <summary>¿El usuario deja de ver algún campo que <c>UNITCOST</c> revela de ese libro?</summary>
    public static bool CostBookHidden(string book, IReadOnlyDictionary<string, EntityDef> model, UserContext u) =>
        CostBookFields(book, model).Any(x => !FieldAccess.CanSee(x.Entity, x.Field, u));

    /// <summary>¿La fórmula se oculta a este usuario por lo que lee? (Falso si no es fórmula o el modelo no se anotó.)</summary>
    public static bool Hides(FieldDef f, UserContext u)
    {
        if (f.Type != FieldTypes.Formula || For(f) is not { } info) return false;
        if (info.CrossesCompany) return true;
        if (info.Reads.Any(r => FieldAccess.ByOwnRule(r.Entity, r.Field, u) == FieldAccessLevels.Hidden)) return true;
        return !u.IsSuperuser && info.Crosses.Any(e => RestrictedFor(e, u));
    }

    /// <summary>¿El usuario tiene restringido por registro (006/015) este catálogo? El superusuario se lo salta.</summary>
    private static bool RestrictedFor(EntityDef e, UserContext u)
    {
        var cache = Restricted.GetOrCreateValue(u);
        lock (cache)
        {
            if (cache.TryGetValue(e.Name, out var known)) return known;
            return cache[e.Name] = RecordFilter.HasGrants(e, u);
        }
    }

    private static Info Compute(EntityDef owner, FieldDef formula, IReadOnlyDictionary<string, EntityDef> model)
    {
        var reads = new List<(EntityDef, FieldDef)>();
        var crosses = new List<EntityDef>();
        var company = false;
        var visited = new HashSet<(string, string)>();
        Visit(owner, formula);
        return new Info(reads, crosses.DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), company);

        EntityDef Root(EntityDef e) => e.IsChild && model.TryGetValue(e.ParentEntity ?? "", out var p) ? p : e;

        void Visit(EntityDef e, FieldDef f)
        {
            if (!visited.Add((e.Name.ToLowerInvariant(), f.Name.ToLowerInvariant()))) return;
            if (f.Type != FieldTypes.Formula || f.Formula is null) { reads.Add((e, f)); return; }
            // Una fórmula con su propia regla de lectura también cuenta.
            if (e.Security?.For(f.Name)?.Read is not null) reads.Add((e, f));
            Node tree;
            try { tree = ExprParser.Parse(f.Formula); }
            catch (ExprException) { return; } // el compilador ya la rechazó al guardarla
            Walk(e, tree);
        }

        void Walk(EntityDef e, Node n)
        {
            switch (n)
            {
                case PathRef p: Path(e, p); break;
                case Unary u: Walk(e, u.Operand); break;
                case Binary b: Walk(e, b.Left); Walk(e, b.Right); break;
                case InList i: Walk(e, i.Value); foreach (var x in i.Items) Walk(e, x); break;
                case Call c:
                    // Spec 029 (revisión adversarial F-C): UNITCOST('Existencia', …) lee las capas de ese catálogo de saldo.
                    if (c.Name == "UNITCOST" && c.Args is [Literal { Value: string book }, ..] && model.TryGetValue(book, out var balance))
                    {
                        crosses.Add(Root(balance));
                        foreach (var (be, bf) in CostBookFields(book, model)) Visit(be, bf); // spec 031 (F-A)
                        if (Root(balance).PerCompany && !Root(e).PerCompany) company = true;
                    }
                    foreach (var a in c.Args) Walk(e, a);
                    break;
            }
        }

        void Path(EntityDef e, PathRef p)
        {
            if (e.FindField(p.Head) is not { } hop) return;
            Visit(e, hop);
            var current = e;
            foreach (var segment in p.Rest)
            {
                EntityDef? target = null;
                if (hop.Type == FieldTypes.Lines) model.TryGetValue(hop.ChildEntity ?? "", out target); // sus partidas: el mismo documento
                else if (hop.Type == FieldTypes.Parent) model.TryGetValue(hop.ReferenceEntity ?? "", out target); // su documento
                else if (hop.Type == FieldTypes.Reference && model.TryGetValue(hop.ReferenceEntity ?? "", out target))
                {
                    var to = Root(target);
                    crosses.Add(to);
                    if (to.PerCompany && !Root(current).PerCompany) company = true;
                }
                // Usuario (nombre, usuario): sin seguridad por campo ni por registro.
                if (target is null || target.FindField(segment) is not { } next) return;
                Visit(target, next);
                (current, hop) = (target, next);
            }
        }
    }
}
