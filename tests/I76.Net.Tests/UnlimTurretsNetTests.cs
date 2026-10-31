using I76.Formats.Vfs;
using I76.Server;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The host's Unlim. Turrets (--turrets): several roof turrets on a joining car and on a bot's, and what the listing says.</summary>
[Collection("AddonOverlay")]
public class UnlimTurretsNetTests
{
    private static byte[] TwoTurretVariant(AssetVfs vfs)
    {
        var db = PartsDatabase.Build(vfs);
        var m = new ChassisModel(TripCar.FromVcf(vfs.Read("vppirna1.vcf"), c => VehicleFactory.EngineCompId(vfs, c)), db, vfs, nitro: false, hidden: false) { UnlimTurrets = true };
        foreach (var t in m.Mounts().Where(x => m.Inv.Slots[x.Slot].MountStyle == 1).ToList())
            m.Fit(t.Slot, m.CatalogueFor(t.Slot).First(c => c.Part.IsTurret).Part);
        var car = m.Inv.ToCar();
        car.Variant = "Twin";
        car.Engine = TripCar.FromVcf(vfs.Read("vppirna1.vcf")).Engine;
        return car.ToVcf();
    }

    private static int Turrets(MeleeHost h, string file) =>
        TripCar.FromVcf(h.Vfs.Read(file)).Weapons.Count(w => w.Gdf.StartsWith('t'));

    [Fact]
    public void AJoiningCarWithTwoTurretsPassesOnlyWhereTheHostAllowsThem()
    {
        if (HostTests.InstallDir is not { } install) return;
        var off = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        var on = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", AllowTurrets = true });
        var vcf = TwoTurretVariant(off.Vfs);
        Assert.NotNull(off.CheckJoin("vppirna9.vcf", vcf));
        Assert.Null(on.CheckJoin("vppirna9.vcf", vcf));
        Assert.NotNull(on.Join("m", "vppirna9.vcf", 0, null, vcf).Player.Car);
        Assert.True(on.Info().Turrets && !off.Info().Turrets);
    }

    [Fact]
    public void ABotsCarKeepsItsFirstTurretWhereTheyAreNotAllowed()
    {
        if (HostTests.InstallDir is not { } install) return;
        var probe = new MeleeHost(new ServerOptions { Install = install, Arena = "m01" });
        const string file = "vppirna6.vcf";
        AssetVfs.Addon[file] = TwoTurretVariant(probe.Vfs);
        try
        {
            var off = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = [file] });
            var on = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Bots = 1, MaxPlayers = 4, BotChoices = [file], AllowTurrets = true });
            Assert.Equal(1, Turrets(off, off.Bots[0].Player.CarFile));
            Assert.Equal(2, Turrets(on, on.Bots[0].Player.CarFile));
            Assert.Equal(1, off.Bots[0].Player.Car.Armory!.Weapons.Count(w => w.Style == 3));
            Assert.Equal(2, on.Bots[0].Player.Car.Armory!.Weapons.Count(w => w.Style == 3));
        }
        finally { AssetVfs.Addon.Remove(file, out _); }
    }

    [Fact]
    public void TheListingCarriesTheOption()
    {
        var info = new SessionInfo(7, Protocol.Version, "g", "m05", 1, 8, false, false, 0, 0, false, [new SessionDriver("a", "vppirna1.vcf", 1)], Turrets: true);
        var w = new NetWriter();
        info.Write(w);
        Assert.True(SessionInfo.TryRead(w.Span)!.Turrets);
        var plain = info with { Turrets = false };
        w = new NetWriter();
        plain.Write(w);
        Assert.False(SessionInfo.TryRead(w.Span)!.Turrets);
    }
}
