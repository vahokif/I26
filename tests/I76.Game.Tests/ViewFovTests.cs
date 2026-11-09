using I76.Sim.Views;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Field of View: the original's 90 degrees across 4:3 is 73.74 high, and a choice scales the tangent of the half angle.</summary>
public class ViewFovTests
{
    [Fact] public void TheOriginalKeepsTheOriginalsHeight() => Assert.Equal(73.74f, ViewFov.Vertical(73.74f, 0), 0.02f);

    [Theory]
    [InlineData(1, 64.4)]    // 80 across 4:3
    [InlineData(2, 83.6)]    // 100
    [InlineData(3, 93.9)]    // 110
    public void AChoiceIsTheFieldAcrossFourByThree(int choice, double vertical)
    {
        float v = ViewFov.Vertical(73.74f, choice);
        Assert.Equal(vertical, v, 0.1);
        // across a 4:3 view: tan(h/2) = tan(v/2) * 4/3
        double across = 2 * System.Math.Atan(System.Math.Tan(v * System.Math.PI / 360) * 4 / 3) * 180 / System.Math.PI;
        Assert.Equal(ViewFov.Across[choice], across, 0.1);
    }

    [Fact] public void ParseKeepsTheOriginalForAnythingElse()
    {
        Assert.Equal(0, ViewFov.Parse(null));
        Assert.Equal(0, ViewFov.Parse(90));
        Assert.Equal(0, ViewFov.Parse(137));
        Assert.Equal(2, ViewFov.Parse(100));
    }

    [Fact] public void ApplyChangesTheMainViewsAndClamps()
    {
        try
        {
            ViewFov.Apply(2);
            Assert.Equal(ViewFov.Vertical(73.74f, 2), ViewFov.Main, 0.001f);
            ViewFov.Apply(99);
            Assert.Equal(3, ViewFov.Choice);
        }
        finally { ViewFov.Apply(0); }
        Assert.Equal(73.74f, ViewFov.Main);
    }

    /// <summary>
    /// The 2D dash's forward reticle sits where the gun line (level with the car, the head 13 degrees up from it at rest) projects:
    /// at the original's view 74 px under the centre, and with another Field of View where that view puts the same direction.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheDashReticleFollowsTheFieldOfView(int choice)
    {
        float vertical = choice == 0 ? ViewFov.OriginalVertical : ViewFov.Vertical(ViewFov.OriginalVertical, choice);
        double Focal(float v) => 240 / Math.Tan(v * Math.PI / 360);
        double drop = Math.Tan(13 * Math.PI / 180);
        float y0 = (float)(240 + Focal(ViewFov.OriginalVertical) * drop);
        Assert.InRange(y0, 313f, 315f);                              // the original's sprite: centre 316 (300 + 16), the gun 2 px under the eye line
        var (x, y) = ViewFov.ScreenPoint(320, y0, vertical);
        Assert.Equal(320f, x, 3);
        Assert.Equal(240 + Focal(vertical) * drop, y, 2);
        // a wider view lifts it toward the centre: 110 across 4:3 puts it 22 px higher, 80 lower
        if (choice == 3) Assert.InRange(y0 - y, 21f, 24f);
        if (choice == 1) Assert.True(y > y0);
    }
}
