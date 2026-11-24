using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Motion Blur: off for a new player and for a settings file without [port] motion_blur.</summary>
public class MotionBlurSettingTests
{
    [Fact]
    public void ANewPlayerHasItOff() => Assert.False(new ShellSettings().MotionBlur);

    [Fact]
    public void AFileWithoutTheKeyHasItOff() => Assert.False(I76.Sim.Views.MotionBlurRules.From(null));

    [Fact]
    public void AFileThatTurnedItOffKeepsItOff() => Assert.False(I76.Sim.Views.MotionBlurRules.From(false));

    [Fact]
    public void AFileThatTurnedItOnKeepsItOn() => Assert.True(I76.Sim.Views.MotionBlurRules.From(true));
}
