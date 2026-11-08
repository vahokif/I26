using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Ai;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// #42, two stalls of the original's AI (both deliberate deviations, docs/deviations.md).
///
/// The endless reverse: the boxed-in check reads the forward-clearance measure, which is the traction
/// limit on the throttle, not a distance; below 1 u/s a strong engine's is under 0.1 on open ground. So a stopped car is
/// "boxed in" four ticks after it moves off, backs away, stops and does it again for good. Measured in the original game
/// (28 fps): T15's t15as01, sent after a distant target from rest,
/// entered back_away ten times running over 25 s with nothing near it (the forward-clearance measure 0.078 at rest), the same
/// cycle the port ran.
///
/// The sit beside: at a standstill the original's steering for its drivers writes 0, so Pursue reads the
/// wheel as straight and brakes for a stopped target within 24, wherever it is: a car stopped with the player alongside
/// stayed put.
/// </summary>
public class AiStuckTests(ITestOutputHelper log)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static MissionSetup T15()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        return MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t15.msn")!), asm, rand: new I76.Sim.Rng.CrtRand(1));
    }

    /// <summary>Puts a car at rest on the ground at (x, z) facing <paramref name="yaw"/> radians from +Z toward +X.</summary>
    private static void Put(MissionSetup s, Vehicle v, float x, float z, float yaw)
    {
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        v.Pos = new Double3(x, s.Ground.BareHeight(x, z), z);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        v.Vel = default; v.Speed = 0; v.Steer = 0; v.Throttle = -1;
    }

    /// <summary>
    /// One frame as MissionScript.AfterFrame and SimWorld run it, but with these orders in place of the mission's script
    /// (the other cars get none and sit).
    /// </summary>
    private static void Frame(MissionSetup s, float dt, Action orders)
    {
        var h = s.Script!.Host;
        h.Now += dt; h.FsmTick++; h.FrameDelta = dt;
        orders();
        h.Ptable.EndFrame();
        s.World.Frame(dt);
    }

    private static int Top(Entity e) => e.Depth >= 0 ? e.Stack[e.Depth] : -1;

    [Theory]
    [InlineData(20)]
    [InlineData(28)]
    public void ACarSentOffFromRestDrivesAwayInsteadOfBackingAwayForGood(int hz)
    {
        if (InstallDir is null) return;
        var s = T15();
        float dt = SimRate.FromLabel(hz).TickSeconds;
        var h = s.Script!.Host;
        var car = s.Entities.First(e => e.Name == "t15as01");
        var v = car.Vehicle!;
        // where T15 has it sit, open ground, then goto 400 ahead
        Put(s, v, 2412.5f, 45757.5f, 0);
        var path = new List<Vector3> { new(2412.5f, 0, 46157.5f) };
        for (int i = 0; i < (int)(1f / dt); i++) Frame(s, dt, () => h.Order(car, null, AiOrder.Sit, null, 0));
        Assert.True(s.World.Step.TractionLimit(v, 1f) < 0.1f, "the clearance reads under 0.1 at rest (the original's 0.078)");
        int backs = 0, prev = -1;
        var start = v.Pos.ToVector3();
        for (int i = 0; i < (int)(20f / dt); i++)
        {
            Frame(s, dt, () => h.Order(car, null, AiOrder.Goto, path, 30f));
            if (Top(car) == 18 && prev != 18) backs++;
            prev = Top(car);
        }
        float went = Vector3.Distance(start, v.Pos.ToVector3());
        log.WriteLine($"{hz} Hz: {backs} back_away entries, {went:0} from the start");
        Assert.Equal(0, backs);
        Assert.True(went > 150f, $"went only {went:0}");
    }

    [Theory]
    [InlineData(5f, 0f)]      // alongside, to its right
    [InlineData(-5f, 0f)]     // to its left
    [InlineData(0f, -8f)]     // behind
    public void AStoppedCarWithItsTargetAlongsideTurnsOnIt(float right, float ahead)
    {
        if (InstallDir is null) return;
        var s = T15();
        float dt = SimRate.R20.TickSeconds;
        var h = s.Script!.Host;
        // a Piranha, whose clearance at rest is over 0.1, so the boxed-in test stays out of it
        var v = VehicleFactory.Create(new AssetVfs(new GameInstall(InstallDir!)), "vppirna1.vcf");
        var car = new Entity("ai", [], 1, 2u << 16) { Vehicle = v };
        s.World.Vehicles.Add(v);
        h.Add(car);
        var me = h.Player!;
        // t15as01 sits on this spot: out of the way, so the two cars do not start inside each other
        Put(s, s.Entities.First(e => e.Name == "t15as01").Vehicle!, 2412.5f, 45857.5f, 0);
        Put(s, v, 2412.5f, 45757.5f, 0);
        void Park() { var p = v.Pos.ToVector3() + v.Rot.ToWorld(new Vector3(right, 0, ahead)); Put(s, me.Vehicle!, p.X, p.Z, 0); }
        Park();
        var start = v.Pos.ToVector3();
        float best = MathF.PI, sat = 0;
        for (int i = 0; i < (int)(1f / dt); i++) Frame(s, dt, () => h.Order(car, null, AiOrder.Sit, null, 0));
        Assert.True(s.World.Step.TractionLimit(v, 1f) >= 0.1f);
        Assert.Equal(0f, v.Speed);
        // already in dirt_gunner on this target, its deadline out of the way: the tactic whose Pursue held the brake
        car.Order = AiOrder.Attack; car.LastTarget = me; car.Stack.Clear(); car.Stack.Add(6); car.Depth = 0; car.TacticDeadline = 1e9f;
        for (int i = 0; i < (int)(15f / dt); i++)
        {
            Frame(s, dt, () => h.Order(car, me, AiOrder.Attack, null, 0));
            var d = me.Vehicle!.Pos.ToVector3() - v.Pos.ToVector3();
            float err = MathF.Abs(AiDriver.AngleDelta(AiDriver.Heading(v), MathF.Atan2(d.X, d.Z)));
            if (Top(car) == 6) best = MathF.Min(best, err);
            if (Top(car) == 6 && v.Speed < 0.5f && err > 0.4f) sat += dt;
        }
        float moved = Vector3.Distance(start, v.Pos.ToVector3());
        log.WriteLine($"target at ({right}, {ahead}): moved {moved:0.0}, best heading error {best:0.00}, sat {sat:0.0} s");
        // unfixed: 3 to 11 s of the 15 stopped, braking, with the target off to the side or behind
        Assert.True(sat < 1f, $"sat {sat:0.0} s stopped with its target off its nose");
        Assert.True(best < 0.4f, $"never faced its target (best {best:0.00} rad)");
    }
    /// <summary>
    /// #60: an AI car nosed up against a building, its target 60 beyond it, backs out and goes round. Nearly every
    /// building of the original (the houses, barns, warehouses, the fences, the wrecks) collides through class-0x2000
    /// node boxes and no polygon mesh, and the AI's sweeps (the blocked-in-gear sweep, the 50 look
    /// ahead) only tested meshes, so the car never saw the wall: it drove into it, bounced back (stop_sliding_backward),
    /// and drove into it again for the whole 40 s (before #42 a strong engine's low clearance sent it to back_away
    /// anyway). The original's sweeps go through the collider lists with a box test.
    /// </summary>
    [Theory]
    [InlineData("t15.msn", "akwreck2", false)]   // a wreck, T15's t15as01 (clearance under 0.1 at rest)
    [InlineData("t02.msn", "bhhouse1", true)]    // a house, a Piranha (clearance over 0.1 at rest)
    [InlineData("t02.msn", "bhihous1", true)]
    public void ACarNosedIntoABuildingBacksOutAndGoesRound(string msn, string building, bool piranha)
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var s = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", msn)!), asm, rand: new I76.Sim.Rng.CrtRand(1));
        float dt = SimRate.R20.TickSeconds;
        var h = s.Script!.Host;
        Entity car; Vehicle v;
        if (piranha)
        {
            v = VehicleFactory.Create(vfs, "vppirna1.vcf");
            car = new Entity("ai", [], 1, 2u << 16) { Vehicle = v };
            s.World.Vehicles.Add(v);
            h.Add(car);
        }
        else { car = s.Entities.First(e => e.Name == "t15as01"); v = car.Vehicle!; }
        var o = s.Statics.Objects.Where(x => x.Name == building).OrderBy(x => x.Radius).Last();
        Assert.Empty(o.Polys);
        var c = new Vector2(o.Centre.X, o.Centre.Z);
        var dir = Vector2.Normalize(new Vector2(1, 0.3f));
        float yaw = MathF.Atan2(dir.X, dir.Y);
        // the car's nose 0.4 from the wall, square on, the target parked 60 beyond the building on the same line
        var from = c - dir * (o.Radius + 25f);
        var p0 = new Vector3(from.X, s.Ground.BareHeight(from.X, from.Y) + 1f, from.Y);
        var wall = I76.Sim.Collision.StaticWorld.RayBoxes(o.Boxes, p0, new Vector3(dir.X, 0, dir.Y) * 100f, 1f);
        Assert.NotNull(wall);
        from += dir * (wall!.Value.T * 100f - v.BoxZ[3] - 0.4f);
        Put(s, v, from.X, from.Y, yaw);
        var me = h.Player!;
        var at = c + dir * (o.Radius + 60f);
        Put(s, me.Vehicle!, at.X, at.Y, yaw);
        int backs = 0, prev = -1;
        float farthest = float.MinValue;
        for (int i = 0; i < (int)(40f / dt); i++)
        {
            Frame(s, dt, () => h.Order(car, me, AiOrder.Attack, null, 0));
            if (Top(car) == 18 && prev != 18) backs++;
            prev = Top(car);
            farthest = MathF.Max(farthest, Vector2.Dot(new Vector2((float)v.Pos.X, (float)v.Pos.Z) - c, dir));
        }
        log.WriteLine($"{building}: {backs} back_away entries, got {farthest:0} past the building's centre");
        // unfixed: no back_away, never past the wall (-6 to -15), bouncing off it for the 40 s
        Assert.InRange(backs, 1, 4);
        Assert.True(farthest > 0, $"never got round the {building} ({farthest:0})");
    }

    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>
    /// back_away that has got nowhere goes to just do it (#42): the original's own test is terrain steps over 3.4 only,
    /// so a car wedged against a bank or an object reversed into it for the whole 4 s, again and again (T05's
    /// t05fp03 spent 181 s of 240 in reverse gear in a ditch, 14 back_aways). On flat open ground the original's test
    /// says no; the port's says yes once 1.5 s have gone by without the car once getting 1 from where it started.
    /// </summary>
    [Fact]
    public void BackAwayThatGetsNowhereDrivesForwardInstead()
    {
        var v = new Vehicle { Pos = new Double3(0, 0, 0), Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), Gear = -1f };
        var e = new Entity("ai", [], 1, 0) { Vehicle = v };
        var world = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
        world.Vehicles.Add(v);
        var host = new ScriptHost(world, new List<Entity> { e }, (Objectives)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        var pred = typeof(Ptable).GetMethod("BackAwayToJustDoIt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool JustDoIt() => (bool)pred.Invoke(host.Ptable, [e, null])!;
        host.Now = 10f;
        e.EscapeTimer = 10f + 4f - 1.0f;          // back_away entered 1 s ago (its clsnTime is enter + 4)
        e.BackFarthest = 0.3f;
        Assert.False(JustDoIt());                  // too soon to tell
        e.EscapeTimer = 10f + 4f - Ptable.BackStuckTime;
        Assert.True(JustDoIt());                   // 1.5 s, never 1 from the start: wedged
        e.BackFarthest = 2f;
        Assert.False(JustDoIt());                  // it has been backing off: the original's test alone, and it says no here
    }
    /// <summary>
    /// The avoid predicates' one-frame look-ahead (VehicleStep.Predict) leaves the car as it was, except its height above
    /// ground: the airborne forces write it into the car, and the original leaves it written.
    /// </summary>
    [Fact]
    public void TheLookAheadLeavesAnAirborneCarAsItWasButItsClearance()
    {
        if (InstallDir is null) return;
        var v = VehicleFactory.Create(new AssetVfs(new GameInstall(InstallDir!)), "vppirna1.vcf");
        var step = new VehicleStep(new Flat(), new I76.Sim.Rng.CrtRand(1));
        v.Pos = new Double3(0, 10, 0); v.Vel = new Vector3(0, 2, 20); v.Flags |= VehicleFlags.Airborne; v.HeightAboveGround = 10;
        var (p, _, _) = step.Predict(v, SimRate.R20.TickSeconds, false, false, 0, 1);
        Assert.True(p.Y > 10f);
        Assert.Equal(p.Y, v.HeightAboveGround, 4);
        Assert.Equal(10.0, v.Pos.Y);
        Assert.Equal(new Vector3(0, 2, 20), v.Vel);
    }
}
