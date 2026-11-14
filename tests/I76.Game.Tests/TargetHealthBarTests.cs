using Godot;
using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The target's health bar: the original's width by the square root of the health, outer (w - 1)x4 and inner (w - 2)x3 quads 5 pixels
/// over the box, at 640x480 and scaled with the window. Testers found the modern look's bar thin and wide.
/// </summary>
public class TargetHealthBarTests
{
    [Theory]
    [InlineData(1f, 30, 3)]
    [InlineData(0.5f, 21, 1)]
    [InlineData(0.25f, 15, 0)]
    [InlineData(0.9f, 28, 2)]
    [InlineData(0f, 1, 0)]     // counted as 0.1 %
    public void TheWidthFollowsTheSquareRootOfTheHealth(float health, int width, int band)
    {
        float hp = TargetHealthBar.Percent(health);
        Assert.Equal(width, TargetHealthBar.Width(hp));
        Assert.Equal(band, TargetHealthBar.Band(hp));
    }

    [Fact]
    public void At640x480TheQuadsAreTheOriginals()
    {
        var (outer, inner) = TargetHealthBar.Rects(100f, 320, 200, 1);
        Assert.Equal(new Rect2(305, 195, 29, 4), outer);
        Assert.Equal(new Rect2(306, 196, 28, 3), inner);
    }

    [Fact]
    public void At1440PixelsHighEverythingIsThreeTimesAsBig()
    {
        var (outer, inner) = TargetHealthBar.Rects(100f, 960, 600, 3);
        Assert.Equal(new Rect2(960 - 15 * 3, 600 - 5 * 3, 29 * 3, 4 * 3), outer);
        Assert.Equal(new Rect2(960 - 14 * 3, 600 - 4 * 3, 28 * 3, 3 * 3), inner);
    }

    [Fact]
    public void ANearlyDeadCarsBarHasNoInnerQuad()
    {
        var (outer, inner) = TargetHealthBar.Rects(TargetHealthBar.Percent(0f), 320, 200, 1);
        Assert.False(outer.HasArea());
        Assert.False(inner.HasArea());
        var (o2, i2) = TargetHealthBar.Rects(TargetHealthBar.Percent(0.005f), 320, 200, 1);   // 0.5 %: width 2
        Assert.True(o2.HasArea());
        Assert.False(i2.HasArea());
    }
}
