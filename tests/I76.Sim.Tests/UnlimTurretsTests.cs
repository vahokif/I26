using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The port's Unlim. Turrets (Play Options; docs/deviations.md): the garage's one-turret rule (DONE's notice 26) is lifted, the roof-mount rule
/// (notice 28) stays; and the sim handles a car with several turrets as the original's helicopters (two) show it can.
/// </summary>
public class UnlimTurretsTests
{
    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static (AssetVfs, PartsDatabase)? Open() =>
        InstallDir is { } dir && new AssetVfs(new GameInstall(dir)) is var vfs ? (vfs, PartsDatabase.Build(vfs)) : null;

    private static ChassisModel Model(AssetVfs vfs, PartsDatabase db, bool unlim) =>
        new(TripCar.FromVcf(vfs.Read("vppirna1.vcf"), c => VehicleFactory.EngineCompId(vfs, c)), db, vfs, nitro: false, hidden: false) { UnlimTurrets = unlim };

    /// <summary>The Piranha with a turret on each of its two roof mounts, as the garage with the option on makes it.</summary>
    private static TripCar TwoTurrets(AssetVfs vfs, PartsDatabase db, out ChassisModel m)
    {
        var mm = m = Model(vfs, db, true);
        var tops = mm.Mounts().Where(x => mm.Inv.Slots[x.Slot].MountStyle == 1).ToList();
        foreach (var t in tops) mm.Fit(t.Slot, mm.CatalogueFor(t.Slot).First(c => c.Part.IsTurret).Part);
        return mm.Inv.ToCar();
    }

    [Fact]
    public void TheGaragesListsAndDoneLiftOnlyTheOneTurretRule()
    {
        if (Open() is not var (vfs, db)) return;
        var off = Model(vfs, db, false);
        var on = Model(vfs, db, true);
        var tops = on.Mounts().Where(x => on.Inv.Slots[x.Slot].MountStyle == 1).ToList();
        var rear = on.Mounts().First(x => on.Inv.Slots[x.Slot].MountStyle == 5);
        var turret = on.CatalogueFor(tops[0].Slot).First(c => c.Part.IsTurret).Part;
        on.Fit(tops[0].Slot, turret); off.Fit(tops[0].Slot, turret);
        Assert.DoesNotContain(off.CatalogueFor(tops[1].Slot), c => c.Part.IsTurret);     // off: as the original's rule
        Assert.Contains(on.CatalogueFor(tops[1].Slot), c => c.Part.IsTurret);
        Assert.DoesNotContain(on.CatalogueFor(rear.Slot), c => c.Part.IsTurret);          // a turret still only on a roof mount
        on.Fit(tops[1].Slot, turret);
        Assert.Equal(26, on.Inv.Validate());                                              // the original's DONE
        Assert.Null(on.Inv.Validate(unlimTurrets: true));
        Assert.NotEqual(ChassisRules.DoneKind.Invalid, on.PlanDone(1, ["a"], 1).Kind);   // the model's own flag
        Assert.Equal(ChassisRules.DoneKind.Invalid, ChassisRules.PlanDone(on.Inv, true, true, 1, ["a"], 1).Kind);
        on.Fit(rear.Slot, turret);
        Assert.Equal(28, on.Inv.Validate(unlimTurrets: true));                            // off the roof: refused with the option on too
    }

    [Fact]
    public void TheServerAcceptsSeveralTurretsOnlyWhereTheHostAllowsThem()
    {
        if (Open() is not var (vfs, db)) return;
        var car = TwoTurrets(vfs, db, out _);
        car.Engine = TripCar.FromVcf(vfs.Read("vppirna1.vcf")).Engine;
        var sent = car.ToVcf();
        Assert.Null(CarConfigCheck.Check(vfs, db, "vppirna", sent, out var why));
        Assert.Contains("26", why);
        var served = CarConfigCheck.Check(vfs, db, "vppirna", sent, out why, allowTurrets: true);
        Assert.True(served is not null, why);
        var back = TripCar.FromVcf(served!);
        Assert.Equal(2, back.Weapons.Count(w => w.Gdf.StartsWith('t')));
        Assert.StartsWith("a second turret", CarConfigCheck.Strict(vfs, back, "vppirna"));
        Assert.Null(CarConfigCheck.Strict(vfs, back, "vppirna", allowTurrets: true));
        // a bot's car under a host that does not allow them keeps the first turret only
        var kept = CarConfigCheck.WithoutExtraTurrets(vfs, TripCar.FromVcf(served!), "vppirna");
        Assert.NotNull(kept);
        Assert.Equal(1, kept!.Weapons.Count(w => w.Gdf.StartsWith('t')));
        Assert.Null(CarConfigCheck.Strict(vfs, kept, "vppirna"));
        Assert.Null(CarConfigCheck.WithoutExtraTurrets(vfs, kept, "vppirna"));
    }

    [Fact]
    public void ACarWithTwoRoofTurretsAimsFiresAndKeepsEachGizmo()
    {
        if (Open() is not var (vfs, db)) return;
        var file = "zturrets1.vcf";
        AssetVfs.Addon[file] = TwoTurrets(vfs, db, out _).ToVcf();
        try
        {
            var world = new SimWorld(new Flat(), new CrtRand(1));
            var car = VehicleFactory.Create(vfs, file);
            car.Flags = VehicleFlags.EngineOn;
            var target = new Vehicle { Pos = new Double3(40, 0, 20), Flags = VehicleFlags.EngineOn };
            world.Vehicles.Add(car); world.Vehicles.Add(target); world.Player = car;
            var c = new Combat(world, _ => null);
            c.RadarTarget[car] = target;
            var a = car.Armory!;
            var turrets = a.LoadOrder.Where(w => w.Style == 3).ToList();
            Assert.Equal(2, turrets.Count);
            Assert.NotEqual(turrets[0].Hardpoint.Id, turrets[1].Hardpoint.Id);
            // each tracks the target from its own pivot
            for (int f = 0; f < 80; f++) c.Frame(0.036f);
            Assert.All(turrets, t => Assert.True(t.OnTarget));
            Assert.NotEqual(turrets[0].AimYaw, turrets[1].AimYaw);        // two mounts, two lines of fire at one target
            // both fire from the trigger of their group
            var keys = new bool[5];
            for (int i = 0; i < a.Weapons.Count && i < keys.Length; i++) if (a.Weapons[i].Style == 3) keys[i] = true;
            int shots = 0;
            for (int f = 0; f < 30; f++)
            {
                a.PlayerTriggers(false, keys);
                c.Frame(0.036f);
                shots += c.Events.OfType<ShotFired>().Count(e => turrets.Contains(e.Weapon));
                c.Events.Clear();
            }
            Assert.True(shots > 2, $"{shots} shots");
            // the link key groups the two turrets (one mount kind and class), and one trigger fires the pair
            a.Selected = a.Weapons.IndexOf(turrets[0]);
            a.Select(false, true, out _);
            Assert.Equal([a.Weapons.IndexOf(turrets[1])], a.Group);
            foreach (var t in turrets) t.Ammo = (int)t.Def.Ammo;
            a.PlayerTriggers(true, new bool[5]);
            c.Frame(0.036f);
            Assert.All(turrets, t => Assert.True(t.Trigger || t.Ammo < t.Def.Ammo, "the linked pair fires on one trigger"));
            Assert.All(turrets, t => Assert.True(t.Ammo < t.Def.Ammo || t.Def.Ammo <= 0, "each turret spent ammo"));
        }
        finally { AssetVfs.Addon.Remove(file, out _); }
    }

    [Fact]
    public void ASavestateOfACarWithTwoTurretsRestoresTheirAim()
    {
        if (SnapshotRig.Install is not { } dir || Open() is not var (vfs, db)) return;
        var file = "zturrets1.vcf";
        AssetVfs.Addon[file] = TwoTurrets(vfs, db, out _).ToVcf();
        try
        {
            var l = new SnapshotRig.Loaded(dir);
            var b = SnapshotRig.BuildOf(dir, "t01", car: file);
            var (s, _) = SnapshotRig.Make(l, b, I76.Sim.Session.SimRate.R20, 5);
            for (int i = 0; i < 40; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
            var turrets = s.World.Player!.Armory!.LoadOrder.Where(w => w.Style == 3).ToList();
            Assert.Equal(2, turrets.Count);
            turrets[0].AimYaw = 0.7f; turrets[1].AimYaw = -0.4f; turrets[1].AimPitch = 0.2f;
            var snap = s.Snapshot();
            var dump = I76.Sim.Save.StateDump.Of(s);
            var (fresh, _) = SnapshotRig.Make(l, b, I76.Sim.Session.SimRate.R20, 99);
            fresh.Restore(snap);
            var back = fresh.World.Player!.Armory!.LoadOrder.Where(w => w.Style == 3).ToList();
            Assert.Equal([0.7f, -0.4f], back.Select(w => w.AimYaw));
            Assert.Equal(0.2f, back[1].AimPitch);
            Assert.Null(I76.Sim.Save.StateDump.FirstDifference(dump, I76.Sim.Save.StateDump.Of(fresh)));
        }
        finally { AssetVfs.Addon.Remove(file, out _); }
    }
}
