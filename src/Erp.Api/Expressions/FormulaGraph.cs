using Erp.Api.Metadata;

namespace Erp.Api.Expressions;

public static class FormulaGraph
{
    /// <summary>
    /// Fórmulas de la entidad en orden de evaluación (dependencias primero).
    /// Lanza <see cref="ExprException"/> si hay un ciclo.
    /// </summary>
    public static IReadOnlyList<FieldDef> Order(EntityDef e)
    {
        var formulas = e.ActiveFields.Where(f => f.Type == FieldTypes.Formula && f.Formula is not null)
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FieldDef>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 visitando, 2 listo

        foreach (var name in formulas.Keys) Visit(name, []);
        return ordered;

        void Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1) throw new ExprException($"Fórmulas en ciclo: {string.Join(" → ", path.Append(name))}.");
                return;
            }
            state[name] = 1;
            path.Add(name);
            foreach (var dep in Dependencies(formulas[name].Formula!).Where(formulas.ContainsKey))
                Visit(dep, path);
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            ordered.Add(formulas[name]);
        }
    }

    /// <summary>Campos propios (primer segmento de cada ruta) usados por una expresión.</summary>
    public static IEnumerable<string> Dependencies(string source)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(ExprParser.Parse(source));
        return found;

        void Walk(Node n)
        {
            switch (n)
            {
                case PathRef p: found.Add(p.Head); break;
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case InList i: Walk(i.Value); foreach (var x in i.Items) Walk(x); break;
                case Call c: foreach (var a in c.Args) Walk(a); break;
            }
        }
    }
}
