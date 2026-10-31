using Godot;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Sim.Missions;
using I76.Formats.Assembly;
using I76.Formats.Mission.Edit;
using LineEdit = Godot.LineEdit;
using I76.Game.Sim;
using I76.Game.World;
using NVec3 = System.Numerics.Vector3;
using NVec2 = System.Numerics.Vector2;

namespace I76.Game.Editor.Objects;

public enum ObjectTool { None, Select, Place }

/// <summary>
/// Building, prop and marker placement in the map editor (docs/map-editor.md): a palette of what the game can place, a ghost that
/// follows the cursor, snapping to the roads like Cities: Skylines, selecting, moving, turning, copying and bulldozing, and the
/// markers (spawns, regen pads, race checkpoints, CTF flags) with the checks their map mode wants. The edits go to the
/// <see cref="ObjectLayer"/> (I76.Editor.Objects) as undoable commands; this node draws them, takes the input and builds the windows
/// (ObjectUi.cs).
/// </summary>
public partial class ObjectEditor : Node
{
    public required MapEditor Ed { get; init; }
    public required MissionBuilder Builder { get; init; }
    public required EditorCamera Cam { get; init; }
    public required TerrainDocument Doc { get; init; }
    public required UndoStack Undo { get; init; }
    public required ObjectLayer Layer { get; init; }
    public required Func<NVec3> Origin { get; init; }
    public required Func<Vector2, NVec3?> PickGround { get; init; }

    public ObjectTool Tool { get; private set; } = ObjectTool.None;
    public bool Active => Tool != ObjectTool.None;
    public SnapMode Snap { get; private set; } = SnapMode.Roads;
    public float Grid { get; set; } = 5;
    public bool Flatten { get; set; }
    public bool CutRoads { get; set; } = true;
    public CatalogueItem? Armed { get; private set; }
    public List<EditObject> Selection { get; } = [];
    public int Team { get; private set; }            // the team a spawn or a flag is placed for
    public bool Finish { get; private set; }         // the next checkpoint is the finish line

    private MissionWorld World => Builder.World!;
    private ScenerySpawner Scenery => Builder.Scenery!;
    private float _yaw;                              // the heading the user has turned the ghost to
    private NVec3? _hit;
    private Node3D? _ghost, _ghostLine, _markers, _route, _selNode, _hoverNode;
    private readonly List<(CatalogueItem Item, MapObject Data, NVec2 Offset, float Yaw)> _clip = [];
    private bool _pasting;
    private bool _markersDirty = true, _selDirty = true, _checksDirty = true;
    private int _checkedVersion = -1;
    private EditObject? _hover;
    private PlacementSnap? _pose;
    private PlaceProblem _problem;
    private readonly Dictionary<string, CatalogueItem> _byName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (NVec3 Min, NVec3 Max)?> Boxes = new(StringComparer.OrdinalIgnoreCase);

    public override void _Ready()
    {
        Layer.Changed += OnLayerChanged;
        if (Builder.Scenery is { } sc) sc.Markers.Visible = false;     // the editor draws its own
        _markers = new Node3D { Name = "EditorMarkers" };
        Scenery.AddChild(_markers);
        _route = new Node3D { Name = "EditorRoute" };
        Scenery.AddChild(_route);
        _selNode = new Node3D { Name = "EditorSelection" };
        Scenery.AddChild(_selNode);
        _hoverNode = new Node3D { Name = "EditorHover" };
        Scenery.AddChild(_hoverNode);
        Layer.Ground = (x, z) => (float)World.BilinearHeight(x, z);
        RebuildMarkers();
    }

    public override void _ExitTree()
    {
        Layer.Changed -= OnLayerChanged;
    }

    private void OnLayerChanged(LayerChange c)
    {
        if (!GodotObject.IsInstanceValid(Builder.Scenery)) return;
        switch (c.Kind)
        {
            case ChangeKind.Added: Scenery.EditShow(c.Item.Placement); break;
            case ChangeKind.Removed: Scenery.EditHide(c.Old!); Selection.Remove(c.Item); break;
            case ChangeKind.Changed:
                if (c.Old!.Definition == c.Item.Placement.Definition) Scenery.EditMove(c.Old, c.Item.Placement);
                else { Scenery.EditHide(c.Old); Scenery.EditShow(c.Item.Placement); }
                break;
        }
        _markersDirty = true; _selDirty = true; _checksDirty = true;
    }

    // ---- the catalogue ----------------------------------------------------------------------------------------------------

    /// <summary>The placeable things, built from the install's maps the first time (a second or two) and kept on disk.</summary>
    public List<CatalogueItem> Catalogue
    {
        get
        {
            if (EditorSession.Catalogue is { } c) return c;
            var install = Ed.Install!;
            string path = ProjectSettings.GlobalizePath($"user://cache/editor-catalogue-{ObjectCatalogue.CacheName(install)}.json");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = ObjectCatalogue.LoadOrScan(install, Builder.Assembler!, path, m => GD.Print(m));
            EditorSession.Catalogue = items;
            GD.Print($"object catalogue ready in {sw.ElapsedMilliseconds} ms: {string.Join(", ", items.GroupBy(i => i.Category).Select(g => $"{g.Key} {g.Count()}"))}");
            foreach (var i in items) _byName[i.Name] = i;
            return items;
        }
    }

    public CatalogueItem? ItemOf(string name)
    {
        if (_byName.Count == 0 && EditorSession.Catalogue is { } c) foreach (var i in c) _byName[i.Name] = i;
        return _byName.GetValueOrDefault(name.TrimStart('#').ToLowerInvariant());
    }

    public CatalogueItem MarkerItem(MarkerKind kind, int team = 0) =>
        Catalogue.FirstOrDefault(i => i.Marker == kind && (kind != MarkerKind.Flag || i.Name == $"a1flag{Math.Clamp(team, 1, 4)}")) ?? ObjectCatalogue.MarkerItems().First(i => i.Marker == kind);

    /// <summary>An object's box in its own frame (game axes): the catalogue's for what it knows, else measured from the model; markers have fixed ones.</summary>
    public (NVec3 Min, NVec3 Max) BoxOf(EditObject e)
    {
        switch (e.Role)
        {
            case MapRole.Spawn: return (new(-1.6f, 0, -2.6f), new(1.6f, 2.5f, 2.6f));
            case MapRole.Regen: return (new(-3.2f, 0, -3.2f), new(3.2f, 1.5f, 3.2f));
            case MapRole.Checkpoint: return (new(-1.2f, 0, -4.6f), new(1.2f, 9f, 4.6f));
        }
        string key = e.Placement.Definition ?? e.Name;
        if (Boxes.TryGetValue(key, out var b) && b is { } known) return known;
        (NVec3, NVec3)? box = null;
        if (ItemOf(e.Name) is { Area: > 0 } it) box = (it.Min, it.Max);
        else if (e.Placement.Definition is { } def && Builder.Assembler!.Assemble(def) is { Parts.Count: > 0 } m)
        {
            var mn = new NVec3(float.MaxValue); var mx = new NVec3(float.MinValue);
            foreach (var p in m.Parts) foreach (var v in p.Mesh.Vertices) { var t = p.ToModel.Apply(v); mn = NVec3.Min(mn, t); mx = NVec3.Max(mx, t); }
            box = (mn, mx);
        }
        Boxes[key] = box;
        return box ?? (new(-1.5f, 0, -1.5f), new(1.5f, 3f, 1.5f));
    }

    private Footprint FootprintOf(EditObject e)
    {
        var (mn, mx) = BoxOf(e);
        var p = e.Position;
        return new Footprint(new NVec2(p.X, p.Z), e.Yaw, new NVec2(mn.X, mn.Z), new NVec2(mx.X, mx.Z));
    }

    // ---- tools ------------------------------------------------------------------------------------------------------------

    public void SetTool(ObjectTool tool)
    {
        if (tool == Tool && tool != ObjectTool.Place) return;
        Tool = tool;
        if (tool != ObjectTool.Place) Disarm();
        if (tool == ObjectTool.None) { Selection.Clear(); _selDirty = true; }
        ToolChanged();
    }

    public void Deactivate() { if (Active) SetTool(ObjectTool.None); }

    /// <summary>Arms an item: the ghost follows the cursor and a click places it.</summary>
    public void Arm(CatalogueItem item)
    {
        EnsureEnds(item);
        Tool = ObjectTool.Place;
        Armed = item;
        _pasting = false;
        if (item.Marker == MarkerKind.Flag && Team is < 1 or > 4) Team = 1;
        if (item.Marker == MarkerKind.Flag) Armed = MarkerItem(MarkerKind.Flag, Team);
        ClearGhost();
        MakeGhost();
        Selection.Clear(); _selDirty = true;
        ToolChanged();
    }

    /// <summary>
    /// The ends of a structure's drive-on surface in its own frame, from the sim's collider for it (the same one a mission builds), by
    /// <see cref="StructureAttach.Ends"/>: where its road ends are. Done once per item.
    /// </summary>
    public void EnsureEnds(CatalogueItem item)
    {
        if (!item.CarriesRoad || item.IsTunnel || item.Ends is not null || item.Definition.Length == 0) return;   // a tunnel has no deck: its portals come from the catalogue
        try
        {
            uint type = item.Type is 0xb or 0xc or 0xd ? item.Type : 0xbu;
            var so = MissionSetup.StructureCollider(Builder.Vfs, Builder.Assembler!, item.Name, item.Definition, type, [1, 0, 0, 0, 1, 0, 0, 0, 1], default);
            var ends = so is null ? [] : StructureAttach.Ends(so.Surfaces.Select(p => p.V));
            if (ends.Count == 2) { item.Ends = ends; GD.Print($"editor objects: {item.Name} ends {string.Join(" | ", ends.Select(e => $"({e.Position.X:0.0},{e.Position.Y:0.0},{e.Position.Z:0.0}) out ({e.Direction.X:0.##},{e.Direction.Y:0.##}) width {e.Width:0.#} {(e.Low ? "low" : "high")}{(e.RoadLevel ? " road level" : "")}"))}"); }
            else GD.Print($"editor objects: {item.Name}: no road ends found, snapping by its box");
        }
        catch (Exception ex) { GD.PrintErr($"editor objects: {item.Name} ends: {ex.Message}"); }
    }

    public void Disarm()
    {
        Armed = null; _pasting = false;
        ClearGhost();
        ToolChanged();
    }

    private void ClearGhost()
    {
        _ghost?.QueueFree(); _ghost = null;
        _ghostLine?.QueueFree(); _ghostLine = null;
        _pose = null;
    }

    private Node3D? MakeModelNode(CatalogueItem item, bool forGhost, int index = 0)
    {
        switch (item.Marker)
        {
            case MarkerKind.Spawn: return MarkerView.Spawn(forGhost ? "SPAWN" : "SPAWN", MarkerView.SpawnGreen);
            case MarkerKind.Regen: return MarkerView.Regen(MarkerView.RegenOrange);
            case MarkerKind.Checkpoint: return MarkerView.Checkpoint(forGhost ? NextCheckNumber() : 1, showRing: forGhost);
        }
        if (item.Definition.Length == 0 || Scenery.EditMeshOf(item.Definition) is not { } m) return null;
        return new MeshInstance3D { Mesh = m.Mesh, Name = item.Name };
    }

    private int NextCheckNumber() => Finish ? 99 : Layer.NextCheckpointNumber();

    private StandardMaterial3D _ghostMat = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.4f, 1f, 0.45f, 0.62f), EmissionEnabled = true, Emission = new Color(0.1f, 0.35f, 0.12f),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private void MakeGhost()
    {
        if (Armed is null) return;
        var root = new Node3D { Name = "Ghost" };
        if (_pasting)
        {
            foreach (var (item, data, off, y) in _clip)
                if (MakeModelNode(item, true) is { } n)
                {
                    TintGhost(n);
                    var holder = new Node3D { Name = item.Name }; holder.AddChild(n);
                    root.AddChild(holder);
                }
        }
        else if (MakeModelNode(Armed, true) is { } node) { TintGhost(node); root.AddChild(node); }
        Scenery.AddChild(root);
        _ghost = root;
    }

    private void TintGhost(Node n) { if (n is MeshInstance3D mi) mi.MaterialOverride = _ghostMat; }

    private void ToolChanged()
    {
        _selDirty = true;
        UiToolChanged();
        Ed.ObjectToolChanged();
    }

    // ---- the frame --------------------------------------------------------------------------------------------------------

    public void Frame(double delta, NVec3? hit, bool overUi)
    {
        if (_markersDirty) RebuildMarkers();
        if (!Active)
        {
            if (_selNode!.GetChildCount() > 0) ClearChildren(_selNode);
            if (_hoverNode!.GetChildCount() > 0) ClearChildren(_hoverNode);
            return;
        }
        _hit = overUi ? null : hit;
        if (_checksDirty || _checkedVersion != Layer.Version) { RefreshChecks(); _checkedVersion = Layer.Version; _checksDirty = false; }
        if (Tool == ObjectTool.Place) UpdateGhost();
        else UpdateSelectFrame();
        if (_selDirty) RebuildSelection();
        UiFrame();
    }

    private static void ClearChildren(Node n) { foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); } }

    private SnapMode EffectiveSnap => Input.IsKeyPressed(Key.Alt) ? SnapMode.Off : Snap;

    private void UpdateGhost()
    {
        if (Armed is null || _ghost is null) return;
        if (_hit is not { } h) { _ghost.Visible = false; _pose = null; ClearLine(); return; }
        _ghost.Visible = true;
        var cursor = new NVec2(h.X, h.Z);
        if (_pasting) { UpdatePasteGhost(cursor); return; }
        var res = Snapping.Place(Armed, cursor, _yaw, EffectiveSnap, Grid, Layer.Polylines);
        _pose = res;
        var pos = new NVec3(res.Origin.X, Ground(res.Origin.X, res.Origin.Y), res.Origin.Y);
        _problem = Check(Armed, res);
        _deckNote = DeckNote(Armed, res, pos.Y);
        SetGhostPose(_ghost, pos, res.Yaw, _problem);
        DrawLine(Armed, res, pos);
    }

    private float Ground(float x, float z) => (float)World.BilinearHeight(x, z);

    private string _deckNote = "";

    /// <summary>For a structure on a road: how far its road-level ends stand from the ground there (the game stands its origin on the ground, so a slope leaves a step at an end); empty when they meet.</summary>
    private string DeckNote(CatalogueItem item, PlacementSnap res, float originY)
    {
        if (!item.CarriesRoad || !res.OnRoad) return "";
        if (item.IsTunnel)
        {
            if (res.Deck is not { } tube) return "";
            float gap = DeckCover.Gap(tube, Layer.Polylines);
            return gap < 6 ? "" : $"the road does not run through the whole tunnel ({gap:0} m of it have none): draw it on, the tunnel leaves the road as it is";
        }
        float worst = 0;
        foreach (var e in item.DeckEnds())
        {
            if (!e.RoadLevel) continue;
            var (at, _) = Snapping.EndAt(e, res.Origin, res.Yaw);
            float d = originY + e.Position.Y - Ground(at.X, at.Y);
            if (MathF.Abs(d) > MathF.Abs(worst)) worst = d;
        }
        return MathF.Abs(worst) < 0.6f ? "" : $"deck {MathF.Abs(worst):0.0} m {(worst > 0 ? "above" : "below")} the road at its end (L levels the ground)";
    }

    private PlaceProblem Check(CatalogueItem item, PlacementSnap res)
    {
        var f = res.Footprint(item);
        var others = Layer.Items.Where(o => NVec2.DistanceSquared(new NVec2(o.Position.X, o.Position.Z), f.Centre) < (f.Radius + 140) * (f.Radius + 140) && o.Role is MapRole.Object or MapRole.Flag && !o.Locked).Select(FootprintOf);
        return PlaceRules.Check(item, f, others, Ground, (x, z) => Doc.Has((int)MathF.Floor(x * (float)MissionWorld.SampleScale), (int)MathF.Floor(z * (float)MissionWorld.SampleScale)),
            EffectiveSnap == SnapMode.Off ? null : Layer.Polylines);
    }

    private void SetGhostPose(Node3D ghost, NVec3 pos, float yaw, PlaceProblem problem)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        ghost.Transform = Coords.ToGodot([c, 0, -s, 0, 1, 0, s, 0, c], pos);
        TintFor(problem);
    }

    private void TintFor(PlaceProblem problem)
    {
        bool bad = problem != PlaceProblem.None;
        _ghostMat.AlbedoColor = bad ? new Color(1f, 0.25f, 0.2f, 0.66f) : new Color(0.4f, 1f, 0.45f, 0.62f);
        _ghostMat.Emission = bad ? new Color(0.45f, 0.06f, 0.04f) : new Color(0.1f, 0.35f, 0.12f);
    }

    private void ClearLine() { _ghostLine?.QueueFree(); _ghostLine = null; }

    /// <summary>The footprint outline on the ground (red when not allowed) and, snapped to a road, the line to the road.</summary>
    private void DrawLine(CatalogueItem item, PlacementSnap res, NVec3 pos)
    {
        ClearLine();
        if (item.Area <= 0 || item.Marker is MarkerKind.Spawn or MarkerKind.Regen or MarkerKind.Checkpoint) return;
        var f = res.Footprint(item);
        var colour = _problem == PlaceProblem.None ? (res.OnRoad ? new Color(1f, 0.82f, 0.2f) : new Color(0.5f, 1f, 0.5f)) : new Color(1f, 0.3f, 0.25f);
        var corners = f.Corners().Select(p => Coords.ToGodot(new NVec3(p.X, Ground(p.X, p.Y) + 0.4f, p.Y))).ToArray();
        _ghostLine = MarkerView.Outline(corners, colour);
        Scenery.AddChild(_ghostLine);
    }

    // ---- selecting ---------------------------------------------------------------------------------------------------------

    private void UpdateSelectFrame()
    {
        var mouse = Mouse();
        // dragging
        if (_drag != DragKind.None && ButtonDown()) DragStep(mouse);
        else if (_drag != DragKind.None) EndDrag(mouse);
        var h = _drag == DragKind.None && _hit is not null ? PickObject(mouse) : null;
        if (h != _hover) { _hover = h; _hoverDirty = true; }
        if (_hoverDirty)
        {
            _hoverDirty = false;
            ClearChildren(_hoverNode!);
            if (_hover is { } hv && !Selection.Contains(hv)) AddBox(_hoverNode!, hv, MarkerView.Hover);
        }
    }

    private bool _hoverDirty;
    private Vector2? _scriptMouse;
    private bool _scriptDown;
    private Vector2 Mouse() => _scriptMouse ?? GetViewport().GetMousePosition();
    private bool ButtonDown() => _scriptMouse is not null ? _scriptDown : Input.IsMouseButtonPressed(MouseButton.Left);

    /// <summary>The object under a screen point: the nearest whose box the camera's ray goes through.</summary>
    public EditObject? PickObject(Vector2 screen)
    {
        var o = Cam.ProjectRayOrigin(screen);
        var d = Cam.ProjectRayNormal(screen);
        var og = Origin();
        var ro = new NVec3(o.X + og.X, o.Y + og.Y, -o.Z + og.Z);
        var rd = new NVec3(d.X, d.Y, -d.Z);
        EditObject? best = null; float bestT = float.MaxValue;
        foreach (var e in Layer.Items)
        {
            if (e.Locked) continue;
            var p = e.Position;
            // far from the ray: skip before the box (the ray's closest approach to the object)
            var rel = new NVec3(p.X, Ground(p.X, p.Z), p.Z) - ro;
            float along = NVec3.Dot(rel, rd);
            if (along < 0) continue;
            var (mn, mx) = BoxOf(e);
            float reach = Math.Max(NVec3.Distance(mn, mx), 6) + 4;
            if ((rel - rd * along).LengthSquared() > reach * reach) continue;
            if (RayBox(e, ro, rd, mn, mx) is { } t && t < bestT) { bestT = t; best = e; }
        }
        return best;
    }

    private float? RayBox(EditObject e, NVec3 ro, NVec3 rd, NVec3 mn, NVec3 mx)
    {
        var p = e.Position; var pos = new NVec3(p.X, Ground(p.X, p.Z), p.Z);
        var r = e.Data.Rotation;
        NVec3 Local(NVec3 v) => new(v.X * r[0] + v.Y * r[1] + v.Z * r[2], v.X * r[3] + v.Y * r[4] + v.Z * r[5], v.X * r[6] + v.Y * r[7] + v.Z * r[8]);
        var lo = Local(ro - pos); var ld = Local(rd);
        var pad = new NVec3(0.6f);
        var a = mn - pad; var b = mx + pad;
        float t0 = 0, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? lo.X : i == 1 ? lo.Y : lo.Z, dd = i == 0 ? ld.X : i == 1 ? ld.Y : ld.Z;
            float lo_ = i == 0 ? a.X : i == 1 ? a.Y : a.Z, hi_ = i == 0 ? b.X : i == 1 ? b.Y : b.Z;
            if (MathF.Abs(dd) < 1e-8f) { if (o < lo_ || o > hi_) return null; continue; }
            float ta = (lo_ - o) / dd, tb = (hi_ - o) / dd;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return null;
        }
        return t0;
    }

    private void AddBox(Node3D parent, EditObject e, Color colour)
    {
        var (mn, mx) = BoxOf(e);
        var box = MarkerView.WireBox(new Vector3(mn.X, mn.Y, -mx.Z), new Vector3(mx.X, mx.Y, -mn.Z), colour);
        var p = e.Position;
        box.Transform = Coords.ToGodot(e.Data.Rotation, new NVec3(p.X, Ground(p.X, p.Z), p.Z));
        parent.AddChild(box);
    }

    private void RebuildSelection()
    {
        _selDirty = false;
        ClearChildren(_selNode!);
        foreach (var e in Selection.Take(300)) AddBox(_selNode!, e, MarkerView.Select);
        _hoverDirty = true;
        UiSelectionChanged();
    }

    public void SelectOnly(EditObject? e)
    {
        Selection.Clear();
        if (e is not null) Selection.Add(e);
        _selDirty = true;
    }

    // ---- dragging: move, copy, box select -----------------------------------------------------------------------------------

    private enum DragKind { None, PendingMove, Move, Box }
    private DragKind _drag;
    private Vector2 _dragStart;
    private NVec3 _dragGround;
    private bool _dragAlt, _duplicated;
    private List<(EditObject Item, Pose Before)> _dragFrom = [];
    private List<EditObject> _copies = [];

    private void BeginDrag(Vector2 mouse, bool shift, bool alt)
    {
        var hit = PickObject(mouse);
        _dragStart = mouse; _dragAlt = alt; _duplicated = false;
        if (hit is not null)
        {
            if (shift)
            {
                if (!Selection.Remove(hit)) Selection.Add(hit);
                _selDirty = true;
                return;
            }
            if (!Selection.Contains(hit)) SelectOnly(hit);
            _dragGround = _hit ?? hit.Position;
            _drag = DragKind.PendingMove;
            _dragFrom = [.. Selection.Select(e => (e, new Pose(e.Position, e.Yaw)))];
        }
        else
        {
            _shiftBox = shift;
            _drag = DragKind.Box;
        }
    }

    private bool _shiftBox;

    private void DragStep(Vector2 mouse)
    {
        if (_drag == DragKind.PendingMove)
        {
            if (mouse.DistanceTo(_dragStart) < 6) return;
            _drag = DragKind.Move;
            if (_dragAlt) Duplicate();
        }
        if (_drag == DragKind.Box) { UiBox(_dragStart, mouse); return; }
        if (_drag != DragKind.Move || _hit is not { } g) return;
        var delta = new NVec2(g.X - _dragGround.X, g.Z - _dragGround.Z);
        if (_dragFrom.Count == 1)
        {
            var (e, before) = _dragFrom[0];
            var item = ItemOf(e.Name);
            var snapMode = EffectiveSnap;
            if (item is { Area: > 0 } && e.Role == MapRole.Object)
            {
                var fp = Footprint.Of(item, before.Position, before.Yaw);
                var res = Snapping.Place(item, fp.Centre + delta, before.Yaw, snapMode, Grid, Layer.Polylines);
                Layer.SetPose(e, new NVec3(res.Origin.X, 0, res.Origin.Y), res.OnRoad ? res.Yaw : before.Yaw);
            }
            else
            {
                var p = before.Position;
                var to = Snapping.SnapGrid(new NVec2(p.X + delta.X, p.Z + delta.Y), snapMode == SnapMode.Off ? 0 : Grid);
                Layer.SetPose(e, new NVec3(to.X, 0, to.Y), before.Yaw);
            }
        }
        else
        {
            var d = EffectiveSnap == SnapMode.Off ? delta : Snapping.SnapGrid(delta, Grid);
            foreach (var (e, before) in _dragFrom) Layer.SetPose(e, new NVec3(before.Position.X + d.X, 0, before.Position.Z + d.Y), before.Yaw);
        }
    }

    /// <summary>Alt + drag: the selection stays where it was and copies are dragged away.</summary>
    private void Duplicate()
    {
        _duplicated = true;
        _copies = [];
        var from = new List<(EditObject, Pose)>();
        foreach (var (e, pose) in _dragFrom)
        {
            var copy = Layer.Add(e.Data.Clone());
            _copies.Add(copy);
            from.Add((copy, pose));
        }
        _dragFrom = from;
        Selection.Clear(); Selection.AddRange(_copies);
        _selDirty = true;
    }

    private void EndDrag(Vector2 mouse)
    {
        var kind = _drag;
        _drag = DragKind.None;
        UiBox(null, mouse);
        if (kind == DragKind.Box)
        {
            if (mouse.DistanceTo(_dragStart) < 5) { if (!_shiftBox) { Selection.Clear(); _selDirty = true; } return; }
            BoxSelect(_dragStart, mouse, _shiftBox);
            return;
        }
        if (kind != DragKind.Move) return;
        var moves = _dragFrom.Where(m => m.Item.Position != m.Before.Position || MathF.Abs(Snapping.Wrap(m.Item.Yaw - m.Before.Yaw)) > 1e-5f)
            .Select(m => (m.Item, m.Before, new Pose(m.Item.Position, m.Item.Yaw))).ToList();
        GD.Print($"editor objects: moved {moves.Count} object(s){(_duplicated ? " (copies)" : "")}, {Layer.Items.Count} objects");
        if (_duplicated)
        {
            var parts = new List<IEditCommand> { new ObjectCommands.Place(Layer, _copies, "Copy") };
            if (moves.Count > 0) parts.Add(new ObjectCommands.Transform(Layer, moves));
            Undo.Push(new ObjectCommands.Group($"Copy {_copies.Count} object{(_copies.Count == 1 ? "" : "s")}", parts));
        }
        else if (moves.Count > 0) Undo.Push(new ObjectCommands.Transform(Layer, moves, moves.Count == 1 ? $"Move {moves[0].Item.Name}" : $"Move {moves.Count} objects"));
        _duplicated = false;
    }

    private Vector2 ScreenOf(EditObject e)
    {
        var (mn, mx) = BoxOf(e);
        var c = (mn + mx) / 2;
        var r = e.Data.Rotation;
        var p = e.Position;
        var w = new NVec3(p.X + c.X * r[0] + c.Y * r[3] + c.Z * r[6], Ground(p.X, p.Z) + c.X * r[1] + c.Y * r[4] + c.Z * r[7], p.Z + c.X * r[2] + c.Y * r[5] + c.Z * r[8]);
        var og = Origin();
        var g = new Vector3(w.X - og.X, w.Y - og.Y, -(w.Z - og.Z));
        return Cam.IsPositionBehind(g) ? new Vector2(-1e6f, -1e6f) : Cam.UnprojectPosition(g);
    }

    private void BoxSelect(Vector2 a, Vector2 b, bool add)
    {
        var rect = new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), (a - b).Abs());
        if (!add) Selection.Clear();
        foreach (var e in Layer.Items)
            if (!e.Locked && rect.HasPoint(ScreenOf(e)) && !Selection.Contains(e)) Selection.Add(e);
        _selDirty = true;
        Ed.Toast($"{Selection.Count} selected");
    }

    // ---- placing -----------------------------------------------------------------------------------------------------------

    private void PlaceHere(bool keep)
    {
        if (Armed is null) return;
        if (_pasting) { PastePlace(keep); return; }
        if (_pose is not { } res || _hit is null) return;
        if (_problem != PlaceProblem.None && !Input.IsKeyPressed(Key.Ctrl)) { Ed.Toast(ProblemText(_problem) + "  (Ctrl places anyway)", true); return; }
        PlaceItem(Armed, res);
        if (!keep) { if (Armed.Marker == MarkerKind.None || Armed.Marker == MarkerKind.Flag) Disarm(); }
        else MakeGhostRefresh();
    }

    private void MakeGhostRefresh()
    {
        if (Armed is { Marker: MarkerKind.Checkpoint }) { ClearGhost(); MakeGhost(); }
    }

    public static string ProblemText(PlaceProblem p) => p switch
    {
        PlaceProblem.OverlapsObject => "It overlaps another object",
        PlaceProblem.TooSteep => "The ground is too steep there",
        PlaceProblem.OnRoad => "It is on a road",
        PlaceProblem.OffMap => "It is off the map",
        _ => "",
    };

    /// <summary>Makes the object (and the flattening and road cut it asks for) and records it as one undoable step.</summary>
    public EditObject PlaceItem(CatalogueItem item, PlacementSnap res)
    {
        var parts = new List<IEditCommand>();
        var fp = res.Footprint(item);
        bool solid = item.Marker == MarkerKind.None && item.Area >= 25 && item.Category is ObjectCategory.Buildings or ObjectCategory.Structures or ObjectCategory.Props;
        // a tunnel's mountain stands on ground levelled under all of it, as the stock maps have it (B01, P18)
        if ((Flatten || item.IsTunnel) && solid && Leveling.Flatten(Doc, fp, Ground) is { } stroke) parts.Add(stroke);
        if (CutRoads && res.Deck is { } deck && item.CutsRoad)
        {
            var ops = RoadSplit.Ops(Layer.Roads.Roads, deck, out int cuts);
            if (cuts > 0) parts.Add(new RoadCommand($"Cut {cuts} road{(cuts == 1 ? "" : "s")}", Layer.Roads, ops).Apply());
        }
        var data = Make(item, new NVec3(res.Origin.X, Ground(res.Origin.X, res.Origin.Y), res.Origin.Y), res.Yaw);
        var e = Layer.Add(data);
        parts.Add(new ObjectCommands.Place(Layer, [e], "Place"));
        Undo.Push(parts.Count == 1 ? new ObjectCommands.Place(Layer, [e], $"Place {item.Label.ToLowerInvariant()}") : new ObjectCommands.Group($"Place {item.Label.ToLowerInvariant()}", parts));
        GD.Print($"editor objects: placed {item.Name} at {res.Origin.X:0.0}, {res.Origin.Y:0.0} heading {res.Yaw * 180 / MathF.PI:0}{(res.OnRoad ? " (on a road)" : "")}, {Layer.Items.Count} objects, {parts.Count} step(s): {string.Join(", ", parts.Select(p => p.Name))}");
        Ed.Toast($"{item.Label} placed{(parts.Count > 1 ? " (" + string.Join(", ", parts.Take(parts.Count - 1).Select(p => p.Name.ToLowerInvariant())) + ")" : "")}");
        return e;
    }

    /// <summary>The record for a placement of an item (marker names and numbers as the loaders read them).</summary>
    private MapObject Make(CatalogueItem item, NVec3 pos, float yaw)
    {
        switch (item.Marker)
        {
            case MarkerKind.Spawn: return MapObject.Spawn(pos, yaw, Team);
            case MarkerKind.Regen: return MapObject.Regen(pos, yaw);
            case MarkerKind.Checkpoint: return MapObject.Checkpoint(NextCheckNumber(), pos, yaw);
            case MarkerKind.Flag: return MapObject.Flag(Math.Clamp(Team, 1, 4), pos, yaw);
        }
        return MapObject.Create(item.Name, item.Type, pos, yaw);
    }

    // ---- bulldoze, rotate, copy and paste -----------------------------------------------------------------------------------

    public void Bulldoze()
    {
        if (Selection.Count == 0) return;
        var gone = Selection.ToList();
        string what = gone.Count == 1 ? gone[0].Name : $"{gone.Count} objects";
        Undo.Push(new ObjectCommands.Remove(Layer, gone, $"Bulldoze {what}"));
        Selection.Clear(); _selDirty = true;
        GD.Print($"editor objects: bulldozed {what}, {Layer.Items.Count} left");
        Ed.Toast($"Bulldozed {what}");
    }

    public void RotateSelection(float delta)
    {
        if (Selection.Count == 0) return;
        float cx = Selection.Average(e => e.Position.X), cz = Selection.Average(e => e.Position.Z);
        float c = MathF.Cos(delta), s = MathF.Sin(delta);
        var moves = new List<(EditObject, Pose, Pose)>();
        foreach (var e in Selection)
        {
            var p = e.Position; float dx = p.X - cx, dz = p.Z - cz;
            var np = Selection.Count == 1 ? p : new NVec3(cx + dx * c + dz * s, p.Y, cz - dx * s + dz * c);
            moves.Add((e, new Pose(p, e.Yaw), new Pose(np, Snapping.Wrap(e.Yaw + delta))));
        }
        foreach (var (e, _, a) in moves) Layer.SetPose(e, a.Position, a.Yaw);
        Undo.Push(new ObjectCommands.Transform(Layer, [.. moves.Select(m => (m.Item1, m.Item2, new Pose(m.Item1.Position, m.Item1.Yaw)))], "Rotate"));
    }

    public void Copy()
    {
        if (Selection.Count == 0) { Ed.Toast("Nothing selected to copy"); return; }
        _clip.Clear();
        float cx = Selection.Average(e => e.Position.X), cz = Selection.Average(e => e.Position.Z);
        foreach (var e in Selection)
        {
            var item = ItemOf(e.Name) ?? ItemFor(e);
            _clip.Add((item, e.Data.Clone(), new NVec2(e.Position.X - cx, e.Position.Z - cz), e.Yaw));
        }
        Ed.Toast($"Copied {_clip.Count} object{(_clip.Count == 1 ? "" : "s")}");
    }

    private CatalogueItem ItemFor(EditObject e) => e.Role switch
    {
        MapRole.Spawn => MarkerItem(MarkerKind.Spawn),
        MapRole.Regen => MarkerItem(MarkerKind.Regen),
        MapRole.Checkpoint => MarkerItem(MarkerKind.Checkpoint),
        MapRole.Flag => MarkerItem(MarkerKind.Flag, e.Data.Number),
        _ => new CatalogueItem { Name = e.Name, Type = e.Data.ObjectType, Definition = e.Placement.Definition ?? "", Category = ObjectCategory.Props },
    };

    public void Paste()
    {
        if (_clip.Count == 0) { Ed.Toast("Nothing copied"); return; }
        Tool = ObjectTool.Place;
        _pasting = true;
        Armed = _clip[0].Item;
        _yaw = 0;
        ClearGhost(); MakeGhost();
        ToolChanged();
    }

    private void UpdatePasteGhost(NVec2 cursor)
    {
        var at = Snapping.SnapGrid(cursor, EffectiveSnap == SnapMode.Off ? 0 : Grid);
        float c = MathF.Cos(_yaw), s = MathF.Sin(_yaw);
        int i = 0;
        foreach (var child in _ghost!.GetChildren())
        {
            var (item, data, off, y) = _clip[i++];
            float x = at.X + off.X * c + off.Y * s, z = at.Y + (-off.X * s + off.Y * c);
            var holder = (Node3D)child;
            float yy = y + _yaw, cc = MathF.Cos(yy), ss = MathF.Sin(yy);
            holder.Transform = Coords.ToGodot([cc, 0, -ss, 0, 1, 0, ss, 0, cc], new NVec3(x, Ground(x, z), z));
        }
        _problem = PlaceProblem.None;
        _pose = new PlacementSnap(at, _yaw, false, null, null);
        TintFor(_problem);
        ClearLine();
    }

    private void PastePlace(bool keep)
    {
        if (_pose is not { } res) return;
        float c = MathF.Cos(_yaw), s = MathF.Sin(_yaw);
        var added = new List<EditObject>();
        foreach (var (item, data, off, y) in _clip)
        {
            float x = res.Origin.X + off.X * c + off.Y * s, z = res.Origin.Y + (-off.X * s + off.Y * c);
            var copy = data.Clone();
            copy.Position = new NVec3(x, Ground(x, z), z);
            copy.Yaw = Snapping.Wrap(y + _yaw);
            // a pasted checkpoint takes the next number, a pasted flag keeps its team
            if (copy.Role == MapRole.Checkpoint) { int n = Layer.NextCheckpointNumber(); copy.Name = $"check{n}"; }
            added.Add(Layer.Add(copy));
        }
        Undo.Push(new ObjectCommands.Place(Layer, added, $"Paste {added.Count} object{(added.Count == 1 ? "" : "s")}"));
        Selection.Clear(); Selection.AddRange(added); _selDirty = true;
        Ed.Toast($"Pasted {added.Count}");
        if (!keep) { Tool = ObjectTool.Select; Disarm(); }
    }

    // ---- editing a record (the inspector) ------------------------------------------------------------------------------------

    /// <summary>Changes one object's record and records the change.</summary>
    public void EditRecord(EditObject e, Action<MapObject> change, string name)
    {
        var before = (byte[])e.Data.Payload.Clone();
        var scratch = e.Data.Clone();
        try { change(scratch); }
        catch (ArgumentException ex) { Ed.Toast(ex.Message, true); return; }
        var after = (byte[])scratch.Payload.Clone();
        if (before.AsSpan().SequenceEqual(after)) return;
        Layer.SetData(e, after);
        Undo.Push(new ObjectCommands.Edit(Layer, e, before, after, name));
    }

    /// <summary>Moves a checkpoint earlier or later in the lap by swapping numbers with its neighbour.</summary>
    public void MoveCheckpoint(EditObject e, int dir)
    {
        var order = Layer.Checkpoints.Where(c => c.Data.Number < 99).ToList();
        int i = order.IndexOf(e);
        if (i < 0 || i + dir < 0 || i + dir >= order.Count) return;
        var other = order[i + dir];
        int a = e.Data.Number, b = other.Data.Number;
        var edits = new List<(EditObject, byte[], byte[])>();
        foreach (var (obj, n) in new[] { (e, b), (other, a) })
        {
            var before = (byte[])obj.Data.Payload.Clone();
            var scratch = obj.Data.Clone(); scratch.Name = $"check{n}";
            edits.Add((obj, before, (byte[])scratch.Payload.Clone()));
        }
        foreach (var (obj, _, after) in edits) Layer.SetData(obj, after);
        Undo.Push(new ObjectCommands.EditMany(Layer, edits, $"Reorder checkpoint {a}"));
    }

    // ---- the markers' drawing ------------------------------------------------------------------------------------------------

    public void RebuildMarkers()
    {
        _markersDirty = false;
        if (_markers is null || !GodotObject.IsInstanceValid(_markers)) return;
        ClearChildren(_markers);
        ClearChildren(_route!);
        int spawnIndex = 0, regenIndex = 0;
        var stops = new List<(Vector3 At, int Number)>();
        bool ctf = ModeNow.Mode == MapMode.Ctf;
        foreach (var e in Layer.Items)
        {
            var p = e.Position;
            var pos = new NVec3(p.X, Ground(p.X, p.Z), p.Z);
            Node3D? n = null;
            switch (e.Role)
            {
                case MapRole.Spawn:
                    spawnIndex++;
                    int team = e.Data.Number;
                    n = MarkerView.Spawn(ctf ? $"T{(team > 0 ? team : 1)}  {spawnIndex}" : spawnIndex.ToString(), ctf ? MarkerView.TeamColour(team > 0 ? team : 1) : MarkerView.SpawnGreen);
                    break;
                case MapRole.Regen: regenIndex++; n = MarkerView.Regen(MarkerView.RegenOrange); break;
                case MapRole.Checkpoint:
                    n = MarkerView.Checkpoint(e.Data.Number);
                    break;
                case MapRole.Flag: n = MarkerView.FlagBase(e.Data.Number); break;
            }
            if (n is null) continue;
            n.Transform = Coords.ToGodot(e.Data.Rotation, pos);
            if (e.Role == MapRole.Checkpoint) { n.Transform = new Transform3D(Basis.Identity, Coords.ToGodot(pos)); stops.Add((Coords.ToGodot(pos + new NVec3(0, 3, 0)), e.Data.Number)); }
            _markers.AddChild(n);
        }
        if (stops.Count >= 2)
        {
            stops.Sort((a, b) => a.Number.CompareTo(b.Number));
            _route!.AddChild(MarkerView.Route(stops));
        }
    }

    // ---- checks -------------------------------------------------------------------------------------------------------------

    public List<Issue> Issues { get; private set; } = [];

    /// <summary>The map check as it stands (for a save).</summary>
    public List<Issue> CheckNow() { RefreshChecks(); _checkedVersion = Layer.Version; _checksDirty = false; return Issues; }

    private void RefreshChecks()
    {
        string ext = Doc.SourcePath is { } sp ? System.IO.Path.GetExtension(sp) : ".cbt";
        Issues = MapChecks.Validate(Layer.Items.Select(i => i.Data), ext);
        UiChecksChanged();
    }

    /// <summary>The current mode of the map (from its file extension).</summary>
    public (MapMode Mode, int Teams) ModeNow => MapChecks.ModeOf(Doc.SourcePath is { } sp ? System.IO.Path.GetExtension(sp) : ".cbt");
}
