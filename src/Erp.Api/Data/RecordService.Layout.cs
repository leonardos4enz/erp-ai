using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Data;

/// <summary>Spec 007: secciones y campos que el layout oculta por condición para este borrador y este usuario.</summary>
public sealed record LayoutState(IReadOnlyList<string> HiddenSections, IReadOnlyList<string> HiddenFields)
{
    public static readonly LayoutState None = new([], []);
}

public sealed partial class RecordService
{
    public static bool HasConditions(EntityDef e) =>
        e.Layout?.Sections.Any(s => s.VisibleWhen is not null || s.Fields.Any(f => f.VisibleWhen is not null)) == true;

    /// <summary>
    /// Evalúa las condiciones <c>visibleWhen</c> con el mismo intérprete que valida al guardar. Desconocido = oculto.
    /// Una condición que lee un campo que el usuario no ve (spec 006) se ignora: su resultado revelaría el dato.
    /// </summary>
    internal static LayoutState EvaluateLayout(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, IExprScope scope, ExprEnv env, UserContext user)
    {
        if (!HasConditions(e)) return LayoutState.None;
        var sections = new List<string>();
        var fields = new List<string>();
        bool Visible(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return true;
            try
            {
                var node = ExprParser.Parse(expr);
                return ReadsHidden(node, e, model, user) || ExprEvaluator.Test(expr, scope, env, whenNull: false);
            }
            catch (ExprException) { return true; } // la presentación nunca bloquea: el servidor valida al guardar
        }
        foreach (var s in e.Layout!.Sections)
        {
            if (!Visible(s.VisibleWhen))
            {
                sections.Add(s.Name);
                fields.AddRange(s.Fields.Select(f => f.Field));
                continue;
            }
            fields.AddRange(s.Fields.Where(f => !Visible(f.VisibleWhen)).Select(f => f.Field));
        }
        return new LayoutState(sections, fields);
    }

    /// <summary>¿La expresión usa un campo (propio o relacionado) oculto para el usuario?</summary>
    internal static bool ReadsHidden(Node n, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user) => n switch
    {
        PathRef p => PathHidden(p, e, model, user),
        Unary u => ReadsHidden(u.Operand, e, model, user),
        Binary b => ReadsHidden(b.Left, e, model, user) || ReadsHidden(b.Right, e, model, user),
        InList i => ReadsHidden(i.Value, e, model, user) || i.Items.Any(x => ReadsHidden(x, e, model, user)),
        // Spec 031 (revisión adversarial F-A): UNITCOST lee el valor y la cantidad costeada del catálogo de saldo.
        Call c => c is { Name: "UNITCOST", Args: [Literal { Value: string book }, ..] } && DerivedAccess.CostBookHidden(book, model, user)
                  || c.Args.Any(a => ReadsHidden(a, e, model, user)),
        _ => false,
    };

    private static bool PathHidden(PathRef p, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        var hop = e.FindField(p.Head);
        if (hop is null) return false;
        if (!FieldAccess.CanSee(e, hop, user)) return true;
        // Spec 023: la seguridad por campo se revisa en cada salto de la ruta (campo de referencia y campo final).
        foreach (var segment in p.Rest)
        {
            var targetName = hop.Type == FieldTypes.Lines ? hop.ChildEntity : hop.ReferenceEntity;
            // Los campos del usuario relacionado (nombre, usuario) no tienen seguridad por campo.
            if (targetName is null || !model.TryGetValue(targetName, out var target)) return false;
            hop = target.FindField(segment);
            if (hop is null) return false;
            if (!FieldAccess.CanSee(target, hop, user)) return true;
        }
        return false;
    }

    private static object? LayoutOutput(LayoutState s) =>
        new { hiddenSections = s.HiddenSections, hiddenFields = s.HiddenFields };
}
