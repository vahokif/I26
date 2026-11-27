using I76.Formats.Imaging;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Clouds: Volumetric for a new player and for a settings file without [port] clouds; Flat only when chosen.</summary>
public class CloudsSettingTests
{
    [Fact]
    public void ANewPlayerHasTheVolumetricClouds() => Assert.Equal(CloudNoise.Volumetric, new ShellSettings().Clouds);

    [Fact]
    public void AFileWithoutTheKeyHasTheVolumetricClouds() => Assert.Equal(CloudNoise.Volumetric, CloudNoise.Parse(null));
}
