using I76.Formats.Shell;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The build-and-repair form draws the rear weapons turned half a turn (ShellFrame.BlitRotated). With an HD menu pack the
/// modern look draws a sprite's upscale in its place; a turned blit must be placed too (the box it covers, marked turned),
/// or the rear weapons stay the original's pixels stretched while the rest of the blueprint is drawn at the window's resolution.
/// </summary>
public class HdTurnedSpriteTests
{
    /// <summary>A sprite of w x h opaque pixels, each its own index, with the header's origin at (x0, y0).</summary>
    private static Sprite Make(int w, int h, int x0, int y0)
    {
        var head = new byte[0x18];
        BitConverter.GetBytes(x0).CopyTo(head, 8); BitConverter.GetBytes(y0).CopyTo(head, 12);
        BitConverter.GetBytes(x0 + w - 1).CopyTo(head, 16); BitConverter.GetBytes(y0 + h - 1).CopyTo(head, 20);
        var rle = new List<byte>(head);
        for (int y = 0; y < h; y++)
        {
            rle.Add((byte)(w << 1 | 1));
            for (int x = 0; x < w; x++) rle.Add((byte)(1 + y * w + x));
            rle.Add(0);
        }
        var s = Sprite.Decode(rle.ToArray());
        s.Bank = 0x35; s.Index = 4;
        return s;
    }

    private static ShellFrame Frame()
    {
        var f = new ShellFrame();
        f.LoadBackground(Pcx.Create(ShellFrame.W, ShellFrame.H, new byte[ShellFrame.W * ShellFrame.H], new byte[768]), black: false);
        return f;
    }

    [Fact]
    public void ATurnedBlitIsPlacedOverTheBoxItDraws()
    {
        var s = Make(5, 3, 2, 1);
        var f = Frame();
        f.HasHdSprite = _ => true;
        f.BlitRotated(s, 100, 200);
        var p = Assert.Single(f.Placed);
        Assert.Same(s, p.Sprite);
        Assert.True(p.Turned);
        // the drawn pixels' bounds are the placed box
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < ShellFrame.H; y++)
            for (int x = 0; x < ShellFrame.W; x++)
                if (f.Pixels[y * ShellFrame.W + x] != 0) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        Assert.Equal((x0, y0, x1 - x0 + 1, y1 - y0 + 1), (p.X, p.Y, s.Width, s.Height));
        // turned: the sprite's first pixel lands at the box's bottom-right corner
        Assert.Equal(1, f.Pixels[(p.Y + s.Height - 1) * ShellFrame.W + p.X + s.Width - 1]);
    }

    [Fact]
    public void AnUprightBlitIsNotTurnedAndARestoreTakesEitherBack()
    {
        var s = Make(4, 4, 0, 0);
        var f = Frame();
        f.HasHdSprite = _ => true;
        f.Blit(s, 10, 10);
        f.BlitRotated(s, 300, 300);
        Assert.Equal([false, true], f.Placed.Select(p => p.Turned).ToArray());
        f.Restore(290, 290, 20, 20);
        Assert.False(Assert.Single(f.Placed).Turned);
    }

    [Fact]
    public void WithoutAnUpscaleNothingIsPlaced()
    {
        var f = Frame();
        f.HasHdSprite = _ => false;
        f.BlitRotated(Make(3, 3, 0, 0), 50, 50);
        Assert.Empty(f.Placed);
        Assert.NotEqual(0, f.Pixels[48 * ShellFrame.W + 48]);
    }
}
