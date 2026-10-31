using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class ViewZoomTests
{
    public static IEnumerable<object[]> Kinds() => Enum.GetValues<ViewZoom.Kind>().Select(k => new object[] { k });

    [Theory, MemberData(nameof(Kinds))]
    public void WheelUpIsNearerAndEveryViewClamps(ViewZoom.Kind k)
    {
        Assert.True(k == ViewZoom.Kind.Side ? ViewZoom.Notch(k, 2f, 1) < 2f : ViewZoom.Notch(k, 1f, 1) < 1f);   // the side view starts at its nearest
        Assert.True(ViewZoom.Notch(k, 1f, -1) > 1f);
        Assert.Equal(ViewZoom.Min(k), ViewZoom.Notch(k, 1f, 200));
        Assert.Equal(ViewZoom.Max(k), ViewZoom.Notch(k, 1f, -200));
        Assert.Equal(ViewZoom.Default, ViewZoom.Clamp(k, float.NaN));
        Assert.True(ViewZoom.Min(k) <= 1f && ViewZoom.Max(k) > 1f);   // the default is inside the range (the side view only zooms out)
    }

    [Fact] public void ChaseKeepsItsOwnBounds()
    {
        Assert.Equal(ChaseZoom.Min, ViewZoom.Min(ViewZoom.Kind.Chase));
        Assert.Equal(ChaseZoom.Max, ViewZoom.Max(ViewZoom.Kind.Chase));
    }

    [Fact] public void TargetBoomScalesTheEyeDistanceAndDefaultsToTheOriginal()
    {
        var first = new Vector3(0, 0, 0);
        var other = new Vector3(0, 0, 100);   // far: no tilt
        var (e1, f1) = TargetViews.Look(first, other);
        var (e1b, _) = TargetViews.Look(first, other, null, 1f);
        Assert.Equal(e1, e1b);
        Assert.Equal(TargetViews.Behind, (e1 - new Vector3(0, TargetViews.PivotLift, 0)).Length(), 3);
        var (e2, f2) = TargetViews.Look(first, other, null, 2f);
        Assert.Equal(2 * TargetViews.Behind, (e2 - new Vector3(0, TargetViews.PivotLift, 0)).Length(), 3);
        Assert.Equal(f1, f2);   // the aim is the same
    }

    [Fact] public void ProjectileBoomScalesTheDistanceBehindTheRound()
    {
        var (e1, _, _) = TargetViews.Projectile(Vector3.Zero, Rows.Identity);
        var (e2, _, _) = TargetViews.Projectile(Vector3.Zero, Rows.Identity, 0.5f);
        Assert.Equal(e1.Length() * 0.5f, e2.Length(), 3);
        Assert.Equal(TargetViews.ProjectileBack, e1.Length(), 3);
    }

    [Fact] public void OverheadHeightScalesAndKeepsTheOriginalsBounds()
    {
        Assert.Equal(100f, OverviewZoom.Scaled(100f, 1f, 2.5f, 20000f));
        Assert.Equal(200f, OverviewZoom.Scaled(100f, 2f, 2.5f, 20000f));
        Assert.Equal(10f, OverviewZoom.Scaled(100f, 0.01f, 2.5f, 20000f));      // 4 radii
        Assert.Equal(17000f, OverviewZoom.Scaled(100f, 1000f, 2.5f, 20000f));   // 0.85 far
    }

    [Fact] public void BumperLensIsThe120DegreeViewAtTheDefaultAndNarrowsOnZoomIn()
    {
        float original = MathF.Atan(MathF.Tan(60f * MathF.PI / 180f) * 0.75f) * 360f / MathF.PI;
        Assert.Equal(original, ViewZoom.BumperFov(1f), 3);
        Assert.True(ViewZoom.BumperFov(ViewZoom.Min(ViewZoom.Kind.Bumper)) < original);
        Assert.InRange(ViewZoom.BumperFov(ViewZoom.Max(ViewZoom.Kind.Bumper)), original, 170f);
    }
}
