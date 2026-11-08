using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Collision;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>The shared transition predicates and the escape states.</summary>
public sealed partial class Ptable
{
    // ---- transition predicates ----

    /// <summary>
    /// The attacker-slot check: full as soon as the count of live others holding a slot on the target reaches its cap,
    /// free otherwise, so a cap of 0 with no attacker is free (a cap of 0 or 1 both allow one attacker).
    /// </summary>
    private bool SlotAvailable(Entity e, Entity? t)
    {
        if (!IsVehicle(t)) return true;
        int n = 0;
        foreach (var o in host.Entities)
        {
            if (o.Vehicle is null || o == t || o == e || o.Dead || o.AttackCommit != t) continue;
            if (++n >= t!.MaxAttackers) return false;
        }
        return true;
    }

    /// <summary>Tactic on deck: the target is the player (in co-op any human) and its attacker roster is full.</summary>
    private bool OnDeckNeeded(Entity e, Entity? t) => host.IsHumanEntity(t) && !SlotAvailable(e, t);

    /// <summary>
    /// The player's radar jammer is running (single player only). In co-op each human's:
    /// a jammer hides its own car, and blinds the AI at anything that is not a human, as the player's blinds it at all
    /// (<paramref name="t"/> null: any human's).
    /// </summary>
    private bool JammerActive(Entity? t = null)
    {
        if (host.Combat?.NetSession == true) return false;
        if (host.Coop is not { } c) return Specials.Jamming(host.Player?.Vehicle, World.Time);
        foreach (var s in c.Seats)
            if (s.Present && Specials.Jamming(s.Car, World.Time) && (t is null || t == s.Entity || !host.IsHumanEntity(t))) return true;
        return false;
    }

    /// <summary>Handle los: while the player jams, no line of sight on the target for 5 s.</summary>
    private bool LosLost(Entity e, Entity? t)
    {
        if (!JammerActive(t)) { e.LosSeen = Now; return false; }
        if (LosBudget() && t is not null && LineOfSight(e, t)) e.LosSeen = Now;
        return Now > e.LosSeen + 5f;
    }

    /// <summary>Actor + 1.5 up to target + 1.5 up (both aim points), against the terrain (objects not tested here).</summary>
    private bool LineOfSight(Entity a, Entity b)
    {
        var p = Pos(a) + new Vector3(0, 1.5f, 0); var q = AimPointOf(b) + new Vector3(0, 1.5f, 0);
        int n = System.Math.Max(2, (int)(Vector3.Distance(p, q) / 5f));
        for (int i = 1; i < n; i++)
        {
            var x = Vector3.Lerp(p, q, i / (float)n);
            if (x.Y < World.Ground.BareHeight(x.X, x.Z)) return false;
        }
        return true;
    }

    /// <summary>The target-too-far check → handle too far search.</summary>
    private static bool TooFar(Entity e, Entity? t)
    {
        if (t is null) return false;
        if (e.IsHeli) return Dist(e, t) > 720f;
        if (!IsVehicle(t)) return Dist(e, t) > 200f;
        if (MathF.Abs(t.Position.Y - Pos(e).Y) > 30f) return true;
        return Spatial(e, t) > 180f;
    }

    /// <summary>The imminent-collision check → stop_sliding_backward: in forward gear, rolling backward.</summary>
    private static bool SlidingBackward(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        return v.Gear > 0 && Vector3.Dot(v.Rot.Forward, v.Vel) + 0.05f < 0;
    }

    /// <summary>A car that has asked to go forward this long with the ring low and nothing in its way is boxed in (#42).</summary>
    public const float StallLimit = 2f;

    /// <summary>
    /// The boxed-in check → back_away: "forward clearance" under 0.1 four FSM ticks running. The clearance
    /// is the forward-clearance measure, which measures no distance: it is the traction limit on the
    /// throttle, and below 1 u/s a strong engine's (the force is not divided by the speed there) is under 0.1 on
    /// open ground. So a stopped car is "boxed in" four ticks after it moves off, backs away, stops, moves off and
    /// backs away again for good (measured in the original: T15's t15as01 does it ten times running, #42). Here
    /// the ring must also be confirmed: the way ahead blocked (as avoid clsn → back_away tests), or
    /// the car asking to go (throttle above 0) without getting going for <see cref="StallLimit"/> s
    /// (docs/deviations.md). A car holding still on the brake is not boxed in.
    /// </summary>
    private bool BoxedIn(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        e.ClearanceRing[((host.FsmTick % 4) + 4) % 4] = Traction(v, 1f);
        if (!e.ClearanceRing.All(c => c < 0.1f) || v.Throttle <= 0) { e.StallTime = 0; return false; }
        if (BlockedInGear(e)) return true;
        e.StallTime += Dt;
        return e.StallTime >= StallLimit;
    }

    /// <summary>The three avoid clsn triggers of the table's transitions.</summary>
    private enum AvoidKind { KeepSteer, Vehicles, Terrain }

    /// <summary>
    /// The avoid clsn triggers: <see cref="AvoidKind.KeepSteer"/> (vehicles, never the target, steer
    /// kept), <see cref="AvoidKind.Vehicles"/> (vehicles), <see cref="AvoidKind.Terrain"/> (vehicles, and terrain unless
    /// on a road surface or heading for a bridge point, horn on a vehicle one time in four). All off while avoid is.
    /// </summary>
    private bool AvoidTrigger(Entity e, Entity? t, AvoidKind which)
    {
        if (!e.Avoid) return false;
        var v = e.Vehicle!;
        bool skip = false, vehOnly = true;
        if (which == AvoidKind.Terrain)
        {
            vehOnly = false;
            if (Now >= e.PlanStamp + 1f)
            {
                int i = e.WaypointIndex;
                if (i < e.WaypointBridge.Count && e.WaypointBridge[i]) skip = true;
                else skip = v.Surface switch
                {
                    2 or 3 or 4 or 7 => true,
                    6 => i >= 1 && i < e.Waypoints.Count && MathF.Abs(Delta(Hdg(e), Heading(e.Waypoints[i] - e.Waypoints[i - 1]))) < 1f,
                    _ => false,
                };
            }
        }
        bool hit = Candidate(e, v.Steer, v.Throttle, out var obstacle, out float outSteer, out e.EscapeTimer, skip, vehOnly, out e.ImpactTime);
        e.Obstacle = obstacle;
        if (which == AvoidKind.KeepSteer) { e.AvoidSteer = outSteer; if (obstacle == t) hit = false; }
        if (which == AvoidKind.Terrain && hit && IsVehicle(obstacle) && Rand() % 4 == 0) Horn(e);
        return hit;
    }

    /// <summary>
    /// The honk: the car's engsnd.dat horn file at the car with no voice settings, so its own GAS0 mode. A horn of mode 0 is a
    /// positional one-shot, which the view plays from <see cref="VehicleStep.Honks"/>. The police car's aps2.wav is mode 1: it
    /// loops, and only setSiren 0 or the car's wreck stops it, so the honk is the siren turned on (<see cref="Entity.Siren"/>).
    /// </summary>
    internal void Horn(Entity e)
    {
        if (e.Vehicle is not { } v) return;
        if (v.HornLoops) e.Siren = true;
        else World.Step.Honks.Add(v);
    }

    /// <summary>Avoid clsn → back_away: sliding backward, or blocked in the gear direction.</summary>
    private bool AvoidToBackAway(Entity e, Entity? t) => !e.IsHeli && (SlidingBackward(e, t) || BlockedInGear(e));

    /// <summary>How long back_away may get nowhere (never <see cref="BackStuckDistance"/> from its start) before it drives forward instead (#42).</summary>
    public const float BackStuckTime = 1.5f, BackStuckDistance = 1f;

    /// <summary>
    /// Back_away → just do it: the way back is blocked and the ground ahead does not climb. Its probe is
    /// terrain steps over 3.4 only, so a car wedged against a bank or an object behind it reversed into it for
    /// the whole 4 s, and again each time the way ahead sent it back (#42). Here back_away that has not once been
    /// <see cref="BackStuckDistance"/> from where it started in its first <see cref="BackStuckTime"/> s goes to just
    /// do it as well (docs/deviations.md).
    /// </summary>
    private bool BackAwayToJustDoIt(Entity e, Entity? _)
    {
        if (Now >= e.EscapeTimer - 4f + BackStuckTime && e.BackFarthest < BackStuckDistance) return true;
        if (!ProbeShort(e)) return false;
        var p = Pos(e); var f = e.Rot.Forward;
        float h0 = World.Ground.BareHeight(p.X, p.Z), h5 = World.Ground.BareHeight(p.X + 5 * f.X, p.Z + 5 * f.Z);
        return h5 - h0 < 0.1f;
    }

    // ---- the speculative collision test ----

    /// <summary>The ground-plane direction of forward × gear, unit.</summary>
    private static Vector3 Travel(Vector3 forward, float gear)
    {
        var d = new Vector3(forward.X * gear, 0, forward.Z * gear);
        float l = d.Length();
        return l > 1e-6f ? d / l : Vector3.UnitZ;
    }

    /// <summary>The look-ahead box: the position to 50 ahead along forward × gear, padded by 3.</summary>
    private static (float MinX, float MinZ, float MaxX, float MaxZ) LookBox(Entity e)
    {
        var p = Pos(e);
        var q = p + Travel(e.Rot.Forward, e.Vehicle?.Gear ?? 1f) * 50f;
        return (MathF.Min(p.X, q.X) - 3, MathF.Min(p.Z, q.Z) - 3, MathF.Max(p.X, q.X) + 3, MathF.Max(p.Z, q.Z) + 3);
    }

    private static bool Overlap((float MinX, float MinZ, float MaxX, float MaxZ) a, (float MinX, float MinZ, float MaxX, float MaxZ) b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinZ <= b.MaxZ && b.MinZ <= a.MaxZ;

    /// <summary>
    /// The back-away look-ahead: would these inputs hit
    /// something? One frame ahead (the step moves the car by the frame
    /// delta through the force model, or the far model when far), then
    /// the terrain probe, the other cars, the scenery sweep, and the
    /// skill-gated projectile test (its rand() is drawn; the test itself is
    /// not ported).
    /// </summary>
    private bool Candidate(Entity e, float steer, float throttle, out Entity? obstacle, out float outSteer, out float outVal, bool skipStatic, bool vehOnly, out float tImpact)
    {
        var v = e.Vehicle!;
        obstacle = null; outSteer = 0; outVal = 0.5f; tImpact = float.MaxValue;
        // With the candidate's steer and throttle in the steering and throttle commands
        var (p, vel, rot) = World.Step.Predict(v, Dt, World.IsFar(v), World.IsHuman(v), steer, throttle);
        var dir = Travel(rot.Forward, v.Gear);
        var right = new Vector3(dir.Z, 0, -dir.X);
        float vl = vel.Length();
        var g = World.Ground;

        if (!skipStatic)
        {
            float max = MathF.Max(20f, 1.5f * vl);
            float pl = g.BareHeight(p.X - right.X * 2.5f, p.Z - right.Z * 2.5f), pr = g.BareHeight(p.X + right.X * 2.5f, p.Z + right.Z * 2.5f);
            // the no-go cell is tested on the first track only, 2.5 to the car's left in either gear (right is along travel)
            float nl = v.Gear < 0 ? -2.5f : 2.5f;
            for (float d = 2.5f; d <= max; d += 2.5f)
            {
                var c = p + dir * d;
                float hl = g.BareHeight(c.X - right.X * 2.5f, c.Z - right.Z * 2.5f), hr = g.BareHeight(c.X + right.X * 2.5f, c.Z + right.Z * 2.5f);
                bool hit = g.Impassable(c.X - right.X * nl, c.Z - right.Z * nl) || hl - pl is < -1f or > 1.5f || hr - pr is < -1f or > 1.5f || MathF.Abs(hl - hr) > 1.5f;
                if (hit)
                {
                    tImpact = vl < 1e-4f ? float.MaxValue : d / vl;
                    outVal = MathF.Min(d, 0.5f);
                    return true;
                }
                pl = hl; pr = hr;
            }
        }

        var box = LookBox(e);
        foreach (var o in host.Entities)
        {
            if (o == e || o.Vehicle is not { } ov || o.Removed || ov.Wreck) continue;
            if (!Overlap(box, LookBox(o))) continue;
            var dp = o.Position - p;
            if (dp.X * dp.X + dp.Z * dp.Z > 100f * 100f) continue;
            // A gun emplacement (type 8) counts only with the static checks on, and the 60 degree
            // forward cone applies unless the caller is a vehicles-only test (candidate argument 8 = 1). So the
            // on-road path test ignores a car behind or beside, and the other two see all.
            if (skipStatic && ov.Emplacement is not null) continue;
            if (!vehOnly)
            {
                float dv = Vector3.Dot(dp, vel);
                if (dv < 0 || dv * dv < 0.25f * dp.LengthSquared() * vel.LengthSquared()) continue;
            }
            var rv = ov.Vel - vel;
            float rr = rv.LengthSquared();
            float t = rr < 0.01f ? 1e7f : -Vector3.Dot(dp, rv) / rr;
            tImpact = t;
            t = System.Math.Clamp(t, 0f, 3f);
            var me = p + vel * t;
            var it = o.Position + ov.Vel * t;
            foreach (var h in Hull(ov))
            {
                var w = it + ov.Rot.ToWorld(h) - me;
                float lx = Vector3.Dot(w, right), lz = Vector3.Dot(w, dir);
                if (MathF.Abs(lz) < 2.5f && MathF.Abs(lx) < 1.3f)
                {
                    outSteer = 1f;
                    outVal = ov.Speed >= 5f ? 0.3f : MathF.Min(t, 0.5f);
                    obstacle = o;
                    return true;
                }
            }
        }

        if (!skipStatic && g is IHasStatics hs)
        {
            var sweep = dir * 50f;
            foreach (var so in hs.World.Objects)
            {
                if (!so.Solid || so.Type == 4 || so.Polys.Count + so.Boxes.Count == 0) continue;
                if (so.Centre.X + so.Radius < box.MinX || so.Centre.X - so.Radius > box.MaxX || so.Centre.Z + so.Radius < box.MinZ || so.Centre.Z - so.Radius > box.MaxZ) continue;
                foreach (float side in new[] { -1f, 0f, 1f })
                {
                    var start = p + right * side + new Vector3(0, 1f, 0);
                    if (WallHit(so, start, sweep) is not { } ht) continue;
                    outSteer = 1f; outVal = 1.25f;
                    tImpact = vl < 1e-4f ? float.MaxValue : ht * 50f / vl;
                    return true;
                }
            }
        }

        if (Rand() % 1000 > 850 + 150 * (1 - e.Ai.Skill) && IncomingRound(e, ref tImpact, out float tr))
        {
            outSteer = 1f; outVal = tr + 0.5f;
            return true;
        }
        outSteer = 0; outVal = 0.5f;
        return false;
    }

    private static readonly HashSet<uint> DodgedKinds = [0xc, 0xe, 0xf, 0x10, 0x11, 0x13];

    /// <summary>
    /// Where along p + v·t (t in 0..1) the sweep first meets a wall of this scenery: a face that is not
    /// floor-like (n.y ≤ 0.4) of its polygon mesh or of its class-0x2000 node boxes. The
    /// original's sweeps go through the whole collider, so a box-only one (a
    /// wreck, a type 2/7/10 object) stops them as a mesh does (#60).
    /// </summary>
    private static float? WallHit(StaticObject so, Vector3 p, Vector3 v)
    {
        float best = float.MaxValue;
        var (t, poly) = StaticWorld.RayMesh(so.Polys, p, v, 1f);
        if (poly is not null && poly.N.Y <= 0.4f) best = t;
        if (StaticWorld.RayBoxes(so.Boxes, p, v, 1f) is { } b && b.N.Y <= 0.4f && b.T < best) best = b.T;
        return best == float.MaxValue ? null : best;
    }

    /// <summary>
    /// An object of a dodged kind (the dropped slicks, mines and the like) within 200 on both
    /// axes that the car, from where it is at its present velocity, passes within 5 of in 0.001..3 s. The
    /// object's velocity is the global, which nothing writes (zero in .data), so every object
    /// counts as standing still; its own drops count too. The time of the last object tested goes to
    /// tImpact, hit or not.
    /// </summary>
    private bool IncomingRound(Entity e, ref float tImpact, out float time)
    {
        time = 0;
        if (host.Combat is not { } c || e.Vehicle is not { } v) return false;
        var p = e.Position; var vel = v.Vel;
        float vv = vel.LengthSquared();
        foreach (var r in c.Rounds)
        {
            if (!DodgedKinds.Contains(r.Kind)) continue;
            var d = p - r.Pos;
            if (MathF.Abs(d.X) > 200f || MathF.Abs(d.Z) > 200f) continue;
            float t = vv < 0.01f ? 1e7f : -Vector3.Dot(vel, d) / vv;
            tImpact = t;
            if (t < 0.001f || t > 3f || (d + vel * t).Length() >= 5f) continue;
            time = t;
            return true;
        }
        return false;
    }

    /// <summary>Ten hull points, here the box corners, edge midpoints and the nose and tail at mid height.</summary>
    private static IEnumerable<Vector3> Hull(Vehicle v)
    {
        float y = (v.BoxY[1] + v.BoxY[3]) / 2, x0 = v.BoxX[0], x3 = v.BoxX[3], z0 = v.BoxZ[0], z3 = v.BoxZ[3];
        float xm = (x0 + x3) / 2, zm = (z0 + z3) / 2;
        return [new(x0, y, z0), new(x3, y, z0), new(x3, y, z3), new(x0, y, z3), new(xm, y, z0), new(xm, y, z3),
                new(x0, y, zm), new(x3, y, zm), new(xm, y, (z0 + zm) / 2), new(xm, y, (zm + z3) / 2)];
    }

    /// <summary>Mode 1: from the position along forward × gear at 2, 4.5, 7; impassable ahead in forward gear, or a height step outside (−3, 3.4).</summary>
    private bool ProbeShort(Entity e)
    {
        var v = e.Vehicle!; var p = Pos(e); var g = World.Ground;
        var dir = Travel(v.Rot.Forward, v.Gear);
        float prev = g.BareHeight(p.X, p.Z);
        foreach (float d in new[] { 2f, 4.5f, 7f })
        {
            var c = p + dir * d;
            if (v.Gear > 0 && g.Impassable(c.X, c.Z)) return true;
            float h = g.BareHeight(c.X, c.Z);
            if (h - prev < -3f || h - prev > 3.4f) return true;
            prev = h;
        }
        return false;
    }

    /// <summary>
    /// Blocked in the gear direction: the short terrain probe, then the car's origin swept 7 along its forward row in
    /// the gear direction against the other cars' boxes (<see cref="OriginSweepHitsBox"/>), then the scenery's meshes
    /// and node boxes along three lines.
    /// </summary>
    private bool BlockedInGear(Entity e)
    {
        if (ProbeShort(e)) return true;
        var v = e.Vehicle!; var p = Pos(e);
        var sweep = v.Rot.Forward * (7f * v.Gear);
        foreach (var o in host.Entities)
        {
            if (o == e || o.Vehicle is not { } ov || o.Removed) continue;
            if (OriginSweepHitsBox(p, sweep, o.Position, ov)) return true;
        }
        var dir = Travel(v.Rot.Forward, v.Gear);
        var right = new Vector3(dir.Z, 0, -dir.X);
        float reach = 7f + MathF.Max(v.BoxZ[3], -v.BoxZ[0]);
        if (World.Ground is IHasStatics hs)
            foreach (var so in hs.World.Objects)
            {
                if (!so.Solid || so.Polys.Count + so.Boxes.Count == 0 || Vector3.DistanceSquared(so.Centre, p) > (so.Radius + reach) * (so.Radius + reach)) continue;
                foreach (float side in new[] { -1f, 0f, 1f })
                    if (WallHit(so, p + right * side + new Vector3(0, 1f, 0), dir * reach) is not null) return true;
            }
        return false;
    }

    /// <summary>
    /// The probe's test of another car: the origin <paramref name="p"/> (on the ground under the car) swept by
    /// <paramref name="sweep"/> against the car's collision box in that car's frame, x and z to the outer planes and y
    /// from the lower inner plane to the upper outer (the planes the original's box test reads), with no pad. A hit is
    /// an entry into the box within the sweep; a start already inside is none. Every car's box begins above its origin
    /// (0.23 to 0.35 up), so a car ahead on the same ground is passed under and never sends a car into back_away; one
    /// the car stands higher than, by the box's height, does. Measured in the original (T02's opening, retail 1.0 on an
    /// emulated Pentium, n = 2): the script-driven player's car, put 5 behind Taurus's at 20 u/s, keeps driving.
    /// </summary>
    public static bool OriginSweepHitsBox(Vector3 p, Vector3 sweep, Vector3 origin, Vehicle o)
    {
        var lp = o.Rot.ToLocal(p - origin); var lv = o.Rot.ToLocal(sweep);
        float t0 = float.NegativeInfinity, t1 = 1f;
        return Slab(lp.X, lv.X, o.BoxX[0], o.BoxX[3], ref t0, ref t1)
            && Slab(lp.Y, lv.Y, o.BoxY[1], o.BoxY[3], ref t0, ref t1)
            && Slab(lp.Z, lv.Z, o.BoxZ[0], o.BoxZ[3], ref t0, ref t1)
            && t0 >= 0f;
    }

    /// <summary>One axis of the slab test: narrows [t0, t1] to the sweep's time inside [min, max]; false when nothing is left, or the sweep is already past it.</summary>
    private static bool Slab(float x, float dx, float min, float max, ref float t0, ref float t1)
    {
        if (MathF.Abs(dx) < 1e-6f) return x >= min && x <= max;
        float a = (min - x) / dx, b = (max - x) / dx;
        if (a > b) (a, b) = (b, a);
        if (b < 0f) return false;
        t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
        return t0 <= t1;
    }

    /// <summary>
    /// The input search: candidate steer/throttle pairs through the collision test; the first clear one wins.
    /// Writes the winner; returns the score. When none is clear, as the original's chooser: with the wheel centred
    /// (+0.25, −0.25, +0.5, −0.5 ... always the positive side first) the highest score, a tie going to the later
    /// candidate; in the throttle sweep the first throttle (every block reads alike there); in the off-centre steer
    /// sweep the far end of the side searched first, with the last blocked candidate's score.
    /// </summary>
    private float InputSearch(Entity e, Entity? obstacle)
    {
        var v = e.Vehicle!;
        float L = SteerLimit(e), s0 = v.Steer, speed = v.Speed, cruise = Cruise(e);
        bool inc = Now >= e.EscapeTimer ? s0 < 0 : s0 > 0;
        float bs = s0, bt = v.Throttle;
        bool Hit(float s, float t, out float val) => Candidate(e, s, t, out _, out _, out val, false, true, out _);
        if (MathF.Abs(s0) < 0.05f)
        {
            Throttle(e, 0.1f * cruise + 0.9f * speed);
            float t = v.Throttle, best = float.MinValue;
            for (float a = 0.25f; a <= L + 1e-4f; a += 0.25f)
                foreach (float s in new[] { MathF.Min(a, L), -MathF.Min(a, L) })
                {
                    if (!Hit(s, t, out float val)) { v.Steer = s; v.Throttle = t; return val; }
                    if (val >= best) { best = val; bs = s; }
                }
            v.Steer = bs; v.Throttle = t;
            return best == float.MinValue ? 0.5f : best;
        }
        bool same = obstacle?.Vehicle is { } ov ? Vector3.Dot(ov.Rot.Forward, v.Rot.Forward) > 0.7f : speed > 15f;
        var throttles = new List<float>();
        bool sweep = !(Motion(obstacle).Speed <= 2f || same);
        if (!sweep)
        {
            Throttle(e, speed <= cruise ? 0.9f * cruise + 0.1f * speed : 0.1f * cruise + 0.9f * speed);
            throttles.Add(v.Throttle);
        }
        else
        {
            float top = inc ? 1f : Traction(v, 1f);
            for (float t = 0; t <= top + 1e-4f; t += 0.2f) throttles.Add(t);
            float bottom = Traction(v, -1f);
            for (float t = -0.2f; t >= bottom - 1e-4f; t -= 0.2f) throttles.Add(t);
        }
        float score = 0.5f;
        bool kept = false;
        foreach (float t in throttles)
        {
            if (!Hit(s0, t, out float val)) { v.Steer = s0; v.Throttle = t; return val; }
            if (!kept) { kept = true; bt = t; score = val; }
        }
        var first = new List<float>(); var second = new List<float>();
        for (float s = s0 + 0.25f; s <= L + 1e-4f; s += 0.25f) first.Add(MathF.Min(s, L));
        for (float s = s0 - 0.25f; s >= -L - 1e-4f; s -= 0.25f) second.Add(MathF.Max(s, -L));
        foreach (float s in inc ? first.Concat(second) : second.Concat(first))
        {
            if (!Hit(s, bt, out float val)) { v.Steer = s; v.Throttle = bt; return val; }
            score = val;
        }
        v.Steer = inc ? L : -L; v.Throttle = bt;
        return score;
    }

    // ---- 16 stop_sliding_backward ----

    /// <summary>Full opposite lock.</summary>
    private void StopSlidingEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!; float L = SteerLimit(e);
        v.Handbrake = 0;
        v.Steer = v.Steer >= 0 ? -L : L;
    }

    /// <summary>Brake, hold the lock.</summary>
    private void StopSlidingTick(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        Brake(e);
        float L = SteerLimit(e);
        v.Steer = v.Steer <= 0 ? -L : L;
    }

    // ---- 17 avoid clsn ----

    /// <summary>Give up the slot, plan the way round.</summary>
    private void AvoidEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        v.Handbrake = 0; v.DriveState = 3; v.Gear = 1f;
        e.AttackCommit = null;
        AvoidPlan(e, true);
        e.AvoidSpeed = v.Speed; e.AvoidStart = Now;
    }

    /// <summary>Every other frame, re-test the current inputs; clear → straight on at the commanded speed.</summary>
    private void AvoidTick(Entity e, Entity? t)
    {
        var v = e.Vehicle!;
        if (e.AvoidSkip) { e.AvoidSkip = false; return; }
        bool hit = Candidate(e, v.Steer, v.Throttle, out var obstacle, out _, out _, false, true, out e.ImpactTime);
        e.Obstacle = obstacle;
        if (!hit) { v.Steer = 0; Throttle(e, e.OrderSpeed); }
        else AvoidPlan(e, false);
    }

    /// <summary>The avoid plan: time to spare → the input search; imminent → full opposite lock and the throttle by the obstacle's bearing.</summary>
    private void AvoidPlan(Entity e, bool enter)
    {
        var v = e.Vehicle!;
        if (e.ImpactTime >= 1f)
        {
            float best = InputSearch(e, e.Obstacle);
            e.EscapeTimer = Now + 2f * (1f - e.Ai.Skill) + best;
            if (enter) e.AvoidSkip = true;
            else { e.AvoidSkip = false; Throttle(e, Cruise(e)); }
            return;
        }
        float L = SteerLimit(e);
        v.Steer = v.Steer >= 0 ? -0.9999f * L : 0.9999f * L;
        if (e.Obstacle is { Vehicle: not null } ob)
        {
            if (v.Speed < 10f) FullThrottle(e);
            else if (v.Speed <= 35f)
            {
                if (Octant(Hdg(e), Brg(e, ob)) is 0 or 1 or 7) Throttle(e, Cruise(e));
                FullThrottle(e);
            }
            else Throttle(e, Cruise(e));
        }
        e.EscapeTimer = Now + 2f * (1f - e.Ai.Skill) + 0.5f;
        e.AvoidSkip = true;
    }

    /// <summary>An avoidance that lasted over 3 s ends the planned path (termFllw re-plans).</summary>
    private void AvoidMatch(Entity e, Entity? _) { if (Now > e.AvoidStart + 3f) e.ReachedEnd = true; }

    // ---- 18 back_away ----

    /// <summary>Lock away from the wheel's side, brake or creep, flip the gear.</summary>
    private void BackOff(Entity e)
    {
        var v = e.Vehicle!; float L = SteerLimit(e);
        v.Steer = 0.95f * L * (v.Gear >= 0 ? (v.Steer >= 0 ? -1f : 1f) : (v.Steer >= 0 ? 1f : -1f));
        if (v.Speed <= 5f) Throttle(e, Cruise(e)); else Brake(e);
        FlipGear(v);
        e.EscapeTimer = Now + 2f;
        e.BackHeading = Hdg(e);
    }

    private void BackAwayEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        v.Handbrake = 0;
        e.BackSteer = v.Steer;
        e.BackHeight = Pos(e).Y;
        e.BackStart = Pos(e); e.BackFarthest = 0;
        BackOff(e);
        e.EscapeTimer = Now + 4f;
        e.BackHeading = Hdg(e);
    }

    /// <summary>Stop rolling the old way, then floor it the new way, then hold a lock at cruise.</summary>
    private void BackAwayTick(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        var at = Pos(e);
        e.BackFarthest = MathF.Max(e.BackFarthest, new Vector2(at.X - e.BackStart.X, at.Z - e.BackStart.Z).Length());
        float vf = Vector3.Dot(v.Rot.Forward, v.Vel), dh = MathF.Abs(Delta(Hdg(e), e.BackHeading));
        if (dh >= 1.5f) Brake(e);
        else if (vf > 0.1f) { Brake(e); v.Steer = 0; }
        else if (vf > -8f) { FloorIt(e); v.Steer = 0; }
        else
        {
            v.Steer = 0.95f * MathF.Abs(SteerLimit(e)) * (e.BackSteer <= 0 ? 1f : -1f);
            Throttle(e, Cruise(e));
        }
    }

    /// <summary>termBK: climbed or dropped 3, or turned 1.5 rad and stopped.</summary>
    private bool TermBackAway(Entity e, Entity? _) =>
        MathF.Abs(e.BackHeight - Pos(e).Y) > 3f || (MathF.Abs(Delta(Hdg(e), e.BackHeading)) >= 1.5f && e.Vehicle!.Speed < 1f);

    /// <summary>Reset the boxed-in ring, brake, gear back.</summary>
    private void BackAwayMatch(Entity e, Entity? _)
    {
        Array.Fill(e.ClearanceRing, 1f);
        e.StallTime = 0;
        Brake(e);
        FlipGear(e.Vehicle!);
    }

    // ---- 19 just do it ----

    /// <summary>The other gear, wheel straight, 1.5 s.</summary>
    private void JustDoItEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        FlipGear(v);
        v.Steer = 0;
        e.EscapeTimer = Now + 1.5f;
    }

    /// <summary>Floor it.</summary>
    private void JustDoItTick(Entity e, Entity? _) => FloorIt(e);

    // ---- 20/21 handle too far ----

    /// <summary>A one-point path to where the target is now (its transform's origin, not its aim point), at 35.</summary>
    private void TooFarEnter(Entity e, Entity? t)
    {
        var v = e.Vehicle!;
        v.Handbrake = 0;
        e.SnapshotPath[0] = t?.Position ?? Pos(e);
        e.Path = e.SnapshotPath;
        e.OrderSpeed = 35f;
        e.PlanState = 2;
        StartPath(e);
        v.Gear = 1f; v.DriveState = 3;
    }

    /// <summary>termTF: close enough again.</summary>
    private static bool TermTooFar(Entity e, Entity? t) => t is not null && Spatial(e, t) <= (e.IsHeli ? 720f : 180f);
}
