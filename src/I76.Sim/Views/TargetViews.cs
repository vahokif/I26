using System.Numerics;
using I76.Sim.Math;

namespace I76.Sim.Views;

/// <summary>
/// The geometry of the three views that follow something: F4 and F5, the target
/// views, and F11, the projectile camera.
/// Pure functions of the two positions (or the round's pose), in game axes (x right, y up, z ahead);
/// presentation only, nothing here is sim state.
/// </summary>
public static class TargetViews
{
    /// <summary>How far the eye stands from the pivot, along the line to the other car.</summary>
    public const float Behind = 7.5f;
    /// <summary>The pivot stands this far above the first car's origin (the original subtracts -3).</summary>
    public const float PivotLift = 3f;
    /// <summary>The look-at line runs to the other car's origin plus 2: d = T - (P.x, P.y - 2, P.z).</summary>
    public const float AimDrop = 2f;
    /// <summary>The tilt starts under 1/0.03 = 33.3 units and grows 7.5 rad per 1/unit up to 30 degrees.</summary>
    public const float TiltStart = 0.03f, TiltRate = 7.5f, TiltMax = 0.5235988f;
    /// <summary>The eye is held to 0.6 over the ground (cond: ground + 0.5 against y - 0.1).</summary>
    public const float GroundClear = 0.6f;
    /// <summary>The target view stays 3 s on the last place the target was seen, F11 stays 1 s on a round that is gone.</summary>
    public const float TargetGrace = 3f, ProjectileGrace = 1f;
    /// <summary>F11: 5 behind the round, pitched 20 degrees.</summary>
    public const float ProjectileBack = 5f, ProjectilePitch = 0.3490658503988659f;

    /// <summary>
    /// The eye looks from the first car towards the other. The pivot is 3 above the first car, the
    /// eye 7.5 back along the line to the other (its origin raised by 2 for the aim), and when the other is nearer than 33
    /// units the whole boom swings up about the pivot (up to 30 degrees, at 10 units or less), so the view looks down on it.
    /// <paramref name="ground"/> is the terrain height at (x, z); the eye is kept 0.6 above it; <paramref name="boom"/> is the port's wheel factor on the 7.5 (view only; 1 is the original).
    /// </summary>
    public static (Vector3 Eye, Vector3 Forward) Look(Vector3 first, Vector3 other, Func<float, float, float>? ground = null, float boom = 1f)
    {
        var pivot = new Vector3(first.X, first.Y + PivotLift, first.Z);
        var d = new Vector3(other.X - first.X, other.Y - (first.Y - AimDrop), other.Z - first.Z);
        float len2 = d.LengthSquared();
        if (len2 < 1e-12f) return (pivot, Vector3.UnitZ);
        float inv = 1f / MathF.Sqrt(len2);
        var n = d * inv;
        var fwd = n;
        var off = -Behind * boom * n;
        float h = MathF.Sqrt(n.X * n.X + n.Z * n.Z);
        if (inv > TiltStart && h > 1e-6f)
        {
            float theta = MathF.Min((inv - TiltStart) * TiltRate, TiltMax);
            // a rotation by -theta about (nz, 0, -nx)/h, concatenated after the look matrix: the axis is up x n, so n tilts down and
            // the offset (-7.5 n) swings up
            var q = Quaternion.CreateFromAxisAngle(new Vector3(n.Z / h, 0, -n.X / h), theta);
            fwd = Vector3.Transform(n, q);
            off = Vector3.Transform(off, q);
        }
        var eye = pivot + off;
        if (ground is not null)
        {
            float g = ground(eye.X, eye.Z);
            if (g + 0.5f > eye.Y - 0.1f) eye.Y = g + GroundClear;
        }
        return (eye, fwd);
    }

    /// <summary>The up vector of a target view: the world's (the look matrix has no roll, and the tilt is about a horizontal axis).</summary>
    public static Vector3 Up => Vector3.UnitY;

    /// <summary>
    /// F11: the local offset (0, 5 sin 20, -5 cos 20) pitched down by 20 degrees, concatenated with the round's
    /// own rotation, so the camera rolls and turns with the round.
    /// </summary>
    public static (Vector3 Eye, Vector3 Forward, Vector3 Up) Projectile(Vector3 pos, Rows rot, float boom = 1f)
    {
        float s = MathF.Sin(ProjectilePitch), c = MathF.Cos(ProjectilePitch);
        var eye = pos + rot.ToWorld(new Vector3(0, ProjectileBack * boom * s, -ProjectileBack * boom * c));
        var r = Rows.Concat(Rows.AboutX(ProjectilePitch), rot);
        return (eye, r.Forward, r.Up);
    }

    /// <summary>Whether a round of this kind can be followed: the kinds the original follows (rockets, mortars and missiles).</summary>
    public static bool Followable(uint kind) => kind is 2 or 3 or 4 or 5 or 7 or 8 or 0x14 or 0x15 or 0x16;
}
