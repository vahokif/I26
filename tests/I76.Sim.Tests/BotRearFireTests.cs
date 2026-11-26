using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// A network melee's bots use every mounted weapon by its own facing (#199): on the way somewhere (a race, a regen pad,
/// capture the flag) a bot fires its rear gun at an enemy on its tail and lays its dropper in front of it, as the original's computer
/// drivers judge every weapon against their target; a teammate behind it gets neither. The bot's fire decision
/// (<see cref="MeleeGame.Shoot"/>) is called directly on cars placed by hand, on M01 with a fixed seed.
/// </summary>
public class BotRearFireTests(ITestOutputHelper output)
{
    private sealed record Rig(MeleeGame Game, ScriptHost Host, MeleePlayer Bot, Weapon Rear, Weapon Drop, MeleePlayer Enemy, MeleePlayer Mate, float Lift);

    private static Rig Make(string dir)
    {
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "m01", melee: [], rules: new MeleeRules { Network = true, TeamPlay = true }), SimRate.R20, 7);
        var game = setup.Melee!;
        var bot = game.AddBot("vppirna1.vcf", "Bot (bot)", 1, BotSkill.Hard);
        var enemy = game.AddPlayer("vppirna1.vcf", "enemy", 2);
        var mate = game.AddPlayer("vppirna1.vcf", "mate", 1);
        s.AssignIds();
        var a = bot.Car.Armory!;
        var drop = a.Weapons.FirstOrDefault(w => w.Def.Class == 6) ?? throw new InvalidOperationException("the Piranha has no dropper");
        // a rear gun: the car's own if it has one, else its first gun again on the tail, pointing back
        var rear = a.Weapons.FirstOrDefault(w => w.Def.Class is 1 or 2 && w.MuzzleRot.Forward.Z < 0);
        if (rear is null)
        {
            var gun = a.Weapons.First(w => w.Def.Class is 1 or 2);
            rear = new Weapon
            {
                Def = gun.Def, Hardpoint = gun.Hardpoint with { Facing = 2, Style = 1 }, Style = 1, MountKind = 2, MuzzlePos = new Vector3(0, 1f, -2.6f),
                BaseRot = Rows.AboutY(MathF.PI), Hp = gun.MaxHp, MaxHp = gun.MaxHp, Damage = gun.Damage, Ammo = gun.Ammo,
            };
            a.Weapons.Add(rear);
        }
        // past the start grid's hold
        setup.Script!.Host.Now = 30f;
        // the bot 80 m on from its spawn, into the arena, on the ground there (a spawn's back is often to the arena's wall), the others placed from it
        var g = setup.World.Ground;
        var sp = bot.Car.Pos.ToVector3();
        float lift = sp.Y - g.Query(sp.X, sp.Y + 50, sp.Z).Height;
        var flat = Vector3.Normalize(new Vector3(bot.Car.Rot.Forward.X, 0, bot.Car.Rot.Forward.Z));
        var to = sp + flat * 80f;
        bot.Car.Pos = Double3.From(new Vector3(to.X, g.Query(to.X, to.Y + 50, to.Z).Height + lift, to.Z));
        return new Rig(game, setup.Script.Host, bot, rear, drop, enemy, mate, lift);
    }

    /// <summary>A point on the ground (the car's origin as high over it as at its spawn).</summary>
    private static Double3 OnGround(Rig r, Vector3 at)
    {
        var g = r.Host.Combat!.World.Ground;
        return Double3.From(new Vector3(at.X, g.Query(at.X, at.Y + 50, at.Z).Height + r.Lift, at.Z));
    }

    /// <summary>
    /// The bot driving on at 20 m/s; <paramref name="car"/> on the same line <paramref name="behind"/> m back on the ground there,
    /// <paramref name="aside"/> m to the side, at the same speed (or parked far off when null).
    /// </summary>
    private static void Place(Rig r, MeleePlayer car, float? behind, float aside = 0)
    {
        var b = r.Bot.Car;
        var fwd = b.Rot.Forward;
        var right = b.Rot.Right;
        var at = b.Pos.ToVector3();
        car.Car.Pos = behind is { } d ? OnGround(r, at - fwd * d + right * aside) : OnGround(r, at + right * 400f);
        car.Car.Rot = b.Rot;
        car.Car.Vel = fwd * 20f; car.Car.Speed = 20f; car.Car.Gear = 1;
    }

    private static void Drive(Rig r)
    {
        var b = r.Bot.Car;
        b.Vel = b.Rot.Forward * 20f; b.Speed = 20f; b.Gear = 1;
    }

    /// <summary>The bot's fire decision tried <paramref name="tries"/> times (the gun's gate is a roll): how often each weapon's trigger was pulled.</summary>
    private static (int Rear, int Drop, int Forward) Tries(Rig r, int tries = 40)
    {
        int rear = 0, drop = 0, other = 0;
        for (int i = 0; i < tries; i++)
        {
            foreach (var w in r.Bot.Car.Armory!.Weapons) w.Trigger = false;
            r.Game.Shoot(r.Bot, 45f, 0.9f);
            foreach (var w in r.Bot.Car.Armory!.Weapons)
                if (w.Trigger) { if (w == r.Rear) rear++; else if (w == r.Drop) drop++; else if (w.MuzzleRot.Forward.Z >= 0) other++; }
        }
        return (rear, drop, other);
    }

    [Fact]
    public void ABotChasedByAnEnemyFiresItsRearGunAndLaysItsDropper()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var r = Make(dir);
        Drive(r);
        Place(r, r.Enemy, 15f);
        Place(r, r.Mate, null);
        var c = r.Host.Combat!;
        output.WriteLine($"rear: class {r.Rear.Def.Class} style {r.Rear.Style} facing {r.Rear.MuzzleRot.Forward} muzzle {r.Rear.MuzzlePos} speed {r.Rear.Def.Speed} sees {c.CanSee(r.Bot.Car, r.Enemy.Car)} skill {r.Bot.Entity.Ai.FireSkill} acc {r.Bot.Entity.Ai.FireAccuracy}; direct {Enumerable.Range(0, 40).Count(_ => c.AiShouldFire(r.Bot.Car, r.Enemy.Car, r.Rear, r.Bot.Entity.Ai.FireSkill, r.Bot.Entity.Ai.FireAccuracy))}/40");
        foreach (var w in r.Bot.Car.Armory!.Weapons) output.WriteLine($"  weapon class {w.Def.Class} style {w.Style} kind {w.MountKind} facing {w.MuzzleRot.Forward}");
        var (rear, drop, other) = Tries(r);
        output.WriteLine($"enemy 15 m behind: rear gun {rear}/40, dropper {drop}/40, forward weapons {other}/40");
        Assert.True(rear > 0, "the rear gun never fired at the enemy on the bot's tail");
        Assert.True(drop > 0, "the dropper never laid in front of the enemy on the bot's tail");
        Assert.Equal(0, other);
        // the radar keeps no target ahead: the pursuer is the radar's (a turret swings round to it)
        Assert.Same(r.Enemy.Car, r.Host.Combat!.RadarTarget.GetValueOrDefault(r.Bot.Car));
    }

    [Fact]
    public void ABotChasedByATeammateDoesNeither()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var r = Make(dir);
        Drive(r);
        Place(r, r.Mate, 15f);
        Place(r, r.Enemy, null);
        var (rear, drop, other) = Tries(r);
        output.WriteLine($"teammate 15 m behind: rear gun {rear}, dropper {drop}, forward {other}");
        Assert.Equal((0, 0, 0), (rear, drop, other));
    }

    [Fact]
    public void AnEnemyOnTheTailWithATeammateJustBehindItGetsNoDrop()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var r = Make(dir);
        Drive(r);
        Place(r, r.Enemy, 15f);
        Place(r, r.Mate, 35f, 20f);     // well off the line of the rear gun, within the 100 m the drop keeps clear of
        var (rear, drop, _) = Tries(r);
        output.WriteLine($"enemy 15 m behind, teammate 35 m behind 20 m aside: rear gun {rear}, dropper {drop}");
        Assert.True(rear > 0);
        Assert.Equal(0, drop);
    }

    [Fact]
    public void NoDropOnTheStartGridOrCrawlingOrFromTheLastQuarter()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var r = Make(dir);
        Drive(r);
        Place(r, r.Enemy, 15f);
        Place(r, r.Mate, null);
        r.Host.Now = MeleeGame.DropGridSeconds - 1f;
        Assert.Equal(0, Tries(r).Drop);
        r.Host.Now = 30f;
        r.Bot.Car.Speed = MeleeGame.DropMinSpeed - 1f;
        Assert.Equal(0, Tries(r).Drop);
        Drive(r);
        int full = r.Drop.Ammo;
        r.Drop.Ammo = (int)r.Drop.Def.Ammo / 4;
        Assert.Equal(0, Tries(r).Drop);
        r.Drop.Ammo = full;
        Assert.True(Tries(r).Drop > 0);
    }
}
