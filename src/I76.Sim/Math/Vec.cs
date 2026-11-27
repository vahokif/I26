using System.Numerics;
using I76.Formats;

namespace I76.Sim.Math;

/// <summary>A double-precision point: the engine keeps object positions as doubles.</summary>
public record struct Double3(double X, double Y, double Z)
{
    public static Double3 operator +(Double3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Double3 operator -(Double3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public static Double3 From(Vector3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// An engine rotation: the object's right, up and forward axes as rows, as in the object. Local to world is Eᵀ·p (the original's point transform).
/// </summary>
public struct Rows
{
    public Vector3 Right, Up, Forward;

    public static readonly Rows Identity = new() { Right = Vector3.UnitX, Up = Vector3.UnitY, Forward = Vector3.UnitZ };

    public Rows(Vector3 right, Vector3 up, Vector3 forward) { Right = right; Up = up; Forward = forward; }

    public static Rows FromEngine(IReadOnlyList<float> m) =>
        new(new(m[0], m[1], m[2]), new(m[3], m[4], m[5]), new(m[6], m[7], m[8]));

    public float[] ToEngine() => [Right.X, Right.Y, Right.Z, Up.X, Up.Y, Up.Z, Forward.X, Forward.Y, Forward.Z];

    /// <summary>Local to world (Eᵀ·v).</summary>
    public Vector3 ToWorld(Vector3 v) => Right * v.X + Up * v.Y + Forward * v.Z;

    /// <summary>World to local (E·v):.</summary>
    public Vector3 ToLocal(Vector3 v) => new(Vector3.Dot(Right, v), Vector3.Dot(Up, v), Vector3.Dot(Forward, v));

    /// <summary>
    /// The original's rotation composition of a and b: the rows of a expressed through b,
    /// so a local rotation a applied to an object whose rotation is b.
    /// </summary>
    public static Rows Concat(Rows a, Rows b) => new(b.ToWorld(a.Right), b.ToWorld(a.Up), b.ToWorld(a.Forward));

    /// <summary>Rotation about local Y, as the engine builds it (m00=c, m02=-s, m20=s, m22=c).</summary>
    public static Rows AboutY(float angle)
    {
        var (s, c) = DetMath.SinCos(angle);
        return new(new(c, 0, -s), Vector3.UnitY, new(s, 0, c));
    }

    /// <summary>Rotation about local X (the pitch part): up (0, c, s), forward (0, −s, c).</summary>
    public static Rows AboutX(float a)
    {
        var (s, c) = DetMath.SinCos(a);
        return new(Vector3.UnitX, new(0, c, s), new(0, -s, c));
    }

    /// <summary>Rotation about local Z (the roll part): right (c, s, 0), up (−s, c, 0).</summary>
    public static Rows AboutZ(float a)
    {
        var (s, c) = DetMath.SinCos(a);
        return new(new(c, s, 0), new(-s, c, 0), Vector3.UnitZ);
    }

    /// <summary>Z · X · Y, composed as the original's rotation composition does.</summary>
    public static Rows Euler(float pitch, float yaw, float roll) => Concat(AboutZ(roll), Concat(AboutX(pitch), AboutY(yaw)));

    public Rows Orthonormalized()
    {
        var f = Vector3.Normalize(Forward);
        var r = Vector3.Normalize(Vector3.Cross(Up, f));
        var u = Vector3.Cross(f, r);
        return new(r, u, f);
    }
}

public static class VecOps
{
    /// <summary>Split v into its part along n and the rest.</summary>
    public static (Vector3 Along, Vector3 Across) Split(Vector3 v, Vector3 n)
    {
        float k = Vector3.Dot(v, n) / Vector3.Dot(n, n);
        var along = n * k;
        return (along, v - along);
    }
}

/// <summary>
/// The engine's own reciprocal square root, which its vector normalisation uses: a seed of 8 bits from a 256-entry
/// table indexed by the exponent's parity and the top 7 bits of the mantissa, one Newton step, then a factor of 1.00001.
/// It is within about 1e-5 of 1/√x, and for most x a little over it, so a vector it normalises comes out slightly longer
/// than 1 (a far car's levelled forward row is about 1.0000043 long).
/// </summary>
public static class EngineRsqrt
{
    private static readonly byte[] Seed = BuildSeed();

    private static byte[] BuildSeed()
    {
        var t = new byte[256];
        for (uint i = 0; i < 256; i++)
        {
            double x = BitConverter.UInt64BitsToDouble((ulong)((0x1ff00u | i) << 13) << 32);
            uint hi = (uint)(BitConverter.DoubleToUInt64Bits(1.0 / System.Math.Sqrt(x)) >> 32);
            t[i] = (byte)((hi + 0x400) >> 12);
        }
        // the entry for [1, 1 + 1/128): 1/√1 has the next exponent up, and its mantissa bits are zero
        t[128] = 0xff;
        return t;
    }

    public static double Of(double x)
    {
        uint hi = (uint)(BitConverter.DoubleToUInt64Bits(x) >> 32);
        uint exp = ((0xbfcu - (hi >> 20)) >> 1) << 20;
        double y = BitConverter.UInt64BitsToDouble((ulong)(exp | (uint)Seed[(hi >> 13) & 0xff] << 12) << 32);
        return 0.5 * y * (3.0 - x * (y * y)) * 1.00001;
    }
}
