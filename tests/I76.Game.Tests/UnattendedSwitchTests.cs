using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

public class UnattendedSwitchTests
{
    [Fact]
    public void The_switches_are_off_until_given()
    {
        var o = new LaunchOptions();
        Assert.False(o.SkipCutscenes);
        Assert.False(o.Mute);
    }

    [Fact]
    public void Skip_cutscenes_sets_only_its_own_option()
    {
        var o = new LaunchOptions();
        Assert.True(LaunchOptions.ApplySwitch(o, "--skip-cutscenes"));
        Assert.True(o.SkipCutscenes);
        Assert.False(o.Mute);
    }

    [Fact]
    public void Mute_sets_only_its_own_option()
    {
        var o = new LaunchOptions();
        Assert.True(LaunchOptions.ApplySwitch(o, "--mute"));
        Assert.True(o.Mute);
        Assert.False(o.SkipCutscenes);
    }

    [Theory]
    [InlineData("--skip-movies")]
    [InlineData("--mission")]
    [InlineData("--muted")]
    public void Other_arguments_are_not_taken(string arg)
    {
        var o = new LaunchOptions();
        Assert.False(LaunchOptions.ApplySwitch(o, arg));
        Assert.False(o.SkipCutscenes || o.Mute);
    }
}
