using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The wheel's texture strip.</summary>
public class WheelStripTests
{
    [Fact]
    public void StandingStillHoldsTheFrameOnRowZero()
    {
        var s = new WheelStrip(0);
        for (float t = 0.1f; t < 2; t += 0.1f) Assert.Equal(0, s.Tick(t, 0.04f));
        s.Tick(0.1f + 2, 5f);   // a step on row 1
        Assert.Equal((1, 1), (s.Row, s.Frame));
        Assert.Equal(1, s.Tick(3, 0));   // stopped: row 0, the frame kept
    }

    [Theory]
    [InlineData(1f, 0)]
    [InlineData(4.4704f, 0)]
    [InlineData(4.5f, 1)]
    [InlineData(11.176f, 1)]
    [InlineData(11.2f, 2)]
    [InlineData(-30f, 2)]
    public void TheRowIsTheSpeedBand(float speed, int row)
    {
        var s = new WheelStrip(0);
        Assert.Equal(0, s.Tick(0.01f, speed));   // the row is stored, not shown, before the first step
        Assert.Equal(row, s.Row);
        Assert.Equal(row * 12 + (speed < 0 ? 11 : 1), s.Tick(0.06f, speed));
    }

    [Fact]
    public void StepsEveryFiftiethOfASecondAndWraps()
    {
        var s = new WheelStrip(1);
        Assert.Equal(0, s.Tick(1.05f, 2));   // due after 1.05, not at it
        var seen = new List<int>();
        for (int k = 1; k <= 13; k++) seen.Add(s.Tick(1 + 0.05f * k + 0.001f, 2));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1], seen);
    }

    [Fact]
    public void ReverseStepsBackward()
    {
        var s = new WheelStrip(0);
        Assert.Equal(11, s.Tick(0.06f, -1));
        Assert.Equal(10, s.Tick(0.11f, -1));
        Assert.False(s.Forward);
    }

    [Fact]
    public void ALateFrameStepsOnceAndTheTimerLags()
    {
        var s = new WheelStrip(0);
        Assert.Equal(1, s.Tick(1.0f, 2));   // 19 steps were due: one is taken
        Assert.Equal(2, s.Tick(1.0f, 2));   // next is 0.1 now, still behind: another one at the same time
    }
}
