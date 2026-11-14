using Godot;
using I76.Game.Ui;

namespace I76.Game.Boot;

/// <summary>
/// The port's Checkpoints (Play Options; docs/savestate.md): in a trip mission, the session's snapshot kept in
/// memory when the intro is over and at each objective done, the latest only, for this mission attempt; a failure
/// (or the Abort pad) can retry from it: the mission is built again as Replay builds it and the snapshot restored,
/// with the game's views brought to it. Using one does not make a cheater (docs/deviations.md).
/// </summary>
public partial class Main
{
    /// <summary>The latest checkpoint of this mission attempt; Replay, Abort, Reconfigure and the mission's end drop it.</summary>
    private static Checkpoint? _checkpoint;
    /// <summary>The retry under way: the reloaded scene restores it once the mission is built.</summary>
    private static Checkpoint? _retry;
    /// <summary>When this attempt keeps checkpoints (null: the option is off, or the mission cannot have them).</summary>
    private I76.Sim.Save.CheckpointPolicy? _checkpoints;
    /// <summary>This scene is a checkpoint's retry: no tick 0, no briefing, the music where it was.</summary>
    private bool _resumed;
    private Checkpoint? _resumedFrom;
    /// <summary>A checkpoint bookmark's checkpoint could not be used: what the player is told once the mission is up (it starts from its beginning).</summary>
    private static string? _resumeNotice;
    /// <summary>This scene's restore failed: it is about to be built again from the mission's start.</summary>
    private bool _restarting;

    /// <summary>The option, or I76_CHECKPOINTS=1 (a capture hook, docs/dev-hooks.md).</summary>
    private static bool CheckpointsOn(I76.Game.Shell.ShellSettings s) => s.Checkpoints || System.Environment.GetEnvironmentVariable("I76_CHECKPOINTS") == "1";

    /// <summary>A trip mission (or one named on the command line), single player: the missions checkpoints are for.</summary>
    private bool CheckpointMission => _net is null && _launch is null or { Kind: I76.Game.Shell.GameKind.Trip } && !_opt.Melee;

    private bool CanRetry => _checkpoint is { } cp && cp.Mission.Equals(_opt.Mission ?? "", System.StringComparison.OrdinalIgnoreCase) && _net is null;

    /// <summary>
    /// Save Bookmark beside Retry from Checkpoint: only in a trip mission (tNN) the shell launched, whose trip state and car at the
    /// mission's start the launch carries (not training, a scenario, a melee, Nitro or a mission from the F9 list or the command line).
    /// </summary>
    private bool CanSaveBookmark => CanRetry && _launch is { Kind: I76.Game.Shell.GameKind.Trip, TripStart: { } start }
        && I76.Game.Shell.CheckpointBookmarks.TripMission(_opt.Mission) == start.Mission;

    /// <summary>
    /// Save Bookmark (the escape menu's): a new Load Bookmark entry, as a bookmark saved before this mission would be (its trip state
    /// and car from the launch), with the latest checkpoint beside it. The trip's own state (trip.cfg, its car) is not touched.
    /// </summary>
    private bool SaveCheckpointBookmark()
    {
        if (!CanSaveBookmark || _checkpoint is not { } cp || _launch?.TripStart is not { } start) return false;
        try
        {
            var marks = I76.Game.Shell.Bookmarks.Load();
            var e = I76.Game.Shell.CheckpointBookmarks.Add(marks, start, _launch.NoSalvage, cp.ToBytes(), I76.Game.Shell.Bookmarks.WriteCar, I76.Game.Shell.Bookmarks.WriteCheckpoint);
            marks.Save();
            GD.Print($"checkpoint bookmark: {e.Stem}, \"Scene {e.Scene}. {e.Name}\" ({cp.Why}, tick {cp.Tick})");
            return true;
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"checkpoint bookmark: not saved ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// A shell launch from a checkpoint bookmark: its checkpoint becomes the retry this mission build restores. One that cannot be
    /// read (another version's, a damaged file) is dropped here, before anything uses its snapshot, and the mission starts from its
    /// beginning with a word to the player.
    /// </summary>
    private static void ResumeFromBookmark(I76.Game.Shell.ShellLaunch l, byte[] data)
    {
        _checkpoint = null;
        _retry = Checkpoint.ForLaunch(l.Mission, data, out _resumeNotice);
        GD.Print(_retry is { } cp ? $"checkpoint bookmark: {l.Mission} resumes at tick {cp.Tick} ({cp.Why})" : $"checkpoint bookmark: not used; {l.Mission} starts from its beginning");
    }

    /// <summary>Once the mission is up: the word that a bookmark's checkpoint could not be used, if there is one.</summary>
    private void ShowResumeNotice()
    {
        if (_restarting || _resumeNotice is not { } text) return;   // (the scene that starts the mission over says it)
        _resumeNotice = null;
        if (_opt.Install is null) return;
        AddChild(new CheckpointNotice { InstallDir = _opt.Install, Faithful = _mode1997, Name = "ResumeNotice", Text = text, Hold = 4.5 });
    }

    /// <summary>The policy for this attempt, from the option as it is now (Play Options' Done applies it from the next checkpoint on).</summary>
    private void SetCheckpointPolicy(I76.Game.Shell.ShellSettings s, bool resumed)
    {
        bool on = CheckpointsOn(s) && CheckpointMission;
        if (!on) { _checkpoints = null; return; }
        if (_checkpoints is null && _sim?.Session is { } session) _checkpoints = I76.Sim.Save.CheckpointPolicy.For(session, true, resumed);
    }

    /// <summary>Once a frame, after the ticks: a checkpoint if one is due.</summary>
    private void CheckpointFrame()
    {
        if (_checkpoints is null || _sim?.Session is not { } s || _ended || _menu is not null) return;
        if (_checkpoints.Due(s) is { } why) TakeCheckpoint(s, why);
        // capture hook: I76_CHECKPOINT_AT=tick keeps one at that tick as well (docs/dev-hooks.md)
        else if (!_resumed && int.TryParse(System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_AT"), out int at) && s.Ticks >= at && _checkpoint?.Why != "hook")
            TakeCheckpoint(s, "hook");
    }

    private void TakeCheckpoint(I76.Sim.Session.SimSession s, string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var music = GetNodeOrNull<AudioStreamPlayer>("Music");
        _checkpoint = new Checkpoint(_opt.Mission ?? "", s.Snapshot(), _salvage?.Save(), music is { Playing: true } ? music.GetPlaybackPosition() : null,
            _chase?.SaveState(), _car?.CockpitCamera?.Current == true, _lastPreset, why, s.Ticks);
        GD.Print($"checkpoint ({why}) at tick {s.Ticks}: {_checkpoint.Snapshot.Length} bytes in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        // I76_CHECKPOINT_SHOT=path: the frame of the checkpoint itself (the cars at the tick, not between two), then the notice
        // (with I76_CHECKPOINT_AT, of that one)
        if (System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_SHOT") is { Length: > 0 } shot && !_checkpointShot
            && (why == "hook" || System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_AT") is null))
        {
            _checkpointShot = true;
            foreach (var c in _sim!.Cars) c.Place(1, _origin);
            RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePostDraw, Callable.From(() =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot);
                GD.Print($"checkpoint shot: {shot}");
                ShowCheckpointNotice();
            }), (uint)GodotObject.ConnectFlags.OneShot);
            return;
        }
        ShowCheckpointNotice();
    }

    private bool _checkpointShot;

    private void ShowCheckpointNotice()
    {
        if (_opt.Install is null) return;
        GetNodeOrNull("CheckpointNotice")?.Free();
        AddChild(new CheckpointNotice { InstallDir = _opt.Install, Faithful = _mode1997, Name = "CheckpointNotice" });
    }

    /// <summary>"Retry from Checkpoint": the mission again as Replay builds it, then the checkpoint restored over it.</summary>
    private void RetryCheckpoint()
    {
        if (_checkpoint is not { } cp) return;
        _ended = true;
        _retry = cp;
        NextMission = _launch?.Mission ?? _opt.Mission;
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>
    /// The retry, once the mission is built and before the player's views (SpawnCar): the snapshot into the session, the car
    /// nodes the restore made or dropped, the trip's salvage, every car's and structure's look, the cars drawn where they are.
    /// False when it could not be restored (the mission then starts over, and the checkpoint is gone).
    /// </summary>
    private bool ResumeCheckpoint(I76.Sim.Session.SimSession session, I76.Sim.Missions.MissionSetup setup, I76.Game.Sim.DamageView damageView)
    {
        if (_retry is not { } cp) return false;
        _retry = null;
        if (!cp.Mission.Equals(_opt.Mission ?? "", System.StringComparison.OrdinalIgnoreCase)) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        I76.Sim.Session.RestoreResult r;
        try { r = session.Restore(cp.Snapshot); }
        catch (System.Exception ex)
        {
            GD.PrintErr($"checkpoint: the restore failed ({ex.Message}); the mission starts over");
            _checkpoint = null;
            _resumeNotice = Checkpoint.Notice(Checkpoint.Problem.Unreadable);   // (another install or car; a version's was refused at the launch)
            NextMission = _launch?.Mission ?? _opt.Mission;
            _restarting = true;
            Callable.From(() => GetTree().ReloadCurrentScene()).CallDeferred();
            return false;
        }
        foreach (var v in r.Dropped)
            if (_carNodes.Remove(v, out var n)) { _sim!.Cars.Remove(n); n.QueueFree(); }
        foreach (var v in r.Created)
            if (v.Definition is { } def) MakeCarNode(v, def, setup.Ground);
        if (cp.Salvage is { } salvage) _salvage?.Load(salvage);
        damageView.Resync();
        _resumed = true;
        _resumedFrom = cp;
        _checkpoint = cp;   // a bookmark's: Retry from Checkpoint (and Save Bookmark) go on from it
        GD.Print($"checkpoint ({cp.Why}, tick {cp.Tick}) restored in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        return true;
    }

    /// <summary>After the player's views exist: the view the checkpoint was seen in, and the sim driver going on without tick 0.</summary>
    private void ResumeViews()
    {
        if (_resumedFrom is not { } cp) return;
        if (cp.Chase is { } chase) _chase?.LoadState(chase);
        _lastPreset = cp.LastPreset;
        if (cp.Cockpit && _car is not null && !_car.Wrecked) { _car.SetCockpit(true); _cockpitFlag = true; }
        else if (cp.LastPreset != 0xf && cp.LastPreset != 0xd) SelectView(cp.LastPreset);
    }

    /// <summary>The music from where it was when the checkpoint was kept (a briefing's wait is over: it plays).</summary>
    private void ResumeMusic()
    {
        if (_resumedFrom?.MusicAt is not { } at || GetNodeOrNull<AudioStreamPlayer>("Music") is not { } music) return;
        if (music.Playing) music.Seek((float)at);
        else StartMissionMusic(at);
    }
}
