using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The game pad's dead zones (docs/joystick.md), opened from <see cref="ControllerScreen"/>'s first button: Left Stick, Right Stick and
/// Trigger Dead Zone, each 0 to 50 % in 5 % steps (a click raises it and wraps, a right click lowers it; Left and Right on the keys or the
/// D-pad). Under them the sticks and triggers live: a dot where the pad says the stick is, a ring the size of its dead zone, and a red dot
/// where the game then takes it, so a worn stick's drift shows as a dot off centre and the zone to raise until the red dot stays home.
/// A change is kept at once; Cancel and Esc put the zones back as they were when the screen opened. Done hands the bindings back to the
/// controller screen. Drawn in the shell's pixels in both looks, on the controller sheet.
/// </summary>
public sealed class PadDeadZoneScreen : OverlayScreen
{
    protected override int Record => 16;
    private readonly BindingTable _map;
    private readonly PadDeadZones _entered;
    private ShellFont _f = null!;
    private bool _loading;
    private int _sel;
    private readonly PadMenu _menu = new();
    private string _lastLive = "";

    private const string Title = "PAD DEAD ZONES";
    private static readonly string[] SlotText = ["", "RESTORE DEFAULTS", "DONE", "CANCEL"];
    private static readonly string[] Names = ["Left Stick Dead Zone", "Right Stick Dead Zone", "Trigger Dead Zone"];
    // the gauges: stick centres, their radius for full deflection, and the trigger bars under them
    private const int GaugeR = 56, GaugeY = 236, LeftCx = 245, RightCx = 395, BarY = 334, BarW = 100, BarH = 8;

    public PadDeadZoneScreen(BindingTable map)
    {
        _map = map;
        _entered = GodotPads.Zones;
    }

    private static float Get(int i) => i switch { 0 => GodotPads.Zones.Left, 1 => GodotPads.Zones.Right, _ => GodotPads.Zones.Trigger };

    private static void Set(int i, float v)
    {
        var z = GodotPads.Zones;
        GodotPads.Zones = i switch { 0 => z with { Left = v }, 1 => z with { Right = v }, _ => z with { Trigger = v } };
    }

    private void Nudge(int i, int dir, bool wrap = false)
    {
        Shell.Sound(82);
        Set(i, PadDeadZones.Next(Get(i), dir, wrap));
    }

    public override void Enter()
    {
        Label = Shell.Db.Font(0x46);
        Small = Shell.Db.Font(0x47);
        _f = Shell.Db.Font(0x44);
        _loading = true;
        _menu.Settle();
        Build();
    }

    protected override void Build()
    {
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            Add(175, ControllerScreen.RowY(3 + i) + 3, () => "", () => { _sel = k; Nudge(k, 1, true); }, _f, w: 290, h: ControllerScreen.RowH - 4);
        }
        var slots = ControllerScreen.Slots;
        Add(slots[1].Position.X, slots[1].Position.Y, () => "", () => { Shell.Sound(82); GodotPads.Zones = PadDeadZones.Default; }, w: slots[1].Size.X, h: slots[1].Size.Y);
        Add(slots[2].Position.X, slots[2].Position.Y, () => "", Done, w: slots[2].Size.X, h: slots[2].Size.Y);
        Add(slots[3].Position.X, slots[3].Position.Y, () => "", Back, w: slots[3].Size.X, h: slots[3].Size.Y);
    }

    private void Done() => Shell.Overlay(new ControllerScreen(_map));
    private void Back() { GodotPads.Zones = _entered; Shell.Overlay(new ControllerScreen(_map)); }
    protected override void Escape() => Back();

    private static byte[] Solid(ShellFont f, byte ink)
    {
        var r = new byte[256];
        Array.Fill(r, ink);
        r[f.Key & 0xff] = 0xff;
        return r;
    }

    private static PadRaw? Pad() => Controls.Focused() ? GodotPads.FirstUnfiltered() : null;

    private string Live()
    {
        var p = Pad();
        string pos = p is { } r ? $"{Px(r.LeftX)},{Px(r.LeftY)},{Px(r.RightX)},{Px(r.RightY)},{(int)(r.TriggerLeft * BarW)},{(int)(r.TriggerRight * BarW)}" : "-";
        return $"{pos}|{_sel}|{GodotPads.Zones}";
    }

    private static int Px(float v) => (int)MathF.Round(v * GaugeR);

    protected override void DrawExtra()
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
        int mid = (ControllerScreen.LeftRule + ControllerScreen.RightRule) / 2;
        void Center(string s, int px, int y) => Frame.Text(_f, px - _f.Measure(s) / 2, y, s, ShellFrame.OverlayInk);
        var pad = Pad();
        string[] notice = pad is null ? ["No controller connected"] : ["Leave the sticks alone: the dot", "should stay inside its ring"];
        for (int k = 0; k < notice.Length; k++) Center(notice[k], mid, ControllerScreen.RowY(k) + 3);
        for (int i = 0; i < 3; i++)
            Center($"{Names[i]}: {(int)MathF.Round(Get(i) * 100)}%", mid, ControllerScreen.RowY(3 + i) + 3);
        byte pencil = Frame.Nearest(ControllerScreen.RedPencil);
        Frame.Fill(177, ControllerScreen.RowY(3 + _sel) + ControllerScreen.RowH - 2, 286, 1, pencil);

        var z = GodotPads.Zones;
        var raw = pad ?? default;
        Gauge(LeftCx, raw.LeftX, raw.LeftY, z.Left, "Left stick", pad is not null);
        Gauge(RightCx, raw.RightX, raw.RightY, z.Right, "Right stick", pad is not null);
        Bar(LeftCx, raw.TriggerLeft, z.Trigger, "Left trigger", pad is not null);
        Bar(RightCx, raw.TriggerRight, z.Trigger, "Right trigger", pad is not null);
        string[] help = ["Click a setting to raise it, right-click", "to lower it. Raise a zone until the red", "dot stays home with the stick at rest."];
        for (int k = 0; k < help.Length; k++) Center(help[k], mid, ControllerScreen.RowY(ControllerScreen.Rows - help.Length + k) + 3);
        _lastLive = Live();
    }

    private void Gauge(int cx, float vx, float vy, float zone, string name, bool live)
    {
        byte rule = Frame.Nearest(ControllerScreen.RuleColor), inkc = Frame.Nearest(ControllerScreen.TitleInk), marker = Frame.Nearest(ControllerScreen.Marker);
        byte shade = Frame.Nearest(Color.Color8(226, 168, 78));
        Disc(cx, GaugeY, Math.Max(1, (int)MathF.Round(zone * GaugeR)), shade);
        Ring(cx, GaugeY, GaugeR, rule);
        Ring(cx, GaugeY, Math.Max(1, (int)MathF.Round(zone * GaugeR)), marker);
        Frame.Fill(cx - GaugeR, GaugeY, GaugeR * 2 + 1, 1, rule);
        Frame.Fill(cx, GaugeY - GaugeR, 1, GaugeR * 2 + 1, rule);
        Frame.Text(_f, cx - _f.Measure(name) / 2, GaugeY + GaugeR + 6, name, ShellFrame.OverlayInk);
        if (!live) return;
        // the game's reading of the stick: the red dot (it sits at the centre while the stick is inside its zone)
        var (gx, gy) = PadDeadZones.Stick(vx, vy, zone);
        Dot(cx + Px(vx), GaugeY + Px(vy), 2, inkc);
        Dot(cx + Px(gx), GaugeY + Px(gy), 1, Frame.Nearest(ControllerScreen.RedPencil));
        int pct = (int)MathF.Round(MathF.Sqrt(vx * vx + vy * vy) * 100);
        string s = $"{pct}% off centre";
        Frame.Text(_f, cx - _f.Measure(s) / 2, GaugeY + GaugeR + 6 + _f.Height + 3, s, ShellFrame.OverlayInk);
    }

    private void Bar(int cx, float v, float zone, string name, bool live)
    {
        byte rule = Frame.Nearest(ControllerScreen.RuleColor), inkc = Frame.Nearest(ControllerScreen.TitleInk), marker = Frame.Nearest(ControllerScreen.Marker);
        int x0 = cx - BarW / 2;
        Frame.Fill(x0 - 1, BarY - 1, BarW + 2, BarH + 2, rule);
        Frame.Fill(x0, BarY, BarW, BarH, Frame.Nearest(Color.Color8(240, 190, 100)));
        int zw = (int)MathF.Round(zone * BarW);
        if (zw > 0) Frame.Fill(x0, BarY, zw, BarH, Frame.Nearest(Color.Color8(226, 168, 78)));
        Frame.Fill(x0 + zw, BarY - 3, 1, BarH + 6, marker);
        if (live)
        {
            int w = (int)MathF.Round(Math.Clamp(v, 0, 1) * BarW);
            if (w > 0) Frame.Fill(x0, BarY + 2, w, BarH - 4, inkc);
        }
        Frame.Text(_f, cx - _f.Measure(name) / 2, BarY + BarH + 5, name, ShellFrame.OverlayInk);
    }

    private void Dot(int x, int y, int r, byte c) => Frame.Fill(x - r, y - r, r * 2 + 1, r * 2 + 1, c);

    private void Disc(int cx, int cy, int r, byte c)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            int dx = (int)MathF.Round(MathF.Sqrt(r * r - dy * dy));
            Frame.Fill(cx - dx, cy + dy, dx * 2 + 1, 1, c);
        }
    }

    private void Ring(int cx, int cy, int r, byte c)
    {
        int steps = Math.Max(24, r * 8);
        for (int k = 0; k < steps; k++)
        {
            float a = k * Mathf.Tau / steps;
            Frame.Fill(cx + (int)MathF.Round(MathF.Cos(a) * r), cy + (int)MathF.Round(MathF.Sin(a) * r), 1, 1, c);
        }
    }

    public override void Tick()
    {
        if (_loading)
        {
            _loading = false;
            Frame.LoadBackground(PaperSheet.Make(Shell.Db.Background(Record), true), 0, 0, black: false);
            Redraw();
            return;
        }
        var pads = Controls.PadSource.Read();
        var act = _menu.Update(pads.Count > 0 && Controls.Focused() ? pads[0] : default);
        if ((act & PadMenu.Act.Up) != 0) _sel = (_sel + 2) % 3;
        if ((act & PadMenu.Act.Down) != 0) _sel = (_sel + 1) % 3;
        if ((act & PadMenu.Act.Left) != 0) Nudge(_sel, -1);
        if ((act & PadMenu.Act.Right) != 0) Nudge(_sel, 1);
        if ((act & PadMenu.Act.Open) != 0) Nudge(_sel, 1, true);
        if ((act & PadMenu.Act.Back) != 0) { Back(); return; }
        if ((act & PadMenu.Act.Done) != 0) { Done(); return; }
        foreach (var k in Shell.Keys)
        {
            if (k is Key.Up) { _sel = (_sel + 2) % 3; Shell.Sound(82); }
            else if (k is Key.Down) { _sel = (_sel + 1) % 3; Shell.Sound(82); }
            else if (k is Key.Left) Nudge(_sel, -1);
            else if (k is Key.Right) Nudge(_sel, 1);
            else if (k is Key.Enter or Key.Space or Key.KpEnter) Nudge(_sel, 1, true);
        }
        if (Shell.RightClicked)
            for (int i = 0; i < 3; i++)
                if (Items[i].Hit(Shell.Mouse)) { _sel = i; Nudge(i, -1, true); Redraw(); return; }
        // the dots follow the sticks
        if (Live() != _lastLive) Redraw();
        base.Tick();
    }
}
