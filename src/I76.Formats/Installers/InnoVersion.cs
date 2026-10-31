using System.Text.RegularExpressions;

namespace I76.Formats.Installers;

/// <summary>
/// An Inno Setup data version, from the 64-byte ID at the start of the setup headers ("Inno Setup Setup Data (5.5.0) (u)").
/// Compared as a.b.c.d; Unicode builds store strings as UTF-16.
/// </summary>
public readonly record struct InnoVersion(int A, int B, int C, int D = 0, bool Unicode = false) : IComparable<InnoVersion>
{
    public uint Value => (uint)((A << 24) | (B << 16) | (C << 8) | D);

    public int CompareTo(InnoVersion o) => Value.CompareTo(o.Value);
    public static bool operator >=(InnoVersion x, InnoVersion y) => x.Value >= y.Value;
    public static bool operator <=(InnoVersion x, InnoVersion y) => x.Value <= y.Value;
    public static bool operator >(InnoVersion x, InnoVersion y) => x.Value > y.Value;
    public static bool operator <(InnoVersion x, InnoVersion y) => x.Value < y.Value;

    public static InnoVersion V(int a, int b, int c, int d = 0) => new(a, b, c, d);

    /// <summary>The oldest data version this reader parses (GOG's installers are 5.2 and later).</summary>
    public static readonly InnoVersion Oldest = V(5, 0, 0);

    /// <summary>The newest layout this reader knows; newer installers are read with it (the checksums catch a mismatch).</summary>
    public static readonly InnoVersion Newest = V(6, 4, 0, 1);

    public override string ToString() => $"{A}.{B}.{C}{(D != 0 ? $".{D}" : "")}{(Unicode ? " (unicode)" : "")}";

    /// <summary>Parses the setup-data ID. Null when it isn't Inno Setup's.</summary>
    public static InnoVersion? Parse(string id)
    {
        if (!id.Contains("Inno Setup")) return null;
        InnoVersion? best = null;
        foreach (Match m in Regex.Matches(id, @"\((\d+)\.(\d+)\.(\d+)(?:a)?(?:\.(\d+))?\)"))
        {
            var v = V(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
            if (best is null || v > best.Value) best = v;
        }
        if (best is not { } b) return null;
        // 6.3 and later are always Unicode
        bool unicode = b >= V(6, 3, 0) || id.Contains("(u)") || id.Contains("(U)");
        return b with { Unicode = unicode };
    }
}
