using System.Numerics;
using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Sim.Vehicles;

/// <summary>
/// Builds a Vehicle from its definition files, as the original's loaders and
/// post-load do. Every value's source is in
///
/// </summary>
public static class VehicleFactory
{
    /// <summary>A compnent.cdf record: A, B and C are its three values, Hp the hit points.</summary>
    public sealed record Component(float A, float B, float C, int Hp);

    /// <summary>
    /// The driver's handguns, one per arm the cockpit has: the car's interior model (detail 5) with a
    /// GUNR part has the right arm, GUNL the left. The game fits them to the player's car when it makes the cockpit;
    /// this is the same rule from the files, for a network melee's cars (the server has no cockpit node).
    /// </summary>
    public static void AddDriverHandguns(AssetVfs vfs, string vcfName, Vehicle v)
    {
        if (v.IsEmplacement) return;
        var interior = new I76.Formats.Assembly.ModelAssembler(vfs) { Detail = '5' }.Assemble(vcfName);
        if (interior is null || vfs.TryRead(vcfName) is not { } vcfBytes || new Bwd2File(vcfBytes).First("VCFC") is not { } vcc
            || vfs.TryRead(Bin.CStr(vcfBytes, vcc.PayloadOffset + 0x10, 13)) is not { } vdfBytes) return;
        bool Has(string code) => interior.Parts.Any(p => p.NodeName.Length >= 8 && p.NodeName[4..8].Equals(code, StringComparison.OrdinalIgnoreCase));
        bool right = Has("GUNR"), left = Has("GUNL");
        if (!right && !left) return;
        v.Armory ??= new Weapons.Armory();
        v.Armory.AddHandguns(new Bwd2File(vdfBytes), right, left);
    }

    /// <summary>
    /// Builds the car of <paramref name="vcfName"/>. With <paramref name="bodySound"/> (a car the shell gave: the player's
    /// chosen car, a melee's cars), a file whose engine is a plain engine part (ENG NUM 0..3, as every shell-saved variant
    /// is) takes its sounds from its body's own engsnd.dat row instead (<see cref="BodySoundRow"/>): the Moth trucks, the
    /// buses and the tractor keep eimarx.wav and vhorn5.wav, the police car its siren, the tank, helicopters, balloon and
    /// UFO their loops. The engine part still sets the power. The original's shell wrote the part's ordinal into the car it
    /// launched (a stock Sheriff left as ENG NUM 3), so the player's own truck
    /// sounded like a V-10 and the police car had no siren (docs/deviations.md).
    /// </summary>
    /// <param name="meleeBrakes">An Auto Melee or a network game: the brake is scaled by the car's BRAK part (1.0 / 1.3 / 1.6 / 2.0). A mission ignores the part.</param>
    /// <param name="specials">False for an Auto Melee's opponents: the game writes their cars with three empty SPEC slots,
    /// so they have no specials and none of the specials' load-time effects (the blower among them).</param>
    public static Vehicle Create(AssetVfs vfs, string vcfName, bool meleeBrakes = false, bool bodySound = false, bool specials = true)
    {
        var vcf = new Bwd2File(vfs.Read(vcfName));
        var vcfc = vcf.First("VCFC") ?? throw new InvalidDataException($"{vcfName}: no VCFC");
        int p = vcfc.PayloadOffset;
        byte[] d = vcf.Data;
        string vdfName = Bin.CStr(d, p + 0x10, 13);
        string[] wdfNames = [.. Enumerable.Range(0, 3).Select(a => Bin.CStr(d, p + 0x36 + 13 * a, 13))];
        int engNum = Bin.I32(d, p + 0x2a), suspId = Bin.I32(d, p + 0x2e), brakeId = Bin.I32(d, p + 0x32);

        int soundNum = bodySound && engNum is >= 0 and <= 3 && BodySoundRow(vfs, vdfName) is { } bodyRow ? bodyRow : engNum;
        var v = new Vehicle { Definition = vcfName, EngineNumber = soundNum, Ignition = IgnitionFor(vfs, soundNum), HornLoops = HornLoops(vfs, soundNum) };
        var vdf = new Bwd2File(vfs.Read(vdfName));
        var vdfc = vdf.First("VDFC")!.Value;
        v.ObjectClass = Bin.U32(vdf.Data, vdfc.PayloadOffset + 0x14);   // the VDFC chunk reader
        float mass = Bin.F32(vdf.Data, vdfc.PayloadOffset + 0x30);
        v.P.Drag = Bin.F32(vdf.Data, vdfc.PayloadOffset + 0x38);

        // COLP: three descending lists of four (Z, X, Y) -> an ascending box
        if (vdf.First("COLP") is { } colp)
        {
            float C(int i) => Bin.F32(vdf.Data, colp.PayloadOffset + 4 * i);
            v.BoxZ = [C(3), C(2), C(1), C(0)];
            v.BoxX = [C(7), C(6), C(5), C(4)];
            v.BoxY = [C(11), C(10), C(9), C(8)];
            v.P.RideOffset = v.BoxY[3];          // the roof, for the ground query when upside down
        }

        // wheels: WLOC slot k → the wheel object (0 FR, 1 FL, 2/3 middle, 4 RR, 5 RL)
        var wloc = vdf.Locations().Where(l => l.Tag == "WLOC").Select(l => new Vector3(l.Transform.Translation[0], l.Transform.Translation[1], l.Transform.Translation[2])).ToArray();
        // the middle pair exists when its .wdf is named (its wheel is set); "null" is no middle axle
        bool middle = wdfNames[1].Length > 0 && !wdfNames[1].Equals("null", StringComparison.OrdinalIgnoreCase) && vfs.TryRead(wdfNames[1]) is not null && wloc.Length >= 4;
        if (middle) { v.MiddleRight = new Wheel(); v.MiddleLeft = new Wheel(); }
        Wheel?[] slots = [v.FrontRight, v.FrontLeft, v.MiddleRight, v.MiddleLeft, v.RearRight, v.RearLeft];
        for (int k = 0; k < 6 && k < wloc.Length; k++)
            if (slots[k] is { } w) { w.Offset = wloc[k]; w.Rolling = 1f; w.Grip = 1f; }

        // wheel files: kind 0 → slots 0/1, kind 2 → 2/3, kind 4 → 4/5. The WDFC grip mass per wheel HP.
        int[] wheelHp = [100, 100, 100];
        for (int kind = 0; kind < 3; kind++)
        {
            if (wdfNames[kind].Length == 0 || wdfNames[kind].Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            if (vfs.TryRead(wdfNames[kind]) is not { } wd) continue;
            var wdf = new Bwd2File(wd);
            if (wdf.First("WDFC") is not { } wc) continue;
            float grip = Bin.F32(wdf.Data, wc.PayloadOffset + 0x30);
            wheelHp[kind] = Bin.I32(wdf.Data, wc.PayloadOffset + 0x28);
            mass += 2 * Bin.F32(wdf.Data, wc.PayloadOffset + 0x2c);
            if (slots[kind * 2] is { } a) a.Grip = grip;
            if (slots[kind * 2 + 1] is { } b) b.Grip = grip;
        }

        // compnent.cdf: ENGN by engsnd.dat's ENG COMP ID; SUSP and BRAK by ordinal.
        var (engns, braks, susps) = ReadComponents(vfs);
        int engId = EngineCompId(vfs, engNum);
        var eng = engns.ElementAtOrDefault(engId) ?? new Component(300000f, 0, 0, 300);
        var brak = braks.ElementAtOrDefault(brakeId) ?? new Component(1, 0, 0, 150);
        var susp = susps.ElementAtOrDefault(suspId) ?? new Component(1, 0, 0, 200);
        mass += eng.B + brak.B + susp.C;   // the ENGN, BRAK and SUSP records carry masses

        // WEPN: each weapon mounted on its hardpoint adds its GDFC mass
        var armory = Weapons.Armory.Create(vfs, vcf, vdf);
        mass += armory.WeaponMass;
        if (armory.Weapons.Count > 0) v.Armory = armory;

        v.P.Mass = mass;
        v.P.DriveScale = 1f / mass;
        v.P.BrakeStrength = 2300f * v.P.DriveScale * (meleeBrakes ? brak.A : 1f);   // brake = 2300/mass, times the BRAK part in a melee or net game
        v.P.ChassisGrip = susp.A;                       // the cornering factor
        v.Engine.PeakForce = eng.A;
        v.Engine.Curve = eng.A * 8.1632656e-8f;         // = A/3500²

        // post-load geometry
        float wheelbase = Vector3.Distance(v.FrontLeft.Offset, v.RearLeft.Offset);
        float track = Vector3.Distance(v.FrontLeft.Offset, v.FrontRight.Offset);
        if (wheelbase < 1e-3f) wheelbase = 2.74f;
        if (track < 1e-3f) track = 1.37f;
        v.P.Wheelbase = wheelbase;
        v.P.Track = track;
        v.P.SteerGain = 1f / (2f * wheelbase);
        v.P.YawRateLimit = 2.65618f / wheelbase;
        v.P.PivotForward = v.RearLeft.Offset.Z;
        v.Flags |= VehicleFlags.EngineOn | VehicleFlags.CommandedYaw;   // |= 0x1001
        foreach (var w in v.Wheels.Concat(v.MiddleWheels)) w.InitSuspension();
        // damage: The VCFC armour and chassis reinforcement (front, left, right, rear), component HP, specials
        var spec = specials ? vcf.All("SPEC").Select(c => (int)Bin.U32(vcf.Data, c.PayloadOffset)).Take(3).ToList() : [];
        // the specials that act once, at the post-load: the blower, Heated Seats, Nitro's Dashboard Mary and Tire Covers
        Specials.AtLoad(v, spec, vfs.Install.IsNitro, wheelHp);
        v.Build = new Damage.VehicleBuild(
            [.. Enumerable.Range(0, 4).Select(z => Bin.I32(d, p + 0x5d + 4 * z))],
            [.. Enumerable.Range(0, 4).Select(z => Bin.I32(d, p + 0x6d + 4 * z))],
            wheelHp, eng.Hp, brak.Hp, susp.Hp, (100f - susp.B) * 0.01f, spec);
        v.Loadout = new Loadout(wdfNames, engId, suspId, brakeId, [.. spec]);
        return v;
    }

    public static (List<Component> Engines, List<Component> Brakes, List<Component> Suspensions) ReadComponents(AssetVfs vfs)
    {
        var cdf = new Bwd2File(vfs.Read("compnent.cdf"));
        Component R(Chunk c, int a, int b, int cc) =>
            new(Bin.F32(cdf.Data, c.PayloadOffset + a), Bin.F32(cdf.Data, c.PayloadOffset + b), cc < 0 ? 0 : Bin.F32(cdf.Data, c.PayloadOffset + cc), Bin.I32(cdf.Data, c.PayloadOffset));
        return (cdf.All("ENGN").Select(c => R(c, 4, 8, -1)).ToList(),
                cdf.All("BRAK").Select(c => R(c, 4, 8, -1)).ToList(),
                cdf.All("SUSP").Select(c => R(c, 4, 8, 0xc)).ToList());
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AssetVfs, Dictionary<string, int>> BodyRows = new();

    /// <summary>
    /// The engsnd.dat row a body's own cars carry when it is not a plain engine part (0..3): the ENG NUM of the install's
    /// model variants (<c>vxmilk1.vcf</c>, <c>vcpolic2.vcf</c>: a model stem and its number) that use the .vdf. 5 for the
    /// Moth trucks, buses and tractor, 10 the police car, 4 the tank, 20 the helicopters, 41 the Phantom, 50 the balloon, 51
    /// the UFO; null for a car body (its variants carry engine parts). Read from the archives (ZFS, PAK) only.
    /// </summary>
    public static int? BodySoundRow(AssetVfs vfs, string vdfName)
    {
        Dictionary<string, int> rows;
        lock (BodyRows)
            if (!BodyRows.TryGetValue(vfs, out rows!))
            {
                rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in vfs.Names.Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^v[a-z]+[1-9]\.vcf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    // the archives' own cars only: a loose or saved variant (the player's addon folder, the shell's) differs
                    // between machines, and the row also sets the ignition time
                    if (vfs.Locate(name) is not (AssetSource.Zfs or AssetSource.Pak) || vfs.TryRead(name) is not { } bytes) continue;
                    try
                    {
                        var f = new Bwd2File(bytes);
                        if (f.First("VCFC") is not { } c || c.PayloadSize < 0x2e) continue;
                        int eng = Bin.I32(bytes, c.PayloadOffset + 0x2a);
                        if (eng is < 0 or > 3) rows.TryAdd(Bin.CStr(bytes, c.PayloadOffset + 0x10, 13), eng);
                    }
                    catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                }
                BodyRows.Add(vfs, rows);
            }
        return rows.TryGetValue(vdfName, out var r) ? r : null;
    }

    /// <summary>engsnd.dat's ENGINE SND FILE for an ENG NUM (column 4).</summary>
    public static string? EngineSound(AssetVfs vfs, int engNum)
    {
        if (vfs.TryRead("engsnd.dat") is not { } d) return null;
        foreach (var line in Bin.Latin1(d).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var f = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 4 && int.TryParse(f[0], out var num) && num == engNum) return f[3].Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : f[3];
        }
        return null;
    }

    /// <summary>engsnd.dat's REVABLE (column 2) for an ENG NUM: a loop that is not revable (helicopters, balloon, tank) plays at its own rate, never re-pitched (the re-pitching is installed only for revable rows).</summary>
    public static bool EngineRevable(AssetVfs vfs, int engNum)
    {
        if (vfs.TryRead("engsnd.dat") is not { } d) return true;
        foreach (var line in Bin.Latin1(d).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var f = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 3 && int.TryParse(f[0], out var num) && num == engNum && int.TryParse(f[2], out var rev)) return rev != 0;
        }
        return true;
    }

    /// <summary>engsnd.dat's IGN NORM / DMG1 / DMG2 sounds and times for an ENG NUM (columns 6..11).</summary>
    public static IgnitionRow? IgnitionFor(AssetVfs vfs, int engNum)
    {
        if (vfs.TryRead("engsnd.dat") is not { } d) return null;
        foreach (var line in Bin.Latin1(d).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var f = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 11 || !int.TryParse(f[0], out var num) || num != engNum) continue;
            string? S(int i) => f[i].Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : f[i];
            float T(int i) => float.TryParse(f[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
            return new IgnitionRow([S(5), S(7), S(9)], [T(6), T(8), T(10)]);
        }
        return null;
    }

    /// <summary>engsnd.dat's HORN SND FILE for an ENG NUM (column 5): the stuck horn of a car whose driver was shot.</summary>
    public static string? HornSound(AssetVfs vfs, int engNum)
    {
        if (vfs.TryRead("engsnd.dat") is not { } d) return null;
        foreach (var line in Bin.Latin1(d).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var f = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 5 && int.TryParse(f[0], out var num) && num == engNum) return f[4].Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : f[4];
        }
        return null;
    }

    /// <summary>
    /// setSiren's sound, on whatever car the script names: the police siren, which loops (its GAS0 mode 1) until setSiren 0.
    /// The 1997 CD build plays it; the 1998 build plays the car's own horn file instead, the siren only for engsnd.dat row 10,
    /// so the trip's and the scenarios' own police variants, saved with a plain engine row, lost theirs (docs/deviations.md).
    /// </summary>
    public const string SirenSound = "aps2.wav";

    /// <summary>Whether the horn file of an ENG NUM loops when started with no voice settings (its GAS0 mode): the police car's aps2.wav does.</summary>
    public static bool HornLoops(AssetVfs vfs, int engNum) => HornSound(vfs, engNum) is { } horn && I76.Formats.Audio.SoundFiles.Loops(vfs, horn);

    /// <summary>engsnd.dat: "ENG NUM, ENG COMP ID, ..." per line.</summary>
    public static int EngineCompId(AssetVfs vfs, int engNum)
    {
        if (vfs.TryRead("engsnd.dat") is not { } d) return 0;
        foreach (var line in Bin.Latin1(d).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var f = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && int.TryParse(f[0], out var num) && num == engNum && int.TryParse(f[1], out var id)) return id;
        }
        return 0;
    }
}
