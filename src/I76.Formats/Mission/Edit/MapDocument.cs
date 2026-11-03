using System.Numerics;
using System.Text;

namespace I76.Formats.Mission.Edit;

/// <summary>
/// An editable mission file (.cbt .rac .cf2-4 .msn .lvl): the BWD2 chunk tree plus typed views of what an editor changes.
/// The model is read, edit some fields, write back: <see cref="ToBytes"/> of a document nobody touched is the file it was
/// read from, byte for byte, and an edit changes only the bytes of the field it names (the FSM script, the other WRLD
/// fields, the unmapped bytes of every object and road point, chunk order and padding all stay).
/// </summary>
public sealed class MapDocument
{
    public const int ArenaNameAt = 0x13b, ArenaNameSize = 16;
    public const int ZoneNameSize = 13;
    public const int WrldHourAt = 0x93, WrldCdTrackAt = 0;

    public BwdTree Tree { get; }

    /// <summary>The ODEF OBJ placements, in file order. Add, remove, reorder and edit them freely.</summary>
    public List<MapObject> Objects { get; }
    /// <summary>The RDEF RSEG roads, in file order.</summary>
    public List<RoadEdit> Roads { get; }
    /// <summary>The LDEF polylines (fences, tyre walls).</summary>
    public List<LineEdit> Lines { get; }
    /// <summary>The TDEF ZMAP: tile ids on the 80x80 grid, row-major (255 empty). Edit in place; see <see cref="TileCount"/>.</summary>
    public byte[] Zmap { get; }
    /// <summary>The number of tiles the .ter must hold (ZMAP's count byte).</summary>
    public byte TileCount { get; set; }

    private readonly BwdNode? _zmapNode;
    private readonly byte[] _zmapTail;

    public MapDocument(BwdTree tree)
    {
        Tree = tree;
        Objects = [];
        foreach (var (_, n) in tree.Subs("ODEF", "OBJ\0")) if (n.Payload.Length >= 0x64) Objects.Add(new MapObject((byte[])n.Payload.Clone()));
        Roads = [];
        foreach (var (_, n) in tree.Subs("RDEF", "RSEG")) if (n.Payload.Length >= 8) Roads.Add(RoadEdit.Read(n.Payload));
        Lines = [];
        foreach (var (_, n) in tree.Subs("LDEF", "OBJ\0")) if (LineEdit.Read(n.Payload) is { } l) Lines.Add(l);
        _zmapNode = tree.Subs("TDEF", "ZMAP").Select(s => s.Node).FirstOrDefault(n => n.Payload.Length >= 1 + MissionFile.ZmapGrid * MissionFile.ZmapGrid);
        Zmap = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        _zmapTail = [];
        if (_zmapNode is not null)
        {
            TileCount = _zmapNode.Payload[0];
            Array.Copy(_zmapNode.Payload, 1, Zmap, 0, Zmap.Length);
            _zmapTail = _zmapNode.Payload.AsSpan(1 + Zmap.Length).ToArray();
        }
    }

    public static MapDocument Parse(byte[] data) => new(BwdTree.Parse(data));
    public static MapDocument Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Apply the edits to the tree and write the file.</summary>
    public byte[] ToBytes()
    {
        Tree.ReplaceSubs("ODEF", "OBJ\0", Objects.Select(o => new BwdNode("OBJ\0", (byte[])o.Payload.Clone())).ToList(), n => n.Payload.Length >= 0x64);
        Tree.ReplaceSubs("RDEF", "RSEG", Roads.Select(r => new BwdNode("RSEG", r.ToPayload())).ToList(), n => n.Payload.Length >= 8);
        Tree.ReplaceSubs("LDEF", "OBJ\0", Lines.Select(l => new BwdNode("OBJ\0", l.ToPayload())).ToList(), n => LineEdit.Read(n.Payload) is not null);
        WritePaths();
        if (_zmapNode is not null)
        {
            var p = new byte[1 + Zmap.Length + _zmapTail.Length];
            p[0] = TileCount;
            Zmap.CopyTo(p, 1);
            _zmapTail.CopyTo(p, 1 + Zmap.Length);
            _zmapNode.Payload = p;
        }
        return Tree.ToBytes();
    }

    // ---- WDEF ----

    /// <summary>The WRLD payload (331 bytes in WREV 9, 318 in WREV 8), edited in place.</summary>
    public byte[]? WorldPayload => Tree.Subs("WDEF", "WRLD").Select(s => s.Node.Payload).FirstOrDefault();

    /// <summary>WDEF/WREV (9 in every Nitro and GOG file, 8 in the 1997 editor's levels), 0 if absent.</summary>
    public uint WorldRevision => Tree.Subs("WDEF", "WREV").Select(s => s.Node.Payload).FirstOrDefault() is { Length: >= 4 } p ? Bin.U32(p, 0) : 0;

    private int ArenaOffset => WorldRevision == 8 ? ArenaNameAt - WorldSettings.OldLayoutShift : ArenaNameAt;

    /// <summary>
    /// The name the games list the map under: 16 bytes at WRLD payload + 0x13b (+0x12e in a WREV 8 level), NUL-padded.
    /// Null if the chunk is too short.
    /// </summary>
    public string? ArenaName
    {
        get => WorldPayload is { } p && p.Length >= ArenaOffset + ArenaNameSize ? Bin.CStr(p, ArenaOffset, ArenaNameSize) : null;
        set
        {
            if (WorldPayload is not { } p || p.Length < ArenaOffset + ArenaNameSize) throw new InvalidOperationException("no WRLD chunk with a name field");
            if (value is null || value.Length > ArenaNameSize - 1 || value.Any(c => c is < ' ' or >= (char)127))
                throw new ArgumentException($"arena name: up to {ArenaNameSize - 1} printable ASCII characters");
            Array.Clear(p, ArenaOffset, ArenaNameSize);
            Encoding.Latin1.GetBytes(value, 0, value.Length, p, ArenaOffset);
        }
    }

    /// <summary>The time of day (WRLD +0x93; picks the light preset).</summary>
    public int Hour
    {
        get => Bin.I32(WorldPayload!, WorldRevision == 8 ? WrldHourAt - 13 : WrldHourAt);
        set => BitConverter.TryWriteBytes(WorldPayload.AsSpan(WorldRevision == 8 ? WrldHourAt - 13 : WrldHourAt), value);
    }

    /// <summary>
    /// The map's look: palette, sky, ground family, horizon (WREV 9) and hour (<see cref="MapLook"/>). Setting it writes only the
    /// fields that differ, so a look read and set again leaves the file as it was. Null if the WRLD is too short.
    /// </summary>
    public MapLook? Look
    {
        get => WorldPayload is { } p ? MapLook.Read(p, WorldRevision) : null;
        set
        {
            if (WorldPayload is not { } p) throw new InvalidOperationException("no WRLD chunk");
            (value ?? throw new ArgumentNullException(nameof(value))).WriteTo(p, WorldRevision);
        }
    }

    /// <summary>The CD track (WRLD +0).</summary>
    public uint CdTrack
    {
        get => Bin.U32(WorldPayload!, 0);
        set => BitConverter.TryWriteBytes(WorldPayload.AsSpan(0), value);
    }

    /// <summary>The style as the game reads it.</summary>
    public WorldSettings? World => WorldSettings.Read(new MissionFile("", Tree.ToBytes()));

    // ---- TDEF ----

    private BwdNode? Zone => Tree.Subs("TDEF", "ZONE").Select(s => s.Node).FirstOrDefault();

    /// <summary>The .ter the mission names (TDEF/ZONE: a byte, then a 13-byte name), or null.</summary>
    public string? TerrainName
    {
        get => Zone is { Payload.Length: > 1 } z ? Bin.CStr(z.Payload, 1, z.Payload.Length - 1) : null;
        set
        {
            if (Zone is not { } z || z.Payload.Length < 1 + ZoneNameSize) throw new InvalidOperationException("no TDEF/ZONE chunk");
            if (value is null || value.Length == 0 || value.Length >= ZoneNameSize || value.Any(c => c is <= ' ' or >= (char)127 or '\\' or '/'))
                throw new ArgumentException("terrain name: 1-12 printable ASCII characters, no folder");
            Array.Clear(z.Payload, 1, z.Payload.Length - 1);
            Encoding.Latin1.GetBytes(value, 0, value.Length, z.Payload, 1);
        }
    }

    // ---- ADEF ----

    /// <summary>The FSM script payload. The writer changes only its nav-path table (<see cref="Paths"/>), and only when that was edited.</summary>
    public byte[] FsmPayload => Tree.Subs("ADEF", "FSM\0").Select(s => s.Node.Payload).FirstOrDefault() ?? [];

    private List<PathEntry>? _paths;

    /// <summary>
    /// The script's nav paths, in table order (the script names a path by its index: replace or append entries, do not move them).
    /// Read on first use; <see cref="ToBytes"/> writes them into the payload's table, the rest of the script as it was.
    /// </summary>
    public List<PathEntry> Paths => _paths ??= FsmPaths.Read(FsmPayload);

    private void WritePaths()
    {
        if (_paths is null) return;
        var old = FsmPayload;
        var now = FsmPaths.Write(old, _paths);
        if (now.AsSpan().SequenceEqual(old)) return;
        if (Tree.Subs("ADEF", "FSM\0").Select(s => s.Node).FirstOrDefault() is { } node) { node.Payload = now; return; }
        // no script chunk at all (every stock file has one, empty in a melee map): one goes into ADEF before its EXIT
        var adef = Tree.Chunks.FirstOrDefault(c => c.Tag == "ADEF" && c.Children is not null) ?? throw new InvalidOperationException("the map has no ADEF chunk to keep its paths in");
        int exit = adef.Children!.FindIndex(c => c.Tag == "EXIT");
        adef.Children.Insert(exit < 0 ? adef.Children.Count : exit, new BwdNode("FSM\0", now));
    }

    // ---- placed-object helpers ----

    public IEnumerable<MapObject> WithRole(MapRole role) => Objects.Where(o => o.Role == role);
    public IEnumerable<MapObject> Spawns => WithRole(MapRole.Spawn);
    public IEnumerable<MapObject> Regens => WithRole(MapRole.Regen);
    /// <summary>Race checkpoints in number order (check99, the finish, last).</summary>
    public IEnumerable<MapObject> Checkpoints => WithRole(MapRole.Checkpoint).OrderBy(o => o.Number);
    public IEnumerable<MapObject> Flags => WithRole(MapRole.Flag).OrderBy(o => o.Number);

    /// <summary>Remove every placed object, road and line (the FSM, WRLD and terrain stay): the start of a blank map.</summary>
    public void ClearContent() { Objects.Clear(); Roads.Clear(); Lines.Clear(); }
}
