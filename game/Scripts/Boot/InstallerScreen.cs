using Godot;
using I76.Formats.Installers;

namespace I76.Game.Boot;

/// <summary>
/// The no-install screen (what was checked, how to point I26 at a copy) with "Use a GOG installer...": pick a GOG offline
/// installer (setup_*.exe), see what it holds, extract the game data inside the I26 folder (i76/, nitro/ next to the executable)
/// or, if that is not possible, into the local user folder (i76/, nitro/), or a chosen folder, with progress and Cancel; then the game starts on it. --from-installer runs the same
/// extraction straight away. The work runs on a worker thread; its progress is polled here, on the main thread.
/// </summary>
public partial class InstallerScreen : CanvasLayer
{
    /// <summary>The no-install text (null when started from --from-installer).</summary>
    public string? Message { get; init; }
    /// <summary>Called on the main thread with the extracted install folder.</summary>
    public Action<string>? OnInstalled { get; init; }
    /// <summary>Started from the game chooser: a failed or cancelled extraction offers "Back" to it.</summary>
    public Action? OnBack { get; init; }
    /// <summary>--from-installer: the installer to extract at once, and --to's folder.</summary>
    public string? AutoInstaller { get; init; }
    public string? AutoTarget { get; init; }

    /// <summary>Set once --from-installer was handled this run (Main's _Ready runs again at each scene reload).</summary>
    public static bool CommandLineHandled;

    /// <summary>An extraction is running (the game's keys, Esc's quit among them, wait).</summary>
    public static bool Busy { get; private set; }

    private Label _text = null!;
    private ProgressBar _bar = null!;
    private HBoxContainer _buttons = null!;
    private GogInstaller? _installer;
    private string? _target;
    private bool _targetChosen;
    private Task<GogExtractResult>? _task;
    private CancellationTokenSource? _cancel;
    private readonly object _lock = new();
    private InnoProgress _progress;
    private double _printed = -1;

    public override void _Ready()
    {
        Layer = 100;
        var bg = new ColorRect { Color = Colors.Black };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);
        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        box.OffsetLeft = box.OffsetTop = 48; box.OffsetRight = box.OffsetBottom = -48;
        box.AddThemeConstantOverride("separation", 18);
        AddChild(box);
        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(_text);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, Visible = false, CustomMinimumSize = new Vector2(560, 24), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        box.AddChild(_bar);
        _buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _buttons.AddThemeConstantOverride("separation", 16);
        box.AddChild(_buttons);
        if (AutoInstaller is { } exe) { _target = AutoTarget; _targetChosen = AutoTarget is not null; Pick(exe, start: true); }
        else Offer();
    }

    private void Buttons(params (string Text, Action Pressed)[] list)
    {
        foreach (var c in _buttons.GetChildren()) c.QueueFree();
        Button? first = null;
        foreach (var (t, a) in list)
        {
            var b = new Button { Text = t, CustomMinimumSize = new Vector2(160, 36) };
            b.Pressed += a;
            _buttons.AddChild(b);
            first ??= b;
        }
        first?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Offer()
    {
        _bar.Visible = false;
        _text.Text = (Message ?? "I26 could not find Interstate '76.") +
            "\n\nNo installed copy? Point I26 at the GOG offline installer (setup_interstate76_....exe, or the Nitro Pack's):\n" +
            "it takes the game data out of it into its own folder, once.";
        Buttons(("Use a GOG installer…", ChooseInstaller), ("Quit", () => GetTree().Quit()));
    }

    private void ChooseInstaller()
    {
        var dlg = new FileDialog
        {
            Title = "The GOG offline installer of Interstate '76 or the Nitro Pack",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = ["setup_*.exe ; GOG offline installers", "*.exe ; Programs"],
            CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile) is { Length: > 0 } home && Directory.Exists(Path.Combine(home, "Downloads"))
                ? Path.Combine(home, "Downloads") : "",
        };
        dlg.FileSelected += p => { dlg.QueueFree(); Pick(p, start: false); };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered(new Vector2I(960, 600));
    }

    private void ChooseFolder()
    {
        var dlg = new FileDialog
        {
            Title = "Where to put the game data",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            CurrentDir = Path.GetDirectoryName(_target ?? "") ?? "",
        };
        dlg.DirSelected += d => { dlg.QueueFree(); _target = GogInstaller.TargetIn(d, _installer!.Game); _targetChosen = true; Confirm(); };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered(new Vector2I(960, 600));
    }

    private void Pick(string exe, bool start)
    {
        try
        {
            _installer = GogInstaller.Open(exe);
        }
        catch (Exception ex) when (ex is InstallerException or IOException or UnauthorizedAccessException)
        {
            Fail(ex is FileNotFoundException ? $"{exe} doesn't exist." : ex.Message);
            return;
        }
        if (!_targetChosen) _target = LaunchOptions.DefaultExtractFolder(_installer.Game, _installer.WantedBytes);
        if (start)
        {
            if (_installer.AlreadyIn(_target!))
            {
                GD.Print($"installer: {_target} already holds {_installer.Title} from {Path.GetFileName(exe)}");
                Done(_target!);
                return;
            }
            Extract();
        }
        else Confirm();
    }

    private void Confirm()
    {
        var g = _installer!;
        _bar.Visible = false;
        _text.Text = $"{g.Title}\n{Path.GetFileName(g.Setup.Path)} (Inno Setup {g.Setup.Version})\n\n" +
            $"{g.Wanted.Count()} files, {GogInstaller.Mb(g.WantedBytes)} of game data, into:\n{_target}";
        Buttons(("Extract", Extract), ("Choose folder…", ChooseFolder), ("Back", Offer));
    }

    private void Extract()
    {
        var g = _installer!;
        var dir = _target!;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _bar.Visible = true;
        _bar.Value = 0;
        _text.Text = $"Extracting {g.Title}…";
        GD.Print($"installer: extracting {g.Title} from {g.Setup.Path} into {dir} ({g.Wanted.Count()} files, {GogInstaller.Mb(g.WantedBytes)})");
        Buttons(("Cancel", () => _cancel?.Cancel()));
        Busy = true;
        _task = Task.Run(() => g.ExtractTo(dir, p => { lock (_lock) _progress = p; }, token));
    }

    public override void _Process(double delta)
    {
        if (_task is null) return;
        InnoProgress p;
        lock (_lock) p = _progress;
        if (p.BytesTotal > 0)
        {
            double f = (double)p.Bytes / p.BytesTotal;
            _bar.Value = f;
            _text.Text = $"Extracting {_installer!.Title}…\n\n{p.Files} / {p.FilesTotal} files, {GogInstaller.Mb(p.Bytes)} / {GogInstaller.Mb(p.BytesTotal)}\n{p.Current}";
            if (f - _printed >= 0.1) { _printed = f; GD.Print($"installer: {f * 100:0}% ({p.Files}/{p.FilesTotal} files)"); }
        }
        if (!_task.IsCompleted) return;
        var task = _task;
        _task = null;
        Busy = false;
        if (task.IsCompletedSuccessfully)
        {
            var r = task.Result;
            GD.Print($"installer: {r.Files} files, {GogInstaller.Mb(r.Bytes)} in {r.Elapsed.TotalSeconds:0.0} s into {r.Folder}");
            Done(r.Folder);
            return;
        }
        var ex = task.Exception?.GetBaseException();
        Fail(ex switch
        {
            OperationCanceledException => "Cancelled. The folder holds a partial copy; extracting again replaces it.",
            InstallerException ie => ie.Message,
            UnauthorizedAccessException or IOException => $"Couldn't write to {_target}: {ex.Message}",
            _ => $"Extraction failed: {ex?.Message}",
        });
        if (ex is not (OperationCanceledException or InstallerException)) GD.PrintErr(ex?.ToString());
    }

    private void Done(string dir)
    {
        if (!I76.Formats.Vfs.InstallSearch.IsInstall(dir))
        {
            Fail($"{dir} isn't an Interstate '76 install after extracting.");
            return;
        }
        LaunchOptions.Extracted = dir;
        // deferred: this can run inside _Ready, where the scene can't be reloaded yet
        Callable.From(() => OnInstalled?.Invoke(dir)).CallDeferred();
    }

    private void Fail(string why)
    {
        GD.PrintErr($"installer: {why}");
        if (DisplayServer.GetName() == "headless") { GetTree().Quit(1); return; }
        _bar.Visible = false;
        _text.Text = why;
        if (OnBack is not null)
            Buttons(("Back to the games", OnBack), ("Quit", () => GetTree().Quit()));
        else if (AutoInstaller is not null && Message is null)
            Buttons(("Use another installer…", ChooseInstaller), ("Quit", () => GetTree().Quit()));
        else
            Buttons(("Back", Offer), ("Quit", () => GetTree().Quit()));
    }

    public override void _ExitTree()
    {
        _cancel?.Cancel();
        Busy = false;
    }
}
