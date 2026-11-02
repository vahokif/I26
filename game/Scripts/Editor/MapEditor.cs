using Godot;
using I76.Editor;
using I76.Game.Editor.Objects;
using I76.Formats.Assembly;
using I76.Game.Sim;
using I76.Game.Editor.RoadTools;
using I76.Game.World;

namespace I76.Game.Editor;

/// <summary>
/// The map editor (docs/map-editor.md): the mission's world with an editor camera, the terrain brushes, undo and redo, the
/// playtest, and the tool windows (MapEditorUi.cs). Edits go to the <see cref="TerrainDocument"/>; this node redraws what they
/// reach: the terrain's chunks (a few a frame, off the main thread), the objects standing on the ground, the plants.
/// </summary>
public partial class MapEditor : Node
{
    public required MissionBuilder Builder { get; init; }
    public required TerrainDocument Doc { get; init; }
    public required EditorCamera Cam { get; init; }
    public required UndoStack Undo { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    public required Action ToggleLook { get; init; }
    public required Func<bool> Is1997 { get; init; }
    public required Action Reload { get; init; }
    /// <summary>Leaves the editor for the game's shell (or the end of the program, when it was started on a mission).</summary>
    public required Action Leave { get; init; }
    public I76.Formats.Vfs.GameInstall? Install { get; init; }
    public Func<VegetationNode?> VegetationOf { get; init; } = () => null;

    public BrushSettings Brush { get; } = new();
    /// <summary>Building, props and markers (Objects/ObjectEditor.cs).</summary>
    public ObjectEditor Objects { get; private set; } = null!;
    /// <summary>The road tool (game/Scripts/Editor/Roads).</summary>
    public RoadTool RoadsTool { get; private set; } = null!;
    /// <summary>The ground under the cursor (game frame), or null.</summary>
    public System.Numerics.Vector3? Hit => _hit;
    private MissionWorld World => Builder.World!;
    private TerrainNode Terrain => Builder.Terrain!;

    private BrushStroke? _stroke;
    private int _strokeFrames, _idleFrames;
    private double _idleMs;
    private double _strokeMs, _strokeMax;
    private System.Numerics.Vector3? _hit;          // the ground under the cursor (game frame)
    private System.Numerics.Vector3? _lastStamp;
    private SampleRect? _fast, _slow;               // changed samples waiting for the terrain's redraw, and for the objects' and plants'
    private double _fastClock, _slowClock;
    private bool _brushOn = true;
    private bool _overlayOn = true;                 // the Surface tool's map over the ground (;)
    private SurfaceOverlay _overlay = null!;
    private int _frame;
    private bool _switchSettled;
    private double _hold;
    private bool _dirtyLook;
    /// <summary>The editor's soundtrack (EditorMusic), null without an install; <see cref="_musicOn"/> is the MUSIC button's setting.</summary>
    private EditorMusic? _music;
    private bool _musicOn = true, _leavingForPlaytest;

    private static readonly bool Hook = OS.GetEnvironment("I76_EDITOR_SHOT") is { Length: > 0 };

    public override void _Ready()
    {
        Terrain.Editing = true;
        Terrain.Refreshed += OnTerrainRefreshed;
        _overlay = new SurfaceOverlay(Doc);
        Doc.Changed += OnChanged;
        Doc.LookChanged += OnLookChanged;
        Cam.GroundAt = (x, z) => (float)World.BilinearHeight(x, z);
        Cam.Origin = Origin;
        Cam.Cursor = () => _hit;
        Cam.Enabled = () => !_modal;
        if (EditorSession.Pose is { Length: 6 } p) Cam.Set(new System.Numerics.Vector3(p[0], p[1], p[2]), p[3], p[4], p[5]);
        else
        {
            var c = World.Centre();
            Cam.Set(c, 0.6f, -0.85f, 900f);
            Cam.Pivot = c;
        }
        ApplyEnvironmentCamera();
        GD.Print($"map editor: camera pivot {Cam.Pivot.X:0}, {Cam.Pivot.Z:0}, ground {Cam.Pivot.Y:0.0} m; {Doc.Tiles.TileCount} tiles, bounds {Doc.Bounds()}");
        if (EditorSession.SwitchedAtMs != 0) { GD.Print($"editor: back from the playtest in {Time.GetTicksMsec() - EditorSession.SwitchedAtMs} ms"); EditorSession.SwitchedAtMs = 0; }
        RoadsTool = new RoadTool { Name = "RoadTool", Ed = this };
        AddChild(RoadsTool);
        Objects = new ObjectEditor { Name = "Objects", Ed = this, Builder = Builder, Cam = Cam, Doc = Doc, Undo = Undo, Layer = Doc.Layer!, Origin = Origin, PickGround = PickGround };
        AddChild(Objects);
        _musicOn = I76.Game.Shell.ShellSettings.Load().EditorMusic;
        if (Install is not null) { _music = new EditorMusic { Name = "EditorMusic", Install = Install }; AddChild(_music); }
        BuildUi();
        SyncUi();
        GetViewport().SizeChanged += OnResize;
        HookStart();
        SwitchTimer.Mark("editor ready");
    }

    public override void _ExitTree()
    {
        Doc.Changed -= OnChanged;
        Doc.LookChanged -= OnLookChanged;
        if (GodotObject.IsInstanceValid(Builder.Terrain)) Terrain.Refreshed -= OnTerrainRefreshed;
        RenderingServer.GlobalShaderParameterSet("i76_brush", Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet("i76_surface_on", Vector4.Zero);
        if (GodotObject.IsInstanceValid(Builder.Terrain)) Terrain.Editing = false;
    }

    private void OnResize() { BuildUi(); SyncUi(); }

    private void OnTerrainRefreshed((int X0, int Z0, int X1, int Z1) vis) => _slowVis = _slowVis is { } sv ? Union(sv, vis) : vis;

    private void OnChanged(SampleRect r)
    {
        _overlay.Touch(r);
        _fast = _fast is { } f ? f.Union(r) : r;
        _slow = _slow is { } s ? s.Union(r) : r;
    }

    // ---- the cursor: the ground under the mouse ----------------------------------------------------------------------------

    private double GroundH(double x, double z) => World.BilinearHeight(x, z);

    /// <summary>Marches the camera's ray through the mouse until it passes under the ground; the point where it does, game frame.</summary>
    public System.Numerics.Vector3? PickGround(Vector2 screen)
    {
        var o = Cam.ProjectRayOrigin(screen);
        var d = Cam.ProjectRayNormal(screen);
        var og = Origin();
        var o3 = new System.Numerics.Vector3(o.X + og.X, o.Y + og.Y, -o.Z + og.Z);
        var d3 = new System.Numerics.Vector3(d.X, d.Y, -d.Z);
        const double max = 12000;
        double t = 0, prevT = 0;
        double Above(double tt) => o3.Y + d3.Y * tt - GroundH(o3.X + d3.X * tt, o3.Z + d3.Z * tt);
        if (Above(0) < 0) return null;
        while (t < max)
        {
            double step = Math.Max(1.5, t * 0.012);
            prevT = t; t += step;
            if (Above(t) < 0)
            {
                double lo = prevT, hi = t;
                for (int i = 0; i < 18; i++) { double mid = (lo + hi) / 2; if (Above(mid) < 0) hi = mid; else lo = mid; }
                double tt = (lo + hi) / 2;
                float x = (float)(o3.X + d3.X * tt), z = (float)(o3.Z + d3.Z * tt);
                if (!Doc.Has((int)Math.Floor(x * MissionWorld.SampleScale), (int)Math.Floor(z * MissionWorld.SampleScale))) return null;
                return new System.Numerics.Vector3(x, (float)GroundH(x, z), z);
            }
        }
        return null;
    }

    // ---- frame ---------------------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!IsInsideTree()) return;
        _frame++;
        if (_frame == 1) SwitchTimer.Mark("editor first frame");
        else if (!_switchSettled && Terrain.StaleChunks == 0) { _switchSettled = true; SwitchTimer.Mark("editor terrain drawn", true); }
        HookFrame();
        if (!IsInsideTree()) return;   // a hook left the scene (the playtest)
        _overUi = GetViewport().GuiGetHoveredControl() is not null;
        if (!_overUi && !_modal && DisplayServerHasMouse()) _lastViewMouse = GetViewport().GetMousePosition();
        if (!_hookCursor) _hit = _modal || _overUi || !DisplayServerHasMouse() ? null : PickGround(GetViewport().GetMousePosition());
        // an eyedropper or a stamp holds while the mouse is over the window only
        Objects.Frame(delta, _hit, _modal || _overUi);
        UpdateBrushUniform();
        UpdateSurfaceOverlay();
        if (_stroke is null && _frame > 15 && Terrain.StaleChunks == 0) { _idleFrames++; _idleMs += delta * 1000; }
        if (_stroke is not null) { _strokeFrames++; _strokeMs += delta * 1000; _strokeMax = Math.Max(_strokeMax, delta * 1000); }
        if (_stroke is not null && _hit is { } h) StrokeStep(h, _hookCursor ? 1 / 30.0 : Math.Min(delta, 0.05));
        PumpRedraw(delta);
        RoadsTool.Process(delta);
        UpdateStatus();
    }

    private static bool DisplayServerHasMouse() => DisplayServer.WindowIsFocused() || OS.GetEnvironment("I76_MOUSE") is { Length: > 0 };

    private void UpdateBrushUniform()
    {
        if (_hit is { } h && _brushOn && !_modal && !RoadsTool.Active && !Objects.Active)
        {
            RenderingServer.GlobalShaderParameterSet("i76_brush", new Vector4(h.X, h.Z, Brush.Radius, 1));
            var c = BrushColour(Brush.Kind);
            RenderingServer.GlobalShaderParameterSet("i76_brush_style", new Vector4(c.R, c.G, c.B, Brush.Kind == BrushKind.Surface ? (int)Falloff.Plateau : (int)Brush.Falloff));
        }
        else RenderingServer.GlobalShaderParameterSet("i76_brush", Vector4.Zero);
    }

    /// <summary>The Surface tool is the one in use.</summary>
    public bool SurfaceToolOn => Brush.Kind == BrushKind.Surface && !RoadsTool.Active && !Objects.Active;

    private void UpdateSurfaceOverlay()
    {
        var c = Cam.Pivot;
        _overlay.Update(SurfaceToolOn && _overlayOn, c.X * MissionWorld.SampleScale, c.Z * MissionWorld.SampleScale);
    }

    private Color BrushColour(BrushKind k)
    {
        // the surface tool's cursor is pale so it shows over its own overlay; the panel and the status line name the type
        return k == BrushKind.Surface ? new Color(1f, 0.97f, 0.85f) : ToolColour(k);
    }

    public static Color ToolColour(BrushKind k) => k switch
    {
        BrushKind.Raise => new Color(0.35f, 0.9f, 0.3f),
        BrushKind.Lower => new Color(0.95f, 0.35f, 0.2f),
        BrushKind.Smooth => new Color(0.3f, 0.75f, 1f),
        BrushKind.Flatten => new Color(1f, 0.85f, 0.25f),
        BrushKind.Level => new Color(1f, 0.6f, 0.15f),
        _ => new Color(0.8f, 0.45f, 1f),
    };

    // ---- the stroke ----------------------------------------------------------------------------------------------------------

    private void BeginStroke()
    {
        if (_hit is null || _modal) return;
        EndStroke();
        _stroke = new BrushStroke(Doc, Brush);
        _lastStamp = null;
    }

    private void StrokeStep(System.Numerics.Vector3 to, double dt)
    {
        // stamps along the path from the last one, close enough that the cursor's passage leaves no beads
        float spacing = Math.Max(Brush.Radius * 0.3f, 4f);
        var from = _lastStamp ?? to;
        float dist = System.Numerics.Vector3.Distance(new(from.X, 0, from.Z), new(to.X, 0, to.Z));
        int n = Math.Max(1, (int)Math.Ceiling(dist / spacing));
        for (int i = 1; i <= n; i++)
        {
            float f = (float)i / n;
            double x = (from.X + (to.X - from.X) * f) * MissionWorld.SampleScale, z = (from.Z + (to.Z - from.Z) * f) * MissionWorld.SampleScale;
            _stroke!.Stamp(x, z, dt / n);
        }
        _lastStamp = to;
    }

    private void EndStroke()
    {
        if (_stroke is null) return;
        var cmd = _stroke.End();
        _stroke = null;
        if (_strokeFrames > 0)
            GD.Print($"editor stroke: {Brush.Kind} r {Brush.Radius:0} m, {_strokeFrames} frames, ground now {(_lastStamp is { } ls ? GroundH(ls.X, ls.Z) : 0):0.0} m, {_strokeMs / _strokeFrames:0.0} ms average (idle {(_idleFrames > 0 ? _idleMs / _idleFrames : 0):0.0} ms) ({1000 * _strokeFrames / Math.Max(_strokeMs, 1):0} fps), worst {_strokeMax:0.0} ms, {Terrain.StaleChunks} chunks still stale; surface refresh {(Terrain.RefreshCount > 0 ? Terrain.RefreshMs / Terrain.RefreshCount : 0):0.0} ms x {Terrain.RefreshCount} off the main thread");
        _strokeFrames = 0; _strokeMs = 0; _strokeMax = 0;
        if (cmd is not null) Undo.Push(cmd);
        _slowClock = 0;   // the objects and plants catch up at once
    }

    /// <summary>
    /// The map style window's APPLY (or a script's mapstyle): the look goes into the document as one undo step, and the map is rebuilt
    /// from it (<see cref="OnLookChanged"/>).
    /// </summary>
    public void ApplyLook(I76.Formats.Mission.MapLook look, string name)
    {
        EndStroke();
        if (Doc.Look is not { } before || look.Equals(before)) return;
        Undo.Push(new LookCommand(name, before, look));
        Doc.SetLook(look);
    }

    /// <summary>
    /// The look changed (an edit, or its undo or redo): the world is built again from the file with the new look over it, as a
    /// playtest's return is, so the sky, the ground, the horizon ring, the fog, the plants and both looks all follow. The heights, roads,
    /// objects, undo history and camera are carried across.
    /// </summary>
    private void OnLookChanged()
    {
        EndStroke();
        EditorSession.Capture();
        EditorSession.Pose = [Cam.Pivot.X, Cam.Pivot.Y, Cam.Pivot.Z, Cam.Yaw, Cam.Pitch, Cam.Distance];
        EditorSession.SwitchedAtMs = Time.GetTicksMsec();
        GD.Print($"editor: map style now {Doc.Look}");
        Reload();
    }

    private void DoUndo() { EndStroke(); if (Undo.Undo()) Toast($"Undo {Undo.NextRedo}"); }
    private void DoRedo() { EndStroke(); if (Undo.Redo()) Toast($"Redo {Undo.NextUndo}"); }

    /// <summary>
    /// Redraw what the edits reached: the terrain every 60 ms (the surface at once, the chunks a few a frame on the
    /// pool), and at most every 0.4 s, and when a stroke ends, the objects standing on the ground, the plants and the cavity maps.
    /// </summary>
    private void PumpRedraw(double delta)
    {
        _fastClock -= delta; _slowClock -= delta;
        if (_fast is { } r && _fastClock <= 0)
        {
            _fastClock = 0.06;
            Terrain.RefreshRegion(r.X0, r.Z0, r.X1, r.Z1);
            _fast = null;
        }
        if (_slowVis is { } v && _slowClock <= 0 && (_stroke is null || _slowClock < -0.4))
        {
            _slowClock = 0.4;
            float k = (float)(1 / MissionWorld.SampleScale);
            Builder.Scenery?.Reground(v.X0 * k, v.Z0 * k, (v.X1 + 1) * k, (v.Z1 + 1) * k);
            VegetationOf()?.Invalidate(v.X0 * k, v.Z0 * k, (v.X1 + 1) * k, (v.Z1 + 1) * k);
            Terrain.RefreshCavities(v.X0, v.Z0, v.X1, v.Z1);
            _slowVis = null; _slow = null;
        }
    }

    private (int X0, int Z0, int X1, int Z1)? _slowVis;
    private static (int, int, int, int) Union((int X0, int Z0, int X1, int Z1) a, (int X0, int Z0, int X1, int Z1) b) =>
        (Math.Min(a.X0, b.X0), Math.Min(a.Z0, b.Z0), Math.Max(a.X1, b.X1), Math.Max(a.Z1, b.Z1));

    // ---- actions -------------------------------------------------------------------------------------------------------------

    public void SetTool(BrushKind k)
    {
        RoadsTool.Deactivate();
        Objects.Deactivate();
        Brush.Kind = k;
        SyncUi();
    }

    public void Eyedrop()
    {
        if (SurfaceToolOn)
        {
            if (_hit is not { } sh || !Doc.Locate((int)Math.Floor(sh.X * MissionWorld.SampleScale + 0.5), (int)Math.Floor(sh.Z * MissionWorld.SampleScale + 0.5), out int tile, out int off)) { Toast("Point at the ground to pick its surface"); return; }
            Brush.SurfaceId = SurfaceTypes.Of(Doc.GetSample(tile, off));
            SyncUi();
            Toast($"Surface {SurfaceTypes.All[Brush.SurfaceId].Name.ToLowerInvariant()}");
            return;
        }
        if (_hit is not { } h) { Toast("Point at the ground to pick its height"); return; }
        Brush.TargetHeight = (float)Math.Round(h.Y, 1);
        Brush.Kind = BrushKind.Level;
        SyncUi();
        Toast($"Level height {Brush.TargetHeight:0.0} m");
    }

    public void FocusCursor()
    {
        if (_hit is { } h) Cam.FocusOn(h);
    }

    /// <summary>Ctrl+S: writes the map into the addon folder, in place if it is a map of the player's own, else asking for a new name (a stock map is never written over).</summary>
    public void Save()
    {
        EndStroke();
        if (Doc.NeedsSaveAs) SaveAs();
        else DoSave(null, null);
    }

    public void SaveAs()
    {
        EndStroke();
        if (Install is null) { Toast("No install to save into", true); return; }
        string stem = Doc.BaseMission.Length > 7 ? Doc.BaseMission[..7] : Doc.BaseMission;
        string suggest = stem + "x";
        for (int n = 2; Install.FindMission(suggest) is not null && n < 100; n++) suggest = stem[..Math.Min(stem.Length, 8 - n.ToString().Length)] + n;
        ShowDialog(FormDialog.SaveAs(Install, suggest, Doc.Name, (name, listed) => { CloseModal(); DoSave(name, listed); }, CloseModal));
    }

    private void DoSave(string? name, string? listed)
    {
        try
        {
            var files = Doc.Save(name, listed);
            EditorSession.Capture();
            var errors = Objects.CheckNow().Where(i => i.Severity == I76.Editor.Objects.Severity.Error).ToList();
            Toast($"Saved {Doc.Name}: {string.Join(", ", files.Select(System.IO.Path.GetFileName))} (addon folder)" + (errors.Count > 0 ? $"  -  {errors.Count} problem{(errors.Count == 1 ? "" : "s")} to fix before it plays: {errors[0].Message}" : ""), errors.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or DirectoryNotFoundException)
        {
            GD.PrintErr($"editor save: {ex}");
            Toast($"Could not save: {ex.Message}", true);
        }
    }

    /// <summary>A new flat map, made as a file in the addon folder and opened.</summary>
    public void NewFlatMap()
    {
        EndStroke();
        if (Install?.AddonDir is null) { Toast("The install has no addon folder to save a map to", true); return; }
        void Go()
        {
            ShowDialog(FormDialog.NewFlatMap(Install, (name, listed, tiles, metres) =>
            {
                CloseModal();
                try
                {
                    NewMap.CreateFlat(Install.AddonDir!, name, listed, tiles, metres, Install.IsNitro);
                    OpenMap(name.ToLowerInvariant());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Toast($"Could not create it: {ex.Message}", true); }
            }, CloseModal));
        }
        if (Doc.Modified) Confirm("Start a new map? Edits not saved are lost.", Go);
        else Go();
    }

    /// <summary>The real-world map dialog (OpenStreetMap picker); the map it builds is opened.</summary>
    public void NewGeoMap()
    {
        EndStroke();
        if (Install?.AddonDir is null) { Toast("The install has no addon folder to save a map to", true); return; }
        void Go()
        {
            ShowDialog(new GeoMapDialog { Install = Install, Built = stem => { CloseModal(); OpenMap(stem); }, Cancelled = CloseModal });
        }
        if (Doc.Modified) Confirm("Start a new map? Edits not saved are lost.", Go);
        else Go();
    }

    /// <summary>The camera's ray through a screen point, in the game frame.</summary>
    public (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Dir) GameRay(Vector2 screen)
    {
        var o = Cam.ProjectRayOrigin(screen);
        var d = Cam.ProjectRayNormal(screen);
        var og = Origin();
        return (new System.Numerics.Vector3(o.X + og.X, o.Y + og.Y, -o.Z + og.Z), new System.Numerics.Vector3(d.X, d.Y, -d.Z));
    }

    /// <summary>The 3D view's last mouse position (while it was over the view, not a window or the bar): a click on PLAYTEST drops the car there.</summary>
    private Vector2? _lastViewMouse;

    /// <summary>
    /// The playtest's cursor ray: through the mouse when it is over the 3D view (F5), through where it last was over the view (the button, the menu),
    /// through the held scripted cursor in a capture. Null with the reason in <see cref="EditorSession.PlaytestNoRay"/>.
    /// </summary>
    private (System.Numerics.Vector3, System.Numerics.Vector3)? PlaytestRay(bool fromBar)
    {
        Vector2? at;
        if (_hookCursor && _hit is { } h)
        {
            var og = Origin();
            var g = new Vector3(h.X - og.X, h.Y - og.Y, -(h.Z - og.Z));
            at = Cam.IsPositionBehind(g) ? null : Cam.UnprojectPosition(g);
        }
        else if (fromBar) at = _lastViewMouse;
        else at = !_modal && !_overUi && DisplayServerHasMouse() ? GetViewport().GetMousePosition() : null;
        if (at is not { } p || !GetViewport().GetVisibleRect().HasPoint(p)) { EditorSession.PlaytestNoRay = fromBar ? "the cursor was not over the view" : "the cursor is not over the 3D view"; return null; }
        return GameRay(p);
    }

    /// <summary>F5: the car is dropped under the cursor (or, with the cursor elsewhere, at the camera's pivot) and the map runs; F5 or Esc comes back here.</summary>
    public void Playtest(bool fromBar = false)
    {
        if (_leavingForPlaytest) return;
        EndStroke();
        EditorSession.PlaytestRay = PlaytestRay(fromBar);
        EditorSession.Capture();
        EditorSession.Pose = [Cam.Pivot.X, Cam.Pivot.Y, Cam.Pivot.Z, Cam.Yaw, Cam.Pitch, Cam.Distance];
        EditorSession.Playtest = true;
        EditorSession.SwitchedAtMs = Time.GetTicksMsec();
        EditorSession.PlaytestAt = [Cam.Pivot.X, Cam.Pivot.Z, Cam.HeadingDegrees];
        // the music fades out first (the mission has its own); the editor starts it again on the way back
        const double fade = 0.4;
        if (_music?.FadeOut(fade) == true)
        {
            _leavingForPlaytest = true;
            GetTree().CreateTimer(fade, processAlways: true).Timeout += Reload;
        }
        else Reload();
    }

    /// <summary>The MUSIC button: the editor's soundtrack on or off, kept with the settings (a new key; nothing else in them changes).</summary>
    public void ToggleMusic()
    {
        var st = I76.Game.Shell.ShellSettings.Load();
        st.EditorMusic = _musicOn = !st.EditorMusic;
        st.Save();
        _music?.Apply();
        Toast(!_musicOn ? "Music off" : st.Audio[0] < 2 ? "Music on (the Music level in Audio Control is off)" : _music is { Available: false } ? "Music on (no tracks in this install)" : "Music on");
    }

    /// <summary>F12 (as in a mission) and F2 switch the look. True when the key was one of them.</summary>
    private bool LookKey(InputEventKey key)
    {
        if (key.Keycode is not (Key.F12 or Key.F2) || key.CtrlPressed || key.AltPressed) return false;
        if (!key.Echo) { ToggleLook(); _dirtyLook = true; GD.Print($"editor: look {(Is1997() ? "1997" : "modern")} ({key.Keycode})"); }
        return true;
    }

    public void OpenMap(string mission)
    {
        EditorSession.Begin(mission);
        Reload();
    }

    public void Exit()
    {
        EditorSession.End();
        Leave();
    }

    // ---- input ---------------------------------------------------------------------------------------------------------------

    private bool _modal, _overUi;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (_modal) return;
            if (mb.ButtonIndex == MouseButton.Left && RoadsTool.Active)
            {
                if (RoadsTool.HandleMouse(mb)) GetViewport().SetInputAsHandled();
                return;
            }
            if (Objects.HandleMouse(mb)) { GetViewport().SetInputAsHandled(); return; }
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (mb.AltPressed) { Eyedrop(); GetViewport().SetInputAsHandled(); }
                    else { BeginStroke(); GetViewport().SetInputAsHandled(); }
                }
                else EndStroke();
            }
            else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                float k = mb.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1 / 1.12f;
                if (mb.ShiftPressed) { Brush.ScaleRadius(k); SyncUi(); GetViewport().SetInputAsHandled(); }
                else if (mb.CtrlPressed) { Brush.AddStrength(mb.ButtonIndex == MouseButton.WheelUp ? 0.04f : -0.04f); SyncUi(); GetViewport().SetInputAsHandled(); }
            }
            return;
        }
        if (e is not InputEventKey { Pressed: true } key) return;
        // the look's keys work with a text field focused and a window open: neither uses them
        if (LookKey(key)) { GetViewport().SetInputAsHandled(); return; }
        if (GetViewport().GuiGetFocusOwner() is LineEdit)
        {
            if (key.Keycode == Key.Escape) { GetViewport().GuiReleaseFocus(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (_modal)
        {
            if (key.Keycode == Key.Escape && !key.Echo) { CloseModal(); GetViewport().SetInputAsHandled(); }
            if (key.Keycode == Key.F1 && !key.Echo) { ToggleHelp(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (RoadsTool.HandleKey(key)) { GetViewport().SetInputAsHandled(); return; }
        if (Objects.HandleKey(key)) { GetViewport().SetInputAsHandled(); return; }
        bool handled = true;
        switch (key.Keycode)
        {
            case Key.Z when key.CtrlPressed && key.ShiftPressed: DoRedo(); break;
            case Key.Z when key.CtrlPressed: DoUndo(); break;
            case Key.Y when key.CtrlPressed: DoRedo(); break;
            case Key.S when key.CtrlPressed: Save(); break;
            case Key.F1: ToggleHelp(); break;
            case Key.F5 when !key.CtrlPressed && !key.Echo: Playtest(); break;
            case Key.Escape when !key.Echo: ToggleMenu(); break;
            case Key.Bracketleft when key.ShiftPressed: Brush.AddStrength(-0.05f); SyncUi(); break;
            case Key.Bracketright when key.ShiftPressed: Brush.AddStrength(0.05f); SyncUi(); break;
            case Key.Braceleft: Brush.AddStrength(-0.05f); SyncUi(); break;
            case Key.Braceright: Brush.AddStrength(0.05f); SyncUi(); break;
            case Key.Bracketleft: Brush.ScaleRadius(1 / 1.15f); SyncUi(); break;
            case Key.Bracketright: Brush.ScaleRadius(1.15f); SyncUi(); break;
            case Key.Key1 or Key.Kp1: SetTool(BrushKind.Raise); break;
            case Key.Key2 or Key.Kp2: SetTool(BrushKind.Lower); break;
            case Key.Key3 or Key.Kp3: SetTool(BrushKind.Smooth); break;
            case Key.Key4 or Key.Kp4: SetTool(BrushKind.Flatten); break;
            case Key.Key5 or Key.Kp5: SetTool(BrushKind.Level); break;
            case Key.Key6 or Key.Kp6: SetTool(BrushKind.Noise); break;
            case Key.Semicolon when SurfaceToolOn && !key.CtrlPressed && !key.Echo: _overlayOn = !_overlayOn; Toast(_overlayOn ? "Surface map on" : "Surface map off"); SyncUi(); break;
            case Key.I when !key.CtrlPressed: Eyedrop(); break;
            case Key.F when !key.CtrlPressed && !key.Echo: FocusCursor(); break;
            case Key.Tab when !key.Echo: Cam.EdgePan = !Cam.EdgePan; Toast(Cam.EdgePan ? "Edge pan on" : "Edge pan off"); break;
            case Key.G when !key.Echo: Brush.Falloff = (Falloff)(((int)Brush.Falloff + 1) % 4); SyncUi(); break;
            case Key.B when !key.Echo: _brushOn = !_brushOn; break;
            default: handled = false; break;
        }
        if (handled) GetViewport().SetInputAsHandled();
    }

    // ---- scripted captures (docs/dev-hooks.md) --------------------------------------------------------------------------------

    private bool _hookCursor;
    /// <summary>Capture scripts: hold the cursor on a ground point (an off-screen window has no mouse).</summary>
    public void SetHookCursor(float x, float z) { _hookCursor = true; _hit = new System.Numerics.Vector3(x, (float)GroundH(x, z), z); }

    private readonly List<(int Frame, string[] Args)> _script = [];
    private (string Tool, float X, float Z, float Radius, float Strength, double Seconds, int Until, string Falloff, float Height)? _scripted;

    private void ApplyEnvironmentCamera()
    {
        // I76_EDITOR_CAM=x,z,yaw,pitch,distance (game metres, degrees): the editor camera there
        if (OS.GetEnvironment("I76_EDITOR_CAM") is { Length: > 0 } cam && cam.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is { Length: 5 } a)
        {
            var piv = new System.Numerics.Vector3(a[0], (float)World.BilinearHeight(a[0], a[1]), a[1]);
            Cam.Set(piv, Mathf.DegToRad(a[2]), Mathf.DegToRad(a[3]), a[4]);
            Rebase(piv);
        }
        else Rebase(Cam.Pivot);
    }

    private void Rebase(System.Numerics.Vector3 p) => Cam.Rebase(p);

    private void HookStart()
    {
        // I76_EDITOR_SCRIPT="frame:cmd:args;...": drives the editor in a capture. Commands:
        //   stroke:tool,x,z,radius,strength,seconds[,falloff[,height]]   one stroke of that tool at game x, z, held for that long
        //   cursor:x,z   hold the cursor on that ground point (no mouse in an off-screen window)
        //   cam:x,z,yawDeg,pitchDeg,distance, undo, redo, flat, tool:NAME, look, style[:tab] (the map style window), mapstyle:ground,NAME[,sky,NAME,horizon,NAME|none,palette,NAME,hour,H]
        if (OS.GetEnvironment("I76_EDITOR_SCRIPT") is not { Length: > 0 } script) return;
        foreach (var item in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = item.Split(':', 3);
            if (p.Length >= 2 && int.TryParse(p[0], out int f)) _script.Add((f, p[1..]));
        }
    }

    private (float X, float Z) _scriptPos, _scriptVel;
    // newmap:name,Listed name,tiles,metres: makes that flat map in the addon folder and opens it, as the form would
    private void OpenNewMapScripted(string[] a)
    {
        // opening the map reloads the scene, which runs the script again from its first frame: a map that is already the one open is not made over and over
        if (EditorSession.Mission.Equals(a[0], StringComparison.OrdinalIgnoreCase)) return;
        NewMap.CreateFlat(Install!.AddonDir!, a[0], a[1], int.Parse(a[2]), double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture), Install.IsNitro);
        OpenMap(a[0].ToLowerInvariant());
    }

    // mapstyle:ground,tp081sd6,sky,nk_6cld0,horizon,p13_16.hzd|none,palette,t02_16.act,hour,12: the map style, applied at once (names without an extension
    // get the field's; without a palette the palette follows as in the window)
    private void MapStyleScripted(string[] a)
    {
        var look = Doc.Look ?? throw new InvalidOperationException("no WRLD look");
        bool palette = false;
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            string v = a[i + 1];
            palette |= a[i].Equals("palette", StringComparison.OrdinalIgnoreCase);
            look = a[i].ToLowerInvariant() switch
            {
                "ground" => look with { Ground = v.Contains('.') ? v : v + ".map" },
                "sky" => look with { Sky = v.Contains('.') ? v : v + ".map" },
                "horizon" => look with { Horizon = v == "none" ? "" : v.Contains('.') ? v : v + ".hzd" },
                "palette" => look with { Palette = v.Contains('.') ? v : v + ".act" },
                "hour" => look with { Hour = int.Parse(v) },
                _ => throw new ArgumentException($"mapstyle: ground, sky, horizon, palette or hour, not '{a[i]}'"),
            };
        }
        if (!palette) look = MapLookPanel.CatalogFor(Builder.Vfs).Suited(look);
        ApplyLook(look, "map style");
    }

    // saveas:name,Listed name
    private void SaveAsScripted(string[] a) => DoSave(a[0], a[1]);

    private int _scriptStrokeEnd = -1;
    private int _scriptStrokeFrames;

    private void HookFrame()
    {
        foreach (var (f, a) in _script.Where(s => s.Frame == _frame).ToList())
        {
            var args = a.Length > 1 ? a[1].Split(',') : [];
            float F(int i) => float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (a[0])
            {
                case "cursor":
                    _hookCursor = true;
                    _hit = new System.Numerics.Vector3(F(0), (float)GroundH(F(0), F(1)), F(1));
                    break;
                case "stroke":
                    {
                        Enum.TryParse<BrushKind>(args[0], true, out var kind);
                        Brush.Kind = kind; Brush.Radius = F(3); Brush.Strength = F(4);
                        if (args.Length > 6 && Enum.TryParse<Falloff>(args[6], true, out var fo)) Brush.Falloff = fo;
                        if (args.Length > 7) Brush.TargetHeight = F(7);
                        _hookCursor = true;
                        _hit = new System.Numerics.Vector3(F(1), (float)GroundH(F(1), F(2)), F(2));
                        _scriptStrokeFrames = Math.Max(1, (int)Math.Round(F(5) * 30));   // held for that many seconds of 30 fps
                        _scriptStrokeEnd = _frame + _scriptStrokeFrames;
                        // the cursor drags at (vx, vz) metres a second from there (args 8, 9)
                        _scriptPos = (F(1), F(2)); _scriptVel = args.Length > 9 ? (F(8), F(9)) : (0f, 0f);
                        BeginStroke();
                        SyncUi();
                        break;
                    }
                case "cam":   // cam:x,z,yawDegrees,pitchDegrees,distance: the camera there at once (the pivot on the ground at x, z), for a path of captures
                    {
                        var piv = new System.Numerics.Vector3(F(0), (float)GroundH(F(0), F(1)), F(1));
                        Cam.Set(piv, Mathf.DegToRad(F(2)), Mathf.DegToRad(F(3)), F(4));
                        Rebase(piv);
                        break;
                    }
                case "undo": DoUndo(); break;
                case "redo": DoRedo(); break;
                case "exit": Exit(); break;
                case "saveform": SaveAs(); break;
                case "newform": NewFlatMap(); break;
                case "geoform": NewGeoMap(); break;
                case "newmap": OpenNewMapScripted(args); break;
                case "saveas": SaveAsScripted(args); break;
                case "brush":   // brush:tool,radius,strength,falloff[,height]: set the brush without painting
                    {
                        if (Enum.TryParse<BrushKind>(args[0], true, out var bk)) Brush.Kind = bk;
                        Brush.Radius = F(1); Brush.Strength = F(2);
                        if (Enum.TryParse<Falloff>(args[3], true, out var bf)) Brush.Falloff = bf;
                        if (args.Length > 4) Brush.TargetHeight = F(4);
                        SyncUi();
                        break;
                    }
                case "surface": Brush.SurfaceId = Math.Clamp((int)F(0), 0, 7); if (args.Length > 1) _overlayOn = F(1) != 0; SyncUi(); break;   // surface:id[,overlay 0|1]
                case "tool": if (Enum.TryParse<BrushKind>(args[0], true, out var tk)) SetTool(tk); break;
                case "look": ToggleLook(); break;
                case "music": ToggleMusic(); break;
                case "key":   // key:F12: that key pressed through the window's input path, as a keyboard sends it
                    if (Enum.TryParse<Key>(args[0], true, out var pk)) Input.ParseInputEvent(new InputEventKey { Keycode = pk, PhysicalKeycode = pk, Pressed = true });
                    break;
                case "playtest": Playtest(args.Length > 0 && args[0] == "bar"); break;   // playtest[:bar]: as the PLAYTEST button (the cursor as it last was over the view)
                case "help": ToggleHelp(); break;
                case "style": OpenStyle(args.Length > 0 && int.TryParse(args[0], out int tab) ? tab : 0); break;
                case "mapstyle": MapStyleScripted(args); break;
                case "menu": ToggleMenu(); break;
                case "pick": OpenPicker(); break;
                case "save": Save(); break;
                case "road": RoadsTool.Script(a.Length > 1 ? a[1] : ""); break;
                default: Objects.Script(a[0], args); break;
            }
        }
        if (_scriptStrokeEnd >= 0 && (_scriptVel.X != 0 || _scriptVel.Z != 0))
        {
            _scriptPos = (_scriptPos.X + _scriptVel.X / 30f, _scriptPos.Z + _scriptVel.Z / 30f);
            _hit = new System.Numerics.Vector3(_scriptPos.X, (float)GroundH(_scriptPos.X, _scriptPos.Z), _scriptPos.Z);
        }
        if (_scriptStrokeEnd >= 0 && _frame >= _scriptStrokeEnd) { EndStroke(); _scriptStrokeEnd = -1; }
    }
}
