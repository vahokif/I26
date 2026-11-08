using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>shell.cfg's [port] developer flag, written by hand: the words that turn the Developer panel on.</summary>
public class DeveloperFlagTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" TRUE ")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("yes")]
    public void TheWordsThatTurnItOn(string word) => Assert.True(ShellSettings.DeveloperWord(word));

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("")]
    [InlineData("developer")]
    [InlineData(null)]
    public void AnythingElseLeavesItOff(string? word) => Assert.False(ShellSettings.DeveloperWord(word));
}
