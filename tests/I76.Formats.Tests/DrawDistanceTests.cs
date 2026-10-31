using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The Draw Distance setting (Graphic Detail): Far by default, the original's 600 m clip at Original.</summary>
public class DrawDistanceTests
{
    [Fact]
    public void AMissingOrUnknownValueIsFar()
    {
        Assert.Equal(DrawDistance.Far, DrawDistance.Parse(null));   // an old shell.cfg without the key
        Assert.Equal(DrawDistance.Far, DrawDistance.Parse(7));
        Assert.Equal(DrawDistance.Far, DrawDistance.Parse(-1));
    }

    [Theory]
    [InlineData(DrawDistance.Original)]
    [InlineData(DrawDistance.Far)]
    public void AChosenValueRoundTrips(int setting) => Assert.Equal(setting, DrawDistance.Parse(setting));

    [Fact]
    public void OriginalClipsAtTheEnginesSixHundred()
    {
        Assert.Equal(600f, DrawDistance.CameraFar(DrawDistance.Original));
        Assert.Equal(20000f, DrawDistance.CameraFar(DrawDistance.Far));
    }

    [Fact]
    public void TheModernFadeEndsInsideTheClip()
    {
        Assert.True(DrawDistance.ModernFog(DrawDistance.Original));
        Assert.False(DrawDistance.ModernFog(DrawDistance.Far));
        Assert.True(DrawDistance.FogBegin < DrawDistance.FogEnd && DrawDistance.FogEnd < DrawDistance.OriginalClip);
    }

    [Fact]
    public void ThePanelsAreDrawnOnlyAtOriginalAndOnlyWhenTheMissionHasThem()
    {
        Assert.True(DrawDistance.ModernPanels(DrawDistance.Original, true));
        Assert.False(DrawDistance.ModernPanels(DrawDistance.Original, false));   // n01: no panels, no white band
        Assert.False(DrawDistance.ModernPanels(DrawDistance.Far, true));
    }
}
