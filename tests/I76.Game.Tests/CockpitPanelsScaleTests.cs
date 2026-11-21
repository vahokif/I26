using I76.Game.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The cockpit's panels drawn from a texture pack: the layout's pixels stamped on a panel and from a sheet each at its own scale,
/// and the radar's blips placed on an upscaled frame. Tiny synthetic pictures only.
/// </summary>
public class CockpitPanelsScaleTests
{
    /// <summary>A w × h RGBA8 sheet in stored (bottom-up) order whose texel (x, y) of the picture is (x, y, 0, a).</summary>
    private static byte[] Sheet(int w, int h, Func<int, int, byte> alpha)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int i = ((h - 1 - y) * w + x) * 4; b[i] = (byte)x; b[i + 1] = (byte)y; b[i + 3] = alpha(x, y); }
        return b;
    }

    private static (byte X, byte Y, byte A) At(byte[] img, int w, int h, int x, int y) { int i = ((h - 1 - y) * w + x) * 4; return (img[i], img[i + 1], img[i + 3]); }

    [Fact]
    public void ASheetAtTwiceThePanelsScaleIsSampledNearest()
    {
        var sheet = Sheet(8, 8, (x, y) => 255);
        var panel = new byte[4 * 4 * 4];
        // the layout's 2x2 rectangle at (1, 1) of the sheet to (2, 2) of the panel
        CockpitPanels.Blit(sheet, 8, 8, 2, 1, 1, 2, 2, panel, 4, 4, 1, 2, 2, keyed: true);
        Assert.Equal(((byte)2, (byte)2, (byte)255), At(panel, 4, 4, 2, 2));
        Assert.Equal(((byte)4, (byte)4, (byte)255), At(panel, 4, 4, 3, 3));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(panel, 4, 4, 1, 1));
    }

    [Fact]
    public void APanelAtTwiceTheSheetsScaleRepeatsEachTexelAndKeepsTheKey()
    {
        var sheet = Sheet(4, 4, (x, y) => (byte)(x == 1 ? 0 : 255));
        var panel = new byte[8 * 8 * 4];
        CockpitPanels.Blit(sheet, 4, 4, 1, 0, 0, 2, 2, panel, 8, 8, 2, 1, 1, keyed: true);
        // texel (0, 0) covers the panel's (2..3, 2..3); texel (1, y) is keyed and leaves the panel alone
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(panel, 8, 8, 2, 2));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(panel, 8, 8, 3, 3));
        Assert.Equal(((byte)0, (byte)1, (byte)255), At(panel, 8, 8, 2, 4));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(panel, 8, 8, 4, 2));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(panel, 8, 8, 1, 1));
    }

    [Fact]
    public void AnUnkeyedCopyClipsAtTheEdges()
    {
        var sheet = Sheet(4, 4, (x, y) => 0);
        var panel = new byte[4 * 4 * 4];
        CockpitPanels.Blit(sheet, 4, 4, 1, 2, 2, 4, 4, panel, 4, 4, 1, 3, 3, keyed: false);
        Assert.Equal(((byte)2, (byte)2, (byte)0), At(panel, 4, 4, 3, 3));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(panel, 4, 4, 2, 2));
    }

    /// <summary>The stamp texel by texel at scale 1, as the panels drew before a pack: what the scaled one must give exactly.</summary>
    private static void Original(byte[] src, int sw, int sh, int sx, int sy, int w, int h, byte[] dst, int dw, int dh, int dx, int dy, bool keyed)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int x = sx + i, y = sy + j, ox = dx + i, oy = dy + j;
                if (x >= sw || y >= sh || ox < 0 || oy < 0 || ox >= dw || oy >= dh) continue;
                int so = ((sh - 1 - y) * sw + x) * 4;
                if (keyed && src[so + 3] < 128) continue;
                System.Buffer.BlockCopy(src, so, dst, ((dh - 1 - oy) * dw + ox) * 4, 4);
            }
    }

    [Fact]
    public void WithoutAPackTheStampIsTheOriginalsTexelForTexel()
    {
        var rng = new Random(76);
        for (int n = 0; n < 200; n++)
        {
            int sw = 24 + rng.Next(3), sh = 20 + rng.Next(3), dw = 30 + rng.Next(3), dh = 26 + rng.Next(3);
            var src = new byte[sw * sh * 4];
            rng.NextBytes(src);
            var a = new byte[dw * dh * 4];
            rng.NextBytes(a);
            var b = (byte[])a.Clone();
            int sx = rng.Next(0, 26), sy = rng.Next(0, 22), w = rng.Next(1, 14), h = rng.Next(1, 14), dx = rng.Next(-6, 34), dy = rng.Next(-6, 30);
            bool keyed = rng.Next(2) == 0;
            Original(src, sw, sh, sx, sy, w, h, a, dw, dh, dx, dy, keyed);
            CockpitPanels.Blit(src, sw, sh, 1, sx, sy, w, h, b, dw, dh, 1, dx, dy, keyed);
            Assert.True(a.AsSpan().SequenceEqual(b), $"rect {sx},{sy} {w}x{h} to {dx},{dy} keyed {keyed}");
        }
    }

    [Fact]
    public void AnOriginalRadarFrameTakesTheBlipAtTheScopesPixels()
    {
        var r = Cockpit.BlipRect(0.5f, -0.5f, 2, 256, 128, 1);
        Assert.Equal((124, 39, 2, 2), (r.Position.X, r.Position.Y, r.Size.X, r.Size.Y));
    }

    [Fact]
    public void AnUpscaledRadarFrameTakesItAtTheSamePlaceScaled()
    {
        // a 4x frame: the same blip four times as far in and four times the size, not in the frame's top-left quarter
        var r = Cockpit.BlipRect(0.5f, -0.5f, 2, 1024, 512, 4);
        Assert.Equal((496, 156, 8, 8), (r.Position.X, r.Position.Y, r.Size.X, r.Size.Y));
    }

    [Fact]
    public void ABlipStaysInsideTheFrameAtAnyScale()
    {
        var a = Cockpit.BlipRect(5f, 5f, 3, 256, 128, 1);
        var b = Cockpit.BlipRect(5f, 5f, 3, 1024, 512, 4);
        Assert.Equal((253, 125), (a.Position.X, a.Position.Y));
        Assert.Equal((1012, 500, 12), (b.Position.X, b.Position.Y, b.Size.X));
    }
}
