using Godot;
using I76.Game.Sim;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game.Ui;

/// <summary>
/// The target box in the 1997 look, as the original draws it on the
/// hardware path: four 8x8 corner
/// sprites in the target's team art (ztarg&lt;T&gt;01..04: white for team 0,
/// the unaffiliated cars of a melee and most missions), a 30-pixel health
/// bar just over the box, no distance, and the yellow starburst ztarg902 at
/// the screen's edge when the target is off screen. Everything is in the
/// 640x480 frame's pixels scaled by window height / 480, drawn
/// nearest-neighbour, the sprites as stored (mode 1 shows them upside down
/// against the picture: the stored rows top to bottom).
///
/// The box is centred on the target's collider centre and
/// sized S = clamp(|R·sx/z|, 23, 150) by its collider radius R
/// (from the collider record) over its depth z, sx
/// being 320 at 640x480: a car of R ≈ 2.5–2.8 m is at the 23-pixel floor
/// from about 35–40 m out, so at range the box is a fixed 26x34 (2·0.6S
/// wide, 2·0.4S + 16 high), and it only grows when the car is close (square
/// at S = 40, about 20 m). The port's R and centre are the sim's
/// (Combat.Collider: the COLP box's middle and farthest corner + 0.1), not
/// the whole hierarchy's that the record also takes in.
/// </summary>
public sealed class TargetIndicator1997
{
    /// <summary>True while the 1997 look is on.</summary>
    public required Func<bool> On { get; init; }
    public required Combat Combat { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public Func<Vehicle, Node3D?>? CarOf { get; init; }
    /// <summary>A texture member by its stem (the ztarg maps), rows bottom-up as stored.</summary>
    public required Func<string, Texture2D?> Texture { get; init; }
    /// <summary>The mission's palette: the bar's colours are its entries, the frame index 0xfe.</summary>
    public I76.Formats.Imaging.Palette? Palette { get; init; }

    /// <summary>Team → art set (the name table): 0 → ztarg2 (white), 1 → ztarg1 (dark green), 2 → ztarg3 (dark red), 3–7 → ztarg4–8.</summary>
    private static readonly int[] TeamArt = [2, 1, 3, 4, 5, 6, 7, 8];
    private const int MinBox = 23, MaxBox = 150;   // 0x17, 0x96

    private readonly Dictionary<string, (int W, int H, List<(int X, int Y, Color C)> Px)?> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private Color[]? _bands;   // red, yellow, green, white
    private Color _frame;

    /// <summary>A sprite's opaque pixels in stored order (row 0 first): drawn that way down the screen, as mode 1 does.</summary>
    private (int W, int H, List<(int X, int Y, Color C)> Px)? Sprite(string stem)
    {
        if (_sprites.TryGetValue(stem, out var s)) return s;
        s = null;
        if (Texture(stem)?.GetImage() is { } img)
        {
            if (img.IsCompressed()) img.Decompress();
            var px = new List<(int, int, Color)>();
            for (int y = 0; y < img.GetHeight(); y++)
                for (int x = 0; x < img.GetWidth(); x++)
                    if (img.GetPixel(x, y) is { A: > 0.5f } c) px.Add((x, y, c));
            s = (img.GetWidth(), img.GetHeight(), px);
        }
        return _sprites[stem] = s;
    }

    private void DrawSprite(CanvasItem ci, string stem, int x, int y)
    {
        if (Sprite(stem) is not { } s) return;
        foreach (var (px, py, c) in s.Px) ci.DrawRect(new Rect2(x + px, y + py, 1, 1), c);
    }

    /// <summary>The nearest of entries 0..254 by squared distance, the first on a tie.</summary>
    private Color Nearest(float r, float g, float b)
    {
        int R = Math.Clamp((int)(r * 255f), 0, 255), G = Math.Clamp((int)(g * 255f), 0, 255), B = Math.Clamp((int)(b * 255f), 0, 255);
        if (Palette is not { } pal) return Color.Color8((byte)R, (byte)G, (byte)B);
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < 255; i++)
        {
            var (pr, pg, pb) = pal[i];
            int d = (R - pr) * (R - pr) + (G - pg) * (G - pg) + (B - pb) * (B - pb);
            if (d < bestD) { bestD = d; best = i; }
        }
        var (er, eg, eb) = pal[best];
        return Color.Color8(er, eg, eb);
    }

    /// <summary>Draws the box for <paramref name="t"/> on <paramref name="ci"/>; <paramref name="health"/> is the bar's 0–1 measure.</summary>
    public void Draw(Control ci, Camera3D cam, Vehicle t, float health, bool mirror = false)
    {
        if (_bands is null)
        {
            // the four colours the original builds: (209,0,41), (204,199,34), (82,156,82), white
            _bands = [Nearest(0.81960785f, 0, 0.16078432f), Nearest(0.8f, 0.78039217f, 0.13333334f), Nearest(0.32156864f, 0.6117647f, 0.32156864f), Nearest(1, 1, 1)];
            _frame = Palette is { } p ? Color.Color8(p[0xfe].R, p[0xfe].G, p[0xfe].B) : Color.Color8(16, 16, 16);
        }
        var size = ci.GetViewportRect().Size;
        // the mirror's own bitmap is drawn pixel for pixel, 256x64; its shader flips it left-right, so the sprites go in pre-flipped
        // and at the mirrored x (the original draws them into the already mirrored picture: the final face is what counts)
        float k = mirror ? 1 : size.Y / 480f;
        // the 640x480 frame's pixels across the whole window, origin top-left
        if (mirror) ci.DrawSetTransform(new Vector2(size.X, 0), 0, new Vector2(-1, 1));
        else ci.DrawSetTransform(Vector2.Zero, 0, new Vector2(k, k));
        float vw = size.X / k;
        float vh = mirror ? size.Y : 480;
        int left = 0, top = 0, right = (int)vw - 1, bottom = (int)vh - 1;

        // The collider record's centre in the world, on the car as drawn
        var (centre, r) = Combat.Collider(t);
        var mid = new System.Numerics.Vector3((t.BoxX[0] + t.BoxX[3]) / 2, (t.BoxY[1] + t.BoxY[3]) / 2, (t.BoxZ[0] + t.BoxZ[3]) / 2);
        var at = CarOf?.Invoke(t) is { } n && GodotObject.IsInstanceValid(n) ? n.GlobalTransform * Coords.ToGodot(mid) : Coords.ToGodot(centre - Origin());
        var local = cam.GlobalTransform.AffineInverse() * at;
        float px = local.X, py = local.Y, z = -local.Z;
        // cam.sx: 320 at 640x480 for the engine's π/2 across a 4:3 view (73.74° high)
        float half = Mathf.DegToRad(cam.Fov) / 2;
        float f = cam.KeepAspect == Camera3D.KeepAspectEnum.Width ? vw / 2 / Mathf.Tan(half) : vh / 2 / Mathf.Tan(half);
        float cx = vw / 2, cy = vh / 2;

        // S = clamp(|trunc(R·sx/z)|, 23, 150)
        double sRaw = z != 0 ? Math.Abs(Math.Truncate((double)r * f / z)) : MaxBox;
        int S = (int)Math.Clamp(sRaw, MinBox, MaxBox);

        // Project, put a target behind at the side its x is on, clamp to the view; any of that = off screen
        bool off = false;
        int x = z != 0 ? Trunc(f * px / z + cx) : 0, y = z != 0 ? Trunc(cy - f * py / z) : 0;
        if (mirror && z > 0) x = (int)vw - 1 - x;   // the final face's x
        if (z <= 0) { x = px >= 0 ? right : left; off = true; }
        if (x < left) { x = left; off = true; }
        if (x > right) { x = right; off = true; }
        if (y < top) { y = top; off = true; }
        if (y > bottom) { y = bottom; off = true; }

        if (off && mirror) { ci.DrawSetTransform(Vector2.Zero, 0, Vector2.One); return; }   // no starburst in the mirror (the mirror view)
        if (off)
        {
            // the main view's starburst, ztarg902 (16x16 at 640x480), centred on the clamped point
            if (Sprite("ztarg902") is { } st) DrawSprite(ci, "ztarg902", x - st.W / 2, y - st.H / 2);
            ci.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            return;
        }

        int team = TeamArt[Combat.Team(t) & 7];
        int a = (int)(S * (double)0.6f), c = (int)(S * (double)0.4f);
        int y2 = y + (int)(c * (double)(-1f / 3f));
        int h = Sprite($"ztarg{team}01")?.H ?? 8, w2 = Sprite($"ztarg{team}02")?.W ?? 8, w4 = Sprite($"ztarg{team}04")?.W ?? 8;
        DrawSprite(ci, $"ztarg{team}01", x - a, y2 + c + h);
        DrawSprite(ci, $"ztarg{team}02", x + a - w2, y2 + c + (Sprite($"ztarg{team}02")?.H ?? 8));
        DrawSprite(ci, $"ztarg{team}03", x - a, y2 - c);
        DrawSprite(ci, $"ztarg{team}04", x + a - w4, y2 - c);

        // the bar: hp = clamp(%, 0.1, 100), white whole, else red/yellow/green by thirds; width trunc(30·√(hp/100) + 0.5)
        float hp = Math.Clamp(health * 100f, 0.1f, 100f);
        int band = hp == 100f ? 3 : hp <= 33.333332f ? 0 : hp <= 66.666664f ? 1 : 2;
        int bw = (int)(30 * Math.Sqrt(hp * 0.01) + 0.5);
        int x0 = x - (bw >> 1), y0 = y2 - c - 5;
        // the original fills (x, y)..(x + w, y + h): the 0xfe frame shows on the top and left only
        if (bw - 1 > 0) ci.DrawRect(new Rect2(x0, y0, bw - 1, 4), _frame);
        if (bw - 2 > 0) ci.DrawRect(new Rect2(x0 + 1, y0 + 1, bw - 2, 3), _bands[band]);
        ci.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private static int Trunc(float v) => float.IsFinite(v) ? (int)Math.Clamp(Math.Truncate(v), -1e8, 1e8) : v > 0 ? 100_000_000 : -100_000_000;
}
