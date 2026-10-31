using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>Gun emplacements, object type 8.</summary>
public class EmplacementTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Vehicle Gun(double x = 0, double z = 0) => new() { Emplacement = new EmplacementState(), Pos = new Double3(x, 0, z), Flags = 0 };

    [Fact]
    public void TheRateGateAsksEveryFrameAtOneAndOneFrameIn2nMinus2Otherwise()
    {
        // rate 1 draws no rand() at all
        var none = CrtRand.Oracle([]);
        Assert.True(Emplacements.RateGate(1, none));
        // the ctor's 5: rand() % 8 == 0
        var r = CrtRand.Oracle([0, 1, 8, 7, 16, 12]);
        Assert.Equal([true, false, true, false, true, false], Enumerable.Range(0, 6).Select(_ => Emplacements.RateGate(5, r)));
        // 2: % 2; 0: C's % −2, so one in two as well
        var r2 = CrtRand.Oracle([3, 4, 5, 6]);
        Assert.Equal([false, true, false, true], Enumerable.Range(0, 4).Select(i => Emplacements.RateGate(i < 2 ? 2 : 0, r2)));
    }

    [Fact]
    public void ItsRadarHoldsThePlayerWhileItLivesAndThePlayersEngineRuns()
    {
        var gun = Gun();
        var player = new Vehicle { Pos = new Double3(2000, 0, 2000), Flags = VehicleFlags.EngineOn };
        Assert.Same(player, Emplacements.RadarTarget(gun, player));   // at any range
        player.Flags = 0;
        Assert.Null(Emplacements.RadarTarget(gun, player));
        player.Flags = VehicleFlags.EngineOn;
        gun.Flags |= VehicleFlags.Dead;
        Assert.Null(Emplacements.RadarTarget(gun, player));
        Assert.Null(Emplacements.RadarTarget(Gun(), null));
    }

    [Fact]
    public void TheFirePassSkipsDisabledAndDeadGunsWithoutDrawingAndDoesNothingInANetGame()
    {
        var world = new SimWorld(new Flat(), CrtRand.Oracle([]));
        var player = new Vehicle { Pos = new Double3(0, 0, 50), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(player);
        world.Player = player;
        var off = Gun(); off.Emplacement!.Enabled = 0;
        var dead = Gun(10); dead.Flags |= VehicleFlags.Dead;
        world.Vehicles.Add(off); world.Vehicles.Add(dead);
        var c = new Combat(world, _ => null);
        Emplacements.Frame(world, c, network: false);   // an empty oracle: any rand() would throw
        Assert.Same(player, c.RadarTarget[off]);         // tracked though not firing
        Assert.Null(c.RadarTarget[dead]);
        var fresh = new Combat(world, _ => null);
        Emplacements.Frame(world, fresh, network: true);
        Assert.Empty(fresh.RadarTarget);
    }

    [Fact]
    public void OnePoolEveryChannelNoArmour_AHitLargerThanWhatIsLeftKills()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var gun = Gun();
        world.Vehicles.Add(gun);
        var c = new Combat(world, _ => null);
        var d = new DamageModel(world, c);
        DamagePacket P(ushort mask, params int[] ch) { var p = new DamagePacket { Mask = mask }; for (int i = 0; i < ch.Length; i++) p.Ch[i] = ch[i]; return p; }
        d.Apply(gun, P(0b0101, 100, 999, 50), Vector3.Zero);   // channels 0 and 2 only
        Assert.Equal(450, gun.Emplacement!.Hp);
        Assert.Equal(75f, gun.Emplacement.HealthPercent, 3);
        d.Destroy(gun);                                         // the FSM's destroy: nothing for a type 8
        Assert.False(Combat.IsDead(gun));
        d.Apply(gun, P(2, 0, 450), Vector3.Zero);               // exactly what is left: down to 0, alive (hp < ch kills)
        Assert.Equal(0, gun.Emplacement.Hp);
        Assert.False(Combat.IsDead(gun));
        int killed = 0;
        d.Killed += _ => killed++;
        d.Apply(gun, P(1, 1), Vector3.Zero);
        Assert.True(Combat.IsDead(gun));
        Assert.Equal(1, killed);
        Assert.Single(d.Events.OfType<VehicleKilled>());
        Assert.Equal(10, d.Events.OfType<Debris>().Count());
        d.Apply(gun, P(1, 1000), Vector3.Zero);                 // dead: no more
        Assert.Equal(1, killed);
        Assert.False(Combat.IsLive(gun));
    }

    [Fact]
    public void TWeighsTheNearestLiveGunOfAnySide_QAndTheArcsNeverPickOne()
    {
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var me = new Vehicle { Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(me);
        world.Player = me;
        var enemy = new Vehicle { Pos = new Double3(0, 0, 200), Flags = VehicleFlags.EngineOn };
        var gun = Gun(0, 100);   // team 0, engine off: still a contact (no engine test for type 8)
        world.Vehicles.Add(enemy); world.Vehicles.Add(gun);
        var c = new Combat(world, _ => null);
        var teams = new Dictionary<Vehicle, int> { [me] = 1, [enemy] = 2 };
        c.Team = v => teams.GetValueOrDefault(v);
        Assert.True(c.IsContact(me, gun));
        Assert.Same(gun, c.NearestEnemy(me));
        Assert.Same(enemy, c.FrontalTarget(me));   // the gun is dead ahead and nearer, but Q takes type-1 colliders only
        gun.Pos = new Double3(0, 0, 300);
        Assert.Same(enemy, c.NearestEnemy(me));
        gun.Pos = new Double3(0, 0, 100);
        gun.Flags |= VehicleFlags.Dead;
        Assert.Same(enemy, c.NearestEnemy(me));
        // E skips team 0 outside a net game: the gun is never cycled to
        gun.Flags = 0;
        Assert.Same(enemy, c.NextTarget(me));
    }

    [Fact]
    public void TheFlameDecisionIs20mAndA06Cone()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var gun = VehicleFactory.Create(vfs, "flm1turr.vcf");
        gun.Emplacement = new EmplacementState();
        var target = new Vehicle { Pos = new Double3(0, 0, 15), Flags = VehicleFlags.EngineOn };
        world.Vehicles.Add(gun); world.Vehicles.Add(target);
        world.Player = target;
        var c = new Combat(world, _ => null);
        var w = Emplacements.Gun(gun)!;
        Assert.Equal(5u, w.Def.Class);
        Assert.True(c.AiShouldFire(gun, target, w, 1, 1));
        target.Pos = new Double3(15, 0, 0);    // beside it: outside the cone
        Assert.False(c.AiShouldFire(gun, target, w, 1, 1));
        target.Pos = new Double3(0, 0, 21);    // too far
        Assert.False(c.AiShouldFire(gun, target, w, 1, 1));
    }

    [Fact]
    public void ItsTurretSlewsAt17RadPerSecondOnThePlayerAndItsGunFires()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var gun = VehicleFactory.Create(vfs, "mg3turr.vcf");
        gun.Emplacement = new EmplacementState { Rate = 1 };
        gun.Flags = 0;
        var player = new Vehicle { Pos = new Double3(60, 0, 0), Flags = VehicleFlags.EngineOn };   // 90° to its right
        world.Vehicles.Add(player); world.Vehicles.Add(gun);
        world.Player = player;
        var c = new Combat(world, _ => null);
        var w = Emplacements.Gun(gun)!;
        Assert.Equal(3u, w.Style);
        const float dt = 0.036f;
        Emplacements.Frame(world, c, network: false);
        c.Frame(dt);
        Assert.Equal(1.7f * dt, w.AimYaw, 4);   // full health: h = 0.75 + 0.25·1
        int shots = 0;
        for (int f = 0; f < 60; f++)
        {
            Emplacements.Frame(world, c, network: false);
            c.Frame(dt);
            shots += c.Events.OfType<ShotFired>().Count(s => s.Shooter == gun);
            c.Events.Clear();
        }
        Assert.Equal(MathF.PI / 2, w.AimYaw, 1);
        Assert.True(w.OnTarget);
        Assert.True(shots > 0);
        // the player's engine off: no radar target, no aim, and the pass still fires on its old aim
        player.Flags = 0;
        Emplacements.Frame(world, c, network: false);
        Assert.Null(c.RadarTarget[gun]);
        // and with no target the turret slews back to its mount's forward at the same rate
        float before = w.AimYaw;
        c.Frame(dt);
        Assert.Equal(before - 1.7f * dt, w.AimYaw, 4);
        for (int f = 0; f < 60; f++) { Emplacements.Frame(world, c, network: false); c.Frame(dt); }
        Assert.Equal(0f, w.AimYaw, 4);
        Assert.Equal(0f, w.AimPitch, 4);
    }
}
