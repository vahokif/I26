using Godot;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>
/// A column of the editor's widgets that scrolls when it is taller than the window leaves room for: as tall as its contents up to
/// the window's height less <see cref="Reserve"/>, then a scroll bar in the editor's style (the wheel over it scrolls, a drag on the
/// bar too). The tool column, and the panels at the right, are made of one each: at 1280x720 and 1024x768 the column is taller than
/// the window.
/// </summary>
public partial class FitScroll : ScrollContainer
{
    /// <summary>The window's height not available to the column (its panel's offset from the top, its margins, the status bar), pixels.</summary>
    public float Reserve { get; set; }
    private readonly Control _content;

    public FitScroll(Control content, float s, float reserve)
    {
        _content = content;
        Reserve = reserve;
        HorizontalScrollMode = ScrollMode.Disabled;
        VerticalScrollMode = ScrollMode.Auto;
        FollowFocus = true;
        // a little room between the widgets and the bar
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_right", (int)(4 * s));
        pad.AddChild(content);
        AddChild(pad);
        var bar = GetVScrollBar();
        var track = EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.7f), 4, s);
        track.ContentMarginLeft = track.ContentMarginRight = 4 * s;
        bar.AddThemeStyleboxOverride("scroll", track);
        bar.AddThemeStyleboxOverride("scroll_focus", track);
        bar.AddThemeStyleboxOverride("grabber", EditorStyle.Flat(new Color(HudStyle.Orange, 0.85f), 4, s));
        bar.AddThemeStyleboxOverride("grabber_highlight", EditorStyle.Flat(HudStyle.Amber, 4, s));
        bar.AddThemeStyleboxOverride("grabber_pressed", EditorStyle.Flat(HudStyle.Amber, 4, s));
        bar.MouseDefaultCursorShape = CursorShape.PointingHand;
        content.MinimumSizeChanged += Fit;
    }

    public override void _Ready()
    {
        GetViewport().SizeChanged += Fit;
        Fit();
    }

    public override void _ExitTree()
    {
        if (GetViewport() is { } vp) vp.SizeChanged -= Fit;
    }

    /// <summary>The height that fits: the contents', or the room the window has.</summary>
    public void Fit()
    {
        if (!IsInsideTree()) return;
        float room = MathF.Max(80, GetViewport().GetVisibleRect().Size.Y - Reserve);
        CustomMinimumSize = new Vector2(0, MathF.Min(_content.GetCombinedMinimumSize().Y, room));
    }

    /// <summary>Scrolls a widget of the column into view (a tool chosen by its key while out of sight).</summary>
    public void Show(Control c)
    {
        if (!IsInsideTree() || !IsAncestorOf(c)) return;
        Callable.From(() => { if (IsInstanceValid(c) && c.IsInsideTree()) EnsureControlVisible(c); }).CallDeferred();
    }
}
