using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Play Options' Auto Reverse in shell.cfg: on by default from settings version 12, and every older file is on too.</summary>
public class AutoReverseSettingTests
{
    [Fact]
    public void ANewPlayerHasItOn() => Assert.True(new ShellSettings().AutoReverse);

    [Theory]
    [InlineData(1, null)]     // before the row
    [InlineData(5, null)]     // off by default then, never written: untouched and turned off read the same
    [InlineData(11, null)]
    [InlineData(11, true)]
    [InlineData(11, false)]   // not written so before version 12, but on all the same
    public void AnOlderFileIsOn(int version, bool? stored) => Assert.True(ShellSettings.AutoReverseFrom(version, stored));

    [Fact]
    public void FromVersionTwelveTheKeyIsTheChoiceAndAMissingOneIsOn()
    {
        Assert.Equal(12, ShellSettings.AutoReverseOnVersion);
        Assert.False(ShellSettings.AutoReverseFrom(12, false));
        Assert.True(ShellSettings.AutoReverseFrom(12, true));
        Assert.True(ShellSettings.AutoReverseFrom(12, null));
        Assert.False(ShellSettings.AutoReverseFrom(13, false));
    }
}
