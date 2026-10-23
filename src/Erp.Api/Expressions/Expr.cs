using System.Globalization;
using System.Text;

namespace Erp.Api.Expressions;

/// <summary>Error de sintaxis o de evaluación; su mensaje es apto para mostrarse al modelador.</summary>
public sealed class ExprException(string message) : Exception(message);

// ---------------- AST ----------------

public abstract record Node;
public sealed record Literal(object? Value) : Node;
public sealed record PathRef(string Head, string? Tail) : Node
{
    public override string ToString() => Tail is null ? Head : $"{Head}.{Tail}";
}
public sealed record Unary(string Op, Node Operand) : Node;
public sealed record Binary(string Op, Node Left, Node Right) : Node;
public sealed record InList(Node Value, IReadOnlyList<Node> Items) : Node;
public sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;

/// <summary>
/// Parser de descenso recursivo para el lenguaje de expresiones del motor (ver plan 002).
/// No hay forma de invocar código .NET, reflexión ni SQL: solo nodos de esta gramática.
/// </summary>
public static class ExprParser
{
    public const int MaxLength = 1000;
    public const int MaxDepth = 50;

    private static readonly Dictionary<string, Node> Cache = new();
    private static readonly object CacheLock = new();

    public static Node Parse(string source)
    {
        lock (CacheLock)
            if (Cache.TryGetValue(source, out var cached)) return cached;

        if (source.Length > MaxLength) throw new ExprException($"La expresión excede {MaxLength} caracteres.");
        var p = new Parser(Tokenize(source));
        var node = p.ParseOr(0);
        if (!p.AtEnd) throw new ExprException($"Sobra texto cerca de '{p.Current.Text}'.");

        lock (CacheLock)
        {
            if (Cache.Count > 5000) Cache.Clear();
            Cache[source] = node;
        }
        return node;
    }

    private enum T { Number, String, Ident, Op, LParen, RParen, Comma, Dot, End }
    private readonly record struct Tok(T Kind, string Text);

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                list.Add(new(T.Number, s[start..i]));
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new(T.Ident, s[start..i]));
                continue;
            }
            if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new ExprException("Falta cerrar una comilla.");
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                list.Add(new(T.String, sb.ToString()));
                continue;
            }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "<>") { list.Add(new(T.Op, two)); i += 2; continue; }
            switch (c)
            {
                case '+' or '-' or '*' or '/' or '=' or '<' or '>' or '&': list.Add(new(T.Op, c.ToString())); break;
                case '(': list.Add(new(T.LParen, "(")); break;
                case ')': list.Add(new(T.RParen, ")")); break;
                case ',': list.Add(new(T.Comma, ",")); break;
                case '.': list.Add(new(T.Dot, ".")); break;
                default: throw new ExprException($"Carácter no permitido: '{c}'.");
            }
            i++;
        }
        list.Add(new(T.End, ""));
        return list;
    }

    private sealed class Parser(List<Tok> toks)
    {
        private int _pos;
        public Tok Current => toks[_pos];
        public bool AtEnd => Current.Kind == T.End;

        private bool IsKeyword(string kw) => Current.Kind == T.Ident && string.Equals(Current.Text, kw, StringComparison.OrdinalIgnoreCase);
        private bool IsOp(params string[] ops) => Current.Kind == T.Op && ops.Contains(Current.Text);
        private Tok Next() => toks[_pos++];

        private void Expect(T kind, string what)
        {
            if (Current.Kind != kind) throw new ExprException($"Se esperaba {what}{(AtEnd ? " al final" : $" cerca de '{Current.Text}'")}.");
            _pos++;
        }

        private static void Guard(int depth)
        {
            if (depth > MaxDepth) throw new ExprException("La expresión está demasiado anidada.");
        }

        public Node ParseOr(int d)
        {
            Guard(d);
            var left = ParseAnd(d + 1);
            while (IsKeyword("OR")) { Next(); left = new Binary("OR", left, ParseAnd(d + 1)); }
            return left;
        }

        private Node ParseAnd(int d)
        {
            var left = ParseNot(d + 1);
            while (IsKeyword("AND")) { Next(); left = new Binary("AND", left, ParseNot(d + 1)); }
            return left;
        }

        private Node ParseNot(int d)
        {
            Guard(d);
            if (IsKeyword("NOT")) { Next(); return new Unary("NOT", ParseNot(d + 1)); }
            return ParseCmp(d + 1);
        }

        private Node ParseCmp(int d)
        {
            var left = ParseAdd(d + 1);
            if (IsOp("=", "<>", "<", "<=", ">", ">="))
                return new Binary(Next().Text, left, ParseAdd(d + 1));
            if (IsKeyword("IN"))
            {
                Next();
                Expect(T.LParen, "'('");
                var items = new List<Node> { ParseOr(d + 1) };
                while (Current.Kind == T.Comma) { Next(); items.Add(ParseOr(d + 1)); }
                Expect(T.RParen, "')'");
                return new InList(left, items);
            }
            return left;
        }

        private Node ParseAdd(int d)
        {
            var left = ParseMul(d + 1);
            while (IsOp("+", "-", "&")) { var op = Next().Text; left = new Binary(op, left, ParseMul(d + 1)); }
            return left;
        }

        private Node ParseMul(int d)
        {
            var left = ParseUnary(d + 1);
            while (IsOp("*", "/")) { var op = Next().Text; left = new Binary(op, left, ParseUnary(d + 1)); }
            return left;
        }

        private Node ParseUnary(int d)
        {
            Guard(d);
            if (IsOp("-")) { Next(); return new Unary("-", ParseUnary(d + 1)); }
            return ParsePrimary(d + 1);
        }

        private Node ParsePrimary(int d)
        {
            var t = Current;
            switch (t.Kind)
            {
                case T.Number:
                    Next();
                    if (!decimal.TryParse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                        throw new ExprException($"Número inválido: {t.Text}");
                    return new Literal(n);
                case T.String:
                    Next();
                    return new Literal(t.Text);
                case T.LParen:
                    Next();
                    var inner = ParseOr(d + 1);
                    Expect(T.RParen, "')'");
                    return inner;
                case T.Ident:
                    Next();
                    var upper = t.Text.ToUpperInvariant();
                    if (upper == "TRUE") return new Literal(true);
                    if (upper == "FALSE") return new Literal(false);
                    if (upper == "NULL") return new Literal(null);
                    if (Current.Kind == T.LParen)
                    {
                        Next();
                        var args = new List<Node>();
                        if (Current.Kind != T.RParen)
                        {
                            args.Add(ParseOr(d + 1));
                            while (Current.Kind == T.Comma) { Next(); args.Add(ParseOr(d + 1)); }
                        }
                        Expect(T.RParen, "')'");
                        return new Call(upper, args);
                    }
                    if (Current.Kind == T.Dot)
                    {
                        Next();
                        if (Current.Kind != T.Ident) throw new ExprException($"Se esperaba un campo después de '{t.Text}.'.");
                        return new PathRef(t.Text, Next().Text);
                    }
                    return new PathRef(t.Text, null);
                default:
                    throw new ExprException(AtEnd ? "La expresión está incompleta." : $"Inesperado: '{t.Text}'.");
            }
        }
    }
}
