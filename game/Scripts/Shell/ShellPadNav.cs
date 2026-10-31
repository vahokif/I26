using Godot;
using I76.Formats.Input;

namespace I76.Game.Shell;

/// <summary>A place on a shell screen the pad can focus: its rectangle, and optionally what a press past its edge does (a list scrolling).</summary>
public readonly record struct PadRegion(NavRect Rect, Func<NavDir, bool>? Edge = null)
{
    /// <summary>A strict hit rectangle as the screens keep them: x0 &lt; x &lt; x1, y0 &lt; y &lt; y1.</summary>
    public static PadRegion Of((int X0, int X1, int Y0, int Y1) r, Func<NavDir, bool>? edge = null) => new(new NavRect(r.X0, r.Y0, r.X1 - r.X0, r.Y1 - r.Y0), edge);
    public static PadRegion Of(int x, int y, int w, int h, Func<NavDir, bool>? edge = null) => new(new NavRect(x, y, w, h), edge);
    public static PadRegion Of(Rect2I r, Func<NavDir, bool>? edge = null) => new(new NavRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y), edge);
}

/// <summary>
/// The shell's pad navigation (the port's): the D-pad and the left stick move a focus between the current screen's
/// <see cref="ShellScreen.PadRegions"/> (the nearest in the direction pressed, repeating while held), A clicks the focused
/// region, B is Esc and so is Start. The focus is the mouse: while it shows the shell's mouse sits on the region's centre, so a
/// screen's own hover highlight lights as it does for the mouse; a screen with none gets an outline (<see cref="PadFocusLayer"/>).
/// Moving the mouse hides the focus; a pad press brings it back.
/// </summary>
public partial class Shell
{
    private readonly PadRepeater _padNav = new();
    private bool _padFocusOn;
    private NavRect? _focus;
    private ShellScreen? _padFor;
    private PadFocusLayer? _focusLayer;

    /// <summary>The focused region while the pad's focus shows, else null.</summary>
    public NavRect? PadFocus => _padFocusOn ? _focus : null;

    private void PadFocusOff() { _padFocusOn = false; }

    private static int IndexOf(IReadOnlyList<PadRegion> regions, NavRect? at)
    {
        if (at is not { } f || regions.Count == 0) return -1;
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i].Rect;
            if (r == f) return i;
            float d = (r.CenterX - f.CenterX) * (r.CenterX - f.CenterX) + (r.CenterY - f.CenterY) * (r.CenterY - f.CenterY);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private void PadNavigate(double dt)
    {
        var cur = Current;
        if (cur is { TextEditing: true } && Capture is null)
        {
            // a field has the keyboard: a pad press ends it, and the pad navigates from the next one
            var typing = Controls.PadSource.Read();
            if (_padNav.Update(typing.Count > 0 && Controls.Focused() ? typing[0] : default, dt).Any) { cur.StopEditing(); _padNav.Settle(); }
            return;
        }
        if (cur is null || Capture is not null || cur.OwnsPad || cur is ControllerScreen or JoystickScreen or PadDeadZoneScreen)
        {
            _padNav.Settle();
            _padFor = null;
            return;
        }
        var pads = Controls.PadSource.Read();
        var raw = pads.Count > 0 && Controls.Focused() ? pads[0] : default;
        if (!ReferenceEquals(cur, _padFor))
        {
            // another screen: its first region has the focus if the pad was the one in use; the button that brought us here is spent
            _padFor = cur;
            _focus = null;
            _padNav.Settle();
        }
        var st = _padNav.Update(raw, dt);
        var regions = cur.PadRegions;
        int at = IndexOf(regions, _focus);
        if (st.Any)
        {
            bool reveal = !_padFocusOn && regions.Count > 0 && (st.Dir != NavDir.None || (st.Buttons & PadRepeater.Btn.Accept) != 0);
            if (reveal)
            {
                _padFocusOn = true;
                if (at < 0) at = PadNav.Pick([.. regions.Select(r => r.Rect)], -1, NavDir.Down);
            }
            else if (st.Dir != NavDir.None && regions.Count > 0)
            {
                if (at >= 0 && regions[at].Edge?.Invoke(st.Dir) == true) { _padFocusOn = true; at = IndexOf(cur.PadRegions, _focus); }
                else
                {
                    int to = PadNav.Pick([.. regions.Select(r => r.Rect)], at, st.Dir);
                    _padFocusOn = true;
                    if (to >= 0 && to != at) { at = to; Sound(82); }
                    else if (at < 0 && to >= 0) at = to;
                }
            }
            else if ((st.Buttons & PadRepeater.Btn.Accept) != 0)
            {
                if (regions.Count == 0) _clickPending = true;          // nothing to focus (a notice, the credits): a plain click
                else if (at >= 0) { _focus = regions[at].Rect; Mouse = Centre(regions[at].Rect); _clickPending = true; }
            }
            if ((st.Buttons & PadRepeater.Btn.Back) != 0 && cur is not MainMenuScreen) _keys.Enqueue(Key.Escape);
            if ((st.Buttons & PadRepeater.Btn.Start) != 0) _keys.Enqueue(Key.Escape);
        }
        if (at >= 0) _focus = regions[at].Rect;
        else if (regions.Count == 0) _focus = null;
        if (_padFocusOn && _focus is { } f) Mouse = Centre(f);
    }

    private static Vector2I Centre(NavRect r) => new((int)r.CenterX, (int)r.CenterY);
}

/// <summary>The pad focus' outline where a screen has no hover of its own: the pencil red of the controller screen's mark, with a light edge so it reads on dark art too.</summary>
public partial class PadFocusLayer : Control
{
    public Shell Shell { get; set; } = null!;
    private NavRect? _drawn;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        NavRect? now = Shell.Current is { HoverShows: false } ? Shell.PadFocus : null;
        if (now != _drawn) { _drawn = now; QueueRedraw(); }
    }

    public override void _Draw()
    {
        if (_drawn is not { } r) return;
        var size = GetViewportRect().Size;
        float scale = Math.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        var off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * scale) / 2;
        var rect = new Rect2(off + new Vector2(r.X - 2, r.Y - 1) * scale, new Vector2(r.W + 4, r.H + 2) * scale);
        float w = Math.Max(2f, 2 * scale);
        DrawRect(rect.Grow(w * 0.75f), new Color(1, 1, 1, 0.55f), false, w * 0.9f);
        DrawRect(rect, Color.Color8(150, 36, 22), false, w);
    }
}
