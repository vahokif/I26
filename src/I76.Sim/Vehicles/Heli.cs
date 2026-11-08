using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Terrain;

namespace I76.Sim.Vehicles;

/// <summary>
/// A helicopter's own block (object type 9):
/// one hit-point pool, the eased controls and the height it holds.
/// </summary>
public sealed class HeliState
{
    public int Hp = 600;
    public int MaxHp = 600;
    public float InvMax = 1f / 600f;
    /// <summary>The throttle eased towards the throttle command.</summary>
    public float Thrust;
    /// <summary>The side input eased towards the steering command.</summary>
    public float Side;
    /// <summary>The height held above the ground (setHeliHeight; 20 on take-off).</summary>
    public float Height;
    /// <summary>Not the original's: a contact record was held this frame (any collision brings it down).</summary>
    public bool Touched;

    /// <summary>The helicopter's post-load: the pool from the front armour.</summary>
    public void SetPool(int hp)
    {
        Hp = MaxHp = hp;
        InvMax = 1f / hp;
    }

    /// <summary>The health percentage, type 9.</summary>
    public float HealthPercent => MaxHp >= 1 ? Hp * InvMax * 100f : 100f;
}

/// <summary>What the helicopter's update hands to the damage model.</summary>
public interface IHeliDamage
{
    /// <summary>The kill (HP gone, a contact, the FSM's destroy): falling from here on, an explosion.</summary>
    void ShootDown(Vehicle v);
    /// <summary>The helicopter's crash: it met the ground.</summary>
    void Crash(Vehicle v);
}

/// <summary>
/// The helicopter's update and flight model (the helicopter's update,
/// The helicopter's flight model; the Nitro Pack's is the same), and the AI's
/// helicopter controls.
/// </summary>
public static class HeliStep
{
    /// <summary>For a helicopter: on the ground, not flying.</summary>
    public const VehicleFlags Landed = (VehicleFlags)0x80;
    /// <summary>Shot down, falling.</summary>
    public const VehicleFlags Falling = VehicleFlags.Disabled;

    /// <summary>Metres above the ground a melee sets a helicopter down at its spawn point (not exactly on it: see MeleeGame.Place).</summary>
    public const float SpawnClearance = 0.5f;

    public const float Thrust = 20.2f, Drag = 0.00155f, Gravity = -9.8f, TakeOffHeight = 20f;

    /// <summary>The helicopter's update: a contact brings it down; throttle takes it off; the flight model; the crash.</summary>
    public static void Frame(Vehicle v, IGround ground, float dt, bool isPlayer, IHeliDamage? damage)
    {
        var h = v.Heli!;
        if (h.Touched && ((uint)v.Flags & 0x8020) == 0) { h.Hp = 0; damage?.ShootDown(v); }
        h.Touched = false;
        if ((v.Flags & VehicleFlags.Dead) != 0) return;
        if ((v.Flags & Landed) != 0 && v.Throttle > 0.0001f) TakeOff(v);
        Fly(v, ground, dt, isPlayer);
        if ((v.Flags & VehicleFlags.Dead) != 0) damage?.Crash(v);
    }

    /// <summary>
    /// The helicopter's creation / the helicopter's post-load for a class-9 vehicle: the block, the pool from the front
    /// armour, flags 0x81 (engine on, landed). A player's helicopter keeps its specials and cockpit panels, with none of
    /// The car's post-load's rand() draws. No car damage state. In a network game (<paramref name="network"/>) the
    /// post-load lifts it at once: not landed, hover height 20 m.
    /// </summary>
    public static void Setup(Vehicle v, bool player, bool network = false)
    {
        v.Heli = new HeliState();
        if (v.Build is { } b)
        {
            v.Heli.SetPool(b.Armour[0]);
            if (player) v.Health = Damage.VehicleDamage.CreateSpecialsOnly(v, b);
        }
        v.Flags = VehicleFlags.EngineOn | Landed;
        if (network) TakeOff(v);
    }

    /// <summary>SetHeliHeight.</summary>
    public static void SetHeight(Vehicle v, float height)
    {
        v.Heli!.Height = height;
        if (height > 0) v.Flags &= ~Landed;
    }

    public static void TakeOff(Vehicle v) => SetHeight(v, TakeOffHeight);

    /// <summary>The engine's easing: the target outright when k is out of [0, 0.95] or within 0.005.</summary>
    private static float Ease(float cur, float target, float k)
    {
        if (k < 0 || k > 0.95f) return target;
        float d = target - cur;
        return MathF.Abs(d) < 0.005f ? target : d * k + cur;
    }

    private static Vector3 Flat(Vector3 a)
    {
        var f = new Vector3(a.X, 0, a.Z);
        float l = f.Length();
        return l > 0 ? f / l : f;
    }

    /// <summary>The helicopter's flight model.</summary>
    public static void Fly(Vehicle v, IGround ground, float dt, bool isPlayer)
    {
        var h = v.Heli!;
        var flags = v.Flags;
        var p = v.Pos.ToVector3();
        var vel = v.Vel;
        var vel0 = vel;
        float speed = v.Speed;
        var fwdH = Flat(v.Rot.Forward);
        var rightH = Flat(v.Rot.Right);
        var a = Vector3.Zero;
        var rot = v.Rot;
        if ((flags & Falling) == 0)
        {
            if ((flags & Landed) == 0)
            {
                // the controls ease in, but only above world y 10 (it climbs straight up off a low pad first)
                if (p.Y >= 10f)
                {
                    h.Thrust = Ease(h.Thrust, v.Throttle, dt);
                    h.Side = Ease(h.Side, v.Steer, dt);
                }
                else h.Thrust = h.Side = 0;
                float fa = h.Thrust * Thrust, sa = h.Side * -Thrust;
                if (fa > 0 || isPlayer) a = fa * fwdH + sa * rightH;
                else if (speed > -fa) a = sa * rightH + fa / speed * vel;
                else if (speed > 1) a = sa * rightH - vel;
                else if (speed > 0) a = -vel;
                else { vel = Vector3.Zero; vel0 = Vector3.Zero; speed = 0; }
                if (speed > 1) a += -Drag * speed * vel;
                p += (vel + 0.5f * a * dt) * dt;
                vel += a * dt;
                if (!isPlayer && h.Thrust < 0 && Vector3.Dot(vel, vel0) < 0) { vel = Vector3.Zero; a = Vector3.Zero; }
                float g = ground.Query(p.X, p.Y, p.Z).Height;
                if (p.Y < g) { vel = Vector3.Zero; a = Vector3.Zero; flags |= VehicleFlags.Dead; }
                var ahead = p + 2f * vel;
                float g2 = ground.Query(ahead.X, ahead.Y, ahead.Z).Height;
                p.Y = Ease(p.Y, h.Height + MathF.Max(g, g2), dt * 0.5f);
            }
            // the attitude: up along the acceleration plus 2g, the nose along the velocity
            var up = Vector3.Normalize(a + new Vector3(0, 19.6f, 0));
            v.AngVel = Vector3.Zero;
            var fwd = speed >= 1f && Vector3.Dot(vel, fwdH) > 0 ? vel : fwdH;
            rot = Basis(up, fwd);
        }
        else
        {
            // shot down: a ballistic fall, turning at the kill's rate, until it meets the ground
            p.X += vel.X * dt;
            p.Z += vel.Z * dt;
            p.Y += (vel.Y + 0.5f * Gravity * dt) * dt;
            vel.Y += Gravity * dt;
            float g = ground.Query(p.X, p.Y, p.Z).Height;
            if (p.Y <= g + 0.5f) { vel.Y = 0; p.Y = g; }
            rot = Rows.Concat(Rows.Euler(-(dt * v.AngVel.X), -(dt * v.AngVel.Y), -(dt * v.AngVel.Z)), rot);
            if (p.Y == ground.Query(p.X, p.Y, p.Z).Height) flags |= VehicleFlags.Dead;
        }
        v.Accel = a;
        v.Vel = vel;
        v.Speed = vel.Length();
        v.Flags = flags;
        v.Rot = rot;
        v.Pos = Double3.From(p);
    }

    /// <summary>The original's up/forward basis: right = norm(up × fwd), forward = right × up.</summary>
    public static Rows Basis(Vector3 up, Vector3 fwd)
    {
        var r = Vector3.Normalize(Vector3.Cross(up, fwd));
        return new Rows(r, up, Vector3.Cross(r, up));
    }

    /// <summary>
    /// The frame time the AI's helicopter controls are worked out for at the most: 28 fps, the rate the game ran at.
    /// Both close a fixed share of their error each frame (∝ 1/dt), against controls that ease in over about a second, so
    /// in the original the gain grows with the frame rate: at 40 fps and more a helicopter weaves left and right on a
    /// straight path, and at 60 fps one of P18's Hueys flies into the ridge in the opening shot (measured in the original game).
    /// Faster ticks keep 28 fps' gain (docs/deviations.md); 20–28 Hz are the original's.
    /// </summary>
    public const float ControlFrame = 0.036f;

    /// <summary>The original's throttle for its drivers (type-9 branch): writes the throttle command.</summary>
    public static void Throttle(Vehicle v, float targetSpeed, float frameDelta)
    {
        frameDelta = MathF.Max(frameDelta, ControlFrame);
        float speed = v.Speed;
        float t = ((targetSpeed - speed) * (1f / frameDelta) * 0.15f + speed * speed * Drag) * 0.049504947f;
        if (t < -1f) t = -0.999f;
        else if (t > 1f) t = 0.999f;
        v.Throttle = t;
    }

    /// <summary>The original's helicopter steering: writes the steering command.</summary>
    public static void Steer(Vehicle v, float targetHeading, float frameDelta, float heading)
    {
        const float Pi = 3.1415927f, TwoPi = 6.2831855f;
        float t = targetHeading;
        if (t > Pi) t -= TwoPi;
        else if (t < -Pi) t += TwoPi;
        float e1, e2;
        if (t <= heading) { e2 = t - heading; e1 = e2 + TwoPi; }
        else { e1 = t - heading; e2 = e1 - TwoPi; }
        float e = MathF.Abs(e1) <= MathF.Abs(e2) ? e1 : e2;
        float dt = MathF.Max(frameDelta, ControlFrame);
        float s = (v.Speed + v.Throttle * dt * Thrust) * dt * MathF.Sin(-e * 0.15f) / (dt * dt * Thrust);
        if (s < -1f) s = -0.999f;
        else if (s > 1f) s = 0.999f;
        v.Steer = s;
    }
}
