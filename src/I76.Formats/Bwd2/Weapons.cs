namespace I76.Formats.Bwd2;

/// <summary>
/// A hardpoint (HLOC, 80 bytes): the mount
/// a WEPN slot id names. Facing 1 front, 2 back; style 1 P, 2 S (side),
/// 4 D (dropper), 5 I; style 3 (turret) is forced by a turret gizmo (GizmoDef.MountsAsTurret).
/// </summary>
public sealed record Hardpoint(string Name, uint Id, uint Facing, uint Style, Transform Transform)
{
    /// <summary>The file's HLOC records, plus the port's patch for a body that has none (<see cref="VehiclePatches"/>).</summary>
    public static List<Hardpoint> Read(Bwd2File f) =>
        f.All("HLOC").Select(c =>
        {
            int p = c.PayloadOffset;
            return new Hardpoint(Bin.CStr(f.Data, p, 8), Bin.U32(f.Data, p + 0x10), Bin.U32(f.Data, p + 0x14),
                Bin.U32(f.Data, p + 0x18), Bwd2File.ReadTransform(f.Data, p + 0x1c));
        }).Concat(VehiclePatches.ExtraHardpoints(f)).ToList();
}

/// <summary>An ordnance kind (ORDF, 137 bytes; §2.3).</summary>
public sealed record OrdnanceDef(
    uint Kind,
    float TerminalSpeed,   // P+0x04
    float Gravity,         // P+0x08, acceleration (0, −9.8·g, 0)
    uint Mask,             // P+0x0c damage channels: 1 ballistic, 2 explosive, 4 fire, 8 impact
    uint Tracer,           // P+0x10
    string[] ImpactXdf,    // by hit class: terrain, vehicle (1/4/8/9), 2/0xc, 3/0xb
    string[] ImpactWav)
{
    public static OrdnanceDef Read(byte[] d, int p) => new(
        Bin.U32(d, p), Bin.F32(d, p + 4), Bin.F32(d, p + 8), Bin.U32(d, p + 0xc), Bin.U32(d, p + 0x10),
        [Bin.CStr(d, p + 0x21, 13), Bin.CStr(d, p + 0x3b, 13), Bin.CStr(d, p + 0x55, 13), Bin.CStr(d, p + 0x6f, 13)],
        [Bin.CStr(d, p + 0x2e, 13), Bin.CStr(d, p + 0x48, 13), Bin.CStr(d, p + 0x62, 13), Bin.CStr(d, p + 0x7c, 13)]);
}

/// <summary>
/// A weapon gizmo (.gdf: GDFC, GPOF, GGEO, ORDF, OGEO; §1.2–1.3). Class 1
/// handgun, 2 gun/cannon, 3 mortar, 4 missile/rocket, 5 flame, 6 dropper.
/// </summary>
public sealed record GizmoDef(
    string File,
    string Name,
    uint Class,            // P+0x10
    uint Tier,             // P+0x14; turret variants are + 100
    uint Damage,           // P+0x2c (u32)
    uint HitPoints,        // P+0x30 (u32): the weapon's own HP, not range
    float Mass,            // P+0x34, added to the vehicle's
    float Life,            // P+0x46, the round's lifetime (s)
    float Delay,           // P+0x4a, after a burst
    float Rate,            // P+0x4e, shots/s
    uint Burst,            // P+0x52
    float Speed,           // P+0x56, initial speed
    uint Kind,             // P+0x5a = ORDF kind
    uint Ammo,             // P+0x5e
    float Spread,          // P+0x62, degrees
    string MuzzleFlash,    // P+0x66 .xdf
    string FireSound,      // P+0x73 .wav
    uint SoundMode,        // P+0x80
    Transform?[] GunPoints,// GPOF: style 1, 2, 3, 5 blocks; style 4 identity
    List<SceneNode> Geometry,
    OrdnanceDef? Ordnance,
    List<SceneNode> OrdnanceGeometry)
{
    /// <summary>
    /// GDFC +0x84 / +0x94: the cockpit weapons panel's plate sprites (zdue.map),
    /// lit and unlit, with the low-resolution '3' prefix (C2 §3.9).
    /// </summary>
    public string PlateOn { get; init; } = "";
    public string PlateOff { get; init; } = "";

    /// <summary>A turret twin (t*.gdf, tier + 100): what the trip's shop and the car checks call a turret.</summary>
    public bool IsTurret => Tier >= 100;

    /// <summary>
    /// Whether the gizmo mounts as a turret (style 3). The 1998 build (GOG, Nitro) forces it from tier 100, its turret twins'
    /// tiers; the 1997 CD build from tier 10, its twins' tiers then (stored as tier − 10). The renumbering missed the two tank
    /// guns, gtptank and gtktank at tier 15, which turned on the CD and have stood fixed since. The 1997 rule: with the 1998
    /// data it changes only those two (deviations.md).
    /// </summary>
    public bool MountsAsTurret => Tier >= 10;

    /// <summary>The mount style: the hardpoint's, or 3 for a turret gizmo.</summary>
    public uint StyleOn(Hardpoint h) => MountsAsTurret ? 3 : h.Style;

    /// <summary>GPOF block for a style: 1 → 0, 2 → 1, 3 → 2, 5 → 3; 4 (dropper) has none (identity).</summary>
    public Transform? GunPoint(uint style) => style switch { 1 => GunPoints[0], 2 => GunPoints[1], 3 => GunPoints[2], 5 => GunPoints[3], _ => null };

    /// <summary>
    /// A turret gizmo's gun node origin in gizmo space: the T style's GN at detail 1, composed through its parents (the
    /// base, TU). The engine builds the gizmo's tree from the detail-1 records, its gun under its base;
    /// The original then turns the gun about this point and leaves the base. Null without one.
    /// </summary>
    public System.Numerics.Vector3? TurretGun()
    {
        var byName = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Geometry) byName.TryAdd(n.Name, n);
        if (Geometry.FirstOrDefault(n => n.Name.Length >= 8 && char.ToUpperInvariant(n.Name[3]) == 'T' && n.Name[4] == '1' && n.Name[5] == '1'
                && n.Name[6..8].Equals("GN", StringComparison.OrdinalIgnoreCase)) is not { } gn) return null;
        var place = Assembly.Affine.From(gn.Transform);
        var at = gn;
        for (int depth = 0; depth < 8 && !at.IsRoot && byName.TryGetValue(at.Parent, out var p) && !ReferenceEquals(p, at); depth++)
        {
            place = Assembly.Affine.From(p.Transform).Compose(place);
            at = p;
        }
        return place.T;
    }

    /// <summary>The style's letter in GGEO node names (&lt;code3&gt;&lt;letter&gt;&lt;detail&gt;&lt;condition&gt;&lt;part&gt;).</summary>
    public static char StyleLetter(uint style) => style switch { 1 => 'P', 2 => 'S', 3 => 'T', 5 => 'I', _ => '?' };

    public static GizmoDef? Read(string file, byte[] data)
    {
        var f = new Bwd2File(data);
        if (f.First("GDFC") is not { } g) return null;
        var d = f.Data;
        int p = g.PayloadOffset;
        var points = new Transform?[4];
        if (f.First("GPOF") is { } gp)
            for (int i = 0; i < 4 && gp.PayloadOffset + (i + 1) * 48 <= gp.Offset + gp.Size; i++)
                points[i] = Bwd2File.ReadTransform(d, gp.PayloadOffset + i * 48);
        var nodes = f.SceneNodes();
        OrdnanceDef? ord = f.First("ORDF") is { } o ? OrdnanceDef.Read(d, o.PayloadOffset) : null;
        return new GizmoDef(file, Bin.CStr(d, p, 16), Bin.U32(d, p + 0x10), Bin.U32(d, p + 0x14),
            Bin.U32(d, p + 0x2c), Bin.U32(d, p + 0x30), Bin.F32(d, p + 0x34), Bin.F32(d, p + 0x46), Bin.F32(d, p + 0x4a),
            Bin.F32(d, p + 0x4e), Bin.U32(d, p + 0x52), Bin.F32(d, p + 0x56), Bin.U32(d, p + 0x5a), Bin.U32(d, p + 0x5e),
            Bin.F32(d, p + 0x62), Bin.CStr(d, p + 0x66, 13), Bin.CStr(d, p + 0x73, 13), Bin.U32(d, p + 0x80), points,
            nodes.Where(t => t.Tag == "GGEO").SelectMany(t => t.Nodes).ToList(), ord,
            nodes.Where(t => t.Tag == "OGEO").SelectMany(t => t.Nodes).ToList())
        {
            PlateOn = g.PayloadSize >= 0x94 ? Bin.CStr(d, p + 0x84, 16) : "",
            PlateOff = g.PayloadSize >= 0xa4 ? Bin.CStr(d, p + 0x94, 16) : "",
        };
    }
}

/// <summary>An effect (.xdf XDFC, 40 bytes; §2.3): sprite frames, lifetime, splash damage and radius.</summary>
public sealed record EffectDef(string File, uint Frames, float Life, float SplashDamage, float SplashRadius, List<SceneNode> Geometry)
{
    public static EffectDef? Read(string file, byte[] data)
    {
        var f = new Bwd2File(data);
        if (f.First("XDFC") is not { } c) return null;
        int p = c.PayloadOffset;
        return new EffectDef(file, Bin.U32(f.Data, p), Bin.F32(f.Data, p + 0x18), Bin.F32(f.Data, p + 0x1c), Bin.F32(f.Data, p + 0x20),
            f.SceneNodes().Where(t => t.Tag == "XGEO").SelectMany(t => t.Nodes).ToList());
    }
}
