using I76.Sim.Damage;
using I76.Sim.Vehicles;

namespace I76.Sim.Trip;

/// <summary>One salvage item (the vsf's SALV record): the part's file key, its health, and whether the shell rolls a random condition (health −1, flag 1).</summary>
public sealed record SalvageItem(string Name, int Health, bool RandomCondition);

/// <summary>
/// The mission's field salvage: one pool per
/// attempt, fed by every non-player car that dies (its surviving parts at
/// their health) and, on a win, the mission's .fsi list (random
/// condition). Every item is clamped to what the next mission allows and
/// joins the group of items with the same type and the same (clamped) name;
/// each group is thinned on its own before the pool goes to the shell. Off
/// entirely with "No Salv. Manag.".
/// </summary>
public sealed class SalvagePool
{
    /// <summary>The groups in creation order, as the engine's list: one per type and name, so four 14in tyres are a group of their own.</summary>
    private readonly List<(int Type, List<SalvageItem> Items)> _groups = [];
    public bool Disabled { get; }
    /// <summary>The clamp's level: the next mission's number + 1.</summary>
    public int Level { get; }
    /// <summary>The game kind from the mission stem: t → 2 trip, s/p → 3, m/n → 1.</summary>
    public int Kind { get; }
    private readonly string _stem;

    public SalvagePool(string missionStem, bool noSalvageManagement = false)
    {
        _stem = missionStem.ToLowerInvariant();
        Disabled = noSalvageManagement;
        int number = _stem.Length > 1 && int.TryParse(new string(_stem.Skip(1).TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 39;
        Level = number + 1;
        Kind = _stem.Length > 0 ? _stem[0] switch { 't' => 2, 's' or 'p' => 3, 'm' or 'n' => 1, _ => 0 } : 0;
    }

    public IEnumerable<SalvageItem> Items => _groups.SelectMany(g => g.Items);

    /// <summary>The groups as bytes, for a checkpoint (the game keeps them beside the session's snapshot).</summary>
    public byte[] Save()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(_groups.Count);
            foreach (var (type, items) in _groups)
            {
                w.Write(type);
                w.Write(items.Count);
                foreach (var it in items) { w.Write(it.Name); w.Write(it.Health); w.Write(it.RandomCondition); }
            }
        }
        return ms.ToArray();
    }

    /// <summary>The groups as <see cref="Save"/> wrote them, in place of these.</summary>
    public void Load(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        _groups.Clear();
        for (int g = r.ReadInt32(); g > 0; g--)
        {
            int type = r.ReadInt32();
            var items = new List<SalvageItem>();
            for (int n = r.ReadInt32(); n > 0; n--) items.Add(new SalvageItem(r.ReadString(), r.ReadInt32(), r.ReadBoolean()));
            _groups.Add((type, items));
        }
    }

    /// <summary>The type from the file key: .wdf 0 wheel, .gdf 1 weapon, eng… 2, sus… 3, bra… 4, spc… 5; −1 unknown.</summary>
    public static int TypeOf(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(".wdf")) return 0;
        if (p.EndsWith(".gdf")) return 1;
        if (p.Contains('.')) return -1;
        return p.StartsWith("eng") ? 2 : p.StartsWith("sus") ? 3 : p.StartsWith("bra") ? 4 : p.StartsWith("spc") ? 5 : -1;
    }

    /// <summary>
    /// The type from the name, a wheel turned into the plain
    /// style in its size (wauto_0?), the tier clamp, then appended to the
    /// group whose type matches and whose first item has the same name
    /// (stricmp), or to a new group at the end of the list. Measured: a T03
    /// win with every car killed has a group of 48 13in tyres and a separate
    /// group of the .fsi's four 14in ones.
    /// </summary>
    public void Add(string path, int health, bool randomCondition)
    {
        if (Disabled) return;
        int type = TypeOf(path);
        if (type < 0) return;           // "Unknown Salvage type %s found"
        var name = path.ToLowerInvariant();
        if (type == 0 && name.Length >= 7) name = "wauto_0" + name[7..];
        if (Clamp(name, type, Level, Kind) is not { } clamped) return;
        var g = _groups.FirstOrDefault(x => x.Type == type && x.Items[0].Name.Equals(clamped, StringComparison.OrdinalIgnoreCase));
        if (g.Items is null) { g = (type, []); _groups.Add(g); }
        g.Items.Add(new SalvageItem(clamped, health, randomCondition));
    }

    /// <summary>The pool fed as the engine feeds it: every car but the player's, at the moment its salvage is taken (<see cref="DamageModel.Salvageable"/>).</summary>
    public void Watch(DamageModel damage, Vehicle? player) => damage.Salvageable += v => { if (v != player) AddDeadCar(v); };

    /// <summary>
    /// A dead non-player car's parts whose health is above 0 —
    /// the six wheels (their pair's .wdf), engine, suspension, brakes, the
    /// weapons (their .gdf) and the specials — at their current health.
    /// </summary>
    public void AddDeadCar(Vehicle v)
    {
        if (Disabled || v.Health is not { } d || v.Loadout is not { } lo) return;
        for (int k = 0; k < 6; k++)
            if (d.Slots[k] is WheelPart w && w.Hp > 0 && lo.Wdf[k / 2] is { Length: > 0 } wdf && !wdf.Equals("null", StringComparison.OrdinalIgnoreCase))
                Add(wdf, w.Hp, false);
        if (d.Engine is { Hp: > 0 } e) Add($"eng{lo.Engine + 1:00}", e.Hp, false);
        if (d.Suspension is { Hp: > 0 } s) Add($"sus{lo.Suspension + 1:00}", s.Hp, false);
        if (d.Brake is { Hp: > 0 } b) Add($"bra{lo.Brakes + 1:00}", b.Hp, false);
        for (int k = 10; k <= 16; k++)
            if (d.Slots[k] is WeaponPart wp && wp.Hp > 0) Add(wp.Weapon.Def.File, wp.Hp, false);
        for (int k = 17; k <= 19; k++)
            if (d.Slots[k] is SpecialPart sp && sp.Hp > 0) Add($"spc{sp.Type:00}", sp.Hp, false);
    }

    /// <summary>
    /// The end of a won mission (the original's start-up): nothing at all after
    /// T12; T04 keeps no salvage; otherwise the .fsi list joins the pool and
    /// each group is thinned: a wheel group to four, a special
    /// group to one, any other group to 3, 1 or 2 (one rand(): &amp;1 → 3,
    /// else &amp;2 → 1, else 2), never more than it holds. The picks are
    /// rand() % count into the group's original order, drawn again when that
    /// item is already taken, and kept in pick order. Measured against the
    /// original's own draws (tests/…/data/salvage-measured.csv). Returns null
    /// when the mission writes no state at all (T12).
    /// </summary>
    public List<SalvageItem>? Finish(Func<string, byte[]?> read, Func<int> rand)
    {
        if (_stem.StartsWith("t12")) return null;
        if (_stem.StartsWith("t04") || Disabled) return [];
        if (read($"{_stem}.fsi") is { } fsi)
            foreach (var raw in System.Text.Encoding.Latin1.GetString(fsi).Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int sp = line.IndexOf(' ');
                Add(sp < 0 ? line : line[..sp], -1, true);
            }
        var o = new List<SalvageItem>();
        foreach (var (type, items) in _groups)
        {
            int keep;
            if (type == 0) keep = 4;
            else if (type == 5) keep = 1;
            else { int r = rand(); keep = (r & 1) != 0 ? 3 : (r & 2) != 0 ? 1 : 2; }
            var left = items.ToArray<SalvageItem?>();
            for (int i = System.Math.Min(keep, items.Count); i > 0; i--)
            {
                int k;
                do k = rand() % items.Count; while (left[k] is null);
                o.Add(left[k]!);
                left[k] = null;
            }
        }
        return o;
    }

    // ---- the tier clamp ----

    private sealed record Tier(string Name, int TripMin, int MeleeMin, bool Always);

    /// <summary>Per salvage type: where in the name the entry names are compared and copied, and the entries in table order (each "always" row starts a family).</summary>
    private static readonly (int Offset, Tier[] Entries)[] Tables =
    [
        (7, [new("a", 1, 1, true), new("b", 3, 1, false), new("c", 7, 3, false), new("d", 11, 5, false)]),
        (0, [
            new("gmlight", 1, 1, true), new("gmmedium", 3, 1, false), new("gclight", 5, 2, false), new("gcmedium", 7, 3, false), new("gmheavy", 9, 4, false), new("gcheavy", 12, 6, false), new("gchades", 15, 7, false),
            new("tmlight", 7, 3, true), new("tmmedium", 7, 3, false), new("tclight", 14, 7, false), new("tcmedium", 14, 7, false), new("tmheavy", 9, 4, false), new("tcheavy", 14, 7, false), new("tchades", 15, 7, false),
            new("gdumb", 1, 1, true), new("gsheat", 5, 2, false), new("gsradar", 10, 5, false), new("gscherub", 14, 7, false),
            new("tdumb", 9, 4, true), new("tsheat", 9, 4, false), new("tsradar", 10, 5, false), new("tscherub", 14, 7, false),
            new("gflight", 2, 1, true), new("gfmedium", 4, 2, false), new("gfheavy", 8, 4, false), new("gfpyro", 12, 6, false),
            new("tflight", 14, 7, true), new("tfmedium", 14, 7, false), new("tfheavy", 14, 7, false), new("tfpyro", 14, 7, false),
            new("ggrenade", 2, 1, true), new("gwhiteph", 6, 3, false), new("gcluster", 10, 5, false), new("gezkill", 13, 6, false),
            new("goilslck", 1, 1, true), new("gblox", 4, 2, false), new("glandmin", 9, 4, false), new("gfirdrop", 13, 6, false), new("gceracer", 16, 7, false),
        ]),
        (3, [new("01", 1, 1, true), new("02", 5, 2, false), new("03", 9, 4, false), new("04", 13, 6, false)]),
        (3, [new("01", 1, 1, true), new("02", 4, 2, false), new("03", 8, 4, false), new("04", 12, 6, false)]),
        (3, [new("01", 1, 1, true), new("02", 2, 1, false), new("03", 6, 3, false), new("04", 10, 5, false)]),
        (3, [new("05", 1, 1, true), new("02", 3, 1, false), new("04", 5, 2, false), new("07", 6, 3, false), new("01", 7, 3, false),
             new("06", 8, 4, false), new("09", 10, 5, false), new("03", 11, 5, false), new("08", 13, 6, false)]),
    ];

    /// <summary>
    /// The name an item arrives as, or null if no entry matches (the item is
    /// dropped). On a match at k, walk back to the first entry that is
    /// "always" or allowed at this level for this kind; if it walked back,
    /// that entry's name goes in at the table's offset (for the weapons,
    /// offset 0, it replaces the stem and the extension stays).
    /// </summary>
    public static string? Clamp(string name, int type, int level, int kind)
    {
        var (off, entries) = Tables[type];
        if (name.Length < off) return null;
        int k = Array.FindIndex(entries, e => string.CompareOrdinal(name, off, e.Name, 0, e.Name.Length) == 0);
        if (k < 0) return null;
        int j = k;
        while (j > 0 && !entries[j].Always && !(kind == 2 && entries[j].TripMin <= level) && !(kind == 3 && entries[j].MeleeMin <= level)) j--;
        if (j == k) return name;
        var e = entries[j];
        if (off == 0)
        {
            int dot = name.IndexOf('.');
            return e.Name + (dot < 0 ? "" : name[dot..]);
        }
        return name[..off] + e.Name + (name.Length > off + e.Name.Length ? name[(off + e.Name.Length)..] : "");
    }
}
