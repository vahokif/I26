using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Graphic Detail's two cloud rows in shell.cfg: the shadows are on and the sky drifts at full speed
/// until the player says otherwise, and a hand-edited file cannot run the speed index off the list.
/// </summary>
public class CloudShadowSettingTests
{
    [Fact]
    public void ANewPlayerHasThemOn()
    {
        var s = new ShellSettings();
        Assert.True(s.CloudShadows);
        Assert.Equal(0, s.CloudSpeed);    // Normal
    }

    [Theory]
    [InlineData(null, true)]      // never written: the feature ships on
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void AFileKeepsItsChoice(string? stored, bool on) => Assert.Equal(on, ShellSettings.CloudShadowsFrom(stored));

    [Theory]
    [InlineData(null, 0)]     // never written: Normal
    [InlineData(0, 0)]
    [InlineData(1, 1)]        // Half
    [InlineData(2, 2)]        // Quarter
    [InlineData(3, 2)]        // past the end of the list: the slowest, not an exception
    [InlineData(-1, 0)]
    public void AFileKeepsItsSpeed(int? stored, int choice) => Assert.Equal(choice, ShellSettings.CloudSpeedFrom(stored));

    /// <summary>The menu offers the names and CloudShadow applies the values, both by the same index.</summary>
    [Fact]
    public void TheSpeedNamesMatchTheSpeeds() =>
        Assert.Equal(I76.Game.World.CloudShadow.Speeds.Length, I76.Game.World.CloudShadow.SpeedNames.Length);
}
