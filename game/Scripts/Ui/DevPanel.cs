using Godot;
using I76.Sim.Session;

namespace I76.Game.Ui;

/// <summary>A weapon mount of the player's car: its hardpoint, a label, the weapon file on it and the files the garage lets onto it (with their names).</summary>
public sealed record DevMount(uint Hardpoint, string Label, string Current, IReadOnlyList<(string File, string Name)> Options);

/// <summary>What the Developer panel asks of the game (Main, MainDev.cs). Each action returns the line the panel shows.</summary>
public interface IDevHost
{
    /// <summary>The live line: tick, rate, substeps, sim time, fps, the car's place.</summary>
    string DevStatus();
    /// <summary>The places to go: the user's kept ones (named "kept: ..."), the generated landmarks, the script's paths and cars.</summary>
    IReadOnlyList<DevPoint> DevPoints();
    /// <summary>Keeps the car's place under that name for this mission (user://dev-places.cfg).</summary>
    string DevKeep(string name);
    /// <summary>Forgets a kept place (by its listed name).</summary>
    string DevForget(string pointName);
    /// <summary>The player's car's (x, z) and heading, or null without one.</summary>
    (double X, double Z, float Heading)? DevHere();
    string DevTeleport(double x, double z, float heading);
    string DevRepair();
    string DevGod(bool on);
    bool DevGodOn { get; }
    string DevBring();
    /// <summary>The player's car's weapon mounts, each with what the garage allows on it.</summary>
    IReadOnlyList<DevMount> DevMounts();
    /// <summary>Puts that weapon file on that hardpoint, loaded.</summary>
    string DevSwap(uint hardpoint, string file);
    /// <summary>Every weapon's ammunition and the specials' charges, nothing repaired.</summary>
    string DevRefill();
    string DevStore();
    string DevRestore();
    /// <summary>What is stored ("tick 1234 of t14"), or null.</summary>
    string? DevStored { get; }
    bool DevFrozen { get; set; }
    string DevStep(int ticks);
    double DevScale { get; set; }
    /// <summary>The running mission's id, upper case.</summary>
    string DevMission { get; }
    /// <summary>Every mission the install offers (the F9 list's), with a label.</summary>
    IReadOnlyList<(string Id, string Label)> DevMissions();
    /// <summary>The player's car file, or null.</summary>
    string? DevCurrentCar { get; }
    /// <summary>Every .vcf the install (and the addon folder) has.</summary>
    IReadOnlyList<string> DevCars();
    /// <summary>Leaves this mission and starts that one in that car ("" = the mission's own), as --mission X --drive [car].</summary>
    string DevStart(string mission, string car);
    void DevClose();
}

/// <summary>
/// The port's Developer panel (docs/dev-hooks.md): a plain Godot panel over the paused mission, off unless --dev, I76_DEV=1 or
/// the setting turns it on, never in a network game. Teleport to the mission's named places or to coordinates, repair and refill,
/// God mode, bring the nearest car, store and restore the whole simulation, freeze, step and scale the ticks. Every action
/// goes through the sim's DevTools or the session's snapshot; the panel itself holds no game state.
/// </summary>
public partial class DevPanel : Control
{
    public required IDevHost Host { get; init; }

    private Label _status = null!, _message = null!, _stored = null!;
    private OptionButton _points = null!, _scale = null!, _missions = null!, _cars = null!;
    private IReadOnlyList<(string Id, string Label)> _missionList = [];
    private readonly List<string> _carFiles = [];
    private LineEdit _x = null!, _z = null!, _heading = null!, _keepName = null!;
    private CheckBox _god = null!, _freeze = null!;
    private readonly List<(DevMount Mount, OptionButton Pick)> _mounts = [];
    private IReadOnlyList<DevPoint> _pointList = [];
    private static readonly double[] Scales = [0.1, 0.25, 0.5, 1, 2, 4];
    /// <summary>The last place chosen, kept for the next time the panel opens in this run.</summary>
    private static int _lastPoint;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        var theme = new Theme { DefaultFontSize = 15 };
        var panel = new PanelContainer { Theme = theme, Position = new Vector2(16, 16), CustomMinimumSize = new Vector2(430, 0) };
        var bg = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.1f, 0.92f), BorderColor = new Color(0.85f, 0.6f, 0.2f) };
        bg.SetBorderWidthAll(2);
        bg.SetContentMarginAll(12);
        bg.SetCornerRadiusAll(4);
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        panel.AddChild(col);

        col.AddChild(Heading("Developer  (Ctrl+F2, Ctrl+` or Esc closes)"));
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        _status.AddThemeColorOverride("font_color", new Color(0.75f, 0.9f, 0.75f));
        col.AddChild(_status);

        col.AddChild(Heading("Scene"));
        _missionList = Host.DevMissions();
        _missions = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        foreach (var m in _missionList) _missions.AddItem(m.Label);
        int here = _missionList.Select(m => m.Id).ToList().IndexOf(Host.DevMission);
        if (_missionList.Count == 0) { _missions.AddItem("(no missions found)"); _missions.Disabled = true; }
        else _missions.Selected = System.Math.Max(0, here);
        // the current car, the mission's own, then every car file
        _cars = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        if (Host.DevCurrentCar is { } current) { _cars.AddItem($"this car ({current})"); _carFiles.Add(current); }
        _cars.AddItem("the mission's own car"); _carFiles.Add("");
        foreach (var c in Host.DevCars()) { _cars.AddItem(c); _carFiles.Add(c); }
        _cars.Selected = 0;
        col.AddChild(Row(_missions, _cars, Button("Start", StartMission)));

        col.AddChild(Heading("Teleport"));
        _points = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
        FillPoints(_lastPoint);
        col.AddChild(Row(_points, Button("Go", GoPoint)));
        _keepName = new LineEdit { PlaceholderText = "name for this place", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddChild(Row(_keepName, Button("Keep", Keep), Button("Forget", Forget)));
        _x = Field("x"); _z = Field("z"); _heading = Field("heading");
        col.AddChild(Row(_x, _z, _heading, Button("Go", GoCoords), Button("Here", FillHere)));
        FillHere();

        col.AddChild(Heading("Car"));
        _god = new CheckBox { Text = "God mode", ButtonPressed = Host.DevGodOn };
        _god.Toggled += on => Say(Host.DevGod(on));
        col.AddChild(Row(Button("Repair & refill", () => Say(Host.DevRepair())), _god, Button("Bring nearest car", () => Say(Host.DevBring()))));

        col.AddChild(Heading("Loadout"));
        foreach (var m in Host.DevMounts())
        {
            var pick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
            for (int i = 0; i < m.Options.Count; i++)
            {
                pick.AddItem($"{m.Options[i].Name}  ({m.Options[i].File})");
                if (m.Options[i].File.Equals(m.Current, System.StringComparison.OrdinalIgnoreCase)) pick.Selected = i;
            }
            _mounts.Add((m, pick));
            col.AddChild(Row(new Label { Text = m.Label, CustomMinimumSize = new Vector2(110, 0) }, pick));
        }
        if (_mounts.Count == 0) col.AddChild(new Label { Text = "(the car has no weapons)" });
        col.AddChild(Row(Button("Apply", ApplyLoadout), Button("Refill ammo & specials", () => Say(Host.DevRefill()))));

        col.AddChild(Heading("State"));
        _stored = new Label();
        col.AddChild(Row(Button("Store", () => { Say(Host.DevStore()); ShowStored(); }), Button("Restore", () => Say(Host.DevRestore())), _stored));
        ShowStored();

        col.AddChild(Heading("Time"));
        _freeze = new CheckBox { Text = "Freeze", ButtonPressed = Host.DevFrozen };
        _freeze.Toggled += on => { Host.DevFrozen = on; Say(on ? "frozen: no ticks run until Freeze is off (Step runs one)" : "running"); };
        _scale = new OptionButton();
        foreach (var s in Scales) _scale.AddItem($"x{s:0.##}");
        _scale.Selected = System.Math.Max(0, System.Array.IndexOf(Scales, Host.DevScale));
        _scale.ItemSelected += i => { Host.DevScale = Scales[i]; Say($"time scale x{Scales[i]:0.##} (the ticks are the same; only their pace changes)"); };
        col.AddChild(Row(_freeze, Button("Step 1 tick", () => Say(Host.DevStep(1))), Button("Step 10", () => Say(Host.DevStep(10))), new Label { Text = "speed" }, _scale));

        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        _message.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.5f));
        col.AddChild(_message);
        col.AddChild(Row(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill }, Button("Close", Host.DevClose)));
    }

    public override void _Process(double delta) => _status.Text = Host.DevStatus();

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode == Key.Escape || (k.Keycode is Key.F2 or Key.Quoteleft && k.CtrlPressed))
        {
            GetViewport().SetInputAsHandled();
            Host.DevClose();
        }
    }

    private void GoPoint()
    {
        if (_pointList.Count == 0) return;
        _lastPoint = _points.Selected;
        var p = _pointList[_points.Selected];
        Say(Host.DevTeleport(p.X, p.Z, p.Heading));
        FillHere();
    }

    private void StartMission()
    {
        if (_missionList.Count == 0) return;
        Say(Host.DevStart(_missionList[_missions.Selected].Id, _carFiles[_cars.Selected]));
    }

    private void FillPoints(int select)
    {
        _pointList = Host.DevPoints();
        _points.Clear();
        foreach (var p in _pointList) _points.AddItem($"{p.Name}  ({p.X:0}, {p.Z:0})");
        _points.Disabled = _pointList.Count == 0;
        if (_pointList.Count == 0) _points.AddItem("(no places)");
        else _points.Selected = System.Math.Clamp(select, 0, _pointList.Count - 1);
    }

    private void Keep()
    {
        Say(Host.DevKeep(_keepName.Text));
        _keepName.Text = "";
        FillPoints(0);   // the kept places come first, the newest at the top
    }

    private void Forget()
    {
        if (_pointList.Count == 0) return;
        Say(Host.DevForget(_pointList[_points.Selected].Name));
        FillPoints(_points.Selected);
    }

    private void ApplyLoadout()
    {
        var lines = new List<string>();
        for (int i = 0; i < _mounts.Count; i++)
        {
            var (m, pick) = _mounts[i];
            if (pick.Selected < 0) continue;
            var file = m.Options[pick.Selected].File;
            if (file.Equals(m.Current, System.StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(Host.DevSwap(m.Hardpoint, file));
            _mounts[i] = (m with { Current = file }, pick);
        }
        Say(lines.Count == 0 ? "the loadout is as chosen already" : string.Join("\n", lines));
    }

    private void GoCoords()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(_x.Text, inv, out double x) || !double.TryParse(_z.Text, inv, out double z)) { Say("x and z must be numbers (game units)"); return; }
        float h = float.TryParse(_heading.Text, inv, out float hh) ? hh : 0;
        Say(Host.DevTeleport(x, z, h));
    }

    private void FillHere()
    {
        if (Host.DevHere() is not { } here) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _x.Text = here.X.ToString("0.0", inv);
        _z.Text = here.Z.ToString("0.0", inv);
        _heading.Text = here.Heading.ToString("0", inv);
    }

    private void ShowStored() => _stored.Text = Host.DevStored ?? "(nothing stored)";

    private void Say(string line)
    {
        _message.Text = line;
        GD.Print($"developer: {line}");
    }

    private static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", new Color(0.95f, 0.65f, 0.25f));
        return l;
    }

    private static Button Button(string text, System.Action pressed)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
        b.Pressed += pressed;
        return b;
    }

    private static LineEdit Field(string hint) => new() { PlaceholderText = hint, CustomMinimumSize = new Vector2(76, 0) };

    private static HBoxContainer Row(params Control[] items)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", 6);
        foreach (var c in items) r.AddChild(c);
        return r;
    }
}
