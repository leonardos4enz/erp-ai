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
        Call c => c.Args.Any(a => ReadsHidden(a, e, model, user)),
        _ => false,
    };

    private static bool PathHidden(PathRef p, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        var head = e.FindField(p.Head);
        if (head is null) return false;
        if (!FieldAccess.CanSee(e, head, user)) return true;
        if (p.Tail is null) return false;
        var targetName = head.Type == FieldTypes.Lines ? head.ChildEntity : head.ReferenceEntity;
        // Los campos del usuario relacionado (nombre, usuario) no tienen seguridad por campo.
        if (targetName is null || !model.TryGetValue(targetName, out var target)) return false;
        return target.FindField(p.Tail) is { } tf && !FieldAccess.CanSee(target, tf, user);
    }

    private static object? LayoutOutput(LayoutState s) =>
        new { hiddenSections = s.HiddenSections, hiddenFields = s.HiddenFields };
}
