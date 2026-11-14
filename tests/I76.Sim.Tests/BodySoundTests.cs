using I76.Formats.Vfs;
using I76.Sim.Trip;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The engine sound, horn and siren of a car the shell gives come from its body's engsnd.dat row (VehicleFactory.BodySoundRow):
/// a variant saved in CONFIGURE CHASSIS carries the engine part's ordinal (atoi("eng04") − 1), and the original
/// launched the player's car that way (a stock Sheriff measured as ENG NUM 3 in the Nitro Pack's vehscn.vcf), losing the heavy trucks'
/// eimarx.wav / vhorn5.wav, the police car's aps2.wav siren and the hidden vehicles' loops. Testers asked for them back.
/// </summary>
public class BodySoundTests
{
    private static string? NitroDir => Dir("I76_NITRO_INSTALL");

    private static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Both games' installs that are set: the base game has the trucks, bus, tractor, police car, tank and helicopters too.</summary>
    private static IEnumerable<AssetVfs> Installs() =>
        new[] { Dir("I76_INSTALL"), NitroDir }.OfType<string>().Select(d => new AssetVfs(new GameInstall(d)));

    [Theory]
    [InlineData("vxmilk1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vmxmarx1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vmload1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vmcargo1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vxbus1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vxbus2.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vxktrac1.vcf", 5, "eimarx.wav", "vhorn5.wav")]
    [InlineData("vcpolic1.vcf", 10, "eisv.wav", "aps2.wav")]
    [InlineData("vstank1.vcf", 4, "eitank.wav", "vhorn6.wav")]
    [InlineData("vuchuey1.vcf", 20, "aheli.wav", "vhorn6.wav")]
    [InlineData("vugcobr1.vcf", 20, "aheli.wav", "vhorn6.wav")]
    [InlineData("vubalon1.vcf", 50, "vballoon.wav", "vhorn6.wav")]
    [InlineData("vxufo1.vcf", 51, "vufo.wav", "vhorn6.wav")]
    public void A_saved_variant_sounds_as_its_body(string stock, int row, string engine, string horn)
    {
        foreach (var vfs in Installs())
        {
            if (vfs.TryRead(stock) is null) continue;      // the balloon and the UFO are Nitro's
            SavedVariant(vfs, stock, row, engine, horn);
        }
    }

    private static void SavedVariant(AssetVfs vfs, string stock, int row, string engine, string horn)
    {
        // the garage's save: the engine as the part's ordinal (ENG NUM 5 → comp 3, written back as 3)
        var car = TripCar.FromVcf(vfs.Read(stock), n => VehicleFactory.EngineCompId(vfs, n));
        Assert.InRange(car.Engine, 0, 3);
        var name = "zbodysnd" + stock;
        AssetVfs.Addon[name] = car.ToVcf();
        try
        {
            var plain = VehicleFactory.Create(vfs, name);
            // a mission's own file keeps its row, but for a truck, tank or aircraft body (the CD build's rule)
            Assert.Equal(row == 10 ? car.Engine : row, plain.EngineNumber);
            var v = VehicleFactory.Create(vfs, name, bodySound: true);
            Assert.Equal(row, v.EngineNumber);
            Assert.Equal(engine, VehicleFactory.EngineSound(vfs, v.EngineNumber));
            Assert.Equal(horn, VehicleFactory.HornSound(vfs, v.EngineNumber));
            Assert.Equal(plain.Engine.PeakForce, v.Engine.PeakForce);    // the fitted engine still sets the power
        }
        finally { AssetVfs.Addon.TryRemove(name, out _); }
    }

    [Fact]
    public void A_secret_body_sounds_as_the_hidden_model()
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        foreach (var (vdf, row) in new[] { ("vstank.vdf", 4), ("vuhuey.vdf", 20), ("vuballon.vdf", 50), ("vxufo.vdf", 51) })
            Assert.Equal(row, VehicleFactory.BodySoundRow(vfs, vdf));
        // a car body has none: its variants carry engine parts
        Assert.Null(VehicleFactory.BodySoundRow(vfs, "vfrattlr.vdf"));
        Assert.Null(VehicleFactory.BodySoundRow(vfs, "vppirnha.vdf"));
    }

    /// <summary>
    /// The missions' own helicopters, tank and trucks, saved with a plain engine row, sound as their bodies: the 1997 CD build
    /// picked the loop by the body's size class (5 trucks, 6 tank, 7 aircraft). The police cars, a size-3 body, keep their files' rows.
    /// </summary>
    [Theory]
    [InlineData("t05ug01.vcf", 0, "aheli.wav")]
    [InlineData("t07uc01.vcf", 0, "aheli.wav")]
    [InlineData("t08uc01.vcf", 0, "aheli.wav")]
    [InlineData("t11ug01.vcf", 0, "aheli.wav")]
    [InlineData("t11ug04.vcf", 0, "aheli.wav")]
    [InlineData("t16ug01.vcf", 0, "aheli.wav")]
    [InlineData("s06vu01.vcf", 0, "aheli.wav")]
    [InlineData("t16st01.vcf", 0, "eitank.wav")]
    [InlineData("s05vxb01.vcf", 3, "eimarx.wav")]
    [InlineData("s07xm01.vcf", 3, "eimarx.wav")]
    [InlineData("t17mx01.vcf", 3, "eimarx.wav")]
    [InlineData("t10cp01.vcf", 0, "einp1.wav")]
    [InlineData("t07cp01.vcf", 2, "eishp.wav")]
    public void A_missions_own_heavy_vehicle_sounds_as_its_body(string vcf, int fileRow, string engine)
    {
        if (Dir("I76_INSTALL") is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var f = new I76.Formats.Bwd2.Bwd2File(vfs.Read(vcf));
        Assert.Equal(fileRow, BitConverter.ToInt32(f.Data, f.First("VCFC")!.Value.PayloadOffset + 0x2a));
        var v = VehicleFactory.Create(vfs, vcf);
        Assert.Equal(engine, VehicleFactory.EngineSound(vfs, v.EngineNumber));
        Assert.Equal(engine == "einp1.wav" || engine == "eishp.wav" || engine == "eimarx.wav", VehicleFactory.EngineRevable(vfs, v.EngineNumber));
    }

    [Theory]
    [InlineData("nobus2.vcf")]
    [InlineData("nocargo2.vcf")]
    public void The_nitro_packs_own_buses_and_trucks_sound_as_their_bodies(string vcf)
    {
        if (NitroDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        Assert.Equal("eimarx.wav", VehicleFactory.EngineSound(vfs, VehicleFactory.Create(vfs, vcf).EngineNumber));
    }

    [Fact]
    public void Only_the_truck_tank_and_aircraft_sizes_pick_the_bodys_sound()
    {
        Assert.Equal([5u, 6u, 7u], Enumerable.Range(0, 12).Select(i => (uint)i).Where(VehicleFactory.HeavyBody));
    }
}
