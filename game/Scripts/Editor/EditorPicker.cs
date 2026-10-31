using Godot;
using I76.Editor;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>
/// The map editor's map list, in the modern HUD's style: the maps saved from the editor (scratch maps), then every mission in the
/// install by its title (Jorczak's Peak (M14)), grouped as the game groups them, with a filter box. Edit opens the selected map; New flat map starts a flat map on the selected mission's tile layout, sky and light.
/// Used from the shell's Map Editor entry and from the editor's map menu.
/// </summary>
public partial class EditorPicker : Control
{
    public required GameInstall Install { get; init; }
    /// <summary>The map to edit: a mission name (a stem).</summary>
    public Action<string>? Picked { get; init; }
    public Action? Cancelled { get; init; }

    private ItemList _list = null!;
    private Label _info = null!;
    private LineEdit _filter = null!;
    private Button _edit = null!, _flat = null!, _geo = null!;
    private readonly List<(string Mission, bool Own)?> _rows = [];
    private static int _remembered = -1;

    public override void _Ready()
    {
        float s = Math.Clamp(GetViewportRect().Size.Y / 1080f, 0.8f, 2f);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = new Color(0.05f, 0.03f, 0.02f, 0.88f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var panel = new PanelContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f };
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        panel.OffsetLeft = -430 * s; panel.OffsetRight = 430 * s; panel.OffsetTop = -Math.Min(400 * s, GetViewportRect().Size.Y * 0.46f); panel.OffsetBottom = -panel.OffsetTop;
        AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", (int)(10 * s));
        panel.AddChild(box);
        var title = new Label { Text = "MAP EDITOR" };
        EditorStyle.Title(title, 34 * s);
        box.AddChild(title);
        var sub = new Label { Text = "Pick a map to edit, or start a new flat one. Saving never writes over a stock map: it asks for a name and goes to the addon folder.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(sub, 16 * s, dim: true);
        box.AddChild(sub);
        _filter = new LineEdit { PlaceholderText = "Type to filter by title or code", ClearButtonEnabled = true };
        _filter.AddThemeFontOverride("font", HudStyle.Bold);
        _filter.AddThemeFontSizeOverride("font_size", (int)(18 * s));
        _filter.TextChanged += Filtered;
        _filter.TextSubmitted += _ => Choose();
        _filter.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Down or Key.Up } k)
            {
                _filter.AcceptEvent();
                int[] sel = _list.GetSelectedItems();
                int i = sel.Length > 0 ? sel[0] : -1, d = k.Keycode == Key.Down ? 1 : -1;
                for (int j = i + d; j >= 0 && j < _rows.Count; j += d) if (_rows[j] is not null) { _list.Select(j); _list.EnsureCurrentIsVisible(); Selected(j); break; }
            }
        };
        box.AddChild(_filter);
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All };
        _list.AddThemeFontOverride("font", HudStyle.Bold);
        _list.AddThemeFontSizeOverride("font_size", (int)(18 * s));
        _list.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _list.AddThemeColorOverride("font_selected_color", HudStyle.Ink);
        _list.AddThemeColorOverride("font_hovered_color", HudStyle.Amber);
        _list.AddThemeStyleboxOverride("panel", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.9f), 6, s));
        _list.AddThemeStyleboxOverride("selected", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _list.AddThemeStyleboxOverride("selected_focus", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _list.AddThemeStyleboxOverride("hovered", EditorStyle.Flat(new Color(HudStyle.Amber, 0.18f), 4, s));
        _list.AddThemeConstantOverride("v_separation", (int)(4 * s));
        box.AddChild(_list);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 48 * s) };
        EditorStyle.Body(_info, 16 * s, dim: true);
        box.AddChild(_info);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(10 * s));
        box.AddChild(row);
        _edit = EditorStyle.Button("EDIT", Choose, s, 20);
        _flat = EditorStyle.Button("NEW FLAT MAP...", NewFlat, s, 20);
        _geo = EditorStyle.Button("REAL-WORLD MAP...", NewGeo, s, 20);
        var back = EditorStyle.Button("BACK", () => Cancelled?.Invoke(), s, 20);
        row.AddChild(_edit); row.AddChild(_flat); row.AddChild(_geo);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(back);
        Fill();
        _list.ItemSelected += i => Selected((int)i);
        _list.ItemActivated += i => { Selected((int)i); Choose(); };
        int first = _remembered >= 0 && _remembered < _rows.Count && _rows[_remembered] is not null ? _remembered : _rows.FindIndex(r => r is not null);
        if (first >= 0) { _list.Select(first); Selected(first); }
        _filter.GrabFocus();
    }

    private void Fill()
    {
        _list.Clear();
        _rows.Clear();
        string f = _filter.Text.Trim();
        bool Match(string label, string stem) => f.Length == 0 || label.Contains(f, StringComparison.OrdinalIgnoreCase) || stem.Contains(f, StringComparison.OrdinalIgnoreCase);
        var own = NewMap.UserMaps(Install.AddonDir).Where(m => Match(m.Listed + " " + m.Stem, m.Stem)).ToList();
        if (own.Count > 0)
        {
            Header("YOUR MAPS  (addon folder)");
            foreach (var (stem, listed, _) in own)
            {
                _list.AddItem(listed.Equals(stem, StringComparison.OrdinalIgnoreCase) ? stem : $"{listed}  ({stem})");
                _rows.Add((stem.ToLowerInvariant(), true));
            }
        }
        string? group = null;
        foreach (var m in _stock ??= MissionTitles.Stock(Install))
        {
            if (!Match(m.Label, m.Stem)) continue;
            if (m.Group != group) Header((group = m.Group).ToUpperInvariant());
            _list.AddItem(m.Label);
            _rows.Add((m.Stem, false));
        }
        if (_rows.Count == 0) Header("NO MAP MATCHES");
    }

    private List<MapListing>? _stock;

    private void Header(string text)
    {
        int h = _list.AddItem($"— {text} —", null, false);
        _list.SetItemDisabled(h, true);
        _rows.Add(null);
    }

    private void Filtered(string _)
    {
        Fill();
        int first = _rows.FindIndex(r => r is not null);
        if (first >= 0) { _list.Select(first); Selected(first); }
    }

    private void Selected(int i)
    {
        _remembered = i;
        if (i < 0 || i >= _rows.Count || _rows[i] is not { } r) return;
        _info.Text = r.Own
            ? $"{r.Mission}: a map in the addon folder. Saving writes it in place."
            : $"{r.Mission.ToUpperInvariant()}: a stock mission. Edit its terrain; Save asks for a new name and writes a copy to the addon folder.";
    }

    private void Choose()
    {
        int[] sel = _list.GetSelectedItems();
        if (sel.Length == 0 || sel[0] >= _rows.Count || _rows[sel[0]] is not { } r) return;
        Picked?.Invoke(r.Mission);
    }

    private void NewFlat()
    {
        if (Install.AddonDir is null) { _info.Text = "The install has no addon folder to save a map to."; return; }
        Control? dlg = null;
        void Close() { dlg?.QueueFree(); _filter.GrabFocus(); }
        dlg = FormDialog.NewFlatMap(Install, (name, listed, tiles, metres) =>
        {
            try
            {
                NewMap.CreateFlat(Install.AddonDir!, name, listed, tiles, metres, Install.IsNitro);
                Close();
                Picked?.Invoke(name.ToLowerInvariant());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Close(); _info.Text = $"Could not create it: {ex.Message}"; }
        }, Close);
        AddChild(dlg);
    }

    private void NewGeo()
    {
        if (Install.AddonDir is null) { _info.Text = "The install has no addon folder to save a map to."; return; }
        Control? dlg = null;
        void Close() { dlg?.QueueFree(); _filter.GrabFocus(); }
        dlg = new GeoMapDialog { Install = Install, Built = stem => { Close(); Picked?.Invoke(stem); }, Cancelled = Close };
        AddChild(dlg);
    }

    private int _frames;

    /// <summary>I76_EDITOR_PICK=NAME: picks that map on the third frame, for captures and tests of the path from the shell.</summary>
    public override void _Process(double delta)
    {
        if (++_frames != 3 || OS.GetEnvironment("I76_EDITOR_PICK") is not { Length: > 0 } pick) return;
        if (_rows.FirstOrDefault(r => r is { } m && m.Mission.Equals(pick, StringComparison.OrdinalIgnoreCase)) is { } hit) Picked?.Invoke(hit.Mission);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { GetViewport().SetInputAsHandled(); Cancelled?.Invoke(); }
    }
}
