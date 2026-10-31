using System.Numerics;

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
        float s = MathF.Sin(angle), c = MathF.Cos(angle);
        return new(new(c, 0, -s), Vector3.UnitY, new(s, 0, c));
    }

    /// <summary>Rotation about local X (the pitch part): up (0, c, s), forward (0, −s, c).</summary>
    public static Rows AboutX(float a)
    {
        float s = MathF.Sin(a), c = MathF.Cos(a);
        return new(Vector3.UnitX, new(0, c, s), new(0, -s, c));
    }

    /// <summary>Rotation about local Z (the roll part): right (c, s, 0), up (−s, c, 0).</summary>
    public static Rows AboutZ(float a)
    {
        float s = MathF.Sin(a), c = MathF.Cos(a);
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
