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
/// The Play Options Unlim. Armor (bit 8) and Unlim. Chas. Rein. (bit 0x10)
/// cheats, as the original applies them:
/// the player's side zones only, no network session, the top untouched.
/// </summary>
public class PlayOptionCheatTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Rough : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0f, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 400);
    }

    private static readonly Vector3 Front = new(0, 0.5f, 2), Top = new(0, 3, 0.1f);

    private static (DamageModel Damage, Vehicle Player, Vehicle Ai)? Setup(bool armour, bool chassis, bool net = false)
    {
        if (InstallDir is null) return null;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Rough(), new CrtRand(1));
        var player = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var ai = VehicleFactory.Create(vfs, "vppirna1.vcf");
        world.Vehicles.Add(player); world.Vehicles.Add(ai);
        world.Player = player;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == world.Player, world.Rand);
        var combat = new Combat(world, _ => null) { NetSession = net };
        var damage = new DamageModel(world, combat) { UnlimitedArmour = armour, UnlimitedChassis = chassis };
        return (damage, player, ai);
    }

    private static DamagePacket Packet(ushort mask, int amount)
    {
        var p = new DamagePacket { Mask = mask };
        for (int c = 0; c < 4; c++) if ((mask & (1 << c)) != 0) p.Ch[c] = amount;
        return p;
    }

    /// <summary>The pools, and the components' HP summed (every slot but the chassis, 6).</summary>
    private static (int[] Armour, int[] Chassis, int Parts) State(Vehicle v) =>
        ([.. v.Health!.Armour], [.. v.Health.Chassis], v.Health.Slots.Where((p, i) => p is not null && i != 6).Sum(p => p!.Hp));

    [Fact]
    public void OffEveryPoolShrinks()
    {
        if (Setup(false, false) is not var (dm, pl, ai)) return;
        var (a0, c0, p0) = State(pl);
        ai.Vel = new Vector3(0, 0, -30);
        dm.CarContact(pl, ai, new Contact { N = Vector3.UnitZ, C = Front });
        Assert.True(pl.Health!.Chassis.Sum() < c0.Sum());       // the ram wears a chassis zone
        (a0, c0, p0) = State(pl);
        dm.VehicleSlot(pl, Packet(1, 50), Front);
        dm.VehicleSlot(pl, Packet(8, 50), Front);
        Assert.Equal(a0[0] - 50, pl.Health!.Armour[0]);
        Assert.Equal(c0[0] - 50, pl.Health.Chassis[0]);
        // an impact past the chassis reaches the components
        dm.VehicleSlot(pl, Packet(8, c0[0] * 4), Front);
        Assert.Equal(0, pl.Health.Chassis[0]);
        Assert.True(State(pl).Parts < p0);
    }

    [Fact]
    public void UnlimitedArmourAbsorbsEveryChannelButLeavesTheChassis()
    {
        if (Setup(true, false) is not var (dm, pl, ai)) return;
        var (a0, c0, p0) = State(pl);
        dm.VehicleSlot(pl, Packet(1, a0[0] * 4), Front);        // ballistic, far past the armour
        dm.VehicleSlot(pl, Packet(2, a0[0] * 4), Front);        // explosive
        dm.VehicleSlot(pl, Packet(4, 5000), Front);             // fire: zeroed too
        Assert.Equal(a0, pl.Health!.Armour);
        Assert.Equal(p0, State(pl).Parts);
        dm.VehicleSlot(pl, Packet(8, 50), Front);               // collisions still wear the chassis
        Assert.Equal(c0[0] - 50, pl.Health.Chassis[0]);
        dm.VehicleSlot(pl, Packet(8, c0[0] * 4), Front);        // the remainder past it is discarded
        Assert.Equal(0, pl.Health.Chassis[0]);
        Assert.Equal(p0, State(pl).Parts);
        Assert.False(DamageModel.IsDead(pl));
        // the top (zone 4) has no cheat: hits there reach the components
        for (int i = 0; i < 20 && !DamageModel.IsDead(pl); i++) dm.VehicleSlot(pl, Packet(1, 500), Top);
        Assert.True(State(pl).Parts < p0);
        // only the player's car
        var aa = ai.Health!.Armour[0];
        dm.VehicleSlot(ai, Packet(1, 50), Front);
        Assert.Equal(aa - 50, ai.Health.Armour[0]);
    }

    [Fact]
    public void UnlimitedChassisAbsorbsImpactsButNotRounds()
    {
        if (Setup(false, true) is not var (dm, pl, ai)) return;
        var (a0, c0, p0) = State(pl);
        dm.VehicleSlot(pl, Packet(8, c0[0] * 4), Front);
        ai.Vel = new Vector3(0, 0, -30);                        // rammed head-on at 30 m/s
        dm.CarContact(pl, ai, new Contact { N = Vector3.UnitZ, C = Front });
        ai.Vel = Vector3.Zero;
        for (int i = 0; i < 200; i++) dm.Rough(pl, 0.1f);       // 400/s of rough ground for 20 s
        Assert.Equal(c0, pl.Health!.Chassis);
        Assert.Equal(a0, pl.Health.Armour);
        Assert.Equal(p0, State(pl).Parts);
        dm.VehicleSlot(pl, Packet(1, 50), Front);
        Assert.Equal(a0[0] - 50, pl.Health.Armour[0]);
        // the AI on the same ground wears down
        var ca = ai.Health!.Chassis.Sum();
        for (int i = 0; i < 200; i++) dm.Rough(ai, 0.1f);
        Assert.True(ai.Health.Chassis.Sum() < ca);
    }

    [Fact]
    public void NetworkSessionsIgnoreTheCheats()
    {
        if (Setup(true, true, net: true) is not var (dm, pl, _)) return;
        var (a0, c0, _) = State(pl);
        dm.VehicleSlot(pl, Packet(1, 50), Front);
        dm.VehicleSlot(pl, Packet(8, 50), Front);
        Assert.Equal(a0[0] - 50, pl.Health!.Armour[0]);
        Assert.Equal(c0[0] - 50, pl.Health.Chassis[0]);
    }
}
