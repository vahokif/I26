using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>F4, F5 and F11: the original's eye and the projectile camera's pose.</summary>
public class TargetViewsTests
{
    [Fact]
    public void Far_target_eye_is_7_5_behind_and_3_up_on_the_line_to_it()
    {
        var p = new Vector3(10, 5, 20);
        var t = p + new Vector3(60, 0, 80);   // 100 away: no tilt
        var (eye, fwd) = TargetViews.Look(p, t);
        var n = Vector3.Normalize(t - new Vector3(p.X, p.Y - 2, p.Z));
        Assert.True(Vector3.Distance(fwd, n) < 1e-5f);
        Assert.True(Vector3.Distance(eye, new Vector3(p.X, p.Y + 3, p.Z) - 7.5f * n) < 1e-4f);
        Assert.True(fwd.X > 0 && fwd.Z > 0);   // ahead-right of the player's frame is +x, +z
    }

    [Fact]
    public void Near_target_swings_the_boom_up_and_the_view_down()
    {
        var p = Vector3.Zero;
        var (eyeFar, fwdFar) = TargetViews.Look(p, new Vector3(0, 0, 40));
        var (eyeNear, fwdNear) = TargetViews.Look(p, new Vector3(0, 0, 8));
        Assert.True(fwdNear.Y < fwdFar.Y - 0.3f);   // 30 degrees down at 8 units
        Assert.True(eyeNear.Y > eyeFar.Y + 2f);
        Assert.Equal(7.5f, Vector3.Distance(eyeNear, new Vector3(0, 3, 0)), 3);
    }

    [Fact]
    public void Tilt_is_zero_at_33_units_and_30_degrees_at_10()
    {
        float Pitch(float dist) { var (_, f) = TargetViews.Look(Vector3.Zero, new Vector3(0, -2, dist)); return MathF.Asin(-f.Y); }
        Assert.Equal(0f, Pitch(34), 3);
        Assert.Equal(MathF.PI / 6, Pitch(9), 2);
        Assert.InRange(Pitch(20), 0.1f, 0.4f);
    }

    [Fact]
    public void Eye_is_held_above_the_ground()
    {
        var (eye, _) = TargetViews.Look(new Vector3(0, 1, 0), new Vector3(0, 30, 50), (x, z) => 4f);
        Assert.Equal(4.6f, eye.Y, 4);
    }

    [Fact]
    public void Projectile_camera_is_5_behind_pitched_20_down()
    {
        var (eye, fwd, up) = TargetViews.Projectile(new Vector3(1, 2, 3), Rows.Identity);
        Assert.Equal(1f, eye.X, 4);
        Assert.Equal(2f + 5 * MathF.Sin(MathF.PI / 9), eye.Y, 4);
        Assert.Equal(3f - 5 * MathF.Cos(MathF.PI / 9), eye.Z, 4);
        Assert.Equal(-MathF.Sin(MathF.PI / 9), fwd.Y, 4);
        Assert.True(up.Y > 0.9f);
        // the camera looks at the round
        Assert.True(Vector3.Distance(eye + fwd * 5f, new Vector3(1, 2, 3)) < 1e-3f);
    }
}
