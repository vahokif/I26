using I76.Game.Shell.Garage3D;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The 3D garage shows every vehicle at a size its bay and camera can frame (issue #126).</summary>
public class GarageFitTests
{
    [Theory]
    [InlineData(4.5f)]
    [InlineData(5.2f)]
    [InlineData(3.0f)]
    public void TheCarsTheBayIsMadeForAreShownAtTheirOwnSize(float longest) => Assert.Equal(1f, GarageViews.FitFor(longest));

    [Theory]
    [InlineData(16.0f)]    // the Huey
    [InlineData(13.6f)]    // the Cobra
    [InlineData(7.1f)]     // the tank
    public void AFlyerOrABigVehicleIsShrunkToTheBaysLargestCar(float longest)
    {
        float fit = GarageViews.FitFor(longest);
        Assert.InRange(fit, 0.01f, 0.99f);
        Assert.Equal(GarageViews.MaxLength, longest * fit, 3);
    }

    [Fact]
    public void ATinyVehicleIsEnlarged() => Assert.Equal(GarageViews.MinLength, 0.93f * GarageViews.FitFor(0.93f), 3);
}
