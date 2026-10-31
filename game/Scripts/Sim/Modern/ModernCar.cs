using Godot;
using I76.Sim.Math;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using N = System.Numerics;

namespace I76.Game.Sim.Modern;

/// <summary>
/// The player's car as a Jolt rigid body with raycast suspension and a
/// slip-based tyre model (docs/modern-physics.md). Everything is in the
/// game's own frame (no mirroring), relative to the physics origin of
/// <see cref="ModernPhysics"/>, in a world of its own that is never drawn.
/// It reads the car's controls from the <see cref="Vehicle"/> (the same keys
/// and ramps) and writes its state back, so the rest of the game sees an
/// ordinary sim car.
/// </summary>
public partial class ModernCar : RigidBody3D
{
    public Vehicle V = null!;
    public ModernPhysics Host = null!;
    public IGround Ground = null!;

    /// <summary>The tuning, from the car's own data where it has any (see <see cref="Setup"/>).</summary>
    public sealed class Tuning
    {
        /// <summary>Tyre friction = the surface's grip (WRLD's table, 1.5 dirt … 2.3 road) × the wheel's grip × this.</summary>
        public float GripScale = 0.66f;
        /// <summary>The same for drive and brake (the friction ellipse's long axis): the original's traction is the surface's grip · g.</summary>
        public float TractionScale = 0.8f;
        /// <summary>Suspension natural frequency (Hz) and damping ratios.</summary>
        public float Frequency = 1.65f, DampBump = 0.45f, DampRebound = 0.75f;
        /// <summary>Travel above and below the resting position (m).</summary>
        public float TravelUp = 0.16f, TravelDown = 0.18f;
        /// <summary>Anti-roll stiffness as a share of the spring rate (soft with the assist, whose lean then shows; "loose" 1.1).</summary>
        public float AntiRoll = 0.15f;
        /// <summary>Front wheel lock at a standstill (rad); at speed, the lock for <see cref="SteerAccel"/> plus <see cref="SteerHigh"/>.</summary>
        public float SteerLow = 0.6f, SteerHigh = 0.11f, SteerRate = 3.2f;
        /// <summary>The lateral acceleration (u/s²) full lock asks for at speed.</summary>
        public float SteerAccel = 16f;
        /// <summary>Magic-formula shape: stiffness B, shape C (peak near 8°, 80% of it at full slide).</summary>
        public float B = 13f, C = 1.45f;
        /// <summary>The rear tyres' cornering stiffness against the front's.</summary>
        public float RearStiffness = 1.35f;
        /// <summary>Brake force at full pedal (N): the original's 2300·8 (BrakeStrength·mass·8), here before the grip limit.</summary>
        public float BrakeForce = 2300f * 8f * 1.4f;
        /// <summary>The original's engine force (its drive, Engine.Force / v) is scaled by this: fitted per Simulation Rate
        /// to the original's acceleration at that rate (<see cref="I76.Sim.Session.ModernRateTuning"/>).</summary>
        public float DriveScale = 1f;
        /// <summary>The original's tick (s) at the session's Simulation Rate. Its model steps the drive and the turn once a
        /// tick at the state the tick began with (explicit Euler), which gains speed in proportion to the tick; the
        /// assist adds that gain over its own step's (docs/modern-physics.md "Rate").</summary>
        public float Tick = 0.036f;
        /// <summary>1: the assist's rules act at the original's pivot on the rear axle; 0: at the centre of mass.</summary>
        public float Pivot = 1f;
        /// <summary>Rolling resistance as a share of the original's (the surface's rolling × the wheel's, on its load; "loose" 0.5).</summary>
        public float RollingScale = 1f;
        /// <summary>Front share of the brake.</summary>
        public float BrakeFront = 0.62f;
        /// <summary>Centre of mass height as a share of the body's bottom-to-roof height, above the bottom.</summary>
        public float ComHeight = 0.22f;

        // ---- the handling assist: the original's handling on the body (docs/modern-physics.md §2.1) ----
        /// <summary>1: the body turns at the original's commanded yaw rate and keeps its velocity along its heading as the
        /// original's grounded model does; 0: tyres alone ("loose").</summary>
        public float Assist = 1f;
        /// <summary>How quickly the yaw rate meets the command (s), and the most yaw acceleration the assist gives (rad/s²).</summary>
        public float YawResponse = 0.01f, YawAccelMax = 200f;
        /// <summary>The lean out of a turn, as the original's body tilts: radians per u/s² of sideways acceleration, the most
        /// (the original's 0.11), and the stiffness of the spring that holds it (1/s²).</summary>
        public float LeanPerAccel = 0.0096f, LeanMax = 0.0975f, LeanSpring = 120f;
        /// <summary>How quickly sideways slip is taken out while gripping (s): 0, at once, as the original sets its velocity
        /// along the heading every tick it grips (a drift's slip then resets as the original's does).</summary>
        public float SlipResponse = 0f;
        /// <summary>Sideways slip (u/s) above which the car slides, and below which a slide ends (the original's 3).</summary>
        public float SlideEnter = 7f, SlideExit = 3f;
        /// <summary>The sliding model's side friction, as a share of the original's (the surface's grip on the four wheels).</summary>
        public float SlideFriction = 1f;
        /// <summary>Speed away from the ground (u/s) past which it lets go: 0.5 below the path within the original's 0.036 s frame.</summary>
        public float StickBreak = 0.5f / 0.036f;
        /// <summary>With the assist, how far the hull's floor is lifted at its outline (a chamfer, u).</summary>
        public float HullChamfer = 0.15f;
        /// <summary>How long after a touchdown the car is held down (s), and the rebound damping then, against the usual.</summary>
        public float Settle = 0.4f, SettleRebound = 3f;
        /// <summary>1: the car follows the ground as the original's does (velocity kept in the ground's plane while a wheel is
        /// within <see cref="StickReach"/> of it, roll and pitch rates damped); 0: free suspension ("loose").</summary>
        public float Stick = 1f;
        /// <summary>How far below the wheels' droop the ground still holds the car (the original leaves it 0.5 above).</summary>
        public float StickReach = 0.5f;
        /// <summary>The speed (u/s) the car may move away from the ground's plane before it is held back, while its suspension is shorter than at rest (the springs' rebound); none past that.</summary>
        public float LiftAllowance = 1.5f;
        /// <summary>Damping of the roll and pitch rates on the ground, and in the air with the assist (1/s).</summary>
        public float TiltDamp = 6f, AirTiltDamp = 2f;
    }

    public Tuning T = new();

    /// <summary>The handling of the original at the session's Simulation Rate (docs/modern-physics.md "Rate"): the yaw's
    /// answer, the ground's hold and the tick's own speed gains from the tick, the drive and the brake as fitted at that rate.</summary>
    public static void ApplyRate(Tuning t, I76.Sim.Session.ModernRateTuning r)
    {
        var d = new Tuning();
        t.YawResponse = r.YawResponse;
        t.StickBreak = r.StickBreak;
        t.DriveScale = r.DriveScale;
        t.BrakeForce = d.BrakeForce * r.BrakeScale;
        t.Tick = r.Tick;
    }

    /// <summary>I76_MODERN_TUNE="GripScale=0.7,Frequency=2": the tuning's fields by name, for trying values without a build.</summary>
    public static void ApplyOverrides(Tuning t, string? spec)
    {
        foreach (var kv in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // "loose": the tyres and suspension alone, as the first version drove
            if (kv.Trim().Equals("loose", StringComparison.OrdinalIgnoreCase)) { var d = new Tuning(); t.Assist = 0; t.Stick = 0; t.AntiRoll = 1.1f; t.RollingScale = 0.5f; t.DriveScale = 1; t.BrakeForce = d.BrakeForce; continue; }
            var p = kv.Split('=');
            if (p.Length == 2 && typeof(Tuning).GetField(p[0].Trim()) is { } f
                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                f.SetValue(t, x);
            else GD.PushWarning($"I76_MODERN_TUNE: {kv}?");
        }
    }

    private sealed class W
    {
        public Wheel Wheel = null!;
        public bool Front, Driven, Left;
        public N.Vector3 Offset;      // WLOC, body frame (y = radius = rest height)
        public float Radius, K, CBump, CReb, Static;
        public bool Contact, Near;
        public float Len, Load;
        public Vector3 Point, Normal;
        public int Surface;
        public bool Slipping;
    }

    private W[] _w = [];
    private Godot.Collections.Array<Rid>? _self;
    private float _delta, _wheelbase = 2.7f;
    private Vector3 _prevVel;
    /// <summary>Impulses queued by the sim's car-against-car contacts (world, at a point relative to the body origin).</summary>
    private readonly List<(Vector3 J, Vector3 At)> _impulses = [];
    // what this body last wrote into the Vehicle, to see the game's own writes (teleports, kicks)
    private Double3 _wPos;
    private N.Vector3 _wVel, _wAng;
    private bool _wrote;
    private float _airTime, _tippedTime;
    private float _groundedY;

    /// <summary>A new hull contact: what was hit (null: the terrain), the car's velocity there before it, the normal toward the car, the point (from the body origin) and the closing speed.</summary>
    public sealed record Hit(object? Other, Vector3 Vel, Vector3 N, Vector3 At, float Speed);
    /// <summary>The hull's new contacts since the sim last looked.</summary>
    public List<Hit> Hits { get; } = [];
    private readonly Dictionary<ulong, float> _touch = [];
    private float _clock;
    private Vector3 _prevAv;
    /// <summary>Set on touchdown after a flight: the velocity just before and the ground normal.</summary>
    public (Vector3 Vel, Vector3 N, float Fall)? Landed;
    public bool Grounded { get; private set; }
    public float SignedSpeed { get; private set; }

    public void Setup(Vehicle v)
    {
        V = v;
        Mass = v.P.Mass;
        CanSleep = false;
        // no damping of Godot's own (its project default 0.1/s would be a drag growing with speed): drag and rolling are modelled
        LinearDampMode = DampMode.Replace;
        LinearDamp = 0;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0.05f;
        ContactMonitor = true;
        MaxContactsReported = 8;
        ContinuousCd = true;
        CollisionLayer = 2;
        CollisionMask = 1;
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.45f, Bounce = 0.05f };
        // the hull: the COLP box, its outer outline at the bottom (y1) and the waist (y2), the cabin's (x1..x2, z1..z2) at the roof (y3)
        float[] x = v.BoxX, y = v.BoxY, z = v.BoxZ;
        var pts = new List<Vector3>();
        // with the assist, the floor is smaller and higher (raised by HullChamfer, drawn in at the sides): the original's
        // grounded car never touches the ground with its body, and a full-size floor catches every dip and side slope and
        // scrapes off speed the original keeps
        float cy = y[1], cx0 = x[0], cx3 = x[3], cz0 = z[0], cz3 = z[3];
        if (T.Assist > 0 && T.HullChamfer > 0)
        {
            // the floor between the axles only, so that the overhangs meet a rise ahead at the waist (a real car's approach
            // and departure angles) rather than the bumper digging in at the bottom of every dip
            cy = MathF.Min(y[1] + T.HullChamfer, (y[1] + y[2]) / 2);
            float ix = (x[3] - x[0]) * 0.08f;
            float zfw = MathF.Max(v.FrontRight.Offset.Z, v.RearRight.Offset.Z), zrw = MathF.Min(v.FrontRight.Offset.Z, v.RearRight.Offset.Z);
            cx0 += ix; cx3 -= ix;
            if (zfw - zrw > 1f) { cz0 = MathF.Max(z[0], zrw); cz3 = MathF.Min(z[3], zfw); }
        }
        foreach (var (yy, xs, zs) in new[] { (cy, (cx0, cx3), (cz0, cz3)), (y[2], (x[0], x[3]), (z[0], z[3])), (y[3], (x[1], x[2]), (z[1], z[2])) })
            foreach (var xx in new[] { xs.Item1, xs.Item2 })
                foreach (var zz in new[] { zs.Item1, zs.Item2 })
                    pts.Add(new Vector3(xx, yy, zz));
        AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = [.. pts] } });

        // wheels: the four WLOC slots; rear-wheel drive, as the original's traction test (the rear grips)
        _w = [Make(v.FrontRight, true, false), Make(v.FrontLeft, true, true), Make(v.RearRight, false, false), Make(v.RearLeft, false, true)];
        float zf = (_w[0].Offset.Z + _w[1].Offset.Z) / 2, zr = (_w[2].Offset.Z + _w[3].Offset.Z) / 2;
        float zc = (zf + zr) / 2;
        _wheelbase = MathF.Max(MathF.Abs(zf - zr), 1f);
        float yc = y[1] + T.ComHeight * (y[3] - y[1]);
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0, yc, zc);
        float wd = x[3] - x[0], ht = y[3] - y[1], ln = z[3] - z[0];
        float m = v.P.Mass;
        Inertia = new Vector3(m / 12f * (ht * ht + ln * ln), m / 12f * (wd * wd + ln * ln) * 1.1f, m / 12f * (wd * wd + ht * ht));

        // springs from the static load on each axle (the centre of mass between the axles) and the frequency
        float frontShare = MathF.Abs(zf - zr) < 1e-3f ? 0.5f : (zc - zr) / (zf - zr);
        foreach (var w in _w)
        {
            float load = m * 9.8f * (w.Front ? frontShare : 1 - frontShare) / 2;
            float mw = load / 9.8f;
            float om = 2 * MathF.PI * T.Frequency;
            w.K = mw * om * om;
            float cc = 2 * MathF.Sqrt(w.K * mw);
            w.CBump = T.DampBump * cc;
            w.CReb = T.DampRebound * cc;
            w.Static = load;
            w.Len = T.TravelUp;
        }
        for (int a = 0; a < 4; a += 2)
        {
            float track = MathF.Abs(_w[a].Offset.X - _w[a + 1].Offset.X);
            _rollStiff += _w[a].K * track * track * (0.5f + T.AntiRoll);
        }
    }

    private W Make(Wheel wh, bool front, bool left)
    {
        var o = wh.Offset;
        float r = wh.Rest > 0.05f ? wh.Rest : (o.Y > 0.05f ? o.Y : 0.33f);
        return new W { Wheel = wh, Front = front, Driven = !front, Left = left, Offset = o, Radius = r };
    }

    private static Vector3 PlaneNormal(Vector3 a, Vector3 b, Vector3 up)
    {
        var n = a.Cross(b);
        if (n.LengthSquared() < 1e-6f) return up;
        n = n.Normalized();
        return n.Dot(up) < 0 ? -n : n;
    }

    public string Debug() => string.Join(" ", _w.Select(w => w.Contact ? $"[{w.Load:0} {w.Len:0.00}{(w.Slipping ? " s" : "")}]" : "[air]"));

    public void QueueImpulse(Vector3 j, Vector3 at) => _impulses.Add((j, at));

    private static Vector3 G(N.Vector3 v) => new(v.X, v.Y, v.Z);
    private static N.Vector3 S(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Place the body where the Vehicle is, with its motion (a teleport, or taking over from the original's model).</summary>
    public void TakeOver(PhysicsDirectBodyState3D? s = null)
    {
        var o = Host.Origin;
        var r = V.Rot.Orthonormalized();
        var xf = new Transform3D(new Basis(G(r.Right), G(r.Up), G(r.Forward)), new Vector3((float)(V.Pos.X - o.X), (float)(V.Pos.Y - o.Y), (float)(V.Pos.Z - o.Z)));
        var lv = G(V.Vel);
        // the original's rates (pitch, yaw, roll) turn the body by Euler(−ω·dt) in its own frame: ω_world = −R·ω
        var av = -G(r.ToWorld(V.AngVel));
        if (s is not null) { s.Transform = xf; s.LinearVelocity = lv; s.AngularVelocity = av; }
        else { Transform = xf; LinearVelocity = lv; AngularVelocity = av; }
        _prevVel = lv;
        _wrote = false;
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D s)
    {
        float dt = s.Step;
        if (dt <= 0) return;
        // the game's own writes since this body last wrote: a teleport (script, melee respawn), or a kick (death, mines)
        if (_wrote)
        {
            if (V.Pos != _wPos) TakeOver(s);
            else
            {
                if (V.Vel != _wVel) s.LinearVelocity = G(V.Vel);
                if (V.AngVel != _wAng) s.AngularVelocity = -G(V.Rot.ToWorld(V.AngVel));
            }
        }
        foreach (var (j, at) in _impulses) s.ApplyImpulse(j, at);
        _impulses.Clear();
        Host.Control(dt);       // the keys now, not as of the sim's last tick

        var xf = s.Transform;
        var b = xf.Basis.Orthonormalized();
        Vector3 right = b.X, up = b.Y, fwd = b.Z;
        var lv = s.LinearVelocity;
        var av = s.AngularVelocity;
        var com = xf.Origin + s.CenterOfMass;
        float speed = lv.Length();
        float vx = lv.Dot(fwd);
        SignedSpeed = vx;
        bool dead = (V.Flags & VehicleFlags.Dead) != 0;
        bool tipped = up.Y < 0.1f;

        // controls, as the original reads them: throttle < 0 brakes; Gear −1 drives backward; drive state 0 brakes, 2 cuts drive
        float throttle = V.Throttle;
        if (dead) throttle = 0;
        if (V.DriveState == 0) throttle = -1;
        if (throttle > 0 && (V.DriveState == 2 || (V.Flags & VehicleFlags.EngineOn) == 0)) throttle = 0;
        // the lock the speed allows: what turns the car at about its grip, plus the front tyres' slip (a steering assist,
        // so that the keyboard's full lock at speed asks for the limit rather than far past it)
        float lockMax = MathF.Min(T.SteerLow, _wheelbase * T.SteerAccel / MathF.Max(vx * vx, 1f) + T.SteerHigh);
        float steerTarget = V.Steer * lockMax;
        // assisted, the front wheels turn to the angle that makes the commanded yaw rate (L·ω/v), so the tyres agree with it
        if (T.Assist > 0 && MathF.Abs(vx) > 1)
        {
            float kin = MathF.Atan(_wheelbase * OriginalYaw(vx, up).Yaw / vx);
            steerTarget += (Math.Clamp(kin, -T.SteerLow, T.SteerLow) - steerTarget) * Math.Clamp(T.Assist, 0, 1);
        }
        if (dead) steerTarget = 0;
        float maxStep = T.SteerRate * dt;
        _delta += Math.Clamp(steerTarget - _delta, -maxStep, maxStep);

        // suspension rays from each wheel's top (the travel above rest), against terrain and scenery
        var space = s.GetSpaceState();
        int contacts = 0, near = 0;
        var nearNormal = Vector3.Zero;
        foreach (var w in _w)
        {
            var top = xf * new Vector3(w.Offset.X, w.Offset.Y + T.TravelUp, w.Offset.Z);
            float reach = T.TravelUp + T.TravelDown + w.Radius;
            // the ray reaches past the droop by the stick reach: ground there is not under the tyre, but still holds the car
            float probe = reach + (T.Stick > 0 ? T.StickReach : 0);
            var q = PhysicsRayQueryParameters3D.Create(top, top - up * probe, 1, _self ??= new Godot.Collections.Array<Rid> { GetRid() });
            var hit = space.IntersectRay(q);
            w.Contact = hit.Count > 0;
            w.Near = false;
            w.Slipping = false;
            if (!w.Contact) { w.Len = T.TravelUp + T.TravelDown; w.Load = 0; continue; }
            w.Point = (Vector3)hit["position"];
            w.Normal = (Vector3)hit["normal"];
            // a wall under the wheel's ray is the hull's to meet, not the tyre's
            if (w.Normal.Dot(up) < 0.35f) { w.Contact = false; w.Len = T.TravelUp + T.TravelDown; w.Load = 0; continue; }
            w.Len = (top - w.Point).Dot(up) - w.Radius;
            near++;
            w.Near = true;
            nearNormal += w.Normal;
            if (w.Len > T.TravelUp + T.TravelDown) { w.Contact = false; w.Len = T.TravelUp + T.TravelDown; w.Load = 0; continue; }
            contacts++;
        }

        // the ground's plane under the car, as the original takes it from its four wheels' heights: through the near
        // wheels' ground points (all four: the two diagonals), not the triangles' own normals, which a rough heightfield
        // tips by tens of degrees from one wheel to the next
        if (near == 4) nearNormal = PlaneNormal(_w[1].Point - _w[2].Point, _w[0].Point - _w[3].Point, up);
        else if (near == 3)
        {
            var p = _w.Where(w => w.Near).Select(w => w.Point).ToArray();
            nearNormal = PlaneNormal(p[1] - p[0], p[2] - p[0], up);
        }

        // the assist holds only a car on its wheels: on its side or past 40° to the ground it is the tyres' and Jolt's,
        // so that it falls back onto its wheels (or rights itself) as freely as without it
        bool upright = near >= 2 && up.Dot(nearNormal.Normalized()) > 0.77f;

        // springs, dampers, bump stops, anti-roll
        foreach (var w in _w)
        {
            if (!w.Contact) continue;
            var top = xf * new Vector3(w.Offset.X, w.Offset.Y + T.TravelUp, w.Offset.Z);
            var vTop = lv + av.Cross(top - com);
            float compVel = -vTop.Dot(up);
            float x = T.TravelUp + w.Static / w.K - w.Len;        // compression past the spring's free length
            float f = w.K * x + (compVel > 0 ? w.CBump : w.CReb * (_settle > 0 && T.Stick > 0 ? T.SettleRebound : 1)) * compVel;
            if (w.Len < 0.03f) f += w.K * 12f * (0.03f - w.Len) + w.CBump * 2 * MathF.Max(compVel, 0);
            w.Load = MathF.Max(f, 0);
        }
        for (int a = 0; a < 4; a += 2)
        {
            W r = _w[a], l = _w[a + 1];
            if (!r.Contact || !l.Contact) continue;
            float d = (r.Len - l.Len) * r.K * T.AntiRoll;
            r.Load = MathF.Max(r.Load - d, 0);
            l.Load = MathF.Max(l.Load + d, 0);
        }

        // the engine and its automatic box (the original's model), on this body's speed
        var engFlags = V.Flags & ~(VehicleFlags.Sliding | VehicleFlags.Airborne);
        if (contacts == 0) engFlags |= VehicleFlags.Airborne;
        V.Engine.Update(V.Throttle, vx, engFlags, V.Gear < 0, V.DriveState, dt);
        float drive = 0;
        if (throttle > 0 && !tipped)
        {
            drive = V.Engine.Force * V.P.EngineScale * T.DriveScale * throttle / MathF.Max(MathF.Abs(vx), 1f);
            if ((V.Flags & VehicleFlags.TurboDrive) != 0) drive *= 5;
            // the original adds drive·tick a tick at the tick's starting speed: for a force ∝ 1/v that gains
            // ½·tick·a²/v a second over the exact motion; the body's own step gains its share of it already
            float v1 = MathF.Max(MathF.Abs(vx), 1f);
            if (T.Assist > 0) drive *= 1 + Math.Clamp(0.5f * MathF.Max(T.Tick - dt, 0) * (drive / Mass) / v1, 0f, 0.5f);
            drive *= V.Gear < 0 ? -1 : 1;
        }
        float brake = throttle < 0 ? -throttle * T.BrakeForce : 0;
        if (tipped || dead) brake = MathF.Max(brake, 0.3f * T.BrakeForce);
        bool hand = V.Handbrake != 0;
        int driven = _w.Count(w => w.Driven && w.Contact);
        // assisted, the drive goes to the driven wheels by their load (a locked axle: the inner wheel, unloaded by the lean
        // in a turn, does not waste it); loose, half each
        float drivenLoad = _w.Where(w => w.Driven && w.Contact).Sum(w => w.Load);
        float DriveShare(W w) => T.Assist > 0 && drivenLoad > 1 ? w.Load / drivenLoad : 1f / driven;

        // tyres: slip angle → lateral force on the magic formula; drive, brake and rolling along the wheel; a friction ellipse
        float mw = Mass / 4;
        // assisted, the assist owns the sideways motion: the springs push along the body's up less its lean across the
        // ground, so that the lean of a turn does not push the car out of it
        bool assisted = T.Assist > 0 && contacts >= 2 && upright && !tipped && !dead;
        // assisted, the drive is the original's: along the heading at the centre of mass, whenever the car is on the ground
        // (a wheel within the stick's reach), up to the original's traction (below), not through
        // whichever rear tyre a bump has just unloaded; the body does not squat under it either
        bool groundDrive = T.Assist > 0 && upright && !tipped && !dead;
        // the original's traction: the rear wheels' grip on a quarter of the load each
        float groundTraction = (V.RearRight.Grip + V.RearLeft.Grip) * 0.25f * Ground.Surface(V.Surface).Grip * 9.8f * Mass;
        var support = up;
        if (assisted && near > 0)
        {
            var gn0 = nearNormal.Normalized();
            var across = (right - gn0 * gn0.Dot(right)).Normalized();
            support = (up - across * up.Dot(across)).Normalized();
        }
        foreach (var w in _w)
        {
            if (!w.Contact) continue;
            // assisted, the tyres' forces are along the heading, as the original's brake and drive (fwd·drive): the assist
            // owns the sideways motion, and a steered front wheel's brake would push the car across and turn it
            float delta = w.Front && !assisted ? _delta : 0;
            // +steer turns the wheel toward −right (the engine's left)
            var wf = fwd * MathF.Cos(delta) - right * MathF.Sin(delta);
            var n = w.Normal;
            var f = (wf - n * n.Dot(wf)).Normalized();
            var side = n.Cross(f).Normalized();
            var vp = lv + av.Cross(w.Point - com);
            float vxw = vp.Dot(f), vyw = vp.Dot(side);
            w.Surface = Ground.SurfaceId(Host.Origin.X + w.Point.X, Host.Origin.Z + w.Point.Z);
            float mu = Ground.Surface(w.Surface).Grip * w.Wheel.Grip * T.GripScale * (w.Wheel.Blown ? 0.45f : 1f);
            float max = mu * w.Load;
            float alpha = MathF.Atan(vyw / MathF.Max(MathF.Abs(vxw), 2.5f));
            // the rear a little stiffer than the front: the car understeers at the limit rather than spinning
            float stiff = w.Front ? T.B : T.B * T.RearStiffness;
            float fy = -max * MathF.Sin(T.C * MathF.Atan(stiff * alpha));
            // assisted, the sideways motion is the assist's (the original's is kinematic): the tyres keep their drive,
            // brake and the friction ellipse, and what is left of their side force is (1 − Assist)
            fy *= 1 - Math.Clamp(T.Assist, 0, 1) * (assisted ? 1 : 0);
            bool locked = hand && !w.Front;
            float fx = 0;
            if (locked)
            {
                // the handbrake: the rear wheels slide, along and across, at the sliding friction
                float slide = 0.8f * max;
                fx = -slide * Math.Clamp(vxw / 1.5f, -1f, 1f);
                fy = Math.Clamp(fy, -0.35f * slide, 0.35f * slide);
                w.Slipping = MathF.Abs(vxw) > 1;
            }
            else
            {
                // the lateral force first; drive and brake get what the friction ellipse leaves (a little ABS and
                // traction control), never less than a third of the wheel's grip
                // the ellipse's long axis is the traction: the original drives and brakes on the surface's full grip
                float maxX = max * T.TractionScale / T.GripScale;
                float share = fy / MathF.Max(max, 1e-3f);
                float room = maxX * MathF.Sqrt(MathF.Max(1 - share * share, 0.11f));
                if (w.Driven && driven > 0 && !groundDrive) fx += Math.Clamp(drive * DriveShare(w), -room, room);
                float wb = brake * (w.Front ? T.BrakeFront : 1 - T.BrakeFront) / 2;
                // the brake stops a still car rather than pushing it
                if (wb > 0) fx -= Math.Clamp(vxw * mw / dt * 0.5f, -MathF.Min(wb, room), MathF.Min(wb, room));
                fx = Math.Clamp(fx, -room, room);
                if (w.Driven && driven > 0 && MathF.Abs(drive * DriveShare(w)) > room && !groundDrive) w.Slipping = true;
                if (w.Driven && groundDrive && MathF.Abs(drive) > groundTraction) w.Slipping = true;
            }
            fx -= Ground.Surface(w.Surface).Rolling * w.Wheel.Rolling * w.Load * Math.Clamp(vxw, -1f, 1f) * T.RollingScale;
            if (MathF.Abs(alpha) > 0.16f) w.Slipping = true;
            var force = support * w.Load + f * fx + side * fy;
            s.ApplyForce(force, w.Point - xf.Origin);
        }

        if (groundDrive && drive != 0 && nearNormal.LengthSquared() > 0)
        {
            var gn1 = nearNormal.Normalized();
            var along = (fwd - gn1 * gn1.Dot(fwd)).Normalized();
            s.ApplyCentralForce(along * Math.Clamp(drive, -groundTraction, groundTraction));
        }
        Assist(s, xf, up, fwd, right, lv, av, near, nearNormal, contacts, tipped || dead || (near > 0 && !upright), hand);

        // air drag as the original's (drag coefficient · v² · 0.1 per unit mass)
        if (speed > 1) s.ApplyCentralForce(-lv * (V.P.Drag * 0.1f * speed * Mass));

        // self-righting: on its side or roof (or leaning past 63°) and nearly still for a moment, the car rolls itself back over
        if (up.Y < 0.45f && speed < 2.5f) _tippedTime += dt; else _tippedTime = 0;
        if (_tippedTime > 1.2f && !dead)
        {
            var axis = up.Cross(Vector3.Up);
            if (axis.LengthSquared() < 1e-4f) axis = fwd;
            axis = axis.Normalized();
            // a roll toward upright about the car's own long axis where it can, damped
            var target = axis * 2.2f;
            s.ApplyTorque((target - av) * Mass * 4.5f);
            s.ApplyCentralForce(Vector3.Up * Mass * 9.8f * 0.6f);
            if (_tippedTime > 6f)
            {
                // still stuck: put it back on its wheels where it is
                var f0 = new Vector3(fwd.X, 0, fwd.Z);
                if (f0.LengthSquared() < 1e-4f) f0 = new Vector3(up.X, 0, up.Z);
                f0 = f0.Normalized();
                s.Transform = new Transform3D(new Basis(Vector3.Up.Cross(f0), Vector3.Up, f0), xf.Origin + Vector3.Up * 1.5f);
                s.LinearVelocity = Vector3.Zero;
                s.AngularVelocity = Vector3.Zero;
                _tippedTime = 0;
            }
        }

        // hull contacts: each new touch of the terrain or a scenery body (none for 0.15 s before) is an impact, at the
        // velocity the car had the step before (the solver has already answered it), for the original's damage
        _clock += dt;
        for (int i = 0; i < s.GetContactCount(); i++)
        {
            ulong id = s.GetContactColliderId(i);
            object? other = id == Host.TerrainId ? null : Host.StaticOf(id);
            if (id != Host.TerrainId && other is null) continue;
            bool fresh = !_touch.TryGetValue(id, out float last) || _clock - last > 0.15f;
            _touch[id] = _clock;
            if (!fresh) continue;
            var at = s.GetContactLocalPosition(i);
            var nrm = s.GetContactLocalNormal(i);
            var pv = _prevVel + _prevAv.Cross(at - com);
            float vn = MathF.Abs(pv.Dot(nrm));
            if (vn < 1f) continue;
            if (nrm.Dot(pv) > 0) nrm = -nrm;        // from what was hit toward the car
            Hits.Add(new Hit(other, pv, nrm, at - xf.Origin, vn));
        }

        // flight and touchdown
        bool grounded = contacts > 0;
        if (!grounded) _airTime += dt;
        else
        {
            if (_airTime > 0.25f)
            {
                var gn = _w.Where(w => w.Contact).Select(w => w.Normal).FirstOrDefault(Vector3.Up);
                Landed = (_prevVel, gn, _groundedY - xf.Origin.Y);
                _settle = T.Settle;
            }
            _airTime = 0;
            _groundedY = xf.Origin.Y;
        }
        Grounded = grounded;
        _prevVel = lv;
        _prevAv = av;
        WriteBack(s, xf, b, lv, av, grounded, contacts);
        // I76_TELEMETRY=phys: a line a physics step as well (DriveTest logs at the sim's ticks), for the yaw's answer
        if (_physTelemetry) GD.Print(FormattableString.Invariant($"ptel {_clock:0.00000} heading {MathF.Atan2(fwd.X, fwd.Z):0.00000} steer {V.Steer:0.000} v {speed:0.000}"));
    }
    private static readonly bool _physTelemetry = System.Environment.GetEnvironmentVariable("I76_TELEMETRY") == "phys";

    private bool _sliding;
    /// <summary>The assist ran the original's blend this step (veh flag 0x2000: past the load-based cap, under twice it).</summary>
    private bool _blending;
    private float _leanTarget;
    /// <summary>The suspension's roll stiffness (N·m/rad): springs and anti-roll across each axle's track.</summary>
    private float _rollStiff;
    /// <summary>Time left in the settle after a touchdown: the car is held to the ground and its tilt damped harder.</summary>
    private float _settle;

    /// <summary>
    /// The original's handling on the body (Tuning.Assist, Tuning.Stick; docs/modern-physics.md §2.1). While the car is
    /// on the ground:
    /// <list type="bullet">
    /// <item>its yaw rate is set to the original's commanded rate for this steer and speed (steer·v·gain,
    /// capped at the lateral limit over v), at the velocity level, as the original sets it outright;</item>
    /// <item>its velocity follows the heading as the original's grounded model has it: under the load-based cap the slip
    /// goes and the whole speed points along the heading; past it only the original's blend of the turn is applied, so
    /// the slip builds and brakes the car; past twice it, or knocked sideways past <see cref="Tuning.SlideEnter"/>, or on
    /// the handbrake, the original's sliding model (yaw eased toward steer·π, side friction against the slip) until the
    /// slip is under 3;</item>
    /// <item>it follows the ground: while a wheel is within the stick reach, velocity away from the ground's plane past
    /// <see cref="Tuning.LiftAllowance"/> is turned back into it at the whole speed (the original keeps its velocity in
    /// that plane until the ground falls 0.5 below its path in a frame), roll and pitch rates are damped, and after a
    /// touchdown the rebound is held for <see cref="Tuning.Settle"/>; a ramp's end or a drop still flies;</item>
    /// <item>the body leans out of a turn, a roll spring toward up to the original's 0.11 rad.</item>
    /// </list>
    /// In the air it only damps the body's roll and pitch rates.
    /// </summary>
    private void Assist(PhysicsDirectBodyState3D s, Transform3D xf, Vector3 up, Vector3 fwd, Vector3 right, Vector3 lv, Vector3 av,
        int near, Vector3 nearNormal, int contacts, bool off, bool hand)
    {
        float dt = s.Step;
        if (_settle > 0) _settle -= dt;
        _blending = false;
        if (near == 0 || off)
        {
            _sliding = false;
            // in the air the original holds the body's roll and eases its pitch (flight): the roll and pitch
            // rates a ramp's lip or a ledge gives the body die away rather than tumbling it
            if (near == 0 && !off && T.Assist > 0)
            {
                var spin = av - up * av.Dot(up);
                s.ApplyTorque(-spin * (T.AirTiltDamp * T.Assist) * (Inertia.X + Inertia.Z) * 0.5f);
            }
            return;
        }
        var gn = nearNormal.Normalized();
        float m = Mass;
        // ---- stay on the ground: the original keeps the velocity in the ground's plane until the ground falls more than
        // 0.5 below the car's path in a frame (0.036 s), i.e. while the speed away from it is under 0.5/0.036 ----
        if (T.Stick > 0 && (near >= 2 || _settle > 0))
        {
            // the allowance is the springs' rebound up to the ride height, not a climb away from the ground: past it (the near
            // wheels' mean travel longer than at rest) the car is held to the plane, or it rose off every crest at the
            // allowance until the wheels were out of reach, where the original's car follows the ground
            float away = lv.Dot(gn), allow = _settle > 0 || Extended() ? 0 : T.LiftAllowance;
            if (away > allow && (away < T.StickBreak || _settle > 0))
            {
                var held = lv - gn * (away - allow);
                // rolling along, the original turns its velocity into the ground's plane at the whole speed (it is rebuilt
                // from the speed along the heading every frame), so a crest bends the path without scrubbing speed; after
                // a touchdown it holds whatever the rebound (the springs and bump stops give back a big landing's speed
                // and the car would bounce off again), and that rebound is lost, not turned along
                if (_settle <= 0 && held.LengthSquared() > 1e-4f) held = held.Normalized() * lv.Length();
                s.ApplyCentralImpulse((held - lv) * (m * T.Stick));
            }
            var tilt = av - up * av.Dot(up);
            s.ApplyTorque(-tilt * (T.TiltDamp * T.Stick * (_settle > 0 ? 2 : 1)) * (Inertia.X + Inertia.Z) * 0.5f);
        }
        if (T.Assist <= 0 || contacts < 2) return;
        // ---- the original's handling ----
        var gf = (fwd - gn * gn.Dot(fwd)).Normalized();
        var gr = gn.Cross(gf).Normalized();                    // the ground-plane right (Y×Z = X, the body's right on the flat)
        // the original turns the car about its pivot on the rear axle (the rear wheels' z), and the velocity
        // it keeps along the heading, blends and slides is that point's: the rules act on it, not on the centre of mass
        var arm = gf * ((V.P.PivotForward - CenterOfMass.Z) * T.Pivot);
        var lvp = lv + av.Cross(arm);
        float vf0 = lvp.Dot(gf), vlat = lvp.Dot(gr);
        // the centre of mass, ahead of the pivot, circles it: the turn carries its sideways velocity into the heading at
        // ω²·d a second, which the original's pivot never sees; that is the pull toward the pivot, taken out here
        float yawNow = av.Dot(up);
        float vf = vf0 - yawNow * yawNow * (CenterOfMass.Z - V.P.PivotForward) * T.Pivot * dt * MathF.Sign(vf0);
        // the original's speed is the whole speed, signed by the heading (its signedSpeed)
        float sp = MathF.Sqrt(vf * vf + vlat * vlat) * (vf < 0 ? -1 : 1);
        var (yawCmd, _, _, sideFriction, maxLatLoad) = OriginalYaw(sp, gn);
        // sliding: on the handbrake, or knocked sideways; it grips again under 3 u/s of slip (the original's exit)
        if (hand && MathF.Abs(vf) > 2) _sliding = true;
        // (a knock: the original's grip would take any slip out at once, but past its cap it carries the slip, unbounded,
        // in its blend, and so does this)
        if (MathF.Abs(vlat) > T.SlideEnter && (maxLatLoad <= 0 || MathF.Abs(yawCmd * sp) <= maxLatLoad)) _sliding = true;
        if (_sliding && !hand && MathF.Abs(vlat) < T.SlideExit) _sliding = false;
        // the turn asks for more than twice the load-based cap: the original's YawRate reports it slid, and
        // the frame goes to the sliding model (a hard turn at speed: full lock above the speed where steering saturates)
        if (!_sliding && maxLatLoad > 0 && MathF.Abs(yawCmd * sp) > 2 * maxLatLoad) _sliding = true;
        float u = av.Dot(up);                                  // + turns the nose toward the right: the original's yaw is −u
        // the original sets the yaw rate and the velocity outright (they are kinematic in its grounded model), so the
        // assist works on the velocities, as a constraint would: the solver still answers contacts after it
        float want, response;
        if (!_sliding) { want = -yawCmd; response = T.YawResponse; }
        else
        {
            // the sliding model: steer·gear·π, eased at 2.66/L a second
            float slideYaw = V.Steer * (V.Gear < 0 ? -1 : 1) * MathF.PI * (MathF.Abs(vf) < 1 ? MathF.Abs(vf) : 1);
            want = -slideYaw;
            response = 1f / MathF.Max(V.P.YawRateLimit, 0.5f);
        }
        float k = T.Assist * MathF.Min(1, dt / response);
        float du = Math.Clamp((want - u) * k, -T.YawAccelMax * dt, T.YawAccelMax * dt);
        s.AngularVelocity = s.AngularVelocity + up * du;
        // the heading turns by (u + du)·dt this step: the velocity's sideways part as seen from the new heading
        float vlatTurned = vlat + vf * (u + du) * dt;
        float vlatNew, vfNew = vf;
        if (!_sliding)
        {
            float need = MathF.Abs(yawCmd * sp);
            if (need <= maxLatLoad || maxLatLoad <= 0)
            {
                // gripping under the load-based cap: the original sets the velocity along the heading at the whole speed
                // (fwd·signedSpeed), so the slip turns into speed along the heading rather than being scrubbed off
                vlatNew = vlatTurned * (1 - MathF.Min(1, dt / T.SlipResponse));
                vfNew = MathF.Sign(vf) * MathF.Sqrt(MathF.Max(vf * vf + vlat * vlat - vlatNew * vlatNew, 0));
            }
            else
            {
                // past it the original keeps the velocity and turns it by a share of the yaw (its blend, veh flag 0x2000:
                // 1 at the cap, 0 at twice it): its centripetal pull is across the heading, so the slip that builds
                // brakes the car, and a hard turn at speed drifts wide and slows
                float blend = Math.Clamp((2 * maxLatLoad - need) / maxLatLoad, 0, 1);
                vlatNew = vlat - yawCmd * sp * blend * dt;
                _blending = true;
            }
        }
        else
        {
            // sliding: the sliding model's side friction, −(right·v)/|v| · the four grips' friction
            float f = sideFriction * T.SlideFriction * dt * MathF.Abs(vlat) / MathF.Max(MathF.Abs(sp), 1f);
            vlatNew = MathF.Abs(vlat) <= f ? 0 : vlat - MathF.Sign(vlat) * f;
        }
        if (!_sliding)
        {
            // the original turns the velocity a tick at a time: each tick adds the centripetal ω·v·tick across a velocity
            // of v, so the speed grows by ½(ω·tick)² a tick, ½·ω²·tick·v a second (the turn's yaw ω, its blended share
            // past the cap); the body's own step pumps only past the cap, by its own share
            float need = MathF.Abs(yawCmd * sp);
            float wc = need <= maxLatLoad || maxLatLoad <= 0 ? yawCmd : yawCmd * Math.Clamp((2 * maxLatLoad - need) / maxLatLoad, 0, 1);
            float tick = need <= maxLatLoad || maxLatLoad <= 0 ? T.Tick : MathF.Max(T.Tick - dt, 0);
            float pump = 1 + 0.5f * wc * wc * tick * dt;
            vfNew *= pump; vlatNew *= pump;
        }
        float dvl = (vlatNew - vlat) * T.Assist, dvf = (vfNew - vf0) * T.Assist;
        // (on the state's velocity as it is now: the stick above may already have turned it)
        // (and the turn is about the pivot: the centre of mass moves with the yaw change so that the pivot does not)
        s.LinearVelocity = s.LinearVelocity + gr * dvl + gf * dvf - (up * du).Cross(arm);
        // the body leans out of the turn as the original's tilts (up to its 0.11 rad): a roll spring toward a lean that
        // grows with the turn's sideways acceleration, holding the suspension's own roll stiffness at that angle
        if (contacts >= 3 && _rollStiff > 0)
        {
            float lat = MathF.Abs(vf) > 1 ? vf * (u + du) : 0;               // toward the right
            float target = Math.Clamp(lat * T.LeanPerAccel, -T.LeanMax, T.LeanMax) * T.Assist;
            float roll = MathF.Asin(Math.Clamp(right.Dot(gn), -1, 1));
            float rate = av.Dot(gf);
            _leanTarget += (target - _leanTarget) * MathF.Min(1, dt / 0.12f);
            float tq = _rollStiff * _leanTarget + Inertia.Z * (T.LeanSpring * (_leanTarget - roll) - 2 * MathF.Sqrt(T.LeanSpring) * rate);
            s.ApplyTorque(gf * tq);
        }
        else _leanTarget = 0;
    }

    /// <summary>Whether the near wheels' suspension is, on the mean, longer than at rest (TravelUp, where the spring holds the static load).</summary>
    private bool Extended()
    {
        float sum = 0;
        int n = 0;
        foreach (var w in _w)
            if (w.Near) { sum += w.Len; n++; }
        return n > 0 && sum / n > T.TravelUp;
    }

    /// <summary>
    /// The original's commanded yaw rate (+ left) for the steer input at this speed, with its lateral limits: the cap
    /// (sumGrip·7.84, or the load-based one with Arcade Phys.), the slide limit (twice the load-based cap), and the
    /// sliding model's side friction (the four grips on the surface's grip × a quarter of the load).
    /// </summary>
    private (float Yaw, float MaxLat, float SlideLimit, float SideFriction, float MaxLatLoad) OriginalYaw(float speed, Vector3 n)
    {
        float steer = V.Steer;
        bool fr = V.FrontRight.Blown, fl = V.FrontLeft.Blown;
        if (fl && !fr) steer += 0.3f;
        if (fr && !fl) steer -= 0.3f;
        steer = Math.Clamp(steer, -1f, 1f);
        float sumGrip = V.RearRight.Grip + V.RearLeft.Grip + V.FrontRight.Grip + V.FrontLeft.Grip;
        float sg = Ground.Surface(V.Surface).Grip;
        if (sg > 0.2f && (V.Flags & VehicleFlags.NoGripOnLowSurface) != 0) sg = 0;
        float load = n.Y * n.Y * n.Y * 9.8f;
        float maxLatLoad = load * V.P.ChassisGrip * sg * sumGrip * 0.25f;
        float maxLat = (V.Flags & VehicleFlags.LoadBasedCornering) == 0 ? sumGrip * 7.84f : maxLatLoad;
        float yaw = speed * speed * V.P.SteerGain <= maxLat ? steer * speed * V.P.SteerGain : steer * maxLat / speed;
        return (yaw, maxLat, maxLatLoad + maxLatLoad, sumGrip * sg * load * 0.25f, maxLatLoad);
    }

    /// <summary>The body's state into the Vehicle, for everything else that reads it.</summary>
    private void WriteBack(PhysicsDirectBodyState3D s, Transform3D xf, Basis b, Vector3 lv, Vector3 av, bool grounded, int contacts)
    {
        var o = Host.Origin;
        var rot = new Rows(S(b.X), S(b.Y), S(b.Z));
        V.Pos = new Double3(o.X + xf.Origin.X, o.Y + xf.Origin.Y, o.Z + xf.Origin.Z);
        V.Rot = rot;
        V.Accel = S((lv - _prevVel) / s.Step);
        V.Vel = S(lv);
        V.Speed = lv.Length();
        V.AngVel = -rot.ToLocal(S(av));
        var flags = V.Flags & ~(VehicleFlags.Airborne | VehicleFlags.Sliding | VehicleFlags.Blending);
        if (!grounded) flags |= VehicleFlags.Airborne;
        if (_w.Any(w => w.Contact && w.Slipping)) flags |= VehicleFlags.Sliding;
        // the blend flag, as the original's grip model sets it (the turn sound reads it, TyreSounds)
        else if (grounded && _blending) flags |= VehicleFlags.Blending;
        V.Flags = flags;
        if (grounded)
        {
            var gw = _w.First(w => w.Contact);
            V.GroundNormal = S(gw.Normal);
            V.Surface = Ground.SurfaceId(V.Pos.X, V.Pos.Z);       // under the body, as the original reads it
            V.GroundHeight = (float)(o.Y + _w.Where(w => w.Contact).Average(w => w.Point.Y));
            V.HeightAboveGround = 0;
        }
        foreach (var w in _w)
        {
            w.Wheel.Travel = Math.Clamp(T.TravelUp - w.Len, -T.TravelDown, T.TravelUp);
            w.Wheel.SteerAngle = w.Front ? -_delta : 0;
            w.Wheel.Spin += V.Speed * s.Step;
        }
        _wPos = V.Pos; _wVel = V.Vel; _wAng = V.AngVel;
        _wrote = true;
    }
}
