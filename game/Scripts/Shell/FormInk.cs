using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>What a shape of the drawn forms is filled with: the forms' ink, a heading strip, the strip's darker foot, a button's orange.</summary>
public enum Paint { Ink, Strip, Shadow, Orange }

/// <summary>
/// The port's drawing of the Nitro Pack's form art (the host form, its GAME OPTIONS, GAME TYPE and WEAPON RESTRICTIONS pop-ups): one
/// description of each shape, put down as palette pixels in the 1997 look and as vectors at the window's resolution in the modern
/// look. The colours are the art's own, measured: the ink of its rules and letters, the grey-beige heading strips (a few tones mixed,
/// as the art's are) with their darker bottom row, and the buttons' orange.
/// </summary>
public static class FormInk
{
    public static readonly Color Ink = Color.Color8(57, 49, 24), Strip = Color.Color8(218, 189, 140), Shadow = Color.Color8(167, 135, 102),
        Orange = Color.Color8(237, 158, 72);

    // the tones each fill mixes in the 1997 look, by how often the art has them
    private static readonly (Color C, int Weight)[] StripTones =
        [(Color.Color8(222, 189, 140), 45), (Color.Color8(214, 186, 129), 20), (Color.Color8(207, 185, 146), 20), (Color.Color8(225, 198, 148), 15)];
    private static readonly (Color C, int Weight)[] ShadowTones = [(Color.Color8(165, 128, 99), 55), (Color.Color8(170, 144, 106), 45)];
    private static readonly (Color C, int Weight)[] OrangeTones =
        [(Color.Color8(237, 156, 74), 60), (Color.Color8(247, 165, 74), 13), (Color.Color8(237, 158, 55), 10), (Color.Color8(239, 165, 74), 10), (Color.Color8(222, 165, 66), 7)];

    public static Color Of(Paint p) => p switch { Paint.Strip => Strip, Paint.Shadow => Shadow, Paint.Orange => Orange, _ => Ink };

    /// <summary>A fill's tone at a pixel (the 1997 look): one of the art's tones for it, the same at the same place every time.</summary>
    public static Color ToneAt(Paint p, int x, int y)
    {
        var tones = p switch { Paint.Strip => StripTones, Paint.Shadow => ShadowTones, Paint.Orange => OrangeTones, _ => null };
        if (tones is null) return Ink;
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        int pick = (int)(h % 100);
        foreach (var (c, w) in tones) { if (pick < w) return c; pick -= w; }
        return tones[0].C;
    }

    private static readonly Dictionary<byte[], Dictionary<int, byte>> Near = new(ReferenceEqualityComparer.Instance);

    /// <summary>The palette index nearest a colour (remembered per palette).</summary>
    public static byte Nearest(byte[] pal, float r, float g, float b)
    {
        if (!Near.TryGetValue(pal, out var cache)) Near[pal] = cache = [];
        int ri = Math.Clamp((int)MathF.Round(r), 0, 255), gi = Math.Clamp((int)MathF.Round(g), 0, 255), bi = Math.Clamp((int)MathF.Round(b), 0, 255);
        int key = ri << 16 | gi << 8 | bi;
        if (cache.TryGetValue(key, out var hit)) return hit;
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int dr = pal[i * 3] - ri, dg = pal[i * 3 + 1] - gi, db = pal[i * 3 + 2] - bi, d = dr * dr + dg * dg + db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return cache[key] = (byte)best;
    }
}

/// <summary>A shape for the modern look's vectors, in the shell's 640x480 pixels: a filled rectangle, a filled polygon or a line through points.</summary>
public abstract record InkShape(Color C);
public sealed record InkRect(Rect2 R, Color C) : InkShape(C);
public sealed record InkPoly(Vector2[] P, Color C) : InkShape(C);
public sealed record InkLine(Vector2[] P, float W, bool Closed, Color C) : InkShape(C);

/// <summary>
/// A pen for <see cref="FormInk"/>'s shapes: in the 1997 look it puts palette pixels into a target (a background being made, or the
/// frame), the slanted edges antialiased into the palette's nearest colours as the art's are; in the modern look it adds vectors to a
/// list (<see cref="InkLayer"/> draws them). Coordinates are the shell's pixels; x0..x1 and y0..y1 are inclusive.
/// </summary>
public sealed class InkPen
{
    private readonly Func<int, int, byte>? _get;
    private readonly Action<int, int, byte>? _put;
    private readonly byte[]? _pal;
    private readonly List<InkShape>? _shapes;

    private InkPen(Func<int, int, byte>? get, Action<int, int, byte>? put, byte[]? pal, List<InkShape>? shapes) { _get = get; _put = put; _pal = pal; _shapes = shapes; }

    /// <summary>Into a 640x480 pixel array (a background being made).</summary>
    public static InkPen Pixels(byte[] px, byte[] pal) => new((x, y) => px[y * ShellFrame.W + x], (x, y, v) => px[y * ShellFrame.W + x] = v, pal, null);

    /// <summary>Into the frame's pixels.</summary>
    public static InkPen Into(ShellFrame f) => new((x, y) => f.Pixels[y * ShellFrame.W + x], (x, y, v) => f.Fill(x, y, 1, 1, v), f.Palette, null);

    /// <summary>As vectors, into <paramref name="shapes"/>.</summary>
    public static InkPen Vectors(List<InkShape> shapes) => new(null, null, null, shapes);

    public bool IsVector => _shapes is not null;

    /// <summary>A filled rectangle.</summary>
    public void Rect(int x, int y, int w, int h, Paint p)
    {
        if (w <= 0 || h <= 0) return;
        if (_shapes is not null) { _shapes.Add(new InkRect(new Rect2(x, y, w, h), FormInk.Of(p))); return; }
        for (int yy = Math.Max(0, y); yy < Math.Min(ShellFrame.H, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(ShellFrame.W, x + w); xx++)
            {
                var c = FormInk.ToneAt(p, xx, yy);
                _put!(xx, yy, FormInk.Nearest(_pal!, c.R8, c.G8, c.B8));
            }
    }

    /// <summary>A rectangle's outline <paramref name="t"/> pixels thick, inside x0..x1, y0..y1.</summary>
    public void Box(int x0, int y0, int x1, int y1, int t = 1)
    {
        Rect(x0, y0, x1 - x0 + 1, t, Paint.Ink);
        Rect(x0, y1 - t + 1, x1 - x0 + 1, t, Paint.Ink);
        Rect(x0, y0 + t, t, y1 - y0 + 1 - 2 * t, Paint.Ink);
        Rect(x1 - t + 1, y0 + t, t, y1 - y0 + 1 - 2 * t, Paint.Ink);
    }

    /// <summary>A button: its fill inside a border <paramref name="t"/> thick.</summary>
    public void Button(int x0, int y0, int x1, int y1, int t = 1)
    {
        Rect(x0 + t, y0 + t, x1 - x0 + 1 - 2 * t, y1 - y0 + 1 - 2 * t, Paint.Orange);
        Box(x0, y0, x1, y1, t);
    }

    /// <summary>A heading strip x0..x1 from y0, its rows to y1 and the darker foot on y1 + 1.</summary>
    public void Strip(int x0, int y0, int x1, int y1)
    {
        Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1, Paint.Strip);
        Rect(x0, y1 + 1, x1 - x0 + 1, 1, Paint.Shadow);
    }

    /// <summary>A filled polygon (its edges antialiased in the 1997 look).</summary>
    public void Poly(Vector2[] pts, Paint p)
    {
        if (_shapes is not null) { _shapes.Add(new InkPoly(pts, FormInk.Of(p))); return; }
        Cover(Bounds(pts, 0), (sx, sy) => Inside(pts, sx, sy), p);
    }

    /// <summary>A line <paramref name="w"/> pixels wide through points (closed back to the first).</summary>
    public void Stroke(Vector2[] pts, float w, bool closed = false, Paint p = Paint.Ink)
    {
        if (_shapes is not null) { _shapes.Add(new InkLine(pts, w, closed, FormInk.Of(p))); return; }
        int n = pts.Length, segs = closed ? n : n - 1;
        Cover(Bounds(pts, w), (sx, sy) =>
        {
            for (int i = 0; i < segs; i++) if (Dist(pts[i], pts[(i + 1) % n], sx, sy) <= w / 2) return true;
            return false;
        }, p);
    }

    private static Rect2I Bounds(Vector2[] pts, float grow)
    {
        float x0 = pts.Min(q => q.X) - grow, y0 = pts.Min(q => q.Y) - grow, x1 = pts.Max(q => q.X) + grow, y1 = pts.Max(q => q.Y) + grow;
        int ix = (int)MathF.Floor(x0), iy = (int)MathF.Floor(y0);
        return new Rect2I(ix, iy, (int)MathF.Ceiling(x1) - ix + 1, (int)MathF.Ceiling(y1) - iy + 1);
    }

    /// <summary>Each pixel of the box by how many of its 4x4 samples the shape covers, blended over what is there into the palette's nearest.</summary>
    private void Cover(Rect2I box, Func<float, float, bool> hit, Paint p)
    {
        for (int y = Math.Max(0, box.Position.Y); y < Math.Min(ShellFrame.H, box.End.Y); y++)
            for (int x = Math.Max(0, box.Position.X); x < Math.Min(ShellFrame.W, box.End.X); x++)
            {
                int n = 0;
                for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) if (hit(x + (i + 0.5f) / 4, y + (j + 0.5f) / 4)) n++;
                if (n == 0) continue;
                var c = FormInk.ToneAt(p, x, y);
                float a = n / 16f;
                if (a > 0.9f) { _put!(x, y, FormInk.Nearest(_pal!, c.R8, c.G8, c.B8)); continue; }
                int u = _get!(x, y) * 3;
                _put!(x, y, FormInk.Nearest(_pal!, _pal![u] + (c.R8 - _pal[u]) * a, _pal[u + 1] + (c.G8 - _pal[u + 1]) * a, _pal[u + 2] + (c.B8 - _pal[u + 2]) * a));
            }
    }

    private static bool Inside(Vector2[] p, float x, float y)
    {
        bool c = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            if ((p[i].Y > y) != (p[j].Y > y) && x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) c = !c;
        return c;
    }

    private static float Dist(Vector2 a, Vector2 b, float x, float y)
    {
        var ab = b - a; var ap = new Vector2(x, y) - a;
        float t = ab.LengthSquared() > 0 ? Math.Clamp(ap.Dot(ab) / ab.LengthSquared(), 0, 1) : 0;
        return (ap - ab * t).Length();
    }

    // ---- the forms' small controls, as the art draws them --------------------------------------------------------------

    /// <summary>A box to tick, <paramref name="size"/> pixels square (its 1-pixel outline).</summary>
    public void Check(int x, int y, int size) => Box(x, y, x + size - 1, y + size - 1);

    /// <summary>The art's − button: a 10x10 orange square in its outline, a bar 6 by 2 across the middle.</summary>
    public void Minus(int x, int y)
    {
        Button(x, y, x + 9, y + 9);
        Rect(x + 2, y + 4, 6, 2, Paint.Ink);
    }

    /// <summary>The art's + button: the − with a bar 2 by 6 down the middle.</summary>
    public void Plus(int x, int y)
    {
        Minus(x, y);
        Rect(x + 4, y + 2, 2, 6, Paint.Ink);
    }

    /// <summary>
    /// The art's list arrow (GAME TYPE, MAKE / MODEL, VARIANT): a box 7 wide and 14 tall with a triangle pointing right drawn in it,
    /// its tip four pixels out of the box (11 x 14 in all), orange inside.
    /// </summary>
    public void Arrow(int x, int y)
    {
        Rect(x + 1, y + 1, 5, 12, Paint.Orange);
        Vector2[] tri = [new(x + 3.5f, y + 2.5f), new(x + 10.5f, y + 7f), new(x + 3.5f, y + 11.5f)];
        Poly(tri, Paint.Orange);
        Rect(x, y, 7, 1, Paint.Ink); Rect(x, y + 13, 7, 1, Paint.Ink); Rect(x, y, 1, 14, Paint.Ink);
        Rect(x + 6, y, 1, 4, Paint.Ink); Rect(x + 6, y + 10, 1, 4, Paint.Ink);
        Rect(x + 3, y + 2, 1, 10, Paint.Ink);
        Stroke([new(x + 3.5f, y + 2.5f), new(x + 10.5f, y + 7f), new(x + 3.5f, y + 11.5f)], 1.15f);
    }

    /// <summary>The list's scroll triangle, 11 wide: up (its apex at the top, 10 tall) or down (9 tall), orange in an outline.</summary>
    public void Triangle(int x, int y, bool up)
    {
        Vector2[] t = up ? [new(x + 5.5f, y + 0.6f), new(x + 10.6f, y + 9.5f), new(x + 0.4f, y + 9.5f)]
                         : [new(x + 0.4f, y + 0.5f), new(x + 10.6f, y + 0.5f), new(x + 5.5f, y + 8.6f)];
        Poly(t, Paint.Orange);
        Stroke(t, 1.1f, closed: true);
    }
}

/// <summary>
/// The modern look's vectors of a drawn form or pop-up, over the frame (and under the text layer's words), scaled with the frame. Nothing
/// is drawn where <see cref="Hole"/> says a pop-up over it is (its rectangles are cut round it, its other shapes left out there).
/// </summary>
public partial class InkLayer : Control
{
    public List<InkShape> Shapes { get; set; } = [];
    /// <summary>Whether the layer is on show (a form's under the notepad overlays is not).</summary>
    public Func<bool>? Shown { get; init; }
    /// <summary>Where a pop-up over this layer is, in the frame's pixels: nothing of this layer shows there.</summary>
    public Func<Rect2I?>? Hole { get; init; }
    private float _s;
    private Vector2 _off;
    private Vector2 T(Vector2 shell) => _off + shell * _s;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        Visible = Shown?.Invoke() ?? true;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        _s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        _off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * _s) / 2;
        Rect2? hole = Hole?.Invoke() is { } h ? new Rect2(h.Position, h.Size) : null;
        foreach (var s in Shapes)
            switch (s)
            {
                case InkRect r:
                    foreach (var part in Cut(r.R, hole)) DrawRect(new Rect2(T(part.Position), part.Size * _s), r.C);
                    break;
                case InkPoly p when !Hit(p.P, 0, hole):
                    DrawColoredPolygon([.. p.P.Select(T)], p.C);
                    break;
                case InkLine l when !Hit(l.P, l.W, hole):
                    var pts = l.Closed ? [.. l.P, l.P[0]] : l.P;
                    DrawPolyline([.. pts.Select(T)], l.C, Math.Max(1f, l.W * _s), true);
                    break;
            }
    }

    private static bool Hit(Vector2[] pts, float grow, Rect2? hole)
    {
        if (hole is not { } h) return false;
        var b = new Rect2(pts[0], Vector2.Zero);
        foreach (var q in pts) b = b.Expand(q);
        return b.Grow(grow).Intersects(h);
    }

    /// <summary>A rectangle less the hole: itself, or up to four pieces round it.</summary>
    private static IEnumerable<Rect2> Cut(Rect2 r, Rect2? hole)
    {
        if (hole is not { } h || !r.Intersects(h)) { yield return r; yield break; }
        var i = r.Intersection(h);
        if (i.Position.Y > r.Position.Y) yield return new Rect2(r.Position.X, r.Position.Y, r.Size.X, i.Position.Y - r.Position.Y);
        if (i.End.Y < r.End.Y) yield return new Rect2(r.Position.X, i.End.Y, r.Size.X, r.End.Y - i.End.Y);
        if (i.Position.X > r.Position.X) yield return new Rect2(r.Position.X, i.Position.Y, i.Position.X - r.Position.X, i.Size.Y);
        if (i.End.X < r.End.X) yield return new Rect2(i.End.X, i.Position.Y, r.End.X - i.End.X, i.Size.Y);
    }
}

/// <summary>
/// The drawn forms' printed words, in the art's lettering (bold Arial-like caps; <see cref="ShellFrame.Label"/>): fitted to the box the
/// art's own word fills (measured), or set at a size from a pen position. In the 1997 look they are blended into the frame's palette;
/// in the modern look they go to the text layer. Without Arial, the shell's label face (font 0x47) struck twice.
/// </summary>
public static class FormLetters
{
    /// <summary>Words fitted to the ink box <paramref name="box"/> (centred in it where the face's proportions leave room).</summary>
    public static void Fit(ShellFrame f, ShellFont fallback, Rect2I box, string text)
    {
        byte ink = f.Nearest(FormInk.Ink);
        if (f.HdFonts is not null)
        {
            if (f.Label(box, text, ink, 0)) return;
        }
        else if (Raster(text, box.Size.Y, box.Size.X) is { } r)
        {
            Blend(f, r, box.Position.X + (box.Size.X - r.W) / 2, box.Position.Y + (box.Size.Y - r.H) / 2, FormInk.Ink);
            return;
        }
        Fallback(f, fallback, box.Position.X + (box.Size.X - fallback.Measure(text)) / 2, box.Position.Y + (box.Size.Y - fallback.Height) / 2 + 1, text);
    }

    /// <summary>Words from a pen at <paramref name="x"/>, their capitals' tops at <paramref name="capTop"/>, the capitals <paramref name="cap"/> pixels tall.</summary>
    public static int At(ShellFrame f, ShellFont fallback, int x, int capTop, int cap, string text)
    {
        if (ShellGlyphs.LabelPixels("H", 6) is null) { Fallback(f, fallback, x, capTop - 2, text); return x + fallback.Measure(text); }
        int px = PxFor(cap);
        // the ink box of the words at that size; its top is the capitals' (or an ascender's, as tall)
        if (ShellGlyphs.LabelPixels(text, px) is not { } r) return x;
        var box = new Rect2I(x, capTop, r.W, r.H);
        if (f.HdFonts is not null) f.Label(box, text, f.Nearest(FormInk.Ink), 0);
        else Blend(f, r, x, capTop, FormInk.Ink);
        return x + r.W;
    }

    /// <summary>The width words set from a pen would take (<see cref="At"/>).</summary>
    public static int Width(ShellFont fallback, int cap, string text) =>
        ShellGlyphs.LabelPixels(text, PxFor(cap)) is { } r ? r.W : fallback.Measure(text);

    private static readonly Dictionary<int, int> CapPx = [];

    /// <summary>The pixel size at which Arial Bold's capitals print <paramref name="cap"/> pixels tall.</summary>
    private static int PxFor(int cap)
    {
        if (CapPx.TryGetValue(cap, out int px)) return px;
        int best = cap, off = int.MaxValue;
        for (int p = 6; p <= 48; p++)
            if (ShellGlyphs.LabelPixels("HEN", p) is { } r && Math.Abs(r.H - cap) < off) { off = Math.Abs(r.H - cap); best = p; }
        return CapPx[cap] = best;
    }

    /// <summary>
    /// Arial Bold's coverage for the words as tall as <paramref name="h"/> (the size that prints them closest, hinted, as the art's were
    /// printed), squeezed or stretched across to <paramref name="w"/> where the art's lettering is narrower or wider than the face's.
    /// </summary>
    private static (byte[] A, int W, int H)? Raster(string text, int h, int w)
    {
        (byte[] A, int W, int H)? best = null;
        int off = int.MaxValue;
        for (int px = 6; px <= 48; px++)
        {
            if (ShellGlyphs.LabelPixels(text, px) is not { } r) return null;
            int d = 3 * Math.Abs(r.H - h) + Math.Abs(r.W - w);
            if (d < off) { off = d; best = r; }
            if (r.H > h + 2) break;
        }
        if (best is not { } b || Math.Abs(b.W - w) <= 2) return best;
        // across only: each target column the average of the source columns it covers
        var a = new byte[w * b.H];
        float k = b.W / (float)w;
        for (int y = 0; y < b.H; y++)
            for (int x = 0; x < w; x++)
            {
                float s0 = x * k, s1 = (x + 1) * k, sum = 0;
                for (int sx = (int)s0; sx < Math.Min(b.W, (int)MathF.Ceiling(s1)); sx++)
                    sum += b.A[y * b.W + sx] * (Math.Min(s1, sx + 1) - Math.Max(s0, sx));
                a[y * w + x] = (byte)Math.Clamp(sum / k, 0, 255);
            }
        return (a, w, b.H);
    }

    /// <summary>Coverage blended over the frame's pixels into the palette's nearest colours.</summary>
    private static void Blend(ShellFrame f, (byte[] A, int W, int H) r, int x0, int y0, Color ink)
    {
        var pal = f.Palette;
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                int a = r.A[y * r.W + x], fx = x0 + x, fy = y0 + y;
                if (a < 16 || fx < 0 || fx >= ShellFrame.W || fy < 0 || fy >= ShellFrame.H) continue;
                int u = f.Pixels[fy * ShellFrame.W + fx] * 3;
                float t = a / 255f;
                f.Fill(fx, fy, 1, 1, FormInk.Nearest(pal, pal[u] + (ink.R8 - pal[u]) * t, pal[u + 1] + (ink.G8 - pal[u + 1]) * t, pal[u + 2] + (ink.B8 - pal[u + 2]) * t));
            }
    }

    private static void Fallback(ShellFrame f, ShellFont font, int x, int y, string text)
    {
        f.Text(font, x, y, text, ShellFrame.DefaultInk);
        f.Text(font, x + 1, y, text, ShellFrame.DefaultInk);
    }
}
