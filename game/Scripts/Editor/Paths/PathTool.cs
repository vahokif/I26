using Godot;
using I76.Editor.Paths;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.PathTools;

/// <summary>The path tool's modes, the original mission builder's four path buttons: a new path, move points, insert a point, delete.</summary>
public enum PathMode { Edit, New, Insert, Delete }

/// <summary>
/// The map editor's path tool (docs/map-editor.md, Paths): the nav paths the mission script names (goto, race, evade, teleport, the
/// isWithinNav tests, the cut-scene cameras) drawn over the ground and edited: draw a new path, select one, drag its points, insert
/// and delete points, delete a path, set its label. The table and its edits are src/I76.Editor/Paths (tested); this node is the
/// input, the layer and the panel. Every edit is one undo step (PathCommand).
/// </summary>
public sealed partial class PathTool : Node
{
    public required MapEditor Ed { get; init; }

    /// <summary>The PATHS layer: the paths shown while another tool is in use (kept for the session).</summary>
    public static bool ShowLayer;

    public bool Active { get; private set; }
    public PathMode Mode { get; private set; } = PathMode.Edit;
    private PathVisuals _vis = null!;
    private PathStore Store => Ed.Doc.Paths ??= new PathStore([]);

    // the selected path (its first table entry) and point, what the cursor is over
    private int _sel = -1, _selPt = -1, _hoverPt = -1, _hoverPath = -1;
    private V2? _hoverOn;
    // a path being drawn
    private List<V2>? _draft;
    // a point being dragged
    private int _dragPt = -1;
    private V2 _dragTo;
    private bool _dragMoved;
    private V2 _pressAt;
    private ulong _lastClickMs;
    private Vector2 _lastClickPos;
    // a scripted cursor (captures)
    private V2? _virtual;
    // what the layer shows, to redraw only on a change
    private (int Version, int Sel, int SelPt, int HoverPt, int HoverPath, int Scale, bool Labels, V2 Drag, int Draft) _shown = (-1, 0, 0, 0, 0, 0, false, default, -1);

    public bool Busy => _draft is not null || _dragPt >= 0;
    public string Hint { get; private set; } = "";

    public override void _Ready()
    {
        var root = (Node3D)Ed.Builder.Terrain!.GetParent();
        _vis = new PathVisuals((x, z) => Ed.GroundAt(x, z));
        root.AddChild(_vis);
        Store.Changed += OnPathsChanged;
    }

    public override void _ExitTree()
    {
        if (Ed.Doc.Paths is { } p) p.Changed -= OnPathsChanged;
        if (IsInstanceValid(_vis)) _vis.QueueFree();
    }

    private void OnPathsChanged(PathChange c)
    {
        // the world the game builds from has these paths (a playtest's script, the plants kept off them)
        Ed.Builder.World!.Mission.FsmOverride = Store.Payload();
        if (_sel >= 0)
        {
            if (Store.GroupOf(_sel) is not { } sel) { _sel = -1; _selPt = -1; }
            else if (_selPt >= sel.Points.Count) _selPt = -1;
        }
        _panelDirty = true;
    }

    // ---- modes --------------------------------------------------------------------------------------------------------------

    public void SetMode(PathMode m)
    {
        if (Store.WhyNotEdit is { } why) { Ed.Toast($"Paths: {why}", true); return; }
        Cancel();
        Mode = m;
        Active = true;
        Ed.PathToolChanged();
        Ed.Toast(ModeHelp());
    }

    public void Deactivate()
    {
        if (!Active) return;
        Cancel();
        Active = false;
        _vis.ShowMarker(null, default, 1);
        Ed.PathToolChanged();
    }

    public string ModeHelp() => Mode switch
    {
        PathMode.New => "New path: click its points in the order it is driven; Enter or a right click finishes, Backspace takes one back, Esc cancels.",
        PathMode.Insert => "Insert: click on a path's line to put a point there (hold the button to drag it into place).",
        PathMode.Delete => "Delete: click a point to remove it; click a path's line to delete the path.",
        _ => "Paths: click one to select it, drag its points; a double click on its line inserts a point; Delete removes the point.",
    };

    private void Cancel()
    {
        _draft = null;
        if (_dragPt >= 0) { _dragPt = -1; _dragMoved = false; }
    }

    private V2? Cursor() => _virtual ?? (Ed.Hit is { } h ? new V2(h.X, h.Z) : null);

    private float Scale => Math.Clamp(Ed.Cam.Distance * 0.012f, 0.8f, 14f);
    private PathEdits.Hit? PickAt(V2 p) => PathEdits.Pick(Store.Groups(), p, MathF.Max(3f, Scale * 1.6f), MathF.Max(2.5f, Scale * 1.2f));
    private PathGroup? Selected => _sel >= 0 ? Store.GroupOf(_sel) : null;

    // ---- input --------------------------------------------------------------------------------------------------------------

    /// <summary>Mouse buttons for the path tool; true if taken.</summary>
    public bool HandleMouse(InputEventMouseButton mb)
    {
        if (!Active) return false;
        if (mb.ButtonIndex == MouseButton.Right)
        {
            // a right click finishes the path being drawn (otherwise the camera orbits as ever)
            if (mb.Pressed && _draft is not null) { Finish(); return true; }
            return false;
        }
        if (mb.ButtonIndex != MouseButton.Left) return false;
        if (mb.Pressed)
        {
            ulong now = Time.GetTicksMsec();
            bool dbl = mb.DoubleClick || (now - _lastClickMs < 320 && _lastClickPos.DistanceTo(mb.Position) < 6);
            _lastClickMs = now; _lastClickPos = mb.Position;
            Press(dbl);
        }
        else Release();
        return true;
    }

    /// <summary>Keys for the path tool; true if taken. Not called while a text field has the focus.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.Minus or Key.KpSubtract when !k.Echo: SetMode(PathMode.Edit); return true;
            case Key.Equal or Key.KpAdd when !k.Echo: SetMode(PathMode.New); return true;
        }
        if (!Active) return false;
        switch (k.Keycode)
        {
            case Key.Escape when !k.Echo:
                if (Busy) { Cancel(); return true; }
                if (_sel >= 0) { _sel = -1; _selPt = -1; _panelDirty = true; return true; }
                return false;
            case Key.Enter or Key.KpEnter when !k.Echo && _draft is not null: Finish(); return true;
            case Key.Backspace when !k.Echo && _draft is not null:
                _draft.RemoveAt(_draft.Count - 1);
                if (_draft.Count == 0) _draft = null;
                return true;
            case Key.Delete or Key.Backspace when !k.Echo && Selected is { } g:
                if (_selPt >= 0) DeletePoint(g, _selPt);
                else AskDelete(g);
                return true;
            case Key.Insert when !k.Echo && Selected is { } g2 && Cursor() is { } c:
                InsertNear(g2, c);
                return true;
        }
        return false;
    }

    private void Press(bool dbl)
    {
        if (Cursor() is not { } cur) return;
        _pressAt = cur;
        switch (Mode)
        {
            case PathMode.New:
                _draft ??= [];
                if (_draft.Count > 0 && V2.Distance(_draft[^1], cur) < 1f) { if (dbl) Finish(); return; }   // a double click lands twice on the spot
                _draft.Add(cur);
                if (dbl && _draft.Count > 1) Finish();
                break;
            case PathMode.Edit:
                {
                    var hit = PickAt(cur);
                    if (hit is { Point: >= 0 } h)
                    {
                        Select(h.Path.First, h.Point);
                        _dragPt = h.Point; _dragTo = h.OnLine; _dragMoved = false;
                    }
                    else if (hit is { Segment: >= 0 } l)
                    {
                        if (dbl && l.Path.First == _sel) InsertAt(l.Path, l.Segment, l.OnLine, drag: true);
                        else Select(l.Path.First, -1);
                    }
                    else Select(-1, -1);
                    break;
                }
            case PathMode.Insert:
                if (PickAt(cur) is { Segment: >= 0 } ins) InsertAt(ins.Path, ins.Segment, ins.OnLine, drag: true);
                else if (PickAt(cur) is { Point: >= 0 } pt) { Select(pt.Path.First, pt.Point); _dragPt = pt.Point; _dragTo = pt.OnLine; _dragMoved = false; }
                else Ed.Toast("Click on a path's line to put a point there");
                break;
            case PathMode.Delete:
                if (PickAt(cur) is { } del)
                {
                    Select(del.Path.First, del.Point);
                    if (del.Point >= 0) DeletePoint(del.Path, del.Point);
                    else AskDelete(del.Path);
                }
                break;
        }
    }

    private void Release()
    {
        if (_dragPt < 0) return;
        if (_dragMoved && Selected is { } g && _dragPt < g.Points.Count)
        {
            Do(PathEdits.MovePoint(Store, g, _dragPt, _dragTo));
            Ed.Toast($"Moved point {_dragPt + 1} of {g.Label}");
        }
        _dragPt = -1; _dragMoved = false;
    }

    private void Select(int first, int point)
    {
        if (first != _sel) _panelDirty = true;
        _sel = first; _selPt = point;
    }

    private void Do(PathCommand c)
    {
        if (Store.WhyNotEdit is { } why) { Ed.Toast($"Paths: {why}", true); return; }
        Ed.Undo.Push(c.Apply());
        _panelDirty = true;
    }

    // ---- edits --------------------------------------------------------------------------------------------------------------

    private void Finish()
    {
        if (_draft is not { Count: > 0 } pts) { _draft = null; return; }
        string label = Store.FreeLabel();
        Do(PathEdits.Add(Store, label, pts));
        _draft = null;
        Select(Store.Groups().Last().First, -1);
        Ed.Toast($"New path {label}, {pts.Count} point{(pts.Count == 1 ? "" : "s")}: give it a label in the panel");
        GD.Print($"editor paths: new {label} ({pts.Count} points), table {Store.Count}");
    }

    private void InsertAt(PathGroup g, int segment, V2 at, bool drag)
    {
        Do(PathEdits.InsertPoint(Store, g, segment, at));
        Select(g.First, segment);
        if (drag) { _dragPt = segment; _dragTo = at; _dragMoved = false; }
        Ed.Toast($"Inserted a point in {g.Label}");
    }

    private void InsertNear(PathGroup g, V2 at)
    {
        if (PathEdits.Pick([g], at, 0, float.MaxValue) is { Segment: >= 0 } h) InsertAt(g, h.Segment, at, drag: false);
        else { Do(PathEdits.InsertPoint(Store, g, g.Points.Count, at)); Select(g.First, g.Points.Count); Ed.Toast($"Added a point at the end of {g.Label}"); }
    }

    private void DeletePoint(PathGroup g, int k)
    {
        if (PathEdits.DeletePoint(Store, g, k) is not { } cmd) { Ed.Toast("A path keeps at least one point (delete the path instead)", true); return; }
        Do(cmd);
        _selPt = -1;
        Ed.Toast($"Deleted point {k + 1} of {g.Label}");
    }

    private void AskDelete(PathGroup g)
    {
        if (Store.WhyNotDelete(g) is { } why) { Ed.Toast($"{g.Label}: {why}", true); return; }
        Ed.Confirm($"Delete the path {g.Label}?", () => DeletePath(g.First));
    }

    private void DeletePath(int first)
    {
        if (Store.GroupOf(first) is not { } g || PathEdits.Delete(Store, g) is not { } cmd) return;
        Do(cmd);
        _sel = -1; _selPt = -1;
        Ed.Toast($"Deleted the path {g.Label} (Ctrl+Z brings it back)");
    }

    /// <summary>The label field: renames the selected path, or says why not.</summary>
    private string? Rename(string label)
    {
        if (Selected is not { } g) return null;
        label = label.Trim();
        if (label == g.Label) return null;
        if (Store.LabelProblem(label, g) is { } problem) return problem;
        Do(PathEdits.Rename(Store, g, label));
        Ed.Toast($"Path {g.Label} is now {label}");
        return null;
    }

    // ---- frame --------------------------------------------------------------------------------------------------------------

    public void Process(double dt)
    {
        _ = dt;
        UpdatePanel();
        bool show = Active || ShowLayer;
        if (!show)
        {
            if (_shown.Version != -1) { _vis.Clear(); _shown = (-1, 0, 0, 0, 0, 0, false, default, -1); }
            return;
        }
        float scale = Scale;
        var cur = Active ? Cursor() : null;
        _hoverPt = -1; _hoverPath = -1; _hoverOn = null;
        if (Active && cur is { } c)
        {
            if (_dragPt >= 0)
            {
                if (V2.Distance(c, _pressAt) > 0.5f) _dragMoved = true;
                if (_dragMoved) _dragTo = c;
            }
            else if (Mode != PathMode.New && PickAt(c) is { } h)
            {
                _hoverPath = h.Path.First;
                if (h.Path.First == _sel) _hoverPt = h.Point;
                _hoverOn = h.OnLine;
            }
        }
        // the marker: where a click puts a point
        var marker = !Active || cur is null ? null : Mode switch
        {
            PathMode.New => cur,
            PathMode.Insert => _hoverOn is { } on && _hoverPt < 0 ? on : null,
            _ => null,
        };
        _vis.ShowMarker(marker, Mode == PathMode.New ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.6f, 0.15f), scale);
        _vis.ShowDraft(_draft, Mode == PathMode.New ? cur : null, scale);

        int sk = (int)MathF.Round(MathF.Log(scale) * 8);
        var key = (Store.Version, _sel, _selPt, _hoverPt, _hoverPath, sk, true, _dragMoved ? _dragTo : default, _draft?.Count ?? -1);
        if (key == _shown) return;
        _shown = key;
        var groups = Store.Groups();
        if (_dragPt >= 0 && _dragMoved && Selected is { } sel && _dragPt < sel.Points.Count)
        {
            // the dragged point where the cursor is, until the button comes up
            var pts = sel.Points.ToList();
            pts[_dragPt] = new System.Numerics.Vector3(_dragTo.X, pts[_dragPt].Y, _dragTo.Y);
            groups = [.. groups.Select(g => g.First == sel.First ? g with { Points = pts } : g)];
        }
        _vis.Show(groups, Active ? _sel : -1, _selPt, _hoverPt, _hoverPath, scale, labels: true);
        Hint = HintText();
    }

    private string HintText()
    {
        if (_draft is { } d) return $"{d.Count} point{(d.Count == 1 ? "" : "s")}: click on, Enter or right click finishes, Backspace takes one back, Esc cancels";
        if (_dragPt >= 0) return "drag the point; let go to place it";
        return Mode switch
        {
            PathMode.New => "click the first point of a new path",
            PathMode.Insert => "click on a line to put a point there",
            PathMode.Delete => "click a point to remove it, a line to delete its path",
            _ => Selected is { } g ? $"{g.Label}: drag a point, double-click the line to insert, Del removes {(_selPt >= 0 ? $"point {_selPt + 1}" : "the path")}" : "click a path to select it",
        };
    }

    public string StatusText(System.Numerics.Vector3? ground)
    {
        string g = ground is { } h ? $"ground {h.Y:0.0} m at {h.X:0}, {h.Z:0}" : "no ground";
        string mode = Mode switch { PathMode.New => "NEW PATH", PathMode.Insert => "INSERT POINT", PathMode.Delete => "DELETE", _ => "EDIT PATHS" };
        return $"{mode}    {Hint}    |    {g}";
    }
}
