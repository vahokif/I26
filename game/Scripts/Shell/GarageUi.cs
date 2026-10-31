using Godot;

namespace I76.Game.Shell;

/// <summary>
/// The garage's notepad pieces drawn from scratch at the window's resolution (the modern look; the 1997 look keeps the form's bitmaps): the
/// ochre paper (a shader: fibres, grain and faint coffee stains in the form's colours), ruled rows, header bars, the orange bevelled
/// buttons, the row tabs, scroll triangles, the red pencil and the check that marks the choice, and the text, set straight from the TrueType
/// faces at one size per style with the font's own advances, kerning and a common baseline. Everything is placed in the 640x480 shell's units
/// (<see cref="GarageUiLayer"/> maps them to the window, 4:3 centred as the shell's frame is), so a screen's layout is kept as it was.
/// A screen sets <see cref="GarageUiLayer.Painter"/> and calls <see cref="GarageUiLayer.Redraw"/>; the painter calls this helper's pieces.
/// The colours are sampled from the 2D Chassis Configuration Form (its paper, ink, buttons and pencil).
/// </summary>
public static class GarageInk
{
    public static readonly Color Paper = new(239 / 255f, 189 / 255f, 90 / 255f);
    public static readonly Color Ink = new(48 / 255f, 37 / 255f, 17 / 255f);
    public static readonly Color Rule = new(120 / 255f, 98 / 255f, 66 / 255f, 0.9f);
    public static readonly Color Bar = new(228 / 255f, 218 / 255f, 196 / 255f, 0.5f);
    public static readonly Color Red = new(150 / 255f, 36 / 255f, 22 / 255f);
    public static readonly Color Orange = new(237 / 255f, 158 / 255f, 74 / 255f);
    public static readonly Color OrangeHi = new(250 / 255f, 176 / 255f, 92 / 255f);
    public static readonly Color OrangeLo = new(190 / 255f, 124 / 255f, 56 / 255f);
    public static readonly Color OrangeEdge = new(92 / 255f, 58 / 255f, 22 / 255f);
    public static readonly Color Label = new(86 / 255f, 66 / 255f, 38 / 255f);
    public static readonly Color Green = new(150 / 255f, 176 / 255f, 78 / 255f, 0.88f);
    public static readonly Color Shadow = new(0, 0, 0, 0.92f);
}

/// <summary>The faces: Lee_____.ttf (the form's handwriting; its lowercase are its small capitals), and the sans the HD menu pack carries (Arial) or the system's.</summary>
public enum GFace { Hand, Sans, SansBold }

/// <summary>A text style: a face and a size in shell units (one size per style, whatever the glyphs).</summary>
public readonly record struct GText(GFace Face, float Size)
{
    public static readonly GText Label = new(GFace.Sans, 12.5f);
    public static readonly GText Note = new(GFace.Sans, 12f);
    public static readonly GText Value = new(GFace.Hand, 15f);
    public static readonly GText Entry = new(GFace.Hand, 15f);
    public static readonly GText Name = new(GFace.Hand, 16.5f);
    public static readonly GText Heading = new(GFace.SansBold, 12.5f);
    public static readonly GText ButtonBig = new(GFace.SansBold, 14.5f);
    public static readonly GText ButtonSmall = new(GFace.SansBold, 11.5f);
}

public static class GarageFonts
{
    private static Font? _hand, _sans, _bold;
    private static string? _handPath;
    private static bool _loaded;

    public static void Load(string? leePath)
    {
        if (_loaded && _handPath == leePath) return;
        _loaded = true;
        _handPath = leePath;
        _hand = null;
        if (leePath is not null)
        {
            var lee = new FontFile { Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.None, SubpixelPositioning = TextServer.SubpixelPositioning.Auto };
            if (lee.LoadDynamicFont(leePath) == Error.Ok) _hand = lee;
        }
        _sans = HdAssets.Arial(false, TextServer.FontAntialiasing.Gray, TextServer.Hinting.None);
        _bold = HdAssets.Arial(true, TextServer.FontAntialiasing.Gray, TextServer.Hinting.None);
        _hand ??= _sans;
    }

    public static Font? Of(GFace f) => f switch { GFace.Hand => _hand, GFace.Sans => _sans, _ => _bold };
}

/// <summary>One painting of the garage's overlay: the shell-unit to window mapping and the drawing pieces.</summary>
public sealed class GarageUi(CanvasItem canvas, float scale, Vector2 origin)
{
    public float S { get; } = scale;
    public Vector2 Origin { get; } = origin;
    /// <summary>The paper's rectangles (window pixels), painted behind everything by the paper shader.</summary>
    public List<Rect2> PaperRects { get; } = [];

    // ---- geometry ----

    private Vector2 P(float x, float y) => (Origin + new Vector2(x, y) * S).Round();
    private Rect2 Px(Rect2 r) { var a = P(r.Position.X, r.Position.Y); var b = P(r.End.X, r.End.Y); return new Rect2(a, b - a); }
    /// <summary>A line's width in window pixels: whole pixels, at least one.</summary>
    private float Lw(float units) => Mathf.Max(1f, Mathf.Round(units * S));

    // ---- primitives ----

    public void Fill(Rect2 r, Color c) => canvas.DrawRect(Px(r), c);

    /// <summary>A rectangle's outline, drawn inside it.</summary>
    public void Frame(Rect2 r, Color c, float units = 1f)
    {
        var p = Px(r);
        float w = Lw(units);
        canvas.DrawRect(new Rect2(p.Position, new Vector2(p.Size.X, w)), c);
        canvas.DrawRect(new Rect2(p.Position.X, p.End.Y - w, p.Size.X, w), c);
        canvas.DrawRect(new Rect2(p.Position, new Vector2(w, p.Size.Y)), c);
        canvas.DrawRect(new Rect2(p.End.X - w, p.Position.Y, w, p.Size.Y), c);
    }

    public void HLine(float x0, float x1, float y, Color c, float units = 1f) => Fill(new Rect2(x0, y, x1 - x0, 0), c, units);
    public void VLine(float x, float y0, float y1, Color c, float units = 1f)
    {
        var a = P(x, y0); var b = P(x, y1);
        float w = Lw(units);
        canvas.DrawRect(new Rect2(a.X, a.Y, w, b.Y - a.Y), c);
    }

    private void Fill(Rect2 r, Color c, float thickUnits)
    {
        var a = P(r.Position.X, r.Position.Y); var b = P(r.End.X, r.End.Y);
        canvas.DrawRect(new Rect2(a.X, a.Y, b.X - a.X, Lw(thickUnits)), c);
    }

    // ---- text ----

    private static int PxSize(GText s, float scale) => Mathf.Max(6, Mathf.RoundToInt(s.Size * scale));

    /// <summary>The width (shell units) of a string in a style, from the font's advances and kerning.</summary>
    public static float Measure(GText s, string text)
    {
        if (GarageFonts.Of(s.Face) is not { } f || text.Length == 0) return 0;
        const int Probe = 256;
        return f.GetStringSize(text, HorizontalAlignment.Left, -1, Probe).X * s.Size / Probe;
    }

    /// <summary>The string with the end cut and ".." added until it fits the width.</summary>
    public static string Fit(GText s, string text, float width)
    {
        if (Measure(s, text) <= width) return text;
        while (text.Length > 1 && Measure(s, text + "..") > width) text = text[..^1];
        return text + "..";
    }

    /// <summary>The lines of a text broken to a width (words kept whole).</summary>
    public static List<string> Wrap(GText s, string text, float width)
    {
        var lines = new List<string>();
        string line = "";
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Length == 0 ? w : line + " " + w;
            if (Measure(s, t) <= width || line.Length == 0) line = t;
            else { lines.Add(line); line = w; }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>Text with its baseline at y (shell units) from x; one size per style, so every line of a style sits and measures alike.</summary>
    public void Text(GText s, string text, float x, float baseline, Color? colour = null)
    {
        if (text.Length == 0 || GarageFonts.Of(s.Face) is not { } f) return;
        var at = P(x, baseline);
        f.DrawString(canvas.GetCanvasItem(), at, text, HorizontalAlignment.Left, -1, PxSize(s, S), colour ?? GarageInk.Ink);
    }

    public void TextCentred(GText s, string text, Rect2 box, float baseline, Color? colour = null)
        => Text(s, text, box.Position.X + (box.Size.X - Measure(s, text)) / 2, baseline, colour);

    // ---- paper and panels ----

    /// <summary>A sheet of the notepad's paper with the form's hard shadow and dark outline.</summary>
    public void Paper(Rect2 r, float shadow = 4f)
    {
        Fill(new Rect2(r.Position.X + shadow, r.End.Y, r.Size.X, shadow), GarageInk.Shadow);
        Fill(new Rect2(r.End.X, r.Position.Y + shadow, shadow, r.Size.Y), GarageInk.Shadow);
        PaperRects.Add(Px(r));
        Frame(r, GarageInk.Ink, 2f);
    }

    /// <summary>The section header bar: a shade over the paper with a thin outline and a bold caps title.</summary>
    public void Bar(Rect2 r, string title)
    {
        Fill(r, GarageInk.Bar);
        Frame(r, GarageInk.Ink, 1f);
        TextCentred(GText.Heading, Fit(GText.Heading, title, r.Size.X - 8), r, r.Position.Y + r.Size.Y / 2 + 4.4f, GarageInk.Label);
    }

    /// <summary>The ruled table of rows: a line under each (the form's rule colour), the outer edges in ink, an optional column rule.</summary>
    public void Rules(Rect2 box, int rows, float rowH, float? columnX = null)
    {
        for (int i = 0; i <= rows; i++)
        {
            bool edge = i == 0 || i == rows;
            HLine(box.Position.X, box.End.X, box.Position.Y + i * rowH - 1, edge ? GarageInk.Ink : GarageInk.Rule);
        }
        VLine(box.Position.X, box.Position.Y - 1, box.Position.Y + rows * rowH, GarageInk.Ink);
        VLine(box.End.X - 1, box.Position.Y - 1, box.Position.Y + rows * rowH, GarageInk.Ink);
        if (columnX is { } cx) VLine(cx, box.Position.Y - 1, box.Position.Y + rows * rowH, GarageInk.Rule);
    }

    // ---- buttons and little widgets ----

    /// <summary>An orange bevelled button with a dark outline and a bold caps label; lit by the red pencil under it.</summary>
    public void Button(Rect2 r, string label, bool lit, GText? style = null)
    {
        var st = style ?? GText.ButtonBig;
        Fill(r, GarageInk.Orange);
        // the bevel: light top and left, dark bottom and right, inside the outline
        var inner = r.Grow(-1f);
        Fill(new Rect2(inner.Position, new Vector2(inner.Size.X, 1.2f)), GarageInk.OrangeHi);
        Fill(new Rect2(inner.Position, new Vector2(1.2f, inner.Size.Y)), GarageInk.OrangeHi);
        Fill(new Rect2(inner.Position.X, inner.End.Y - 1.2f, inner.Size.X, 1.2f), GarageInk.OrangeLo);
        Fill(new Rect2(inner.End.X - 1.2f, inner.Position.Y, 1.2f, inner.Size.Y), GarageInk.OrangeLo);
        Frame(r, GarageInk.OrangeEdge, 1f);
        float baseline = r.Position.Y + r.Size.Y / 2 + st.Size * 0.36f;
        TextCentred(st, label, r, baseline, GarageInk.Label);
        if (lit) Pencil(r.Position.X + 5, r.End.Y - 3.2f, r.Size.X - 10, mark: false);
    }

    /// <summary>The tab on a row's right that opens another page: a box with an arrow in it.</summary>
    public void RowTab(Rect2 r, bool lit = false)
    {
        Fill(r, new Color(GarageInk.Paper, 0.85f));
        Frame(r, GarageInk.Ink, 1f);
        float cx = r.Position.X + r.Size.X / 2, cy = r.Position.Y + r.Size.Y / 2;
        Poly([new(cx - 3, cy - 4.5f), new(cx + 3.5f, cy), new(cx - 3, cy + 4.5f)], lit ? GarageInk.Red : GarageInk.Ink, 1.1f, fill: new Color(GarageInk.Paper, 0.9f));
    }

    /// <summary>A true-colour swatch (a paint) with a dark outline.</summary>
    public void Swatch(Rect2 r, Color c) { Fill(r, c); Frame(r, GarageInk.Ink, 1f); }

    /// <summary>A scroll triangle (outline), pointing up or down, in the box.</summary>
    public void Triangle(Rect2 r, bool up, bool lit)
    {
        float cx = r.Position.X + r.Size.X / 2, cy = r.Position.Y + r.Size.Y / 2;
        float w = 6.5f, h = 5.5f, d = up ? -1 : 1;
        Poly([new(cx - w, cy - d * h / 2), new(cx + w, cy - d * h / 2), new(cx, cy + d * h / 2)], lit ? GarageInk.Red : GarageInk.Ink, 1.1f);
    }

    /// <summary>A small square button with a - or + (the DEFENSE steppers).</summary>
    public void StepBox(Rect2 r, bool plus, bool lit = false)
    {
        Fill(r, new Color(GarageInk.Paper, 0.9f));
        Frame(r, lit ? GarageInk.Red : GarageInk.Ink, 1f);
        float cx = r.Position.X + r.Size.X / 2, cy = r.Position.Y + r.Size.Y / 2;
        HLine(cx - 3, cx + 3, cy - 0.5f, GarageInk.Ink, 1.2f);
        if (plus) VLine(cx - 0.5f, cy - 3, cy + 3, GarageInk.Ink, 1.2f);
    }

    /// <summary>A long list's place: a track and a thumb on the paper's right edge.</summary>
    public void ScrollBar(float x, float y, float height, int visible, int total, int scroll)
    {
        if (total <= visible) return;
        float barH = Mathf.Max(10, height * visible / total), barY = y + (height - barH) * scroll / Mathf.Max(1, total - visible);
        Fill(new Rect2(x, y, 3, height), GarageInk.Rule);
        Fill(new Rect2(x, barY, 3, barH), GarageInk.Ink);
    }

    private void Poly(Vector2[] pts, Color line, float units, Color? fill = null)
    {
        var px = pts.Select(p => P(p.X, p.Y)).ToArray();
        if (fill is { } f) canvas.DrawColoredPolygon(px, f);
        var loop = px.Append(px[0]).ToArray();
        canvas.DrawPolyline(loop, line, Lw(units), antialiased: true);
    }

    // ---- the marks ----

    private static float Jit(int seed, int k) { uint h = (uint)(seed * 73856093 ^ k * 19349663); h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15; return (h & 0xffff) / 65535f * 2 - 1; }

    /// <summary>The red pencil: a slightly wavering line under the row's text, and (with <paramref name="mark"/>) the little arrow before it.</summary>
    public void Pencil(float x, float y, float width, bool mark = true)
    {
        int seed = (int)(x * 13 + y * 7);
        for (int pass = 0; pass < 2; pass++)
        {
            var pts = new List<Vector2>();
            int n = Mathf.Max(3, (int)(width / 14));
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                pts.Add(P(x + t * width + Jit(seed, i + pass * 31) * 0.6f, y + 0.45f * t + Jit(seed + 5, i + pass * 17) * (0.35f + pass * 0.2f) + pass * 0.5f));
            }
            canvas.DrawPolyline(pts.ToArray(), new Color(GarageInk.Red, pass == 0 ? 0.95f : 0.55f), Lw(pass == 0 ? 1.3f : 0.8f), antialiased: true);
        }
        if (mark)
        {
            float mx = x - 6, my = y - 6.5f;
            canvas.DrawColoredPolygon([P(mx, my - 3.2f), P(mx + 3.8f, my + 0.2f), P(mx - 0.2f, my + 3.4f)], GarageInk.Red);
        }
    }

    /// <summary>The choice made: a hand-drawn check mark in ink (the original's pick lists mark nothing; this stays off the text).</summary>
    public void Check(float x, float baseline)
    {
        int seed = (int)(x * 3 + baseline);
        canvas.DrawPolyline([P(x, baseline - 5.2f + Jit(seed, 1) * 0.3f), P(x + 3.2f, baseline - 0.9f), P(x + 9.5f, baseline - 9.6f + Jit(seed, 2) * 0.4f)], GarageInk.Ink, Lw(1.5f), antialiased: true);
    }

    /// <summary>A highlighter stroke behind a row's text (a slightly irregular band; the repair lists' look, kept for pages that want it).</summary>
    public void Highlighter(Rect2 r, Color? colour = null)
    {
        int seed = (int)(r.Position.X * 5 + r.Position.Y * 11);
        var top = new List<Vector2>(); var bot = new List<Vector2>();
        int n = Mathf.Max(4, (int)(r.Size.X / 16));
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n, x = r.Position.X + t * r.Size.X;
            float round = i == 0 || i == n ? 1.5f : 0f;
            top.Add(P(x + (i == 0 ? round : -round * 0), r.Position.Y + 1.5f + Jit(seed, i) * 0.7f));
            bot.Add(P(x, r.End.Y - 1.5f + Jit(seed + 3, i) * 0.7f));
        }
        bot.Reverse();
        canvas.DrawColoredPolygon(top.Concat(bot).ToArray(), colour ?? GarageInk.Green);
    }
}

/// <summary>
/// The overlay's control: it maps the shell's 640x480 units to the window as the shell's frame is mapped (4:3, centred), paints the paper
/// with its shader behind what <see cref="Painter"/> draws, and repaints on <see cref="Redraw"/> and when the window changes.
/// </summary>
public partial class GarageUiLayer : Control
{
    public Action<GarageUi>? Painter { get; set; }
    private PaperCanvas _paper = null!;
    private Vector2 _lastSize;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Linear;
        AddChild(_paper = new PaperCanvas { ShowBehindParent = true });
    }

    public void Redraw() => QueueRedraw();

    public override void _Process(double delta)
    {
        var size = GetViewportRect().Size;
        if (size != _lastSize) { _lastSize = size; QueueRedraw(); }
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
        var ui = new GarageUi(this, s, off);
        Painter?.Invoke(ui);
        _paper.Set(ui.PaperRects, s);
    }

    /// <summary>Paints the paper rectangles with the shader: fibres, grain, a mottled tone and faint coffee stains, continuous across the sheets.</summary>
    private partial class PaperCanvas : Control
    {
        private List<Rect2> _rects = [];

        private const string Code = """
            shader_type canvas_item;
            uniform float unit = 1.0;
            varying vec2 wp;
            float h(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
            float n(vec2 p) {
                vec2 i = floor(p), f = fract(p);
                f = f * f * (3.0 - 2.0 * f);
                return mix(mix(h(i), h(i + vec2(1, 0)), f.x), mix(h(i + vec2(0, 1)), h(i + vec2(1, 1)), f.x), f.y);
            }
            float fbm(vec2 p) {
                float a = 0.5, s = 0.0;
                for (int i = 0; i < 5; i++) { s += a * n(p); p = p * 2.03 + 7.1; a *= 0.5; }
                return s;
            }
            void vertex() { wp = VERTEX; }
            void fragment() {
                vec2 p = wp / unit;
                vec3 base = vec3(0.937, 0.741, 0.353);
                float tone = fbm(p * 0.03 + 3.0);
                float stain = smoothstep(0.55, 0.72, fbm(p * 0.011 + 11.0));
                float edge = smoothstep(0.0, 0.1, abs(fbm(p * 0.011 + 11.0) - 0.62));
                float fibre = n(vec2(p.x * 0.9, p.y * 0.11)) * n(vec2(p.x * 0.1 + 20.0, p.y * 0.8)) + n(vec2(p.x * 0.13, p.y * 1.0 + 9.0)) * n(vec2(p.x * 1.1, p.y * 0.1 + 5.0));
                float mottle = n(p * 0.55) - 0.5;
                vec3 c = base * (0.9 + 0.2 * tone);
                c = mix(c, vec3(0.80, 0.56, 0.22), stain * (0.30 + 0.25 * (1.0 - edge)));
                c -= vec3(0.05, 0.055, 0.035) * smoothstep(0.35, 0.8, fibre);
                c += vec3(0.018) * mottle;
                COLOR = vec4(c, 1.0);
            }
            """;

        private readonly ShaderMaterial _mat = new() { Shader = new Shader { Code = Code } };

        public override void _Ready()
        {
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;
            Material = _mat;
        }

        public void Set(List<Rect2> rects, float unit)
        {
            _rects = rects;
            _mat.SetShaderParameter("unit", unit);
            QueueRedraw();
        }

        public override void _Draw()
        {
            foreach (var r in _rects) DrawRect(r, Colors.White);
        }
    }
}
