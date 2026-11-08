using Godot;
using I76.Game.Ui;

namespace I76.Game.Shell;

/// <summary>
/// The port's Developer panel on the shell's main menu (docs/dev-hooks.md): the in-mission panel's look, opened by the same
/// keys (Ctrl+F2 or Ctrl+`) when the developer setting, --dev or I76_DEV=1 is on. Its Saves section holds the tools that
/// once stood on Load Bookmark: Import Saves (the original game's bookmarks from the install) and All Scenes (a bookmark
/// per mission of the trip). Both are the base game's only: the Nitro Pack has no trip.
/// </summary>
public partial class ShellDevPanel : Control
{
    private System.Action _rescale = null!;
    public required Shell Shell { get; init; }
    public required System.Action Close { get; init; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;   // the menu underneath takes no clicks while the panel is up
        var theme = new Theme { DefaultFontSize = 15 };
        var bg = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.1f, 0.92f), BorderColor = new Color(0.85f, 0.6f, 0.2f) };
        var (panel, scroll, col) = DevScale.Frame(theme, bg);
        AddChild(panel);
        _rescale = () => DevScale.Apply(panel, scroll, col, theme, bg, DevScale.Factor(GetViewportRect().Size.Y));
        GetViewport().SizeChanged += _rescale;
        TreeExiting += () => GetViewport().SizeChanged -= _rescale;

        col.AddChild(Heading("Developer  (Ctrl+F2, Ctrl+` or Esc closes)"));
        col.AddChild(Heading("Saves"));
        bool trip = !Shell.Install.IsNitro;
        col.AddChild(Row(Button("Import Saves", ImportSaves, trip), Note("the original game's bookmarks, car and parts included")));
        col.AddChild(Row(Button("All Scenes", AllScenes, trip), Note("a bookmark for every scene of the trip, in its own car")));
        if (!trip) col.AddChild(Note("(the Nitro Pack has no trip)"));
        col.AddChild(Row(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }, Button("Close", Close, true)));
        _rescale();
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode == Key.Escape || (k.Keycode is Key.F2 or Key.Quoteleft && k.CtrlPressed))
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    /// <summary>The list of the original's bookmarks; Import and Cancel go on to Load Bookmark, whose Cancel comes back here.</summary>
    private void ImportSaves()
    {
        Close();
        Shell.Overlay(new ImportSavesScreen { Direct = true });
    }

    /// <summary>One bookmark per mission of the trip, in that mission's stock car; asks first, since it makes many files, and leaves a bookmark of the same scene and name alone.</summary>
    private void AllScenes()
    {
        Close();
        var shell = Shell;
        shell.Modal(new ConfirmPopup
        {
            Lines = ["Make a bookmark for every scene", "of the trip, each in its own car?"],
            Answer = yes =>
            {
                if (!yes) return;
                var marks = Bookmarks.Load();
                var vfs = shell.Vfs;
                var (made, had) = SceneBookmarks.Generate(marks, n => SceneBookmarks.NameOf(shell.Install, n), n =>
                {
                    var car = I76.Sim.Trip.TripCar.FromVcf(vfs.Read(SceneBookmarks.CarFile(n)), c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c));
                    return I76.Sim.Trip.TripInventory.FromCar(car, shell.Parts, vfs).Save();
                }, Bookmarks.WriteCar);
                marks.Save();
                var text = $"{made} bookmark{(made == 1 ? "" : "s")} made." + (had > 0 ? $" {had} already were." : "");
                shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = text, Closed = () => shell.Overlay(new LoadBookmarkScreen { Direct = true }) });
            },
        });
    }

    private static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", new Color(0.95f, 0.65f, 0.25f));
        return l;
    }

    private static Label Note(string text) => new() { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };

    private static Button Button(string text, System.Action pressed, bool enabled)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, Disabled = !enabled, };
        DevScale.Min(b, 120);
        b.Pressed += pressed;
        return b;
    }

    private static HBoxContainer Row(params Control[] items)
    {
        var r = DevScale.Sep(new HBoxContainer(), 8);
        foreach (var c in items) r.AddChild(c);
        return r;
    }
}
