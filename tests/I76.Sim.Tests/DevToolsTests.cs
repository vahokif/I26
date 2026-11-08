using System.Numerics;
using I76.Formats.Bwd2;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The Developer panel's sim actions (DevTools) and the in-place store / restore it uses, on a world made in code.</summary>
public class DevToolsTests
{
    /// <summary>A slope (height 0.05·x), so a teleport's height is the terrain's at the new place, not the old.</summary>
    private sealed class Slope : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => ((float)(0.05 * x), Vector3.Normalize(new Vector3(-0.05f, 1, 0)));
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 3);
    }

    private static readonly VehicleBuild Build = new([120, 80, 80, 60], [60, 40, 40, 30], [80, 80, 80], 300, 150, 200, 1f, [1, 2]);

    private static Vehicle Car(double x, double z, bool player, CrtRand rand)
    {
        var v = new Vehicle { Pos = new Double3(x, 0.05 * x + 0.5, z), Build = Build, ObjectClass = 1 };
        (v.FrontRight.Offset, v.FrontLeft.Offset, v.RearRight.Offset, v.RearLeft.Offset) =
            (new(0.7f, 0.35f, 1.3f), new(-0.7f, 0.35f, 1.3f), new(0.7f, 0.35f, -1.3f), new(-0.7f, 0.35f, -1.3f));
        foreach (var w in v.Wheels) { w.InitSuspension(); w.Grip = 1; w.Rolling = 1; }
        v.Armory = new Armory();
        var def = Armory.HandgunDef(1) with { Ammo = 100 };
        var gun = new Weapon
        {
            Def = def, Hardpoint = new Hardpoint("gun", 1, 1, 1, new Transform([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2])),
            MountKind = 1, MuzzlePos = new(0, 1, 2), BaseRot = Rows.Identity, Hp = (int)def.HitPoints, MaxHp = (int)def.HitPoints, Damage = (int)def.Damage, Ammo = 100,
        };
        v.Armory.Weapons.Add(gun);
        v.Armory.LoadOrder.Add(gun);
        v.Health = VehicleDamage.Create(v, Build, player, rand);
        return v;
    }

    private static SimSession World(uint seed, SessionOptions? options = null)
    {
        var rand = new CrtRand(seed);
        var world = new SimWorld(new Slope(), rand);
        world.Vehicles.AddRange([Car(0, 0, true, rand), Car(6, 30, false, rand), Car(-8, 60, false, rand)]);
        world.Player = world.Vehicles[0];
        var combat = new Combat(world, _ => null);
        _ = new DamageModel(world, combat);
        var s = new SimSession(world, combat, null, options ?? SessionOptions.SinglePlayer);
        s.AddDriver(world.Player);
        return s;
    }

    private static void Run(SimSession s, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            int t = s.Ticks;
            s.Tick([SnapshotRig.Input(t, 0)]);
            if (t % 37 == 0) foreach (var v in s.World.Vehicles.Skip(1)) { v.Throttle = 0.9f; v.Steer = (t % 74 == 0) ? 0.3f : -0.3f; }
        }
    }

    private static List<ulong> Hashes(SimSession s, int ticks)
    {
        var list = new List<ulong>();
        for (int i = 0; i < ticks; i++) { Run(s, 1); list.Add(s.StateHash()); }
        return list;
    }

    private static DamageModel Damage(SimSession s) => (DamageModel)s.Combat!.Damage!;

    private static void Hit(SimSession s, Vehicle v, int mask, int amount, Vector3 at)
    {
        var p = new DamagePacket { Mask = (ushort)mask, Kind = DamageKind.Round };
        for (int i = 0; i < 4; i++) if ((mask & 1 << i) != 0) p.Ch[i] = amount;
        Damage(s).Apply(v, p, at);
    }

    [Fact]
    public void ATeleportPutsTheCarOnTheGroundThereFacingTheHeadingAtRest()
    {
        var s = World(1);
        Run(s, 60);
        var p = s.World.Player!;
        Assert.True(p.Speed > 0.5f, "the run should have the car moving");
        int gen = p.NetGeneration;
        Assert.True(DevTools.Teleport(s, p, 400, -250, 90));
        Assert.Equal(400, p.Pos.X, 6);
        Assert.Equal(-250, p.Pos.Z, 6);
        Assert.Equal(20, p.Pos.Y, 3);                    // the slope's height at x = 400
        Assert.Equal(Vector3.Zero, p.Vel);
        Assert.Equal(0, p.Speed);
        Assert.True(p.Rot.Forward.X > 0.99f, $"heading 90 faces +X, got {p.Rot.Forward}");
        Assert.True(p.Rot.Up.Y > 0.99f && p.Rot.Up.X < 0, "upright on the slope (its normal leans toward -X)");
        Assert.True(p.NetGeneration > gen);
        Run(s, 20);   // and it drives on from there
        Assert.True(System.Math.Abs(p.Pos.X - 400) < 30);
    }

    [Fact]
    public void ANetworkSessionRefusesEveryAction()
    {
        var s = World(1, SessionOptions.Network);
        var p = s.World.Player!;
        var before = p.Pos;
        Assert.False(DevTools.Teleport(s, p, 100, 100, 0));
        Assert.Equal(before, p.Pos);
        Assert.False(DevTools.Repair(s, p));
        Assert.False(DevTools.SetGodMode(s, true));
        Assert.False(DevTools.GodMode(s));
        Assert.Null(DevTools.BringNearest(s, p));
    }

    [Fact]
    public void RepairMakesTheCarAsBuiltAndDrawsNoRand()
    {
        var s = World(1);
        Run(s, 30);
        var p = s.World.Player!;
        var h = p.Health!;
        // armour off the front, then the components under it, a tyre blown, the gun shot up and half fired
        for (int k = 0; k < 6; k++) Hit(s, p, 0xf, 400, new Vector3(0, 0.5f, 2));
        Damage(s).FlattenTyre(p, 1);
        p.Armory!.Weapons[0].Ammo = 37;
        p.Armory.Weapons[0].Hp = 1;
        Assert.True(h.Armour[0] < h.ArmourMax[0]);
        Assert.Contains(h.Slots, x => x is not null and not WeaponPart && x.Hp < x.Max);
        Assert.True(p.FrontLeft.Blown);
        uint seed = s.World.Rand.Seed;

        Assert.True(DevTools.Repair(s, p));
        Assert.Equal(seed, s.World.Rand.Seed);
        Assert.Equal(h.ArmourMax, h.Armour);
        Assert.Equal(h.ChassisMax, h.Chassis);
        foreach (var part in h.Slots) if (part is not null) Assert.Equal(part.Max, part.Hp);
        Assert.False(p.FrontLeft.Blown);
        Assert.Equal(p.FrontLeft.Offset.Y, p.FrontLeft.Rest);
        Assert.Equal(1f, p.FrontLeft.Grip);
        Assert.Equal(1f, p.FrontLeft.Rolling);
        Assert.Equal(1f, p.Engine.Damage);
        Assert.Equal(h.Brake!.Nominal, p.P.BrakeStrength);
        Assert.Equal(h.Suspension!.Nominal, p.P.ChassisGrip);
        Assert.Equal(100, p.Armory.Weapons[0].Ammo);
        Assert.Equal(p.Armory.Weapons[0].MaxHp, p.Armory.Weapons[0].Hp);
        Assert.Equal(100f, h.HealthPercent());
    }

    [Fact]
    public void RepairLeavesAWholeCarAsItWas()
    {
        // as built (a run scrapes the chassis and fires the gun, so only a fresh car is whole)
        var a = World(1); var b = World(1);
        Assert.True(DevTools.Repair(b, b.World.Player!));
        Assert.True(StateDump.FirstDifference(StateDump.Of(a), StateDump.Of(b)) is null, StateDump.FirstDifference(StateDump.Of(a), StateDump.Of(b)));
        Assert.Equal(a.StateHash(), b.StateHash());
        Run(a, 50); Run(b, 50);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ADeadCarIsNotRepairedOrTeleported()
    {
        var s = World(1);
        var p = s.World.Player!;
        Damage(s).Destroy(p);
        Assert.False(DevTools.Repair(s, p));
        Assert.False(DevTools.Teleport(s, p, 50, 50, 0));
    }

    [Fact]
    public void GodModeIsThePlayOptionsThreeCheats()
    {
        var s = World(1);
        Assert.False(DevTools.GodMode(s));
        Assert.True(DevTools.SetGodMode(s, true));
        Assert.True(DevTools.GodMode(s));
        Assert.True(s.Combat!.UnlimitedAmmo && Damage(s).UnlimitedArmour && Damage(s).UnlimitedChassis);
        Assert.True(DevTools.SetGodMode(s, false));
        Assert.False(s.Combat.UnlimitedAmmo || Damage(s).UnlimitedArmour || Damage(s).UnlimitedChassis);
    }

    [Fact]
    public void BringNearestPutsTheNearestOtherCarAheadFacingThePlayer()
    {
        var s = World(1);
        var p = s.World.Player!;
        DevTools.Teleport(s, p, 0, 0, 0);
        var moved = DevTools.BringNearest(s, p, 30);
        Assert.Same(s.World.Vehicles[1], moved);   // (6, 30) is nearer than (-8, 60)
        Assert.Equal(0, moved!.Pos.X, 4);
        Assert.Equal(30, moved.Pos.Z, 4);
        Assert.True(moved.Rot.Forward.Z < -0.99f, "facing back toward the player");
    }

    [Fact]
    public void NoScriptNoPoints() => Assert.Empty(DevTools.Points(World(1)));

    /// <summary>
    /// The panel's Store / Restore: the same session's snapshot written back into it (not a rebuilt mission, as a checkpoint's
    /// retry does) goes on tick for tick as the stored run did, after a teleport and a repair in between.
    /// </summary>
    [Fact]
    public void AStateRestoredIntoTheSameSessionReplaysTheStoredRunExactly()
    {
        var s = World(1);
        Run(s, 120);
        var snap = s.Snapshot();
        var dump = StateDump.Of(s);
        var first = Hashes(s, 200);
        Assert.NotEqual(first[0], first[^1]);
        // wander off: hits, a teleport, a repair, more ticks
        var p = s.World.Player!;
        Hit(s, p, 0xf, 300, new Vector3(0, 0.5f, 2));
        DevTools.Teleport(s, p, 900, 900, 45);
        DevTools.Repair(s, p);
        Run(s, 77);

        var r = s.Restore(snap);
        Assert.Empty(r.Created);
        Assert.Empty(r.Dropped);
        Assert.Null(StateDump.FirstDifference(dump, StateDump.Of(s)));
        var second = Hashes(s, 200);
        for (int k = 0; k < first.Count; k++) Assert.True(first[k] == second[k], $"tick {k} after the restore: the run went its own way");
    }

    /// <summary>
    /// The same on a scripted mission from the install (skipped without one): t14's paths and named cars are the points, and
    /// a store, a teleport to the last point and a restore into the same session replay the stored run tick for tick, script
    /// and AI included.
    /// </summary>
    [Fact]
    public void AScriptedMissionsPointsAndInPlaceRestore()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t14"), SimRate.R20, 2);
        var points = DevTools.Points(s);
        Assert.Contains(points, p => p.Name.StartsWith("path "));
        Assert.Contains(points, p => p.Name.StartsWith("car "));
        for (int i = 0; i < 400; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
        var snap = s.Snapshot();
        var dump = StateDump.Of(s);
        List<ulong> Go(int n)
        {
            var list = new List<ulong>();
            for (int i = 0; i < n; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); list.Add(s.StateHash()); }
            return list;
        }
        var first = Go(300);
        var last = points[^1];
        Assert.True(DevTools.Teleport(s, s.World.Player!, last.X, last.Z, last.Heading));
        Go(50);
        s.Restore(snap);
        SnapshotRig.TakeEvents(s);
        Assert.Null(StateDump.FirstDifference(dump, StateDump.Of(s)));
        var second = Go(300);
        for (int k = 0; k < first.Count; k++) Assert.True(first[k] == second[k], $"tick {k} after the restore: the run went its own way");
    }
}
