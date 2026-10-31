using System.Numerics;
using I76.Formats.Mission;
using I76.Sim.Ai;
using I76.Sim.Collision;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The October 2026 audit's vehicle findings, each checked in the real game.
/// </summary>
public class AuditPhysicsTests
{
    /// <summary>A plane y = sx·x + sz·z.</summary>
    private sealed class Slope(float sx, float sz) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) =>
            ((float)(sx * x + sz * z), Vector3.Normalize(new Vector3(-sx, 1, -sz)));
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Vehicle Car()
    {
        var v = new Vehicle();
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z);
            w.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f; v.P.RideOffset = 1.4f;
        return v;
    }

    private sealed class Damage : IStepDamage
    {
        public int FallKills;
        public void Timer(Vehicle v, float dt) { }
        public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) { }
        public void Landing(Vehicle v, Vector3 impactVel, Vector3 n) { }
        public void FallKill(Vehicle v) { FallKills++; v.Flags |= VehicleFlags.Dead; }
        public void Rough(Vehicle v, float dt) { }
        public void StaticContact(Vehicle v, StaticObject o, Contact rec) { }
    }

    /// <summary>
    /// P1: a dead car's throttle is −1 on every substep, whatever its driver writes. Measured: t01fc01
    /// destroyed on a 0.26 grade with +1 written into the vehicle's dead-state field every frame stayed within 0.15 of its spot for 27 s;
    /// t01js01 killed at 30 u/s braked from 15 to 0 in about 60 frames after landing. Alive and coasting, the
    /// same car rolls on down the slope.
    /// </summary>
    [Fact]
    public void ADeadCarBrakesToAStopOnASlopeAndStays()
    {
        Vector3 Run(bool dead, out float speed)
        {
            var g = new Slope(0, -0.26f);
            var step = new VehicleStep(g, new CrtRand());
            var v = Car();
            v.Pos = new Double3(0, 0, 0);
            v.Vel = new Vector3(0, -0.26f, 1) * 15f / MathF.Sqrt(1 + 0.26f * 0.26f);
            v.Speed = v.Vel.Length();
            if (dead) v.Flags |= VehicleFlags.Dead;
            for (int i = 0; i < 400; i++)
            {
                v.Throttle = dead ? 1 : 0;          // a held key (dead) or none (alive)
                step.Frame(v, 0.05f - 1e-4f);
                if (dead) Assert.Equal(-1f, v.Throttle);
            }
            speed = v.Speed;
            return v.Pos.ToVector3();
        }
        var stopped = Run(true, out float s1);
        Assert.True(s1 < 1e-3f, $"dead car still moving at {s1}");
        var held = Run(true, out _);
        Assert.Equal(stopped, held);
        var rolled = Run(false, out float s2);
        Assert.True(s2 > 5f && rolled.Z > stopped.Z + 50, $"alive car {rolled.Z} vs dead {stopped.Z}, speed {s2}");
    }

    /// <summary>
    /// P2: the forward-clearance measure and its sibling skip their limits for a far car or a helicopter.
    /// Measured: a car 1500 from the camera got its argument back on 1000 of 1000 calls, and 1.0 from the steering
    /// limit on 500 of 500; near cars got 0.25, 0.703 and 0.94-0.98.
    /// </summary>
    [Fact]
    public void AFarCarOrAHelicopterSkipsTheTractionAndSteeringLimits()
    {
        var g = new Slope(0, 0);
        var step = new VehicleStep(g, new CrtRand());
        var v = Car();
        foreach (var w in v.Wheels) w.Grip = 0.05f;     // little grip: the near limits bite
        v.Engine.Force = 300000f;
        v.Speed = 30; v.Vel = new Vector3(0, 0, 30);
        Assert.True(step.TractionLimit(v, 1f) < 0.9f);
        Assert.Equal(1f, step.TractionLimit(v, 1f, far: true));
        Assert.Equal(-1f, step.TractionLimit(v, -1f, far: true));
        Assert.True(AiDriver.SteerLimit(v, g) < 0.9f);
        Assert.Equal(1f, AiDriver.SteerLimit(v, g, far: true));
        v.Heli = new HeliState();
        Assert.Equal(1f, step.TractionLimit(v, 1f));
        Assert.Equal(1f, AiDriver.SteerLimit(v, g));
    }

    /// <summary>P5: Arcade Phys. rights a car resting on its roof only outside a network game.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACarOnItsRoofIsRightedOnlyOutsideANetworkGame(bool net)
    {
        var step = new VehicleStep(new Slope(0, 0), new CrtRand()) { NetSession = net };
        var v = Car();
        v.Flags |= VehicleFlags.LoadBasedCornering;
        v.Rot = new Rows(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1));
        v.Pos = new Double3(0, 1.4, 0);
        step.Step(v, 0.04f, true);
        Assert.Equal(net, step.Wrecked);
        Assert.Equal(!net, v.Rot.Up.Y > 0);
    }

    /// <summary>P6: an airborne car more than 35 below its last ground height when a contact bounces it is killed there.</summary>
    [Theory]
    [InlineData(100f, 1)]
    [InlineData(60f, 0)]
    public void ACarFallingPastItsLastGroundIsKilledMidBounce(float lastGround, int kills)
    {
        var dmg = new Damage();
        var step = new VehicleStep(new Slope(0, 0), new CrtRand()) { Damage = dmg };
        var v = Car();
        v.Flags |= VehicleFlags.Airborne;
        v.GroundHeight = lastGround;
        v.Pos = new Double3(0, 50, 0);
        v.Vel = new Vector3(0, -10, 5); v.Speed = v.Vel.Length();
        v.Rec = new Contact { Hit = true, Other = null, N = Vector3.Normalize(new Vector3(0, 0.3f, -1)), C = new Vector3(0.9f, 0.6f, 2f), RelVel = -v.Vel };
        step.Step(v, 0.04f, false);
        Assert.Equal(kills, dmg.FallKills);
    }

    /// <summary>
    /// P4: a mark's first substep starts it with no rand(), each later one draws one, and a wheel at its
    /// travel limit (or the mark off) ends it with none. Measured: 5 first calls with 0 draws, 14 later with 1, 6 at the
    /// limit with 0.
    /// </summary>
    [Fact]
    public void ASkidMarkDrawsNoRandOnItsFirstSubstepNorAtTheTravelLimit()
    {
        var rand = new CrtRand(7);
        var step = new VehicleStep(new Slope(0, 0), rand);
        var w = new Wheel();
        int Draws(Action a) { var s0 = rand.Seed; a(); var r = new CrtRand(s0); int n = 0; while (r.Seed != rand.Seed && n < 10) { r.Next(); n++; } return n; }
        Assert.Equal(0, Draws(() => step.Skid(w, true)));
        Assert.True(w.Marking);
        Assert.Equal(1, Draws(() => step.Skid(w, true)));
        Assert.Equal(1, Draws(() => step.Skid(w, true)));
        w.AtLimit = true;
        Assert.Equal(0, Draws(() => step.Skid(w, true)));
        Assert.False(w.Marking);
        w.AtLimit = false;
        Assert.Equal(0, Draws(() => step.Skid(w, true)));
        Assert.Equal(0, Draws(() => step.Skid(w, false)));
        Assert.False(w.Marking);
    }

    /// <summary>P3: the AI's look-ahead steps the force model into copies; the car itself is left alone.</summary>
    [Fact]
    public void TheLookAheadStepsTheForceModelAndLeavesTheCarAlone()
    {
        var step = new VehicleStep(new Slope(0, 0), new CrtRand());
        var v = Car();
        v.Vel = new Vector3(0, 0, 20); v.Speed = 20;
        var (pos, vel, rot, steer, thr) = (v.Pos, v.Vel, v.Rot, v.Steer, v.Throttle);
        var (p, nv, r) = step.Predict(v, 0.05f, far: false, isPlayer: false, steer: 0.5f, throttle: -1f);
        Assert.Equal((pos, vel, rot, steer, thr), (v.Pos, v.Vel, v.Rot, v.Steer, v.Throttle));
        Assert.True(nv.Length() < 20f, "the brake slows the prediction");
        Assert.True(p.Z > 0.5f && p.Z < 1.01f);
        Assert.True(MathF.Abs(r.Forward.X) > 1e-4f, "the steer turns it");
        var (fp, fv, _) = step.Predict(v, 0.05f, far: true, isPlayer: false, steer: 0f, throttle: 0f);
        Assert.Equal(20f * 0.05f, fp.Z, 2);
        Assert.Equal(20f, fv.Z, 1);
    }
}

public class AuditScriptTests
{
    private sealed class Flat(StaticWorld? w = null) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World { get; } = w ?? new StaticWorld();
    }

    private static Entity Car(string name, int team, Vector3 at, bool emplacement = false)
    {
        var v = new Vehicle { Pos = Double3.From(at) };
        if (emplacement) v.Emplacement = new EmplacementState();
        return new Entity(name, [], emplacement ? 8u : 1u, (uint)team << 16) { Vehicle = v };
    }

    private static Entity Placed(StaticWorld w, string name, uint type, Vector3 at)
    {
        var so = w.AddBox(name, type, at - new Vector3(1, 0, 1), at + new Vector3(1, 3, 1));
        var e = new Entity(name, [], type, 0) { StaticPosition = at, Static = so };
        so.Owner = e;
        return e;
    }

    private static ScriptHost Host(StaticWorld w, params Entity[] es)
    {
        var world = new SimWorld(new Flat(w), new CrtRand());
        foreach (var e in es) if (e.Vehicle is { } v) world.Vehicles.Add(v);
        world.Player = es[0].Vehicle;
        return new ScriptHost(world, new List<Entity>(es), new Objectives(new ObjectiveText([("a", false), ("b", false)], [])), _ => 1f);
    }

    /// <summary>
    /// F2 (nearestEnemy): the AI vehicle lists hold no gun emplacements and no structures.
    /// Measured in T11: with eight team-2 turrets within 550 of Groove (flm4turr at 112) success(2) fired on the first
    /// frame, while one team-2 car 300 away held it off; the lists held cars and helicopters only.
    /// </summary>
    [Fact]
    public void EnemyTestsSeeTheAiVehicleListsOnly()
    {
        var w = new StaticWorld();
        var groove = Car("groove", 1, new Vector3(0, 0, 0));
        var turret = Car("turret", 2, new Vector3(112, 0, 0), emplacement: true);
        var shack = Placed(w, "shack", 3, new Vector3(50, 0, 0));
        var h = Host(w, groove, turret, shack);
        Assert.DoesNotContain(turret, h.AiVehicles);
        Assert.DoesNotContain(shack, h.AiVehicles);
        var heli = Car("heli", 2, new Vector3(5000, 0, 0));
        heli.Vehicle!.Heli = new HeliState();
        h.Add(heli);
        Assert.Contains(heli, h.AiVehicles);
    }

    /// <summary>F3 (the structure test): types 2, 3, 4, 0xb, 0xc and 0x5c. Measured in S03: s03cg01's nearest was the sign swend_1 at 27.6.</summary>
    [Fact]
    public void NearestBuildingTakesSignsAndTrees()
    {
        var w = new StaticWorld();
        var car = Car("car", 2, new Vector3(0, 0, 0));
        var sign = Placed(w, "sign", 4, new Vector3(27, 0, 0));
        var tree = Placed(w, "tree", 3, new Vector3(40, 0, 0));
        var building = Placed(w, "blg", 2, new Vector3(60, 0, 0));
        var deck = Placed(w, "deck", 0xd, new Vector3(10, 0, 0));
        var h = Host(w, car, sign, tree, building, deck);
        Assert.Equal([sign, tree, building], h.Structures.Cast<Entity>());
    }

    /// <summary>F5: an objective already succeeded or failed stays as it is, and plays no second note.</summary>
    [Fact]
    public void ADoneObjectiveStaysDone()
    {
        var o = new Objectives(new ObjectiveText([("a", false), ("b", false)], []));
        int notes = 0;
        o.Sound += _ => notes++;
        o.Success(1); o.Success(1); o.Fail(1);
        Assert.Equal(Objectives.Succeeded, o.Flags[0]);
        Assert.Equal(1, notes);
        o.Fail(2); o.Success(2);
        Assert.Equal(Objectives.Failed, o.Flags[1]);
        Assert.Equal(1, notes);
    }

    /// <summary>F6: popCam brings back the placement saved by its push and clears camIsArrived.</summary>
    [Fact]
    public void PopCamRestoresTheOuterSceneAndClearsArrived()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        cam.Push();
        cam.Set("camPosDir", [0, 500, 0, 0, 0], [null, null, null, null, null], [new Vector3(10, 0, 20)]);
        cam.Update(0.05f);
        var outer = cam.Eye;
        cam.Push();
        var path = new List<Vector3> { new(0, 0, 0), new(0, 0, 1) };
        for (int i = 0; i < 30; i++) { cam.Set("camTransObj", [0, 300, 1000, 0], [null, null, null, null], path); cam.Update(0.05f); }
        Assert.NotEqual(outer, cam.Eye);
        cam.Pop();
        Assert.False(cam.ConsumeArrived());
        Assert.True(cam.Active);
        cam.Update(0.05f);
        Assert.Equal(outer, cam.Eye);
        cam.Pop();
        Assert.False(cam.Active);
    }

    /// <summary>F7: the view looks along b − a + (0, 2, 0) from a + h − dist along it, pitched down within 33.3.</summary>
    [Fact]
    public void CamF12FramesFromBehindA()
    {
        var cam = new ScriptCamera((x, z) => 0f);
        var (eye, look) = cam.F12(new Vector3(0, 0, 0), new Vector3(0, -2, 100), 10f, 3f);
        Assert.Equal(new Vector3(0, 3, -10), eye);
        Assert.True(look.Z > eye.Z && MathF.Abs(look.Y - eye.Y) < 1e-3f);
        var (eye2, look2) = cam.F12(new Vector3(0, 0, 0), new Vector3(0, -2, 20), 10f, 3f);
        Assert.True(eye2.Y > 3f && eye2.Z > -10f, $"orbited up: {eye2}");
        Assert.True(look2.Y < eye2.Y, "looks down");
    }

    /// <summary>F10: range checks, the reverse from the last entry, controlDone 1 by default.</summary>
    [Fact]
    public void ControlChecksItsRangesAndTakesReverseFromTheLastEntry()
    {
        var w = new StaticWorld();
        var car = Car("car", 1, Vector3.Zero);
        var h = Host(w, car);
        Assert.True(h.ControlDone(car));
        h.Control(car, 150, -50, 0, -1);
        Assert.False(h.ControlDone(car));
        h.Control(car, 50, 20, null, null);
        h.Control(car, 50, 20, 3, 0);
        var q = car.Controls.ToArray();
        float half = 50 * 0.01f, fifth = 20 * 0.01f;
        Assert.Equal((0f, -50 * 0.01f, -1f, 0), q[0]);
        Assert.Equal((half, fifth, -1f, 0), q[1]);   // ebrake and reverse from the entry before
        Assert.Equal((half, fifth, 1f, 0), q[2]);    // ebrake 3 → 0, reverse 0 → 1
        for (int i = 0; i < 3; i++) h.DriveControl(car);
        Assert.True(h.ControlDone(car));
    }

    /// <summary>F8: with nobody yet, whoAttacked still writes: the first actor whose handle is null, else a new one.</summary>
    [Fact]
    public void WhoAttackedWithNobodyWritesANullActor()
    {
        var host = new RecordingHost();
        string[] p = ["whoAttacked", "set"];
        (uint, uint)[] code =
        [
            (6, 2),                          // 0 allocLocals 2: local1 = actor 0, local2 the answer
            (4, 1), (4, 2), (13, 0),         // 1-3 whoAttacked(local1, local2)
            (5, unchecked((uint)-2)), (4, 2), (13, 1),   // 4-6 set(data[0], local2)
            (10, 7),                         // 7 yieldJump 7
        ];
        var prog = new FsmProgram([.. p], [("me", new byte[] { 1 })], [], [], [(0, 1, [0])], [5], code, true);
        var vm = new FsmVm(prog, host, new CrtRand());
        vm.Tick();
        Assert.Equal(1, vm.Data[0]);
        Assert.Null(vm.Actors[1].Handle);
    }

    /// <summary>F13: destroy also sets the dead-state field to −1.</summary>
    [Fact]
    public void DestroySetsTheBrake()
    {
        var w = new StaticWorld();
        var car = Car("car", 1, Vector3.Zero);
        var h = Host(w, car);
        h.Damage = new I76.Sim.Damage.DamageModel(h.World, new I76.Sim.Weapons.Combat(h.World, _ => null));
        car.Vehicle!.Throttle = 1;
        h.Destroy(car);
        Assert.Equal(-1f, car.Vehicle.Throttle);
    }

    /// <summary>F12 (Nitro): canSee is blocked by a placed object between the two, not by vehicles.</summary>
    [Fact]
    public void CanSeeIsBlockedByScenery()
    {
        var w = new StaticWorld();
        var a = Car("a", 1, Vector3.Zero);
        var b = Car("b", 2, new Vector3(0, 0, 50));
        var h = Host(w, a, b);
        Assert.True(h.CanSee(a, b));
        Placed(w, "wall", 2, new Vector3(0, 0, 25));
        Assert.False(h.CanSee(a, b));
    }
}

public class AuditPlannerTests
{
    private sealed class Strip : IGround
    {
        // off-road everywhere but a road along x = 100
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => System.Math.Abs(x - 100) < 6 ? 3 : 1;
        public Surface Surface(int id) => new(1f, 0.03f, 0, 0, 0);
    }

    /// <summary>The path is a chain of grid neighbours from the goal back to the start, with no node twice.</summary>
    [Fact]
    public void APathIsAChainOfNeighbours()
    {
        var s = PathSearch.Begin(new Vector2(0, 0), new Vector2(400, 900));
        int calls = 1;
        while (!s.Step(new Strip(), true) && calls < 50) calls++;
        var c = s.Chain();
        Assert.Equal((0, 0), (c[^1].X, c[^1].Z));
        Assert.Equal(c.Count, c.Select(n => (n.X, n.Z)).Distinct().Count());
        for (int i = 1; i < c.Count; i++)
            Assert.True(System.Math.Abs(c[i].X - c[i - 1].X) <= 10 && System.Math.Abs(c[i].Z - c[i - 1].Z) <= 10, $"step {i}");
        Assert.Contains(c, n => n.Road);
    }

    /// <summary>A search saved mid-way and restored carries on to the same path.</summary>
    [Fact]
    public void ASearchRestoredMidwayFindsTheSamePath()
    {
        var g = new Strip();
        var a = PathSearch.Begin(new Vector2(0, 0), new Vector2(400, 900));
        a.Step(g, true); a.Step(g, true);
        var w = new I76.Sim.Save.SaveWriter(new I76.Sim.Save.SaveRefs());
        a.Save(w);
        var b = PathSearch.Load(new I76.Sim.Save.SaveReader(w.ToArray(), new I76.Sim.Save.SaveRefs()), null);
        while (!a.Step(g, true)) { }
        while (!b.Step(g, true)) { }
        Assert.Equal(a.Chain(), b.Chain());
        Assert.Equal(a.Expansions, b.Expansions);
    }
}

public class AuditT11Tests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>
    /// T11's win (thread 37): after Groove reaches "westbank", success(2) and successAll once Taurus is within 200 and
    /// no enemy within 500. Measured in the game (original game): with the team-2 cars and helicopters
    /// held 5000 away, Groove at (4150, 47800), Taurus 36 off and eight live turrets within 550 (flm4turr at 112),
    /// success(2) fired on the first frame.
    /// </summary>
    [Fact]
    public void T11IsWonWithTurretsStandingNearTheMeetingPoint()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var install = new I76.Formats.Vfs.GameInstall(dir);
        var vfs = new I76.Formats.Vfs.AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new I76.Formats.Assembly.MissionWorld(vfs, asm, install.Resolve("miss16", "t11.msn")!), asm);
        var s = new Session.SimSession(setup);
        s.AddDriver(setup.World.Player!);
        var h = setup.Script!.Host;
        var vm = setup.Script.Vm!;
        Entity Actor(string label) => (Entity)vm.Actors.First(a => a.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).Handle!;
        var groove = h.Player!; var taurus = Actor("taurus");
        void Hold(Entity e, Vector3 at)
        {
            var v = e.Vehicle!;
            v.Pos = new Double3(at.X, setup.Ground.BareHeight(at.X, at.Z) + 0.5, at.Z);
            v.Vel = default; v.Speed = 0; v.AngVel = default;
        }
        var input = new Session.CarInput();
        for (int i = 0; i < 600 && (h.Objectives.Flags[0] & Objectives.Succeeded) == 0; i++)
        {
            Hold(groove, new Vector3(4690, 0, 47120));
            s.Tick([input]);
        }
        Assert.True((h.Objectives.Flags[0] & Objectives.Succeeded) != 0, "success(1) at westbank");
        var meet = new Vector3(4150, 0, 47800);
        var turrets = h.Entities.Where(e => e.Team == 2 && e.Vehicle is { IsEmplacement: true } && !e.Dead && Vector3.Distance(e.Position with { Y = 0 }, meet) < 550).ToList();
        log.WriteLine($"{turrets.Count} turrets within 550: {string.Join(", ", turrets.Select(t => $"{t.Name} {Vector3.Distance(t.Position with { Y = 0 }, meet):0}"))}");
        Assert.NotEmpty(turrets);
        int k = 0;
        for (; k < 100 && (h.Objectives.Flags[1] & Objectives.Succeeded) == 0; k++)
        {
            foreach (var e in h.Entities.Where(e => e.Team == 2 && e.Vehicle is { IsEmplacement: false }))
                Hold(e, new Vector3(9000, 0, 47800 + 20 * e.Name.Length));
            Hold(groove, meet);
            Hold(taurus, meet + new Vector3(36, 0, 0));
            s.Tick([input]);
        }
        Assert.True((h.Objectives.Flags[1] & Objectives.Succeeded) != 0, "success(2) with the turrets standing");
        Assert.All(turrets, t => Assert.False(t.Dead));
    }
}

public class AuditS03Tests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>
    /// F3: what S03's nearestBlg walks. Measured in the game (original game): S03's root collider lists
    /// hold, of the structure test's types, class 2 ×31, class 3 ×1, class 4 ×25 and class 11 ×3 (nine class-1 cars besides).
    /// </summary>
    [Fact]
    public void S03sBuildingsAreTheGamesStructureColliders()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var install = new I76.Formats.Vfs.GameInstall(dir);
        var vfs = new I76.Formats.Vfs.AssetVfs(install);
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new I76.Formats.Assembly.MissionWorld(vfs, asm, install.Resolve("miss16", "s03.msn")!), asm);
        var counts = setup.Script!.Host.Structures.Cast<Entity>().GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        log.WriteLine(string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"type {kv.Key}: {kv.Value}")));
        Assert.Equal(1, counts.GetValueOrDefault(3u));
        Assert.Equal(25, counts.GetValueOrDefault(4u));
        Assert.Equal(3, counts.GetValueOrDefault(11u));
        Assert.Equal(31, counts.GetValueOrDefault(2u));
    }
}
