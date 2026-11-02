using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The shell's 640x480 8 bpp display: a clean copy of the background, a work copy that widgets draw into
/// and restore from the clean one, and the background's own palette. Sprites
/// and fonts are palette indices drawn into whatever palette the background
/// installed, as in the original. After a background load the first present
/// is black (the shell writes a black palette, flips, then the real one).
///
/// In the modern look the strings in the fonts <see cref="HdFonts"/> accepts
/// are not blitted: each glyph is kept as a record of its cell (the bitmap
/// glyph's advance by the font's height), its character and its ink, for
/// ShellTextLayer to draw at the window's resolution. Each cell's pixels are
/// marked as the glyph's, and whatever writes a pixel later (a restore, a
/// sprite, a fill, other text) takes it back, so a glyph lives as long as most
/// of its cell is left untouched — as long as the original's pixels would
/// have stayed on screen.
/// </summary>
public sealed class ShellFrame
{
    public const int W = 640, H = 480;
    private readonly byte[] _clean = new byte[W * H];
    private readonly byte[] _work = new byte[W * H];
    public byte[] Palette { get; private set; } = new byte[768];
    private bool _dirty = true, _black;
    /// <summary>The background record shown (0 before any, or when loaded from a picture alone).</summary>
    public int Record { get; private set; }
    /// <summary>On Nitro, the database's own number of the background shown (also for the Nitro-only ones, which have no base number); 0 on the base game.</summary>
    public int Native { get; private set; }

    /// <summary>Unpack, install the palette, decode into the clean buffer, everything dirty.</summary>
    public void LoadBackground(Pcx p, int record = 0, int native = 0, bool black = true)
    {
        Array.Clear(_clean);
        for (int y = 0; y < Math.Min(H, p.Height); y++)
            Array.Copy(p.Pixels, y * p.Width, _clean, y * W, Math.Min(W, p.Width));
        Array.Copy(_clean, _work, _clean.Length);
        Unplace(0, 0, W, H);
        Palette = p.Palette;
        Record = record;
        Native = native;
        ForgetText();
        _dirty = true;
        _black = black;
    }

    /// <summary>A DATABASE.MW2 background by record.</summary>
    public void LoadBackground(ShellDatabase db, int record, bool black = true) => LoadBackground(db.Background(record), record, db.NativeRecord(record), black);

    /// <summary>Puts the background back under a rectangle.</summary>
    public void Restore(int x, int y, int w, int h)
    {
        Clip(ref x, ref y, ref w, ref h);
        Unplace(x, y, x + w, y + h);
        for (int r = 0; r < h; r++)
        {
            Take((y + r) * W + x, w);
            Array.Copy(_clean, (y + r) * W + x, _work, (y + r) * W + x, w);
        }
        _dirty = true;
    }

    /// <summary>Copies a patch of the background onto another place of it (the port's: a printed check box onto a line that has none).</summary>
    public void CopyBackground(int sx, int sy, int w, int h, int dx, int dy)
    {
        for (int r = 0; r < h; r++)
            if (sy + r is >= 0 and < H && dy + r is >= 0 and < H && sx >= 0 && dx >= 0 && sx + w <= W && dx + w <= W)
                Array.Copy(_clean, (sy + r) * W + sx, _clean, (dy + r) * W + dx, w);
        Restore(dx, dy, w, h);
    }

    /// <summary>The frame's pixels as they stand (a screen's own layer over the frame compares them with what the screen drew, to keep off a pop-up).</summary>
    public ReadOnlySpan<byte> Pixels => _work;

    /// <summary>The whole work buffer back to the background.</summary>
    public void RestoreAll() { Array.Copy(_clean, _work, _clean.Length); Unplace(0, 0, W, H); ForgetText(); _dirty = true; }

    /// <summary>The sprite with its own header offset added (the garage's parts sit by it).</summary>
    public void BlitAt(Sprite? s, int x, int y) { if (s is not null) Blit(s, x + s.X0, y + s.Y0); }

    /// <summary>A draw with scale (−1, −1), the rear weapons: source pixel (sx, sy), header offset included, lands at (ax − sx, ay − sy).</summary>
    public void BlitRotated(Sprite? s, int ax, int ay)
    {
        if (s is null) return;
        for (int r = 0; r < s.Height; r++)
            for (int c = 0; c < s.Width; c++)
            {
                int i = r * s.Width + c, xx = ax - (s.X0 + c), yy = ay - (s.Y0 + r);
                if (!s.Opaque[i] || xx < 0 || xx >= W || yy < 0 || yy >= H) continue;
                Put(yy * W + xx, s.Pixels[i]);
            }
        _dirty = true;
    }

    // ---- sprites drawn at the window's resolution (the modern look; ShellSprites.cs) ----

    private readonly bool[] _hide = new bool[W * H];
    private readonly List<(Sprite Sprite, int X, int Y)> _placed = [];
    /// <summary>Whether a sprite has an upscale to show in its place (set in the modern look).</summary>
    public Func<Sprite, bool>? HasHdSprite { get; set; }
    /// <summary>The sprites with an upscale that are on the frame now, where they were blitted.</summary>
    public IReadOnlyList<(Sprite Sprite, int X, int Y)> Placed => _placed;
    /// <summary>Changes whenever <see cref="Placed"/> does.</summary>
    public int PlacedVersion { get; private set; }

    private void Unplace(int x0, int y0, int x1, int y1)
    {
        if (_placed.RemoveAll(p => p.X < x1 && p.X + p.Sprite.Width > x0 && p.Y < y1 && p.Y + p.Sprite.Height > y0) > 0) PlacedVersion++;
        for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++) Array.Clear(_hide, y * W + Math.Max(0, x0), Math.Max(0, Math.Min(W, x1) - Math.Max(0, x0)));
    }

    public void Blit(Sprite? s, int x, int y)
    {
        if (s is null) return;
        bool hd = HasHdSprite?.Invoke(s) == true;
        if (hd) { Unplace(x, y, x + s.Width, y + s.Height); _placed.Add((s, x, y)); PlacedVersion++; }
        for (int r = 0; r < s.Height; r++)
        {
            int yy = y + r;
            if (yy < 0 || yy >= H) continue;
            for (int c = 0; c < s.Width; c++)
            {
                int xx = x + c, i = r * s.Width + c;
                if (xx < 0 || xx >= W || !s.Opaque[i]) continue;
                Put(yy * W + xx, s.Pixels[i]);
                if (hd) _hide[yy * W + xx] = true;
            }
        }
        _dirty = true;
    }

    /// <summary>
    /// Draws a string in a bitmap font with its top-left at (x, y), each glyph
    /// pixel through <paramref name="remap"/> and skipped where that gives 0xFF
    /// (without a remap the font's key is the transparent one).
    /// A leading '~' centres the string on x. Returns the pen's end x.
    /// </summary>
    public int Text(ShellFont f, int x, int y, string s, byte[]? remap = null)
    {
        if (s.StartsWith('~')) { s = s[1..]; x -= f.Measure(s) / 2; }
        // the modern look: recorded for the text layer instead, in the colour the face's solid ink lands in
        if (HdFonts?.Invoke(f) == true && InkOf(f, remap) is >= 0 and var ink)
        {
            foreach (char ch in s)
            {
                if (f.Glyph(ch) is not { } g) continue;
                Claim(new HdGlyph { Font = f, Ch = ch, Box = new Rect2I(x, y, g.Width, f.Height), Ink = (byte)ink });
                x += g.Width;
            }
            return x;
        }
        foreach (char ch in s)
        {
            if (f.Glyph(ch) is not { } g) continue;
            for (int r = 0; r < f.Height; r++)
            {
                int yy = y + r;
                if (yy < 0 || yy >= H) continue;
                for (int c = 0; c < g.Width; c++)
                {
                    int xx = x + c;
                    byte v = g.Px[r * g.Width + c];
                    if (remap is not null) { v = remap[v]; if (v == 0xff) continue; }
                    else if (v == f.Key) continue;
                    if (xx < 0 || xx >= W) continue;
                    Put(yy * W + xx, v);
                }
            }
            x += g.Width;
        }
        _dirty = true;
        return x;
    }

    /// <summary>The display's default remap: identity, 0 transparent — glyphs draw in their own index (font 0x45's ink is 15).</summary>
    public static readonly byte[] DefaultInk = MakeDefault();

    private static byte[] MakeDefault()
    {
        var r = new byte[256];
        for (int i = 0; i < 256; i++) r[i] = (byte)i;
        r[0] = 0xff;
        return r;
    }

    /// <summary>The overlays' ink remap: identity, glyph ink 1 drawn as palette 200, 255 transparent.</summary>
    public static readonly byte[] OverlayInk = MakeInk(200);

    public static byte[] MakeInk(byte ink)
    {
        var r = new byte[256];
        for (int i = 0; i < 256; i++) r[i] = (byte)i;
        r[1] = ink;
        return r;
    }

    /// <summary>The palette index nearest a colour.</summary>
    public byte Nearest(Color c)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int dr = Palette[i * 3] - (int)(c.R * 255), dg = Palette[i * 3 + 1] - (int)(c.G * 255), db = Palette[i * 3 + 2] - (int)(c.B * 255);
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }

    public void Fill(int x, int y, int w, int h, byte index)
    {
        Clip(ref x, ref y, ref w, ref h);
        for (int r = 0; r < h; r++) { Take((y + r) * W + x, w); Array.Fill(_work, index, (y + r) * W + x, w); Array.Clear(_hide, (y + r) * W + x, w); }
        _dirty = true;
    }

    // ---- the modern look's text ----------------------------------------------------------------

    /// <summary>
    /// One glyph the text layer draws: a bitmap font's character in its cell,
    /// or (Caption) a whole string fitted to an ink box in the shell's
    /// handwriting. Ink is a palette index, looked up when it is drawn.
    /// </summary>
    public sealed class HdGlyph
    {
        public ShellFont? Font;
        public char Ch;
        public string? Caption;
        public Rect2I Box;
        public byte Ink;
        /// <summary>The cell's pixels on the frame, and how many are still the glyph's.</summary>
        public int Cells, Live;
        /// <summary>The part of the box still the glyph's: the bounds of the pixels it has left (a pop-up over half a word cuts its glyphs).</summary>
        public Rect2I Shown;
        internal int Id;
        /// <summary>Shown while most of its cell is left as it was.</summary>
        public bool Visible => Live * 2 > Cells;
    }

    /// <summary>Which fonts are drawn by the text layer (null: none, the 1997 look).</summary>
    public Func<ShellFont, bool>? HdFonts { get; set; }
    private readonly int[] _owner = new int[W * H];
    private readonly List<HdGlyph?> _glyphs = [];
    private readonly Stack<int> _free = new();
    /// <summary>Bumped whenever a glyph comes or goes, for the text layer to redraw.</summary>
    public int TextVersion { get; private set; }

    /// <summary>The glyphs on the frame now, each with the part of it left (<see cref="HdGlyph.Shown"/>).</summary>
    public IEnumerable<HdGlyph> Glyphs
    {
        get
        {
            foreach (var g in _glyphs)
            {
                if (g is not { Visible: true }) continue;
                g.Shown = g.Live == g.Cells ? g.Box : Owned(g);
                yield return g;
            }
        }
    }

    private Rect2I Owned(HdGlyph g)
    {
        var box = g.Box.Intersection(new Rect2I(0, 0, W, H));
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int y = box.Position.Y; y < box.End.Y; y++)
            for (int x = box.Position.X; x < box.End.X; x++)
                if (_owner[y * W + x] == g.Id + 1) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return x1 < x0 ? default : new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>
    /// The palette index a string's solid ink lands in: the face's top glyph
    /// value through the remap, down the ramp while the remap retires it (0xFF);
    /// without a remap the glyph's own value. −1 when nothing would show.
    /// </summary>
    public static int InkOf(ShellFont f, byte[]? remap)
    {
        int v = f.InkValue;
        if (v < 0) return -1;
        if (remap is null) return v;
        for (; v > 0; v--)
            if (remap[v] != 0xff) return remap[v];
        return -1;
    }

    /// <summary>A string set in the shell's handwriting and fitted to an ink box (the action names painted into record 16, when the upscale has none).</summary>
    public void Caption(Rect2I box, string text, byte ink) => Claim(new HdGlyph { Caption = text, Box = box, Ink = ink });

    private void Claim(HdGlyph g)
    {
        int x = g.Box.Position.X, y = g.Box.Position.Y, w = g.Box.Size.X, h = g.Box.Size.Y;
        Clip(ref x, ref y, ref w, ref h);
        if (w <= 0 || h <= 0) return;
        int id = _free.Count > 0 ? _free.Pop() : _glyphs.Count;
        if (id == _glyphs.Count) _glyphs.Add(null);
        _glyphs[id] = g;
        g.Id = id;
        g.Cells = g.Live = w * h;
        for (int r = 0; r < h; r++)
        {
            int at = (y + r) * W + x;
            Take(at, w);
            Array.Fill(_owner, id + 1, at, w);
        }
        TextVersion++;
    }

    private void Put(int i, byte v)
    {
        if (_owner[i] != 0) Take(i, 1);
        _work[i] = v;
        _hide[i] = false;
    }

    /// <summary>A run of pixels written over: whatever glyph had them loses them.</summary>
    private void Take(int at, int n)
    {
        bool taken = false;
        for (int i = at; i < at + n; i++)
        {
            int o = _owner[i];
            if (o == 0) continue;
            _owner[i] = 0;
            taken = true;
            if (--_glyphs[o - 1]!.Live == 0) { _glyphs[o - 1] = null; _free.Push(o - 1); }
        }
        if (taken) TextVersion++;
    }

    private void ForgetText()
    {
        if (_glyphs.Count == 0) return;
        Array.Clear(_owner);
        _glyphs.Clear();
        _free.Clear();
        TextVersion++;
    }

    private static void Clip(ref int x, ref int y, ref int w, ref int h)
    {
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        w = Math.Max(0, Math.Min(w, W - x));
        h = Math.Max(0, Math.Min(h, H - y));
    }

    private bool _transparent;

    /// <summary>
    /// Whether the background shows from beneath (an upscaled one is drawn
    /// under the frame): the pixels that are still the background's are
    /// presented transparent, keeping their colour for the filtering at the edges.
    /// </summary>
    public bool Transparent
    {
        get => _transparent;
        set { if (_transparent != value) { _transparent = value; _dirty = true; } }
    }

    private bool[]? _keep;

    /// <summary>Pixels presented opaque even where they are the background (where a borrowed upscale differs from it), or null.</summary>
    public bool[]? Keep
    {
        get => _keep;
        set { if (_keep != value) { _keep = value; _dirty = true; } }
    }

    /// <summary>Whether the last present was the black one after a background load.</summary>
    public bool ShowingBlack { get; private set; }

    /// <summary>The frame to show when something changed (a present), as RGBA; null when nothing did.</summary>
    public Image? Present()
    {
        if (!_dirty) return null;
        var rgba = new byte[W * H * 4];
        ShowingBlack = _black;
        if (_black)
        {
            // the black palette's frame; the real one shows on the next present
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            _black = false;
        }
        else
        {
            var pal = Palette;
            bool see = _transparent;
            var keep = _keep;
            for (int i = 0, o = 0; i < _work.Length; i++, o += 4)
            {
                int c = _work[i] * 3;
                rgba[o] = pal[c]; rgba[o + 1] = pal[c + 1]; rgba[o + 2] = pal[c + 2];
                rgba[o + 3] = _hide[i] || see && _work[i] == _clean[i] && keep?[i] != true ? (byte)0 : (byte)255;
            }
            _dirty = false;
        }
        return Image.CreateFromData(W, H, false, Image.Format.Rgba8, rgba);
    }
}
