using System.Globalization;
using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The weapons a network game does not allow (the Nitro host form's WEAPON RESTRICTIONS, docs/hidden-content.md §4), as the
/// original keeps them: every weapon (<see cref="All"/>, the original's count −1), or GDF (class, tier) pairs where tier −1
/// stands for the whole class. A weapon is matched by its .gdf's GDFC class and tier. The port adds the specials no car may
/// carry (<see cref="Specials"/>; the original restricts none).
/// </summary>
public sealed record WeaponRestrictions(bool All, IReadOnlyList<(int Class, int Tier)> Pairs)
{
    public static readonly WeaponRestrictions None = new(false, []);
    public static readonly WeaponRestrictions Everything = new(true, []);
    /// <summary>The most pairs a list carries (the original's block holds 50).</summary>
    public const int MaxPairs = 64;

    /// <summary>
    /// The specials the game does not allow (the port's SPECIALS page of the checklist, docs/hidden-content.md §5): their types as
    /// a .vcf's SPEC holds them (1 Radar Jammer .. 11 Tire Covers, <see cref="PartsDatabase.SpecialNames"/>), ascending, each once.
    /// All Weapons does not cover them.
    /// </summary>
    public IReadOnlyList<int> Specials { get; init; } = [];

    /// <summary>The highest special type (Nitro's Tire Covers).</summary>
    public static int MaxSpecial => PartsDatabase.SpecialNames.Length;

    public bool IsEmpty => !All && Pairs.Count == 0 && Specials.Count == 0;

    /// <summary>No weapon is restricted (specials may be).</summary>
    public bool NoWeapons => !All && Pairs.Count == 0;

    public bool Restricts(int cls, int tier) => All || Pairs.Any(p => p.Class == cls && (p.Tier == -1 || p.Tier == tier));

    public bool RestrictsSpecial(int type) => Specials.Contains(type);

    /// <summary>Equal by content (a record's own equality would compare the lists by reference).</summary>
    public bool Equals(WeaponRestrictions? o) => o is not null && All == o.All && Pairs.SequenceEqual(o.Pairs) && Specials.SequenceEqual(o.Specials);

    public override int GetHashCode() =>
        Specials.Aggregate(Pairs.Aggregate(All ? 1 : 0, (h, p) => HashCode.Combine(h, p.Class, p.Tier)), (h, s) => HashCode.Combine(h, s));

    /// <summary>The same weapons with these specials restricted (only types 1..<see cref="MaxSpecial"/>, ascending, each once); <see cref="None"/> when that leaves nothing.</summary>
    public WeaponRestrictions WithSpecials(IEnumerable<int> types)
    {
        var l = types.Where(t => t >= 1 && t <= MaxSpecial).Distinct().Order().ToList();
        if (l.Count == 0 && NoWeapons) return None;
        if (l.SequenceEqual(Specials)) return this;
        return this with { Specials = l };
    }

    /// <summary>The restricted specials among a car's (its SPEC types; 0 an empty slot), each once, in slot order.</summary>
    public List<int> SpecialViolations(IEnumerable<int> specials) => [.. specials.Where(s => s > 0 && RestrictsSpecial(s)).Distinct()];

    /// <summary>A special's name as the garage shows it (<see cref="PartsDatabase.SpecialNames"/>).</summary>
    public static string SpecialName(int type) => type >= 1 && type <= MaxSpecial ? PartsDatabase.SpecialNames[type - 1] : $"special {type}";

    /// <summary>
    /// The command line's names for the specials, by type: the first of each is the one <see cref="ToSpec"/> writes. The garage's own name
    /// without spaces or dashes (<c>radarjammer</c>, <c>xaustbrake</c>) and the part's stem (<c>spc01</c>) are taken too.
    /// </summary>
    public static readonly (int Type, string[] Names)[] SpecialKeys =
    [
        (1, ["jammer", "radar"]), (2, ["nitro", "nitrous"]), (3, ["blower"]), (4, ["exhaust", "xaust"]), (5, ["bumper", "structo"]),
        (6, ["curb", "feelers"]), (7, ["mudflaps", "mud"]), (8, ["seats", "heated"]), (9, ["cupholders", "cup"]), (10, ["mary", "dashboard"]),
        (11, ["covers", "tirecovers"]),
    ];

    /// <summary>A special by a command-line name (<see cref="SpecialKeys"/>, the garage's name, spcNN), or 0.</summary>
    public static int SpecialByName(string name)
    {
        var t = new string([.. name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        foreach (var (type, names) in SpecialKeys) if (names.Contains(t)) return type;
        for (int i = 1; i <= MaxSpecial; i++)
            if (t == new string([.. SpecialName(i).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]) || t == $"spc{i:00}") return i;
        return 0;
    }

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
    /// <c>i26-server --restrict</c>: a comma-separated list of <c>all</c> (every weapon), a group (<c>slugs</c>, <c>mortars</c>,
    /// <c>missiles</c>, <c>flames</c>, <c>droppers</c>), a weapon's file (<c>gchades</c>, <c>tdumb.gdf</c>) or <c>class:tier</c> (tier
    /// −1 or * for the whole class), a special (<see cref="SpecialKeys"/>: <c>jammer</c>, <c>nitro</c>, <c>blower</c> ...) or
    /// <c>specials</c> (every special the install's garage offers; all eleven without an install). Null with <paramref name="error"/>
    /// for a name it does not know.
    /// </summary>
    public static WeaponRestrictions? Parse(string spec, AssetVfs? vfs, out string error)
    {
        error = "";
        bool all = false;
        var pairs = new List<(int, int)>();
        var specials = new List<int>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = raw.ToLowerInvariant();
            if (t == "all") { all = true; continue; }
            if (t is "specials" or "special")
            {
                specials.AddRange(Enumerable.Range(1, vfs is null ? MaxSpecial : PartsDatabase.SpecialCount(vfs.Install.IsNitro)));
                continue;
            }
            if (SpecialByName(t) is > 0 and var sp) { specials.Add(sp); continue; }
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
            error = $"no weapon, special or group {raw}";
            return null;
        }
        return Normalize(all, pairs).WithSpecials(specials);
    }

    /// <summary>The list as <see cref="Parse"/> reads it back: <c>all</c> or the pairs as class:tier, then the specials by their first names.</summary>
    public string ToSpec() => string.Join(",", (All ? new[] { "all" } : Pairs.Select(p => $"{p.Class}:{p.Tier}").ToArray())
        .Concat(Specials.Select(s => SpecialKeys.FirstOrDefault(k => k.Type == s).Names?[0] ?? $"spc{s:00}")));

    /// <summary>All, or the pairs without repeats and without those a whole-class pair already covers, at most <see cref="MaxPairs"/> (no specials: <see cref="WithSpecials"/>).</summary>
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

/// <summary>
/// The checklist's SPECIALS page (the port's; docs/hidden-content.md §5): a box for each special the install's garage offers, by
/// the garage's name, and NO SPECIALS IN GAME, set out on the WEAPONS RESTRICTIONS art's own grid. The clicks follow the
/// weapons page's: NO SPECIALS IN GAME stands for every special and does not tick the others; any row unchecked clears it.
/// </summary>
public sealed class SpecialChecklist
{
    /// <summary>The page's rows: NO SPECIALS IN GAME first (class −2, tier −1), then each special (class −2, tier its type).</summary>
    public IReadOnlyList<RestrictionRow> Rows { get; }
    public bool[] Checked { get; }

    /// <summary>The marker a special's row carries for its class (no weapon class is negative but All Weapons' −1).</summary>
    public const int Class = -2;

    public SpecialChecklist(bool nitro)
    {
        Rows = ChecklistRows(nitro);
        Checked = new bool[Rows.Count];
    }

    /// <summary>
    /// The rows on the art's grid (its box columns 0..5 and rows 0..17): the specials one under another from the first line under
    /// the headers, the first six in the left half's header column, the rest in the right half's, and NO SPECIALS IN GAME where the
    /// weapons page has NO WEAPONS IN GAME (column 3, row 17).
    /// </summary>
    public static List<RestrictionRow> ChecklistRows(bool nitro)
    {
        var rows = new List<RestrictionRow> { new(0, Class, -1, 3, 17, "No Specials") };
        int n = PartsDatabase.SpecialCount(nitro);
        for (int t = 1; t <= n; t++)
            rows.Add(new RestrictionRow(t <= 6 ? 1 : 2, Class, t, t <= 6 ? 0 : 3, t <= 6 ? t : t - 6, WeaponRestrictions.SpecialName(t)));
        return rows;
    }

    public void Toggle(int i)
    {
        if (i < 0 || i >= Rows.Count) return;
        bool on = Checked[i] = !Checked[i];
        if (!on && !Rows[i].IsHeader) for (int k = 0; k < Rows.Count; k++) if (Rows[k].IsHeader) Checked[k] = false;
    }

    /// <summary>The specials the boxes restrict: every one of the page's under NO SPECIALS IN GAME, else each checked row's.</summary>
    public IReadOnlyList<int> Build()
    {
        bool all = Rows.Where((r, i) => r.IsHeader && Checked[i]).Any();
        return [.. Rows.Where((r, i) => !r.IsHeader && (all || Checked[i])).Select(r => r.Tier)];
    }

    /// <summary>Ticks the boxes a list stands for: NO SPECIALS IN GAME alone when it restricts every special of the page, else each one's row.</summary>
    public void Load(WeaponRestrictions w)
    {
        bool all = Rows.Where(r => !r.IsHeader).All(r => w.RestrictsSpecial(r.Tier));
        for (int i = 0; i < Rows.Count; i++) Checked[i] = Rows[i].IsHeader ? all : !all && w.RestrictsSpecial(Rows[i].Tier);
    }
}
