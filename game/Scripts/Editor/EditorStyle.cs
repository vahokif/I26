using Godot;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>The editor's widgets in the modern HUD's style (HudStyle): dark brown panels with a faint edge, parchment text, amber for what is on.</summary>
public static class EditorStyle
{
    public static StyleBoxFlat Flat(Color bg, float radius, float s, Color? border = null, float margin = 0)
    {
        var b = new StyleBoxFlat { BgColor = bg, AntiAliasing = true };
        b.SetCornerRadiusAll((int)Math.Round(radius * s));
        if (border is { } c) { b.BorderColor = c; b.SetBorderWidthAll(Math.Max(1, (int)Math.Round(s))); }
        if (margin > 0) b.SetContentMarginAll(margin * s);
        return b;
    }

    public static StyleBoxFlat Panel(float s) => Flat(HudStyle.Panel, 10, s, HudStyle.Edge, 14);

    public static void Title(Label l, float size)
    {
        l.AddThemeFontOverride("font", HudStyle.Title);
        l.AddThemeFontSizeOverride("font_size", (int)size);
        l.AddThemeColorOverride("font_color", HudStyle.Amber);
        l.AddThemeColorOverride("font_shadow_color", new Color(HudStyle.Ink, 0.85f));
        l.AddThemeConstantOverride("shadow_offset_x", 2);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
    }

    public static void Body(Label l, float size, bool dim = false, Color? colour = null)
    {
        l.AddThemeFontOverride("font", HudStyle.Bold);
        l.AddThemeFontSizeOverride("font_size", (int)size);
        l.AddThemeColorOverride("font_color", colour ?? (dim ? HudStyle.ParchmentDim : HudStyle.Parchment));
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    public static void Style(Button b, float s, float size)
    {
        b.FocusMode = Control.FocusModeEnum.None;
        b.AddThemeFontOverride("font", HudStyle.Bold);
        b.AddThemeFontSizeOverride("font_size", (int)(size * s * 0.8f));
        var normal = Flat(new Color(0.21f, 0.12f, 0.07f, 0.92f), 6, s, new Color(HudStyle.Edge, 0.25f), 8);
        normal.ContentMarginLeft = normal.ContentMarginRight = 12 * s;
        var hover = Flat(new Color(0.34f, 0.18f, 0.09f, 0.96f), 6, s, new Color(HudStyle.Amber, 0.55f), 8);
        hover.ContentMarginLeft = hover.ContentMarginRight = 12 * s;
        var pressed = Flat(HudStyle.Amber, 6, s, HudStyle.Orange, 8);
        pressed.ContentMarginLeft = pressed.ContentMarginRight = 12 * s;
        var disabled = Flat(new Color(0.15f, 0.1f, 0.07f, 0.7f), 6, s, null, 8);
        disabled.ContentMarginLeft = disabled.ContentMarginRight = 12 * s;
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("hover_pressed", pressed);
        b.AddThemeStyleboxOverride("disabled", disabled);
        b.AddThemeColorOverride("font_color", HudStyle.Parchment);
        b.AddThemeColorOverride("font_hover_color", HudStyle.Amber);
        b.AddThemeColorOverride("font_pressed_color", HudStyle.Ink);
        b.AddThemeColorOverride("font_hover_pressed_color", HudStyle.Ink);
        b.AddThemeColorOverride("font_disabled_color", new Color(HudStyle.ParchmentDim, 0.45f));
    }

    public static Button Button(string text, Action onPress, float s, float size = 20, bool toggle = false)
    {
        var b = new Button { Text = text, ToggleMode = toggle, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        Style(b, s, size);
        b.Pressed += () => onPress();
        return b;
    }

    public static void Slider(HSlider sl, float s)
    {
        sl.FocusMode = Control.FocusModeEnum.None;
        sl.AddThemeStyleboxOverride("slider", Flat(new Color(0.3f, 0.18f, 0.1f, 0.9f), 3, s, null, 3));
        sl.AddThemeStyleboxOverride("grabber_area", Flat(HudStyle.Orange, 3, s, null, 3));
        sl.AddThemeStyleboxOverride("grabber_area_highlight", Flat(HudStyle.Amber, 3, s, null, 3));
        sl.CustomMinimumSize = new Vector2(0, 22 * s);
    }
}
