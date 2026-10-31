using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The 3D garage drives <see cref="ChassisModel"/>; the 2D CONFIGURE CHASSIS form edits a <see cref="TripInventory"/> directly
/// (the same operations, GarageScreen.cs). For the same choices they must write the same .vcf, so a variant made in either is
/// readable by the original game and checks out online exactly as before.
/// </summary>
public class ChassisModelTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (AssetVfs, PartsDatabase)? Open() =>
        InstallDir is { } dir && new AssetVfs(new GameInstall(dir)) is var vfs ? (vfs, PartsDatabase.Build(vfs)) : null;

    private static TripCar Stock(AssetVfs vfs, string vcf) => TripCar.FromVcf(vfs.Read(vcf), n => VehicleFactory.EngineCompId(vfs, n));

    private static PartDef Part(PartsDatabase db, string stem) => db.ByStem(stem) ?? throw new InvalidOperationException(stem);

    /// <summary>What the 2D form does for the same choices, written out as its code does it (a catalogue pick installs a node of its own).</summary>
    private static byte[] TwoD(AssetVfs vfs, PartsDatabase db, string vcf, string name, string paint, Action<TripInventory> picks, string makeModel)
    {
        var inv = TripInventory.FromCar(Stock(vfs, vcf), db, vfs);
        picks(inv);
        inv.Car.Variant = name;
        if (paint.Length > 0) inv.Car.Vtf = paint;
        var car = inv.ToCar();
        return car.ToVcf();
    }

    [Fact]
    public void TheSameChoicesWriteTheSameFileInBothForms()
    {
        if (Open() is not var (vfs, db)) return;
        foreach (var vcf in new[] { "vppirna1.vcf", "valepre1.vcf" })
        {
            if (vfs.TryRead(vcf) is null) continue;
            var m = new ChassisModel(Stock(vfs, vcf), db, vfs, nitro: false, hidden: false);
            var inv = TripInventory.FromCar(Stock(vfs, vcf), db, vfs);

            // engine, suspension, brakes: the catalogue's third of each
            foreach (int type in new[] { 2, 3, 4 })
            {
                var pick = m.Catalogue(type)[2].Part;
                int slot = m.SlotOf(type);
                m.Fit(slot, pick);
                inv.Install(inv.Slots.FindIndex(s => s.Type == type), new PartNode(pick, pick.MaxHp, PartState.Van), catalogue: true);
            }
            // a weapon into the first weapon slot, another stripped
            var mounts = m.Mounts();
            Assert.NotEmpty(mounts);
            var gun = m.Catalogue(7)[3].Part;
            m.Fit(mounts[0].Slot, gun);
            inv.Install(mounts[0].Slot, new PartNode(gun, gun.MaxHp, PartState.Van), catalogue: true);
            if (mounts.Count > 1)
            {
                m.Strip(mounts[1].Slot);
                if (inv.InSlot(mounts[1].Slot) is { } n) inv.Nodes.Remove(n);
            }
            // two specials, then the first again in the second slot (the same name leaves the first)
            var specials = m.SpecialSlots();
            var sp = m.Catalogue(0xc);
            m.Fit(specials[0].Slot, sp[1].Part, specials.Select(s => s.Slot));
            inv.Install(specials[0].Slot, new PartNode(sp[1].Part, 0, PartState.Van), catalogue: true, specials.Select(s => s.Slot));
            m.Fit(specials[1].Slot, sp[4].Part, specials.Select(s => s.Slot));
            inv.Install(specials[1].Slot, new PartNode(sp[4].Part, 0, PartState.Van), catalogue: true, specials.Select(s => s.Slot));
            // the car's own wheels in another size
            var own = Stock(vfs, vcf).Wdf[0];
            string sized = own[..7] + "c.wdf";
            var wheel = Part(db, sized);
            m.FitAxle(Axle.Front, wheel);
            m.FitAxle(Axle.Rear, wheel);
            foreach (var s in inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 5).Select(x => x.i).ToList())
                inv.Install(s, new PartNode(wheel, wheel.MaxHp, PartState.Van), catalogue: true);
            // defense: front armour down, rear chassis up, then BALANCE after one more step
            Assert.True(m.AdjustDefense(0, chassis: false, more: false));
            Assert.True(m.AdjustDefense(3, chassis: true, more: true));
            var c = inv.Car;
            c.Armour[0] -= 10; c.Ninth += 10; c.Chassis[3] += 10; c.Ninth -= 10;
            var paint = m.PaintSchemes().Last().Stem;
            Assert.NotEqual(Stock(vfs, vcf).Vtf, paint);
            m.SetPaint(paint);
            inv.Car.Vtf = paint;
            m.Rename("GarageTest");
            inv.Car.Variant = "GarageTest";

            var a = m.VariantFile();
            var car = inv.ToCar();
            ChassisRules.Secrets(car, inv.MakeModel, nitro: false);
            var b = car.ToVcf();
            Assert.Equal(b, a);
            // and the file is readable: the same car back
            var back = TripCar.FromVcf(a);
            Assert.Equal("GarageTest", back.Variant);
            Assert.Equal(sized, back.Wdf[0], ignoreCase: true);
            Assert.Equal(m.Inv.Weight(), inv.Weight());
            Assert.Equal(m.Inv.Validate(), inv.Validate());
        }
    }

    [Fact]
    public void ThePlainCarWritesWhatTheStockFileHolds()
    {
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        var plain = TwoD(vfs, db, "vppirna1.vcf", m.Inv.Car.Variant, "", _ => { }, m.MakeModel);
        Assert.Equal(plain, m.VariantFile());
    }

    [Fact]
    public void AnyCarsWheelsGoOnEitherAxleAndStayInTheFile()
    {
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        var all = m.AllWheels();
        Assert.Equal(vfs.Names.Count(n => n.EndsWith(".wdf", StringComparison.OrdinalIgnoreCase)), all.Count);
        var bus = Part(db, "wbtck_1b.wdf");
        var big = Part(db, "wauto_9d.wdf");
        m.FitAxle(Axle.Front, bus);
        m.FitAxle(Axle.Rear, big);
        Assert.Null(m.Inv.Validate());                       // each pair is the same wheel: the garage's check passes
        var car = TripCar.FromVcf(m.VariantFile());
        Assert.Equal("wbtck_1b.wdf", car.Wdf[0], ignoreCase: true);
        Assert.Equal("wauto_9d.wdf", car.Wdf[2], ignoreCase: true);
        // the server's check of a joining player's car accepts it and hands it back the same
        var served = CarConfigCheck.Check(vfs, db, "vppirna", m.VariantFile(), out var why);
        Assert.True(served is not null, why);
        var back = TripCar.FromVcf(served!);
        Assert.Equal(["wbtck_1b.wdf", "null", "wauto_9d.wdf"], back.Wdf.Select(w => w.ToLowerInvariant()));
        // the sim reads the new wheel's file for grip, mass and hit points
        AssetVfs.Addon["zzgarage.vcf"] = m.VariantFile();
        try
        {
            var v = VehicleFactory.Create(vfs, "zzgarage.vcf");
            Assert.Equal("wbtck_1b.wdf", v.Loadout.Wdf[0], ignoreCase: true);
            Assert.Equal("wauto_9d.wdf", v.Loadout.Wdf[2], ignoreCase: true);
            var stats = m.WheelStats(bus);
            Assert.NotNull(stats);
            Assert.Equal(stats!.Value.Grip, v.FrontRight.Grip, 3);
        }
        finally { AssetVfs.Addon.TryRemove("zzgarage.vcf", out _); }
        // opened again in the form: the same wheels, not the car's own
        var again = new ChassisModel(car, db, vfs, false, false);
        Assert.Equal("wbtck_1b.wdf", again.WheelStem(Axle.Front));
        Assert.Equal("wauto_9d.wdf", again.WheelStem(Axle.Rear));
        Assert.Equal(m.VariantFile(), again.VariantFile());
    }

    [Fact]
    public void ValidationIsTheFormsOwn()
    {
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        Assert.Null(m.Inv.Validate());
        // one tyre of a pair another size: the axles are not matched (27)
        var slots = m.WheelSlots(Axle.Front);
        m.Fit(slots[0], Part(db, "wauto_9d.wdf"));
        Assert.Equal(27, m.Inv.Validate());
        Assert.Equal(ChassisRules.DoneKind.Invalid, m.PlanDone(1, ["A"], 1).Kind);
        // no engine (24 would be tyres): the engine slot emptied
        var m2 = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        m2.Strip(m2.SlotOf(2));
        Assert.Equal(21, m2.Inv.Validate());
        // a second turret off the roof
        var m3 = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        var turrets = m3.Catalogue(7).Where(n => n.Part.IsTurret).ToList();
        if (turrets.Count > 0)
        {
            foreach (var mount in m3.Mounts().Where(x => x.Type == 7)) m3.Fit(mount.Slot, turrets[0].Part);
            Assert.NotNull(m3.Inv.Validate());
        }
    }

    [Fact]
    public void DoneAsksWhatTheFormAsks()
    {
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        string[] variants = ["Stock", "Mine"];
        Assert.Equal(ChassisRules.DoneKind.Unchanged, m.PlanDone(1, variants, 1).Kind);
        m.SetPaint("piranha2.vtf");
        Assert.Equal(ChassisRules.DoneKind.NeedsName, m.PlanDone(1, variants, 1).Kind);       // a stock variant: notice 30
        var over = m.PlanDone(2, variants, 1);
        Assert.Equal((ChassisRules.DoneKind.AskOverwrite, 2), (over.Kind, over.Save));
        m.Rename("Another");
        var fresh = m.PlanDone(2, variants, 1);
        Assert.Equal((ChassisRules.DoneKind.AskSaveNew, 3), (fresh.Kind, fresh.Save));
        m.Rename("mine");
        var named = m.PlanDone(1, variants, 1);
        Assert.Equal((ChassisRules.DoneKind.AskOverwrite, 2), (named.Kind, named.Save));
        m.Rename("stock");
        Assert.Equal(ChassisRules.DoneKind.NeedsName, m.PlanDone(1, variants, 1).Kind);
        m.RestoreEntry();
        Assert.False(m.Renamed);
        Assert.Equal(m.EntryName, m.Inv.Car.Variant);
        Assert.Equal(m.EntryVtf, m.Inv.Car.Vtf);
    }

    [Fact]
    public void TheCataloguesAreTheFormsOwn()
    {
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(Stock(vfs, "vppirna1.vcf"), db, vfs, false, false);
        Assert.Equal(4, m.Catalogue(2).Count);
        Assert.Equal(9, m.Catalogue(0xc).Count);
        Assert.All(m.Catalogue(8), n => Assert.Equal(8, n.Part.Type));
        Assert.DoesNotContain(m.Catalogue(7), n => n.Part.Type == 8);
        // the four mount groups of the Piranha as the 2D form lists them
        Assert.True(m.Mounts().Count >= 4);
    }
}
