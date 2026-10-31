using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Weapons;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The buildings that fall into ramps (the SDFs the stock maps place as drive-on structures, type 0xb or 0xc, with an 'A'
/// twin that slopes): once the placement is destroyed (the script's destroy-object action's way, the whole of it) a car driven along the
/// remains' slope climbs it, as the destroyed collider's polygons say. The grain hopper's fallen bin (A01), the water
/// tower (M05), the lean-to (M05) and the hut (M06).
/// </summary>
public class DestroyedRampTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static bool Steep(Poly p) => p.N.Y is > 0.3f and < 0.97f;

    /// <summary>
    /// Destroys <paramref name="name"/>, puts the player's car 40 m below the remains' highest sloping polygon facing up its
    /// slope, drives at 15 u/s and returns the highest the car got over the bare terrain, with the slope's own height over it.
    /// </summary>
    private static (float Climbed, float RampTop, StaticObject Object) Climb(string mission, string name, bool destroy, bool shoot = false, bool fire = false)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", mission)!), asm, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup);
        var player = setup.World.Player!;
        s.AddDriver(player);
        var o = setup.Statics.Objects.First(x => x.Name == name && x.Remains is { } r && r.Surfaces.Any(Steep));
        var top = o.Remains!.Surfaces.Where(Steep).OrderByDescending(p => p.V.Max(v => v.Y)).First();
        // the intro's script holds the car and T16 cranks its engine meanwhile (its enemies open fire at 24 s, so it waits less): skip it and wait before moving the car
        var hp = player.Health!;
        for (int i = 0; i < 20 * (mission == "t16.msn" ? 12 : 30); i++) { Array.Copy(hp.ArmourMax, hp.Armour, 4); Array.Copy(hp.ChassisMax, hp.Chassis, 4); s.Tick([new CarInput { Commands = CarCommands.Skip }]); }
        if (destroy) setup.Damage.DestroyStructure(o);
        if (shoot) ShootApart(setup, o);
        if (fire) FireAt(setup, s, o);
        var up = Vector2.Normalize(new Vector2(-top.N.X, -top.N.Z));
        var centre = new Vector2(top.V.Average(v => v.X), top.V.Average(v => v.Z));
        var from = centre - up * 40;
        var fwd = new Vector3(up.X, 0, up.Y);
        player.Pos = new Double3(from.X, setup.Ground.BareHeight(from.X, from.Y) + 0.5f, from.Y);
        player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        player.Vel = default;
        // other cars parked on the run-up (T16's hidden tanks) are moved off it: the test drives the slope, not them
        foreach (var v in setup.World.Vehicles)
            if (v != player && new Vector2((float)v.Pos.X - from.X, (float)v.Pos.Z - from.Y).Length() < 60) v.Pos = new Double3(v.Pos.X + 100, v.Pos.Y, v.Pos.Z);
        float climbed = 0;
        for (int i = 0; i < 20 * 8; i++)
        {
            Array.Copy(hp.ArmourMax, hp.Armour, 4); Array.Copy(hp.ChassisMax, hp.Chassis, 4);
            float err = MathF.Atan2(up.X, up.Y) - MathF.Atan2(player.Rot.Forward.X, player.Rot.Forward.Z);
            while (err > MathF.PI) err -= 2 * MathF.PI;
            while (err < -MathF.PI) err += 2 * MathF.PI;
            s.Tick([new CarInput
            {
                Buttons = CarButtons.Analog,
                Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767),
                Throttle = (short)(System.Math.Clamp((15 - player.Speed) * 0.3f, -1f, 1f) * 32767),
            }]);
            var p = player.Pos.ToVector3();
            climbed = MathF.Max(climbed, p.Y - setup.Ground.BareHeight(p.X, p.Z));
        }
        return (climbed, top.V.Max(v => v.Y) - setup.Ground.BareHeight(centre.X, centre.Y), o);
    }

    /// <summary>The player's own guns: the car 25 m off the structure's centre facing it, fire held for 6 s.</summary>
    private static void FireAt(MissionSetup setup, SimSession s, StaticObject o)
    {
        var player = setup.World.Player!;
        var hp = player.Health!;
        var c = new Vector2(o.Centre.X, o.Centre.Z - 25);
        var fwd = Vector3.UnitZ;
        for (int i = 0; i < 20 * 6; i++)
        {
            Array.Copy(hp.ArmourMax, hp.Armour, 4); Array.Copy(hp.ChassisMax, hp.Chassis, 4);
            player.Pos = new Double3(c.X, setup.Ground.BareHeight(c.X, c.Y) + 0.5f, c.Y);
            player.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
            player.Vel = default;
            s.Tick([new CarInput { Buttons = CarButtons.Fire | CarButtons.Hardpoint1 | CarButtons.Hardpoint2 }]);
        }
    }

    /// <summary>Shoots the structure the player's way: 50cal rounds (10 a channel) on each node the sweep can name, until every node is dead.</summary>
    private static void ShootApart(MissionSetup setup, StaticObject o)
    {
        for (int pass = 0; pass < 200 && o.Nodes.Any(n => !n.Destroyed && n.Damageable); pass++)
            for (int i = 0; i < o.Nodes.Count; i++)
            {
                if (o.Nodes[i].Destroyed || !o.Nodes[i].Damageable) continue;
                var p = new DamagePacket { Mask = 8 }; p.Ch[3] = 50;
                setup.Damage.StructureSlot(o, i, p);
            }
    }

    [Theory]
    [InlineData("a01.msn", "bcrhopr1")]     // the grain hopper's fallen bin
    [InlineData("m05.msn", "awatert1")]     // the water tower
    [InlineData("m05.msn", "aleanto1")]     // the lean-to
    [InlineData("m06.msn", "ahut1")]        // the hut
    [InlineData("t16.msn", "awatert1")]     // T16's water tower, the jump
    public void ADestroyedBuildingIsARampTheCarClimbs(string mission, string name)
    {
        if (InstallDir is null) return;
        var (climbed, rampTop, o) = Climb(mission, name, destroy: true);
        Assert.True(o.Solid);
        Assert.True(climbed >= rampTop, $"{name}: the car rose {climbed:0.0} m over the terrain, the remains' slope tops {rampTop:0.0}");
    }

    /// <summary>The hopper standing is no ramp: the same run, undestroyed, never leaves the ground.</summary>
    /// <summary>
    /// The player's way (the car's own guns, held on the base for 6 s) T16's and M05's water towers fall to a ramp that
    /// carries the car clear of the ground. Measured in the original (M05, the same sdf: two 50cal hits kill AW1_BAS1 and
    /// its subtree, the root's 400 untouched, and a car at 18 u/s leaves the slope at 2.8 m over the terrain).
    /// </summary>
    [Theory]
    [InlineData("m05.msn")]
    [InlineData("t16.msn")]
    public void ATowerShotAtItsBaseIsARampTheCarJumpsOff(string mission)
    {
        if (InstallDir is null) return;
        var (climbed, rampTop, o) = Climb(mission, "awatert1", destroy: false, fire: true);
        Assert.True(o.Destroyed);
        Assert.True(climbed >= rampTop + 1f, $"{mission}: the car rose {climbed:0.0} m over the terrain, the slope tops {rampTop:0.0}");
    }

    /// <summary>
    /// The tank on top is a child of the base, not of the root (the root test fires for a direct child only): shot alone
    /// it dies and the base stands, so there is no ramp. The same in the original (ROOTTEST AW1_TNK1, parent AW1_BAS1).
    /// </summary>
    [Fact]
    public void TheTowersTopAloneLeavesTheBaseStanding()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t16.msn")!), asm, rand: new I76.Sim.Rng.CrtRand(1));
        var o = setup.Statics.Objects.First(x => x.Name == "awatert1");
        int tank = o.Nodes.FindIndex(n => n.Name.Equals("AW1_TNK1", StringComparison.OrdinalIgnoreCase));
        for (int k = 0; k < 10; k++) { var p = new DamagePacket { Mask = 8 }; p.Ch[3] = 50; setup.Damage.StructureSlot(o, tank, p); }
        Assert.True(o.Nodes[tank].Destroyed);
        Assert.False(o.Destroyed);
        Assert.Contains(o.Nodes, n => !n.Destroyed);
    }

    [Fact]
    public void TheStandingHopperIsNoRamp()
    {
        if (InstallDir is null) return;
        var (climbed, _, o) = Climb("a01.msn", "bcrhopr1", destroy: false);
        Assert.False(o.Destroyed);
        Assert.True(climbed < 1f, $"the car rose {climbed:0.0} m");
    }

    /// <summary>Destroyed the player's way (rounds node by node, no whole destroy) the remains are the same ramp.</summary>
    [Theory]
    [InlineData("a01.msn", "bcrhopr1")]
    [InlineData("m05.msn", "awatert1")]
    [InlineData("t16.msn", "awatert1")]
    public void AShotApartBuildingIsARampTheCarClimbs(string mission, string name)
    {
        if (InstallDir is null) return;
        var (climbed, rampTop, o) = Climb(mission, name, destroy: false, shoot: true);
        Assert.True(o.Nodes.All(n => n.Destroyed || !n.Damageable));
        Assert.True(climbed >= rampTop, $"{name}: shot apart, the car rose {climbed:0.0} m over the terrain, the remains' slope tops {rampTop:0.0}");
    }
}
