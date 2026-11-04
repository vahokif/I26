using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Damage;
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
/// The difficulty (Play Options' Difficulty Level) is the factor on the player's own rounds and flame, as measured in the
/// original game: Wimp ×2, Champion ×1, Badass ×0.75, truncated; anyone else's ×1, and everyone's ×1 in a network game.
/// The T08 Huey (its pool set to 800, broadside 30 m ahead of the braked car, 28 fps) takes the original's number of hits.
/// </summary>
public class DifficultyTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0f, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 400);
    }

    private static (Combat Combat, Vehicle Player, Vehicle Ai, Weapon Mg)? Pair()
    {
        if (InstallDir is null) return null;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var player = VehicleFactory.Create(vfs, "vppa01.vcf");
        var ai = VehicleFactory.Create(vfs, "vppa01.vcf");
        world.Vehicles.Add(player); world.Vehicles.Add(ai);
        world.Player = player;
        var mg = player.Armory!.Weapons.First(w => w.Def.Name == "30cal MG");
        return (new Combat(world, _ => null), player, ai, mg);
    }

    private static Round Shot(Vehicle shooter, Weapon w) => new()
    {
        Kind = w.Def.Kind, Shooter = shooter, Def = w.Def, Ord = w.Def.Ordnance!, Damage = w.Damage,
    };

    private static int Total(DamagePacket p) => p.Ch.Sum();

    [Theory]
    [InlineData(0, 30)]     // Wimp: 15 × 2
    [InlineData(1, 15)]     // Champion
    [InlineData(2, 11)]     // Badass: 15 × 0.75 = 11.25, truncated
    public void ThePlayersRoundTakesTheDifficultysFactor(int difficulty, int packet)
    {
        if (Pair() is not var (c, player, ai, mg)) return;
        Assert.Equal(15, mg.Damage);
        c.Difficulty = difficulty;
        Assert.Equal(packet, Total(c.RoundPacket(Shot(player, mg))));
        // an AI's round, with the same gun, is never scaled
        Assert.Equal(15, Total(c.RoundPacket(Shot(ai, mg))));
    }

    [Theory]
    [InlineData("net")]
    [InlineData("client")]
    [InlineData("coop")]
    public void ANetworkGameIsChampionWhateverTheSetting(string game)
    {
        if (Pair() is not var (c, player, _, mg)) return;
        if (game == "net") c.NetSession = true;
        else if (game == "client") c.RemoteSim = true;
        else c.World.Coop = true;
        foreach (int d in new[] { 0, 1, 2 })
        {
            c.Difficulty = d;
            Assert.True(c.NetworkGame);
            Assert.Equal(1f, c.DifficultyFactor(player));
            Assert.Equal(15, Total(c.RoundPacket(Shot(player, mg))));
        }
    }

    [Fact]
    public void ANetworkHumansRoundIsNotScaledEither()
    {
        if (Pair() is not var (c, _, ai, mg)) return;
        c.World.Humans.Add(ai);
        c.NetSession = true;
        c.Difficulty = 0;
        Assert.Equal(15, Total(c.RoundPacket(Shot(ai, mg))));
    }

    /// <summary>
    /// T08's Huey (t08uc01), its pool set to 800, pinned broadside 30 m ahead of the braked car before and after every
    /// tick at 28 fps; the car fires one weapon until the Huey is shot down. Returns the packets that reached it while it
    /// was alive: every one, and the splashes among them.
    /// </summary>
    private static (int Packets, int Splash)? HueyHits(string car, string weapon, int difficulty)
    {
        if (InstallDir is null) return null;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t08.msn")!), asm, car, script: false, rand: new CrtRand(1));
        var s = new SimSession(setup, rate: SimRate.FromLabel(28));
        var player = setup.World.Player!;
        s.AddDriver(player);
        var huey = setup.World.Vehicles.Single(v => string.Equals(v.Definition, "t08uc01.vcf", StringComparison.OrdinalIgnoreCase));
        huey.Heli!.SetPool(800);
        setup.Combat.Difficulty = difficulty;
        var arm = player.Armory!;
        int gun = arm.Weapons.FindIndex(w => w.Def.Name == weapon);
        Assert.True(gun >= 0, $"{car} has no {weapon}: {string.Join(", ", arm.Weapons.Select(w => w.Def.Name))}");
        arm.Selected = gun; arm.Group.Clear(); arm.Link = false;
        int packets = 0, splash = 0;
        bool down = false;
        setup.Damage.Damaging += (v, p) =>
        {
            if (v != huey || (huey.Flags & (VehicleFlags.Dead | HeliStep.Falling)) != 0) return;
            packets++;
            if (p.Kind == DamageKind.Explosion) splash++;
        };
        // the car stands still; the Huey 30 m ahead, its nose to the car's right, on the ground
        var input = new CarInput { Buttons = CarButtons.EBrake };
        for (int i = 0; i < 28 * 2; i++) s.Tick([input]);
        var fwd = player.Rot.Forward with { Y = 0 };
        fwd = Vector3.Normalize(fwd);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fwd));
        var at = player.Pos.ToVector3() + fwd * 30f;
        at.Y = setup.Ground.BareHeight(at.X, at.Z);
        var rot = new Rows(-fwd, Vector3.UnitY, right);
        void Pin() { huey.Pos = new Double3(at.X, at.Y, at.Z); huey.Rot = rot; huey.Vel = Vector3.Zero; huey.AngVel = Vector3.Zero; }
        input.Buttons |= CarButtons.Fire;
        for (int t = 0; t < 28 * 30 && !down; t++)
        {
            Pin();
            s.Tick([input]);
            down = (huey.Flags & (VehicleFlags.Dead | HeliStep.Falling)) != 0;
            if (!down) Pin();
        }
        Assert.True(down, $"{weapon} at difficulty {difficulty}: the Huey still flies after {packets} packets ({huey.Heli.Hp} left)");
        return (packets, splash);
    }

    [Theory]
    [InlineData("vppa01.vcf", "30cal MG", 0, 27)]
    [InlineData("vppa01.vcf", "30cal MG", 1, 54)]
    [InlineData("vppa01.vcf", "30cal MG", 2, 73)]
    [InlineData("vppt05.vcf", "50cal MG", 0, 17)]
    [InlineData("vppt05.vcf", "50cal MG", 1, 33)]
    [InlineData("vppt08.vcf", "20mm Cannon", 0, 9)]
    public void TheHueyTakesTheOriginalsHitsAtEachDifficulty(string car, string weapon, int difficulty, int hits)
    {
        if (HueyHits(car, weapon, difficulty) is not var (packets, splash)) return;
        Assert.Equal(0, splash);
        Assert.Equal(hits, packets);
    }

    [Theory]
    [InlineData("vppa01.vcf", "FireRite Rkt", 4, 3)]     // 240 a rocket and its own splash of 2
    [InlineData("vppt09.vcf", "Aim-Nein Msl", 2, 1)]     // 440 and 2
    public void AtWimpTheRocketsAndMissilesTakeTheOriginalsShots(string car, string weapon, int hits, int splash)
    {
        if (HueyHits(car, weapon, 0) is not var (packets, sp)) return;
        Assert.Equal(hits, packets - sp);
        Assert.Equal(splash, sp);
    }
}
