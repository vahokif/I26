using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The Show FPS overlay's ring of frame times (Ui/FrameStats.cs): window trimming, rate, text.</summary>
public class FrameStatsTests
{
    [Fact]
    public void SteadyFramesReadAsTheirRate()
    {
        var s = new FrameStats();
        for (int i = 0; i < 300; i++) s.Add(1.0 / 100);
        Assert.Equal(100, s.Fps(1.0), 3);
        Assert.Equal("FPS 100", s.Text());
    }

    [Fact]
    public void TheRingKeepsOnlyTheLastSecond()
    {
        var s = new FrameStats();
        for (int i = 0; i < 100; i++) s.Add(0.1);   // 10 s of 10 fps
        Assert.InRange(s.Count, 9, 11);
        for (int i = 0; i < 100; i++) s.Add(0.01);  // then 1 s of 100 fps: the slow frames are gone
        Assert.Equal("FPS 100", s.Text());
    }

    [Fact]
    public void NothingYetAndBadDeltasAreIgnored()
    {
        var s = new FrameStats();
        Assert.Equal("FPS --", s.Text());
        s.Add(0); s.Add(-1); s.Add(double.NaN);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void ACapacityFullRingDropsTheOldest()
    {
        var s = new FrameStats(1000, capacity: 8);
        for (int i = 0; i < 20; i++) s.Add(0.001 * (i + 1));
        Assert.Equal(8, s.Count);
    }
}
