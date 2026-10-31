using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class ChaseZoomTests
{
    [Fact] public void WheelUpIsNearerAndClamps()
    {
        Assert.True(ChaseZoom.Notch(1f, 1) < 1f);
        Assert.True(ChaseZoom.Notch(1f, -1) > 1f);
        Assert.Equal(ChaseZoom.Min, ChaseZoom.Notch(1f, 100));
        Assert.Equal(ChaseZoom.Max, ChaseZoom.Notch(1f, -100));
        Assert.Equal(ChaseZoom.Default, ChaseZoom.Clamp(float.NaN));
    }

    [Fact] public void EaseApproachesWithoutOvershoot()
    {
        float z = 1f;
        for (int i = 0; i < 600; i++) { float n = ChaseZoom.Ease(z, 2f, 1 / 60f); Assert.InRange(n, z, 2f); z = n; }
        Assert.Equal(2f, z, 3);
    }

    [Fact] public void MouseOrbitReturnsAfterTheIdleWait()
    {
        var o = new MouseOrbitOffset();
        o.Add(1f, 0.3f);
        for (int i = 0; i < 250; i++) o.Step(1 / 60f);   // 4.2 s: held
        Assert.Equal(1f, o.Yaw);
        for (int i = 0; i < 600; i++) o.Step(1 / 60f);
        Assert.Equal(0f, o.Yaw);
        Assert.Equal(0f, o.Pitch);
    }

    [Fact] public void ReturnOffKeepsTheOffset()
    {
        var o = new MouseOrbitOffset();
        o.Add(1f, 0.3f);
        for (int i = 0; i < 3000; i++) o.Step(1 / 60f, false);
        Assert.Equal(1f, o.Yaw);
        Assert.Equal(0.3f, o.Pitch);
    }

    [Fact] public void MultiTurnOffsetReturnsTheShortWay()
    {
        var o = new MouseOrbitOffset();
        o.Add(MathF.Tau * 3 + 0.2f, 0);
        for (int i = 0; i < 400; i++) o.Step(1 / 60f);
        Assert.True(MathF.Abs(o.Yaw) < 0.25f);
    }

    [Fact] public void MotionRestartsTheWaitAndPitchIsLimited()
    {
        var o = new MouseOrbitOffset();
        o.Add(0, 5f);
        Assert.Equal(MouseOrbitOffset.PitchUp, o.Pitch);
        for (int i = 0; i < 250; i++) o.Step(1 / 60f);
        o.Add(0.1f, 0);
        for (int i = 0; i < 250; i++) o.Step(1 / 60f);
        Assert.Equal(MouseOrbitOffset.PitchUp, o.Pitch);
    }
}
