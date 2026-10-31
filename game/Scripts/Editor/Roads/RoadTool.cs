using Godot;
using I76.Editor;
using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Game.Editor.Intersections;
using I76.Formats.Mission.Edit;
using I76.Game.Sim;
using I76.Sim.Missions;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.RoadTools;

public enum RoadMode { Straight, Curve, Freeform, Select, Upgrade, Bulldoze }

/// <summary>
/// The map editor's road tool (docs/map-editor.md): draw roads (straight, curve, freeform), snap them, edit them, change their
/// type, bulldoze them. The maths is in src/I76.Editor/Roads (tested); this node is the input, the preview, the handles and
/// the panel. A road and the ground cut and filled for it are one undo step (RoadCommand).
/// </summary>
public sealed partial class RoadTool : Node
{
    public required MapEditor Ed { get; init; }

    // ---- settings (kept for the session) ------------------------------------------------------------------------------------
    public static uint Kind = 0;
    public static bool Cubic, Chain, Conform = true;
    public static readonly SnapOptions Snap = new() { Angle = true, Grid = false };
    public static float MaxGrade = RoadGrade.DefaultMaxGrade;
    public static float Shoulder = 14f;
    /// <summary>How many points either side a dragged point takes with it (0: only that point).</summary>
    public static int Reach = 6;

    public bool Active { get; private set; }
    public RoadMode Mode { get; private set; } = RoadMode.Straight;
    private RoadVisuals _vis = null!;
    private RoadPainter _painter = null!;
    private Node3D _worldRoot = null!;
    // intersection pieces (docs/map-editor.md, Intersections)
    private Junctions _junctions = null!;
    private PieceView _pieceView = null!;
    private List<Proposal> _proposals = [];
    private bool _piecesDirty;
    private System.Numerics.Vector4? _pieceBounds;
    private readonly List<StructureAttach.Attach> _snapEnds = [];
    private (int Roads, int Pieces, int Ends) _armsKey = (-1, -1, -1);

    // drawing
    private RoadDraft? _draft;
    private SnapResult _snap;
    private RoadPlan? _plan;
    private float[]? _profile, _natural;
    private float _cut, _fill;
    private string _hint = "", _problem = "";
    private ulong _lastClickMs;
    private int _planKey;
    // selection and editing
    private int _sel = -1, _selPt = -1, _hoverPt = -1, _hoverRoad = -1;
    private int _dragPt = -1;
    private RoadEdit? _dragAfter;
    private bool _dragSingle;
    // scripted
    private V2? _virtual;

    private RoadStore Roads => Ed.Doc.Roads;
    private double Ground(double x, double z) => Ed.Doc.Tiles is null ? 0 : RoadTerrain.Ground(Ed.Doc, x, z);
    private float GroundV(V2 p) => (float)Ground(p.X, p.Y);

    public bool Busy => _draft is not null || _dragPt >= 0;

    public override void _Ready()
    {
        _worldRoot = (Node3D)Ed.Builder.Terrain!.GetParent();
        _vis = new RoadVisuals(Ed.Builder, Ed.Is1997);
        _worldRoot.AddChild(_vis);
        _painter = new RoadPainter(Ed.Builder, Roads);
        Roads.Changed += OnRoadsChanged;
        var pieces = IntersectionSession.Pieces ?? new PieceStore();
        _junctions = new Junctions(Roads, pieces, GroundV) { LevelDoc = Ed.Doc };
        _pieceView = new PieceView();
        _worldRoot.AddChild(_pieceView);
        pieces.Changed += OnPiecesChanged;
    }

    private void OnPiecesChanged(PieceChange c)
    {
        // the placements the loader paints are the store's: made again, the tops repainted, once a frame
        _piecesDirty = true;
        _pieceBounds = _pieceBounds is { } b ? RoadStore.Merge(b, c.Bounds) : c.Bounds;
    }

    public override void _ExitTree()
    {
        Roads.Changed -= OnRoadsChanged;
        _junctions.Pieces.Changed -= OnPiecesChanged;
        if (IsInstanceValid(_vis)) _vis.QueueFree();
        if (IsInstanceValid(_pieceView)) _pieceView.QueueFree();
    }

    private void OnRoadsChanged(RoadChange c)
    {
        // the world the game builds from has these roads (the road routes, a playtest, the painting)
        Ed.Builder.World!.Mission.RoadOverride = Roads.ToSegments();
        _painter.Request(c.Bounds);
        if (_sel >= Roads.Count) { _sel = -1; _selPt = -1; }
        _planKey = 0;
    }

    // ---- modes --------------------------------------------------------------------------------------------------------------

    public void SetMode(RoadMode m)
    {
        if (Active && m == RoadMode.Curve && Mode == RoadMode.Curve && !Busy) Cubic = !Cubic;
        Cancel();
        Mode = m;
        Active = true;
        if (m is not (RoadMode.Select)) { _sel = -1; _selPt = -1; }
        Ed.RoadToolChanged();
        Ed.Toast(ModeHelp());
    }

    public void Deactivate()
    {
        if (!Active) return;
        Cancel();
        Active = false;
        _vis.Clear();
        Ed.RoadToolChanged();
    }

    public string ModeHelp() => IsStretch ? $"{(Build == BuildMode.Bridge ? "Bridge" : "Tunnel")}: click the start, click toward the far end (curves are laid straight). Shift+Tab: ground, bridge, tunnel." : Mode switch
    {
        RoadMode.Straight => "Straight road: click the start, click the end. Hold Shift on a click to chain on. Enter finishes.",
        RoadMode.Curve => Cubic ? "Cubic curve: start, two control points, end. Shift chains on." : "Curve: start, control point, end. Shift chains on. (8 again: cubic)",
        RoadMode.Freeform => "Freeform: click points to bend a spline through; Enter or a double click finishes.",
        RoadMode.Select => "Select a road to edit: drag a point, double-click to add one, Delete removes, K splits, T changes the type.",
        RoadMode.Upgrade => "Upgrade: click a road to give it the road type chosen in the panel.",
        _ => "Bulldoze: click a road to remove it. Ctrl+Z brings it back.",
    };

    /// <summary>Drops what is half drawn or being dragged.</summary>
    public void Cancel()
    {
        _draft = null; _plan = null; _dragPt = -1; _dragAfter = null;
        _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0); _vis.ShowStretch(null, _ => null, _ => 0); _stretch = null;
        _proposals = []; _pieceView.Show(_proposals, GroundV);
        _problem = _hint = "";
        _planKey = 0;
    }

    /// <summary>
    /// Makes an edit of the roads (<paramref name="make"/> does it and returns its command) and lets the junctions follow: pieces that
    /// lost a road at an arm change or go, the roads it made get their pieces. One undo step. Returns whether pieces followed.
    /// </summary>
    private bool Edit(Func<IEditCommand> make, string name)
    {
        var before = _junctions.Take();
        var cmd = make();
        var follow = _junctions.Settle(before, name, Conform);
        Ed.Undo.Push(Junctions.Join(name, cmd, follow));
        return follow is not null;
    }

    // ---- input --------------------------------------------------------------------------------------------------------------

    public bool HandleMouse(InputEventMouseButton mb)
    {
        if (!Active) return false;
        if (mb.ButtonIndex != MouseButton.Left) return false;
        if (mb.Pressed)
        {
            ulong now = Time.GetTicksMsec();
            bool dbl = mb.DoubleClick || (now - _lastClickMs < 320 && _lastClickPos.DistanceTo(mb.Position) < 6);
            _lastClickMs = now; _lastClickPos = mb.Position;
            Press(mb.ShiftPressed, mb.AltPressed, dbl);
        }
        else Release();
        return true;
    }

    private Vector2 _lastClickPos;

    public bool HandleKey(InputEventKey k)
    {
        bool ctrl = k.CtrlPressed;
        switch (k.Keycode)
        {
            case Key.Key7 or Key.Kp7 when !ctrl: SetMode(RoadMode.Straight); return true;
            case Key.Key8 or Key.Kp8 when !ctrl: SetMode(RoadMode.Curve); return true;
            case Key.Key9 or Key.Kp9 when !ctrl: SetMode(RoadMode.Freeform); return true;
            case Key.Key0 or Key.Kp0 when !ctrl: SetMode(RoadMode.Select); return true;
        }
        if (!Active) return false;
        switch (k.Keycode)
        {
            case Key.Escape when !k.Echo:
                if (Busy) { Cancel(); return true; }
                if (_sel >= 0) { _sel = -1; _selPt = -1; return true; }
                return false;
            case Key.Tab when k.ShiftPressed && !k.Echo && Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform: CycleBuild(); return true;
            case Key.Enter or Key.KpEnter when !k.Echo:
                if (_draft is not null) { if (IsStretch) CommitStretch(false); else Finish(false); return true; }
                return false;
            case Key.Backspace when !k.Echo && _draft is not null:
                if (_draft.Clicks.Count > 1) _draft.Clicks.RemoveAt(_draft.Clicks.Count - 1);
                else Cancel();
                _planKey = 0;
                return true;
            case Key.Delete when !k.Echo && Mode == RoadMode.Select && _sel >= 0:
                DeleteSelection();
                return true;
            case Key.K when !ctrl && !k.Echo && Mode == RoadMode.Select && _sel >= 0:
                SplitSelection();
                return true;
            case Key.T when !ctrl && !k.Echo && _sel >= 0:
                SetKind((Kind + 1) % 3, applyToSelected: true);
                return true;
            case Key.Insert when !k.Echo && Mode == RoadMode.Select && _sel >= 0 && Cursor() is { } c:
                InsertAt(c);
                return true;
            case Key.X when !ctrl && !k.Echo: SetMode(RoadMode.Bulldoze); return true;
            case Key.U when !ctrl && !k.Echo: SetMode(RoadMode.Upgrade); return true;
            case Key.V when !ctrl && !k.Echo: Snap.Angle = !Snap.Angle; Ed.Toast(Snap.Angle ? "Angle snap on (15 degrees)" : "Angle snap off"); SyncPanel(); return true;
            case Key.H when !ctrl && !k.Echo: Snap.Grid = !Snap.Grid; Ed.Toast(Snap.Grid ? $"Grid snap on ({Snap.GridSize:0} m)" : "Grid snap off"); SyncPanel(); return true;
            case Key.C when !ctrl && !k.Echo: Chain = !Chain; Ed.Toast(Chain ? "Chaining on: each click goes on" : "Chaining off (Shift chains one click)"); SyncPanel(); return true;
            case Key.O when !ctrl && !k.Echo: Conform = !Conform; Ed.Toast(Conform ? "The ground is cut and filled under new roads" : "Roads follow the ground as it is"); SyncPanel(); return true;
        }
        return false;
    }

    private V2? Cursor() => _virtual ?? (Ed.Hit is { } h ? new V2(h.X, h.Z) : null);

    private void Press(bool shift, bool alt, bool dbl)
    {
        if (Cursor() is not { } cur) return;
        switch (Mode)
        {
            case RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform:
                ClickDraw(shift, dbl);
                break;
            case RoadMode.Select:
                if (_hoverPt >= 0 && _sel >= 0) { _selPt = _hoverPt; _dragPt = _hoverPt; _dragSingle = alt; _dragAfter = null; _dragMoved = false; }
                else if (dbl && _sel >= 0 && NearSelected(cur)) InsertAt(cur);
                else { _sel = PickRoad(cur); _selPt = -1; }
                break;
            case RoadMode.Upgrade:
                if (PickRoad(cur) is var u and >= 0)
                {
                    if (Roads[u].Kind == Kind) { Ed.Toast($"Already {RoadBuilder.KindName(Kind)}"); break; }
                    Edit(() => RoadEditing.Retype(Ed.Doc, Roads, u, Kind), "Change road type");
                    Ed.Toast($"Road {u + 1}: {RoadBuilder.KindName(Kind)}");
                }
                break;
            case RoadMode.Bulldoze:
                if (PickRoad(cur) is var b and >= 0)
                {
                    int n = Roads[b].Points.Count;
                    bool follow = Edit(() => RoadEditing.Bulldoze(Ed.Doc, Roads, b), "Bulldoze road");
                    Ed.Toast($"Bulldozed a road of {n} points{(follow ? "; the junctions beside it changed" : "")} (Ctrl+Z brings it back; the ground stays as it was graded)");
                }
                break;
        }
    }

    private bool _dragMoved;

    private void Release()
    {
        if (_dragPt >= 0)
        {
            if (_dragAfter is not null && _dragMoved && _sel >= 0)
            {
                int reach = _dragSingle ? 0 : Reach;
                int sel = _sel, pt = _dragPt; var after = _dragAfter;
                bool follow = Edit(() => RoadEditing.Replace(Ed.Doc, Roads, sel, after, pt - reach - 2, pt + reach + 2, Conform, Grade(), Shoulders(), "Move road point"), "Move road point");
                Ed.Toast((_dragSingle ? "Moved a point" : "Moved the road") + (follow ? "; its junctions followed" : ""));
            }
            _dragPt = -1; _dragAfter = null;
            _vis.ShowRoad(null, null);
        }
    }

    private RoadGrade.Settings Grade() => new(MaxGrade, 30f);
    private RoadTerrain.Settings Shoulders() => new(RoadBuilder.HalfWidth + 3f, Shoulder);

    // ---- drawing --------------------------------------------------------------------------------------------------------------

    private void ClickDraw(bool shift, bool dbl)
    {
        if (IsStretch) { ClickStretch(shift); return; }
        var p = _snap.Point;
        if (_draft is null)
        {
            _draft = new RoadDraft { Mode = DrawMode(), Start = _snap };
            _draft.Clicks.Add(p);
            _planKey = 0;
            return;
        }
        bool ending = _snap.Kind is SnapKind.Structure or SnapKind.RoadEnd or SnapKind.RoadMiddle;
        if (_draft.Clicks.Count > 0 && V2.Distance(_draft.Clicks[^1], p) < 1f)
        {
            // a double click lands twice on the same spot: that finishes a freeform or chained road
            if (dbl || Mode == RoadMode.Freeform) Finish(false);
            return;
        }
        _draft.Clicks.Add(p);
        int need = Mode switch { RoadMode.Curve => Cubic ? 4 : 3, _ => 2 };
        bool chain = shift || Chain;
        switch (Mode)
        {
            case RoadMode.Straight:
                if (ending || !chain) Finish(true); 
                break;
            case RoadMode.Curve:
                if (_draft.Clicks.Count >= need)
                {
                    if (ending || !chain) Finish(true);
                    else
                    {
                        _draft.Done.Add(RoadPlanner.Path(_draft.Mode, _draft.Clicks));
                        _draft.Clicks = [p];
                    }
                }
                break;
            case RoadMode.Freeform:
                if (ending && _draft.Clicks.Count >= 2) Finish(true);
                break;
        }
        _planKey = 0;
    }

    private DrawMode DrawMode() => Mode switch { RoadMode.Curve => Cubic ? I76.Editor.Roads.DrawMode.Cubic : I76.Editor.Roads.DrawMode.Curve, RoadMode.Freeform => I76.Editor.Roads.DrawMode.Freeform, _ => I76.Editor.Roads.DrawMode.Straight };

    /// <summary>Makes the road of the clicks so far (the last click being the end when <paramref name="endClicked"/>, else the cursor is not part of it).</summary>
    private void Finish(bool endClicked)
    {
        if (_draft is null) return;
        var d = _draft;
        var path = RoadPlanner.DraftPath(d, null);
        SnapResult? end = endClicked && _snap.Kind is SnapKind.Structure or SnapKind.RoadEnd or SnapKind.RoadMiddle ? _snap : null;
        var plan = RoadPlanner.Plan(path, Kind, d.Start is { Kind: SnapKind.Structure or SnapKind.RoadEnd } s ? s : null, end, Roads);
        if (plan.Problem is not null) { Ed.Toast(plan.Problem, true); if (!endClicked) { } return; }
        var before = _junctions.Take();
        int pieces0 = _junctions.Pieces.Count;
        var made = RoadPlanner.Commit(Ed.Doc, Roads, plan, Conform, Grade(), Shoulders());
        if (made is not { } r) { Ed.Toast("Could not make that road", true); return; }
        // the junctions the new road makes (a T where it ends on a road, an X where it crosses one): the same undo step
        var follow = _junctions.Settle(before, "Junctions", Conform);
        Ed.Undo.Push(Junctions.Join(r.Command.Name, r.Command, follow));
        _sel = follow is not null ? -1 : plan.ExtendRoad >= 0 ? plan.ExtendRoad : Roads.Count - 1;
        string verb = plan.Closes ? "Closed a loop" : plan.ExtendRoad >= 0 ? "Extended road" : "Road";
        string ground = r.Terrain is { } t && Conform ? $"; cut {t.MaxCut:0.0} m, fill {t.MaxFill:0.0} m" : "";
        int made2 = _junctions.Pieces.Count - pieces0;
        string pieces = made2 > 0 ? $"; {made2} intersection piece{(made2 > 1 ? "s" : "")} placed" : follow is not null ? "; junctions changed" : "";
        Ed.Toast($"{verb}: {plan.Centres.Count} points, {RoadCurves.Length(plan.Centres):0} m{ground}{pieces}");
        Cancel();
    }

    // ---- editing ----------------------------------------------------------------------------------------------------------------

    private int PickRoad(V2 p)
    {
        int best = -1; float bd = 7f * 7f;
        for (int i = 0; i < Roads.Count; i++)
        {
            var b = RoadStore.Bounds(Roads[i]);
            if (p.X < b.X - 8 || p.X > b.Z + 8 || p.Y < b.Y - 8 || p.Y > b.W + 8) continue;
            var pts = Roads[i].Points;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                V2 a = pts[k].Centre, c = pts[k + 1].Centre, ab = c - a;
                float l2 = ab.LengthSquared();
                float t = l2 < 1e-6f ? 0 : Math.Clamp(V2.Dot(p - a, ab) / l2, 0, 1);
                float d = V2.DistanceSquared(p, a + ab * t);
                if (d < bd) { bd = d; best = i; }
            }
        }
        return best;
    }

    private bool NearSelected(V2 p) => _sel >= 0 && PickRoad(p) == _sel;

    private void InsertAt(V2 p)
    {
        if (_sel < 0) return;
        var pts = Roads[_sel].Points;
        int bestK = -1; float bd = float.MaxValue; V2 at = default;
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            V2 a = pts[k].Centre, c = pts[k + 1].Centre, ab = c - a;
            float l2 = ab.LengthSquared();
            float t = l2 < 1e-6f ? 0 : Math.Clamp(V2.Dot(p - a, ab) / l2, 0, 1);
            var q = a + ab * t;
            float d = V2.DistanceSquared(p, q);
            if (d < bd) { bd = d; bestK = k; at = q; }
        }
        if (bestK < 0) return;
        var after = RoadOps.InsertPoint(Roads[_sel], bestK, at);
        Ed.Undo.Push(RoadEditing.Replace(Ed.Doc, Roads, _sel, after, 0, 0, false, name: "Add road point"));
        _selPt = bestK + 1;
        Ed.Toast("Added a point");
    }

    private void DeleteSelection()
    {
        if (_sel < 0) return;
        if (_selPt >= 0)
        {
            if (RoadOps.DeletePoint(Roads[_sel], _selPt) is { } after)
            {
                int sel = _sel;
                Edit(() => RoadEditing.Replace(Ed.Doc, Roads, sel, after, 0, 0, false, name: "Delete road point"), "Delete road point");
                _selPt = -1;
                Ed.Toast("Deleted a point");
            }
            else Ed.Toast("A road needs at least 3 points: use Delete with no point selected to remove it", true);
            return;
        }
        int del = _sel;
        Edit(() => RoadEditing.Bulldoze(Ed.Doc, Roads, del), "Bulldoze road");
        _sel = -1;
        Ed.Toast("Bulldozed (Ctrl+Z brings it back)");
    }

    private void SplitSelection()
    {
        if (_selPt < 0) { Ed.Toast("Select a point of the road to split it there", true); return; }
        if (RoadEditing.Split(Roads, _sel, _selPt) is { } cmd) { Ed.Undo.Push(cmd); _selPt = -1; Ed.Toast("Split in two roads (each is a road of its own for the AI)"); }
        else Ed.Toast("Too close to an end to split", true);
    }

    public void SetKind(uint kind, bool applyToSelected)
    {
        Kind = kind;
        if (applyToSelected && Active && _sel >= 0 && _sel < Roads.Count && Roads[_sel].Kind != kind)
        {
            int sel = _sel;
            Edit(() => RoadEditing.Retype(Ed.Doc, Roads, sel, kind), "Change road type");
            Ed.Toast($"Road {_sel + 1}: {RoadBuilder.KindName(kind)}");
        }
        _planKey = 0;
        Ed.RoadToolChanged();
    }

    // ---- structures to snap to ------------------------------------------------------------------------------------------------

    private readonly List<StructureAttach.Attach> _structures = [];
    private readonly Dictionary<I76.Formats.Assembly.Placement, StructureAttach.Attach[]> _endsOf = new(ReferenceEqualityComparer.Instance);
    private Queue<I76.Formats.Assembly.Placement>? _toDo;
    private int _layerSeen = -1;
    private bool _endsStale;
    private int _structVersion;
    public int StructureCount => _structures.Count;
    public int StructuresLeft => _toDo?.Count ?? 0;

    /// <summary>
    /// Reads the ends of the map's bridges, jumps and ramps (the object layer's: the map's own and the ones placed in the editor, including the
    /// pieces of a bridge stretch) a few a frame, since their colliders are built as a mission builds them; again for what a change of the layer brings.
    /// </summary>
    private void PumpStructures()
    {
        var world = Ed.Builder.World!;
        var layer = Ed.Objects.Layer;
        if (_toDo is null || _layerSeen != layer.Version)
        {
            _layerSeen = layer.Version;
            _toDo = new Queue<I76.Formats.Assembly.Placement>(layer.Items.Select(i => i.Placement).Where(p => p.Definition is not null && p.Object.ObjectType is 0xb or 0xc or 0xd && !_endsOf.ContainsKey(p)));
            _endsStale = true;
        }
        for (int i = 0; i < 4 && _toDo.Count > 0; i++)
        {
            var p = _toDo.Dequeue();
            try
            {
                var pos = world.GroundedPosition(p);
                _endsOf[p] = MissionSetup.StructureCollider(Ed.Builder.Vfs, Ed.Builder.Assembler!, p.Object.Name, p.Definition!, p.Object.ObjectType, p.Object.Rotation, pos) is { } so
                    ? [.. StructureAttach.Ends(so.Surfaces.Select(s => s.V))] : [];
            }
            catch (Exception ex) { _endsOf[p] = []; GD.PrintErr($"editor roads: structure {p.Object.Name}: {ex.Message}"); }
        }
        if (_toDo.Count == 0 && _endsStale)
        {
            _endsStale = false;
            _structures.Clear(); _structVersion++;
            foreach (var it in layer.Items) if (_endsOf.TryGetValue(it.Placement, out var ends)) _structures.AddRange(ends);
            if (!_structuresLogged) { _structuresLogged = true; GD.Print($"editor roads: {_structures.Count} structure ends to snap to"); }
        }
    }

    private bool _structuresLogged;

    // ---- per frame ---------------------------------------------------------------------------------------------------------------

    public void Process(double dt)
    {
        if (_piecesDirty)
        {
            _piecesDirty = false;
            IntersectionSession.Sync(Ed.Builder.World!);
            Ed.Builder.RefreshPieceTops();
            if (_pieceBounds is { } pb) _painter.Request(pb);
            _pieceBounds = null;
        }
        _painter.Pump();
        _ = dt;
        UpdateUi();
        if (!Active) return;
        PumpStructures();
        var cam = Ed.Cam;
        float scale = Math.Clamp(cam.Distance * 0.012f, 0.8f, 14f);
        if (Cursor() is not { } cur) { _vis.ShowMarker(null, 0, SnapKind.None, scale); return; }

        bool drawing = Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform;
        UpdateSnap(cur, scale);

        if (!drawing && _proposals.Count > 0) { _proposals = []; _pieceView.Show(_proposals, GroundV); }
        if (drawing) UpdateDraw();
        else if (Mode == RoadMode.Select) UpdateSelect(cur, scale);
        else UpdateHover(cur);
        _vis.SetMode1997(Ed.Is1997());
    }

    /// <summary>The structure ends the snap offers: the bridges' and ramps', and the free arms of the intersection pieces (a road meets one square on).</summary>
    private void RefreshArms()
    {
        var key = (Roads.Version, _junctions.Pieces.Version, _structVersion);
        if (key == _armsKey) return;
        _armsKey = key;
        _snapEnds.Clear();
        _snapEnds.AddRange(_structures);
        _snapEnds.AddRange(_junctions.FreeArms());
    }

    private void UpdateSnap(V2 cur, float scale)
    {
        V2? anchor = null, refDir = null;
        if (_draft is { Clicks.Count: > 0 } d)
        {
            anchor = d.Clicks[^1];
            if (d.Clicks.Count >= 2) refDir = d.Clicks[^1] - d.Clicks[^2];
            else if (d.Done.Count > 0 && d.Done[^1].Count >= 2) refDir = d.Done[^1][^1] - d.Done[^1][^2];
        }
        bool drawing = Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform;
        var cam = Ed.Cam;
        var opts = Mode == RoadMode.Straight ? Snap : new SnapOptions { Angle = false, Grid = Snap.Grid, GridSize = Snap.GridSize, Radius = Snap.Radius, Roads = Snap.Roads, Structures = Snap.Structures };
        if (!drawing && _dragPt < 0) opts = new SnapOptions { Angle = false, Grid = false, Radius = Snap.Radius, Roads = false, Structures = false };
        else if (_dragPt >= 0) opts = new SnapOptions { Angle = false, Grid = Snap.Grid, GridSize = Snap.GridSize, Radius = Snap.Radius, Roads = false, Structures = Snap.Structures };
        // the radius follows the view: a click lands within a dozen pixels' worth of ground
        opts = new SnapOptions { Angle = opts.Angle, AngleStep = opts.AngleStep, Grid = opts.Grid, GridSize = opts.GridSize, Radius = Math.Clamp(cam.Distance * 0.02f, 6f, 25f), Roads = opts.Roads, Structures = opts.Structures };
        RefreshArms();
        _snap = RoadSnap.Snap(cur, opts, Roads, _snapEnds, anchor, refDir);
        _vis.ShowMarker(_snap.Kind == SnapKind.None ? null : _snap.Point, (float)Ground(_snap.Point.X, _snap.Point.Y), _snap.Kind, scale);

    }

    private void UpdateDraw()
    {
        _vis.ShowSelection(null, GroundV, Colors.White);
        if (_draft is null)
        {
            _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0);
            _vis.ShowStretch(null, _ => null, _ => 0); _stretch = null;
            if (_proposals.Count > 0) { _proposals = []; _pieceView.Show(_proposals, GroundV); }
            _hint = _snap.Hint ?? SnapDescription(); _problem = "";
            _plan = null;
            return;
        }
        if (IsStretch) { UpdateStretch(); return; }
        if (_stretch is not null) { _stretch = null; _vis.ShowStretch(null, _ => null, _ => 0); }
        int key = HashCode.Combine(_snap.Point, _draft.Clicks.Count, _draft.Done.Count, Mode, Cubic, Kind, MaxGrade, Conform) ^ HashCode.Combine(Shoulder, _snap.Kind, _snap.Road);
        if (key == _planKey) return;
        _planKey = key;
        var path = RoadPlanner.DraftPath(_draft, _snap.Point);
        var endSnap = _snap.Kind is SnapKind.Structure or SnapKind.RoadEnd or SnapKind.RoadMiddle ? _snap : (SnapResult?)null;
        // a piece not yet at its end (a curve waiting for its control point) is only a guide
        _plan = RoadPlanner.Plan(path, Kind, _draft.Start is { Kind: SnapKind.Structure or SnapKind.RoadEnd } s ? s : null, endSnap, Roads);
        _problem = _plan.Problem ?? "";
        if (_plan.Centres.Count < RoadCurves.MinPoints) { _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0); _proposals = []; _pieceView.Show(_proposals, GroundV); _hint = ""; return; }
        var c = _plan.Centres;
        var pins = RoadPlanner.AllPins(Ed.Doc, _plan);
        _profile = RoadGrade.Profile(c, GroundV, Grade(), pins);
        _natural = RoadGrade.NaturalGrades(c, GroundV);
        _cut = _fill = 0;
        for (int i = 0; i < c.Count; i++) { float dlt = _profile[i] - GroundV(c[i]); if (dlt > 0) _fill = Math.Max(_fill, dlt); else _cut = Math.Max(_cut, -dlt); }
        // an extension is of the road it extends (its kind), whatever the panel has chosen for new roads
        var preview = RoadBuilder.Build(c, _plan.ExtendRoad >= 0 && _plan.ExtendRoad < Roads.Count ? Roads[_plan.ExtendRoad].Kind : Kind, _plan.StartDir, _plan.EndDir, _plan.Closes);
        // the road mesh stands a hair above its profile so it draws over the ground it is being cut into
        // (where the profile is below the ground, the mesh would be buried: it stands on the ground there, and the ribbon shows the profile)
        var edges = preview.Points.Select((p, i) => Math.Max(_profile[i], (float)Math.Max(Ground(p.Centre.X, p.Centre.Y), Math.Max(Ground(p.Left.X, p.Left.Y), Ground(p.Right.X, p.Right.Y)))) + 0.1f).ToArray();
        _vis.ShowRoad(preview, edges);
        _vis.ShowGrade(c, _profile, _natural, MaxGrade);
        var notes = new List<string>();
        // the pieces this road would get where it ends on a road or crosses one (shown while drawing)
        _proposals = _junctions.Detect(preview, _plan.ExtendRoad >= 0 && _plan.ExtendRoad < Roads.Count ? Roads[_plan.ExtendRoad] : null);
        _pieceView.Show(_proposals, GroundV);
        var cross = RoadSnap.Crossings(c, Roads, _plan.ExtendRoad);
        int bare = cross.Count(x => !_proposals.Any(p => V2.Distance(p.Centre, x) < 16f));
        if (_proposals.Count > 0)
            notes.Add($"{string.Join(" and ", _proposals.Select(p => p.Describe))}: the roads are cut at its arms. AI cars drive one road at a time: they will not turn here.");
        else if (_snap.Hint is not null)
            notes.Add(_snap.Kind == SnapKind.RoadMiddle ? _snap.Hint + " No piece fits (square on or about 41 degrees, 12 m or more of road, not planks)." : _snap.Hint);
        if (bare > 0) notes.Add($"Crosses {bare} road{(bare > 1 ? "s" : "")} with no piece to fit: the game has no junctions, cars will not turn there.");
        float worst = RoadGrade.MaxAbs(_natural);
        if (worst > MaxGrade) notes.Add($"The ground is up to {worst * 100:0}% steep here (limit {MaxGrade * 100:0}%): {(Conform ? "cut and fill will level it" : "the road follows it")}.");
        if (_plan.ExtendRoad >= 0) notes.Add(_plan.Closes ? "Closes the loop." : "Extends the road you started on.");
        if (_snap.Kind == SnapKind.Structure) notes.Add(_snap.Structure?.Source is { } src ? $"Meets the free arm of a junction ({src}): square on, at its height." : "Meets a bridge or ramp: heading and height follow it.");
        _hint = string.Join("  ", notes);
    }

    private string SnapDescription() => _snap.Kind switch
    {
        SnapKind.Structure => _snap.Structure?.Source is { } src ? $"The free arm of a junction ({src}): the road meets it square on." : "A bridge, jump or ramp end: the road meets it square on and at its height.",
        SnapKind.RoadEnd => "The end of a road: the new road extends it.",
        _ => "",
    };

    private void UpdateHover(V2 cur)
    {
        _hoverRoad = PickRoad(cur);
        var col = Mode == RoadMode.Bulldoze ? new Color(0.95f, 0.25f, 0.2f) : new Color(0.4f, 0.8f, 1f);
        _vis.ShowSelection(_hoverRoad >= 0 ? Roads[_hoverRoad] : null, GroundV, col);
        _vis.ShowHandles(null, GroundV, -1, -1, 1);
        _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0);
        _hint = _hoverRoad >= 0 ? $"Road {_hoverRoad + 1}: {RoadBuilder.KindName(Roads[_hoverRoad].Kind)}, {Roads[_hoverRoad].Points.Count} points, {Roads[_hoverRoad].Length():0} m" : "";
    }

    private void UpdateSelect(V2 cur, float scale)
    {
        if (_sel >= Roads.Count) { _sel = -1; _selPt = -1; }
        _hoverRoad = _sel < 0 ? PickRoad(cur) : -1;
        var road = _sel >= 0 ? Roads[_sel] : null;
        if (_dragPt >= 0 && road is not null)
        {
            // the drag: the point goes where the cursor is (snapped), the road bends about it
            var to = _snap.Point;
            int n = road.Points.Count;
            bool endPoint = _dragPt == 0 || _dragPt == n - 1;
            V2? head = endPoint && _snap.Kind == SnapKind.Structure && _snap.Structure is { } st ? (_dragPt == 0 ? st.Direction : -st.Direction) : null;
            if (V2.Distance(to, road.Points[_dragPt].Centre) > 0.5f || _dragMoved)
            {
                _dragMoved = true;
                _dragAfter = RoadOps.MovePoint(road, _dragPt, to, _dragSingle ? 0 : Reach, head);
                _vis.ShowRoad(_dragAfter, _dragAfter.Points.Select(p => (float)Math.Max(Ground(p.Centre.X, p.Centre.Y), Math.Max(Ground(p.Left.X, p.Left.Y), Ground(p.Right.X, p.Right.Y))) + 0.12f).ToArray());
            }
            _vis.ShowSelection(null, GroundV, Colors.White);
            _vis.ShowHandles(_dragAfter ?? road, GroundV, _dragPt, _dragPt, scale);
            _hint = $"Moving point {_dragPt + 1} of {n}{(_dragSingle ? " alone" : "")}. {(Conform ? "The ground under it is graded when you let go." : "")}";
            return;
        }
        _vis.ShowSelection(road ?? (_hoverRoad >= 0 ? Roads[_hoverRoad] : null), GroundV, road is not null ? new Color(1f, 0.7f, 0.2f) : new Color(0.4f, 0.8f, 1f));
        // the handle under the mouse
        _hoverPt = -1;
        if (road is not null)
        {
            var cam = Ed.Cam;
            var mouse = Ed.GetViewport().GetMousePosition();
            float best = 14f * 14f * (Ed.GetViewport().GetVisibleRect().Size.Y / 1080f);
            for (int i = 0; i < road.Points.Count; i++)
            {
                var c = road.Points[i].Centre;
                var g = _worldRoot.ToGlobal(new Vector3(c.X, GroundV(c) + 0.5f, -c.Y));
                if (cam.IsPositionBehind(g)) continue;
                float d = cam.UnprojectPosition(g).DistanceSquaredTo(mouse);
                if (d < best) { best = d; _hoverPt = i; }
            }
        }
        _vis.ShowHandles(road, GroundV, _selPt, _hoverPt, scale);
        _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0);
        _hint = road is null ? (_hoverRoad >= 0 ? "Click to select this road." : "Click a road to select it.")
            : $"Road {_sel + 1}: {RoadBuilder.KindName(road.Kind)}, {road.Points.Count} points, {road.Length():0} m{(_selPt >= 0 ? $", point {_selPt + 1} selected" : "")}.";
    }

    // ---- readouts for the panel ----------------------------------------------------------------------------------------------------

    public string Readout()
    {
        if (IsStretch) return StretchReadout();
        if (_plan is { } p && _draft is not null && p.Centres.Count >= 3)
            return $"{RoadCurves.Length(p.Centres):0} m   {p.Centres.Count} points\nsteepest ground {RoadGrade.MaxAbs(_natural ?? [0]) * 100:0}%   limit {MaxGrade * 100:0}%\ncut {_cut:0.0} m   fill {_fill:0.0} m";
        return "";
    }

    public string Hint => _problem.Length > 0 ? _problem : _hint;
    public bool HintIsProblem => _problem.Length > 0;
    public int Selected => _sel;
    public int PendingPaint => _painter.Busy ? 1 : 0;
    public RoadPainter Painter => _painter;
}
