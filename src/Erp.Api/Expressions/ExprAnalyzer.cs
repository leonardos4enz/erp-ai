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
        // Spec 010: empresa y monedas.
        ["BASECURRENCY"] = (0, 0), ["CURRENTCOMPANY"] = (0, 0), ["RATE"] = (2, 3), ["CONVERT"] = (3, 4),
        // Spec 020: costo unitario de las siguientes N unidades de las capas de una llave.
        ["UNITCOST"] = (3, 7),
    };

    public sealed record FunctionInfo(string Name, int Min, int Max);

    /// <summary>Spec 026 (H3): las funciones y su aridad, para el autocompletado del editor de expresiones.</summary>
    public static readonly IReadOnlyList<FunctionInfo> FunctionList =
        Functions.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FunctionInfo(f.Key, f.Value.Min, f.Value.Max)).ToList();

    /// <summary>Spec 010: funciones cuyo valor depende de la empresa del contexto.</summary>
    public static readonly IReadOnlySet<string> CompanyFunctions = new HashSet<string> { "BASECURRENCY", "CURRENTCOMPANY" };

    /// <summary>Nombres de las funciones que usa la expresión (vacío si no se puede analizar).</summary>
    public static IReadOnlySet<string> UsedFunctions(string source)
    {
        var used = new HashSet<string>();
        try { Collect(ExprParser.Parse(source)); }
        catch (ExprException) { return used; }
        return used;

        void Collect(Node n)
        {
            switch (n)
            {
                case Unary u: Collect(u.Operand); break;
                case Binary b: Collect(b.Left); Collect(b.Right); break;
                case InList i: Collect(i.Value); foreach (var x in i.Items) Collect(x); break;
                case Call c: used.Add(c.Name); foreach (var a in c.Args) Collect(a); break;
            }
        }
    }

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
            if (c.Name == "UNITCOST" && (c.Args[0] is not Literal { Value: string book } || !model.TryGetValue(book, out var be) || be.IsArchived))
                errors.Add("UNITCOST necesita entre comillas el catálogo que guarda el saldo valuado, p. ej. UNITCOST('Existencia', Cantidad, Almacen, Producto).");
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
                if (!p.IsOwn) errors.Add("COUNT se usa con el campo de partidas, p. ej. COUNT(Partidas).");
                return;
            }
            if (p.IsOwn) { errors.Add("SUM necesita el campo a sumar, p. ej. SUM(Partidas.Importe)."); return; }
            if (p.Single is not { } name) { errors.Add($"SUM suma un campo de la partida (un nivel), p. ej. SUM({p.Head}.Importe); '{p}' no lo es."); return; }
            if (!model.TryGetValue(lines.ChildEntity ?? "", out var child)) return; // se reporta en la validación del campo
            var f = child.FindField(name);
            if (f is null || f.IsArchived) errors.Add($"'{name}' no existe en {child.Name}.");
            else if (!IsNumeric(f)) errors.Add($"SUM necesita un campo numérico; '{name}' no lo es.");
        }

        void CheckPath(PathRef p)
        {
            var f = entity.FindField(p.Head);
            if (f is null || f.IsArchived) { errors.Add($"El campo '{p.Head}' no existe en {entity.Name}."); return; }
            if (p.IsOwn)
            {
                if (f.Type == FieldTypes.Lines) errors.Add($"'{p.Head}' son partidas: usa SUM({p.Head}.Campo) o COUNT({p.Head}).");
                own.Add(f.Name);
                return;
            }
            if (f.Type == FieldTypes.Lines) { errors.Add($"Para usar las partidas '{p.Head}' usa SUM o COUNT."); return; }
            if (f.Type is not (FieldTypes.Reference or FieldTypes.Parent or FieldTypes.User))
            {
                errors.Add($"'{p.Head}' no es una referencia; no se puede usar '{p}'.");
                return;
            }
            own.Add(f.Name);
            if (PathError(f, p.Rest, model, p.ToString()) is { } error) errors.Add(error);
        }
    }

    /// <summary>
    /// Spec 023: valida los saltos de una ruta desde el campo <paramref name="hop"/> (referencia, padre o usuario) contra los
    /// metadatos. Intermedios: referencia o padre (o usuario como penúltimo); final: un campo existente que no sean partidas.
    /// </summary>
    public static string? PathError(FieldDef hop, IReadOnlyList<string> rest, IReadOnlyDictionary<string, EntityDef> model, string path)
    {
        // Un nivel conserva los mensajes de siempre; con más, se agrega la ruta para ubicar el salto.
        var where = rest.Count > 1 ? $" ('{path}')" : "";
        for (var i = 0; i < rest.Count; i++)
        {
            var last = i == rest.Count - 1;
            if (hop.Type == FieldTypes.User)
                return last && UserFields.Contains(rest[i]) ? null
                    : $"De un usuario solo se puede leer {string.Join(" o ", UserFields)}{where}.";
            if (!model.TryGetValue(hop.ReferenceEntity ?? "", out var target)) return null; // se reporta en la validación del campo
            var next = target.FindField(rest[i]);
            if (next is null || next.IsArchived || next.Type == FieldTypes.Lines)
                return $"'{rest[i]}' no existe en {target.Name}{where}.";
            if (last) return null;
            if (next.Type is not (FieldTypes.Reference or FieldTypes.Parent or FieldTypes.User))
                return $"'{rest[i]}' de {target.Name} no es una referencia; no se puede seguir la ruta '{path}'.";
            hop = next;
        }
        return null;
    }

    public static bool IsNumeric(FieldDef f) =>
        f.Type is FieldTypes.Integer or FieldTypes.Decimal ||
        (f.Type == FieldTypes.Formula && f.ResultType is FieldTypes.Integer or FieldTypes.Decimal);
}
