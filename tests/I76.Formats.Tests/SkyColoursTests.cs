using System;
using System.Numerics;
using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The modern sky's colours from a mission's sky texture: sky and clouds told apart by colour, not brightness.</summary>
public class SkyColoursTests
{
    // linear pixels: a share of them one colour, the rest another, with a little spread
    private static float[] TwoColours(Vector3 a, Vector3 b, double shareA, int n = 4096)
    {
        var rgb = new float[n * 3];
        var rand = new Random(7);
        for (int i = 0; i < n; i++)
        {
            var c = (i < n * shareA ? a : b) * (0.95f + 0.1f * (float)rand.NextDouble());
            rgb[i * 3] = c.X; rgb[i * 3 + 1] = c.Y; rgb[i * 3 + 2] = c.Z;
        }
        return rgb;
    }

    private static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 0.08f, $"{got} not near {want}");

    [Fact]
    public void ADayIsBlueSkyUnderWhiteClouds()
    {
        Vector3 blue = new(0.05f, 0.12f, 0.5f), white = new(0.8f, 0.8f, 0.82f);
        var m = SkyColours.Measure(TwoColours(blue, white, 0.6));
        Near(blue, m.Sky);
        Near(white, m.Cloud);
    }

    [Fact]
    public void ASunsetsRustCloudsDarkerThanItsBlueAreTheClouds()
    {
        // nk_6clda's kind: a light blue sky behind rust-red clouds darker than it (the darker group was taken for the sky)
        Vector3 blue = new(0.25f, 0.4f, 0.75f), rust = new(0.3f, 0.08f, 0.04f);
        var m = SkyColours.Measure(TwoColours(blue, rust, 0.5));
        Near(blue, m.Sky);
        Near(rust, m.Cloud);
        Near(rust, m.CloudEnd);
        Assert.True(SkyColours.Cover(rust, m.Sky, m.CloudEnd) > 0.9f);
        Assert.True(SkyColours.Cover(blue, m.Sky, m.CloudEnd) < 0.1f);
    }

    [Fact]
    public void ABlueGreySkysSlateCloudsAreTheClouds()
    {
        // nk_6cld0's kind: light blue with darker blue-grey patches
        Vector3 light = new(0.45f, 0.55f, 0.85f), slate = new(0.15f, 0.17f, 0.24f);
        var m = SkyColours.Measure(TwoColours(light, slate, 0.55));
        Near(light, m.Sky);
        Near(slate, m.Cloud);
    }

    [Fact]
    public void ANightIsNavyUnderVioletClouds()
    {
        Vector3 navy = new(0.01f, 0.01f, 0.09f), violet = new(0.09f, 0.05f, 0.16f);
        var m = SkyColours.Measure(TwoColours(navy, violet, 0.6));
        Near(navy, m.Sky);
        Near(violet, m.Cloud);
    }

    [Fact]
    public void AGreySkyAsBlueAsItsCloudsTakesTheDarkerAsSky()
    {
        Vector3 dark = new(0.3f, 0.3f, 0.32f), light = new(0.7f, 0.7f, 0.72f);
        var m = SkyColours.Measure(TwoColours(dark, light, 0.5));
        Near(dark, m.Sky);
        Near(light, m.Cloud);
    }

    [Fact]
    public void CoverRampsAlongTheSkyToCloudLine()
    {
        Vector3 sky = new(0.1f, 0.2f, 0.6f), cloud = new(0.8f);
        Assert.Equal(0f, SkyColours.Along(sky, sky, cloud), 4);
        Assert.Equal(1f, SkyColours.Along(cloud, sky, cloud), 4);
        Assert.Equal(0f, SkyColours.Cover(sky - (cloud - sky) * 0.2f, sky, cloud));
        Assert.Equal(1f, SkyColours.Cover(cloud + (cloud - sky) * 0.2f, sky, cloud));
        Assert.InRange(SkyColours.Cover((sky + cloud) * 0.5f, sky, cloud), 0.4f, 0.65f);
    }
}
