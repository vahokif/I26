using I76.Formats.Imaging;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Clouds: Flat (as before the setting existed) for a new player and for a settings file without [port] clouds.</summary>
public class CloudsSettingTests
{
    [Fact]
    public void ANewPlayerHasTheFlatClouds() => Assert.Equal(CloudNoise.Flat, new ShellSettings().Clouds);

    [Fact]
    public void AFileWithoutTheKeyHasTheFlatClouds() => Assert.Equal(CloudNoise.Flat, CloudNoise.Parse(null));
}
