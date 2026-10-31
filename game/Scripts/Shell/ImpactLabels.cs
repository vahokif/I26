using Godot;

namespace I76.Game.Shell;

/// <summary>
/// The main menu's buttons and banners (bank 0x3e) are white or grey Impact,
/// slanted 18°, on black boxes. In the modern look they are drawn again at the
/// window's resolution, over the sprites, from the system's Impact: each
/// sprite's text, size, tracking and placement fitted to the original pixels
/// (tools/fit-impact.py; the glyphs at Impact's kerned positions plus the
/// tracking, slanted about the baseline). Without Impact installed, in the
/// 1997 look, or while a pop-up or overlay is up, the sprites show as they are.
/// </summary>
public static class ImpactLabels
{
    /// <summary>One label: the text, the font size, tracking and extra word spacing in shell pixels, the pen's start and baseline in the box, the slant in degrees.</summary>
    /// <summary>A label's fit; Strike draws a line through it, Centre sets it in the middle of its box (X unused).</summary>
    public sealed record Spec(string Text, float Size, float Track, float X, float Baseline, float WordSpace, string? Underline = null, float Slant = ImpactLabels.Slant,
        bool Strike = false, bool Centre = false, int Dim = 0x6d6d6d);

    /// <summary>The lit sprite of each pair (the dim one is the next, OTHER's 46 is 45's dim) → its label.</summary>
    public static readonly Dictionary<int, Spec> Lit = new()
    {
        [0] = new("OPTIONS", 24.125f, 0.969f, 24.479f, 33.375f, 0.000f),
        [2] = new("AUTO MELEE", 15.000f, 0.000f, 8.693f, 16.880f, 0.250f),
        [4] = new("MELEE", 40.250f, 0.000f, 6.875f, 40.883f, 0.000f),
        [6] = new("HOST", 15.000f, 0.000f, 7.402f, 16.973f, 0.000f),
        [8] = new("INSTANT MELEE", 15.000f, 0.000f, 7.389f, 16.880f, 0.250f),
        [10] = new("JOIN", 15.000f, 0.000f, 8.199f, 17.106f, 0.000f),
        [12] = new("MODEM", 15.000f, 0.000f, 8.010f, 17.005f, 0.000f),
        [14] = new("IPX", 14.750f, 0.500f, 6.025f, 16.880f, 0.000f),
        [16] = new("NULLMODEM", 15.000f, 0.000f, 6.203f, 16.880f, 0.000f),
        [18] = new("SCENARIO", 14.938f, 0.000f, 6.873f, 17.122f, 0.000f),
        [20] = new("LOAD BOOKMARK", 15.000f, 0.000f, 9.521f, 16.966f, 0.000f),
        [22] = new("NEW TRIP", 15.000f, 0.000f, 5.906f, 17.122f, 0.125f),
        [24] = new("TRIP", 40.125f, -0.062f, 9.209f, 40.883f, 0.000f),
        [26] = new("INTERNET", 15.000f, 0.000f, 4.631f, 16.880f, 0.000f),
        [28] = new("MULTI MELEE", 15.000f, 0.000f, 10.748f, 16.880f, 0.750f),
        [30] = new("EXIT", 24.000f, 1.000f, 1.949f, 32.953f, 0.000f),
        [32] = new("TRAINING", 14.875f, 0.500f, 8.994f, 16.919f, 0.000f),
        [34] = new("FUNKY SINGLE-PLAYER UNMITIGATED VENGEANCE QUEST", 13.000f, 0.500f, 49.941f, 14.623f, 0.008f),
        [35] = new("START A NEW TRIP", 12.000f, 1.000f, 147.484f, 14.123f, -0.062f),
        [36] = new("LOAD A SAVED TRIP", 12.000f, 1.000f, 146.670f, 14.123f, -0.062f),
        [37] = new("OTHER STUFF THAT\u2019S NOT THE TRIP", 12.000f, 1.000f, 103.820f, 14.123f, -0.078f),
        [38] = new("SINGLE PLAYER FESTIVALS OF AUTO COMBAT AND JOY", 12.000f, 1.000f, 63.539f, 14.123f, -0.227f),
        [39] = new("AUTO BRAWL VS. CARS EQUIPPED WITH COMPUTER BRAINS", 12.000f, 1.000f, 44.123f, 14.123f, -0.078f, Underline: "COMPUTER BRAINS"),
        [40] = new("DON\u2019T TRY THIS AT HOME", 12.000f, 1.000f, 132.084f, 13.881f, 0.000f),
        [41] = new("MULTI-PLAYER CELEBRATION OF RETRIBUTION AND HUMILIATION", 12.000f, 1.000f, 27.385f, 14.123f, -0.250f),
        [42] = new("GRAPHIC, AUDIO, GAME SETTINGS AND MORE...", 12.000f, 1.000f, 79.816f, 14.123f, -0.195f),
        [43] = new("LEAVING SO SOON?", 12.000f, 1.000f, 137.941f, 13.967f, -0.250f),
        [44] = new("HELPING YOU TO HELP YOURSELF", 12.000f, 1.000f, 110.889f, 14.123f, -0.125f),
        [45] = new("OTHER", 14.625f, 0.500f, 8.125f, 16.880f, 0.000f),
    };

    /// <summary>Nitro's main menu (bank 56): a button's label by its dim (even) sprite, the lit one the next; a banner by its own sprite. Dim is the sprite's grey.</summary>
    public static readonly Dictionary<int, Spec> NitroMain = new()
    {
        [0] = new("OPTIONS", 24.125f, 0.969f, 24.479f, 33.375f, 0.000f, Dim: 0x777370),
        [2] = new("MELEE", 40.250f, 0.000f, 6.875f, 40.883f, 0.000f, Dim: 0x6a6a6c),
        [4] = new("HOST", 15.000f, 0.000f, 7.402f, 16.973f, 0.000f, Dim: 0x777370),
        [6] = new("INSTANT MELEE", 15.000f, 0.000f, 7.389f, 16.880f, 0.250f),
        [8] = new("JOIN", 15.000f, 0.000f, 8.199f, 17.106f, 0.000f, Dim: 0x777370),
        [10] = new("MODEM", 15.000f, 0.000f, 8.010f, 17.005f, 0.000f),
        [12] = new("IPX", 14.750f, 0.500f, 6.025f, 16.880f, 0.000f, Dim: 0x777370),
        [14] = new("NULLMODEM", 15.000f, 0.000f, 6.203f, 16.880f, 0.000f),
        [16] = new("INTERNET", 15.000f, 0.000f, 4.631f, 16.880f, 0.000f),
        [18] = new("MULTI MELEE", 15.000f, 0.000f, 10.748f, 16.880f, 0.750f),
        [20] = new("EXIT", 24.000f, 1.000f, 1.949f, 32.953f, 0.000f, Dim: 0x777370),
        [22] = new("MUST BE AT LEAST 52\" TALL TO RIDE", 12.000f, 0.000f, 52.867f, 14.100f, 0.031f),
        [23] = new("BAD MOFO RISING", 12.000f, 0.000f, 93.855f, 13.881f, 0.062f),
        [24] = new("DAISY WHO?", 12.000f, 0.000f, 104.008f, 13.506f, 0.000f),
        [25] = new("SHOULD BE MEDICATED...BUT ISN\u2019T", 12.000f, 0.000f, 55.926f, 13.881f, 0.375f),
        [26] = new("COME BACK AFTER YOU FINISH THE OTHER MISSIONS", 12.000f, 0.000f, 14.793f, 14.123f, 0.125f),
        [27] = new("KICK SOME VIGILANTE ASS", 12.000f, 0.000f, 74.332f, 14.123f, -0.203f),
        [28] = new("SCENARIOS", 40.250f, 0.000f, 7.484f, 40.883f, 0.000f, Dim: 0x6a6a6c),
        [30] = new("TAURUS", 14.000f, 0.000f, 6.328f, 16.880f, 0.000f, Dim: 0x454545),
        [32] = new("JADE", 14.000f, 0.000f, 7.350f, 16.880f, 0.000f, Dim: 0x454545),
        [34] = new("SKEETER", 13.938f, 0.000f, 5.496f, 17.075f, 0.000f, Dim: 0x454545),
        [36] = new("?", 14.500f, -0.500f, 38.695f, 17.169f, 0.000f, Dim: 0x454545),
        [38] = new("NATTY DREAD", 14.000f, 0.000f, 6.459f, 17.380f, 0.250f, Dim: 0x454545),
        [40] = new("TRAINING", 15.000f, 0.008f, 5.119f, 17.364f, 0.000f, Dim: 0x454545),
        [42] = new("GRAPHIC, AUDIO, GAME SETTINGS AND MORE...", 12.000f, 1.000f, 79.816f, 14.123f, -0.195f),
        [43] = new("LEAVING SO SOON?", 12.000f, 1.000f, 137.941f, 13.967f, -0.250f),
        [44] = new("HELPING YOU TO HELP YOURSELF", 12.000f, 1.000f, 110.889f, 14.123f, -0.125f),
        [47] = new("HOW TO KILL FRIENDS AND IMMOLATE ENEMIES", 12.000f, 0.000f, 26.250f, 13.983f, 0.125f),
        [48] = new("AUTO BRAWL VS. CARS EQUIPPED WITH COMPUTER BRAINS", 12.000f, 0.000f, -0.010f, 13.881f, 0.258f, Underline: "COMPUTER BRAINS"),
        [49] = new("MULTI-PLAYER FESTIVAL OF RETRIBUTION & HUMILIATION", 12.000f, 0.000f, 5.908f, 13.889f, 0.062f),
    };

    /// <summary>The banners of bank 56 (one lit sprite each).</summary>
    private static bool NitroBanner(int s) => s is >= 22 and <= 27 or >= 42 and <= 44 or >= 47 and <= 49;

    /// <summary>The label of a Nitro bank 56 sprite, and whether it is the lit (white) one.</summary>
    public static (Spec Spec, bool Lit)? OfNitro(int sprite)
    {
        if (NitroBanner(sprite)) return NitroMain.TryGetValue(sprite, out var b) ? (b, true) : null;
        if (sprite % 2 == 0) return NitroMain.TryGetValue(sprite, out var d) ? (d, false) : null;
        return NitroMain.TryGetValue(sprite - 1, out var l) ? (l, true) : null;
    }

    public const float Slant = 18f;

    /// <summary>The label a sprite shows, and whether it is the lit (white) one.</summary>
    public static (Spec Spec, bool Lit)? Of(int sprite)
    {
        if (Lit.TryGetValue(sprite, out var s)) return (s, true);
        if (sprite == 46) return Lit.TryGetValue(45, out var o) ? (o, false) : null;
        if (sprite % 2 == 1 && Lit.TryGetValue(sprite - 1, out var d)) return (d, false);
        return null;
    }
}

/// <summary>
/// Draws the visible Impact labels over the shell's frame, mapped as the frame
/// is (640x480 kept 4:3 and centred): a black box over the sprite, then the
/// text glyph by glyph, white when lit, grey (109) when dim — OTHER's dim
/// grey is the warmer 119/115/112 its sprite has.
/// </summary>
public partial class ImpactLayer : Control
{
    private readonly Dictionary<(int X, int Y), (Rect2I Box, ImpactLabels.Spec Spec, Color Colour)> _labels = [];
    private Font? _impact;
    private readonly Dictionary<float, FontVariation> _fonts = [];
    public bool Enabled { get; set; } = true;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        // a missing family falls back to another font silently: only Impact itself will do (HdAssets checks)
        if (HdAssets.Impact(TextServer.FontAntialiasing.Gray, TextServer.Hinting.None) is not { } impact) { Enabled = false; return; }
        _impact = impact;
    }

    public bool Available => _impact is not null && Enabled;

    /// <summary>Impact slanted about the baseline, as Godot's own italic (xx 1, xy tan, yx 0, yy 1).</summary>
    private FontVariation Slanted(float slant)
    {
        if (!_fonts.TryGetValue(slant, out var f))
            _fonts[slant] = f = new FontVariation { BaseFont = _impact, VariationTransform = new Transform2D(1f, Mathf.Tan(Mathf.DegToRad(slant)), 0f, 1f, 0f, 0f) };
        return f;
    }

    /// <summary>
    /// Where glyph i starts: where the kerned text up to and including it ends, less its own advance (so the pair
    /// with the glyph before it counts: T. in SOUTHWEST... pulls the dot under the T's arm), plus the tracking and
    /// the word spacing before it. At the end of the text, its kerned width.
    /// </summary>
    private float Advance(ImpactLabels.Spec spec, int i, int px, float k, float s)
    {
        var font = Slanted(spec.Slant);
        float Width(string t) => font.GetStringSize(t, HorizontalAlignment.Left, -1, px).X;
        float x = i < spec.Text.Length ? Width(spec.Text[..(i + 1)]) - Width(spec.Text[i].ToString()) : Width(spec.Text);
        return x * k + (i * spec.Track + spec.Text[..i].Count(c => c == ' ') * spec.WordSpace) * s;
    }

    public void Show(int x, int y, int w, int h, int sprite, bool nitro = false)
    {
        if ((nitro ? ImpactLabels.OfNitro(sprite) : ImpactLabels.Of(sprite)) is not { } l) { _labels.Remove((x, y)); QueueRedraw(); return; }
        Put(x, y, w, h, l.Spec, l.Lit ? Colors.White : nitro ? Color.Color8((byte)(l.Spec.Dim >> 16), (byte)(l.Spec.Dim >> 8), (byte)l.Spec.Dim) : sprite == 46 ? Color.Color8(119, 115, 112) : Color.Color8(109, 109, 109));
    }

    /// <summary>Any label over a black box of the 640x480 frame (the loading screen's line, say).</summary>
    public void Put(int x, int y, int w, int h, ImpactLabels.Spec spec, Color colour)
    {
        _labels[(x, y)] = (new Rect2I(x, y, w, h), spec, colour);
        QueueRedraw();
    }

    public void Hide(int x, int y) { if (_labels.Remove((x, y))) QueueRedraw(); }

    public void Clear() { _labels.Clear(); QueueRedraw(); }

    public override void _Draw()
    {
        if (!Available) return;
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * s) / 2;
        foreach (var (box, spec, colour) in _labels.Values)
        {
            var at = off + new Vector2(box.Position.X, box.Position.Y) * s;
            DrawRect(new Rect2(at, new Vector2(box.Size.X, box.Size.Y) * s), Colors.Black);
            int px = Mathf.Max(1, Mathf.RoundToInt(spec.Size * s));
            float k = spec.Size * s / px;     // the integer font size's remainder, made up in the advance
            float pen = spec.Centre ? (box.Size.X * s - Advance(spec, spec.Text.Length, px, k, s)) / 2 : spec.X * s;
            var baseline = at.Y + spec.Baseline * s;
            for (int i = 0; i < spec.Text.Length; i++)
            {
                // Impact's own kerning: the pen at the kerned width of the text before it, plus the tracking
                float x = Advance(spec, i, px, k, s);
                DrawString(Slanted(spec.Slant), new Vector2(at.X + pen + x, baseline), spec.Text[i].ToString(), HorizontalAlignment.Left, -1, px, colour);
            }
            if (spec.Strike)
            {
                // through the middle of the capitals (Impact's cap height is about 0.79 em), moved over by the slant at
                // that height so it sits centred on the leaning letters, a little past each end, rising slightly
                float w = Advance(spec, spec.Text.Length, px, k, s);
                float mid = spec.Size * 0.4f * s, lean = mid * Mathf.Tan(Mathf.DegToRad(spec.Slant)), rise = spec.Size * 0.12f * s;
                var a = new Vector2(at.X + pen + lean - 2 * s, baseline - mid + rise);
                var b = new Vector2(at.X + pen + lean + w + 2 * s, baseline - mid - rise);
                DrawLine(a, b, colour, Mathf.Max(1f, spec.Size * 0.09f * s), true);
            }
            if (spec.Underline is { } u && spec.Text.IndexOf(u, StringComparison.Ordinal) is >= 0 and var from)
            {
                float x0 = Advance(spec, from, px, k, s), x1 = Advance(spec, from + u.Length, px, k, s) - spec.Track * s;
                // straight, under the glyphs' feet (which the slant leaves where they are): 1.4 px below the baseline, 1.2 thick (banner 39)
                DrawRect(new Rect2(at.X + pen + x0, baseline + 1.4f * s, x1 - x0, 1.2f * s), colour);
            }
        }
    }
}
