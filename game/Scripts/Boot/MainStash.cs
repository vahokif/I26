using Godot;
using I76.Editor;

namespace I76.Game.Boot;

/// <summary>
/// The map editor's playtest without throwing the editor away (docs/map-editor.md, Playtest): the editor's scene is put aside, inert and
/// out of sight, while a second scene plays the map, and taken back when the playtest ends, so that the way back costs no build of the
/// map. It is kept only for the revision of the document it was built at (<see cref="I76.Game.Editor.EditorSession.Revision"/>); any
/// other case, a stash disabled (I76_EDITOR_STASH=0) or a map too big to hold twice, reloads the scene as before.
/// </summary>
public partial class Main
{
    /// <summary>The editor scene held while its playtest runs.</summary>
    internal static readonly ViewStash<Main> Stash = new(m => { if (IsInstanceValid(m)) m.QueueFree(); });

    private readonly List<(Node Node, ProcessModeEnum Mode)> _stashedModes = [];
    private readonly List<CanvasItem> _stashedItems = [];
    private readonly List<CanvasLayer> _stashedLayers = [];
    private readonly List<Node3D> _stashedRoots = [];
    private ProcessModeEnum _stashedProcess;
    private bool _stashedEnv;

    /// <summary>I76_EDITOR_STASH=0: the playtest reloads the scene, both ways, as it did (for the timing before and after).</summary>
    private static readonly bool StashOff = System.Environment.GetEnvironmentVariable("I76_EDITOR_STASH") == "0";

    /// <summary>Whether this editor scene can wait aside for its playtest: not a map streamed in pieces (it takes the memory of a second copy).</summary>
    private bool CanStash => !StashOff && _editor is not null && _builder?.World is { } w && !w.Terrain.Huge && I76.Game.Editor.EditorSession.Doc is not null;

    /// <summary>The editor asks for its playtest: a second scene plays, this one waits; or, when it cannot wait, the scene is reloaded.</summary>
    private void EnterPlaytest(bool musicFading)
    {
        ReleaseCursor();
        if (!CanStash)
        {
            if (musicFading) GetTree().CreateTimer(0.4, processAlways: true).Timeout += () => GetTree().ReloadCurrentScene();
            else GetTree().ReloadCurrentScene();
            return;
        }
        Stash.Hold(this, StashKey, I76.Game.Editor.EditorSession.Doc!.Revision);
        Suspend();
        var play = ResourceLoader.Load<PackedScene>(SceneFilePath).Instantiate();
        GetTree().Root.AddChild(play);
        GetTree().CurrentScene = play;
        I76.Game.Editor.SwitchTimer.Mark("playtest scene added");
    }

    private string StashKey => $"{_opt.Install}|{I76.Game.Editor.EditorSession.Mission}";

    /// <summary>Back from the playtest: this scene was the playtest's. True when the editor scene was taken back (this one then frees itself).</summary>
    private bool ReturnToStashedEditor()
    {
        if (I76.Game.Editor.EditorSession.Doc is not { } doc || Stash.Take(StashKey, doc.Revision) is not { } editor) return false;
        GetTree().Paused = false;
        ReleaseCursor();
        editor.Unsuspend();
        GetTree().CurrentScene = editor;
        _loading = true;           // this scene takes no more keys
        ProcessMode = ProcessModeEnum.Disabled;
        Visible = false;
        QueueFree();
        return true;
    }

    /// <summary>Puts the scene aside: nothing runs, nothing is drawn, nothing is heard.</summary>
    private void Suspend()
    {
        _stashedModes.Clear(); _stashedItems.Clear(); _stashedLayers.Clear(); _stashedRoots.Clear();
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is I76.Game.Editor.EditorMusic) { _stashedModes.Add((c, c.ProcessMode)); c.ProcessMode = ProcessModeEnum.Always; continue; }   // its fade-out runs on while the playtest loads
                if (c.ProcessMode == ProcessModeEnum.Always) { _stashedModes.Add((c, ProcessModeEnum.Always)); c.ProcessMode = ProcessModeEnum.Disabled; }
                if (c is CanvasLayer { Visible: true } cl) { _stashedLayers.Add(cl); cl.Visible = false; continue; }
                if (c is CanvasItem { Visible: true } ci) { _stashedItems.Add(ci); ci.Visible = false; continue; }
                // a Node3D hides its Node3D children, but not through a plain node between them
                if (c is Node3D { Visible: true } n3 && n is not Node3D) { _stashedRoots.Add(n3); n3.Visible = false; }
                Walk(c);
            }
        }
        Walk(this);
        _stashedProcess = ProcessMode;
        ProcessMode = ProcessModeEnum.Disabled;
        Visible = false;
        // the world environment applies from the first in the tree: the playtest's own has to be that
        if (_env is { } e && e.IsInsideTree()) { RemoveChild(e); _stashedEnv = true; }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Takes the scene back: shown, running, its look and its globals as it had them.</summary>
    private void Unsuspend()
    {
        ProcessMode = _stashedProcess;
        foreach (var (n, m) in _stashedModes) if (IsInstanceValid(n)) n.ProcessMode = m;
        foreach (var l in _stashedLayers) if (IsInstanceValid(l)) l.Visible = true;
        foreach (var i in _stashedItems) if (IsInstanceValid(i)) i.Visible = true;
        foreach (var r in _stashedRoots) if (IsInstanceValid(r)) r.Visible = true;
        _stashedModes.Clear(); _stashedItems.Clear(); _stashedLayers.Clear(); _stashedRoots.Clear();
        Visible = true;
        if (_stashedEnv && _env is { } e && !e.IsInsideTree()) { AddChild(e); MoveChild(e, 0); }
        _stashedEnv = false;
        _cam.MakeCurrent();
        I76.Game.Editor.SwitchTimer.Mark("editor scene taken back");
        // the playtest's scene is freed at the end of this frame and its nodes reset some globals as they go: the editor's are set again after
        CallDeferred(MethodName.Reassert);
    }

    private async void Reassert()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!IsInsideTree()) return;
        // the playtest's scene is gone; what it made is held by managed wrappers until a collection: let go of it now, not when the collector gets to it
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GD.Print($"collected the playtest's leftovers in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
        var saved = I76.Game.Shell.ShellSettings.Load();
        _lookSaved = saved.Look1997;
        SetMode1997(_opt.Mode1997 || saved.Look1997);     // the look the playtest left (F12 there is kept in the settings), with every global of it
        _veg?.Reassert();
        _editor?.Resumed();
        I76.Game.Editor.SwitchTimer.Mark("editor globals set again", true);
    }
}
