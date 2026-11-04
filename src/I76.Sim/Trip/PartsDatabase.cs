using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// One part the garage can fit (the 0x54-byte record):
/// its display name (what the car's slots are matched by), type
/// (2 engine, 3 suspension, 4 brakes, 5 tyre, 7 weapon, 8 dropper,
/// 9 handgun, 13 special, 14 paint), subtype (0xb turret, 10
/// handgun), component code, file stem / salvage key, the
/// ordinal of a component, max HP in file units and weight; for a weapon also its GDFC class and tier (what WEAPON RESTRICTIONS matches).
/// </summary>
public sealed record PartDef(string Id, int Type, int Subtype, string Code, string Stem, int Ordinal, int MaxHp, float Weight, int Class = 0, int Tier = 0)
{
    public bool IsEmpty => Id == "EMPTY";
    public bool IsTurret => Subtype == 0xb;
}

/// <summary>
/// The parts database the engine builds for the shell (callback [0]), in its order: an EMPTY placeholder per slot type, every
/// .wdf, every .gdf, every .vtf, the four engines, brakes and suspensions of
/// compnent.cdf (named from its NTBL/BTBL/STBL tables), and the nine
/// specials.
/// </summary>
public sealed class PartsDatabase
{
    public List<PartDef> Parts { get; } = [];

    /// <summary>
    /// The specials' names (the string table keys), for spc01..spc11. The base shell's catalogue stops at the ninth;
    /// the Nitro Pack's loop has eleven, adding Dashboard Mary and Tire Covers (types 10 and 11: in the Nitro Pack, damage
    /// per shot × 1.1 and wheel HP × 2 at load, see <see cref="Vehicles.Specials.AtLoad"/>; the base game's engine does
    /// nothing with them).
    /// </summary>
    public static readonly string[] SpecialNames =
        ["Radar Jammer", "NitrousOxide", "Blower", "X-Aust Brake", "Structo Bmpr", "Curb Feelers", "Mud Flaps", "Heated Seats", "Cup Holders", "Dashboard Mary", "Tire Covers"];

    /// <summary>How many specials a shell's catalogue has.</summary>
    public static int SpecialCount(bool nitro) => nitro ? 11 : 9;

    /// <summary>The repair cost by part type.</summary>
    public static readonly int[] RepairCost = [0, 1, 4, 3, 2, 1, 2, 2, 2, 1, 1, 2, 5, 5, 0];

    public static PartsDatabase Build(AssetVfs vfs)
    {
        var db = new PartsDatabase();
        foreach (int t in new[] { 2, 3, 4, 5, 7, 8, 9, 10, 11, 13 }) db.Parts.Add(new PartDef("EMPTY", t, 0, "", "", 0, 1, 0));
        var names = vfs.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names.Where(n => n.EndsWith(".wdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (vfs.TryRead(n) is not { } d || new Bwd2File(d).First("WDFC") is not { } c) continue;
            int p = c.PayloadOffset;
            db.Parts.Add(new PartDef(Bin.CStr(d, p, 16), 5, 0, Bin.CStr(d, p + 0x34, 13), n.ToLowerInvariant(), 0, Bin.I32(d, p + 0x28), 2 * Bin.F32(d, p + 0x2c)));
        }
        foreach (var n in names.Where(n => n.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (vfs.TryRead(n) is not { } d || new Bwd2File(d).First("GDFC") is not { } c) continue;
            int p = c.PayloadOffset;
            uint cls = Bin.U32(d, p + 0x10), tier = Bin.U32(d, p + 0x14);
            (int type, int sub) = cls switch { 1 => (9, 10), 2 or 3 or 4 or 5 => (7, tier >= 100 ? 0xb : 0), 6 => (8, 0), _ => (0, 0) };
            if (type == 0) continue;          // the engine stops with "ERROR weapon has no class"
            db.Parts.Add(new PartDef(Bin.CStr(d, p, 16), type, sub, Bin.CStr(d, p + 0x38, 13), n.ToLowerInvariant(), 0, Bin.I32(d, p + 0x30), Bin.F32(d, p + 0x34), (int)cls, (int)tier));
        }
        foreach (var n in names.Where(n => n.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)))
        {
            if (vfs.TryRead(n) is not { } d || new Bwd2File(d).First("VTFC") is not { } c) continue;
            db.Parts.Add(new PartDef(Bin.CStr(d, c.PayloadOffset + 0x0d, 16), 14, 0, "", n.ToLowerInvariant(), 0, 1, 0));
        }
        if (vfs.TryRead("compnent.cdf") is { } cd)
        {
            var cdf = new Bwd2File(cd);
            List<string> Table(string tag) => cdf.First(tag) is { } t
                ? [.. Enumerable.Range(0, Bin.I32(cd, t.PayloadOffset)).Select(i => Bin.CStr(cd, t.PayloadOffset + 4 + 16 * i, 16))]
                : [];
            // ENGN/BRAK: HP, two floats (the second the mass), the code; SUSP has a third float (the mass) before it
            void Components(string chunk, string table, int type, int massAt, int codeAt)
            {
                var label = Table(table);
                var all = cdf.All(chunk).ToList();
                for (int i = 0; i < all.Count && i < 4; i++)
                {
                    int p = all[i].PayloadOffset;
                    var code = Bin.CStr(cd, p + codeAt, 13);
                    db.Parts.Add(new PartDef(i < label.Count ? label[i] : code, type, 0, code, code, i, Bin.I32(cd, p), Bin.F32(cd, p + massAt)));
                }
            }
            Components("ENGN", "NTBL", 2, 0x08, 0x0c);
            Components("BRAK", "BTBL", 4, 0x08, 0x0c);
            Components("SUSP", "STBL", 3, 0x0c, 0x10);
        }
        for (int i = 0; i < SpecialCount(vfs.Install.IsNitro); i++) db.Parts.Add(new PartDef(SpecialNames[i], 13, 0, $"spc{i + 1:00}", $"spc{i + 1:00}", i, 0, 0));
        return db;
    }

    /// <summary>The part whose file stem is this salvage key or file name.</summary>
    public PartDef? ByStem(string stem) => Parts.FirstOrDefault(p => !p.IsEmpty && p.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase));

    /// <summary>The part a car slot names (slots are matched by display name).</summary>
    public PartDef? ById(string id, int type) => Parts.FirstOrDefault(p => p.Type == type && p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public PartDef Empty(int type) => Parts.First(p => p.IsEmpty && p.Type == type);

    /// <summary>The repair severity of a part at a health: 1 at two thirds or more, 2 at a third or more, 3 below.</summary>
    public static int Severity(int health, int max) => max <= 0 || health * 3 >= max * 2 ? 1 : health * 3 >= max ? 2 : 3;
}
