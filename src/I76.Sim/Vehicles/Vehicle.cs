using System.Numerics;
using I76.Sim.Math;

namespace I76.Sim.Vehicles;

/// <summary>Per-wheel data the step reads (wheel object's block).</summary>
public sealed class Wheel
{
    /// <summary>Grip coefficient.</summary>
    public float Grip;
    /// <summary>Rolling resistance.</summary>
    public float Rolling;
    /// <summary>The wheel's position in the body frame; x and z drive the force model.</summary>
    public Vector3 Offset;
    /// <summary>Steering angle about the body's up axis: −π/4·steer on the fronts.</summary>
    public float SteerAngle;
    /// <summary>A blown tyre.</summary>
    public bool Blown;
    /// <summary>Visual spin.</summary>
    public float Spin;

    // The suspension, wheel block offsets.
    /// <summary>Travel, clamped to [TravelMin, TravelMax].</summary>
    public float Travel;
    /// <summary>Rest height, the WLOC slot's y (also the tyre's radius for contact).</summary>
    public float Rest;
    /// <summary>Ground height under the wheel.</summary>
    public float Ground;
    /// <summary>Last target height and its rate.</summary>
    public float PrevTarget, PrevRate;
    /// <summary>±0.25·rest.</summary>
    public float TravelMax, TravelMin;
    /// <summary>At the lower limit (or locked).</summary>
    public bool AtLimit;
    /// <summary>A skid mark is being laid (the original sets it on a mark's first substep, clears it when the mark ends).</summary>
    public bool Marking;
    /// <summary>Two-pole IIR coefficients and state, doubles.</summary>
    public double A1 = -1.2789602, A2 = 0.49, B0 = 0.2110398, Y1, Y2;

    /// <summary>The wheel's offset with the current suspension travel, body frame.</summary>
    public Vector3 Mount => new(Offset.X, Rest + Travel, Offset.Z);

    public void InitSuspension()
    {
        Rest = Offset.Y;
        TravelMax = 0.25f * Rest;
        TravelMin = -0.25f * Rest;
        double r = 0.7, w = 2 * System.Math.PI / 15.0;
        A1 = -2 * r * System.Math.Cos(w);
        A2 = r * r;
        B0 = 1 + A1 + A2;
    }
}

/// <summary>
/// The constants a vehicle's step reads, as the vehicle block holds them.
/// </summary>
public sealed class VehicleParams
{
    public float Mass = 1000;
    public float DriveScale = 1;
    public float SteerGain = 0.05f;
    public float PivotForward;
    public float Drag = 0.005f;
    public float EngineScale = 1;
    public float YawRateLimit;
    public float RideOffset;
    /// <summary>The chassis grip scales the load-based cornering limit.</summary>
    public float ChassisGrip = 1;
    /// <summary>The brake strength.</summary>
    public float BrakeStrength = 1;
    public float Wheelbase = 2.74f;
    public float Track = 1.37f;

    /// <summary>
    /// The pivot (x, z) per contact mask (bit 8 FL, 4 FR, 2 RL, 1 RR
    /// in the air), from track T and wheelbase L. Unlisted masks are (0, 0).
    /// </summary>
    public (float X, float Z) Pivot(int mask) => mask switch
    {
        3 => (0, Wheelbase / 2), 5 => (-Track / 2, 0), 7 => (-Track / 2, Wheelbase / 2),
        10 => (Track / 2, 0), 11 => (Track / 2, Wheelbase / 2), 12 => (0, -Wheelbase / 2),
        13 => (-Track / 2, -Wheelbase / 2), 14 => (Track / 2, -Wheelbase / 2), _ => (0, 0),
    };
}

/// <summary>
/// The engine block and its update: a
/// four-speed automatic with shift lines in throttle against km/h, rpm from
/// road speed through the gear ratio, and force on an inverted parabola
/// peaking at 3500 rpm.
/// </summary>
public sealed class Engine
{
    /// <summary>Reverse, (unused), 2nd..4th; first gear holds 1050 rpm.</summary>
    public static readonly float[] Ratios = [3.0f, 0f, 1.67f, 0.96f, 0.67f];

    public int Gear = 1;
    public float Curve = 300000f * 8.1632656e-8f;
    /// <summary>Current force (the original returns it).</summary>
    public float Force;
    public float PeakForce = 300000f;
    public float Damage = 1f;
    public float Rpm = 1050f;
    public float Speedo;

    /// <summary>
    /// The RPM and speedometer smoothing. The
    /// smoothing runs on the real frame delta, not the
    /// substep: a frame-rate coupling of the original's.
    /// </summary>
    public void Update(float throttle, float speed, VehicleFlags flags, bool reverse, int driveState, float frameDelta)
    {
        float k = frameDelta + frameDelta;
        float wheelRpm = MathF.Abs(speed) * 0.59738356f * 60f;
        int g = reverse ? 0 : (Gear == 0 ? (Gear = 1) : Gear);
        if (driveState == 2 || driveState == 0) { g = 1; Gear = 1; }
        float rpm, speedo = MathF.Abs(speed);
        bool lerp = true;
        if ((flags & VehicleFlags.EngineOn) == 0)
        {
            rpm = k < 0 || k > 0.95f ? 0 : (MathF.Abs(-Rpm) >= 0.005f ? -Rpm * k + Rpm : 0);
        }
        else if (((uint)flags & 6) == 0 && driveState != 2 && driveState != 0)
        {
            int next = 0;
            if (!reverse)
            {
                float kmh = MathF.Abs(speed * 3.6f);
                next = g;
                if (g == 1) { if (throttle > 0.002f) next = 2; }
                else if (g == 2)
                {
                    if (driveState != 5 && ((throttle < 0.0625f && kmh > 25) ||
                        (throttle >= 0.0625f && throttle <= 0.875f && kmh >= 25 && kmh <= 62 && throttle <= (kmh - 62) * 0.8125f * 0.027027028f * 0.875f) ||
                        kmh > 62)) next = 3;
                }
                else if (g == 3 && driveState != 5 && driveState != 4)
                {
                    next = 4;
                    if (throttle >= 0.0625f || kmh <= 40)
                    {
                        int line = ShiftLine(throttle, kmh, 0.0625f, 40, 0.875f, 106);
                        if (line != 0 && kmh <= 106) next = 3;
                    }
                }
                next = Downshift(throttle, speed, next, driveState);
            }
            Gear = next;
            rpm = next == 1 ? 1050f : Ratios[next] * wheelRpm * 3.538f + 850f;
            speedo = wheelRpm * 1.6739663f * 0.016666668f * 2.2369363f;
            lerp = false;
        }
        else
        {
            if (throttle >= 0.002f)
            {
                rpm = throttle * 4950f + 1050f;
                if (k >= 0 && k <= 0.95f && MathF.Abs(rpm - Rpm) >= 0.005f) rpm = k * (rpm - Rpm) + Rpm;
                if (rpm > 6000f) rpm = 6000f;
            }
            else if (k < 0 || k > 0.95f) rpm = 1050f;
            else rpm = MathF.Abs(1050f - Rpm) >= 0.005f ? (1050f - Rpm) * k + Rpm : 1050f;
        }
        if (lerp && k >= 0 && k <= 0.95f && MathF.Abs(speedo - Speedo) >= 0.005f) speedo = k * (speedo - Speedo) + Speedo;
        Speedo = speedo;
        Rpm = rpm;
        Force = (PeakForce - Curve * (rpm - 3500f) * (rpm - 3500f)) * Damage;
    }

    /// <summary>1 above the line through the box's corners, 0 below, −1 outside the box.</summary>
    private static int ShiftLine(float t, float kmh, float t0, float k0, float t1, float k1)
    {
        if (t0 <= t && t <= t1 && k0 <= kmh && kmh <= k1)
            return (t1 - t0) * (kmh - k1) / (k1 - k0) * t1 < t ? 1 : 0;
        return -1;
    }

    private static int Downshift(float t, float speed, int g, int driveState)
    {
        float kmh = MathF.Abs(speed * 3.6f);
        if (driveState == 5 && g > 2) return 2;
        if (driveState == 4 && g > 3) return 3;
        if (g == 2) { if (t < 0.002f && kmh < 0.5f) return 1; }
        else if (g == 3)
        {
            if ((t > 0.875f && kmh < 60) ||
                (t >= 0.3125f && t <= 0.875f && kmh >= 12 && kmh <= 60 && (kmh - 60) * 0.5625f * 0.020833334f * 0.875f < t) ||
                kmh < 12) return 2;
        }
        else if (g == 4)
        {
            if ((t > 0.87f && kmh < 98) ||
                (t >= 0.25f && t <= 0.875f && kmh >= 31 && kmh <= 98 && (kmh - 98) * 0.625f * 0.014925373f * 0.875f < t) ||
                kmh < 31) return 3;
        }
        return g;
    }
}

[Flags]
public enum VehicleFlags : uint
{
    /// <summary>Bit 0: required for drive. Read as engine running.</summary>
    EngineOn = 1,
    Sliding = 2,
    Airborne = 4,
    /// <summary>Bit 6: the lights are on (initially from the time-of-day preset, setLights and H toggle it).</summary>
    Lights = 0x40,
    /// <summary>Bit 3: set with 0x80000000 by startCar (the engine-start sound plays).</summary>
    Started = 8,
    Dead = 0x20,
    /// <summary>Set by a landing, cleared once the car has settled; while set the airborne test is skipped.</summary>
    JustLanded = 0x200,
    NoGripOnLowSurface = 0x400,
    TurboDrive = 0x800,
    /// <summary>The radar jammer is running.</summary>
    Jamming = 0x40000,
    CommandedYaw = 0x1000,
    Blending = 0x2000,
    LoadBasedCornering = 0x10000,
    Disabled = 0x8000,
    /// <summary>The horn key is held (set by honk_horn in a network game only, the caller); remote clients play the horn from it.</summary>
    Honking = 0x200000,
}

/// <summary>Where the vehicle step hands damage to the damage model.</summary>
public interface IStepDamage
{
    /// <summary>The death / engine-start timer (per substep near, per frame far).</summary>
    void Timer(Vehicle v, float dt);
    /// <summary>From the static branch: a terrain contact that bounced harder than 4 u/s.</summary>
    void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal);
    /// <summary>On landing, impact −vel against the ground normal.</summary>
    void Landing(Vehicle v, Vector3 impactVel, Vector3 n);
    /// <summary>When the car lands more than 35 below where it was last grounded.</summary>
    void FallKill(Vehicle v);
    /// <summary>Rough terrain on a fast grounded substep.</summary>
    void Rough(Vehicle v, float dt);
    /// <summary>A car met a scenery collider this frame.</summary>
    void StaticContact(Vehicle v, Collision.StaticObject o, Contact rec);
}

/// <summary>A vehicle's mutable state, named by its offsets in the original.</summary>
public sealed class Vehicle
{
    public VehicleParams P = new();
    public Engine Engine = new();
    /// <summary>The .vcf it was built from (VehicleFactory), for a rebuild; null for a car made in code.</summary>
    public string? Definition;
    /// <summary>The car's respray (I26's custom paint; null = the factory paint). Cosmetic: the renderer reads it, the sim never does. Saved, and sent to network clients with the car.</summary>
    public I76.Formats.Imaging.CarPaint? Paint;
    /// <summary>The engsnd.dat ENG NUM (engine sound row).</summary>
    public int EngineNumber;
    /// <summary>The engsnd.dat ignition row of <see cref="EngineNumber"/> (static: not hashed).</summary>
    public IgnitionRow? Ignition;
    /// <summary>
    /// The horn file of <see cref="EngineNumber"/> loops by its own header: the police car's siren. Started outside the horn key
    /// (an AI's honk, setSiren) it sounds until it is stopped, so the AI's honk turns the car's siren on.
    /// </summary>
    public bool HornLoops;
    public Wheel FrontRight = new(), FrontLeft = new(), RearRight = new(), RearLeft = new();
    public IEnumerable<Wheel> Wheels => [FrontRight, FrontLeft, RearRight, RearLeft];
    /// <summary>
    /// The optional middle pair (a 6x4's second axle; null on a car, the .vcf's middle .wdf is "null").
    /// The original gives them hit points, mass, a blow-out and a mesh, but its force and suspension code reads only
    /// the four others: "middle wheels: 0"), so they stay out of <see cref="Wheels"/>.
    /// </summary>
    public Wheel? MiddleRight, MiddleLeft;
    public IEnumerable<Wheel> MiddleWheels => MiddleRight is null || MiddleLeft is null ? [] : [MiddleRight, MiddleLeft];
    /// <summary>The wheel at a WLOC slot (0 FR, 1 FL, 2 MR, 3 ML, 4 RR, 5 RL), or null.</summary>
    public Wheel? WheelAt(int slot) => slot switch { 0 => FrontRight, 1 => FrontLeft, 2 => MiddleRight, 3 => MiddleLeft, 4 => RearRight, 5 => RearLeft, _ => null };

    /// <summary>Rows right, up, forward.</summary>
    public Rows Rot = Rows.Identity;
    /// <summary>The position, in doubles.</summary>
    public Double3 Pos;

    public Vector3 Vel;
    /// <summary>pitch, yaw, roll rates.</summary>
    public Vector3 AngVel;
    public float Speed;
    public Vector3 Accel;
    public float Steer;
    public float Throttle;
    /// <summary>+1 forward, −1 reverse: multiplies the drive force.</summary>
    public float Gear = 1;
    /// <summary>Handbrake / locked rear.</summary>
    public int Handbrake;
    public int Unk_0xf4;
    /// <summary>start_engine was pressed this tick (the original writes it every frame). Nothing reads it: its behaviour is not documented.</summary>
    public int StartEngineKey;
    /// <summary>Drive state: 0 forces full brake; 2 cuts drive.</summary>
    public int DriveState = 3;
    /// <summary>Post-load 0x1001 (VehicleFactory); 0x200 marks a landing until the car settles.</summary>
    public VehicleFlags Flags = VehicleFlags.EngineOn;
    public int Surface;
    public Vector3 GroundNormal = Vector3.UnitY;
    public float NormalLoad = 9.8f;
    public float HeightAboveGround;
    public float GroundHeight;

    public float InvSpeed => Speed <= 1e-4f ? 1e30f : 1f / Speed;

    /// <summary>The COLP box: ascending x0..x3, y0..y3, z0..z3.</summary>
    public float[] BoxX = [-0.9f, -0.5f, 0.5f, 0.9f], BoxY = [0f, 0.3f, 1.0f, 1.4f], BoxZ = [-2.1f, -1f, 0.5f, 2f];

    /// <summary>The four horizontal box corners at mid-height (y1+y2)/2, for the terrain sweep.</summary>
    public Vector3[] Corners => [new(BoxX[0], Ym, BoxZ[0]), new(BoxX[3], Ym, BoxZ[0]), new(BoxX[3], Ym, BoxZ[3]), new(BoxX[0], Ym, BoxZ[3])];
    private float Ym => (BoxY[1] + BoxY[2]) * 0.5f;

    /// <summary>The contact record.</summary>
    public Contact Rec;
    /// <summary>The mounted weapons (null for a vehicle without any).</summary>
    public Weapons.Armory? Armory;
    /// <summary>Hit points, armour and the damage state (null until a damage model takes the car on).</summary>
    public Damage.VehicleDamage? Health;
    /// <summary>What the files say about this car's damage (armour, component HP, specials).</summary>
    public Damage.VehicleBuild? Build;
    /// <summary>What the car was built from, for field salvage.</summary>
    public Loadout? Loadout;
    /// <summary>The death / engine-start timer.</summary>
    public float Timer;
    /// <summary>An AI car 10 s after its death: its collider is unlinked and it no longer moves.</summary>
    public bool Wreck;
    /// <summary>The driver's arms, which gate the weapons.</summary>
    public CrewArms Arms = new();
    /// <summary>The session's stable id for this car (0 until a session assigns one; never reused within it).</summary>
    public int NetId;
    /// <summary>Bumped each time the car is renewed in place (a regen pad, a respawn): its state jumps.</summary>
    public int NetGeneration;
    /// <summary>A helicopter's block (object type 9): it flies (HeliStep) instead of driving. Null for a car.</summary>
    public HeliState? Heli;
    public bool IsHeli => Heli is not null;
    /// <summary>The object class, from the .vdf, not from the mission placement: 1 car, 8 emplacement, 9 helicopter.</summary>
    public uint ObjectClass = 1;
    /// <summary>A gun emplacement's block (object type 8): it never moves. Null for anything else.</summary>
    public EmplacementState? Emplacement;
    public bool IsEmplacement => Emplacement is not null;
}

/// <summary>A contact waiting for the step's response.</summary>
public struct Contact
{
    public bool Hit;
    /// <summary>The other object, or null for terrain.</summary>
    public object? Other;
    public float OtherMass;
    public Vector3 OtherVel;
    public float T;
    /// <summary>The contact point in this body's frame.</summary>
    public Vector3 C;
    /// <summary>Other's velocity minus this body's point velocity, world.</summary>
    public Vector3 RelVel;
    /// <summary>World normal, from the other toward this body.</summary>
    public Vector3 N;
    /// <summary>The structure node touched (<see cref="Collision.StaticObject.Nodes"/>), null when not known.</summary>
    public int? Node;

    /// <summary>Clears hit, other, n and the state; t, c and relvel survive.</summary>
    public void Clear() { Hit = false; Other = null; N = default; OtherMass = 0; OtherVel = default; }
}

/// <summary>
/// A car's parts as its .vcf names them: the front, middle and rear .wdf,
/// the engine, suspension and brakes as compnent.cdf ordinals, and the
/// special types. Salvage names the parts eng%02d, sus%02d, bra%02d, spc%02d
/// and the wheel stems from these.
/// </summary>
public sealed record Loadout(string[] Wdf, int Engine, int Suspension, int Brakes, int[] Specials);

/// <summary>
/// An engsnd.dat row's three ignition sounds and their lengths (IGN NORM, DMG1, DMG2), picked by the engine's health
/// ratio: above 0.66, above 0.33, otherwise. A NONE sound is a null name.
/// </summary>
public sealed record IgnitionRow(string?[] Sounds, float[] Times)
{
    public int Pick(float ratio) => ratio > 0.66f ? 0 : ratio > 0.33f ? 1 : 2;
}
