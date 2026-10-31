using I76.Formats.Vfs;
using I76.Net;
using I76.Server;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The car a player picks (a stock variant or their own saved one) is the car they play, and everyone sees.</summary>
public class VariantTests
{
    private static string[] Guns(Vehicle v) => [.. v.Armory!.Weapons.Select(w => w.Def.Name)];

    private static (MeleeHost Host, AssetVfs Vfs) Host()
    {
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir!, Arena = "m01" });
        return (host, host.Vfs);
    }

    /// <summary>A custom Piranha: variant 1's body, variant 3's guns, its own name.</summary>
    internal static byte[] Custom(AssetVfs vfs, Action<TripCar>? edit = null)
    {
        var car = TripCar.FromVcf(vfs.Read("vppirna1.vcf"));
        car.Weapons = TripCar.FromVcf(vfs.Read("vppirna3.vcf")).Weapons;
        car.Variant = "Mine";
        edit?.Invoke(car);
        return car.ToVcf();
    }

    [Fact]
    public void AStockVariantOtherThanTheFirstIsTheCarThatSpawns()
    {
        if (HostTests.InstallDir is null) return;
        var (host, vfs) = Host();
        foreach (var n in new[] { 1, 2, 3 })
        {
            if (vfs.TryRead($"vppirna{n}.vcf") is null) continue;
            var seat = host.Join($"p{n}", $"vppirna{n}.vcf", 0);
            Assert.Equal($"vppirna{n}.vcf", seat.Player.CarFile);
            Assert.Equal(Guns(VehicleFactory.Create(vfs, $"vppirna{n}.vcf")), Guns(seat.Player.Car));
        }
    }

    [Fact]
    public void AnOwnVariantPlaysOnTheServerAndOnAnotherClient()
    {
        if (HostTests.InstallDir is null) return;
        var (host, vfs) = Host();
        var vcf = Custom(vfs);
        Assert.Null(host.CheckJoin("vppirna4.vcf", vcf));
        var seat = host.Join("me", "vppirna4.vcf", 0, null, vcf);
        var other = host.Join("you", "vcgcour2.vcf", 0);
        var first = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var third = VehicleFactory.Create(vfs, "vppirna3.vcf");
        Assert.NotEqual(Guns(first), Guns(third));
        Assert.Equal(Guns(third), Guns(seat.Player.Car));
        Assert.Equal("vcgcour2.vcf", other.Player.CarFile);
        Assert.NotEqual("vppirna4.vcf", seat.Player.CarFile);        // served under a name of its own, not the client's

        // a second player joining sees the first one's car as the server has it
        var accept = host.AcceptFor(other);
        var info = accept.Cars.Single(c => c.Owner == seat.Slot);
        Assert.NotNull(info.Vcf);
        var w = new NetWriter(); accept.Write(w);
        var r = new NetReader(w.Span); r.U8();
        var wire = JoinAccept.Read(ref r);
        AssetVfs.Addon.TryRemove(info.Def, out _);                    // this process stands for a machine that does not have it
        var mirror = new ClientWorld(HostTests.Mirror("m01"), wire);
        var theirs = mirror.Cars.Values.Single(c => c.Info.Serial == seat.CarSerial).Vehicle;
        Assert.Equal(Guns(seat.Player.Car), Guns(theirs));
        Assert.Equal(seat.Player.Car.P.Mass, theirs.P.Mass);
        Assert.Equal(seat.Player.Car.Health!.Armour, theirs.Health!.Armour);
    }

    /// <summary>
    /// A car file the server's install has on disk rather than in its archive (a community car in addon\, which a listen host
    /// may pick) goes to the clients by its content, as a player's own variant: a client without the file builds it too. It
    /// went by its name before, and nobody else could build the host's car, so nobody saw the host.
    /// </summary>
    [Fact]
    public void ACarInTheServersAddonFolderGoesWithItsFile()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var scratch = new ScratchMapInstall(install, "addoncar");
        var stock = new AssetVfs(new GameInstall(install));
        var vcf = Custom(stock);
        scratch.Put("vppirna7.vcf", vcf);
        var host = new MeleeHost(new ServerOptions { Install = scratch.Root, Arena = "m01" });
        Assert.Null(host.CheckJoin("vppirna7.vcf", vcf));
        var seat = host.Join("host", "vppirna7.vcf", 0, null, vcf);
        var other = host.Join("you", "vcgcour1.vcf", 0);
        Assert.NotEqual("vppirna7.vcf", seat.Player.CarFile);
        Assert.Equal("vcgcour1.vcf", other.Player.CarFile);            // the archive's cars still go by name
        var info = host.AcceptFor(other).Cars.Single(c => c.Owner == seat.Slot);
        Assert.Equal(vcf, info.Vcf);
        AssetVfs.Addon.TryRemove(info.Def, out _);                    // this process stands for a machine that does not have it
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(other));
        var theirs = mirror.Cars[seat.CarSerial];
        Assert.Null(theirs.StandInFor);
        Assert.Equal(Guns(seat.Player.Car), Guns(theirs.Vehicle));
    }

    [Fact]
    public void AnImpossibleCarIsRefused()
    {
        if (HostTests.InstallDir is null) return;
        var (host, vfs) = Host();
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", [1, 2, 3]));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", new byte[5000]));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Armour[0] = 99999)));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Weapons.Add((77, "cannon.gdf")))));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Weapons[0] = (c.Weapons[0].Hardpoint, "nosuchgun"))));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Vdf = "vcgcour.vdf")));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Suspension = 99)));
        Assert.NotNull(host.CheckJoin("vppirna4.vcf", Custom(vfs, c => c.Wdf = ["null", "null", "null"])));
        Assert.NotNull(host.CheckJoin("../x.vcf", Custom(vfs)));
        // no configuration at all: the old way, the model's first variant
        Assert.Null(host.CheckJoin("vppirna4.vcf", null));
        Assert.Equal("vppirna1.vcf", host.Join("x", "vppirna4.vcf", 0).Player.CarFile);
    }

    [Fact]
    public void TheJoinAndCarInfoRoundTrip()
    {
        var w = new NetWriter();
        new CarInfo(3, "n1.vcf", 1, 0, "a", [1, 2, 3]).Write(w);
        new CarInfo(4, "b.vcf", -1, 0, "b").Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal([1, 2, 3], CarInfo.Read(ref r).Vcf);
        Assert.Null(CarInfo.Read(ref r).Vcf);
    }
}
