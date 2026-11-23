using System.Numerics;
using I76.Sim.Sound;
using Xunit;

namespace I76.Sim.Tests;

public class ListenerMotionTests
{
    private static readonly object Cam = new(), Other = new();

    /// <summary>A camera moving at <paramref name="v"/> for <paramref name="seconds"/> at <paramref name="fps"/>, from <paramref name="from"/>.</summary>
    private static Vector3 Run(ListenerMotion m, ref Vector3 at, Vector3 v, float seconds, float fps, object? cam = null)
    {
        float dt = 1f / fps;
        for (int i = 0; i < (int)MathF.Round(seconds * fps); i++) { at += v * dt; m.Update(at, cam ?? Cam, dt); }
        return m.Velocity;
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(144f)]
    public void ASteadyCameraHasItsVelocity(float fps)
    {
        var m = new ListenerMotion();
        var at = new Vector3(5, 1, 7);
        Assert.Equal(Vector3.Zero, m.Update(at, Cam, 1f / fps));   // the first frame: no step seen yet, standing
        var v = Run(m, ref at, new Vector3(20, 0, -10), 1f, fps);
        Assert.Equal(20f, v.X, 3);
        Assert.Equal(-10f, v.Z, 3);
    }

    [Fact]
    public void EasingIsTheSameAtAnyFrameRate()
    {
        Vector3 After(float fps)
        {
            var m = new ListenerMotion();
            var at = Vector3.Zero;
            Run(m, ref at, new Vector3(0, 0, 10), 1f, fps);
            return Run(m, ref at, new Vector3(0, 0, 12), 0.1f, fps);   // a speed-up under the acceleration limit at any rate
        }
        float a = After(30).Z, b = After(144).Z;
        Assert.InRange(a, 11.1f, 11.4f);   // 1 − 1/e of the way after the smoothing time
        Assert.InRange(MathF.Abs(a - b), 0f, 0.05f);
    }

    [Fact]
    public void ATeleportIsNotMotion()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 30), 1f, 60);
        at += new Vector3(400, 0, 0);                                         // a respawn 400 m off
        Assert.Equal(Vector3.Zero, m.Update(at, Cam, 1f / 60));               // standing for that frame
        at += new Vector3(0, 0, 0.5f);
        Assert.Equal(30f, m.Update(at, Cam, 1f / 60).Z, 3);                   // and the next step believed at once
    }

    [Fact]
    public void AnotherCameraStartsStanding()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 30), 1f, 60);
        Assert.Equal(Vector3.Zero, m.Update(at + new Vector3(2, 1, 0), Other, 1f / 60));
        at += new Vector3(2, 1, 0.5f);
        Assert.Equal(30f, m.Update(at, Other, 1f / 60).Z, 3);
    }

    [Fact]
    public void AReportedCutStandsStill()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 30), 1f, 60);
        m.Cut();
        Assert.Equal(Vector3.Zero, m.Velocity);
        at += new Vector3(0, 0, 0.5f);
        Assert.Equal(Vector3.Zero, m.Update(at, Cam, 1f / 60));   // the first position after the cut: nothing to step from
        at += new Vector3(0, 0, 0.5f);
        Assert.Equal(30f, m.Update(at, Cam, 1f / 60).Z, 3);
    }

    [Fact]
    public void AViewSwungOnTheSameCameraHoldsForAFrame()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 30), 1f, 60);
        at += new Vector3(0, 1.5f, -4f);                                       // F2 to F3: the eye 4 m back and up, in one frame
        Assert.Equal(30f, m.Update(at, Cam, 1f / 60).Z, 3);                    // held, not 270 m/s backwards
        Assert.Equal(30f, Run(m, ref at, new Vector3(0, 0, 30), 0.5f, 60).Z, 3);
    }

    [Fact]
    public void ACrashIsBelievedAfterAFrame()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 40), 1f, 60);
        Assert.Equal(40f, m.Update(at, Cam, 1f / 60).Z, 3);                    // stopped dead at a wall: held one frame
        float z = m.Update(at, Cam, 1f / 60).Z;                                // then eased toward it
        Assert.InRange(z, 30f, 40f);
        Assert.InRange(Run(m, ref at, Vector3.Zero, 1f, 60).Z, 0f, 0.01f);
    }

    [Fact]
    public void APausedFrameKeepsTheVelocity()
    {
        var m = new ListenerMotion();
        var at = Vector3.Zero;
        Run(m, ref at, new Vector3(0, 0, 30), 1f, 60);
        Assert.Equal(30f, m.Update(at, Cam, 0f).Z, 3);
        Assert.Equal(Vector3.Zero, m.Update(new Vector3(float.NaN, 0, 0), Cam, 1f / 60));
    }
}
