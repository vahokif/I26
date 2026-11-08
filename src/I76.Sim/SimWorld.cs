using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;

namespace I76.Sim;

/// <summary>
/// Every simulated vehicle and the world they share. One frame does what the
/// original's frame does for them: the object collision pass first
/// (each car against the static colliders, then every pair of
/// cars, box against box both ways), then each car's update,
/// which responds to its contact record in its first substep.
/// </summary>
public sealed partial class SimWorld(IGround ground, CrtRand rand)
{
    public IGround Ground { get; } = ground;
    public CrtRand Rand { get; } = rand;
    public List<Vehicle> Vehicles { get; } = [];
    public Vehicle? Player { get; set; }
    /// <summary>
    /// The other human-driven cars (a network melee's players): each takes the
    /// player's rules as the original's own machine gave them to its car --
    /// the player's step, the weapon click, no wreck.
    /// </summary>
    public List<Vehicle> Humans { get; } = [];

    /// <summary>The player's car or another human's.</summary>
    public bool IsHuman(Vehicle v) => ReferenceEquals(v, Player) || Humans.Contains(v);

    /// <summary>
    /// A co-op story mission (docs/multiplayer.md "Co-op"): <see cref="Humans"/> are the player's teammates, and the rules
    /// the original gives its one player alone (the cheats, the emplacements' aim, the jammer's blind radars, the T key's
    /// guns) are each human's. Off in single player and in a network melee.
    /// </summary>
    public bool Coop { get; set; }

    /// <summary>A car the single player's own rules apply to: the player's, or in co-op any human's.</summary>
    public bool IsPlayerSide(Vehicle? v) => v is not null && (ReferenceEquals(v, Player) || Coop && Humans.Contains(v));
    public VehicleStep Step { get; } = new(ground, rand) { ExternalContacts = true };

    /// <summary>AI-driven vehicles and what they do this frame (placeholder behaviours until the FSM runs them).</summary>
    public Dictionary<Vehicle, (Ai.AiBlock Ai, Func<Vehicle, System.Numerics.Vector3?> Target)> Ai { get; } = [];
    /// <summary>Each car's collide callback for a car-against-car contact (the original calls A's, then B's).</summary>
    public Action<Vehicle, Vehicle, Contact>? CarContact { get; set; }
    /// <summary>
    /// Vehicles another model moves (the port's optional modern physics,
    /// docs/modern-physics.md): the frame still runs their pairs (and the
    /// sign test of their object pass), so those contacts are found and
    /// damaged as the original does, but calls <see cref="ExternalFrame"/> in
    /// place of their step; that model finds their scenery contacts. Unset,
    /// the frame is the original's.
    /// </summary>
    public Func<Vehicle, bool>? External { get; set; }
    /// <summary>Where a helicopter's kill and crash go (the damage model).</summary>
    public IHeliDamage? HeliDamage { get; set; }

    private static bool Still(Vehicle v) => v.Vel.X * v.Vel.X < 1e-8f && v.Vel.Y * v.Vel.Y < 1e-8f && v.Vel.Z * v.Vel.Z < 1e-8f;
    public Action<Vehicle, float>? ExternalFrame { get; set; }

    /// <summary>The simulation clock in seconds, advanced by every frame (the simulation clock for the specials' on-times).</summary>
    public float Time { get; set; }

    /// <summary>A helicopter that met the ground (flag 0x20): the helicopter's crash unlinked its collider, the player's included, so nothing collides with the wreck.</summary>
    private static bool Crashed(Vehicle v) => v.IsHeli && (v.Flags & VehicleFlags.Dead) != 0;

    public void Frame(float frameDelta)
    {
        Time += frameDelta;
        // the gates' pass-A tick: they slide while the script has them moving
        if (Ground is Collision.IHasStatics hs) hs.World.TickGates(frameDelta);
        foreach (var (v, (ai, target)) in Ai)
            if (target(v) is { } t) Sim.Ai.AiDriver.DriveTo(v, ai, Ground, t, ai.CommandSpeed, frameDelta, traction: m => Step.TractionLimit(v, m, IsFar(v)));
            else { v.Throttle = -1; v.Steer = 0; }
        // the original's pass: each mover against every later mover, then against the statics. A car's record is the
        // last one written, so a scenery contact in the same pass overwrites a car-car one: the wall wins.
        for (int i = 0; i < Vehicles.Count; i++)
        {
            var v = Vehicles[i];
            for (int j = i + 1; j < Vehicles.Count; j++)
                if (!v.Wreck && !Vehicles[j].Wreck && !Crashed(v) && !Crashed(Vehicles[j]) && !v.IsEmplacement && !Vehicles[j].IsEmplacement) PairContacts(v, Vehicles[j], frameDelta);
            // the pre-test: a still body meets no scenery (a helicopter on its pad; cars keep the port's sweep as it was)
            if (!v.Wreck && !Crashed(v) && !v.IsEmplacement && !(v.IsHeli && Still(v))) Step.StaticContacts(v, frameDelta, signsOnly: External?.Invoke(v) == true);
        }
        foreach (var v in Vehicles)
        {
            if (v.Wreck) continue;
            // an emplacement's class update is empty: it never moves; it collides through its static collider
            if (v.IsEmplacement) continue;
            if (External?.Invoke(v) == true) { ExternalFrame?.Invoke(v, frameDelta); continue; }
            bool player = IsHuman(v);
            if (v.Heli is { } heli)
            {
                // The helicopter's update: the frame's contact record is fatal; no terrain sweep, no substeps, no far path
                heli.Touched |= v.Rec.Hit;
                v.Rec.Clear();
                HeliStep.Frame(v, Ground, frameDelta, player, HeliDamage);
                continue;
            }
            // No exemption for the player -- in a cut-scene with the camera far away it takes the far path too
            if (IsFar(v)) FarFrame(v, frameDelta, player);
            else Step.Frame(v, frameDelta, player);
        }
    }

    /// <summary>
    /// <see cref="Frame"/> for one car alone (a network client's prediction of its own car): its pairs with the others
    /// in <paramref name="cars"/> (the server's list order, <paramref name="v"/> among them) with only
    /// <paramref name="v"/>'s record written and no collide callback, then its scenery contacts, then its step. The others are not stepped.
    /// The gates, the AI and the clock are left alone.
    /// </summary>
    public void FrameOne(Vehicle v, float frameDelta, IReadOnlyList<Vehicle> cars)
    {
        if (v.Wreck || Crashed(v) || v.IsEmplacement) return;
        bool past = false;
        foreach (var o in cars)
        {
            if (ReferenceEquals(o, v)) { past = true; continue; }
            if (o.Wreck || Crashed(o) || o.IsEmplacement) continue;
            // the server tests (earlier, later) in its list order
            if (past) PairContacts(v, o, frameDelta, oneSided: v);
            else PairContacts(o, v, frameDelta, oneSided: v);
        }
        // its statics last, as the server's pass
        if (!(v.IsHeli && Still(v))) Step.StaticContacts(v, frameDelta, signsOnly: External?.Invoke(v) == true);
        if (External?.Invoke(v) == true) { ExternalFrame?.Invoke(v, frameDelta); return; }
        if (v.Heli is { } heli)
        {
            heli.Touched |= v.Rec.Hit;
            v.Rec.Clear();
            HeliStep.Frame(v, Ground, frameDelta, IsHuman(v), HeliDamage);
            return;
        }
        if (IsFar(v)) FarFrame(v, frameDelta, IsHuman(v));
        else Step.Frame(v, frameDelta, IsHuman(v));
    }

    /// <summary>
    /// What decides a car's near/far path. The original measures from the camera; here it is the
    /// player's car, or in a network session every human's car, so that no view (F10, binoculars, a script camera,
    /// the projectile camera) changes the simulation (docs/deviations.md, "Near/far path").
    /// </summary>
    public FarPolicy Far { get; set; } = FarPolicy.Player;
    /// <summary>The script's camera (its state, the same on every peer): while a cut-scene holds it, the cars it shows are near.</summary>
    public Missions.ScriptCamera? Scene { get; set; }
    /// <summary>
    /// The camera's own distance term, set
    /// to 600·zoom·sqrt(1 + tan²(fov/2)): 848.5 for the game's
    /// 90° view at zoom 1 (the binoculars raise the zoom). So a car takes the
    /// far path only beyond about 873 from the camera.
    /// </summary>
    public float CameraRange { get; set; } = 600f * MathF.Sqrt(2f);

    /// <summary>
    /// The far path (it does not follow the ground). While a cut-scene is on, the camera cuts between shots and a car far
    /// from this one is shown by the next (Velocity's bus, seen from afar): it keeps its height over the ground it
    /// drives over, so it neither sinks into a hill nor jumps up when the camera comes near.
    /// </summary>
    private void FarFrame(Vehicle v, float frameDelta, bool player)
    {
        bool scene = Scene is { Active: true };
        double was = scene ? Ground.BareHeight(v.Pos.X, v.Pos.Z) : 0;
        Step.CheapFrame(v, frameDelta, player);
        if (scene) v.Pos = new I76.Sim.Math.Double3(v.Pos.X, v.Pos.Y + Ground.BareHeight(v.Pos.X, v.Pos.Z) - was, v.Pos.Z);
    }

    /// <summary>
    /// Distance class 2 at 25, measured from the player's car (and, by <see cref="Far"/>, every human's) in place
    /// of the camera: farther than 25 + the camera term. The same as the original's whenever its camera is on or near
    /// the player's car (the chase and cockpit views).
    /// </summary>
    public bool IsFar(Vehicle v)
    {
        float r = 25f + CameraRange;
        if (Player is { } p && Near(p.Pos.ToVector3())) return false;
        foreach (var h in Humans) if (Near(h.Pos.ToVector3())) return false;
        // a script camera far from the humans (a cut-scene's): the cars it shows are near, as they are to the original's
        // camera -- on the far path a car does not follow the ground, and a bus on a hill sank through it
        if (Scene is { Active: true } scene && Near(scene.Eye)) return false;
        // with nobody to measure from: a network session takes every car far, a game without a player's car none
        return Far == FarPolicy.Humans || Player is not null;
        bool Near(Vector3 at) => (v.Pos.ToVector3() - at).LengthSquared() <= r * r;
    }

    /// <summary>For two COLP boxes: XZ boxes, swept spheres, then the mesh test both ways.</summary>
    private void PairContacts(Vehicle a, Vehicle b, float dt, Vehicle? oneSided = null)
    {
        var (ca, ra) = Sphere(a);
        var (cb, rb) = Sphere(b);
        float minAx = MathF.Min(ca.X, ca.X + a.Vel.X * dt) - ra, maxAx = MathF.Max(ca.X, ca.X + a.Vel.X * dt) + ra;
        float minAz = MathF.Min(ca.Z, ca.Z + a.Vel.Z * dt) - ra, maxAz = MathF.Max(ca.Z, ca.Z + a.Vel.Z * dt) + ra;
        float minBx = MathF.Min(cb.X, cb.X + b.Vel.X * dt) - rb, maxBx = MathF.Max(cb.X, cb.X + b.Vel.X * dt) + rb;
        float minBz = MathF.Min(cb.Z, cb.Z + b.Vel.Z * dt) - rb, maxBz = MathF.Max(cb.Z, cb.Z + b.Vel.Z * dt) + rb;
        if (maxAx < minBx || maxBx < minAx || maxAz < minBz || maxBz < minAz) return;
        // Both still -> no contact
        if (a.Vel.LengthSquared() < 3e-8f && b.Vel.LengthSquared() < 3e-8f) return;
        var dv = a.Vel - b.Vel;
        var d0 = cb - ca;
        float vv = dv.LengthSquared();
        float t = vv < 1e-4f ? dt : Vector3.Dot(dv, d0) / vv;
        if (t > dt) t = dt;
        var d = t > 0 ? d0 - dv * t : d0;
        if (d.LengthSquared() > (ra + rb) * (ra + rb)) return;
        if (BoxVsBox(a, b, dt) is not { } hit)
        {
            if (BoxVsBox(b, a, dt) is not { } hit2) return;
            if (oneSided is not null) { if (oneSided == a) a.Rec = hit2.Q; else b.Rec = hit2.P; return; }
            b.Rec = hit2.P; a.Rec = hit2.Q;
            CarContact?.Invoke(b, a, b.Rec); CarContact?.Invoke(a, b, a.Rec);
            return;
        }
        if (oneSided is not null) { if (oneSided == a) a.Rec = hit.P; else b.Rec = hit.Q; return; }
        a.Rec = hit.P; b.Rec = hit.Q;
        CarContact?.Invoke(a, b, a.Rec); CarContact?.Invoke(b, a, b.Rec);
    }

    private static (Vector3 C, float R) Sphere(Vehicle v)
    {
        var mid = new Vector3((v.BoxX[0] + v.BoxX[3]) / 2, (v.BoxY[1] + v.BoxY[3]) / 2, (v.BoxZ[0] + v.BoxZ[3]) / 2);
        float r = 0;
        foreach (var x in new[] { v.BoxX[0], v.BoxX[3] })
            foreach (var y in new[] { v.BoxY[1], v.BoxY[3] })
                foreach (var z in new[] { v.BoxZ[0], v.BoxZ[3] })
                    r = MathF.Max(r, Vector3.Distance(mid, new Vector3(x, y, z)));
        return (v.Pos.ToVector3() + v.Rot.ToWorld(mid), r + 0.1f);
    }

    /// <summary>
    /// Eight mid-height sample points of P (corners and
    /// edge midpoints), each cast in Q's frame with P's point velocity minus
    /// Q's velocity against Q's box (x0, y1, z0)–(x3, y3, z3); the earliest wins.
    /// </summary>
    private static (Contact P, Contact Q)? BoxVsBox(Vehicle p, Vehicle q, float dt)
    {
        float ym = (p.BoxY[1] + p.BoxY[3]) / 2, xm = (p.BoxX[0] + p.BoxX[3]) / 2, zm = (p.BoxZ[0] + p.BoxZ[3]) / 2;
        float x0 = p.BoxX[0], x3 = p.BoxX[3], z0 = p.BoxZ[0], z3 = p.BoxZ[3];
        Vector3[] pts = [new(x0, ym, z0), new(xm, ym, z0), new(x3, ym, z0), new(x3, ym, zm),
                         new(x3, ym, z3), new(xm, ym, z3), new(x0, ym, z3), new(x0, ym, zm)];
        var qmin = new Vector3(q.BoxX[0], q.BoxY[1], q.BoxZ[0]);
        var qmax = new Vector3(q.BoxX[3], q.BoxY[3], q.BoxZ[3]);
        var pPos = p.Pos.ToVector3(); var qPos = q.Pos.ToVector3();
        float best = float.MaxValue;
        (Contact, Contact)? o = null;
        foreach (var lp in pts)
        {
            var pw = pPos + p.Rot.ToWorld(lp);
            var pvw = p.Vel + p.Rot.ToWorld(Vector3.Cross(lp, p.AngVel));
            var ql = q.Rot.ToLocal(pw - qPos);
            var rv = q.Rot.ToLocal(pvw - q.Vel);
            if (Slab(ql, rv, qmin, qmax, dt) is not { } hit || hit.T >= best) continue;
            best = hit.T;
            var n = q.Rot.ToWorld(hit.N);
            o = (new Contact { Hit = true, Other = q, OtherMass = q.P.Mass, OtherVel = q.Vel, T = hit.T, C = lp, RelVel = q.Vel - pvw, N = n },
                 new Contact { Hit = true, Other = p, OtherMass = p.P.Mass, OtherVel = p.Vel, T = hit.T, C = ql + rv * hit.T, RelVel = pvw - q.Vel, N = -n });
        }
        return o;
    }

    /// <summary>A slab ray against an AABB, axes x, z, y; inside at t = 0 is not a hit.</summary>
    private static (float T, Vector3 N)? Slab(Vector3 p, Vector3 v, Vector3 min, Vector3 max, float dt)
    {
        float enter = float.MinValue, exit = float.MaxValue;
        Vector3 n = default;
        foreach (int axis in new[] { 0, 2, 1 })
        {
            float pp = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
            float vv = axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(vv) < 1e-8f)
            {
                if (pp < lo || pp > hi) return null;
                continue;
            }
            float t1 = (lo - pp) / vv, t2 = (hi - pp) / vv;
            float tn = MathF.Min(t1, t2), tf = MathF.Max(t1, t2);
            if (tn > enter)
            {
                enter = tn;
                n = axis switch { 0 => new(-MathF.Sign(vv), 0, 0), 1 => new(0, -MathF.Sign(vv), 0), _ => new(0, 0, -MathF.Sign(vv)) };
            }
            exit = MathF.Min(exit, tf);
        }
        if (enter > exit || exit < 0 || enter < 0 || enter > dt) return null;
        return (enter, n);
    }
}

/// <summary>What <see cref="SimWorld.IsFar"/> measures from.</summary>
public enum FarPolicy
{
    /// <summary>The player's car (the original measures from the camera): single player.</summary>
    Player,
    /// <summary>Every human's car: near if within the camera term of any of them (a network session).</summary>
    Humans,
}
