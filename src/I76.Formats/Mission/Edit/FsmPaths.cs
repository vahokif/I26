using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace I76.Formats.Mission.Edit;

/// <summary>
/// One entry of a mission script's nav-path table: a label and its points. The script names a path by its index in the table,
/// so an entry is replaced in place (or appended), never moved. The points are world x, y, z; every stock file keeps y at 0 (the
/// game takes the ground's height where it needs one).
/// </summary>
public sealed class PathEntry(string name, IEnumerable<Vector3> points)
{
    public string Name { get; } = name;
    public IReadOnlyList<Vector3> Points { get; } = [.. points];
    /// <summary>The label's 40 bytes as the file has them, written back while the label is the one read.</summary>
    internal byte[]? Slot { get; init; }

    public PathEntry With(string? name = null, IEnumerable<Vector3>? points = null) =>
        new(name ?? Name, points ?? Points) { Slot = name is null || name == Name ? Slot : null };

    /// <summary>The same label and the same points (the script compiler writes one copy of a path for every place that names it).</summary>
    public bool SameAs(PathEntry o) => Name == o.Name && Points.SequenceEqual(o.Points);

    public NavPath ToNavPath() => new(Name, [.. Points]);
}

/// <summary>
/// The nav-path table inside an ADEF/FSM payload (u32 prototype count and 40-byte names, u32 actor count and
/// 48-byte records, u32 sound count and 40-byte names, then u32 path count and per path a 40-byte label, a u32 point count and
/// that many float triples; the threads, data and code follow). Reading and writing touch only the path table: the rest of the
/// payload is kept byte for byte. An empty payload (a map with no script) reads as no paths; written with paths it gets empty
/// prototype, actor and sound tables before them and empty thread, data and code tables after, which the game reads as it reads
/// the empty payload (its loader treats a payload of no bytes as all those counts zero).
/// </summary>
public static class FsmPaths
{
    public const int LabelSize = MissionFile.FsmPathName;
    /// <summary>The longest label: the slot keeps a terminating NUL.</summary>
    public const int MaxLabel = LabelSize - 1;
    private static readonly int[] Strides = [40, 48, 40];

    /// <summary>Where the path table is (its count's offset and the first byte after it), or null when the payload is empty or does not parse that far.</summary>
    public static (int Start, int End)? Locate(byte[] payload)
    {
        if (payload.Length < 4) return null;
        long off = 0;
        foreach (int stride in Strides)
        {
            if (off + 4 > payload.Length) return null;
            off += 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan((int)off)) * stride;
        }
        if (off + 4 > payload.Length) return null;
        int start = (int)off;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(start));
        off += 4;
        for (uint i = 0; i < n; i++)
        {
            if (off + LabelSize + 4 > payload.Length) return null;
            off += LabelSize + 4 + 12L * BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan((int)off + LabelSize));
            if (off > payload.Length) return null;
        }
        return (start, (int)off);
    }

    /// <summary>
    /// Whether <see cref="Write"/> can put a path table into the payload: it is empty (a map with no script) or parses up to the
    /// end of its table. A truncated or non-standard script chunk cannot hold paths; the editor leaves its paths alone.
    /// </summary>
    public static bool CanHold(byte[] payload) => payload.Length == 0 || Locate(payload) is not null;

    public static List<PathEntry> Read(byte[] payload)
    {
        var o = new List<PathEntry>();
        if (Locate(payload) is not { } at) return o;
        int off = at.Start;
        int n = (int)Bin.U32(payload, off);
        off += 4;
        for (int i = 0; i < n; i++)
        {
            var slot = payload.AsSpan(off, LabelSize).ToArray();
            int count = (int)Bin.U32(payload, off + LabelSize);
            int b = off + LabelSize + 4;
            var pts = new Vector3[count];
            for (int k = 0; k < count; k++) pts[k] = new Vector3(Bin.F32(payload, b + k * 12), Bin.F32(payload, b + k * 12 + 4), Bin.F32(payload, b + k * 12 + 8));
            o.Add(new PathEntry(Bin.CStr(slot), pts) { Slot = slot });
            off = b + count * 12;
        }
        return o;
    }

    /// <summary>
    /// The payload with its path table replaced by <paramref name="paths"/>. An empty payload with no paths stays empty. Throws
    /// when a non-empty payload does not parse (it is not a script the editor can put paths into: <see cref="CanHold"/>).
    /// </summary>
    public static byte[] Write(byte[] payload, IReadOnlyList<PathEntry> paths)
    {
        if (payload.Length == 0 && paths.Count == 0) return [];
        byte[] head, tail;
        if (payload.Length == 0) { head = new byte[12]; tail = new byte[12]; }   // no prototypes, actors, sounds; no threads, data, code
        else
        {
            var at = Locate(payload) ?? throw new InvalidDataException("the mission's script does not parse up to its path table");
            head = payload.AsSpan(0, at.Start).ToArray();
            tail = payload.AsSpan(at.End).ToArray();
        }
        using var ms = new MemoryStream();
        ms.Write(head);
        Span<byte> u = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)paths.Count);
        ms.Write(u);
        foreach (var p in paths)
        {
            ms.Write(Label(p));
            BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)p.Points.Count);
            ms.Write(u);
            foreach (var v in p.Points)
            {
                BinaryPrimitives.WriteSingleLittleEndian(u, v.X); ms.Write(u);
                BinaryPrimitives.WriteSingleLittleEndian(u, v.Y); ms.Write(u);
                BinaryPrimitives.WriteSingleLittleEndian(u, v.Z); ms.Write(u);
            }
        }
        ms.Write(tail);
        var o = ms.ToArray();
        // a script of nothing but empty tables is the empty payload of a map without one
        return paths.Count == 0 && o.Length == 28 && !o.AsSpan().ContainsAnyExcept((byte)0) ? [] : o;
    }

    /// <summary>Whether a label can be written: 1 to 39 printable ASCII characters.</summary>
    public static bool ValidLabel(string label) => label.Length is >= 1 and <= MaxLabel && label.All(c => c is >= ' ' and <= '~');

    private static byte[] Label(PathEntry p)
    {
        if (p.Slot is { Length: LabelSize } s && Bin.CStr(s) == p.Name) return s;
        if (!ValidLabel(p.Name)) throw new ArgumentException($"path label '{p.Name}': 1-{MaxLabel} printable ASCII characters");
        var b = new byte[LabelSize];
        Encoding.Latin1.GetBytes(p.Name, 0, p.Name.Length, b, 0);
        return b;
    }
}
