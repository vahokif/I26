using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

public class GlassShapeTests
{
    private static byte[] Mask(int w, int h, Func<int, int, bool> glass)
    {
        var m = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) m[y * w + x] = glass(x, y) ? (byte)255 : (byte)0;
        return m;
    }

    [Fact]
    public void A_stepped_diagonal_edge_comes_out_straight()
    {
        // glass below the line y = x / 2: each texel row's edge steps by two texels
        const int w = 32, h = 16, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => y + 0.5f > x / 2f), w, h);
        // along the edge, each pixel row's half-coverage crossing lies on one straight line (within half a texel), not on the steps
        var cross = new List<(float X, float Y)>();
        for (int py = 4 * k; py < 12 * k; py++)
            for (int px = 1; px < w * k; px++)
                if (cover[py * w * k + px - 1] >= 128 && cover[py * w * k + px] < 128) { cross.Add((px / (float)k, (py + 0.5f) / k)); break; }
        Assert.True(cross.Count > 20);
        // a least-squares line through the crossings, and how far any crossing strays from it
        float mx = cross.Average(c => c.X), my = cross.Average(c => c.Y);
        float sxy = cross.Sum(c => (c.X - mx) * (c.Y - my)), sxx = cross.Sum(c => (c.X - mx) * (c.X - mx));
        float slope = sxy / sxx;
        float worst = cross.Max(c => MathF.Abs(c.Y - (my + slope * (c.X - mx))));
        Assert.InRange(slope, 0.4f, 0.6f);
        Assert.True(worst < 0.5f, $"a crossing {worst:F2} texels off the line");
    }

    [Fact]
    public void A_pillar_one_texel_wide_keeps_its_width()
    {
        // two panes with a one-texel pillar between them at x = 10
        const int w = 24, h = 8, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => y >= 2 && y < 6 && x >= 2 && x < 22 && x != 10), w, h);
        int row = 4 * k;
        // the pillar's pixels are clear of glass, the panes either side full
        for (int px = 10 * k; px < 11 * k; px++) Assert.True(cover[row * w * k + px] < 64, $"pillar pixel {px} covered {cover[row * w * k + px]}");
        Assert.Equal(255, cover[row * w * k + 9 * k + 1]);
        Assert.Equal(255, cover[row * w * k + 11 * k + 2]);
    }

    [Fact]
    public void A_window_on_the_texture_edge_reaches_it()
    {
        const int w = 8, h = 8, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => x < 4), w, h);
        Assert.Equal(255, cover[3 * k * w * k + 0]);
        Assert.Equal(0, cover[3 * k * w * k + w * k - 1]);
    }

    [Fact]
    public void Specks_and_pinholes_are_left_out()
    {
        // a pane with a one-texel hole, and a one-texel speck of glass beside it
        const int w = 16, h = 8, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => (x >= 1 && x < 8 && y >= 1 && y < 7 && !(x == 4 && y == 4)) || (x == 12 && y == 4)), w, h);
        Assert.Equal(255, cover[(4 * k + 2) * w * k + 4 * k + 2]);
        Assert.Equal(0, cover[(4 * k + 2) * w * k + 12 * k + 2]);
    }

    [Fact]
    public void A_notch_in_an_edge_is_filled()
    {
        // a pane whose top edge has a bite two texels wide and one deep
        const int w = 24, h = 10, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => x >= 2 && x < 22 && y >= 2 && y < 8 && !(y == 2 && (x == 10 || x == 11))), w, h);
        Assert.Equal(255, cover[(2 * k + 2) * w * k + 10 * k + 2]);
    }

    [Fact]
    public void A_step_in_a_panes_edge_is_drawn_as_its_hull()
    {
        // a pane whose top edge steps down two texels over its left part (too deep to straighten, too big a notch to fill)
        const int w = 30, h = 14, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => x >= 2 && x < 28 && y >= (x < 8 ? 4 : 2) && y < 12), w, h);
        // the hull runs from the left corner to the step: the texel at (4, 3), outside the stepped outline, is glass
        Assert.True(cover[(3 * k + 2) * w * k + 4 * k + 2] > 200);
    }

    [Fact]
    public void An_L_shaped_pane_keeps_its_shape()
    {
        const int w = 24, h = 24, k = GlassShape.Scale;
        var cover = GlassShape.Rasterize(Mask(w, h, (x, y) => x >= 2 && y >= 2 && (x < 8 || y < 8) && x < 22 && y < 22), w, h);
        Assert.Equal(0, cover[(15 * k) * w * k + 15 * k]);
    }
}
