using Godot;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using I76.Game.Shell;

namespace I76.Game.Ui;

/// <summary>What the escape menu leaves behind: the game state it sets.</summary>
public enum MenuOutcome { Resume, Replay, Reconfigure, Abort, Exit, RetryCheckpoint, Developer }

/// <summary>
/// The engine's own 2D menu, the "Spanner's Cafe" order pad:
/// Esc in a mission opens the Options Menu over the frozen last frame; its
/// pads (Abort Mission, Play Options, Graphic Detail, Audio Control, the
/// exit confirmation, the cheater warning) edit the settings block the
/// shell shares (I76PLYR.DEF, here user://shell.cfg). The mission-end menu
/// (failed, or won with cheats) is the same machinery. Everything is placed
/// in pad coordinates of the 296x425 art, centred as the original's 1:1 blit
/// at 640x480 is, and scaled with the window; text in the game's own Lee
/// handwriting and Arial Bold, ink (16,16,16), the selection a grey bar.
/// </summary>
public sealed partial class EscapeMenu : Control
{
    // item types and modifiers
    private const int Button = 0x002, Command = 0x004, Cycle = 0x008, Static = 0x010, Check = 0x040, Slider = 0x080, Numeric = 0x100, Act = 0x200,
        OwnStep = 0x800, Min1 = 0x1000, WrapAround = 0x2000, OneIsNumber = 0x4000;

    private sealed class Item
    {
        public int Flags;
        public string? Label;
        public int Pair;
        public int N;
        public string[]? Names;
        public int Value;
        /// <summary>The item's callback (msg 2/3 own step, 4 activate, 6 opened, 7 changed, 8 done, 9 cancel); null = not selectable.</summary>
        public Action<Item, int>? Cb;
        /// <summary>A row of a pad's scrolling list (Graphic Detail's and Play Options', every row): its place in the list, from 1 (0: not in
        /// one); the pad row it is on is that minus the screen's scroll. Pair stays the item's own id.</summary>
        public int At;
        public bool Scrolls => At > 0;
        public int Type => Flags & 0x3ff;
    }

    private sealed class Screen
    {
        public required string Key;
        public Screen? Parent;
        public bool EscIgnored;
        public required string Art;
        public List<Item> Items = [];
        public int Order;
        /// <summary>The scrolling rows' first pad row, how many show at once (the pad's fourteen ruled rows), and how far they are scrolled (in rows).</summary>
        public const int FirstRow = 1, VisibleRows = 14;
        public int Scroll;
        public int Scrollable => Items.Count(i => i.Scrolls);
        public int MaxScroll => Math.Max(0, Scrollable - VisibleRows);
        /// <summary>Screen +0x20: remembered for the whole session.</summary>
        public int Selected { get => Selection.GetValueOrDefault(Key); set => Selection[Key] = value; }
    }

    private static readonly Dictionary<string, int> Selection = [];
    private static int _orderMission = -1, _orderBase;
    internal static void ResetForGameSwitch() { Selection.Clear(); _orderMission = -1; _orderBase = 0; }

    public required AssetVfs Vfs { get; init; }
    public required Palette Palette { get; init; }
    public required string InstallDir { get; init; }
    public required string Mission { get; init; }
    /// <summary>The 4-item Abort pad / 5-item end menu: no shell trip to go back to, No Salv. Manag., or t01/t13/t17.</summary>
    public required bool NoReconfigure { get; init; }
    public bool Faithful { get; init; }
    /// <summary>A network melee: the game runs on under it, so there is no Play Options (the cheats) and no replay; Leave Game goes back to the shell.</summary>
    public bool Net { get; init; }
    /// <summary>The menu closed with this outcome; the settings are saved by then.</summary>
    public required Action<MenuOutcome> Closed { get; init; }
    /// <summary>A setting that applies at once changed (the audio levels).</summary>
    public Action<ShellSettings>? Live { get; init; }
    /// <summary>For the end menu: the failure line (or null), and whether the mission was won with cheats on.</summary>
    public (string? Message, bool Cheated)? End { get; init; }
    /// <summary>The end menu's "Turn off Cheater Options".</summary>
    public Action? CheatsOff { get; init; }
    /// <summary>The port's Checkpoints: a checkpoint is kept, so the end menu after a failure and the Abort pad offer "Retry from Checkpoint".</summary>
    public bool CanRetry { get; init; }
    /// <summary>The port's Developer panel is on (docs/dev-hooks.md): the main pad offers it on the free line under Control Configuration.</summary>
    public bool Developer { get; init; }
    /// <summary>Save the current checkpoint as a trip bookmark.</summary>
    public Func<bool>? SaveBookmark { get; init; }

    public ShellSettings Settings { get; private set; } = null!;
    private ShellSettings _snap = null!;
    private Screen _main = null!, _abort = null!, _exit = null!, _play = null!, _graphics = null!, _audio = null!, _cheater = null!, _end = null!;
    private Screen _screen = null!;
    private MenuOutcome _exitYes;
    private readonly Dictionary<string, ImageTexture?> _art = [];
    /// <summary>The modern look's upscaled pads (none shipped): name → texture and the pad's rectangle in it; null when there is none.</summary>
    private readonly Dictionary<string, (ImageTexture Tex, Rect2 Src)?> _hd = [];
    private Dictionary<string, (string? File, float[]? Affine)>? _hdIndex;
    private ImageTexture? _x;
    private Font _lee = null!, _arial = null!;
    private Color _grey;
    private static readonly Color Ink = Color.Color8(16, 16, 16);
    private Item? _pressed;
    private int _pressedMark = -1;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Settings = ShellSettings.Load();
        _snap = ShellSettings.Load();
        var lee = new FontFile { Antialiasing = Faithful ? TextServer.FontAntialiasing.None : TextServer.FontAntialiasing.Gray };
        if (lee.LoadDynamicFont((new I76.Formats.Vfs.GameInstall(InstallDir).FindFile("Lee_____.ttf") ?? System.IO.Path.Combine(InstallDir, "Lee_____.ttf"))) != Error.Ok) lee = null;
        _arial = HdAssets.Arial(true, Faithful ? TextServer.FontAntialiasing.None : TextServer.FontAntialiasing.Gray)
            ?? new SystemFont { FontWeight = 700 };
        _lee = (Font?)lee ?? _arial;
        // the stock GRAY_BRUSH resolved against the mission palette: its nearest to RGB 128
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = Palette[i];
            int d = (r - 128) * (r - 128) + (g - 128) * (g - 128) + (b - 128) * (b - 128);
            if (d < bestD) (best, bestD) = (i, d);
        }
        _grey = Color.Color8(Palette[best].R, Palette[best].G, Palette[best].B);
        _x = Art("x1", key: true);
        Build();
        _padNav.Settle();    // the Start that opened it
        Switch(End is null ? _main : _end);
    }

    // ---- the screens -------------------------------------------------------------------------

    private Item Action(string label, int pair, Action then, string? price = null) =>
        new() { Flags = Act, Label = label, Pair = pair, N = 1, Names = price is null ? null : [price], Cb = (_, m) => { if (m == 4) then(); } };

    private Item Text(string? text, int pair) => new() { Flags = Static, Label = text, Pair = pair };
    private Item DoneButton() => new() { Flags = Button, Label = "Done", Pair = 15, Cb = (_, m) => { if (m == 4) Done(); } };
    private Item CancelButton(string label = "Cancel") => new() { Flags = Button, Label = label, Pair = 16, Cb = (_, m) => { if (m == 4) Cancel(); } };

    /// <summary>
    /// An item over one value of the settings block: msg 6 loads it, msg 7
    /// writes it when <paramref name="live"/>, msg 8 commits it (and its
    /// snapshot byte), msg 9 restores it from the snapshot.
    /// </summary>
    private Item Setting(int flags, string label, int pair, int n, string[]? names, Func<ShellSettings, int> get, Action<ShellSettings, int> put,
        bool live = true, Action? apply = null, Action? applyDone = null, Action? changed = null)
    {
        return new Item
        {
            Flags = flags, Label = label, Pair = pair, N = n, Names = names,
            Cb = (it, m) =>
            {
                switch (m)
                {
                    case 6: it.Value = get(Settings); break;
                    case 7: if (live) { put(Settings, it.Value); apply?.Invoke(); changed?.Invoke(); } break;
                    case 8: put(Settings, it.Value); put(_snap, it.Value); applyDone?.Invoke(); break;
                    case 9: put(Settings, get(_snap)); it.Value = get(Settings); apply?.Invoke(); break;
                }
            },
        };
    }

    /// <summary>A Setting in the pad's scrolling list (<paramref name="at"/> is its place in the list, from 1, the pad row it has unscrolled).</summary>
    private Item Scrolling(int at, int flags, string label, int n, string[]? names, Func<ShellSettings, int> get, Action<ShellSettings, int> put,
        bool live = true, Action? apply = null)
    {
        var it = Setting(flags, label, at, n, names, get, put, live, apply);
        it.At = at;
        return it;
    }

    private void Build()
    {
        bool four = NoReconfigure;
        _main = new Screen { Key = "main", Art = "mainmn1" };
        _abort = new Screen { Key = four ? "abort4" : "abort5", Art = "abrtms1", Parent = _main };
        _exit = new Screen { Key = "exit", Art = "extgme1" };
        _play = new Screen { Key = "play", Art = "plyopt1", Parent = _main };
        _graphics = new Screen { Key = "graphics", Art = "grxdet1", Parent = _main };
        _audio = new Screen { Key = "audio", Art = "audcon1", Parent = _main };
        _cheater = new Screen { Key = "cheater", Art = "plyopt1", EscIgnored = true };
        _end = new Screen { Key = four ? "end5" : "end6", Art = "extgme1", EscIgnored = true };

        _main.Items = Net ? [
            Action("Leave Game", 1, () => AskExit(_main, MenuOutcome.Abort)),
            Action("Exit Game", 3, () => AskExit(_main, MenuOutcome.Exit)),
            Action("Graphic Detail", 4, () => Switch(_graphics), "$8.98"),
            Action("Audio Control", 5, () => Switch(_audio), "$9.01"),
            Action("Control Configuration", 6, OpenControls, "$7.65"),
            Action("Mouse Options", 7, OpenMouse, "$6.50"),
            CancelButton("Resume"),
        ] : [
            Action("Abort Mission", 1, () => Switch(_abort), "$6.78"),
            Action("Play Options", 3, () => Switch(_play), "$5.34"),
            Action("Graphic Detail", 4, () => Switch(_graphics), "$8.98"),
            Action("Audio Control", 5, () => Switch(_audio), "$9.01"),
            // the port's own, on the free ruled lines (rows 6 and 7) under Audio Control: Control Configuration and Mouse Options, over the paused game
            Action("Control Configuration", 6, OpenControls, "$7.65"),
            Action("Mouse Options", 7, OpenMouse, "$6.50"),
            CancelButton("Exit"),
        ];
        // the port's Developer panel, only when it is on (--dev, I76_DEV=1 or shell.cfg's developer=true): the bottom of the pad's
        // list, the free line under its last entry
        if (Developer && !Net) _main.Items.Insert(_main.Items.Count - 1, Action("Developer", _main.Items.Max(i => i.Pair is >= 1 and <= 14 ? i.Pair : 0) + 1, () => Close(MenuOutcome.Developer)));

        _abort.Items = [Action("Replay", 1, () => Close(MenuOutcome.Replay))];
        if (!four) _abort.Items.Add(Action("Reconfigure", 3, () => Close(MenuOutcome.Reconfigure)));
        _abort.Items.Add(Action("Abort Mission", four ? 3 : 5, () => AskExit(_abort, MenuOutcome.Abort)));
        _abort.Items.Add(Action("Exit Game", four ? 5 : 7, () => AskExit(_abort, MenuOutcome.Exit)));
        // the port's own: the checkpoint on the next free line under Exit Game
        if (CanRetry)
        {
            _abort.Items.Add(Action("Retry from Checkpoint", four ? 7 : 9, () => Close(MenuOutcome.RetryCheckpoint)));
            if (SaveBookmark is not null)
            {
                Item? saveItem = null;
                saveItem = Action("Save Bookmark", four ? 9 : 11, () =>
                {
                    if (SaveBookmark())
                    {
                        saveItem!.Label = "Bookmark Saved";
                        saveItem.Cb = null;
                        QueueRedraw();
                    }
                });
                _abort.Items.Add(saveItem);
            }
        }
        _abort.Items.Add(CancelButton());

        _exit.Items = [
            Action("Yes", 27, () => Close(_exitYes)),
            Text("Are you gonna leave me, Sweetheart?", 2),
            Text(null, 28),
            Action("No", 29, () => Switch(_exit.Parent)),
        ];

        // Play Options: the six check boxes commit on Done (msg 7 does nothing), Cancel restores the whole dword
        string[] checks = ["Arcade Phys.", "No Salv. Manag.", "Unlim. Ammo", "Unlim. Armor", "Unlim. Chas. Rein.", "Unlim. Wiper Fluid"];
        for (int k = 0; k < 6; k++)
        {
            int bit = 1 << k;
            // Nitro has only Arcade Phys. and Unlim. Wiper Fluid (PlayProfile.NitroMask): no salvage management, no cheats
            if (ShellSettings.Nitro && (bit & I76.Formats.Shell.PlayProfile.NitroMask) == 0) continue;
            _play.Items.Add(new Item
            {
                Flags = Check, Label = checks[k], Pair = 18 + k, N = 2,
                Cb = (it, m) =>
                {
                    if (m == 6) it.Value = (Settings.PlayOptions & bit) != 0 ? 1 : 0;
                    else if (m == 8) { Settings.PlayOptions = it.Value != 0 ? Settings.PlayOptions | bit : Settings.PlayOptions & ~bit; _snap.PlayOptions = Settings.PlayOptions; }
                    else if (m == 9) { Settings.PlayOptions = _snap.PlayOptions; it.Value = (Settings.PlayOptions & bit) != 0 ? 1 : 0; }
                },
            });
        }
        // Difficulty, the original's last row, after them
        _play.Items.Add(Setting(Cycle, "Difficulty Level", 24, 3, ["Wimp", "Champ", "Badass"], s => s.Difficulty, (s, v) => s.Difficulty = v));
        // the port's own rows after the original's, in the shell's order
        _play.Items.Add(new Item
        {
            Flags = Check, Label = "Modern Physics", Pair = 26, N = 2,
            Cb = (it, m) =>
            {
                if (m == 6) it.Value = Settings.ModernPhysics ? 1 : 0;
                else if (m == 8) { Settings.ModernPhysics = it.Value != 0; _snap.ModernPhysics = Settings.ModernPhysics; }
                else if (m == 9) { Settings.ModernPhysics = _snap.ModernPhysics; it.Value = Settings.ModernPhysics ? 1 : 0; }
            },
        });
        // Simulation Rate: from the next mission start
        var rates = I76.Sim.Session.SimRate.Offered;
        _play.Items.Add(Setting(Cycle, "Simulation Rate", 8, rates.Length, System.Array.ConvertAll(rates, r => $"{r.Label} Hz"),
            st => System.Math.Max(0, System.Array.FindIndex(rates, r => r.Label == st.SimRate)), (st, v) => st.SimRate = rates[v].Label, live: false));
        // Checkpoints: from the next checkpoint on (a kept one stays)
        _play.Items.Add(new Item
        {
            Flags = Check, Label = "Checkpoints", Pair = 31, N = 2,
            Cb = (it, m) =>
            {
                if (m == 6) it.Value = Settings.Checkpoints ? 1 : 0;
                else if (m == 8) { Settings.Checkpoints = it.Value != 0; _snap.Checkpoints = Settings.Checkpoints; }
                else if (m == 9) { Settings.Checkpoints = _snap.Checkpoints; it.Value = Settings.Checkpoints ? 1 : 0; }
            },
        });
        _play.Items.Add(new Item
        {
            Flags = Check, Label = "Force Feedback", Pair = 30, N = 2,
            Cb = (it, m) =>
            {
                if (m == 6) it.Value = Settings.ForceFeedback ? 1 : 0;
                else if (m == 8) { Settings.ForceFeedback = it.Value != 0; _snap.ForceFeedback = Settings.ForceFeedback; I76.Game.ForceFeedback.Enabled = Settings.ForceFeedback; }
                else if (m == 9) { Settings.ForceFeedback = _snap.ForceFeedback; it.Value = Settings.ForceFeedback ? 1 : 0; I76.Game.ForceFeedback.Enabled = Settings.ForceFeedback; }
            },
        });
        _play.Items.Add(new Item
        {
            Flags = Check, Label = "Auto Reverse", Pair = 25, N = 2,
            Cb = (it, m) =>
            {
                if (m == 6) it.Value = Settings.AutoReverse ? 1 : 0;
                else if (m == 8) { Settings.AutoReverse = it.Value != 0; _snap.AutoReverse = Settings.AutoReverse; }
                else if (m == 9) { Settings.AutoReverse = _snap.AutoReverse; it.Value = Settings.AutoReverse ? 1 : 0; }
            },
        });
        // Chase Crosshair: the F2 chase view's aiming mark, from the resume on (Play Options is not on a network game's pad)
        _play.Items.Add(new Item
        {
            Flags = Check, Label = "Chase Crosshair", Pair = 33, N = 2,
            Cb = (it, m) =>
            {
                if (m == 6) it.Value = Settings.ChaseCrosshair ? 1 : 0;
                else if (m == 8) { Settings.ChaseCrosshair = it.Value != 0; _snap.ChaseCrosshair = Settings.ChaseCrosshair; }
                else if (m == 9) { Settings.ChaseCrosshair = _snap.ChaseCrosshair; it.Value = Settings.ChaseCrosshair ? 1 : 0; }
            },
        });
        // every row in one list that scrolls over the pad's rows, in the order added; Done and Cancel stay put
        int at = 1;
        foreach (var it in _play.Items) it.At = at++;
        _play.Items.Add(DoneButton());
        _play.Items.Add(CancelButton());

        _cheater.Items = [
            Text("You have chosen an option that would make you a cheater. Cheaters aren't allowed to advance.", 47),
            Action("I'm a cheater", 11, () => { _snap.PlayOptions = Settings.PlayOptions; Switch(_main); }),
            Action("I'm a swinger", 13, () => Switch(_play)),
        ];

        // Graphic Detail: only the settings this port honours (visibility and the 1997 renderer's detail levels are gone; the resolution is the port's window size),
        // every row in one list that scrolls over the pad's rows, the original's first; Reset, Done and Cancel stay put
        Item G(int flags, string label, int row, int n, string[]? names, int at) =>
            Scrolling(row, flags, label, n, names, s => s.Graphics[at], (s, v) => s.Graphics[at] = (byte)v, apply: () => Live?.Invoke(Settings));
        string[] offOn = ["Off", "On"], offLowHigh = ["Off", "Low", "High"];
        // Resolution's choices: the desktop (""), the window sizes that fit it, and a saved size from another screen
        var (dw, dh) = I76.Game.Boot.WindowApplier.Desktop;
        var sizes = I76.Game.Boot.WindowSizes.Offered(dw, dh).Select(z => I76.Game.Boot.WindowSizes.Format(z.W, z.H)).Prepend("").ToList();
        if (!sizes.Contains(Settings.Resolution)) sizes.Add(Settings.Resolution);
        _graphics.Items = [
            G(Numeric | Min1 | WrapAround | OneIsNumber, "Monitor Brightness", 1, 11, null, 0xc),
            G(Cycle, "Ext. Instrum. Disp.", 2, 2, offOn, 0xe),
            G(Cycle, "Rear Mirror", 3, 3, offLowHigh, 0xd),
            G(Cycle, "Shadow Detail", 4, 3, offLowHigh, 0x6),
            G(Cycle, "Terrain Detail", 5, 2, ["Low", "High"], 0x2),
            G(Cycle, "Anti-Aliasing", 6, I76.Game.Boot.RenderScale.AntiAliasing.Length, I76.Game.Boot.RenderScale.AntiAliasing, 0x8),   // the shell's list, 8x too
            // the port's own, from row 7: the window's size, Desktop (fullscreen, as the game starts) or a window of that size, applied at once
            Scrolling(7, Cycle, "Resolution", sizes.Count, [.. sizes.Select(r => I76.Game.Boot.WindowSizes.Label(I76.Game.Boot.WindowSizes.Parse(r)))],
                s => Math.Max(0, sizes.IndexOf(s.Resolution)), (s, v) => s.Resolution = sizes[v], apply: () => I76.Game.Boot.WindowApplier.Apply(Settings)),
            // the 3D view supersampled at 100, 150 or 200 % of the window, live
            Scrolling(8, Cycle, "Render Scale", I76.Game.Boot.RenderScale.Names.Length, I76.Game.Boot.RenderScale.Names, s => Math.Clamp(s.RenderScale, 0, I76.Game.Boot.RenderScale.Names.Length - 1), (s, v) => s.RenderScale = v, apply: () => Live?.Invoke(Settings)),
            // the main 3D views' field of view (the original's 90 across 4:3, or 80, 100, 110), live
            Scrolling(9, Cycle, "Field of View", I76.Sim.Views.ViewFov.Names.Length, I76.Sim.Views.ViewFov.Names, s => Math.Clamp(s.FieldOfView, 0, I76.Sim.Views.ViewFov.Names.Length - 1), (s, v) => s.FieldOfView = v, apply: () => Live?.Invoke(Settings)),
            // the 1997 look, off, the original's 3D card or its software renderer, applied on resume (as F2)
            Scrolling(10, Cycle, "1997 Mode", 3, ["Off", "3D Card", "Software"], s => !s.Look1997 ? 0 : s.SoftwareRenderer ? 2 : 1, (s, v) => { s.Look1997 = v != 0; s.SoftwareRenderer = v == 2; }, live: false),
            // the modern look's palette grade (SoftwareLook), live
            Scrolling(11, Cycle, "Palette Grade", 2, offOn, s => s.PaletteGrade ? 1 : 0, (s, v) => s.PaletteGrade = v != 0, apply: () => Live?.Invoke(Settings)),
            // the software renderer's double image on a big cockpit hit (the original's in the 1997 look, smooth in the modern look), live
            Scrolling(12, Cycle, "Crash Blur", 2, I76.Sim.Views.CrashBlurMode.Names, s => Math.Clamp(s.CrashBlur, 0, 1), (s, v) => s.CrashBlur = v, apply: () => Live?.Invoke(Settings)),
            // the modern look's plants
            Scrolling(13, Cycle, "Vegetation", 3, offLowHigh, s => s.Vegetation, (s, v) => s.Vegetation = v, apply: () => Live?.Invoke(Settings)),
            // and how far the world is drawn (either look), live
            Scrolling(14, Cycle, "Draw Distance", 2, I76.Formats.Imaging.DrawDistance.Names, s => s.DrawDistance, (s, v) => s.DrawDistance = v, apply: () => Live?.Invoke(Settings)),
            // and the frame rate limiter and vsync (drawing only; the simulation keeps its own rate), live
            Scrolling(15, Cycle, "Max FPS", I76.Sim.Views.FrameLimit.Names.Length, I76.Sim.Views.FrameLimit.Names, s => s.MaxFps, (s, v) => s.MaxFps = v, apply: () => Live?.Invoke(Settings)),
            Scrolling(16, Cycle, "VSync", 2, offOn, s => s.VSync ? 1 : 0, (s, v) => s.VSync = v != 0, apply: () => Live?.Invoke(Settings)),
            new Item { Flags = Command, Label = "Reset", Pair = 17, Cb = (_, m) => { if (m == 4) ResetGraphics(); } },
            DoneButton(),
            CancelButton(),
        ];

        Item Level(string label, int row, int n, int at, bool live) =>
            Setting(Slider | Min1, label, row, n, null, s => Math.Clamp((int)s.Audio[at], 1, n - 1), (s, v) => s.Audio[at] = (byte)v, live,
                apply: () => Live?.Invoke(Settings), changed: () => LevelPreview.Play(this, Vfs, at));   // the preview on a change only, not on Cancel's restore (msg 9)
        _audio.Items = [
            Level("Music Level", 2, 11, 0, true),
            Level("SFX Level", 5, 11, 1, true),
            Level("Voice Level", 8, 11, 2, true),
            DoneButton(),
            CancelButton(),
        ];

        if (Net)
        {
            // the port's own, on the row under the levels: the CB voice chat's microphone (the same setting as the shell's Audio Control), live
            var mics = PlayOptionsScreen.MicChoices();
            if (!mics.Contains(Settings.CbMic)) mics.Add(Settings.CbMic);
            var names = mics.Select(PlayOptionsScreen.MicLabel).ToArray();
            _audio.Items.Insert(3, Setting(Cycle, "CB Microphone", 32, mics.Count, names, s => Math.Max(0, mics.IndexOf(s.CbMic)), (s, v) => s.CbMic = mics[v],
                apply: () => Live?.Invoke(Settings)));
        }
        if (I76.Game.Assets.HiFiAudio.Installed)
        {
            // the port's own, only with a pack installed: HiFi Audio on or off (the shell's Audio Control row), live: sounds and movies started after a change follow it
            _audio.Items.Insert(_audio.Items.Count - 2, Setting(Cycle, AudioControlRows.HiFiAudio, 13, 2, ["Off", "On"], s => s.HiFiAudio ? 1 : 0, (s, v) => s.HiFiAudio = v == 1,
                apply: () => Live?.Invoke(Settings)));
        }

        if (End is { } netEnd && Net)
        {
            // a network co-op mission's end: nothing to replay or reconfigure here, the host's game is over for everyone
            _end.Items = [
                Text(netEnd.Message, 47),
                Action("Leave Game", 10, () => AskExit(_end, MenuOutcome.Abort)),
                Action("Exit Game", 11, () => AskExit(_end, MenuOutcome.Exit)),
            ];
            _end.Selected = 1;
        }
        else if (End is { } end)
        {
            _end.Items = [
                Text(end.Cheated ? "Congratulations! You won! Not too bad for a cheater. Sorry, but you can't advance." : end.Message, 47),
                // the cheater's line, or the port's checkpoint on the free line over Replay after a failure
                end.Cheated ? Action("Turn off Cheater Options", four ? 8 : 7, CheaterOff)
                    : CanRetry ? Action("Retry from Checkpoint", four ? 8 : 7, () => Close(MenuOutcome.RetryCheckpoint)) : Text(null, four ? 8 : 7),
                Action("Replay", four ? 9 : 8, () => Close(MenuOutcome.Replay)),
            ];
            if (!four) _end.Items.Add(Action("Reconfigure", 9, () => Close(MenuOutcome.Reconfigure)));
            _end.Items.Add(Action("Abort Mission", 10, () => AskExit(_end, MenuOutcome.Abort)));
            _end.Items.Add(Action("Exit Game", 11, () => AskExit(_end, MenuOutcome.Exit)));
            _end.Selected = end.Cheated || CanRetry ? 1 : 2;
        }

        // the order numbers: regenerated when the mission number changes, for t/m/n/s/p missions
        if (Mission.Length >= 3 && "tmnsp".Contains(char.ToLowerInvariant(Mission[0])) && int.TryParse(Mission.AsSpan(1, 2), out int no))
        {
            if (no != _orderMission) { _orderMission = no; _orderBase = new Random().Next(0x8000) % 992 + (no + 900) * 1000; }
            Screen?[] order = [_main, null, _abort, _play, _graphics, _audio, _exit];
            for (int k = 0; k < order.Length; k++) if (order[k] is { } sc) sc.Order = _orderBase + k;
        }
    }

    /// <summary>Reset: the default profile, then the pad again (msg 6 reloads every value).</summary>
    private void ResetGraphics()
    {
        Array.Copy(ShellSettings.DefaultGraphics, Settings.Graphics, 16);
        Settings.Resolution = "";
        I76.Game.Boot.WindowApplier.Apply(Settings);
        Settings.RenderScale = 0;
        Settings.FieldOfView = 0;
        Settings.Look1997 = false;
        Settings.SoftwareRenderer = false;
        Settings.PaletteGrade = false;
        Settings.CrashBlur = I76.Sim.Views.CrashBlurMode.On;
        Settings.Vegetation = 1;
        Settings.DrawDistance = I76.Formats.Imaging.DrawDistance.Far;
        Settings.MaxFps = I76.Sim.Views.FrameLimit.Unlimited;
        Settings.VSync = true;
        Live?.Invoke(Settings);
        Switch(_graphics);
    }

    private void CheaterOff()
    {
        Settings.PlayOptions &= ~0x1c;
        _snap.PlayOptions = Settings.PlayOptions;
        Settings.Save();
        CheatsOff?.Invoke();
        _end.Items[0].Label = "Yeah! Now you're respectable. What next?";
        _end.Items[1] = Text(null, _end.Items[1].Pair);
        _end.Selected = 2;
        QueueRedraw();
    }

    private void AskExit(Screen from, MenuOutcome yes)
    {
        _exit.Parent = from;
        _exitYes = yes;
        Switch(_exit);
    }

    // ---- flow ------------------------------------------------------------------------------------

    /// <summary>The menu screen switch: every item told msg 6, the selection kept; null closes the menu (resume).</summary>
    private void Switch(Screen? s)
    {
        if (s is null) { Close(_screen == _end ? MenuOutcome.Abort : MenuOutcome.Resume); return; }
        _screen = s;
        foreach (var it in s.Items) it.Cb?.Invoke(it, 6);
        if (s == _cheater) s.Selected = 2;
        if (s.Selected >= s.Items.Count || s.Items[s.Selected].Cb is null) s.Selected = Next(s, s.Selected, +1);
        EnsureVisible();
        QueueRedraw();
    }

    /// <summary>MenuDone: msg 8 to every item but the buttons, then the parent — or the cheater warning from Play Options with a cheat on.</summary>
    private void Done()
    {
        foreach (var it in _screen.Items) if (it.Type != Button) it.Cb?.Invoke(it, 8);
        if (_screen == _play && (Settings.PlayOptions & 0x1c) != 0) { Switch(_cheater); return; }
        Switch(_screen.Parent);
    }

    /// <summary>MenuCancel: msg 9 to every item but the buttons, then the parent.</summary>
    private void Cancel()
    {
        foreach (var it in _screen.Items) if (it.Type != Button) it.Cb?.Invoke(it, 9);
        Switch(_screen.Parent);
    }

    private bool _closed;

    private void Close(MenuOutcome outcome)
    {
        if (_closed) return;
        _closed = true;
        Settings.Save();
        Live?.Invoke(Settings);
        Closed(outcome);
        QueueFree();
    }

    private static int Next(Screen s, int from, int dir)
    {
        int n = s.Items.Count;
        for (int k = 1; k <= n; k++)
        {
            int i = ((from + dir * k) % n + n) % n;
            if (s.Items[i].Cb is not null) return i;
        }
        return Math.Clamp(from, 0, n - 1);
    }

    /// <summary>+ and − with clamping for sliders and numbers (unless they wrap), wrapping for everything else; msg 7 after.</summary>
    private static void Step(Item it, int dir)
    {
        if ((it.Flags & OwnStep) != 0) { it.Cb?.Invoke(it, dir > 0 ? 2 : 3); StepPlain(it, dir); it.Cb?.Invoke(it, 7); return; }
        StepPlain(it, dir);
        it.Cb?.Invoke(it, 7);
    }

    private static void StepPlain(Item it, int dir)
    {
        int min = (it.Flags >> 12) & 1;
        if (it.N <= 1) { it.Value = 0; return; }
        bool clamp = (it.Flags & (Slider | Numeric)) != 0 && (it.Flags & WrapAround) == 0;
        int v = it.Value + dir;
        if (v >= it.N) v = clamp ? it.N - 1 : min;
        else if (v < min) v = clamp ? min : it.N - 1;
        it.Value = v;
    }

    // ---- input ------------------------------------------------------------------------------

    private I76.Game.Shell.Shell? _host;

    /// <summary>
    /// Control Configuration (and from it the controller screen) as the shell's own screens: a shell with nothing but that overlay
    /// on the pad's place over the paused game. The pad hides while it is up and takes no input; Done saves and applies the bindings
    /// (Controls.Save puts them in use), Cancel and Esc come back unchanged. Mouse Options (from here or from Control Configuration)
    /// saves its own settings on its Done; the menu commits nothing of its own on the way there and takes only those settings back
    /// (<see cref="MouseOptionsScreen.CopyMouse"/>), so its own Done and Cancel still decide the rest.
    /// </summary>
    private void OpenControls() => OpenHosted(new ControlsScreen());
    private void OpenMouse() => OpenHosted(new MouseOptionsScreen());

    private void OpenHosted(ShellScreen screen)
    {
        if (_host is not null || GetParent() is not { } parent) return;
        Visible = false;
        _pressed = null;
        _host = new I76.Game.Shell.Shell
        {
            Install = new GameInstall(InstallDir), OnLaunch = _ => { }, Faithful = Faithful, Name = "ControlsHost",
            Hosted = screen,
            HostDone = () => Callable.From(CloseControls).CallDeferred(),
        };
        parent.AddChild(_host);
    }

    private void CloseControls()
    {
        if (_host?.Settings is { } hosted)
        {
            // the mouse's settings as Mouse Options left them (its Done saved them; its Cancel put them back), into what the menu holds
            MouseOptionsScreen.CopyMouse(hosted, Settings);
            MouseOptionsScreen.CopyMouse(hosted, _snap);
        }
        _host?.QueueFree();
        _host = null;
        _padNav.Settle();
        Visible = true;
        QueueRedraw();
    }

    public override void _Input(InputEvent e)
    {
        if (_closed || _host is not null) return;
        if (e is InputEventKey { Pressed: true } k)
        {
            GetViewport().SetInputAsHandled();
            if (!Navigate(k.Keycode, k.ShiftPressed)) return;
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel)
        {
            GetViewport().SetInputAsHandled();
            if (_pressed is not null || _screen.MaxScroll == 0) return;
            _screen.Scroll = Math.Clamp(_screen.Scroll + (wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1), 0, _screen.MaxScroll);
            QueueRedraw();
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            GetViewport().SetInputAsHandled();
            var p = ToPad(mb.Position);
            var (hit, mark) = Hit(p);
            if (mb.Pressed)
            {
                if (hit is null) return;
                _screen.Selected = _screen.Items.IndexOf(hit);
                if (mark >= 0) hit.Value = mark + 1;
                (_pressed, _pressedMark) = (hit, mark);
                QueueRedraw();
                return;
            }
            var was = _pressed;
            int wasMark = _pressedMark;
            (_pressed, _pressedMark) = (null, -1);
            if (was is null || hit != was) return;
            if (wasMark >= 0) { if (mark == wasMark) was.Cb?.Invoke(was, 7); }
            else if (was.Type is Act or Button or Command) was.Cb?.Invoke(was, 4);
            else Step(was, +1);
            QueueRedraw();
        }
    }

    /// <summary>
    /// The keyboard's navigation (and the pad's, which presses the same keys): arrows and Tab move, Right/Left/Space step a value,
    /// Enter acts or is Done, Esc cancels. Returns whether the key did anything (a redraw follows).
    /// </summary>
    private bool Navigate(Key key, bool shift = false)
    {
        if (_pressed is not null) return false;
        var s = _screen;
        var it = s.Items.Count > 0 ? s.Items[Math.Clamp(s.Selected, 0, s.Items.Count - 1)] : null;
        switch (key)
        {
            case Key.Down or Key.Kp2: s.Selected = Next(s, s.Selected, +1); break;
            case Key.Up or Key.Kp8: s.Selected = Next(s, s.Selected, -1); break;
            case Key.Tab: s.Selected = Next(s, s.Selected, shift ? -1 : +1); break;
            case (Key.Right or Key.Kp6 or Key.Space) when it?.Cb is not null && !shift: Step(it, +1); break;
            case (Key.Left or Key.Kp4) when it?.Cb is not null && !shift: Step(it, -1); break;
            case Key.Enter or Key.KpEnter when it?.Cb is not null:
                if (it.Type is Act or Button or Command) it.Cb(it, 4);
                else Done();
                break;
            case Key.Escape when !s.EscIgnored: Cancel(); break;
            default: return false;
        }
        EnsureVisible();
        QueueRedraw();
        return true;
    }

    private readonly I76.Formats.Input.PadRepeater _padNav = new();

    /// <summary>
    /// The pad (the port's): the D-pad and the left stick press the arrow keys (auto-repeating), A is Enter, B and Start are Esc.
    /// Polled, not event driven, so that a held stick repeats; the press that opened the menu (Start) is ignored until released.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_closed) return;
        using var _ = Hitch.Time(Hitch.Phase.Shell);
        if (_host is not null) { _padNav.Settle(); return; }
        var pads = Controls.PadSource.Read();
        var raw = pads.Count > 0 && Controls.Focused() ? pads[0] : default;
        var step = _padNav.Update(raw, delta);
        if (!step.Any) return;
        var dir = step.Dir switch { I76.Formats.Input.NavDir.Up => Key.Up, I76.Formats.Input.NavDir.Down => Key.Down, I76.Formats.Input.NavDir.Left => Key.Left, I76.Formats.Input.NavDir.Right => Key.Right, _ => Key.None };
        if (dir != Key.None) Navigate(dir);
        if ((step.Buttons & I76.Formats.Input.PadRepeater.Btn.Accept) != 0) Navigate(Key.Enter);
        else if ((step.Buttons & (I76.Formats.Input.PadRepeater.Btn.Back | I76.Formats.Input.PadRepeater.Btn.Start)) != 0) Navigate(Key.Escape);
    }

    /// <summary>The first selectable item whose rect A or B holds the point (edges inclusive), or one of a slider's marks.</summary>
    private (Item?, int) Hit(Vector2 p)
    {
        foreach (var it in _screen.Items)
        {
            if (it.Cb is null || !Shown(it)) continue;
            var (a, b) = Rects(it);
            if (Inside(a, p) || (b is { } bb && Inside(bb, p))) return (it, -1);
            if (it.Type == Slider)
                for (int k = 0; k < it.N - 1; k++)
                    if (Inside(new Rect4(a.X0 + 5 + 17 * k, a.Y0 + 28, a.X0 + 5 + 17 * k + 17, a.Y0 + 28 + 17), p)) return (it, k);
        }
        return (null, -1);
    }

    private static bool Inside(Rect4 r, Vector2 p) => p.X >= r.X0 && p.X <= r.X1 && p.Y >= r.Y0 && p.Y <= r.Y1;

    // ---- layout ---------------------------------------------------------------------------

    private readonly record struct Rect4(int X0, int Y0, int X1, int Y1);

    private static int RowY(int r) => 84 + 23 * (r - 1);
    private static Rect4 RowA(int r) => new(21, RowY(r), 296, RowY(r) + 24);

    /// <summary>The 640-wide menu layout, pair p: rect A (label and hit box) and rect B (the value column), in pad pixels.</summary>
    private static (Rect4 A, Rect4? B) Layout(int p) => p switch
    {
        >= 1 and <= 14 => (RowA(p), new Rect4(226, RowY(p), 301, RowY(p) + 24)),
        15 => (new(158, 407, 218, 426), null),
        16 => (new(229, 407, 289, 426), null),
        17 => (new(87, 407, 147, 426), null),
        32 => (RowA(11), new Rect4(24, RowY(12), 301, RowY(12) + 24)),   // the net game's CB Microphone: the label on the row, the device's name under it
        27 => (new(40, 245, 80, 269), null),
        28 => (new(130, 245, 180, 269), null),
        29 => (new(222, 245, 262, 269), null),
        47 => (new(21, 107, 296, 347), null),
        _ => (new(40, 25, 265, 55), null),
    };

    /// <summary>A check row on pad row <paramref name="row"/> (Play Options' list): the label's rect, and the mark's box at the pad's left edge.</summary>
    private static (Rect4 A, Rect4? B) Box(int row) => (RowA(row), new Rect4(2, RowY(row), 32, RowY(row) + 30));

    /// <summary>Whether the item is on the pad now: a scrolling row is only when it is within the window of rows.</summary>
    private bool Shown(Item it) => !it.Scrolls || (it.At - _screen.Scroll is >= Screen.FirstRow and < Screen.FirstRow + Screen.VisibleRows);

    private (Rect4 A, Rect4? B) Rects(Item it)
    {
        // a list row on pad row r: a check row as the original's check rows are laid out, any other as a row of the 640-wide menu
        int r = it.At - _screen.Scroll;
        var (a, b) = !it.Scrolls ? Layout(it.Pair) : it.Type == Check ? Box(r) : Layout(Math.Clamp(r, 1, 14));
        if (it.Type == Check) a = a with { X0 = a.X0 + 2 + 13 };
        return (a, b);
    }

    private static bool Centred(int pair) => pair is 15 or 16 or 17 or 27 or 29;

    // ---- drawing ----------------------------------------------------------------------------

    private const int PadW = 296, PadH = 425;
    private float _s = 1;
    /// <summary>The pad being drawn is an upscale: the check marks are drawn as strokes.</summary>
    private bool _hdPad;
    private Vector2 _origin;

    private void Place()
    {
        var size = GetViewportRect().Size;
        // the pad as it sits on the original's 640x480 frame (at (172, 27)), that frame fitted to the window
        _s = Mathf.Min(size.X / 640f, size.Y / 480f);
        var frame = new Vector2(640, 480) * _s;
        _origin = (size - frame) / 2 + new Vector2(172, 27) * _s;
    }

    private Vector2 ToPad(Vector2 screen) { Place(); return (screen - _origin) / _s; }
    private Vector2 At(float x, float y) => _origin + new Vector2(x, y) * _s;

    private ImageTexture? Art(string name, bool key = false)
    {
        string file = $"6{name}.vqm";
        if (_art.TryGetValue(file, out var t)) return t;
        ImageTexture? tex = null;
        if (Indices(name) is { } px)
        {
            // Audio Control's Number Of Channels boxes (row 12's eight, the dotted box under it) belong to an option this
            // port does not have: from y 338 down the Options Menu's pad, whose rows there are the same but clean
            if (name == "audcon1" && Indices("mainmn1") is { } clean && clean.Stride == px.Stride)
                Array.Copy(clean.Indices, ChannelsY * clean.Stride, px.Indices, ChannelsY * px.Stride,
                    Math.Min(clean.Indices.Length, px.Indices.Length) - ChannelsY * px.Stride);
            var img = Image.CreateEmpty(px.Width, px.Height, false, Image.Format.Rgba8);
            for (int y = 0; y < px.Height; y++)
                for (int x = 0; x < px.Width; x++)
                {
                    int i = px.Indices[y * px.Stride + x];
                    var (r, g, b) = Palette[i];
                    img.SetPixel(x, y, key && i == 255 ? Colors.Transparent : Color.Color8(r, g, b));
                }
            tex = ImageTexture.CreateFromImage(img);
        }
        _art[file] = tex;
        return tex;
    }

    /// <summary>The first pad row of Audio Control's unused channel boxes (row 12's top rule is at 337).</summary>
    private const int ChannelsY = 338;

    /// <summary>A pad's palette indices, 6&lt;name&gt;.vqm decoded (each row the whole blocks wide), or null.</summary>
    private (byte[] Indices, int Stride, int Width, int Height)? Indices(string name)
    {
        if (Vfs.TryRead($"6{name}.vqm") is not { } d) return null;
        var v = new VqmImage(d);
        if (Vfs.TryRead(v.CodebookName) is not { } cbk) return null;
        var px = v.Decode(new Codebook(cbk));
        return (px.Indices, px.Width, v.Width, v.Height);
    }

    /// <summary>
    /// An upscale of the pad <c>6&lt;name&gt;.vqm</c> for the modern look, read from the
    /// same place as the shell's backgrounds (HdAssets): 6&lt;name&gt;.webp,
    /// .png or .jpg, placed by pads.json's affine [ax, bx, ay, by] (image = a·pad + b,
    /// continuous coordinates) or, without one, the whole picture over the pad.
    /// Null in the 1997 look or when there is none.
    /// </summary>
    private (ImageTexture Tex, Rect2 Src)? HdArt(string name)
    {
        if (Faithful || !HdAssets.Enabled) return null;
        if (_hd.TryGetValue(name, out var hit)) return hit;
        if (_hdIndex is null)
        {
            _hdIndex = [];
            const string manifest = "pads.json";
            try
            {
                if (HdAssets.Read(manifest) is { } mbytes)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(mbytes);
                    foreach (var e in doc.RootElement.EnumerateObject())
                    {
                        if (e.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        string? file = e.Value.TryGetProperty("file", out var f) ? f.GetString() : null;
                        float[]? a = e.Value.TryGetProperty("affine", out var af) && af.GetArrayLength() == 4 ? [.. af.EnumerateArray().Select(v => v.GetSingle())] : null;
                        _hdIndex[e.Name.ToLowerInvariant()] = (file, a);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or System.IO.IOException)
            {
                GD.PushWarning($"escape menu pads: {manifest} unreadable ({ex.Message})");
            }
        }
        (ImageTexture, Rect2)? result = null;
        _hdIndex.TryGetValue(name.ToLowerInvariant(), out var m);
        var path = new[] { m.File, $"6{name}.webp", $"6{name}.png", $"6{name}.jpg" }
            .Where(f => f is not null).FirstOrDefault(f => HdAssets.Exists(f!));
        if (path is not null && HdAssets.LoadImage(path) is { } img)
        {
            img.GenerateMipmaps();
            var size = new Vector2(img.GetWidth(), img.GetHeight());
            var src = m.Affine is [var ax, var bx, var ay, var by]
                ? new Rect2(bx, by, ax * PadW, ay * PadH) : new Rect2(Vector2.Zero, size);
            result = (ImageTexture.CreateFromImage(img), src);
        }
        return _hd[name] = result;
    }

    /// <summary>The check mark over an upscaled pad: x1's pencil cross as two strokes (the sprite carries the low-resolution paper around it), in its 16x16 cell at (x, y).</summary>
    private void Cross(float x, float y)
    {
        var ink = Color.Color8(48, 48, 48);
        float w = Mathf.Max(1, 1.5f * _s);
        DrawLine(At(x + 2.5f, y + 1.5f), At(x + 10.5f, y + 13.5f), ink, w, true);
        DrawLine(At(x + 12.5f, y + 1.5f), At(x + 1.5f, y + 14.5f), ink, w, true);
    }

    /// <summary>A GDI font of the given cell height (lfHeight > 0), at the pad's scale.</summary>
    private int Size(Font f, int cell)
    {
        float h = f.GetHeight(cell);
        return Math.Max(1, (int)MathF.Round(cell * _s * cell / Math.Max(1f, h)));
    }

    private void Print(Font f, int cell, float x, float y, string s)
    {
        int size = Size(f, cell);
        DrawString(f, At(x, y) + new Vector2(0, f.GetAscent(size)), s, HorizontalAlignment.Left, -1, size, Ink);
    }

    private float Width(Font f, int cell, string s) => f.GetStringSize(s, HorizontalAlignment.Left, -1, Size(f, cell)).X / _s;

    public override void _Draw()
    {
        Place();
        TextureFilter = Faithful ? TextureFilterEnum.Nearest : TextureFilterEnum.LinearWithMipmaps;
        var hd = HdArt(_screen.Art);
        _hdPad = hd is not null;
        if (hd is { } h) DrawTextureRectRegion(h.Tex, new Rect2(At(0, 0), new Vector2(PadW, PadH) * _s), h.Src);
        else if (Art(_screen.Art) is { } bg) DrawTextureRect(bg, new Rect2(At(0, 0), new Vector2(PadW, PadH) * _s), false);
        if (_screen.Order > 0) Print(_arial, 16, 226, 60, $"{_screen.Order / 1000:00000}-{_screen.Order % 1000:000}");
        for (int i = 0; i < _screen.Items.Count; i++) DrawItem(_screen.Items[i], i == _screen.Selected);
        DrawScrollMark();
    }

    /// <summary>The scrolling list: a thin track before the value column with a thumb, and an arrow at an end where there is more.</summary>
    private void DrawScrollMark()
    {
        int max = _screen.MaxScroll;
        if (max <= 0) return;
        // the track starts lower than the first row's top, so the arrow over it stays on that row, clear of the pad's header
        float top = RowY(Screen.FirstRow) + 11, bottom = RowY(Screen.FirstRow + Screen.VisibleRows - 1) + 22, h = bottom - top;
        const float x = 212;
        float thumb = h * Screen.VisibleRows / _screen.Scrollable, at = top + (h - thumb) * _screen.Scroll / max;
        DrawRect(new Rect2(At(x + 2, top), new Vector2(1, h) * _s), Ink with { A = 0.35f });
        DrawRect(new Rect2(At(x, at), new Vector2(5, thumb) * _s), Ink);
        if (_screen.Scroll > 0) DrawColoredPolygon([At(x + 2.5f, top - 9), At(x - 1, top - 3), At(x + 6, top - 3)], Ink);
        if (_screen.Scroll < max) DrawColoredPolygon([At(x + 2.5f, bottom + 9), At(x - 1, bottom + 3), At(x + 6, bottom + 3)], Ink);
    }

    /// <summary>Scroll the list so the selected row shows.</summary>
    private void EnsureVisible()
    {
        var s = _screen;
        if (s.Items.Count == 0 || s.MaxScroll == 0) return;
        var it = s.Items[Math.Clamp(s.Selected, 0, s.Items.Count - 1)];
        if (!it.Scrolls) return;
        int row = it.At - Screen.FirstRow;
        s.Scroll = Math.Clamp(s.Scroll, Math.Max(0, row - Screen.VisibleRows + 1), Math.Min(row, s.MaxScroll));
    }

    /// <summary>The original's menu-item drawing: the selection bar, the label (wrapped, or centred on the buttons), then column B.</summary>
    private void DrawItem(Item it, bool selected)
    {
        if (!Shown(it)) return;
        var (font, cell) = it.Type switch { Button or Command => (_arial, 14), _ => (_lee, 18) };
        var (a, b) = Rects(it);
        if (selected && it.Cb is not null)
        {
            // GDI Rectangle: a 1-px black outline on x0..x1−1, y0..y1−1, GRAY_BRUSH inside
            var r = new Rect2(At(a.X0, a.Y0), new Vector2(a.X1 - a.X0, a.Y1 - a.Y0) * _s);
            DrawRect(r, _grey);
            DrawRect(r, Colors.Black, false, Mathf.Max(1, _s));
        }
        if (it.Label is { Length: > 0 } label)
        {
            float x = a.X0 + 3, y = a.Y0 + 3;
            if (!it.Scrolls && Centred(it.Pair)) { x += ((a.X1 - x) - Width(font, cell, label)) / 2; Print(font, cell, x, y, label); }
            else foreach (var line in Wrap(font, cell, label, a.X1 - x)) { Print(font, cell, x, y, line); y += 24; }
        }
        if (b is not { } bb && it.Type != Slider) return;
        string? value = null;
        if (it.Type == Check)
        {
            if (it.Value != 0 && b is { } cb)
            {
                if (_hdPad) Cross(cb.X0 + 5 + 13, cb.Y0 + 3);
                else if (_x is not null) DrawTextureRect(_x, new Rect2(At(cb.X0 + 5 + 13, cb.Y0 + 3), _x.GetSize() * _s), false);
            }
        }
        else if (it.Type is Slider or Numeric)
        {
            value = it.Value == 0 || ((it.Flags & Min1) != 0 && it.Value == 1 && (it.Flags & OneIsNumber) == 0) ? "Off" : $"{it.Value}.00";
            if (it.Type == Slider && (_x is not null || _hdPad))
                for (int k = 0; k < it.Value; k++)
                {
                    if (_hdPad) Cross(a.X0 + 5 + 17 * k, a.Y0 + 28);
                    else DrawTextureRect(_x!, new Rect2(At(a.X0 + 5 + 17 * k, a.Y0 + 28), _x!.GetSize() * _s), false);
                }
        }
        else if (it.Names is { } names && it.Value >= 0 && it.Value < names.Length) value = names[it.Value];
        if (value is { Length: > 0 } && b is { } vb)
        {
            float w = Width(font, cell, value), x = vb.X0;
            if (x + w > vb.X1) x = vb.X1 - w;
            Print(font, cell, x, vb.Y0, value);
        }
    }

    /// <summary>The word-wrapping TextOut loop: lines broken at spaces to the width.</summary>
    private List<string> Wrap(Font f, int cell, string text, float width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var t = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Width(f, cell, t) > width) { lines.Add(line); line = word; }
            else line = t;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
