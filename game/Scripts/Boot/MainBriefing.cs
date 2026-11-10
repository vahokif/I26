using Godot;
using I76.Formats.Vfs;
using I76.Game.Sim;
using I76.Game.Ui;
using I76.Game.World;
using System.Collections.Generic;
using System.Linq;

namespace I76.Game.Boot;

/// <summary>
/// A mission's briefing played while the mission loads (Play Options' Load During Briefing, docs/deviations.md): the world built,
/// and what the rest of the load can make without the scene tree made ahead, on a thread of its own; the rest on the main thread
/// under the movie, in slices of a few milliseconds with a drawn frame between them, so the movie never stands still.
/// </summary>
public partial class Main
{
    /// <summary>
    /// The briefing a mission start plays while its world builds, or null for the original's order (the build, then the
    /// briefing): the setting on, a single-player start with a briefing movie that is there, and no checkpoint to resume, the
    /// map editor's playtest or --skip-movies (<see cref="BriefingOverlap.Plan"/>).
    /// </summary>
    private string? BriefingDuringLoad()
    {
        if (_opt.Install is null || _opt.Mission is null) return null;
        var install = new GameInstall(_opt.Install);
        string? name = null;
        try
        {
            if (install.FindMission(_opt.Mission) is { } path)
                name = I76.Formats.Mission.WorldSettings.Read(new I76.Formats.Mission.MissionFile(path))?.Briefing;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.ArgumentException) { return null; }
        bool on = BriefingOverlap.Plan(I76.Game.Shell.ShellSettings.Load().BriefingWhileLoading, name, name is { Length: > 0 } && MoviePlayer.Find(install, name) is not null,
            _opt.SkipMovies, _net is not null || _opt.Network, I76.Game.Editor.EditorSession.Active, _retry is not null);
        return on ? name : null;
    }

    /// <summary>What the build thread makes after the world, ahead of the rest of the load (plain C# and resources: nothing in the tree).</summary>
    private sealed class Ahead
    {
        /// <summary>The simulation's setup for <see cref="Requested"/>, as SpawnCar would make it (its seed and melee roster with it).</summary>
        public I76.Sim.Missions.MissionSetup? Setup;
        public uint Seed;
        public List<string>? Roster;
        public string? Requested;
        /// <summary>The sounds (the cars' engines, the impacts and blasts) and the effects' sprite looks, decoded.</summary>
        public I76.Game.Assets.AudioBank? Audio;
        public EffectSprites? Sprites;
        /// <summary>The player's car's in-car view (its meshes and dashboard layout) and every car's wreck, made.</summary>
        public PlayerCarParts? Player;
        public DamageView.WreckMeshes? Wrecks;
        /// <summary>The modern look's plants at <see cref="VegLevel"/> (VegetationNode.Create), outside the tree.</summary>
        public VegetationNode? Veg;
        public int VegLevel;
    }

    /// <summary>The build thread's work ahead, taken by SpawnCar and UpdateVegetation; null but under a briefing.</summary>
    private Ahead? _ahead;

    /// <summary>A world built on a thread of its own (<see cref="LoadUnderBriefing"/>), and who frees it if the scene goes meanwhile.</summary>
    private sealed class BuildJob(MissionBuilder builder)
    {
        public readonly System.Threading.CancellationTokenSource Cancel = new();
        public Node3D? World;
        public Ahead? Ahead;
        public System.Exception? Error;
        private int _owner;   // 0 building, 1 given up (the scene went), 2 built

        /// <summary>The thread's last word: given up meanwhile, what it made is nobody's (outside the tree, so the thread may free it).</summary>
        public void Done()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _owner, 2, 0) != 1) return;
            (World ?? builder.Root)?.Free();
            World = null;
            Ahead?.Veg?.Free();
        }

        /// <summary>The scene goes: true while the thread still builds (it stops at its next stage and frees what it made).</summary>
        public bool GiveUp()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _owner, 1, 0) != 0) return false;
            Cancel.Cancel();
            return true;
        }
    }

    /// <summary>How long the loading card stays up before a briefing played over the build (after its fade-in, before its fade-out), seconds.</summary>
    private const double CardHold = 0.7;

    /// <summary>The build under the briefing, given up when this scene goes.</summary>
    private BuildJob? _buildJob;

    /// <summary>
    /// The mission's start with its briefing played while the world builds. The card is up already (as in the original's order) and
    /// goes back to black as the build starts on a thread of its own (MissionBuilder.OffMainThread: it makes its nodes outside the
    /// tree and touches nothing in it; then <see cref="MakeAhead"/>) and the movie plays on this one. The build done, the rest of the
    /// load (<see cref="Rest"/>) runs here under the movie in slices, the tree held still. A skip before the mission can start is kept, with
    /// "PLEASE STAND BY" faded in over the movie, and done when it can; a movie that runs out first holds its last frame under the
    /// line (MoviePlayer.Waiting), and
    /// one that could not play at all leaves the card to come back. False when the mission is not going on (the scene went, or the build
    /// failed and the game went back to the shell).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> LoadUnderBriefing(string briefing, LoadingScreen loading)
    {
        // the builder is the game's (_builder, which the views and the overlay read) only once the thread is done with it
        var start = StartLoad();
        var builder = start.Builder;
        builder.OffMainThread = true;
        var job = _buildJob = new BuildJob(builder);
        builder.Cancel = job.Cancel.Token;
        var settings = I76.Game.Shell.ShellSettings.Load();
        // what the thread makes ahead is decided here: the capture hooks that change a car's files first, the plants' level as SpawnCar will set it
        if (_opt.Drive is not null) ApplyCarHooks(builder);
        int vegLevel = _opt.Mode1997 ? 0 : VegetationNode.LevelFor(settings.Vegetation);
        // the game's code compiled meanwhile, so its first runs under the movie do not wait for the JIT
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = JitWarm.Prepare(typeof(Main).Assembly, typeof(I76.Sim.Session.SimSession).Assembly, typeof(I76.Formats.Assembly.MissionWorld).Assembly);
            if (n > 0) GD.Print($"briefing load: {n} methods compiled ahead in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
        });
        MissionBuilder.Trace("build: on its own thread, under the briefing");
        var thread = new System.Threading.Thread(() =>
        {
            MainThread.OwnsItsScene = true;
            try
            {
                job.World = builder.Build(start.Msn);
                job.Ahead = MakeAhead(builder, vegLevel);
            }
            catch (System.Exception ex) { job.Error = ex; }
            start.Clock.Stop();   // the status line's time is the build's, not the movie's
            job.Done();
        }, 64 << 20) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "mission-build" };
        bool paced = System.Environment.GetEnvironmentVariable("I76_BUILD_PACE") != "0";
        if (paced) FrameLimiter.ForBuild(settings);
        void Unpace() { if (paced) { paced = false; FrameLimiter.Apply(settings); } }
        // a failure, the movie ended first: back to the shell saying why (true), or on into an empty world from the command line
        bool Failed(System.Exception ex)
        {
            GetNodeOrNull<MoviePlayer>("Movie")?.End();
            return LoadFailed(ex, loading);
        }
        GetTree().Paused = true;   // nothing moves under the movie until the mission starts, as under the original's briefing
        EngineThreads.Start(thread);   // (a quit meanwhile waits for it: the engine must not go from under it)
        try
        {
            // the card held a moment (it would otherwise only flash up), the build going on meanwhile, then back to black for the movie
            await ToSignal(GetTree().CreateTimer(CardHold, processAlways: true), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return false;
            await loading.FadeOut();
            if (!IsInsideTree()) return false;
            MissionBuilder.Trace("loading card: black for the briefing");
            bool over = false, ready = false;
            // until the mission can start: a skip kept (and "PLEASE STAND BY" faded in, StandByTag), a movie that runs out held on its last frame
            try { PlayMovies(start.Install, [briefing], () => over = true, overLoad: true, waiting: () => !ready); }
            catch (System.Exception ex) { GD.PrintErr($"movie {briefing}: not played: {ex}"); over = true; }
            System.Threading.Tasks.Task<bool>? rest = null;
            bool card = false;
            while (true)
            {
                if (AuditOn) Audit();
                // the mission stands still under the movie until it ends or is skipped: nothing may have let it go
                if (!GetTree().Paused) { GD.PrintErr("briefing: the game was let go under the movie; held again"); GetTree().Paused = true; }
                if (rest is null && !thread.IsAlive)
                {
                    thread.Join();   // (over already: never waited on while it runs, as its calls to the renderer wait for this thread's frames)
                    MissionBuilder.Trace($"build: done under the briefing ({start.Clock.ElapsedMilliseconds} ms)");
                    if (job.Error is { } error)
                    {
                        if (builder.Root is { } part && IsInstanceValid(part)) part.Free();
                        job.World = null;
                        if (Failed(error)) return false;
                    }
                    _builder = builder;
                    _ahead = job.Ahead;
                    builder.Materials?.FlushPropMaps();   // the props' maps, here as at the end of a built frame
                    rest = Rest(start, job.World, Failed);
                }
                if (rest is { IsCompleted: true })
                {
                    Unpace();   // (the rest of the load ran a slice a frame: as many frames as the renderer gives)
                    if (rest.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rest.Exception!.GetBaseException()).Throw();
                    if (!rest.Result) return false;
                    ready = true;
                    if (over) break;
                    // loaded with the movie still on: as under the original's briefing, the game is there, held still (the 1997 look's
                    // modern terrain goes on building behind it, Main._Process)
                    if (_loading) MissionBuilder.Trace("mission ready under the briefing");
                    _loading = false;
                }
                // the movie gone before the mission is ready (it could not be played: one that runs out is held): the card for the rest
                if (over && !card)
                {
                    card = true;
                    MissionBuilder.Trace("loading card up, after the briefing");
                    _ = loading.FadeIn();
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInsideTree()) return false;
            }
            if (card)
            {
                MissionBuilder.Trace("loading card: fade out");
                await loading.FadeOut();
                if (!IsInsideTree()) return false;
            }
            loading.QueueFree();
            MissionBuilder.Trace("loading card gone: the mission goes on");
            _loading = false;
            GetTree().Paused = false;
            if (AuditOn) AuditAfter();
            return true;
        }
        finally
        {
            Unpace();
            _buildJob = null;
            // the scene went mid-build: the thread stops at its next stage and frees what it made; done, its world is freed here unless it is the game's
            if (!job.GiveUp())
            {
                if (_builder != builder && job.World is { } w && IsInstanceValid(w) && !w.IsInsideTree()) w.Free();
                if (job.Ahead?.Veg is { } veg && IsInstanceValid(veg) && !veg.IsInsideTree()) veg.Free();   // made for a level the start did not use
            }
            _ahead = null;
        }
    }

    /// <summary>
    /// On the build thread, after the world: what the rest of the load makes without the scene tree. The effects' run-wide textures
    /// and shaders; with a car to drive, the simulation's setup (MissionSetup, plain C#: the same call SpawnCar makes, from the same
    /// origin, so the same game), every car's meshes, the sounds and the effects' looks the views will ask for; the modern look's
    /// plants. Nothing here is reachable from the main thread until the thread is done.
    /// </summary>
    private Ahead MakeAhead(MissionBuilder b, int vegLevel)
    {
        var a = new Ahead();
        OilSlicks.Prebake();
        FireDrops.Prebake();
        ModernFx.PrepareVfx();
        DropShadows.PrepareShaders();
        MissionBuilder.Trace("ahead: the effects' textures and shaders");
        // the distant terrain's meshes (computed during the build): made here, Main only places them
        if (b.HorizonBuild is { } hb) hb.GetAwaiter().GetResult().Finish();
        MissionBuilder.Trace("ahead: distant terrain");
        if (_opt.Drive is { } drive && b.World is { } w)
        {
            var (setup, seed, roster) = MakeSetup(b, w, drive, StartOrigin(w));
            (a.Setup, a.Seed, a.Roster, a.Requested) = (setup, seed, roster, drive);
            MissionBuilder.Trace("ahead: the simulation set up");
            // (I76_CAR_CONDITION's cars are made each time: MakeCarNode)
            if (System.Environment.GetEnvironmentVariable("I76_CAR_CONDITION") is not { Length: 1 })
                foreach (var (_, def, v) in setup.Cars) b.CarParts.MakeAhead(def, v.IsHeli);
            MissionBuilder.Trace("ahead: the cars' meshes");
            if (setup.World.Player is { } pv && setup.Cars.FirstOrDefault(c => c.Item3 == pv).Item2 is { } playerDef) a.Player = MakePlayerCarParts(b, playerDef);
            MissionBuilder.Trace("ahead: the in-car view");
            // the wrecks, as SpawnCar asks for them (the player's first, in the look the mission starts in)
            var wrecks = a.Wrecks = new DamageView.WreckMeshes(b);
            wrecks.Prewarm(setup.Cars.Where(c => !c.Item3.IsHeli).Select(c => c.Item2), !_opt.Mode1997);
            if (WreckFire.Enabled) WreckFire.Shared.Init();
            MissionBuilder.Trace("ahead: the wrecks");
            var audio = a.Audio = new I76.Game.Assets.AudioBank(b.Vfs);
            var sprites = a.Sprites = new EffectSprites(b);
            CombatView.PrepareImpacts(setup.Combat, sprites, audio);
            CombatView.PrepareExplosions(setup.Combat, sprites);
            foreach (var snd in (string[])["vexplode.wav", "tblow.wav"]) audio.Get(snd);
            foreach (var (_, _, v) in setup.Cars)
                if (!v.IsEmplacement && I76.Sim.Vehicles.VehicleFactory.EngineSound(b.Vfs, v.EngineNumber) is { } engine) audio.Get(engine, loop: true);
            MissionBuilder.Trace("ahead: sounds and effect looks");
        }
        if (vegLevel > 0 && b.ModernReady)
        {
            a.Veg = VegetationNode.Create(b, vegLevel);
            a.VegLevel = vegLevel;
            MissionBuilder.Trace("ahead: plants");
        }
        return a;
    }

    /// <summary>
    /// I76_BRIEFING_AUDIT=1: under the briefing, twice a second, a line of what the mission does meanwhile (it must do nothing): its
    /// ticks, its script's tick, and the game's sounds heard (a player whose position moved since the last line; the movie's own
    /// are left out). "briefing audit: ... heard none" all the way through is the frozen mission.
    /// </summary>
    private static readonly bool AuditOn = System.Environment.GetEnvironmentVariable("I76_BRIEFING_AUDIT") == "1";
    private readonly Dictionary<ulong, double> _audited = [];
    private double _auditAt;

    /// <summary>The audit's lines for the first two seconds after the movie: the mission's own sounds (T02's first CB line) are heard now.</summary>
    private async void AuditAfter()
    {
        for (int i = 0; i < 5; i++)
        {
            _auditAt = 0;
            Audit();
            await ToSignal(GetTree().CreateTimer(0.5, processAlways: true), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }
    }

    private void Audit()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - _auditAt < 0.5) return;
        _auditAt = now;
        var heard = new List<string>();
        void Walk(Node n)
        {
            if (n is MoviePlayer) return;
            double? at = n switch
            {
                AudioStreamPlayer { Playing: true } p => p.GetPlaybackPosition(),
                AudioStreamPlayer3D { Playing: true } p => p.GetPlaybackPosition(),
                AudioStreamPlayer2D { Playing: true } p => p.GetPlaybackPosition(),
                _ => null,
            };
            if (at is { } pos)
            {
                ulong id = n.GetInstanceId();
                if (_audited.TryGetValue(id, out var was) && pos != was) heard.Add($"{n.GetParent()?.Name}/{n.Name} {was:0.00}->{pos:0.00}");
                _audited[id] = pos;
            }
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(GetTree().Root);
        GD.Print($"briefing audit{(GetNodeOrNull("Movie") is null ? " (the mission going)" : "")}: ticks {_sim?.Session.Ticks.ToString() ?? "-"}, script tick {_sim?.Script?.Host.FsmTick.ToString() ?? "-"}, "
            + $"CB line {_sim?.Script?.Host.Cb.Current?.Name ?? "none"}, paused {GetTree().Paused}, heard {(heard.Count == 0 ? "none" : string.Join(", ", heard))}");
    }

    /// <summary>The plants made ahead for <paramref name="level"/> (<see cref="MakeAhead"/>), taken once, or null.</summary>
    private VegetationNode? TakeAheadVeg(int level)
    {
        if (_ahead is not { Veg: { } v } a || a.VegLevel != level) return null;
        a.Veg = null;
        return v;
    }

    /// <summary>
    /// What follows the build under the briefing, on the main thread in slices: the world into the tree and the rest of the load,
    /// then the effects' rehearsal. False when a failure took the game back to the shell (<paramref name="failed"/>).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> Rest(Load start, Node3D? world, System.Func<System.Exception, bool> failed)
    {
        _slice = System.Diagnostics.Stopwatch.StartNew();
        (_sliceWorst, _sliceWorstAt, _slices) = (0, "", 0);
        // the world drawn behind the movie only for the effects' rehearsal, whose frames make the renderer's pipelines
        var view = GetViewport();
        bool no3D = view.Disable3D;
        view.Disable3D = true;
        try
        {
            if (world is not null)
            {
                try { await RunSteps(AttachSteps(start, world), sliced: true); }
                catch (System.Exception ex) { if (failed(ex)) return false; }
            }
            await RunSteps(FinishSteps(sliced: true), sliced: true);
            await EndSlice();
            view.Disable3D = no3D;
            await RunSteps(WarmSteps(sliced: true), sliced: true);
            await EndSlice();
            GD.Print($"briefing load: {_slices} slices on the main thread under the movie, the longest {_sliceWorst:0.0} ms ({_sliceWorstAt})");
            return true;
        }
        finally { _slice = null; view.Disable3D = no3D; }
    }

    /// <summary>Under the briefing, a frame's share of the load: steps go on in the same frame until it is spent.</summary>
    private const double SliceBudgetMs = 12;

    /// <summary>In <see cref="RunSteps"/>: a frame drawn before the next step, whether or not the steps are sliced.</summary>
    private static readonly object NextFrame = new();

    /// <summary>Under the briefing: the main thread's time in the slice so far (null otherwise), and the longest slice.</summary>
    private System.Diagnostics.Stopwatch? _slice;
    private double _sliceWorst;
    private string _sliceWorstAt = "";
    private int _slices;
    private System.TimeSpan _sliceGc;

    /// <summary>
    /// Runs a load's steps. Behind the card (not <paramref name="sliced"/>) as one stretch of work, as the load always ran, but for
    /// <see cref="NextFrame"/>; under the briefing a step's end is a slice's once the slice has had <see cref="SliceBudgetMs"/>, a
    /// frame drawn before the next, and a Task a step yields is waited for a frame at a time.
    /// </summary>
    private async System.Threading.Tasks.Task RunSteps(IEnumerable<object?> steps, bool sliced)
    {
        foreach (var step in steps)
        {
            if (step is System.Threading.Tasks.Task t)
            {
                if (!sliced) { t.Wait(); continue; }
                while (!t.IsCompleted) await EndSlice();
                if (t.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.Exception!.GetBaseException()).Throw();
                continue;
            }
            bool frame = ReferenceEquals(step, NextFrame);
            if (frame && !sliced) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); continue; }
            // under the briefing the steps go on in one frame while it has time left (a step is a few milliseconds to a few tens)
            if (sliced && (frame || _slice is not { } s || s.Elapsed.TotalMilliseconds >= SliceBudgetMs)) await EndSlice();
        }
    }

    /// <summary>A slice over: its time noted (I76_BUILD_TRACE logs the ones over 10 ms, under the last stage stamped), a frame drawn.</summary>
    private async System.Threading.Tasks.Task EndSlice()
    {
        if (_slice is { } s)
        {
            double ms = s.Elapsed.TotalMilliseconds;
            _slices++;
            if (ms > _sliceWorst) (_sliceWorst, _sliceWorstAt) = (ms, MissionBuilder.LastStage);
            var gc = System.GC.GetTotalPauseDuration();
            if (MissionBuilder.TraceOn && ms > 10) GD.Print($"slice: {ms:0.0} ms after {MissionBuilder.LastStage} (gc pauses {(gc - _sliceGc).TotalMilliseconds:0.0} ms)");
        }
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _slice?.Restart();
        _sliceGc = System.GC.GetTotalPauseDuration();
    }
}
