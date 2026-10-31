using System.Numerics;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Nitro Pack's 4Get-U-Not (gs4get.gdf, class 4 tier 4, ordnance kind 0x14): the lock the original builds while
/// the weapon is selected and its arc holds a target, the trigger's 1.6 s, the
/// shot that spends it, and the missile's flight.
/// </summary>
public class ForgetUNotTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private const float Dt = 102 / 2048f;

    /// <summary>The Clown with the 4Get-U-Not (vwclown1.vcf) as the player, a Piranha <paramref name="ahead"/> m in front.</summary>
    private static (SimWorld World, Combat Combat, DamageModel Damage, Vehicle Me, Vehicle Target, Weapon Msl, int Index) Rig(float ahead = 80f)
    {
        var vfs = new AssetVfs(new GameInstall(NitroDir!));
        var world = new SimWorld(new Flat(), new CrtRand(1));
        var me = VehicleFactory.Create(vfs, "vwclown1.vcf");
        var target = VehicleFactory.Create(vfs, "vppirna1.vcf");
        target.Pos = new Double3(0, 0, ahead);
        world.Vehicles.Add(me); world.Vehicles.Add(target);
        world.Player = me;
        foreach (var v in world.Vehicles) v.Health = VehicleDamage.Create(v, v.Build!, v == me, world.Rand);
        var effects = new Dictionary<string, EffectDef?>(StringComparer.OrdinalIgnoreCase);
        var combat = new Combat(world, n => effects.TryGetValue(n, out var e) ? e : effects[n] = vfs.TryRead(n) is { } d ? EffectDef.Read(n, d) : null);
        var dm = new DamageModel(world, combat);
        var a = me.Armory!;
        var msl = a.Weapons.Single(w => w.Def.Kind == 0x14);
        int i = a.Weapons.IndexOf(msl);
        a.Selected = i;
        return (world, combat, dm, me, target, msl, i);
    }

    [Fact]
    public void ItLocksForOnePointSixSecondsThenFires()
    {
        if (NitroDir is null) return;
        var (_, combat, _, me, target, msl, _) = Rig();
        Assert.Equal((4u, 4u, 1), (msl.Def.Class, msl.Def.Tier, msl.MountKind));
        var a = me.Armory!;
        int fired = -1;
        var tones = new List<string?>();
        for (int t = 1; t <= 60 && fired < 0; t++)
        {
            a.PlayerTriggers(true, new bool[5]);
            combat.Frame(Dt);
            tones.Add(combat.LockTone.GetValueOrDefault(me));
            if (combat.Rounds.Any(r => r.Kind == 0x14)) fired = t;
        }
        // 33 ticks of 0.0498 s make 1.64 s ≥ 1.6; the trigger reads the lock after that tick's aims, so it fires then
        // (measured in Nitro: the shot leaves on the frame the lock passes 1.6 s, with msllock2.wav asked for that frame)
        Assert.Equal(33, fired);
        Assert.Equal(0f, msl.LockTime);   // the shot spent the lock
        Assert.All(tones.Take(32), x => Assert.Equal("msllock1.wav", x));
        Assert.Equal("msllock2.wav", tones[32]);   // asked for in the aim pass, before the shot
        var r = combat.Rounds.Single(x => x.Kind == 0x14);
        Assert.Same(target, r.Target);
        // the next tick the lock starts again
        a.PlayerTriggers(true, new bool[5]);
        combat.Frame(Dt);
        Assert.Equal(Dt, msl.LockTime, 5);
        Assert.Equal("msllock1.wav", combat.LockTone[me]);
        // nobody locks the player, so no warning
        Assert.Empty(combat.LockedOn);
    }

    [Fact]
    public void TheMissileHitsItsTarget()
    {
        if (NitroDir is null) return;
        var (_, combat, dm, me, target, msl, i) = Rig(60f);
        var a = me.Armory!;
        msl.LockTime = 2f;
        a.PlayerTriggers(true, new bool[5]);
        combat.Frame(Dt);
        var r = combat.Rounds.Single(x => x.Kind == 0x14);
        int armour = target.Health!.Armour.Sum();
        for (int t = 0; t < 100 && combat.Rounds.Contains(r); t++) combat.Frame(Dt);
        Assert.DoesNotContain(r, combat.Rounds);
        Assert.True(target.Health.Armour.Sum() < armour, "the target took no damage");
    }

    [Fact]
    public void TheLockIsLostWithTheTargetOrTheSelection()
    {
        if (NitroDir is null) return;
        var (world, combat, _, me, target, msl, i) = Rig();
        var a = me.Armory!;
        for (int t = 0; t < 10; t++) combat.Frame(Dt);
        Assert.Equal(10 * Dt, msl.LockTime, 4);
        // another weapon selected: the lock goes to 0 and no tone plays
        a.Selected = (i + 1) % a.Weapons.Count;
        combat.Frame(Dt);
        Assert.Equal(0f, msl.LockTime);
        Assert.False(combat.LockTone.ContainsKey(me));
        a.Selected = i;
        combat.Frame(Dt);
        Assert.Equal(Dt, msl.LockTime, 5);
        // the target leaves the front arc (15°)
        target.Pos = new Double3(60, 0, 20);
        combat.Frame(Dt);
        Assert.Equal(0f, msl.LockTime);
        // a trigger without the lock does nothing
        a.PlayerTriggers(true, new bool[5]);
        combat.Frame(Dt);
        Assert.DoesNotContain(combat.Rounds, r => r.Kind == 0x14);
        // no ammo: the lock builds but its tone is silent
        target.Pos = new Double3(0, 0, 80);
        msl.Ammo = 0;
        combat.Frame(Dt);
        Assert.Equal(Dt, msl.LockTime, 5);
        Assert.False(combat.LockTone.ContainsKey(me));
    }

    [Fact]
    public void AnAiCarNeverLocks()
    {
        if (NitroDir is null) return;
        var (world, combat, _, me, target, msl, _) = Rig();
        // the Clown is an AI car now: only the player's record has a selection (the original starts it at −1)
        world.Player = target;
        for (int t = 0; t < 40; t++) combat.Frame(Dt);
        Assert.Equal(0f, msl.LockTime);
        Assert.Empty(combat.LockTone);
        Assert.Empty(combat.LockedOn);
    }

    [Fact]
    public void AHumanTargetHearsTheWarningOnceLocked()
    {
        if (NitroDir is null) return;
        var (world, combat, _, me, target, msl, _) = Rig();
        world.Humans.Add(target);
        int warned = -1;
        for (int t = 1; t <= 40 && warned < 0; t++)
        {
            combat.Frame(Dt);
            if (combat.LockedOn.Contains(target)) warned = t;
        }
        Assert.Equal(33, warned);
    }
}
