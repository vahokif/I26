using Godot;

namespace I76.Game.Ui;

/// <summary>
/// The target's health bar as the original draws it over the target box, shared by both looks. In the 640x480 frame's
/// pixels: the health counted as clamp(%, 0.1, 100); the width trunc(30·√(hp/100) + 0.5), so it shrinks as the car is hurt;
/// an outer quad (w - 1)x4 and an inner (w - 2)x3 one pixel in from its top left, the outer's top 5 pixels over the box,
/// centred on the box's x. White unhurt, else red, yellow or green by thirds.
/// </summary>
public static class TargetHealthBar
{
    /// <summary>The health the bar counts, from the 0-1 fraction: clamp(%, 0.1, 100).</summary>
    public static float Percent(float health) => Math.Clamp(health * 100f, 0.1f, 100f);

    /// <summary>The colour band: 0 red, 1 yellow, 2 green, 3 white (whole).</summary>
    public static int Band(float percent) => percent == 100f ? 3 : percent <= 33.333332f ? 0 : percent <= 66.666664f ? 1 : 2;

    /// <summary>The bar's width in the 640x480 frame's pixels.</summary>
    public static int Width(float percent) => (int)(30 * Math.Sqrt(percent * 0.01) + 0.5);

    /// <summary>
    /// The outer and inner quads for a box centred at <paramref name="x"/> whose top is at <paramref name="boxTop"/>, both in
    /// window pixels, with <paramref name="scale"/> window pixels to the frame's pixel (window height / 480). A quad with no
    /// width (a nearly dead car's inner one) comes back empty.
    /// </summary>
    public static (Rect2 Outer, Rect2 Inner) Rects(float percent, float x, float boxTop, float scale)
    {
        int w = Width(percent);
        float x0 = x - (w >> 1) * scale, y0 = boxTop - 5 * scale;
        var outer = w - 1 > 0 ? new Rect2(x0, y0, (w - 1) * scale, 4 * scale) : new Rect2(x0, y0, 0, 0);
        var inner = w - 2 > 0 ? new Rect2(x0 + scale, y0 + scale, (w - 2) * scale, 3 * scale) : new Rect2(x0 + scale, y0 + scale, 0, 0);
        return (outer, inner);
    }
}
