using System.Numerics;
using System.Text.RegularExpressions;
using I76.Formats.Bwd2;

namespace I76.Formats.Mission;

/// <summary>A placement from ODEF. The name's high bits are per-placement data (Q28).</summary>
public sealed record PlacedObject(string Name, byte Flags, float[] Rotation, Vector3 Position, byte[] Raw)
{
    public bool IsKeyword => MissionFile.Keywords.Contains(Name.ToLowerInvariant());
    /// <summary>OBJ payload from 0x38 to 0x5b: not yet mapped.</summary>
    public ReadOnlySpan<byte> Unk_0x38 => Raw.AsSpan(0x38, 0x24);
    /// <summary>+0x5c: the object type (obj+0x6c): 1 vehicle, 3 mesh scenery, 4 sphere, 0xb–0xd structures, 0x52 road pieces…</summary>
    public uint ObjectType => Raw.Length >= 0x60 ? BitConverter.ToUInt32(Raw, 0x5c) : 0;
    /// <summary>+0x60: the object's flags (obj+0x10); 0x10 marks the player's car.</summary>
    public uint ObjectFlags => Raw.Length >= 0x64 ? BitConverter.ToUInt32(Raw, 0x60) : 0;
    public bool IsPlayer => (ObjectFlags & 0x10) != 0;
}

/// <summary>An RSEG point; only the leading X and Z are decoded.</summary>
public sealed record RoadPoint(float X, float Z, byte[] Raw);

public sealed record RoadSegment(uint Kind, List<RoadPoint> Points);

public sealed record NavPath(string Name, List<Vector3> Points);

/// <summary>A mission script: tables, threads (start pc, depth, data indices), data and code.</summary>
public sealed record FsmProgram(
    List<string> Prototypes,
    List<(string Label, byte[] InstanceId)> Actors,
    List<string> Sounds,
    List<NavPath> Paths,
    List<(int StartPc, int Depth, int[] Stack)> Threads,
    int[] Data,
    (uint Op, uint Arg)[] Code,
    bool EndsExactly);

/// <summary>The FSM chunk's counted tables, as the reference reader walks them.</summary>
public sealed record FsmTables(
    (int Offset, int Count, int Stride) Opcodes,
    (int Offset, int Count, int Stride) Actors,
    (int Offset, int Count, int Stride) Sounds,
    (int Offset, int Count) NavPaths,
    List<NavPath> Paths,
    int ProgramOffset,
    int PayloadSize);

/// <summary>
/// A mission .msn (a BWD2 file), a port of the reference reader
/// (docs/formats/mission-files.md). Missions are read from the install's
/// miss16/ (or miss8/) directory, not from I76.ZFS.
/// </summary>
public sealed class MissionFile
{
    public const int TerrainTile = 128;
    public const int TerrainTileBytes = TerrainTile * TerrainTile * 2;
    public const int HeightMask = 0x0FFF;
    public const int SurfaceShift = 12;
    public const int ZmapGrid = 80;
    public const byte ZmapEmpty = 255;
    /// <summary>World units per terrain sample, established by overlay.</summary>
    public const float WorldScale = 5.0f;
    public const int RsegPoint = 24;
    public const int FsmNameSlot = 40;
    public const int FsmActorLabel = 0;
    public const int FsmActorObject = 40;
    public const int FsmPathName = 40;
    public const string ObjTag = "OBJ\0";
    public const int NameMask = 0x7F;
    public static readonly string[] MissionChunks = ["WDEF", "TDEF", "RDEF", "ODEF", "LDEF", "ADEF"];
    public static readonly string[] Keywords = ["spawn", "regen"];
    private static readonly (string Name, int Stride)[] FsmTableDefs = [("opcodes", 40), ("actors", 48), ("sounds", 40)];
    private static readonly Regex WdefRef = new(@"[A-Za-z0-9_]{1,8}\.(?:act|lum|tbl|npt|map|sdf|tab)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public Bwd2File File { get; }
    public byte[] Data => File.Data;

    public MissionFile(string path) : this(path, System.IO.File.ReadAllBytes(path)) { }

    public MissionFile(string path, byte[] data)
    {
        Path = path;
        File = new Bwd2File(data);
    }

    /// <summary>Chunks nested in a payload (TDEF holds TREV + ZMAP). Stops at the first bad header.</summary>
    public static List<Chunk> SubChunks(ReadOnlySpan<byte> payload)
    {
        var o = new List<Chunk>();
        int p = 0;
        while (p + Bwd2File.HeaderSize <= payload.Length)
        {
            var tag = Bin.Latin1(payload.Slice(p, 4));
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[(p + 4)..]);
            if (size < Bwd2File.HeaderSize || p + (long)size > payload.Length) break;
            o.Add(new Chunk(tag, p, (int)size));
            p += (int)size;
        }
        return o;
    }

    private ReadOnlySpan<byte> Body(Chunk c) => Data.AsSpan(c.PayloadOffset, c.PayloadSize);

    /// <summary>(sub-chunk, its payload) for every sub-chunk of every top-level chunk tagged <paramref name="tag"/>.</summary>
    private IEnumerable<(Chunk Sub, byte[] Payload)> Subs(string tag)
    {
        foreach (var c in File.Chunks.Where(c => c.Tag == tag))
        {
            var body = Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in SubChunks(body))
                yield return (sc, body.AsSpan(sc.PayloadOffset, sc.PayloadSize).ToArray());
        }
    }

    public List<string> ChunkTags() => File.Chunks.Select(c => c.Tag).ToList();

    public List<PlacedObject> PlacedObjects()
    {
        var o = new List<PlacedObject>();
        Span<byte> masked = stackalloc byte[8];
        foreach (var (sc, b) in Subs("ODEF"))
        {
            if (sc.Tag != ObjTag) continue;
            var raw = b.AsSpan(0, 8);
            byte flags = 0;
            for (int i = 0; i < 8; i++)
            {
                masked[i] = (byte)(raw[i] & NameMask);
                flags |= (byte)(((raw[i] >> 7) & 1) << i);
            }
            o.Add(new PlacedObject(Bin.CStr(masked), flags, Bin.F32s(b, 8, 9),
                new Vector3(Bin.F32(b, 44), Bin.F32(b, 48), Bin.F32(b, 52)), b));
        }
        return o;
    }

    /// <summary>(tile count, 80×80 grid of tile ids, row-major) from TDEF/ZMAP.</summary>
    public (int Count, byte[] Grid) TerrainGrid()
    {
        foreach (var (sc, pay) in Subs("TDEF"))
            if (sc.Tag == "ZMAP")
                return (pay[0], pay.AsSpan(1, ZmapGrid * ZmapGrid).ToArray());
        throw new InvalidDataException("no TDEF/ZMAP");
    }

    /// <summary>
    /// The .ter the mission names: TDEF/ZONE's payload is one byte (0xff in every file seen) and the file name from +1
    /// (the ZONE handler). Not always the mission's own stem: the
    /// community pack's ofjuicy_8.cbt names ofjuicy.ter. Null if the mission has no ZONE.
    /// </summary>
    public string? TerrainName()
    {
        foreach (var (sc, pay) in Subs("TDEF"))
            if (sc.Tag == "ZONE" && pay.Length > 1 && Bin.CStr(pay, 1, pay.Length - 1) is { Length: > 0 } n)
                return n;
        return null;
    }

    /// <summary>
    /// WDEF/WREV: 9 in every mission the GOG base game and the Nitro Pack ship and expect (their revision table), 8 in levels the 1997 release and its editor I76EDIT wrote (the 1997 build's table): a WRLD
    /// 13 bytes shorter, with no horizon (.hzd) field. 0 if absent.
    /// </summary>
    public uint WorldRevision()
    {
        foreach (var (sc, pay) in Subs("WDEF"))
            if (sc.Tag == "WREV" && pay.Length >= 4) return Bin.U32(pay, 0);
        return 0;
    }

    /// <summary>
    /// The roads in place of the file's own, for the map editor: its edited roads reach the world the game builds (the editor's
    /// view, a playtest and the AI's road routes) without being written. Null: the file's.
    /// </summary>
    public List<RoadSegment>? RoadOverride { get; set; }

    public List<RoadSegment> RoadSegments()
    {
        if (RoadOverride is { } ov) return [.. ov];
        var o = new List<RoadSegment>();
        foreach (var (sc, b) in Subs("RDEF"))
        {
            if (sc.Tag != "RSEG") continue;
            uint kind = Bin.U32(b, 0), count = Bin.U32(b, 4);
            var pts = new List<RoadPoint>();
            for (int k = 0; k < count; k++)
            {
                int r = 8 + k * RsegPoint;
                pts.Add(new RoadPoint(Bin.F32(b, r), Bin.F32(b, r + 4), b.AsSpan(r, RsegPoint).ToArray()));
            }
            o.Add(new RoadSegment(kind, pts));
        }
        return o;
    }

    /// <summary>The ADEF/FSM payload; empty for missions without scripting.</summary>
    public byte[] FsmPayload()
    {
        foreach (var (sc, b) in Subs("ADEF"))
            if (sc.Tag == "FSM\0") return b;
        return [];
    }

    /// <summary>The scripting opcode vocabulary declared by the FSM chunk.</summary>
    public List<string> FsmOpcodes()
    {
        var b = FsmPayload();
        if (b.Length < 4) return [];
        uint count = Bin.U32(b, 0);
        var names = new List<string>();
        int off = 4;
        while (off + FsmNameSlot <= b.Length && names.Count < count)
        {
            var slot = b.AsSpan(off, FsmNameSlot);
            int z = slot.IndexOf((byte)0);
            var raw = z < 0 ? slot : slot[..z];
            if (raw.Length == 0) break;
            bool ok = true;
            foreach (var ch in raw) if (ch < 32 || ch >= 127) { ok = false; break; }
            if (!ok) break;
            names.Add(Bin.Latin1(raw));
            off += FsmNameSlot;
        }
        return names;
    }

    public FsmTables? GetFsmTables()
    {
        var b = FsmPayload();
        if (b.Length < 4) return null;
        int off = 0;
        var tabs = new (int, int, int)[3];
        for (int i = 0; i < FsmTableDefs.Length; i++)
        {
            int n = (int)Bin.U32(b, off);
            tabs[i] = (off, n, FsmTableDefs[i].Stride);
            off += 4 + n * FsmTableDefs[i].Stride;
        }
        int np = (int)Bin.U32(b, off);
        int start = off;
        off += 4;
        var paths = new List<NavPath>();
        for (int i = 0; i < np; i++)
        {
            string nm = Bin.CStr(b, off, FsmPathName);
            int cnt = (int)Bin.U32(b, off + FsmPathName);
            int bse = off + FsmPathName + 4;
            var pts = new List<Vector3>(cnt);
            for (int k = 0; k < cnt; k++)
                pts.Add(new Vector3(Bin.F32(b, bse + k * 12), Bin.F32(b, bse + k * 12 + 4), Bin.F32(b, bse + k * 12 + 8)));
            paths.Add(new NavPath(nm, pts));
            off = bse + cnt * 12;
        }
        return new FsmTables(tabs[0], tabs[1], tabs[2], (start, np), paths, off, b.Length);
    }

    /// <summary>
    /// The whole FSM chunk: prototypes, actors (label +
    /// raw 8-byte instance id), sounds, nav paths, then the thread table
    /// (start pc, depth, 40 data indices), the data array and the code.
    /// </summary>
    public FsmProgram? ReadFsmProgram()
    {
        var b = FsmPayload();
        if (b.Length < 4) return null;
        int off = 0;
        int U() { int v = (int)Bin.U32(b, off); off += 4; return v; }
        var protos = new List<string>();
        for (int n = U(), i = 0; i < n; i++, off += 40) protos.Add(Bin.CStr(b, off, 40));
        var actors = new List<(string, byte[])>();
        for (int n = U(), i = 0; i < n; i++, off += 48) actors.Add((Bin.CStr(b, off, 40), b.AsSpan(off + 40, 8).ToArray()));
        var sounds = new List<string>();
        for (int n = U(), i = 0; i < n; i++, off += 40) sounds.Add(Bin.CStr(b, off, 40));
        var paths = new List<NavPath>();
        for (int n = U(), i = 0; i < n; i++)
        {
            string nm = Bin.CStr(b, off, FsmPathName);
            int cnt = (int)Bin.U32(b, off + FsmPathName);
            int bse = off + FsmPathName + 4;
            var pts = new List<Vector3>(cnt);
            for (int k = 0; k < cnt; k++) pts.Add(new Vector3(Bin.F32(b, bse + k * 12), Bin.F32(b, bse + k * 12 + 4), Bin.F32(b, bse + k * 12 + 8)));
            paths.Add(new NavPath(nm, pts));
            off = bse + cnt * 12;
        }
        var threads = new List<(int, int, int[])>();
        for (int n = U(), i = 0; i < n; i++)
        {
            int pc = U(), depth = U();
            var st = new int[40];
            for (int k = 0; k < 40; k++) st[k] = U();
            threads.Add((pc, depth, st));
        }
        var data = new int[U()];
        for (int i = 0; i < data.Length; i++) data[i] = U();
        var code = new (uint, uint)[U()];
        for (int i = 0; i < code.Length; i++) { code[i] = (Bin.U32(b, off), Bin.U32(b, off + 4)); off += 8; }
        return new FsmProgram(protos, actors, sounds, paths, threads, data, code, off == b.Length);
    }

    /// <summary>(script label, object name) per actor; object names are masked like ODEF's.</summary>
    public List<(string Label, string Object)> FsmActors()
    {
        var b = FsmPayload();
        if (GetFsmTables() is not { } t) return [];
        var o = new List<(string, string)>();
        for (int k = 0; k < t.Actors.Count; k++)
        {
            int r = t.Actors.Offset + 4 + k * t.Actors.Stride;
            var obj = new byte[8];
            for (int i = 0; i < 8; i++) obj[i] = (byte)(b[r + FsmActorObject + i] & NameMask);
            o.Add((Bin.CStr(b, r + FsmActorLabel, 8), Bin.CStr(obj)));
        }
        return o;
    }

    /// <summary>Assets named by WDEF: the palette, .lum, .tbl, .npt, three .map, one .sdf.</summary>
    public List<string> WdefRefs()
    {
        var c = File.First("WDEF");
        if (c is null) return [];
        return WdefRef.Matches(Bin.Latin1(Body(c.Value))).Select(m => m.Value.ToLowerInvariant()).ToList();
    }

    public string? Palette() => WdefRefs().FirstOrDefault(r => r.EndsWith(".act"));

    /// <summary>The CD track the mission plays: WDEF/WRLD's first u32.</summary>
    public uint? MusicTrack()
    {
        foreach (var (sc, pay) in Subs("WDEF"))
            if (sc.Tag == "WRLD" && sc.PayloadSize >= 4)
                return Bin.U32(pay, 0);
        return null;
    }

    /// <summary>The LDEF payload, unparsed (R4).</summary>
    public byte[] LdefRaw() => File.First("LDEF") is { } c ? Body(c).ToArray() : [];
}
