using System.Linq;
using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>Graphic Detail's Clouds (the modern sky's volumetric clouds): the setting, the tiling noise and the cover map.</summary>
public class CloudNoiseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("flat")]
    [InlineData("puffy")]
    public void AMissingOrUnknownWordIsFlat(string? word) => Assert.Equal(CloudNoise.Flat, CloudNoise.Parse(word));

    [Theory]
    [InlineData("volumetric")]
    [InlineData(" Volumetric ")]
    public void VolumetricIsReadInAnyCase(string word) => Assert.Equal(CloudNoise.Volumetric, CloudNoise.Parse(word));

    [Theory]
    [InlineData(CloudNoise.Flat)]
    [InlineData(CloudNoise.Volumetric)]
    public void AChosenValueRoundTrips(int mode) => Assert.Equal(mode, CloudNoise.Parse(CloudNoise.Word(mode)));

    // the noise repeats across the unit cube on every axis, so the textures tile without a seam
    [Theory]
    [InlineData(0.13f, 0.71f, 0.42f)]
    [InlineData(0.98f, 0.02f, 0.5f)]
    public void WorleyTiles(float x, float y, float z)
    {
        foreach (int cells in new[] { 2, 4, 16 })
        {
            var w = new CloudNoise.WorleyGrid(cells);
            float v = w.At(x, y, z);
            Assert.Equal(v, w.At(x + 1f, y, z), 3);
            Assert.Equal(v, w.At(x, y - 1f, z), 3);
            Assert.Equal(v, w.At(x, y, z + 1f), 3);
        }
    }

    [Theory]
    [InlineData(0.13f, 0.71f, 0.42f)]
    [InlineData(0.98f, 0.02f, 0.5f)]
    public void PerlinTiles(float x, float y, float z)
    {
        float v = CloudNoise.PerlinFbm(x, y, z, 4, 4);
        Assert.Equal(v, CloudNoise.PerlinFbm(x + 1f, y, z, 4, 4), 3);
        Assert.Equal(v, CloudNoise.PerlinFbm(x, y + 1f, z, 4, 4), 3);
        Assert.Equal(v, CloudNoise.PerlinFbm(x, y, z - 1f, 4, 4), 3);
    }

    [Fact]
    public void TheVolumesSpanTheirRange()
    {
        var shape = CloudNoise.Shape(16);
        var detail = CloudNoise.Detail(8);
        Assert.Equal(16 * 16 * 16, shape.Length);
        Assert.Equal(8 * 8 * 8, detail.Length);
        // neither flat nor saturated: lumps with clear air between them
        // stretched over the bytes: lumps with clear air between them, not bunched at one end
        Assert.Equal(0, shape.Min());
        Assert.Equal(255, shape.Max());
        Assert.InRange(shape.Average(b => (double)b), 96, 224);
        Assert.InRange(detail.Average(b => (double)b), 64, 192);
    }

    [Fact]
    public void TheFarShapeIsTheBlockMean()
    {
        // a 4^3 volume, x and y as written, shrunk by 2: each texel the mean of its 2^3 block
        var v = new byte[64];
        for (int i = 0; i < 64; i++) v[i] = (byte)(i * 4);
        var s = CloudNoise.Shrink(v, 4, 2);
        Assert.Equal(8, s.Length);
        // the first block: x 0-1, y 0-1, z 0-1 → indices 0,1,4,5,16,17,20,21, times 4, mean 42
        Assert.Equal(42, s[0]);
        Assert.Equal(s[0] + 8, s[1]);   // one block along x: two texels on
    }

    [Fact]
    public void TheCoverFollowsTheSkyTexturesBrightness()
    {
        const int n = 32;
        // the left half open sky (dark blue), the right half cloud (white)
        var rgb = new byte[n * n * 3];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = x < n / 2 ? ((byte)40, (byte)70, (byte)150) : ((byte)255, (byte)255, (byte)255);
            }
        var cover = CloudNoise.Cover(rgb, n, n, skyLum: 0.06f, cloudLum: 0.9f, scale: 2);
        int m = n * 2;
        Assert.Equal(m * m * 2, cover.Length);
        // two values a texel, enlarged twice: the softened cover, then the wide one; both about clear in the open sky's middle and whole in the cloud's
        Assert.True(cover[(10 * m + m / 4) * 2] < 0.06f);
        Assert.True(cover[(10 * m + 3 * m / 4) * 2] > 0.98f);
        Assert.True(cover[(10 * m + m / 4) * 2 + 1] < 0.25f);
        Assert.True(cover[(10 * m + 3 * m / 4) * 2 + 1] > 0.75f);
    }

    [Fact]
    public void EnlargingKeepsAFlatImageFlatAndItsMean()
    {
        var flat = Enumerable.Repeat(0.4f, 16).ToArray();
        Assert.All(CloudNoise.Enlarge(flat, 4, 4, 3), v => Assert.Equal(0.4f, v, 4));
        // a tiling image keeps its mean (the B-spline's weights sum to one and it wraps)
        var spot = new float[64];
        spot[27] = 1f;
        Assert.Equal(spot.Average(), CloudNoise.Enlarge(spot, 8, 8, 4).Average(), 4);
    }
}
