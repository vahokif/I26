using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The software renderer's crash jolt and the Crash Blur setting.</summary>
public class ScreenWobbleTests
{
    [Theory]
    [InlineData(true, 8, 21, true, true)]
    [InlineData(true, 8, 20, true, false)]    // 20 itself is not enough: ch[3] > 20
    [InlineData(true, 8, 500, false, false)]  // not in the cockpit view
    [InlineData(false, 8, 500, true, false)]  // not the player
    [InlineData(true, 1, 500, true, false)]   // not the impact channel's packet (mask bit 8)
    public void JoltHit(bool player, int mask, int impact, bool cockpit, bool expect) =>
        Assert.Equal(expect, ScreenWobble.IsJoltHit(player, mask, impact, cockpit));

    [Fact]
    public void JoltStartsTheEffectWithoutTheCheat()
    {
        var w = new ScreenWobble();
        Assert.False(w.On);
        w.StartJolt();
        Assert.True(w.On && w.Jolting);
    }

    [Fact]
    public void JoltMatchesTheRealGame()
    {
        // measured in the original at a fixed 0.0361 s step (t02, frame 678 on): 19 -> 17.646, 16.370, 15.187, 14.105 ...
        var w = new ScreenWobble();
        w.StartJolt();
        float[] expect = [17.646f, 16.370f, 15.187f, 14.105f, 13.085f];
        foreach (float e in expect)
        {
            var (x, y) = w.Step(0.0361f, whole: false);
            Assert.InRange(x, e - 0.1f, e + 0.1f);
            Assert.Equal(x * 0.5f, y, 3);
        }
    }

    [Fact]
    public void JoltEndsUnderOnePixelAndTakesTheCheatWithIt()
    {
        var w = new ScreenWobble();
        w.Toggle();                // wiggleburger on
        w.StartJolt();
        int frames = 0;
        while (w.On && frames < 1000) { w.Step(0.036f); frames++; }
        Assert.False(w.On);
        Assert.False(w.Jolting);
        Assert.InRange(frames, 30, 50);   // about 1.4 s at 28 fps
    }

    [Fact]
    public void JoltOffsetsAreWholePixels()
    {
        var w = new ScreenWobble();
        w.StartJolt();
        var (x, y) = w.Step(0.0361f);
        Assert.Equal(17f, x);
        Assert.Equal(8f, y);
    }

    [Fact]
    public void LongFrameJumpsStraightThere()
    {
        Assert.Equal(0f, ScreenWobble.Ease(19f, 0f, 1.2f));   // 2*dt over 0.95: the target outright
        Assert.Equal(5f, ScreenWobble.Ease(10f, 0f, 0.5f));
    }

    [Fact]
    public void WiggleburgerEasesTowardsRandomTargets()
    {
        var w = new ScreenWobble(() => 12);
        w.Toggle();
        var (x, _) = w.Step(0.5f);
        Assert.Equal(6f, x);
        Assert.True(w.On && !w.Jolting);
    }

    [Theory]
    [InlineData("off", CrashBlurMode.Off)]
    [InlineData("ON", CrashBlurMode.On)]
    [InlineData("auto", CrashBlurMode.On)]      // the old three-way setting's default reads as on
    [InlineData("", CrashBlurMode.On)]
    [InlineData(null, CrashBlurMode.On)]
    [InlineData("nonsense", CrashBlurMode.On)]
    public void Parse(string? s, int expect) => Assert.Equal(expect, CrashBlurMode.Parse(s));

    [Fact]
    public void WordRoundTrips()
    {
        for (int m = 0; m < 2; m++) Assert.Equal(m, CrashBlurMode.Parse(CrashBlurMode.Word(m)));
    }

    [Theory]
    [InlineData(CrashBlurMode.On, true)]
    [InlineData(CrashBlurMode.Off, false)]
    public void Resolve(int mode, bool expect) => Assert.Equal(expect, CrashBlurMode.Resolve(mode));
}
