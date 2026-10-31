using System.Numerics;
using System.Text;

namespace I76.Formats.Mission.Edit;

/// <summary>What a placed object means to the melee, race and CTF loaders.</summary>
public enum MapRole
{
    /// <summary>An ordinary object: scenery, a vehicle, a building.</summary>
    Object,
    /// <summary>"spawn" / "#spawnN": a player start (N is the CTF team, 1-5; plain spawn is team 0 / free-for-all).</summary>
    Spawn,
    /// <summary>"regen": a regeneration pad.</summary>
    Regen,
    /// <summary>"checkN" (N 1..98) a race checkpoint; "check99" the finish.</summary>
    Checkpoint,
    /// <summary>"a1flagN": a CTF flag (N is the team).</summary>
    Flag,
}

/// <summary>
/// One ODEF OBJ: a placed object. The object is the 100-byte payload itself, so every byte a property does not touch (the
/// 36 bytes at 0x38 and the rest) is written back as it was read. Name bytes carry per-placement flag bits in their
/// top bits (<see cref="NameFlags"/>); <see cref="Name"/> is the 7-bit text.
/// </summary>
public sealed class MapObject
{
    public const int PayloadSize = 100;
    public const int ObjectTypeAt = 0x5c, ObjectFlagsAt = 0x60;

    /// <summary>The payload, edited in place.</summary>
    public byte[] Payload { get; }

    public MapObject(byte[] payload)
    {
        if (payload.Length < 0x64) throw new InvalidDataException($"OBJ payload of {payload.Length} bytes");
        Payload = payload;
    }

    /// <summary>A new object at a position with yaw (radians about +Y, 0 facing +Z) and the given type.</summary>
    public static MapObject Create(string name, uint objectType, Vector3 position, float yaw = 0, uint objectFlags = 0)
    {
        var o = new MapObject(new byte[PayloadSize]) { Name = name, ObjectType = objectType, ObjectFlags = objectFlags, Position = position };
        o.Yaw = yaw;
        return o;
    }

    public static MapObject Spawn(Vector3 position, float yaw = 0, int team = 0) =>
        Create(team > 0 ? $"#spawn{team}" : "spawn", 1, position, yaw);
    public static MapObject Regen(Vector3 position, float yaw = 0) => Create("regen", 1, position, yaw);
    public static MapObject Checkpoint(int number, Vector3 position, float yaw = 0) => Create($"check{number}", 1, position, yaw);
    public static MapObject Flag(int team, Vector3 position, float yaw = 0) => Create($"a1flag{team}", 2, position, yaw);

    public MapObject Clone() => new((byte[])Payload.Clone());

    /// <summary>The 7-bit name (up to 8 characters; the loaders strip a leading '#').</summary>
    public string Name
    {
        get
        {
            Span<byte> m = stackalloc byte[8];
            for (int i = 0; i < 8; i++) m[i] = (byte)(Payload[i] & MissionFile.NameMask);
            return Bin.CStr(m);
        }
        set
        {
            if (value.Length > 8 || value.Any(c => c is < ' ' or >= (char)127)) throw new ArgumentException($"object name '{value}': 1-8 printable ASCII characters");
            byte f = NameFlags;
            for (int i = 0; i < 8; i++) Payload[i] = (byte)(i < value.Length ? value[i] : 0);
            NameFlags = f;
        }
    }

    /// <summary>The top bit of each name byte, bit i for byte i (per-placement data, see Q28).</summary>
    public byte NameFlags
    {
        get { int f = 0; for (int i = 0; i < 8; i++) f |= ((Payload[i] >> 7) & 1) << i; return (byte)f; }
        set { for (int i = 0; i < 8; i++) Payload[i] = (byte)((Payload[i] & 0x7f) | (((value >> i) & 1) << 7)); }
    }

    /// <summary>The 3x3 rotation, row-major as stored (rows are the object's axes; row 2 is forward).</summary>
    public float[] Rotation
    {
        get => Bin.F32s(Payload, 8, 9);
        set
        {
            if (value.Length != 9) throw new ArgumentException("nine floats");
            for (int i = 0; i < 9; i++) BitConverter.TryWriteBytes(Payload.AsSpan(8 + 4 * i), value[i]);
        }
    }

    /// <summary>Heading about +Y in radians (0 faces +Z); setting it replaces the rotation with a pure yaw.</summary>
    public float Yaw
    {
        get { var r = Rotation; return MathF.Atan2(r[6], r[8]); }
        set { float s = MathF.Sin(value), c = MathF.Cos(value); Rotation = [c, 0, -s, 0, 1, 0, s, 0, c]; }
    }

    public Vector3 Position
    {
        get => new(Bin.F32(Payload, 44), Bin.F32(Payload, 48), Bin.F32(Payload, 52));
        set
        {
            BitConverter.TryWriteBytes(Payload.AsSpan(44), value.X);
            BitConverter.TryWriteBytes(Payload.AsSpan(48), value.Y);
            BitConverter.TryWriteBytes(Payload.AsSpan(52), value.Z);
        }
    }

    /// <summary>+0x5c: 1 vehicle, 2/3 flag-like, 3 mesh scenery, 4 sphere, 0xb-0xd structures, 0x52 road pieces...</summary>
    public uint ObjectType
    {
        get => Bin.U32(Payload, ObjectTypeAt);
        set => BitConverter.TryWriteBytes(Payload.AsSpan(ObjectTypeAt), value);
    }

    /// <summary>+0x60: the object's flags; 0x10 marks the player's car.</summary>
    public uint ObjectFlags
    {
        get => Bin.U32(Payload, ObjectFlagsAt);
        set => BitConverter.TryWriteBytes(Payload.AsSpan(ObjectFlagsAt), value);
    }

    private string Stem => Name.TrimStart('#');

    public MapRole Role
    {
        get
        {
            var n = Stem;
            if (ObjectType == 1)
            {
                if (n.StartsWith("spawn", StringComparison.OrdinalIgnoreCase)) return MapRole.Spawn;
                if (n.Equals("regen", StringComparison.OrdinalIgnoreCase)) return MapRole.Regen;
                if (n.StartsWith("check", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[5..], out int c) && c is >= 1 and <= 99) return MapRole.Checkpoint;
            }
            else if (ObjectType is 2 or 3 && n.StartsWith("a1flag", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[6..], out _)) return MapRole.Flag;
            return MapRole.Object;
        }
    }

    /// <summary>The number in a checkpoint (1-99, 99 the finish), a flag (its team) or a CTF spawn (its team, 0 for a plain spawn); 0 otherwise.</summary>
    public int Number => Role switch
    {
        MapRole.Checkpoint => int.Parse(Stem[5..]),
        MapRole.Flag => int.Parse(Stem[6..]),
        MapRole.Spawn => int.TryParse(Stem[5..], out int n) ? n : 0,
        _ => 0,
    };

    public bool IsPlayer => (ObjectFlags & 0x10) != 0;
}

/// <summary>
/// One RSEG point, 24 bytes: the road's left edge (x, centre z, z), then the right edge (x, a flag word that is 0 or 36, z)
/// (docs/formats/mission-files.md; Roads.cs). The y words are not heights (the engine overwrites both from the terrain at
/// load): the left y holds the point's centre z. <see cref="Move"/> keeps that word consistent.
/// </summary>
public sealed class RoadEditPoint
{
    public const int Size = 24;
    public byte[] Raw { get; }

    public RoadEditPoint(byte[] raw) { if (raw.Length != Size) throw new ArgumentException("24 bytes"); Raw = raw; }

    public RoadEditPoint(Vector2 left, Vector2 right) : this(new byte[Size])
    {
        Left = left; Right = right; CentreZ = (left.Y + right.Y) / 2;
    }

    /// <summary>The left edge as (x, z).</summary>
    public Vector2 Left
    {
        get => new(Bin.F32(Raw, 0), Bin.F32(Raw, 8));
        set { Put(0, value.X); Put(8, value.Y); }
    }
    /// <summary>The right edge as (x, z).</summary>
    public Vector2 Right
    {
        get => new(Bin.F32(Raw, 12), Bin.F32(Raw, 20));
        set { Put(12, value.X); Put(20, value.Y); }
    }
    /// <summary>The left edge's y word: the centre z of the point (not a height).</summary>
    public float CentreZ { get => Bin.F32(Raw, 4); set => Put(4, value); }
    /// <summary>The right edge's y word, an integer: 0 or 36.</summary>
    public int Flag { get => Bin.I32(Raw, 16); set => BitConverter.TryWriteBytes(Raw.AsSpan(16), value); }
    public Vector2 Centre => (Left + Right) / 2;

    /// <summary>Move both edges by an offset and keep the centre-z word in step.</summary>
    public void Move(Vector2 delta)
    {
        Left += delta; Right += delta;
        CentreZ = (Left.Y + Right.Y) / 2;
    }

    /// <summary>Move the point so its centre is at <paramref name="centre"/>, keeping the road's width and heading.</summary>
    public void MoveTo(Vector2 centre) => Move(centre - Centre);

    public RoadEditPoint Clone() => new((byte[])Raw.Clone());

    private void Put(int at, float v) => BitConverter.TryWriteBytes(Raw.AsSpan(at), v);
}

/// <summary>An RSEG: a road segment (a run of points, drawn as strips) with its kind (texture family: kind % 3).</summary>
public sealed class RoadEdit
{
    public uint Kind { get; set; }
    public List<RoadEditPoint> Points { get; }
    /// <summary>Bytes after the last point in the payload (none in any stock file); kept.</summary>
    public byte[] Tail { get; set; } = [];

    public RoadEdit(uint kind, List<RoadEditPoint> points) { Kind = kind; Points = points; }

    internal static RoadEdit Read(byte[] b)
    {
        uint kind = Bin.U32(b, 0), count = Bin.U32(b, 4);
        int n = (int)Math.Min(count, (uint)((b.Length - 8) / RoadEditPoint.Size));
        var pts = new List<RoadEditPoint>(n);
        for (int k = 0; k < n; k++) pts.Add(new RoadEditPoint(b.AsSpan(8 + k * RoadEditPoint.Size, RoadEditPoint.Size).ToArray()));
        var tail = b.AsSpan(8 + n * RoadEditPoint.Size).ToArray();
        // a count that overstates the payload cannot be written back from the points alone: keep the header's count
        return new RoadEdit(kind, pts) { Tail = tail, _declared = count, _read = n };
    }

    // a count that overstates the payload cannot come from the points alone: while the points are as many as were read, the header's count is written back
    private uint _declared;
    private int _read = -1;

    internal byte[] ToPayload()
    {
        var o = new byte[8 + Points.Count * RoadEditPoint.Size + Tail.Length];
        BitConverter.TryWriteBytes(o.AsSpan(0), Kind);
        BitConverter.TryWriteBytes(o.AsSpan(4), Points.Count == _read ? _declared : (uint)Points.Count);
        for (int k = 0; k < Points.Count; k++) Points[k].Raw.CopyTo(o, 8 + k * RoadEditPoint.Size);
        Tail.CopyTo(o, 8 + Points.Count * RoadEditPoint.Size);
        return o;
    }

    /// <summary>A copy that shares nothing with this road (the header's count is not kept: the copy writes its own).</summary>
    public RoadEdit Clone() => new(Kind, Points.Select(p => p.Clone()).ToList()) { Tail = (byte[])Tail.Clone() };

    /// <summary>Total length of the centre line.</summary>
    public float Length() { float l = 0; for (int i = 1; i < Points.Count; i++) l += Vector2.Distance(Points[i - 1].Centre, Points[i].Centre); return l; }
}

/// <summary>An LDEF OBJ: a def strung along a polyline (fences, tyre walls): a 0x64-byte header, a point count, then xyz points.</summary>
public sealed class LineEdit
{
    public byte[] Header { get; }
    public List<Vector3> Points { get; }
    public byte[] Tail { get; set; } = [];
    private uint _declared;
    private int _read = -1;

    public LineEdit(byte[] header, List<Vector3> points) { Header = header; Points = points; }

    public string Name
    {
        get { var m = new byte[8]; for (int i = 0; i < 8; i++) m[i] = (byte)(Header[i] & MissionFile.NameMask); return Bin.CStr(m); }
        set
        {
            if (value.Length > 8) throw new ArgumentException("1-8 characters");
            for (int i = 0; i < 8; i++) Header[i] = (byte)((Header[i] & 0x80) | (i < value.Length ? value[i] : 0));
        }
    }

    internal static LineEdit? Read(byte[] p)
    {
        if (p.Length < 0x68) return null;
        uint n = Bin.U32(p, 0x64);
        int have = (int)Math.Min(n, (uint)((p.Length - 0x68) / 12));
        var pts = new List<Vector3>(have);
        for (int i = 0; i < have; i++) pts.Add(new Vector3(Bin.F32(p, 0x68 + 12 * i), Bin.F32(p, 0x6c + 12 * i), Bin.F32(p, 0x70 + 12 * i)));
        return new LineEdit(p[..0x64], pts) { Tail = p.AsSpan(0x68 + have * 12).ToArray(), _declared = n, _read = have };
    }

    internal byte[] ToPayload()
    {
        var o = new byte[0x68 + Points.Count * 12 + Tail.Length];
        Header.CopyTo(o, 0);
        BitConverter.TryWriteBytes(o.AsSpan(0x64), Points.Count == _read ? _declared : (uint)Points.Count);
        for (int i = 0; i < Points.Count; i++)
        {
            BitConverter.TryWriteBytes(o.AsSpan(0x68 + 12 * i), Points[i].X);
            BitConverter.TryWriteBytes(o.AsSpan(0x6c + 12 * i), Points[i].Y);
            BitConverter.TryWriteBytes(o.AsSpan(0x70 + 12 * i), Points[i].Z);
        }
        Tail.CopyTo(o, 0x68 + Points.Count * 12);
        return o;
    }
}
