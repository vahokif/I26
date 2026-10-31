using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Editor;
using I76.Game.Net;
using I76.Game.Editor.RoadTools;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Game.Editor;

/// <summary>The map editor's windows, in the modern HUD's style: top bar, tool palette, brush settings, status bar, the map menu, help.</summary>
public partial class MapEditor
{
    private CanvasLayer _layer = null!;
    private Control _root = null!;
    private float _s = 1;
    private Label _status = null!, _title = null!, _toast = null!, _undoLabel = null!, _radiusLabel = null!, _strengthLabel = null!;
    private Button _lookButton = null!;
    private readonly Dictionary<BrushKind, Button> _toolButtons = [];
    private readonly Dictionary<Falloff, Button> _falloffButtons = [];
    private HSlider _radius = null!, _strength = null!;
    private readonly List<Control> _heightOnly = [], _surfaceOnly = [];
    private readonly Dictionary<int, Button> _surfaceButtons = [];
    private Button _overlayButton = null!;
    private LineEdit _height = null!;
    private Control? _menu, _help, _dialog;
    private double _toastUntil;
    private bool _syncing;

    private static readonly (BrushKind Kind, string Key, string Name, string Tip)[] Tools =
    [
        (BrushKind.Raise, "1", "RAISE", "Lift the ground"),
        (BrushKind.Lower, "2", "LOWER", "Dig it down"),
        (BrushKind.Smooth, "3", "SMOOTH", "Even bumps out"),
        (BrushKind.Flatten, "4", "FLATTEN", "To the height under the first click"),
        (BrushKind.Level, "5", "LEVEL", "To a typed or picked height"),
        (BrushKind.Noise, "6", "ROUGHEN", "Add noise"),
        (BrushKind.Surface, "", "SURFACE", "Paint the ground's surface type: tyre grip and sounds, plants"),
    ];

    private void BuildUi()
    {
        _layer?.QueueFree();
        _modal = false; _menu = _help = _dialog = null;
        _s = Math.Clamp(GetViewport().GetVisibleRect().Size.Y / 1080f, 0.8f, 2.4f);
        float s = _s;
        _layer = new CanvasLayer { Layer = 30, Name = "EditorUi" };
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_root);
        AddChild(_layer);
        _toolButtons.Clear(); _falloffButtons.Clear(); _heightOnly.Clear(); _surfaceOnly.Clear(); _surfaceButtons.Clear();

        // ---- the top bar
        var top = new PanelContainer { Name = "TopBar" };
        top.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        var topStyle = EditorStyle.Flat(HudStyle.Panel, 0, s, HudStyle.Edge, 6);
        topStyle.ContentMarginLeft = topStyle.ContentMarginRight = 14 * s;
        top.AddThemeStyleboxOverride("panel", topStyle);
        _root.AddChild(top);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", (int)(10 * s));
        top.AddChild(bar);
        var logo = new Label { Text = "I26 MAP EDITOR" };
        EditorStyle.Title(logo, 24 * s);
        bar.AddChild(logo);
        bar.AddChild(new VSeparator());
        var menu = EditorStyle.Button("MAP", ToggleMenu, s, 20);
        bar.AddChild(menu);
        bar.AddChild(EditorStyle.Button("PLAYTEST  F5", Playtest, s, 20));
        _lookButton = EditorStyle.Button("LOOK  F2", () => { ToggleLook(); }, s, 20);
        bar.AddChild(_lookButton);
        bar.AddChild(EditorStyle.Button("STYLE", ToggleStyle, s, 20));
        bar.AddChild(EditorStyle.Button("HELP  F1", ToggleHelp, s, 20));
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _title = new Label();
        EditorStyle.Body(_title, 18 * s);
        bar.AddChild(_title);
        top.MouseFilter = Control.MouseFilterEnum.Stop;

        // ---- the tool palette, left
        var palette = new PanelContainer { Name = "Tools" };
        palette.AnchorLeft = 0; palette.AnchorRight = 0; palette.AnchorTop = 0; palette.AnchorBottom = 0;
        palette.OffsetLeft = 14 * s; palette.OffsetTop = 70 * s;
        palette.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        _root.AddChild(palette);
        var tools = new VBoxContainer();
        tools.AddThemeConstantOverride("separation", (int)(6 * s));
        palette.AddChild(tools);
        var tl = new Label { Text = "TERRAIN" };
        EditorStyle.Title(tl, 18 * s);
        tools.AddChild(tl);
        var group = new ButtonGroup();
        foreach (var (kind, key, name, tip) in Tools)
        {
            var b = EditorStyle.Button(key.Length == 0 ? name : $"{key}   {name}", () => SetTool(kind), s, 22, toggle: true);
            b.ButtonGroup = group; b.TooltipText = tip;
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(190 * s, 38 * s);
            // the tool's colour as its brush draws
            b.AddThemeColorOverride("icon_normal_color", ToolColour(kind));
            tools.AddChild(b);
            _toolButtons[kind] = b;
        }
        RoadsTool.BuildPalette(tools, s);
        Objects.BuildUi(_root, tools, group, s);
        tools.AddChild(new HSeparator());
        var undoRow = new HBoxContainer();
        undoRow.AddThemeConstantOverride("separation", (int)(6 * s));
        undoRow.AddChild(EditorStyle.Button("UNDO", DoUndo, s, 20));
        undoRow.AddChild(EditorStyle.Button("REDO", DoRedo, s, 20));
        tools.AddChild(undoRow);
        _undoLabel = new Label { ClipText = true, CustomMinimumSize = new Vector2(190 * s, 0) };
        EditorStyle.Body(_undoLabel, 14 * s, dim: true);
        tools.AddChild(_undoLabel);

        // ---- the brush settings, right
        var side = new PanelContainer { Name = "Brush" };
        side.AnchorLeft = 1; side.AnchorRight = 1; side.AnchorTop = 0; side.AnchorBottom = 0;
        side.OffsetRight = -14 * s; side.OffsetTop = 70 * s; side.GrowHorizontal = Control.GrowDirection.Begin;
        side.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        _root.AddChild(side);
        Objects.SetBrushPanel(side);
        var bx = new VBoxContainer { CustomMinimumSize = new Vector2(260 * s, 0) };
        bx.AddThemeConstantOverride("separation", (int)(6 * s));
        side.AddChild(bx);
        var bl = new Label { Text = "BRUSH" };
        EditorStyle.Title(bl, 18 * s);
        bx.AddChild(bl);
        _radiusLabel = new Label(); EditorStyle.Body(_radiusLabel, 16 * s);
        bx.AddChild(_radiusLabel);
        _radius = new HSlider { MinValue = BrushSettings.MinRadius, MaxValue = BrushSettings.MaxRadius, Step = 1 };
        EditorStyle.Slider(_radius, s);
        _radius.ValueChanged += v => { if (!_syncing) { Brush.Radius = (float)v; SyncUi(); } };
        bx.AddChild(_radius);
        _strengthLabel = new Label(); EditorStyle.Body(_strengthLabel, 16 * s);
        bx.AddChild(_strengthLabel);
        _heightOnly.Add(_strengthLabel);
        _strength = new HSlider { MinValue = BrushSettings.MinStrength * 100, MaxValue = BrushSettings.MaxStrength * 100, Step = 1 };
        EditorStyle.Slider(_strength, s);
        _strength.ValueChanged += v => { if (!_syncing) { Brush.Strength = (float)v / 100f; SyncUi(); } };
        bx.AddChild(_strength);
        _heightOnly.Add(_strength);
        int firstHeightOnly = bx.GetChildCount();
        var fl = new Label { Text = "FALLOFF   (G)" };
        EditorStyle.Body(fl, 16 * s);
        bx.AddChild(fl);
        var fg = new ButtonGroup();
        var fgrid = new GridContainer { Columns = 2 };
        fgrid.AddThemeConstantOverride("h_separation", (int)(6 * s)); fgrid.AddThemeConstantOverride("v_separation", (int)(6 * s));
        foreach (var f in Enum.GetValues<Falloff>())
        {
            var fb = EditorStyle.Button(f.ToString().ToUpperInvariant(), () => { Brush.Falloff = f; SyncUi(); }, s, 18, toggle: true);
            fb.ButtonGroup = fg; fb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            fgrid.AddChild(fb);
            _falloffButtons[f] = fb;
        }
        bx.AddChild(fgrid);
        bx.AddChild(new HSeparator());
        var hl = new Label { Text = "LEVEL HEIGHT (m)" };
        EditorStyle.Body(hl, 16 * s);
        bx.AddChild(hl);
        var hrow = new HBoxContainer();
        hrow.AddThemeConstantOverride("separation", (int)(6 * s));
        _height = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Right, SelectAllOnFocus = true };
        _height.AddThemeFontOverride("font", HudStyle.Bold);
        _height.AddThemeFontSizeOverride("font_size", (int)(18 * s));
        _height.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _height.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 6));
        _height.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 6));
        _height.TextSubmitted += t => { ApplyHeightText(t); GetViewport().GuiReleaseFocus(); };
        _height.FocusExited += () => ApplyHeightText(_height.Text);
        hrow.AddChild(_height);
        hrow.AddChild(EditorStyle.Button("PICK  I", Eyedrop, s, 18));
        bx.AddChild(hrow);
        var hint = new Label { Text = "Alt+click picks the height under the cursor", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(hint, 13 * s, dim: true);
        bx.AddChild(hint);
        for (int i = firstHeightOnly; i < bx.GetChildCount(); i++) _heightOnly.Add((Control)bx.GetChild(i));

        // the surface tool's list: the ground's classes, each with its colour in the overlay
        var sl = new Label { Text = "SURFACE TYPE" };
        EditorStyle.Body(sl, 16 * s);
        bx.AddChild(sl); _surfaceOnly.Add(sl);
        var sg = new ButtonGroup();
        foreach (var info in SurfaceTypes.All)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", (int)(6 * s));
            var sw = new ColorRect { Color = new Color(info.R / 255f, info.G / 255f, info.B / 255f), CustomMinimumSize = new Vector2(18 * s, 18 * s), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddChild(sw);
            var id = info.Id;
            var sb2 = EditorStyle.Button(info.Name, () => { Brush.SurfaceId = id; SyncUi(); }, s, 18, toggle: true);
            sb2.ButtonGroup = sg; sb2.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; sb2.TooltipText = info.Tip;
            sb2.Alignment = HorizontalAlignment.Left;
            row.AddChild(sb2);
            bx.AddChild(row); _surfaceOnly.Add(row);
            _surfaceButtons[id] = sb2;
        }
        _overlayButton = EditorStyle.Button("SURFACE MAP  ;", () => { _overlayOn = !_overlayOn; SyncUi(); }, s, 18, toggle: true);
        bx.AddChild(_overlayButton); _surfaceOnly.Add(_overlayButton);
        var sh = new Label { Text = "Hard-edged brush; heights are kept. Alt+click or I picks the type under the cursor", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(sh, 13 * s, dim: true);
        bx.AddChild(sh); _surfaceOnly.Add(sh);

        RoadsTool.BuildPanel(_root, s, side);

        // ---- the status bar
        var sb = new PanelContainer { Name = "Status" };
        sb.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        sb.GrowVertical = Control.GrowDirection.Begin;
        var sbStyle = EditorStyle.Flat(HudStyle.Panel, 0, s, HudStyle.Edge, 6);
        sbStyle.ContentMarginLeft = sbStyle.ContentMarginRight = 14 * s;
        sb.AddThemeStyleboxOverride("panel", sbStyle);
        _root.AddChild(sb);
        _status = new Label();
        EditorStyle.Body(_status, 17 * s);
        sb.AddChild(_status);

        // ---- the map style window (open across a rebuild of the map or the window)
        if (EditorSession.StyleTab >= 0) OpenStyle(EditorSession.StyleTab);

        // ---- the message line
        _toast = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
        EditorStyle.Body(_toast, 20 * s, colour: HudStyle.Amber);
        _toast.AddThemeColorOverride("font_outline_color", HudStyle.Ink);
        _toast.AddThemeConstantOverride("outline_size", (int)(5 * s));
        _toast.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toast.OffsetTop = -110 * s; _toast.OffsetBottom = -80 * s; _toast.OffsetLeft = -600 * s; _toast.OffsetRight = 600 * s;
        _root.AddChild(_toast);
    }

    /// <summary>The road tool changed mode: the widgets follow.</summary>
    public void RoadToolChanged() { if (RoadsTool.Active) Objects.Deactivate(); SyncUi(); }

    /// <summary>An object tool came on or off: the road tool gives way, the widgets follow.</summary>
    public void ObjectToolChanged() { if (Objects.Active) RoadsTool.Deactivate(); SyncUi(); }

    private MapLookPanel? _style;

    private void ToggleStyle()
    {
        if (_style is not null && IsInstanceValid(_style)) { CloseStyle(); return; }
        OpenStyle(Math.Max(0, EditorSession.StyleTab));
    }

    private void CloseStyle()
    {
        _style?.QueueFree();
        _style = null;
        EditorSession.StyleTab = -1;
    }

    private void OpenStyle(int tab)
    {
        if (Doc.Look is null) { Toast("This map has no style to edit", true); return; }
        _style?.QueueFree();
        EditorSession.StyleTab = tab;
        _style = new MapLookPanel { Doc = Doc, Builder = Builder, S = _s, Tab = tab, Apply = ApplyLook, TabChanged = t => EditorSession.StyleTab = t };
        _root.AddChild(_style);
    }

    private void ApplyHeightText(string text)
    {
        if (float.TryParse(text.Trim().TrimEnd('m', ' '), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            Brush.TargetHeight = Math.Clamp(v, 0f, (float)Units.MaxMetres);
        SyncUi();
    }

    /// <summary>The widgets show the brush as it is (after a key, a slider, an eyedrop).</summary>
    private void SyncUi()
    {
        if (_radius is null || !IsInstanceValid(_radius)) return;
        _syncing = true;
        _radius.SetValueNoSignal(Brush.Radius);
        _strength.SetValueNoSignal(Brush.Strength * 100);
        _radiusLabel.Text = $"RADIUS   {Brush.Radius:0} m     [ ]";
        _strengthLabel.Text = $"STRENGTH   {Brush.Strength * 100:0}%     Shift+[ ]";
        foreach (var (k, b) in _toolButtons) b.SetPressedNoSignal(k == Brush.Kind && !RoadsTool.Active && !Objects.Active);
        RoadsTool.SyncPanel();
        Objects.KeepBrushPanelAway();
        foreach (var (f, b) in _falloffButtons) b.SetPressedNoSignal(f == Brush.Falloff);
        bool surface = Brush.Kind == BrushKind.Surface;
        foreach (var c in _heightOnly) c.Visible = !surface;
        foreach (var c in _surfaceOnly) c.Visible = surface;
        foreach (var (id, b) in _surfaceButtons) b.SetPressedNoSignal(id == Brush.SurfaceId);
        _overlayButton.SetPressedNoSignal(_overlayOn);
        if (!_height.HasFocus()) _height.Text = Brush.TargetHeight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        _syncing = false;
    }

    public void Toast(string text, bool error = false)
    {
        if (_toast is null || !IsInstanceValid(_toast)) return;
        _toast.Text = text;
        _toast.Visible = true;
        _toast.AddThemeColorOverride("font_color", error ? HudStyle.Danger : HudStyle.Amber);
        _toastUntil = Time.GetTicksMsec() / 1000.0 + 2.6;
    }

    private int _statusFrame;

    private void UpdateStatus()
    {
        if (_status is null || !IsInstanceValid(_status)) return;
        if (_toast.Visible && Time.GetTicksMsec() / 1000.0 > _toastUntil) _toast.Visible = false;
        if (++_statusFrame % 3 != 0) return;
        string ground = _hit is { } h ? $"{h.Y:0.0} m   at {h.X:0}, {h.Z:0}" : "no ground";
        if (RoadsTool.Active)
            _status.Text = $"ROAD {RoadsTool.Mode.ToString().ToUpperInvariant()}    {RoadsTool.Hint}    |    ground {ground}    |    {Engine.GetFramesPerSecond()} fps";
        else
        {
        var tool = Tools.First(t => t.Kind == Brush.Kind).Name;
        _status.Text = Objects.Active ? Objects.StatusText(_hit) + $"    |    {Engine.GetFramesPerSecond()} fps" : Brush.Kind == BrushKind.Surface ? $"SURFACE {SurfaceTypes.All[Brush.SurfaceId & 7].Name}    radius {Brush.Radius:0} m    |    ground {ground}{UnderCursorSurface()}    |    {Engine.GetFramesPerSecond()} fps" : $"{tool}    radius {Brush.Radius:0} m    strength {Brush.Strength * 100:0}%    {Brush.Falloff.ToString().ToLowerInvariant()}    |    ground {ground}"
            + $"    |    {Engine.GetFramesPerSecond()} fps";
        }
        _title.Text = $"{TitleLabel()}{(Doc.Modified ? " *" : "")}";
        _undoLabel.Text = Undo.CanUndo ? $"{Undo.Count} edits   next undo: {Undo.NextUndo}" : "nothing to undo";
        _lookButton.Text = Is1997() ? "LOOK: 1997   F2" : "LOOK: MODERN   F2";
    }

    /// <summary>"   surface DIRT" for the sample under the cursor.</summary>
    private string UnderCursorSurface()
    {
        if (_hit is not { } h || !Doc.Locate((int)Math.Floor(h.X * MissionWorld.SampleScale + 0.5), (int)Math.Floor(h.Z * MissionWorld.SampleScale + 0.5), out int t, out int o)) return "";
        return $"   under it {SurfaceTypes.All[SurfaceTypes.Of(Doc.GetSample(t, o))].Name}";
    }

    private List<MapListing>? _stock;

    /// <summary>A stock map by the picker's label ("Jorczak's Peak (M14)"), a map of yours as "Display (stem)", never "T01 (T01)".</summary>
    private string TitleLabel()
    {
        _stock ??= Install is null ? [] : MissionTitles.Stock(Install);
        if (_stock.FirstOrDefault(m => m.Stem.Equals(Doc.BaseMission, StringComparison.OrdinalIgnoreCase) && m.Stem.Equals(Doc.Name, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit.Label;
        return Doc.Name.Equals(Doc.BaseMission, StringComparison.OrdinalIgnoreCase) ? Doc.Name : $"{Doc.Name}  ({Doc.BaseMission.ToUpperInvariant()})";
    }

    // ---- the map menu, help, dialogs ----------------------------------------------------------------------------------------

    private void CloseModal()
    {
        _menu?.QueueFree(); _help?.QueueFree(); _dialog?.QueueFree(); _picker?.QueueFree();
        _menu = _help = _dialog = _picker = null;
        _modal = false;
    }

    private void ToggleMenu()
    {
        if (_menu is not null) { CloseModal(); return; }
        CloseModal();
        float s = _s;
        var panel = new PanelContainer { Name = "MapMenu" };
        panel.OffsetLeft = 250 * s; panel.OffsetTop = 52 * s;
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(250 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        panel.AddChild(box);
        void Item(string text, Action a) { var b = EditorStyle.Button(text, () => { CloseModal(); a(); }, s, 20); b.Alignment = HorizontalAlignment.Left; box.AddChild(b); }
        Item("New flat map...", NewFlatMap);
        Item("New map from real world...", NewGeoMap);
        Item("Open...", OpenPicker);
        Item("Save   Ctrl+S", Save);
        Item("Save as...", SaveAs);
        Item("Playtest   F5", Playtest);
        Item("Exit editor", () => Confirm(Doc.Modified ? "Leave the editor? Edits not saved are lost." : "Leave the editor?", Exit));
        _root.AddChild(panel);
        _menu = panel;
        _modal = true;
    }

    private Control? _picker;

    private void OpenPicker()
    {
        CloseModal();
        if (Install is null) { Toast("No install to list maps from", true); return; }
        _picker = new EditorPicker
        {
            Install = Install,
            Picked = mission =>
            {
                void Go() => OpenMap(mission);
                if (Doc.Modified) Confirm("Open another map? Edits not saved are lost.", Go);
                else Go();
            },
            Cancelled = CloseModal,
        };
        _root.AddChild(_picker);
        _modal = true;
    }

    /// <summary>A form over the editor (modal).</summary>
    private void ShowDialog(Control dialog)
    {
        CloseModal();
        _root.AddChild(dialog);
        _dialog = dialog;
        _modal = true;
    }

    private void Confirm(string text, Action yes)
    {
        CloseModal();
        float s = _s;
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        holder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        var panel = new PanelContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both };
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(460 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(14 * s));
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        EditorStyle.Body(l, 20 * s);
        box.AddChild(l);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", (int)(12 * s));
        row.AddChild(EditorStyle.Button("YES", () => { CloseModal(); yes(); }, s, 22));
        row.AddChild(EditorStyle.Button("CANCEL", CloseModal, s, 22));
        box.AddChild(row);
        panel.AddChild(box);
        holder.AddChild(panel);
        _root.AddChild(holder);
        _dialog = holder;
        _modal = true;
    }

    private static readonly (string Keys, string What)[] HelpRows =
    [
        ("LEFT BUTTON", "Paint with the brush"),
        ("1 2 3 4 5 6", "Raise, lower, smooth, flatten, level, roughen"),
        ("SURFACE (palette)", "Paint the ground's surface type (grip, tyre sounds, plants) with a hard-edged brush ([ ] sizes it); Alt+click or I picks the type under the cursor; ; shows or hides the surface map"),
        ("[  ]", "Brush radius down, up (also Shift + wheel)"),
        ("Shift+[  Shift+]", "Brush strength down, up (also Ctrl + wheel)"),
        ("G", "Next falloff shape"),
        ("Alt + click, I", "Pick the ground's height for the Level brush"),
        ("7  8  9  0", "Road tool: straight, curve (8 again: cubic), freeform, select and edit"),
        ("U  X", "Road tool: upgrade a road's type, bulldoze a road"),
        ("Shift + click", "Roads: chain the next piece from this click. Enter finishes, Backspace takes a click back, Esc cancels"),
        ("V  H  C  O", "Roads: angle snap, grid snap, chain, cut and fill the ground"),
        ("Shift+Tab", "Roads: build on the ground, a bridge or a tunnel (the BUILD ON buttons). Bridge and tunnel lay one straight stretch of whole pieces: click the start, click toward the end"),
        ("Roads, select", "Drag a point (Alt: that point alone), double-click or Insert adds one, Delete removes a point or the road, K splits, T changes type"),
        ("M   P", "Object tools: select; build (the palette: buildings, props, plants, bridges, ramps)"),
        ("J  R  Y  Z", "Object tools: spawn point, regen pad, CTF flag, race checkpoint"),
        ("Wheel  ,  .", "Objects: turn what is being placed (15 degrees; Shift: 1); with a selection , . turn it; Ctrl+wheel zooms"),
        ("Click", "Objects: place (Shift keeps placing); select; Shift adds; drag a box on empty ground"),
        ("Drag, Alt+drag", "Objects: move the selection; copy it"),
        ("Del  Ctrl+C  Ctrl+V", "Objects: bulldoze; copy; paste"),
        ("N   L   T", "Objects: snap (roads, grid, off; Alt: off while held); level the ground under a building; team or finish line"),
        ("Ctrl+Z", "Undo a stroke, a road or an object edit"),
        ("Ctrl+Y, Ctrl+Shift+Z", "Redo"),
        ("Ctrl+S", "Save (a stock map asks for a new name: it is copied to addon)"),
        ("W A S D, arrows", "Slide over the ground (Shift: faster)"),
        ("Q  E", "Turn the view"),
        ("RIGHT BUTTON drag", "Orbit"),
        ("MIDDLE BUTTON drag", "Drag the ground"),
        ("WHEEL, Page Up/Down", "Zoom toward the cursor"),
        ("F", "Bring the point under the cursor to the middle"),
        ("Tab", "Pan by moving the mouse to the window's edge, on or off"),
        ("B", "Hide or show the brush circle"),
        ("F2", "Modern look or 1997 look"),
        ("F5", "Playtest: drop a car where the camera looks. F5 or Esc comes back"),
        ("Esc", "Map menu"),
        ("F1", "This help"),
    ];

    private void ToggleHelp()
    {
        if (_help is not null) { CloseModal(); return; }
        CloseModal();
        float s = _s;
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        holder.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        var panel = new PanelContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both };
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(860 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(5 * s));
        var t = new Label { Text = "MAP EDITOR KEYS" };
        EditorStyle.Title(t, 30 * s);
        box.AddChild(t);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", (int)(26 * s));
        grid.AddThemeConstantOverride("v_separation", (int)(4 * s));
        foreach (var (k, w) in HelpRows)
        {
            var kl = new Label { Text = k, CustomMinimumSize = new Vector2(290 * s, 0) };
            EditorStyle.Body(kl, 17 * s, colour: HudStyle.Amber);
            var wl = new Label { Text = w };
            EditorStyle.Body(wl, 17 * s);
            grid.AddChild(kl); grid.AddChild(wl);
        }
        box.AddChild(grid);
        var foot = new Label { Text = "Press F1 or Esc to close." };
        EditorStyle.Body(foot, 15 * s, dim: true);
        box.AddChild(foot);
        panel.AddChild(box);
        holder.AddChild(panel);
        _root.AddChild(holder);
        _help = holder;
        _modal = true;
    }
}
