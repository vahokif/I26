using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

public class KeyRampTests
{
    private static (float Throttle, float Steer) Hold(CarButtons keys, float seconds, float dt, float? timeBasedTick)
    {
        var s = new DriverState();
        float t = 0, st = 0;
        int n = (int)MathF.Round(seconds / dt);
        for (int i = 0; i < n; i++) KeyRamp.Apply(ref s, new CarInput { Buttons = keys }, dt, timeBasedTick, ref t, ref st);
        return (t, st);
    }

    public static TheoryData<int> Rates() => [.. SimRate.Offered.Select(r => r.Label)];

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.2f)]
    [InlineData(2.4f)]
    public void TimeBasedRampDoesNotDependOnTheStep(float seconds)
    {
        // the Modern Physics ramp: the same throttle and steer after the same hold time whatever the step is
        float tick = SimRate.R28.TickSeconds;
        var a = Hold(CarButtons.ThrottleUp | CarButtons.SteerLeft, seconds, 1 / 20f, tick);
        var b = Hold(CarButtons.ThrottleUp | CarButtons.SteerLeft, seconds, 1 / 60f, tick);
        var c = Hold(CarButtons.ThrottleUp | CarButtons.SteerLeft, seconds, 1 / 120f, tick);
        Assert.Equal(c.Throttle, a.Throttle, 0.03f);
        Assert.Equal(c.Throttle, b.Throttle, 0.01f);
        Assert.Equal(c.Steer, a.Steer, 0.03f);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void RampPerSecondIsTheOriginalsPerTickAtThatRate(int hz)
    {
        var r = SimRate.FromLabel(hz);
        Assert.Equal(0.4f / r.TickSeconds, KeyRamp.UpPerSecond(r), 1e-4f);
        Assert.Equal(0.5f / r.TickSeconds, KeyRamp.DownPerSecond(r), 1e-4f);
        // stepped at the rate's own tick, the time-based ramp is the original's per-tick ramp exactly
        foreach (var keys in new[] { CarButtons.ThrottleUp, CarButtons.ThrottleDown })
        {
            var o = Hold(keys, 0.1f, r.TickSeconds, null);
            var m = Hold(keys, 0.1f, r.TickSeconds, r.TickSeconds);
            Assert.Equal(o.Throttle, m.Throttle, 1e-5f);
        }
        // and at Modern Physics' 120 Hz it builds as fast in time: the throttle after the original's first two ticks
        var orig = Hold(CarButtons.ThrottleUp, 2 * r.TickSeconds, r.TickSeconds, null).Throttle;
        var modern = Hold(CarButtons.ThrottleUp, 2 * r.TickSeconds, r.TickSeconds / MathF.Round(r.TickSeconds * 120), r.TickSeconds).Throttle;
        Assert.Equal(orig, modern, 0.01f);
    }

    [Fact]
    public void FasterRateRampsFasterInTime()
    {
        // full throttle: within 3 ticks of 0.4, as the original (0.15 s at 20 Hz, 0.05 s at 60); by time, 2.5 ticks
        float Full(SimRate r) { var s = new DriverState(); float t = 0, st = 0, time = 0; while (t < 0.9999f) { KeyRamp.Apply(ref s, new CarInput { Buttons = CarButtons.ThrottleUp }, 1 / 120f, r.TickSeconds, ref t, ref st); time += 1 / 120f; } return time; }
        foreach (var r in SimRate.Offered) Assert.InRange(Full(r), 2 * r.TickSeconds, 3 * r.TickSeconds + 1 / 120f);
        Assert.Equal(3f, Full(SimRate.R20) / Full(SimRate.R60), 0.35f);
    }

    [Fact]
    public void SteeringIsByTimeAtEveryRate()
    {
        // √(t/3) of full lock after t seconds held, whatever the rate
        foreach (var r in SimRate.Offered)
            Assert.Equal(MathF.Sqrt(1.5f / 3f), Hold(CarButtons.SteerLeft, 1.5f, 1 / 120f, r.TickSeconds).Steer, 0.01f);
    }

    [Fact]
    public void OriginalRampIsPerTick()
    {
        // 0.4 on the first tick (hold = one tick of 3 s), and the faster rate builds faster
        var s = new DriverState();
        float t = 0, st = 0;
        KeyRamp.Apply(ref s, new CarInput { Buttons = CarButtons.ThrottleUp }, 0.05f, null, ref t, ref st);
        Assert.Equal(0.4f * (1 - 0.05f / 3f), t, 1e-6f);
        var slow = Hold(CarButtons.ThrottleUp, 0.1f, 1 / 20f, null).Throttle;
        var fast = Hold(CarButtons.ThrottleUp, 0.1f, 1 / 60f, null).Throttle;
        Assert.True(fast > slow);
        Assert.Equal(0.9999f, Hold(CarButtons.ThrottleUp, 2f, 1 / 20f, null).Throttle, 1e-6f);
    }

    [Fact]
    public void ReleaseZeroesAndAnalogPassesThrough()
    {
        var s = new DriverState();
        float t = 0.7f, st = 0.3f;
        KeyRamp.Apply(ref s, default, 0.01f, SimRate.R20.TickSeconds, ref t, ref st);
        Assert.Equal(0, t); Assert.Equal(0, st);
        KeyRamp.Apply(ref s, new CarInput { Buttons = CarButtons.Analog, Throttle = 16384, Steer = -8192 }, 0.01f, SimRate.R20.TickSeconds, ref t, ref st);
        Assert.Equal(0.5f, t, 1e-3f); Assert.Equal(-0.25f, st, 1e-3f);
    }
}
