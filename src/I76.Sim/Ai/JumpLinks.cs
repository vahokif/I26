using I76.Formats.Assembly;
using System.Diagnostics;
using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;

namespace I76.Sim.Ai;

/// <summary>
/// A ramp a car can jump from: where its foot and lip are, which way it climbs, how much straight run-up lies before it
/// and where a car should line up. Found from the map's structures (<see cref="StructureAttach.Ends"/>: a drive-on surface
/// that climbs), so stock arenas and editor-made maps alike.
/// </summary>
public sealed class JumpLink
{
    public required int Index { get; init; }
    public required StaticObject Ramp { get; init; }
    /// <summary>The middle of the ramp's foot and of its lip (the top edge), world space.</summary>
    public required Vector3 Foot { get; init; }
    public required Vector3 Lip { get; init; }
    /// <summary>The horizontal unit vector the ramp climbs along (x, z).</summary>
    public required Vector2 Heading { get; init; }
    public required float Width { get; init; }
    /// <summary>The clear straight length before the foot a car can run up along (capped at <see cref="JumpLinks.MaxRunUp"/>).</summary>
    public required float RunUp { get; init; }
    /// <summary>
    /// Something lies beyond the lip that a car cannot drive over (impassable ground, or a drop of more than a few metres) within
    /// <see cref="JumpLinks.CrossingReach"/>: the ramp is a way across. A ramp that only throws a car over flat ground is a bump, not a link.
    /// </summary>
    public bool Crossing { get; init; }
    /// <summary>Where a car lines up: on the ramp's axis this far before the foot (the shorter of the run-up and <see cref="JumpLinks.MaxLineUp"/>).</summary>
    public float LineUp => System.Math.Min(RunUp, JumpLinks.MaxLineUp);
    public Vector3 Approach => new(Foot.X - Heading.X * LineUp, Foot.Y, Foot.Z - Heading.Y * LineUp);
    /// <summary>The ramp still stands as it was found (a destroyed one is no jump).</summary>
    public bool Standing => !Ramp.Destroyed && !Ramp.Collapsed && Ramp.Solid;
    public override string ToString() => $"{Ramp.Name} at ({Foot.X:0},{Foot.Z:0}) heading ({Heading.X:0.00},{Heading.Y:0.00}) run-up {RunUp:0} m";
}

/// <summary>What a car did when it left a ramp's lip at one speed: where it came down, for how long it flew, and whether that was a good landing.</summary>
public readonly record struct JumpSample(float Speed, Vector3 Landing, float Air, float Impact, bool Good, string Why);

/// <summary>A ramp a particular car can use: the speed it should reach the foot at, the window of speeds that land well, where it comes down.</summary>
public sealed record JumpUse(JumpLink Link, float Speed, float MinSpeed, float MaxSpeed, Vector3 Landing, float Reach);

/// <summary>A ramp's landings for one car, by take-off speed, and the window of speeds that land well.</summary>
public sealed class JumpProfile(JumpLink link, JumpSample[] samples)
{
    public JumpLink Link { get; } = link;
    public JumpSample[] Samples { get; } = samples;
    /// <summary>The lowest and highest speed of the longest run of good landings (m/s); both 0 when none lands well.</summary>
    public float MinSpeed { get; } = Window(samples).Lo;
    public float MaxSpeed { get; } = Window(samples).Hi;
    public bool Any => MaxSpeed > 0;
    public float Mid => 0.5f * (MinSpeed + MaxSpeed);

    private static (float Lo, float Hi) Window(JumpSample[] s)
    {
        float bestLo = 0, bestHi = 0, lo = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!s[i].Good) { lo = 0; continue; }
            if (lo == 0) lo = s[i].Speed;
            if (s[i].Speed - lo >= bestHi - bestLo) (bestLo, bestHi) = (lo, s[i].Speed);
        }
        return (bestLo, bestHi);
    }

    /// <summary>Where the car comes down at <paramref name="speed"/>: between the two samples either side.</summary>
    public Vector3 LandingAt(float speed)
    {
        var s = Samples;
        if (speed <= s[0].Speed) return s[0].Landing;
        for (int i = 1; i < s.Length; i++)
            if (speed <= s[i].Speed) return Vector3.Lerp(s[i - 1].Landing, s[i].Landing, (speed - s[i - 1].Speed) / (s[i].Speed - s[i - 1].Speed));
        return s[^1].Landing;
    }
}

/// <summary>
/// The jump links of a map (docs/multiplayer.md "Bots"): its ramps, and for a car, what the ramp does to it. Everything is found
/// by driving a copy of the car over the ramp in a world of its own (the map's ground and statics, a random stream of its own,
/// no damage model), so nothing here touches the mission's state or its random sequence, and the same map and car give the same
/// answer on every machine.
/// </summary>
public sealed class JumpLinks
{
    /// <summary>The longest run-up looked for, and the longest lined-up approach used, metres.</summary>
    public const float MaxRunUp = 220f, MaxLineUp = 200f;
    /// <summary>Shorter than this a run-up gives a car no room to line up.</summary>
    public const float MinRunUp = 24f;
    /// <summary>How far past the lip a gap is looked for, metres.</summary>
    public const float CrossingReach = 90f;
    /// <summary>The speeds tried (m/s): from <see cref="SpeedLow"/> in steps of <see cref="SpeedStep"/> up to <see cref="SpeedHigh"/>.</summary>
    public const float SpeedLow = 10f, SpeedStep = 2.5f, SpeedHigh = 55f;
    /// <summary>The speed a car has when it reaches the line-up point, for the reach test (the bot slows to line up).</summary>
    public const float LineUpSpeed = 10f;
    /// <summary>A landing harder than this (the speed into the ground along its normal, m/s) is not a good one.</summary>
    public const float HardLanding = 17f;

    private readonly IGround _ground;
    public IReadOnlyList<JumpLink> Links { get; }
    /// <summary>Milliseconds spent finding links and simulating them so far (the cost of the map, for the log).</summary>
    public double Milliseconds { get; private set; }
    /// <summary>How many probe drives have been run.</summary>
    public int Drives { get; private set; }

    private readonly Dictionary<(int Link, string Build), JumpProfile> _profiles = [];
    private readonly Dictionary<(int Link, string Build), float> _reach = [];

    private JumpLinks(IGround ground, List<JumpLink> links) { _ground = ground; Links = links; }

    public bool Any => Links.Count > 0;

    /// <summary>The ramps of the map's ground: every standing type-0xb structure whose drive-on surface climbs from a foot to a lip.</summary>
    public static JumpLinks Find(IGround ground)
    {
        var sw = Stopwatch.StartNew();
        var links = new List<JumpLink>();
        if (ground is IHasStatics hs)
            foreach (var o in hs.World.Objects)
            {
                if (o.Type != 0xb || o.Destroyed || o.Surfaces.Count == 0) continue;
                var ends = StructureAttach.Ends(o.Surfaces.Select(p => p.V));
                if (ends.Count != 2) continue;
                var foot = ends.FirstOrDefault(e => e.Low);
                var lip = ends.FirstOrDefault(e => !e.Low);
                if (foot.Width <= 0 || lip.Width <= 0) continue;
                // a jump: it climbs, and enough of it to leave the ground
                if (foot.Slope < 0.12f || lip.Position.Y - foot.Position.Y < 0.8f) continue;
                var h = -foot.Direction;
                float run = RunUpOf(ground, foot.Position, h, foot.Width, o);
                if (run < MinRunUp) continue;
                links.Add(new JumpLink { Index = links.Count, Ramp = o, Foot = foot.Position, Lip = lip.Position, Heading = h, Width = foot.Width, RunUp = run, Crossing = CrossesSomething(ground, lip.Position, h) });
            }
        return new JumpLinks(ground, links) { Milliseconds = sw.Elapsed.TotalMilliseconds };
    }

    private static bool CrossesSomething(IGround g, Vector3 lip, Vector2 h)
    {
        for (float d = 6f; d <= CrossingReach; d += 3f)
        {
            float x = lip.X + h.X * d, z = lip.Z + h.Y * d;
            if (g.Impassable(x, z) || g.BareHeight(x, z) < lip.Y - 6f) return true;
        }
        return false;
    }

    /// <summary>How far back from the foot the ground runs straight and open: not impassable across the ramp's width, no steeper than a gentle slope.</summary>
    private static float RunUpOf(IGround g, Vector3 foot, Vector2 h, float width, StaticObject ramp)
    {
        float open = MaxRunUp;
        var side = new Vector2(-h.Y, h.X);
        float half = System.Math.Min(width * 0.5f, 4f);
        float prev = g.BareHeight(foot.X - h.X * 1, foot.Z - h.Y * 1);
        float d;
        for (d = 2f; d <= MaxRunUp; d += 2f)
        {
            float x = foot.X - h.X * d, z = foot.Z - h.Y * d;
            foreach (float s in (ReadOnlySpan<float>)[-half, 0f, half])
                if (g.Impassable(x + side.X * s, z + side.Y * s)) return d - 2f;
            float y = g.BareHeight(x, z);
            if (System.Math.Abs(y - prev) > 0.9f) return d - 2f;
            prev = y;
        }
        return System.Math.Min(open, FirstSolid(g, foot, h, side, half, ramp));
    }

    /// <summary>How far back along the run-up the first building, wall or other solid scenery stands (the car's width either side of the axis, two heights): <see cref="MaxRunUp"/> when none.</summary>
    private static float FirstSolid(IGround g, Vector3 foot, Vector2 h, Vector2 side, float half, StaticObject ramp)
    {
        if (g is not IHasStatics hs) return MaxRunUp;
        float best = MaxRunUp;
        var dir = new Vector3(-h.X, 0, -h.Y);
        foreach (var o in hs.World.Objects)
        {
            if (ReferenceEquals(o, ramp) || !o.Solid || o.Vehicle is not null || o.Type == 0xc) continue;
            // only what lies near the line
            var rel = new Vector2(o.Centre.X - foot.X, o.Centre.Z - foot.Z);
            float along = -Vector2.Dot(rel, h), lateral = System.Math.Abs(Vector2.Dot(rel, side));
            float reach = o.BoundRadius + o.Radius + half + 6f;
            if (along < -reach || along > MaxRunUp + reach || lateral > reach) continue;
            foreach (float s in (ReadOnlySpan<float>)[-half, 0f, half])
                foreach (float up in (ReadOnlySpan<float>)[0.7f, 1.6f])
                {
                    var p = new Vector3(foot.X + side.X * s, foot.Y + up, foot.Z + side.Y * s);
                    var d = dir * MaxRunUp;
                    float t = 2f;
                    if (o.Polys.Count > 0 && StaticWorld.RayMesh(o.Polys, p, d, 1f).Hit is not null) t = System.Math.Min(t, StaticWorld.RayMesh(o.Polys, p, d, 1f).T);
                    if (o.Boxes.Count > 0 && StaticWorld.RayBoxes(o.Boxes, p, d, 1f) is { } b) t = System.Math.Min(t, b.T);
                    if (t <= 1f) best = System.Math.Min(best, t * MaxRunUp);
                }
        }
        return System.Math.Max(0, best - 4f);
    }

    // ---- driving a copy of the car ----

    /// <summary>What marks a build for the caches: the car file, its mass, engine and tyres, how much engine it has left, which tyres are blown.</summary>
    public static string BuildKey(Vehicle v, bool healthy = false) =>
        $"{v.Definition}|{v.P.Mass:0}|{v.Engine.PeakForce:0}|{v.FrontLeft.Grip:0.00}/{v.RearLeft.Grip:0.00}" + (healthy ? "" : $"|{v.Engine.Damage:0.0}|{(v.FrontLeft.Blown ? 1 : 0)}{(v.FrontRight.Blown ? 1 : 0)}{(v.RearLeft.Blown ? 1 : 0)}{(v.RearRight.Blown ? 1 : 0)}");

    private sealed class Recorder : IStepDamage
    {
        public readonly List<StaticObject> Hit = [];
        public float Impact;
        public bool Fell;
        public bool Armed;
        public void Timer(Vehicle v, float dt) { }
        public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) { }
        public void Landing(Vehicle v, Vector3 impactVel, Vector3 n) { if (Armed) Impact = System.Math.Max(Impact, System.Math.Abs(Vector3.Dot(impactVel, n))); }
        public void FallKill(Vehicle v) => Fell = true;
        public void Rough(Vehicle v, float dt) { }
        public void StaticContact(Vehicle v, StaticObject o, Contact rec) { if (Armed) Hit.Add(o); }
    }

    private static Wheel Copy(Wheel w) { var c = Session.FieldCopy.Clone(w); c.Travel = c.Ground = c.PrevTarget = c.PrevRate = 0; c.Y1 = c.Y2 = 0; c.AtLimit = false; return c; }

    /// <summary>A copy of the car to drive: its mass, engine, tyres and suspension as they are, none of its weapons, damage or hits.</summary>
    private static Vehicle CopyOf(Vehicle src, bool healthy = false)
    {
        var c = Session.FieldCopy.Clone(src);
        c.P = Session.FieldCopy.Clone(src.P);
        c.Engine = Session.FieldCopy.Clone(src.Engine);
        c.FrontRight = Copy(src.FrontRight); c.FrontLeft = Copy(src.FrontLeft); c.RearRight = Copy(src.RearRight); c.RearLeft = Copy(src.RearLeft);
        c.MiddleRight = src.MiddleRight is { } mr ? Copy(mr) : null; c.MiddleLeft = src.MiddleLeft is { } ml ? Copy(ml) : null;
        c.Armory = null; c.Health = null; c.Build = null; c.Arms = new CrewArms(); c.Rec = default; c.Paint = null;
        c.Wreck = false; c.Timer = 0; c.NetId = 0;
        c.Flags = (c.Flags & ~(VehicleFlags.Dead | VehicleFlags.Airborne | VehicleFlags.JustLanded | VehicleFlags.Disabled | VehicleFlags.Jamming | VehicleFlags.Honking)) | VehicleFlags.EngineOn;
        c.Engine.Gear = 1; c.Engine.Rpm = 1050f; c.Engine.Speedo = 0;
        if (healthy)
        {
            c.Engine.Damage = 1f;
            foreach (var w in new[] { c.FrontRight, c.FrontLeft, c.RearRight, c.RearLeft }) w.Blown = false;
        }
        c.Gear = 1; c.DriveState = 3; c.Handbrake = 0;
        return c;
    }

    private sealed class Run
    {
        public SimWorld World = null!;
        public Vehicle Car = null!;
        public Recorder Rec = new();
        public AiBlock Ai = new();
    }

    private Run Start(Vehicle template, Vector3 at, Vector2 h, float speed, bool healthy = false)
    {
        var car = CopyOf(template, healthy);
        var w = new SimWorld(_ground, new CrtRand(1));
        var rec = new Recorder();
        w.Step.Damage = rec;
        w.Humans.Add(car);
        w.Player = car;
        w.Vehicles.Add(car);
        var fwd = new Vector3(h.X, 0, h.Y);
        float y = _ground.Query(at.X, at.Y + 2f, at.Z).Height;
        car.Pos = new Double3(at.X, (double)y + 0.8, at.Z);
        car.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        car.Vel = fwd * speed; car.Speed = speed; car.AngVel = default;
        var ai = new AiBlock(); ai.SetSkill(1, 1);
        Drives++;
        return new Run { World = w, Car = car, Rec = rec, Ai = ai };
    }

    private static void Hold(Run r, float heading, float speed, float dt)
    {
        var v = r.Car; var w = r.World;
        AiDriver.Steer(v, r.Ai, w.Ground, heading, dt);
        AiDriver.Throttle(v, r.Ai, w.Ground, speed, dt, m => w.Step.TractionLimit(v, m, false));
    }

    private static float HeadingOf(Vector2 h) => MathF.Atan2(h.X, h.Y);

    /// <summary>
    /// The speed the car has at the foot after a run from the line-up point (at <see cref="LineUpSpeed"/>) flat out along the ramp's
    /// axis; 0 when something other than the ramp is in the way (a wall, a building) or it cannot get there.
    /// </summary>
    public float Reach(JumpLink l, Vehicle car, float dt, bool healthy = false)
    {
        var key = (l.Index, BuildKey(car, healthy));
        if (_reach.TryGetValue(key, out var cached)) return cached;
        var sw = Stopwatch.StartNew();
        var r = Start(car, l.Approach, l.Heading, LineUpSpeed, healthy);
        r.Rec.Armed = true;
        float head = HeadingOf(l.Heading);
        float result = 0;
        int max = (int)(25f / dt);
        for (int t = 0; t < max; t++)
        {
            Hold(r, head, 200f, dt);
            r.Car.Throttle = MathF.Min(r.Car.Throttle, 1f);
            r.World.FrameOne(r.Car, dt, [r.Car]);
            float along = Vector2.Dot(new Vector2((float)r.Car.Pos.X - l.Foot.X, (float)r.Car.Pos.Z - l.Foot.Z), l.Heading);
            if (r.Rec.Hit.Any(o => !ReferenceEquals(o, l.Ramp))) break;
            if (along >= -0.5f) { result = r.Car.Speed; break; }
        }
        Milliseconds += sw.Elapsed.TotalMilliseconds;
        _reach[key] = result;
        return result;
    }

    /// <summary>What the ramp does to this car at each speed (cached by build): the table of landings and the window of good speeds.</summary>
    public JumpProfile Profile(JumpLink l, Vehicle car, float dt)
    {
        var key = (l.Index, BuildKey(car, healthy: true));
        if (_profiles.TryGetValue(key, out var cached)) return cached;
        // only speeds the healthy car can reach are of use
        float top = MathF.Min(SpeedHigh, Reach(l, car, dt, healthy: true) + SpeedStep);
        var sw = Stopwatch.StartNew();
        // from the fastest the car gets down while the landings stay good (and one more, to close the window): the window is the run of good ones
        var samples = new List<JumpSample>();
        int steps = (int)MathF.Floor((top - SpeedLow) / SpeedStep + 0.01f);
        bool seen = false;
        for (int k = steps; k >= 0; k--)
        {
            var smp = Fly(l, car, SpeedLow + k * SpeedStep, dt, healthy: true);
            samples.Add(smp);
            if (smp.Good) seen = true; else if (seen) break;
        }
        samples.Reverse();
        var p = new JumpProfile(l, [.. samples]);
        Milliseconds += sw.Elapsed.TotalMilliseconds;
        _profiles[key] = p;
        return p;
    }

    /// <summary>One drive over the ramp at a held speed, then a coast to rest on the other side.</summary>
    public JumpSample Fly(JumpLink l, Vehicle car, float speed, float dt, bool healthy = false)
    {
        float lead = System.Math.Min(l.RunUp, 30f);
        var r = Start(car, new Vector3(l.Foot.X - l.Heading.X * lead, l.Foot.Y, l.Foot.Z - l.Heading.Y * lead), l.Heading, speed, healthy);
        var v = r.Car;
        float head = HeadingOf(l.Heading);
        bool took = false, landed = false;
        float tTake = 0, tLand = 0, t = 0, after = 0;
        Vector3 land = default;
        string why = "";
        int max = (int)(14f / dt);
        for (int i = 0; i < max; i++, t += dt)
        {
            var foot2 = new Vector2((float)v.Pos.X - l.Foot.X, (float)v.Pos.Z - l.Foot.Z);
            float along = Vector2.Dot(foot2, l.Heading);
            if (!took && along < -6f) Hold(r, head, speed, dt);
            else if (!took) { v.Steer = 0; AiDriver.Throttle(v, r.Ai, r.World.Ground, speed, dt, m => r.World.Step.TractionLimit(v, m, false)); }
            else { v.Steer = 0; v.Throttle = 0; }
            r.Rec.Armed = true;
            r.World.FrameOne(v, dt, [v]);
            bool air = (v.Flags & VehicleFlags.Airborne) != 0;
            if (!took && air && Vector2.Dot(new Vector2((float)v.Pos.X - l.Foot.X, (float)v.Pos.Z - l.Foot.Z), l.Heading) > 1f) { took = true; tTake = t; r.Rec.Hit.Clear(); }
            else if (took && !landed && !air && t - tTake > 2 * dt) { landed = true; tLand = t; land = v.Pos.ToVector3(); }
            if (!took && r.Rec.Hit.Any(o => !ReferenceEquals(o, l.Ramp))) { why = "hit something before the lip"; break; }
            if (landed) { after += dt; if (after > 3f) break; }
            if (r.Rec.Fell) { why = "fell"; break; }
        }
        float impact = r.Rec.Impact;
        if (why.Length == 0)
        {
            var end = v.Pos.ToVector3();
            if (!took) why = "never left the ramp";
            else if (!landed) why = "still in the air";
            else if (tLand - tTake < 0.25f) why = "no jump";
            else if (r.Rec.Hit.Any(o => !ReferenceEquals(o, l.Ramp))) why = "hit something after the lip";
            else if (v.Rot.Up.Y < 0.75f) why = "not upright";
            else if (impact > HardLanding) why = "landed hard";
            else if (AnyImpassable(land) || AnyImpassable(end)) why = "off the road (water or a cliff)";
            else if (v.Speed < 3f) why = "stopped";
            else if (v.Pos.Y - _ground.BareHeight(end.X, end.Z) > 6f) why = "not on the ground";
        }
        bool good = why.Length == 0;
        return new JumpSample(speed, took && landed ? land : new Vector3(l.Lip.X, l.Lip.Y, l.Lip.Z), landed ? tLand - tTake : 0, impact, good, good ? "" : why);
    }

    /// <summary>What a car does with a ramp: the speed to hit it at and where that lands, or null when this car cannot use it.</summary>
    public JumpUse? Use(JumpLink l, Vehicle car, float dt)
    {
        if (!l.Standing) return null;
        var prof = Profile(l, car, dt);
        if (!prof.Any || prof.MaxSpeed - prof.MinSpeed < SpeedStep - 0.01f) return null;
        float reach = Reach(l, car, dt);
        // aim for the middle of the good speeds, as fast as the car gets there with a little to spare, and well inside the window
        float v = MathF.Min(prof.Mid, reach * 0.97f - 0.3f);
        v = MathF.Min(v, prof.MaxSpeed - 0.7f);
        if (v < prof.MinSpeed + 0.7f) return null;
        return new JumpUse(l, v, prof.MinSpeed, prof.MaxSpeed, prof.LandingAt(v), reach);
    }

    private bool AnyImpassable(Vector3 p)
    {
        foreach (var (dx, dz) in new (float, float)[] { (0, 0), (3, 0), (-3, 0), (0, 3), (0, -3) })
            if (_ground.Impassable(p.X + dx, p.Z + dz)) return true;
        return false;
    }
}
