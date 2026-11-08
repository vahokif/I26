using I76.Formats.Shell;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The port's patches to a shell background (ShellFrame.Edits) made to an upscale drawn under the frame: with an HD menu pack
/// the frame is see-through where it is still the background, so the upscale must carry the same patches (Play Options'
/// copied check boxes) or the original's printed rows show through.
/// </summary>
public class HdBackdropEditsTests
{
    private const int W = ShellFrame.W, H = ShellFrame.H, S = 4;

    /// <summary>A background whose every pixel differs from its neighbours (index from x and y), so any copy is seen.</summary>
    private static ShellFrame Frame()
    {
        var px = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) px[y * W + x] = (byte)(x * 7 + y * 13);
        var f = new ShellFrame();
        f.LoadBackground(Pcx.Create(W, H, px, new byte[768]), black: false);
        return f;
    }

    /// <summary>The frame's background now, as a 4x picture: each pixel a 4 x 4 block whose texels say (index, x within, y within).</summary>
    private static byte[] Upscale(byte[] clean)
    {
        var px = new byte[W * S * H * S * 4];
        for (int y = 0; y < H * S; y++)
            for (int x = 0; x < W * S; x++)
            {
                int o = (y * W * S + x) * 4;
                px[o] = clean[y / S * W + x / S]; px[o + 1] = (byte)(x % S); px[o + 2] = (byte)(y % S); px[o + 3] = 255;
            }
        return px;
    }

    /// <summary>The frame's background now (its pixels where nothing is drawn over it).</summary>
    private static byte[] Clean(ShellFrame f)
    {
        f.RestoreAll();
        return f.Pixels.ToArray();
    }

    [Fact]
    public void TheUpscaleTakesTheSamePatchesAsTheFrame()
    {
        var f = Frame();
        var hd = Upscale(Clean(f));
        // Play Options' check box onto rows without one, one of them over another copy
        f.CopyBackground(200, 139, 22, 18, 200, 116);
        f.CopyBackground(20, 100, 50, 30, 10, 356);
        f.CopyBackground(200, 116, 22, 18, 30, 360);
        Assert.Equal(3, f.Edits.Count);
        ShellBackdrop.Edit(hd, W * S, H * S, (S, 0, S, 0), f.Edits);
        Assert.Equal(Upscale(Clean(f)), hd);
    }

    [Fact]
    public void APatchThatChangesNothingIsNotKept()
    {
        var f = Frame();
        f.CopyBackground(200, 139, 22, 18, 200, 116);
        int v = f.EditsVersion;
        f.CopyBackground(200, 139, 22, 18, 200, 116);   // a screen built again on the same background
        Assert.Single(f.Edits);
        Assert.Equal(v, f.EditsVersion);
        f.LoadBackground(Pcx.Black, black: false);
        Assert.Empty(f.Edits);
        Assert.NotEqual(v, f.EditsVersion);
    }

    [Fact]
    public void APictureAtAnOffsetIsPatchedAtItsOwnPlace()
    {
        // a 16:9 picture: the frame's 4:3 content 4 px a frame pixel, 640 texels in from the left
        var f = Frame();
        int iw = W * S + 1280, ih = H * S;
        var hd = new byte[iw * ih * 4];
        var inner = Upscale(Clean(f));
        for (int y = 0; y < ih; y++) Array.Copy(inner, y * W * S * 4, hd, (y * iw + 640) * 4, W * S * 4);
        f.CopyBackground(200, 139, 22, 18, 200, 116);
        ShellBackdrop.Edit(hd, iw, ih, (S, 640, S, 0), f.Edits);
        var want = Upscale(Clean(f));
        for (int y = 116 * S; y < 134 * S; y++)
            Assert.True(want.AsSpan(y * W * S * 4, W * S * 4).SequenceEqual(hd.AsSpan((y * iw + 640) * 4, W * S * 4)), $"row {y}");
    }
}
