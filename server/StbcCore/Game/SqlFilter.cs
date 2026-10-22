using System;
using System.Collections.Generic;
using System.Globalization;

namespace StbcServer.Game;

/// <summary>
/// The subset of Photon's SQL lobby filter syntax used for random matchmaking, e.g.
/// <c>C0 = 1 AND C3 = 2 AND C1 &lt; 4 AND C7 NOT IN ('a','b')</c>.
/// Supports = == &lt;&gt; != &lt; &lt;= &gt; &gt;=, AND, OR, NOT, parentheses, [NOT] IN (...), IS [NOT] NULL.
/// Identifiers refer to custom room properties (string keys). A missing property is NULL and
/// makes any comparison false, as in SQL.
/// </summary>
public sealed class SqlFilter
{
    private static readonly Dictionary<string, SqlFilter> Cache = new();

    private readonly Node root;

    private SqlFilter(Node root) => this.root = root;

    public static SqlFilter Parse(string text)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var cached)) return cached;
            var parsed = new SqlFilter(new Parser(Tokenize(text)).ParseExpression());
            if (Cache.Count > 1000) Cache.Clear();
            Cache[text] = parsed;
            return parsed;
        }
    }

    public bool Matches(Func<string, object?> lookup) => root.Eval(lookup) == true;

    // ---- AST ------------------------------------------------------------------------------

    private abstract class Node
    {
        /// <summary>Three-valued logic: null is SQL UNKNOWN.</summary>
        public abstract bool? Eval(Func<string, object?> lookup);
    }

    private sealed class And(Node l, Node r) : Node
    {
        public override bool? Eval(Func<string, object?> f)
        {
            var a = l.Eval(f);
            if (a == false) return false;
            var b = r.Eval(f);
            if (b == false) return false;
            return a == true && b == true ? true : null;
        }
    }

    private sealed class Or(Node l, Node r) : Node
    {
        public override bool? Eval(Func<string, object?> f)
        {
            var a = l.Eval(f);
            if (a == true) return true;
            var b = r.Eval(f);
            if (b == true) return true;
            return a == false && b == false ? false : null;
        }
    }

    private sealed class Not(Node n) : Node
    {
        public override bool? Eval(Func<string, object?> f) => n.Eval(f) is { } v ? !v : null;
    }

    private abstract class Operand
    {
        public abstract object? Value(Func<string, object?> f);
    }

    private sealed class Column(string name) : Operand
    {
        public override object? Value(Func<string, object?> f) => f(name);
    }

    private sealed class Literal(object? value) : Operand
    {
        public override object? Value(Func<string, object?> f) => value;
    }

    private sealed class Compare(Operand l, string op, Operand r) : Node
    {
        public override bool? Eval(Func<string, object?> f)
        {
            var c = CompareValues(l.Value(f), r.Value(f));
            if (c == null) return null;
            return op switch
            {
                "=" or "==" => c == 0,
                "<>" or "!=" => c != 0,
                "<" => c < 0,
                "<=" => c <= 0,
                ">" => c > 0,
                ">=" => c >= 0,
                _ => throw new FormatException($"unknown operator {op}"),
            };
        }
    }

    private sealed class In(Operand l, List<Operand> list, bool negated) : Node
    {
        public override bool? Eval(Func<string, object?> f)
        {
            var v = l.Value(f);
            if (v == null) return null;
            bool unknown = false;
            foreach (var item in list)
            {
                var c = CompareValues(v, item.Value(f));
                if (c == 0) return !negated;
                if (c == null) unknown = true;
            }
            return unknown ? null : negated;
        }
    }

    private sealed class IsNull(Operand l, bool negated) : Node
    {
        public override bool? Eval(Func<string, object?> f) => (l.Value(f) == null) != negated;
    }

    private static int? CompareValues(object? a, object? b)
    {
        if (a == null || b == null) return null;
        if (ToNumber(a) is { } x && ToNumber(b) is { } y) return x.CompareTo(y);
        if (a is string s && b is string t) return string.CompareOrdinal(s, t);
        return null;
    }

    private static double? ToNumber(object v) => v switch
    {
        byte b => b, short s => s, int i => i, long l => l, float f => f, double d => d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    // ---- Parsing --------------------------------------------------------------------------

    private enum Tok { Ident, Number, String, Op, LParen, RParen, Comma, End }

    private readonly struct Token
    {
        public Token(Tok kind, string text) { Kind = kind; Text = text; }
        public Tok Kind { get; }
        public string Text { get; }
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new(Tok.LParen, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new(Tok.RParen, ")")); i++; continue; }
            if (c == ',') { tokens.Add(new(Tok.Comma, ",")); i++; continue; }
            if (c == '\'')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new FormatException("unterminated string");
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                tokens.Add(new(Tok.String, sb.ToString()));
                continue;
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int start = i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                tokens.Add(new(Tok.Number, s.Substring(start, i - start)));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new(Tok.Ident, s.Substring(start, i - start)));
                continue;
            }
            foreach (var op in new[] { "<=", ">=", "<>", "!=", "==", "=", "<", ">" })
            {
                if (string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
                {
                    tokens.Add(new(Tok.Op, op));
                    i += op.Length;
                    goto next;
                }
            }
            throw new FormatException($"unexpected character '{c}' at {i}");
            next: ;
        }
        tokens.Add(new(Tok.End, ""));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int pos;

        private Token Peek => tokens[pos];
        private Token Next() => tokens[pos++];
        private bool IsKeyword(string kw) => Peek.Kind == Tok.Ident && Peek.Text.Equals(kw, StringComparison.OrdinalIgnoreCase);

        private void Expect(Tok kind)
        {
            if (Next().Kind != kind) throw new FormatException($"expected {kind} at token {pos - 1}");
        }

        public Node ParseExpression()
        {
            var n = ParseOr();
            if (Peek.Kind != Tok.End) throw new FormatException($"unexpected '{Peek.Text}'");
            return n;
        }

        private Node ParseOr()
        {
            var n = ParseAnd();
            while (IsKeyword("OR")) { Next(); n = new Or(n, ParseAnd()); }
            return n;
        }

        private Node ParseAnd()
        {
            var n = ParseNot();
            while (IsKeyword("AND")) { Next(); n = new And(n, ParseNot()); }
            return n;
        }

        private Node ParseNot()
        {
            if (IsKeyword("NOT")) { Next(); return new Not(ParseNot()); }
            if (Peek.Kind == Tok.LParen)
            {
                Next();
                var inner = ParseOr();
                Expect(Tok.RParen);
                return inner;
            }
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParseOperand();
            if (IsKeyword("IS"))
            {
                Next();
                bool neg = false;
                if (IsKeyword("NOT")) { Next(); neg = true; }
                if (!IsKeyword("NULL")) throw new FormatException("expected NULL");
                Next();
                return new IsNull(left, neg);
            }
            bool negated = false;
            if (IsKeyword("NOT")) { Next(); negated = true; }
            if (IsKeyword("IN"))
            {
                Next();
                Expect(Tok.LParen);
                var list = new List<Operand> { ParseOperand() };
                while (Peek.Kind == Tok.Comma) { Next(); list.Add(ParseOperand()); }
                Expect(Tok.RParen);
                return new In(left, list, negated);
            }
            if (negated) throw new FormatException("expected IN after NOT");
            var op = Next();
            if (op.Kind != Tok.Op) throw new FormatException($"expected operator, got '{op.Text}'");
            return new Compare(left, op.Text, ParseOperand());
        }

        private Operand ParseOperand()
        {
            var t = Next();
            switch (t.Kind)
            {
                case Tok.Number:
                    return new Literal(t.Text.IndexOf('.') >= 0
                        ? double.Parse(t.Text, CultureInfo.InvariantCulture)
                        : long.Parse(t.Text, CultureInfo.InvariantCulture));
                case Tok.String:
                    return new Literal(t.Text);
                case Tok.Ident when t.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase):
                    return new Literal(true);
                case Tok.Ident when t.Text.Equals("FALSE", StringComparison.OrdinalIgnoreCase):
                    return new Literal(false);
                case Tok.Ident when t.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase):
                    return new Literal(null);
                case Tok.Ident:
                    return new Column(t.Text);
                default:
                    throw new FormatException($"expected value, got '{t.Text}'");
            }
        }
    }
}
