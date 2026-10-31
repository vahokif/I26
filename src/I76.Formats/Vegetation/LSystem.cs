using System.Globalization;
using System.Text;

namespace I76.Formats.Vegetation;

/// <summary>One production of a stochastic L-system: <see cref="Predecessor"/> becomes <see cref="Successor"/> with a weight among its alternatives.</summary>
public sealed record Production(char Predecessor, float Weight, string Successor);

/// <summary>
/// A bracketed, stochastic L-system (Prusinkiewicz and Lindenmayer, "The Algorithmic Beauty of Plants", ch. 1.7-1.10)
/// with turtle parameters written after a symbol in parentheses: <c>+(30)</c>, <c>F(0.4)</c>. Only bare symbols are
/// rewritten; a symbol with a parameter is a finished turtle command and is copied as it is. Each alternative of a
/// predecessor is chosen by weight from a seeded stream, so a seed always grows the same string.
/// </summary>
public sealed class LSystem
{
    public required string Axiom { get; init; }
    private readonly Dictionary<char, Production[]> _rules = [];

    public LSystem() { }

    public IEnumerable<Production> Productions
    {
        init { foreach (var g in value.GroupBy(p => p.Predecessor)) _rules[g.Key] = [.. g]; }
    }

    /// <summary>Symbols left over after the last step become these (an apex that never grew ends as a leaf, say).</summary>
    public Dictionary<char, string> Finish { get; init; } = [];

    /// <summary>The string after <paramref name="iterations"/> parallel rewrites, then <see cref="Finish"/>; stops growing past <paramref name="maxLength"/> symbols.</summary>
    public string Rewrite(int iterations, ref VegRng rng, int maxLength = 40000)
    {
        string s = Axiom;
        for (int i = 0; i < iterations && s.Length < maxLength; i++) s = Step(s, ref rng);
        if (Finish.Count == 0) return s;
        var sb = new StringBuilder(s.Length * 2);
        foreach (var (sym, par) in Tokens(s))
        {
            if (par is null && Finish.TryGetValue(sym, out var f)) sb.Append(f);
            else Append(sb, sym, par);
        }
        return sb.ToString();
    }

    private string Step(string s, ref VegRng rng)
    {
        var sb = new StringBuilder(s.Length * 3);
        foreach (var (sym, par) in Tokens(s))
        {
            if (par is null && _rules.TryGetValue(sym, out var alts)) sb.Append(Choose(alts, ref rng).Successor);
            else Append(sb, sym, par);
        }
        return sb.ToString();
    }

    private static Production Choose(Production[] alts, ref VegRng rng)
    {
        if (alts.Length == 1) return alts[0];
        float total = 0;
        foreach (var a in alts) total += a.Weight;
        float r = rng.Float() * total;
        foreach (var a in alts)
        {
            r -= a.Weight;
            if (r < 0) return a;
        }
        return alts[^1];
    }

    private static void Append(StringBuilder sb, char sym, string? par)
    {
        sb.Append(sym);
        if (par is not null) sb.Append('(').Append(par).Append(')');
    }

    /// <summary>The symbols of a string with their parameter text (null when bare).</summary>
    public static IEnumerable<(char Symbol, string? Param)> Tokens(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) continue;
            if (i + 1 < s.Length && s[i + 1] == '(')
            {
                int close = s.IndexOf(')', i + 2);
                if (close < 0) throw new FormatException($"unclosed parameter at {i} in an L-system string");
                yield return (c, s[(i + 2)..close]);
                i = close;
            }
            else yield return (c, null);
        }
    }

    public static float? Number(string? p) => p is null ? null : float.Parse(p, CultureInfo.InvariantCulture);

    /// <summary>A turtle command with a number, formatted for an axiom or successor built in code.</summary>
    public static string Cmd(char c, float v) => c + "(" + v.ToString("0.###", CultureInfo.InvariantCulture) + ")";
}
