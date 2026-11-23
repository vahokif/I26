using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Motion Blur: on for a new player and for a settings file from before it ([port] motion_blur, written only when off).</summary>
public class MotionBlurSettingTests
{
    [Fact]
    public void ANewPlayerHasItOn() => Assert.True(new ShellSettings().MotionBlur);

    [Fact]
    public void AFileWithoutTheKeyHasItOn() => Assert.True(I76.Sim.Views.MotionBlurRules.From(null));

    [Fact]
    public void AFileThatTurnedItOffKeepsItOff() => Assert.False(I76.Sim.Views.MotionBlurRules.From(false));
}
