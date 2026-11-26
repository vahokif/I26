using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;

namespace I76.Sim.Damage;

/// <summary>
/// The debris jitter table: 251 values, each the sum of ten rand()s from seed 0 scaled to −1..1 (mean 0, spread about
/// 0.18), read in turn, round and round, by every piece thrown.
/// </summary>
public sealed class DebrisTable
{
    public const int Count = 251;
    private static readonly float[] Values = Build(0);
    private int _next;

    internal static float[] Build(uint seed)
    {
        var rand = new CrtRand(seed);
        var v = new float[Count];
        for (int i = 0; i < Count; i++)
        {
            float s = 0;
            for (int k = 0; k < 10; k++) s -= rand.Next() * -3.0518509e-05f;
            v[i] = s * 0.2f - 1f;
        }
        return v;
    }

    /// <summary>The table's value at <paramref name="i"/>.</summary>
    public static float At(int i) => Values[i];

    /// <summary>The next value.</summary>
    public float Next()
    {
        float x = Values[_next];
        _next = (_next + 1) % Count;
        return x;
    }
}

/// <summary>
/// A piece thrown clear of the world: a sign knocked off, or anything placed with the Sign class id when a splash or a
/// round throws it. It has no collider and does no damage; it is only seen. It flies and turns about the middle of its
/// bounds: its velocity the throw's plus 10 x three table values, its spin (radians a second about its own axes) 20 x
/// the next three, kept until it rests. Gravity 9.8. When the middle of its bounds comes down to the ground under it,
/// it bounces off the ground's slope with a quarter of its speed, and rests where the bounce is below 0.1 u/s upwards,
/// tilted as it landed; one still rising there is gone. It is gone after 15 s.
/// </summary>
public sealed class ThrownPiece
{
    public const float Life = 15f;
    /// <summary>The middle of its bounds, in the world.</summary>
    public Vector3 Centre;
    public Rows Rot;
    public Vector3 Vel;
    /// <summary>About its own axes: pitch, yaw, roll.</summary>
    public Vector3 Spin;
    public float Age;
    /// <summary>The middle of its bounds in its own frame.</summary>
    public Vector3 Pivot { get; init; }
    /// <summary>Its own origin, in the world.</summary>
    public Vector3 Origin => Centre - Rot.ToWorld(Pivot);
    public bool Resting => Vel == Vector3.Zero && Spin == Vector3.Zero;

    /// <summary>A piece standing at <paramref name="origin"/> turned by <paramref name="rot"/>, thrown at <paramref name="vel"/>.</summary>
    public static ThrownPiece Throw(Vector3 origin, Rows rot, Vector3 pivot, Vector3 vel, DebrisTable table)
    {
        var p = new ThrownPiece { Rot = rot, Pivot = pivot, Centre = origin + rot.ToWorld(pivot) };
        float x = table.Next(), y = table.Next(), z = table.Next();
        p.Vel = vel + 10f * new Vector3(x, y, z);
        x = table.Next(); y = table.Next(); z = table.Next();
        p.Spin = 20f * new Vector3(x, y, z);
        return p;
    }

    /// <summary>One frame of <paramref name="dt"/> s; <paramref name="ground"/> gives the height and normal under a point. False once gone.</summary>
    public bool Step(float dt, Func<Vector3, (float Height, Vector3 Normal)> ground)
    {
        if (Age > Life) return false;
        Age += dt;
        float vy0 = Vel.Y;
        Centre += new Vector3(Vel.X * dt, (Vel.Y - 4.9f * dt) * dt, Vel.Z * dt);
        Vel.Y -= 9.8f * dt;
        Rot = Rows.Concat(Rows.Euler(-(dt * Spin.X), -(dt * Spin.Y), -(dt * Spin.Z)), Rot).Orthonormalized();
        var (h, n) = ground(Centre);
        if (Centre.Y <= h)
        {
            if ((Vel.Y + vy0) * 0.5f > 0) return false;
            Centre.Y = h;
            Vel = (Vel + n * (-2f * Vector3.Dot(n, Vel))) * 0.25f;
            if (MathF.Abs(Vel.Y) < 0.1f) { Vel = Vector3.Zero; Spin = Vector3.Zero; }
        }
        return true;
    }
}
