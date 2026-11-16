namespace I76.Sim.Session;

/// <summary>
/// Play Options' Keyboard Steering Assist (the port's; on unless the player turns it off; docs/deviations.md). The original steers
/// a held key by its hold time, √(t/3) of full lock, so full lock takes 3 s and the first second averages under 40% of it: in a
/// sharp turn a car on the keys runs wide where a stick turns at full lock at once. Assisted, the key's steer eases toward the
/// key (1 − e^(−t/τ), <see cref="Assist.SteerTau"/> in, <see cref="Assist.CentreTau"/> back to the centre); for the first
/// <see cref="Assist.LimitHold"/> s of a press the key's lock is held just short of where the 1997 grip model starts to give way
/// (<see cref="BlendLock"/>, the turn squeal's onset) and to <see cref="Assist.LatCap"/> of cornering, then let go over <see cref="Assist.LimitLift"/> s, so a correction never
/// squeals and a held turn has the whole lock; while the car slides a countersteer of <see cref="Assist.CsGain"/> lock a radian
/// of slip turns its heading onto its velocity. It reads the own car as this machine has it (the sim's, or a network game's
/// predicted one). All of it is by time, the same at every Sim Rate and at Modern Physics' own steps. The throttle keys keep the
/// original's ramp (<see cref="KeyRamp"/>, run here with the caller's step) and both go out as the pad's Analog input, so the
/// simulation, the wire, replays and saves see a stick. A held driving key makes the pad's axes count for nothing, as in the
/// original; with no key held a stick's input is left alone. Off, the keys go to the sim as they are, the original's.
/// Measured in tests/I76.Sim.Tests/KeyboardSteeringBench.cs and KeyboardSteeringTerrainBench.cs.
/// </summary>
public sealed class KeyStick(KeyStick.Assist? tuning = null)
{
    /// <summary>
    /// The constants: the ease toward a key and back (s), the countersteer (lock a radian of slip, at most that lock; only while
    /// the car slides unless <paramref name="CsSlidingOnly"/> is off), the hold (a share of the grip's lock, for so long, let go over so long).
    /// </summary>
    public sealed record Assist(float SteerTau, float CentreTau, float CsGain, float CsLimit, float GripLimit, float LimitHold, float LimitLift, bool CsSlidingOnly = true, float LatCap = 0);

    /// <summary>
    /// Assisted's constants, tuned on the bench and on players' recorded drives: the key's steer eases in with a time constant
    /// of 0.2 s and back to the centre with 0.05 s (0.1 s kept a released car turning 4–9° on at speed); the countersteer is 2
    /// lock a radian of slip, up to full lock, while the car slides (gripping, the 1997 model's slip is a turn's own degree or two
    /// toward the inside, and steering on it added lock to the turn). For the first 0.6 s of a press the key's lock is held at 98%
    /// of where the grip gives way (0.9 of lock for the Piranha on dirt from about 30 mph, 0.6 for A01's car, 0.58 to above full
    /// lock over the stock cars) and to no more than 25 m/s² of cornering (a grippy car, the F9 list's OP Piranha, asked 4–5 g
    /// of a short press at 110 mph), then let go over 0.3 s. No cut of the lock with the speed as such: the 1997 model already
    /// holds full lock to the grip at speed.
    /// </summary>
    public static readonly Assist Default = new(0.2f, 0.05f, 2f, 1f, 0.98f, 0.6f, 0.3f, LatCap: 25);

    /// <summary>The least lock the hold leaves the keys (a steep slope's light load would leave them nearly nothing).</summary>
    public const float MinHold = 0.25f;

    /// <summary>The setting as shell.cfg's older key held it: "original" was off; any other word, or none, is on.</summary>
    public static bool AssistedFromKeyboardSteering(string? word) => !string.Equals(word?.Trim(), "original", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What Assisted reads of the car: its speed along the heading (m/s, − backing), the slip, the angle from the heading to the
    /// velocity (+ to the left, radians), the lock where its grip gives way (<see cref="BlendLock"/>; 0 unknown), a blown front
    /// tyre's pull on the steer, and whether it slides.
    /// </summary>
    public readonly record struct Motion(float ForwardSpeed, float Slip, float BlendLock = 0, float Pull = 0, bool Sliding = true, float LatPerLock = 0);

    /// <summary>The car's motion as Assisted reads it on this ground; the slip is folded to ±90° (a car rolling backward measures it from its tail, the other way round).</summary>
    public static Motion MotionOf(I76.Sim.Vehicles.Vehicle v, I76.Sim.Terrain.IGround? ground) =>
        MotionOf(v, ground is null ? float.NaN : SurfaceGrip(v, ground));

    /// <summary>The surface's grip under the car as the vehicle step reads it (none on a grippy surface for a car flagged so).</summary>
    public static float SurfaceGrip(I76.Sim.Vehicles.Vehicle v, I76.Sim.Terrain.IGround ground)
    {
        float g = ground.Surface(v.Surface).Grip;
        return g > 0.2f && (v.Flags & I76.Sim.Vehicles.VehicleFlags.NoGripOnLowSurface) != 0 ? 0 : g;
    }

    public static Motion MotionOf(I76.Sim.Vehicles.Vehicle v, float surfaceGrip = float.NaN)
    {
        float fwd = System.Numerics.Vector3.Dot(v.Vel, v.Rot.Forward), left = -System.Numerics.Vector3.Dot(v.Vel, v.Rot.Right);
        float blend = float.IsNaN(surfaceGrip) ? 0 : BlendLock(v, fwd, surfaceGrip), perLock = LatPerLock(v, fwd);
        // a blown front tyre pulls the player's steer 0.3 its way (VehicleStep.YawRate); both blown pull at random
        float pull = v.FrontLeft.Blown == v.FrontRight.Blown ? 0 : v.FrontLeft.Blown ? 0.3f : -0.3f;
        bool sliding = (v.Flags & I76.Sim.Vehicles.VehicleFlags.Sliding) != 0;
        if (fwd * fwd + left * left < 1f) return new(fwd, 0, blend, pull, sliding, perLock);
        float slip = MathF.Atan2(left, MathF.Abs(fwd));
        return new(fwd, fwd < 0 ? -slip : slip, blend, pull, sliding, perLock);
    }

    /// <summary>
    /// The lock at which the 1997 model's grip gives way at this speed on a surface of this grip: its cornering asks for
    /// steer · min(v² · gain, the cap) of lateral acceleration, and past the load-based limit (load · chassis grip · surface
    /// grip · the wheels' grip / 4) it blends toward a slide (the turn squeal, flag 0x2000), past twice that it slides.
    /// </summary>
    /// <summary>The lateral acceleration (m/s²) a full lock asks of the 1997 model at this speed: min(v² · gain, the cap); load-based cornering caps it at the load's limit, which needs the surface, so it is left out here.</summary>
    public static float LatPerLock(I76.Sim.Vehicles.Vehicle v, float forwardSpeed)
    {
        float sumGrip = v.RearRight.Grip + v.RearLeft.Grip + v.FrontRight.Grip + v.FrontLeft.Grip;
        return MathF.Min(forwardSpeed * forwardSpeed * v.P.SteerGain, sumGrip * 7.84f);
    }

    public static float BlendLock(I76.Sim.Vehicles.Vehicle v, float forwardSpeed, float surfaceGrip)
    {
        float sumGrip = v.RearRight.Grip + v.RearLeft.Grip + v.FrontRight.Grip + v.FrontLeft.Grip;
        float load = v.NormalLoad * v.P.ChassisGrip * surfaceGrip * sumGrip * 0.25f;
        float cap = (v.Flags & I76.Sim.Vehicles.VehicleFlags.LoadBasedCornering) == 0 ? sumGrip * 7.84f : load;
        float perLock = MathF.Min(forwardSpeed * forwardSpeed * v.P.SteerGain, cap);
        return perLock > 1e-3f ? load / perLock : float.PositiveInfinity;
    }

    private readonly Assist _a = tuning ?? Default;

    /// <summary>The steer last sent, +1 full left (the engine's sign).</summary>
    public float Steer { get; private set; }
    /// <summary>The last step's parts, for the input log: the key's (after the hold), the countersteer's, and the hold's lock (NaN: none).</summary>
    public float LastRamp { get; private set; }
    public float LastCountersteer { get; private set; }
    public float LastLimit { get; private set; } = float.NaN;

    /// <summary>The key's eased steer; how long the key now held has been held, and which.</summary>
    private float _eased, _heldFor, _throttle;
    private int _heldKey;
    private DriverState _ramp;

    public void Reset() { Steer = 0; _eased = 0; _heldFor = 0; _heldKey = 0; _throttle = 0; _ramp = default; }

    private const CarButtons DriveKeys = CarButtons.ThrottleUp | CarButtons.ThrottleDown | CarButtons.SteerLeft | CarButtons.SteerRight;

    /// <summary>
    /// One step of dt seconds: the keys of <paramref name="input"/> to the pad's Analog input. The throttle ramp is
    /// <see cref="KeyRamp"/>'s with <paramref name="timeBasedTick"/> as the caller would run it (null: the original's, per call).
    /// An input with no driving key held and nothing left of the steer comes back as it was (the stick's, or none).
    /// </summary>
    public CarInput Apply(CarInput input, float dt, float? timeBasedTick, Motion m = default)
    {
        bool left = input.Has(CarButtons.SteerLeft), right = input.Has(CarButtons.SteerRight);
        // left over right when both are held, as the original's ramp (its left is applied last)
        int key = left ? 1 : right ? -1 : 0;
        bool keys = (input.Buttons & DriveKeys) != 0;
        // the stick, with no key held, is the stick's alone
        if (!keys && input.Has(CarButtons.Analog)) { Reset(); _throttle = input.Throttle / 32767f; return input; }
        _heldFor = key != 0 && key == _heldKey ? _heldFor + dt : key != 0 ? dt : 0;
        _heldKey = key;
        _eased += (key - _eased) * (1 - MathF.Exp(-dt / MathF.Max(key != 0 ? _a.SteerTau : _a.CentreTau, 1e-4f)));
        if (key == 0 && MathF.Abs(_eased) < 1e-3f) _eased = 0;
        float slip = _a.CsSlidingOnly && !m.Sliding ? 0 : m.Slip;
        float cs = System.Math.Clamp(slip * _a.CsGain, -_a.CsLimit, _a.CsLimit);
        float held = _eased;
        LastLimit = float.NaN;
        float cap = 1;
        if (_a.GripLimit > 0 && m.BlendLock > 0) cap = MathF.Min(cap, _a.GripLimit * m.BlendLock);
        if (_a.LatCap > 0 && m.LatPerLock > 0) cap = MathF.Min(cap, _a.LatCap / m.LatPerLock);
        if (cap < 1)
        {
            float lim = System.Math.Clamp(cap, MinHold, 1f);
            // a blown tyre's pull comes on top of the key's: the room is the limit less it on its side, more on the other
            float toLeft = System.Math.Clamp(lim - m.Pull, MinHold, 1f), toRight = System.Math.Clamp(lim + m.Pull, MinHold, 1f);
            // a key held past LimitHold lets the hold go over LimitLift: a held turn may go into the blend (and squeal), a correction not
            float lift = _heldFor > _a.LimitHold ? (_a.LimitLift > 0 ? MathF.Min(1, (_heldFor - _a.LimitHold) / _a.LimitLift) : 1) : 0;
            toLeft += (1 - toLeft) * lift; toRight += (1 - toRight) * lift;
            held = System.Math.Clamp(held, -toRight, toLeft);
            LastLimit = held >= 0 ? toLeft : toRight;
        }
        LastRamp = held; LastCountersteer = cs;
        Steer = System.Math.Clamp(cs + held, -1f, 1f);
        if (MathF.Abs(Steer) < 0.5f / 32767) Steer = 0;
        if (!keys)
        {
            _ramp = default; _throttle = 0;
            if (Steer == 0) return input;
            input.Buttons |= CarButtons.Analog;
            input.Steer = Wire(Steer);
            input.Throttle = 0;
            return input;
        }
        var thr = input;
        thr.Buttons &= ~(CarButtons.Analog | CarButtons.SteerLeft | CarButtons.SteerRight);
        float unused = 0;
        KeyRamp.Apply(ref _ramp, thr, dt, timeBasedTick, ref _throttle, ref unused);
        input.Buttons |= CarButtons.Analog;
        input.Steer = Wire(Steer);
        input.Throttle = Wire(_throttle);
        return input;
    }

    private static short Wire(float x) => (short)MathF.Round(System.Math.Clamp(x, -1f, 1f) * 32767f);
}
