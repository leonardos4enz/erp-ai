using System.Globalization;
using System.Text.RegularExpressions;

namespace Erp.Api.Expressions;

/// <summary>Registro sobre el que se evalúa una expresión (documento o partida).</summary>
public interface IExprScope
{
    object? Field(string name);
    /// <summary>Campo de un registro relacionado por un campo referencia, padre o usuario.</summary>
    object? Related(string refField, string field);
    /// <summary>
    /// Spec 023: ruta de varios saltos (<c>Recepcion.OrdenCompra.Moneda.Clave</c>): <paramref name="path"/> son los segmentos
    /// después de <paramref name="refField"/>. Los ámbitos que solo conocen un nivel no leen más.
    /// </summary>
    object? RelatedPath(string refField, IReadOnlyList<string> path) => path.Count == 1 ? Related(refField, path[0]) : null;
    IReadOnlyList<IExprScope> Lines(string linesField);
}

/// <summary>Spec 010: tipos de cambio registrados en el entorno.</summary>
public interface ICurrencyRates
{
    /// <summary>Unidades de <paramref name="to"/> por una de <paramref name="from"/> vigentes en la fecha; null si no hay.</summary>
    decimal? Rate(string from, string to, DateTime date);
}

/// <summary>Spec 020: capas de costo (para UNITCOST).</summary>
public interface ICostLayers
{
    /// <summary>Costo unitario de las siguientes <paramref name="quantity"/> unidades de la llave en el catálogo de saldo; null sin capas.</summary>
    decimal? UnitCost(string book, decimal quantity, IReadOnlyList<object?> key, string? company);
}

/// <summary>
/// Quién evalúa: necesario para HASROLE, ISCREATOR, CURRENTUSER y CURRENTUNIT. Spec 010: empresa del contexto (clave y
/// moneda base) y tipos de cambio para BASECURRENCY, CURRENTCOMPANY, RATE y CONVERT.
/// </summary>
public sealed record ExprEnv(int UserId, IReadOnlyCollection<string> Roles, int? CreatorId, string? Unit = null)
{
    public static readonly ExprEnv System = new(0, [], null);
    public string? Company { get; init; }
    public string? BaseCurrency { get; init; }
    public ICurrencyRates? Rates { get; init; }
    /// <summary>Spec 020: capas de costo para UNITCOST.</summary>
    public ICostLayers? Costs { get; init; }
}

public static partial class ExprEvaluator
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    public static object? Evaluate(string source, IExprScope scope, ExprEnv env) =>
        Eval(ExprParser.Parse(source), scope, env);

    /// <summary>Evalúa una condición: nulo cuenta como "no aplica" (devuelve <paramref name="whenNull"/>).</summary>
    public static bool Test(string source, IExprScope scope, ExprEnv env, bool whenNull) =>
        Evaluate(source, scope, env) switch
        {
            null => whenNull,
            bool b => b,
            _ => throw new ExprException("La condición debe dar verdadero o falso."),
        };

    private static object? Eval(Node n, IExprScope s, ExprEnv env) => n switch
    {
        Literal l => l.Value,
        PathRef p => Normalize(p.IsOwn ? s.Field(p.Head) : s.RelatedPath(p.Head, p.Rest)),
        Unary { Op: "-" } u => -Num(Eval(u.Operand, s, env)),
        Unary { Op: "NOT" } u => Bool(Eval(u.Operand, s, env)) is { } b ? !b : null,
        Binary b => EvalBinary(b, s, env),
        InList i => EvalIn(i, s, env),
        Call c => EvalCall(c, s, env),
        _ => throw new ExprException("Expresión no soportada."),
    };

    private static object? EvalBinary(Binary b, IExprScope s, ExprEnv env)
    {
        // AND/OR con cortocircuito y lógica de tres valores (como SQL).
        if (b.Op is "AND" or "OR")
        {
            var decisive = b.Op == "OR"; // OR se decide con true; AND con false
            var l = Bool(Eval(b.Left, s, env));
            if (l == decisive) return decisive;
            var r = Bool(Eval(b.Right, s, env));
            if (r == decisive) return decisive;
            return l is null || r is null ? null : !decisive;
        }

        var left = Eval(b.Left, s, env);
        var right = Eval(b.Right, s, env);
        switch (b.Op)
        {
            case "+":
                if (left is DateTime d1) return d1.AddDays((double)Num(right));
                return Num(left) + Num(right);
            case "-":
                if (left is DateTime a && right is DateTime c) return (decimal)(a.Date - c.Date).TotalDays;
                if (left is DateTime d2) return d2.AddDays(-(double)Num(right));
                return Num(left) - Num(right);
            case "*": return Num(left) * Num(right);
            case "/":
                var den = Num(right);
                return den == 0 ? null : Num(left) / den;
            case "&": return Text(left) + Text(right);
            default: return Compare(b.Op, left, right);
        }
    }

    private static object? EvalIn(InList i, IExprScope s, ExprEnv env)
    {
        var v = Eval(i.Value, s, env);
        return i.Items.Any(item => Compare("=", v, Eval(item, s, env)) == true);
    }

    private static bool? Compare(string op, object? l, object? r)
    {
        // Como en SQL, ordenar contra un nulo es "desconocido": una regla sobre un campo opcional vacío
        // no se considera violada y una condición desconocida no habilita una transición.
        if (l is null || r is null)
            return op switch { "=" => l is null && r is null, "<>" => !(l is null && r is null), _ => null };

        int cmp;
        if (l is decimal || r is decimal) cmp = Num(l).CompareTo(Num(r));
        else if (l is DateTime ld && r is DateTime rd) cmp = ld.CompareTo(rd);
        else if (l is bool lb && r is bool rb) cmp = lb.CompareTo(rb);
        else cmp = string.Compare(Text(l), Text(r), StringComparison.OrdinalIgnoreCase);

        return op switch
        {
            "=" => cmp == 0, "<>" => cmp != 0, "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, ">=" => cmp >= 0,
            _ => throw new ExprException($"Operador desconocido {op}."),
        };
    }

    private static object? EvalCall(Call c, IExprScope s, ExprEnv env)
    {
        object? Arg(int i) => Eval(c.Args[i], s, env);
        switch (c.Name)
        {
            case "IF": return Bool(Arg(0)) == true ? Arg(1) : Arg(2);
            case "COALESCE":
                foreach (var a in c.Args) { var v = Eval(a, s, env); if (!IsBlank(v)) return v; }
                return null;
            case "ISBLANK": return IsBlank(Arg(0));
            case "ROUND": return Math.Round(Num(Arg(0)), c.Args.Count > 1 ? (int)Math.Clamp(Num(Arg(1)), 0, 10) : 0, MidpointRounding.AwayFromZero);
            case "ABS": return Math.Abs(Num(Arg(0)));
            case "MIN": return Math.Min(Num(Arg(0)), Num(Arg(1)));
            case "MAX": return Math.Max(Num(Arg(0)), Num(Arg(1)));
            case "LEN": return (decimal)Text(Arg(0)).Length;
            case "UPPER": return Text(Arg(0)).ToUpperInvariant();
            case "ISEMAIL": return Arg(0) is string e && EmailPattern().IsMatch(e);
            case "TODAY": return DateTime.Today;
            case "NOW": return DateTime.UtcNow;
            case "ADDDAYS": return Arg(0) is DateTime d ? d.AddDays((double)Num(Arg(1))) : null;
            case "DAYS":
                return Arg(0) is DateTime from && Arg(1) is DateTime to ? (decimal)(to.Date - from.Date).TotalDays : null;
            case "CURRENTUSER": return (decimal)env.UserId;
            case "CURRENTUNIT": return env.Unit;
            case "HASROLE": return env.Roles.Contains(Text(Arg(0)), StringComparer.OrdinalIgnoreCase);
            case "ISCREATOR": return env.CreatorId is null || env.CreatorId == env.UserId;
            case "BASECURRENCY": return env.BaseCurrency;
            case "CURRENTCOMPANY": return env.Company;
            case "RATE": return Rate(Arg(0), Arg(1), c.Args.Count > 2 ? Arg(2) : DateTime.Today, env);
            case "CONVERT":
            {
                var amount = Arg(0);
                if (amount is null) return null;
                var rate = Rate(Arg(1), Arg(2), c.Args.Count > 3 ? Arg(3) : DateTime.Today, env);
                return rate is null ? null : Num(amount) * rate.Value;
            }
            case "UNITCOST":
            {
                var key = c.Args.Skip(2).Select(a => Eval(a, s, env)).ToList();
                return env.Costs?.UnitCost(Text(Arg(0)), Num(Arg(1)), key, env.Company);
            }
            case "SUM":
            {
                var p = (PathRef)c.Args[0];
                var field = p.Single ?? throw new ExprException("SUM suma un campo de la partida, p. ej. SUM(Partidas.Importe).");
                return s.Lines(p.Head).Sum(line => Num(Normalize(line.Field(field))));
            }
            case "COUNT":
            {
                var p = (PathRef)c.Args[0];
                return (decimal)s.Lines(p.Head).Count;
            }
            default: throw new ExprException($"Función desconocida: {c.Name}.");
        }
    }

    /// <summary>Spec 010: misma moneda = 1; moneda o fecha vacías, o sin tipo registrado = nulo (desconocido).</summary>
    private static decimal? Rate(object? from, object? to, object? date, ExprEnv env)
    {
        var f = Text(from).Trim().ToUpperInvariant();
        var t = Text(to).Trim().ToUpperInvariant();
        if (f.Length == 0 || t.Length == 0) return null;
        if (f == t) return 1m;
        if (date is not DateTime d) return null;
        return env.Rates?.Rate(f, t, d.Date);
    }

    // ---------------- Conversión de valores ----------------

    /// <summary>Unifica los tipos numéricos de .NET a decimal para que la aritmética sea exacta.</summary>
    internal static object? Normalize(object? v) => v switch
    {
        null or DBNull => null,
        int i => (decimal)i,
        long l => (decimal)l,
        short sh => (decimal)sh,
        byte by => (decimal)by,
        double db => (decimal)db,
        float f => (decimal)f,
        _ => v,
    };

    private static decimal Num(object? v) => Normalize(v) switch
    {
        null => 0m,
        decimal d => d,
        bool b => b ? 1 : 0,
        string str when decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        string str => throw new ExprException($"'{str}' no es un número."),
        DateTime => throw new ExprException("No se puede usar una fecha como número."),
        var x => throw new ExprException($"Valor no numérico: {x}."),
    };

    private static bool? Bool(object? v) => v switch
    {
        null => null,
        bool b => b,
        _ => throw new ExprException("Se esperaba verdadero o falso."),
    };

    private static string Text(object? v) => Normalize(v) switch
    {
        null => "",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        var x => x.ToString() ?? "",
    };

    private static bool IsBlank(object? v) => v is null || v is string s && string.IsNullOrWhiteSpace(s);
}
