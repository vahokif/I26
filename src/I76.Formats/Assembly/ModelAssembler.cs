using I76.Formats.Bwd2;
using I76.Formats.Geo;
using I76.Formats.Vfs;

namespace I76.Formats.Assembly;

/// <summary>One mesh of an assembled model, already placed in model space.</summary>
public sealed record ModelPart(string NodeName, GeoMesh Mesh, Affine ToModel);

public sealed class AssembledModel
{
    public required string Definition { get; init; }
    public required string Kind { get; init; } // "sdf" or "vcf"
    public List<ModelPart> Parts { get; } = [];
    /// <summary>Scene-graph nodes whose .GEO is not in the data.</summary>
    public List<string> MissingGeo { get; } = [];
    /// <summary>For vehicles: the texture set's .TMT names, which faces' slots resolve against.</summary>
    public List<string> TmtNames { get; } = [];
    /// <summary>For vehicles: the .map names the texture set lists directly (the body texture among them).</summary>
    public List<string> VtfMaps { get; } = [];
    public string? ObjectCode { get; init; }
    public List<Location> Locations { get; } = [];
    /// <summary>For vehicles: each mounted weapon (WEPN slot), its gizmo and the hardpoint it sits on.</summary>
    public List<(uint Slot, GizmoDef Gizmo, Hardpoint Hardpoint)> Gizmos { get; } = [];
    /// <summary>For vehicles: each turret's gun, by weapon (the index in Gizmos), with its pivot in model space (the base is a fixed part of the gizmo).</summary>
    public List<TurretPart> Turrets { get; } = [];
    /// <summary>For vehicles: which weapon (the index in Gizmos) each of the gizmos' parts belongs to; by reference, as two of a gun share their names.</summary>
    public Dictionary<ModelPart, int> GizmoOf { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>For vehicles: each WLOC slot's wheel mesh from its .wdf WGEO (right wheels RWL, left LWL), intact and blown ('A').</summary>
    public Dictionary<int, (GeoMesh Intact, GeoMesh? Blown, Affine Node)> WheelGeos { get; } = [];
}

/// <summary>A turret's moving part, the gun (GN): it yaws about the mount's up through its own origin and pitches about its own x; the base (TU) stays.</summary>
public sealed record TurretPart(int Weapon, string Node, System.Numerics.Vector3 Pivot);

/// <summary>
/// Builds a definition's scene graph into placed meshes, following
/// the reference GEO/OBJ assembler:
/// <list type="bullet">
/// <item>the first (name, parent) occurrence of a node wins; later duplicates are dropped;</item>
/// <item>transforms compose up the parent chain to WORLD, parents found by name (first occurrence);</item>
/// <item>vehicle node names are &lt;obj&gt;&lt;detail&gt;&lt;condition&gt;&lt;part&gt;: detail 1 and condition 1 are the
///   near, intact car (detail 5 is the cockpit);</item>
/// <item>scenery node names are &lt;obj&gt;&lt;detail&gt;&lt;'_' or 'A'&gt;&lt;part&gt;. Here detail 1 and '_' are taken
///   where an 'A' twin exists; reading 'A' as the destroyed alternative is ours, from the names
///   alone, and not yet checked against the engine.</item>
/// </list>
/// </summary>
public sealed class ModelAssembler(AssetVfs vfs)
{
    public AssetVfs Vfs => vfs;
    public char Detail { get; init; } = '1';
    public char Condition { get; init; } = '1';
    /// <summary>
    /// Vehicles: the condition wanted per part code (name[4..8]); each part
    /// takes its highest condition digit not above it (the original picks per
    /// zone). Null means Condition for every part.
    /// </summary>
    public Func<string, char>? PartCondition { get; init; }
    /// <summary>Scenery: prefer the 'A' alternative parts (read as the destroyed state).</summary>
    public bool Destroyed { get; init; }
    /// <summary>
    /// Scenery, node by node (on the dead ones only):
    /// whether the node a '_' part is drawn for is dead. A dead node's part
    /// gives way to its 'A' twin, or to nothing; the rest are drawn intact.
    /// Wins over <see cref="Destroyed"/>.
    /// </summary>
    public Func<string, bool>? DestroyedNode { get; init; }
    /// <summary>
    /// Vehicles: the dead car (every zone set to state 4):
    /// every body zone node and its subtree take their state-3 geometry slot
    /// for slot (<see cref="VehicleStates"/>). On every shipped car that is
    /// the burnt hull xx14BDYM alone (the bus keeps its front too); the
    /// panels, wheel wells, mirrors, pipes, sirens of states 0-2 are gone.
    /// Nodes outside the zones (the lamps, a truck's chassis) are picked as
    /// without it.
    /// </summary>
    public bool Wrecked { get; init; }
    /// <summary>
    /// With <see cref="Wrecked"/>, the modern look's wreck: instead of the state-3 hull, each body zone (BDYF/BDYM/BDYB/BDYT)
    /// draws its worst state below 3 that exists (2, then 1, then 0) and nothing hangs from it: the car's own shape as a bare
    /// shell, no mirrors, sirens or pipes. The 1997 look keeps the original's box.
    /// </summary>
    public bool WreckShape { get; init; }
    /// <summary>
    /// Part codes left out of vehicles. BLGT and TLGT are two states of one
    /// lamp 8 mm apart, so drawing both z-fights; the reference assembler keeps TLGT.
    /// </summary>
    public HashSet<string> SkipParts { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "BLGT" };

    private readonly Dictionary<string, GeoMesh?> _geo = new(StringComparer.OrdinalIgnoreCase);

    public GeoMesh? Geo(string nodeName)
    {
        lock (_geo)
        {
            if (_geo.TryGetValue(nodeName, out var g)) return g;
            var d = vfs.TryRead(nodeName + ".GEO");
            try { g = d is null ? null : GeoMesh.Parse(d); }
            catch (InvalidDataException) { g = null; }
            _geo[nodeName] = g;
            return g;
        }
    }

    /// <summary>Resolve an ODEF name to its definition: scenery first, then vehicle config.</summary>
    public string? ResolveDefinition(string name)
    {
        foreach (var ext in new[] { ".sdf", ".vcf" })
            if (vfs.Exists(name + ext)) return name + ext;
        return null;
    }

    public AssembledModel? Assemble(string definition)
    {
        var data = vfs.TryRead(definition);
        if (data is null) return null;
        var def = new Bwd2File(data);
        return Bwd2File.Ext(definition) switch
        {
            "sdf" => AssembleTree(definition, "sdf", def, "SGEO", null, null),
            "vcf" => AssembleVehicle(definition, def),
            "vdf" => AssembleTree(definition, "vcf", def, "VGEO", VehicleVariantOk, null),
            _ => null,
        };
    }

    private AssembledModel? AssembleVehicle(string definition, Bwd2File vcf)
    {
        if (vcf.VcfcFields() is not { } f) return null;
        var vdfData = vfs.TryRead(f.Vdf);
        if (vdfData is null) return null;
        var vdf = new Bwd2File(vdfData);
        var tmts = new List<string>();
        var maps = new List<string>();
        if (vfs.TryRead(f.Vtf) is { } vtfData)
        {
            var vtf = new Bwd2File(vtfData);
            tmts.AddRange(VtfTmtNames(vtf));
            maps.AddRange(vtf.References().Where(r => r.EndsWith(".map")).Order(StringComparer.Ordinal));
        }
        var m = AssembleTree(definition, "vcf", vdf, "VGEO", VehicleVariantOk, tmts);
        m.VtfMaps.AddRange(maps);
        m.Locations.AddRange(vdf.Locations());
        AddGizmos(m, vcf, vdf);
        AddWheels(m, vcf);
        return m;
    }

    /// <summary>
    /// The wheels: VCFC names a .wdf per axle (+0x36 front, +0x43 middle,
    /// +0x50 rear); its WGEO holds &lt;code&gt;&lt;detail&gt;&lt;condition&gt;RWL&lt;n&gt; and LWL
    /// meshes for the right (slots 0, 2, 4) and left (1, 3, 5) wheels.
    /// Condition 1 is the tyre, 'A' the blown one.
    /// </summary>
    private void AddWheels(AssembledModel m, Bwd2File vcf)
    {
        if (vcf.First("VCFC") is not { } c) return;
        for (int axle = 0; axle < 3; axle++)
        {
            var name = Bin.CStr(vcf.Data, c.PayloadOffset + 0x36 + 13 * axle, 13);
            if (name.Length == 0 || name.Equals("null", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(name) is not { } d) continue;
            var nodes = new Bwd2File(d).SceneNodes().Where(t => t.Tag == "WGEO").SelectMany(t => t.Nodes).ToList();
            foreach (var (slot, side) in new[] { (axle * 2, "RWL"), (axle * 2 + 1, "LWL") })
            {
                SceneNode? Find(char cond) => nodes.FirstOrDefault(n => n.Name.Length >= 7 && n.Name[2] == Detail && n.Name[3] == cond && n.Name[4..7].Equals(side, StringComparison.OrdinalIgnoreCase));
                if (Find('1') is not { } node || Geo(node.Name) is not { } geo) continue;
                var blown = Find('A') is { } bn ? Geo(bn.Name) : null;
                m.WheelGeos[slot] = (geo, blown, Affine.From(node.Transform));
            }
        }
    }

    /// <summary>
    /// The weapons on their hardpoints (WEPN → HLOC by slot id): the gizmo's
    /// GGEO nodes for the mount style's letter at this detail, condition 1,
    /// composed within the gizmo and placed by the hardpoint. Side mounts on
    /// the far side are mirrored in the original; not here yet.
    /// </summary>
    private void AddGizmos(AssembledModel m, Bwd2File vcf, Bwd2File vdf)
    {
        var hps = Hardpoint.Read(vdf);
        foreach (var mount in vcf.WeaponMounts().Where(w => !w.Empty))
        {
            if (hps.FirstOrDefault(h => h.Id == mount.Slot) is not { } hp) continue;
            if (vfs.TryRead(mount.Gizmo) is not { } gd || GizmoDef.Read(mount.Gizmo, gd) is not { } g) continue;
            char letter = GizmoDef.StyleLetter(g.StyleOn(hp));
            var at = Affine.From(hp.Transform);
            var byName = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in g.Geometry) byName.TryAdd(n.Name, n);
            Affine World(SceneNode n, int depth) =>
                depth < 8 && !n.IsRoot && byName.TryGetValue(n.Parent, out var p) && !ReferenceEquals(p, n)
                    ? World(p, depth + 1).Compose(Affine.From(n.Transform)) : Affine.From(n.Transform);
            foreach (var n in g.Geometry)
            {
                if (n.Name.Length < 6 || char.ToUpperInvariant(n.Name[3]) != letter || n.Name[4] != Detail || n.Name[5] != '1') continue;
                if (Geo(n.Name) is not { } geo) continue;
                var place = at.Compose(World(n, 0));
                var part = new ModelPart(n.Name, geo, place);
                m.Parts.Add(part);
                m.GizmoOf[part] = m.Gizmos.Count;
                // only the gun turns: the original slips the yaw node in under the base (TU), which stays on the mount
                if (letter == 'T' && n.Name.Length >= 8 && n.Name[6..8].Equals("GN", StringComparison.OrdinalIgnoreCase) && byName.ContainsKey(n.Parent))
                    m.Turrets.Add(new TurretPart(m.Gizmos.Count, n.Name, place.T));
            }
            m.Gizmos.Add((mount.Slot, g, hp));
        }
    }

    /// <summary>The .TMT names a .vtf's VTFC lists (13-byte fields after the .vdf name and 16-byte paint name).</summary>
    public static List<string> VtfTmtNames(Bwd2File vtf)
    {
        var o = new List<string>();
        if (vtf.First("VTFC") is not { } c) return o;
        for (int p = c.PayloadOffset + Bwd2File.FileField + Bwd2File.NameField; p + Bwd2File.FileField <= c.Offset + c.Size; p += Bwd2File.FileField)
        {
            var n = Bin.CStr(vtf.Data, p, Bwd2File.FileField);
            if (n.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase)) o.Add(n);
        }
        return o;
    }

    private bool VehicleVariantOk(string name)
    {
        if (name.Length < 4) return true;
        if (char.IsDigit(name[2]) && name[2] != Detail) return false;
        if (char.IsDigit(name[3]) && name[3] != Condition) return false;
        if (name.Length >= 8 && SkipParts.Contains(name[4..8])) return false;
        return true;
    }

    /// <summary>
    /// Scenery: detail on the third character; on the fourth, '_' marks the
    /// intact part and 'A' its alternative, but only where both exist
    /// (NS1_ARR1 / NS1AARR1) -- some part codes simply begin with A
    /// (SL1ACFP1, a sign with no '_' twin). Destroyed, the '_' parts give
    /// way to their twins, and those without one go.
    /// </summary>
    private static bool SceneryVariantOk(string name, char detail, ISet<string> names, bool destroyed = false, Func<string, bool>? dead = null)
    {
        if (name.Length < 4) return true;
        if (char.IsDigit(name[2]) && name[2] != detail) return false;
        if (dead is not null)
        {
            if (name[3] == '_') return !dead(name);
            if (name[3] is 'A' or 'a' && names.Contains(name[..3] + "_" + name[4..])) return dead(name[..3] + "_" + name[4..]);
            return true;
        }
        if (!destroyed && name[3] is 'A' or 'a' && names.Contains(name[..3] + "_" + name[4..])) return false;
        // destroyed: each part takes its 'A' geometry, and where the object has
        // such twins at all, a part without one is gone (the grain hopper's legs under its fallen bin)
        if (destroyed && name[3] == '_' && (names.Contains(name[..3] + "A" + name[4..]) || HasTwins(names))) return false;
        return true;
    }

    private static bool HasTwins(ISet<string> names) =>
        names.Any(n => n.Length > 4 && n[3] is 'A' or 'a' && names.Contains(n[..3] + "_" + n[4..]));

    /// <summary>
    /// A car's interior nodes (detail 5) carry their placement in the car's frame even when they name a parent: in every
    /// stock .vdf the cockpit's MIRL/MIRR (Piranha, Cavera, Leopard), the Grappler's dash, post and seat, the Sovereign's
    /// horn, the balloon's burner and rope, the tractor's pipe and every GLPF/GRPF sit exactly where the same part of
    /// detail 1, composed through its parents, does. Composing them again lifts them by the parent's offset (the
    /// Piranha's side mirrors floated above the roof, #25); the real game draws them on the doors.
    /// </summary>
    public static bool CarFrameNode(string kind, string name) => kind == "vcf" && name.Length >= 4 && name[2] == '5' && char.IsDigit(name[3]);

    private AssembledModel AssembleTree(string definition, string kind, Bwd2File def, string chunk, Func<string, bool>? variantOk, List<string>? tmts)
    {
        var nodes = def.SceneNodes().Where(t => t.Tag == chunk).SelectMany(t => t.Nodes).ToList();
        var seen = new HashSet<(string, string)>();
        var unique = new List<SceneNode>();
        foreach (var n in nodes)
            if (seen.Add((n.Name.ToUpperInvariant(), n.Parent.ToUpperInvariant()))) unique.Add(n);
        var first = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in unique) first.TryAdd(n.Name, n);

        Affine World(SceneNode n, int depth)
        {
            var t = Affine.From(n.Transform);
            if (depth > 16 || n.IsRoot || n.Detached || CarFrameNode(kind, n.Name)) return t;
            if (!first.TryGetValue(n.Parent, out var p) || ReferenceEquals(p, n)) return t;
            return World(p, depth + 1).Compose(t);
        }

        var code = unique.Select(n => n.Name).FirstOrDefault(n => n.Length >= 4 && char.IsDigit(n[2]))?[..2].ToUpperInvariant();
        var m = new AssembledModel { Definition = definition, Kind = kind, ObjectCode = code };
        var allNames = new HashSet<string>(unique.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
        variantOk ??= n => SceneryVariantOk(n, Detail, allNames, Destroyed, DestroyedNode);
        if (kind == "vcf" && PartCondition is { } want)
        {
            // per part code, the best condition at or below the wanted one
            var best = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in allNames)
                if (n.Length >= 8 && n[2] == Detail && char.IsDigit(n[3]))
                {
                    var part = n[4..8];
                    if (n[3] <= want(part) && (!best.TryGetValue(part, out var c) || n[3] > c)) best[part] = n[3];
                }
            variantOk = n => n.Length < 8 || !char.IsDigit(n[2]) || (n[2] == Detail && (!char.IsDigit(n[3]) || best.GetValueOrDefault(n[4..8]) == n[3]) && !SkipParts.Contains(n[4..8]));
        }
        if (kind == "vcf" && Wrecked && def.VgeoStates() is { } states)
        {
            // the zones' nodes: every name they have in any state goes, but for their state-3 names
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wreck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int k in states.ZoneSlots(Detail))
            {
                for (int s = 0; s < VehicleStates.States; s++)
                    if (states.At(Detail, s, k) is { } x) gone.Add(x.Node.Name);
                if (WreckShape)
                {
                    if (states.At(Detail, 0, k) is { } z0 && VehicleStates.IsZoneType(z0.Type))
                        for (int s = VehicleStates.States - 2; s >= 0; s--)
                            if (states.At(Detail, s, k) is { } w) { wreck.Add(w.Node.Name); break; }
                }
                else if (states.At(Detail, VehicleStates.States - 1, k) is { } w) wreck.Add(w.Node.Name);
            }
            var alive = variantOk;
            variantOk = n => wreck.Contains(n) || !gone.Contains(n) && alive(n);
        }
        if (tmts is not null) m.TmtNames.AddRange(tmts);
        foreach (var n in unique)
        {
            if (!variantOk(n.Name)) continue;
            var g = Geo(n.Name);
            if (g is null) { m.MissingGeo.Add(n.Name); continue; }
            m.Parts.Add(new ModelPart(n.Name, g, World(n, 0)));
        }
        if (kind == "vcf") MountSeating.Seat(m);
        return m;
    }
}
