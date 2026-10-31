using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Collision damage and the "ram kill" a player remembered: in the original a
/// collision never destroys a car, however fast and however far in the red it is, because the
/// kill roll is skipped for impact packets. Measured in the original game.
/// </summary>
public class RamDamageTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0f, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void TheConstantsAreTheGamesFloats()
    {
        Assert.Equal(0x388bf19c, BitConverter.SingleToInt32Bits(DamageModel.KVehicle));
        Assert.Equal(0x38d1ea6a, BitConverter.SingleToInt32Bits(DamageModel.KOther));
        Assert.Equal(1.000956e-4f, DamageModel.KOther, 1e-9f);
    }

    [Theory]
    // measured (T01): the player's vppirna1 (1951) side-on into the Taurus at 45 and 90
    [InlineData(1951f, 45f, 263)]
    [InlineData(1951f, 90f, 1054)]
    [InlineData(1951f, 6f, 4)]          // and its first touch at 6
    public void ARamsAmountIsTheOtherCarsMassTimesTheClosingSpeedSquared(float otherMass, float speed, int expected) =>
        Assert.Equal(expected, DamageModel.ImpactAmount(otherMass, DamageModel.KVehicle, Vector3.UnitZ, new Vector3(0, 0, -speed)));

    [Fact]
    public void TerrainAndScenerySideUseTheGamesConstantNotTenToTheMinusFour()
    {
        // 1000 · 31.62² · K: 99.98 with 1e-4, 100.08 with the game's 1.000956e-4
        Assert.Equal(100, DamageModel.ImpactAmount(1000f, DamageModel.KOther, Vector3.UnitY, new Vector3(0, -31.62f, 0)));
        // only the normal component counts, and its sign does not
        Assert.Equal(DamageModel.ImpactAmount(1951f, DamageModel.KVehicle, Vector3.UnitZ, new Vector3(30, 5, -20)),
            DamageModel.ImpactAmount(1951f, DamageModel.KVehicle, Vector3.UnitZ, new Vector3(0, 0, 20)));
    }

    private static (DamageModel Damage, SimWorld World, Vehicle Player, Vehicle Ai)? Setup()
    {
        if (InstallDir is null) return null;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var player = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var ai = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(player); world.Vehicles.Add(ai);
        world.Player = player;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        var combat = new Combat(world, _ => null);
        return (new DamageModel(world, combat), world, player, ai);
    }

    /// <summary>What the original measurement did to its victim: every component at 1 HP, no armour, no chassis.</summary>
    private static void PutInTheRed(Vehicle v)
    {
        var d = v.Health!;
        foreach (var part in d.Slots) if (part is { Hp: > 1 }) part.Hp = 1;
        for (int z = 0; z < 4; z++) d.Armour[z] = d.Chassis[z] = d.ArmourMirror[z] = d.ChassisMirror[z] = 0;
    }

    [Fact]
    public void NoRamKillsACarInTheRedButOneBulletDoes()
    {
        if (Setup() is not var (dm, _, pl, ai)) return;
        PutInTheRed(ai);
        var kills = new List<(Vehicle, object?)>();
        dm.KilledBy += (v, a) => kills.Add((v, a));
        var packets = new List<(int Mask, int Amount, object? Attacker)>();     // as they arrive: the slot spends them
        dm.Damaging += (v, p) => { if (v == ai) packets.Add((p.Mask, p.Ch[3], p.Attacker)); };

        // 400 rams at 90 m/s, into the front, back, both sides and the roof
        Vector3[] normals = [Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX];
        Vector3[] points = [new(0, 0.5f, 2), new(0, 0.5f, -2), new(-1.5f, 0.5f, 0), new(1.5f, 0.5f, 0), new(0, 3, 0.1f)];
        for (int i = 0; i < 400; i++)
        {
            var n = normals[i % 4];
            pl.Vel = -n * 90f;
            ai.Vel = Vector3.Zero;
            dm.CarContact(ai, pl, new Contact { N = n, C = points[i % 5] });
        }
        Assert.False(DamageModel.IsDead(ai));
        Assert.Empty(kills);
        Assert.All(packets, p => { Assert.Equal(8, p.Mask); Assert.Same(pl, p.Attacker); });
        Assert.All(packets, p => Assert.Equal(1054, p.Amount));
        Assert.True(ai.Health!.Slots.Count(p => p is { Hp: 0 }) >= 5, "the rams wreck the components");

        // the same car, a 10-point round: the kill roll, which an impact never makes
        for (int i = 0; i < 50 && !DamageModel.IsDead(ai); i++)
        {
            var round = new DamagePacket { Attacker = pl, Mask = 1, Kind = DamageKind.Round };
            round.Ch[0] = 10;
            dm.VehicleSlot(ai, round, new Vector3(1.5f, 0.5f, 0));
        }
        Assert.True(DamageModel.IsDead(ai));
        Assert.Equal((ai, (object?)pl), Assert.Single(kills));
    }

    [Fact]
    public void ARamThatRollsACarOverKillsItWithNoCredit()
    {
        if (Setup() is not var (dm, _, pl, ai)) return;
        PutInTheRed(ai);
        var kills = new List<(Vehicle, object?)>();
        dm.KilledBy += (v, a) => kills.Add((v, a));
        pl.Vel = new Vector3(0, 0, -45);
        dm.CarContact(ai, pl, new Contact { N = Vector3.UnitZ, C = new Vector3(0, 0.5f, 2) });
        Assert.False(DamageModel.IsDead(ai));
        dm.Destroy(ai);                 // Upside down at rest, or the 35 m fall
        Assert.True(DamageModel.IsDead(ai));
        // no packet, so no attacker: nobody is credited (the network game books it as a death with no killer)
        Assert.Equal((ai, (object?)null), Assert.Single(kills));
    }
}
