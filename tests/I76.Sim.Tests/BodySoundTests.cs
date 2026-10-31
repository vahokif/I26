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
            Assert.Equal(car.Engine, plain.EngineNumber);                  // a mission's own file keeps its row
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
}
