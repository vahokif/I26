using System.Numerics;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;

namespace I76.Sim.Ai;

/// <summary>The AI block fields the controllers read.</summary>
public sealed class AiBlock
{
    /// <summary>Skill / aggression, 0..1 (setSkill / setAgg). Scales the steering demand.</summary>
    public float Skill = 0.4f;
    /// <summary>Acceleration factor; scales a positive speed error and the throttle cap 0.75 + 0.25·this.</summary>
    public float AccelFactor = 0.4f;
    /// <summary>The commanded speed and target (goto/follow/evade).</summary>
    public float CommandSpeed = 30f;
    /// <summary>The fire decision's accuracy (setSkill's second index).</summary>
    public float FireAccuracy = 0.2f;
    /// <summary>The fire decision's skill (setSkill's second index).</summary>
    public float FireSkill = 0.6f;

    /// <summary>The setSkill tables the original writes (index 1 best … 5 worst; index 0 stays 0).</summary>
    public static readonly float[] DriveSkill = [0, 1.0f, 0.8f, 0.6f, 0.5f, 0.4f];
    public static readonly float[] Accuracy = [0, 1.0f, 0.8f, 0.6f, 0.4f, 0.2f];
    public static readonly float[] Shooting = [0, 1.0f, 0.9f, 0.8f, 0.7f, 0.6f];

    /// <summary>setSkill a, b: a picks the driving pair, b the shooting pair.</summary>
    public void SetSkill(int a, int b)
    {
        static float At(float[] t, int i) => i >= 0 && i < t.Length ? t[i] : 0;
        Skill = At(DriveSkill, a); AccelFactor = At(DriveSkill, a);
        FireAccuracy = At(Accuracy, b); FireSkill = At(Shooting, b);
    }

    /// <summary>The block's defaults: the worst tier.</summary>
    public AiBlock() => SetSkill(5, 5);
}

/// <summary>
/// The AI's two driving actuators, ported: the original's steering for its drivers
/// and the original's throttle for its drivers. Both read the
/// frame delta (the frame delta / the step rate): commands scale as 1/dt,
/// which is why they want the fixed step.
/// </summary>
public static class AiDriver
{
    /// <summary>The engine's heading (the object's cached geometry): atan2 of forward x over z, with forward × gear.</summary>
    public static float Heading(Vehicle v)
    {
        var f = v.Rot.Forward * v.Gear;
        return f.X == 0 && f.Z == 0 ? 0 : MathF.Atan2(f.X, f.Z);
    }

    /// <summary>The original's angle difference: b − a wrapped to ±π.</summary>
    public static float AngleDelta(float a, float b)
    {
        float d = b - a;
        if (d > MathF.PI) return d - 2 * MathF.PI;
        if (d < -MathF.PI) d += 2 * MathF.PI;
        return d;
    }

    public static float HeadingTo(Vehicle v, Vector3 target)
    {
        var p = v.Pos.ToVector3();
        return MathF.Atan2(target.X - p.X, target.Z - p.Z);
    }

    private static float MaxLat(Vehicle v, IGround g, out float sumGrip)
    {
        sumGrip = v.FrontLeft.Grip + v.FrontRight.Grip + v.RearLeft.Grip + v.RearRight.Grip;
        float mu = g.Surface(v.Surface).Grip;
        if (mu > 0.2f && (v.Flags & VehicleFlags.NoGripOnLowSurface) != 0) mu = 0;
        float lat = mu * v.NormalLoad * v.P.ChassisGrip * sumGrip * 0.25f;
        if ((v.Flags & VehicleFlags.LoadBasedCornering) == 0) lat = sumGrip * 7.84f;
        return lat;
    }

    /// <summary>The steering limit grip allows at this speed (≤ 1); 1 for a far car or a helicopter.</summary>
    public static float SteerLimit(Vehicle v, IGround g, bool far = false)
    {
        if (far || v.IsHeli) return 1f;
        float sum = v.FrontLeft.Grip + v.FrontRight.Grip + v.RearLeft.Grip + v.RearRight.Grip;
        float flat = sum * 7.84f;
        float mu = g.Surface(v.Surface).Grip;
        if (mu > 0.2f && (v.Flags & VehicleFlags.NoGripOnLowSurface) != 0) mu = 0;
        float load = v.NormalLoad * v.P.ChassisGrip * mu * sum * 0.25f;
        if (v.Speed < 1e-4f) return 1f;
        float need = v.P.SteerGain * v.Speed * v.Speed;
        float r = need <= flat ? load / need : load / flat;
        return MathF.Min(r, 1f);
    }

    /// <summary>The original's steering for its drivers, given the target heading: writes the steering command.</summary>
    public static void Steer(Vehicle v, AiBlock ai, IGround g, float targetHeading, float frameDelta, bool far = false)
    {
        if (v.IsHeli) { HeliStep.Steer(v, targetHeading, frameDelta, Heading(v)); return; }   // the original's helicopter steering
        if ((v.Flags & VehicleFlags.Airborne) != 0) { v.Steer = 0; return; }
        float speed = v.Speed;
        if (speed > -1e-4f && speed < 1e-4f) { v.Steer = 0; return; }
        float err = AngleDelta(Heading(v), targetHeading);
        float demand = ai.Skill * -err;
        float gain = v.P.SteerGain;
        float lat = MaxLat(v, g, out _);
        if (lat < 0.001f) { v.Steer = 0; return; }
        float s = gain * speed * speed <= lat ? demand / (frameDelta * gain * speed) : demand * speed / (lat * frameDelta);
        float lim = SteerLimit(v, g, far);
        if (s >= 0) { s = MathF.Min(s, lim); s = MathF.Min(s, 0.9999f); }
        else { s = MathF.Max(s, -lim); s = MathF.Max(s, -0.9999f); }
        v.Steer = v.Gear * s;
    }

    /// <summary>
    /// The original's throttle for its drivers, given the target speed: writes the throttle command, capped by
    /// the traction limit (the forward-clearance measure, see VehicleStep.TractionLimit).
    /// </summary>
    public static void Throttle(Vehicle v, AiBlock ai, IGround g, float targetSpeed, float frameDelta, Func<float, float>? traction = null)
    {
        if (v.IsHeli) { HeliStep.Throttle(v, targetSpeed, frameDelta); return; }   // the type-9 branch
        float hz = 1f / frameDelta;
        float speed = v.Speed;
        float e = (targetSpeed - speed) * hz;
        if (e >= 0) e *= ai.AccelFactor;
        float resist = 0;
        if (speed >= 1f)
        {
            float roll = v.RearRight.Rolling + v.RearLeft.Rolling + v.FrontRight.Rolling + v.FrontLeft.Rolling;
            resist = v.P.Drag * speed * speed * -0.1f - g.Surface(v.Surface).Rolling * roll * (v.NormalLoad * 0.25f);
        }
        e -= resist;
        if (e > 0)
        {
            float f = v.P.EngineScale * (v.Engine.Force * v.P.DriveScale);
            if ((v.Flags & VehicleFlags.TurboDrive) != 0) f *= 5;
            if (speed > 1f) f *= v.InvSpeed;
            if (f < 1e-4f) f = 1e-4f;
            e /= f;
        }
        else
        {
            float b = v.P.BrakeStrength;
            e = b <= -1e-4f || b >= 1e-4f ? e / (b * 8f) : 1f;
        }
        if (e >= 0)
        {
            e = MathF.Min(e, 0.75f + 0.25f * ai.AccelFactor);
            float lim = (traction?.Invoke(1f) ?? 1f) * 0.9999f;
            if (lim < e) { v.Throttle = lim; return; }
        }
        else
        {
            float lim = (traction?.Invoke(-1f) ?? -1f) * 0.9999f;
            if (e < lim) { v.Throttle = lim; return; }
        }
        v.Throttle = e;
    }

    /// <summary>
    /// A placeholder behaviour until the FSM and the Ptable behaviours are
    /// ported: drive toward a point at a speed, slowing for hard steering the
    /// way the road-war behaviour's arms do (|steer| &gt; 0.75 → cruise, &gt; 0.5 → 30).
    /// </summary>
    public static void DriveTo(Vehicle v, AiBlock ai, IGround g, Vector3 target, float speed, float frameDelta, float arriveRadius = 6f, Func<float, float>? traction = null)
    {
        var p = v.Pos.ToVector3();
        float dist = new Vector2(target.X - p.X, target.Z - p.Z).Length();
        if (dist < arriveRadius) { Throttle(v, ai, g, 0, frameDelta, traction); v.Steer = 0; return; }
        Steer(v, ai, g, HeadingTo(v, target), frameDelta);
        float want = MathF.Abs(v.Steer) > 0.75f ? MathF.Min(speed, 15f) : MathF.Abs(v.Steer) > 0.5f ? MathF.Min(speed, 30f) : speed;
        // no faster than turns the target in: the radius it needs, d / (2 sin e), at half the yaw-rate limit
        float err = MathF.Abs(AngleDelta(Heading(v), HeadingTo(v, target)));
        if (err > 0.05f && v.P.YawRateLimit > 0)
        {
            float need = dist / (2f * MathF.Sin(MathF.Min(err, MathF.PI / 2)));
            want = MathF.Min(want, MathF.Max(5f, 0.5f * v.P.YawRateLimit * need));
        }
        Throttle(v, ai, g, want, frameDelta, traction);
    }
}
