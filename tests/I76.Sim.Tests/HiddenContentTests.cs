using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Sim.Vehicles;
using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The shells' hidden weapons, the Hidden Weapons &amp; Cars filter and WEAPON RESTRICTIONS (docs/hidden-content.md).</summary>
public class HiddenContentTests
{
    /// <summary>#53: a saved variant of a hidden model (the overlay) is the player's own, not a stock one: it can be overwritten and deleted.</summary>
    [Fact]
    public void ASavedVariantOfAHiddenModelIsNotStock()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var stem = HiddenContent.CompleteVehicles.First(s => vfs.InArchive(s + "1.vcf"));
        int stock = HiddenContent.StockVariants(vfs, stem);
        Assert.True(stock >= 1);
        try
        {
            AssetVfs.Addon[$"{stem}{stock + 1}.vcf"] = vfs.TryRead($"{stem}1.vcf")!;
            Assert.Equal(stock, HiddenContent.StockVariants(vfs, stem));
        }
        finally { AssetVfs.Addon.TryRemove($"{stem}{stock + 1}.vcf", out _); }
    }

    [Fact]
    public void TheBaseShellHidesTankOrdnanceAlwaysAndTheRestUnlessNatk()
    {
        foreach (var g in new[] { "gh45.gdf", "gtktank.gdf", "gtptank.gdf", "tthowitz.gdf" })
        {
            Assert.True(HiddenContent.ShellHides(g, nitro: false));
            Assert.True(HiddenContent.ShellHides(g, nitro: false, natk: true));
        }
        foreach (var g in new[] { "gchades.gdf", "tchades.gdf", "gscherub.gdf", "gfpyro.gdf", "tfpyro.gdf" })
        {
            Assert.True(HiddenContent.ShellHides(g, nitro: false));
            Assert.False(HiddenContent.ShellHides(g, nitro: false, natk: true));
        }
        Assert.True(HiddenContent.ShellHides("gceracer.gdf", false, natk: true));     // a dropper: natk does not open it
        Assert.False(HiddenContent.ShellHides("gmlight.gdf", false));
        Assert.False(HiddenContent.ShellHides("glandmin.gdf", false));
    }

    [Fact]
    public void NitroOffersItsOwnWeaponsTheBaseListWouldHide()
    {
        // the Nitro Pack's shorter list: the chemical mortar, 4Get-U-Not, Ball O'Flame and the caltrops are offered
        foreach (var g in new[] { "ggchem.gdf", "gs4get.gdf", "gsball.gdf", "tsball.gdf", "gcaltrop.gdf" })
        {
            Assert.False(HiddenContent.ShellHides(g, nitro: true));
            Assert.True(HiddenContent.ShellHides(g, nitro: false));
        }
        Assert.True(HiddenContent.ShellHides("tm125mg.gdf", nitro: true));
        Assert.False(HiddenContent.ShellHides("tm125mg.gdf", nitro: true, natk: true));
        Assert.True(HiddenContent.ShellHides("gceracer.gdf", nitro: true));
    }

    [Fact]
    public void TheSettingBringsBackOnlyTheCompleteHiddenWeapons()
    {
        foreach (var nitro in new[] { false, true })
        {
            foreach (var g in new[] { "gchades", "tchades", "gscherub", "tscherub", "gfpyro", "tfpyro", "gceracer", "gtktank", "gtptank", "tthowitz", "tm125mg", "tm125lmg" })
            {
                Assert.False(HiddenContent.Offered(g, nitro, natk: false, hidden: false));
                Assert.True(HiddenContent.Offered(g, nitro, natk: false, hidden: true));
            }
            // the handgun stays out: not a car weapon
            Assert.False(HiddenContent.Offered("gh45", nitro, natk: false, hidden: true));
            Assert.True(HiddenContent.Offered("gmlight", nitro, natk: false, hidden: false));
        }
    }

    [Fact]
    public void RestrictionsMatchByClassAndTier()
    {
        var w = WeaponRestrictions.Normalize(false, [(2, 6), (4, -1)]);
        Assert.True(w.Restricts(2, 6));       // HADES
        Assert.False(w.Restricts(2, 106));    // its turret is another row
        Assert.True(w.Restricts(4, 3));       // every missile, Cherub too
        Assert.True(w.Restricts(4, 105));     // and Ball O'Flame's turret, which is class 4
        Assert.False(w.Restricts(5, 0));
        Assert.True(WeaponRestrictions.Everything.Restricts(1, 0));
        Assert.False(WeaponRestrictions.None.Restricts(2, 0));
        // a whole class swallows its own rows; repeats go
        Assert.Equal([(4, -1)], WeaponRestrictions.Normalize(false, [(4, 3), (4, -1), (4, 3)]).Pairs);
        Assert.Same(WeaponRestrictions.None, WeaponRestrictions.Normalize(false, []));
        Assert.Equal(WeaponRestrictions.Normalize(false, [(2, 6)]), WeaponRestrictions.Normalize(false, [(2, 6)]));
    }

    [Fact]
    public void TheCommandLineListParsesAndPrintsBack()
    {
        var w = WeaponRestrictions.Parse("missiles, 2:6 ,flame,6:*", null, out var err)!;
        Assert.Equal("", err);
        Assert.Equal([(4, -1), (2, 6), (5, -1), (6, -1)], w.Pairs);
        Assert.Equal(w, WeaponRestrictions.Parse(w.ToSpec(), null, out _));
        Assert.True(WeaponRestrictions.Parse("slugs,all", null, out _)!.All);
        Assert.Null(WeaponRestrictions.Parse("lasers", null, out err));
        Assert.Contains("lasers", err);
    }

    /// <summary>A few rows laid out as the Nitro Pack's table: All Weapons, the slug throwers' header, two guns, the flamethrowers' header, a flamer and Ball O'Flame (class 4).</summary>
    private static List<RestrictionRow> Rows() =>
    [
        new(0, -1, -1, 3, 17, "All Weapons"),
        new(1, 2, -1, 3, 0, "All Slug Throwers"), new(1, 2, 0, 4, 1, "30cal MG"), new(1, 2, 100, 5, 1, "30cal MG Turret"),
        new(4, 5, -1, 0, 6, "All Flame Throwers"), new(4, 5, 0, 1, 7, "Flamethrower"), new(4, 4, 5, 1, 10, "BallOFlame"),
        new(1, 2, 6, 4, 13, "HADES Cannon"),        // the port's row, in the slug throwers' group
    ];

    [Fact]
    public void AHeaderTicksItsGroupAndMakesAWholeClass()
    {
        var c = new RestrictionChecklist(Rows());
        c.Toggle(4);                                   // All Flame Throwers
        Assert.True(c.Checked[5] && c.Checked[6]);
        // the header is class 5; Ball O'Flame (class 4) is kept as its own pair, as the original does
        Assert.Equal([(5, -1), (4, 5)], c.Build().Pairs);
        c.Toggle(5);                                   // a row off under the header clears the header
        Assert.False(c.Checked[4]);
        Assert.Equal([(4, 5)], c.Build().Pairs);
        c.Toggle(1);                                   // All Slug Throwers covers the hidden HADES row too
        Assert.True(c.Checked[7]);
        Assert.Contains((2, -1), c.Build().Pairs);
        Assert.True(c.Build().Restricts(2, 6));
    }

    [Fact]
    public void AllWeaponsIsEverythingAndAnyRowOffClearsIt()
    {
        var c = new RestrictionChecklist(Rows());
        c.Toggle(0);
        Assert.True(c.Build().All);
        Assert.False(c.Checked[2]);                     // it ticks nothing else
        c.Toggle(2); c.Toggle(2);                       // a row on, then off
        Assert.False(c.Checked[0]);
        Assert.True(c.Build().IsEmpty);
    }

    [Fact]
    public void AKeptListTicksTheBoxesBack()
    {
        var c = new RestrictionChecklist(Rows());
        c.Load(WeaponRestrictions.Normalize(false, [(2, -1), (4, 5)]));
        Assert.Equal(new[] { false, true, true, true, false, false, true, true }, c.Checked);
        Assert.Equal(WeaponRestrictions.Normalize(false, [(2, -1), (4, 5)]), c.Build());
    }

    /// <summary>The Armadillo (vdillo, the scenery critter) is a hidden model on both games: found by <c>HiddenModels</c>, unarmed (no hardpoints, so no weapon slots) and never in the stock list the A.I. picks from.</summary>
    [Theory]
    [InlineData("I76_INSTALL")]
    [InlineData("I76_NITRO_INSTALL")]
    public void TheArmadilloIsAnUnarmedHiddenModel(string env)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        Assert.Contains("vdillo", HiddenContent.HiddenModels(vfs, new HashSet<string>()));
        Assert.Equal("Armadillo", HiddenContent.ModelName(vfs, "vdillo"));
        Assert.Contains("vdillo", HiddenContent.CompleteVehicles);
        var car = TripCar.FromVcf(vfs.Read("vdillo1.vcf"), n => VehicleFactory.EngineCompId(vfs, n));
        var inv = TripInventory.FromCar(car, PartsDatabase.Build(vfs), vfs);
        Assert.DoesNotContain(inv.Slots, sl => sl.Type is 7 or 8);        // no guns, no droppers
        Assert.Null(HiddenContent.Check(vfs, new HashSet<string>(), nitro: false, allow: true, "vdillo", []));
        Assert.Contains("hidden car", HiddenContent.Check(vfs, new HashSet<string>(), nitro: false, allow: false, "vdillo", []));
    }
}
