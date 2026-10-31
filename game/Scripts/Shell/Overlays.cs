using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// One of the Joe's Fish Shack notepad overlays: a full 640x480 picture with its own palette, an item
/// table of text rows (font 0x46, ink 1 drawn as palette 200), hit-tested
/// x &lt;= mx &lt; x + w on press, the whole table redrawn after any click
/// and no hover feedback. Esc goes back to the Options Menu.
/// </summary>
public abstract class OverlayScreen : ShellScreen
{
    protected abstract int Record { get; }
    public override bool EscapeOpensOptions => false;
    protected ShellFont Label = null!, Small = null!;
    protected readonly List<Item> Items = [];

    /// <summary>A row: text at (x, y) in a font, its rectangle from the text unless given, and what a click does.</summary>
    protected sealed class Item
    {
        public int X, Y, W = -1, H = -1;
        public Func<string> Text = () => "";
        public ShellFont? Font;
        public Action? Click;
        /// <summary>A scroll arrow: held, it clicks again (HoldRepeat).</summary>
        public bool Repeats;
        /// <summary>Scrolled out of view: not drawn, not hit, not focusable.</summary>
        public bool Hidden;
        public bool Hit(Vector2I m) => Click is not null && !Hidden && X <= m.X && m.X < X + W && Y <= m.Y && m.Y < Y + H;
    }

    protected Item Add(int x, int y, Func<string> text, Action? click, ShellFont? font = null, int w = -1, int h = -1)
    {
        var it = new Item { X = x, Y = y, Text = text, Click = click, Font = font, W = w, H = h };
        Items.Add(it);
        return it;
    }

    private readonly I76.Formats.Input.HoldRepeat _hold = new();

    protected Item Add(int x, int y, string text, Action? click, ShellFont? font = null) => Add(x, y, () => text, click, font);

    public override void Enter()
    {
        Label = Shell.Db.Font(0x46);
        Small = Shell.Db.Font(0x47);
        // no black frame between the screens: the sheet and its text come up together, a flash of black is a flicker
        Frame.LoadBackground(Shell.Db, Record, black: false);
        Build();
        Redraw();
    }

    protected abstract void Build();

    /// <summary>The background back, every row drawn again.</summary>
    protected virtual void Redraw()
    {
        Frame.RestoreAll();
        foreach (var it in Items)
        {
            if (it.Hidden) continue;
            var f = it.Font ?? Label;
            var s = it.Text();
            if (it.W < 0) it.W = Math.Max(1, f.Measure(s.TrimStart('~')));
            if (it.H < 0) it.H = f.Height;
            Frame.Text(f, it.X, it.Y, s, ShellFrame.OverlayInk);
        }
        DrawExtra();
    }

    protected virtual void DrawExtra() { }

    public override IReadOnlyList<PadRegion> PadRegions =>
        [.. Items.Where(i => i.Click is not null && i.W > 0 && !i.Hidden).Select(i => PadRegion.Of(i.X, i.Y, i.W, i.H))];

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Escape(); return; }
        if (Shell.Wheel != 0 && Wheeled(Shell.WheelRows)) Redraw();
        int held = _hold.Id;
        if (_hold.Update(Shell.MouseHeld, Items.FindIndex(i => i.Repeats && i.Hit(Shell.Mouse)), Shell.Delta) is var reps and > 0)
        {
            for (int r = 0; r < reps; r++) Items[held].Click!();
            Redraw();
        }
        if (!Shell.Clicked) return;
        var m = Shell.Mouse;
        var hit = Items.FirstOrDefault(i => i.Hit(m));
        if (hit is null) { ClickElsewhere(m); return; }
        if (hit.Repeats) _hold.Begin(Items.IndexOf(hit));
        hit.Click!();
        if (Shell.Current == this) Redraw();
    }

    protected virtual void ClickElsewhere(Vector2I m) { }

    /// <summary>The mouse wheel turned (rows to scroll, positive down): true when the screen needs drawing again.</summary>
    protected virtual bool Wheeled(int rows) => false;

    protected virtual void Escape() => Shell.Overlay(new OptionsScreen());

    protected string Check(bool on) => on ? "x" : "";
}

/// <summary>Options Menu, overlay 0: record 6, three item tables by where it was opened.</summary>
public sealed class OptionsScreen : OverlayScreen
{
    protected override int Record => 6;

    public override void Enter()
    {
        Shell.ReloadSettings();                // I76PLYR.DEF is read again, so Esc out of an edit screen discards its changes
        Shell.StopVoice();                     // The salvage intro (0x49) and garage (0x4e) voices end
        base.Enter();
        Shell.Sound(72);       // once on entry
    }

    protected override void Build()
    {
        bool inTrip = Shell.InTrip, fromMain = Shell.Underneath is MainMenuScreen;
        if (Shell.Install.IsNitro)
        {
            // Nitro's one table: Play Options wherever it is opened, no bookmarks, no trip
            Add(202, 119, "Play Options", () => Shell.Overlay(new PlayOptionsScreen()));
            Add(202, 142, "Graphic Detail", () => Shell.Overlay(new GraphicsScreen()));
            Add(202, 165, "Audio Control", () => Shell.Overlay(new AudioScreen()));
            Add(202, 211, "Control Configuration", () => Shell.Overlay(new ControlsScreen()));
            Add(202, 280, "Credits", () => Shell.Overlay(new CreditsScreen { FromOptions = true }));
            if (fromMain && I76.Game.Boot.GameChooser.SwitchAvailable) Add(202, 303, "Switch Game", () => Shell.SwitchGame());
            Add(202, 326, "Exit Interstate '76", () => Shell.Overlay(new ExitScreen()));
            Add(402, 438, () => "Done", () => Shell.Overlay(null), Small, w: 100);
            return;
        }
        if (inTrip || fromMain) Add(202, 119, "Play Options", () => Shell.Overlay(new PlayOptionsScreen()));
        Add(202, 142, "Graphic Detail", () => Shell.Overlay(new GraphicsScreen()));
        Add(202, 165, "Audio Control", () => Shell.Overlay(new AudioScreen()));
        Add(202, 211, "Control Configuration", () => Shell.Overlay(new ControlsScreen()));
        Add(202, 280, "Load Bookmark", () => Shell.Overlay(new LoadBookmarkScreen()));
        if (inTrip) Add(202, 303, "Save Bookmark", () => Shell.Overlay(new SaveBookmarkScreen()));
        Add(202, 349, "Credits", () => Shell.Overlay(new CreditsScreen { FromOptions = true }));
        if (fromMain && I76.Game.Boot.GameChooser.SwitchAvailable) Add(202, 372, "Switch Game", () => Shell.SwitchGame());
        Add(202, 395, "Exit Interstate '76", () => Shell.Overlay(new ExitScreen()));
        if (inTrip) Add(202, 418, "Exit\tTrip".Replace('\t', ' '), () => { Shell.Overlay(null); Shell.Go(new MainMenuScreen()); });
        Add(402, 438, () => "Done", () => Shell.Overlay(null), Small, w: 100);
    }

    protected override void Escape() => Shell.Overlay(null);
}

/// <summary>Play Options, overlay 4: record 10, the cheat and salvage flags and the difficulty.</summary>
public sealed class PlayOptionsScreen : OverlayScreen
{
    protected override int Record => 10;
    private int _savedFlags;
    private bool _savedAutoReverse, _savedModern, _savedForce, _savedCheckpoints, _savedHidden, _savedTurrets, _savedCb;
    private int _savedRate, _savedDownloads;
    /// <summary>0 the sheet, 1 its second page (the port's rows past the sheet's fourteen lines: Unlim. Turrets), a click on More / Back turns it.</summary>
    public int Page { get; init; }
    private static PlayOptionsScreen? _first;       // the first page's snapshot, which Cancel restores from either page
    private static readonly (int Y, string Label, int Bit)[] Rows =
    [
        (139, "Arcade Phys.", 0x01), (162, "No Salv. Manag.", 0x02), (185, "Unlim. Ammo", 0x04),
        (208, "Unlim. Armor", 0x08), (231, "Unlim. Chas. Rein.", 0x10), (254, "Unlim. Wiper Fluid", 0x20),
    ];
    private static int NextRate(int hz)
    {
        var o = I76.Sim.Session.SimRate.Offered;
        int i = Array.FindIndex(o, r => r.Label == hz);
        return o[(i + 1) % o.Length].Label;
    }
    private static readonly string[] Levels = ["Wimp", "Champion", "Badass"];

    protected override void Build()
    {
        var s = Shell.Settings;
        if (Page == 1) { BuildSecond(); return; }
        _first = this;
        _savedFlags = s.PlayOptions;
        _savedAutoReverse = s.AutoReverse;
        _savedModern = s.ModernPhysics;
        _savedForce = s.ForceFeedback;
        _savedCheckpoints = s.Checkpoints;
        _savedRate = s.SimRate;
        _savedDownloads = s.MapDownloads;
        _savedHidden = s.HiddenContent;
        _savedTurrets = s.UnlimTurrets;
        _savedCb = s.CbVoice;
        // the original's six check rows first, on consecutive ruled lines, then the port's own on the lines under them. The sheet
        // prints a check box only on every other line (where the original's rows were): the others get Arcade Phys.'s box copied
        bool nitro = Shell.Install.IsNitro;
        foreach (int y in nitro ? new[] { 116, 162, 208, 254 } : new[] { 116, 162, 208, 254, 346 }) Frame.CopyBackground(200, 139, 22, 18, 200, y);
        // Nitro's table has three rows: Arcade Phys. (222,139), Unlim. Wiper Fluid (222,185), Difficulty (222,323);
        // the salvage-management and cheat rows are the base game's
        foreach (var (y, label, bit) in Rows)
        {
            if (nitro && (bit & PlayProfile.NitroMask) == 0) continue;
            int ry = nitro && bit == 0x20 ? 185 : y;
            Add(222, ry, label, null);
            // the check strip, x 205..404, y+1..y+12; a set bit shows "x" at (205, y+1)
            Add(205, ry + 1, () => Check((s.PlayOptions & bit) != 0), () => s.PlayOptions ^= bit, Small, w: 200, h: 12);
        }
        void Toggle(int y, string label, Func<bool> get, Action flip)
        {
            Add(222, y, label, null);
            Add(205, y + 1, () => Check(get()), flip, Small, w: 200, h: 12);
        }
        // Hidden Weapons & Cars (docs/hidden-content.md), on the sheet's first line, the only one free
        Toggle(116, "Hidden Wpns & Cars", () => s.HiddenContent, () => s.HiddenContent = !s.HiddenContent);
        Toggle(nitro ? 162 : 277, "Modern Physics", () => s.ModernPhysics, () => s.ModernPhysics = !s.ModernPhysics);   // Ctrl+F7 flips it in play
        // Simulation Rate (docs/frame-rate.md): a click steps to the next rate
        Add(222, 300, "Simulation Rate", null);
        Add(205, 301, () => "", () => { s.SimRate = NextRate(s.SimRate); WarnRate(); }, Small, w: 300, h: 12);
        Add(398, 302, () => $"{s.SimRate} Hz", null);
        // Nitro's Difficulty sits on 323, where the base game puts Checkpoints; there Checkpoints take the free line at 208
        Toggle(nitro ? 208 : 323, "Checkpoints", () => s.Checkpoints, () => s.Checkpoints = !s.Checkpoints);            // docs/savestate.md
        Toggle(nitro ? 231 : 346, "Force Feedback", () => s.ForceFeedback, () => s.ForceFeedback = !s.ForceFeedback);   // the pad's rumble
        Toggle(nitro ? 254 : 369, "Auto Reverse", () => s.AutoReverse, () => s.AutoReverse = !s.AutoReverse);
        // Map Downloads (docs/multiplayer.md "Custom maps"): a network host's map this game lacks is asked about, always
        // downloaded or never; a click steps
        Add(222, 392, "Map Downloads", null);
        Add(205, 393, () => "", () => s.MapDownloads = (s.MapDownloads + 1) % 3, Small, w: 300, h: 12);
        Add(398, 394, () => ShellSettings.MapDownloadNames[Math.Clamp(s.MapDownloads, 0, 2)], null);
        int dy = nitro ? 323 : 415;
        Add(222, dy, "Difficulty Level", null);
        Add(205, dy + 1, () => "", () => s.Difficulty = s.Difficulty >= 2 ? 0 : s.Difficulty + 1, Small, w: 300, h: 12);
        Add(398, dy + 2, () => Levels[Math.Clamp(s.Difficulty, 0, 2)], null);
        Add(246, 438, "More", () => Shell.Overlay(new PlayOptionsScreen { Page = 1 }), Small);
        Add(350, 438, "Done", Done, Small);
        Add(402, 438, "Cancel", Cancel, Small);
    }

    /// <summary>The second page: the sheet's fourteen lines are all taken, so the port's later rows go on a page of their own (the boxes of the first page's rows wiped).</summary>
    private void BuildSecond()
    {
        var s = Shell.Settings;
        Frame.CopyBackground(200, 139, 22, 18, 200, 116);
        for (int k = 1; k < 14; k++) if (k != 1) Frame.CopyBackground(300, 116 + 23 * k, 22, 18, 200, 116 + 23 * k);    // (the sheet's own box on the second line stays: CB Voice's)
        Add(222, 116, "Unlim. Turrets", null);
        Add(205, 117, () => Check(s.UnlimTurrets), () => s.UnlimTurrets = !s.UnlimTurrets, Small, w: 200, h: 12);
        // CB Voice: push-to-talk in network games (the Poem key); its microphone is chosen in Audio Control
        Add(222, 139, "CB Voice", null);
        Add(205, 140, () => Check(s.CbVoice), () => s.CbVoice = !s.CbVoice, Small, w: 200, h: 12);
        Add(246, 438, "Back", () => Shell.Overlay(new PlayOptionsScreen()), Small);
        Add(350, 438, "Done", Done, Small);
        Add(402, 438, "Cancel", Cancel, Small);
    }

    /// <summary>The sound system's input devices after the current one (the default, then each in the list, then the default again).</summary>
    internal static string NextMic(string now)
    {
        var all = new List<string> { "" };
        try { all.AddRange(Godot.AudioServer.GetInputDeviceList().Where(d => d != "Default")); } catch { }
        int i = all.IndexOf(now);
        return all[(i + 1) % all.Count];
    }

    internal static string MicLabel(string mic)
    {
        string t = MicNames.Of(mic);
        return t.Length > 26 ? t[..25] + "." : t;
    }

    private void Cancel()
    {
        var s = Shell.Settings;
        var f = _first ?? this;
        s.CbVoice = f._savedCb;
        s.PlayOptions = f._savedFlags; s.AutoReverse = f._savedAutoReverse; s.ModernPhysics = f._savedModern; s.ForceFeedback = f._savedForce; s.Checkpoints = f._savedCheckpoints;
        s.SimRate = f._savedRate; s.MapDownloads = f._savedDownloads; s.HiddenContent = f._savedHidden; s.UnlimTurrets = f._savedTurrets;   // the difficulty is not restored
        Shell.Overlay(new OptionsScreen());
    }

    /// <summary>The first time the rate leaves 20 Hz, once ever: it can break the game (docs/frame-rate.md); going back to 20 never warns.</summary>
    private void WarnRate()
    {
        var s = Shell.Settings;
        if (s.SimRateWarned || s.SimRate == I76.Sim.Session.SimRate.Default.Label) return;
        s.SimRateWarned = true;
        Shell.Modal(new NoticePopup
        {
            Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Closed = Redraw,
            Text = "This can break the game. Leave it at 20 Hz for the best results.",
        });
    }

    private void Done()
    {
        if ((Shell.Settings.PlayOptions & 0x1c) == 0) { Shell.Settings.Save(); Shell.Overlay(new OptionsScreen()); return; }
        Shell.Modal(new ConfirmPopup
        {
            Lines = ["You have chosen an option which", "will not allow you to advance in", "the trip. Do you wish to continue?"],
            Answer = yes => { if (yes) { Shell.Settings.Save(); Shell.Overlay(new OptionsScreen()); } else Redraw(); },
        });
    }
}

/// <summary>Audio Control, overlay 6: record 7, the music, effects and voice levels with a value and a row of check boxes each.</summary>
public sealed class AudioScreen : OverlayScreen
{
    protected override int Record => 7;
    private byte[] _saved = [];
    private string _savedMic = "";
    // Number Of Channels (+0x53, 347) is gone: it capped the sound hardware's voices, and this port plays every sound
    private static readonly (int Y, string Label, int Boxes)[] Rows = [(140, "Music Level", 10), (209, "Sfx Level", 10), (278, "Voice Level", 10)];

    protected override void Build()
    {
        var s = Shell.Settings;
        _saved = (byte[])s.Audio.Clone();
        _savedMic = s.CbMic;
        for (int r = 0; r < Rows.Length; r++)
        {
            int row = r;
            var (y, label, boxes) = Rows[r];
            Add(204, y, label, null);
            Add(400, y, () => s.Audio[row] < 2 ? "Off" : $"{s.Audio[row]}.00", () => { s.Audio[row] = (byte)(s.Audio[row] >= boxes ? 0 : s.Audio[row] + 1); Apply(row); });
            for (int i = 1; i <= boxes; i++)
            {
                int n = i;
                Add(202 + 17 * (i - 1), y + 24, () => Check(s.Audio[row] >= n), () => { s.Audio[row] = (byte)n; Apply(row); }, Small, w: 12, h: 15);
            }
        }
        // the port's: the microphone the CB voice chat uses (network games), under the levels; a click steps through the input devices
        Frame.CopyBackground(200, 324, 150, 18, 200, 371);      // (the sheet's unused check boxes on that line, wiped)
        Add(204, 347, "CB Microphone", null);
        Add(204, 371, () => PlayOptionsScreen.MicLabel(s.CbMic), () => s.CbMic = PlayOptionsScreen.NextMic(s.CbMic), w: 300, h: 18);
        Add(350, 438, "Done", () => { s.Save(); Shell.Overlay(new OptionsScreen()); }, Small);
        // Cancel puts the four bytes back but resets only the music live (as written)
        Add(402, 438, "Cancel", () => { Array.Copy(_saved, s.Audio, 4); s.CbMic = _savedMic; Apply(0); Shell.Overlay(new OptionsScreen()); }, Small);
    }

    // the music level is live; the SFX and voice levels reach their buses and play their preview (cammo.wav for the SFX level, croger.wav for the voice level)
    private void Apply(int row)
    {
        if (row == 0) Shell.ApplyMusicLevel();
        else { I76.Game.Boot.Main.ApplyAudio(Shell.Settings); I76.Game.Ui.LevelPreview.Play(Shell, Shell.Vfs, row); }
    }
}

/// <summary>Graphic Detail, overlay 5: record 9, the sixteen-byte graphics block.</summary>
public sealed class GraphicsScreen : OverlayScreen
{
    protected override int Record => 9;
    private byte[] _saved = [];
    private bool _savedHd, _savedLook, _savedSoftware, _savedGrade;
    private int _savedBlur;
    private int _savedVeg, _savedDraw, _savedMouse, _savedMaxFps;
    private bool _savedVSync, _savedConfine, _savedLog, _saved3D, _savedInteriors, _savedReturn, _savedZoomReset;
    // only the settings this port honours: resolution, draw distance and most detail levels of the 1997 renderer are gone
    private static readonly (int Y, string Label, int Byte, string[] Values)[] Rows =
    [
        (118, "Monitor Brightness", 0xc, ["0.00", "1.00", "2.00", "3.00", "4.00", "5.00", "6.00", "7.00", "8.00", "9.00", "10.00"]),
        (141, "Ext. Instrum. Disp", 0xe, ["Off", "On"]),
        (164, "Rear Mirror", 0xd, ["Off", "Low", "High"]),
        // the modern look's cost (Main.ApplyModernQuality); the first two are the original's own rows and bytes
        (187, "Shadow Detail", 0x6, ["Off", "Low", "High"]),
        (210, "Terrain Detail", 0x2, ["Low", "High"]),
        (233, "Anti-Aliasing", 0x8, ["Off", "2x", "4x"]),
    ];

    private void Apply(bool on) { Shell.Settings.HdMenuArt = on; HdAssets.Enabled = on; }

    // the port's own rows scroll in the sheet's free space under the original's: eight rows show of thirteen, on the original's 23 px line
    private const int ListY = 256, LineH = 23, Shown = 8;
    private int _scroll;
    private readonly List<(Item It, int Slot, int Dy)> _list = [];

    private int Slots => _list.Count == 0 ? 0 : _list.Max(l => l.Slot) + 1;
    private int MaxScroll => Math.Max(0, Slots - Shown);

    /// <summary>One of the scrolling rows: its label and its value (a click on the value is <paramref name="click"/>), in list slot <paramref name="slot"/>.</summary>
    private void Row(int slot, string label, Func<string> value, Action click)
    {
        // the menu-art row (slot 0) is there only when an art pack is installed; without one the rest move up a line
        if (!HdAssets.Installed) { if (slot == 0) return; slot--; }
        _list.Add((Add(200, ListY, label, null), slot, 0));
        _list.Add((Add(397, ListY, value, click), slot, 0));
    }

    private void Reposition()
    {
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        foreach (var (it, slot, dy) in _list)
        {
            int row = slot - _scroll;
            it.Hidden = row < 0 || row >= Shown;
            it.Y = ListY + LineH * row + dy;
        }
    }

    private bool ScrollBy(int by)
    {
        int to = Math.Clamp(_scroll + by, 0, MaxScroll);
        if (to == _scroll) return false;
        _scroll = to;
        return true;
    }

    protected override void Redraw() { Reposition(); base.Redraw(); }

    protected override bool Wheeled(int rows) => ScrollBy(Math.Sign(rows));

    public override void Tick()
    {
        int by = (Shell.Keys.Contains(Key.Down) ? 1 : 0) - (Shell.Keys.Contains(Key.Up) ? 1 : 0)
            + (Shell.Keys.Contains(Key.Pagedown) ? Shown : 0) - (Shell.Keys.Contains(Key.Pageup) ? Shown : 0);
        bool moved = by != 0 && ScrollBy(by);
        base.Tick();
        if (moved && Shell.Current == this) Redraw();
    }

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            // a press past the top or bottom visible row scrolls the list a row
            var l = new List<PadRegion>();
            foreach (var i in Items)
            {
                if (i.Click is null || i.W <= 0 || i.Hidden) continue;
                var slot = _list.FirstOrDefault(e => e.It == i);
                Func<NavDir, bool>? edge = null;
                if (slot.It is not null)
                {
                    int row = slot.Slot - _scroll;
                    edge = d => d == NavDir.Up && row == 0 ? ScrollThenDraw(-1) : d == NavDir.Down && row == Shown - 1 ? ScrollThenDraw(1) : false;
                }
                l.Add(PadRegion.Of(i.X, i.Y, i.W, i.H, edge));
            }
            return l;
        }
    }

    private bool ScrollThenDraw(int by)
    {
        if (!ScrollBy(by)) return false;
        Redraw();
        return true;
    }

    /// <summary>The list's scroll mark, in the sheet's ink: a thin track in the gap before the value column with a thumb, and an arrow at an end where there is more.</summary>
    protected override void DrawExtra()
    {
        if (MaxScroll == 0) return;
        const int x = 386, top = ListY + 6, h = LineH * Shown - 18;
        int thumb = Math.Max(10, h * Shown / Slots), at = top + (h - thumb) * _scroll / MaxScroll;
        Frame.Fill(x + 2, top, 1, h, 200);
        Frame.Fill(x, at, 5, thumb, 200);
        for (int k = 0; k < 4; k++)
        {
            if (_scroll > 0) Frame.Fill(x + 2 - k, top - 7 + k, 1 + 2 * k, 1, 200);
            if (_scroll < MaxScroll) Frame.Fill(x + 2 - (3 - k), top + h + 3 + k, 1 + 2 * (3 - k), 1, 200);
        }
    }

    protected override void Build()
    {
        var g = Shell.Settings.Graphics;
        _saved = (byte[])g.Clone();
        _savedHd = Shell.Settings.HdMenuArt;
        _savedLook = Shell.Settings.Look1997;
        _savedSoftware = Shell.Settings.SoftwareRenderer;
        _savedGrade = Shell.Settings.PaletteGrade;
        _savedBlur = Shell.Settings.CrashBlur;
        _savedVeg = Shell.Settings.Vegetation;
        _savedDraw = Shell.Settings.DrawDistance;
        _savedMouse = Shell.Settings.MouseCamera;
        _savedMaxFps = Shell.Settings.MaxFps;
        _savedVSync = Shell.Settings.VSync;
        _savedConfine = Shell.Settings.KeepMouseInWindow;
        _savedLog = Shell.Settings.LogFile;
        _saved3D = Shell.Settings.Garage3D;
        _savedInteriors = Shell.Settings.CarInteriors;
        _savedReturn = Shell.Settings.CameraReturn;
        _savedZoomReset = Shell.Settings.ZoomReset;
        foreach (var (y, label, b, values) in Rows)
        {
            Add(200, y, label, null);
            Add(397, y, () => values[Math.Min(g[b], values.Length - 1)], () => g[b] = (byte)((g[b] + 1) % values.Length));
        }
        var st = Shell.Settings;
        // the port's own rows, one to a line: the menu art pack's (only when one is installed), with the pack's name beside it
        Row(0, "HD Menu Art", () => st.HdMenuArt ? "On" : "Off", () => Apply(!st.HdMenuArt));
        if (HdAssets.Installed) _list.Add((Add(318, ListY + 3, () => HdAssets.Label ?? "", null, Small), 0, 3));
        Row(1, "Mouse In Window", () => st.KeepMouseInWindow ? "On" : "Off", () => st.KeepMouseInWindow = !st.KeepMouseInWindow);
        // the 1997 look: off, the original's 3D card or its software renderer (the shell keeps the look it started in; a change shows
        // from the next start or mission), with the modern look's palette grade (SoftwareLook) after it
        string[] modes = ["Off", "3D Card", "Software"];
        Row(2, "1997 Mode", () => modes[!st.Look1997 ? 0 : st.SoftwareRenderer ? 2 : 1], () =>
        {
            int m = ((!st.Look1997 ? 0 : st.SoftwareRenderer ? 2 : 1) + 1) % 3;
            st.Look1997 = m != 0; st.SoftwareRenderer = m == 2;
        });
        Row(3, "Palette Grade", () => st.PaletteGrade ? "On" : "Off", () => st.PaletteGrade = !st.PaletteGrade);
        // the software renderer's double image on a big cockpit hit: Auto, Off, On
        Row(4, "Crash Blur", () => I76.Sim.Views.CrashBlurMode.Names[Math.Clamp(st.CrashBlur, 0, 1)], () => st.CrashBlur = 1 - Math.Clamp(st.CrashBlur, 0, 1));
        // the modern look's plants (docs/modern-vegetation.md)
        string[] veg = ["Off", "Low", "High"];
        Row(5, "Vegetation", () => veg[Math.Clamp(st.Vegetation, 0, 2)], () => st.Vegetation = (st.Vegetation + 1) % 3);
        // how far the world is drawn, in either look
        Row(6, "Draw Distance", () => I76.Formats.Imaging.DrawDistance.Names[st.DrawDistance], () => st.DrawDistance = 1 - st.DrawDistance);
        // the mouse orbiting the chase view: Off, Slow, Normal, Fast (Invert Y is shell.cfg's [port] mouse_invert_y)
        Row(7, "Mouse Camera", () => I76.Sim.Views.MouseLook.Names[Math.Clamp(st.MouseCamera, 0, 3)], () => st.MouseCamera = (st.MouseCamera + 1) % I76.Sim.Views.MouseLook.Names.Length);
        // the frame rate limiter and vsync: drawing only, the simulation keeps the Play Options rate; both live (the engine's, so the shell shows it too)
        Row(8, "Max FPS", () => I76.Sim.Views.FrameLimit.Names[Math.Clamp(st.MaxFps, 0, I76.Sim.Views.FrameLimit.Names.Length - 1)], () => { st.MaxFps = (st.MaxFps + 1) % I76.Sim.Views.FrameLimit.Names.Length; I76.Game.FrameLimiter.Apply(st); });
        Row(9, "VSync", () => st.VSync ? "On" : "Off", () => { st.VSync = !st.VSync; I76.Game.FrameLimiter.Apply(st); });
        // the log file: Godot reads it only at start, so a change shows from the next start (docs/dev-hooks.md: --log forces it for a run)
        Row(10, "Log (restart)", () => st.LogFile ? "On" : "Off", () => st.LogFile = !st.LogFile);
        // CONFIGURE CHASSIS as the 3D garage (on) or the original's 2D form (off)
        Row(11, "3D Garage", () => st.Garage3D ? "On" : "Off", () => st.Garage3D = !st.Garage3D);
        // the modern look's cabin and driver behind a car's windows (off: plain reflective glass), live
        Row(12, "Car Interiors", () => st.CarInteriors ? "On" : "Off", () => { st.CarInteriors = !st.CarInteriors; I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(st.CarInteriors); });
        // the chase view easing back behind the car after the mouse or stick rests, live
        Row(13, "Camera Return", () => st.CameraReturn ? "On" : "Off", () => { st.CameraReturn = !st.CameraReturn; I76.Game.MouseCamera.Apply(st); });
        // the wheel zoom back to the original's framing at a switch of view, a cut-scene's end and a respawn (off: each view keeps it), live
        Row(14, "View Zoom Reset", () => st.ZoomReset ? "On" : "Off", () => { st.ZoomReset = !st.ZoomReset; I76.Game.MouseCamera.Apply(st); });
        Add(288, 438, "Reset", () => { Shell.Settings.MouseCamera = I76.Sim.Views.MouseLook.Normal; Shell.Settings.KeepMouseInWindow = false; Shell.Settings.MaxFps = I76.Sim.Views.FrameLimit.Unlimited; Shell.Settings.VSync = true; I76.Game.FrameLimiter.Apply(Shell.Settings); Array.Copy(ShellSettings.DefaultGraphics, g, 16); Apply(true); Shell.Settings.Look1997 = false; Shell.Settings.SoftwareRenderer = false; Shell.Settings.PaletteGrade = false; Shell.Settings.CrashBlur = I76.Sim.Views.CrashBlurMode.On; Shell.Settings.Vegetation = 1; Shell.Settings.DrawDistance = I76.Formats.Imaging.DrawDistance.Far; Shell.Settings.Garage3D = true; Shell.Settings.CarInteriors = true; Shell.Settings.CameraReturn = true; Shell.Settings.ZoomReset = true; I76.Game.MouseCamera.Apply(Shell.Settings); I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(true); }, Small);
        Add(350, 438, "Done", () => { Shell.Settings.Save(); Shell.Overlay(new OptionsScreen()); }, Small);
        Add(402, 438, "Cancel", () => { Array.Copy(_saved, g, 16); Apply(_savedHd); Shell.Settings.Look1997 = _savedLook; Shell.Settings.SoftwareRenderer = _savedSoftware; Shell.Settings.PaletteGrade = _savedGrade; Shell.Settings.CrashBlur = _savedBlur; Shell.Settings.Vegetation = _savedVeg; Shell.Settings.DrawDistance = _savedDraw; Shell.Settings.MouseCamera = _savedMouse; Shell.Settings.KeepMouseInWindow = _savedConfine; Shell.Settings.LogFile = _savedLog; Shell.Settings.Garage3D = _saved3D; Shell.Settings.CarInteriors = _savedInteriors; Shell.Settings.CameraReturn = _savedReturn; Shell.Settings.ZoomReset = _savedZoomReset; I76.Game.MouseCamera.Apply(Shell.Settings); I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(_savedInteriors); Shell.Settings.MaxFps = _savedMaxFps; Shell.Settings.VSync = _savedVSync; I76.Game.FrameLimiter.Apply(Shell.Settings); Shell.Overlay(new OptionsScreen()); }, Small);
    }
}

/// <summary>Exit Game, overlay 2: record 8, the only quit confirmation.</summary>
public sealed class ExitScreen : OverlayScreen
{
    protected override int Record => 8;

    protected override void Build()
    {
        Add(213, 142, "Are you gonna", null);
        Add(208, 164, "leave me, Sweetheart ?", null);
        Add(218, 254, "Yes", () => Shell.Quit());
        Add(318, 254, "No", () => Shell.Overlay(new OptionsScreen()));
    }
}

/// <summary>
/// Credits, overlay 9: record 13 (black), record 83's lines
/// centred at 320 (measured in font 0x46, drawn in 0x42, as written), from
/// y = 480 + 17·i, scrolling up 1 px whenever 30 ms have passed. Ends when
/// the last line is gone, on a click or on Esc. The CD plays track 8 under
/// it, then track 13 again.
/// </summary>
public sealed class CreditsScreen : OverlayScreen
{
    protected override int Record => 13;
    public bool FromOptions { get; init; }
    /// <summary>Where the end of the campaign's credits leads (the main menu).</summary>
    public Func<ShellScreen>? Then { get; init; }
    private readonly List<(string Text, int Y)> _lines = [];
    private ShellFont _measure = null!, _draw = null!;
    private double _since;

    public override bool EscapeOpensOptions => false;

    protected override void Build()
    {
        _measure = Label;
        _draw = Shell.Db.Font(0x42);
        var lines = Shell.Db.Credits();
        for (int i = 0; i < lines.Length; i++) _lines.Add((lines[i], 480 + (_draw.Height + 2) * i));
        Shell.StopMusic();
        Shell.PlayMusic(8, loop: false);
    }

    protected override void Redraw() => Frame.RestoreAll();

    public override void Tick()
    {
        if (Shell.Clicked || Shell.Keys.Contains(Key.Escape) || _lines.Count == 0) { End(); return; }
        _since += Shell.Delta;
        if (_since < 0.030) return;
        _since = 0;
        int step = _draw.Height + 2;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            var (text, y) = _lines[i];
            if (y < 480) Frame.Restore(0, y, 640, step);
            y--;
            if (y < -step) { _lines.RemoveAt(i); continue; }
            _lines[i] = (text, y);
        }
        foreach (var (text, y) in _lines)
            if (y < 480 && text.Length > 0) Frame.Text(_draw, 320 - _measure.Measure(text) / 2, y, text);
    }

    private void End()
    {
        Shell.StopMusic();
        Shell.PlayMusic(13);
        if (FromOptions) Shell.Overlay(new OptionsScreen());
        else { Shell.Overlay(null); Shell.Go(Then?.Invoke() ?? new MainMenuScreen()); }
    }
}

/// <summary>
/// The confirm dialog: bank 0x24 sprite 11, the "Tad's Auto
/// Salvage" receipt, at (115, 130) with a 6 px shadow, its text from
/// (224, 166) in font 0x45; YES and NO rectangles relative to the dialog.
/// </summary>
public sealed class ConfirmPopup : ShellScreen
{
    public required string[] Lines { get; init; }
    public required Action<bool> Answer { get; init; }
    /// <summary>Where each line goes, when the caller places them itself (the garage's "Groove…" at (225, 180), (215, 200), (265, 220)).</summary>
    public Vector2I[]? At { get; init; }
    public override bool EscapeOpensOptions => false;
    private static readonly Rect2I Yes = new(369, 245, 49, 27), No = new(428, 245, 44, 27);   // screen coordinates: the buttons are drawn into the 410x220 receipt at (115, 130)

    public override void Enter()
    {
        Frame.Blit(Shell.Db.Bank(0x24)[11], 115, 130);
        Frame.Fill(121, 350, 410, 6, 0x0f);
        Frame.Fill(525, 136, 6, 220, 0x0f);
        var f = Shell.Db.Font(0x45);
        for (int i = 0; i < Lines.Length; i++)
        {
            var at = At is { } a && i < a.Length ? a[i] : new Vector2I(224, 166 + 15 * i);
            Frame.Text(f, at.X, at.Y, Lines[i]);
        }
    }

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of(Yes), PadRegion.Of(No)];

    public override void Tick()
    {
        bool esc = Shell.Keys.Contains(Key.Escape);        // the pad's B and Start: No
        if (!Shell.Clicked && !esc) return;
        bool yes = !esc && Yes.HasPoint(Shell.Mouse), no = esc || No.HasPoint(Shell.Mouse);
        if (!yes && !no) return;
        Frame.Restore(115, 130, 428, 238);
        Shell.Modal(null);
        Answer(yes);
    }
}

/// <summary>A notice: a bank 0x24 sprite (282x93) at (179, 194) with a shadow, closed by a press on its OK.</summary>
public sealed class NoticePopup : ShellScreen
{
    public required int Sprite { get; init; }
    /// <summary>A Nitro notice with no base twin: its sprite in the Nitro shell's popup bank (30).</summary>
    public int? NativeSprite { get; init; }
    /// <summary>The OK rectangle, relative to the notice.</summary>
    public required Rect2I Ok { get; init; }
    /// <summary>Text drawn on a blank notice (sprite 17), for messages the original never had.</summary>
    public string? Text { get; init; }
    /// <summary>What happens once it is closed (the screen's repaint, or the next screen).</summary>
    public Action? Closed { get; init; }
    public override bool EscapeOpensOptions => false;

    public override void Enter()
    {
        var s = NativeSprite is { } ns ? Shell.Db.BankNative(30)[ns] : Shell.Db.Bank(0x24)[Sprite];
        Frame.Blit(s, 179, 194);
        int w = s?.Width ?? 282, h = s?.Height ?? 93;
        Frame.Fill(185, 194 + h, w, 6, 0x0f);
        Frame.Fill(179 + w, 200, 6, h, 0x0f);
        if (Text is null) return;
        // a long message (a custom map's missing terrain) is wrapped into up to three centred lines above OK
        var font = Shell.Db.Font(0x45);
        var lines = Wrap(font, Text, w - 24, 3);
        int y0 = 194 + 22 - (lines.Count - 1) * 8;
        for (int i = 0; i < lines.Count; i++) Frame.Text(font, 179 + w / 2, y0 + i * 15, "~" + lines[i]);
    }

    /// <summary>Words into lines no wider than <paramref name="width"/>, at most <paramref name="max"/> (the last one cut to fit).</summary>
    public static List<string> Wrap(ShellFont f, string text, int width, int max)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (f.Measure(next) <= width || line.Length == 0) { line = next; continue; }
            lines.Add(line);
            line = word;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count > max) { lines = lines.Take(max).ToList(); lines[^1] += "..."; }
        for (int i = 0; i < lines.Count; i++) lines[i] = AddressBookPopup.Fit(f, lines[i], width);
        return lines;
    }

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of(new Rect2I(179 + Ok.Position.X, 194 + Ok.Position.Y, Ok.Size))];

    public override void Tick()
    {
        bool esc = Shell.Keys.Contains(Key.Escape);        // the pad's B and Start
        if (!esc && (!Shell.Clicked || !new Rect2I(179 + Ok.Position.X, 194 + Ok.Position.Y, Ok.Size).HasPoint(Shell.Mouse))) return;
        Frame.Restore(179, 194, 300, 110);
        Shell.Modal(null);
        Closed?.Invoke();
    }
}
