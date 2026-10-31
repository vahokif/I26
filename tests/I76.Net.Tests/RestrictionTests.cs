using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Trip;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The server's rule for a joiner's car (docs/hidden-content.md §5): WEAPON RESTRICTIONS and Hidden Weapons &amp; Cars, checked
/// by the server against its own files for every car (an install car asked by name, the car standing in for one it lacks, a
/// player's own variant), refused with the weapons named.
/// </summary>
public class RestrictionTests
{
    private static string? Nitro
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e ? e : Environment.GetEnvironmentVariable("I76_NITRO");
            return d is { Length: > 0 } && Directory.Exists(d) ? d : null;
        }
    }

    private static MeleeHost Host(WeaponRestrictions? r = null, bool hidden = false, string? install = null, string arena = "m01") =>
        new(new ServerOptions { Install = install ?? HostTests.InstallDir!, Arena = arena, Restrictions = r ?? WeaponRestrictions.None, AllowHidden = hidden });

    /// <summary>A custom Piranha (variant 1's body and name changed), its weapons edited.</summary>
    private static byte[] Custom(AssetVfs vfs, Action<TripCar> edit)
    {
        var car = TripCar.FromVcf(vfs.Read("vppirna1.vcf"));
        car.Variant = "Mine";
        edit(car);
        return car.ToVcf();
    }

    private static WeaponRestrictions R(params (int, int)[] p) => WeaponRestrictions.Normalize(false, p);

    [Fact]
    public void AStockCarWithARestrictedWeaponIsRefusedByName()
    {
        if (HostTests.InstallDir is null) return;
        // vppirna1 carries landmines (6:3), a gas launcher, a 50cal and a 25mm cannon
        var host = Host(R((6, 3)));
        var why = host.CheckJoin("vppirna1.vcf", null);
        Assert.NotNull(why);
        Assert.Contains("Restricted weapons", why);
        Assert.Contains("Landmines", why);
        Assert.Null(host.CheckJoin("vcgcour1.vcf", null));               // the Courchevel's first variant has none
        // the server's own answer says so, for the join form
        Assert.Equal(R((6, 3)), host.Info().Restricted);
    }

    [Fact]
    public void AWholeClassAndAllWeaponsCoverEverythingOfIt()
    {
        if (HostTests.InstallDir is null) return;
        Assert.Contains("Restricted", Host(R((6, -1))).CheckJoin("vppirna1.vcf", null));
        Assert.Contains("Restricted", Host(WeaponRestrictions.Everything).CheckJoin("vcgcour1.vcf", null));
        Assert.Null(Host(R((5, -1))).CheckJoin("vcgcour1.vcf", null));    // no flamethrower on it
    }

    [Fact]
    public void AnOwnVariantIsCheckedToo()
    {
        if (HostTests.InstallDir is null) return;
        var host = Host(R((2, 1)));                                        // the 50cal MG
        var vfs = host.Vfs;
        var clean = Custom(vfs, c => c.Weapons = [.. c.Weapons.Where(w => !w.Gdf.StartsWith("gmmedium", StringComparison.OrdinalIgnoreCase))]);
        Assert.Null(host.CheckJoin("vppirna4.vcf", clean));
        var armed = Custom(vfs, _ => { });                                 // variant 1's own guns, the 50cal among them
        var why = host.CheckJoin("vppirna4.vcf", armed);
        Assert.NotNull(why);
        Assert.Contains("50cal", why);
    }

    [Fact]
    public void TheCarStandingInForAMissingOneIsChecked()
    {
        if (HostTests.InstallDir is null) return;
        // vppirna9.vcf is not in the install: the join gets vppirna1, which carries landmines
        Assert.Contains("Restricted", Host(R((6, 3))).CheckJoin("vppirna9.vcf", null));
    }

    [Fact]
    public void AHiddenCarIsRefusedUnlessTheHostAllowsHiddenContent()
    {
        if (HostTests.InstallDir is null) return;
        var why = Host().CheckJoin("vstank1.vcf", null);
        Assert.NotNull(why);
        Assert.Contains("Hidden Weapons & Cars off", why);
        Assert.Null(Host(hidden: true).CheckJoin("vstank1.vcf", null));    // its tank cannon comes with it
        Assert.Null(Host(hidden: true).CheckJoin("vuchuey1.vcf", null));
        // the Armadillo: a hidden car like the others, allowed with the setting
        Assert.Contains("Armadillo", Host().CheckJoin("vdillo1.vcf", null));
        Assert.Contains("Hidden Weapons & Cars off", Host().CheckJoin("vdillo1.vcf", null));
        Assert.Null(Host(hidden: true).CheckJoin("vdillo1.vcf", null));
        // a mission's car is no melee car either way, nor an emplacement
        Assert.Contains("not a melee car", Host(hidden: true).CheckJoin("t16st01.vcf", null));
        Assert.Contains("not a melee car", Host(hidden: true).CheckJoin("vxhowit1.vcf", null));
        // the hidden model is still bound by the restrictions
        Assert.Contains("Restricted", Host(R((2, 15)), hidden: true).CheckJoin("vstank1.vcf", null));
    }

    [Fact]
    public void AHiddenWeaponOnAnOwnVariantNeedsTheHostsSetting()
    {
        if (HostTests.InstallDir is null) return;
        var vfs = Host().Vfs;
        var hades = Custom(vfs, c => c.Weapons = [.. c.Weapons.Select(w => w.Gdf.StartsWith("gcmedium", StringComparison.OrdinalIgnoreCase) ? (w.Hardpoint, "gchades.gdf") : w)]);
        var off = Host().CheckJoin("vppirna4.vcf", hades);
        Assert.NotNull(off);
        Assert.Contains("HADES", off);
        Assert.Contains("Hidden Weapons & Cars off", off);
        Assert.Null(Host(hidden: true).CheckJoin("vppirna4.vcf", hades));
        // and a restriction names it like any other: by its row, or by its whole class
        Assert.Contains("HADES", Host(R((2, 6)), hidden: true).CheckJoin("vppirna4.vcf", hades));
        Assert.Contains("HADES", Host(R((2, -1)), hidden: true).CheckJoin("vppirna4.vcf", hades));
        // a weapon left out as incomplete is refused even then
        var gun = Custom(vfs, c => c.Weapons = [.. c.Weapons.Select(w => w.Gdf.StartsWith("gcmedium", StringComparison.OrdinalIgnoreCase) ? (w.Hardpoint, "gh45.gdf") : w)]);
        Assert.NotNull(Host(hidden: true).CheckJoin("vppirna4.vcf", gun));
    }

    [Fact]
    public void ARefusedJoinGetsNoSeatOverTheWire()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Restrictions = R((6, 3)) });
        server.Start();
        var client = new MeleeClient();
        client.Connect("127.0.0.1", server.Port, "Jade", "vppirna1.vcf");
        for (int t = 0; t < 250 && client.State == ClientState.Connecting; t++) { client.Poll(); Thread.Sleep(20); }
        Assert.Equal(ClientState.Rejected, client.State);
        Assert.Contains("Landmines", client.Reason);
        client.Dispose();
        Assert.Empty(server.Host.Seats);
    }

    [Fact]
    public void NitrosChecklistIsReadFromItsShell()
    {
        if (Nitro is null) return;
        var install = new GameInstall(Nitro);
        var dll = CarTable.NitShell(install);
        var rows = RestrictionTable.Read(dll);
        Assert.Equal(43, rows.Count);
        Assert.Equal(("All Weapons", -1, -1, 3, 17), (rows[0].Name, rows[0].Class, rows[0].Tier, rows[0].Column, rows[0].Row));
        Assert.Contains(rows, r => r.Name == "4Get-U-Not" && r.Class == 4 && r.Tier == 4);
        Assert.Contains(rows, r => r.Name == "BallOFlame" && r.Class == 4 && r.Group == 4);          // a missile, under the flamethrowers
        Assert.DoesNotContain(rows, r => (r.Class, r.Tier) is (2, 6) or (4, 3) or (5, 3) or (6, 6));   // HADES, Cherub, Pyro-Tomic, Car-E-Racer
        var (x, y) = RestrictionTable.Grid(dll);
        Assert.Equal([39, 64, 93, 321, 346, 375], x);
        Assert.Equal(66, y[0]);
        Assert.Equal(370, y[17]);
        // the port's rows for the hidden weapons sit in boxes the original leaves empty
        var mine = HiddenContent.ChecklistRows(new AssetVfs(install));
        Assert.Equal(9, mine.Count);
        Assert.DoesNotContain(mine, m => rows.Any(r => (r.Column, r.Row) == (m.Column, m.Row)));
        Assert.Equal(mine.Count, mine.Select(m => (m.Column, m.Row)).Distinct().Count());
        Assert.Contains(mine, m => m.Name == "Howitzer" && (m.Class, m.Tier, m.Group, m.Column, m.Row) == (3, 103, 2, 5, 16));
        Assert.Contains(mine, m => m.Name == "HADES Cannon" && (m.Class, m.Tier) == (2, 6));
    }

    [Fact]
    public void NitroAllowsItsOwnWeaponsAndOffersItsOwnCars()
    {
        if (Nitro is null) return;
        var arena = "N01.CBT";
        var host = Host(install: Nitro, arena: arena);
        Assert.Contains("vxmilk", host.OfferedModels);                     // the tanker is on Nitro's list
        Assert.Null(host.CheckJoin("vxmilk1.vcf", null));                  // with its Ball O'Flame
        Assert.Contains("Hidden Weapons & Cars off", host.CheckJoin("vxufo1.vcf", null));
        Assert.Null(Host(hidden: true, install: Nitro, arena: arena).CheckJoin("vxufo1.vcf", null));
        Assert.Null(Host(hidden: true, install: Nitro, arena: arena).CheckJoin("vubalon1.vcf", null));
        Assert.Contains("Hidden Weapons & Cars off", host.CheckJoin("vdillo1.vcf", null));
        Assert.Null(Host(hidden: true, install: Nitro, arena: arena).CheckJoin("vdillo1.vcf", null));
    }
}
