using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Only a new press made while a movie plays skips it (MovieSkipGate).</summary>
public class MovieSkipGateTests
{
    [Fact]
    public void AFreshPressSkips()
    {
        var g = new MovieSkipGate();
        g.HeldAtStart([]);
        Assert.True(g.Press("key:Enter"));
    }

    /// <summary>The Enter, click or pad button that started the mission, still down as the movie starts.</summary>
    [Fact]
    public void APressDownAsTheMovieStartsDoesNotSkipUntilLetGo()
    {
        var g = new MovieSkipGate();
        g.HeldAtStart(["key:Enter", "mouse:Left", "pad0:A"]);
        Assert.False(g.Press("key:Enter"));
        Assert.False(g.Press("mouse:Left"));
        Assert.False(g.Press("pad0:A"));
        g.Release("pad0:A");
        Assert.True(g.Press("pad0:A"));              // let go, then pressed again: the player's skip
        Assert.True(g.Press("pad1:A"));              // another pad's own press
    }

    [Fact]
    public void ASecondPressWithoutARelease_IsNotANewOne()
    {
        var g = new MovieSkipGate();
        Assert.True(g.Press("key:Space"));
        Assert.False(g.Press("key:Space"));
        g.Release("key:Space");
        Assert.True(g.Press("key:Space"));
    }

    [Fact]
    public void TheWheelAndTheSideButtonsNeverSkip()
    {
        Assert.True(MovieSkipGate.SkipsWith(1));    // left
        Assert.True(MovieSkipGate.SkipsWith(2));    // right
        Assert.True(MovieSkipGate.SkipsWith(3));    // middle
        foreach (int b in new[] { 4, 5, 6, 7, 8, 9 }) Assert.False(MovieSkipGate.SkipsWith(b));   // wheel up/down/left/right, side buttons
    }
}
