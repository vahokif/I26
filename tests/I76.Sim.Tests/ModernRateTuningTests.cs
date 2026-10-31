using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

public class ModernRateTuningTests
{
    [Fact]
    public void TableHasOneRowPerOfferedRate()
    {
        Assert.Equal(SimRate.Offered.Select(r => r.Label), ModernRateTuning.Table.Select(r => r.Rate));
        foreach (var r in SimRate.Offered) Assert.Equal(r.Label, ModernRateTuning.For(r).Rate);
    }

    [Fact]
    public void UnofferedRateGetsTheDefaultsRow()
    {
        Assert.Equal(SimRate.Default.Label, ModernRateTuning.For(new SimRate(50, 0.02f)).Rate);
    }

    [Theory]
    [InlineData(20, 0.0207f, 10.04f)]
    [InlineData(28, 0.0138f, 13.89f)]
    [InlineData(60, 0.0041f, 30.12f)]
    public void RuleDerivedValuesFollowTheTick(int hz, float yawResponse, float stickBreak)
    {
        // the yaw's answer: half a tick, the original's mean wait for its next key sample, less the body's own half step;
        // the ground lets go at 0.5 below the path within a tick
        var row = ModernRateTuning.For(SimRate.FromLabel(hz));
        Assert.Equal(yawResponse, row.YawResponse, 1e-3f);
        Assert.Equal(stickBreak, row.StickBreak, 0.01f);
    }

    [Fact]
    public void FittedValuesMoveMonotonicallyWithTheRate()
    {
        // the fitted parameters follow smooth measured trends in the tick (docs/modern-physics.md "Rate"): no row
        // out of order
        var t = ModernRateTuning.Table;
        for (int i = 1; i < t.Length; i++)
        {
            Assert.True(t[i].DriveScale <= t[i - 1].DriveScale + 1e-4f, $"DriveScale {t[i].Rate}");
            Assert.True(t[i].BrakeScale >= t[i - 1].BrakeScale - 1e-4f, $"BrakeScale {t[i].Rate}");
        }
    }
}
