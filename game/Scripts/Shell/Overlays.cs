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
            Add(202, 234, "Mouse Options", () => Shell.Overlay(new MouseOptionsScreen()));
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
        Add(202, 234, "Mouse Options", () => Shell.Overlay(new MouseOptionsScreen()));
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

/// <summary>
/// An overlay with a scrolling list of rows on the sheet's ruled lines (Graphic Detail: every row, the original's and the port's;
/// Play Options: the port's rows in the free lines under the original's): the mouse wheel, Up/Down and Page Up/Down, or the pad
/// past the first or last visible row move the list; a thin bar in the sheet's ink, with an arrow at an end where there is more,
/// marks it. The rest of the sheet (the rows outside the list, Done, Cancel) stays put.
/// </summary>
public abstract class ScrollingOverlayScreen : OverlayScreen
{
    protected readonly ScrollRows<Item> List = new();
    /// <summary>The scroll mark's x: a thumb 5 px wide, its track on x + 2.</summary>
    protected abstract int MarkX { get; }

    /// <summary>An item of the scrolling list, in its slot, <paramref name="dy"/> px below its line.</summary>
    protected Item Listed(Item it, int slot, int dy = 0) => List.Add(it, slot, dy);

    protected override void Redraw()
    {
        foreach (var (it, y, shows) in List.Layout()) { it.Y = y; it.Hidden = !shows; }
        PrepareLines();
        base.Redraw();
    }

    /// <summary>The sheet under the visible lines made ready for the rows now on them (before anything is drawn).</summary>
    protected virtual void PrepareLines() { }

    protected override bool Wheeled(int rows) => List.ScrollBy(Math.Sign(rows));

    public override void Tick()
    {
        var k = Shell.Keys;
        int by = List.KeyStep(k.Contains(Key.Up), k.Contains(Key.Down), k.Contains(Key.Pageup), k.Contains(Key.Pagedown));
        bool moved = by != 0 && List.ScrollBy(by);
        base.Tick();
        if (moved && Shell.Current == this) Redraw();
    }

    // a press past the top or bottom visible row scrolls the list a row
    public override IReadOnlyList<PadRegion> PadRegions =>
        [.. Items.Where(i => i.Click is not null && i.W > 0 && !i.Hidden)
            .Select(i => PadRegion.Of(i.X, i.Y, i.W, i.H, List.SlotOf(i) < 0 ? null : d => List.EdgeStep(i, d) is var by && by != 0 && ScrollThenDraw(by)))];

    private bool ScrollThenDraw(int by)
    {
        if (!List.ScrollBy(by)) return false;
        Redraw();
        return true;
    }

    protected override void DrawExtra()
    {
        if (List.Mark() is not { } m) return;
        int x = MarkX;
        Frame.Fill(x + 2, m.Top, 1, m.H, 200);
        Frame.Fill(x, m.ThumbAt, 5, m.Thumb, 200);
        for (int k = 0; k < 4; k++)
        {
            if (m.MoreAbove) Frame.Fill(x + 2 - k, m.Top - 7 + k, 1 + 2 * k, 1, 200);
            if (m.MoreBelow) Frame.Fill(x + 2 - (3 - k), m.Top + m.H + 3 + k, 1 + 2 * (3 - k), 1, 200);
        }
    }
}

/// <summary>Play Options, overlay 4: record 10, the cheat and salvage flags and the difficulty.</summary>
public sealed class PlayOptionsScreen : ScrollingOverlayScreen
{
    protected override int Record => 10;
    // in the value column's right end: the labels run up to the column's rule
    protected override int MarkX => 458;
    private int _savedFlags;
    private I76.Formats.Input.RumbleMode _savedRumble;
    private int _savedRumbleStrength;
    private bool _savedAutoReverse, _savedModern, _savedForce, _savedCheckpoints, _savedHidden, _savedTurrets, _savedCb, _savedUpdate, _savedCrosshair, _savedBriefing;
    private int _savedRate, _savedDownloads;
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
    /// <summary>The list slots that are check rows (the others a value): their line gets a check box.</summary>
    private readonly HashSet<int> _checks = [];

    /// <summary>
    /// Where the port's rows scroll: the base game's sheet has the original's six check rows on consecutive lines from 139 and
    /// Difficulty on its last line (415), the six lines between for the list; Nitro's keeps Arcade Phys. (139), Unlim. Wiper
    /// Fluid (185) and Difficulty (323) where its sheet has them, the five lines between the last two for the list.
    /// </summary>
    public static (int Top, int Shown) ListLines(bool nitro) => nitro ? (208, 5) : (277, 6);

    /// <summary>The port's rows, in the list's order (a check row, or a value row stepped by a click).</summary>
    public static readonly string[] PortRows =
    [
        "Hidden Wpns & Cars", "Modern Physics", "Simulation Rate", "Checkpoints", "Force Feedback", "Auto Reverse", "Map Downloads",
        "Unlim. Turrets", "CB Voice", "Check for Updates", "Chase Crosshair", "Rumble", "Rumble Strength", "Load During Briefing",
    ];

    protected override void Build()
    {
        var s = Shell.Settings;
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
        _savedUpdate = s.UpdateCheck;
        _savedCrosshair = s.ChaseCrosshair;
        _savedRumble = s.RumbleMode;
        _savedRumbleStrength = s.RumbleStrength;
        _savedBriefing = s.BriefingWhileLoading;
        bool nitro = Shell.Install.IsNitro;
        (List.Top, List.Shown) = ListLines(nitro);
        // the original's six check rows on consecutive ruled lines. The sheet prints a check box only on every other line (where
        // the original's rows were): the others get Arcade Phys.'s box copied
        if (!nitro) foreach (int y in new[] { 162, 208, 254 }) Frame.CopyBackground(200, 139, 22, 18, 200, y);
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
        int dy = nitro ? 323 : 415;
        Add(222, dy, "Difficulty Level", null);
        Add(205, dy + 1, () => "", () => s.Difficulty = s.Difficulty >= 2 ? 0 : s.Difficulty + 1, Small, w: 300, h: 12);
        Add(398, dy + 2, () => Levels[Math.Clamp(s.Difficulty, 0, 2)], null);

        // the port's own rows, one to a line, scrolling in the free lines
        int slot = 0;
        void Toggle(string label, Func<bool> get, Action flip)
        {
            _checks.Add(slot);
            Listed(Add(222, 0, label, null), slot);
            Listed(Add(205, 0, () => Check(get()), flip, Small, w: 200, h: 12), slot, 1);
            slot++;
        }
        void Step(string label, Action click, Func<string> value)
        {
            Listed(Add(222, 0, label, null), slot);
            Listed(Add(205, 0, () => "", click, Small, w: 300, h: 12), slot, 1);
            Listed(Add(398, 0, value, null), slot, 2);
            slot++;
        }
        // Hidden Weapons & Cars (docs/hidden-content.md)
        Toggle(PortRows[0], () => s.HiddenContent, () => s.HiddenContent = !s.HiddenContent);
        Toggle(PortRows[1], () => s.ModernPhysics, () => s.ModernPhysics = !s.ModernPhysics);   // Ctrl+F7 flips it in play
        // Simulation Rate (docs/frame-rate.md): a click steps to the next rate
        Step(PortRows[2], () => { s.SimRate = NextRate(s.SimRate); WarnRate(); }, () => $"{s.SimRate} Hz");
        Toggle(PortRows[3], () => s.Checkpoints, () => s.Checkpoints = !s.Checkpoints);            // docs/savestate.md
        Toggle(PortRows[4], () => s.ForceFeedback, () => s.ForceFeedback = !s.ForceFeedback);   // the pad's rumble
        Toggle(PortRows[5], () => s.AutoReverse, () => s.AutoReverse = !s.AutoReverse);
        // Map Downloads (docs/multiplayer.md "Custom maps"): a network host's map this game lacks is asked about, always
        // downloaded or never; a click steps
        Step(PortRows[6], () => s.MapDownloads = (s.MapDownloads + 1) % 3, () => ShellSettings.MapDownloadNames[Math.Clamp(s.MapDownloads, 0, 2)]);
        Toggle(PortRows[7], () => s.UnlimTurrets, () => s.UnlimTurrets = !s.UnlimTurrets);
        // CB Voice: push-to-talk in network games (the Poem key); its microphone is chosen in Audio Control
        Toggle(PortRows[8], () => s.CbVoice, () => s.CbVoice = !s.CbVoice);
        // Check for Updates: whether the next start asks GitHub if a newer release exists (a line under the version says so)
        Toggle(PortRows[9], () => s.UpdateCheck, () => s.UpdateCheck = !s.UpdateCheck);
        // Chase Crosshair: the aiming mark in the F2 chase view (a network game shows it only when the host allows it)
        Toggle(PortRows[10], () => s.ChaseCrosshair, () => s.ChaseCrosshair = !s.ChaseCrosshair);
        // Rumble (docs/playing.md): the game's own effects, with the port's pulses added, or off; and the strength of every level
        Step(PortRows[11], () => s.RumbleMode = (I76.Formats.Input.RumbleMode)(((int)s.RumbleMode + 1) % 3), () => s.RumbleMode.ToString());
        Step(PortRows[12], () => s.RumbleStrength = I76.Formats.Input.RumbleStrength.Next(s.RumbleStrength), () => s.RumbleStrength + " %");
        // Load During Briefing (docs/deviations.md): a mission's briefing plays while the mission builds, the loading card before it
        // (and after it, while the build finishes, if it is skipped); off, the original's order: the load, then the briefing
        Toggle(PortRows[13], () => s.BriefingWhileLoading, () => s.BriefingWhileLoading = !s.BriefingWhileLoading);
        Add(350, 438, "Done", Done, Small);
        Add(402, 438, "Cancel", Cancel, Small);
        // Check for Updates (upstream's row, under the port's own): whether the next start asks GitHub if a newer release exists (a line under the version says so)
        Frame.CopyBackground(200, 139, 22, 18, 200, 300);
        Add(222, 300, "Check for Updates", null);
        Add(205, 301, () => Check(s.UpdateCheck), () => s.UpdateCheck = !s.UpdateCheck, Small, w: 200, h: 12);
    }

    /// <summary>Each visible line of the list gets the sheet's check box when a check row is on it and blank paper when a value row is (or none).</summary>
    protected override void PrepareLines()
    {
        for (int line = 0; line < List.Shown; line++)
        {
            int y = List.Top + List.LineH * line;
            Frame.CopyBackground(300, y, 22, 18, 200, y);
            if (_checks.Contains(List.SlotOn(line))) Frame.CopyBackground(200, 139, 22, 18, 200, y);
        }
    }

    /// <summary>The CB Microphone choices: none (the empty string), the system's default, then each input device.</summary>
    internal static List<string> MicChoices()
    {
        try { return MicNames.Choices(Godot.AudioServer.GetInputDeviceList()); } catch { return MicNames.Choices([]); }
    }

    /// <summary>The choice after the current one (none, the default, each device, then none again).</summary>
    internal static string NextMic(string now)
    {
        var all = MicChoices();
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
        s.CbVoice = _savedCb; s.UpdateCheck = _savedUpdate; s.ChaseCrosshair = _savedCrosshair; s.RumbleMode = _savedRumble; s.RumbleStrength = _savedRumbleStrength; s.BriefingWhileLoading = _savedBriefing;
        s.PlayOptions = _savedFlags; s.AutoReverse = _savedAutoReverse; s.ModernPhysics = _savedModern; s.ForceFeedback = _savedForce; s.Checkpoints = _savedCheckpoints;
        s.SimRate = _savedRate; s.MapDownloads = _savedDownloads; s.HiddenContent = _savedHidden; s.UnlimTurrets = _savedTurrets;   // the difficulty is not restored
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

/// <summary>The port's rows under Audio Control's levels: the CB microphone, and HiFi Audio's switch only when a pack is installed (the front end's and the mission menu's).</summary>
public static class AudioControlRows
{
    public const string Mic = "CB Microphone", HiFiAudio = "HiFi Audio";
    public static IReadOnlyList<string> Labels(bool pack) => pack ? [Mic, HiFiAudio] : [Mic];
}

/// <summary>Audio Control, overlay 6: record 7, the music, effects and voice levels with a value and a row of check boxes each.</summary>
public sealed class AudioScreen : OverlayScreen
{
    protected override int Record => 7;
    private byte[] _saved = [];
    private string _savedMic = "";
    private bool _savedHiFi;
    // Number Of Channels (+0x53, 347) is gone: it capped the sound hardware's voices, and this port plays every sound
    private static readonly (int Y, string Label, int Boxes)[] Rows = [(140, "Music Level", 10), (209, "Sfx Level", 10), (278, "Voice Level", 10)];

    protected override void Build()
    {
        var s = Shell.Settings;
        _saved = (byte[])s.Audio.Clone();
        _savedMic = s.CbMic;
        _savedHiFi = s.HiFiAudio;
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
        var rows = AudioControlRows.Labels(I76.Game.Assets.HiFiAudio.Installed);
        Add(204, 347, AudioControlRows.Mic, null);
        Add(204, 371, () => PlayOptionsScreen.MicLabel(s.CbMic), () => s.CbMic = PlayOptionsScreen.NextMic(s.CbMic), w: 300, h: 18);
        // HiFi Audio's sounds and movie tracks, on or off (only with a pack installed, its name under it): sounds and movies started after a change follow it
        if (rows.Contains(AudioControlRows.HiFiAudio))
        {
            // (the two unused check boxes in the sheet's bottom box, wiped)
            Frame.CopyBackground(240, 405, 14, 16, 206, 405);
            Frame.CopyBackground(240, 405, 14, 16, 332, 405);
            Add(204, 400, AudioControlRows.HiFiAudio, null);
            Add(400, 400, () => s.HiFiAudio ? "On" : "Off", () => ApplyHiFi(!s.HiFiAudio));
            Add(204, 420, () => I76.Game.Assets.HiFiAudio.Label ?? "", null, Small);
        }
        Add(350, 438, "Done", () => { s.Save(); Shell.Overlay(new OptionsScreen()); }, Small);
        // Cancel puts the four bytes back but resets only the music live (as written)
        Add(402, 438, "Cancel", () => { Array.Copy(_saved, s.Audio, 4); s.CbMic = _savedMic; ApplyHiFi(_savedHiFi); Apply(0); Shell.Overlay(new OptionsScreen()); }, Small);
    }

    private void ApplyHiFi(bool on) { Shell.Settings.HiFiAudio = on; I76.Game.Assets.HiFiAudio.Enabled = on; }

    // the music level is live; the SFX and voice levels reach their buses and play their preview (cammo.wav for the SFX level, croger.wav for the voice level)
    private void Apply(int row)
    {
        if (row == 0) Shell.ApplyMusicLevel();
        else { I76.Game.Boot.Main.ApplyAudio(Shell.Settings); I76.Game.Ui.LevelPreview.Play(Shell, Shell.Vfs, row); }
    }
}

/// <summary>Graphic Detail, overlay 5: record 9, the sixteen-byte graphics block.</summary>
public sealed class GraphicsScreen : ScrollingOverlayScreen
{
    protected override int Record => 9;
    private byte[] _saved = [];
    private bool _savedHd, _savedLook, _savedSoftware, _savedGrade;
    private int _savedBlur;
    private int _savedVeg, _savedDraw, _savedMaxFps;
    private I76.Game.Boot.RendererChoice _savedRenderer;
    private string _savedRes = "";
    private int _savedScale;
    private bool _savedVSync, _savedLog, _saved3D, _savedInteriors;
    // only the settings this port honours: the original's draw distance and most detail levels of the 1997 renderer are gone (the
    // resolution is the port's window size)
    private static readonly (string Label, int Byte, string[] Values)[] Rows =
    [
        ("Monitor Brightness", 0xc, ["0.00", "1.00", "2.00", "3.00", "4.00", "5.00", "6.00", "7.00", "8.00", "9.00", "10.00"]),
        ("Ext. Instrum. Disp", 0xe, ["Off", "On"]),
        ("Rear Mirror", 0xd, ["Off", "Low", "High"]),
        // the modern look's cost (Main.ApplyModernQuality); the first two are the original's own rows and bytes
        ("Shadow Detail", 0x6, ["Off", "Low", "High"]),
        ("Terrain Detail", 0x2, ["Low", "High"]),
        ("Anti-Aliasing", 0x8, I76.Game.Boot.RenderScale.AntiAliasing),   // the original's byte, the port's MSAA: Off, 2x, 4x and 8x
    ];

    private void Apply(bool on) { Shell.Settings.HdMenuArt = on; HdAssets.Enabled = on; }

    // every row, the original's six and then the port's own, one to a ruled line in one list over the sheet's fourteen lines
    // (the original's first, from its first line at 118, on the original's 23 px line)
    private const int ListY = 118, Shown = 14;
    protected override int MarkX => 386;    // in the gap before the value column
    private int _slot;

    /// <summary>The next row of the list: its label and its value (a click on the value is <paramref name="click"/>).</summary>
    private void Row(string label, Func<string> value, Action click)
    {
        Listed(Add(200, ListY, label, null), _slot);
        Listed(Add(397, ListY, value, click), _slot);
        _slot++;
    }

    protected override void Build()
    {
        var g = Shell.Settings.Graphics;
        (List.Top, List.Shown) = (ListY, Shown);
        _slot = 0;
        _saved = (byte[])g.Clone();
        _savedHd = Shell.Settings.HdMenuArt;
        _savedLook = Shell.Settings.Look1997;
        _savedSoftware = Shell.Settings.SoftwareRenderer;
        _savedGrade = Shell.Settings.PaletteGrade;
        _savedBlur = Shell.Settings.CrashBlur;
        _savedVeg = Shell.Settings.Vegetation;
        _savedDraw = Shell.Settings.DrawDistance;
        _savedMaxFps = Shell.Settings.MaxFps;
        _savedVSync = Shell.Settings.VSync;
        _savedLog = Shell.Settings.LogFile;
        _savedRenderer = Shell.Settings.Renderer;
        _saved3D = Shell.Settings.Garage3D;
        _savedInteriors = Shell.Settings.CarInteriors;
        _savedRes = Shell.Settings.Resolution;
        _savedScale = Shell.Settings.RenderScale;
        foreach (var (label, b, values) in Rows)
            Row(label, () => values[Math.Min(g[b], values.Length - 1)], () => g[b] = (byte)((g[b] + 1) % values.Length));
        var st = Shell.Settings;
        // the port's own rows, first the window's size: Desktop (fullscreen, as the game starts) or a window of that size, applied at once
        Row("Resolution", () => I76.Game.Boot.WindowSizes.Label(I76.Game.Boot.WindowSizes.Parse(st.Resolution)), () =>
        {
            var (dw, dh) = I76.Game.Boot.WindowApplier.Desktop;
            var n = I76.Game.Boot.WindowSizes.Next(I76.Game.Boot.WindowSizes.Parse(st.Resolution), dw, dh);
            st.Resolution = n is { } z ? I76.Game.Boot.WindowSizes.Format(z.W, z.H) : "";
            I76.Game.Boot.WindowApplier.Apply(st);
        });
        // the 3D view supersampled at 100, 150 or 200 % of the window, live (the HUD and menus are not scaled)
        Row("Render Scale", () => I76.Game.Boot.RenderScale.Names[Math.Clamp(st.RenderScale, 0, I76.Game.Boot.RenderScale.Names.Length - 1)], () => { st.RenderScale = (st.RenderScale + 1) % I76.Game.Boot.RenderScale.Names.Length; I76.Game.Boot.RenderScale.Apply(st); });
        // the menu art pack's (only when one is installed), with the pack's name beside it
        if (HdAssets.Installed)
        {
            Row("HD Menu Art", () => st.HdMenuArt ? "On" : "Off", () => Apply(!st.HdMenuArt));
            Listed(Add(318, ListY + 3, () => HdAssets.Label ?? "", null, Small), _slot - 1, 3);
        }
        // the 1997 look: off, the original's 3D card or its software renderer (the shell keeps the look it started in; a change shows
        // from the next start or mission), with the modern look's palette grade (SoftwareLook) after it
        string[] modes = ["Off", "3D Card", "Software"];
        Row("1997 Mode", () => modes[!st.Look1997 ? 0 : st.SoftwareRenderer ? 2 : 1], () =>
        {
            int m = ((!st.Look1997 ? 0 : st.SoftwareRenderer ? 2 : 1) + 1) % 3;
            st.Look1997 = m != 0; st.SoftwareRenderer = m == 2;
        });
        Row("Palette Grade", () => st.PaletteGrade ? "On" : "Off", () => st.PaletteGrade = !st.PaletteGrade);
        // the software renderer's double image on a big cockpit hit: Auto, Off, On
        Row("Crash Blur", () => I76.Sim.Views.CrashBlurMode.Names[Math.Clamp(st.CrashBlur, 0, 1)], () => st.CrashBlur = 1 - Math.Clamp(st.CrashBlur, 0, 1));
        // the modern look's plants (docs/modern-vegetation.md)
        string[] veg = ["Off", "Low", "High"];
        Row("Vegetation", () => veg[Math.Clamp(st.Vegetation, 0, 2)], () => st.Vegetation = (st.Vegetation + 1) % 3);
        // how far the world is drawn, in either look
        Row("Draw Distance", () => I76.Formats.Imaging.DrawDistance.Names[st.DrawDistance], () => st.DrawDistance = 1 - st.DrawDistance);
        // the frame rate limiter and vsync: drawing only, the simulation keeps the Play Options rate; both live (the engine's, so the shell shows it too)
        Row("Max FPS", () => I76.Sim.Views.FrameLimit.Names[Math.Clamp(st.MaxFps, 0, I76.Sim.Views.FrameLimit.Names.Length - 1)], () => { st.MaxFps = (st.MaxFps + 1) % I76.Sim.Views.FrameLimit.Names.Length; I76.Game.FrameLimiter.Apply(st); });
        Row("VSync", () => st.VSync ? "On" : "Off", () => { st.VSync = !st.VSync; I76.Game.FrameLimiter.Apply(st); });
        // the log file: Godot reads it only at start, so a change shows from the next start (docs/dev-hooks.md: --log forces it for a run)
        Row("Log (restart)", () => st.LogFile ? "On" : "Off", () => st.LogFile = !st.LogFile);
        // CONFIGURE CHASSIS as the 3D garage (on) or the original's 2D form (off)
        Row("3D Garage", () => st.Garage3D ? "On" : "Off", () => st.Garage3D = !st.Garage3D);
        // the modern look's cabin and driver behind a car's windows (off: plain reflective glass), live
        Row("Car Interiors", () => st.CarInteriors ? "On" : "Off", () => { st.CarInteriors = !st.CarInteriors; I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(st.CarInteriors); });
        // the renderer: Auto, Vulkan, D3D12 (Windows only) or OpenGL, read by Godot at start (an unavailable choice falls back there)
        Row("Renderer (restart)", () => st.Renderer == I76.Game.Boot.RendererChoice.D3D12 && !I76.Game.Boot.StartupSettings.D3D12Offered ? "Auto" : st.Renderer.ToString(), () =>
        {
            var all = I76.Game.Boot.StartupSettings.D3D12Offered ? new[] { I76.Game.Boot.RendererChoice.Auto, I76.Game.Boot.RendererChoice.Vulkan, I76.Game.Boot.RendererChoice.D3D12, I76.Game.Boot.RendererChoice.OpenGL }
                : new[] { I76.Game.Boot.RendererChoice.Auto, I76.Game.Boot.RendererChoice.Vulkan, I76.Game.Boot.RendererChoice.OpenGL };
            st.Renderer = all[(Math.Max(0, Array.IndexOf(all, st.Renderer)) + 1) % all.Length];
        });
        Add(288, 438, "Reset", () => { Shell.Settings.Resolution = ""; I76.Game.Boot.WindowApplier.Apply(Shell.Settings); Shell.Settings.RenderScale = 0; I76.Game.Boot.RenderScale.Apply(Shell.Settings); Shell.Settings.MaxFps = I76.Sim.Views.FrameLimit.Unlimited; Shell.Settings.VSync = true; I76.Game.FrameLimiter.Apply(Shell.Settings); Array.Copy(ShellSettings.DefaultGraphics, g, 16); Apply(true); Shell.Settings.Look1997 = false; Shell.Settings.SoftwareRenderer = false; Shell.Settings.PaletteGrade = false; Shell.Settings.CrashBlur = I76.Sim.Views.CrashBlurMode.On; Shell.Settings.Vegetation = 1; Shell.Settings.DrawDistance = I76.Formats.Imaging.DrawDistance.Far; Shell.Settings.Garage3D = true; Shell.Settings.CarInteriors = true; I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(true); }, Small);
        Add(350, 438, "Done", () => { Shell.Settings.Save(); Shell.Overlay(new OptionsScreen()); }, Small);
        Add(402, 438, "Cancel", () => { Array.Copy(_saved, g, 16); Apply(_savedHd); Shell.Settings.Look1997 = _savedLook; Shell.Settings.SoftwareRenderer = _savedSoftware; Shell.Settings.PaletteGrade = _savedGrade; Shell.Settings.CrashBlur = _savedBlur; Shell.Settings.Vegetation = _savedVeg; Shell.Settings.DrawDistance = _savedDraw; Shell.Settings.LogFile = _savedLog; Shell.Settings.Renderer = _savedRenderer; Shell.Settings.Garage3D = _saved3D; Shell.Settings.CarInteriors = _savedInteriors; I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(_savedInteriors); Shell.Settings.Resolution = _savedRes; I76.Game.Boot.WindowApplier.Apply(Shell.Settings); Shell.Settings.RenderScale = _savedScale; I76.Game.Boot.RenderScale.Apply(Shell.Settings); Shell.Settings.MaxFps = _savedMaxFps; Shell.Settings.VSync = _savedVSync; I76.Game.FrameLimiter.Apply(Shell.Settings); Shell.Overlay(new OptionsScreen()); }, Small);
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
