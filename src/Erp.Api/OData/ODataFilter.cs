using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Erp.Api.Metadata;
using Microsoft.Data.SqlClient;

namespace Erp.Api.OData;

// ---------- Árbol de $filter (solo sintaxis; el significado lo da el modelo al traducir) ----------
public abstract record FNode;
public sealed record FAnd(FNode Left, FNode Right) : FNode;
public sealed record FOr(FNode Left, FNode Right) : FNode;
public sealed record FNot(FNode Operand) : FNode;
public sealed record FCompare(string Property, string Op, FLiteral Value) : FNode;
public sealed record FIn(string Property, IReadOnlyList<FLiteral> Items) : FNode;
public sealed record FFunction(string Name, string Property, FLiteral Value) : FNode;
/// <summary>Una propiedad sí/no sola (<c>$filter=Activo</c>) equivale a <c>Activo eq true</c>.</summary>
public sealed record FBoolProperty(string Property) : FNode;

public enum LitKind { String, Number, Boolean, Null, Date, DateTimeOffset }
public sealed record FLiteral(LitKind Kind, object? Value, string Raw);

/// <summary>
/// Spec 018: <c>$filter</c> de OData traducido a SQL. Descenso recursivo sobre una gramática cerrada: las propiedades se
/// resuelven contra el modelo del usuario (lista blanca) y todo literal va como <see cref="SqlParameter"/>.
/// </summary>
public static partial class ODataFilter
{
    public const int MaxLength = 2000;
    public const int MaxComparisons = 50;
    public const int MaxInItems = 50;
    public const int MaxDepth = 32;
    public const int MaxText = 400;

    private static readonly HashSet<string> Comparisons = ["eq", "ne", "gt", "ge", "lt", "le"];
    private static readonly HashSet<string> Functions = ["contains", "startswith", "endswith"];
    private static readonly HashSet<string> Keywords = ["and", "or", "not", "in", "true", "false", "null", .. Comparisons];

    private enum Tok { Ident, String, Number, Date, DateTimeOffset, LParen, RParen, Comma, Slash, End }
    private sealed record Token(Tok Kind, string Text, int At);

    [GeneratedRegex(@"\G\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeToken();
    [GeneratedRegex(@"\G\d{4}-\d{2}-\d{2}(?![\dT])", RegexOptions.CultureInvariant)]
    private static partial Regex DateToken();
    [GeneratedRegex(@"\G-?\d+(\.\d+)?([eE][+-]?\d+)?(?![A-Za-z_\d])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();
    [GeneratedRegex(@"\G[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentToken();

    // ======================= Sintaxis =======================

    public static FNode Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ODataException("$filter está vacío.");
        if (text.Length > MaxLength) throw new ODataException($"$filter admite hasta {MaxLength} caracteres.");
        var p = new Parser(Tokenize(text));
        var node = p.Or(0);
        if (p.Peek.Kind != Tok.End) throw new ODataException($"$filter: no se esperaba «{p.Peek.Text}» en la posición {p.Peek.At + 1}.");
        return node;
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '(': list.Add(new(Tok.LParen, "(", i++)); continue;
                case ')': list.Add(new(Tok.RParen, ")", i++)); continue;
                case ',': list.Add(new(Tok.Comma, ",", i++)); continue;
                case '/': list.Add(new(Tok.Slash, "/", i++)); continue;
                case '\'':
                {
                    var sb = new StringBuilder();
                    var start = i++;
                    while (true)
                    {
                        if (i >= s.Length) throw new ODataException($"$filter: falta cerrar el texto que empieza en la posición {start + 1}.");
                        if (s[i] == '\'')
                        {
                            if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(s[i++]);
                    }
                    list.Add(new(Tok.String, sb.ToString(), start));
                    continue;
                }
            }
            Match m;
            if ((m = DateTimeToken().Match(s, i)).Success) list.Add(new(Tok.DateTimeOffset, m.Value, i));
            else if ((m = DateToken().Match(s, i)).Success) list.Add(new(Tok.Date, m.Value, i));
            else if ((m = NumberToken().Match(s, i)).Success) list.Add(new(Tok.Number, m.Value, i));
            else if ((m = IdentToken().Match(s, i)).Success) list.Add(new(Tok.Ident, m.Value, i));
            else throw new ODataException($"$filter: carácter no válido «{c}» en la posición {i + 1}.");
            i += m.Length;
        }
        list.Add(new(Tok.End, "fin", s.Length));
        return list;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;
        private int _comparisons;
        public Token Peek => tokens[_pos];
        private Token Next() => tokens[_pos++];
        private bool IsWord(string w) => Peek.Kind == Tok.Ident && string.Equals(Peek.Text, w, StringComparison.Ordinal);

        private void Expect(Tok kind, string what)
        {
            if (Peek.Kind != kind) throw new ODataException($"$filter: se esperaba {what} en la posición {Peek.At + 1}.");
            _pos++;
        }

        private void Count()
        {
            if (++_comparisons > MaxComparisons) throw new ODataException($"$filter admite hasta {MaxComparisons} comparaciones.");
        }

        public FNode Or(int depth)
        {
            if (depth > MaxDepth) throw new ODataException("$filter tiene demasiados niveles de paréntesis.");
            var left = And(depth);
            while (IsWord("or")) { _pos++; left = new FOr(left, And(depth)); }
            return left;
        }

        private FNode And(int depth)
        {
            var left = Unary(depth);
            while (IsWord("and")) { _pos++; left = new FAnd(left, Unary(depth)); }
            return left;
        }

        private FNode Unary(int depth)
        {
            if (IsWord("not")) { _pos++; return new FNot(Unary(depth + 1)); }
            return Primary(depth);
        }

        private FNode Primary(int depth)
        {
            if (Peek.Kind == Tok.LParen)
            {
                _pos++;
                var inner = Or(depth + 1);
                Expect(Tok.RParen, "«)»");
                return inner;
            }
            if (Peek.Kind == Tok.Ident && tokens[_pos + 1].Kind == Tok.LParen && !Keywords.Contains(Peek.Text))
            {
                var name = Next().Text;
                if (!Functions.Contains(name)) throw new ODataException($"$filter: la función {name}() no se admite (solo contains, startswith y endswith).");
                _pos++;
                var prop = Property();
                Expect(Tok.Comma, "«,»");
                var lit = Literal();
                Expect(Tok.RParen, "«)»");
                Count();
                return new FFunction(name, prop, lit);
            }

            var first = Operand();
            if (Peek.Kind == Tok.Ident && Comparisons.Contains(Peek.Text))
            {
                var op = Next().Text;
                var second = Operand();
                Count();
                return (first, second) switch
                {
                    (string p, FLiteral l) => new FCompare(p, op, l),
                    (FLiteral l, string p) => new FCompare(p, Flip(op), l),
                    (string, string) => throw new ODataException("$filter: solo se compara una propiedad con un valor, no dos propiedades."),
                    _ => throw new ODataException("$filter: una comparación necesita una propiedad."),
                };
            }
            if (IsWord("in"))
            {
                _pos++;
                if (first is not string prop) throw new ODataException("$filter: «in» va después de una propiedad.");
                Expect(Tok.LParen, "«(»");
                var items = new List<FLiteral> { Literal() };
                while (Peek.Kind == Tok.Comma) { _pos++; items.Add(Literal()); }
                Expect(Tok.RParen, "«)»");
                if (items.Count > MaxInItems) throw new ODataException($"$filter: «in» admite hasta {MaxInItems} valores.");
                Count();
                return new FIn(prop, items);
            }
            if (first is string bare) { Count(); return new FBoolProperty(bare); }
            throw new ODataException($"$filter: se esperaba una condición en la posición {Peek.At + 1}.");
        }

        private static string Flip(string op) => op switch { "gt" => "lt", "lt" => "gt", "ge" => "le", "le" => "ge", _ => op };

        /// <summary>Propiedad (string) o literal (<see cref="FLiteral"/>).</summary>
        private object Operand()
        {
            if (Peek.Kind == Tok.Ident && !(Peek.Text is "true" or "false" or "null")) return Property();
            return Literal();
        }

        private string Property()
        {
            if (Peek.Kind != Tok.Ident || Keywords.Contains(Peek.Text)) throw new ODataException($"$filter: se esperaba una propiedad en la posición {Peek.At + 1}.");
            var name = Next().Text;
            if (Peek.Kind == Tok.Slash) throw new ODataException($"$filter: las rutas a otros catálogos ({name}/…) no se admiten; usa _{name}_value o $expand.");
            if (Peek.Kind == Tok.LParen) throw new ODataException($"$filter: la función {name}() no se admite.");
            return name;
        }

        private FLiteral Literal()
        {
            var t = Next();
            try
            {
                return t.Kind switch
                {
                    Tok.String => new FLiteral(LitKind.String, t.Text, t.Text),
                    Tok.Number => new FLiteral(LitKind.Number, decimal.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture), t.Text),
                    Tok.Date => new FLiteral(LitKind.Date, DateOnly.ParseExact(t.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture), t.Text),
                    Tok.DateTimeOffset => new FLiteral(LitKind.DateTimeOffset, DateTimeOffset.Parse(t.Text, CultureInfo.InvariantCulture), t.Text),
                    Tok.Ident when t.Text is "true" or "false" => new FLiteral(LitKind.Boolean, t.Text == "true", t.Text),
                    Tok.Ident when t.Text == "null" => new FLiteral(LitKind.Null, null, t.Text),
                    _ => throw new ODataException($"$filter: se esperaba un valor en la posición {t.At + 1}."),
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new ODataException($"$filter: «{t.Text}» no es un valor válido.");
            }
        }
    }

    // ======================= Traducción =======================

    /// <summary>
    /// Condición SQL sobre el alias <c>t</c>. <paramref name="resolve"/> da la propiedad visible o null (no existe u oculta:
    /// el mismo mensaje). <c>not</c> se empuja hasta las hojas (De Morgan) con el complemento de cada comparación con nulos,
    /// así la condición queda de dos valores como pide OData y sin <c>CASE</c> que impida usar índices.
    /// </summary>
    public static string ToSql(FNode node, Func<string, ODataProperty?> resolve, SqlParameterCollection ps, EntityDef e, string prefix = "@od")
    {
        var n = 0;
        string Add(object value, EdmKind kind)
        {
            var name = $"{prefix}{n++}";
            ps.Add(kind is EdmKind.Date or EdmKind.DateTimeOffset
                ? new SqlParameter(name, SqlDbType.DateTime2) { Value = value }
                : new SqlParameter(name, value));
            return name;
        }

        ODataProperty Prop(string name) =>
            resolve(name) ?? throw new ODataException($"$filter: la propiedad '{name}' no existe en {e.Name}.");

        string Sql(FNode x, bool neg) => x switch
        {
            FAnd a => $"({Sql(a.Left, neg)} {(neg ? "OR" : "AND")} {Sql(a.Right, neg)})",
            FOr o => $"({Sql(o.Left, neg)} {(neg ? "AND" : "OR")} {Sql(o.Right, neg)})",
            FNot no => Sql(no.Operand, !neg),
            FBoolProperty b => Compare(Prop(b.Property), "eq", new FLiteral(LitKind.Boolean, true, "true"), neg),
            FCompare c => Compare(Prop(c.Property), c.Op, c.Value, neg),
            FIn i => InList(Prop(i.Property), i.Items, neg),
            FFunction f => Like(Prop(f.Property), f, neg),
            _ => throw new ODataException("$filter no válido."),
        };

        string Compare(ODataProperty p, string op, FLiteral lit, bool neg)
        {
            var col = p.Column;
            if (lit.Kind == LitKind.Null)
            {
                if (op is not ("eq" or "ne")) throw new ODataException($"$filter: con null solo se usan eq y ne ({p.Name}).");
                return (op == "eq") != neg ? $"{col} IS NULL" : $"{col} IS NOT NULL";
            }
            if (p.Kind == EdmKind.Boolean && op is not ("eq" or "ne")) throw new ODataException($"$filter: {p.Name} es sí/no; solo se usan eq y ne.");
            var v = Add(Value(p, lit, op is "eq" or "ne"), p.Kind);
            // Complemento de dos valores: lo nulo cumple «distinto» y «no mayor que».
            var (pos, cmp) = (op, neg) switch
            {
                ("eq", false) or ("ne", true) => (true, "="),
                ("eq", true) or ("ne", false) => (false, "<>"),
                ("gt", false) or ("le", true) => (true, ">"),
                ("ge", false) or ("lt", true) => (true, ">="),
                ("lt", false) or ("ge", true) => (true, "<"),
                ("le", false) or ("gt", true) => (true, "<="),
                _ => throw new ODataException("$filter: operador no válido."),
            };
            // «gt» negado = «le o nulo»: el complemento de una comparación que no es «=» incluye los nulos.
            var negated = neg && op is not ("eq" or "ne");
            return !pos || negated ? $"({col} {cmp} {v} OR {col} IS NULL)" : $"{col} {cmp} {v}";
        }

        string InList(ODataProperty p, IReadOnlyList<FLiteral> items, bool neg)
        {
            if (items.Any(i => i.Kind == LitKind.Null)) throw new ODataException($"$filter: «in» no admite null ({p.Name}); usa «eq null».");
            var list = string.Join(", ", items.Select(i => Add(Value(p, i, exact: true), p.Kind)));
            return neg ? $"({p.Column} NOT IN ({list}) OR {p.Column} IS NULL)" : $"{p.Column} IN ({list})";
        }

        string Like(ODataProperty p, FFunction f, bool neg)
        {
            if (p.Kind != EdmKind.String || f.Value.Kind != LitKind.String)
                throw new ODataException($"$filter: {f.Name}() compara una propiedad de texto con un texto ({p.Name}).");
            var text = (string)Value(p, f.Value, exact: false);
            var pattern = f.Name switch { "contains" => $"%{EscapeLike(text)}%", "startswith" => $"{EscapeLike(text)}%", _ => $"%{EscapeLike(text)}" };
            var v = Add(pattern, EdmKind.String);
            return neg ? $"({p.Column} NOT LIKE {v} ESCAPE '\\' OR {p.Column} IS NULL)" : $"{p.Column} LIKE {v} ESCAPE '\\'";
        }

        object Value(ODataProperty p, FLiteral lit, bool exact)
        {
            string Wrong(string expected) => $"$filter: {p.Name} espera {expected}; «{lit.Raw}» no lo es.";
            switch (p.Kind)
            {
                case EdmKind.String:
                    if (lit.Value is not string s) throw new ODataException(Wrong("un texto entre comillas"));
                    if (s.Length > MaxText) throw new ODataException($"$filter: el texto para {p.Name} admite hasta {MaxText} caracteres.");
                    if (exact && p.Field is { Type: FieldTypes.Choice } c && c.Options?.Contains(s) != true)
                        throw new ODataException($"$filter: '{s}' no es una opción de {c.Label}.");
                    if (exact && p.Field is { Type: FieldTypes.State })
                        return e.Workflow?.FindState(s)?.Name ?? throw new ODataException($"$filter: '{s}' no es un estado del proceso.");
                    return s;
                case EdmKind.Int64 or EdmKind.Int32:
                    if (lit.Value is not decimal d || d != decimal.Truncate(d)) throw new ODataException(Wrong("un número entero"));
                    if (p.Kind == EdmKind.Int32 ? d is < int.MinValue or > int.MaxValue : d is < long.MinValue or > long.MaxValue)
                        throw new ODataException(Wrong("un número entero dentro de rango"));
                    return p.Kind == EdmKind.Int32 ? (int)d : (long)d;
                case EdmKind.Decimal:
                    return lit.Value as decimal? ?? throw new ODataException(Wrong("un número"));
                case EdmKind.Boolean:
                    return lit.Value as bool? ?? throw new ODataException(Wrong("true o false"));
                case EdmKind.Date:
                    return lit.Value is DateOnly day ? day.ToDateTime(TimeOnly.MinValue) : throw new ODataException(Wrong("una fecha aaaa-mm-dd"));
                default: // DateTimeOffset: se guarda en UTC; una fecha sola es su medianoche UTC.
                    return lit.Value switch
                    {
                        DateTimeOffset dto => dto.UtcDateTime,
                        DateOnly midnight => midnight.ToDateTime(TimeOnly.MinValue),
                        _ => throw new ODataException(Wrong("una fecha y hora (aaaa-mm-ddThh:mm:ssZ)")),
                    };
            }
        }

        return Sql(node, neg: false);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
