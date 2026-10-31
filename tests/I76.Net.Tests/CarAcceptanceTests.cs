using System.Text.RegularExpressions;
using I76.Formats.Vfs;
using I76.Server;
using I76.Sim.Trip;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>Whatever a garage can build, a server accepts (docs/multiplayer.md, car configurations).</summary>
public class CarAcceptanceTests(ITestOutputHelper output)
{
    private static string? Nitro
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e ? e : Environment.GetEnvironmentVariable("I76_NITRO");
            return d is { Length: > 0 } && Directory.Exists(d) ? d : null;
        }
    }

    private static IEnumerable<string> Installs() { if (HostTests.InstallDir is { } a) yield return a; if (Nitro is { } n) yield return n; }

    private static MeleeHost Host(string install, bool hidden) =>
        new(new ServerOptions { Install = install, Arena = install == HostTests.InstallDir ? "m01" : "N01.CBT", AllowHidden = hidden });

    private static IEnumerable<string> StockCars(AssetVfs vfs) =>
        vfs.Names.Select(n => n.ToLowerInvariant()).Where(n => Regex.IsMatch(n, @"^v[a-z]+\d\.vcf$")).Distinct().Order();

    /// <summary>Every stock variant, sent as a player's own saved copy (a name the server lacks), is accepted wherever the model is allowed.</summary>
    [Fact]
    public void EveryStockVariantOfBothGamesPassesAsAnOwnVariant()
    {
        var bad = new List<string>();
        foreach (var install in Installs())
        {
            var host = Host(install, hidden: true);
            var vfs = host.Vfs;
            int n = 0;
            var models = host.OfferedModels.Concat(HiddenContent.HiddenModels(vfs, host.OfferedModels)).ToHashSet();
            foreach (var car in StockCars(vfs))
            {
                if (!models.Contains(car[..^5].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))) continue;
                var stem = car[..^5].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                var vcf = vfs.Read(car);
                var why = host.CheckJoin(stem + "9.vcf", vcf);
                n++;
                // data the install itself lacks: the base game's milk truck names Nitro's gsball.gdf, its van's paint sktvan2.vtf is not there
                if (why is not null && !why.Contains("gsball") && !why.Contains("sktvan2")) bad.Add($"{Path.GetFileName(install)} {car}: {why}");
            }
            output.WriteLine($"{install}: {n} cars");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    private static string Rattler(MeleeHost h) => h.OfferedModels.First(m => HiddenContent.ModelName(h.Vfs, m).Equals("Phaedra Rattler", StringComparison.OrdinalIgnoreCase));

    /// <summary>The garage moves armour between zones, chassis and the pool, never adds: any such split of a stock total passes, one point more does not.</summary>
    [Fact]
    public void AGarageMaxedVariantPassesAndOnePointMoreDoesNot()
    {
        foreach (var install in Installs())
        {
            var host = Host(install, hidden: false);
            var vfs = host.Vfs;
            foreach (var stem in host.OfferedModels)
            {
                var car = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
                car.Variant = "Maxed";
                int total = car.Armour.Sum() + car.Chassis.Sum() + car.Ninth;
                // everything into the front armour, as the garage's + button can
                Array.Clear(car.Armour); Array.Clear(car.Chassis); car.Ninth = 0;
                car.Armour[0] = total;
                Assert.True(host.CheckJoin(stem + "9.vcf", car.ToVcf()) is not { } why || !why.Contains("armour"), $"{stem} maxed: {host.CheckJoin(stem + "9.vcf", car.ToVcf())}");
                car.Armour[0]++;
                Assert.Contains("armour beyond", host.CheckJoin(stem + "9.vcf", car.ToVcf()) ?? "");
            }
        }
    }

    [Theory]
    [InlineData("natk", "vstank.vdf", "tank1.vtf")]
    [InlineData("retpocileh", "vuhuey.vdf", "huey2.vtf")]
    public void TheRattlersSecretBodiesNeedTheHostsHiddenSetting(string name, string vdf, string vtf)
    {
        foreach (var install in Installs())
        {
            var vfs = Host(install, false).Vfs;
            if (vfs.TryRead(vdf) is null) continue;
            var host = Host(install, hidden: true);
            var stem = Rattler(host);
            var car = TripCar.FromVcf(vfs.Read(stem + "1.vcf"));
            car.Variant = name + "x";
            (car.Vdf, car.Vtf) = (vdf, vtf);
            if (name == "natk") { (car.Wdf[0], car.Wdf[2]) = ("wctnk_1a.wdf", "wdtnk_1a.wdf"); for (int z = 0; z < 4; z++) car.Armour[z] = car.Chassis[z] = 6000; }
            else car.Armour[0] = car.Chassis[0] = 3000;
            var vcf = car.ToVcf();
            Assert.Null(host.CheckJoin(stem + "9.vcf", vcf));
            var seat = host.Join("secret", stem + "9.vcf", 0, null, vcf);        // it builds and plays
            Assert.NotNull(seat.Player.Car);
            var off = Host(install, hidden: false).CheckJoin(stem + "9.vcf", vcf);
            Assert.Contains("hidden car", off);
            Assert.Contains("Hidden Weapons & Cars off", off);
        }
    }

    /// <summary>A hidden model's own saved variant (CONFIGURE CHASSIS on the hidden Tank) joins when the host allows hidden content and is refused saying so when not.</summary>
    [Fact]
    public void AVariantOfAHiddenModelJoinsOnlyWhereHiddenContentIsAllowed()
    {
        foreach (var install in Installs())
        {
            var on = Host(install, hidden: true);
            foreach (var stem in HiddenContent.HiddenModels(on.Vfs, on.OfferedModels))
            {
                var car = TripCar.FromVcf(on.Vfs.Read(stem + "1.vcf"));
                car.Variant = "Mine";
                var vcf = car.ToVcf();
                if (on.CheckJoin(stem + "9.vcf", vcf) is { } why) { Assert.Contains("gsball", why); continue; }      // the base game's milk truck names a gun it lacks
                Assert.Contains("Hidden Weapons & Cars off", Host(install, false).CheckJoin(stem + "9.vcf", vcf));
            }
        }
    }

    /// <summary>Nitro's catalogue has eleven specials (Dashboard Mary and Tire Covers do nothing but are the player's to pick); the base game's has nine.</summary>
    [Fact]
    public void NitrosElevenSpecialsAreAccepted()
    {
        foreach (var install in Installs())
        {
            var host = Host(install, hidden: false);
            bool nitro = host.Vfs.Install.IsNitro;
            var car = TripCar.FromVcf(host.Vfs.Read("vppirna1.vcf"));
            car.Variant = "Mary";
            car.Specials = [10, 11, 3];
            var vcf = car.ToVcf();
            if (nitro)
            {
                Assert.Null(host.CheckJoin("vppirna9.vcf", vcf));
                Assert.NotNull(host.Join("m", "vppirna9.vcf", 0, null, vcf).Player.Car);
            }
            else Assert.Contains("unknown special", host.CheckJoin("vppirna9.vcf", vcf));
        }
    }
}
