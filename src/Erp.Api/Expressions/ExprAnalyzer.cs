using Erp.Api.Metadata;

namespace Erp.Api.Expressions;

/// <summary>
/// Validación estática de expresiones contra el modelo: sintaxis, campos, funciones y aridad.
/// Lo usa el compilador de planes para rechazar fórmulas inválidas antes de aplicarlas.
/// </summary>
public static class ExprAnalyzer
{
    private static readonly Dictionary<string, (int Min, int Max)> Functions = new()
    {
        ["IF"] = (3, 3), ["COALESCE"] = (1, 10), ["ISBLANK"] = (1, 1),
        ["ROUND"] = (1, 2), ["ABS"] = (1, 1), ["MIN"] = (2, 2), ["MAX"] = (2, 2),
        ["LEN"] = (1, 1), ["UPPER"] = (1, 1), ["ISEMAIL"] = (1, 1),
        ["TODAY"] = (0, 0), ["NOW"] = (0, 0), ["ADDDAYS"] = (2, 2), ["DAYS"] = (2, 2),
        ["CURRENTUSER"] = (0, 0), ["CURRENTUNIT"] = (0, 0), ["HASROLE"] = (1, 1), ["ISCREATOR"] = (0, 0),
        ["SUM"] = (1, 1), ["COUNT"] = (1, 1),
    };

    /// <summary>Campos del usuario relacionados que se pueden leer con <c>CampoUsuario.X</c>.</summary>
    public static readonly IReadOnlySet<string> UserFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nombre", "Usuario" };

    public sealed record Result(IReadOnlyList<string> Errors, IReadOnlySet<string> OwnFields);

    /// <summary>
    /// Analiza <paramref name="source"/> en el contexto de <paramref name="entity"/>.
    /// <paramref name="ownFields"/> devuelve los campos propios usados (para ordenar fórmulas).
    /// </summary>
    public static Result Analyze(string source, EntityDef entity, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var errors = new List<string>();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Node root;
        try { root = ExprParser.Parse(source); }
        catch (ExprException ex) { return new([ex.Message], own); }

        Walk(root, inAggregate: false);
        return new(errors, own);

        void Walk(Node n, bool inAggregate)
        {
            switch (n)
            {
                case Literal: break;
                case Unary u: Walk(u.Operand, inAggregate); break;
                case Binary b: Walk(b.Left, inAggregate); Walk(b.Right, inAggregate); break;
                case InList i: Walk(i.Value, inAggregate); foreach (var x in i.Items) Walk(x, inAggregate); break;
                case PathRef p: CheckPath(p); break;
                case Call c: CheckCall(c); break;
            }
        }

        void CheckCall(Call c)
        {
            if (!Functions.TryGetValue(c.Name, out var arity)) { errors.Add($"Función desconocida: {c.Name}."); return; }
            if (c.Args.Count < arity.Min || c.Args.Count > arity.Max)
            {
                errors.Add($"{c.Name} recibe {(arity.Min == arity.Max ? arity.Min : $"{arity.Min} a {arity.Max}")} argumento(s).");
                return;
            }
            if (c.Name is "SUM" or "COUNT")
            {
                CheckAggregate(c);
                return;
            }
            if (c.Name == "HASROLE")
            {
                if (c.Args[0] is not Literal { Value: string role }) errors.Add("HASROLE necesita el nombre del rol entre comillas.");
                else if (!roles.Contains(role)) errors.Add($"El rol '{role}' no existe.");
                return;
            }
            foreach (var a in c.Args) Walk(a, false);
        }

        void CheckAggregate(Call c)
        {
            if (c.Args[0] is not PathRef p) { errors.Add($"{c.Name} necesita un campo de partidas, p. ej. {c.Name}(Partidas{(c.Name == "SUM" ? ".Importe" : "")})."); return; }
            var lines = entity.FindField(p.Head);
            if (lines is null || lines.IsArchived || lines.Type != FieldTypes.Lines)
            {
                errors.Add($"'{p.Head}' no es un campo de partidas de {entity.Name}.");
                return;
            }
            own.Add(lines.Name);
            if (c.Name == "COUNT")
            {
                if (p.Tail is not null) errors.Add("COUNT se usa con el campo de partidas, p. ej. COUNT(Partidas).");
                return;
            }
            if (p.Tail is null) { errors.Add("SUM necesita el campo a sumar, p. ej. SUM(Partidas.Importe)."); return; }
            if (!model.TryGetValue(lines.ChildEntity ?? "", out var child)) return; // se reporta en la validación del campo
            var f = child.FindField(p.Tail);
            if (f is null || f.IsArchived) errors.Add($"'{p.Tail}' no existe en {child.Name}.");
            else if (!IsNumeric(f)) errors.Add($"SUM necesita un campo numérico; '{p.Tail}' no lo es.");
        }

        void CheckPath(PathRef p)
        {
            var f = entity.FindField(p.Head);
            if (f is null || f.IsArchived) { errors.Add($"El campo '{p.Head}' no existe en {entity.Name}."); return; }
            if (p.Tail is null)
            {
                if (f.Type == FieldTypes.Lines) errors.Add($"'{p.Head}' son partidas: usa SUM({p.Head}.Campo) o COUNT({p.Head}).");
                own.Add(f.Name);
                return;
            }
            switch (f.Type)
            {
                case FieldTypes.Reference or FieldTypes.Parent:
                    own.Add(f.Name);
                    if (model.TryGetValue(f.ReferenceEntity ?? "", out var target))
                    {
                        var tf = target.FindField(p.Tail);
                        if (tf is null || tf.IsArchived || tf.Type == FieldTypes.Lines)
                            errors.Add($"'{p.Tail}' no existe en {target.Name}.");
                    }
                    break;
                case FieldTypes.User:
                    own.Add(f.Name);
                    if (!UserFields.Contains(p.Tail)) errors.Add($"De un usuario solo se puede leer {string.Join(" o ", UserFields)}.");
                    break;
                case FieldTypes.Lines:
                    errors.Add($"Para usar las partidas '{p.Head}' usa SUM o COUNT.");
                    break;
                default:
                    errors.Add($"'{p.Head}' no es una referencia; no se puede usar '{p}'.");
                    break;
            }
        }
    }

    public static bool IsNumeric(FieldDef f) =>
        f.Type is FieldTypes.Integer or FieldTypes.Decimal ||
        (f.Type == FieldTypes.Formula && f.ResultType is FieldTypes.Integer or FieldTypes.Decimal);
}
