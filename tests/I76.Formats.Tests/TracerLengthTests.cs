using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Testers: the 7.62 turret's tracers seemed to start near its base. In the 1997 look a round just fired was drawn as a 4 m
/// bar ending at it, reaching back through the gun and out of the far side of a turret turned to the side.
/// </summary>
public class TracerLengthTests
{
    [Fact]
    public void ARoundJustFiredReachesNoFurtherBackThanTheMuzzle()
    {
        // a 7.62 round (speed 200, a 20 Hz tick: 9.96 m a step), a quarter of a tick out of the muzzle
        float step = 200f * 102 / 2048, travelled = step * 0.25f;
        foreach (float alpha in new[] { 0f, 0.1f, 0.5f, 0.9f, 1f })
        {
            // the drawn head is (1 - alpha) of the last step short of the round; the muzzle is travelled - that behind it
            float flown = TracerLength.Flown(travelled, step, alpha);
            Assert.True(TracerLength.Bar(step, flown) <= System.MathF.Max(0.05f, travelled - step * (1 - alpha)) + 1e-5f);
            Assert.True(TracerLength.Streak(6f, flown) <= flown);
        }
        // a round well on its way keeps its full bar
        Assert.Equal(4f, TracerLength.Bar(step, TracerLength.Flown(50f, step, 0.5f)));
    }
}
