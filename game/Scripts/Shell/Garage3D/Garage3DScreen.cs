using Godot;
using I76.Formats.Imaging;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Sim.Trip;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// CONFIGURE CHASSIS as a garage (the port's, docs/deviations.md): the car on a turntable in a bay, a notepad menu in the form's own fonts, paper
/// and art over it, a camera that flies to the part being chosen, and a live preview of whatever is under the cursor. It edits the same
/// <see cref="ChassisModel"/> the 2D form's operations make and writes the same variant file; DONE asks what the 2D form asks.
/// Pages: MAKE / MODEL and VARIANT (the melee form's lists, swapping the car on the turntable), RESPRAY (the paint schemes; a colour picker
/// will be a second row there), WHEELS (any car's, a style and a size per axle), WEAPONS and droppers by hardpoint, SPECIALS, ARMOUR
/// (the four sides' armour and chassis), ENGINE, SUSPENSION, BRAKES, NAME.
/// The look follows the 2D form: font 0x47 (the sans) for labels and numbers, 0x45 (the handwriting) for
/// every part, make and name; the lists are its own bank 0x24 pop-up frames (engines, suspensions, brakes, weapons, specials, wheels, make /
/// model, variant, paint), the buttons its own, the marks its red pencil and a check on the choice (modern look: all drawn from scratch, GarageUi.cs). F12 switches the look.
/// </summary>
public sealed class Garage3DScreen : ShellScreen, IChassisForm
{
    public required MeleeChassis Melee { get; init; }

    public override bool EscapeOpensOptions => false;
    public override bool HoverShows => true;
    public override bool TransparentFrame => true;
    public override bool[]? FrameKeep => _keep;
    public override bool TextEditing => _editing;
    public override bool SwitchesLook => true;

    // ---- the form's state ----
    private ChassisModel _m = null!;
    private MeleeData _data = null!;
    private MeleeData.Model _model = null!;
    private int _cm, _cv;
    private GarageAssets _assets = null!;
    private GarageStage? _stage;
    private SwatchLayer? _swatches;
    private bool _built, _toCatalog;
    private ShellFont F45 = null!, F47 = null!;
    /// <summary>The modern look's overlay (GarageUi.cs): everything drawn from scratch at the window's resolution; the 1997 look draws into the frame as before.</summary>
    private GarageUiLayer? _ui;
    private readonly SpriteBank?[] _strokes = new SpriteBank?[3];
    private readonly bool[] _keep = new bool[ShellFrame.W * ShellFrame.H];
    private bool Faithful => Shell.Faithful;

    // ---- the menu ----
    private enum RowKind { Normal, Entry, Adjust, Note, Button }

    /// <summary>A row: a label (font 0x47) and a value (0x45) on the notepad, an entry (0x45) in a list; Nav draws the form's arrow.</summary>
    private sealed record Row(string Label, string Value = "", bool Marked = false, string[]? Info = null, RowKind Kind = RowKind.Normal, int Tag = 0, bool Nav = false, Color? Swatch = null);

    /// <summary>A list drawn on one of the form's bank 0x24 pop-up frames: where its rows, arrows and CANCEL are (relative to the frame, as the 2D form's pickers have them).</summary>
    private sealed record ListFrame(int Sprite, int Rows, (int X0, int X1, int Y0, int Y1) List, int TextX, int TextY,
        (int X0, int X1, int Y0, int Y1)? Up, (int X0, int X1, int Y0, int Y1)? Down, (int X0, int X1, int Y0, int Y1) Cancel)
    {
        public bool Scrolls => Up is not null;

        /// <summary>The scrolling lists: the weapons (8) and wheels (10) frames, eleven rows.</summary>
        public static ListFrame Scroll(int sprite) => new(sprite, 11, (17, 155, 48, 231), 17, 48, (18, 36, 8, 24), (18, 36, 235, 251), (88, 158, 241, 257));
        public static ListFrame Paint() => new(15, 5, (13, 158, 48, 131), 18, 48, (17, 35, 10, 26), (17, 35, 135, 150), (89, 158, 141, 157));
        public static ListFrame Models() => new(7, 9, (16, 214, 47, 198), 22, 48, (18, 36, 12, 25), (18, 36, 204, 217), (146, 215, 209, 225));
        public static ListFrame Variants() => new(9, 9, (16, 158, 47, 198), 15, 50, (18, 36, 12, 25), (18, 36, 204, 217), (89, 158, 209, 225));
        // the fixed lists: rows from the list's corner + (3, 1), as many as the frame has
        public static ListFrame Engines() => Fixed(0, (13, 158, 29, 96), (88, 158, 110, 125));
        public static ListFrame Suspensions() => Fixed(1, (13, 158, 29, 97), (89, 158, 111, 124));
        public static ListFrame Brakes() => Fixed(2, (9, 158, 29, 112), (90, 158, 125, 138));
        public static ListFrame Specials(bool nitro) => Fixed(5, (13, 158, 29, 182 + (nitro ? 34 : 0)), (89, 158, 194 + (nitro ? 34 : 0), 208 + (nitro ? 34 : 0)));
        private static ListFrame Fixed(int sprite, (int, int, int, int) list, (int, int, int, int) cancel) => new(sprite, (list.Item4 - list.Item3 + 9) / 17, list, list.Item1 + 3, list.Item3 + 1, null, null, cancel);
    }

    private sealed class Page
    {
        public string Title = "";
        public Func<List<Row>> Rows = () => [];
        public Action<int>? Hover, Choose;
        /// <summary>Where the camera goes (for the row under the cursor, or -1).</summary>
        public Func<int, FocusSpec?>? Focus;
        /// <summary>Left or right on a row (an armour value).</summary>
        public Func<int, int, bool>? Step;
        public List<Row> Cache = [];
        public int Scroll;
        /// <summary>A list on a pop-up frame; else the notepad.</summary>
        public ListFrame? Art;
        /// <summary>ARMOR: the rows laid out as the form's DEFENSE block (<see cref="DefenseLayout"/>) on the notepad, not one under another.</summary>
        public bool Defense;
    }

    private readonly List<Page> _stack = [];
    private Page Cur => _stack[^1];
    private int _hover = -1, _hoverShown = -2;
    private Vector2I _lastMouse = new(-1, -1);
    private bool _editing;
    /// <summary>
    /// DONE on a changed stock variant opened the name with a free one proposed: Enter saves the new variant, Esc (or a click elsewhere)
    /// puts back <see cref="_nameBefore"/> and stays. The first key typed replaces the proposal (<see cref="_replaceName"/>).
    /// </summary>
    private bool _savingAs, _replaceName;
    private string _nameBefore = "";
    private readonly HoldRepeat _hold = new();
    private bool _dragging;

    // hover ids that are not rows
    private const int HDone = -10, HCancel = -11, HCatalog = -12, HPopCancel = -20, HUp = -21, HDown = -22;

    // ---- the layout (shell coordinates) ----
    private const int PadX = 8, PadY = 8, PadW = 268, RowH = 17, RowsY = 39, NotepadRows = 12;
    private const int FrameX = 12, FrameY = 10, ValueX = 100;
    private static readonly Rect2I TagR = new(398, 8, 232, 52), InfoR = new(398, 368, 232, 76);
    // the 2D form's own buttons (PARTS CATALOG, DONE, CANCEL), cut from its background and used as they are
    private static readonly Rect2I CatalogR = new(168, 449, 145, 23), DoneR = new(329, 449, 145, 23), CancelR = new(489, 449, 145, 23);
    private byte _ink, _bar, _rule, _red, _paper, _orange;

    private int VisibleRows => Cur.Art?.Rows ?? NotepadRows;
    private int Rows => Math.Min(VisibleRows, Cur.Cache.Count);
    private static Rect2I Panel(Page p) => p.Defense
        ? new(PadX, PadY, PadW, DefenseAt.Y + DefenseLayout.H + 8 - PadY)
        : new(PadX, PadY, PadW, RowsY - PadY + Math.Max(1, Math.Min(NotepadRows, p.Cache.Count)) * RowH + 10);

    /// <summary>The DEFENSE block's top-left on the ARMOR page: under the title bar, where the first row's rule would be.</summary>
    private static readonly Vector2I DefenseAt = new(PadX + (PadW - DefenseLayout.W) / 2, RowsY - 1);
    private static Rect2I OnBlock(Rect2I r) => new(r.Position + DefenseAt, r.Size);

    /// <summary>Where a visible row of the page is, for the mouse and the pad.</summary>
    private static Rect2I RowRect(Page p, int i) => p.Art is { } f
        ? new(FrameX + f.List.X0 + 1, FrameY + f.List.Y0 + i * RowH, f.List.X1 - f.List.X0 - 2, RowH)
        : p.Defense ? OnBlock(DefenseLayout.Row(p.Scroll + i))
        : new(PadX + 6, RowsY + i * RowH, PadW - 12, RowH);

    /// <summary>Where a press on an adjust row's [-] or [+] counts (the row by its index on the page).</summary>
    private static Rect2I StepRect(Page p, int idx, bool plus)
    {
        if (p.Defense) return OnBlock(DefenseLayout.Hit(idx, plus));
        int y = RowRect(p, idx - p.Scroll).Position.Y;
        return plus ? PlusR(y) : MinusR(y);
    }

    private static Rect2I PopRect((int X0, int X1, int Y0, int Y1) r) => new(FrameX + r.X0, FrameY + r.Y0, r.X1 - r.X0, r.Y1 - r.Y0);

    public void CancelMelee()
    {
        _m.RestoreEntry();
        Melee.Cancel();
    }

    public override void Enter()
    {
        if (!_built)
        {
            try { Build(); }
            catch (Exception ex)
            {
                // the garage could not be set up (a car it cannot assemble, a renderer it cannot use): the original's form instead
                GD.PrintErr($"garage3d: {ex}");
                Shell.Go(new GarageScreen { Melee = Melee });
                return;
            }
        }
        if (_stage is not null) _stage.Visible = true;
        if (_ui is not null) _ui.Visible = !Faithful;
        if (_swatches is not null) _swatches.Visible = true;
        _toCatalog = false;
        Repaint();
    }

    public override void Leave()
    {
        if (_toCatalog) { if (_stage is not null) _stage.Visible = false; if (_ui is not null) _ui.Visible = false; if (_swatches is not null) _swatches.Visible = false; return; }
        _stage?.QueueFree();
        _stage = null;
        _ui?.QueueFree();
        _ui = null;
        _swatches?.QueueFree();
        _swatches = null;
        // the shell's own music (or a game's going on under a hosted garage) goes on from where it was paused
        foreach (var p in _paused) if (GodotObject.IsInstanceValid(p)) { p.StreamPaused = false; GD.Print($"garage3d: {p.Name} goes on"); }
        _paused.Clear();
    }

    private readonly List<AudioStreamPlayer> _paused = [];

    /// <summary>The soundtrack of this game and, where the machine has it, the other.</summary>
    private List<I76.Formats.Vfs.GameInstall> MusicInstalls()
    {
        var all = new List<I76.Formats.Vfs.GameInstall> { Shell.Install };
        var dirs = I76.Game.Boot.LaunchOptions.OtherGameDirs();
        foreach (var d in dirs.Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            try { if (I76.Formats.Vfs.InstallSearch.IsInstall(d!) && !all.Any(a => Path.GetFullPath(a.Root) == Path.GetFullPath(d!))) all.Add(new I76.Formats.Vfs.GameInstall(d!)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return all;
    }

    /// <summary>The menu's track (or the mission's, under a hosted garage) is paused while the boombox plays.</summary>
    private void SilenceOtherMusic()
    {
        foreach (var p in Shell.GetTree().Root.FindChildren("Music*", nameof(AudioStreamPlayer), true, false).OfType<AudioStreamPlayer>())
            if (p.Playing && !p.StreamPaused) { p.StreamPaused = true; _paused.Add(p); GD.Print($"garage3d: {p.Name} paused for the garage's radio"); }
    }

    private void Build()
    {
        _data = Melee.Data ?? Shell.Melee;
        _assets = GarageAssets.For(Shell);
        GarageFonts.Load(Shell.Install.FindFile("Lee_____.ttf"));
        F45 = Shell.Db.Font(0x45);
        F47 = Shell.Db.Font(0x47);
        for (int k = 0; k < 3; k++) _strokes[k] = Shell.Db.Bank(0x21 + k);
        LoadCar(Melee.Model, Melee.Variant);
        _stage = new GarageStage { Assets = _assets, Name = "Garage3D", MusicInstalls = MusicInstalls(), MusicLevel = Shell.Settings.Audio[0] };
        Shell.AddLayer(_stage, underFrame: true);
        _swatches = new SwatchLayer { Name = "Swatches" };
        Shell.AddLayerOverFrame(_swatches);
        RaiseSprites();
        _stage.SetLook(Faithful);
        SilenceOtherMusic();
        ShowCar(_m.Snapshot(), force: true);
        _stage.Focus(HookFocus() ?? GarageViews.Overview(_stage.Car!, Faithful));
        _built = true;
        _stack.Clear();
        _stack.Add(TopPage());
        // capture hook: I76_GARAGE_PAINT=PRIMARY[,SECONDARY][,gloss|metallic|matte] (hex colours, - for none) puts the colours on the car, and
        // I76_GARAGE_PAGE=respray opens the paint scheme page
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_PAINT") is { Length: > 0 } hook)
        {
            var parts = hook.Split(',');
            var fin = parts.Select(x => Enum.TryParse<PaintFinish>(x, true, out var f) ? (PaintFinish?)f : null).FirstOrDefault(f => f is not null) ?? PaintFinish.Gloss;
            _m.SetColours(new CarPaint(PaintColour.ParseHex(parts[0]), parts.Length > 1 ? PaintColour.ParseHex(parts[1]) : null, fin));
            ShowCommitted();
        }
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_PAGE") == "respray") _stack.Add(ResprayPage());
        Refresh();
    }

    /// <summary>The upscaled sprites (the HD menu art's pop-ups) go over the 3D view, under the frame, which hides the pixels they replace.</summary>
    private void RaiseSprites()
    {
        if (Shell.SpritesLayer is { } s && _stage is not null) Shell.MoveChild(s, _stage.GetIndex());
    }

    /// <summary>The melee form's car (a model and its variant n, 1-based) as the working copy: the chassis model, the wheels' style table, the page the menu starts on.</summary>
    private void LoadCar(int model, int variant)
    {
        var vfs = Shell.Vfs;
        (_cm, _cv) = (model, variant);
        _model = _data.Models[model];
        var car = TripCar.FromVcf(vfs.Read(_data.Vcf(model, variant)), n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, n));
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_WDF") is { Length: > 0 } wdf) car.Wdf = [wdf, car.Wdf[1], wdf];   // a capture hook: the car on this wheel file
        _m = new ChassisModel(car, Shell.Parts, vfs, Shell.Install.IsNitro, Shell.Settings.HiddenContent, _data.Colours(model, variant)) { UnlimTurrets = Shell.UnlimTurrets };
        // a capture hook: a turret on every roof mount
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_TURRETS") == "1")
            foreach (var t in _m.Mounts().Where(x => _m.Inv.Slots[x.Slot].MountStyle == 1).ToList())
                if (_m.CatalogueFor(t.Slot).FirstOrDefault(c => c.Part.IsTurret) is { } tp) _m.Fit(t.Slot, tp.Part);
    }

    /// <summary>
    /// Another car in the bay (MAKE / MODEL and VARIANT): a model's first variant, or one of its variants. The working copy is given up, as
    /// the melee form's list does when it changes the car; with changes made the form asks first, in the words of the original's
    /// exit form (Tad's Auto Salvage, "EXIT WITHOUT SAVING CHANGES?"), and CANCEL still leaves the car the melee form had.
    /// </summary>
    private void SwitchTo(int model, int variant)
    {
        if (model == _cm && variant == _cv) { Pop(); return; }
        void Go()
        {
            LoadCar(model, variant);
            _wanted = null;
            _stack.RemoveRange(1, _stack.Count - 1);
            _hover = -1; _hoverShown = -2;
            ShowCar(_m.Snapshot(), force: true);
            Refresh();
        }
        if (_m.Changed || _m.Renamed) Ask("Exit without saving changes?", Go, Repaint);
        else Go();
    }

    // ---- the paper ----

    private (int X, int Y)? _patch;

    private static byte Near(byte[] pal, int r, int g, int b)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b, d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }

    /// <summary>
    /// The frame's own background: the chassis form's palette and paper (the plainest patch of its picture, mirrored to fill) where the pieces
    /// of notepad are, the form's own buttons cut from its picture, and nothing elsewhere, so the garage shows through the rest.
    /// The notepad's bar, rules and columns are painted in the colours the form's pop-ups use (read from the art).
    /// </summary>
    private void Background()
    {
        var src = Shell.Db.Background(2);
        var pal = src.Palette;
        const int N = 40;
        if (_patch is null)
        {
            int bestX = 300, bestY = 100;
            double best = double.MaxValue;
            double Lum(byte i) => 0.3 * pal[i * 3] + 0.59 * pal[i * 3 + 1] + 0.11 * pal[i * 3 + 2];
            for (int y = 60; y + N < 440; y += 6)
                for (int x = 262; x + N < 628; x += 6)
                {
                    double sum = 0, sq = 0;
                    for (int r = 0; r < N; r += 2)
                        for (int c = 0; c < N; c += 2) { double l = Lum(src.Pixels[(y + r) * src.Width + x + c]); sum += l; sq += l * l; }
                    int n = (N / 2) * (N / 2);
                    double v = sq / n - (sum / n) * (sum / n);
                    if (v < best) { best = v; bestX = x; bestY = y; }
                }
            _patch = (bestX, bestY);
        }
        (_ink, _bar, _rule, _red, _paper) = (Near(pal, 74, 57, 33), Near(pal, 222, 189, 140), Near(pal, 120, 98, 66), Near(pal, 150, 36, 22), Near(pal, 239, 189, 99));
        _orange = src.Pixels[460 * src.Width + 340];
        var (px0, py0) = _patch.Value;
        var px = new byte[ShellFrame.W * ShellFrame.H];
        Array.Clear(_keep);
        static int Mirror(int i, int n) { int m = ((i % (2 * n)) + 2 * n) % (2 * n); return m < n ? m : 2 * n - 1 - m; }
        void Paper(Rect2I r)
        {
            for (int y = r.Position.Y; y < r.End.Y; y++)
                for (int x = r.Position.X; x < r.End.X; x++)
                {
                    px[y * ShellFrame.W + x] = src.Pixels[(py0 + Mirror(y - r.Position.Y, N)) * src.Width + px0 + Mirror(x - r.Position.X, N)];
                    _keep[y * ShellFrame.W + x] = true;
                }
        }
        void Rect(Rect2I r, byte c)
        {
            for (int y = Math.Max(0, r.Position.Y); y < Math.Min(ShellFrame.H, r.End.Y); y++)
                for (int x = Math.Max(0, r.Position.X); x < Math.Min(ShellFrame.W, r.End.X); x++) { px[y * ShellFrame.W + x] = c; _keep[y * ShellFrame.W + x] = true; }
        }
        void Outline(Rect2I r, int shadow = 4)
        {
            Rect(new Rect2I(r.Position.X + shadow, r.End.Y, r.Size.X, shadow), 0x0f);
            Rect(new Rect2I(r.End.X, r.Position.Y + shadow, shadow, r.Size.Y), 0x0f);
            Paper(r);
            Rect(new Rect2I(r.Position, new Vector2I(r.Size.X, 2)), _ink); Rect(new Rect2I(r.Position.X, r.End.Y - 2, r.Size.X, 2), _ink);
            Rect(new Rect2I(r.Position, new Vector2I(2, r.Size.Y)), _ink); Rect(new Rect2I(r.End.X - 2, r.Position.Y, 2, r.Size.Y), _ink);
        }
        void Cut(Rect2I r)
        {
            for (int y = r.Position.Y; y < r.End.Y; y++)
                for (int x = r.Position.X; x < r.End.X; x++) { px[y * ShellFrame.W + x] = src.Pixels[y * src.Width + x]; _keep[y * ShellFrame.W + x] = true; }
        }
        if (!Faithful)
        {
            // nothing of the frame's own: the overlay draws the paper, the buttons and the text
            Array.Clear(_keep);
            Frame.LoadBackground(Pcx.Create(ShellFrame.W, ShellFrame.H, new byte[ShellFrame.W * ShellFrame.H], pal), 0, 0, black: false);
            return;
        }
        Outline(TagR);
        Outline(InfoR);
        foreach (var b in new[] { CatalogR, DoneR, CancelR }) Cut(b);
        var page = Cur;
        if (page.Art is null)
        {
            var pr = Panel(page);
            Outline(pr, 6);
            // the title's bar, the way the pop-ups have it, and the form's ruled table: a line under each row, the label column's edge
            Rect(new Rect2I(PadX + 8, PadY + 8, PadW - 16, 17), _bar);
            Rect(new Rect2I(PadX + 8, PadY + 8, PadW - 16, 1), _ink); Rect(new Rect2I(PadX + 8, PadY + 24, PadW - 16, 1), _ink);
            Rect(new Rect2I(PadX + 8, PadY + 8, 1, 17), _ink); Rect(new Rect2I(PadX + PadW - 9, PadY + 8, 1, 17), _ink);
            if (page.Defense)
            {
                // ARMOR: the form's own DEFENSE block, cut from its picture as it is (labels, boxes, rules, BALANCE); the values go over it
                for (int y = 0; y < DefenseLayout.H; y++)
                    for (int x = 0; x < DefenseLayout.W; x++)
                    {
                        int at = (DefenseAt.Y + y) * ShellFrame.W + DefenseAt.X + x;
                        px[at] = src.Pixels[(DefenseLayout.FormY + y) * src.Width + DefenseLayout.FormX + x];
                        _keep[at] = true;
                    }
                Frame.LoadBackground(Pcx.Create(ShellFrame.W, ShellFrame.H, px, pal), 0, 0, black: false);
                return;
            }
            int n = Math.Min(NotepadRows, page.Cache.Count);
            for (int i = 0; i <= n; i++) Rect(new Rect2I(PadX + 6, RowsY + i * RowH - 1, PadW - 12, 1), i == 0 || i == n ? _ink : _rule);
            bool labels = page.Cache.Any(r => r.Kind is RowKind.Normal or RowKind.Adjust);
            if (labels) Rect(new Rect2I(PadX + ValueX - 4, RowsY - 1, 1, n * RowH + 1), _rule);
            Rect(new Rect2I(PadX + 6, RowsY - 1, 1, n * RowH + 1), _ink); Rect(new Rect2I(PadX + PadW - 7, RowsY - 1, 1, n * RowH + 1), _ink);
        }
        Frame.LoadBackground(Pcx.Create(ShellFrame.W, ShellFrame.H, px, pal), 0, 0, black: false);
    }

    public override void Repaint()
    {
        if (!_built) return;
        Cur.Cache = Cur.Rows();
        Background();
        if (Faithful)
        {
            if (_ui is not null) _ui.Visible = false;
            Draw();
            return;
        }
        // the modern look: the frame stays empty and the overlay is drawn from scratch over the 3D view
        if (_ui is null)
        {
            _ui = new GarageUiLayer { Name = "GarageUi", Painter = PaintModern };
            Shell.AddLayer(_ui, underFrame: true);
        }
        _ui.Visible = true;
        _swatches?.Set([], false);      // the modern look draws its swatches itself
        _ui.Redraw();
    }

    public override void LookChanged()
    {
        if (!_built || _stage is null) return;
        RaiseSprites();
        _stage.SetLook(Faithful);
        LookAt(Cur.Focus?.Invoke(_hover) ?? Overview());
        _hoverShown = -2;
    }

    // ---- drawing ----

    private int Text(ShellFont f, int x, int y, string s) => Frame.Text(f, x, y, s, ShellFrame.DefaultInk);

    private static string Fit(ShellFont f, string s, int width)
    {
        if (f.Measure(s) <= width) return s;
        while (s.Length > 1 && f.Measure(s + "..") > width) s = s[..^1];
        return s + "..";
    }

    /// <summary>The lines of a text broken to a width in a font (words kept whole).</summary>
    private static List<string> Wrap(ShellFont f, string s, int width)
    {
        var lines = new List<string>();
        string line = "";
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Length == 0 ? w : line + " " + w;
            if (f.Measure(t) <= width || line.Length == 0) line = t;
            else { lines.Add(line); line = w; }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private void Keep(Rect2I r)
    {
        for (int y = Math.Max(0, r.Position.Y); y < Math.Min(ShellFrame.H, r.End.Y); y++)
            for (int x = Math.Max(0, r.Position.X); x < Math.Min(ShellFrame.W, r.End.X); x++) _keep[y * ShellFrame.W + x] = true;
    }

    /// <summary>The cursor, as the form's pop-ups show it: a red pencil mark before the row and a line under it.</summary>
    private void Pencil(int x, int y, int width, ShellFont f)
    {
        Frame.Fill(x, y + f.Height + 1, width, 1, _red);
        for (int i = 0; i < 3; i++) Frame.Fill(x - 7 + i, y + 3 + i, 1, 7 - 2 * i, _red);
    }

    /// <summary>The green highlighter behind what is on the car now (bank 0x23, as the form marks a part's health).</summary>
    /// <summary>The choice made, as a check mark in ink (the original's pick lists mark nothing; the green highlighter is the repair lists' look).</summary>
    private void Highlight(int x, int y)
    {
        foreach (var (dx, dy) in new[] { (0, 7), (1, 8), (2, 9), (3, 8), (4, 7), (5, 6), (6, 5), (7, 4), (8, 3), (9, 2) })
            Frame.Fill(x + dx, y + dy - 1, 1, 2, _ink);
    }

    private string Weight => $"{_m.Inv.Weight()} LBS";
    private static string Tenths(int v) => (v * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private void Draw()
    {
        if (!_built) return;
        var page = Cur;
        // the tag: whose car this is (the form's MAKE / MODEL and VARIANT lines)
        Text(F45, TagR.Position.X + 8, TagR.Position.Y + 3, Fit(F45, _m.MakeModel, 214));
        Text(F45, TagR.Position.X + 8, TagR.Position.Y + 18, Fit(F45, _m.Inv.Car.Variant + (_editing ? "_" : ""), 214));
        Text(F47, TagR.Position.X + 8, TagR.Position.Y + 34, Fit(F47, $"{Weight}     Armor left {Tenths(_m.Inv.Car.Ninth)}", 214));
        // the note: what the row under the cursor is
        var info = _hover >= 0 && _hover < page.Cache.Count ? page.Cache[_hover].Info : _editing && page.Cache.Count > 0 ? page.Cache[0].Info : null;
        DrawInfo(info ?? DefaultInfo());
        // the form's own buttons, lit by the pencil
        foreach (var (id, r) in new[] { (HCatalog, CatalogR), (HDone, DoneR), (HCancel, CancelR) })
            if (_hover == id) Frame.Fill(r.Position.X + 3, r.End.Y - 4, r.Size.X - 6, 1, _red);
        page.Scroll = Math.Clamp(page.Scroll, 0, Math.Max(0, page.Cache.Count - VisibleRows));
        _swatchList.Clear();
        if (page.Art is { } f) DrawFrame(page, f);
        else DrawNotepad(page);
        _swatches?.Set([.. _swatchList], !Shell.ModalOpen);
    }

    /// <summary>
    /// The note: a heading and lines, in the form's two faces: a line that is a part's, a car's or a style's name (marked "@") in the handwriting
    /// of font 0x45 as the form prints every name, the rest in the sans of 0x47.
    /// </summary>
    private void DrawInfo(string[] info)
    {
        int y = InfoR.Position.Y + 5;
        for (int i = 0; i < info.Length; i++)
        {
            if (info[i].Length == 0) continue;
            bool name = i == 0 || info[i][0] == '@';
            var font = name ? F45 : F47;
            string text = info[i][0] == '@' ? info[i][1..] : info[i];
            IEnumerable<string> lines = name ? new[] { Fit(F45, text, 214) } : Wrap(F47, text, 214);
            foreach (var line in lines)
            {
                if (y + 14 > InfoR.End.Y - 3) return;
                Text(font, InfoR.Position.X + 8, y, line);
                y += name ? 18 : 13;
            }
        }
    }

    private string[] DefaultInfo() => ["YOUR CAR", $"Weight {Weight}", "Drag to turn, wheel to zoom", "Esc back, Tab to the buttons, F12 look"];

    /// <summary>The colour swatches of the rows on show (shell coordinates): drawn true by the layer over the frame, whose own palette has no such colours.</summary>
    private readonly List<(Rect2I, Color)> _swatchList = [];

    private void DrawNotepad(Page page)
    {
        var pr = Panel(page);
        string title = _stack.Count > 1 ? string.Join(" / ", _stack.Skip(1).Select(p => p.Title)) : "CONFIGURE CHASSIS";
        title = Fit(F47, title, PadW - 24);
        Text(F47, PadX + PadW / 2 - F47.Measure(title) / 2, PadY + 8 + (17 - F47.Height) / 2, title);
        if (page.Defense) { DrawDefense(); return; }
        var arrow = Shell.Db.Bank(0x24)[14];
        for (int i = 0; i < Rows; i++)
        {
            int idx = page.Scroll + i;
            var r = RowRect(page, i);
            var row = page.Cache[idx];
            bool lit = idx == _hover && row.Kind != RowKind.Note;
            if (row.Marked) Highlight(r.End.X - 20, r.Position.Y + 1);
            switch (row.Kind)
            {
                case RowKind.Entry:
                    Text(F45, r.Position.X + 6, r.Position.Y + 1, Fit(F45, row.Label, r.Size.X - 14));
                    break;
                case RowKind.Adjust:
                    Text(F47, r.Position.X + 6, r.Position.Y + 2, row.Label);
                    DrawStep(r.Position.Y, row);
                    break;
                case RowKind.Note:
                    Text(F47, r.Position.X + 6, r.Position.Y + 2, row.Label);
                    int nx = PadX + ValueX + 5;
                    if (row.Swatch is { } nsw) { _swatchList.Add((new Rect2I(nx, r.Position.Y + 1, 20, RowH - 4), nsw)); nx += 26; }
                    Text(F47, nx, r.Position.Y + 2, row.Value);
                    break;
                case RowKind.Button:
                    DrawButton(new Rect2I(PadX + (PadW - 100) / 2, r.Position.Y + 1, 100, RowH - 3), row.Label, lit);
                    break;
                default:
                    Text(F47, r.Position.X + 6, r.Position.Y + 2, Fit(F47, row.Label, ValueX - 14));
                    int vx = PadX + ValueX + 1;
                    if (row.Swatch is { } vsw) { _swatchList.Add((new Rect2I(vx, r.Position.Y + 1, 20, RowH - 4), vsw)); vx += 25; }
                    Text(F45, vx, r.Position.Y + 1, Fit(F45, row.Value, PadW - (vx - PadX) - (row.Nav ? 30 : 14)));
                    break;
            }
            if (row.Nav) Frame.BlitAt(arrow, r.End.X - 17, r.Position.Y + 1);
            if (lit && row.Kind is not (RowKind.Adjust or RowKind.Button)) Pencil(r.Position.X + 9, r.Position.Y + 1, r.Size.X - 12, F45);
        }
        DrawScrollMark(page, pr.End.X - 12, RowsY, Rows * RowH);
    }

    /// <summary>A long list's place: a tick on the paper's right edge.</summary>
    private void DrawScrollMark(Page page, int x, int y, int height)
    {
        if (page.Cache.Count <= VisibleRows) return;
        int barH = Math.Max(10, height * VisibleRows / page.Cache.Count), barY = y + (height - barH) * page.Scroll / Math.Max(1, page.Cache.Count - VisibleRows);
        Frame.Fill(x, y, 3, height, _rule);
        Frame.Fill(x, barY, 3, barH, _ink);
    }

    /// <summary>A bank 0x24 frame with its rows in font 0x45, a pencil on the cursor and the highlighter on what the car has.</summary>
    private void DrawFrame(Page page, ListFrame f)
    {
        var art = Shell.Db.Bank(0x24)[f.Sprite];
        int w = art?.Width ?? 172, h = art?.Height ?? 136;
        Frame.Fill(FrameX + 6, FrameY + h, w, 6, 0x0f);
        Frame.Fill(FrameX + w, FrameY + 6, 6, h, 0x0f);
        Frame.Blit(art, FrameX, FrameY);
        Keep(new Rect2I(FrameX, FrameY, w + 6, h + 6));
        for (int i = 0; i < Rows; i++)
        {
            int idx = page.Scroll + i;
            var row = page.Cache[idx];
            int x = FrameX + f.TextX, y = FrameY + f.TextY + i * RowH;
            if (row.Marked) Highlight(FrameX + f.List.X1 - 16, y);
            int lx = x;
            if (row.Swatch is { } fsw) { _swatchList.Add((new Rect2I(x, y + 1, 20, RowH - 4), fsw)); lx += 25; }
            Text(F45, lx, y, Fit(F45, row.Label, f.List.X1 - f.TextX - 12 - (lx - x)));
            if (idx == _hover) Pencil(x, y, f.List.X1 - f.TextX - 6, F45);
        }
        if (_hover == HPopCancel) Frame.Fill(FrameX + f.Cancel.X0 + 4, FrameY + f.Cancel.Y1 - 3, f.Cancel.X1 - f.Cancel.X0 - 8, 1, _red);
        if (f.Scrolls)
        {
            if (_hover == HUp) Pencil(FrameX + f.Up!.Value.X1 + 8, FrameY + f.Up.Value.Y0 + 1, 8, F45);
            if (_hover == HDown) Pencil(FrameX + f.Down!.Value.X1 + 8, FrameY + f.Down.Value.Y0 + 1, 8, F45);
        }
    }

    /// <summary>A button of the notepad's own (BALANCE), in the form's button colours.</summary>
    private void DrawButton(Rect2I r, string label, bool lit)
    {
        Frame.Fill(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y, _orange);
        Frame.Fill(r.Position.X, r.Position.Y, r.Size.X, 1, _ink); Frame.Fill(r.Position.X, r.End.Y - 1, r.Size.X, 1, _ink);
        Frame.Fill(r.Position.X, r.Position.Y, 1, r.Size.Y, _ink); Frame.Fill(r.End.X - 1, r.Position.Y, 1, r.Size.Y, _ink);
        Text(F47, r.Position.X + (r.Size.X - F47.Measure(label)) / 2, r.Position.Y + (r.Size.Y - F47.Height) / 2, label);
        if (lit) Frame.Fill(r.Position.X + 2, r.End.Y - 3, r.Size.X - 4, 1, _red);
    }

    /// <summary>
    /// ARMOR in the 1997 look: the values over the form's own DEFENSE block (the background has its art), where the 2D form prints them, in
    /// font 0x47; the cell under the cursor underlined by the red pencil, BALANCE lit as the form's buttons are.
    /// </summary>
    private void DrawDefense()
    {
        var c = _m.Inv.Car;
        int ox = DefenseAt.X, oy = DefenseAt.Y;
        for (int cell = 0; cell < DefenseLayout.Cells; cell++)
        {
            var (zone, chassis) = DefenseLayout.ZoneOf(cell);
            int car = ChassisRules.Zone[zone], x = ox + DefenseLayout.ValueLeft(cell), y = oy + zone * DefenseLayout.RowH + DefenseLayout.ValueY;
            Text(F47, x, y, DefenseLayout.Value(chassis ? c.Chassis[car] : c.Armour[car]));
            if (_hover == cell)
            {
                var minus = OnBlock(DefenseLayout.Box(cell, false));
                int x0 = minus.End.X + 2, x1 = OnBlock(DefenseLayout.Box(cell, true)).Position.X - 2;
                Frame.Fill(x0, oy + zone * DefenseLayout.RowH + 15, x1 - x0, 1, _red);
            }
        }
        Text(F47, ox + DefenseLayout.LbsValueX, oy + DefenseLayout.LbsValueY, DefenseLayout.Value(c.Ninth));
        var b = OnBlock(DefenseLayout.Balance);
        if (_hover == DefenseLayout.BalanceRow) Frame.Fill(b.Position.X + 3, b.End.Y - 3, b.Size.X - 6, 1, _red);
    }

    // an armour or chassis row: [-] value [+], the form's DEFENSE buttons
    private static Rect2I MinusR(int y) => new(PadX + ValueX + 2, y + 2, 13, 13);
    private static Rect2I PlusR(int y) => new(PadX + PadW - 36, y + 2, 13, 13);

    private void DrawStep(int y, Row row)
    {
        foreach (var (r, glyph) in new[] { (MinusR(y), "-"), (PlusR(y), "+") })
        {
            Frame.Fill(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y, _paper);
            Frame.Fill(r.Position.X, r.Position.Y, r.Size.X, 1, _ink); Frame.Fill(r.Position.X, r.End.Y - 1, r.Size.X, 1, _ink);
            Frame.Fill(r.Position.X, r.Position.Y, 1, r.Size.Y, _ink); Frame.Fill(r.End.X - 1, r.Position.Y, 1, r.Size.Y, _ink);
            Text(F47, r.Position.X + (r.Size.X - F47.Measure(glyph)) / 2, r.Position.Y - 1, glyph);
        }
        int mid = (MinusR(y).End.X + PlusR(y).Position.X) / 2;
        Text(F47, mid - F47.Measure(row.Value) / 2, y + 2, row.Value);
    }

    // ---- the modern look: drawn from scratch (GarageUi.cs) ----

    private const float Base = 12.6f;   // a row's baseline below its top, for every style

    private void PaintModern(GarageUi ui)
    {
        if (!_built) return;
        var page = Cur;
        page.Scroll = Math.Clamp(page.Scroll, 0, Math.Max(0, page.Cache.Count - VisibleRows));
        // the tag: whose car this is
        ui.Paper(TagR);
        float tx = TagR.Position.X + 9, ty = TagR.Position.Y;
        ui.Text(GText.Name, GarageUi.Fit(GText.Name, _m.MakeModel, 212), tx, ty + 17);
        ui.Text(GText.Name, GarageUi.Fit(GText.Name, _m.Inv.Car.Variant + (_editing ? "_" : ""), 212), tx, ty + 33);
        ui.Text(GText.Label, GarageUi.Fit(GText.Label, $"{Weight}     Armor left {Tenths(_m.Inv.Car.Ninth)}", 212), tx, ty + 47.5f);
        // the note: what the row under the cursor is
        var info = _hover >= 0 && _hover < page.Cache.Count ? page.Cache[_hover].Info : _editing && page.Cache.Count > 0 ? page.Cache[0].Info : null;
        ui.Paper(InfoR);
        PaintInfo(ui, info ?? DefaultInfo());
        // the form's own three buttons
        ui.Button(CatalogR, "PARTS CATALOG", _hover == HCatalog);
        ui.Button(DoneR, "DONE", _hover == HDone);
        ui.Button(CancelR, "CANCEL", _hover == HCancel);
        if (page.Art is { } f) PaintList(ui, page, f);
        else PaintNotepad(ui, page);
    }

    private static void PaintInfo(GarageUi ui, string[] info)
    {
        float y = InfoR.Position.Y + 5, x = InfoR.Position.X + 9;
        for (int i = 0; i < info.Length; i++)
        {
            if (info[i].Length == 0) continue;
            bool name = i == 0 || info[i][0] == '@';
            string text = info[i][0] == '@' ? info[i][1..] : info[i];
            var style = name ? GText.Name : GText.Note;
            IEnumerable<string> lines = name ? new[] { GarageUi.Fit(style, text, 212) } : GarageUi.Wrap(style, text, 212);
            foreach (var line in lines)
            {
                float step = name ? 19 : 14;
                if (y + step > InfoR.End.Y - 2) return;
                ui.Text(style, line, x, y + (name ? 14 : 11.5f));
                y += step;
            }
        }
    }

    private void PaintNotepad(GarageUi ui, Page page)
    {
        var pr = Panel(page);
        ui.Paper(pr, 6);
        string title = _stack.Count > 1 ? string.Join(" / ", _stack.Skip(1).Select(p => p.Title)) : "CONFIGURE CHASSIS";
        ui.Bar(new Rect2(PadX + 8, PadY + 8, PadW - 16, 17), title);
        if (page.Defense) { PaintDefense(ui); return; }
        int n = Math.Min(NotepadRows, page.Cache.Count);
        bool labels = page.Cache.Any(r => r.Kind is RowKind.Normal or RowKind.Adjust);
        ui.Rules(new Rect2(PadX + 6, RowsY, PadW - 12, n * RowH), n, RowH, labels ? PadX + ValueX - 4 : null);
        for (int i = 0; i < Rows; i++)
        {
            int idx = page.Scroll + i;
            var r = RowRect(page, i);
            var row = page.Cache[idx];
            bool lit = idx == _hover && row.Kind != RowKind.Note;
            float x = r.Position.X + 6, by = r.Position.Y + Base;
            switch (row.Kind)
            {
                case RowKind.Entry:
                    ui.Text(GText.Entry, GarageUi.Fit(GText.Entry, row.Label, r.Size.X - 30), x, by);
                    break;
                case RowKind.Adjust:
                    ui.Text(GText.Label, row.Label, x, by);
                    ui.StepBox(MinusR(r.Position.Y), false, lit && false);
                    ui.StepBox(PlusR(r.Position.Y), true);
                    {
                        float mid = (MinusR(r.Position.Y).End.X + PlusR(r.Position.Y).Position.X) / 2;
                        ui.Text(GText.Label, row.Value, mid - GarageUi.Measure(GText.Label, row.Value) / 2, by);
                    }
                    break;
                case RowKind.Note:
                    ui.Text(GText.Label, row.Label, x, by);
                    {
                        float nx = PadX + ValueX + 5;
                        if (row.Swatch is { } nsw) { ui.Swatch(new Rect2(nx, r.Position.Y + 2, 20, RowH - 5), nsw); nx += 26; }
                        ui.Text(GText.Label, row.Value, nx, by);
                    }
                    break;
                case RowKind.Button:
                    ui.Button(new Rect2(PadX + (PadW - 100) / 2, r.Position.Y + 1, 100, RowH - 3), row.Label, lit, GText.ButtonSmall);
                    break;
                default:
                    ui.Text(GText.Label, GarageUi.Fit(GText.Label, row.Label, ValueX - 14), x, by);
                    {
                        float vx = PadX + ValueX + 1;
                        if (row.Swatch is { } vsw) { ui.Swatch(new Rect2(vx, r.Position.Y + 2, 20, RowH - 5), vsw); vx += 25; }
                        ui.Text(GText.Value, GarageUi.Fit(GText.Value, row.Value, PadW - (vx - PadX) - (row.Nav ? 30 : 14)), vx, by);
                    }
                    break;
            }
            if (row.Marked) ui.Check(r.End.X - 20, by);
            if (row.Nav) ui.RowTab(new Rect2(r.End.X - 17, r.Position.Y + 1.5f, 13, 14), lit);
            if (lit && row.Kind is not (RowKind.Adjust or RowKind.Button)) ui.Pencil(r.Position.X + 9, r.Position.Y + 15.4f, r.Size.X - 12);
        }
        ui.ScrollBar(pr.End.X - 12, RowsY, Rows * RowH, VisibleRows, page.Cache.Count, page.Scroll);
    }

    // the DEFENSE block's inks, sampled from the form: the zone names and LBS. AVAILABLE, the small "armor" / "chassis", DEFENSE, the rules
    private static readonly Color ZoneInk = Color.Color8(123, 100, 69), SmallInk = Color.Color8(150, 124, 88), DefenseInk = Color.Color8(83, 71, 49), BlockRule = Color.Color8(94, 79, 54);
    // the form's faces are narrower than Arial: its caps (the zone names, LBS. AVAILABLE) a size down to keep to the form's columns
    private static readonly GText Small = new(GFace.Sans, 10.5f), Caps = new(GFace.Sans, 11f);

    /// <summary>
    /// ARMOR in the modern look: the form's DEFENSE block drawn from scratch on its own geometry (<see cref="DefenseLayout"/>): DEFENSE up its
    /// shaded column, a row for each side with "armor" and "chassis" side by side, each [-] value [+], LBS. AVAILABLE with its underlined
    /// value, BALANCE; the cell under the cursor underlined by the red pencil.
    /// </summary>
    private void PaintDefense(GarageUi ui)
    {
        var c = _m.Inv.Car;
        float ox = DefenseAt.X, oy = DefenseAt.Y;
        const int W = DefenseLayout.W, H = DefenseLayout.H, B = DefenseLayout.Border, RH = DefenseLayout.RowH;
        Rect2 On(Rect2I r) => new(ox + r.Position.X, oy + r.Position.Y, r.Size.X, r.Size.Y);
        // the DEFENSE column's shade, the rules, the border (the form's frame: two units on three sides, a single rule on top)
        ui.Fill(new Rect2(ox + B, oy + 1, DefenseLayout.LabelCol - B, H - B - 1), GarageInk.Bar);
        for (int k = 1; k <= DefenseLayout.Zones; k++) ui.HLine(ox + DefenseLayout.LabelCol, ox + W - B, oy + k * RH, BlockRule);
        ui.VLine(ox + DefenseLayout.LabelCol, oy, oy + H - B, BlockRule);
        foreach (int x in new[] { DefenseLayout.ZoneCol, DefenseLayout.ArmorCol, DefenseLayout.PairCol, DefenseLayout.ChassisCol })
            ui.VLine(ox + x, oy, oy + DefenseLayout.LbsY, BlockRule);
        ui.HLine(ox, ox + W, oy, GarageInk.Ink);
        ui.Fill(new Rect2(ox, oy, B, H), GarageInk.Ink);
        ui.Fill(new Rect2(ox + W - B, oy, B, H), GarageInk.Ink);
        ui.Fill(new Rect2(ox, oy + H - B, W, B), GarageInk.Ink);
        ui.TextUp(GText.Heading, "DEFENSE", new Vector2(ox + (B + DefenseLayout.LabelCol) / 2f, oy + (H - B) / 2f), DefenseInk);
        const float Baseline = 13.8f;   // the form's caps stand on its rows' 14th line
        for (int zone = 0; zone < DefenseLayout.Zones; zone++)
        {
            float top = oy + zone * RH, by = top + Baseline;
            ui.Text(Caps, DefenseLayout.ZoneNames[zone], ox + DefenseLayout.ZoneX, by, ZoneInk);
            ui.Text(Small, "armor", ox + DefenseLayout.ArmorLabelX, by - 1, SmallInk);
            ui.Text(Small, "chassis", ox + DefenseLayout.ChassisLabelX, by - 1, SmallInk);
            foreach (bool chassis in new[] { false, true })
            {
                int cell = DefenseLayout.CellOf(zone, chassis), car = ChassisRules.Zone[zone];
                Rect2 minus = On(DefenseLayout.Box(cell, false)), plus = On(DefenseLayout.Box(cell, true));
                ui.StepBox(minus, false, form: true);
                ui.StepBox(plus, true, form: true);
                string v = DefenseLayout.Value(chassis ? c.Chassis[car] : c.Armour[car]);
                float mid = (minus.End.X + plus.Position.X) / 2;
                ui.Text(GText.Label, v, mid - GarageUi.Measure(GText.Label, v) / 2, by, GarageInk.Ink);
                if (_hover == cell) ui.Pencil(minus.End.X + 2, top + 15.4f, plus.Position.X - minus.End.X - 4, mark: false);
            }
        }
        // LBS. AVAILABLE: its value on the form's underline (moved along should the label run longer than the form's narrow face)
        float ly = oy + DefenseLayout.UnderlineY;   // the label stands on the underline's level, the value just above the line
        const string Lbs = "LBS. AVAILABLE:";
        ui.Text(Caps, Lbs, ox + DefenseLayout.LbsLabelX, ly, ZoneInk);
        float ux0 = Math.Max(ox + DefenseLayout.UnderlineX0, ox + DefenseLayout.LbsLabelX + GarageUi.Measure(Caps, Lbs) + 3);
        float ux1 = Math.Max(ox + DefenseLayout.UnderlineX1, ux0 + 40);
        ui.HLine(ux0, ux1, oy + DefenseLayout.UnderlineY, GarageInk.Ink);
        ui.Text(GText.Label, DefenseLayout.Value(c.Ninth), ux0 + (DefenseLayout.LbsValueX - DefenseLayout.UnderlineX0), ly - 2, GarageInk.Ink);
        ui.Button(On(DefenseLayout.Balance), "BALANCE", _hover == DefenseLayout.BalanceRow, GText.ButtonSmall);
    }

    /// <summary>A pick list on a paper of its own: a header bar with the page's title, the ruled rows in the handwriting, the scroll triangles and CANCEL.</summary>
    private void PaintList(GarageUi ui, Page page, ListFrame f)
    {
        var art = Shell.Db.Bank(0x24)[f.Sprite];
        int w = art?.Width ?? 172, h = art?.Height ?? 136;
        ui.Paper(new Rect2(FrameX, FrameY, w, h), 6);
        float lx = FrameX + f.List.X0, lw = f.List.X1 - f.List.X0, ly = FrameY + f.List.Y0;
        ui.Bar(new Rect2(lx, ly - 18, lw, 17), page.Title);
        ui.Rules(new Rect2(lx, ly, lw, f.Rows * RowH), f.Rows, RowH);
        for (int i = 0; i < Rows; i++)
        {
            int idx = page.Scroll + i;
            var row = page.Cache[idx];
            float x = lx + 5, top = ly + i * RowH, by = top + Base;
            float tx = x;
            if (row.Swatch is { } sw) { ui.Swatch(new Rect2(x, top + 2, 20, RowH - 5), sw); tx += 26; }
            ui.Text(GText.Entry, GarageUi.Fit(GText.Entry, row.Label, lw - (row.Marked ? 26 : 12) - (tx - x)), tx, by);
            if (row.Marked) ui.Check(lx + lw - 16, by);
            if (idx == _hover) ui.Pencil(x, top + 15.4f, lw - 12);
        }
        if (f.Scrolls)
        {
            ui.Triangle(PopRect(f.Up!.Value), true, _hover == HUp);
            ui.Triangle(PopRect(f.Down!.Value), false, _hover == HDown);
        }
        ui.Button(PopRect(f.Cancel), "CANCEL", _hover == HPopCancel, GText.ButtonSmall);
    }

    // ---- the car ----

    private string _shownName = "";

    private void ShowCar(TripCar car, bool force = false) => ShowCar(car, _m.Colours, force);

    private void ShowCar(TripCar car, CarPaint? paint, bool force = false)
    {
        if (_stage is null) return;
        if (paint is { IsNone: true }) paint = null;
        var name = _assets.NameOf(car.ToVcf()) + (paint is null ? "" : Convert.ToHexString(paint.ToBytes()));
        if (!force && name == _shownName) return;
        try { _stage.SetCar(car, paint); _shownName = name; }
        catch (Exception ex) { GD.PrintErr($"garage3d: cannot show {name}: {ex.Message}"); }
    }

    /// <summary>The car as it is, the cursor's preview gone.</summary>
    private void ShowCommitted() => ShowCar(_m.Snapshot());

    /// <summary>The car as it is, with this change shown on it.</summary>
    private void Preview(Action<TripCar> tweak)
    {
        var car = _m.Snapshot();
        tweak(car);
        ShowCar(car);
    }

    /// <summary>The car with these colours shown on it (the cursor on a colour; nothing is chosen until the click).</summary>
    private void PreviewPaint(CarPaint? paint) => ShowCar(_m.Snapshot(), paint);

    private FocusSpec Overview(float closer = 1f) => GarageViews.Overview(_stage!.Car!, Faithful, closer);

    // ---- the pages ----

    private void Push(Page p)
    {
        _stack.Add(p);
        _hover = -1; _hoverShown = -2;
        Shell.Sound(82);
        Refresh();
    }

    private void Pop()
    {
        if (_stack.Count <= 1) return;
        _stack.RemoveAt(_stack.Count - 1);
        _hover = -1; _hoverShown = -2;
        _wanted = null;
        ShowCommitted();
        _stage?.Mark(null);
        Refresh();
    }

    /// <summary>Capture hook: I76_GARAGE_FOCUS=overview|hp:N|wheel:front|wheel:rear|side:N|engine|specials|under holds that view whatever page is open.</summary>
    private void LookAt(FocusSpec spec) => _stage!.Focus(HookFocus() ?? spec);

    private FocusSpec? HookFocus()
    {
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_FOCUS") is not { Length: > 0 } h || _stage?.Car is not { } c) return null;
        var a = h.Split(':');
        int n = a.Length > 1 && int.TryParse(a[1], out int v) ? v : 0;
        return a[0] switch
        {
            "hp" => GarageViews.Hardpoint(c, n, Faithful),
            "wheel" => GarageViews.Wheel(c, a.Length > 1 && a[1] == "rear" ? Axle.Rear : Axle.Front, Faithful),
            "side" => GarageViews.Side(c, n, Faithful),
            "engine" => GarageViews.Engine(c, Faithful),
            "specials" => GarageViews.Specials(c, Faithful),
            "under" => GarageViews.Underside(c, Faithful),
            _ => GarageViews.Overview(c, Faithful),
        };
    }

    private void Refresh()
    {
        if (_stage?.Car is null) return;
        _stage.Mark(null);
        var f = Cur.Focus?.Invoke(-1);
        _stage.Focus(HookFocus() ?? f ?? Overview());
        Repaint();
    }

    private Page TopPage() => new()
    {
        Title = "GARAGE",
        Rows = () =>
        {
            var c = _m.Inv.Car;
            string Slot(int type) => _m.Fitted(_m.SlotOf(type))?.Part.Id ?? "Empty";
            int guns = _m.Mounts().Count(x => _m.Fitted(x.Slot) is not null);
            return
            [
                new("Make / Model", _model.MakeModel, Info: ["MAKE / MODEL", "@" + _model.MakeModel, "ANOTHER CAR IN THE BAY", $"{_data.Models.Count} CARS"], Nav: true),
                new("Variant", c.Variant, Info: ["VARIANT", "@" + c.Variant, "ONE OF THIS CAR'S VARIANTS", $"{_model.Variants.Count} OF THEM, {_model.Base} STOCK"], Nav: true),
                new("Respray", _m.Paint?.Id ?? "", Info: ["RESPRAY", "THE PAINT SCHEMES OF THIS MODEL,", "AND YOUR OWN COLOURS"], Nav: true),
                _stage?.Car is { IsHeli: true }
                    ? new("Wheels", "None", Info: ["WHEELS", "A HELICOPTER RIDES ON SKIDS"])
                    : new("Wheels", WheelStyles.NameOf(WheelStyles.Family(_m.WheelStem(Axle.Front) ?? "")), Info: ["WHEELS", "A STYLE AND A SIZE FOR EACH AXLE", "ANY CAR'S STYLE"], Nav: true),
                new("Weapons", $"{guns} / {_m.Mounts().Count}", Info: ["WEAPONS", "ONE HARDPOINT AT A TIME"], Nav: true),
                new("Specials", $"{_m.SpecialSlots().Count(x => _m.Fitted(x.Slot) is not null)} / 3", Info: ["SPECIALS"], Nav: true),
                new("Armor", Tenths(c.Ninth) + " left", Info: ["ARMOR", "THE FOUR SIDES' ARMOR AND CHASSIS"], Nav: true),
                new("Engine", Slot(2), Info: ["ENGINE"], Nav: true),
                new("Suspension", Slot(3), Info: ["SUSPENSION"], Nav: true),
                new("Brakes", Slot(4), Info: ["BRAKES"], Nav: true),
                new("Name", c.Variant, Info: ["NAME", "RENAME THIS VARIANT", "A NEW NAME SAVES A NEW VARIANT"], Nav: true),
            ];
        },
        Hover = i => { },
        Choose = i =>
        {
            switch (i)
            {
                case 0: Push(ModelsPage()); break;
                case 1: Push(VariantsPage()); break;
                case 2: Push(ResprayPage()); break;
                case 3: if (_stage?.Car is not { IsHeli: true }) Push(AxlesPage()); break;
                case 4: Push(SlotsPage("WEAPONS", () => _m.Mounts(), weapons: true)); break;
                case 5: Push(SlotsPage("SPECIALS", () => _m.SpecialSlots(), weapons: false)); break;
                case 6: Push(ArmourPage()); break;
                case 7: Push(ComponentPage("ENGINE", 2, ListFrame.Engines())); break;
                case 8: Push(ComponentPage("SUSPENSION", 3, ListFrame.Suspensions())); break;
                case 9: Push(ComponentPage("BRAKES", 4, ListFrame.Brakes())); break;
                case 10: Push(NamePage()); _editing = true; break;
            }
        },
    };

    // ---- MAKE / MODEL and VARIANT: the melee form's lists ----

    private readonly Dictionary<(int, int), TripCar> _carCache = [];
    private (int M, int V)? _wanted;
    private double _wantedFor;
    private const double PreviewDelay = 0.12;

    /// <summary>A model's variant as a trip car, kept (hovering down a list asks for each in turn; the assembled parts are cached too, GarageAssets).</summary>
    private TripCar CarOf(int model, int variant)
    {
        if (!_carCache.TryGetValue((model, variant), out var c))
            _carCache[(model, variant)] = c = TripCar.FromVcf(Shell.Vfs.Read(_data.Vcf(model, variant)), n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(Shell.Vfs, n));
        return c;
    }

    /// <summary>The cursor on another car in MAKE / MODEL or VARIANT: it shows on the turntable once the cursor rests (a fast scroll builds none on the way), and the committed car comes back when the cursor leaves.</summary>
    private void PreviewCarOf(int model, int variant)
    {
        if (model == _cm && variant == _cv) { _wanted = null; ShowCommitted(); return; }
        _wanted = (model, variant);
        _wantedFor = 0;
    }

    private void TickPreview()
    {
        if (_wanted is not { } w) return;
        _wantedFor += Shell.Delta;
        if (_wantedFor < PreviewDelay) return;
        _wanted = null;
        try { ShowCar(CarOf(w.M, w.V)); }
        catch (Exception ex) { GD.PrintErr($"garage3d: cannot preview {w}: {ex.Message}"); }
    }

    private Page ModelsPage()
    {
        var look = Overview();      // the camera stays where it is while the cars change
        return new()
        {
            Title = "MAKE / MODEL",
            Art = ListFrame.Models(),
            Rows = () => [.. _data.Models.Select((m, i) => new Row(m.MakeModel, Marked: i == _cm,
                Info: [m.MakeModel, $"{m.Variants.Count} VARIANTS, {m.Base} STOCK", m.HiddenModel ? "A HIDDEN CAR" : "", i == _cm ? "THE CAR IN THE BAY" : "PICK: ITS FIRST VARIANT"]))],
            Focus = _ => look,
            Hover = i => { if (i >= 0 && i < _data.Models.Count) PreviewCarOf(i, 1); else PreviewCarOf(_cm, _cv); },
            Choose = i => SwitchTo(i, 1),
        };
    }

    private Page VariantsPage()
    {
        var look = Overview();
        return new()
        {
            Title = "VARIANT",
            Art = ListFrame.Variants(),
            Rows = () => [.. _model.Variants.Select((v, i) => new Row(v, Marked: i + 1 == _cv, Info: [v, i < _model.Base ? "A STOCK VARIANT" : "ONE OF YOURS", i + 1 == _cv ? "THE ONE IN THE BAY" : ""]))],
            Focus = _ => look,
            Hover = i => { if (i >= 0 && i < _model.Variants.Count) PreviewCarOf(_cm, i + 1); else PreviewCarOf(_cm, _cv); },
            Choose = i => SwitchTo(_cm, i + 1),
        };
    }

    // RESPRAY: the model's own paint schemes, and I26's own colours for the scheme's primary paint and, where it has one, its secondary
    private bool HasSecondary => _stage?.Car is { } car && _assets.Meshes.PaintMasksOf(car.Model)?.HasSecondary == true;

    private Page ResprayPage() => new()
    {
        Title = "RESPRAY",
        Rows = () =>
        {
            var c = _m.Colours;
            var rows = new List<Row>
            {
                new("Paint Scheme", _m.Paint?.Id ?? "", Info: ["PAINT SCHEME", "THE MODEL'S OWN SCHEMES"], Tag: 0, Nav: true),
                new("Primary", ColourName(c?.Primary), Info: ["PRIMARY COLOUR", "THE BODY PAINT IN A COLOUR OF YOUR OWN,", "SEEN BY EVERYONE IN A NETWORK GAME"], Tag: 1, Nav: true, Swatch: SwatchOf(c?.Primary)),
            };
            if (HasSecondary) rows.Add(new("Secondary", ColourName(c?.Secondary), Info: ["SECONDARY COLOUR", "THIS SCHEME'S SECOND COLOUR", "(STRIPES, ROOF, TRIM)"], Tag: 2, Nav: true, Swatch: SwatchOf(c?.Secondary)));
            rows.Add(new("Finish", (c?.Finish ?? PaintFinish.Gloss).ToString(), Info: ["FINISH", "GLOSS, METALLIC OR MATTE", "FOR YOUR OWN COLOURS"], Tag: 3, Nav: true));
            return rows;
        },
        Focus = _ => Overview(0.85f),
        Choose = i =>
        {
            switch (Cur.Cache[i].Tag)
            {
                case 0: Push(SchemesPage()); break;
                case 1: Push(ColoursPage(secondary: false)); break;
                case 2: Push(ColoursPage(secondary: true)); break;
                case 3: Push(FinishPage()); break;
            }
        },
    };

    private static Color? SwatchOf(PaintColour? c) => c is { } k ? Color.Color8(k.R, k.G, k.B) : null;

    private static string ColourName(PaintColour? c) =>
        c is not { } k ? "Factory" : Classic.FirstOrDefault(x => x.Colour == k).Name ?? "Custom";

    /// <summary>Classic American car paints of the 1970s, for the picker.</summary>
    private static readonly (string Name, PaintColour Colour)[] Classic =
    [
        ("Hugger Orange", new(0xF0, 0x54, 0x1E)), ("Grabber Blue", new(0x1E, 0x9B, 0xE0)), ("Plum Crazy", new(0x5B, 0x1E, 0x8C)),
        ("Sublime", new(0x8D, 0xD0, 0x2A)), ("Hemi Orange", new(0xFF, 0x8C, 0x1A)), ("Rallye Red", new(0xC4, 0x1E, 0x1E)),
        ("Forest Green", new(0x1E, 0x4D, 0x2B)), ("Black", new(0x0E, 0x0E, 0x0F)), ("White", new(0xF2, 0xF2, 0xEE)),
        ("Silver", new(0xB9, 0xBC, 0xC0)), ("Gold", new(0xC9, 0xA2, 0x27)), ("Brown", new(0x5A, 0x3A, 0x1E)),
        ("Lemon Twist", new(0xF5, 0xD6, 0x0A)), ("Candy Apple", new(0x9B, 0x11, 0x1E)), ("Midnight Blue", new(0x1C, 0x2E, 0x6B)),
        ("Burnt Copper", new(0xA8, 0x44, 0x1C)), ("Avocado", new(0x6B, 0x7A, 0x2A)), ("Champagne", new(0xD9, 0xC7, 0xA0)),
        ("Burgundy", new(0x6B, 0x1B, 0x2A)), ("Panther Pink", new(0xE8, 0x62, 0x8F)), ("Turquoise", new(0x2A, 0xA6, 0xA0)),
        ("Powder Blue", new(0x7F, 0xB2, 0xD9)), ("Gunmetal", new(0x4A, 0x4F, 0x55)), ("Olive Drab", new(0x5B, 0x60, 0x30)),
    ];

    /// <summary>The colours as they would be with <paramref name="c"/> as the primary (or secondary) paint.</summary>
    private CarPaint With(bool secondary, PaintColour? c)
    {
        var b = _m.Colours ?? new CarPaint(null);
        return secondary ? b with { Secondary = c } : b with { Primary = c };
    }

    /// <summary>Picks the primary (or secondary) colour on the paint scheme's frame: factory, a colour of your own, or one of the classic paints; the cursor on one shows it on the car.</summary>
    private Page ColoursPage(bool secondary)
    {
        PaintColour? Now() => secondary ? _m.Colours?.Secondary : _m.Colours?.Primary;
        return new()
        {
            Title = secondary ? "SECONDARY" : "PRIMARY",
            Art = ListFrame.Paint(),
            Rows = () =>
            {
                var now = Now();
                var rows = new List<Row>
                {
                    new("Factory", Marked: now is null, Info: ["FACTORY", "THE SCHEME'S OWN PAINT"]),
                    new("Custom...", Info: ["CUSTOM", "HUE, SATURATION AND BRIGHTNESS"]),
                };
                rows.AddRange(Classic.Select(x => new Row(x.Name, Marked: now == x.Colour, Info: [x.Name, x.Colour.Hex.ToUpperInvariant(), "CLICK TO PAINT"], Swatch: SwatchOf(x.Colour))));
                return rows;
            },
            Focus = _ => Overview(0.85f),
            Hover = i =>
            {
                if (i < 0 || i == 1) ShowCommitted();
                else PreviewPaint(With(secondary, i == 0 ? null : Classic[i - 2].Colour));
            },
            Choose = i =>
            {
                if (i == 1) { Push(CustomPage(secondary)); return; }
                _m.SetColours(With(secondary, i == 0 ? null : Classic[i - 2].Colour));
                ShowCommitted();
                Pop();
            },
        };
    }

    /// <summary>A colour of your own by hue, saturation and brightness; the car shows it as it is set, ESC puts it back.</summary>
    private Page CustomPage(bool secondary)
    {
        var start = secondary ? _m.Colours?.Secondary : _m.Colours?.Primary;
        float h = 24f, sat = 0.85f, v = 0.9f;
        if (start is { } k) { var hsv = ResprayPaint.ToHsv(k.R / 255f, k.G / 255f, k.B / 255f); (h, sat, v) = (hsv.H, hsv.S, hsv.V); }
        PaintColour Colour()
        {
            var (r, g, b) = ResprayPaint.FromHsv(h, sat, v);
            return new PaintColour((byte)Math.Clamp(MathF.Round(r * 255), 0, 255), (byte)Math.Clamp(MathF.Round(g * 255), 0, 255), (byte)Math.Clamp(MathF.Round(b * 255), 0, 255));
        }
        void Show() => PreviewPaint(With(secondary, Colour()));
        var page = new Page
        {
            Title = "CUSTOM",
            Rows = () =>
            [
                new("Hue", ((int)MathF.Round(h)).ToString(), Info: ["HUE", "LEFT / RIGHT, OR THE - + BUTTONS"], Kind: RowKind.Adjust, Tag: 0),
                new("Saturation", ((int)MathF.Round(sat * 100)).ToString(), Info: ["SATURATION", "0 IS GREY, 100 IS FULL COLOUR"], Kind: RowKind.Adjust, Tag: 1),
                new("Brightness", ((int)MathF.Round(v * 100)).ToString(), Info: ["BRIGHTNESS", "0 IS BLACK"], Kind: RowKind.Adjust, Tag: 2),
                new("Your colour", Colour().Hex.ToUpperInvariant(), Kind: RowKind.Note, Swatch: SwatchOf(Colour())),
                new("USE COLOUR", "", Info: ["USE COLOUR", "PAINT THE CAR THIS COLOUR"], Kind: RowKind.Button, Tag: 99),
            ],
            Focus = _ => Overview(0.85f),
            Step = (i, dir) =>
            {
                switch (i)
                {
                    case 0: h = (h + dir * 5 + 360) % 360; break;
                    case 1: sat = Math.Clamp(sat + dir * 0.05f, 0, 1); break;
                    case 2: v = Math.Clamp(v + dir * 0.05f, 0, 1); break;
                    default: return false;
                }
                Show();
                return true;
            },
        };
        page.Choose = i =>
        {
            if (page.Cache[i].Kind != RowKind.Button) return;
            _m.SetColours(With(secondary, Colour()));
            ShowCommitted();
            Pop();
            Pop();
        };
        // the car shows the colour from the start
        Callable.From(Show).CallDeferred();
        return page;
    }

    private Page FinishPage()
    {
        PaintFinish[] all = [PaintFinish.Gloss, PaintFinish.Metallic, PaintFinish.Matte];
        string[] notes = ["THE CAR'S OWN CLEAR COAT", "A STEEPER SHADE AND A FLAKE UNDER THE COAT", "NO COAT, A FLAT PAINT"];
        PaintFinish Now() => _m.Colours?.Finish ?? PaintFinish.Gloss;
        CarPaint With(PaintFinish f) => (_m.Colours ?? new CarPaint(null)) with { Finish = f };
        return new()
        {
            Title = "FINISH",
            Rows = () => [.. all.Select((f, k) => new Row(f.ToString(), Kind: RowKind.Entry, Marked: Now() == f, Info: [f.ToString().ToUpperInvariant(), notes[k], "THE MODERN LOOK; THE 1997 LOOK SHOWS ONLY THE COLOUR"]))],
            Focus = _ => Overview(0.85f),
            Hover = i => { if (i >= 0 && i < all.Length) PreviewPaint(With(all[i])); else ShowCommitted(); },
            Choose = i => { _m.SetColours(With(all[i])); ShowCommitted(); Pop(); },
        };
    }

    private Page SchemesPage()
    {
        var schemes = _m.PaintSchemes();
        return new()
        {
            Title = "SCHEME",
            Art = ListFrame.Paint(),
            Rows = () => [.. schemes.Select(p => new Row(p.Id, Marked: _m.Inv.Car.Vtf.Equals(p.Stem, StringComparison.OrdinalIgnoreCase), Info: [p.Id, "A PAINT SCHEME OF THIS MODEL"]))],
            Focus = _ => Overview(0.8f),
            Hover = i => { if (i >= 0 && i < schemes.Count) Preview(c => c.Vtf = schemes[i].Stem); else ShowCommitted(); },
            Choose = i => { _m.SetPaint(schemes[i].Stem); ShowCommitted(); Pop(); },
        };
    }

    // ---- WHEELS: a style and a size for each axle ----

    private Dictionary<string, List<string>>? _wearers;

    /// <summary>The cars of this game's list that wear each style (its first variant's files), by make and model.</summary>
    private Dictionary<string, List<string>> Wearers()
    {
        if (_wearers is not null) return _wearers;
        var d = new Dictionary<string, List<string>>();
        for (int i = 0; i < _data.Models.Count; i++)
        {
            try
            {
                var c = TripCar.FromVcf(Shell.Vfs.Read(_data.Vcf(i, 1)));
                foreach (var w in c.Wdf.Where(w => w.Length >= 7 && !w.Equals("null", StringComparison.OrdinalIgnoreCase)).Select(WheelStyles.Family).Distinct())
                {
                    if (!d.TryGetValue(w, out var l)) d[w] = l = [];
                    if (!l.Contains(_data.Models[i].MakeModel)) l.Add(_data.Models[i].MakeModel);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or KeyNotFoundException) { }
        }
        return _wearers = d;
    }

    private Page AxlesPage()
    {
        var axles = new[] { Axle.Front, Axle.Middle, Axle.Rear }.Where(_m.HasAxle).ToList();
        string SizeOf(Axle a) => _m.AllWheels().FirstOrDefault(w => w.Stem.Equals(_m.WheelStem(a), StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        string Style(Axle a) => WheelStyles.NameOf(WheelStyles.Family(_m.WheelStem(a) ?? ""));
        return new()
        {
            Title = "WHEELS",
            Rows = () => [.. axles.SelectMany(a => new[]
            {
                new Row($"{a} style", Style(a), Info: [$"{a.ToString().ToUpperInvariant()} STYLE", "@" + Style(a), "How the rim and tyre look"], Tag: (int)a * 2, Nav: true),
                new Row($"{a} size", SizeOf(a), Info: [$"{a.ToString().ToUpperInvariant()} SIZE", "@" + SizeOf(a), "Grip, mass and hits"], Tag: (int)a * 2 + 1, Nav: true),
            })],
            Focus = i => GarageViews.Wheel(_stage!.Car!, i >= 0 && i < Cur.Cache.Count ? (Axle)(Cur.Cache[i].Tag / 2) : axles[0], Faithful),
            Choose = i => { var a = (Axle)(Cur.Cache[i].Tag / 2); Push(Cur.Cache[i].Tag % 2 == 0 ? StylePage(a) : SizePage(a)); },
        };
    }

    private string SizeLabel(char letter) => _m.AllWheels().FirstOrDefault(w => WheelStyles.SizeOf(w.Stem) == letter)?.Id ?? letter.ToString();

    private Page StylePage(Axle axle)
    {
        var all = _m.AllWheels();
        var families = WheelStyles.Families(all);
        char size = WheelStyles.SizeOf(_m.WheelStem(axle) ?? "a");
        string cur = WheelStyles.Family(_m.WheelStem(axle) ?? "");
        return new()
        {
            Title = axle.ToString().ToUpperInvariant() + " STYLE",
            Art = ListFrame.Scroll(10),
            Rows = () => [.. families.Select(f =>
            {
                var pick = WheelStyles.Pick(all, f, size, out bool exact);
                var cars = Wearers().TryGetValue(f, out var l) ? l : [];
                string[] info =
                [
                    WheelStyles.NameOf(f),
                    cars.Count > 0 ? "As on the " + string.Join(", ", cars.Take(2)) + (cars.Count > 2 ? " and others" : "") : "On no car of the list",
                    pick is null ? "" : exact ? $"Keeps the size: {pick.Id}" : $"No {SizeLabel(size)} in this style: {pick.Id} instead",
                    $"File {f}{(pick is null ? "" : WheelStyles.SizeOf(pick.Stem))}.wdf",
                ];
                return new Row(WheelStyles.NameOf(f), Marked: f == cur, Info: info);
            })],
            Focus = _ => GarageViews.Wheel(_stage!.Car!, axle, Faithful),
            Hover = i =>
            {
                if (i >= 0 && i < families.Count && WheelStyles.Pick(all, families[i], size, out _) is { } w) Preview(c => c.Wdf[(int)axle] = w.Stem);
                else ShowCommitted();
            },
            Choose = i =>
            {
                if (WheelStyles.Pick(all, families[i], size, out _) is { } w) _m.FitAxle(axle, w);
                ShowCommitted();
                Pop();
            },
        };
    }

    private Page SizePage(Axle axle)
    {
        var all = _m.AllWheels();
        string fam = WheelStyles.Family(_m.WheelStem(axle) ?? "");
        var sizes = WheelStyles.Sizes(all, fam);
        var stats = sizes.Select(w => _m.WheelStats(w)).ToList();
        return new()
        {
            Title = axle.ToString().ToUpperInvariant() + " SIZE",
            Art = ListFrame.Scroll(10),
            Rows = () =>
            {
                var cur = _m.WheelStem(axle);
                return [.. sizes.Select((w, k) => new Row(w.Id, Marked: cur is not null && w.Stem.Equals(cur, StringComparison.OrdinalIgnoreCase),
                    Info: [w.Id, stats[k] is { } t ? $"Grip {t.Grip:0.00}   Mass {t.Mass:0}   Hits {t.Hp}" : "", $"File {w.Stem}", sizes.Count == 1 ? "The only size of this style" : ""]))];
            },
            Focus = _ => GarageViews.Wheel(_stage!.Car!, axle, Faithful),
            Hover = i => { if (i >= 0 && i < sizes.Count) Preview(c => c.Wdf[(int)axle] = sizes[i].Stem); else ShowCommitted(); },
            Choose = i => { _m.FitAxle(axle, sizes[i]); ShowCommitted(); Pop(); },
        };
    }

    // ---- weapons, droppers, specials ----

    private Page SlotsPage(string title, Func<List<ChassisModel.Mount>> mountsOf, bool weapons)
    {
        return new()
        {
            Title = title,
            Rows = () => [.. mountsOf().Select(s =>
            {
                var fitted = _m.Fitted(s.Slot);
                return new Row(s.Label, fitted?.Part.Id ?? "Empty", false, [s.Label.ToUpperInvariant(), "@" + (fitted?.Part.Id ?? "Empty"), fitted is null ? "" : $"Weight {fitted.Part.Weight:0} lbs"], Nav: true);
            })],
            Focus = i =>
            {
                var l = mountsOf();
                if (weapons && i >= 0 && i < l.Count && l[i].Hardpoint >= 0) return GarageViews.Hardpoint(_stage!.Car!, l[i].Hardpoint, Faithful);
                return weapons && l.Count > 0 && l[0].Hardpoint >= 0 && i < 0 ? GarageViews.Hardpoint(_stage!.Car!, l[0].Hardpoint, Faithful) : weapons ? null : GarageViews.Specials(_stage!.Car!, Faithful);
            },
            Hover = i =>
            {
                var l = mountsOf();
                _stage?.Mark(weapons && i >= 0 && i < l.Count && _stage.Car!.Hardpoints.TryGetValue(l[i].Hardpoint, out var h) ? h.Pos : null);
            },
            Choose = i => Push(PartsPage(mountsOf()[i], weapons, mountsOf())),
        };
    }

    /// <summary>The parts a slot may take (<see cref="ChassisModel.CatalogueFor"/>: the original's rule for the hardpoint), after an Empty row.</summary>
    private Page PartsPage(ChassisModel.Mount mount, bool weapons, List<ChassisModel.Mount> all)
    {
        var items = _m.CatalogueFor(mount.Slot);
        var sameName = weapons ? null : all.Select(x => x.Slot).ToList();
        int rows = items.Count + 1;
        var pop = mount.Type == 0xc ? ListFrame.Specials(Shell.Install.IsNitro) : ListFrame.Scroll(8);
        return new()
        {
            Title = mount.Label.ToUpperInvariant(),
            Art = pop.Rows >= rows || pop.Scrolls ? pop : null,
            Rows = () =>
            {
                var fitted = _m.Fitted(mount.Slot);
                var list = new List<Row> { new("Empty", "", fitted is null, ["EMPTY", "Nothing in this " + (weapons ? "mount" : "slot")], RowKind.Entry) };
                list.AddRange(items.Select(n => new Row(n.Part.Id, "", fitted is not null && fitted.Part.Stem.Equals(n.Part.Stem, StringComparison.OrdinalIgnoreCase),
                    mount.Type == 0xc ? [n.Part.Id, .. SpecialNotes(n.Part.Ordinal)]
                    : [n.Part.Id, $"Weight {n.Part.Weight:0} lbs", $"Hits {n.Part.MaxHp}", n.Part.IsTurret ? "A turret: roof mount only" : ""], RowKind.Entry)));
                return list;
            },
            Focus = _ => weapons && mount.Hardpoint >= 0 ? GarageViews.Hardpoint(_stage!.Car!, mount.Hardpoint, Faithful) : GarageViews.Specials(_stage!.Car!, Faithful),
            Hover = i =>
            {
                if (_stage is null) return;
                _stage.Mark(weapons && _stage.Car!.Hardpoints.TryGetValue(mount.Hardpoint, out var h) ? h.Pos : null);
                if (!weapons) return;
                if (i < 0) { ShowCommitted(); return; }
                string gdf = i == 0 ? "null" : items[i - 1].Part.Stem;
                Preview(c =>
                {
                    int at = c.Weapons.FindIndex(w => w.Hardpoint == mount.Hardpoint);
                    if (at >= 0) c.Weapons[at] = (mount.Hardpoint, gdf); else c.Weapons.Add((mount.Hardpoint, gdf));
                });
            },
            Choose = i =>
            {
                if (i == 0) _m.Strip(mount.Slot); else _m.Fit(mount.Slot, items[i - 1].Part, sameName);
                ShowCommitted();
                Pop();
            },
        };
    }

    private Page ComponentPage(string title, int type, ListFrame pop)
    {
        var items = _m.Catalogue(type);
        int slot = _m.SlotOf(type);
        return new()
        {
            Title = title,
            Art = pop.Rows >= items.Count ? pop : null,
            Rows = () =>
            {
                var fitted = _m.Fitted(slot);
                return [.. items.Select(n => new Row(n.Part.Id, "", fitted is not null && fitted.Part.Ordinal == n.Part.Ordinal && fitted.Part.Type == n.Part.Type,
                    [n.Part.Id, $"Weight {n.Part.Weight:0} lbs", $"Hits {n.Part.MaxHp}"], RowKind.Entry))];
            },
            Focus = _ => type == 2 ? GarageViews.Engine(_stage!.Car!, Faithful) : type == 3 ? GarageViews.Underside(_stage!.Car!, Faithful) : GarageViews.Wheel(_stage!.Car!, Axle.Front, Faithful),
            Choose = i => { _m.Fit(slot, items[i].Part); Pop(); },
        };
    }

    /// <summary>What each special does: they weigh nothing and have no hit points of their own to show.</summary>
    private static string[] SpecialNotes(int ordinal) => ordinal switch
    {
        0 => ["KEYED: 5 USES, 8 SECONDS EACH", "BREAKS THE MISSILE LOCK ON YOU"],
        1 => ["KEYED: 3 USES, 15 SECONDS EACH", "FIVE TIMES THE DRIVE FORCE"],
        2 => ["ALWAYS ON", "ENGINE POWER X 1.25"],
        3 => ["KEYED, ONCE PRESSED IT STAYS", "BRAKING TWICE AS HARD"],
        4 => ["ALWAYS ON", "FRONT AND REAR HOLD UP TWICE AS WELL"],
        5 => ["ALWAYS ON", "ARMOR X 1.1 ON EVERY SIDE"],
        6 => ["ALWAYS ON", "A LITTLE MORE CHASSIS ON EVERY SIDE"],
        7 => ["JUST FOR THE COMFORT", "DOES NOTHING"],
        8 => ["ALWAYS ON", "ONE HIT IN TEN SPARES THE PARTS"],
        10 => ["ALWAYS ON", "CALTROPS BLOW TYRES LESS EASILY"],
        _ => ["FOR LOOKS", "DOES NOTHING"],
    };

    private static readonly string[] ZoneNames = ["Front", "Right", "Left", "Rear"];

    /// <summary>ARMOR: the form's DEFENSE block; its rows are the eight cells in reading order, then LBS. AVAILABLE and BALANCE (<see cref="DefenseLayout"/>).</summary>
    private Page ArmourPage() => new()
    {
        Title = "ARMOR",
        Defense = true,
        Rows = () =>
        {
            var c = _m.Inv.Car;
            var rows = new List<Row>();
            for (int z = 0; z < 4; z++)
            {
                int car = ChassisRules.Zone[z];
                rows.Add(new($"{ZoneNames[z]} armor", Tenths(c.Armour[car]), Info: [$"{ZoneNames[z].ToUpperInvariant()} ARMOR", "LEFT / RIGHT: 1.0 AT A TIME", $"POINTS LEFT  {Tenths(c.Ninth)}"], Kind: RowKind.Adjust, Tag: z * 2));
                rows.Add(new($"{ZoneNames[z]} chassis", Tenths(c.Chassis[car]), Info: [$"{ZoneNames[z].ToUpperInvariant()} CHASSIS", "LEFT / RIGHT: 1.0 AT A TIME", $"POINTS LEFT  {Tenths(c.Ninth)}"], Kind: RowKind.Adjust, Tag: z * 2 + 1));
            }
            rows.Add(new("Lbs. available", Tenths(c.Ninth), Kind: RowKind.Note));
            rows.Add(new("BALANCE", "", Info: ["BALANCE", "SHARE ALL THE ARMOR EVENLY"], Kind: RowKind.Button, Tag: 99));
            return rows;
        },
        Focus = i => i >= 0 && i < 8 ? GarageViews.Side(_stage!.Car!, i / 2, Faithful) : GarageViews.Side(_stage!.Car!, 0, Faithful),
        Choose = i => { if (Cur.Cache[i].Kind == RowKind.Button) { _m.Balance(); Refresh(); } },
        Step = (i, dir) =>
        {
            if (i < 0 || i >= 8) return false;
            _m.AdjustDefense(i / 2, chassis: i % 2 == 1, more: dir > 0);
            return true;
        },
    };

    private Page NamePage() => new()
    {
        Title = "NAME",
        Rows = () => [new("Variant", _m.Inv.Car.Variant + (_editing ? "_" : ""), Info: _savingAs
            ? ["NAME", "A NAME FOR THE NEW VARIANT: TYPE TO REPLACE IT", "ENTER SAVES IT, ESC GOES BACK"]
            : ["NAME", "TYPE THE NAME, ENTER WHEN DONE", "A NEW NAME SAVES A NEW VARIANT"])],
        Focus = _ => GarageViews.Engine(_stage!.Car!, Faithful),
        Choose = _ => _editing = true,
    };

    // ---- input ----

    /// <summary>The row (or button) a point is on: a row index, or one of the H* ids, or -1.</summary>
    private int HitAt(Vector2I m)
    {
        if (DoneR.HasPoint(m)) return HDone;
        if (CancelR.HasPoint(m)) return HCancel;
        if (CatalogR.HasPoint(m)) return HCatalog;
        var page = Cur;
        if (page.Art is { } f)
        {
            if (PopRect(f.Cancel).HasPoint(m)) return HPopCancel;
            if (f.Up is { } u && PopRect(u).HasPoint(m)) return HUp;
            if (f.Down is { } d && PopRect(d).HasPoint(m)) return HDown;
        }
        for (int i = 0; i < Rows; i++)
            if (RowRect(page, i).HasPoint(m) && page.Cache[page.Scroll + i].Kind != RowKind.Note) return page.Scroll + i;
        return -1;
    }

    private bool OverMenu(Vector2I m)
    {
        if (TagR.HasPoint(m) || InfoR.HasPoint(m)) return true;
        var p = Cur;
        if (p.Art is { } f) return Shell.Db.Bank(0x24)[f.Sprite] is { } s && new Rect2I(FrameX, FrameY, s.Width + 6, s.Height + 6).HasPoint(m);
        return Panel(p).HasPoint(m);
    }

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            if (!_built) return l;
            var page = Cur;
            for (int i = 0; i < Rows; i++)
            {
                int idx = page.Scroll + i;
                var row = page.Cache[idx];
                if (row.Kind == RowKind.Note) continue;
                int ii = i, rows = Rows;
                Func<NavDir, bool> edge = d => d == NavDir.Up && ii == 0 ? Scroll(-1) : d == NavDir.Down && ii == rows - 1 ? Scroll(1) : false;
                var r = RowRect(page, i);
                if (row.Kind == RowKind.Adjust)
                {
                    l.Add(PadRegion.Of(StepRect(page, idx, false), edge));
                    l.Add(PadRegion.Of(StepRect(page, idx, true), edge));
                }
                else l.Add(PadRegion.Of(r, edge));
            }
            if (page.Art is { } f) l.Add(PadRegion.Of(PopRect(f.Cancel)));
            l.AddRange([PadRegion.Of(CatalogR), PadRegion.Of(DoneR), PadRegion.Of(CancelR)]);
            return l;
        }
    }

    private bool Scroll(int by)
    {
        var p = Cur;
        int to = Math.Clamp(p.Scroll + by, 0, Math.Max(0, p.Cache.Count - VisibleRows));
        if (to == p.Scroll) return false;
        p.Scroll = to;
        Repaint();
        return true;
    }

    /// <summary>The row the keyboard stands on now: moves the cursor (and with it the preview), scrolling a long list to keep it in view.</summary>
    private void MoveCursor(int by)
    {
        var p = Cur;
        var rows = p.Cache;
        if (rows.Count == 0) return;
        int at = _hover;
        if (at < 0) at = by > 0 ? -1 : rows.Count;
        do { at += by; } while (at >= 0 && at < rows.Count && rows[at].Kind == RowKind.Note);
        at = Math.Clamp(at, 0, rows.Count - 1);
        if (rows[at].Kind == RowKind.Note) return;
        _hover = at;
        if (at < p.Scroll) p.Scroll = at;
        if (at >= p.Scroll + VisibleRows) p.Scroll = at - VisibleRows + 1;
    }

    private void Back()
    {
        Shell.Sound(82);
        if (_editing) { EndEdit(); return; }
        if (_stack.Count > 1) Pop(); else CancelMelee();
    }

    private void EndEdit()
    {
        _editing = false;
        if (_savingAs) { _savingAs = _replaceName = false; _m.Rename(_nameBefore); }
        Repaint();
    }

    public override void StopEditing() => EndEdit();


    // ---- capture hook: I76_GARAGE_TOUR=DIR[;FIRST;LAST] photographs every model (FIRST..LAST) and every variant from four angles ----

    private static readonly string? _tourDir = System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR")?.Split(';')[0];
    private int _tourModel = -1, _tourVariant, _tourCam, _tourWait = 30;
    private bool _tourLoaded, _tourPainted;
    private static readonly (string Name, float Yaw, float Pitch, float Reach, float Low)[] TourCams =
        [("front", 140, 12, 0.62f, 0), ("rear", 38, 38, 0.7f, 0), ("side", 90, 4, 0.6f, 0), ("low", 172, 2, 0.65f, 0.3f)];

    private List<string>? _tourExtras;
    private static readonly (Vector3 From, Vector3 At)[]? TourHold = System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR_CAMS")?.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).Select(v => (new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]))).ToArray();

    /// <summary>The model's paint schemes that none of its variants wear (I76_GARAGE_TOUR_EXTRA=1 photographs only those, on the first variant).</summary>
    private List<string> TourExtras(int model)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int v = 1; v <= _data.Models[model].Variants.Count; v++)
            try { used.Add(TripCar.FromVcf(Shell.Vfs.Read(_data.Vcf(model, v))).Vtf); } catch (Exception) { }
        return [.. _m.PaintSchemes().Select(p => p.Stem).Where(s => !used.Contains(s))];
    }

    private void TourSwitch(int model, int variant)
    {
        LoadCar(model, variant);
        _wanted = null;
        _stack.RemoveRange(1, _stack.Count - 1);
        ShowCar(_m.Snapshot(), force: true);
        Refresh();
    }

    private void TourTick()
    {
        var a = System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR")!.Split(';');
        bool extra = System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR_EXTRA") == "1";
        int first = a.Length > 2 ? int.Parse(a[1]) : 0, last = Math.Min(a.Length > 2 ? int.Parse(a[2]) : 999, _data.Models.Count - 1);
        if (_tourWait-- > 0) return;
        int only = int.TryParse(System.Environment.GetEnvironmentVariable("I76_GARAGE_TOUR_ONLY"), out int ov) ? ov : 0;
        if (_tourModel < 0) { _tourModel = first; _tourVariant = Math.Max(only, 1); if (_cm != first || _cv != _tourVariant) { TourSwitch(first, _tourVariant); _tourWait = 8; return; } }
        if (_tourModel > last) { GD.Print("garage tour: done"); Shell.GetTree().Quit(); return; }
        var model = _data.Models[_tourModel];
        if (extra && _tourExtras is null)
        {
            _tourExtras = TourExtras(_tourModel);
            if (_tourExtras.Count == 0) { _tourExtras = null; _tourModel++; if (_tourModel <= last) { TourSwitch(_tourModel, 1); _tourWait = 8; } return; }
        }
        if (extra && _tourCam == 0 && !_tourPainted)
        {
            _m.SetPaint(_tourExtras![_tourVariant - 1]);
            ShowCommitted();
            _tourWait = 3; _tourPainted = true; return;
        }
        var car = _stage!.Car!;
        if (_tourCam < (TourHold?.Length ?? TourCams.Length))
        {
            var (name, yaw, pitch, reach, low) = TourHold is null ? TourCams[_tourCam] : ($"c{_tourCam}", 0f, 0f, 0f, 0f);
            var b = car.Bounds;
            if (TourHold is not null) { _stage.Hold = TourHold[_tourCam]; _stage.Snap(false); }
            else if (b.GetLongestAxisSize() > 12f || b.GetLongestAxisSize() < 2f || b.Position.Y > 0.2f)
            {
                // a big vehicle or a flyer: a camera held round its middle, out of the bay's clamp
                float p = Mathf.DegToRad(pitch), yw = Mathf.DegToRad(yaw);
                var mid = _stage.ToWorld(b.GetCenter() - new Vector3(0, b.Size.Y * low, 0));
                _stage.Hold = (mid + new Vector3(MathF.Sin(yw) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(yw) * MathF.Cos(p)) * b.GetLongestAxisSize() * (b.GetLongestAxisSize() > 7.5f ? 0.75f : 1.5f) * (reach / 0.65f), mid);
                _stage.ShowRoom(false);
                _stage.Snap(b.GetLongestAxisSize() > 7.5f);
            }
            else { _stage.ShowRoom(true); _stage.Hold = null; _stage.Focus(new FocusSpec(b.GetCenter() - new Vector3(0, b.Size.Y * low, 0), true, yaw, pitch, GarageViews.Reach(car, Faithful) * reach)); _stage.Snap(b.GetLongestAxisSize() > 7.5f); }
            if (!_tourLoaded) { _tourLoaded = true; _tourWait = 3; return; }
            string dir = System.IO.Path.Combine(_tourDir!, $"{_tourModel:00}_{model.Stem}");
            System.IO.Directory.CreateDirectory(dir);
            string tag = extra ? $"s{_tourVariant:00}_{_tourExtras![_tourVariant - 1]}" : $"v{_tourVariant:00}";
            _stage.Grab().SavePng(System.IO.Path.Combine(dir, $"{tag}_{name}.png"));
            _tourLoaded = false; _tourCam++; _tourWait = 2;
            return;
        }
        GD.Print($"garage tour: {model.Stem} v{_tourVariant} bounds {car.Bounds}");
        string vtf = _m.Inv.Car.Vtf;
        System.IO.File.AppendAllText(System.IO.Path.Combine(_tourDir!, "index.txt"), $"{_tourModel}\t{model.Stem}\t{model.MakeModel}\t{(extra ? "s" : "")}{_tourVariant}\t{(extra ? vtf : model.Variants[_tourVariant - 1])}\t{vtf}\t{(extra ? "extra" : _tourVariant <= model.Base ? "stock" : "addon")}\n");
        _tourCam = 0; _tourLoaded = false; _tourPainted = false;
        int count = extra ? _tourExtras!.Count : model.Variants.Count;
        if (++_tourVariant > count || only > 0) { _tourModel++; _tourVariant = 1; _tourExtras = null; if (_tourModel <= last) { TourSwitch(_tourModel, 1); _tourWait = 8; } return; }
        if (!extra) { TourSwitch(_tourModel, _tourVariant); _tourWait = 8; }
    }


    // ---- capture hook: I76_GARAGE_SAMVIEWS=DIR renders the car as DIR/views.tsv's cameras (i76 car-views: to-camera xyz, centre xyz, half-frame,
    // in the model's own coordinates) in the modern look: DIR/view_NN.png, and DIR/view_NN.wheels.png with only the wheels drawn (the
    // segmentation's ID pass has no wheels: those pixels are left out of the vote). A DIR without views.tsv is a root: each folder under it
    // with a car.txt (stem, paint scheme) of a model this game has, and no renders yet, is shown in that scheme and rendered in turn ----

    private static readonly string? _samViews = System.Environment.GetEnvironmentVariable("I76_GARAGE_SAMVIEWS");
    private List<float[]>? _samCams;
    private int _samI, _samPass, _samWait = 40;
    private Queue<string>? _samQueue;
    private string? _samDir;

    // the next folder to render (null: none left), its car loaded and painted
    private string? SamNext()
    {
        if (System.IO.File.Exists(System.IO.Path.Combine(_samViews!, "views.tsv"))) return _samDir is null ? _samViews : null;
        _samQueue ??= new(System.IO.Directory.GetDirectories(_samViews!).Order(StringComparer.Ordinal).Where(d =>
            System.IO.File.Exists(System.IO.Path.Combine(d, "car.txt")) && !System.IO.File.Exists(System.IO.Path.Combine(d, "view_00.wheels.png"))));
        while (_samQueue.Count > 0)
        {
            string d = _samQueue.Dequeue();
            var car = System.IO.File.ReadAllText(System.IO.Path.Combine(d, "car.txt")).Trim().Split('\t');
            int mi = _data.Models.FindIndex(m => m.Stem.Equals(car[0], StringComparison.OrdinalIgnoreCase));
            if (mi < 0) { GD.Print($"garage samviews: {car[0]} not in this game, skipped"); continue; }
            TourSwitch(mi, 1);
            _m.SetPaint(car[1]);
            ShowCommitted();
            GD.Print($"garage samviews: {System.IO.Path.GetFileName(d)} ({car[0]} {car[1]})");
            return d;
        }
        return null;
    }

    private void SamTick()
    {
        if (_samWait-- > 0) return;
        if (_samCams is null)
        {
            _samDir = SamNext();
            if (_samDir is null) { GD.Print("garage samviews: done"); Shell.GetTree().Quit(); return; }
            _samCams = System.IO.File.ReadAllLines(System.IO.Path.Combine(_samDir, "views.tsv")).Where(l => l.Length > 0)
                .Select(l => l.Split('\t').Skip(1).Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
            _samI = 0; _samPass = 0; _samWait = 20;   // the new car's textures and materials settle
            return;
        }
        var car = _stage!.Car!;
        if (_samI >= _samCams.Count) { GD.Print($"garage samviews: {_samCams.Count} views"); _samCams = null; return; }
        var c = _samCams[_samI];
        var dir = new System.Numerics.Vector3(c[0], c[1], c[2]); var centre = new System.Numerics.Vector3(c[3], c[4], c[5]); float half = c[6];
        // the software renderer's camera basis (CarCamera), in the model's coordinates, carried to the world through the car node
        var cam = new I76.Formats.Imaging.CarCamera(dir, centre, half);
        var at = car.Node.ToGlobal(I76.Game.Sim.Coords.ToGodot(centre));
        var to = (car.Node.ToGlobal(I76.Game.Sim.Coords.ToGodot(centre + System.Numerics.Vector3.Normalize(dir))) - at).Normalized();
        var up = (car.Node.ToGlobal(I76.Game.Sim.Coords.ToGodot(centre + cam.Up)) - at).Normalized();
        bool wheels = _samPass == 1;
        _stage.ShowRoom(false); _stage.ShowTurntable(!wheels);
        _stage.Background(wheels ? new Color(0, 0, 0) : new Color(0.78f, 0.79f, 0.81f));
        foreach (var m in car.Node.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            string nm = m.Name.ToString();
            // the lamp cards and their glows lie over the real lamps: never drawn for the segmenter (the ID pass has none either)
            bool lamp = nm.Contains("Lamp") || nm.Contains("Glow") || nm.Contains("Glare");
            m.Visible = !lamp && (!wheels || nm.StartsWith("Wheel") || nm.StartsWith("Weapon"));
        }
        _stage.OrthoCamera(at + to * 30f, at, up, half * 2);
        if (_samWait > -3) return;   // a few frames for the view to settle
        string f = System.IO.Path.Combine(_samDir!, $"view_{_samI:00}{(wheels ? ".wheels" : "")}.png");
        _stage.Grab().SavePng(f);
        _samWait = 2;
        if (wheels) { foreach (var m in car.Node.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>()) m.Visible = true; _samI++; _samPass = 0; }
        else _samPass = 1;
    }

    public override void Tick()
    {
        if (!_built || _stage is null) return;
        if (_samViews is not null) { SamTick(); return; }
        if (_tourDir is not null) { TourTick(); return; }
        var m = Shell.Mouse;
        bool moved = m != _lastMouse;
        var page = Cur;

        // the hand on the camera: a button held off the notepad turns it, the wheel zooms; the pad's right stick turns it too
        bool held = Input.IsMouseButtonPressed(MouseButton.Right) || Shell.MouseHeld;
        if (_dragging && !held) _dragging = false;
        if (held && moved && _lastMouse.X >= 0 && (_dragging || !OverMenu(m))) { _dragging = true; _stage.Orbit((m.X - _lastMouse.X) * 1.5f, (m.Y - _lastMouse.Y) * 1.5f); }
        if (Shell.Wheel != 0 && !OverMenu(m)) _stage.Zoom(Shell.Wheel);
        if (Controls.PadSource.Read() is { Count: > 0 } pads && Controls.Focused())
        {
            var p = pads[0];
            if (MathF.Abs(p.RightX) > 0.25f || MathF.Abs(p.RightY) > 0.25f) _stage.Orbit(p.RightX * 9f, p.RightY * 7f);
            if (p.TriggerRight > 0.3f) _stage.Zoom(1);
            if (p.TriggerLeft > 0.3f) _stage.Zoom(-1);
        }

        if (_editing) { Edit(m); _lastMouse = m; return; }

        // what is under the cursor: the mouse when it has moved, else where the keys left it
        if (moved) _hover = HitAt(m);
        _lastMouse = m;
        if (Shell.Wheel != 0 && OverMenu(m) && Scroll(Shell.WheelRows)) { _hover = moved ? _hover : HitAt(m); }

        foreach (var k in Shell.Keys.ToList())
        {
            switch (k)
            {
                case Key.Down: MoveCursor(1); break;
                case Key.Up: MoveCursor(-1); break;
                case Key.Pagedown: for (int n = 0; n < 8; n++) MoveCursor(1); break;
                case Key.Pageup: for (int n = 0; n < 8; n++) MoveCursor(-1); break;
                case Key.Home: _hover = -1; MoveCursor(1); break;
                case Key.End: _hover = -1; MoveCursor(-1); break;
                case Key.Left or Key.Right:
                    if (_hover >= 0 && page.Step?.Invoke(_hover, k == Key.Right ? 1 : -1) == true) { Shell.Sound(82); Refresh(); LookAt(page.Focus?.Invoke(_hover) ?? Overview()); }
                    break;
                case Key.Enter or Key.KpEnter or Key.Space:
                    if (_hover >= 0) ChooseRow(_hover);
                    else if (_hover <= -10) PressButton(_hover);
                    break;
                case Key.Tab: _hover = _hover is <= -10 and > -12 ? _hover - 1 : -10; break;
                case Key.Escape or Key.Backspace: Back(); return;
            }
        }

        // a press: a button, a row, a step button, a list's arrows
        if (Shell.Clicked)
        {
            int hit = HitAt(m);
            if (hit <= -10) { if (hit is HUp or HDown) { _hold.Begin(hit == HUp ? 1000 : 1001); Scroll(hit == HUp ? -1 : 1); } else PressButton(hit); }
            else if (hit >= 0)
            {
                var row = page.Cache[hit];
                if (row.Kind == RowKind.Adjust)
                {
                    int dir = StepRect(page, hit, false).HasPoint(m) ? -1 : StepRect(page, hit, true).HasPoint(m) ? 1 : 0;
                    if (dir != 0 && page.Step?.Invoke(hit, dir) == true) { Shell.Sound(82); _hold.Begin(hit * 2 + (dir > 0 ? 1 : 0)); Refresh(); }
                }
                else { _hover = hit; ChooseRow(hit); }
            }
        }
        int heldId = _hold.Id;
        if (heldId >= 1000)
        {
            for (int n = _hold.Update(Shell.MouseHeld, HitAt(m) == (heldId == 1000 ? HUp : HDown) ? heldId : -1, Shell.Delta); n > 0; n--) Scroll(heldId == 1000 ? -1 : 1);
        }
        else if (heldId >= 0)
        {
            int r = heldId / 2;
            int hit = HitAt(m) == r && (StepRect(Cur, r, false).HasPoint(m) || StepRect(Cur, r, true).HasPoint(m)) ? heldId : -1;
            for (int n = _hold.Update(Shell.MouseHeld, hit, Shell.Delta); n > 0; n--)
                if (Cur.Step?.Invoke(r, heldId % 2 == 1 ? 1 : -1) == true) Repaint();
        }

        // the preview and the camera follow the cursor
        TickPreview();
        page = Cur;
        if (_hover != _hoverShown)
        {
            _hoverShown = _hover;
            if (_hover >= 0 && _hover < page.Cache.Count) page.Hover?.Invoke(_hover);
            else page.Hover?.Invoke(-1);
            LookAt((_hover >= 0 ? page.Focus?.Invoke(_hover) : page.Focus?.Invoke(-1)) ?? Overview());
            Repaint();
        }
    }

    private void ChooseRow(int i)
    {
        var page = Cur;
        if (i < 0 || i >= page.Cache.Count) return;
        Shell.Sound(82);
        page.Choose?.Invoke(i);
        if (Cur == page && !Shell.ModalOpen) Repaint();
    }

    private void PressButton(int id)
    {
        Shell.Sound(82);
        switch (id)
        {
            case HDone: Done(); break;
            case HCancel: CancelMelee(); break;
            case HCatalog: _toCatalog = true; Shell.Go(new PartsCatalogScreen { Back = this }); break;
            case HPopCancel: Back(); break;
        }
    }

    /// <summary>The name being typed: as RENAME does (15 characters, 160 pixels), Enter or Esc or a click ends it.</summary>
    private void Edit(Vector2I m)
    {
        var name = _m.Inv.Car.Variant;
        bool changed = false;
        // a proposed name goes at the first key, as a selected field's text would
        if (_replaceName && (Shell.Typed.Length > 0 || Shell.Keys.Contains(Key.Backspace))) { name = ""; _replaceName = false; changed = true; }
        foreach (char c in Shell.Typed) if (name.Length < 15 && F45.Measure(name + c) <= 160) { name += c; changed = true; }
        if (Shell.Keys.Contains(Key.Backspace) && name.Length > 0) { name = name[..^1]; changed = true; }
        // a click on DONE while typing is DONE (the name ends and the car is saved), not a click away
        bool done = Shell.Clicked && HitAt(m) == HDone;
        bool enter = done || Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter);
        if (_savingAs && !enter && (Shell.Keys.Contains(Key.Escape) || Shell.Clicked)) { EndEdit(); return; }
        if (enter || Shell.Keys.Contains(Key.Escape) || Shell.Clicked) { _editing = false; changed = true; }
        if (changed) { _m.Rename(name); Repaint(); }
        if (_savingAs && enter) { _savingAs = _replaceName = false; Done(); }
        else if (done) PressButton(HDone);
    }

    // ---- DONE: the checks, the questions, the file ----

    private void Notice(int sprite, Action? then = null) =>
        Under(() => Shell.Modal(new NoticePopup { Sprite = sprite, Ok = new Rect2I(119, 61, 42, 16), Closed = then ?? Repaint }));

    /// <summary>A pop-up goes over the notepad: the colour swatches (a layer over the frame) go while it does; the next repaint brings them back.</summary>
    private void Under(Action modal) { _swatches?.Set([], false); modal(); }

    private void Ask(string line, Action yes, Action no) =>
        Under(() => Shell.Modal(new ConfirmPopup { Lines = [line], Answer = a => { if (a) yes(); else no(); } }));

    /// <summary>Back to the melee form with the car and variant the garage ended on.</summary>
    private void Finish(int n)
    {
        if (Melee.DoneAs is { } d) d(_cm, n); else Melee.Done(n);
    }

    /// <summary>The name opened to save a changed stock variant as a new one, a free name proposed.</summary>
    private void SaveAs()
    {
        _nameBefore = _m.Inv.Car.Variant;
        _m.Rename(FreeName(_model.Variants));
        if (_stack.Count == 0 || _stack[^1].Title != "NAME") Push(NamePage());
        _editing = _savingAs = _replaceName = true;
        Repaint();
    }

    /// <summary>"Custom 1", "Custom 2", ...: the first no variant of the model is called.</summary>
    public static string FreeName(IReadOnlyList<string> variants)
    {
        for (int n = 1; ; n++)
        {
            string name = $"Custom {n}";
            if (!variants.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    private void Done()
    {
        var plan = _m.PlanDone(_cv, _model.Variants, _model.Base);
        void Save(int n)
        {
            var file = ChassisRules.VariantFile(_m.Inv, _model.MakeModel, Shell.Install.IsNitro, out var car);
            _data.SaveVariant(_cm, n, file, car.Variant, _m.ColoursToSave, setColours: true);
            Finish(n);
        }
        switch (plan.Kind)
        {
            case ChassisRules.DoneKind.Invalid: Notice(plan.Notice); break;
            case ChassisRules.DoneKind.Unchanged: Finish(_cv); break;
            // the port's garage asks for the name in place (the 2D form's notice 30 sent the player off to find RENAME): the name opens with a
            // free one proposed, Enter saves
            // (a name typed that is a stock variant's: the notice first, then the name again)
            case ChassisRules.DoneKind.NeedsName: if (_m.Renamed) Notice(plan.Notice, SaveAs); else SaveAs(); break;
            case ChassisRules.DoneKind.AskOverwrite: Ask("Overwrite the current variant?", () => Save(plan.Save), Repaint); break;
            // a new name and DONE is the answer: no "Wanna save the new variant?" (whose NO put the name and paint back)
            case ChassisRules.DoneKind.AskSaveNew: Save(plan.Save); break;
        }
    }
}


/// <summary>
/// True-colour swatches over the shell's frame (whose own palette has no such colours), drawn in the frame's 640x480 coordinates, fitted the way
/// the frame is (its aspect kept, centred).
/// </summary>
internal sealed partial class SwatchLayer : Control
{
    private List<(Rect2I Rect, Color Colour)> _items = [];

    public SwatchLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void Set(List<(Rect2I, Color)> items, bool visible)
    {
        Visible = visible;
        _items = items;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float s = MathF.Min(Size.X / ShellFrame.W, Size.Y / ShellFrame.H);
        var o = new Vector2((Size.X - ShellFrame.W * s) / 2, (Size.Y - ShellFrame.H * s) / 2);
        foreach (var (r, c) in _items)
        {
            var rect = new Rect2(o + new Vector2(r.Position.X, r.Position.Y) * s, new Vector2(r.Size.X, r.Size.Y) * s);
            DrawRect(rect, c);
            DrawRect(rect, new Color(0.29f, 0.22f, 0.13f), false, MathF.Max(1f, s));
        }
    }
}
