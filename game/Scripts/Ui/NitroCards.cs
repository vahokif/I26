using Godot;
using I76.Game.Shell;

namespace I76.Game.Ui;

/// <summary>
/// The Nitro Pack's loading cards (addon\pNNload.pcx, b01load.pcx, loadscr.pcx) are white Impact on black at 640x480: a small
/// underlined "EPISODE n", the title, the level's date. In the modern look their lines are set again in the system's Impact at
/// the window's resolution, fitted to each picture (tools/fit-cards.py: per line the em size, slant, rotation, pen start on
/// the baseline, and the horizontal scale the cards stretch Impact by). The pictures are not kept, only these lines.
/// </summary>
public static class NitroCards
{
    /// <summary>One line: em size (640x480 px), slant and rotation (degrees, rotation counter-clockwise), the pen's start on the baseline (640x480 px), horizontal scale.</summary>
    public sealed record Line(string Text, float Size, float Slant, float Rot, float X, float Y, float Sx, bool Underline = false);

    private static Line[] Lines(params Line[] l) => l;
    private static Line L(string t, float size, float slant, float rot, float x, float y, float sx, bool underline = false) => new(t, size, slant, rot, x, y, sx, underline);

    /// <summary>The lines of a card by its file stem: p01 .. p19, b01, loadscr (and the base game's card has none).</summary>
    public static Line[]? Of(string card) => Table.GetValueOrDefault(card.ToLowerInvariant() is var c && c.EndsWith("load") ? c[..^4] : c.ToLowerInvariant());

    private static readonly Dictionary<string, Line[]> Table = new()
    {
        ["p01"] = Lines(L("EPISODE 1", 28.22f, 18.54f, 25.06f, 94.95f, 153.18f, 1.066f, underline: true), L("\"A New Hope\"", 62.06f, 18.57f, 24.96f, 83.28f, 247.85f, 0.972f), L("March 24, 1975", 48.64f, 18.77f, -0.01f, 320.33f, 456.59f, 0.988f)),
        ["p02"] = Lines(L("EPISODE 2", 28.87f, 18.28f, 25.02f, 95.35f, 153.55f, 1.050f, underline: true), L("\"Mad Clown Disease\"", 59.63f, 18.74f, 24.99f, 14.86f, 279.86f, 1.008f), L("January 16, 1974", 49.02f, 18.78f, 0.02f, 293.18f, 457.40f, 0.984f)),
        ["p03"] = Lines(L("EPISODE 3", 28.49f, 18.53f, 25.07f, 94.75f, 152.23f, 1.057f, underline: true), L("\"Bad Fuel\"", 60.82f, 18.68f, 24.99f, 77.88f, 251.31f, 0.991f), L("June 11, 1975", 48.60f, 18.77f, -0.08f, 364.88f, 456.10f, 0.997f)),
        ["p04"] = Lines(L("EPISODE 4", 28.63f, 18.39f, 24.99f, 94.31f, 152.55f, 1.050f, underline: true), L("\"Two Days Before\"", 59.99f, 18.64f, 24.99f, 35.13f, 269.43f, 1.004f), L("July 1, 1976", 47.14f, 18.75f, 0.07f, 398.47f, 456.48f, 1.024f)),
        ["p05"] = Lines(L("EPISODE 5", 28.20f, 18.56f, 25.11f, 94.72f, 152.61f, 1.067f, underline: true), L("\"Stop 'N' Go\"", 59.70f, 18.51f, 25.01f, 100.33f, 240.44f, 1.011f), L("November 20, 1974", 50.36f, 18.78f, 0.01f, 242.80f, 456.78f, 0.957f)),
        ["p06"] = Lines(L("EPISODE 6", 28.96f, 18.54f, 25.03f, 94.70f, 153.65f, 1.037f, underline: true), L("\"Crimson\"", 59.77f, 18.72f, 25.03f, 116.48f, 232.09f, 1.009f), L("July 3, 1975", 47.43f, 18.76f, 0.01f, 390.96f, 456.88f, 1.016f)),
        ["p07"] = Lines(L("EPISODE 7", 28.93f, 18.52f, 25.03f, 95.34f, 153.12f, 1.037f, underline: true), L("\"Piece Be With You\"", 59.14f, 18.70f, 25.01f, 31.00f, 271.66f, 1.017f), L("May 12, 1976", 50.15f, 18.70f, -0.10f, 369.36f, 457.22f, 0.960f)),
        ["p08"] = Lines(L("EPISODE 8", 26.85f, 18.60f, 25.09f, 94.82f, 153.03f, 1.124f, underline: true), L("\"Betrayed\"", 61.45f, 18.55f, 24.97f, 110.04f, 236.24f, 0.977f), L("September 2, 1975", 46.22f, 18.77f, 0.01f, 251.41f, 455.41f, 1.041f)),
        ["p09"] = Lines(L("EPISODE 9", 28.72f, 18.50f, 25.01f, 94.56f, 152.68f, 1.050f, underline: true), L("\"Mojo Rising\"", 62.10f, 18.72f, 25.02f, 82.04f, 248.80f, 0.974f), L("April 5, 1976", 50.49f, 18.70f, -0.07f, 373.51f, 457.50f, 0.955f)),
        ["p10"] = Lines(L("EPISODE 10", 28.19f, 18.49f, 25.03f, 94.42f, 152.78f, 1.067f, underline: true), L("\"Mother\"", 63.09f, 18.65f, 24.95f, 132.63f, 224.77f, 0.957f), L("November 7, 1975", 49.62f, 18.68f, -0.11f, 273.80f, 456.69f, 0.972f)),
        ["p11"] = Lines(L("EPISODE 11", 28.41f, 18.53f, 25.04f, 95.07f, 153.93f, 1.058f, underline: true), L("\"Never Get Out\"", 60.80f, 18.77f, 24.98f, 62.22f, 255.78f, 0.993f), L("June 23, 1975", 47.24f, 18.80f, 0.03f, 351.71f, 456.23f, 1.022f)),
        ["p12"] = Lines(L("EPISODE 12", 29.81f, 18.43f, 25.02f, 94.52f, 153.22f, 1.013f, underline: true), L("\"Allies\"", 61.54f, 18.56f, 24.92f, 99.02f, 240.22f, 0.977f), L("March 19, 1976", 48.90f, 18.76f, 0.02f, 323.03f, 457.22f, 0.985f)),
        ["p13"] = Lines(L("EPISODE 13", 27.67f, 18.58f, 25.07f, 95.60f, 152.87f, 1.085f, underline: true), L("\"Shutdown\"", 60.67f, 18.62f, 25.01f, 93.86f, 241.81f, 0.992f), L("May 27, 1975", 47.24f, 18.76f, 0.00f, 368.96f, 455.85f, 1.021f)),
        ["p14"] = Lines(L("EPISODE 14", 28.13f, 18.49f, 25.02f, 93.99f, 153.42f, 1.067f, underline: true), L("\"Nice Try\"", 62.57f, 18.50f, 24.95f, 100.14f, 239.33f, 0.962f), L("February 3, 1976", 50.48f, 18.77f, -0.02f, 292.05f, 457.63f, 0.953f)),
        ["p15"] = Lines(L("EPISODE 15", 27.66f, 18.49f, 25.07f, 94.68f, 153.47f, 1.085f, underline: true), L("\"Decoys\"", 61.30f, 18.86f, 24.99f, 101.63f, 239.17f, 0.982f), L("June 30, 1975", 50.07f, 18.56f, -0.20f, 350.61f, 457.03f, 0.962f)),
        ["p16"] = Lines(L("EPISODE 16", 27.71f, 18.49f, 25.06f, 94.88f, 152.97f, 1.085f, underline: true), L("\"Foxy Cat\"", 60.71f, 18.66f, 24.97f, 102.88f, 237.92f, 0.993f), L("October 15, 1974", 49.54f, 18.78f, -0.04f, 294.02f, 456.48f, 0.973f)),
        ["p17"] = Lines(L("EPISODE 17", 28.34f, 18.47f, 25.06f, 94.95f, 154.23f, 1.067f, underline: true), L("\"Velocity\"", 60.69f, 18.79f, 24.96f, 98.36f, 239.53f, 0.992f), L("October 28, 1974", 47.85f, 18.79f, 0.01f, 289.87f, 455.98f, 1.009f)),
        ["p18"] = Lines(L("EPISODE 18", 28.33f, 18.46f, 25.05f, 95.75f, 153.11f, 1.067f, underline: true), L("\"Break-In\"", 60.59f, 18.81f, 25.03f, 93.08f, 242.13f, 0.996f), L("December 24, 1974", 48.94f, 18.70f, -0.03f, 243.74f, 456.25f, 0.982f)),
        ["p19"] = Lines(L("EPISODE 19", 29.70f, 18.22f, 24.99f, 94.72f, 153.51f, 1.013f, underline: true), L("\"Assassination\"", 59.21f, 18.70f, 25.02f, 54.53f, 260.27f, 1.019f), L("April 4, 1976", 49.77f, 18.73f, -0.10f, 376.99f, 457.23f, 0.969f)),
        ["b01"] = Lines(L("PROLOGUE", 28.13f, 18.41f, 25.03f, 96.00f, 153.11f, 1.067f, underline: true), L("\"Auto-Vigilante 101\"", 59.84f, 18.74f, 25.00f, 24.29f, 273.93f, 1.009f), L("February 2, 1974", 45.87f, 18.64f, 0.02f, 296.09f, 455.81f, 1.053f)),
        ["loadscr"] = Lines(L("DRIVE OFFENSIVELY...", 42.38f, 18.83f, 0.01f, 176.79f, 442.32f, 0.990f)),
    };
}

/// <summary>Draws a card's lines in the system's Impact over black, mapped as the picture is (4:3, centred): white, slanted about the baseline, rotated, the underline under the baseline.</summary>
public partial class NitroCardLayer : Control
{
    public NitroCards.Line[] Lines { get; set; } = [];
    private Font? _impact;

    /// <summary>True when the system has Impact itself (a missing family falls back to another font silently, which will not do).</summary>
    public bool Available => _impact is not null;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _impact = HdAssets.Impact(TextServer.FontAntialiasing.Gray, TextServer.Hinting.None);
        QueueRedraw();
    }

    /// <summary>Whether this machine can set the cards (so the caller knows to fall back to the bitmap).</summary>
    public static bool HasImpact()
    {
        return HdAssets.Impact(TextServer.FontAntialiasing.Gray) is not null;
    }

    public override void _Draw()
    {
        if (_impact is null) return;
        var size = GetViewportRect().Size;
        float s = Mathf.Min(size.X / 640f, size.Y / 480f);
        var off = (size - new Vector2(640f, 480f) * s) / 2;
        foreach (var l in Lines)
        {
            int px = Mathf.Max(1, Mathf.RoundToInt(l.Size * s));
            float t = Mathf.Tan(Mathf.DegToRad(l.Slant)), r = Mathf.DegToRad(l.Rot), c = Mathf.Cos(r), sn = Mathf.Sin(r);
            // text frame (u right, w down from the baseline) -> screen: shear, horizontal scale, rotation, then the pen
            Transform2D Frame(float k) => new(new Vector2(c * l.Sx, -sn * l.Sx) * k, new Vector2(-c * t + sn, sn * t + c) * k, off + new Vector2(l.X, l.Y) * s);
            DrawSetTransformMatrix(Frame(l.Size / px * s));
            DrawString(_impact, Vector2.Zero, l.Text, HorizontalAlignment.Left, -1, px, Colors.White);
            if (l.Underline)
            {
                float w = _impact.GetStringSize(l.Text, HorizontalAlignment.Left, -1, px).X * l.Size / px;
                DrawSetTransformMatrix(Frame(s));
                DrawRect(new Rect2(0, 2f, w, 1.5f), Colors.White);
            }
        }
        DrawSetTransform(Vector2.Zero);
    }
}
