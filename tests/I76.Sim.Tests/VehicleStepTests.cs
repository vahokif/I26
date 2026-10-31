using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

public class VehicleStepTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Flat(float grip = 2f) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(grip, 0.03f, 0, 0, 0);
    }

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
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        return v;
    }

    /// <summary>Flat ground with a 10-unit wall at z = 30 (a steep ramp over one unit).</summary>
    private sealed class Wall : IGround
    {
        public float H(double z) => z < 30 ? 0f : z > 31 ? 10f : (float)(z - 30) * 10f;
        public (float Height, Vector3 Normal) Query(double x, double y, double z) =>
            (H(z), z >= 30 && z <= 31 ? Vector3.Normalize(new Vector3(0, 1, -10)) : Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void BouncesOffATerrainWall()
    {
        var g = new Wall();
        var step = new VehicleStep(g, new CrtRand());
        var v = Car();
        v.Throttle = 1;
        bool hit = false;
        for (int i = 0; i < 200; i++)
        {
            step.Frame(v, 0.036f);
            if (v.Vel.Z < -0.5f) hit = true;
            if (hit) v.Throttle = 0;
        }
        Assert.True(hit, $"never bounced; z {v.Pos.Z}, vz {v.Vel.Z}");
        Assert.True(v.Pos.Z < 31, $"climbed the wall: z {v.Pos.Z}");
        Assert.True(v.Pos.Y < 1, $"y {v.Pos.Y}");
    }

    private sealed class FlatTerrain : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void BouncesOffAStaticObject()
    {
        var world = new I76.Sim.Collision.StaticWorld();
        world.AddBox("block", 3, new Vector3(-5, 0, 40), new Vector3(5, 4, 44));
        var g = new FlatWorld(world);
        var step = new VehicleStep(g, new CrtRand());
        var v = Car();
        v.Throttle = 1;
        bool hit = false;
        for (int i = 0; i < 250; i++)
        {
            step.Frame(v, 0.036f);
            if (step.ObjectHits.Count > 0) hit = true;
            if (hit) v.Throttle = 0;
        }
        Assert.True(hit, $"no contact; z {v.Pos.Z}");
        Assert.True(v.Pos.Z < 40, $"drove through the block: z {v.Pos.Z}");
    }

    [Fact]
    public void RamsAParkedCar()
    {
        var world = new SimWorld(new FlatTerrain(), new CrtRand());
        var a = Car(); var b = Car();
        foreach (var c in new[] { a, b }) { c.BoxX = [-0.9f, -0.5f, 0.5f, 0.9f]; c.BoxY = [0, 0.3f, 1f, 1.4f]; c.BoxZ = [-2.1f, -1f, 1f, 2.1f]; c.P.Mass = 1500; }
        b.Pos = new I76.Sim.Math.Double3(0, 0, 40);
        world.Vehicles.Add(a); world.Vehicles.Add(b); world.Player = a;
        a.Throttle = 1;
        for (int i = 0; i < 200; i++) world.Frame(0.036f);
        Assert.True(b.Pos.Z > 40.5, $"the parked car was not pushed: z {b.Pos.Z}");
        Assert.True(a.Pos.Z < b.Pos.Z - 3, $"cars overlap: {a.Pos.Z} vs {b.Pos.Z}");
    }

    [Fact]
    public void DrivesOntoARamp()
    {
        var world = new I76.Sim.Collision.StaticWorld();
        world.AddBox("deck", 0xb, new Vector3(-5, 0, 20), new Vector3(5, 1.0f, 60));
        var g = new FlatWorld(world);
        Assert.Equal(1.0f, g.Query(0, 1.5, 30).Height, 3);     // within ±3: the deck
        Assert.Equal(0f, g.Query(0, 5, 10).Height, 3);         // off the deck: terrain
    }

    [Fact]
    public void ASignIsKnockedOffAndTheCarDrivesOn()
    {
        var world = new I76.Sim.Collision.StaticWorld();
        var sign = world.AddBox("ssign", 4, new Vector3(-0.1f, 0, 19.9f), new Vector3(0.1f, 2.5f, 20.1f));
        sign.SphereRadius = 0.75f * 1.27f;
        var g = new FlatWorld(world);
        var sim = new SimWorld(g, new CrtRand(1));
        var car = Car();
        car.Vel = new Vector3(0, 0, 20); car.Speed = 20;
        sim.Vehicles.Add(car); sim.Player = car;
        var damage = new I76.Sim.Damage.DamageModel(sim, new I76.Sim.Weapons.Combat(sim, _ => null));
        for (int f = 0; f < 60; f++) sim.Frame(0.036f);
        Assert.True(sign.Destroyed);
        Assert.Contains(damage.Events, e => e is I76.Sim.Damage.SignKnocked);
        Assert.True(car.Pos.Z > 30, $"the car drove on: z {car.Pos.Z:0.0}, speed {car.Speed:0.0}");
    }

    /// <summary>
    /// A type-2 placement whose node is type 4, as Nitro p12's barrier (swend_1): its box across the road, 0.5 thick.
    /// Measured in the Nitro Pack: the car met the node at 25.46 u/s and kept that speed, and the
    /// sign slot threw the whole placement.
    /// </summary>
    private static I76.Sim.Collision.StaticObject Barrier(I76.Sim.Collision.StaticWorld world)
    {
        var o = new I76.Sim.Collision.StaticObject { Name = "swend_1", Type = 2, Centre = new Vector3(0, 1, 20), Radius = 3f, BoundRadius = 3f };
        o.Nodes.Add(new I76.Sim.Collision.StructureNode { Name = "SW1ENDS1", Type = 4, Hp = 5, MaxHp = 5 });
        o.Boxes.Add(new I76.Sim.Collision.NodeBox(Rows.Identity, new Vector3(0, 0, 20), new Vector3(-3, 0.5f, -0.25f), new Vector3(3, 1.5f, 0.25f), 3.2f) { Node = 0 });
        world.Objects.Add(o);
        return o;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // another model moves the car (modern physics): its sign pass still meets the node
    public void AStructuresSignNodeIsKnockedOffAndTheCarDrivesThrough(bool external)
    {
        var world = new I76.Sim.Collision.StaticWorld();
        var barrier = Barrier(world);
        var sim = new SimWorld(new FlatWorld(world), new CrtRand(1));
        if (external)
        {
            sim.External = _ => true;
            sim.ExternalFrame = (v, dt) => v.Pos = new Double3(v.Pos.X, v.Pos.Y, v.Pos.Z + v.Vel.Z * dt);
        }
        var car = Car();
        car.Vel = new Vector3(0, 0, 20); car.Speed = 20;
        sim.Vehicles.Add(car); sim.Player = car;
        var damage = new I76.Sim.Damage.DamageModel(sim, new I76.Sim.Weapons.Combat(sim, _ => null));
        float before = 0;
        for (int f = 0; f < 60 && !barrier.Destroyed; f++) { before = car.Speed; sim.Frame(0.036f); }
        Assert.True(barrier.Destroyed, $"knocked off: z {car.Pos.Z:0.0}");
        Assert.False(barrier.Solid);
        Assert.Contains(damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == barrier);
        Assert.DoesNotContain(damage.Events, e => e is I76.Sim.Damage.StructureDestroyed);
        // The sweep skips a type-4 node: no response
        Assert.True(car.Speed > before - 1f, $"no bounce: {before:0.0} -> {car.Speed:0.0}");
        if (external) return;
        for (int f = 0; f < 30; f++) sim.Frame(0.036f);
        Assert.True(car.Pos.Z > 30, $"the car drove through: z {car.Pos.Z:0.0}, speed {car.Speed:0.0}");
    }

    [Fact]
    public void ASignNodeFallsToExplosivesAndRamsButNotToRounds()
    {
        var world = new I76.Sim.Collision.StaticWorld();
        var barrier = Barrier(world);
        var sim = new SimWorld(new FlatWorld(world), new CrtRand(1));
        var damage = new I76.Sim.Damage.DamageModel(sim, new I76.Sim.Weapons.Combat(sim, _ => null));
        // the sign slot takes masks 0xa only (measured: a 50-point ballistic packet left swend_1 standing)
        var round = new I76.Sim.Weapons.DamagePacket { Mask = 1 }; round.Ch[0] = 50;
        damage.Apply(barrier, round, Vector3.Zero, 0);
        var fire = new I76.Sim.Weapons.DamagePacket { Mask = 4 }; fire.Ch[2] = 50;
        damage.Apply(barrier, fire, Vector3.Zero, 0);
        Assert.False(barrier.Destroyed);
        Assert.Equal(5, barrier.Nodes[0].Hp);
        // a splash reaches the root, a type-2 structure's 400
        var splash = new I76.Sim.Weapons.DamagePacket { Mask = 2 }; splash.Ch[1] = 30;
        damage.Apply(barrier, splash, Vector3.Zero, -1);
        Assert.Equal((370, false), (barrier.Hp, barrier.Destroyed));
        var rocket = new I76.Sim.Weapons.DamagePacket { Mask = 2 }; rocket.Ch[1] = 1;
        damage.Apply(barrier, rocket, Vector3.Zero, 0);
        Assert.True(barrier.Destroyed);
        Assert.True(barrier.Nodes[0].Destroyed);
        Assert.Single(damage.Events.OfType<I76.Sim.Damage.SignKnocked>());
    }

    /// <summary>A saguaro: a type-3 placement whose node is class 0x2000, a thin box dead ahead between the car's corners.</summary>
    [Fact]
    public void ACactusTrunkStopsTheCarOnceAndFalls()
    {
        var world = new I76.Sim.Collision.StaticWorld();
        var cactus = new I76.Sim.Collision.StaticObject { Name = "nsaguar1", Type = 3, Centre = new Vector3(0, 2, 20), Radius = 0.9f, Hp = 5, MaxHp = 5 };
        cactus.Boxes.Add(new I76.Sim.Collision.NodeBox(Rows.Identity, new Vector3(0, 0, 20), new Vector3(-0.3f, 0, -0.3f), new Vector3(0.3f, 4f, 0.3f), 2.1f));
        world.Objects.Add(cactus);
        var sim = new SimWorld(new FlatWorld(world), new CrtRand(1));
        var car = Car();
        car.Vel = new Vector3(0, 0, 20); car.Speed = 20;
        sim.Vehicles.Add(car); sim.Player = car;
        var damage = new I76.Sim.Damage.DamageModel(sim, new I76.Sim.Weapons.Combat(sim, _ => null));
        bool bounced = false;
        for (int f = 0; f < 60 && !bounced; f++)
        {
            sim.Frame(0.036f);
            bounced = sim.Step.ObjectHits.Any(h => h.Name == "nsaguar1");
        }
        Assert.True(bounced, $"the car hit the trunk: z {car.Pos.Z:0.0}");
        Assert.True(car.Pos.Z < 20, $"stopped short of it: z {car.Pos.Z:0.0}");
        Assert.True(cactus.Destroyed, $"5 HP is one hit: hp {cactus.Hp}");
    }

    /// <summary>Flat, a 20° ramp from z 20 to 30, then a drop back to flat.</summary>
    private sealed class Ramp : IGround
    {
        static readonly float Tan = MathF.Tan(20 * MathF.PI / 180);
        public (float Height, Vector3 Normal) Query(double x, double y, double z) =>
            z > 20 && z < 30 ? ((float)(z - 20) * Tan, Vector3.Normalize(new Vector3(0, 1, -Tan))) : (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    public static Action<string>? Log;

    [Fact]
    public void OffARampTheCarFliesLandsAndSettles()
    {
        Log = output.WriteLine;
        var step = new VehicleStep(new Ramp(), new CrtRand(1));
        var v = Car();
        v.Vel = new Vector3(0, 0, 25); v.Speed = 25; v.Throttle = 0.3f;
        bool flew = false; int landedAt = -1;
        float before = 0, after = 0;
        for (int f = 0; f < 150; f++)
        {
            float s0 = v.Speed;
            step.Frame(v, 0.036f);
            if ((v.Flags & VehicleFlags.Airborne) != 0) { if (!flew) before = s0; flew = true; }
            else if (flew && landedAt < 0) landedAt = f;
            if (landedAt >= 0 && f == landedAt + 5) after = v.Speed;
            if (flew && (landedAt < 0 || f <= landedAt + 6)) Log?.Invoke($"{f} y {v.Pos.Y:0.00} z {v.Pos.Z:0.0} v {v.Vel} speed {v.Speed:0.0} flags 0x{(uint)v.Flags:x} up {v.Rot.Up} w {v.AngVel} rec {v.Rec.Hit}");
        }
        Assert.True(flew, "left the ramp");
        Assert.True(landedAt > 0, "came down");
        Assert.Equal((VehicleFlags)0, v.Flags & (VehicleFlags.JustLanded | VehicleFlags.Airborne));
        Assert.True(v.Rot.Up.Y > 0.99f, $"settled upright: up {v.Rot.Up}");
        Assert.InRange(v.Pos.Y, -0.15, 0.6);    // the origin rests at about ground − travel
        // Speed through the landing is not asserted: off a sheer drop the crest's nose-down rate (kept in
        // flight when faster than -0.157) tips the car far enough to land nose first.
        output.WriteLine($"speed {before:0.0} -> {after:0.0}");
    }

    /// <summary>Flat terrain with a static world on it.</summary>
    private sealed class FlatWorld(I76.Sim.Collision.StaticWorld w) : IGround, I76.Sim.Collision.IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z)
        {
            foreach (var o in w.Objects)
                foreach (var p in o.Surfaces)
                    if (I76.Sim.Collision.StaticWorld.InsideXZ(p, (float)x, (float)z))
                    {
                        float hp = -(p.N.X * (float)x + p.N.Z * (float)z + p.D) / p.N.Y;
                        if (hp > y - 3 && hp < y + 3) return (hp, p.N);
                    }
            return (0, Vector3.UnitY);
        }
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public I76.Sim.Collision.StaticWorld World => w;
    }

    [Theory]
    [InlineData(0f, 0.15f)]    // uphill ahead
    [InlineData(0f, -0.15f)]   // downhill ahead
    [InlineData(0.15f, 0f)]    // rising to the right
    [InlineData(-0.15f, 0f)]   // rising to the left
    public void SettlesOntoASlope(float sx, float sz)
    {
        var g = new Slope(sx, sz);
        var step = new VehicleStep(g, new CrtRand());
        var v = Car();
        v.Throttle = -1;   // hold the brakes so it does not roll away
        for (int i = 0; i < 120; i++) step.Frame(v, 0.036f);
        var n = Vector3.Normalize(new Vector3(-sx, 1, -sz));
        float dot = Vector3.Dot(v.Rot.Up, n);
        Assert.True(dot > 0.995f, $"up {v.Rot.Up} vs slope normal {n}: dot {dot}");
        Assert.InRange(v.Pos.Y - g.Query(v.Pos.X, 0, v.Pos.Z).Height, -0.2, 0.2);
    }

    [Fact]
    public void SubstepsSplitLikeTheEngine()
    {
        Assert.Equal((1, 0.036f), VehicleStep.Substeps(0.036f));
        Assert.Equal(2, VehicleStep.Substeps(0.06f).Count);
        Assert.Equal(20, VehicleStep.Substeps(5f).Count);
    }

    [Fact]
    public void ThrottleAcceleratesAlongForwardAndBrakeStops()
    {
        var step = new VehicleStep(new Flat(), new CrtRand());
        var v = Car();
        v.Throttle = 1;
        for (int i = 0; i < 140; i++) step.Frame(v, 0.036f);
        Assert.True(v.Speed > 5, $"speed {v.Speed}");
        Assert.True(v.Pos.Z > 10, $"z {v.Pos.Z}");
        Assert.InRange(v.Pos.X, -0.01, 0.01);
        v.Throttle = -1;
        for (int i = 0; i < 300 && v.Speed > 0; i++) step.Frame(v, 0.036f);
        Assert.Equal(0, v.Speed);
    }

    [Fact]
    public void AFarCarIsLevelledAndKeepsItsHeight()
    {
        // A car that goes far while climbing moves one frame along its pitched forward row,
        // then is levelled to the forced (0, 1, 0) normal, so it does not climb on into the sky
        var step = new VehicleStep(new Flat(), new CrtRand());
        var v = Car();
        float a = 15 * MathF.PI / 180;
        v.Rot = new Rows(Vector3.UnitX, new Vector3(0, MathF.Cos(a), -MathF.Sin(a)), new Vector3(0, MathF.Sin(a), MathF.Cos(a)));
        v.Speed = 20; v.Vel = v.Rot.Forward * 20; v.Throttle = 1; v.DriveState = 3;
        step.CheapFrame(v, 0.036f, isPlayer: false);
        Assert.InRange(v.Rot.Forward.Y, -1e-5f, 1e-5f);
        Assert.InRange(v.Rot.Up.Y, 0.9999f, 1.0001f);
        double y = v.Pos.Y, z = v.Pos.Z;
        for (int i = 0; i < 100; i++) step.CheapFrame(v, 0.036f, isPlayer: false);
        Assert.InRange(v.Pos.Y, y - 1e-4, y + 1e-4);
        Assert.True(v.Pos.Z > z + 50, $"z {v.Pos.Z}");
    }

    [Fact]
    public void SteeringTurnsTheHeading()
    {
        var step = new VehicleStep(new Flat(), new CrtRand());
        var v = Car();
        v.Throttle = 1;
        for (int i = 0; i < 80; i++) step.Frame(v, 0.036f);
        var before = v.Rot.Forward;
        v.Steer = 1;
        for (int i = 0; i < 30; i++) step.Frame(v, 0.036f);
        Assert.True(Vector3.Dot(before, v.Rot.Forward) < 0.99f);
        Assert.False(float.IsNaN(v.Rot.Forward.X));
        Assert.InRange(v.Rot.Up.Y, 0.99f, 1.01f);
    }

    /// <summary>
    /// An oil slick's effect (flag 0x400 and the timer) zeroes the grip
    /// of any surface gripping above 0.2: a steered car keeps
    /// sliding the way it went. On a surface of 0.2 or less the oil changes nothing.
    /// </summary>
    [Theory]
    [InlineData(2f, true)]
    [InlineData(0.2f, false)]
    public void OilTakesTheGripOfAGrippySurface(float grip, bool slides)
    {
        float Turn(bool oiled)
        {
            var step = new VehicleStep(new Flat(grip), new CrtRand());
            var v = Car();
            v.Throttle = 1;
            for (int i = 0; i < 80; i++) step.Frame(v, 0.036f);
            var before = Vector3.Normalize(new Vector3(v.Vel.X, 0, v.Vel.Z));
            if (oiled) I76.Sim.Weapons.Combat.Oil(v);
            Assert.Equal(oiled, (v.Flags & VehicleFlags.NoGripOnLowSurface) != 0);
            v.Steer = 1;
            for (int i = 0; i < 40; i++) step.Frame(v, 0.036f);
            var after = Vector3.Normalize(new Vector3(v.Vel.X, 0, v.Vel.Z));
            return MathF.Acos(System.Math.Clamp(Vector3.Dot(before, after), -1f, 1f));
        }
        float clean = Turn(false), oiled = Turn(true);
        output.WriteLine($"grip {grip}: course change clean {clean * 57.3f:0.0} deg, oiled {oiled * 57.3f:0.0} deg");
        if (slides) Assert.True(oiled < clean * 0.25f, $"oiled {oiled} clean {clean}");
        else Assert.Equal(clean, oiled, 4);
    }
}

public class AiDriverTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void DrivesToAPointBehindIt()
    {
        var v = new Vehicle();
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        { w.Grip = 1f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z); w.InitSuspension(); }
        v.P.Mass = 1600; v.P.DriveScale = 1f / 1600; v.P.BrakeStrength = 2300f / 1600; v.P.SteerGain = 0.19f; v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        v.Engine.PeakForce = 190000; v.Engine.Curve = 190000 * 8.1632656e-8f;
        v.Flags |= VehicleFlags.CommandedYaw;
        var world = new SimWorld(new Flat(), new CrtRand());
        world.Vehicles.Add(v);
        var target = new Vector3(60, 0, -60);
        world.Ai[v] = (new I76.Sim.Ai.AiBlock { CommandSpeed = 20 }, _ => target);
        float closest = float.MaxValue;
        for (int i = 0; i < 600; i++)
        {
            world.Frame(0.036f);
            closest = MathF.Min(closest, Vector2.Distance(new Vector2((float)v.Pos.X, (float)v.Pos.Z), new Vector2(target.X, target.Z)));
        }
        Assert.True(closest < 8, $"never got near the target: closest {closest}, at {v.Pos}");
    }
}
