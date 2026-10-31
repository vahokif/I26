using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using I76.Sim.Math;

namespace I76.Net;

/// <summary>
/// A growable little-endian byte buffer for the messages: fixed-size
/// numbers, LEB128 varints, half floats, 6-byte rotations and short strings.
/// </summary>
public sealed class NetWriter
{
    private byte[] _buf;
    public int Length { get; private set; }

    public NetWriter(int capacity = 256) => _buf = new byte[capacity];

    /// <summary>The connection's interned names (<see cref="Name"/>); null writes every name in full.</summary>
    public NameTable? Names { get; set; }

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);
    public byte[] ToArray() => Span.ToArray();
    public void Clear() => Length = 0;

    private Span<byte> Take(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var s = _buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public void U8(byte v) => Take(1)[0] = v;
    public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public void I8(sbyte v) => Take(1)[0] = (byte)v;
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void I16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);
    public void Half(float v) => BinaryPrimitives.WriteHalfLittleEndian(Take(2), (Half)v);
    public void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));

    /// <summary>An unsigned LEB128 varint: 1 byte below 128, 2 below 16384.</summary>
    public void VarU(uint v)
    {
        while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
        U8((byte)v);
    }

    /// <summary>A signed varint (zigzag).</summary>
    public void VarI(int v) => VarU((uint)((v << 1) ^ (v >> 31)));

    /// <summary>A string of at most 255 UTF-8 bytes (longer ones are cut).</summary>
    public void Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(b.Length, 255);
        U8((byte)n);
        Bytes(b.AsSpan(0, n));
    }

    /// <summary>
    /// A name the receiver may already have (an effect's or a sound's file): its number in the connection's
    /// <see cref="NameTable"/> + 1, or 0 and the string the first time, which both ends then add. The connection's
    /// messages must be read in the order written (the reliable ordered channel).
    /// </summary>
    public void Name(string s)
    {
        if (Names?.IdOf(s) is { } id) { VarU((uint)id + 1); return; }
        VarU(0);
        Str(s);
        Names?.Learn(s);
    }

    public void BlobU16(ReadOnlySpan<byte> b)
    {
        int n = Math.Min(b.Length, ushort.MaxValue);
        U16((ushort)n);
        Bytes(b[..n]);
    }

    public void Vec(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
    public void HalfVec(Vector3 v) { Half(v.X); Half(v.Y); Half(v.Z); }
    public void Pos(Double3 p) { F32((float)p.X); F32((float)p.Y); F32((float)p.Z); }

    /// <summary>A unit vector in 4 bytes (octahedral, 16 bits a coordinate).</summary>
    public void Dir(Vector3 d)
    {
        var (u, v) = Wire.Octa(d);
        I16(u); I16(v);
    }

    /// <summary>
    /// A rotation in 6 bytes: the quaternion's three smallest components at 15
    /// bits each and the largest's index (about 4e-5 rad).
    /// </summary>
    public void Rot(Rows r)
    {
        ulong packed = Wire.PackQuat(Wire.ToQuat(r));
        Span<byte> b = Take(6);
        for (int i = 0; i < 6; i++) b[i] = (byte)(packed >> (8 * i));
    }
}

/// <summary>Reads what <see cref="NetWriter"/> wrote; running off the end throws <see cref="WireException"/>.</summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _b;
    public int Position { get; private set; }

    public NetReader(ReadOnlySpan<byte> b) { _b = b; Position = 0; Names = null; }

    /// <summary>The connection's interned names (<see cref="NetWriter.Name"/>).</summary>
    public NameTable? Names;

    public readonly int Remaining => _b.Length - Position;
    public readonly bool End => Position >= _b.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || Position + n > _b.Length) throw new WireException($"message cut short at {Position} (+{n} of {_b.Length})");
        var s = _b.Slice(Position, n);
        Position += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public sbyte I8() => (sbyte)Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public float Half() => (float)BinaryPrimitives.ReadHalfLittleEndian(Take(2));
    public ReadOnlySpan<byte> Bytes(int n) => Take(n);

    public uint VarU()
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = U8();
            v |= (uint)(b & 0x7f) << shift;
            if (b < 0x80) return v;
        }
        throw new WireException("varint too long");
    }

    public int VarI() { uint u = VarU(); return (int)(u >> 1) ^ -(int)(u & 1); }

    public string Str() => Encoding.UTF8.GetString(Take(U8()));

    /// <summary>What <see cref="NetWriter.Name"/> wrote.</summary>
    public string Name()
    {
        uint id = VarU();
        if (id == 0)
        {
            var s = Str();
            Names?.Learn(s);
            return s;
        }
        return Names?.Get((int)Math.Min(id - 1, int.MaxValue)) ?? throw new WireException($"name {id - 1} not known");
    }
    public byte[] BlobU16() => Take(U16()).ToArray();
    public Vector3 Vec() => new(F32(), F32(), F32());
    public Vector3 HalfVec() => new(Half(), Half(), Half());
    public Double3 Pos() => new(F32(), F32(), F32());
    public Vector3 Dir() { short u = I16(), v = I16(); return Wire.UnOcta(u, v); }

    public Rows Rot()
    {
        var b = Take(6);
        ulong packed = 0;
        for (int i = 0; i < 6; i++) packed |= (ulong)b[i] << (8 * i);
        return Wire.ToRows(Wire.UnpackQuat(packed));
    }
}

public sealed class WireException(string message) : Exception(message);

/// <summary>
/// The names a connection has sent once and sends by number since (protocol 13: the events' effect and sound files):
/// both ends add each new one in the same order, up to <see cref="Max"/>; past that names go in full.
/// </summary>
public sealed class NameTable
{
    public const int Max = 4096;
    private readonly Dictionary<string, int> _ids = [];
    private readonly List<string> _names = [];
    public int Count => _names.Count;
    public int? IdOf(string s) => _ids.TryGetValue(s, out var id) ? id : null;
    public string? Get(int id) => id < _names.Count ? _names[id] : null;

    public void Learn(string s)
    {
        if (_names.Count >= Max) return;
        _ids.TryAdd(s, _names.Count);
        _names.Add(s);
    }

    public void Clear() { _ids.Clear(); _names.Clear(); }
}

/// <summary>The encodings' arithmetic.</summary>
public static class Wire
{
    public static Quaternion ToQuat(Rows r)
    {
        // rows are the axes: local v → v·M is Rows.ToWorld
        var m = new Matrix4x4(r.Right.X, r.Right.Y, r.Right.Z, 0, r.Up.X, r.Up.Y, r.Up.Z, 0, r.Forward.X, r.Forward.Y, r.Forward.Z, 0, 0, 0, 0, 1);
        var q = Quaternion.CreateFromRotationMatrix(m);
        return q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    public static Rows ToRows(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        return new Rows(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33));
    }

    private const float Sqrt2 = 1.41421356f;
    private const int QMax = 16383;

    public static ulong PackQuat(Quaternion q)
    {
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        int big = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[big])) big = i;
        float sign = c[big] < 0 ? -1 : 1;
        ulong packed = (ulong)big << 45;
        int shift = 30;
        for (int i = 0; i < 4; i++)
        {
            if (i == big) continue;
            int v = (int)MathF.Round(Math.Clamp(c[i] * sign * Sqrt2, -1f, 1f) * QMax) + QMax;
            packed |= (ulong)v << shift;
            shift -= 15;
        }
        return packed;
    }

    public static Quaternion UnpackQuat(ulong packed)
    {
        int big = (int)(packed >> 45) & 3;
        Span<float> c = stackalloc float[4];
        int shift = 30;
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == big) continue;
            int v = (int)(packed >> shift) & 0x7fff;
            c[i] = (v - QMax) / (float)QMax / Sqrt2;
            sum += c[i] * c[i];
            shift -= 15;
        }
        c[big] = MathF.Sqrt(MathF.Max(0, 1 - sum));
        return Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
    }

    public static (short U, short V) Octa(Vector3 d)
    {
        if (d.LengthSquared() < 1e-12f) d = Vector3.UnitZ;
        d /= MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
        float u = d.X, v = d.Y;
        if (d.Z < 0)
        {
            u = (1 - MathF.Abs(d.Y)) * (d.X >= 0 ? 1 : -1);
            v = (1 - MathF.Abs(d.X)) * (d.Y >= 0 ? 1 : -1);
        }
        return ((short)MathF.Round(u * 32767), (short)MathF.Round(v * 32767));
    }

    public static Vector3 UnOcta(short su, short sv)
    {
        float u = su / 32767f, v = sv / 32767f;
        var d = new Vector3(u, v, 1 - MathF.Abs(u) - MathF.Abs(v));
        if (d.Z < 0)
        {
            float x = (1 - MathF.Abs(v)) * (u >= 0 ? 1 : -1);
            float y = (1 - MathF.Abs(u)) * (v >= 0 ? 1 : -1);
            d.X = x; d.Y = y;
        }
        return Vector3.Normalize(d);
    }

    /// <summary>A value in [−range, range] as a signed 16-bit fraction.</summary>
    public static short Q16(float v, float range) => (short)Math.Clamp(MathF.Round(v / range * 32767), -32767, 32767);
    public static float UnQ16(short q, float range) => q / 32767f * range;
    public static sbyte Q8(float v, float range) => (sbyte)Math.Clamp(MathF.Round(v / range * 127), -127, 127);
    public static float UnQ8(sbyte q, float range) => q / 127f * range;
}
