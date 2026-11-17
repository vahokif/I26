using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Play Options' Auto Reverse in shell.cfg: off by default, as the original; a file's own choice is kept.</summary>
public class AutoReverseSettingTests
{
    [Fact]
    public void ANewPlayerHasItOff() => Assert.False(new ShellSettings().AutoReverse);

    [Theory]
    [InlineData(null, false)]   // never written: up to version 11 that was off or never touched, from 12 a file without the key
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AFileKeepsItsChoice(bool? stored, bool on) => Assert.Equal(on, ShellSettings.AutoReverseFrom(stored));
}
