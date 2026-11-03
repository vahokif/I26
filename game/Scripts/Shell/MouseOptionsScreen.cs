using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Sim.Views;

namespace I76.Game.Shell;

/// <summary>
/// The port's Mouse Options screen, opened from <see cref="OptionsScreen"/>, <see cref="ControlsScreen"/>, or the in-mission
/// <see cref="I76.Game.Ui.EscapeMenu"/>: uses Control Configuration's two-column ruled table (record 16's frame, column dividers, row rules
/// and button slots). The left table holds Mouse Camera (On/Off), Mouse Sensitivity (10% to 200% in 10% steps), Invert Y Axis,
/// Cockpit Mouse Look (3D cockpit free look), Camera Return, View Zoom Reset, and Mouse In Window; the right table holds the mouse
/// buttons (Mouse1Btn..Mouse5Btn) and the wheel zoom row. Done saves and applies; Cancel and Esc restore.
/// </summary>
public sealed class MouseOptionsScreen : OverlayScreen
{
    protected override int Record => 16;

    /// <summary>When opened from <see cref="ControlsScreen"/>, Done and Cancel return there instead of <see cref="Shell.Options"/>.</summary>
    public bool ReturnToControls { get; init; }
    private readonly BindingTable? _given;
    private readonly BindingTable _map;

    public MouseOptionsScreen(BindingTable? pending = null)
    {
        _given = pending;
        _map = (pending ?? Controls.Edit()).Clone();
    }

    private ShellFont _f = null!;
    private Control? _layer;
    private bool _loading;
    private int _sel;
    private readonly PadMenu _menu = new();

    private int _savedMouse;
    private float _savedSens;
    private bool _savedInvert, _savedCockpit, _savedWheelCycle, _savedReturn, _savedZoomReset, _savedConfine;

    internal const string Title = "MOUSE OPTIONS";
    internal const int LeftCount = 7;
    internal const int ButtonCount = 5;
    internal const int WheelRow = LeftCount + ButtonCount;
    internal const int TotalRows = LeftCount + ButtonCount + 1;
    private const int RowH = ControllerScreen.RowH;
    private const int NameW = ControlsScreen.NameW, LeftValW = ControlsScreen.LeftBindW, RightValW = ControlsScreen.RightBindW;
    internal static readonly string[] SlotText = ["", "RESTORE DEFAULTS", "DONE", "CANCEL"];

    private static readonly (string Label, string Channel)[] MouseButtons =
    [
        ("Left Button", "LeftBtn"),
        ("Right Button", "RightBtn"),
        ("Middle Button", "MiddleBtn"),
        ("Side Button 1", "Mouse4Btn"),
        ("Side Button 2", "Mouse5Btn"),
    ];

    private static readonly int[] Entries = [.. ControlsScreen.LeftRows, .. ControlsScreen.RightRows];
    private static string NameOf(int entry) => ControlsScreen.Names[System.Array.IndexOf(Entries, entry)].Text;

    internal int Selected => _sel;

    /// <summary>The settings this screen edits, copied from one settings object to another (the in-mission menu takes them back from the screen's shell).</summary>
    public static void CopyMouse(ShellSettings from, ShellSettings to)
    {
        to.MouseCamera = from.MouseCamera;
        to.MouseSensitivity = from.MouseSensitivity;
        to.MouseInvertY = from.MouseInvertY;
        to.MouseCockpit = from.MouseCockpit;
        to.WheelCycleWeapon = from.WheelCycleWeapon;
        to.CameraReturn = from.CameraReturn;
        to.ZoomReset = from.ZoomReset;
        to.KeepMouseInWindow = from.KeepMouseInWindow;
    }

    public override void Enter()
    {
        var st = Shell.Settings;
        _savedMouse = st.MouseCamera;
        _savedSens = st.MouseSensitivity;
        _savedInvert = st.MouseInvertY;
        _savedCockpit = st.MouseCockpit;
        _savedWheelCycle = st.WheelCycleWeapon;
        _savedReturn = st.CameraReturn;
        _savedZoomReset = st.ZoomReset;
        _savedConfine = st.KeepMouseInWindow;

        Label = Shell.Db.Font(0x46);
        Small = Shell.Db.Font(0x47);
        _f = Shell.Db.Font(0x44);
        if (!Shell.Faithful)
        {
            _layer = new MouseSheet { Screen = this };
            Shell.AddLayerOverFrame(_layer);
        }
        _loading = true;
        _menu.Settle();
        Build();
    }

    public override void Leave()
    {
        _layer?.QueueFree();
        _layer = null;
    }

    protected override void Build()
    {
        for (int i = 0; i < LeftCount; i++)
        {
            int k = i;
            Add(30, ControllerScreen.RowY(k) + 1, () => "", () => { _sel = k; Nudge(k, 1, true); }, _f, w: 282, h: 16);
        }
        for (int i = 0; i < ButtonCount; i++)
        {
            int k = i;
            Add(330, ControllerScreen.RowY(k) + 1, () => "", () => { _sel = LeftCount + k; OpenButton(k, false); }, _f, w: 282, h: 16);
        }
        Add(330, ControllerScreen.RowY(ButtonCount) + 1, () => "", () => { _sel = WheelRow; ToggleWheel(); }, _f, w: 282, h: 16);
        var slots = ControllerScreen.Slots;
        Add(slots[1].Position.X, slots[1].Position.Y, () => "", RestoreDefaults, w: slots[1].Size.X, h: slots[1].Size.Y);
        Add(slots[2].Position.X, slots[2].Position.Y, () => "", Done, w: slots[2].Size.X, h: slots[2].Size.Y);
        Add(slots[3].Position.X, slots[3].Position.Y, () => "", Back, w: slots[3].Size.X, h: slots[3].Size.Y);
    }

    private void RestoreDefaults()
    {
        Shell.Sound(82);
        var st = Shell.Settings;
        st.MouseCamera = MouseLook.Normal;
        st.MouseSensitivity = MouseLook.DefaultSensitivity;
        st.MouseInvertY = false;
        st.MouseCockpit = false;
        st.WheelCycleWeapon = false;
        st.CameraReturn = true;
        st.ZoomReset = true;
        st.KeepMouseInWindow = false;
        MouseCamera.Apply(st);
        // restore default mouse button bindings (LeftBtn, RightBtn, MiddleBtn) and clear Mouse4Btn, Mouse5Btn
        var def = Controls.KeyboardDefaults();
        foreach (var (_, ch) in MouseButtons) ClearMouseChannel(ch);
        foreach (var entry in Entries)
        {
            var cmd = Controls.Commands[entry];
            foreach (var b in def.Of(cmd))
                if (!BindingTable.IsPad(b) && b.Terms.Length > 0 && b.Terms[0].Device.Equals("Mouse", System.StringComparison.OrdinalIgnoreCase))
                    _map.Assign(cmd, "Mouse", b.Terms[0].Channel);
        }
        Redraw();
    }

    private void Nudge(int row, int dir, bool wrap = false)
    {
        if (row < 0 || row >= LeftCount) return;
        Shell.Sound(82);
        var st = Shell.Settings;
        switch (row)
        {
            case 0:
                bool on = st.MouseCamera == MouseLook.Off;
                st.MouseCamera = MouseLook.LevelFromSensitivity(on, st.MouseSensitivity);
                break;
            case 1:
                st.MouseSensitivity = MouseLook.NextSensitivity(st.MouseSensitivity, dir, wrap);
                if (st.MouseCamera != MouseLook.Off)
                    st.MouseCamera = MouseLook.LevelFromSensitivity(true, st.MouseSensitivity);
                break;
            case 2:
                st.MouseInvertY = !st.MouseInvertY;
                break;
            case 3:
                st.MouseCockpit = !st.MouseCockpit;
                break;
            case 4:
                st.CameraReturn = !st.CameraReturn;
                break;
            case 5:
                st.ZoomReset = !st.ZoomReset;
                break;
            case 6:
                st.KeepMouseInWindow = !st.KeepMouseInWindow;
                break;
        }
        MouseCamera.Apply(st);
    }

    private void ToggleWheel()
    {
        Shell.Sound(82);
        var st = Shell.Settings;
        st.WheelCycleWeapon = !st.WheelCycleWeapon;
        MouseCamera.Apply(st);
        Redraw();
    }

    private string ActionOfButton(string channel)
    {
        foreach (var entry in Entries)
        {
            var cmd = Controls.Commands[entry];
            foreach (var b in _map.Of(cmd))
                if (!BindingTable.IsPad(b) && b.Terms.Length > 0
                    && b.Terms[0].Device.Equals("mouse", System.StringComparison.OrdinalIgnoreCase)
                    && b.Terms[0].Channel.Equals(channel, System.StringComparison.OrdinalIgnoreCase))
                    return NameOf(entry);
        }
        return BindingTable.None;
    }

    private void ClearMouseChannel(string channel)
    {
        foreach (var entry in Entries)
        {
            var cmd = Controls.Commands[entry];
            if (_map.Of(cmd).Any(b => !BindingTable.IsPad(b) && b.Terms.Length > 0
                && b.Terms[0].Device.Equals("mouse", System.StringComparison.OrdinalIgnoreCase)
                && b.Terms[0].Channel.Equals(channel, System.StringComparison.OrdinalIgnoreCase)))
                _map.ClearOn(cmd, false);
        }
    }

    private void OpenButton(int btnIndex, bool byPad)
    {
        if (btnIndex < 0 || btnIndex >= ButtonCount) return;
        string channel = MouseButtons[btnIndex].Channel;
        var items = new List<string> { "(none)" };
        foreach (var n in Entries) items.Add(NameOf(n));
        var s = Shell.Db.Bank(0x24)[16];
        int w = s?.Width ?? 172, h = s?.Height ?? 235;
        Shell.Modal(new ListPopup
        {
            Sprite = 16, X = 320 - w / 2, Y = 240 - h / 2, TextX = 0x16, TextY = 0x30, Visible = 9, Items = items, Font = _f,
            Up = (18, 36, 12, 25), Down = (18, 36, 204, 217), List = (16, 214, 47, 198), Cancel = (88, 159, 209, 225),
            PadPicks = true, CursorShown = byPad,
            Picked = i =>
            {
                _menu.Settle();
                ClearMouseChannel(channel);
                if (i > 0) _map.Assign(Controls.Commands[Entries[i - 1]], "Mouse", channel);
                Redraw();
            },
        });
    }

    private static (string Name, string Value) LeftRow(ShellSettings st, int row) => row switch
    {
        0 => ("Mouse Camera", st.MouseCamera != MouseLook.Off ? "On" : "Off"),
        1 => ("Mouse Sensitivity", $"{(int)System.MathF.Round(MouseLook.ClampSensitivity(st.MouseSensitivity) * 100f)}%"),
        2 => ("Invert Y Axis", st.MouseInvertY ? "On" : "Off"),
        3 => ("Cockpit Mouse Look", st.MouseCockpit ? "On" : "Off"),
        4 => ("Camera Return", st.CameraReturn ? "On" : "Off"),
        5 => ("View Zoom Reset", st.ZoomReset ? "On" : "Off"),
        _ => ("Mouse In Window", st.KeepMouseInWindow ? "On" : "Off"),
    };

    private string Fit(string s, int max)
    {
        if (_f.Measure(s) <= max) return s;
        s = s.Replace(" / ", "/");
        if (_f.Measure(s) <= max) return s;
        while (s.Length > 1 && _f.Measure(s + "...") > max) s = s[..^1];
        return s + "...";
    }

    private void Close(BindingTable? handBack)
    {
        if (ReturnToControls) Shell.Overlay(new ControlsScreen(handBack));
        else Shell.Options();
    }

    private void Done()
    {
        Shell.Settings.Save();
        MouseCamera.Apply(Shell.Settings);
        if (!ReturnToControls) Controls.Save(_map);
        Close(_map);
    }

    private void Back()
    {
        var st = Shell.Settings;
        st.MouseCamera = _savedMouse;
        st.MouseSensitivity = _savedSens;
        st.MouseInvertY = _savedInvert;
        st.MouseCockpit = _savedCockpit;
        st.WheelCycleWeapon = _savedWheelCycle;
        st.CameraReturn = _savedReturn;
        st.ZoomReset = _savedZoomReset;
        st.KeepMouseInWindow = _savedConfine;
        MouseCamera.Apply(st);
        Close(_given);
    }

    protected override void Escape() => Back();

    private static byte[] Solid(ShellFont f, byte ink)
    {
        var r = new byte[256];
        System.Array.Fill(r, ink);
        r[f.Key & 0xff] = 0xff;
        return r;
    }

    private void DrawSheet1997()
    {
        var title = Shell.Db.Font(0x43);
        var ink = Solid(title, Frame.Nearest(ControllerScreen.TitleInk));
        int x = 320 - title.Measure(Title) / 2;
        Frame.Text(title, x, 12, Title, ink);
        Frame.Text(title, x + 1, 12, Title, ink);
        var small = Shell.Db.Font(0x47);
        var dark = Solid(small, Frame.Nearest(ControllerScreen.TitleInk));
        for (int i = 1; i < ControllerScreen.Slots.Length; i++)
        {
            var s = ControllerScreen.Slots[i];
            string text = ControllerScreen.Fit(SlotText[i], t => small.Measure(t) + 1, s.Size.X - 8);
            int tx = s.Position.X + (s.Size.X - small.Measure(text)) / 2, ty = s.Position.Y + (s.Size.Y - small.Height) / 2 + 1;
            Frame.Text(small, tx, ty, text, dark);
            Frame.Text(small, tx + 1, ty, text, dark);
        }
    }

    protected override void DrawExtra()
    {
        if (Shell.Faithful) DrawSheet1997();
        _layer?.QueueRedraw();

        var st = Shell.Settings;
        for (int i = 0; i < LeftCount; i++)
        {
            var (name, val) = LeftRow(st, i);
            int y = ControllerScreen.RowY(i) + 3;
            Frame.Text(_f, 40, y, Fit(name, NameW), ShellFrame.OverlayInk);
            Frame.Text(_f, 190, y, Fit(val, LeftValW), ShellFrame.OverlayInk);
        }

        string[] hintName = ["Left-click:", "Right-click:"];
        string[] hintDo = ["next / assign", "prev / clear"];
        for (int k = 0; k < 2; k++)
        {
            int y = ControllerScreen.RowY(19 + k) + 3;
            Frame.Text(_f, 40, y, Fit(hintName[k], NameW), ShellFrame.OverlayInk);
            Frame.Text(_f, 190, y, Fit(hintDo[k], LeftValW), ShellFrame.OverlayInk);
        }

        for (int i = 0; i < ButtonCount; i++)
        {
            var (label, ch) = MouseButtons[i];
            int y = ControllerScreen.RowY(i) + 3;
            Frame.Text(_f, 338, y, Fit(label, NameW), ShellFrame.OverlayInk);
            Frame.Text(_f, 486, y, Fit(ActionOfButton(ch), RightValW), ShellFrame.OverlayInk);
        }
        int wheelY = ControllerScreen.RowY(ButtonCount) + 3;
        Frame.Text(_f, 338, wheelY, Fit("Wheel Up / Down", NameW), ShellFrame.OverlayInk);
        Frame.Text(_f, 486, wheelY, Fit(st.WheelCycleWeapon ? "Cycle Weapon" : "View Zoom", RightValW), ShellFrame.OverlayInk);
    }

    private void StepRow(int d)
    {
        _sel = (_sel + d + TotalRows) % TotalRows;
        Shell.Sound(82);
        Redraw();
    }

    public override void Tick()
    {
        if (_loading)
        {
            _loading = false;
            Frame.LoadBackground(PaperSheet.Make(Shell.Db.Background(Record), Shell.Faithful, 1, table: true), 0, 0, black: false);
            Redraw();
            return;
        }
        var pads = Controls.PadSource.Read();
        var act = _menu.Update(pads.Count > 0 && Controls.Focused() ? pads[0] : default);
        if ((act & PadMenu.Act.Up) != 0) StepRow(-1);
        if ((act & PadMenu.Act.Down) != 0) StepRow(1);
        if ((act & PadMenu.Act.Left) != 0)
        {
            if (_sel < LeftCount) { Nudge(_sel, -1); Redraw(); }
            else if (_sel == WheelRow) ToggleWheel();
        }
        if ((act & PadMenu.Act.Right) != 0)
        {
            if (_sel < LeftCount) { Nudge(_sel, 1); Redraw(); }
            else if (_sel == WheelRow) ToggleWheel();
        }
        if ((act & PadMenu.Act.Open) != 0)
        {
            if (_sel < LeftCount) { Nudge(_sel, 1, true); Redraw(); }
            else if (_sel == WheelRow) ToggleWheel();
            else { Shell.Sound(82); OpenButton(_sel - LeftCount, true); return; }
        }
        if ((act & PadMenu.Act.Back) != 0) { Back(); return; }
        if ((act & PadMenu.Act.Done) != 0) { Done(); return; }
        foreach (var k in Shell.Keys)
        {
            if (k is Key.Up) StepRow(-1);
            else if (k is Key.Down) StepRow(1);
            else if (k is Key.Left)
            {
                if (_sel < LeftCount) { Nudge(_sel, -1); Redraw(); }
                else if (_sel == WheelRow) ToggleWheel();
            }
            else if (k is Key.Right)
            {
                if (_sel < LeftCount) { Nudge(_sel, 1); Redraw(); }
                else if (_sel == WheelRow) ToggleWheel();
            }
            else if (k is Key.Enter or Key.Space or Key.KpEnter)
            {
                if (_sel < LeftCount) { Nudge(_sel, 1, true); Redraw(); }
                else if (_sel == WheelRow) ToggleWheel();
                else { Shell.Sound(82); OpenButton(_sel - LeftCount, false); return; }
            }
            else if ((k is Key.Delete or Key.Backspace) && _sel >= LeftCount && _sel < WheelRow)
            {
                Shell.Sound(82);
                ClearMouseChannel(MouseButtons[_sel - LeftCount].Channel);
                Redraw();
            }
        }
        if (Shell.RightClicked)
        {
            for (int i = 0; i < LeftCount; i++)
                if (Items[i].Hit(Shell.Mouse)) { _sel = i; Nudge(i, -1, true); Redraw(); return; }
            for (int i = 0; i < ButtonCount; i++)
                if (Items[LeftCount + i].Hit(Shell.Mouse))
                {
                    _sel = LeftCount + i;
                    Shell.Sound(82);
                    ClearMouseChannel(MouseButtons[i].Channel);
                    Redraw();
                    return;
                }
            if (Items[WheelRow].Hit(Shell.Mouse)) { _sel = WheelRow; ToggleWheel(); return; }
        }
        base.Tick();
    }
}

/// <summary>The modern look's two-column table rules, title, button bars and selection mark for <see cref="MouseOptionsScreen"/>.</summary>
public partial class MouseSheet : Control
{
    public required MouseOptionsScreen Screen { get; init; }
    private Font? _arial;
    private float _s;
    private Vector2 _off;
    private Vector2 T(Vector2 shell) => _off + shell * _s;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        _s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        _off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * _s) / 2;
        var rng = new System.Random(16);
        foreach (var r in ControllerScreen.TableRules())
        {
            bool across = r.Size.X >= r.Size.Y;
            int len = across ? r.Size.X : r.Size.Y;
            for (int k = 0; k < len; k += 6)
            {
                int run = System.Math.Min(6, len - k);
                var at = across ? new Rect2(r.Position.X + k, r.Position.Y, run, r.Size.Y) : new Rect2(r.Position.X, r.Position.Y + k, r.Size.X, run);
                DrawRect(new Rect2(T(at.Position), at.Size * _s), new Color(ControllerScreen.RuleColor, 0.8f + 0.2f * (float)rng.NextDouble()));
            }
        }

        _arial ??= HdAssets.Arial(true, TextServer.FontAntialiasing.Gray);
        var arial = _arial;
        if (arial is null) return;
        Caps(arial, MouseOptionsScreen.Title, new Vector2(320, 33), 29f, 0.86f, ControllerScreen.TitleInk, 1.6f);
        for (int i = 1; i < ControllerScreen.Slots.Length; i++)
        {
            var b = ControllerScreen.Slots[i];
            var r = new Rect2(T(b.Position), new Vector2(b.Size.X, b.Size.Y) * _s);
            DrawRect(r, ControllerScreen.BarEdge);
            DrawRect(r.Grow(-2 * _s), ControllerScreen.BarColor);
            int fsize = System.Math.Max(1, (int)System.MathF.Round(14.6f * _s));
            var text = ControllerScreen.Fit(MouseOptionsScreen.SlotText[i], t => arial.GetStringSize(t, HorizontalAlignment.Left, -1, fsize).X / _s * 0.94f, b.Size.X - 8);
            Caps(arial, text, new Vector2(b.Position.X + b.Size.X / 2f, b.Position.Y + 15), 14.6f, 0.94f, ControllerScreen.TitleInk, 0.7f);
        }
    }

    private void Caps(Font f, string s, Vector2 at, float px, float narrow, Color c, float bold = 0)
    {
        int size = System.Math.Max(1, (int)System.MathF.Round(px * _s));
        float w = f.GetStringSize(s, HorizontalAlignment.Left, -1, size).X * narrow;
        var p = T(at);
        DrawSetTransform(new Vector2(p.X - w / 2, p.Y), 0, new Vector2(narrow, 1));
        if (bold > 0) DrawStringOutline(f, Vector2.Zero, s, HorizontalAlignment.Left, -1, size, System.Math.Max(1, (int)System.MathF.Round(bold * _s)), c);
        DrawString(f, Vector2.Zero, s, HorizontalAlignment.Left, -1, size, c);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
