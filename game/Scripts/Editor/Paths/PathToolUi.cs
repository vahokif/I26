using Godot;
using I76.Editor.Paths;
using I76.Formats.Mission.Edit;
using LineEdit = Godot.LineEdit;
using I76.Game.Net;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.PathTools;

public sealed partial class PathTool
{
    private readonly Dictionary<PathMode, Button> _modeButtons = [];
    private Button? _layerButton, _panelLayer, _delPoint, _delPath;
    private PanelContainer? _panel;
    private Control? _brushPanel;
    private LineEdit? _label;
    private Label? _labelNote, _info, _script, _hintL, _listTitle, _note;
    private ItemList? _list;
    private List<PathGroup> _listed = [];
    private bool _panelDirty = true, _syncing;

    private static readonly (PathMode Mode, string Key, string Name, string Tip)[] Modes =
    [
        (PathMode.Edit, "-", "EDIT PATHS", "Select a path, drag its points; a double click on its line inserts a point, Delete removes one"),
        (PathMode.New, "=", "NEW PATH", "Click the points of a new path in the order it is driven; Enter or a right click finishes"),
        (PathMode.Insert, "", "INSERT POINT", "Click on a path's line to put a point there"),
        (PathMode.Delete, "", "DELETE", "Click a point to remove it, or a line to delete its path"),
    ];

    /// <summary>The PATHS group of the tool column.</summary>
    public void BuildPalette(VBoxContainer tools, float s)
    {
        _modeButtons.Clear();
        tools.AddChild(new HSeparator());
        var title = new Label { Text = "PATHS" };
        EditorStyle.Title(title, 18 * s);
        tools.AddChild(title);
        var group = new ButtonGroup { AllowUnpress = true };
        foreach (var (mode, key, name, tip) in Modes)
        {
            var m = mode;
            var b = EditorStyle.Button(key.Length == 0 ? "    " + name : $"{key}   {name}", () => SetMode(m), s, 20, toggle: true);
            b.ButtonGroup = group; b.TooltipText = tip;
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(EditorStyle.ToolWidth * s, 30 * s);
            tools.AddChild(b);
            _modeButtons[mode] = b;
        }
        _layerButton = EditorStyle.Button("    SHOW PATHS", ToggleLayer, s, 20, toggle: true);
        _layerButton.TooltipText = "Show the paths over the ground while another tool is in use";
        _layerButton.Alignment = HorizontalAlignment.Left;
        _layerButton.CustomMinimumSize = new Vector2(EditorStyle.ToolWidth * s, 30 * s);
        tools.AddChild(_layerButton);
    }

    private void ToggleLayer()
    {
        ShowLayer = !ShowLayer;
        Ed.Toast(ShowLayer ? "Paths shown with every tool" : "Paths shown with the path tool only");
        SyncPanel();
    }

    /// <summary>The path panel at the right, in place of the brush's while the path tool is on.</summary>
    public void BuildPanel(Control root, float s, Control brushPanel)
    {
        _brushPanel = brushPanel;
        _panel = new PanelContainer { Name = "PathPanel", Visible = false };
        _panel.AnchorLeft = 1; _panel.AnchorRight = 1; _panel.AnchorTop = 0; _panel.AnchorBottom = 0;
        _panel.OffsetRight = -14 * s; _panel.OffsetTop = 70 * s; _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        root.AddChild(_panel);
        var bx = new VBoxContainer { CustomMinimumSize = new Vector2(290 * s, 0) };
        bx.AddThemeConstantOverride("separation", (int)(6 * s));
        _panel.AddChild(EditorStyle.Scrolling(bx, s, 70, 60));
        var t = new Label { Text = "PATH" };
        EditorStyle.Title(t, 18 * s);
        bx.AddChild(t);

        var ll = new Label { Text = "PATH LABEL" };
        EditorStyle.Body(ll, 15 * s);
        bx.AddChild(ll);
        _label = new LineEdit { MaxLength = FsmPaths.MaxLabel, SelectAllOnFocus = true, PlaceholderText = "select a path", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _label.AddThemeFontOverride("font", HudStyle.Bold);
        _label.AddThemeFontSizeOverride("font_size", (int)(18 * s));
        _label.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _label.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 6));
        _label.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 6));
        _label.TextSubmitted += text => { ApplyLabel(text); GetViewport().GuiReleaseFocus(); };
        _label.FocusExited += () => ApplyLabel(_label.Text);
        _label.TextChanged += text => { if (Selected is { } g) Note(text.Trim() == g.Label ? null : Store.LabelProblem(text.Trim(), g)); };
        bx.AddChild(_label);
        _labelNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        EditorStyle.Body(_labelNote, 14 * s, colour: HudStyle.Danger);
        bx.AddChild(_labelNote);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_info, 15 * s);
        bx.AddChild(_info);
        _script = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_script, 14 * s, dim: true);
        bx.AddChild(_script);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(6 * s));
        _delPoint = EditorStyle.Button("DELETE POINT", () => { if (Selected is { } g && _selPt >= 0) DeletePoint(g, _selPt); }, s, 17);
        _delPoint.TooltipText = "Delete or Backspace: remove the selected point";
        _delPoint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _delPath = EditorStyle.Button("DELETE PATH", () => { if (Selected is { } g) AskDelete(g); }, s, 17);
        _delPath.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(_delPoint); row.AddChild(_delPath);
        bx.AddChild(row);
        bx.AddChild(new HSeparator());

        _listTitle = new Label();
        EditorStyle.Body(_listTitle, 15 * s);
        bx.AddChild(_listTitle);
        _list = new ItemList { CustomMinimumSize = new Vector2(0, 200 * s), AllowReselect = true, SelectMode = ItemList.SelectModeEnum.Single, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _list.AddThemeFontOverride("font", HudStyle.Bold);
        _list.AddThemeFontSizeOverride("font_size", (int)(15 * s));
        _list.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _list.AddThemeColorOverride("font_selected_color", HudStyle.Ink);
        _list.AddThemeColorOverride("font_hovered_color", HudStyle.Amber);
        _list.AddThemeStyleboxOverride("panel", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.6f), 5, s, null, 4));
        _list.AddThemeStyleboxOverride("selected", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _list.AddThemeStyleboxOverride("selected_focus", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _list.AddThemeStyleboxOverride("hovered", EditorStyle.Flat(new Color(0.34f, 0.18f, 0.09f, 0.9f), 4, s));
        _list.ItemClicked += (i, _, _) => { if (i >= 0 && i < _listed.Count) PickFromList(_listed[(int)i]); };
        bx.AddChild(_list);
        _panelLayer = EditorStyle.Button("SHOW PATHS WITH EVERY TOOL", ToggleLayer, s, 17, toggle: true);
        _panelLayer.Alignment = HorizontalAlignment.Left;
        bx.AddChild(_panelLayer);
        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_note, 13 * s, dim: true);
        bx.AddChild(_note);
        _hintL = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_hintL, 14 * s, colour: HudStyle.Amber);
        bx.AddChild(_hintL);
        _panelDirty = true;
        SyncPanel();
    }

    private void Note(string? problem)
    {
        if (_labelNote is null || !IsInstanceValid(_labelNote)) return;
        _labelNote.Visible = problem is not null;
        _labelNote.Text = problem ?? "";
    }

    private void ApplyLabel(string text)
    {
        if (_syncing) return;
        Note(Rename(text));
        if (_labelNote is { Visible: false } && Selected is { } g && _label is not null) _label.Text = g.Label;
    }

    private void PickFromList(PathGroup g)
    {
        if (!Active) SetMode(PathMode.Edit);
        Select(g.First, -1);
        if (g.Points.Count > 0)
        {
            var p = g.Points[0];
            Ed.Cam.FocusOn(new System.Numerics.Vector3(p.X, Ed.GroundAt(p.X, p.Z), p.Z));
        }
    }

    public void SyncPanel()
    {
        foreach (var (m, b) in _modeButtons) if (IsInstanceValid(b)) b.SetPressedNoSignal(Active && m == Mode);
        if (_layerButton is not null && IsInstanceValid(_layerButton)) _layerButton.SetPressedNoSignal(ShowLayer);
        if (_panel is null || !IsInstanceValid(_panel)) return;
        _panel.Visible = Active;
        if (Active && _brushPanel is not null && IsInstanceValid(_brushPanel)) _brushPanel.Visible = false;
        _panelLayer?.SetPressedNoSignal(ShowLayer);
        _panelDirty = true;
    }

    /// <summary>The panel's contents follow the selection and the table (a few times a second at most).</summary>
    private void UpdatePanel()
    {
        if (!Active || _panel is null || !IsInstanceValid(_panel)) return;
        if (_hintL is not null) _hintL.Text = Hint;
        if (!_panelDirty) return;
        _panelDirty = false;
        _syncing = true;
        var groups = Store.Groups();
        var g = Selected;
        if (_label is not null && !_label.HasFocus()) { _label.Text = g?.Label ?? ""; _label.Editable = g is not null; }
        if (g is null) Note(null);
        _info!.Text = g is null ? "Click a path, or pick it from the list" : $"{g.Points.Count} point{(g.Points.Count == 1 ? "" : "s")}, {PathEdits.Length(g.Points):0} m" + (g.Entries.Count > 1 ? $"; the script's table holds it {g.Entries.Count} times (one copy for each place that names it), all edited together" : "");
        _script!.Text = g is null ? "" : ScriptText(g);
        _delPoint!.Disabled = g is null || _selPt < 0 || g.Points.Count <= 1;
        _delPath!.Disabled = g is null || Store.WhyNotDelete(g) is not null;
        _delPath.TooltipText = g is not null && Store.WhyNotDelete(g) is { } why ? why : "Delete the path (asks first)";
        _listTitle!.Text = $"PATHS ON THIS MAP   ({groups.Count})";
        _listed = groups;
        _list!.Clear();
        foreach (var p in groups)
        {
            bool named = Store.UsesOf(p).Any();
            int i = _list.AddItem($"{p.Label}   {p.Points.Count} pt{(p.Points.Count == 1 ? "" : "s")}{(named ? "   *" : "")}");
            _list.SetItemTooltip(i, named ? "named by the map's script" : p.First >= Store.Fixed ? "added in the editor" : "the script's");
            if (p.First == _sel) { _list.Select(i); _list.EnsureCurrentIsVisible(); }
            else _list.SetItemCustomFgColor(i, PathVisuals.Colour(p.First, false).Lightened(0.3f));   // the selected row keeps the list's dark text on amber
        }
        _note!.Text = Store.HasScript
            ? "* named by the mission's script. It names paths by their place in its table: its own can be reshaped and renamed, not deleted. A new path is saved with the map; the script cannot order a car along it."
            : "This map has no script: its paths are saved with it for a script to use.";
        _syncing = false;
    }

    private string ScriptText(PathGroup g)
    {
        if (!Store.HasScript) return "No script on this map names it.";
        var uses = Store.UsesOf(g).GroupBy(u => u.Op).Select(x => $"{x.Key} ({x.Count()})").ToList();
        if (uses.Count > 0) return "The script names it: " + string.Join(", ", uses);
        return g.First >= Store.Fixed ? "Added in the editor: the script does not name it." : "Not found in the script's orders (it may pick it while it runs).";
    }

    // ---- capture hooks (docs/dev-hooks.md) -------------------------------------------------------------------------------------

    private static float F(string[] a, int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// I76_EDITOR_SCRIPT commands "path:..." for captures and tests without a mouse: mode,edit|new|insert|delete | off | layer,0|1 |
    /// cursor,x,z (or off) | click,x,z[,double] | finish | select,x,z | drag,x,z,toX,toZ | insert,x,z | delete,x,z (a point there) |
    /// deletepath,x,z (asks, as the tool does) | label,NAME (the selected path's) | log.
    /// </summary>
    public void Script(string line)
    {
        int comma = line.IndexOf(',');
        string cmd = comma < 0 ? line : line[..comma];
        var args = comma < 0 ? [] : line[(comma + 1)..].Split(',');
        switch (cmd)
        {
            case "mode": SetMode(Enum.Parse<PathMode>(args[0], true)); break;
            case "off": Deactivate(); break;
            case "layer": ShowLayer = args.Length == 0 || args[0] != "0"; SyncPanel(); break;
            case "cursor": _virtual = args[0] == "off" ? null : new V2(F(args, 0), F(args, 1)); break;
            case "click":
                _virtual = new V2(F(args, 0), F(args, 1));
                Press(args.Length > 2 && args[2] == "double");
                Release();
                break;
            case "finish": Finish(); break;
            case "select":
                _virtual = new V2(F(args, 0), F(args, 1));
                if (PickAt(_virtual.Value) is { } h) Select(h.Path.First, h.Point); else Select(-1, -1);
                break;
            case "drag":
                {
                    var from = new V2(F(args, 0), F(args, 1));
                    if (PickAt(from) is not { Point: >= 0 } dh0) { GD.Print("editor paths: no point to drag there"); break; }
                    Select(dh0.Path.First, dh0.Point);
                    Do(PathEdits.MovePoint(Store, dh0.Path, dh0.Point, new V2(F(args, 2), F(args, 3))));
                    _virtual = new V2(F(args, 2), F(args, 3));
                    break;
                }
            case "insert":
                _virtual = new V2(F(args, 0), F(args, 1));
                if (PickAt(_virtual.Value) is { Segment: >= 0 } ins) InsertAt(ins.Path, ins.Segment, ins.OnLine, drag: false);
                else GD.Print("editor paths: no line to insert on there");
                break;
            case "delete":
                if (PickAt(new V2(F(args, 0), F(args, 1))) is { Point: >= 0 } dp) { Select(dp.Path.First, dp.Point); DeletePoint(dp.Path, dp.Point); }
                break;
            case "deletepath":
                if (PickAt(new V2(F(args, 0), F(args, 1))) is { } dh) { Select(dh.Path.First, -1); AskDelete(dh.Path); }
                break;
            case "label": if (Rename(string.Join(",", args)) is { } problem) GD.Print($"editor paths: label refused: {problem}"); break;
            case "log":
                foreach (var g in Store.Groups())
                    GD.Print($"editor paths: {g.First} {g.Label} x{g.Entries.Count}: {string.Join(" ", g.Points.Select(p => $"({p.X:0.#},{p.Z:0.#})"))}");
                GD.Print($"editor paths: table {Store.Count}, version {Store.Version}, override {(Ed.Builder.World!.Mission.FsmOverride is { } o ? o.Length + " bytes" : "none")}");
                break;
            default: GD.PrintErr($"editor script: path: unknown command {cmd}"); break;
        }
        _panelDirty = true;
    }
}
