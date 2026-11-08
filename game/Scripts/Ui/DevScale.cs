using Godot;

namespace I76.Game.Ui;

/// <summary>
/// Scaling for the Developer panels (in mission and on the shell's main menu): font size, margins, separations and minimum
/// sizes are multiplied by a whole factor from the window's height, so the theme re-rasterises the text sharp. 1 up to a
/// 1079-pixel window, 2 at 1080 and 1440, 3 from 2160. Sizes are tagged with their 1x value (metadata) and reapplied when the window resizes.
/// </summary>
public static class DevScale
{
    /// <summary>The panel's frame, a vertical scroll box in it and the column of rows inside that.</summary>
    public static (PanelContainer Panel, ScrollContainer Scroll, VBoxContainer Col) Frame(Theme theme, StyleBoxFlat bg)
    {
        var panel = Min(new PanelContainer { Theme = theme }, 430);
        panel.AddThemeStyleboxOverride("panel", bg);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        var col = Sep(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 6);
        scroll.AddChild(col);
        return (panel, scroll, col);
    }

    private const string MinKey = "dev_min", SepKey = "dev_sep", GridKey = "dev_grid";

    /// <summary>max(1, round(height / 720)), at most 3: 720 is 1, 1080 is 2 (1.5 rounds up), 1440 is 2, 2160 is 3.</summary>
    public static int Factor(float windowHeight) => System.Math.Clamp((int)System.Math.Round(windowHeight / 720f, System.MidpointRounding.AwayFromZero), 1, 3);

    /// <summary>A minimum size at 1x, scaled by <see cref="Apply"/>.</summary>
    public static T Min<T>(T c, float w, float h = 0) where T : Control
    {
        c.SetMeta(MinKey, new Vector2(w, h));
        c.CustomMinimumSize = new Vector2(w, h);
        return c;
    }

    /// <summary>A box's separation at 1x, scaled by <see cref="Apply"/>.</summary>
    public static T Sep<T>(T c, int sep) where T : BoxContainer
    {
        c.SetMeta(SepKey, sep);
        c.AddThemeConstantOverride("separation", sep);
        return c;
    }

    /// <summary>A grid's column and row separations at 1x, scaled by <see cref="Apply"/>.</summary>
    public static GridContainer Grid(GridContainer g, int h, int v)
    {
        g.SetMeta(GridKey, new Vector2I(h, v));
        g.AddThemeConstantOverride("h_separation", h);
        g.AddThemeConstantOverride("v_separation", v);
        return g;
    }

    /// <summary>
    /// Sets the panel's theme, frame and every tagged control below it for the factor f. <paramref name="at"/>, when given, places
    /// the panel once it has its size (a second column beside the first, or against the window's right edge); else it is at the top left.
    /// </summary>
    public static void Apply(PanelContainer panel, ScrollContainer scroll, Control content, Theme theme, StyleBoxFlat bg, int f, System.Func<Vector2>? at = null)
    {
        theme.DefaultFontSize = 15 * f;
        bg.SetBorderWidthAll(2 * f);
        bg.SetContentMarginAll(12 * f);
        bg.SetCornerRadiusAll(4 * f);
        panel.Position = new Vector2(16, 16) * f;
        Walk(panel, f);
        // the panel is taller than a 720-pixel window at 1x, so it scrolls instead of running off the bottom
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            float avail = panel.GetViewportRect().Size.Y - 2 * 16 * f - 2 * 12 * f - 4 * f;
            scroll.CustomMinimumSize = new Vector2(0, System.Math.Min(content.GetCombinedMinimumSize().Y, avail));
            panel.ResetSize();
            if (at is not null) panel.Position = at();
        }).CallDeferred();
    }

    private static void Walk(Node n, int f)
    {
        if (n is Control c)
        {
            if (c.HasMeta(MinKey)) c.CustomMinimumSize = c.GetMeta(MinKey).AsVector2() * f;
            if (c.HasMeta(SepKey)) c.AddThemeConstantOverride("separation", c.GetMeta(SepKey).AsInt32() * f);
            if (c.HasMeta(GridKey))
            {
                var g = c.GetMeta(GridKey).AsVector2I();
                c.AddThemeConstantOverride("h_separation", g.X * f);
                c.AddThemeConstantOverride("v_separation", g.Y * f);
            }
        }
        foreach (var k in n.GetChildren()) Walk(k, f);
    }
}
