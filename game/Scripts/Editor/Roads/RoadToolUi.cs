using Godot;
using I76.Editor.Roads;
using I76.Game.Net;

namespace I76.Game.Editor.RoadTools;

public sealed partial class RoadTool
{
    private readonly Dictionary<RoadMode, Button> _modeButtons = [];
    private readonly Dictionary<uint, Button> _kindButtons = [];
    private Button? _angleB, _gridB, _roadsB, _structB, _chainB, _conformB;
    private HSlider? _gradeS, _shoulderS;
    private Label? _gradeL, _shoulderL, _readout, _hintL;
    private PanelContainer? _panel;
    private Control? _brushPanel;
    private bool _syncing;

    private static readonly (RoadMode Mode, string Key, string Name, string Tip)[] Modes =
    [
        (RoadMode.Straight, "7", "STRAIGHT", "Straight roads, Shift chains"),
        (RoadMode.Curve, "8", "CURVE", "Start, control point, end; 8 again for a cubic"),
        (RoadMode.Freeform, "9", "FREEFORM", "A spline through your clicks"),
        (RoadMode.Select, "0", "SELECT", "Pick a road: drag points, add, delete, split"),
        (RoadMode.Upgrade, "U", "UPGRADE", "Change a road's type"),
        (RoadMode.Bulldoze, "X", "BULLDOZE", "Remove a road"),
    ];

    /// <summary>The palette group under the terrain tools.</summary>
    public void BuildPalette(VBoxContainer tools, float s)
    {
        _modeButtons.Clear();
        tools.AddChild(new HSeparator());
        var title = new Label { Text = "ROADS" };
        EditorStyle.Title(title, 18 * s);
        tools.AddChild(title);
        var group = new ButtonGroup { AllowUnpress = true };
        foreach (var (mode, key, name, tip) in Modes)
        {
            var m = mode;
            var b = EditorStyle.Button($"{key}   {name}", () => SetMode(m), s, 22, toggle: true);
            b.ButtonGroup = group; b.TooltipText = tip;
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(EditorStyle.ToolWidth * s, 38 * s);
            tools.AddChild(b);
            _modeButtons[mode] = b;
        }
        foreach (var (b, name, tip) in new[] { (BuildMode.Bridge, "BRIDGE", "Lay a bridge in whole deck pieces on one grade, over a valley, river or road (Shift+Tab cycles ground, bridge, tunnel)"),
                                                (BuildMode.Tunnel, "TUNNEL", "Lay a tunnel through a hill with the road running through it (Shift+Tab cycles ground, bridge, tunnel)") })
        {
            var bm = b;
            var sb = EditorStyle.Button("    " + name, () => { if (Active && Build == bm) SetBuild(BuildMode.Ground); else SetBuild(bm); }, s, 22, toggle: true);
            sb.TooltipText = tip;
            sb.Alignment = HorizontalAlignment.Left;
            sb.CustomMinimumSize = new Vector2(EditorStyle.ToolWidth * s, 38 * s);
            tools.AddChild(sb);
            _buildPalette[bm] = sb;
        }
    }

    private readonly Dictionary<BuildMode, Button> _buildPalette = [];
    private readonly Dictionary<BuildMode, Button> _buildButtons = [];
    private OptionButton? _pieceOpt;
    private Label? _pieceL;
    private Control? _buildBox;
    private BuildMode _optionsFor = BuildMode.Ground;

    private void BuildBuildSection(VBoxContainer bx, float s)
    {
        var l = new Label { Text = "BUILD ON   (Shift+Tab)" };
        EditorStyle.Body(l, 15 * s);
        bx.AddChild(l);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(6 * s));
        var g = new ButtonGroup();
        foreach (var (m, name, tip) in new[] { (BuildMode.Ground, "GROUND", "The road follows the terrain (graded and cut and filled as set below)"),
                                               (BuildMode.Bridge, "BRIDGE", "Straight deck pieces end to end on one grade; the terrain under them is left, apart from a pier of ground under each joint"),
                                               (BuildMode.Tunnel, "TUNNEL", "A tunnel piece through a hill, level, with the road through it and the ground levelled under its mountain") })
        {
            var mm = m;
            var b = EditorStyle.Button(name, () => SetBuild(mm), s, 17, toggle: true);
            b.ButtonGroup = g; b.TooltipText = tip; b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(b);
            _buildButtons[m] = b;
        }
        bx.AddChild(row);
        _buildBox = new VBoxContainer();
        ((VBoxContainer)_buildBox).AddThemeConstantOverride("separation", (int)(4 * s));
        _pieceL = new Label { Text = "PIECE" };
        EditorStyle.Body(_pieceL, 15 * s);
        _buildBox.AddChild(_pieceL);
        _pieceOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "AUTO picks the pieces that fit the span best; or lay one model repeated" };
        _pieceOpt.AddThemeFontSizeOverride("font_size", (int)(16 * s));
        _pieceOpt.ItemSelected += i =>
        {
            if (_syncing) return;
            PieceName = _pieceOpt.GetItemMetadata((int)i).AsString();
            _planKey = 0;
        };
        _buildBox.AddChild(_pieceOpt);
        bx.AddChild(_buildBox);
        bx.AddChild(new HSeparator());
    }

    private void SyncBuild()
    {
        foreach (var (m, b) in _buildButtons) b.SetPressedNoSignal(m == Build);
        foreach (var (m, b) in _buildPalette) b.SetPressedNoSignal(Active && Build == m);
        if (_buildBox is null || _pieceOpt is null) return;
        bool on = Build != BuildMode.Ground;
        _buildBox.Visible = on && Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform;
        if (!on) return;
        if (_optionsFor != Build || _pieceOpt.ItemCount == 0)
        {
            _optionsFor = Build;
            _pieceOpt.Clear();
            _pieceOpt.AddItem("AUTO: best fit"); _pieceOpt.SetItemMetadata(0, "");
            foreach (var p in StretchPiecesNow())
            {
                float len = StretchPieces.Length(p);
                string what = p.IsTunnel ? $"tube {len:0} m" : $"{len:0} m, {StretchPieces.StyleName(StretchPieces.Style(p)).ToLowerInvariant()}";
                _pieceOpt.AddItem($"{p.Name.ToUpperInvariant()}   {what}");
                _pieceOpt.SetItemMetadata(_pieceOpt.ItemCount - 1, p.Name);
            }
        }
        for (int i = 0; i < _pieceOpt.ItemCount; i++) if (_pieceOpt.GetItemMetadata(i).AsString() == PieceName) { _pieceOpt.Select(i); break; }
    }

    /// <summary>The road settings, at the right where the brush's are; they replace those while a road tool is on.</summary>
    public void BuildPanel(Control root, float s, Control brushPanel)
    {
        _brushPanel = brushPanel;
        _kindButtons.Clear();
        _panel = new PanelContainer { Name = "RoadPanel", Visible = false };
        _panel.AnchorLeft = 1; _panel.AnchorRight = 1; _panel.AnchorTop = 0; _panel.AnchorBottom = 0;
        _panel.OffsetRight = -14 * s; _panel.OffsetTop = 70 * s; _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        root.AddChild(_panel);
        var bx = new VBoxContainer { CustomMinimumSize = new Vector2(270 * s, 0) };
        bx.AddThemeConstantOverride("separation", (int)(6 * s));
        _panel.AddChild(EditorStyle.Scrolling(bx, s, 70, 50));
        var t = new Label { Text = "ROAD" };
        EditorStyle.Title(t, 18 * s);
        bx.AddChild(t);
        BuildBuildSection(bx, s);

        var tl = new Label { Text = "TYPE   (T changes the selected road's)" };
        EditorStyle.Body(tl, 15 * s);
        bx.AddChild(tl);
        var kinds = new HBoxContainer();
        kinds.AddThemeConstantOverride("separation", (int)(6 * s));
        var kg = new ButtonGroup();
        foreach (uint k in new uint[] { 0, 1, 2 })
        {
            uint kk = k;
            var b = EditorStyle.Button(RoadBuilder.KindName(k).ToUpperInvariant(), () => SetKind(kk, true), s, 17, toggle: true);
            b.ButtonGroup = kg; b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            kinds.AddChild(b);
            _kindButtons[k] = b;
        }
        bx.AddChild(kinds);
        bx.AddChild(new HSeparator());

        Button Toggle(string text, string tip, Func<bool> get, Action<bool> set)
        {
            var b = EditorStyle.Button(text, () => { }, s, 17, toggle: true);
            b.TooltipText = tip; b.Alignment = HorizontalAlignment.Left;
            b.Toggled += on => { if (!_syncing) { set(on); _planKey = 0; } };
            bx.AddChild(b);
            return b;
        }
        _angleB = Toggle("ANGLE SNAP 15°   V", "Snap each piece to 15 degrees from the last", () => Snap.Angle, v => Snap.Angle = v);
        _gridB = Toggle("GRID SNAP 10 m   H", "Snap points to a 10 m grid", () => Snap.Grid, v => Snap.Grid = v);
        _roadsB = Toggle("SNAP TO ROADS", "Road ends (it extends that road) and middles", () => Snap.Roads, v => Snap.Roads = v);
        _structB = Toggle("SNAP TO BRIDGES AND RAMPS", "Meet a deck or the foot of a ramp, square and at its height", () => Snap.Structures, v => Snap.Structures = v);
        _chainB = Toggle("CHAIN   C   (or hold Shift)", "Each click goes on from the last", () => Chain, v => Chain = v);
        _conformB = Toggle("CUT AND FILL THE GROUND   O", "Grade the ground under the road and blend a shoulder", () => Conform, v => Conform = v);

        _gradeL = new Label(); EditorStyle.Body(_gradeL, 16 * s);
        bx.AddChild(_gradeL);
        _gradeS = new HSlider { MinValue = 2, MaxValue = 40, Step = 1 };
        EditorStyle.Slider(_gradeS, s);
        _gradeS.ValueChanged += v => { if (!_syncing) { MaxGrade = (float)v / 100f; _planKey = 0; SyncPanel(); } };
        bx.AddChild(_gradeS);
        _shoulderL = new Label(); EditorStyle.Body(_shoulderL, 16 * s);
        bx.AddChild(_shoulderL);
        _shoulderS = new HSlider { MinValue = 0, MaxValue = 40, Step = 1 };
        EditorStyle.Slider(_shoulderS, s);
        _shoulderS.ValueChanged += v => { if (!_syncing) { Shoulder = (float)v; _planKey = 0; SyncPanel(); } };
        bx.AddChild(_shoulderS);

        bx.AddChild(new HSeparator());
        _readout = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_readout, 15 * s);
        bx.AddChild(_readout);
        _hintL = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_hintL, 14 * s, colour: HudStyle.Amber);
        bx.AddChild(_hintL);
        SyncPanel();
    }

    public void SyncPanel()
    {
        if (_panel is null || !IsInstanceValid(_panel)) return;
        _syncing = true;
        _panel.Visible = Active;
        if (_brushPanel is not null && IsInstanceValid(_brushPanel)) _brushPanel.Visible = !Active;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(Active && m == Mode);
        foreach (var (k, b) in _kindButtons) b.SetPressedNoSignal(k == Kind);
        SyncBuild();
        _angleB?.SetPressedNoSignal(Snap.Angle); _gridB?.SetPressedNoSignal(Snap.Grid); _roadsB?.SetPressedNoSignal(Snap.Roads);
        _structB?.SetPressedNoSignal(Snap.Structures); _chainB?.SetPressedNoSignal(Chain); _conformB?.SetPressedNoSignal(Conform);
        if (_gradeS is not null) { _gradeS.SetValueNoSignal(MaxGrade * 100); _gradeL!.Text = $"MAX GRADE   {MaxGrade * 100:0}%"; }
        if (_shoulderS is not null) { _shoulderS.SetValueNoSignal(Shoulder); _shoulderL!.Text = $"SHOULDER   {Shoulder:0} m"; }
        _syncing = false;
    }

    private int _uiFrame;

    /// <summary>The panel's text, a few times a second.</summary>
    public void UpdateUi()
    {
        if (!Active || _readout is null || !IsInstanceValid(_readout) || ++_uiFrame % 4 != 0) return;
        _readout.Text = Readout();
        _hintL!.Text = Hint;
        _hintL.AddThemeColorOverride("font_color", HintIsProblem ? HudStyle.Danger : HudStyle.Amber);
    }
}
