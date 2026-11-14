using System.Text.RegularExpressions;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern sky's cloud noise (sky_modern.gdshaderinc): its lattice hash has no sine, since the sine of the arguments the finer
/// octaves reach lost its precision on the GPU and left runs of lattice points alike, which the density ramp drew as straight-edged
/// blocks in the clouds (#149). The hash is mirrored here over the lattice the octaves reach and must be uniform and uncorrelated.
/// </summary>
public class SkyNoiseTests
{
    // sky_modern.gdshaderinc's hash(vec2 p), in the shader's single precision
    private static float Hash(float x, float y)
    {
        float qx = Frac(x * 0.1031f), qy = Frac(y * 0.1030f), qz = Frac(x * 0.0973f);
        float d = qx * (qy + 33.33f) + qy * (qz + 33.33f) + qz * (qx + 33.33f);
        qx += d; qy += d; qz += d;
        return Frac((qx + qy) * qz);
    }

    private static float Frac(float v) => v - MathF.Floor(v);

    [Fact]
    public void TheHashIsUniformAndUncorrelatedOverTheLatticeTheOctavesReach()
    {
        // the finest octave of the edge noise reaches p of about 5 x 17 x 2.03^3 = 700 across the dome; take the lattice to 2000
        const int n = 2000, bins = 16;
        var hist = new int[bins];
        double sum = 0, sumSq = 0, cross = 0; long count = 0;
        for (int y = 0; y < n; y += 3)
            for (int x = 0; x < n; x++)
            {
                float h = Hash(x, y), right = Hash(x + 1, y);
                Assert.InRange(h, 0f, 1f);
                hist[Math.Min(bins - 1, (int)(h * bins))]++;
                sum += h; sumSq += h * h; cross += (h - 0.5) * (right - 0.5); count++;
            }
        double expected = (double)count / bins;
        for (int b = 0; b < bins; b++) Assert.InRange(hist[b] / expected, 0.9, 1.1);
        double variance = sumSq / count - (sum / count) * (sum / count);
        Assert.InRange(cross / count / variance, -0.02, 0.02);   // a lattice point and its right neighbour
    }

    [Fact]
    public void TheShadersHashHasNoSine()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", "sky_modern.gdshaderinc"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "game", "Shaders", "sky_modern.gdshaderinc"));
        var m = Regex.Match(src, @"float hash\(vec2 p\) \{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(m.Success, "hash(vec2 p)");
        Assert.DoesNotContain("sin(", m.Groups[1].Value);
        Assert.Contains("fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973))", m.Groups[1].Value);
    }
}
