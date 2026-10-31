using System.Globalization;
using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The weapons a network game does not allow (the Nitro host form's WEAPON RESTRICTIONS, docs/hidden-content.md §4), as the
/// original keeps them: every weapon (<see cref="All"/>, the original's count −1), or GDF (class, tier) pairs where tier −1
/// stands for the whole class. A weapon is matched by its .gdf's GDFC class and tier.
/// </summary>
public sealed record WeaponRestrictions(bool All, IReadOnlyList<(int Class, int Tier)> Pairs)
{
    public static readonly WeaponRestrictions None = new(false, []);
    public static readonly WeaponRestrictions Everything = new(true, []);
    /// <summary>The most pairs a list carries (the original's block holds 50).</summary>
    public const int MaxPairs = 64;

    public bool IsEmpty => !All && Pairs.Count == 0;

    public bool Restricts(int cls, int tier) => All || Pairs.Any(p => p.Class == cls && (p.Tier == -1 || p.Tier == tier));

    /// <summary>Equal by content (a record's own equality would compare the list by reference).</summary>
    public bool Equals(WeaponRestrictions? o) => o is not null && All == o.All && Pairs.SequenceEqual(o.Pairs);

    public override int GetHashCode() => Pairs.Aggregate(All ? 1 : 0, (h, p) => HashCode.Combine(h, p.Class, p.Tier));

    /// <summary>A .gdf's GDFC class and tier, or null for a file that is not a weapon.</summary>
    public static (int Class, int Tier)? KeyOf(AssetVfs vfs, string gdf)
    {
        if (gdf.Length == 0 || gdf.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (!gdf.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase)) gdf += ".gdf";
        try
        {
            if (vfs.TryRead(gdf) is not { } d || new Bwd2File(d).First("GDFC") is not { } c || c.PayloadSize < 0x18) return null;
            return ((int)Bin.U32(d, c.PayloadOffset + 0x10), (int)Bin.U32(d, c.PayloadOffset + 0x14));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The .gdf files of <paramref name="weapons"/> this list forbids, each once.</summary>
    public List<string> Violations(AssetVfs vfs, IEnumerable<string> weapons)
    {
        var bad = new List<string>();
        if (IsEmpty) return bad;
        foreach (var w in weapons)
            if (KeyOf(vfs, w) is { } k && Restricts(k.Class, k.Tier) && !bad.Contains(w, StringComparer.OrdinalIgnoreCase)) bad.Add(w);
        return bad;
    }

    /// <summary>The groups the command line names (the checklist's headers), by GDF class.</summary>
    public static readonly (string Name, int Class)[] Groups =
        [("slugs", 2), ("mortars", 3), ("missiles", 4), ("flames", 5), ("droppers", 6)];

    /// <summary>
    /// <c>i26-server --restrict</c>: a comma-separated list of <c>all</c>, a group (<c>slugs</c>, <c>mortars</c>, <c>missiles</c>,
    /// <c>flames</c>, <c>droppers</c>), a weapon's file (<c>gchades</c>, <c>tdumb.gdf</c>) or <c>class:tier</c> (tier −1 or *
    /// for the whole class). Null with <paramref name="error"/> for a name it does not know.
    /// </summary>
    public static WeaponRestrictions? Parse(string spec, AssetVfs? vfs, out string error)
    {
        error = "";
        bool all = false;
        var pairs = new List<(int, int)>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = raw.ToLowerInvariant();
            if (t == "all") { all = true; continue; }
            int group = Array.FindIndex(Groups, g => g.Name == t || g.Name.TrimEnd('s') == t);
            if (group >= 0) { pairs.Add((Groups[group].Class, -1)); continue; }
            var colon = t.Split(':');
            if (colon.Length == 2 && int.TryParse(colon[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)
                && (colon[1] == "*" || int.TryParse(colon[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                pairs.Add((c, colon[1] == "*" ? -1 : int.Parse(colon[1], CultureInfo.InvariantCulture)));
                continue;
            }
            if (vfs is not null && KeyOf(vfs, t) is { } k) { pairs.Add(k); continue; }
            error = $"no weapon or group {raw}";
            return null;
        }
        return Normalize(all, pairs);
    }

    /// <summary>The list as <see cref="Parse"/> reads it back: <c>all</c>, or the pairs as class:tier.</summary>
    public string ToSpec() => All ? "all" : string.Join(",", Pairs.Select(p => $"{p.Class}:{p.Tier}"));

    /// <summary>All, or the pairs without repeats and without those a whole-class pair already covers, at most <see cref="MaxPairs"/>.</summary>
    public static WeaponRestrictions Normalize(bool all, IEnumerable<(int Class, int Tier)> pairs)
    {
        if (all) return Everything;
        var l = new List<(int, int)>();
        var list = pairs.ToList();
        foreach (var p in list)
        {
            if (l.Contains(p)) continue;
            if (p.Tier != -1 && list.Contains((p.Class, -1))) continue;
            l.Add(p);
        }
        return l.Count == 0 ? None : new WeaponRestrictions(false, l.Take(MaxPairs).ToList());
    }

    public override string ToString() => IsEmpty ? "none" : ToSpec();
}

/// <summary>
/// The WEAPON RESTRICTIONS popup's boxes (the clicks and the list it makes), over
/// the original's rows (<see cref="RestrictionTable"/>) and the port's own for the hidden weapons (docs/hidden-content.md §5).
/// </summary>
public sealed class RestrictionChecklist
{
    public IReadOnlyList<RestrictionRow> Rows { get; }
    public bool[] Checked { get; }

    public RestrictionChecklist(IReadOnlyList<RestrictionRow> rows, bool[]? state = null)
    {
        Rows = rows;
        Checked = new bool[rows.Count];
        if (state is not null) Array.Copy(state, Checked, System.Math.Min(state.Length, Checked.Length));
    }

    /// <summary>
    /// A click on row <paramref name="i"/>: it flips. A group's header carries its new state to every row of its group; a row
    /// unchecked under a checked header clears the header; any row unchecked clears All Weapons (which itself ticks no other).
    /// </summary>
    public void Toggle(int i)
    {
        if (i < 0 || i >= Rows.Count) return;
        bool on = Checked[i] = !Checked[i];
        var r = Rows[i];
        if (r.Class == -1) return;
        if (r.IsHeader)
        {
            for (int k = 0; k < Rows.Count; k++) if (Rows[k].Group == r.Group && Rows[k].Class != -1) Checked[k] = on;
        }
        else if (!on)
        {
            for (int k = 0; k < Rows.Count; k++) if (Rows[k].Group == r.Group && Rows[k].IsHeader && Rows[k].Class != -1) Checked[k] = false;
        }
        if (!on) for (int k = 0; k < Rows.Count; k++) if (Rows[k].Class == -1) Checked[k] = false;
    }

    /// <summary>The list the boxes make: All Weapons checked is everything; a checked header its whole class; else each checked row.</summary>
    public WeaponRestrictions Build()
    {
        if (Rows.Where((r, i) => r.Class == -1 && Checked[i]).Any()) return WeaponRestrictions.Everything;
        return WeaponRestrictions.Normalize(false, Rows.Where((r, i) => Checked[i]).Select(r => (r.Class, r.Tier)));
    }

    /// <summary>Ticks the boxes a list stands for (a list kept from before): All Weapons, the headers of whole classes, each row's pair.</summary>
    public void Load(WeaponRestrictions w)
    {
        Array.Clear(Checked);
        for (int i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            Checked[i] = r.Class == -1 ? w.All : w.All ? false : r.IsHeader ? w.Pairs.Contains((r.Class, -1)) : w.Restricts(r.Class, r.Tier);
        }
    }
}
