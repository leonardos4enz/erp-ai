using Erp.Api.Expressions;

namespace Erp.Api.Data;

/// <summary>
/// Spec 039 (H5): evaluador de las medidas calculadas de un reporte, sobre el subconjunto que admite
/// <see cref="Metadata.InterfaceRules.CheckCalc"/>. A diferencia de las fórmulas (donde un vacío vale 0, lección 022 F-04), aquí
/// un vacío **se propaga** como en SQL: dividir entre cero, o un promedio sin datos, deja vacía la medida (un margen sin ventas
/// no es 0 %). <c>COALESCE(x, 0)</c> lo trata como cero. No lee registros, usuario ni empresa: solo los valores de las medidas.
/// </summary>
public static class ReportCalc
{
    public static decimal? Evaluate(string expr, IReadOnlyDictionary<string, object?> measures, int scale)
    {
        try
        {
            return Eval(ExprParser.Parse(expr), measures) is decimal d ? decimal.Round(d, scale, MidpointRounding.AwayFromZero) : null;
        }
        catch (ExprException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>El valor sin redondear (decimal, booleano o nulo); la validación lo usa para rechazar lo que no da un número.</summary>
    public static object? Eval(Node n, IReadOnlyDictionary<string, object?> v) => n switch
    {
        Literal l => l.Value,
        PathRef { IsOwn: true } p => v.TryGetValue(p.Head, out var x) && x is decimal d ? d : null,
        Unary { Op: "-" } u => Eval(u.Operand, v) is decimal d ? -d : null,
        Unary { Op: "NOT" } u => Eval(u.Operand, v) is bool b ? !b : null,
        Binary b => Binary(b, v),
        InList i => Eval(i.Value, v) is decimal x ? i.Items.Any(item => Eval(item, v) is decimal y && x == y) : null,
        Call c => Call(c, v),
        _ => throw new ExprException("Expresión no soportada en una medida calculada."),
    };

    private static object? Binary(Binary b, IReadOnlyDictionary<string, object?> v)
    {
        if (b.Op is "AND" or "OR")
        {
            // Lógica de tres valores, como SQL.
            var decisive = b.Op == "OR";
            var l = Eval(b.Left, v) as bool?;
            if (l == decisive) return decisive;
            var r = Eval(b.Right, v) as bool?;
            if (r == decisive) return decisive;
            return l is null || r is null ? null : !decisive;
        }
        if (Eval(b.Left, v) is not decimal x || Eval(b.Right, v) is not decimal y) return null;
        return b.Op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? null : x / y,
            "=" => x == y, "<>" => x != y, "<" => x < y, "<=" => x <= y, ">" => x > y, ">=" => x >= y,
            _ => throw new ExprException($"El operador {b.Op} no se usa en medidas calculadas."),
        };
    }

    private static object? Call(Call c, IReadOnlyDictionary<string, object?> v)
    {
        object? Arg(int i) => Eval(c.Args[i], v);
        switch (c.Name)
        {
            case "IF": return Arg(0) is true ? Arg(1) : Arg(2);
            case "COALESCE":
                foreach (var a in c.Args) if (Eval(a, v) is { } x) return x;
                return null;
            case "ISBLANK": return Arg(0) is null;
            case "ROUND":
                if (Arg(0) is not decimal d) return null;
                var places = c.Args.Count > 1 ? Arg(1) is decimal p ? (int)Math.Clamp(p, 0, 10) : 0 : 0;
                return Math.Round(d, places, MidpointRounding.AwayFromZero);
            case "ABS": return Arg(0) is decimal a0 ? Math.Abs(a0) : null;
            case "MIN": return Arg(0) is decimal m0 && Arg(1) is decimal m1 ? Math.Min(m0, m1) : null;
            case "MAX": return Arg(0) is decimal n0 && Arg(1) is decimal n1 ? Math.Max(n0, n1) : null;
            default: throw new ExprException($"La función {c.Name} no se usa en medidas calculadas.");
        }
    }
}
