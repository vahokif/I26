using Godot;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Game.Ui;

/// <summary>
/// The start menu: the missions in miss16, under the original's TRIP
/// (A01, then T01–T17) and MELEE (M, S) headings, each with its first
/// objective line. Picking one reloads the scene into it, driving the
/// mission's own player car.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    public required GameInstall Install { get; init; }
    /// <summary>Esc: back to the shell when it opened this list; quits otherwise.</summary>
    public Action? OnBack { get; init; }
    private ItemList _list = null!;
    private Label _info = null!;
    private readonly List<string> _ids = [];
    private Label _car = null!;
    private int _carChoice;
    private I76.Sim.Trip.TripCar? _op;
    private string _opError = "";
    private static int _remembered;     // the choice survives coming back to the list
    internal static void ResetForGameSwitch() => _remembered = 0;

    /// <summary>The cars a pick can start in (debug): null .vcf = the mission's own player car.</summary>
    private static readonly (string Label, string? Vcf)[] Cars =
    [
        ("OP Piranha: best parts from the catalogue, heaviest guns, armour 150 a zone", "dbgop.vcf"),
        ("Stock T17 Piranha (vppt17: V-10, Structo Bumper, Nitrous, Blower)", "vppt17.vcf"),
        ("Army tank (vstank1)", "vstank1.vcf"),
        ("The mission's own car (the first Piranha variant in most)", null),
    ];

    public override void _Ready()
    {
        var bg = new ColorRect { Color = new Color(0.08f, 0.06f, 0.05f), AnchorRight = 1, AnchorBottom = 1 };
        AddChild(bg);
        var box = new VBoxContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0, AnchorBottom = 1, OffsetLeft = -380, OffsetRight = 380, OffsetTop = 40, OffsetBottom = -40 };
        AddChild(box);
        var title = new Label { Text = "INTERSTATE '76", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 44);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        box.AddChild(title);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_list);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 60) };
        box.AddChild(_info);
        _car = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _car.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        box.AddChild(_car);
        var hint = new Label { Text = $"Enter or double-click to play  ·  Left/Right to change the car  ·  Esc to {(OnBack is null ? "quit" : "go back")}", HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(hint);

        var vfs = new AssetVfs(Install);
        try
        {
            var db = I76.Sim.Trip.PartsDatabase.Build(vfs);
            _op = I76.Sim.Trip.DebugCar.Build(I76.Sim.Trip.TripCar.FromVcf(vfs.Read("vppirna1.vcf")), db, vfs);
        }
        catch (Exception ex) { _opError = ex.Message; GD.PrintErr($"debug car: {ex}"); }
        _carChoice = _op is null && _remembered == 0 ? 1 : _remembered;
        ShowCar();
        string? section = null;
        foreach (var id in MissionIds(Install))
        {
            var sec = IsTrip(id) ? "TRIP" : "MELEE";
            if (sec != section) { section = sec; int h = _list.AddItem($"— {sec} —", null, false); _list.SetItemDisabled(h, true); _ids.Add(""); }
            string first = "";
            if (vfs.TryRead(id.ToLowerInvariant() + ".npt") is { } npt)
                first = ObjectiveText.Parse(System.Text.Encoding.Latin1.GetString(npt)).Objectives.FirstOrDefault().Text ?? "";
            string? name = sec == "MELEE" ? ArenaName(Install, id) : null;
            _list.AddItem(name is null ? $"{id}   {first}" : first.Length > 0 ? $"{id}   {name} — {first}" : $"{id}   {name}");
            _ids.Add(id);
        }
        _list.ItemSelected += i => _info.Text = _ids[(int)i] is { Length: > 0 } id ? $"{id}: {_list.GetItemText((int)i)[(id.Length + 3)..]}" : "";
        _list.ItemActivated += i => Play((int)i);
        int firstPlayable = _ids.FindIndex(x => x.Length > 0);
        if (firstPlayable >= 0) { _list.Select(firstPlayable); _list.GrabFocus(); }
    }

    private void ShowCar()
    {
        var (label, vcf) = Cars[_carChoice];
        _car.Text = $"Car {_carChoice + 1}/{Cars.Length}: {label}" + (vcf == "dbgop.vcf" && _op is null ? $"  (unavailable: {_opError})" : "");
    }

    private void Play(int i)
    {
        if (i < 0 || i >= _ids.Count || _ids[i].Length == 0) return;
        _remembered = _carChoice;
        var vcf = Cars[_carChoice].Vcf;
        if (vcf == "dbgop.vcf")
        {
            // the car goes into the addon folder's memory like the shell's vehscn.vcf; the trip's own save is untouched
            if (_op is null) vcf = "vppt17.vcf"; else AssetVfs.Addon[vcf] = _op.ToVcf();
        }
        Boot.Main.DebugCar = vcf;
        Boot.Main.DevCar = null;
        Boot.Main.NextMission = _ids[i];
        GetTree().ReloadCurrentScene();
    }

    // _Input: the list would take the arrows first
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Left or Key.Right } k)
        {
            _carChoice = (_carChoice + (k.Keycode == Key.Right ? 1 : Cars.Length - 1)) % Cars.Length;
            ShowCar();
            GetViewport().SetInputAsHandled();
            return;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        if (OnBack is { } back) back(); else GetTree().Quit();
    }

    /// <summary>The install's missions (its mission folder's files), upper case without the extension: the trip's (A, T; Nitro's P, B), the melee maps, the scenarios.</summary>
    internal static List<string> MissionIds(GameInstall install)
    {
        var dir = install.MissionDir;
        var files = dir is null ? [] : Directory.EnumerateFiles(dir).Where(f => I76.Formats.Vfs.GameInstall.IsMissionFile(f))
            .Select(f => Path.GetFileNameWithoutExtension(f).ToUpperInvariant()).ToList();
        int Order(string id) => id[0] switch { 'A' => 0, 'T' => 1, 'P' => 0, 'B' => 1, 'S' => 3, _ => 2 };
        return [.. files.OrderBy(Order).ThenBy(x => x, StringComparer.Ordinal)];
    }

    internal static bool IsTrip(string id) => id[0] is 'A' or 'T' or 'P' or 'B';

    /// <summary>A melee or scenario mission's name: the shell's tables (the arenas, the scenarios), else its WRLD's own name.</summary>
    internal static string? ArenaName(GameInstall Install, string id)
    {
        foreach (var t in new[] { I76.Game.Shell.MeleeData.Arenas, I76.Game.Shell.MeleeData.Scenarios })
            foreach (var (name, msn) in t)
                if (msn.Equals(id, StringComparison.OrdinalIgnoreCase)) return name;
        try
        {
            if (Install.FindMission(id.ToLowerInvariant()) is { } path && I76.Formats.Mission.WorldSettings.ArenaName(new I76.Formats.Mission.MissionFile(path)) is { Length: > 0 } n)
                return n.Trim();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        return null;
    }

    /// <summary>I76_MENU_PICK=A01: pick that mission on the third frame, for off-screen captures of the menu path; I76_MENU_SHOW=M15 only selects it and scrolls to it.</summary>
    private int _frames;
    public override void _Process(double delta)
    {
        if (++_frames != 3) return;
        if (System.Environment.GetEnvironmentVariable("I76_MENU_PICK") is { Length: > 0 } pick)
            Play(_ids.IndexOf(pick.ToUpperInvariant()));
        else if (System.Environment.GetEnvironmentVariable("I76_MENU_SHOW") is { Length: > 0 } show && _ids.IndexOf(show.ToUpperInvariant()) is >= 0 and var i)
        {
            _list.Select(i);
            _list.EnsureCurrentIsVisible();
            _list.EmitSignal(ItemList.SignalName.ItemSelected, i);
        }
    }
}
