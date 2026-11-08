using System.Globalization;

namespace I76.Formats.Fsm;

/// <summary>A source-form error, with where it was found.</summary>
public sealed class ScriptError(string message) : Exception(message);

/// <summary>
/// The text of conditions and actions: <c>name(arg, ...)</c> calls; arguments are field or variable names, <c>*variable</c>, integers
/// and table references (<c>actor.KEY</c>, <c>path.KEY</c>, <c>sound.KEY</c>); conditions combine calls with <c>!</c>, <c>&amp;&amp;</c>,
/// <c>||</c> and parentheses (! binds tightest, then &amp;&amp;, then ||; &amp;&amp; and || group to the right, as the bytecode
/// does), and isEqual, isGreater, isLesser of two arguments may be written <c>a == b</c>, <c>a &gt; b</c>, <c>a &lt; b</c>.
/// </summary>
public static class ScriptText
{
    public static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "stop", "restart", "next", "continue", "break", "actor", "path", "sound",
    };

    public static bool IsIdentifier(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A table key: letters, digits, _ . - (not starting with - or .).</summary>
    public static bool IsKey(string s) =>
        s.Length > 0 && s[0] is not ('-' or '.') && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static readonly Dictionary<string, string> Compare = new(StringComparer.OrdinalIgnoreCase) { ["isEqual"] = "==", ["isGreater"] = ">", ["isLesser"] = "<" };
    private static readonly Dictionary<string, string> CompareOp = new() { ["=="] = "isEqual", [">"] = "isGreater", ["<"] = "isLesser" };

    // ---- printing ----

    public static string Operand(ScriptOperand o) => o switch
    {
        ScriptOperand.Name n => n.Id,
        ScriptOperand.Deref d => "*" + d.Id,
        ScriptOperand.Literal l => l.Value.ToString(),
        _ => "?",
    };

    public static string Action(ScriptAction a)
    {
        if (a.Args.Count == 0 && (a.Op.Equals("true", StringComparison.OrdinalIgnoreCase) || a.Op.Equals("false", StringComparison.OrdinalIgnoreCase))) return a.Op;
        return a.Op + "(" + string.Join(", ", a.Args.Select(Operand)) + ")";
    }

    public static string Cond(ScriptCond c)
    {
        switch (c)
        {
            case ScriptCond.Test t:
                return IsComparison(t) ? $"{Operand(t.Op.Args[0])} {Compare[t.Op.Op]} {Operand(t.Op.Args[1])}" : Action(t.Op);
            case ScriptCond.Not n:
                return n.E is ScriptCond.Not || n.E is ScriptCond.Test t2 && !IsComparison(t2) ? "!" + Cond(n.E) : "!(" + Cond(n.E) + ")";
            case ScriptCond.And a:
                return (a.L is ScriptCond.And or ScriptCond.Or ? "(" + Cond(a.L) + ")" : Cond(a.L)) + " && " + (a.R is ScriptCond.Or ? "(" + Cond(a.R) + ")" : Cond(a.R));
            case ScriptCond.Or o:
                return (o.L is ScriptCond.And or ScriptCond.Or ? "(" + Cond(o.L) + ")" : Cond(o.L)) + " || " + (o.R is ScriptCond.And ? "(" + Cond(o.R) + ")" : Cond(o.R));
        }
        return "?";
    }

    private static bool IsComparison(ScriptCond.Test t) => !t.Op.IsCall && t.Op.Args.Count == 2 && Compare.ContainsKey(t.Op.Op);

    // ---- parsing ----

    public static ScriptValue ParseValue(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)) return new ScriptValue(v);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)) return new ScriptValue((int)h);
        int dot = s.IndexOf('.');
        if (dot > 0)
        {
            RefKind? k = s[..dot] switch { "actor" => RefKind.Actor, "path" => RefKind.Path, "sound" => RefKind.Sound, _ => null };
            if (k is { } kind && IsKey(s[(dot + 1)..])) return ScriptValue.Ref(kind, s[(dot + 1)..]);
        }
        throw new ScriptError($"'{s}' is not a number or a reference (actor.KEY, path.KEY, sound.KEY)");
    }

    public static ScriptAction ParseAction(string s)
    {
        var p = new Lexer(s);
        var a = p.Call(allowBare: true);
        p.End();
        return a;
    }

    public static ScriptCond ParseCond(string s)
    {
        var p = new Lexer(s);
        var c = p.Or();
        p.End();
        return c;
    }

    public static ScriptOperand ParseOperand(string s)
    {
        var p = new Lexer(s);
        var o = p.Operand();
        p.End();
        return o;
    }

    private sealed class Lexer(string s)
    {
        private int _i;

        private void Ws() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        private ScriptError Err(string what) => new($"{what} at column {_i + 1} of \"{s}\"");

        public void End()
        {
            Ws();
            if (_i < s.Length) throw Err("unexpected '" + s[_i..] + "'");
        }

        private bool Eat(string t)
        {
            Ws();
            if (string.CompareOrdinal(s, _i, t, 0, t.Length) != 0) return false;
            _i += t.Length;
            return true;
        }

        private string Word()
        {
            Ws();
            int st = _i;
            while (_i < s.Length && (char.IsAsciiLetterOrDigit(s[_i]) || s[_i] is '_' or '.' or '-' || (s[_i] == '+' && _i == st))) _i++;
            if (_i == st) throw Err("expected a name or a number");
            return s[st.._i];
        }

        public ScriptCond Or()
        {
            var l = And();
            return Eat("||") ? new ScriptCond.Or(l, Or()) : l;
        }

        private ScriptCond And()
        {
            var l = Unary();
            return Eat("&&") ? new ScriptCond.And(l, And()) : l;
        }

        private ScriptCond Unary()
        {
            Ws();
            if (_i < s.Length && s[_i] == '!' && !(_i + 1 < s.Length && s[_i + 1] == '='))
            {
                _i++;
                return new ScriptCond.Not(Unary());
            }
            if (Eat("("))
            {
                var e = Or();
                if (!Eat(")")) throw Err("expected ')'");
                return e;
            }
            int save = _i;
            Ws();
            int st = _i;
            while (_i < s.Length && (char.IsAsciiLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
            string word = s[st.._i];
            Ws();
            bool call = IsIdentifier(word) && (_i < s.Length && s[_i] == '(' || word is "true" or "false" && !NextIsCompare());
            _i = save;
            if (call) return new ScriptCond.Test(Call(allowBare: true));
            var a = Operand();
            Ws();
            foreach (var (tok, name) in CompareOp)
                if (Eat(tok))
                {
                    if (tok is ">" or "<" && _i < s.Length && s[_i] == '=') throw Err($"no '{tok}=': write !(a {(tok == ">" ? "<" : ">")} b)");
                    var b = Operand();
                    return new ScriptCond.Test(new ScriptAction(name, [a, b]));
                }
            if (Eat("!=")) throw Err("no '!=': write !(a == b)");
            throw Err("expected a call or a comparison (==, >, <)");
        }

        private bool NextIsCompare()
        {
            int k = _i;
            while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
            return k < s.Length && (s[k] is '>' or '<' || k + 1 < s.Length && s[k] == '=' && s[k + 1] == '=');
        }

        public ScriptAction Call(bool allowBare)
        {
            Ws();
            int st = _i;
            while (_i < s.Length && (char.IsAsciiLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
            string name = s[st.._i];
            if (!IsIdentifier(name)) throw Err("expected an operation name");
            var args = new List<ScriptOperand>();
            if (Eat("("))
            {
                if (!Eat(")"))
                {
                    do args.Add(Operand());
                    while (Eat(","));
                    if (!Eat(")")) throw Err("expected ',' or ')'");
                }
            }
            else if (!allowBare) throw Err("expected '('");
            return new ScriptAction(name, args);
        }

        public ScriptOperand Operand()
        {
            Ws();
            if (Eat("*"))
            {
                var w = Word();
                if (!IsIdentifier(w)) throw Err($"'*{w}': * takes a variable");
                return new ScriptOperand.Deref(w);
            }
            if (Eat("&")) throw Err("'&' is not needed: a variable is passed by its address");
            var t = Word();
            if (IsIdentifier(t) && !t.Contains('.')) return new ScriptOperand.Name(t);
            try { return new ScriptOperand.Literal(ParseValue(t)); }
            catch (ScriptError e) { throw Err(e.Message); }
        }
    }
}
