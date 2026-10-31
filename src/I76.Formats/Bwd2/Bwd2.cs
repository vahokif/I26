using System.Text.RegularExpressions;

namespace I76.Formats.Bwd2;

/// <summary>One chunk; <see cref="Size"/> includes the 8-byte header.</summary>
public readonly record struct Chunk(string Tag, int Offset, int Size)
{
    public int PayloadOffset => Offset + Bwd2File.HeaderSize;
    public int PayloadSize => Size - Bwd2File.HeaderSize;
}

/// <summary>A row-major 3×3 rotation and a translation.</summary>
public sealed record Transform(float[] Rotation, float[] Translation)
{
    public bool Orthonormal(double tol = 1e-3) => MathUtil.Orthonormal(Rotation, tol);
    public bool IsZero => Rotation.All(x => x == 0);
}

public sealed record WeaponMount(uint Slot, string Gizmo)
{
    public bool Empty => Gizmo.Equals("null", StringComparison.OrdinalIgnoreCase);
}

public sealed record SceneNode(string Name, string Parent, Transform Transform)
{
    public bool IsRoot => Parent.Equals(Bwd2File.RootParent, StringComparison.OrdinalIgnoreCase);
    public bool Detached => Parent.Equals(Bwd2File.NullName, StringComparison.OrdinalIgnoreCase);
}

public sealed record Location(string Tag, int Index, Transform Transform);

/// <summary>A vehicle's VGEO slots (<see cref="Bwd2File.VgeoStates"/>): <see cref="PerGroup"/> per (detail, state) group.</summary>
public sealed record VehicleStates(int PerGroup, (SceneNode Node, int Type)?[] Slots)
{
    public const int States = 4;
    /// <summary>The body zone nodes' types (BDYF, BDYM, BDYB, BDYT): the original sets their subtrees' state.</summary>
    public static bool IsZoneType(int type) => type is >= 0x41 and <= 0x44;

    /// <summary>Slot k of the group for a detail digit ('1'..'7') and state (0..3), or null if empty or past the end.</summary>
    public (SceneNode Node, int Type)? At(char detail, int state, int k)
    {
        int i = ((detail - '1') * States + state) * PerGroup + k;
        return k < 0 || k >= PerGroup || i < 0 || i >= Slots.Length ? null : Slots[i];
    }

    /// <summary>
    /// The slots of a detail whose node is a body zone or hangs under one in
    /// state 0 (its parents, by name, up to WORLD): the nodes the dead car's
    /// the original walks for a zone.
    /// </summary>
    public IEnumerable<int> ZoneSlots(char detail)
    {
        var byName = new Dictionary<string, (SceneNode Node, int Type)>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < PerGroup; k++) if (At(detail, 0, k) is { } s) byName.TryAdd(s.Node.Name, s);
        for (int k = 0; k < PerGroup; k++)
        {
            if (At(detail, 0, k) is not { } s) continue;
            var at = s;
            for (int depth = 0; depth < 16; depth++)
            {
                if (IsZoneType(at.Type)) { yield return k; break; }
                if (at.Node.IsRoot || !byName.TryGetValue(at.Node.Parent, out var up) || ReferenceEquals(up.Node, at.Node)) break;
                at = up;
            }
        }
    }
}

/// <summary>
/// The BWD2 chunked definition files (.sdf .vcf .vtf .wdf .vdf .gdf .xdf .cdf),
/// a port of the reference reader (docs/formats/bwd2-definitions.md). Also the
/// container of mission .msn files.
/// </summary>
public sealed class Bwd2File
{
    public const int HeaderSize = 8;
    public static readonly string[] Extensions = ["sdf", "vcf", "vtf", "wdf", "vdf", "gdf", "xdf", "cdf"];

    public static readonly IReadOnlyDictionary<string, string> TypeChunks = new Dictionary<string, string>
    {
        ["SDFC"] = "sdf", ["VCFC"] = "vcf", ["VTFC"] = "vtf", ["WDFC"] = "wdf",
        ["VDFC"] = "vdf", ["GDFC"] = "gdf", ["XDFC"] = "xdf",
    };

    /// <summary>compnent.cdf opens straight into ECNK.</summary>
    public static readonly string[] NoTypeChunk = ["cdf"];

    public const int NameField = 16;  // display name; not NUL-terminated at 16 chars
    public const int FileField = 13;  // 8.3 + NUL

    /// <summary>Fixed payload sizes of the type chunks, which are plain C structs.</summary>
    public static readonly IReadOnlyDictionary<string, int> TypeChunkSize = new Dictionary<string, int>
    {
        ["SDFC"] = 70, ["VCFC"] = 129, ["VTFC"] = 1212, ["WDFC"] = 66,
        ["VDFC"] = 77, ["GDFC"] = 164, ["XDFC"] = 40,
    };

    public static readonly IReadOnlyDictionary<string, int> TransformAt = new Dictionary<string, int> { ["VLOC"] = 4, ["HLOC"] = 28 };
    public const int WlocStride = 56;
    public const int WlocSlots = 6;

    public static readonly IReadOnlyDictionary<string, int> GeoStride = new Dictionary<string, int>
    {
        ["SGEO"] = 120, ["WGEO"] = 100, ["VGEO"] = 100, ["GGEO"] = 100, ["OGEO"] = 100, ["XGEO"] = 100,
    };
    public const string SingleNode = "SOBJ";
    public const int NodeName = 8;
    public const int NodeParentAt = 56;
    public const string RootParent = "WORLD";
    public const string NullName = "NULL";

    private static readonly Regex RefRx = new(@"[A-Za-z0-9_\-]{1,8}\.(?:vdf|vtf|tmt|geo|map|m16|vcf|wdf|sdf|gdf)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public byte[] Data { get; }
    public IReadOnlyList<Chunk> Chunks { get; }

    public Bwd2File(byte[] data)
    {
        Data = data;
        Chunks = Walk(data);
    }

    public static List<Chunk> Walk(byte[] d)
    {
        if (d.Length < 4 || Bin.Latin1(d.AsSpan(0, 4)) != "BWD2")
            throw new InvalidDataException("not a BWD2 file");
        var o = new List<Chunk>();
        int p = 0;
        while (p + HeaderSize <= d.Length)
        {
            var tag = Bin.Latin1(d.AsSpan(p, 4));
            uint size = Bin.U32(d, p + 4);
            if (size < HeaderSize || p + (long)size > d.Length)
                throw new InvalidDataException($"bad chunk '{tag}' size {size} at offset {p}");
            o.Add(new Chunk(tag, p, (int)size));
            p += (int)size;
        }
        if (p != d.Length) throw new InvalidDataException($"{d.Length - p} trailing bytes");
        return o;
    }

    public IEnumerable<Chunk> All(string tag) => Chunks.Where(c => c.Tag == tag);
    public Chunk? First(string tag) => Chunks.Where(c => c.Tag == tag).Select(c => (Chunk?)c).FirstOrDefault();

    public static Transform ReadTransform(byte[] d, int at) => new(Bin.F32s(d, at, 9), Bin.F32s(d, at + 36, 3));

    /// <summary>WEPN: u32 slot, then a 13-byte .gdf filename.</summary>
    public List<WeaponMount> WeaponMounts() =>
        All("WEPN").Select(c => new WeaponMount(Bin.U32(Data, c.PayloadOffset),
            Bin.CStr(Data, c.PayloadOffset + 4, FileField))).ToList();

    public List<Location> Locations()
    {
        var o = new List<Location>();
        foreach (var c in Chunks)
        {
            int p = c.PayloadOffset;
            if (TransformAt.TryGetValue(c.Tag, out var at))
                o.Add(new Location(c.Tag, c.Tag == "VLOC" ? (int)Bin.U32(Data, p) : 0, ReadTransform(Data, p + at)));   // VLOC: its kind (40 eye, 35 speedo, 36 tacho)
            else if (c.Tag == "WLOC")
                for (int i = 0; i < WlocSlots; i++)
                    o.Add(new Location(c.Tag, i, ReadTransform(Data, p + i * WlocStride + 4)));
        }
        return o;
    }

    private static bool Printable(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        var s = n < 0 ? b : b[..n];
        if (s.Length == 0) return false;
        foreach (var c in s) if (c < 32 || c >= 127) return false;
        return true;
    }

    /// <summary>
    /// A node, or null for an unused slot: an all-zero name, the literal name
    /// NULL, or uninitialised bytes (sstop_1.sdf declares 12 slots and fills 2).
    /// </summary>
    private SceneNode? NodeAt(int q)
    {
        var raw = Data.AsSpan(q, NodeName);
        if (!raw.ContainsAnyExcept((byte)0)) return null;
        if (Bin.CStr(raw).Equals(NullName, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Printable(raw)) return null;
        return new SceneNode(Bin.CStr(raw), Bin.CStr(Data, q + NodeParentAt, NodeName), ReadTransform(Data, q + NodeName));
    }

    /// <summary>(chunk tag, nodes) for every scene-graph chunk, empty slots skipped.</summary>
    public List<(string Tag, List<SceneNode> Nodes)> SceneNodes()
    {
        var o = new List<(string, List<SceneNode>)>();
        foreach (var c in Chunks)
        {
            if (c.Tag == SingleNode)
            {
                var n = NodeAt(c.PayloadOffset);
                o.Add((c.Tag, n is null ? [] : [n]));
                continue;
            }
            if (!GeoStride.TryGetValue(c.Tag, out var stride)) continue;
            int body = c.PayloadSize - 4;
            if (body < 0 || body % stride != 0)
                throw new InvalidDataException($"{c.Tag} payload {c.PayloadSize} is not 4+n*{stride}");
            var nodes = new List<SceneNode>();
            for (int k = 0; k < body / stride; k++)
                if (NodeAt(c.PayloadOffset + 4 + k * stride) is { } n) nodes.Add(n);
            o.Add((c.Tag, nodes));
        }
        return o;
    }

    /// <summary>
    /// A .vdf's VGEO as the engine indexes it: the leading u32 is the slots
    /// per group, and the groups run detail 1..7 × damage state 0..3, so slot
    /// k of every group is one node, and state s's name for it is what the
    /// node draws in that state (an empty slot draws nothing).
    /// Each node's type is the u32 at record +0x5c: 0x41..0x44 the body
    /// zones BDYF, BDYM, BDYB, BDYT (veh+0x408..+0x414), 0x47 HLGT, 0x48 BLGT.
    /// Null without a VGEO.
    /// </summary>
    public VehicleStates? VgeoStates()
    {
        if (First("VGEO") is not { } c || c.PayloadSize < 4) return null;
        int per = (int)Bin.U32(Data, c.PayloadOffset), stride = GeoStride["VGEO"];
        int n = (c.PayloadSize - 4) / stride;
        if (per <= 0 || n < per) return null;
        var slots = new (SceneNode Node, int Type)?[n];
        for (int k = 0; k < n; k++)
        {
            int q = c.PayloadOffset + 4 + k * stride;
            if (NodeAt(q) is { } node) slots[k] = (node, (int)Bin.U32(Data, q + VgeoTypeAt));
        }
        return new VehicleStates(per, slots);
    }

    public const int VgeoTypeAt = 0x5c;

    /// <summary>The node type of a spinner: a node that turns on its own.</summary>
    public const uint SpinnerType = 10;

    /// <summary>
    /// A .sdf's spinners: each SGEO node of type 10, keyed by name, with its turn rate in revolutions
    /// per second about its own x, y and z (record +0x68..+0x70). The node loader
    /// hands those three floats to the spinner's init, which stores them times 2π, and the class's per-frame tick
    /// turns the node by them every frame. Every shipped one turns about y alone (the
    /// gas-station signs). Empty for anything without one.
    /// </summary>
    public Dictionary<string, System.Numerics.Vector3> Spinners()
    {
        var o = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase);
        if (First("SGEO") is not { } c) return o;
        int stride = GeoStride["SGEO"];
        for (int q = c.PayloadOffset + 4; q + stride <= c.PayloadOffset + c.PayloadSize; q += stride)
        {
            if (Bin.U32(Data, q + VgeoTypeAt) != SpinnerType) continue;
            var name = Bin.CStr(Data, q, 8);
            if (name.Length == 0 || name.Equals(NullName, StringComparison.OrdinalIgnoreCase)) continue;
            o.TryAdd(name, new System.Numerics.Vector3(Bin.F32(Data, q + 0x68), Bin.F32(Data, q + 0x6c), Bin.F32(Data, q + 0x70)));
        }
        return o;
    }

    /// <summary>The node flags dword in a GEO record (the original's scene loader: record +0x60 → node +0x10).</summary>
    public const int NodeFlagsAt = 0x60;

    /// <summary>
    /// Node flag 0x40: a camera-facing card. The scene-object draw swaps such a node's world rotation
    /// for the frame's yaw-only camera matrix (built from the view's forward), keeping
    /// its position, so it turns about the vertical through its origin to face the eye.
    /// </summary>
    public const uint BillboardFlag = 0x40;

    /// <summary>
    /// The names of the nodes flagged <see cref="BillboardFlag"/> in every GEO chunk (SGEO, VGEO, ...): in the stock
    /// games only the pine trees' SGEO nodes (npinetr1/2, their stumps too). Empty for anything without one.
    /// </summary>
    public HashSet<string> BillboardNodes()
    {
        var o = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Chunks)
        {
            if (!GeoStride.TryGetValue(c.Tag, out var stride)) continue;
            for (int q = c.PayloadOffset + 4; q + stride <= c.PayloadOffset + c.PayloadSize; q += stride)
            {
                if ((Bin.U32(Data, q + NodeFlagsAt) & BillboardFlag) == 0) continue;
                var name = Bin.CStr(Data, q, NodeName);
                if (name.Length > 0 && !name.Equals(NullName, StringComparison.OrdinalIgnoreCase)) o.Add(name);
            }
        }
        return o;
    }

    /// <summary>A .sdf's SGEO nodes of one type (record +0x5c), by name: type 7 the gates.</summary>
    public HashSet<string> NodesOfType(uint type)
    {
        var o = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (First("SGEO") is not { } c) return o;
        int stride = GeoStride["SGEO"];
        for (int q = c.PayloadOffset + 4; q + stride <= c.PayloadOffset + c.PayloadSize; q += stride)
        {
            if (Bin.U32(Data, q + VgeoTypeAt) != type) continue;
            var name = Bin.CStr(Data, q, 8);
            if (name.Length > 0 && !name.Equals(NullName, StringComparison.OrdinalIgnoreCase)) o.Add(name);
        }
        return o;
    }

    public string? DisplayName()
    {
        foreach (var c in Chunks)
            if (TypeChunks.ContainsKey(c.Tag))
                return Bin.CStr(Data, c.PayloadOffset, c.Tag == "VTFC" ? FileField : NameField);
        return null;
    }

    public string? FileType() => Chunks.Where(c => TypeChunks.ContainsKey(c.Tag)).Select(c => TypeChunks[c.Tag]).FirstOrDefault();

    /// <summary>.vcf type chunk: 16-byte name, then .vdf and .vtf filenames.</summary>
    public (string Name, string Vdf, string Vtf)? VcfcFields()
    {
        if (First("VCFC") is not { } c) return null;
        int p = c.PayloadOffset;
        return (Bin.CStr(Data, p, NameField), Bin.CStr(Data, p + NameField, FileField),
            Bin.CStr(Data, p + NameField + FileField, FileField));
    }

    /// <summary>Filenames embedded anywhere in the file, by extension match.</summary>
    public HashSet<string> References()
    {
        var s = Bin.Latin1(Data);
        return RefRx.Matches(s).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
    }

    public static string Ext(string name)
    {
        int i = name.LastIndexOf('.');
        return i < 0 ? "" : name[(i + 1)..].ToLowerInvariant();
    }

    public static bool IsDefinition(string name) => Extensions.Contains(Ext(name));
}
