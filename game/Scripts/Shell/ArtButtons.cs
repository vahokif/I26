using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// Buttons of the port's set among the art's own (the internet roster's SERVERS, the base game's address book's DONE / NEW /
/// DELETE / CANCEL), made from a button of the same art: its face, its label left out, resized to the new place; then the
/// art's own word where the art has it, cut out and centred; a word the art lacks lettered as the art letters its buttons
/// (<see cref="ShellFrame.Label"/>: Arial Bold, as tall as the art's label beside it, as wide for its letters and as heavy, in
/// its ink; vectors in the modern look, antialiased into the palette in the 1997 look). Drawn into the frame (not the
/// background), so they show over an upscaled background or popup too.
/// </summary>
public static class ArtButtons
{
    /// <summary>
    /// A label of the art: its ink's bounds, the ink a stroke's core is printed in (the 1997 look prints its pixels in it), and the
    /// ink its strokes show in on the whole, lighter (the modern look's vectors: the art's own letters show softened when scaled up).
    /// </summary>
    public sealed record ArtLabel(Rect2I Box, byte Ink, byte Soft);

    /// <summary>
    /// A button's label in the art: the pixels inside <paramref name="inside"/> clearly darker than its face (the median
    /// brightness less 25). Null for a blank button. (Given the button with its surroundings, the bounds are its border's.)
    /// </summary>
    public static ArtLabel? Label(Func<int, int, byte> px, byte[] pal, Rect2I inside)
    {
        float Lum(byte i) => 0.299f * pal[i * 3] + 0.587f * pal[i * 3 + 1] + 0.114f * pal[i * 3 + 2];
        var lums = new List<float>();
        for (int y = inside.Position.Y; y < inside.End.Y; y++)
            for (int x = inside.Position.X; x < inside.End.X; x++) lums.Add(Lum(px(x, y)));
        if (lums.Count == 0) return null;
        lums.Sort();
        float face = lums[lums.Count / 2], cut = face - 25;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        var dark = new List<(float L, byte V)>();
        for (int y = inside.Position.Y; y < inside.End.Y; y++)
            for (int x = inside.Position.X; x < inside.End.X; x++)
            {
                byte v = px(x, y);
                float l = Lum(v);
                if (l >= cut) continue;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                dark.Add((l, v));
            }
        if (x1 < 0) return null;
        // the core's ink: a fifth of the way up from the darkest (a stray darkest pixel would print it too heavy); on the whole, two fifths
        dark.Sort((a, b) => a.L.CompareTo(b.L));
        return new ArtLabel(new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1), dark[dark.Count / 5].V, dark[dark.Count * 2 / 5].V);
    }

    /// <summary>
    /// The face of the art's button <paramref name="src"/> (its border's bounds) at <paramref name="dest"/> (the same height):
    /// its ends <paramref name="edge"/> px wide as they are, the middle tiled from its plain columns between the left end and
    /// the label (which starts at <paramref name="labelX"/>), so no word and no streak of one column comes along.
    /// </summary>
    public static void Face(ShellFrame frame, Func<int, int, byte> px, Rect2I src, int labelX, Rect2I dest, int edge)
    {
        int sw = src.Size.X, dw = dest.Size.X, plain = Math.Max(1, labelX - 1 - (src.Position.X + edge));
        for (int ty = 0; ty < dest.Size.Y; ty++)
            for (int tx = 0; tx < dw; tx++)
            {
                int sx = tx < edge ? tx : tx >= dw - edge ? sw - (dw - tx) : edge + (tx - edge) % plain;
                frame.Fill(dest.Position.X + tx, dest.Position.Y + ty, 1, 1, px(src.Position.X + sx, src.Position.Y + ty));
            }
    }

    /// <summary>The art's own word (its label's box, one pixel round it for the faint edges) centred across a button at <paramref name="dest"/>, at the height it had.</summary>
    public static void Word(ShellFrame frame, Func<int, int, byte> px, Rect2I word, Rect2I dest)
    {
        var w = word.Grow(1);
        int x = dest.Position.X + (dest.Size.X - w.Size.X) / 2;
        for (int y = 0; y < w.Size.Y; y++)
            for (int c = 0; c < w.Size.X; c++)
                frame.Fill(x + c, w.Position.Y + y, 1, 1, px(w.Position.X + c, w.Position.Y + y));
    }

    /// <summary>
    /// A word the art lacks, centred across a button at <paramref name="dest"/>, lettered like the art's label <paramref name="like"/>
    /// (whose text is <paramref name="likeText"/>), in Arial Bold: in the 1997 look printed at the pixel size that prints
    /// <paramref name="likeText"/> closest to the art's (as tall and as wide), hinted, in its core's ink; in the modern look as tall
    /// as the art's label, as wide as the two words' proportions make it, in the ink its strokes show. Without Arial, the shell's
    /// label face (font 0x47), doubled for weight.
    /// </summary>
    public static void Letter(ShellFrame frame, ShellFont fallback, string text, ArtLabel like, string likeText, Rect2I dest)
    {
        if (ShellGlyphs.LabelAspect(text) is { } a && ShellGlyphs.LabelAspect(likeText) is { } b && b > 0)
        {
            // the art's ink box counts the faint antialiased row and column round its letters, which a vector's edge has not:
            // the vector's box is that much smaller (the 1997 look's print is centred in the box as it is)
            bool vector = frame.HdFonts is not null;
            // (half of it on each side: a pixel of height, from the top, and of width)
            int rim = vector && like.Box.Size.Y >= 8 ? 1 : 0;
            int h = like.Box.Size.Y - rim, w = Math.Max(1, (int)MathF.Round((like.Box.Size.X - rim) * a / b));
            var box = new Rect2I(dest.Position.X + (dest.Size.X - w) / 2, like.Box.Position.Y + rim, w, h);
            if (frame.Label(box, text, vector ? like.Soft : like.Ink, PixelSize(like, likeText))) return;
        }
        int tx = dest.Position.X + (dest.Size.X - fallback.Measure(text)) / 2, ty = dest.Position.Y + (dest.Size.Y - fallback.Height) / 2 + 1;
        frame.Text(fallback, tx, ty, text, ShellFrame.DefaultInk);
        frame.Text(fallback, tx + 1, ty, text, ShellFrame.DefaultInk);
    }

    private static readonly Dictionary<(string, Vector2I), int> Sizes = [];

    /// <summary>The pixel size at which Arial Bold prints <paramref name="likeText"/> closest to the art's label's ink box (its height counting double).</summary>
    private static int PixelSize(ArtLabel like, string likeText)
    {
        var want = like.Box.Size;
        if (Sizes.TryGetValue((likeText, want), out int s)) return s;
        int best = 0, off = int.MaxValue;
        for (int px = 6; px <= 40; px++)
        {
            if (ShellGlyphs.LabelPixels(likeText, px) is not { } r) continue;
            int d = 2 * Math.Abs(r.H - want.Y) + Math.Abs(r.W - want.X);
            if (d < off) { off = d; best = px; }
        }
        return Sizes[(likeText, want)] = best;
    }
}
