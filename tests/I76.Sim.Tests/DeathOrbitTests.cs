using I76.Sim.Session;
using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The death and end-of-match orbit: 8 degrees per sim tick's worth of time, as the original's 8 per frame.</summary>
public class DeathOrbitTests
{
    private const float Deg = MathF.PI / 180f;

    /// <summary>At each offered sim rate: 8 degrees times the ticks a second (160 degrees a second at 20 Hz, 222 at 28).</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(28)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(60)]
    public void ItTurnsEightDegreesATick(int hz)
    {
        var rate = SimRate.FromLabel(hz);
        Assert.Equal(hz, rate.Label);
        var o = new DeathOrbit();
        o.Start(0);
        for (int i = 0; i < 1440; i++) o.Advance(1f / 144, rate.TickSeconds);   // 10 s of 144 fps display frames
        Assert.Equal(8f / rate.TickSeconds, o.Yaw / Deg / 10f, 0.5f);
        Assert.InRange(o.Yaw / Deg / 10f, 8f * hz * 0.98f, 8f * hz * 1.02f);
    }

    /// <summary>The same display frames at two sim rates: the turn follows the sim rate, not the display.</summary>
    [Fact]
    public void TheRateFollowsTheSimRate()
    {
        float Turned(SimRate r, int fps)
        {
            var o = new DeathOrbit();
            o.Start(0);
            for (int i = 0; i < 10 * fps; i++) o.Advance(1f / fps, r.TickSeconds);
            return o.Yaw / Deg / 10f;
        }
        Assert.Equal(160.6f, Turned(SimRate.R20, 60), 0.1f);
        Assert.Equal(222.2f, Turned(SimRate.R28, 60), 0.1f);
        Assert.Equal(Turned(SimRate.R20, 60), Turned(SimRate.R20, 165), 0.1f);   // the display rate does not matter
        Assert.Equal(2f, Turned(SimRate.R40, 60) / Turned(SimRate.R20, 60), 0.01f);
    }

    /// <summary>Continuous: every display frame turns its share, a third of a step at 60 fps and 20 Hz, never a held or whole step.</summary>
    [Fact]
    public void EveryFrameTurnsItsShare()
    {
        var o = new DeathOrbit();
        o.Start(1f);
        float tick = SimRate.R20.TickSeconds, last = o.Yaw;
        for (int i = 0; i < 300; i++)
        {
            o.Advance(1f / 60, tick);
            Assert.Equal(8f / 60 / tick, (o.Yaw - last) / Deg, 0.01f);
            last = o.Yaw;
        }
    }
}
