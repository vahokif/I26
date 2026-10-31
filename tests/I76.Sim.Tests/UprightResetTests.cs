using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// RESET_UPRIGHT (the port's key, X): the car back on the bare terrain under it, upright, heading kept, at rest
/// (the original's old F12 trick). The placement on synthetic ground, then the key through a session.
/// </summary>
public class UprightResetTests
{
    /// <summary>The terrain: a plane tilted by <c>slope</c> along x, with a "deck" of structure surface above it where asked.</summary>
    private sealed class Ground(float slope = 0, float deck = 0) : IGround
    {
        private float Terrain(double x) => (float)(slope * x);
        public (float Height, Vector3 Normal) Query(double x, double y, double z) =>
            deck > 0 && System.Math.Abs(z) < 10 ? (Terrain(x) + deck, Vector3.UnitY) : (Terrain(x), BareNormal(x, z));
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public float BareHeight(double x, double z) => Terrain(x);
        public Vector3 BareNormal(double x, double z) => Vector3.Normalize(new Vector3(-slope, 1, 0));
    }

    private static Vehicle Tumbling(Rows rot, double y = 30)
    {
        var v = new Vehicle { Pos = new Double3(100, y, 40), Rot = rot, Vel = new Vector3(8, -12, 3), AngVel = new Vector3(2, 1, 3), Speed = 14, Steer = 0.5f, Throttle = 1 };
        v.Flags |= VehicleFlags.Airborne | VehicleFlags.Sliding;
        v.Health = new I76.Sim.Damage.VehicleDamage(v, false, false);
        return v;
    }

    private static Rows Rolled(float yaw, float roll) => Rows.Concat(Rows.AboutZ(roll), Rows.AboutY(yaw));

    [Fact]
    public void OnFlatGroundTheCarStandsUprightAtRestWithItsHeading()
    {
        var world = new SimWorld(new Ground(), new CrtRand(1));
        var v = Tumbling(Rolled(0.7f, 1.9f));
        var headingBefore = Vector3.Normalize(new Vector3(v.Rot.Forward.X, 0, v.Rot.Forward.Z));
        var health = v.Health;
        UprightReset.Apply(world, v);
        Assert.Equal((100.0, 0.0, 40.0), (v.Pos.X, v.Pos.Y, v.Pos.Z));
        Assert.True(v.Rot.Up.Y > 0.9999f, $"up {v.Rot.Up}");
        Assert.True(Vector3.Dot(v.Rot.Forward, headingBefore) > 0.9999f, "the heading is kept");
        Assert.Equal((Vector3.Zero, Vector3.Zero, 0f), (v.Vel, v.AngVel, v.Speed));
        Assert.Equal((0f, 0f), (v.Steer, v.Throttle));
        Assert.Equal(0, (int)(v.Flags & (VehicleFlags.Airborne | VehicleFlags.Sliding)));
        Assert.Same(health, v.Health);   // the damage is untouched
    }

    [Fact]
    public void OnASlopeTheCarLiesOnTheSlopeWithItsHeading()
    {
        var g = new Ground(slope: 0.4f);
        var world = new SimWorld(g, new CrtRand(1));
        var v = Tumbling(Rolled(2.2f, -2.5f));
        var heading = new Vector3(v.Rot.Forward.X, 0, v.Rot.Forward.Z);
        UprightReset.Apply(world, v);
        Assert.Equal(g.BareHeight(100, 40), (float)v.Pos.Y, 3);
        Assert.True(Vector3.Dot(v.Rot.Up, g.BareNormal(100, 40)) > 0.9999f, $"up {v.Rot.Up}");
        Assert.True(MathF.Abs(Vector3.Dot(v.Rot.Forward, v.Rot.Up)) < 1e-4f && MathF.Abs(Vector3.Dot(v.Rot.Right, v.Rot.Forward)) < 1e-4f, "an orthonormal basis");
        // the nose points the way it did, seen from above
        Assert.True(Vector3.Dot(Vector3.Normalize(new Vector3(v.Rot.Forward.X, 0, v.Rot.Forward.Z)), Vector3.Normalize(heading)) > 0.99f);
        Assert.True(v.GroundNormal.Y > 0.9f && v.HeightAboveGround == 0);
    }

    [Fact]
    public void WithTheNoseStraightUpTheHeadingIsTheWayTheRoofLeans()
    {
        var world = new SimWorld(new Ground(), new CrtRand(1));
        // nose up 90 degrees about the car's right row: forward (0,1,0), up (0,0,-1) when heading +z
        var v = Tumbling(Rows.AboutX(-MathF.PI / 2));
        Assert.True(v.Rot.Forward.Y > 0.99f);
        UprightReset.Apply(world, v);
        Assert.True(v.Rot.Up.Y > 0.9999f);
        Assert.True(v.Rot.Forward.Z > 0.99f, $"forward {v.Rot.Forward}: the car was heading +z");
    }

    /// <summary>A deck above the terrain (a bridge): the car goes to the terrain under it, not onto the deck.</summary>
    [Fact]
    public void OverABridgeTheCarGoesToTheTerrainNotTheDeck()
    {
        var world = new SimWorld(new Ground(deck: 8), new CrtRand(1));
        var v = Tumbling(Rolled(0, 3.0f), y: 12);
        UprightReset.Apply(world, v);
        Assert.Equal(0.0, v.Pos.Y);
    }

    [Fact]
    public void ADeadCarAHelicopterAndAnEmplacementAreNotReset()
    {
        var dead = Tumbling(Rows.Identity);
        dead.Flags |= VehicleFlags.Dead;
        Assert.False(UprightReset.Can(dead));
        Assert.False(UprightReset.Can(new Vehicle { Heli = new HeliState() }));
        Assert.False(UprightReset.Can(new Vehicle { Emplacement = new EmplacementState() }));
        Assert.True(UprightReset.Can(Tumbling(Rows.Identity)));
    }

    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (SimSession S, Vehicle P, MissionSetup Setup) T01()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t01.msn")!), asm);
        var s = new SimSession(setup);
        var p = setup.World.Player!;
        s.AddDriver(p);
        // the opening cut-scene (T01's) holds the car: play it out
        for (int i = 0; i < 4000 && (i < 5 || s.Cutscene); i++) s.Tick([default]);
        Assert.False(s.Cutscene);
        return (s, p, setup);
    }

    private static void Throw(Vehicle p)
    {
        p.Pos = new Double3(p.Pos.X, p.Pos.Y + 25, p.Pos.Z);
        p.Rot = Rolled(1.0f, 2.2f);
        p.Vel = new Vector3(6, -9, 2);
        p.AngVel = new Vector3(1, 2, 1);
        p.Flags |= VehicleFlags.Airborne;
    }

    private static readonly CarInput Press = new() { Commands = CarCommands.ResetUpright };

    /// <summary>Through a session in single player: thrown into the air tumbling, one press and it stands on the terrain, still.</summary>
    [Fact]
    public void TheKeyPutsASingleplayerCarOnTheTerrainUprightAndStill()
    {
        if (InstallDir is null) return;
        var (s, p, setup) = T01();
        for (int i = 0; i < 20; i++) s.Tick([default]);
        Throw(p);
        s.Tick([Press]);
        float ground = setup.World.Ground.BareHeight(p.Pos.X, p.Pos.Z);
        Assert.True(MathF.Abs((float)p.Pos.Y - ground) < 0.6f, $"{p.Pos.Y} on ground {ground}");
        Assert.True(p.Rot.Up.Y > 0.95f, $"up {p.Rot.Up}");
        Assert.True(p.Speed < 1.5f && (p.Flags & VehicleFlags.Airborne) == 0, $"speed {p.Speed} flags {p.Flags}");
        for (int i = 0; i < 40; i++) s.Tick([default]);   // and it stays
        Assert.True(p.Speed < 1f && p.Rot.Up.Y > 0.95f);
    }

    [Fact]
    public void NotWhileDeadNotInACutsceneNotWhereTheHostTurnedItOff()
    {
        if (InstallDir is null) return;
        // the host's switch
        var (s, p, _) = T01();
        for (int i = 0; i < 10; i++) s.Tick([default]);
        s.ResetUprightAllowed = false;
        Throw(p);
        int gen = p.NetGeneration;
        s.Tick([Press]);
        Assert.Equal(gen, p.NetGeneration);
        Assert.True(p.Rot.Up.Y < 0.5f, "the car is as thrown");
        s.ResetUprightAllowed = true;
        // a cut-scene: the script holds the car
        s.Script!.Host.View.Mirror(true, Vector3.Zero, null, Vector3.Zero, null, null);
        Assert.True(s.Cutscene);
        s.Tick([Press]);
        Assert.Equal(gen, p.NetGeneration);
        s.Script.Host.View.Mirror(false, Vector3.Zero, null, Vector3.Zero, null, null);
        // dead
        p.Flags |= VehicleFlags.Dead;
        s.Tick([Press]);
        Assert.Equal(gen, p.NetGeneration);
        p.Flags &= ~VehicleFlags.Dead;
        // and alive, outside a cut-scene, allowed: it works
        s.Tick([Press]);
        Assert.Equal(gen + 1, p.NetGeneration);
    }

    /// <summary>A recorded input is the key: the same pressed tick in two sessions gives the same car (replays, savestates).</summary>
    [Fact]
    public void TheResetIsDeterministicFromTheInputAlone()
    {
        if (InstallDir is null) return;
        Vector3 Run()
        {
            var (s, p, _) = T01();
            for (int i = 0; i < 20; i++) s.Tick([default]);
            Throw(p);
            s.Tick([Press]);
            for (int i = 0; i < 10; i++) s.Tick([default]);
            return p.Pos.ToVector3();
        }
        Assert.Equal(Run(), Run());
    }
}
