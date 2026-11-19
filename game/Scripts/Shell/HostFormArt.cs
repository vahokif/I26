using Godot;

namespace I76.Game.Shell;

/// <summary>
/// The network host form (melee modes 3 and 4) as the port draws it: the Nitro Pack's NET MELEE·HOST EVENT FORM rebuilt shape for
/// shape (measured from its art, record 19 of its database), so it needs no Nitro file. Both games host with it: the base game always,
/// the Nitro Pack in the modern look (its 1997 look keeps the art: <see cref="Drawn"/>). Its left column is LOCAL DRIVER, GAME TYPE,
/// AREA OF PLAY with its list, OPTIONS (WEAPON RESTRICTIONS, GAME OPTIONS, PASSWORD), GAME NAME and NO. DRIVERS; its right panel the
/// car: DRIVER, the blueprint views, WEAPONS and PARTS, MAKE / MODEL, VARIANT and CONFIGURE CHASSIS; BROADCAST GAME and CANCEL under it.
/// The words, values and hotspots are where the Nitro form has them (MeleeScreen's tables). Pure layout, no engine.
/// </summary>
public static class HostFormArt
{
    /// <summary>
    /// The one switch: whether the host form (and the pop-ups it opens that have Nitro art: GAME TYPE, GAME OPTIONS, WEAPON
    /// RESTRICTIONS) is the port's drawing. Everywhere but the Nitro Pack's 1997 look, which keeps the original art.
    /// </summary>
    public static bool Drawn(bool nitro, bool look1997) => !(nitro && look1997);

    public const string Title = "NET MELEE·HOST EVENT FORM";
    /// <summary>The title's ink box.</summary>
    public static readonly Rect2I TitleBox = new(255, 10, 351, 19);

    /// <summary>The printed words: each with the box its ink fills in the art.</summary>
    public static readonly (string Text, Rect2I Box)[] Words =
    [
        ("LOCAL DRIVER", new(60, 29, 103, 11)), ("GAME TYPE", new(16, 73, 82, 11)), ("AREA OF PLAY", new(64, 100, 102, 11)),
        ("OPTIONS", new(83, 288, 62, 11)), ("GAME NAME", new(67, 382, 86, 11)), ("NO. DRIVERS", new(37, 424, 90, 11)),
        ("RENAME", new(177, 48, 34, 7)), ("RENAME", new(177, 401, 34, 7)),
        ("WEAPON RESTRICTIONS", new(52, 315, 122, 7)), ("GAME OPTIONS", new(74, 334, 77, 8)), ("PASSWORD", new(84, 356, 58, 7)),
        ("DRIVER:", new(227, 64, 48, 9)), ("WEAPONS", new(509, 44, 62, 10)), ("PARTS", new(521, 163, 40, 10)),
        ("MAKE / MODEL:", new(229, 405, 92, 10)), ("VARIANT:", new(230, 425, 54, 9)),
        ("CONFIGURE CHASSIS", new(479, 424, 117, 10)), ("BROADCAST GAME", new(238, 457, 125, 10)), ("CANCEL", new(536, 457, 54, 10)),
    ];

    /// <summary>The left column's panels: the line on top, the heading strip's rows, the line under it (−1: none, the strip is the panel), the panel's 2-pixel foot.</summary>
    public static readonly (int Top, int Strip0, int Strip1, int Line, int Foot)[] Panels =
    [
        (26, 27, 41, 43, 60),       // LOCAL DRIVER
        (69, 70, 85, -1, 87),       // GAME TYPE (its strip only as wide as its word)
        (97, 98, 112, 114, 276),    // AREA OF PLAY
        (285, 286, 300, 302, 372),  // OPTIONS
        (379, 380, 394, 396, 413),  // GAME NAME
        (420, 421, 436, -1, 438),   // NO. DRIVERS (likewise)
    ];

    /// <summary>The left column's x (its 2-pixel sides at 8 and 216, the strips 10..215) and the right panel's (216..631, 37..443).</summary>
    public const int L0 = 8, L1 = 217, R1 = 631, RTop = 37, RFoot = 442;

    /// <summary>The area list: the highlighted area's box, the rules under its rows, the scroll triangles.</summary>
    public static readonly Rect2I Highlight = new(16, 126, 194, 22);
    public const int ListRule0 = 169, ListRules = 6, UpY = 152, DownY = 261, ArrowX = 14;

    /// <summary>The buttons: x0, y0, x1, y1 and the border's thickness.</summary>
    public static readonly (int X0, int Y0, int X1, int Y1, int T)[] Buttons =
    [
        (173, 46, 213, 57, 1), (173, 399, 213, 410, 1),                               // the two RENAMEs
        (33, 310, 193, 325, 1), (57, 330, 169, 345, 1), (66, 351, 160, 366, 1),         // WEAPON RESTRICTIONS, GAME OPTIONS, PASSWORD
        (457, 421, 617, 436, 1),                                                        // CONFIGURE CHASSIS
        (229, 452, 370, 471, 2), (490, 452, 631, 471, 2),                               // BROADCAST GAME, CANCEL
    ];

    /// <summary>The list arrows' places (GAME TYPE, MAKE / MODEL, VARIANT), each 11 x 14.</summary>
    public static readonly Vector2I[] Arrows = [new(204, 72), new(611, 400), new(438, 420)];

    /// <summary>NO. DRIVERS' − and + (10 x 10).</summary>
    public static readonly Vector2I Minus = new(155, 425), Plus = new(201, 425);

    /// <summary>The right panel's lines to write on: DRIVER's, the six weapons', the seven parts', MAKE / MODEL's and VARIANT's (x0, x1, y).</summary>
    public static IEnumerable<(int X0, int X1, int Y)> Lines()
    {
        yield return (277, 420, 72);
        for (int k = 0; k < 6; k++) yield return (458, 616, 72 + 17 * k);
        for (int k = 0; k < 7; k++) yield return (459, 616, 190 + 17 * k);
        yield return (325, 617, 415);
        yield return (288, 444, 435);
    }

    /// <summary>Every shape of the form (no words).</summary>
    public static void Shapes(InkPen pen)
    {
        // the left column's panels and the right panel's left side, which they share
        pen.Rect(216, 26, 2, RFoot + 2 - 26, Paint.Ink);
        for (int i = 0; i < Panels.Length; i++)
        {
            var (top, s0, s1, line, foot) = Panels[i];
            pen.Rect(L0, top, L1 - L0 + 1, 1, Paint.Ink);
            pen.Rect(L0, top, 2, foot + 2 - top, Paint.Ink);
            int sx1 = i == 1 ? 104 : i == 5 ? 147 : 215;
            pen.Strip(10, s0, sx1, s1);
            if (sx1 < 215) pen.Rect(sx1 + 1, s0, 1, s1 - s0 + 2, Paint.Ink);
            if (line >= 0) pen.Rect(L0, line, L1 - L0 + 1, 1, Paint.Ink);
            pen.Rect(L0, foot, L1 - L0 + 1, 2, Paint.Ink);
        }
        pen.Box(Highlight.Position.X, Highlight.Position.Y, Highlight.End.X - 1, Highlight.End.Y - 1);
        for (int k = 0; k < ListRules; k++) pen.Rect(L0, ListRule0 + 17 * k, L1 - L0 + 1, 1, Paint.Ink);
        pen.Triangle(ArrowX, UpY, up: true);
        pen.Triangle(ArrowX, DownY, up: false);
        pen.Minus(Minus.X, Minus.Y);
        pen.Plus(Plus.X, Plus.Y);
        // the right panel
        pen.Rect(216, RTop, R1 - 216 + 1, 2, Paint.Ink);
        pen.Rect(216, RFoot, R1 - 216 + 1, 2, Paint.Ink);
        pen.Rect(R1 - 1, RTop, 2, RFoot + 2 - RTop, Paint.Ink);
        foreach (var (x0, x1, y) in Lines()) pen.Rect(x0, y, x1 - x0 + 1, 1, Paint.Ink);
        foreach (var (x0, y0, x1, y1, t) in Buttons) pen.Button(x0, y0, x1, y1, t);
        foreach (var a in Arrows) pen.Arrow(a.X, a.Y);
    }

    /// <summary>The Turrets setting's words in a short value column (Play Options' sheet, GAME OPTIONS): the builds by their years.</summary>
    public static string TurretsShort(bool cd1997) => cd1997 ? "1997 CD" : "1998";
}
