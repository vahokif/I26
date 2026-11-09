using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Resolution: the sizes offered, the shell.cfg text and the row's cycle.</summary>
public class WindowSizesTests
{
    [Fact] public void OffersTheCommonSizesThatFitBelowTheDesktop()
    {
        var o = WindowSizes.Offered(1920, 1080);
        Assert.Contains((1280, 720), o);
        Assert.Contains((1600, 900), o);
        Assert.DoesNotContain((1920, 1080), o);   // the desktop's own is the Desktop choice
        Assert.DoesNotContain((2560, 1440), o);
    }

    [Fact] public void ParseAcceptsSizesAndTakesTheDesktopForAnythingElse()
    {
        Assert.Equal((1280, 720), WindowSizes.Parse("1280x720"));
        Assert.Equal((1280, 720), WindowSizes.Parse(" 1280X720 "));
        Assert.Null(WindowSizes.Parse(""));
        Assert.Null(WindowSizes.Parse(null));
        Assert.Null(WindowSizes.Parse("1280"));
        Assert.Null(WindowSizes.Parse("axb"));
        Assert.Null(WindowSizes.Parse("10x10"));
        Assert.Null(WindowSizes.Parse("-1280x720"));
    }

    [Fact] public void TheRowCyclesDesktopThroughTheSizesAndBack()
    {
        (int W, int H)? now = null;
        var seen = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 40; i++)
        {
            now = WindowSizes.Next(now, 1920, 1080);
            seen.Add(WindowSizes.Label(now));
            if (now is null) break;
        }
        Assert.Equal("Desktop", seen[^1]);
        Assert.Equal(WindowSizes.Offered(1920, 1080).Count + 1, seen.Count);
        Assert.Equal("Desktop", WindowSizes.Label(null));
    }

    [Fact] public void ASavedSizeFromAnotherScreenStepsToTheNextLarger()
    {
        Assert.Equal((1280, 960), WindowSizes.Next((1500, 800), 1920, 1080));
        Assert.Null(WindowSizes.Next((3000, 2000), 1920, 1080));
    }

    [Fact] public void AWindowIsHeldToTheScreen() => Assert.Equal((1366, 768), WindowSizes.Fit((1920, 1080), 1366, 768));
}
