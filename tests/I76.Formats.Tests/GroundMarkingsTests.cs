using I76.Formats.Assembly;
using I76.Formats.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// GroundMarkings on the real ground textures: M15's parking lot is painted, its lines land where the texture has
/// them; no natural ground family is taken for one.
/// </summary>
public class GroundMarkingsTests(ITestOutputHelper output)
{
    private static (byte[] Rgba, int W, int H) Texture(string stem, string palette)
    {
        var res = new TextureResolver(TestInstall.Vfs);
        var member = res.FindByStem(stem) ?? throw new FileNotFoundException(stem);
        var t = res.Load(member) ?? throw new InvalidDataException(member);
        var pal = TestInstall.Vfs.TryRead(palette) is { } pd ? new Palette(pd) : null;
        return (TextureReader.ToRgba(t, pal), t.Width, t.Height);
    }

    /// <summary>The field's signed distance at a point in source texels (bilinear, as the shader reads it).</summary>
    private static float Distance(GroundMarkings m, float sx, float sy)
    {
        int fw = m.Width * m.Scale, fh = m.Height * m.Scale;
        float x = sx * m.Scale - 0.5f, y = sy * m.Scale - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float At(int xx, int yy) => (m.Field[((yy % fh + fh) % fh * fw + (xx % fw + fw) % fw) * 2] - 127.5f) / 127.5f * GroundMarkings.Range;
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    [InstallFact]
    public void M15sLotIsPaintedWhereTheTextureIs()
    {
        var (rgba, w, h) = Texture("tm152as_", "m15_16.act");
        Assert.Equal(128, w);
        var m = GroundMarkings.Find(rgba, w, h, 40f);
        Assert.NotNull(m);
        output.WriteLine($"{m!.Measured}; scale {m.Scale}, narrowed {m.Narrowed:0.000} texels, coverage {m.Coverage:0.0000}, paint {m.Paint}");
        Assert.Equal(5, m.Scale);
        // the paint's colour is the texture's white, the base its two greys (asphalt 118, lanes 101) with no white left
        Assert.InRange(m.Paint.R, 0.78f, 0.88f);
        float maxBase = 0;
        for (int i = 0; i < w * h; i++) maxBase = Math.Max(maxBase, m.Base[i * 4] / 255f);
        Assert.InRange(maxBase, 118 / 255f - 0.01f, 128 / 255f);
        Assert.InRange(m.Base[(50 * w + 5) * 4], 114, 120);                // asphalt inside a stall (row 50)
        Assert.InRange(m.Base[(21 * w + 50) * 4], 99, 108);                // the tyre lane down the aisle (rows 17-25)
        // stall ticks: one texel wide every 10 columns (x 0..100) through rows 2..13; drawn 0.13 m wide on their centres
        for (int x = 0; x <= 100; x += 10)
        {
            Assert.True(Distance(m, x + 0.5f, 8f) < -0.15f, $"tick {x}: {Distance(m, x + 0.5f, 8f)}");
            Assert.True(Distance(m, x + 0.5f + 0.35f, 8f) > 0.05f, $"tick {x} right edge");
            Assert.True(Distance(m, x + 0.5f - 0.35f, 8f) > 0.05f, $"tick {x} left edge");
        }
        Assert.True(Distance(m, 5.5f, 8f) > 1.5f, "inside a stall");
        // the line heading the double rows: rows 0-1 and 42-43, columns 0..100
        Assert.True(Distance(m, 55f, 1f) < -0.5f && Distance(m, 55f, 43f) < -0.5f, "head lines");
        Assert.True(Distance(m, 55f, 16f) > 1.5f, "aisle");
        // across the lines' ends: the aisle down the right side (columns 102..127) is clear
        Assert.True(Distance(m, 115f, 1f) > 1.5f, "right aisle");
        // the drawn paint: a tick is 0.13 m of 0.3125 a texel; about 6 % of the texture, under its 7.7 % of white texels
        Assert.InRange(m.Coverage, 0.04f, 0.07f);
    }

    private static byte[] Made(int w, int h, Func<int, int, (byte R, byte G, byte B)> px)
    {
        var p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = px(x, y);
                (p[(y * w + x) * 4], p[(y * w + x) * 4 + 1], p[(y * w + x) * 4 + 2], p[(y * w + x) * 4 + 3]) = (r, g, b, 255);
            }
        return p;
    }

    [Fact]
    public void AYellowLineOnFlatAsphaltIsFoundAndKeepsItsPlace()
    {
        // 64x64 (0.625 m texels): grey asphalt, a yellow line two texels wide down columns 20-21, a white one along row 40
        var tex = Made(64, 64, (x, y) => x is 20 or 21 ? ((byte)220, (byte)190, (byte)40) : y == 40 ? ((byte)230, (byte)230, (byte)230) : ((byte)90, (byte)90, (byte)90));
        var m = GroundMarkings.Find(tex, 64, 64, 40f);
        Assert.NotNull(m);
        Assert.Equal(9, m!.Scale);
        Assert.True(m.Paint.B < m.Paint.R, "the paint's colour is the lines' mean, yellow and white");
        Assert.True(Distance(m, 21f, 10f) < -0.4f, "the yellow line's centre");
        Assert.True(Distance(m, 19f, 10f) > 0f && Distance(m, 23f, 10f) > 0f, "either side of it");
        Assert.True(Distance(m, 50f, 40.5f) < -0.05f && Distance(m, 50f, 41.2f) > 0f, "the white line, narrowed to 0.13 m on its centre");
        Assert.Equal(90, m.Base[(10 * 64 + 21) * 4]);   // the base under the paint is the asphalt
    }

    [Fact]
    public void NoiseAndBlobsAreNotPainted()
    {
        var rng = new Random(3);
        var noise = Made(64, 64, (x, y) => { byte v = (byte)rng.Next(60, 200); return (v, v, (byte)(v / 2)); });
        Assert.Null(GroundMarkings.Find(noise, 64, 64, 40f));
        // flat snow with round brown patches: flat and two-toned, but nothing runs in lines
        var snow = Made(64, 64, (x, y) => (x % 16 - 8) * (x % 16 - 8) + (y % 16 - 8) * (y % 16 - 8) < 20 ? ((byte)90, (byte)60, (byte)40) : ((byte)235, (byte)235, (byte)240));
        Assert.False(GroundMarkings.Measure(snow, 64, 64).Painted);
    }

    public static TheoryData<string> Natural => new()
    {
        "tp012gr6", "tp022db6", "tp032db6", "tp042db6", "tp052dr6", "tp062dr6", "tp072dr6", "tp082sd6", "tp092db6", "tp182sw6",
        "tp192sr6", "tt032sd_", "tn102md_",
    };

    [InstallTheory]
    [MemberData(nameof(Natural))]
    public void NaturalGroundIsNotPainted(string family)
    {
        var (rgba, w, h) = Texture(family, "t01_16.act");
        var st = GroundMarkings.Measure(rgba, w, h);
        output.WriteLine($"{family}: {st}");
        Assert.False(st.Painted);
        Assert.Null(GroundMarkings.Find(rgba, w, h, 40f));
    }
}
