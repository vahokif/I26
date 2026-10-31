using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Bwd2;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The keyed specials and the jammer's consumers.</summary>
public class SpecialsTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Vehicle Car(double x, double z, params int[] specials)
    {
        var v = new Vehicle { Pos = new Double3(x, 0, z), Flags = VehicleFlags.EngineOn };
        v.Health = new VehicleDamage(v, true);
        for (int i = 0; i < specials.Length; i++) v.Health.Slots[17 + i] = new SpecialPart(specials[i]);
        return v;
    }

    private static SpecialPart Sp(Vehicle v, int i) => (SpecialPart)v.Health!.Slots[17 + i]!;

    private static (SimWorld, Combat) Arena(params Vehicle[] cars)
    {
        var world = new SimWorld(new Flat(), CrtRand.Oracle([]));
        world.Vehicles.AddRange(cars);
        world.Player = cars[0];
        return (world, new Combat(world, _ => null));
    }

    private static void Destroy(SpecialPart sp)
    {
        var p = new DamagePacket { Mask = 1 };
        p.Ch[0] = 1000;
        sp.Apply(p);
    }

    [Fact]
    public void TheJammerHasFiveChargesAndEachPressLastsEightSeconds()
    {
        var car = Car(0, 0, 1);
        var sp = Sp(car, 0);
        Assert.Equal(5, sp.Charges);
        Assert.Equal("cjamm.wav", Specials.Activate(car, 0, 10f));
        Assert.Equal(4, sp.Charges);
        Assert.Equal(1, sp.Unk_0x10);
        Assert.Equal(10f, sp.OnSince);
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.Jamming));
        Assert.True(Specials.Jamming(car, 17.9f));
        // a second press while it runs wastes a charge and does not restart the 8 s
        Specials.Activate(car, 0, 15f);
        Assert.Equal(3, sp.Charges);
        Assert.Equal(10f, sp.OnSince);
        Assert.True(Specials.Jamming(car, 18f));
        Assert.False(Specials.Jamming(car, 18.01f));
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.Jamming));
        Assert.Equal(0, sp.Unk_0x10);
        // no cooldown: the next press goes straight on
        Specials.Activate(car, 0, 19f);
        Assert.True(Specials.Jamming(car, 20f));
        Assert.Equal(2, sp.Charges);
    }

    [Fact]
    public void TheJammerUsesUpItsFiveChargesAndADestroyedOneDoesNothing()
    {
        var car = Car(0, 0, 1);
        for (int i = 0; i < 7; i++) Specials.Activate(car, 0, i * 20f);
        Assert.Equal(0, Sp(car, 0).Charges);
        var dead = Car(0, 0, 1);
        Destroy(Sp(dead, 0));
        Assert.Null(Specials.Activate(dead, 0, 0f));
        Assert.Equal(0, (int)(dead.Flags & VehicleFlags.Jamming));
    }

    [Fact]
    public void DamageThatDestroysTheJammerEndsTheJamAtTheNextQuery()
    {
        var car = Car(0, 0, 1);
        Specials.Activate(car, 0, 0f);
        Assert.True(Specials.Jamming(car, 1f));
        Destroy(Sp(car, 0));
        Assert.Equal(0, Sp(car, 0).Charges);
        Assert.False(Specials.Jamming(car, 2f));
    }

    [Fact]
    public void ANetworkClientTakesTheFlagAsTheTruth()
    {
        var car = new Vehicle { Flags = VehicleFlags.Jamming };
        Assert.True(Specials.Jamming(car, 1000f, trustFlag: true));
        Assert.False(Specials.Jamming(car, 1000f));
    }

    [Fact]
    public void NitroTakesAChargeEachPressAndRestartsTheFifteenSeconds()
    {
        var car = Car(0, 0, 4, 2);
        var nitro = Sp(car, 1);
        Assert.Equal(3, nitro.Charges);
        Assert.Equal("bnitro.wav", Specials.Activate(car, 1, 0f));
        Assert.NotEqual(0, (int)(car.Flags & VehicleFlags.TurboDrive));
        Assert.Equal(15f, car.Timer);
        car.Timer = 4f;
        Specials.Activate(car, 1, 0f);
        Assert.Equal(15f, car.Timer);
        Assert.Equal(1, nitro.Charges);
        Specials.Activate(car, 1, 0f);
        car.Timer = 0; car.Flags &= ~VehicleFlags.TurboDrive;
        Assert.Null(Specials.Activate(car, 1, 0f));
        Assert.Equal(0, (int)(car.Flags & VehicleFlags.TurboDrive));
    }

    [Fact]
    public void ADestroyedNitroDoesNothingButTheOtherSlotsStillDo()
    {
        var car = Car(0, 0, 2, 2);
        Destroy(Sp(car, 0));
        // the first ACTIVE type-2 slot gives it, whichever key was pressed
        Assert.Equal("bnitro.wav", Specials.Activate(car, 0, 0f));
        Assert.Equal(2, Sp(car, 1).Charges);
        var only = Car(0, 0, 2);
        Destroy(Sp(only, 0));
        Assert.Null(Specials.Activate(only, 0, 0f));
        Assert.Equal(0, (int)(only.Flags & VehicleFlags.TurboDrive));
    }

    [Fact]
    public void TheXAustBrakeWorksEvenDestroyedAndIsNeverCleared()
    {
        var car = Car(0, 0, 4);
        Destroy(Sp(car, 0));
        Specials.Activate(car, 0, 0f);
        Assert.Equal(1, car.Unk_0xf4);
        Specials.Activate(car, 0, 0f);
        Assert.Equal(1, car.Unk_0xf4);
    }

    [Fact]
    public void ThePassiveTypesDoNothingWhenKeyed()
    {
        var car = Car(0, 0, 5, 6, 9);
        for (int i = 0; i < 3; i++) Assert.Null(Specials.Activate(car, i, 0f));
        Assert.Equal(0, (int)car.Flags & ~1);
        Assert.Equal(1, Sp(car, 0).Charges);
    }

    [Fact]
    public void ADestroyedSpecialAbsorbsTheHitAndStopsBeingActive()
    {
        var sp = new SpecialPart(5);
        Assert.True(sp.Active);
        var p = new DamagePacket { Mask = 1 };
        p.Ch[0] = 4;
        Assert.False(sp.Apply(p));
        Assert.Equal(6, sp.Hp);
        p.Ch[0] = 7;
        Assert.True(sp.Apply(p));
        Assert.False(sp.Active);
    }

    [Fact]
    public void ARadarGoesBlankWhileItsOwnerOrAnyContactJams()
    {
        var me = Car(0, 0);
        var other = Car(300, 0, 1);
        var (world, combat) = Arena(me, other);
        combat.Frame(0.01f);
        Assert.Equal([other], combat.Contacts(me));
        combat.RadarTarget[me] = other;
        Specials.Activate(other, 0, world.Time);
        combat.Frame(0.01f);
        Assert.Empty(combat.Contacts(me));
        Assert.Null(combat.RadarTarget[me]);
        Assert.Null(combat.NearestEnemy(me));
        // the jammer blanks its own radar too
        Assert.Empty(combat.Contacts(other));
        // beyond 600 m it blanks nobody else's
        other.Pos = new Double3(700, 0, 0);
        combat.Frame(0.01f);
        Assert.Empty(combat.Contacts(me));   // not a contact, whatever the jam
        var third = Car(0, 100);
        world.Vehicles.Add(third);
        combat.Frame(0.01f);
        Assert.Equal([me], combat.Contacts(third));
        // eight seconds later the radar lists again
        world.Time += 8.1f;
        other.Pos = new Double3(300, 0, 0);
        combat.Frame(0.01f);
        Assert.Equal([other, third], combat.Contacts(me).OrderByDescending(v => v.Pos.X));
    }

    [Fact]
    public void TheArcTargetsAreComputedBeforeTheJamSoFixedGunsStillPick()
    {
        var me = Car(0, 0, 1);
        var other = Car(0, 0);
        me.Rot = Rows.Identity;
        other.Pos = new Double3(me.Rot.Forward.X * 100, 0, me.Rot.Forward.Z * 100);
        var (world, combat) = Arena(me, other);
        Specials.Activate(me, 0, world.Time);
        combat.Frame(0.01f);
        Assert.Empty(combat.Contacts(me));
        Assert.Same(other, combat.TargetFor(me, 1));
    }

    [Fact]
    public void AnEmplacementStopsTrackingWhilePlayerJams()
    {
        var gun = new Vehicle { Emplacement = new EmplacementState() };
        var player = Car(2000, 0);
        Assert.Same(player, Emplacements.RadarTarget(gun, player));
        Assert.Null(Emplacements.RadarTarget(gun, player, playerJams: true));
    }

    [Fact]
    public void TheEmplacementPassSeesAJamStartedThisTickBeforeTheWeaponsRun()
    {
        var player = Car(0, 0, 1);
        var gun = new Vehicle { Emplacement = new EmplacementState() };
        var (world, combat) = Arena(player, gun);
        Emplacements.Frame(world, combat, network: false);
        Assert.Same(player, combat.RadarTarget[gun]);
        // the key comes before the emplacement pass, and Combat.Frame after it (SimSession.Tick, MissionScript.AfterFrame)
        Specials.Activate(player, 0, world.Time);
        Emplacements.Frame(world, combat, network: false);
        Assert.Null(combat.RadarTarget[gun]);
        world.Time += 8.5f;
        Emplacements.Frame(world, combat, network: false);
        Assert.Same(player, combat.RadarTarget[gun]);
        Assert.Equal(0, (int)(player.Flags & VehicleFlags.Jamming));
    }

    [Fact]
    public void AnAiCarsTurretsLoseTheTargetWhilePlayerJamsButItsFixedGunsKeepIt()
    {
        var player = Car(0, 0, 1);
        var ai = Car(100, 0);
        var (world, combat) = Arena(player, ai);
        combat.RadarTarget[ai] = player;
        Assert.Same(player, combat.TargetFor(ai, 4));
        Specials.Activate(player, 0, world.Time);
        combat.Frame(0.01f);
        Assert.Null(combat.TargetFor(ai, 4));
        Assert.Same(player, combat.TargetFor(ai, 1));
        world.Time += 9f;
        combat.Frame(0.01f);
        Assert.Same(player, combat.TargetFor(ai, 4));
    }

    /// <summary>
    /// #37: the original's AI radar-target update blanked every AI turret while the player's engine was off,
    /// so a wingman stopped shooting its enemies with them. Here the engine off hides only the player.
    /// </summary>
    [Fact]
    public void ThePlayersEngineOffHidesOnlyThePlayerFromAiTurrets()
    {
        var player = Car(0, 0);
        var enemy = Car(100, 0);
        var wingman = Car(0, 100);
        var (_, combat) = Arena(player, enemy, wingman);
        combat.RadarTarget[enemy] = player;
        combat.RadarTarget[wingman] = enemy;
        player.Flags &= ~VehicleFlags.EngineOn;
        Assert.Null(combat.TargetFor(enemy, 4));            // the player is hidden from the enemy's turrets
        Assert.Same(player, combat.TargetFor(enemy, 1));    // its fixed guns keep the order target, as the original's
        Assert.Same(enemy, combat.TargetFor(wingman, 4));   // the wingman's turrets keep fighting
        Assert.Same(enemy, combat.TargetFor(wingman, 1));
    }

    [Fact]
    public void AGuidedMissileLosesItsLockForGoodToAJamOrAnEngineOff()
    {
        var shooter = Car(0, 0);
        var target = Car(0, 200, 1);
        var (world, combat) = Arena(shooter, target);
        Round Make(uint kind) => new()
        {
            Kind = kind, Shooter = shooter, Target = target, Life = 10, Pos = new Vector3(0, 1, 5), Rot = Rows.Identity,
            Def = (GizmoDef)RuntimeHelpers.GetUninitializedObject(typeof(GizmoDef)),
            Ord = (OrdnanceDef)RuntimeHelpers.GetUninitializedObject(typeof(OrdnanceDef)),
        };
        var jammed = Make(8);
        combat.Rounds.Add(jammed);
        Specials.Activate(target, 0, world.Time);
        combat.Frame(0.01f);
        Assert.Null(jammed.Target);
        // the jam ends; the lock does not come back
        world.Time += 9f;
        combat.Frame(0.01f);
        Assert.Null(jammed.Target);
        // a target whose engine is off is let go as well
        var other = Car(0, 200);
        world.Vehicles.Add(other);
        var m2 = Make(8); m2.Target = other;
        combat.Rounds.Add(m2);
        other.Flags = 0;
        combat.Frame(0.01f);
        Assert.Null(m2.Target);
    }

    [Fact]
    public void TheFourGetUNotFollowsItsTargetThroughAJamAndDeath()
    {
        // The 4Get-U-Not (kind 0x14) is the turn of the missile without its letting go
        var shooter = Car(0, 0);
        var target = Car(0, 200, 1);
        var (world, combat) = Arena(shooter, target);
        var m = new Round
        {
            Kind = 0x14, Shooter = shooter, Target = target, Life = 10, Pos = new Vector3(0, 1, 5), Rot = Rows.Identity,
            Def = (GizmoDef)RuntimeHelpers.GetUninitializedObject(typeof(GizmoDef)),
            Ord = (OrdnanceDef)RuntimeHelpers.GetUninitializedObject(typeof(OrdnanceDef)),
        };
        combat.Rounds.Add(m);
        Specials.Activate(target, 0, world.Time);
        combat.Frame(0.01f);
        Assert.Same(target, m.Target);
        target.Flags = VehicleFlags.Dead;
        combat.Frame(0.01f);
        Assert.Same(target, m.Target);
    }

    [Fact]
    public void TheAiLineOfSightLogicRunsOnlyWhileThePlayerJams()
    {
        var player = Car(0, 0, 1);
        var ai = Car(50, 0);
        var (world, _) = Arena(player, ai);
        var pe = new Entity("player", [], 1, 0) { Vehicle = player };
        var ae = new Entity("ai", [], 1, 0) { Vehicle = ai };
        var host = new ScriptHost(world, new List<Entity> { pe, ae },
            (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        var los = typeof(Ptable).GetMethod("LosLost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool Lost() => (bool)los.Invoke(host.Ptable, [ae, null])!;
        host.Now = 100f;
        Assert.False(Lost());                 // no jam: it never counts as lost
        Specials.Activate(player, 0, world.Time);
        ae.LosSeen = 90f;
        Assert.True(Lost());                  // jamming, and no look at the target for 5 s
        world.Time += 9f;
        host.Now = 101f;
        Assert.False(Lost());                 // the jam is over
        Assert.Equal(101f, ae.LosSeen);
    }
}
