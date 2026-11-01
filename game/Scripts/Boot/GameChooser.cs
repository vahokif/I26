using Godot;
using I76.Formats.Installers;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Game.Boot;

/// <summary>
/// The game chooser: shown at start when both Interstate '76 and the Nitro Pack are available (an install, or only the GOG
/// offline installer, which is extracted when chosen). The two games' main-menu logos side by side on black, each read
/// from the game's own shell database (record 26 of DATABASE.MW2, its Nitro counterpart in database.tvf; for an installer-only
/// game straight out of the installer, nothing extracted). Hover or focus lights a logo; click, Enter or the pad's A starts
/// it; Left/Right or the D-pad/stick move; Esc, B or Start quits. The last choice is remembered and preselected.
/// </summary>
public partial class GameChooser : CanvasLayer
{
    public required List<AvailableGame> Games { get; init; }
    public Action<AvailableGame>? OnChosen { get; init; }

    /// <summary>The chooser was shown this run: the main menu's Options offers "Switch Game" (in-process, <see cref="GameSwitch"/>).</summary>
    public static bool SwitchAvailable = System.Environment.GetEnvironmentVariable("I76_SHOW_SWITCH") == "1";   // I76_SHOW_SWITCH=1: for a capture of the Options row
    /// <summary>The logos are still being read (a capture's frame count waits for them).</summary>
    public static bool Loading { get; private set; }

    private const string Section = "chooser", LastKey = "last";

    /// <summary>The game chosen last time (settings.cfg), if any.</summary>
    public static GogGame? Last()
    {
        var cfg = new ConfigFile();
        if (cfg.Load("user://settings.cfg") != Error.Ok) return null;
        return (string)cfg.GetValue(Section, LastKey, "") switch { "i76" => GogGame.I76, "nitro" => GogGame.Nitro, _ => null };
    }

    public static void RememberChoice(GogGame g)
    {
        var cfg = new ConfigFile();
        cfg.Load("user://settings.cfg");
        cfg.SetValue(Section, LastKey, g == GogGame.Nitro ? "nitro" : "i76");
        cfg.Save("user://settings.cfg");
    }

    private sealed class Logo { public AvailableGame Game = null!; public Image? Image; }

    private readonly List<Logo> _logos = [];
    private Task? _loading;
    private Control _root = null!;

    public override void _Ready()
    {
        Layer = 100;
        SwitchAvailable = true;
        Loading = true;
        var bg = new ColorRect { Color = Colors.Black };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Pass };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        foreach (var g in Games) _logos.Add(new Logo { Game = g });
        // an installer is read for one file (a second, perhaps): off the main thread
        _loading = Task.Run(() => { foreach (var l in _logos) l.Image = LoadLogo(l.Game); });
    }

    public override void _Process(double delta)
    {
        if (_loading is null) { PollPad(delta); return; }
        if (!_loading.IsCompleted) return;
        _loading = null;
        _pad.Settle();
        Build();
        Loading = false;
    }

    private readonly I76.Formats.Input.PadRepeater _pad = new();
    private bool _chosen;

    /// <summary>
    /// The pad, polled as the shell does (Godot's own ui_accept / ui_cancel never reach us from a pad here): A starts the focused
    /// game, B and Start quit. The D-pad and stick move the focus through the buttons' neighbours, which already works.
    /// </summary>
    private void PollPad(double delta)
    {
        var pads = I76.Game.Controls.PadSource.Read();
        var step = _pad.Update(pads.Count > 0 && I76.Game.Controls.Focused() ? pads[0] : default, delta);
        switch (Act(step))
        {
            case PadAct.Accept:
                if (!_chosen && GetViewport().GuiGetFocusOwner() is Tile t) Choose(t.Game);
                break;
            case PadAct.Quit:
                _chosen = true;
                GetTree().Quit();
                break;
        }
    }

    public enum PadAct { None, Accept, Quit }

    /// <summary>What a pad press does here: A starts the focused game, B or Start quits (A first when both come in one frame, as the shell's).</summary>
    public static PadAct Act(I76.Formats.Input.PadRepeater.Step step) =>
        (step.Buttons & I76.Formats.Input.PadRepeater.Btn.Accept) != 0 ? PadAct.Accept
        : (step.Buttons & (I76.Formats.Input.PadRepeater.Btn.Back | I76.Formats.Input.PadRepeater.Btn.Start)) != 0 ? PadAct.Quit
        : PadAct.None;

    /// <summary>The main-menu logo card of a game, cropped to its logo, or null.</summary>
    public static Image? LoadLogo(AvailableGame g)
    {
        try
        {
            ShellDatabase db;
            if (g.InstallDir is { } dir)
            {
                var path = new GameInstall(dir).FindFile(g.ShellDatabaseName) ?? throw new FileNotFoundException(g.ShellDatabaseName);
                db = ShellDatabase.Open(path);
            }
            else
            {
                var bytes = GogInstaller.Open(g.InstallerPath!).ReadFile(g.ShellDatabaseName) ?? throw new FileNotFoundException(g.ShellDatabaseName);
                db = new ShellDatabase(bytes);
            }
            return Crop(db.Background(26));
        }
        catch (Exception ex) when (ex is IOException or InstallerException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException)
        {
            GD.PushWarning($"chooser: no logo for {g.Title}: {ex.Message}");
            return null;
        }
    }

    private static Image Crop(Pcx p)
    {
        var rgba = new byte[p.Width * p.Height * 4];
        int x0 = p.Width, y0 = p.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                int i = y * p.Width + x, c = p.Pixels[i];
                byte r = p.Palette[c * 3], gr = p.Palette[c * 3 + 1], b = p.Palette[c * 3 + 2];
                rgba[i * 4] = r; rgba[i * 4 + 1] = gr; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
                if (r + gr + b > 60) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            }
        var img = Image.CreateFromData(p.Width, p.Height, false, Image.Format.Rgba8, rgba);
        if (x1 < 0) return img;
        const int Margin = 10;
        x0 = Math.Max(0, x0 - Margin); y0 = Math.Max(0, y0 - Margin);
        x1 = Math.Min(p.Width - 1, x1 + Margin); y1 = Math.Min(p.Height - 1, y1 + Margin);
        return img.GetRegion(new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
    }

    private void Build()
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = row.OffsetRight = 0; row.OffsetTop = 60; row.OffsetBottom = -60;
        row.AddThemeConstantOverride("separation", 24);
        _root.AddChild(row);
        var last = Last();
        Tile? pick = null;
        var tiles = new List<Tile>();
        foreach (var l in _logos)
        {
            var t = new Tile(l.Game, l.Image);
            t.Pressed += () => Choose(t.Game);
            row.AddChild(t);
            tiles.Add(t);
            if (pick is null || l.Game.Game == last) pick = t;
        }
        for (int i = 0; i < tiles.Count; i++)
        {
            tiles[i].FocusNeighborLeft = tiles[(i + tiles.Count - 1) % tiles.Count].GetPath();
            tiles[i].FocusNeighborRight = tiles[(i + 1) % tiles.Count].GetPath();
        }
        var hint = new Label
        {
            Text = "Left / Right to choose   Enter to start   Esc to quit", HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.35f), MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide, Control.LayoutPresetMode.Minsize);
        hint.OffsetTop = -28; hint.OffsetBottom = -8;
        _root.AddChild(hint);
        // the version, small and dim, in the lower right corner, as the main menu has it
        var build = new Label { Text = BuildInfo.Line, Name = "Build", MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Right, Modulate = new Color(1, 1, 1, 0.35f) };
        build.AddThemeFontSizeOverride("font_size", 11);
        build.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize);
        build.GrowHorizontal = Control.GrowDirection.Begin;
        build.OffsetLeft = -300; build.OffsetRight = -6; build.OffsetTop = -20; build.OffsetBottom = -4;
        _root.AddChild(build);
        _root.AddChild(UpdateNotice.Link());
        UpdateNotice.Start(_root);
        pick?.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>The same as clicking that game's logo (the switch guard's hook).</summary>
    public void Pick(GogGame g) => Choose(Games.First(x => x.Game == g));

    private void Choose(AvailableGame g)
    {
        if (_chosen) return;
        _chosen = true;
        GD.Print($"chooser: {g.Title} ({(g.Installed ? g.InstallDir : g.InstallerPath)})");
        RememberChoice(g.Game);
        OnChosen?.Invoke(g);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel")) { GetTree().Quit(); GetViewport().SetInputAsHandled(); }
    }

    /// <summary>One game: its logo (dim until hovered or focused) and, for an installer-only game, what will happen.</summary>
    private sealed partial class Tile : Button
    {
        public AvailableGame Game { get; }
        private readonly TextureRect _pic;

        public Tile(AvailableGame game, Image? logo)
        {
            Game = game;
            Flat = true;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(200, 120);
            FocusMode = FocusModeEnum.All;
            var none = new StyleBoxEmpty();
            foreach (var n in new[] { "normal", "pressed", "disabled" }) AddThemeStyleboxOverride(n, none);
            AddThemeStyleboxOverride("hover", none);
            AddThemeStyleboxOverride("focus", none);
            AddThemeStyleboxOverride("hover_pressed", none);
            _pic = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            };
            _pic.SetAnchorsPreset(LayoutPreset.FullRect);
            _pic.OffsetLeft = _pic.OffsetTop = 12; _pic.OffsetRight = -12; _pic.OffsetBottom = game.Installed ? -12 : -56;
            if (logo is not null) _pic.Texture = ImageTexture.CreateFromImage(logo);
            AddChild(_pic);
            if (logo is null)
            {
                var name = new Label { Text = game.Title, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                name.AddThemeFontSizeOverride("font_size", 28);
                name.SetAnchorsPreset(LayoutPreset.FullRect);
                AddChild(name);
            }
            if (!game.Installed)
            {
                var cap = new Label
                {
                    Text = $"{game.Title} will be unpacked from the GOG installer\n({GogInstaller.Mb(game.InstallerBytes)})",
                    HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0.6f),
                };
                cap.AddThemeFontSizeOverride("font_size", 13);
                cap.SetAnchorsPreset(LayoutPreset.BottomWide);
                cap.OffsetTop = -50; cap.OffsetBottom = -8;
                AddChild(cap);
            }
            MouseEntered += () => { if (!HasFocus()) GrabFocus(); };
            FocusEntered += Lit; FocusExited += Lit;
            Lit();
        }

        // no frame: the chosen logo at full brightness, the other faded well down, a short fade between them
        private Tween? _fade;
        private void Lit()
        {
            var to = HasFocus() ? Colors.White : new Color(0.2f, 0.2f, 0.2f);
            _fade?.Kill();
            if (!IsInsideTree()) { _pic.Modulate = to; return; }
            _fade = CreateTween();
            _fade.TweenProperty(_pic, "modulate", to, 0.18);
        }
    }
}
