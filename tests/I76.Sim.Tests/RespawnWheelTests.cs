using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #167: after a respawn in a network melee the tanker's back tyres were still blown. A six-wheeler's middle pair
/// (the tanker's second rear axle) was left out of the fresh car's wheels; only the four corners were renewed.
/// </summary>
public class RespawnWheelTests
{
    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>A six-wheeler as built: every wheel whole, grip 2, rest 0.5.</summary>
    private static Vehicle SixWheeler()
    {
        var v = new Vehicle { MiddleRight = new Wheel(), MiddleLeft = new Wheel() };
        for (int k = 0; k < 6; k++) { var w = v.WheelAt(k)!; w.Grip = 2f; w.Rolling = 0.01f; w.Rest = 0.5f; w.Offset = new Vector3(k % 2 == 0 ? 1 : -1, 0.5f, 2 - k); }
        return v;
    }

    /// <summary>Every tyre blown, as a death leaves them: grip gone, the rim's lower rest.</summary>
    private static void BlowAll(Vehicle v)
    {
        for (int k = 0; k < 6; k++) { var w = v.WheelAt(k)!; w.Blown = true; w.Grip = 0.5f; w.Rest = 0.3f; }
    }

    /// <summary>A melee on flat ground; single player with its player's car, a network game with none yet.</summary>
    private static (MeleeGame Game, ScriptHost Host) Arena(bool network)
    {
        var world = new SimWorld(new Flat(), new CrtRand(5));
        List<Entity> entities = [];
        if (!network)
        {
            var car = SixWheeler();
            world.Vehicles.Add(car);
            world.Player = car;
            entities.Add(new Entity("plyr", [], 1, 0) { Vehicle = car });
        }
        var host = new ScriptHost(world, entities, (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => 1f);
        var game = new MeleeGame
        {
            World = world, Host = host, Rand = world.Rand, Roster = [], PlayerCar = "tanker.vcf",
            Build = (_, _) => SixWheeler(), Rules = new MeleeRules { Network = network },
        };
        game.Spawns.Add(new MeleeGame.Spot(Rows.Identity, new Double3(0, 0, 0)));
        game.Spawns.Add(new MeleeGame.Spot(Rows.Identity, new Double3(200, 0, 0)));
        game.Regens.Add(new MeleeGame.Spot(Rows.Identity, new Double3(500, 0, 0)));
        return (game, host);
    }

    private static void AssertWhole(Vehicle v)
    {
        for (int k = 0; k < 6; k++)
        {
            var w = v.WheelAt(k)!;
            Assert.False(w.Blown, $"wheel {k} still blown");
            Assert.Equal((2f, 0.5f), (w.Grip, w.Rest));
        }
    }

    [Fact]
    public void ANetworkRespawnRenewsAllSixWheels()
    {
        var (game, host) = Arena(network: true);
        game.Start();
        var p = game.AddPlayer("tanker.vcf", "p0");
        BlowAll(p.Car);
        p.Car.Flags |= VehicleFlags.Dead;
        game.Frame(0.05f);
        Assert.True(p.Destroyed);
        host.Now = 10f;
        Assert.Null(game.RequestRespawn(p));
        game.Frame(0.05f);
        Assert.False(p.Destroyed);
        AssertWhole(p.Car);
    }

    /// <summary>A regen pad: a network game's renews the whole car, single player's the wheels' grip, rolling, rest and blow-out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARegenPadRenewsAllSixWheels(bool network)
    {
        var (game, host) = Arena(network);
        game.Start();
        var p = network ? game.AddPlayer("tanker.vcf", "p0") : game.Players.Single();
        BlowAll(p.Car);
        p.Car.Pos = new Double3(500, 0, 0);
        host.Now = 60f;
        game.Frame(0.05f);
        AssertWhole(p.Car);
    }

    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// Every stock car with a middle pair, killed by gunfire in a network melee on M01 and respawned: all six tyres whole
    /// again (a death blows a car's tyres, so the middle pair was blown on every one of them).
    /// </summary>
    [Fact]
    public void EveryStockSixWheelerRespawnsOnSixWholeTyres()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "m01.msn")!), asm, melee: [], meleeRules: new MeleeRules { Network = true }, seed: 7);
        var game = setup.Melee!;
        var shooter = game.AddPlayer("vppirna1.vcf", "shooter");
        game.Start();
        var s = new SimSession(setup, SessionOptions.Network);
        s.AddDriver(shooter.Car);
        var six = new List<string>();
        var blownAtDeath = new List<string>();
        foreach (var vcf in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            Vehicle built;
            try { built = game.Build(vcf, true); } catch (Exception) { continue; }
            if (built.MiddleRight is null || built.IsHeli) continue;
            six.Add(vcf);
            var p = game.AddPlayer(vcf, vcf);
            s.AddDriver(p.Car);
            for (int k = 0; k < 400 && !Damage.DamageModel.IsDead(p.Car); k++)
            {
                var pk = new DamagePacket { Attacker = shooter.Car, Mask = 2, Kind = Damage.DamageKind.Round };
                pk.Ch[1] = 5000;
                setup.Damage.Apply(p.Car, pk, new Vector3(0, 0.5f, 2));
            }
            Assert.True(Damage.DamageModel.IsDead(p.Car), vcf);
            if (p.Car.MiddleRight.Blown && p.Car.MiddleLeft!.Blown) blownAtDeath.Add(vcf);
            for (int t = 0; t < 5.2f / s.Rate.TickSeconds; t++) s.Tick([]);
            Assert.Null(game.RequestRespawn(p));
            s.Tick([]);
            Assert.False(p.Destroyed, vcf);
            for (int k = 0; k < 6; k++) Assert.False(p.Car.WheelAt(k)!.Blown, $"{vcf}: wheel {k} blown after the respawn");
            game.RemovePlayer(p);
        }
        Assert.NotEmpty(six);
        Assert.Equal(six, blownAtDeath);
    }
}
