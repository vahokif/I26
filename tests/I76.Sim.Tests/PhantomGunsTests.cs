using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Nitro Pack's hidden F4D Phantom II has no HLOC of its own; the port patches two forward wing gun mounts and a belly dropper (no roof or turret mount) into
/// it as the .vdf is read (<see cref="VehiclePatches"/>, docs/hidden-content.md).
/// </summary>
public class PhantomGunsTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void OnlyThePatchGivesThePhantomItsHardpoints()
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var vdf = new Bwd2File(vfs.Read("vuphantm.vdf"));
        Assert.Empty(vdf.All("HLOC"));                                   // the install has none
        var hps = Hardpoint.Read(vdf);
        Assert.Equal([0u, 1u, 2u, 3u], hps.Select(h => h.Id));
        Assert.Equal([2u, 2u, 4u, 1u], hps.Select(h => h.Style));
        Assert.All(hps.Where(h => h.Style != 4), h => Assert.Equal(1u, h.Facing));
        Assert.Equal(1u, hps.Single(h => h.Style == 1).Id - 2);              // one roof-style (turret) mount: id 3, under the belly
        // every other body reads exactly its own records
        Assert.Equal(4, Hardpoint.Read(new Bwd2File(vfs.Read("vuhuey.vdf"))).Count);
        var belly = hps.Single(h => h.Style == 1);
        Assert.Equal(-1f, Rows.FromEngine(belly.Transform.Rotation).Up.Y);       // hangs upside down
        Assert.Equal(1f, Rows.FromEngine(belly.Transform.Rotation).Forward.Z);   // forward still along the nose
        Assert.Empty(VehiclePatches.ExtraHardpoints(new Bwd2File(vfs.Read("vuhuey.vdf"))));
        // the guns sit on the airframe: inside its box, wings at the sides, the nose centred
        var m = new ModelAssembler(vfs).Assemble("vuphant1.vcf")!;
        var all = m.Parts.SelectMany(p => p.Mesh.Vertices.Select(v => p.ToModel.Apply(v))).ToList();
        foreach (var h in hps)
        {
            var t = new Vector3(h.Transform.Translation[0], h.Transform.Translation[1], h.Transform.Translation[2]);
            Assert.InRange(t.X, all.Min(v => v.X), all.Max(v => v.X));
            Assert.InRange(t.Y, all.Min(v => v.Y), all.Max(v => v.Y));
            Assert.InRange(t.Z, all.Min(v => v.Z), all.Max(v => v.Z));
            if (h.Style == 2)
            {
                // under the wing (its underside at y 0.31, a swept panel whose leading edge runs from (1.33, 3.25) to (4.1, -0.2),
                // the trailing edge at z about -3.4), clear of the fuselage (|x| 1.37) and short of the tip (5.8)
                float x = MathF.Abs(t.X), leading = 3.25f + (x - 1.33f) * (-0.2f - 3.25f) / (4.1f - 1.33f);
                Assert.InRange(x, 1.5f, 5.5f);
                Assert.InRange(t.Y, 0f, 0.31f);
                Assert.InRange(t.Z, -3.4f, leading - 0.8f);
            }
            else if (h.Style == 1) { Assert.True(MathF.Abs(t.X) < 0.01f && t.Y is >= 0f and <= 0.3f && t.Z > 0, $"{h.Name} {t}: the belly turret under the fuselage ahead of the dropper"); Assert.True(t.Z < hps[2].Transform.Translation[2] + 6); }
            else Assert.True(MathF.Abs(t.X) < 0.01f && t.Y is >= 0f and <= 0.3f, $"{h.Name} {t}: the belly dropper under the fuselage");
        }
        Assert.True(hps[0].Transform.Translation[0] < -1 && hps[1].Transform.Translation[0] > 1);
        Assert.Equal(0, hps[2].Transform.Translation[0]);
    }

    private static TripCar Armed(AssetVfs vfs, PartsDatabase db, out ChassisModel model)
    {
        var car = TripCar.FromVcf(vfs.Read("vuphant1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var m = model = new ChassisModel(car, db, vfs, nitro: true, hidden: true);
        var gun = db.ByStem("gmlight.gdf")!;
        var mounts = model.Mounts();
        Assert.Equal(3, mounts.Count(x => x.Type == 7));
        Assert.Single(mounts, x => x.Type == 8);
        foreach (var mt in mounts.Where(x => x.Type == 7 && !ChassisRules.TurretMount(m.Inv.Slots[x.Slot]))) model.Fit(mt.Slot, gun);
        Assert.Single(m.Mounts(), x => ChassisRules.TurretMount(m.Inv.Slots[x.Slot]));
        return car;
    }

    [Fact]
    public void AVariantWithPhantomGunsRoundTripsAndPassesTheServersCheck()
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        Armed(vfs, db, out var model);
        var file = model.VariantFile();
        var back = TripCar.FromVcf(file);
        Assert.Equal(["gmlight.gdf", "gmlight.gdf"], back.Weapons.Where(w => w.Gdf != "null").OrderBy(w => w.Hardpoint).Select(w => w.Gdf));
        Assert.Equal(back.Weapons.Order(), TripCar.FromVcf(back.ToVcf()).Weapons.Order());      // ToVcf writes the WEPNs in reverse
        var served = CarConfigCheck.Check(vfs, db, "vuphant", file, out var why, allowHidden: true);
        Assert.True(served is not null, why);
        Assert.Equal(TripCar.FromVcf(file).Weapons.Where(w => w.Gdf != "null").OrderBy(w => w.Hardpoint), TripCar.FromVcf(served!).Weapons.Where(w => w.Gdf != "null").OrderBy(w => w.Hardpoint));
        Assert.Null(CarConfigCheck.Strict(vfs, TripCar.FromVcf(file), "vuphant"));
        // a gun on a hardpoint the body lacks is still refused
        var bad = TripCar.FromVcf(file);
        bad.Weapons.Add((6, "gmlight.gdf"));
        Assert.Null(CarConfigCheck.Check(vfs, db, "vuphant", bad.ToVcf(), out _, allowHidden: true));
    }

    [Fact]
    public void ThePhantomFiresForwardFromItsMounts()
    {
        if (NitroDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var db = PartsDatabase.Build(vfs);
        Armed(vfs, db, out var model);
        const string name = "zphantom.vcf";
        AssetVfs.Addon[name] = model.VariantFile();
        try
        {
            var asm = new ModelAssembler(vfs);
            var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "n01.cbt")!);
            var setup = MissionSetup.Build(vfs, w, asm, name, seed: 3);
            var s = new SimSession(setup);
            var me = setup.World.Player!;
            s.AddDriver(me);
            Assert.True(me.IsHeli);
            Assert.Equal(2, me.Armory!.Weapons.Count(x => x.Style == 2));
            var seen = new Dictionary<uint, (Vector3 Muzzle, Vector3 Forward)>();
            for (int t = 0; t < 120; t++)
            {
                me.Armory.Selected = t / 60;     // each gun in turn: the trigger fires the selected one
                s.Tick([new CarInput { Buttons = CarButtons.Fire }]);
                foreach (var e in setup.Combat.Events.OfType<ShotFired>().Where(e => e.Shooter == me))
                    seen.TryAdd(e.Weapon.Hardpoint.Id, (e.Muzzle, e.Forward));
                setup.Combat.Events.Clear(); setup.Damage.Events.Clear();
            }
            Assert.Contains(0u, seen.Keys);
            Assert.Contains(1u, seen.Keys);
            var nose = me.Rot.Forward;
            foreach (var (id, (muzzle, fwd)) in seen)
            {
                Assert.True(Vector3.Dot(Vector3.Normalize(fwd), Vector3.Normalize(nose)) > 0.99f, $"hardpoint {id} fires {fwd}, the nose points {nose}");
                Assert.True(Vector3.Dot(muzzle - me.Pos.ToVector3(), nose) > 0, $"hardpoint {id} muzzle behind the centre");
                var local = me.Rot.ToLocal(muzzle - me.Pos.ToVector3());
                Assert.Equal(id == 0 ? -1 : 1, MathF.Sign(local.X));      // the left wing gun is on the left
                Assert.InRange(MathF.Abs(local.X), 1.5f, 5.8f);
            }
        }
        finally { AssetVfs.Addon.TryRemove(name, out _); }
    }

    [Fact]
    public void TheBellyMountTakesATurretAndOnlyOne()
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        var car = TripCar.FromVcf(vfs.Read("vuphant1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var model = new ChassisModel(car, db, vfs, nitro: true, hidden: true);
        var belly = model.Mounts().Single(x => ChassisRules.TurretMount(model.Inv.Slots[x.Slot]));
        var turrets = model.CatalogueFor(belly.Slot).Where(c => c.Part.IsTurret).ToList();
        Assert.NotEmpty(turrets);                                                        // offered
        foreach (var w in model.Mounts().Where(x => x.Slot != belly.Slot && x.Type == 7))
            Assert.DoesNotContain(model.CatalogueFor(w.Slot), c => c.Part.IsTurret);     // never on a wing
        Assert.Null(model.Inv.Validate());                                               // the stock variant is valid
        model.Fit(belly.Slot, turrets[0].Part);
        Assert.Null(model.Inv.Validate());                                               // DONE accepts it
        var file = model.VariantFile();
        Assert.True(CarConfigCheck.Check(vfs, db, "vuphant", file, out var why, allowHidden: true) is not null, why);
        Assert.Equal(3u, (uint)TripCar.FromVcf(file).Weapons.Single(w => w.Gdf != "null" && w.Hardpoint == 3).Hardpoint);
        // every other body is untouched: the Huey's four, no extras
        Assert.Empty(VehiclePatches.ExtraHardpoints(new Bwd2File(vfs.Read("vuhuey.vdf"))));
    }

    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void TheBellyTurretAimsAtWhatLiesBelowAndAroundAndNeverUpThroughTheHull()
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        var car = TripCar.FromVcf(vfs.Read("vuphant1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var model = new ChassisModel(car, db, vfs, nitro: true, hidden: true);
        var belly = model.Mounts().Single(x => ChassisRules.TurretMount(model.Inv.Slots[x.Slot]));
        model.Fit(belly.Slot, model.CatalogueFor(belly.Slot).First(c => c.Part.IsTurret).Part);
        const string name = "zphantomt.vcf";
        AssetVfs.Addon[name] = model.VariantFile();
        try
        {
            foreach (var (tx, ty, tz) in new[] { (30.0, 0.0, 20.0), (-30.0, 0.0, 10.0), (5.0, 0.0, -40.0), (-20.0, 0.0, -20.0) })
            {
                var world = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
                var me = VehicleFactory.Create(vfs, name);
                me.Pos = new Double3(0, 30, 0);
                me.Flags = VehicleFlags.EngineOn;
                var target = new Vehicle { Pos = new Double3(tx, ty, tz), Flags = VehicleFlags.EngineOn };
                world.Vehicles.Add(me); world.Vehicles.Add(target); world.Player = me;
                var c = new Combat(world, _ => null);
                c.RadarTarget[me] = target;
                var w = me.Armory!.Weapons.Single(x => x.Style == 3);
                Assert.Equal(3u, w.Hardpoint.Id);
                for (int f = 0; f < 120; f++) c.Frame(0.036f);
                Assert.True(w.OnTarget, $"target ({tx},{tz})");
                // the barrel points at the target (below and to the side), out of the turret's own pivot
                var muzzle = me.Pos.ToVector3() + me.Rot.ToWorld(w.MuzzlePos);
                var dir2 = Vector3.Normalize(me.Rot.ToWorld(Rows.Concat(w.MuzzleRot, Rows.Identity).Forward));
                var want = Vector3.Normalize(new Vector3((float)tx, 0.8f, (float)tz) - muzzle);
                Assert.True(dir2.Y < -0.4f, $"fires {dir2}: down");
                Assert.True(Vector3.Dot(dir2, want) > 0.97f, $"fires {dir2}, target at {want}");
            }
            // a target above the plane: the barrel stays level at most, never up through the hull
            var world2 = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
            var me2 = VehicleFactory.Create(vfs, name);
            me2.Pos = new Double3(0, 5, 0);
            me2.Flags = VehicleFlags.EngineOn;
            var above = new Vehicle { Pos = new Double3(10, 80, 10), Flags = VehicleFlags.EngineOn };
            world2.Vehicles.Add(me2); world2.Vehicles.Add(above); world2.Player = me2;
            var c2 = new Combat(world2, _ => null);
            c2.RadarTarget[me2] = above;
            for (int f = 0; f < 120; f++) c2.Frame(0.036f);
            var barrel = me2.Rot.ToWorld(Rows.Concat(me2.Armory!.Weapons.Single(x => x.Style == 3).MuzzleRot, Rows.Identity).Forward);
            Assert.True(barrel.Y <= 0.01f, $"{barrel}");
        }
        finally { AssetVfs.Addon.TryRemove(name, out _); }
    }
}
