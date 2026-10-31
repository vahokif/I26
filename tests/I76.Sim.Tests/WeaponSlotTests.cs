using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// CONFIGURE CHASSIS's weapon rows: one per hardpoint of the body, in its HLOC order, whatever
/// WEPNs the file has and in whatever order. Checked against the Nitro Pack with a Piranha file holding only hardpoints 3 and 1, in
/// that order: the form showed "#1 Top Empty", "#2 Top 7.62mm MG", "#1 Rear Gas Launcher", "#1 Dropper Empty".
/// </summary>
public class WeaponSlotTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static (AssetVfs, PartsDatabase)? Open() =>
        InstallDir is { } dir && new AssetVfs(new GameInstall(dir)) is var vfs ? (vfs, PartsDatabase.Build(vfs)) : null;

    private static TripCar Piranha(AssetVfs vfs) => TripCar.FromVcf(vfs.Read("vppirna1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));

    /// <summary>The weapon and dropper rows: hardpoint and the part's file, "null" for an empty one, in slot order.</summary>
    private static List<(int, string)> Rows(TripInventory inv) =>
        [.. inv.Slots.Select((s, i) => (s, i)).Where(x => x.s.Hardpoint >= 0).Select(x => (x.s.Hardpoint, inv.InSlot(x.i)?.Part.Stem ?? "null"))];

    private static TripInventory Reload(TripInventory inv, AssetVfs vfs, PartsDatabase db) =>
        TripInventory.FromCar(TripCar.FromVcf(inv.ToCar().ToVcf(), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);

    [Fact]
    public void ASavedVariantKeepsItsWeaponsInTheirRowsSaveAfterSave()
    {
        if (Open() is not var (vfs, db)) return;
        var inv = TripInventory.FromCar(Piranha(vfs), db, vfs);
        var rows = Rows(inv);
        Assert.Equal([0, 1, 2, 3], rows.Select(r => r.Item1));
        // the shell writes the WEPNs in reverse order: reading them back must not swap the two Top guns
        for (int k = 0; k < 3; k++)
        {
            inv = Reload(inv, vfs, db);
            Assert.Equal(rows, Rows(inv));
        }
    }

    [Fact]
    public void ANetworkCarComesBackFromTheServersCheckInTheSameRows()
    {
        if (Open() is not var (vfs, db)) return;
        var car = Piranha(vfs);
        var inv = TripInventory.FromCar(car, db, vfs);
        var mine = inv.ToCar();
        mine.Engine = TripCar.FromVcf(vfs.Read("vppirna1.vcf")).Engine;       // the file carries the ENG NUM, as the garage writes it
        var sent = mine.ToVcf();
        var served = CarConfigCheck.Check(vfs, db, "vppirna", sent, out var why);
        Assert.True(served is not null, why);
        // the player's garage reopening what it sent, and the car the server serves (rewritten, its WEPNs reversed again)
        TripInventory Read(byte[] f) => TripInventory.FromCar(TripCar.FromVcf(f, n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        Assert.Equal(Rows(inv), Rows(Read(sent)));
        Assert.Equal(Rows(inv), Rows(Read(served!)));
    }

    [Fact]
    public void AVariantWithEmptyMountsStillOffersThemAll()
    {
        if (Open() is not var (vfs, db)) return;
        var car = Piranha(vfs);
        // as the original's garage writes a car: WEPNs only for what is fitted, last first
        car.Weapons = [(3, "gmheavy.gdf"), (1, "gfmedium.gdf")];
        var vcf = car.ToVcf();
        Assert.Equal(2, new Bwd2File(vcf).WeaponMounts().Count);
        var inv = TripInventory.FromCar(TripCar.FromVcf(vcf, n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        Assert.Equal([(0, "null"), (1, "gfmedium.gdf"), (2, "null"), (3, "gmheavy.gdf")], Rows(inv));
        Assert.Equal([8, 7, 7, 7], inv.Slots.Where(s => s.Hardpoint >= 0).Select(s => s.Type));

        // a race car: no WEPN at all, every mount still there to fit a gun to
        car.Weapons = [];
        inv = TripInventory.FromCar(TripCar.FromVcf(car.ToVcf(), n => VehicleFactory.EngineCompId(vfs, n)), db, vfs);
        Assert.Equal([(0, "null"), (1, "null"), (2, "null"), (3, "null")], Rows(inv));
        inv = Reload(inv, vfs, db);
        Assert.Equal(4, Rows(inv).Count);
    }
}
