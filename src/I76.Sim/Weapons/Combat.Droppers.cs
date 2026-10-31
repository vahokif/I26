using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>
/// A flame thrower's stream (the original grows it, shrinks it,
/// rebuilds, tests and draws it).
/// One per weapon: a chain of up to 19 cross-sections a metre apart along the
/// muzzle's forward, one more per shot, two fewer on a frame without one.
/// </summary>
public sealed class Flame
{
    public required Vehicle Owner { get; init; }
    public required Weapon Weapon { get; init; }
    /// <summary>0 light (FlameThrower, kind 9), 1 medium (Gas Launcher, 10), 2 heavy (Napalm Hose, Pyro-Tomic, 11): the xfl1 / xfm1 / xfh1 frames.</summary>
    public required int Style { get; init; }
    /// <summary>The instance damage at the shot, per point per second.</summary>
    public int Damage;
    /// <summary>[5]: cross-sections in the chain, 2..19.</summary>
    public int Count;
    /// <summary>[4]: a shot grew it this frame.</summary>
    public bool Fired;
    /// <summary>[0x22]: built once already (the hit spread and the whip need last frame's end).</summary>
    public bool Built;
    /// <summary>The chain's far end as built last frame (the translation of the stored transform, [0x1c..0x20]).</summary>
    public Vector3 PrevEnd;
    /// <summary>The cross-sections as built this frame, three world points each (Count of them).</summary>
    public Vector3[] Cross = new Vector3[19 * 3];
    /// <summary>The owner's pose the chain was built at, so a view can carry it on the car between ticks.</summary>
    public Vector3 OwnerPos;
    public Rows OwnerRot;
    /// <summary>The cross-section the stream first met something at this frame, −1 if none (from there it lies flat).</summary>
    public int FirstHit = -1;
}

public sealed partial class Combat
{
    /// <summary>The frame's delta (the raw frame delta): the droppers' placement and the flames' damage scale by it.</summary>
    public float FrameDt { get; internal set; } = I76.Sim.Session.SimRate.R20.TickSeconds;

    /// <summary>The live flame streams.</summary>
    public List<Flame> Flames { get; } = [];

    /// <summary>The stream's cross-section, a triangle of radius 0.15 across the muzzle's forward.</summary>
    private static readonly Vector3[] FlameRound = [new(0.15f, 0, 0), new(-0.075f, 0.1299f, 0), new(-0.075f, -0.1299f, 0)];
    /// <summary>Flattened once the stream has met something.</summary>
    private static readonly Vector3[] FlameFlat = [new(0, -0.01f, 0), new(0.1299f, 0, 0), new(-0.1299f, 0, 0)];

    /// <summary>
    /// Once per shot: a new stream starts with one cross-section
    /// and grows at once to two; an existing one grows by one, to at most 19.
    /// </summary>
    private void FlameShot(Vehicle owner, Weapon w, int style, int damage)
    {
        var f = Flames.Find(x => x.Weapon == w);
        if (f is null)
        {
            if (style >= 3) return;
            f = new Flame { Owner = owner, Weapon = w, Style = style, Count = 1 };
            Flames.Add(f);
        }
        f.Damage = damage;
        if (f.Count < 19) f.Count++;
        f.Fired = true;
    }

    /// <summary>The flame-hold deviation below (on; the tests switch it off to see the original's stream at each rate).</summary>
    public bool FlameHoldEnabled { get; set; } = true;

    /// <summary>
    /// Deviation: a tick on which the trigger is held counts as a shot for the stream's
    /// length when the timing loop fired none, as every tick does at 20 fps or
    /// slower (ammo, rounds and damage follow the loop unchanged). A no-op at the
    /// default 20 Hz tick, where the loop fires on every held tick; it matters from 25 Hz up.
    /// </summary>
    private void FlameHold(Vehicle owner, Weapon w)
    {
        if (!FlameHoldEnabled || _silent) return;
        var f = Flames.Find(x => x.Weapon == w);
        if (f is not null && f.Fired) return;
        FlameShot(owner, w, (int)(w.Def.Kind - 9), f?.Damage ?? w.Damage);
    }

    /// <summary>
    /// The render pass's flame update, once per frame (after a preparatory call), which
    /// builds the streams and burns what they touch. Here it is the sim's, every tick, whatever is on screen.
    /// Deviation: the original's rear-view mirror pass calls the flame draw again every second frame,
    /// so with the cockpit mirror up its flames did half as much again; damage here does not depend on the view
    /// (docs/deviations.md).
    /// </summary>
    private void FlameFrame(float dt, Vehicle? only = null)
    {
        for (int i = Flames.Count - 1; i >= 0; i--)
        {
            var f = Flames[i];
            if (only is not null && f.Owner != only) continue;
            // the first block: the muzzle's object is gone
            if (!World.Vehicles.Contains(f.Owner) || f.Owner.Wreck) { Flames.RemoveAt(i); continue; }
            // A frame without a shot takes two cross-sections off; down to one, the stream is gone
            if (!f.Fired)
            {
                if (f.Count > 1) f.Count--;
                if (f.Count > 1) f.Count--;
                if (f.Count <= 1) { Flames.RemoveAt(i); continue; }
            }
            f.Fired = false;
            BuildFlame(f, dt);
        }
    }

    /// <summary>
    /// For one stream: the chain from the muzzle, a metre a step
    /// along its forward with a droop of 6.37·t² (t = k·dt/n, so next to none)
    /// and, from the second build, a pull toward where last frame's end was;
    /// every point from the second tests the colliders and burns what it
    /// touches, max(1, ftol(damage·mult·dt)) on the fire channel; from the first
    /// point that touches, the rest lie flat on the surface, an eighth of a
    /// step apart, each lighting an xflht1.
    /// </summary>
    private void BuildFlame(Flame f, float dt)
    {
        var v = f.Owner; var w = f.Weapon;
        var rot = Rows.Concat(w.MuzzleRot, v.Rot);
        var pos = v.Pos.ToVector3() + v.Rot.ToWorld(w.MuzzlePos);
        f.OwnerPos = v.Pos.ToVector3(); f.OwnerRot = v.Rot;
        int n = f.Count;
        var fwd = rot.Forward;
        float t = 0, tStep = dt / n;
        float scale = 1f, grow = n * 0.05f * 2.3f / n;
        var shape = FlameRound;
        float shapeScale = 1f;
        bool met = false;
        int firstHit = -1;
        var start = pos;
        var ord = w.Def.Ordnance;
        string fx = ord?.ImpactXdf[1] ?? "";
        f.FirstHit = -1;
        for (int k = 1; k <= n; k++)
        {
            int idx = k - 1;
            for (int j = 0; j < 3; j++) f.Cross[idx * 3 + j] = pos + rot.ToWorld(shape[j] * shapeScale);
            shape = met ? FlameFlat : FlameRound;
            shapeScale = scale;
            scale += grow;
            var step = new Vector3(fwd.X, t * t * -9.8f * 0.65f + fwd.Y, fwd.Z);
            t += tStep;
            if (k > 1)
            {
                bool hit = FlameTouch(f, pos, start, dt);
                if (f.Built)
                {
                    if (met || hit)
                    {
                        if (firstHit == -1) { firstHit = idx; f.FirstHit = idx; }
                        grow = (1f - (float)(n - idx) / (n - firstHit)) * 1.5f * grow;
                        step = new Vector3(fwd.X * 0.125f, 0.01f, fwd.Z * 0.125f);
                        if (!_cosmetic && fx.Length > 0 && !fx.Equals("null", StringComparison.OrdinalIgnoreCase) && effects(fx) is { } e)
                        {
                            Events.Add(new Explosion(e, pos));
                            if (e.SplashDamage != 0) Splash(pos, e.SplashRadius, e.SplashDamage, v);
                        }
                        met = true;
                    }
                    else
                    {
                        float wk = (float)k / (n + 1);
                        wk = wk * wk * dt * 0.85f;
                        step += (f.PrevEnd - (pos + step)) * wk;
                    }
                }
            }
            if (k == 1) start = pos;
            pos += step;
        }
        f.PrevEnd = pos;
        f.Built = true;
    }

    /// <summary>
    /// For a stream point p: every collider (not the owner) whose
    /// sphere p is within its radius + 1 of, and whose shape the segment from p
    /// on by (p − first point) crosses, takes the packet: flags
    /// 4, ch2 = max(1, ftol(damage · mult · dt)), the owner the attacker.
    /// </summary>
    private bool FlameTouch(Flame f, Vector3 p, Vector3 start, float dt)
    {
        var d = p - start;
        if (d.LengthSquared() < 1e-12f) return false;
        float mult = !World.IsHuman(f.Owner) ? 1f : Difficulty switch { 0 => 2f, 2 => 0.75f, _ => 1f };
        int ch = (int)(f.Damage * mult * dt);
        if (ch == 0) ch = 1;
        bool any = false;
        DamagePacket Packet()
        {
            var pk = new DamagePacket { Attacker = f.Owner, Mask = 4, Kind = I76.Sim.Damage.DamageKind.Flame };
            pk.Ch[2] = ch;
            return pk;
        }
        foreach (var v in World.Vehicles)
        {
            if (v == f.Owner || v.Wreck || v.IsEmplacement) continue;
            var (c, rad) = Collider(v);
            if ((p - c).LengthSquared() >= (rad + 1f) * (rad + 1f)) continue;
            if (FlameEntersBox(v, p, d) is not { } t) continue;
            any = true;
            Sink?.Apply(v, Packet(), Local(v, p + d * t));
        }
        if (World.Ground is IHasStatics hs)
            foreach (var o in hs.World.Objects)
            {
                if (!o.Solid || o.Destroyed || o.Vehicle == f.Owner) continue;
                if ((p - o.Centre).LengthSquared() >= (o.Radius + 1f) * (o.Radius + 1f)) continue;
                var (tm, poly) = StaticWorld.RayMesh(o.Polys, p, d, 1f);
                float tt = poly is not null ? tm : 2f;
                int node = poly?.Node ?? -1;
                if (StaticWorld.RayBoxes(o.Boxes, p, d, 1f) is { } bh && bh.T < tt) { tt = bh.T; node = bh.Box.Node; }
                if (tt > 1f) continue;
                any = true;
                Sink?.Apply(o, Packet(), Local(o, p + d * tt), node);
            }
        return any;
    }

    /// <summary>
    /// Kind 1 for a flame point: the slab test of the segment from p on by d (the
    /// velocity (p − first point)·the step rate over the frame delta) against the COLP box, margin 0. The entry
    /// must lie in [0, 1]: a point already inside the box touches nothing, so of the points lying flat on a car
    /// only those short of its face burn (a stream 1.5 m from a car's nose gives 5 packets a frame, not 18:
    /// measured in the original game). An axis the segment barely moves along (|d| &lt; 0.0001) tests the
    /// point's side only.
    /// </summary>
    private static float? FlameEntersBox(Vehicle v, Vector3 p, Vector3 d)
    {
        var lp = v.Rot.ToLocal(p - v.Pos.ToVector3()); var ld = v.Rot.ToLocal(d);
        Span<float> lo = [v.BoxX[0], v.BoxY[1], v.BoxZ[0]], hi = [v.BoxX[3], v.BoxY[3], v.BoxZ[3]];
        Span<float> pp = [lp.X, lp.Y, lp.Z], qq = [ld.X, ld.Y, ld.Z];
        float enter = float.NegativeInfinity, exit = float.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            float q = qq[a];
            if (q > -0.0001f && q < 0.0001f)
            {
                if (pp[a] < lo[a] || pp[a] > hi[a]) return null;
                continue;
            }
            float t1 = (lo[a] - pp[a]) / q, t2 = (hi[a] - pp[a]) / q;
            enter = MathF.Max(enter, MathF.Min(t1, t2));
            exit = MathF.Min(exit, MathF.Max(t1, t2));
            if (exit < 0) return null;
        }
        if (enter > exit || enter > 1f || enter < 0) return null;
        return enter;
    }

    // ---- droppers: oil (0xc), fire (0x11), and the unshipped 0xe ----

    /// <summary>
    /// The dropper placement after the three common draws: moved on along
    /// the muzzle's forward by P56·dt·0.2, a yaw of 2s·u − s degrees if that is
    /// positive, on again by (shooter speed·dt + 1.5)·u, then down onto the
    /// ground 0.0001 above it, lying in its plane.
    /// </summary>
    private (Vector3 Pos, Rows Rot) DropPlace(Vehicle shooter, I76.Formats.Bwd2.GizmoDef def, Vector3 pos, Rows rot, float s)
    {
        float u4 = U();
        pos += rot.Forward * (def.Speed * FrameDt * 0.2f);
        float yaw = 2 * s * u4 - s;
        if (yaw > 0) rot = Rows.Concat(Rows.AboutY(yaw * MathF.PI / 180f), rot);
        float u5 = U();
        pos += rot.Forward * ((shooter.Speed * FrameDt + 1.5f) * u5);
        var (h, n) = World.Ground.Query(pos.X, pos.Y, pos.Z);
        pos.Y = h + 0.0001f;
        var fwd = rot.Forward - n * Vector3.Dot(rot.Forward, n);
        if (fwd.LengthSquared() < 1e-6f) fwd = Vector3.Cross(Vector3.UnitX, n);
        fwd = Vector3.Normalize(fwd);
        return (pos, new Rows(Vector3.Cross(n, fwd), n, fwd));
    }

    /// <summary>
    /// Oil / fire: the drop lies where it fell;
    /// every frame, count times, a 1 m probe straight up from it (the dropper
    /// excluded for its first 2 s). A car in an oil slick loses its grip for
    /// 2 s (the ignition timer 2, flags |= 0x80000400); fire burns
    /// with the round's packet every frame, not scaled by the frame. Either
    /// way the hit's effect and sound (vcdoil.wav, xfire1.wav). Only the life
    /// running out ends a drop. The Nitro Pack's caltrops (kind 0xe)
    /// are the same probe, and a car
    /// over one has its nearest tyre go down (CaltropTyreHit) every frame.
    /// </summary>
    private bool StepSlick(Round r, float dt)
    {
        r.PrevPos = r.Pos;
        r.Age += dt;
        var up = Vector3.UnitY;
        bool met = false;
        object? victim = null; Vector3 at = r.Pos; int node = -1;
        for (int i = 0; i < r.Count; i++)
        {
            if (Probe(r, r.Pos, up) is { } h) { met = true; victim = h.Victim; at = h.At; node = h.Node; }
            if (!met) continue;
            if (_cosmetic) return false;
            if (victim is Vehicle v && r.Kind == 0xc) Oil(v);
            if (victim is Vehicle cv && r.Kind == 0xe) Damage?.CaltropTyreHit(cv, r.Pos);
            OnHitOnce(r, victim, at, up, node);
        }
        return false;
    }

    /// <summary>2 s of no grip on any surface gripping above 0.2, on the car's shared timer.</summary>
    public static void Oil(Vehicle v)
    {
        if (v.IsHeli || v.IsEmplacement) return;
        v.Timer = 2f;
        v.Flags |= VehicleFlags.NoGripOnLowSurface | (VehicleFlags)0x80000000;
    }

    /// <summary>For a short segment: the earliest collider it crosses (vehicles, then structures).</summary>
    private (object Victim, Vector3 At, int Node)? Probe(Round r, Vector3 p0, Vector3 d)
    {
        float best = 2f; object? victim = null; int node = -1;
        foreach (var v in World.Vehicles)
        {
            if (v.Wreck || v.IsEmplacement || (v == r.Shooter && ShooterExcluded(r))) continue;
            if (SegmentVsVehicle(v, p0, d) is { } h && h.T < best) { best = h.T; victim = v; }
        }
        if (World.Ground is IHasStatics hs)
            foreach (var o in hs.World.Objects)
            {
                if (!o.Solid || (o.Vehicle is { } ov && ov == r.Shooter && ShooterExcluded(r))) continue;
                if ((p0 - o.Centre).LengthSquared() > (o.Radius + 1f) * (o.Radius + 1f)) continue;
                var (t, poly) = StaticWorld.RayMesh(o.Polys, p0, d, 1f);
                if (poly is not null && t < best) { best = t; victim = o; node = poly.Node; }
                if (StaticWorld.RayBoxes(o.Boxes, p0, d, 1f) is { } bh && bh.T < best) { best = bh.T; victim = o; node = bh.Box.Node; }
            }
        return victim is null ? null : (victim, p0 + d * best, victim is StaticObject ? node : -1);
    }

    /// <summary>For one probe hit: the packet (oil's is empty) and the impact pair by what was hit.</summary>
    private void OnHitOnce(Round r, object? victim, Vector3 at, Vector3 dir, int node = -1)
    {
        if (_cosmetic) return;
        if (victim is not null && Damage is { } sink && r.Ord.Mask != 0) sink.Apply(victim, RoundPacket(r), Local(victim, at), node);
        int cls = ImpactClass(victim, node);
        var fx = r.Ord.ImpactXdf[cls];
        Events.Add(new Impact(fx, r.Ord.ImpactWav[cls], at, victim, r.Kind, at, default, dir));
        if (fx.Length > 0 && !fx.Equals("null", StringComparison.OrdinalIgnoreCase) && effects(fx) is { } e)
        {
            Events.Add(new Explosion(e, at));
            if (e.SplashDamage != 0) Splash(at, e.SplashRadius, e.SplashDamage, r.Shooter);
        }
    }

    /// <summary>
    /// the class 6: a vehicle target, a shooter that is not an
    /// emplacement, in a forward gear, on the ground; the target behind the
    /// shooter's motion, reaching the shooter's spot in [−1, 7] s at its speed
    /// (planar distance / speed, 9.9 when still), and predicted there within 2.5 m.
    /// </summary>
    private static bool AiDrop(Vehicle shooter, Vehicle target)
    {
        if (shooter.IsEmplacement || shooter.Gear < 1f || (shooter.Flags & VehicleFlags.Airborne) != 0) return false;
        var s = shooter.Pos.ToVector3();
        var tp = target.Pos.ToVector3();
        if (Vector3.Dot(tp - s, shooter.Vel) > 0) return false;
        float t = target.Speed >= 0.001f ? new Vector2(tp.X - s.X, tp.Z - s.Z).Length() / target.Speed : 9.9f;
        if (t > 7f || t < -1f) return false;
        if (t < 0) t = 0;
        var at = tp + target.Vel * t;
        return (s - at).LengthSquared() < 6.25f;
    }
}
