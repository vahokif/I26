using Godot;
using I76.Editor;
using I76.Editor.Objects;
using I76.Formats.Mission.Edit;
using LineEdit = Godot.LineEdit;
using I76.Game.Net;
using NVec3 = System.Numerics.Vector3;
using NVec2 = System.Numerics.Vector2;

namespace I76.Game.Editor.Objects;

/// <summary>The object tools' windows (the palette along the bottom, the inspector and the map check at the right), their input and the capture hooks.</summary>
public partial class ObjectEditor
{
    private float _s = 1;
    private Control? _root;
    private PanelContainer? _palette, _inspector, _checks;
    private VBoxContainer? _inspectBox, _checkBox;
    private ItemList? _list;
    private LineEdit? _search;
    private Label? _hoverLabel, _checkTitle;
    private Control? _boxRect;
    private readonly Dictionary<ObjectCategory, Button> _tabs = [];
    private readonly Dictionary<ObjectTool, Button> _toolButtons = [];
    private Button? _snapButton, _flattenButton, _cutButton, _spawnButton, _regenButton, _flagButton, _checkButton, _teamButton;
    private ObjectCategory _tab = ObjectCategory.Buildings;
    private List<CatalogueItem> _shown = [];
    private Thumbnails? _thumbs;
    private bool _uiSync;
    private Button? _selectButton, _placeButton;

    private static readonly (ObjectCategory Cat, string Name)[] Tabs =
    [
        (ObjectCategory.Buildings, "BUILDINGS"), (ObjectCategory.Structures, "BRIDGES, RAMPS, TUNNELS"), (ObjectCategory.Roadside, "ROADSIDE"), (ObjectCategory.Props, "PROPS"),
        (ObjectCategory.Vegetation, "PLANTS"), (ObjectCategory.Fences, "FENCES"), (ObjectCategory.Markers, "MARKERS"), (ObjectCategory.Scripted, "SCRIPTED"),
    ];

    /// <summary>The BUILD group in the tool column, and the windows. Called whenever the editor's UI is built (it is built again on a resize).</summary>
    public void BuildUi(Control root, VBoxContainer tools, ButtonGroup group, float s)
    {
        _root = root; _s = s;
        _tabs.Clear(); _toolButtons.Clear();
        tools.AddChild(new HSeparator());
        var bl = new Label { Text = "OBJECTS" };
        EditorStyle.Title(bl, 18 * s);
        tools.AddChild(bl);
        Button Tool(string text, string tip, Action a)
        {
            var b = EditorStyle.Button(text, a, s, 20, toggle: true);
            b.ButtonGroup = group; b.TooltipText = tip; b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(EditorStyle.ToolWidth * s, 30 * s);
            tools.AddChild(b);
            return b;
        }
        _selectButton = Tool("M   SELECT", "Click, drag a box, move, turn, copy, bulldoze", () => SetTool(ObjectTool.Select));
        _placeButton = Tool("P   BUILD", "Buildings, props, plants, bridges: pick from the palette", () => { SetTool(ObjectTool.Place); Disarm(); ShowTab(_tab); });
        _spawnButton = Tool("J   SPAWN", "Spawn points (numbered by order)", () => ArmMarker(MarkerKind.Spawn));
        _regenButton = Tool("R   REGEN", "Regeneration pad", () => ArmMarker(MarkerKind.Regen));
        _flagButton = Tool("Y   FLAG", "CTF flag for a team", () => ArmMarker(MarkerKind.Flag));
        _checkButton = Tool("Z   CHECKPOINT", "Race checkpoint with the next number", () => ArmMarker(MarkerKind.Checkpoint));
        BuildPalette(root, s);
        BuildSidePanels(root, s);
        _boxRect = new Panel { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        var boxStyle = EditorStyle.Flat(new Color(MarkerView.Select, 0.12f), 0, s, MarkerView.Select);
        ((Panel)_boxRect).AddThemeStyleboxOverride("panel", boxStyle);
        root.AddChild(_boxRect);
        UiToolChanged();
        UiChecksChanged();
    }

    /// <summary>
    /// The road tool's BRIDGE / TUNNEL button: the build tool on the structures tab, snapping to roads (and cutting them where a deck carries
    /// them): pick a bridge, ramp or tunnel and lay it along a road.
    /// </summary>
    public void PickStructure()
    {
        Snap = SnapMode.Roads;
        _tab = ObjectCategory.Structures;
        SetTool(ObjectTool.Place);
        Disarm();
        ShowTab(ObjectCategory.Structures);
        Ed.Toast("Pick a bridge, ramp or tunnel, then click on a road: it lies along it" + (CutRoads ? "; a bridge's deck replaces the road under it, a tunnel leaves the road running through" : ""));
    }

    private void ArmMarker(MarkerKind kind)
    {
        Tool = ObjectTool.Place;
        if (kind == MarkerKind.Flag && Team is < 1 or > 4) Team = 1;
        if (kind == MarkerKind.Spawn && ModeNow.Mode != MapMode.Ctf) Team = 0;
        if (kind == MarkerKind.Spawn && ModeNow.Mode == MapMode.Ctf && Team == 0) Team = 1;
        Arm(MarkerItem(kind, Team));
        _tab = ObjectCategory.Markers;
    }

    private void BuildPalette(Control root, float s)
    {
        _palette = new PanelContainer { Name = "ObjectPalette", Visible = false };
        _palette.AnchorLeft = 0; _palette.AnchorRight = 1; _palette.AnchorTop = 1; _palette.AnchorBottom = 1;
        _palette.OffsetLeft = 240 * s; _palette.OffsetRight = -14 * s; _palette.OffsetBottom = -44 * s; _palette.OffsetTop = -(44 + 330) * s;
        _palette.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        root.AddChild(_palette);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        _palette.AddChild(box);

        var tabRow = new HBoxContainer();
        tabRow.AddThemeConstantOverride("separation", (int)(5 * s));
        var group = new ButtonGroup();
        foreach (var (cat, name) in Tabs)
        {
            var b = EditorStyle.Button(name, () => { if (!_uiSync) ShowTab(cat); }, s, 18, toggle: true);
            b.ButtonGroup = group;
            tabRow.AddChild(b);
            _tabs[cat] = b;
        }
        tabRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        box.AddChild(tabRow);

        var opts = new HBoxContainer();
        opts.AddThemeConstantOverride("separation", (int)(8 * s));
        _search = new LineEdit { PlaceholderText = "Search by name or code...", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(220 * s, 0) };
        _search.AddThemeFontOverride("font", HudStyle.Bold);
        _search.AddThemeFontSizeOverride("font_size", (int)(16 * s));
        _search.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _search.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 5));
        _search.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 5));
        _search.TextChanged += _ => Refill();
        opts.AddChild(_search);
        _snapButton = EditorStyle.Button("", CycleSnap, s, 18);
        _snapButton.TooltipText = "N: snap to roads and grid, to the grid, or nothing (Alt held: nothing)";
        opts.AddChild(_snapButton);
        _flattenButton = EditorStyle.Button("FLATTEN GROUND  L", () => { Flatten = !Flatten; UiToolChanged(); }, s, 18, toggle: true);
        _flattenButton.TooltipText = "Level the ground under a building as it is placed (one undo)";
        opts.AddChild(_flattenButton);
        _cutButton = EditorStyle.Button("CUT ROADS", () => { CutRoads = !CutRoads; UiToolChanged(); }, s, 18, toggle: true);
        _cutButton.TooltipText = "End the road at a bridge's or ramp's ends when one is placed on it";
        opts.AddChild(_cutButton);
        _teamButton = EditorStyle.Button("", CycleTeam, s, 18);
        _teamButton.TooltipText = "T: the team a spawn or a flag is for; the finish line for a checkpoint";
        opts.AddChild(_teamButton);
        _hoverLabel = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        EditorStyle.Body(_hoverLabel, 16 * s, dim: true);
        opts.AddChild(_hoverLabel);
        box.AddChild(opts);

        _list = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, SameColumnWidth = true, MaxTextLines = 3, IconMode = ItemList.IconModeEnum.Top,
            FixedIconSize = new Vector2I((int)(88 * s), (int)(88 * s)), FixedColumnWidth = (int)(150 * s), MaxColumns = 0, AllowReselect = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _list.AddThemeFontOverride("font", HudStyle.Bold);
        _list.AddThemeFontSizeOverride("font_size", (int)(14 * s));
        _list.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _list.AddThemeColorOverride("font_selected_color", HudStyle.Ink);
        _list.AddThemeColorOverride("font_hovered_color", HudStyle.Amber);
        _list.AddThemeStyleboxOverride("panel", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.6f), 5, s, null, 4));
        _list.AddThemeStyleboxOverride("selected", EditorStyle.Flat(HudStyle.Amber, 5, s));
        _list.AddThemeStyleboxOverride("selected_focus", EditorStyle.Flat(HudStyle.Amber, 5, s));
        _list.AddThemeStyleboxOverride("hovered", EditorStyle.Flat(new Color(0.34f, 0.18f, 0.09f, 0.9f), 5, s));
        _list.AddThemeConstantOverride("icon_margin", (int)(4 * s));
        _list.ItemSelected += i => { if (!_uiSync && i >= 0 && i < _shown.Count) { Arm(_shown[(int)i]); } };
        _list.ItemClicked += (i, _, _) => { if (i >= 0 && i < _shown.Count) Arm(_shown[(int)i]); };
        _list.GuiInput += e =>
        {
            if (e is InputEventMouseMotion mm && _list.GetItemAtPosition(mm.Position, true) is var i && i >= 0 && i < _shown.Count) _hoverLabel!.Text = HoverText(_shown[i]);
        };
        _list.MouseExited += () => _hoverLabel!.Text = TabNote();
        box.AddChild(_list);
        var credit = new Label { Text = AssetBible.Credit, HorizontalAlignment = HorizontalAlignment.Right, ClipText = true };
        EditorStyle.Body(credit, 12 * s, dim: true);
        box.AddChild(credit);
    }

    private static string HoverText(CatalogueItem it)
    {
        if (it.Marker is MarkerKind.Spawn or MarkerKind.Regen or MarkerKind.Checkpoint) return it.Label;
        string use = it.Category == ObjectCategory.Scripted ? "needs a mission script" : it.NotInStockMaps ? "in no stock map" : it.NitroMapsOnly ? "Nitro Pack maps only" : it.InMelee ? "in melee maps" : "campaign only";
        return $"{(it.Description is { } d ? d + "   " : "")}{it.Name.ToUpperInvariant()}   {it.Width:0.#} x {it.Depth:0.#} m, {it.Height:0.#} m high   type {it.Type:x}{(it.NotInStockMaps || it.NitroMapsOnly ? "" : $"   placed {it.Placed}x in {it.Maps} maps")}   {use}";
    }

    /// <summary>What the line beside the buttons says when nothing is hovered.</summary>
    private string TabNote() => _tab == ObjectCategory.Scripted && (_search?.Text.Length ?? 0) == 0 ? "These need a mission script: placed in a melee they just stand there" : "";

    private void ShowTab(ObjectCategory cat)
    {
        _tab = cat;
        _search?.Clear();
        Refill();
    }

    private void Refill()
    {
        if (_list is null) return;
        EnsureThumbs();
        string q = _search?.Text.Trim().ToLowerInvariant() ?? "";
        var all = Catalogue;
        _shown = q.Length > 0 ? all.Where(i => i.Name.Contains(q) || (i.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || i.Category.ToString().ToLowerInvariant().Contains(q)).OrderBy(i => i.Category).ThenBy(i => i.Name).ToList()
                              : all.Where(i => i.Category == _tab).ToList();
        _uiSync = true;
        _list.Clear();
        foreach (var it in _shown)
        {
            int idx = _list.AddItem(it.Marker is MarkerKind.Spawn or MarkerKind.Regen or MarkerKind.Checkpoint ? it.Label : WrapLabel(it.Title), _thumbs!.Get(it) ?? Placeholder());
            _list.SetItemTooltip(idx, it.Tooltip() + (it.NeedsScript ? "\nInert in a melee: only a mission script drives it." : it.InMelee || it.NotInStockMaps || it.NitroMapsOnly ? "" : "\nPlaced by campaign missions only (harmless in a melee)."));
            if (it == Armed) _list.Select(idx);
            if (!it.InMelee && it.Marker == MarkerKind.None) _list.SetItemCustomFgColor(idx, new Color(HudStyle.ParchmentDim, 0.8f));
            _thumbs.Request(it, true);
        }
        foreach (var it in Catalogue) _thumbs!.Request(it, false);
        if (_hoverLabel is not null) _hoverLabel.Text = TabNote();
        foreach (var (cat, b) in _tabs) { b.SetPressedNoSignal(cat == _tab && q.Length == 0); b.Text = $"{Tabs.First(t => t.Cat == cat).Name}  {Catalogue.Count(i => i.Category == cat)}"; }
        _uiSync = false;
    }

    /// <summary>A label broken at word boundaries into at most 3 lines that fit a tile; what does not fit ends in an ellipsis (the tooltip has it all).</summary>
    private string WrapLabel(string text)
    {
        var font = _list!.GetThemeFont("font");
        int size = _list.GetThemeFontSize("font_size");
        float max = _list.FixedColumnWidth - 14 * _s;
        float W(string t) => font.GetStringSize(t, HorizontalAlignment.Left, -1, size).X;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>(); string cur = ""; bool more = false;
        for (int i = 0; i < words.Length; i++)
        {
            string t = cur.Length == 0 ? words[i] : cur + " " + words[i];
            if (W(t) <= max || cur.Length == 0) { cur = t; continue; }
            lines.Add(cur); cur = words[i];
            if (lines.Count == 2 && i < words.Length) { /* the third line takes the rest */ }
            if (lines.Count == 3) { more = true; cur = ""; break; }
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 3 && !more)
        {
            // the third line took everything after the second: does it fit?
            string rest = lines[2];
            if (W(rest) > max) more = true;
        }
        if (more || lines.Count > 3)
        {
            lines = lines.Take(3).ToList();
            string last = lines[2];
            while (last.Length > 1 && W(last + "...") > max) last = last[..^1];
            lines[2] = last.TrimEnd() + "...";
        }
        return string.Join("\n", lines);
    }

    private static Texture2D? _placeholder;
    private static Texture2D Placeholder() => _placeholder ??= ImageTexture.CreateFromImage(Image.CreateEmpty(8, 8, false, Image.Format.Rgba8));

    private void EnsureThumbs()
    {
        if (_thumbs is not null && GodotObject.IsInstanceValid(_thumbs)) return;
        string key = ObjectCatalogue.CacheName(Ed.Install!);
        string dir = ProjectSettings.GlobalizePath($"user://cache/editor-thumbs/{key}");
        _thumbs = new Thumbnails { Name = "Thumbnails", CacheDir = dir, Model = item => MakeModelNode(item, false), Ready = OnThumb };
        AddChild(_thumbs);
    }

    private void OnThumb(CatalogueItem item, Texture2D tex)
    {
        if (_list is null || !GodotObject.IsInstanceValid(_list)) return;
        int i = _shown.IndexOf(item);
        if (i >= 0 && i < _list.ItemCount) _list.SetItemIcon(i, tex);
    }

    private void CycleSnap()
    {
        Snap = Snap switch { SnapMode.Roads => SnapMode.Grid, SnapMode.Grid => SnapMode.Off, _ => SnapMode.Roads };
        UiToolChanged();
        Ed.Toast($"Snap: {SnapName}");
    }

    private string SnapName => Snap switch { SnapMode.Roads => "roads and grid", SnapMode.Grid => $"grid {Grid:0} m", _ => "off" };

    private void CycleTeam()
    {
        if (Armed?.Marker == MarkerKind.Checkpoint) { Finish = !Finish; ClearGhost(); MakeGhost(); }
        else if (Armed?.Marker == MarkerKind.Flag) { Team = Team % TeamCount + 1; Arm(MarkerItem(MarkerKind.Flag, Team)); }
        else if (Armed?.Marker == MarkerKind.Spawn) { Team = ModeNow.Mode == MapMode.Ctf ? Team % TeamCount + 1 : 0; }
        UiToolChanged();
    }

    /// <summary>How many teams the map has: a CTF map's (.cf2-4), else the most there are (4).</summary>
    private int TeamCount => ModeNow is { Mode: MapMode.Ctf, Teams: > 0 and var n } ? n : 4;

    private void UiToolChanged()
    {
        if (_palette is null || !GodotObject.IsInstanceValid(_palette)) return;
        _uiSync = true;
        bool place = Tool == ObjectTool.Place;
        _palette.Visible = place;
        if (_palette.Visible && (_list?.ItemCount ?? 0) == 0 || (_palette.Visible && _shownTab != _tab)) { Refill(); _shownTab = _tab; }
        _selectButton?.SetPressedNoSignal(Tool == ObjectTool.Select);
        _placeButton?.SetPressedNoSignal(place && Armed?.Marker is null or MarkerKind.None || place && Armed is null);
        _spawnButton?.SetPressedNoSignal(Armed?.Marker == MarkerKind.Spawn);
        _regenButton?.SetPressedNoSignal(Armed?.Marker == MarkerKind.Regen);
        _flagButton?.SetPressedNoSignal(Armed?.Marker == MarkerKind.Flag);
        _checkButton?.SetPressedNoSignal(Armed?.Marker == MarkerKind.Checkpoint);
        if (_snapButton is not null) _snapButton.Text = $"SNAP: {SnapName.ToUpperInvariant()}  N";
        _flattenButton?.SetPressedNoSignal(Flatten);
        _cutButton?.SetPressedNoSignal(CutRoads);
        if (_teamButton is not null)
        {
            _teamButton.Visible = Armed?.Marker is MarkerKind.Spawn or MarkerKind.Flag or MarkerKind.Checkpoint;
            _teamButton.Text = Armed?.Marker switch
            {
                MarkerKind.Checkpoint => Finish ? "FINISH LINE  T" : "NEXT NUMBER  T",
                MarkerKind.Flag => $"TEAM {Team}  T",
                _ => Team == 0 ? "ANY TEAM  T" : $"TEAM {Team}  T",
            };
        }
        if (_list is not null && Armed is not null) { int i = _shown.IndexOf(Armed); _list.DeselectAll(); if (i >= 0) _list.Select(i); }
        _uiSync = false;
        if (_brushPanel is not null && GodotObject.IsInstanceValid(_brushPanel)) _brushPanel.Visible = !Active;
        UiSelectionChanged();
    }

    private ObjectCategory _shownTab = ObjectCategory.Buildings;
    private Control? _brushPanel;
    /// <summary>The terrain brush's window, which gives way to the inspector while the object tools are in use.</summary>
    /// <summary>The road tool's panel logic shows the brush window whenever it is not on; while the object tools are, it stays hidden.</summary>
    public void KeepBrushPanelAway() { if (Active && _brushPanel is not null && GodotObject.IsInstanceValid(_brushPanel)) _brushPanel.Visible = false; }

    public void SetBrushPanel(Control? panel) { _brushPanel = panel; if (panel is not null) panel.Visible = !Active; }

    // ---- inspector and checks ---------------------------------------------------------------------------------------------------

    private void BuildSidePanels(Control root, float s)
    {
        _inspector = new PanelContainer { Name = "ObjectInspector", Visible = false };
        _inspector.AnchorLeft = 1; _inspector.AnchorRight = 1; _inspector.AnchorTop = 0; _inspector.AnchorBottom = 0;
        _inspector.OffsetRight = -14 * s; _inspector.OffsetTop = 70 * s; _inspector.GrowHorizontal = Control.GrowDirection.Begin;
        _inspector.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        root.AddChild(_inspector);
        _inspectBox = new VBoxContainer { CustomMinimumSize = new Vector2(280 * s, 0) };
        _inspectBox.AddThemeConstantOverride("separation", (int)(5 * s));
        // a short window: the inspector scrolls above the map check (the palette, when open, is below both)
        _inspector.AddChild(EditorStyle.Scrolling(_inspectBox, s, 70, 44 + 338 + 120));

        _checks = new PanelContainer { Name = "MapCheck", Visible = false };
        _checks.AnchorLeft = 1; _checks.AnchorRight = 1; _checks.AnchorTop = 1; _checks.AnchorBottom = 1;
        _checks.OffsetRight = -14 * s; _checks.GrowHorizontal = Control.GrowDirection.Begin; _checks.GrowVertical = Control.GrowDirection.Begin;
        _checks.OffsetBottom = -(44 + 338) * s;
        _checks.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        root.AddChild(_checks);
        _checkBox = new VBoxContainer { CustomMinimumSize = new Vector2(280 * s, 0) };
        _checkBox.AddThemeConstantOverride("separation", (int)(4 * s));
        _checks.AddChild(EditorStyle.Scrolling(_checkBox, s, 70 + 260, 44 + 338));
    }

    private void UiChecksChanged()
    {
        if (_checkBox is null || !GodotObject.IsInstanceValid(_checkBox)) return;
        foreach (var c in _checkBox.GetChildren()) { _checkBox.RemoveChild(c); c.QueueFree(); }
        var (mode, teams) = ModeNow;
        string modeName = mode switch { MapMode.Race => "RACE", MapMode.Ctf => $"CAPTURE THE FLAG, {teams} TEAMS", MapMode.Melee => "MELEE", _ => "MAP" };
        var title = new Label { Text = $"MAP CHECK  -  {modeName}" };
        EditorStyle.Title(title, 17 * _s);
        _checkBox.AddChild(title);
        var spawns = Layer.Spawns.Count(); var flags = Layer.Flags.Count(); var cps = Layer.Checkpoints.Count(); var regens = Layer.Regens.Count();
        var counts = new Label { Text = $"{spawns} spawns   {regens} regens" + (mode == MapMode.Ctf ? $"   {flags} flags" : "") + (mode == MapMode.Race ? $"   {cps} checkpoints" : "") };
        EditorStyle.Body(counts, 14 * _s, dim: true);
        _checkBox.AddChild(counts);
        if (Issues.Count == 0)
        {
            var ok = new Label { Text = "Ready to play." };
            EditorStyle.Body(ok, 15 * _s, colour: new Color(0.55f, 0.95f, 0.5f));
            _checkBox.AddChild(ok);
        }
        foreach (var i in Issues.OrderByDescending(i => i.Severity).Take(8))
        {
            var col = i.Severity switch { Severity.Error => HudStyle.Danger, Severity.Warning => HudStyle.Amber, _ => HudStyle.ParchmentDim };
            string tag = i.Severity switch { Severity.Error => "ERROR  ", Severity.Warning => "WARNING  ", _ => "" };
            var l = new Label { Text = tag + i.Message, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270 * _s, 0) };
            EditorStyle.Body(l, 14 * _s, colour: col);
            _checkBox.AddChild(l);
        }
        if (Issues.Count > 8) { var more = new Label { Text = $"... and {Issues.Count - 8} more" }; EditorStyle.Body(more, 14 * _s, dim: true); _checkBox.AddChild(more); }
        if (_checks is not null) _checks.Visible = Active;
    }

    private void UiSelectionChanged()
    {
        if (_inspectBox is null || !GodotObject.IsInstanceValid(_inspectBox)) return;
        foreach (var c in _inspectBox.GetChildren()) { _inspectBox.RemoveChild(c); c.QueueFree(); }
        if (_inspector is not null) _inspector.Visible = Active && Selection.Count > 0;
        if (_checks is not null) _checks.Visible = Active;
        if (Selection.Count == 0) return;
        float s = _s;
        void Title(string t) { var l = new Label { Text = t }; EditorStyle.Title(l, 18 * s); _inspectBox.AddChild(l); }
        void Row(string label, Control field)
        {
            var r = new HBoxContainer();
            var l = new Label { Text = label, CustomMinimumSize = new Vector2(92 * s, 0) };
            EditorStyle.Body(l, 15 * s, dim: true);
            r.AddChild(l);
            field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            r.AddChild(field);
            _inspectBox.AddChild(r);
        }
        LineEdit Field(string text, Action<string> submit, bool right = false)
        {
            var le = new LineEdit { Text = text, SelectAllOnFocus = true, Alignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            le.AddThemeFontOverride("font", HudStyle.Bold);
            le.AddThemeFontSizeOverride("font_size", (int)(16 * s));
            le.AddThemeColorOverride("font_color", HudStyle.Parchment);
            le.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 4));
            le.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 4));
            le.TextSubmitted += t => { submit(t); GetViewport().GuiReleaseFocus(); };
            le.FocusExited += () => submit(le.Text);
            return le;
        }
        if (Selection.Count > 1)
        {
            Title($"{Selection.Count} OBJECTS");
            var names = Selection.GroupBy(e => e.Name.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}");
            var l = new Label { Text = string.Join("   ", names), AutowrapMode = TextServer.AutowrapMode.WordSmart };
            EditorStyle.Body(l, 14 * s, dim: true); _inspectBox.AddChild(l);
        }
        else
        {
            var e = Selection[0];
            var item = ItemOf(e.Name);
            Title(e.Role switch { MapRole.Spawn => "SPAWN POINT", MapRole.Regen => "REGEN PAD", MapRole.Checkpoint => e.Data.Number >= 99 ? "FINISH LINE" : $"CHECKPOINT {e.Data.Number}", MapRole.Flag => $"TEAM {e.Data.Number} FLAG", _ => e.Name.ToUpperInvariant() });
            if (e.Role == MapRole.Object)
            {
                Row("NAME", Field(e.Name, t => { if (t != e.Name) EditRecord(e, o => o.Name = t.Trim(), "Rename"); }));
                var types = new OptionButton { FocusMode = Control.FocusModeEnum.None };
                var seen = (item?.TypesSeen is { Length: > 0 } ts ? ts : [2u, 3u, 4u, 0xbu, 0xcu]).Union([e.Data.ObjectType]).Distinct().OrderBy(x => x).ToList();
                foreach (var t in seen) types.AddItem($"{t:x}  {TypeName(t)}");
                types.Selected = seen.IndexOf(e.Data.ObjectType);
                types.ItemSelected += i => EditRecord(e, o => o.ObjectType = seen[(int)i], "Change type");
                Row("TYPE", types);
            }
            if (e.Role == MapRole.Spawn)
            {
                var team = new OptionButton { FocusMode = Control.FocusModeEnum.None };
                for (int t = 0; t <= 4; t++) team.AddItem(t == 0 ? "any (melee)" : $"team {t}");
                team.Selected = Math.Clamp(e.Data.Number, 0, 4);
                team.ItemSelected += i => EditRecord(e, o => o.Name = i == 0 ? "spawn" : $"#spawn{i}", "Spawn team");
                Row("TEAM", team);
            }
            if (e.Role == MapRole.Flag)
            {
                var team = new OptionButton { FocusMode = Control.FocusModeEnum.None };
                for (int t = 1; t <= 4; t++) team.AddItem($"team {t}");
                team.Selected = Math.Clamp(e.Data.Number - 1, 0, 3);
                team.ItemSelected += i => EditRecord(e, o => o.Name = $"a1flag{i + 1}", "Flag team");
                Row("TEAM", team);
            }
            if (e.Role == MapRole.Checkpoint)
            {
                var r = new HBoxContainer();
                r.AddThemeConstantOverride("separation", (int)(5 * s));
                r.AddChild(EditorStyle.Button("EARLIER", () => MoveCheckpoint(e, -1), s, 17));
                r.AddChild(EditorStyle.Button("LATER", () => MoveCheckpoint(e, 1), s, 17));
                _inspectBox.AddChild(r);
                _inspectBox.AddChild(EditorStyle.Button(e.Data.Number >= 99 ? "MAKE AN ORDINARY CHECKPOINT" : "MAKE THE FINISH LINE (99)", () =>
                    EditRecord(e, o => o.Name = o.Number >= 99 ? $"check{Layer.NextCheckpointNumber()}" : "check99", "Finish line"), s, 17));
            }
            Row("X", Field(e.Position.X.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), t => MoveTo(e, ParseF(t, e.Position.X), e.Position.Z, e.Yaw), true));
            Row("Z", Field(e.Position.Z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), t => MoveTo(e, e.Position.X, ParseF(t, e.Position.Z), e.Yaw), true));
            Row("HEADING", Field((e.Yaw * 180 / MathF.PI).ToString("0", System.Globalization.CultureInfo.InvariantCulture), t => MoveTo(e, e.Position.X, e.Position.Z, ParseF(t, e.Yaw * 180 / MathF.PI) * MathF.PI / 180), true));
            if (item is { Area: > 0 } && e.Role == MapRole.Object)
            {
                var info = new Label { Text = $"{item.Width:0.#} x {item.Depth:0.#} m, {item.Height:0.#} m high" + (item.NeedsScript ? "\nneeds a mission script" : "") };
                EditorStyle.Body(info, 14 * s, dim: true); _inspectBox.AddChild(info);
            }
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(5 * s));
        row.AddChild(EditorStyle.Button("TURN  ,", () => RotateSelection(-MathF.PI / 12), s, 17));
        row.AddChild(EditorStyle.Button("TURN  .", () => RotateSelection(MathF.PI / 12), s, 17));
        row.AddChild(EditorStyle.Button("COPY", Copy, s, 17));
        _inspectBox.AddChild(row);
        _inspectBox.AddChild(EditorStyle.Button("BULLDOZE  Del", Bulldoze, s, 18));
    }

    private static float ParseF(string t, float fallback) => float.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static string TypeName(uint t) => t switch
    {
        2 => "solid", 3 => "breakable", 4 => "sign", 0xb => "structure", 0xc => "bridge", 7 => "gate", 8 => "gun", 9 => "helicopter", 1 => "vehicle", _ => "",
    };

    /// <summary>The inspector's position and heading fields.</summary>
    private void MoveTo(EditObject e, float x, float z, float yaw)
    {
        var before = new Pose(e.Position, e.Yaw);
        if (MathF.Abs(before.Position.X - x) < 0.005f && MathF.Abs(before.Position.Z - z) < 0.005f && MathF.Abs(Snapping.Wrap(before.Yaw - yaw)) < 1e-4f) return;
        Layer.SetPose(e, new NVec3(x, 0, z), yaw);
        Undo.Push(new ObjectCommands.Transform(Layer, [(e, before, new Pose(e.Position, e.Yaw))], $"Move {e.Name}"));
    }

    private void UiFrame()
    {
        if (_palette is not null && Tool == ObjectTool.Place) { }
    }

    private void UiBox(Vector2? from, Vector2 to)
    {
        if (_boxRect is null) return;
        if (from is not { } a) { _boxRect.Visible = false; return; }
        _boxRect.Visible = true;
        _boxRect.Position = new Vector2(Math.Min(a.X, to.X), Math.Min(a.Y, to.Y));
        _boxRect.Size = (a - to).Abs();
    }

    // ---- input ---------------------------------------------------------------------------------------------------------------

    /// <summary>Mouse buttons for the object tools; true if taken.</summary>
    public bool HandleMouse(InputEventMouseButton mb)
    {
        if (!Active) return false;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            if (!mb.Pressed) return Tool == ObjectTool.Select && _drag != DragKind.None;
            var mouse = Mouse();
            if (Tool == ObjectTool.Place)
            {
                if (Armed is null) { SetTool(ObjectTool.Select); BeginDrag(mouse, mb.ShiftPressed, mb.AltPressed); return true; }
                PlaceHere(mb.ShiftPressed);
                return true;
            }
            BeginDrag(mouse, mb.ShiftPressed, mb.AltPressed);
            return true;
        }
        if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float dir = mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
            if (mb.CtrlPressed)
            {
                float k = dir > 0 ? 0.86f : 1 / 0.86f;
                Cam.Distance = Mathf.Clamp(Cam.Distance * k, EditorCamera.MinDistance, EditorCamera.MaxDistance);
                return true;
            }
            if (Tool == ObjectTool.Place && Armed is not null) { TurnGhost(dir, mb.ShiftPressed); return true; }
        }
        return false;
    }

    private void TurnGhost(float dir, bool free)
    {
        float step = free ? MathF.PI / 180 : Snapping.AngleStep;
        _yaw = free ? Snapping.Wrap(_yaw + dir * step) : Snapping.SnapAngle(_yaw + dir * step, Snapping.AngleStep);
    }

    /// <summary>Keys for the object tools; true if taken. Not called while a text field has the focus.</summary>
    public bool HandleKey(InputEventKey key)
    {
        if (key.CtrlPressed)
        {
            switch (key.Keycode)
            {
                case Key.C when Active: Copy(); return true;
                case Key.V: Paste(); return true;
                case Key.A when Tool == ObjectTool.Select: Selection.Clear(); Selection.AddRange(Layer.Items.Where(i => !i.Locked)); _selDirty = true; return true;
            }
            return false;
        }
        switch (key.Keycode)
        {
            case Key.Escape when Active && !key.Echo:
                if (_drag != DragKind.None) { CancelDrag(); return true; }
                if (Armed is not null) { Disarm(); SetTool(ObjectTool.Select); return true; }
                if (Selection.Count > 0) { Selection.Clear(); _selDirty = true; return true; }
                if (Tool == ObjectTool.Place) { SetTool(ObjectTool.Select); return true; }
                return false;
            case Key.M when !key.Echo: SetTool(ObjectTool.Select); return true;
            case Key.P when !key.Echo: SetTool(ObjectTool.Place); Disarm(); ShowTab(_tab); return true;
            case Key.J when !key.Echo: ArmMarker(MarkerKind.Spawn); return true;
            case Key.R when !key.Echo: ArmMarker(MarkerKind.Regen); return true;
            case Key.Y when !key.Echo: ArmMarker(MarkerKind.Flag); return true;
            case Key.Z when !key.Echo: ArmMarker(MarkerKind.Checkpoint); return true;
        }
        if (!Active) return false;
        float step = key.ShiftPressed ? MathF.PI / 180 : Snapping.AngleStep;
        switch (key.Keycode)
        {
            case Key.Comma or Key.Period:
                float d = key.Keycode == Key.Period ? 1 : -1;
                if (Tool == ObjectTool.Place && Armed is not null) TurnGhost(d, key.ShiftPressed);
                else RotateSelection(d * step);
                return true;
            case Key.Delete or Key.Backspace or Key.X when Tool == ObjectTool.Select && !key.Echo: Bulldoze(); return true;
            case Key.N when !key.Echo: CycleSnap(); return true;
            case Key.L when !key.Echo: Flatten = !Flatten; UiToolChanged(); Ed.Toast(Flatten ? "Flatten ground under buildings: on" : "Flatten ground: off"); return true;
            case Key.T when !key.Echo: CycleTeam(); return true;
        }
        return false;
    }

    private void CancelDrag()
    {
        foreach (var (e, before) in _dragFrom) if (Layer.Items.Contains(e)) Layer.SetPose(e, before.Position, before.Yaw);
        if (_duplicated) { foreach (var c in _copies) Layer.Remove(c); Selection.Clear(); _selDirty = true; }
        _drag = DragKind.None; _duplicated = false;
        UiBox(null, default);
    }

    // ---- status ---------------------------------------------------------------------------------------------------------------

    public string StatusText(NVec3? ground)
    {
        string g = ground is { } h ? $"ground {h.Y:0.0} m at {h.X:0}, {h.Z:0}" : "no ground";
        string issues = Issues.Count(i => i.Severity == Severity.Error) is > 0 and var ne ? $"    |    {ne} map error{(ne == 1 ? "" : "s")}" : Issues.Count(i => i.Severity == Severity.Warning) is > 0 and var nw ? $"    |    {nw} map warning{(nw == 1 ? "" : "s")}" : "";
        if (Tool == ObjectTool.Place)
        {
            if (Armed is null) return "BUILD    pick something from the palette below" + issues;
            string prob = _problem == PlaceProblem.None ? (_pose is { OnRoad: true } ? "snapped to the road" + (_deckNote.Length > 0 ? ", " + _deckNote : "") : "fits") : ProblemText(_problem).ToUpperInvariant();
            string yaw = $"{MathF.Round((_pose?.Yaw ?? _yaw) * 180 / MathF.PI):0} deg";
            string what = _pasting ? $"PASTE {_clip.Count}" : Armed.Marker switch
            {
                MarkerKind.Spawn => $"SPAWN {Layer.Spawns.Count() + 1}", MarkerKind.Regen => "REGEN", MarkerKind.Checkpoint => Finish ? "FINISH LINE" : $"CHECKPOINT {NextCheckNumber()}",
                MarkerKind.Flag => $"TEAM {Team} FLAG", _ => Armed.Name.ToUpperInvariant(),
            };
            return $"PLACE {what}    heading {yaw}    snap {SnapName}    {prob}    |    {g}    |    wheel . , turn    click place    Shift keeps going    Esc cancels" + issues;
        }
        string hover = _hover is { } hv ? $"    |    {hv.Name.ToUpperInvariant()} ({hv.Data.ObjectType:x})" : "";
        return $"SELECT    {Selection.Count} selected{hover}    |    {g}    |    click, Shift adds, drag a box, drag moves, Alt+drag copies, Del bulldozes, , . turn, Ctrl+C/V" + issues;
    }

    // ---- capture hooks (docs/dev-hooks.md) -------------------------------------------------------------------------------------

    /// <summary>I76_EDITOR_SCRIPT commands of the object tools; true if it was one.</summary>
    public bool Script(string cmd, string[] a)
    {
        float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
        EditObject? At(float x, float z) => Layer.Items.Where(e => !e.Locked).OrderBy(e => NVec2.DistanceSquared(new NVec2(e.Position.X, e.Position.Z), new NVec2(x, z))).FirstOrDefault();
        switch (cmd)
        {
            case "buildtool": SetTool(a.Length > 0 && a[0] == "select" ? ObjectTool.Select : ObjectTool.Place); if (Tool == ObjectTool.Place) { Disarm(); if (a.Length > 0 && Enum.TryParse<ObjectCategory>(a[0], true, out var cat)) _tab = cat; ShowTab(_tab); } return true;
            case "palette": SetTool(ObjectTool.Place); Disarm(); if (Enum.TryParse<ObjectCategory>(a[0], true, out var c2)) _tab = c2; ShowTab(_tab); if (a.Length > 1) { _search!.Text = a[1]; Refill(); } return true;
            case "arm":
                {
                    var it = Catalogue.FirstOrDefault(i => i.Name.Equals(a[0], StringComparison.OrdinalIgnoreCase));
                    if (it is null) { GD.PrintErr($"editor script: no item {a[0]}"); return true; }
                    if (a.Length > 1) Team = int.Parse(a[1]);
                    _tab = it.Category; Arm(it); ShowTab(_tab);
                    return true;
                }
            case "marker": ArmMarker(Enum.Parse<MarkerKind>(a[0], true)); if (a.Length > 1) { Team = int.Parse(a[1]); Arm(MarkerItem(Armed!.Marker, Team)); } return true;
            case "turn": _yaw = Snapping.Wrap(F(0) * MathF.PI / 180); return true;
            case "place": PlaceHere(a.Length > 0); return true;
            case "snap": Snap = Enum.Parse<SnapMode>(a[0], true); UiToolChanged(); return true;
            case "flatten": Flatten = a[0] == "1"; UiToolChanged(); return true;
            case "cut": CutRoads = a[0] == "1"; UiToolChanged(); return true;
            case "team": Team = int.Parse(a[0]); if (Armed?.Marker == MarkerKind.Flag) Arm(MarkerItem(MarkerKind.Flag, Team)); UiToolChanged(); return true;
            case "finish": Finish = a[0] == "1"; ClearGhost(); MakeGhost(); UiToolChanged(); return true;
            case "search": _search!.Text = a[0]; Refill(); return true;
            case "pickobj": SetTool(ObjectTool.Select); SelectOnly(At(F(0), F(1))); return true;
            case "addobj": if (At(F(0), F(1)) is { } ao && !Selection.Contains(ao)) { Selection.Add(ao); _selDirty = true; } return true;
            case "box":
                {
                    SetTool(ObjectTool.Select);
                    Selection.Clear();
                    float x0 = Math.Min(F(0), F(2)), x1 = Math.Max(F(0), F(2)), z0 = Math.Min(F(1), F(3)), z1 = Math.Max(F(1), F(3));
                    Selection.AddRange(Layer.Items.Where(e => !e.Locked && e.Position.X >= x0 && e.Position.X <= x1 && e.Position.Z >= z0 && e.Position.Z <= z1));
                    _selDirty = true;
                    return true;
                }
            case "moveobj": MoveBy(new NVec2(F(0), F(1)), a.Length > 2 && a[2] == "copy"); return true;
            case "rotobj": RotateSelection(F(0) * MathF.PI / 180); return true;
            case "del": Bulldoze(); return true;
            case "copy": Copy(); return true;
            case "paste": Paste(); return true;
            case "put":   // put:name,x,z,yawDeg[,roads]: places that item there, as a click on the ghost would (snapping when asked)
                {
                    var it = Catalogue.FirstOrDefault(i => i.Name.Equals(a[0], StringComparison.OrdinalIgnoreCase));
                    if (it is null) { GD.PrintErr($"editor script: no item {a[0]}"); return true; }
                    EnsureEnds(it);
                    var res = Snapping.Place(it, new NVec2(F(1), F(2)), F(3) * MathF.PI / 180, a.Length > 4 ? Enum.Parse<SnapMode>(a[4], true) : SnapMode.Off, Grid, Layer.Polylines);
                    if (it.Marker == MarkerKind.None) _problem = Check(it, res);
                    var e = PlaceItem(it, res);
                    SelectOnly(e);
                    return true;
                }
            case "spawn": { Team = a.Length > 2 ? int.Parse(a[2]) : 0; PlaceMarker(MarkerKind.Spawn, F(0), F(1), a.Length > 3 ? F(3) : 0); return true; }
            case "regen": PlaceMarker(MarkerKind.Regen, F(0), F(1), a.Length > 2 ? F(2) : 0); return true;
            case "flag": Team = int.Parse(a[0]); PlaceMarker(MarkerKind.Flag, F(1), F(2), a.Length > 3 ? F(3) : 0); return true;
            case "check": { Finish = a.Length > 2 && a[2] == "99"; PlaceMarker(MarkerKind.Checkpoint, F(0), F(1), 0); Finish = false; return true; }
            case "mdown": case "mmove": case "mup":
                {
                    // a scripted mouse: the ground point's place on the screen (x, z), with shift and alt as words
                    float x = F(0), z = F(1);
                    var g = new NVec3(x, Ground(x, z), z); var og = Origin();
                    var screen = Cam.UnprojectPosition(new Vector3(g.X - og.X, g.Y - og.Y, -(g.Z - og.Z)));
                    Ed.SetHookCursor(x, z);
                    _hit = new NVec3(x, Ground(x, z), z);
                    _scriptMouse = screen;
                    if (cmd == "mdown") { _scriptDown = true; SetTool(Tool == ObjectTool.None ? ObjectTool.Select : Tool); BeginDrag(screen, a.Contains("shift"), a.Contains("alt")); }
                    else if (cmd == "mup") { _scriptDown = false; EndDrag(screen); _scriptMouse = null; }
                    return true;
                }
            case "tabs": return true;
        }
        return false;
    }

    private void PlaceMarker(MarkerKind kind, float x, float z, float yawDeg)
    {
        var item = MarkerItem(kind, Team);
        var res = new PlacementSnap(new NVec2(x, z), yawDeg * MathF.PI / 180, false, null, null);
        var e = PlaceItem(item, res);
        SelectOnly(e);
    }

    /// <summary>Moves (or, copying, duplicates and moves) the selection by a ground offset as one undoable step: the drag without a mouse.</summary>
    public void MoveBy(NVec2 delta, bool copy)
    {
        if (Selection.Count == 0) return;
        _dragFrom = [.. Selection.Select(e => (e, new Pose(e.Position, e.Yaw)))];
        _duplicated = false;
        if (copy) Duplicate();
        foreach (var (e, before) in _dragFrom) Layer.SetPose(e, new NVec3(before.Position.X + delta.X, 0, before.Position.Z + delta.Y), before.Yaw);
        _drag = DragKind.Move;
        EndDrag(default);
    }
}
