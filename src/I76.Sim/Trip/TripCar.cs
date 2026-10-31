using I76.Formats;
using I76.Formats.Bwd2;

namespace I76.Sim.Trip;

/// <summary>
/// The player's car as the trip carries it (the .vcf the shell writes as
/// addon\vehscn.vcf): VCFC's variant name, .vdf,
/// .vtf, engine / suspension / brakes ordinals, front / middle / rear .wdf,
/// armour and chassis (front, left, right, rear — the engine's order) and the
/// ninth value; the three SPEC types (0 for none); the WEPN mounts ("null"
/// for an empty one).
/// </summary>
public sealed class TripCar
{
    public string Variant = "", Vdf = "", Vtf = "";
    public int Engine, Suspension, Brakes;
    public string[] Wdf = ["null", "null", "null"];
    public int[] Armour = new int[4], Chassis = new int[4];
    public int Ninth;
    public int[] Specials = new int[3];
    public List<(int Hardpoint, string Gdf)> Weapons = [];

    /// <summary>
    /// A .vcf as the engine reads it. The engine field is an engsnd.dat ENG
    /// NUM; <paramref name="engineOrdinal"/> maps it to the compnent.cdf
    /// ordinal the garage works in (rows 0..3 are the same; the shell rewrites
    /// special ENG NUMs as an ordinal).
    /// </summary>
    public static TripCar FromVcf(byte[] data, Func<int, int>? engineOrdinal = null)
    {
        var f = new Bwd2File(data);
        var c = f.First("VCFC") ?? throw new InvalidDataException("no VCFC");
        int p = c.PayloadOffset;
        var d = f.Data;
        var car = new TripCar
        {
            Variant = Bin.CStr(d, p, 16), Vdf = Bin.CStr(d, p + 0x10, 13), Vtf = Bin.CStr(d, p + 0x1d, 13),
            Engine = engineOrdinal?.Invoke(Bin.I32(d, p + 0x2a)) ?? Bin.I32(d, p + 0x2a),
            Suspension = Bin.I32(d, p + 0x2e), Brakes = Bin.I32(d, p + 0x32),
            Wdf = [Bin.CStr(d, p + 0x36, 13), Bin.CStr(d, p + 0x43, 13), Bin.CStr(d, p + 0x50, 13)],
            Armour = [.. Enumerable.Range(0, 4).Select(z => Bin.I32(d, p + 0x5d + 4 * z))],
            Chassis = [.. Enumerable.Range(0, 4).Select(z => Bin.I32(d, p + 0x6d + 4 * z))],
            Ninth = c.PayloadSize >= 0x81 ? Bin.I32(d, p + 0x7d) : 0,
        };
        var spec = f.All("SPEC").Select(s => Bin.I32(d, s.PayloadOffset)).Take(3).ToList();
        for (int i = 0; i < spec.Count; i++) car.Specials[i] = spec[i];
        car.Weapons = [.. f.WeaponMounts().Select(w => ((int)w.Slot, w.Gizmo))];      // "null" mounts kept: each is a slot
        return car;
    }

    /// <summary>
    /// The PARTS list of the melee and scenario forms (x 470, y 177 + 17k): the engine, the suspension and the brakes by their compnent.cdf names, then
    /// the car's tyres, at most four rows. The specials are not listed. The tyre rows are the shell's list of the
    /// car's type-5 slots (Nitro Pack), one per wheel position whose pair has a .wdf (front
    /// right, left, middle, back); a slot's tyre is listed when its file name begins like the car's front .wdf (the
    /// first two rows) or like its rear .wdf (later rows; the front's where the rear is empty), 7 characters in the
    /// base shell, 8 in Nitro's. So a stock car lists "13in Stock" four times. The engine is an ordinal here
    /// (<see cref="FromVcf"/> with the engine mapping).
    /// </summary>
    public List<string> PartNames(PartsDatabase db, bool nitro = false)
    {
        var o = new List<string>();
        foreach (var (type, ordinal) in new[] { (2, Engine), (3, Suspension), (4, Brakes) })
            if (db.Parts.FirstOrDefault(p => p.Type == type && !p.IsEmpty && p.Ordinal == ordinal) is { } part) o.Add(part.Id);
        static bool None(string w) => w.Length == 0 || w.Equals("null", StringComparison.OrdinalIgnoreCase);
        int n = nitro ? 8 : 7;
        var rows = new List<string>();
        for (int k = 0; k < 6 && rows.Count < 4; k++)
        {
            string wdf = Wdf[k / 2];
            if (None(wdf)) continue;
            if (db.ByStem(wdf) is not { Type: 5 } tyre) { rows.Add(""); continue; }      // an empty slot lists the blank placeholder
            string want = rows.Count < 2 || None(Wdf[2]) ? Wdf[0] : Wdf[2];
            if (string.Compare(want, 0, tyre.Stem, 0, n, StringComparison.OrdinalIgnoreCase) == 0) rows.Add(tyre.Id);
        }
        o.AddRange(rows);
        return o;
    }

    /// <summary>
    /// The car's weapon mounts as the shell lists them: one per hardpoint of the body (its HLOC records, in
    /// their order), each with the gdf the WEPN for that hardpoint names, "null" where none does. The WEPN handler
    /// files each gdf under its hardpoint, so the file's order of WEPNs does not matter, a hardpoint
    /// without one is an empty mount still offered, and a WEPN for a hardpoint the body lacks is dropped. Without
    /// the body's hardpoints, the WEPNs as they are.
    /// </summary>
    public List<(int Hardpoint, string Gdf)> Mounts(IReadOnlyList<Hardpoint> hardpoints)
    {
        if (hardpoints.Count == 0) return [.. Weapons];
        return [.. hardpoints.Select(h => ((int)h.Id, Weapons.LastOrDefault(w => w.Hardpoint == h.Id && Fitted(w.Gdf)) is { Gdf: { } g } ? g : "null"))];
        // a "null" or blank gdf loads nothing, so it never replaces one an earlier WEPN filed
        static bool Fitted(string gdf) => gdf.Length > 0 && !gdf.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The .vcf the shell writes: BWD2, REV 3,
    /// VCFC, three SPECs always (zeros included), the WEPNs in reverse order,
    /// then EXIT and EXIT, each with the size field 0x10, so the first one's
    /// span takes in the second.
    /// </summary>
    public byte[] ToVcf()
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Chunk(string tag, byte[] payload, int? size = null)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(tag.PadRight(4, '\0')[..4]));
            w.Write(size ?? payload.Length + 8);
            w.Write(payload);
        }
        static void Str(byte[] b, int at, int width, string s)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(s);
            Array.Copy(bytes, 0, b, at, System.Math.Min(bytes.Length, width - (width > 13 ? 0 : 1)));
        }
        Chunk("BWD2", []);
        Chunk("REV", BitConverter.GetBytes(3));
        var v = new byte[0x81];
        Str(v, 0x00, 16, Variant);
        Str(v, 0x10, 13, Vdf);
        Str(v, 0x1d, 13, Vtf);
        BitConverter.TryWriteBytes(v.AsSpan(0x2a), Engine);
        BitConverter.TryWriteBytes(v.AsSpan(0x2e), Suspension);
        BitConverter.TryWriteBytes(v.AsSpan(0x32), Brakes);
        Str(v, 0x36, 13, Wdf[0]);
        Str(v, 0x43, 13, Wdf[1]);
        Str(v, 0x50, 13, Wdf[2]);
        for (int z = 0; z < 4; z++)
        {
            BitConverter.TryWriteBytes(v.AsSpan(0x5d + 4 * z), Armour[z]);
            BitConverter.TryWriteBytes(v.AsSpan(0x6d + 4 * z), Chassis[z]);
        }
        BitConverter.TryWriteBytes(v.AsSpan(0x7d), Ninth);
        Chunk("VCFC", v);
        for (int i = 0; i < 3; i++) Chunk("SPEC", BitConverter.GetBytes(Specials[i]));
        for (int i = Weapons.Count - 1; i >= 0; i--)
        {
            var (hp, gdf) = Weapons[i];
            if (gdf.Length == 0) continue;
            var b = new byte[17];
            BitConverter.TryWriteBytes(b.AsSpan(0), hp);
            Str(b, 4, 13, gdf);
            Chunk("WEPN", b);
        }
        Chunk("EXIT", [], 0x10);
        Chunk("EXIT", [], 0x10);
        return ms.ToArray();
    }
}
