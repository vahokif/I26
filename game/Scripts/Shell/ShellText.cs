using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The shell's text at the window's resolution (the modern look). The shell
/// has no vector text: its fonts are glyph bitmaps (DATABASE.MW2 records
/// 65–71, 10, 12 or 15 px high), and records 66, 68, 69 and 70 are the game's
/// own Lee_____.ttf, pre-rendered; 71 is an upright sans like Arial. So each
/// glyph's shape is taken from the TTF and its place from the bitmap: the
/// TTF's ink, rendered large and trimmed, is fitted into the bitmap glyph's
/// inked box, inside a cell the bitmap's advance wide and the font's height
/// high, and the pen steps by the bitmap's advances — the layout is the
/// original's to the pixel, whatever the TTF's own metrics (as the original does).
/// 65 and 67 are left to the shell's pixels.
/// </summary>
public static class ShellGlyphs
{
    /// <summary>Atlas texels per shell pixel.</summary>
    public const int Scale = 8;
    private const int Pad = Scale, RenderSize = 200, AtlasWidth = 4096;

    /// <summary>A font record's glyphs, white with their coverage in alpha, and each character's cell in the atlas.</summary>
    public sealed class Atlas
    {
        public required ImageTexture Texture { get; init; }
        public required Dictionary<char, Rect2> Cells { get; init; }
    }

    private static Font? _lee, _arial;
    private static string? _leePath;
    private static readonly Dictionary<int, Atlas?> Atlases = [];
    private static readonly Dictionary<(string, int, int), ImageTexture?> Captions = [];

    /// <summary>The faces, from the install's Lee_____.ttf and the system's Arial (or a metric twin).</summary>
    public static void Load(string? leePath)
    {
        if (_leePath == leePath && (_lee is not null || leePath is null)) return;
        _leePath = leePath;
        Atlases.Clear();
        Captions.Clear();
        _lee = null;
        if (leePath is not null)
        {
            var lee = new FontFile { Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.None, SubpixelPositioning = TextServer.SubpixelPositioning.Disabled };
            if (lee.LoadDynamicFont(leePath) == Error.Ok) _lee = lee;
        }
        _arial ??= HdAssets.Arial(false, TextServer.FontAntialiasing.Gray, TextServer.Hinting.None);
    }

    /// <summary>The face a font record is redrawn from, or null to leave it as pixels.</summary>
    public static Font? Face(int record) => record switch
    {
        66 or 68 or 69 or 70 => _lee,
        71 => _arial,
        _ => null,
    };

    /// <summary>The record's atlas, built on first use.</summary>
    public static Atlas? For(ShellFont f)
    {
        if (Atlases.TryGetValue(f.Record, out var a)) return a;
        return Atlases[f.Record] = Face(f.Record) is { } face ? Build(f, face) : null;
    }

    /// <summary>
    /// The atlas: per character a cell of advance × height at <see cref="Scale"/>,
    /// the TTF's glyph stretched into the bitmap's ink box within it; a
    /// character the face lacks keeps the bitmap glyph, enlarged.
    /// </summary>
    private static Atlas Build(ShellFont f, Font face)
    {
        int ch = f.Height * Scale;
        var cells = new Dictionary<char, Rect2>();
        var placed = new List<(char C, int X, int Y)>();
        int x = Pad, y = Pad, rowH = ch + 2 * Pad;
        for (int c = 33; c < Math.Min(256, f.Count); c++)
        {
            if (f.Glyph((char)c) is not { } g) continue;
            int cw = g.Width * Scale;
            if (x + cw + Pad > AtlasWidth) { x = Pad; y += rowH; }
            placed.Add(((char)c, x, y));
            cells[(char)c] = new Rect2(x, y, cw, ch);
            x += cw + 2 * Pad;
        }
        int w = AtlasWidth, h = y + rowH;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4) rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
        foreach (var (c, cx, cy) in placed)
        {
            var (gw, px) = f.Glyph(c)!.Value;
            if (InkBox(px, gw, f.Height, f.Key) is not { } box) continue;
            int tw = box.Size.X * Scale, th = box.Size.Y * Scale;
            byte[]? cov = Circled.TryGetValue((f.Record, c), out var letter) ? Ringed(face, letter, tw, th)
                : face.HasChar(c) && Raster(face, c.ToString()) is { } r ? Fit(r, tw, th) : null;
            if (cov is not null)
            {
                for (int yy = 0; yy < th; yy++)
                    for (int xx = 0; xx < tw; xx++)
                        rgba[((cy + box.Position.Y * Scale + yy) * w + cx + box.Position.X * Scale + xx) * 4 + 3] = cov[yy * tw + xx];
                continue;
            }
            // no such glyph in the face: the bitmap's own, its ramp as coverage
            int ink = Math.Max(1, f.InkValue);
            for (int yy = 0; yy < f.Height * Scale; yy++)
                for (int xx = 0; xx < gw * Scale; xx++)
                {
                    byte v = px[yy / Scale * gw + xx / Scale];
                    if (v == f.Key) continue;
                    rgba[((cy + yy) * w + cx + xx) * 4 + 3] = (byte)Math.Clamp(255 * v / ink, 0, 255);
                }
        }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        img.GenerateMipmaps();
        return new Atlas { Texture = ImageTexture.CreateFromImage(img), Cells = cells };
    }

    /// <summary>Record 69's braces are the garage's part legends, a letter in a ring: (C) in the car, (V) the van, (R) in repair.</summary>
    private static readonly Dictionary<(int, char), char> Circled = new() { [(69, '{')] = 'C', [(69, '|')] = 'V', [(69, '}')] = 'R' };

    /// <summary>The face's O as the ring over the whole ink box, its letter inside where the bitmap has it, a little smaller so that it clears the ring (9/26 in from the left, 7/26 from the right, 5/22 from top and bottom).</summary>
    private static byte[]? Ringed(Font face, char letter, int tw, int th)
    {
        if (Raster(face, "O") is not { } ring || Raster(face, letter.ToString()) is not { } l) return null;
        var cov = Fit(ring, tw, th);
        int lx = tw * 9 / 26, ly = th * 5 / 22, lw = tw - lx - tw * 7 / 26, lh = th - 2 * ly;
        var inner = Fit(l, lw, lh);
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            {
                int o = (ly + y) * tw + lx + x;
                cov[o] = Math.Max(cov[o], inner[y * lw + x]);
            }
        return cov;
    }

    /// <summary>A string fitted to a box of w × h shell pixels, in Lee (the action names of record 16).</summary>
    public static ImageTexture? Caption(string text, int w, int h)
    {
        if (Captions.TryGetValue((text, w, h), out var t)) return t;
        ImageTexture? tex = null;
        if (_lee is not null && Raster(_lee, text) is { } r)
        {
            int tw = w * Scale, th = h * Scale;
            var cov = Fit(r, tw, th);
            var rgba = new byte[tw * th * 4];
            for (int i = 0; i < cov.Length; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = cov[i]; }
            var img = Image.CreateFromData(tw, th, false, Image.Format.Rgba8, rgba);
            img.GenerateMipmaps();
            tex = ImageTexture.CreateFromImage(img);
        }
        return Captions[(text, w, h)] = tex;
    }

    /// <summary>The bitmap glyph's inked pixels' bounds (non-key), or null when blank.</summary>
    private static Rect2I? InkBox(byte[] px, int w, int h, int key)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * w + x] != key) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return x1 < 0 ? null : new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Coverage (w × h, row-major) stretched to exactly tw × th.</summary>
    private static byte[] Fit((byte[] A, int W, int H) r, int tw, int th)
    {
        var img = Image.CreateFromData(r.W, r.H, false, Image.Format.L8, r.A);
        img.Resize(Math.Max(1, tw), Math.Max(1, th), Image.Interpolation.Lanczos);
        return img.GetData();
    }

    /// <summary>
    /// The string as the face draws it at <see cref="RenderSize"/> px, as
    /// coverage trimmed to its ink: the shaped glyphs' bitmaps taken from the
    /// font's own cache. Null without ink, or when a character is missing.
    /// </summary>
    private static (byte[] A, int W, int H)? Raster(Font face, string s)
    {
        var ts = TextServerManager.GetPrimaryInterface();
        var line = new TextLine();
        if (!line.AddString(s, face, RenderSize)) return null;
        var parts = new List<(byte[] Data, int Stride, int Bpp, Rect2I Src, Vector2I At)>();
        var pages = new Dictionary<(Rid, long), (byte[] Data, int W, int Bpp)>();
        float pen = 0;
        foreach (Godot.Collections.Dictionary g in ts.ShapedTextGetGlyphs(line.GetRid()))
        {
            int flags = g["flags"].AsInt32();
            if ((flags & (int)TextServer.GraphemeFlag.Valid) == 0 && (flags & (int)TextServer.GraphemeFlag.Space) == 0) return null;
            var font = g["font_rid"].AsRid();
            long index = g["index"].AsInt64();
            var size = new Vector2I(g["font_size"].AsInt32(), 0);
            var off = g["offset"].AsVector2();
            if (font.IsValid && (flags & (int)TextServer.GraphemeFlag.Space) == 0)
            {
                long page = ts.FontGetGlyphTextureIdx(font, size, index);
                if (page >= 0)
                {
                    if (!pages.TryGetValue((font, page), out var p))
                    {
                        var img = ts.FontGetTextureImage(font, size, page);
                        if (img.IsCompressed()) img.Decompress();
                        int bpp = img.GetFormat() switch { Image.Format.L8 or Image.Format.R8 => 1, Image.Format.La8 or Image.Format.Rg8 => 2, Image.Format.Rgba8 => 4, _ => 0 };
                        if (bpp == 0) { img.Convert(Image.Format.Rgba8); bpp = 4; }
                        pages[(font, page)] = p = (img.GetData(), img.GetWidth(), bpp);
                    }
                    var uv = ts.FontGetGlyphUVRect(font, size, index);
                    var at = ts.FontGetGlyphOffset(font, size, index);
                    parts.Add((p.Data, p.W, p.Bpp, new Rect2I((Vector2I)uv.Position.Round(), (Vector2I)uv.Size.Round()),
                        (Vector2I)(new Vector2(pen, 0) + off + at).Round()));
                }
            }
            pen += g["advance"].AsSingle();
        }
        if (parts.Count == 0) return null;
        // the coverage of each glyph laid down at its place (max where two overlap), then trimmed
        int minX = parts.Min(q => q.At.X), minY = parts.Min(q => q.At.Y);
        int w = parts.Max(q => q.At.X + q.Src.Size.X) - minX, h = parts.Max(q => q.At.Y + q.Src.Size.Y) - minY;
        if (w <= 0 || h <= 0) return null;
        var a = new byte[w * h];
        foreach (var (data, stride, bpp, src, at) in parts)
            for (int y = 0; y < src.Size.Y; y++)
                for (int x = 0; x < src.Size.X; x++)
                {
                    int i = ((src.Position.Y + y) * stride + src.Position.X + x) * bpp + bpp - 1;
                    if (i < 0 || i >= data.Length) continue;
                    int o = (at.Y - minY + y) * w + at.X - minX + x;
                    if (data[i] > a[o]) a[o] = data[i];
                }
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a[y * w + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return null;
        int tw = x1 - x0 + 1, th = y1 - y0 + 1;
        var t = new byte[tw * th];
        for (int y = 0; y < th; y++) Array.Copy(a, (y0 + y) * w + x0, t, y * tw, tw);
        return (t, tw, th);
    }
}

/// <summary>
/// Draws the frame's recorded glyphs (ShellFrame.Glyphs) over it, mapped as
/// the frame is (640x480 kept 4:3 and centred): each from its font's atlas
/// into its cell, or a caption into its box, in its ink's colour in the
/// frame's palette now.
/// </summary>
public partial class ShellTextLayer : Control
{
    public required ShellFrame Frame { get; init; }
    private int _version = -1;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    public override void _Process(double delta)
    {
        if (Frame.TextVersion == _version) return;
        _version = Frame.TextVersion;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
        var pal = Frame.Palette;
        foreach (var g in Frame.Glyphs)
        {
            // the part of the box left, and the same part of the glyph's texels
            var box = new Rect2(g.Box.Position, g.Box.Size);
            var part = new Rect2(g.Shown.Position, g.Shown.Size);
            var dest = new Rect2(off + part.Position * s, part.Size * s);
            var ink = Color.Color8(pal[g.Ink * 3], pal[g.Ink * 3 + 1], pal[g.Ink * 3 + 2]);
            Rect2 Sub(Rect2 src)
            {
                var k = src.Size / box.Size;
                return new Rect2(src.Position + (part.Position - box.Position) * k, part.Size * k);
            }
            if (g.Caption is { } text)
            {
                if (ShellGlyphs.Caption(text, g.Box.Size.X, g.Box.Size.Y) is { } t) DrawTextureRectRegion(t, dest, Sub(new Rect2(Vector2.Zero, t.GetSize())), ink);
            }
            else if (g.Font is { } f && ShellGlyphs.For(f) is { } atlas && atlas.Cells.TryGetValue(g.Ch, out var src))
                DrawTextureRectRegion(atlas.Texture, dest, Sub(src), ink);
        }
    }
}
