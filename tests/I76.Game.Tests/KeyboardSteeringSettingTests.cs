using I76.Game.Shell;
using I76.Sim.Session;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Play Options' Keyboard Steering Assist: on for a new player and for a settings file from before it; the branch-era keyboard_steering word "original" reads as off.</summary>
public class KeyboardSteeringSettingTests
{
    [Fact]
    public void ANewPlayerHasItOn() => Assert.True(new ShellSettings().AssistedSteering);

    [Theory]
    [InlineData("original", false)]
    [InlineData("Original", false)]
    [InlineData("assisted", true)]
    [InlineData("progressive", true)]
    [InlineData("linear", true)]
    [InlineData("assisted_free", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void TheOlderWordReadsAsOnUnlessOriginal(string? word, bool on) => Assert.Equal(on, KeyStick.AssistedFromKeyboardSteering(word));

    [Fact]
    public void ItIsAPlayOptionsRow() => Assert.Contains("Keyboard Steering Assist", PlayOptionsScreen.PortRows);
}
