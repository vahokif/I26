using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// More cars than spawn points (a network game of up to 64): the original stacks them on the first pick; here the
/// point with the most room is taken and the car is spread to clear ground beside it (docs/deviations.md).
/// </summary>
public class SpawnSpreadTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Flat ground with a drop: x beyond 30 is 12 m lower, a cliff the spread must not put a car over.</summary>
    private sealed class Cliff : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (x > 30 ? -12 : 0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static MeleeGame Synthetic(out SimWorld world, int points, double spacing = 12, int roster = 0)
    {
        world = new SimWorld(new Cliff(), new CrtRand(5));
        var host = new ScriptHost(world, [], (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => 1f);
        var game = new MeleeGame
        {
            World = world, Host = host, Rand = world.Rand, Roster = Enumerable.Repeat("x.vcf", roster).ToList(), PlayerCar = "x.vcf",
            Build = (_, _) => new Vehicle(), Rules = new MeleeRules { Network = true },
        };
        for (int i = 0; i < points; i++) game.Spawns.Add(new MeleeGame.Spot(Rows.Identity, new Double3(i * spacing, 0, 0)));
        return game;
    }

    private static double MinGap(IReadOnlyList<Vehicle> cars)
    {
        double min = double.MaxValue;
        for (int i = 0; i < cars.Count; i++)
            for (int j = i + 1; j < cars.Count; j++)
                min = System.Math.Min(min, System.Math.Sqrt(System.Math.Pow(cars[i].Pos.X - cars[j].Pos.X, 2) + System.Math.Pow(cars[i].Pos.Z - cars[j].Pos.Z, 2)));
        return min;
    }

    [Fact]
    public void ManyMoreCarsThanPointsDoNotStackOrGoOverACliff()
    {
        var game = Synthetic(out var world, 4);          // points at x 0, 12, 24, 36; the last is past the cliff's edge
        game.Start();
        for (int i = 0; i < 40; i++) game.AddPlayer("x.vcf", $"p{i}");
        var cars = world.Vehicles;
        Assert.Equal(40, cars.Count);
        Assert.True(MinGap(cars) >= 5.0, $"closest pair {MinGap(cars):0.0} m: " + string.Join(" ", cars.Select(c => $"({c.Pos.X:0.#},{c.Pos.Z:0.#})")));
        // the point at x 36 is itself over the drop; the others' spreads stay on their level
        Assert.All(cars.Where(c => c.Pos.X < 30), c => Assert.Equal(0, c.Pos.Y, 3));
    }

    [Fact]
    public void AFullInstantMeleeRosterOfThirtyOneOpponentsDoesNotStackOnEightPoints()
    {
        var game = Synthetic(out var world, 8, roster: 31);
        game.Start();
        Assert.Equal(31, world.Vehicles.Count);
        Assert.True(MinGap(world.Vehicles) >= 5.0, $"closest pair {MinGap(world.Vehicles):0.0} m");
    }

    [Fact]
    public void WithAFreePointThePickIsTheOriginals()
    {
        var game = Synthetic(out var world, 6, 30);
        game.Start();
        // cars on points 0, 1 and 4: whichever the random start lands on, the original takes the next free one in turn
        foreach (int k in new[] { 0, 1, 4 }) world.Vehicles.Add(new Vehicle { Pos = new Double3(k * 30, 0, 0) });
        for (uint seed = 1; seed < 40; seed++)
        {
            world.Rand.Seed = seed;
            int got = game.PickSpawn();
            world.Rand.Seed = seed;
            int start = world.Rand.Next() % 6, expect = start;
            for (int k = 0; k < 6; k++, expect = (expect + 1) % 6)
                if (!world.Vehicles.Any(v => Vector3.DistanceSquared(v.Pos.ToVector3(), new Vector3(expect * 30, 0, 0)) < 23.05f * 23.05f)) break;
            Assert.Equal(expect, got);
        }
    }

    [Fact]
    public void WhenEveryPointIsBusyTheOneWithTheMostRoomIsPicked()
    {
        var game = Synthetic(out var world, 3);          // points at x 0, 12, 24
        game.Start();
        world.Vehicles.Add(new Vehicle { Pos = new Double3(0, 0, 0) });
        world.Vehicles.Add(new Vehicle { Pos = new Double3(24, 0, 0) });
        for (uint seed = 1; seed < 10; seed++) { world.Rand.Seed = seed; Assert.Equal(1, game.PickSpawn()); }   // all within 23 m of a car; the middle has 12 m to each
    }

    private static MissionSetup Arena(string mission, MeleeRules rules)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", mission + ".msn")!);
        return MissionSetup.Build(vfs, w, asm, melee: [], meleeRules: rules, seed: 7);
    }

    [Fact]
    public void OnTheCraterFortyJoinersAndTheirRespawnsDoNotStack()
    {
        if (InstallDir is null) return;
        var setup = Arena("m01", new MeleeRules { Network = true });
        var game = setup.Melee!;
        string[] cars = ["valepre1.vcf", "vppirna1.vcf", "vcgcour1.vcf"];
        for (int i = 0; i < 25; i++) game.AddPlayer(cars[i % 3], $"p{i}");
        game.Start();
        for (int i = 25; i < 40; i++) game.AddPlayer(cars[i % 3], $"p{i}");     // joins into a running game
        var s = new SimSession(setup, SessionOptions.Network);
        foreach (var p in game.Players) s.AddDriver(p.Car);
        var all = game.Players.Select(p => p.Car).ToList();
        Assert.True(MinGap(all) >= 5.0, $"joins: closest pair {MinGap(all):0.0} m");
        // each car stands within reach of a spawn point and on ground at the point's height (no cliff, no ledge)
        Assert.All(all, c =>
        {
            var sp = game.Spawns.MinBy(q => System.Math.Pow(q.Pos.X - c.Pos.X, 2) + System.Math.Pow(q.Pos.Z - c.Pos.Z, 2))!;
            double reach = System.Math.Sqrt(System.Math.Pow(sp.Pos.X - c.Pos.X, 2) + System.Math.Pow(sp.Pos.Z - c.Pos.Z, 2));
            float h = setup.World.Ground.Query(sp.Pos.X, sp.Pos.Y + 2, sp.Pos.Z).Height;
            Assert.True(reach < 50, $"{reach:0} m from the nearest point");
            Assert.True(System.Math.Abs(c.Pos.Y - h) <= 1.3, $"{c.Pos.Y - h:0.0} m above its point's ground");
        });
        // the first dozen die together and respawn together
        foreach (var p in game.Players.Take(12))
            for (int k = 0; k < 200 && !Damage.DamageModel.IsDead(p.Car); k++)
            {
                var pk = new DamagePacket { Attacker = game.Players[20].Car, Mask = 2, Kind = Damage.DamageKind.Round };
                pk.Ch[1] = 5000;
                setup.Damage.Apply(p.Car, pk, new Vector3(0, 0.5f, 2));
            }
        for (int t = 0; t < 5.5f / SimRate.Default.TickSeconds; t++) s.Tick([]);
        // each presses RAISE_DEAD once the 5 s are up (there is no automatic respawn)
        foreach (var p in game.Players.Take(12)) Assert.Null(game.RequestRespawn(p));
        for (int t = 0; t < 2; t++) s.Tick([]);
        Assert.All(game.Players.Take(12), p => Assert.False(p.Destroyed));
        Assert.True(MinGap(all) >= 5.0, $"respawns: closest pair {MinGap(all):0.0} m");
    }
}
