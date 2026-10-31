using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Which parts each slot of the CONFIGURE CHASSIS form takes. The original's pickers list every part of a slot's kind and DONE refuses a
/// turret anywhere but a roof turret mount (notice 28) or a second turret (notice 26); the garage offers only what DONE
/// would pass, from the same <see cref="ChassisRules.Accepts"/>.
/// </summary>
public class ChassisRulesTests
{
    // a weapon of each kind as the parts database types it: a gun (7), an MG-class gun (6), a launcher (9, 10), a turret (11 with subtype 0xb), a dropper (8)
    private static PartDef Gun(string stem, int type = 7) => new("gun", type, 0, "slg01", stem, 1, 100, 10);
    private static readonly PartDef Mg = Gun("gmg.gdf", 7), Cannon = Gun("gcn.gdf", 6), Launcher = Gun("gfr.gdf", 9), Mine = new("mine", 8, 0, "dro01", "gmine.gdf", 1, 100, 10),
        Turret = new("turret", 11, 0xb, "slg11", "tgmg.gdf", 1, 100, 10), Radar = new("Radar Jammer", 12, 0, "spc01", "spc01", 0, 0, 0), Engine = new("eng", 2, 0, "eng01", "eng01", 1, 1, 1);

    private static CarSlot Weapon(int style, int facing = 1) => new(7, "HP", 3, style, facing);
    private static CarSlot Dropper => new(8, "HP", 4, 4, 2);

    /// <summary>HLOC styles: 1 P (roof, forward), 2 S (side), 3 (never in a file, forced for turrets), 4 D (dropper), 5 I.</summary>
    public static IEnumerable<object[]> Styles() => [[1], [2], [3], [4], [5]];

    [Theory, MemberData(nameof(Styles))]
    public void EveryHardpointStyleAgainstEveryWeaponClass(int style)
    {
        // a gun-class weapon slot (style 4 makes a dropper slot, as FromCar types it): any gun on any weapon mount, no dropper, no turret off a roof mount
        var slot = style == 4 ? Dropper with { MountStyle = 4 } : Weapon(style);
        if (style == 4)
        {
            Assert.True(ChassisRules.Accepts(slot, Mine));
            foreach (var p in new[] { Mg, Cannon, Launcher, Turret, Radar, Engine }) Assert.False(ChassisRules.Accepts(slot, p), p.Id);
            return;
        }
        foreach (var p in new[] { Mg, Cannon, Launcher }) Assert.True(ChassisRules.Accepts(slot, p), p.Id);
        foreach (var p in new[] { Mine, Radar, Engine }) Assert.False(ChassisRules.Accepts(slot, p), p.Id);
        // the turret: the roof mount (style 1) only, which is DONE's own test as well
        Assert.Equal(style == 1, ChassisRules.Accepts(slot, Turret));
        Assert.Equal(style == 1, ChassisRules.TurretMount(slot));
    }

    [Fact]
    public void SpecialsAndComponentsTakeTheirOwnKindOnly()
    {
        var special = new CarSlot(0xc, "Special 1");
        Assert.True(ChassisRules.Accepts(special, Radar));
        Assert.True(ChassisRules.Accepts(special, Radar with { Type = 13 }));
        Assert.False(ChassisRules.Accepts(special, Mg));
        Assert.True(ChassisRules.Accepts(new CarSlot(2, "ENGINE"), Engine));
        Assert.False(ChassisRules.Accepts(new CarSlot(3, "SUSP"), Engine));
        Assert.True(ChassisRules.Accepts(new CarSlot(4, "BRAKE"), Engine with { Type = 1 }));
        Assert.True(ChassisRules.Accepts(new CarSlot(4, "BRAKE"), Engine with { Type = 4 }));
        Assert.False(ChassisRules.Accepts(new CarSlot(5, "WHL FR"), Engine));
    }

    [Fact]
    public void ADonePassesExactlyWhatTheSlotsAccept()
    {
        // DONE's check (TripInventory.Validate) and the slot rule agree: a turret on a style-1 slot passes, on any other it is notice 28
        foreach (int style in new[] { 1, 2, 3, 5 })
        {
            var slot = Weapon(style);
            Assert.Equal(ChassisRules.Accepts(slot, Turret), ChassisRules.TurretMount(slot));
        }
    }

    // ---- against the install: every stock car, every hardpoint ----

    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EveryStockCarsOwnGunsPassItsSlotsAndNoTurretIsOfferedOffTheRoof()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(dir));
        var db = PartsDatabase.Build(vfs);
        int checkedSlots = 0, turretsOffered = 0;
        foreach (var (stem, n) in I76.Formats.Shell.CarTable.BaseModels.Select(m => (m.Stem, m.Variants)))
            for (int v = 1; v <= n; v++)
            {
                if (vfs.TryRead($"{stem}{v}.vcf") is not { } bytes) continue;
                var car = TripCar.FromVcf(bytes, c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c));
                var m = new ChassisModel(car, db, vfs, nitro: false, hidden: true);
                foreach (var mount in m.Mounts())
                {
                    var slot = m.Inv.Slots[mount.Slot];
                    var offered = m.CatalogueFor(mount.Slot);
                    checkedSlots++;
                    // what the car came with is allowed there (it is stock), when the catalogue shows it at all
                    if (m.Fitted(mount.Slot) is { } own && m.Catalogue(slot.Type).Any(c => c.Part.Stem == own.Part.Stem))
                        Assert.Contains(offered, c => c.Part.Stem == own.Part.Stem);
                    foreach (var c in offered)
                    {
                        Assert.True(ChassisRules.TypeFits(slot.Type, c.Part.Type));
                        if (c.Part.IsTurret) { turretsOffered++; Assert.Equal(1, slot.MountStyle); }
                    }
                    // everything the 2D form's catalogue lists and this drops is a turret off the roof (or a second one)
                    foreach (var c in m.Catalogue(slot.Type).Where(c => offered.All(o => o.Part.Stem != c.Part.Stem)))
                        Assert.True(c.Part.IsTurret);
                }
            }
        Assert.True(checkedSlots > 20);
        Assert.True(turretsOffered > 0, "the roof mounts do offer turrets");
    }

    private static (I76.Formats.Vfs.AssetVfs, PartsDatabase)? Open() =>
        InstallDir is { } dir && new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(dir)) is var vfs ? (vfs, PartsDatabase.Build(vfs)) : null;

    private static ChassisModel Model(I76.Formats.Vfs.AssetVfs vfs, PartsDatabase db, string vcf) =>
        new(TripCar.FromVcf(vfs.Read(vcf), c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c)), db, vfs, nitro: false, hidden: false);

    [Fact]
    public void ASecondTurretIsNotOfferedAndDoneAgreesWithTheSlotRule()
    {
        if (Open() is not var (vfs, db)) return;
        var m = Model(vfs, db, "vppirna1.vcf");               // two roof mounts, a rear gun mount (style 5)
        var tops = m.Mounts().Where(x => m.Inv.Slots[x.Slot].MountStyle == 1).ToList();
        var rear = m.Mounts().First(x => m.Inv.Slots[x.Slot].MountStyle == 5);
        Assert.Equal(2, tops.Count);
        var turret = m.CatalogueFor(tops[0].Slot).First(c => c.Part.IsTurret).Part;
        Assert.DoesNotContain(m.CatalogueFor(rear.Slot), c => c.Part.IsTurret);          // not off the roof
        m.Fit(tops[0].Slot, turret);
        Assert.Contains(m.CatalogueFor(tops[0].Slot), c => c.Part.IsTurret);              // it may be changed for another
        Assert.DoesNotContain(m.CatalogueFor(tops[1].Slot), c => c.Part.IsTurret);        // but not doubled (notice 26)
        Assert.Null(m.Inv.Validate());
        // what the 2D form would let through, DONE refuses as the garage does not offer it
        m.Fit(tops[1].Slot, turret);
        Assert.Equal(26, m.Inv.Validate());
        m.Strip(tops[0].Slot);
        m.Strip(tops[1].Slot);
        m.Fit(rear.Slot, turret);
        Assert.Equal(28, m.Inv.Validate());
    }

    [Fact]
    public void AHelicopterTakesATurretOnItsRoofMountsOnlyAsAnyCarDoes()
    {
        // the Huey's two top mounts (style 1) take a turret; its side guns (style 2, "Fwd") do not: the 2D form lets the pick through and DONE
        // refuses it (notice 28), the 3D garage does not offer it, one rule for the helicopters as for the cars (issue #131)
        if (Open() is not var (vfs, db)) return;
        var m = new ChassisModel(TripCar.FromVcf(vfs.Read("vuchuey1.vcf"), c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c)), db, vfs, nitro: false, hidden: true);   // Hidden Weapons & Cars: the turrets
        var sides = m.Mounts().Where(x => m.Inv.Slots[x.Slot].MountStyle == 2).ToList();
        var tops = m.Mounts().Where(x => m.Inv.Slots[x.Slot].MountStyle == 1).ToList();
        Assert.NotEmpty(sides);
        Assert.NotEmpty(tops);
        foreach (var side in sides) Assert.DoesNotContain(m.CatalogueFor(side.Slot), c => c.Part.IsTurret);
        foreach (var t in tops) m.Strip(t.Slot);                 // the Huey's own turrets out: one is offered again
        var turret = m.CatalogueFor(tops[0].Slot).First(c => c.Part.IsTurret).Part;
        m.Fit(sides[0].Slot, turret);
        Assert.Equal(28, m.Inv.Validate());
        // and the stock Huey, two turrets on its two roof mounts, still goes
        var stock = new ChassisModel(TripCar.FromVcf(vfs.Read("vuchuey1.vcf"), c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c)), db, vfs, nitro: false, hidden: true);
        Assert.Null(stock.Inv.Validate());
    }

    [Fact]
    public void EveryStyleAtEverySizeWritesItsOwnFile()
    {
        if (Open() is not var (vfs, db)) return;
        var m = Model(vfs, db, "vppirna1.vcf");
        var all = m.AllWheels();
        foreach (var family in WheelStyles.Families(all))
            foreach (var size in WheelStyles.Sizes(all, family))
            {
                var pick = WheelStyles.Pick(all, family, WheelStyles.SizeOf(size.Stem), out bool exact)!;
                Assert.True(exact);
                m.FitAxle(Axle.Front, pick);
                Assert.Equal($"{family}{WheelStyles.SizeOf(size.Stem)}.wdf", m.Snapshot().Wdf[0], ignoreCase: true);
                Assert.Equal(pick.Stem, m.WheelStem(Axle.Front));
            }
        // a size the style lacks is the nearest one
        var single = all.First(w => WheelStyles.Sizes(all, WheelStyles.Family(w.Stem)).Count == 1);
        Assert.Equal(single.Stem, WheelStyles.Pick(all, WheelStyles.Family(single.Stem), 'd', out bool near)!.Stem);
        Assert.False(near);
    }
}
