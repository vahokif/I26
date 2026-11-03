using Godot;
using I76.Formats.Input;
using Environment = Godot.Environment;
using I76.Formats.Vfs;
using I76.Game.Camera;
using I76.Game.Sim;
using I76.Game.Ui;
using I76.Game.World;
using I76.Sim.Views;
using System.Linq;
using System.Collections.Generic;

namespace I76.Game.Boot;

/// <summary>
/// M1: a mission flyover straight from the install.
///
/// Rendering is modern by default: lit terrain, sun shadows, a sky. F2 (or
/// --1997) switches to 1997 mode, which draws the terrain with the engine's
/// own light model and drops the modern effects. F4 shows the 1997 terrain
/// light alone.
/// </summary>
public partial class Main : Node3D
{
    private LaunchOptions _opt = null!;
    private MissionBuilder? _builder;
    private Node3D _worldRoot = null!;
    private FlyCamera _cam = null!;
    /// <summary>The map editor (game/Scripts/Editor): this scene is the editor, with its camera, brushes and windows.</summary>
    private I76.Game.Editor.MapEditor? _editor;
    private bool _editorMode;
    private ChaseCamera? _chase;
    private PresetCamera? _preset;
    private TargetViewCamera? _tv;
    private CarNode? _car;
    private SimDriver? _sim;
    /// <summary>I76_SPAWN_AT's timed stops: mission second, then x, z, yaw.</summary>
    private readonly Queue<(float At, float[] Stop)> _spawnStops = new();

    /// <summary>The player's car put at x, z (on the terrain) facing yaw degrees from +Z toward +X, at rest: a capture hook.</summary>
    private static void PlacePlayer(I76.Sim.Vehicles.Vehicle pv, I76.Sim.Terrain.IGround ground, float[] a)
    {
        float yaw = Mathf.DegToRad(a[2]);
        var f = new System.Numerics.Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
        pv.Pos = new I76.Sim.Math.Double3(a[0], ground.BareHeight(a[0], a[1]), a[1]);
        pv.Rot = new I76.Sim.Math.Rows(System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitY, f), System.Numerics.Vector3.UnitY, f);
        pv.Vel = default;
    }
    private I76.Sim.Missions.MissionScript? _script;
    private I76.Formats.Mission.ObjectiveText? _objectives;
    private Camera3D? _scriptCam;
    private bool _cockpitBeforeScript, _presetBeforeScript, _tvBeforeScript;
    private static bool _backToMenu;
    private bool _inMenu;
    private DebugOverlay? _overlay;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _env = null!;
    private Environment _modernEnv = null!, _env1997 = null!;
    private bool _mode1997, _lookSaved;
    private I76.Game.Sim.Cockpit? _pit;
    private int _lastShotTick = -1;
    // capture hook: I76_SHOT_PIPE=PATH sends the series frames (I76_SHOT_SERIES) there as raw RGB24, one after another, instead
    // of PNGs: a FIFO an encoder reads (ffmpeg -f rawvideo -pix_fmt rgb24 -s WxH), so no PNG is compressed on the main thread
    private static readonly string? ShotPipe = System.Environment.GetEnvironmentVariable("I76_SHOT_PIPE");
    private System.IO.Stream? _shotPipe;

    /// <summary>One frame of a capture series: a PNG at the path, or the next raw frame down I76_SHOT_PIPE.</summary>
    private void SaveSeriesFrame(string path)
    {
        var img = GetViewport().GetTexture().GetImage();
        if (ShotPipe is null) { img.SavePng(path); return; }
        if (img.GetFormat() != Image.Format.Rgb8) img.Convert(Image.Format.Rgb8);
        _shotPipe ??= new System.IO.FileStream(ShotPipe, System.IO.FileMode.Open, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite, 1 << 20);
        _shotPipe.Write(img.GetData());
        _shotPipe.Flush();
    }
    private (System.Numerics.Vector3 Centre, float Radius) _playerCollider = (new(0, 0.7f, 0), 2.5f);

    /// <summary>The two headlamps: the centres of the HLGT part's left and right halves, or null without one.</summary>
    private static System.Numerics.Vector3[]? HeadlampCentres(I76.Formats.Assembly.AssembledModel m) => LampCentres(m, "HLGT", rear: false);

    /// <summary>A lamp part's two halves (left and right of centre), each at its centre, pushed to its outer face (front or back).</summary>
    private static System.Numerics.Vector3[]? LampCentres(I76.Formats.Assembly.AssembledModel m, string code, bool rear)
    {
        var pts = m.Parts.Where(p => CarNode.IsLamp(p.NodeName, code)).SelectMany(p => p.Mesh.Vertices.Select(x => p.ToModel.Apply(x))).ToList();
        var left = pts.Where(q => q.X < 0).ToList();
        var right = pts.Where(q => q.X > 0).ToList();
        if (left.Count == 0 || right.Count == 0) return null;
        System.Numerics.Vector3 Centre(List<System.Numerics.Vector3> l) =>
            (l.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / l.Count) with { Z = rear ? l.Min(q => q.Z) : l.Max(q => q.Z) };
        return [Centre(left), Centre(right)];
    }

    /// <summary>
    /// The collider record's centre and radius: the midpoint
    /// of the hierarchy's box, the farthest point of it from there, raised
    /// for a car to its farthest COLP corner + 0.1. The chase
    /// camera stands a multiple of it behind that centre.
    /// </summary>
    private static (System.Numerics.Vector3 Centre, float Radius) Collider(I76.Formats.Assembly.AssembledModel m, I76.Sim.Vehicles.Vehicle v)
    {
        // the whole hierarchy: body, weapons and the wheels at their WLOCs (measured in the original game: centre y 0.826 on a01's
        // Piranha = (wheel bottom 0.001 + roof gun top 1.651) / 2, and radius 2.753)
        var pts = m.Parts.SelectMany(p => p.Mesh.Vertices.Select(x => p.ToModel.Apply(x))).ToList();
        foreach (var loc in m.Locations.Where(l => l.Tag == "WLOC"))
            if (m.WheelGeos.TryGetValue(loc.Index, out var wg))
            {
                var at = new System.Numerics.Vector3(loc.Transform.Translation[0], loc.Transform.Translation[1], loc.Transform.Translation[2]);
                if (at.LengthSquared() < 1e-12f) continue;
                // the wheel mesh hangs from its top; the WDF node's height brings its hub to the WLOC
                var lift = new System.Numerics.Vector3(0, wg.Node.T.Y, 0);
                pts.AddRange(wg.Intact.Vertices.Select(x => at + wg.Node.Rotate(x) + lift));
            }
        if (pts.Count == 0) return (new(0, 0.7f, 0), 2.5f);
        var min = pts.Aggregate(System.Numerics.Vector3.Min);
        var max = pts.Aggregate(System.Numerics.Vector3.Max);
        var c = (min + max) * 0.5f;
        float r = pts.Max(x => System.Numerics.Vector3.Distance(x, c));
        foreach (var x in new[] { v.BoxX[0], v.BoxX[3] })
            foreach (var y in new[] { v.BoxY[1], v.BoxY[3] })
                foreach (var z in new[] { v.BoxZ[0], v.BoxZ[3] })
                    r = MathF.Max(r, System.Numerics.Vector3.Distance(new(x, y, z), c) + 0.1f);
        return (c, r);
    }

    /// <summary>GRPF/GLPF, the parts the handguns fire from (types 0x2b/0x2c): the engine hides them.</summary>
    private static bool HandgunMuzzle(string node) => node.Length >= 8 && node[4..8].ToUpperInvariant() is "GRPF" or "GLPF";
    private bool _lightOnly;
    private int _frames, _ptFrames;
    /// <summary>Game-space position of the Godot origin (floating origin).</summary>
    private System.Numerics.Vector3 _origin;
    private const float RebaseDistance = 2000f;

    /// <summary>The game's sun as measured in s01.</summary>
    private static readonly System.Numerics.Vector3 GameLight = new(0.9659f, 0.2588f, 0f);

    /// <summary>The mission the menu picked; the scene reloads into it.</summary>
    public static string? NextMission;
    /// <summary>The F9 list's car (an addon .vcf name): the missions it starts, and their replays, are driven in it. A shell launch clears it.</summary>
    public static string? DebugCar;
    /// <summary>A mission launched from the shell failed to build: back to the shell (the form it came from), which shows <paramref name="error"/>.</summary>
    private void BackFromLoadFailure(string error)
    {
        GD.PrintErr($"back to the shell: {error}");
        _lastLoadError = error;
        (_lastLaunch, _launch, _lastResult, _lastTrip, _lastMelee) = (_launch, null, I76.Game.Shell.MissionResult.Quit, null, null);
        _backToMenu = true;
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>The shell launch that started the current mission (null for one named on the command line).</summary>
    private static I76.Game.Shell.ShellLaunch? _launch;
    /// <summary>What the shell is told when it comes back: the launch and how it ended (the original's start-up state).</summary>
    private static I76.Game.Shell.ShellLaunch? _lastLaunch;
    private static I76.Game.Shell.MissionResult _lastResult = I76.Game.Shell.MissionResult.FirstEntry;
    private static I76.Game.Shell.TripResult? _lastTrip;
    private static I76.Game.Shell.MeleeResult? _lastMelee;
    /// <summary>What a network melee hands back to the shell: the standings, or why it did not start.</summary>
    private static I76.Game.Shell.NetResult? _lastNet;
    /// <summary>Why a mission launched from the shell could not be built (a custom map without its terrain, say): the shell says it.</summary>
    private static string? _lastLoadError;
    /// <summary>A cheat was on when the escape menu closed; a win then gets the end menu instead.</summary>
    private static bool _cheated;
    /// <summary>The typed cheats' buffer: a global of the original, kept for the whole run.</summary>
    private static I76.Sim.Session.CheatTyper _typer = new();
    private EscapeMenu? _menu;
    private System.Func<bool>? _held;
    private bool _endMenuShown;
    private bool _ended;
    /// <summary>This trip mission's field salvage, fed by every non-player car that dies.</summary>
    private I76.Sim.Trip.SalvagePool? _salvage;
    private static bool _menuShown;
    /// <summary>The program was started on a mission (--mission): it has no shell to go back to. Fixed at the first start, so a shell launch's reload is never mistaken for it.</summary>
    private static bool _cmdLine;
    private static bool _cmdLineKnown;

    /// <summary>The loading screen is up and the mission is being built: frames and keys wait.</summary>
    private bool _loading;

    private static bool _buildPrinted;

    /// <summary>For the switch guard (<see cref="SwitchProbe"/>): the install this scene runs, and whether a mission is built and running.</summary>
    internal string? InstallPath => _opt?.Install;
    internal string? MissionName => _opt?.Mission;
    internal bool MissionRunning => _sim is not null && !_loading && !_inMenu;

    /// <summary>A game switch (<see cref="GameSwitch"/>): everything of the running game that lives in statics, as a fresh process has it.</summary>
    internal static void ResetForGameSwitch()
    {
        NextMission = null; DebugCar = null;
        _launch = null; _lastLaunch = null; _lastResult = I76.Game.Shell.MissionResult.FirstEntry;
        _lastTrip = null; _lastMelee = null; _lastNet = null; _lastLoadError = null;
        _cheated = false; _typer = new();
        _menuShown = false; _backToMenu = false;
        _cmdLine = false; _cmdLineKnown = false;
        _checkpoint = null; _retry = null;
    }

    /// <summary>A scene has built a world before in this run (so there is a finished one to let go of).</summary>
    private static bool _sceneBuilt;

    public override async void _Ready()
    {
        I76.Game.Editor.SwitchTimer.Mark("scene ready");
        HeapTrim.Configure();
        if (!I76.Game.Editor.EditorSession.Active) Stash.Drop();   // no editor session: nothing to wait for
        LogFile.InstallForced();
        VramWatch.Install(GetTree());   // --log / I76_LOG=1: the log file even where the setting is off
        // the log's first line from us: the build, so a bug report's godot.log says which one it came from
        if (!_buildPrinted)
        {
            _buildPrinted = true;
            GD.Print(BuildInfo.Line);
            GD.Print(RenderBackend.Describe());
            if (RenderBackend.Compatibility)
                GD.Print("renderer: Compatibility (OpenGL): SSAO and some finer effects are off (docs/compatibility-renderer.md)");
        }
        // release.sh, before the export: the terrain's compressed material pack, for builds that cannot compress (World/TerrainMaterials.WritePrebaked)
        if (OS.GetCmdlineUserArgs() is var cl && Array.IndexOf(cl, "--bake-ground-pack") is var bk and >= 0 && bk + 1 < cl.Length)
        {
            SetProcess(false);
            int rc = I76.Game.World.TerrainMaterials.WritePrebaked(cl[bk + 1]) ? 0 : 1;
            GetTree().Quit(rc);
            return;
        }
        _opt = LaunchOptions.Parse(OS.GetCmdlineUserArgs());
        // an --install (or I76_INSTALL) that is not a game: say so and stop, rather than fail every frame after (no camera, no world)
        if (_opt.Install is { Length: > 0 } given && !I76.Formats.Vfs.InstallSearch.IsInstall(given))
        {
            GD.PrintErr($"I26: {given} is not an Interstate '76 install (no such folder, or no I76.ZFS / nitro.zfs in it)");
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }
        FrameLimiter.Apply(I76.Game.Shell.ShellSettings.Load());   // Max FPS and VSync, from the first frame
        if (ShaderSmoke.Requested) { SetProcess(false); AddChild(new ShaderSmoke()); return; }
        SwitchProbe.Install(GetTree());
        // --from-installer: the game data out of a GOG offline installer first, then this scene again on it
        if (_opt.FromInstaller is { } setupExe && !InstallerScreen.CommandLineHandled)
        {
            InstallerScreen.CommandLineHandled = true;
            _installerOnly = true;
            AddChild(new InstallerScreen { AutoInstaller = setupExe, AutoTarget = _opt.InstallerTarget, OnInstalled = _ => GetTree().ReloadCurrentScene() });
            return;
        }
        // both games available: choose one first (an installer-only game is extracted when chosen), then this scene again on it
        if (_opt.Choose)
        {
            _chooser = true; _installerOnly = true;
            ShowChooser();
            return;
        }
        // the saved look (Graphic Detail's 1997 Mode); --1997 forces it on for this run without touching the setting
        // the settings' game (Nitro keeps its own play flags) before any Load or Save; a wrong --install fails later, as before
        try { I76.Game.Shell.ShellSettings.Nitro = _opt.Install is { } profileDir && new GameInstall(profileDir).IsNitro; }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException) { I76.Game.Shell.ShellSettings.Nitro = false; }
        // an install unpacked by an earlier I26 lacks the game's executable: restore it from the GOG installer if one is at hand (an existence check otherwise)
        if (_opt.Install is { Length: > 0 } fixDir)
        {
            var fixExe = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
            I76.Formats.Installers.ExeRepair.Repair(fixDir, [fixExe, fixExe is null ? null : System.IO.Path.GetDirectoryName(fixExe), I76.Game.Boot.LaunchOptions.LocalRoot()], GD.Print);
        }
        I76.Formats.Audio.CompanionSounds.Locator = I76.Game.Boot.LaunchOptions.FindI76Install;
        _lookSaved = I76.Game.Shell.ShellSettings.Load().Look1997;
        _opt.Mode1997 |= _lookSaved;
        if (_opt.Install is not null) I76.Game.Editor.EditorSession.Install = new GameInstall(_opt.Install);
        // I76_EDITOR=<mission>: the map editor on that mission (docs/dev-hooks.md); the shell's Map Editor entry does the same through EditorSession
        if (!I76.Game.Editor.EditorSession.EnvTaken && System.Environment.GetEnvironmentVariable("I76_EDITOR") is { Length: > 0 } editorMission)
        {
            I76.Game.Editor.EditorSession.EnvTaken = true;
            I76.Game.Editor.EditorSession.Begin(editorMission);
            _opt.MissionGiven = true;
        }
        if (!_cmdLineKnown) { _cmdLine = _opt.MissionGiven; _cmdLineKnown = true; }
        // once a run, under the root: sees the engine's exit through (an exit hang of Godot's on Wayland)
        ExitWatchdog.Install(GetTree());
        HitchWatch.Install(GetTree());
        MusicProbe.Install(GetTree());
        Controls.InstallDir = _opt.Install;
        GameKeys.InstallDir = _opt.Install;
        // over the engine's boot splash: this install's loadgame.pcx
        if (_opt.Install is not null) { I76.Game.Shell.ShellSettings.Load(); BootSplash.Show(this, _opt.Install, _opt.Mode1997); }
        // a network melee from the shell's MULTI MELEE forms: the launch says what --host-listen / --connect would
        if (NextMission is not null && _launch is { Kind: I76.Game.Shell.GameKind.Multiplayer, Net: { } nl })
        {
            NextMission = null;
            ApplyNetLaunch(_launch, nl);
        }
        // The original's start-up shows addon\loadscr.pcx while a mission loads ("SOMEWHERE IN THE SOUTHWEST..."); a network game's is up
        // while it hosts or joins, then the same screen goes on through the build
        LoadingScreen? loading = null;
        // a network melee: the server names the arena and the car (MainNet.cs)
        if (_opt.Network && _opt.Install is not null)
        {
            _loading = true;
            AddChild(loading = new LoadingScreen { Install = new GameInstall(_opt.Install), Faithful = _opt.Mode1997, Name = "Loading" });
            await loading.FadeIn();
            BootSplash.Dismiss();
            // a co-op story mission's briefing, as single player's before the mission: each player's own, before hosting or joining, so
            // that nobody's mission waits for it (the server holds a new mission for its first player's inputs, and a late joiner would
            // stand in a running one); Esc skips it
            // (a joiner of a game the roster showed in its lobby, or a host, has it when the mission starts, in StartNetwork)
            if (_launch is { Kind: I76.Game.Shell.GameKind.Multiplayer, Net: { Coop: true, Connect: not null, InLobby: false } } && !_opt.SkipMovies) await CoopBriefing(_launch.Mission);
            if (!await StartNetwork(loading)) return;
        }
        // the map editor, or a playtest of the map in it: the mission is the editor's, with its edited heights (EditorSession.Prepare)
        if (I76.Game.Editor.EditorSession.Active)
        {
            _opt.Mission = I76.Game.Editor.EditorSession.Mission; _opt.MissionGiven = true; NextMission = null;
            if (I76.Game.Editor.EditorSession.Playtest) { _opt.Drive ??= ""; _opt.Melee = true; }
            else { _opt.Drive = null; _editorMode = true; }
            _opt.NoMusic = true; _opt.SkipMovies = true;
        }
        if (NextMission is { } next)
        {
            _opt.Mission = next; _opt.MissionGiven = true; _opt.Drive ??= _launch?.Car ?? DebugCar ?? ""; NextMission = null;
            // Nitro's melee files (.cbt, .cf2-4, .rac) have no script: they run as an auto melee, as --melee does
            if (_opt.Install is not null && new GameInstall(_opt.Install).FindMission(next) is { } mp && !mp.EndsWith(".msn", System.StringComparison.OrdinalIgnoreCase))
                _opt.Melee = true;
        }
        else if (!_opt.MissionGiven && _opt.Install is not null)
        {
            var install = new GameInstall(_opt.Install);
            // The original's start-up plays introf01 then credf01 before the shell, at every start (--movie names one instead)
            var startup = _opt.Movie is { } only ? [only] : _menuShown || _opt.SkipMovies ? System.Array.Empty<string>() : ["introf01.smk", "credf01.smk"];
            _menuShown = true; _backToMenu = false;
            _inMenu = true;
            var lastNet = _lastNet;
            _lastNet = null;
            var loadError = _lastLoadError;
            _lastLoadError = null;
            I76.Game.Shell.Shell.DropCarriedMusic(GetTree());
            PlayMovies(install, startup, () => AddChild(new I76.Game.Shell.Shell
            {
                Install = install, Name = "Shell", Faithful = _opt.Mode1997,
                LastResult = _lastResult, LastLaunch = _lastLaunch, LastTrip = _lastTrip, LastMelee = _lastMelee, LastNet = lastNet, LoadError = loadError,
                OnLaunch = l => { GD.Print($"shell: launch {l.Mission} ({l.Kind})"); _launch = l; DebugCar = null; _cheated = false; NextMission = l.Mission; GetTree().ReloadCurrentScene(); },
                OnEditor = () =>
                {
                    GetNodeOrNull("Shell")?.QueueFree();
                    var layer = new CanvasLayer { Name = "EditorPicker", Layer = 40 };
                    AddChild(layer);
                    layer.AddChild(new I76.Game.Editor.EditorPicker
                    {
                        Install = install,
                        Picked = mission =>
                        {
                            GD.Print($"shell: map editor on {mission}");
                            I76.Game.Editor.EditorSession.Install = install;
                            I76.Game.Editor.EditorSession.Begin(mission);
                            _launch = null; DebugCar = null; _cheated = false;
                            GetTree().ReloadCurrentScene();
                        },
                        Cancelled = () => { _backToMenu = true; GetTree().ReloadCurrentScene(); },
                    });
                },
                OnMissionList = () =>
                {
                    GetNodeOrNull("Shell")?.QueueFree();
                    AddChild(new MainMenu { Install = install, Name = "Menu", OnBack = () => { _backToMenu = true; GetTree().ReloadCurrentScene(); } });
                },
            }));
            _lastResult = I76.Game.Shell.MissionResult.FirstEntry;
            return;
        }
        // up, and drawn once, before the build
        if (_opt.Install is not null && _opt.Mission is not null)
        {
            _loading = true;
            if (loading is null)
            {
                AddChild(loading = new LoadingScreen { Install = new GameInstall(_opt.Install), Mission = _opt.Mission, Faithful = _opt.Mode1997, Name = "Loading", FadeSeconds = I76.Game.Editor.EditorSession.Active ? 0.1 : 0.32 });
                await loading.FadeIn();
            }
            MissionBuilder.Trace("loading card up");
            BootSplash.Dismiss(now: true);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!IsInsideTree()) return;
        }
        // the scene before this one is gone (a restart, the next mission, the shell): its meshes and textures are held by wrappers until a
        // collection, and its native memory stays with the allocator until trimmed; let both go before the next build takes its share
        if (_sceneBuilt && !Stash.Holds) HeapTrim.Run();
        _sceneBuilt = true;
        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        SetupEnvironment();
        _cam = _editorMode ? new I76.Game.Editor.EditorCamera { Name = "Camera", Far = 20000, Near = 0.25f, Fov = 60 } : new FlyCamera { Name = "Camera", Far = 20000, Near = 0.25f, Fov = 60 };
        AddChild(_cam);
        _overlay = new DebugOverlay { Text = OverlayText, Visible = false };   // Ctrl+F3 shows it
        AddChild(_overlay);
        if (GpuTimer.Enabled) AddChild(new GpuTimer { Name = "GpuTimer" });
        if (FrameProfiler.Enabled) AddChild(new FrameProfiler { Name = "FrameProfiler" });

        if (_opt.Install is null)
        {
            var checkedDirs = string.Join("\n", _opt.Checked.Select(d => "    " + d));
            _status = "I26 could not find Interstate '76.\n\n"
                + "Copy your game folder into the I26 folder as  i76  (and  nitro  for the Nitro Pack),\n"
                + "or unpack the I26 folder into your game's folder, or put the GOG setup_*.exe next to i26,\n"
                + "or start it with:  --install <your Interstate '76 folder>\n\n"
                + "The folder must contain I76.ZFS (or nitro.zfs).\n\nFolders checked:\n"
                + (checkedDirs.Length > 0 ? checkedDirs : "    (none)");
            GD.PrintErr(_status);
            // with "Use a GOG installer...": extracted, this scene starts again on the new install
            AddChild(new InstallerScreen { Message = _status, OnInstalled = _ => GetTree().ReloadCurrentScene() });
            return;
        }
        // the build is one long frame behind the loading card: timed as a phase, so its hitch line says what it was
        var load = Hitch.Time(Hitch.Phase.Load);
        try { LoadMission(); }
        catch (System.Exception ex)
        {
            _status = $"failed to load: {ex.Message}";
            GD.PrintErr(ex.ToString());
            // from the shell, back to it saying why, rather than an empty world (a custom map missing its terrain)
            if (_launch is { Kind: not I76.Game.Shell.GameKind.Multiplayer })
            {
                load.Dispose();
                if (loading is not null) loading.QueueFree();
                _loading = false;
                BackFromLoadFailure(ex is System.IO.FileNotFoundException ? ex.Message : $"{_opt.Mission} could not be loaded: {ex.Message}");
                return;
            }
        }
        MissionBuilder.Trace("mission loaded");
        AddDropShadows();
        MissionBuilder.Trace("drop shadows");
        var loaded = I76.Game.Shell.ShellSettings.Load();
        _drawDistance = DrawDistanceHook(loaded.DrawDistance);
        _vegetation = loaded.Vegetation;   // SpawnCar applies all the settings; a mission without a car has only this
        SetMode1997(_opt.Mode1997);
        MissionBuilder.Trace("look set");
        if (_opt.Drive is not null && _builder?.World is not null)
        {
            SpawnCar(_opt.Drive);
            MissionBuilder.Trace("car spawned");
            if (_car is { } drv && _damageView is not null) _damageView.Prewarm(drv);   // as a mission's own car gets it (the capture path)
            MissionBuilder.Trace("damage view prewarmed");
            if (_builder.Scenery is { } sc) sc.Markers.Visible = false;   // the original draws no marker for its keywords
        }
        if (_editorMode && _builder?.World is not null) StartEditor();
        // From the menu the scene is rebuilt after the frame's _Process calls and that frame is still
        // drawn, so tick 0 and the camera choice happen here, or the chase view flashes before a cut-scene.
        if (_sim is not null)
        {
            if (_resumed) { _sim.Resume(); _sim.Hold = System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_HOLD") == "1"; }
            else { if (_net is null) _sim.Session.SettleCars(); _sim.FirstTick(); }
            UpdateScriptCamera();
        }
        // the plants about the first view, behind the loading card
        _loaded = true;
        MissionBuilder.Trace("first tick done");
        _veg?.Prewarm();
        MissionBuilder.Trace("vegetation prewarmed");
        _builder?.Scenery?.Trees.Prewarm();
        MissionBuilder.Trace("trees prewarmed");
        load.Dispose();
        MissionBuilder.Trace("car, first tick");
        if (MissionBuilder.TraceOn) TraceFrames(6);
        if (loading is not null) await WarmEffects();
        if (loading is not null)
        {
            // back out to black with the mission held still, then the briefing or the mission itself
            GetTree().Paused = true;
            MissionBuilder.Trace("loading card: fade out");
            await loading.FadeOut();
            GetTree().Paused = false;
            loading.QueueFree();
            MissionBuilder.Trace("loading card gone");
        }
        _loading = false;
        if (_playtestNote is { } note && _opt.Install is not null) { AddChild(new CheckpointNotice { InstallDir = _opt.Install, Faithful = _mode1997, Text = note, Hold = 4.0, Name = "PlaytestNote" }); _playtestNote = null; }
        // a checkpoint's retry goes on from where it was: the music there, no briefing
        if (_resumed) { ResumeMusic(); return; }
        if (_editorMode) return;
        // The original's start-up plays the mission's briefing (WRLD +0x04) before it starts, and the music after it
        // (a network co-op mission has none: its host's mission runs from the first player in, docs/deviations.md)
        if (_builder?.Settings is { Briefing.Length: > 0 } ws && !_opt.SkipMovies && _net is null && MoviePlayer.Find(new GameInstall(_opt.Install), ws.Briefing) is not null)
            MissionMovie(new GameInstall(_opt.Install), ws.Briefing);
        else StartMissionMusic();
    }

    /// <summary>
    /// The mission is over and the player moves on: a win plays the outro
    /// (WRLD +0x11) as the original's start-up does for state 1, then the shell comes
    /// back with the result; anything else goes back as a quit (state 2: the
    /// end menu's or escape menu's Abort Mission).
    /// </summary>
    /// <summary>
    /// The escape menu over the frozen frame: the
    /// world stops (single player), the music plays on. With <paramref name="end"/>,
    /// the mission-end menu instead.
    /// </summary>
    private void OpenMenu((string? Message, bool Cheated)? end = null)
    {
        if (_menu is not null || _ended || _builder?.ActPalette is not { } pal || _opt.Install is null) return;
        // a network game runs on under its menu (the server does not wait); the player's own input is held back instead (SimDriver.InputHeld)
        if (_net is null) GetTree().Paused = true;
        if (GetNodeOrNull<AudioStreamPlayer>("Music") is { } music) music.ProcessMode = ProcessModeEnum.Always;
        var mission = _opt.Mission ?? "";
        // no Reconfigure without a trip to go back to, with salvage management off, or in t01, t13 and t17
        bool four = _launch is not { Kind: I76.Game.Shell.GameKind.Trip } || mission.StartsWith("a01", System.StringComparison.OrdinalIgnoreCase)
            || (I76.Game.Shell.ShellSettings.Load().PlayOptions & 2) != 0 || mission.ToLowerInvariant() is "t01" or "t13" or "t17";
        var layer = new CanvasLayer { Layer = 50, ProcessMode = ProcessModeEnum.Always, Name = "EscapeMenu" };
        _menu = new EscapeMenu
        {
            Vfs = _builder.Vfs, Palette = pal, InstallDir = _opt.Install, Mission = mission, NoReconfigure = four, Faithful = _mode1997, End = end, Net = _net is not null,
            Live = s => { ApplyAudio(s); ApplyGraphics(s); }, CheatsOff = () => _cheated = false, CanRetry = CanRetry,
            Closed = o => { layer.QueueFree(); _menu = null; MenuClosed(o); },
        };
        layer.AddChild(_menu);
        AddChild(layer);
    }

    private CanvasLayer? _picture;

    /// <summary>A texture member's picture as it is meant to be seen: opaque (index 255 is a colour here) and the right way up.</summary>
    private Texture2D? Picture(string stem)
    {
        if (_builder?.Resolver?.FindByStem(stem) is not { } m || _builder.Textures?.GetRgba(m) is not { } t) return null;
        var rgba = (byte[])t.Rgba.Clone();
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        var img = Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, rgba);
        // .map and .m16 rows are stored bottom up (as the handset's are); a .vqm's top down
        if (!m.EndsWith(".vqm", System.StringComparison.OrdinalIgnoreCase)) img.FlipY();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// The map or the notepad (MissionPicture, re-m2): the game paused under it, the music playing on; the map
    /// plays cmap2 and gdsgc26 and stops them when it goes. Any key closes it and then acts as it would.
    /// </summary>
    private int DriverVariant() => GameInstall.DriverVariant(_opt.Mission, _opt.Install is { } i && new GameInstall(i).IsNitro);

    private void OpenPicture(MissionPicture.Kind kind)
    {
        Texture2D? pic;
        if (kind == MissionPicture.Kind.Map)
        {
            // WRLD +0x79 names it; none ("No map specified for this mission"): nothing happens
            if (_builder?.Settings?.Overview is not { Length: > 0 } name) return;
            pic = Picture(System.IO.Path.GetFileNameWithoutExtension(name));
        }
        else pic = Picture($"ZNPD6{DriverVariant()}01");
        if (pic is null) return;
        if (_net is null) GetTree().Paused = true;
        if (GetNodeOrNull<AudioStreamPlayer>("Music") is { } music) music.ProcessMode = ProcessModeEnum.Always;
        var layer = new CanvasLayer { Layer = 40, ProcessMode = ProcessModeEnum.Always, Name = "MissionPicture" };
        Font? lee = null;
        if (kind == MissionPicture.Kind.Notepad && _opt.Install is not null)
        {
            var f = new FontFile { Antialiasing = _mode1997 ? TextServer.FontAntialiasing.None : TextServer.FontAntialiasing.Gray };
            if (f.LoadDynamicFont((new I76.Formats.Vfs.GameInstall(_opt.Install).FindFile("Lee_____.ttf") ?? System.IO.Path.Combine(_opt.Install, "Lee_____.ttf"))) == Error.Ok) lee = f;
        }
        var objectives = new List<(string, int)>();
        if (_script?.Host.Objectives is { } ob)
            for (int i = 0; i < ob.Flags.Length; i++) objectives.Add((ob.Text.Objectives[i].Text, ob.Flags[i]));
        var view = new MissionPicture
        {
            What = kind, Picture = pic, Objectives = objectives, Lee = lee,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,   // the art's pixels kept in both looks (the text is set at the window's scale)
            Closed = k =>
            {
                layer.QueueFree();
                _picture = null;
                GetTree().Paused = false;
                // the key then acts: N on the map swaps to the notepad, M back, Esc the menu
                if (Controls.Pressed(k, "SHOW_MAP")) { if (kind != MissionPicture.Kind.Map) OpenPicture(MissionPicture.Kind.Map); }
                else if (Controls.Pressed(k, "SHOW_NOTEPAD")) { if (kind != MissionPicture.Kind.Notepad) OpenPicture(MissionPicture.Kind.Notepad); }
                else if (k.Keycode == Key.Escape) OpenMenu();
                else Callable.From(() => Input.ParseInputEvent(k)).CallDeferred();
            },
        };
        layer.AddChild(view);
        if (kind == MissionPicture.Kind.Map && _sim?.Audio is { } bank)
            foreach (var (snd, bus) in new[] { ("cmap2.wav", "SFX"), ("gdsgc26.wav", "Voice") })
                if (bank.Get(snd) is { } st) layer.AddChild(new AudioStreamPlayer { Stream = st, Bus = bus, Autoplay = true, ProcessMode = ProcessModeEnum.Always });
        _picture = layer;
        AddChild(layer);
    }

    /// <summary>The menu's outcome as the game state it set: resume (5), replay (7; T17's becomes 8), reconfigure (8), abort (2), exit (10).</summary>
    private void MenuClosed(MenuOutcome o)
    {
        var settings = I76.Game.Shell.ShellSettings.Load();
        if ((settings.PlayOptions & 0x1c) != 0) _cheated = true;
        if (o == MenuOutcome.Replay && (_opt.Mission ?? "").Equals("t17", System.StringComparison.OrdinalIgnoreCase)) o = MenuOutcome.Reconfigure;
        // a mission given on the command line: every outcome but resume and replay ends the program
        if (_cmdLine && _launch is null && o is MenuOutcome.Abort or MenuOutcome.Reconfigure) o = MenuOutcome.Exit;
        switch (o)
        {
            case MenuOutcome.Resume:
                ApplyPlayOptions(settings);
                SetCheckpointPolicy(settings, resumed: true);   // Checkpoints from Play Options: from the next one on
                if (settings.Look1997 != _lookSaved) { _lookSaved = settings.Look1997; SetMode1997(_lookSaved); }   // 1997 Mode on the Graphic Detail pad, as F2
                GetTree().Paused = false;
                break;
            case MenuOutcome.RetryCheckpoint:
                RetryCheckpoint();
                break;
            case MenuOutcome.Replay:
                _ended = true;
                _checkpoint = null;   // a new attempt
                // the same mission, car and launch kind; a shell launch lost from here goes back to the shell, never to a default mission
                // a mission from the debug list (F9) or the command line has no launch: it replays as itself
                NextMission = _launch?.Mission ?? _opt.Mission;
                if (NextMission is null && !_cmdLine) { _backToMenu = true; _lastResult = I76.Game.Shell.MissionResult.Quit; }
                GetTree().Paused = false;
                GetTree().ReloadCurrentScene();
                break;
            case MenuOutcome.Reconfigure:
                _ended = true;
                _checkpoint = null;
                (_lastLaunch, _launch, _lastResult, _lastTrip, _lastMelee) = (_launch, null, I76.Game.Shell.MissionResult.Reconfigure, null, null);
                _backToMenu = true;
                GetTree().Paused = false;
                GetTree().ReloadCurrentScene();
                break;
            case MenuOutcome.Abort when _net is not null:
                BackFromNetwork(NetRows(), null);   // Leave Game: the tree's exit says goodbye (a host's closes the game for the others)
                break;
            case MenuOutcome.Abort:
                EndMission(false);
                break;
            case MenuOutcome.Exit:
                GetTree().Quit();
                break;
        }
    }

    /// <summary>The play options the mission uses: Unlim. Ammo for the player's guns, Arcade Phys. as the car's 0x10000.</summary>
    private Func<I76.Game.Sim.Modern.ModernPhysics>? _modernSetup;
    private I76.Game.Sim.Modern.ModernPhysics? _modern;

    /// <summary>The player's car on the modern physics (docs/modern-physics.md) or back on the original's model.</summary>
    private void EnableModern(bool on)
    {
        on &= _sim?.Session.Options.ModernPhysics != false && _sim?.World.Player?.ObjectClass == 1;   // a helicopter flies its own model   // a session may keep to the original's physics (multiplayer)
        if (on && _modern is null && _modernSetup is not null && _car is not null)
        {
            _modern = _modernSetup();
            AddChild(_modern);
            _car.LivePose = _modern.LivePose;
        }
        if (_modern is not null) _modern.Enabled = on;
    }

    private void ApplyPlayOptions(I76.Game.Shell.ShellSettings s)
    {
        if (_sim is not null) _sim.AutoReverse = s.AutoReverse;
        I76.Game.ForceFeedback.Enabled = s.ForceFeedback;
        I76.Game.Ui.ChaseCrosshair.Enabled = s.ChaseCrosshair;
        I76.Game.ForceFeedback.Held = () => _menu is not null || _picture is not null || _ended;
        // Modern Physics, flipped from Play Options as by F7 (EnableModern keeps a network session on the original's)
        if (_modernSetup is not null && s.ModernPhysics != (_modern?.Enabled == true)) EnableModern(s.ModernPhysics);
        // the cheats (Unlim. Ammo / Armor / Chas. Rein.) are for the player and never in a network session, as the original's
        // tests; Unlim. Wiper Fluid (0x20) has no reader in either game
        if (_sim?.Combat is { } c)
        {
            c.UnlimitedAmmo = (s.PlayOptions & 4) != 0 && !c.NetSession;
            if (c.Damage is I76.Sim.Damage.DamageModel dm)
            {
                dm.UnlimitedArmour = (s.PlayOptions & 8) != 0;
                dm.UnlimitedChassis = (s.PlayOptions & 0x10) != 0;
            }
        }
        if (_sim?.World.Player is { } p)
            p.Flags = (s.PlayOptions & 1) != 0 ? p.Flags | I76.Sim.Vehicles.VehicleFlags.LoadBasedCornering : p.Flags & ~I76.Sim.Vehicles.VehicleFlags.LoadBasedCornering;
    }

    /// <summary>
    /// Graphic Detail as this port honours it: Monitor Brightness (+0xc, 3 as
    /// recorded; the 1997 look's transfer curve, MonitorBrightness.CardCurve, the
    /// modern look's brightness 7% a step), Rear Mirror
    /// (+0xd, the mirror's starting mode) and Ext. Instrum. Disp. (+0xe, the
    /// 2D panels outside the cockpit).
    /// </summary>
    private void ApplyGraphics(I76.Game.Shell.ShellSettings s)
    {
        I76.Game.Assets.MaterialLibrary.ApplyCarInteriors(s.CarInteriors);
        if (_modernEnv is not null) { _modernEnv.AdjustmentEnabled = true; _modernEnv.AdjustmentBrightness = ModernBrightness(s.Graphics[0xc]); }
        if (_env1997 is not null)
        {
            _env1997.AdjustmentEnabled = true;
            _env1997.AdjustmentBrightness = 1f;
            _env1997.AdjustmentColorCorrection = CardCurve(s.Graphics[0xc]);
        }
        if (_pit is not null) _pit.MirrorSetting = s.Graphics[0xd];
        _extInstruments = s.Graphics[0xe] != 0;
        _shadowDetail = s.Graphics[0x6];
        _terrainDetail = s.Graphics[0x2];
        _antialias = s.Graphics[0x8];
        MouseCamera.Apply(s);
        FrameLimiter.Apply(s);
        _vegetation = s.Vegetation;
        _drawDistance = DrawDistanceHook(s.DrawDistance);
        ApplyDrawDistance();
        ApplyModernQuality();
        ApplySoftware(s);   // MainSoftware.cs
    }

    // Graphic Detail's rows that trade the modern look's cost: Shadow Detail (+0x6: Off, Low, High), Terrain Detail
    // (+0x2: Low, High), Anti-Aliasing (+0x8, the port's: Off, 2x, 4x). The 1997 look ignores them.
    private int _shadowDetail = 1, _terrainDetail = 0, _antialias = 2, _vegetation = 1;

    /// <summary>
    /// The modern look's quality settings: High shadow detail is the sun's shadow map and SSAO, Low the shadow
    /// map alone (SSAO makes the depth pre-pass draw every surface's normals, the terrain's whole material for
    /// them: 3.5-6 ms a frame on an integrated GPU at 1080p), Off neither; Terrain Detail Low leaves out the
    /// 1.25 m level near the camera; Anti-Aliasing is the MSAA. Then the I76_* overrides (RenderTweaks).
    /// </summary>
    private void ApplyModernQuality()
    {
        if (_mode1997 || _sun is null) return;
        _sun.ShadowEnabled = _shadowDetail > 0;
        UpdateDrops();
        _modernEnv.SsaoEnabled = _shadowDetail > 1 && !RenderBackend.Compatibility;   // Compatibility has no SSAO
        if (_builder?.Terrain is { } t) t.FinestLevel = _terrainDetail > 0 ? 0 : 1;
        GetViewport().Msaa3D = _antialias switch { 0 => Viewport.Msaa.Disabled, 1 => Viewport.Msaa.Msaa2X, _ => Viewport.Msaa.Msaa4X };
        RenderTweaks.ApplyModern(_modernEnv, _sun, GetViewport());
        UpdateVegetation();
    }

    private VegetationNode? _veg;
    private int _vegLevel = -1;
    /// <summary>The mission is built and its first tick done: from now on a vegetation (re)build places its near chunks at once.</summary>
    private bool _loaded;

    /// <summary>
    /// The modern look's plants (docs/modern-vegetation.md) at Graphic Detail's Vegetation (I76_VEG over it): made the
    /// first time the modern look shows with them on (a 1997 start makes none until the switch), hidden in the 1997 look.
    /// </summary>
    private void UpdateVegetation()
    {
        // the placed trees (Seminole's pines) are the map's, not the setting's: tinted for the ground once the modern look shows
        if (!_mode1997 && _builder is { ModernReady: true } tb) tb.Scenery?.Trees.Show(tb);
        MissionBuilder.Trace("trees shown");
        int level = VegetationNode.LevelFor(_vegetation);
        if (_mode1997 || level == 0)
        {
            if (_veg is not null) { if (level == 0 && _vegLevel != 0) _veg.SetLevel(0); _veg.Visible = false; }
            if (level == 0) _vegLevel = 0;
            return;
        }
        if (_veg is null)
        {
            if (_builder is not { ModernReady: true } b || b.Terrain?.Surface is null) return;
            _veg = VegetationNode.Create(b, level);
            if (_veg is null) return;
            _worldRoot.AddChild(_veg);
            _vegLevel = level;
            _status += "\n" + _veg.Stats;
            if (_loaded) _veg.Prewarm();
            return;
        }
        if (level != _vegLevel) { _veg.SetLevel(level); _vegLevel = level; if (_loaded) _veg.Prewarm(); }
        _veg.Visible = true;
    }

    private bool _extInstruments = true;

    /// <summary>The audio levels on their buses: Music, SFX and Voice, level 1 off, 8 as recorded, 2 dB a step.</summary>
    public static void ApplyAudio(I76.Game.Shell.ShellSettings s)
    {
        void Bus(string name, int level)
        {
            int i = AudioServer.GetBusIndex(name);
            if (i < 0) { AudioServer.AddBus(); i = AudioServer.BusCount - 1; AudioServer.SetBusName(i, name); AudioServer.SetBusSend(i, "Master"); }
            AudioServer.SetBusMute(i, level < 2);
            AudioServer.SetBusVolumeDb(i, (level - 8) * 2f);
        }
        // the buses sum past full scale (an explosion over the engine peaks at +18 dB, ordinary driving at +1): with nothing on
        // Master the output stage hard-clips them, which is a crackle, loudest on headphones. A look-ahead limiter keeps the peak under -1 dB
        if (!Enumerable.Range(0, AudioServer.GetBusEffectCount(0)).Any(e => AudioServer.GetBusEffect(0, e) is AudioEffectHardLimiter))
            AudioServer.AddBusEffect(0, new AudioEffectHardLimiter { CeilingDb = -1f, PreGainDb = 0f, Release = 0.1f }, 0);
        I76.Game.Net.CbVoice.Active?.ApplyMic(s.CbMic);
        Bus("Music", s.Audio[0]);
        Bus("SFX", s.Audio[1]);
        Bus("Voice", s.Audio[2]);
    }

    private void EndMission(bool won)
    {
        if (_ended) return;
        _ended = true;
        _checkpoint = null;   // the attempt is over, won or abandoned
        // a melee or scenario: NSCR.DAT's row and the car's mass go back with it
        _lastMelee = null;
        if (_launch is { Kind: I76.Game.Shell.GameKind.AutoMelee or I76.Game.Shell.GameKind.Scenario } ml && _sim?.World.Player is { } mp)
        {
            var game = _script?.Melee;
            int deaths = game?.PlayerDestroyed == true || I76.Sim.Damage.DamageModel.IsDead(mp) ? 1 : 0;
            _lastMelee = new I76.Game.Shell.MeleeResult(ml.Driver, ml.Model, deaths, game?.Kills ?? 0, game?.Score ?? 0, won, mp.P.Mass);
        }
        _lastLaunch = _launch;
        _launch = null;
        _lastResult = won ? I76.Game.Shell.MissionResult.Won : I76.Game.Shell.MissionResult.Quit;
        // a won trip mission hands back the car's state and the thinned salvage (T12 writes nothing)
        _lastTrip = null;
        if (won && _salvage is { } pool && _builder?.Vfs is { } vfs && _sim?.World.Player is { } p)
        {
            // (a failure here goes on without the salvage: the mission is over either way, and _ended keeps the menu shut)
            try
            {
                var rng = new System.Random();
                var items = pool.Finish(vfs.TryRead, () => rng.Next(0x8000));
                _lastTrip = new I76.Game.Shell.TripResult(items is null ? null : I76.Sim.Trip.CarState.Capture(p), items);
                GD.Print($"field salvage: {(items is null ? "none (no state written)" : $"{items.Count} items: " + string.Join(' ', items.Select(i => $"{i.Name}:{i.Health}")))}");
            }
            catch (System.Exception ex) { GD.PrintErr($"field salvage: not taken: {ex}"); }
        }
        _backToMenu = true;
        // a mission given on the command line has no shell to go back to: every state but replay ends the program
        // Reloading would only build the same mission again, its frame count from 0, so a melee whose player
        // dies started over five seconds later and a capture never reached --quit-after.
        bool commandLine = _cmdLine && _lastLaunch is null;
        void Back()
        {
            GetTree().Paused = false;
            if (commandLine)
            {
                GD.Print($"mission over ({(won ? "won" : "quit")}) at frame {_frames}: a command-line mission ends the program");
                GetTree().Quit();
            }
            else GetTree().ReloadCurrentScene();
        }
        if (won && !_opt.SkipMovies && _builder?.Settings is { Outro.Length: > 0 } ws && _opt.Install is { } dir)
        {
            GetNodeOrNull<AudioStreamPlayer>("Music")?.Stop();
            GetTree().Paused = true;
            try { PlayMovies(new GameInstall(dir), [ws.Outro], Back); }
            catch (System.Exception ex) { GD.PrintErr($"movie {ws.Outro}: not played: {ex}"); Back(); }
        }
        else Back();
    }

    /// <summary>
    /// The mission made drivable (MissionSetup): every placed car simulated,
    /// the player's at its placement (or the requested car), and the mission
    /// script running the rest unless --ai says otherwise.
    /// </summary>
    private void SpawnCar(string requested)
    {
        if (_net is not null && _netAccept is { } accept) { SpawnNetwork(accept); return; }
        // capture hook I76_ADDON_CARS=a.vcf,b.vcf (paths): those car files in the addon overlay under their file names (a saved variant to --drive)
        if (System.Environment.GetEnvironmentVariable("I76_ADDON_CARS") is { Length: > 0 } addons)
            foreach (var path in addons.Split(',', System.StringSplitOptions.RemoveEmptyEntries)) AssetVfs.Addon[System.IO.Path.GetFileName(path)] = System.IO.File.ReadAllBytes(path);
        // capture hook I76_VCF_SWAP=car.vcf:old=new,old=new (weapon files without the .gdf): that car with other weapons
        if (System.Environment.GetEnvironmentVariable("I76_VCF_SWAP")?.Split(':', 2) is { Length: 2 } swap && _builder!.Vfs.TryRead(swap[0]) is { } swapped)
        {
            var text = System.Text.Encoding.Latin1.GetString(swapped);
            foreach (var pair in swap[1].Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).Where(x => x.Length == 2))
                text = text.Replace(pair[0] + ".gdf", pair[1] + ".gdf", System.StringComparison.OrdinalIgnoreCase);
            AssetVfs.Addon[swap[0]] = System.Text.Encoding.Latin1.GetBytes(text);
        }
        var w = _builder!.World!;
        // srand(time(NULL)) at each mission's start, as the original (RandSeed); --seed N or [port] seed fixes it
        uint randSeed = I76.Sim.Rng.RandSeed.Pick(_opt.FixedSeed);
        GD.Print($"mission {_opt.Mission}: rand seed {randSeed}{(_opt.FixedSeed is null ? " (clock)" : "")}");
        System.Collections.Generic.List<string>? roster = _launch is { Kind: I76.Game.Shell.GameKind.AutoMelee } ml ? ml.Opponents.Select(o => o.Car).ToList()
            // I76_MELEE_OPPONENTS=a.vcf,b.vcf: --melee with those opponents, for captures of a fight
            : _opt.Melee ? [.. (System.Environment.GetEnvironmentVariable("I76_MELEE_OPPONENTS") ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries)] : null;
        var setup = I76.Sim.Missions.MissionSetup.Build(_builder.Vfs, w, _builder.Assembler!, requested, _origin, script: _opt.Ai == "script",
            rand: new I76.Sim.Rng.CrtRand(randSeed), melee: roster,
            // one player at one machine: the poems may come in any order (from the same seed, so a run repeats)
            seed: unchecked((int)randSeed));
        MissionBuilder.Trace("spawn: sim world set up");
        var world = setup.World;
        var session = new I76.Sim.Session.SimSession(setup, I76.Sim.Session.SessionOptions.SinglePlayer,
            // the setting applies from this mission start; a checkpoint's retry runs at the checkpoint's rate
            _retry is { } retryOf ? I76.Sim.Save.SnapshotHeader.Read(retryOf.Snapshot).Rate : I76.Sim.Session.SimRate.FromLabel(I76.Game.Shell.ShellSettings.Load().SimRate))
        {
            // what the mission was built from, which its snapshots carry and a restore checks (docs/savestate.md)
            Build = new I76.Sim.Save.SessionBuild
            {
                Mission = (_opt.Mission ?? "").ToLowerInvariant(),
                Install = _opt.Install is { } installDir ? I76.Sim.Save.SessionBuild.InstallIdentity(new GameInstall(installDir), _opt.Mission ?? "") : 0,
                Car = string.IsNullOrEmpty(requested) ? null : requested, Script = _opt.Ai == "script", Melee = roster,
            },
        };
        I76.Game.Sim.SimClock.Rate = session.Rate;
        GD.Print($"sim rate: {session.Rate.Label} Hz, tick {session.TickSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)} s");
        // a trip mission: the car's carried damage (the vsf, applied at load in trip games), and its salvage pool
        if (_launch is { Kind: I76.Game.Shell.GameKind.Trip } tl && tl.Mission.StartsWith("t", System.StringComparison.OrdinalIgnoreCase))
        {
            if (tl.State is { } carState && world.Player is { } tp) carState.Apply(tp);
            _salvage = new I76.Sim.Trip.SalvagePool(tl.Mission, tl.NoSalvage);
            _salvage.Watch(setup.Damage, world.Player);
            if (setup.Script is { } salvScript) { var pool = _salvage; salvScript.Host.Salvaged += f => pool.Add(f, -1, true); }
        }
        MissionBuilder.Trace("spawn: session");
        var ground = setup.Ground;
        var audio = new I76.Game.Assets.AudioBank(_builder.Vfs);
        _script = setup.Script;
        if (_script is not null) _script.Host.Movie += name => MissionMovie(new GameInstall(_opt.Install!), name);
        _objectives = setup.ObjectiveText;
        // the player drives the session's one car; I76_BOOM=tick[,effect]: an explosion 15 m ahead of the player at that tick, for captures
        // (effect "kill": the player's car destroyed then, "kill-ai": the nearest other car, "hit-ai[:N]": the nearest other car hit by the player for N, default 1,
        // "hit-me[:N[:zone]]": the player's car hit for N in a zone, default the rear)
        if (world.Player is { } driven) session.AddDriver(driven);
        if (session.Options.DebugHooks) session.DebugBoom = System.Environment.GetEnvironmentVariable("I76_BOOM");
        _sim = new SimDriver { Session = session, Name = "Sim", HoldThrottle = _opt.HoldThrottle, HoldFire = _opt.HoldFire, Origin = () => _origin, Audio = audio, CameraSubject = () => _tv?.Subject };
        // the sim ticks before Main reads the script camera, so a cut-scene owns the very first frame
        _sim.ProcessPriority = -1;
        _sim.FlyCameraActive = () => _cam.Current;
        AddChild(_sim);
        AddChild(new PausedClock { Owner = this, Name = "PausedClock" });
        var playSettings = I76.Game.Shell.ShellSettings.Load();
        ApplyAudio(playSettings);
        ApplyPlayOptions(playSettings);
        ApplyGraphics(playSettings);
        MissionBuilder.Trace("spawn: settings applied");
        var carNodes = _carNodes = new System.Collections.Generic.Dictionary<I76.Sim.Vehicles.Vehicle, CarNode>();
        _audio = audio;
        AddCombatView(setup.Combat);
        MissionBuilder.Trace("spawn: combat view");
        System.Numerics.Vector3 pos = default;
        string def = "";
        CarNode MakeNode(I76.Sim.Vehicles.Vehicle vehicle, string carDef) => MakeCarNode(vehicle, carDef, ground);
        foreach (var (p, carDef, vehicle) in setup.Cars)
        {
            var node = MakeNode(vehicle, carDef);
            if (vehicle != world.Player) continue;
            pos = vehicle.Pos.ToVector3(); def = carDef;
            SetupPlayerCar(node, vehicle, carDef, setup.Combat, handguns: true);
        }
        MissionBuilder.Trace("spawn: cars made");
        if (_car is null) { _status += "\nno drivable car"; return; }
        var damageView = new DamageView { Damage = setup.Damage, Origin = () => _origin, Builder = _builder, Cars = carNodes, PlacementOf = setup.PlacementOf, Audio = audio, Ground = setup.Ground, Name = "Damage" };
        AddChild(damageView);
        _damageView = damageView;
        MissionBuilder.Trace("spawn: damage view");
        if (_car is { } warm) damageView.Prewarm(warm);
        MissionBuilder.Trace("spawn: car prewarmed");
        damageView.PrewarmWrecks(setup.Cars.Where(c => !c.Item3.IsHeli).Select(c => c.Item2), _car?.Modern ?? true);   // and the other cars' wrecks, built once
        MissionBuilder.Trace("spawn: wrecks prewarmed");
        // the gates the script slides (type-7 nodes): their meshes follow the sim's colliders
        if (_builder.Scenery is { } gateScenery)
        {
            var gateOf = new Dictionary<(I76.Formats.Assembly.Placement, string), I76.Sim.Collision.GateMotion>();
            foreach (var (so, gp) in setup.PlacementOf)
                foreach (var n in so.Nodes) if (n.Gate is { } g) gateOf[(gp, n.Name)] = g;
            if (gateOf.Count > 0) gateScenery.GateShift = (gp, name) => gateOf.TryGetValue((gp, name), out var g) ? g.Shift : default;
        }
        // a melee builds its cars as it goes: the roster now and every wave, a fresh car on a regen pad
        if (setup.Melee is { } melee)
        {
            melee.Spawned += (v, d) => carNodes[v] = MakeNode(v, d);
            melee.Removed += v => { if (carNodes.Remove(v, out var n)) { _sim.Cars.Remove(n); n.QueueFree(); } };
            melee.PlayerRenewed += v => { if (carNodes.GetValueOrDefault(v) is { } n) damageView.Restore(n); };
            melee.Start();
            MissionBuilder.Trace("spawn: melee started");
            damageView.PrewarmWrecks(melee.Roster.Concat(carNodes.Values.Select(n => n.Definition)), _car?.Modern ?? true);   // every kind of car that will fall, built once
        }
        // I76_SPAWN_AT=x,z,yaw[;x,z,yaw@s...] (game units, degrees from +Z toward +X): put the player's car there, for captures
        // (after a melee's own spawn); a stop with @s waits for mission second s (SpawnStops, _Process)
        if (session.Options.DebugHooks && System.Environment.GetEnvironmentVariable("I76_SPAWN_AT") is { } at && world.Player is { } pv)
        {
            _spawnStops.Clear();
            foreach (var stop in at.Split(';'))
            {
                var parts = stop.Split('@');
                if (parts[0].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is not { Length: 3 } a) continue;
                if (parts.Length > 1) _spawnStops.Enqueue((float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), a));
                else PlacePlayer(pv, setup.Ground, a);
            }
        }
        // a playtest from the map editor: the car under the editor's cursor if a car can stand there, else where the camera's pivot was (after a melee's own spawn)
        if (I76.Game.Editor.EditorSession.Playtest && I76.Game.Editor.EditorSession.PlaytestAt is { } ptAt && world.Player is { } ptv)
        {
            float? ptY = null;
            string? why = I76.Game.Editor.EditorSession.PlaytestNoRay;
            if (I76.Game.Editor.EditorSession.PlaytestRay is { } ray)
            {
                var doc = I76.Game.Editor.EditorSession.Doc;
                var plan = I76.Sim.Terrain.PlaytestSpawn.FromRay(setup.Ground, setup.Statics, (x, z) => doc is null || doc.Has((int)System.Math.Floor(x * I76.Formats.Assembly.MissionWorld.SampleScale), (int)System.Math.Floor(z * I76.Formats.Assembly.MissionWorld.SampleScale)), ray.Origin, ray.Dir);
                if (plan.Ok) { ptAt = [plan.At.X, plan.At.Z, ptAt[2]]; ptY = plan.At.Y; why = null; }
                else why = plan.Why;
            }
            PlacePlayer(ptv, setup.Ground, ptAt);
            if (ptY is { } hy) ptv.Pos = new I76.Sim.Math.Double3(ptAt[0], hy, ptAt[1]);   // on a bridge's deck, the deck
            // I76_PLAYTEST_DECK=1: onto the highest drive-on surface there (a bridge's deck) instead of the bare ground, for a capture of a bridge
            if (session.Options.DebugHooks && System.Environment.GetEnvironmentVariable("I76_PLAYTEST_DECK") is { Length: > 0 })
                ptv.Pos = new I76.Sim.Math.Double3(ptAt[0], setup.Ground.Query(ptAt[0], 1e4, ptAt[1]).Height, ptAt[1]);
            pos = ptv.Pos.ToVector3();
            _playtestNote = why is null ? null : $"Car at the camera's pivot: {why}";
            GD.Print($"editor playtest: car at {ptAt[0]:0}, {ptAt[1]:0} (ground {setup.Ground.BareHeight(ptAt[0], ptAt[1]):0.00}) heading {ptAt[2]:0} {(why is null ? "under the cursor" : "at the pivot (" + why + ")")}, {Time.GetTicksMsec() - I76.Game.Editor.EditorSession.SwitchedAtMs} ms after F5");
        }
        // Placeholder behaviour without the script: chase the player when within 400 units.
        if (_opt.Ai == "chase")
            foreach (var v in world.Vehicles.Where(v => v != world.Player))
                world.Ai[v] = (new I76.Sim.Ai.AiBlock { CommandSpeed = 35 }, me =>
                {
                    var p = world.Player!.Pos.ToVector3();
                    var d = me.Pos.ToVector3() - p;
                    return d.X * d.X + d.Z * d.Z < 400f * 400f ? p : null;
                });
        // a checkpoint's retry: its state over the mission just built, before the views (docs/savestate.md)
        if (ResumeCheckpoint(session, setup, damageView))
        {
            pos = world.Player!.Pos.ToVector3();
            ApplyPlayOptions(playSettings);   // the options as they are now, over the checkpoint's copies
        }
        Rebase(pos);
        AddPlayerViews(world, setup.Combat);
        ResumeViews();
        // the optional modern physics for the player's car (--modern-physics, or the setting; F7 flips it in play)
        _modernSetup = () => new I76.Game.Sim.Modern.ModernPhysics
        {
            Name = "ModernPhysics", World = world, Car = world.Player!, Statics = setup.Statics, Ground = setup.Ground,
            Cutscene = () => _sim?.Script?.Host.View.Active == true,
            Live = () => _sim?.LiveDriving(),
            Rate = session.Rate,
        };
        // (after a retry's restore: the body takes the restored car's pose and motion, ModernCar.TakeOver)
        if (_opt.ModernPhysics || playSettings.ModernPhysics) EnableModern(true);
        SetCheckpointPolicy(playSettings, _resumed);
        // the capture hook's held scene holds the body still too (it would drive on under the held sim)
        if (_resumed && _modern is not null && System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_HOLD") == "1") _modern.Enabled = false;
        var statics = setup.Statics;
        _status += $"\n{statics.Objects.Count} colliders ({statics.Objects.Sum(o => o.Polys.Count)} polygons, {statics.Objects.Count(o => o.Surfaces.Count > 0)} drive-on structures)";
        _status += $"\n{world.Vehicles.Count} cars simulated; AI: {(_script is not null ? $"the mission script ({_script.Vm?.Threads.Count ?? 0} threads)" : _opt.Ai == "chase" ? "placeholder chase" : "parked")}";
        _status += $"\ndriving {def} at {pos.X:0},{pos.Y:0.0},{pos.Z:0} -- keys as Control Configuration has them (input.map; the original drives on the numpad), Ctrl+F5 free camera, C poetry, ` mirror, Esc menu, Ctrl+Q quit";
    }

    private string? _playtestNote;
    /// <summary>The looks (1 modern, 2 1997) whose effects have been rehearsed in this run.</summary>
    private static int _warmedLooks;
    private bool _switchFrameLogged;
    private int _playFrames;
    private static readonly int _debugReturn = int.TryParse(System.Environment.GetEnvironmentVariable("I76_PLAYTEST_RETURN"), out int dr) ? dr : 0;

    /// <summary>Back from a playtest to the map editor (F5, Esc, or I76_PLAYTEST_RETURN's frame).</summary>
    private void LeavePlaytest()
    {
        I76.Game.Editor.EditorSession.Playtest = false;
        I76.Game.Editor.EditorSession.PlaytestAt = null;
        I76.Game.Editor.EditorSession.SwitchedAtMs = Time.GetTicksMsec();
        if (ReturnToStashedEditor()) return;   // the editor's scene was waiting (MainStash.cs)
        GetTree().Paused = false;
        ReleaseCursor();
        GetTree().ReloadCurrentScene();
    }

    private System.Collections.Generic.Dictionary<I76.Sim.Vehicles.Vehicle, CarNode> _carNodes = [];
    private I76.Game.Assets.AudioBank? _audio;

    /// <summary>Combat's looks and sounds for the session's rounds and events.</summary>
    private CombatView? _combatView;

    private void AddCombatView(I76.Sim.Weapons.Combat combat, Func<float>? predictedAlpha = null)
    {
        AddChild(_combatView = new CombatView { Combat = combat, Origin = () => _origin, Audio = _audio, Sprites = new EffectSprites(_builder!), CarOf = v => _carNodes.GetValueOrDefault(v),
            Alpha = () => _sim?.Alpha ?? 1f, PredictedAlpha = predictedAlpha, Modern = () => !_mode1997, Builder = _builder, Name = "Combat",
            Inside = v => _car is { } c && c.Sim == v && c.CockpitCamera?.Current == true,
            SceneLight = Mathf.Clamp(_builder!.Light.Intensity * 1.5f, 0.25f, 1f) });
    }

    /// <summary>
    /// The first explosion, the first death and the first fire of a mission each cost the renderer a compile of their materials
    /// (and the game the first run of the code behind them): hundreds of milliseconds in the frame that showed them. Rehearsed here
    /// for a few frames, behind the loading card (the world is paused and drawn under it), then taken away: nothing of it is left.
    /// </summary>
    private async System.Threading.Tasks.Task WarmEffects()
    {
        if (System.Environment.GetEnvironmentVariable("I76_NO_WARMUP") == "1" || GetViewport().GetCamera3D() is not { } cam) return;
        // a second playtest in a run: the renderer has made these pipelines and the views' code has run, in this look, once already
        if (I76.Game.Editor.EditorSession.Playtest && (_warmedLooks & (_mode1997 ? 2 : 1)) != 0 && System.Environment.GetEnvironmentVariable("I76_WARM_AGAIN") != "1") { GD.Print("effects warm-up: done already in this look"); return; }
        _warmedLooks |= _mode1997 ? 2 : 1;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        GetTree().Paused = true;
        // all of it at a spot far below the world, out of sight (the renderer makes a material's pipelines when its instance is made, seen
        // or not), and on stand-ins of its own: nothing of the game's is touched, nothing is left to take away but what was made
        var away = cam.GlobalPosition + Vector3.Down * 400f;
        _combatView?.Warm(away);
        _combatView?.WarmImpacts(away);
        // the death is rehearsed on a stand-in for the player's car (never in the sim's list, freed after)
        // so the real car is never burnt and restored
        CarNode? stage = null;
        if (_car is { Definition: { } def, Sim: { IsHeli: false } sv } && _builder is { } sb && sb.CarParts.Get(def, false) is { } sp)
        {
            stage = new CarNode { Name = "WarmStage", Definition = def, Modern = !_mode1997 };
            AddChild(stage);
            stage.Init(sv, sp.Body);
            stage.GlobalPosition = away;
            _damageView?.Warm(stage);
        }
        // the views' own per-frame code (the cockpit's panels, the HUD, the shadows, the weeds, the combat and damage views) runs for these
        // frames too: its first run is the first frame of play, a 150 ms stall (the code compiled, the panels' textures made)
        var views = new List<Node>();
        void Collect(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c.GetType().Name is "Cockpit" or "Hud" or "TumbleweedNode" or "DropShadows" or "Vegetation" or "SkidMarks" or "GroundClutterView" or "CombatView" or "DamageView" or "DamageSmoke" or "SmokeLayer" or "WreckFire")
                    views.Add(c);
                if (c is not CarNode) Collect(c);
            }
        }
        Collect(this);
        var was = views.Select(v => v.ProcessMode).ToList();
        foreach (var v in views) v.ProcessMode = Node.ProcessModeEnum.Always;
        for (int i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (int i = 0; i < views.Count; i++) if (IsInstanceValid(views[i])) views[i].ProcessMode = was[i];
        if (stage is not null) { _damageView?.EndWarm(stage); stage.QueueFree(); }
        _combatView?.EndWarm();
        GetTree().Paused = false;
        GD.Print($"effects warmed up in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
    }

    /// <summary>The VLOC kind that places a car's headlight pool and night light.</summary>
    private const int LightPoolVloc = 38;
    private (Mesh, Material)? _lightPool;
    private bool _lightPoolTried;

    /// <summary>
    /// The headlight pool every lit car lays ahead of it at night: the object "hdlt_msk", whose
    /// hdlt_msk.geo is its shape (hdlv_msk.geo, twice as wide, goes into its slot 4 and is not used here), drawn as
    /// light_pool_1997.gdshader; null if the shape is missing.
    /// </summary>
    private (Mesh, Material)? LightPool()
    {
        if (_lightPoolTried) return _lightPool;
        _lightPoolTried = true;
        if (_builder!.Vfs.TryRead("hdlt_msk.geo") is not { } gd) return null;
        var g = I76.Formats.Geo.GeoMesh.Parse(gd);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var f in g.Faces)
            for (int i = 1; i + 1 < f.Vertices.Length; i++)
                foreach (var fv in new[] { f.Vertices[0], f.Vertices[i], f.Vertices[i + 1] })
                    st.AddVertex(Coords.ToGodot(g.Vertices[fv.Index]));
        return _lightPool = (st.Commit(), new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/light_pool_1997.gdshader") });
    }

    /// <summary>A car on screen: its body, lamps, turrets, wheels, headlights and engine sound, drawn by the SimDriver.</summary>
    /// <summary>Make a kind of car's meshes ahead of its first spawn (behind the loading card): the first of a kind also loads its textures and makes its materials.</summary>
    private void PrewarmCars(IEnumerable<string> carDefs)
    {
        foreach (var def in carDefs.Distinct(System.StringComparer.OrdinalIgnoreCase))
        {
            try { _builder!.CarParts.Get(def); }
            catch (System.Exception e) { GD.Print($"car prewarm {def}: {e.Message}"); }
        }
    }

    private CarNode MakeCarNode(I76.Sim.Vehicles.Vehicle vehicle, string carDef, I76.Sim.Collision.WorldGround ground)
    {
        var builder = _builder!;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); string tl = "";
        void Lap(string n) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); tl += $" {n} {(t - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0}"; }
        // I76_CAR_CONDITION=N: every car shows damage level N, for off-screen captures of the damage looks
        char? cond = System.Environment.GetEnvironmentVariable("I76_CAR_CONDITION") is { Length: 1 } cs ? cs[0] : null;
        CarPartsCache.Parts parts;
        if (cond is { } cc)
        {
            var model = new I76.Formats.Assembly.ModelAssembler(builder.Vfs) { SkipParts = [], PartCondition = _ => cc }.Assemble(carDef)!;
            parts = builder.CarParts.Make(model, vehicle.IsHeli, CarNode.BuildLamps(builder.Meshes!, model, new I76.Formats.Assembly.ModelAssembler(builder.Vfs) { SkipParts = [] }.Assemble(carDef)));
        }
        else parts = builder.CarParts.Get(carDef, vehicle.IsHeli);
        var model2 = parts.Model;
        Lap("parts");
        var node = new CarNode { Name = System.IO.Path.GetFileNameWithoutExtension(carDef), Definition = carDef, GroundForDisplay = (x, z) => ground.Query(x, 1e6, z).Height };
        _carNodes[vehicle] = node;
        AddChild(node);
        Lap("addchild");
        node.Init(vehicle, parts.Body);
        Lap("init");
        node.SetLamps(parts.Lamps);
        Lap("setlamps");
        foreach (var (w, m) in parts.Weapons) node.AddWeapon(w, m);
        Lap("addweapons");
        // a turret's gun on its own node; its base is among the weapon's fixed parts
        foreach (var (w, m, pivot) in parts.Turrets) node.AddTurret(w, m, pivot);
        if (vehicle.IsHeli && parts.Rotors is { } rm)
        {
            // a helicopter's blades are the animated flat discs; the modern look draws a spinning, blurred disc instead
            node.AddRotors(rm, CarNode.Rotors(model2), Mathf.Clamp(builder.Light.Intensity * 1.5f, 0.25f, 1f));
        }
        Lap("meshes");
        // a helicopter rides on its skids: the .vcf's WLOC slots are the car format's, with nothing to draw
        if (parts.Wheels is { } shared) { foreach (var (slot, wm, centre) in shared) node.AddWheel(slot, wm, centre); }
        else foreach (var loc in model2.Locations.Where(l => !vehicle.IsHeli && l.Tag == "WLOC"))
        {
            var t = loc.Transform.Translation;
            if (MathF.Abs(t[0]) + MathF.Abs(t[1]) + MathF.Abs(t[2]) < 1e-6f) continue;
            if (builder.Meshes!.BuildWheel(model2, loc.Index) is { } wm) node.AddWheel(loc.Index, wm, new System.Numerics.Vector3(t[0], t[1], t[2]));
        }
        Lap("wheels");
        bool player = vehicle == _sim!.World.Player;
        // a gun emplacement (type 8) has no lamps and no engine: the emplacement's post-load starts no sound
        if (!vehicle.IsEmplacement)
        {
            node.AddHeadlights(player, HeadlampCentres(model2));
            // the modern look's police light bar (every Sheriff car, the player's, an AI's or a remote one: all come through here)
            if (I76.Game.Sim.SirenBar.Build(builder.Meshes!, model2, night: !builder.Light.IsDay) is { } siren) node.AddSirenBar(siren);
            node.AddRearLamps(LampCentres(model2, "BLGT", rear: true) ?? LampCentres(model2, "TLGT", rear: true));
            // VLOC kind 38: the headlight pool, made only under a night preset
            if (!builder.Light.IsDay && model2.Locations.FirstOrDefault(l => l.Tag == "VLOC" && l.Index == LightPoolVloc) is { } pv
                && LightPool() is var (poolMesh, poolMat))
                node.AddLightPool(poolMesh, poolMat, Coords.ToGodot(new System.Numerics.Vector3(pv.Transform.Translation[0], pv.Transform.Translation[1], pv.Transform.Translation[2])));
        }
        Lap("lights");
        node.Modern = !_mode1997;
        // a respray: the car's own colours (a network game's, from its car list), else the variant's sidecar (a local game's cars); the factory paint costs nothing
        if ((vehicle.Paint ?? (_net is null ? I76.Game.Shell.CarPaints.Of(builder.Vfs, carDef) : null)) is { IsNone: false } paint) builder.Meshes!.Paint(node, model2, paint);
        if (!vehicle.IsEmplacement && I76.Sim.Vehicles.VehicleFactory.EngineSound(builder.Vfs, vehicle.EngineNumber) is { } snd)
            node.SetEngineSound(_audio?.Get(snd, loop: true), player, I76.Sim.Vehicles.VehicleFactory.EngineRevable(builder.Vfs, vehicle.EngineNumber));
        if (!vehicle.IsEmplacement)
            Voice3D.Trace($"car {vehicle.Definition}{(player ? " (player)" : "")}: engsnd row {vehicle.EngineNumber}, engine {I76.Sim.Vehicles.VehicleFactory.EngineSound(builder.Vfs, vehicle.EngineNumber) ?? "none"}"
                + $"{(I76.Sim.Vehicles.VehicleFactory.EngineRevable(builder.Vfs, vehicle.EngineNumber) ? "" : " (not revable)")}, horn and siren {I76.Sim.Vehicles.VehicleFactory.HornSound(builder.Vfs, vehicle.EngineNumber) ?? "none"}");
        _sim.Cars.Add(node);
        Lap("audio");
        if (I76.Game.Hitch.LimitMs < 200) GD.Print($"spawn {carDef} frame {Engine.GetProcessFrames()}:{tl}");
        return node;
    }

    /// <summary>
    /// The player's own car: the chase camera's collider, the in-car view
    /// (interior, gauge needles, the live dashboard) and, with
    /// <paramref name="handguns"/>, the driver's handguns.
    /// </summary>
    private void SetupPlayerCar(CarNode node, I76.Sim.Vehicles.Vehicle vehicle, string carDef, I76.Sim.Weapons.Combat combat, bool handguns)
    {
        var builder = _builder!;
        var model = new I76.Formats.Assembly.ModelAssembler(builder.Vfs) { SkipParts = [] }.Assemble(carDef)!;
        _car = node; _sim!.Player = node;
        _playerCollider = Collider(model, vehicle);
        if (System.Environment.GetEnvironmentVariable("I76_DEBUG_CAM") is not null)
        {
            GD.Print($"collider centre {_playerCollider.Centre} radius {_playerCollider.Radius}");
        }
        var interior = new I76.Formats.Assembly.ModelAssembler(builder.Vfs) { Detail = '5' }.Assemble(carDef);
        var vloc = model.Locations.FirstOrDefault(l => l.Tag == "VLOC" && l.Index == 40) ?? model.Locations.FirstOrDefault(l => l.Tag == "VLOC");
        var eye = vloc is null ? new System.Numerics.Vector3(-0.4f, 1.25f, 0) : new System.Numerics.Vector3(vloc.Transform.Translation[0], vloc.Transform.Translation[1], vloc.Transform.Translation[2]);
        static bool Ring(I76.Formats.Assembly.ModelPart p, char n) => p.NodeName.Length == 8 && p.NodeName[4..7].Equals("RTC", System.StringComparison.OrdinalIgnoreCase) && p.NodeName[7] == n;
        // the roof panels (weapons, specials, damage, radar) drawn nearer than the body: see I76.Formats.Assembly.PanelPriority
        if (interior is not null)
            for (int i = 0; i < interior.Parts.Count; i++)
                if (I76.Formats.Assembly.PanelPriority.PullForward(interior.Parts[i], eye))
                    interior.Parts[i] = interior.Parts[i] with { ToModel = interior.Parts[i].ToModel.ScaledAbout(eye, I76.Formats.Assembly.PanelPriority.Scale) };
        // the hood (V5 FT TP, the front zone's damage texture) apart, to be rebuilt as the front is hit
        static bool Hood(I76.Formats.Assembly.ModelPart p) => p.Mesh.Faces.Any(f => I76.Formats.Assembly.DamageTextures.Zone(f.Texture) >= 0);
        node.AddCockpit(interior is null ? null : builder.Meshes!.Build(interior, wheels: false, include: p => !LowResTwin(interior, p.NodeName) && !HandgunMuzzle(p.NodeName) && !Ring(p, '6') && !Hood(p)), eye,
            interior is null ? null : builder.Meshes!.Build(interior, wheels: false, include: p => Ring(p, '1')),
            interior is null ? null : builder.Meshes!.Build(interior, wheels: false, include: p => Ring(p, '6')),
            interior is null ? null : frameOf => builder.Meshes!.Build(interior, wheels: false, include: p => !LowResTwin(interior, p.NodeName) && Hood(p), frameOf: frameOf));
        // the gauge needles: needle.geo at the VLOCs of kind 35 (speedo) and 36 (tacho), turned every frame
        if (interior is not null && builder.Assembler?.Geo("needle") is { } needleGeo)
        {
            var nm = new I76.Formats.Assembly.AssembledModel { Definition = "needle", Kind = "sdf", ObjectCode = interior.ObjectCode };
            nm.Parts.Add(new I76.Formats.Assembly.ModelPart("needle", needleGeo, I76.Formats.Assembly.Affine.Identity));
            var needleMesh = builder.Meshes!.Build(nm, wheels: false);
            foreach (var l in model.Locations.Where(l => l.Tag == "VLOC" && l.Index is 35 or 36))
                node.AddNeedle(l.Index - 35, needleMesh, Coords.ToGodot(I76.Formats.Assembly.Affine.From(l.Transform)));
        }
        // the live dashboard: the layout the car's VDF names at VDFC +0x40, vpit_1.elt for every car
        string eltName = "vpit_1.elt";
        if (builder.Vfs.TryRead(carDef) is { } vcfData && new I76.Formats.Bwd2.Bwd2File(vcfData).First("VCFC") is { } vc
            && builder.Vfs.TryRead(I76.Formats.Bin.CStr(vcfData, vc.PayloadOffset + 0x10, 13)) is { } vdfData
            && new I76.Formats.Bwd2.Bwd2File(vdfData).First("VDFC") is { } vd && I76.Formats.Bin.CStr(vdfData, vd.PayloadOffset + 0x40, 13) is { Length: > 0 } named)
            eltName = named;
        if (builder.Vfs.TryRead(eltName.Length > 0 ? eltName : "vpit_1.elt") is { } eltData && builder.Resolver is { } res && builder.Textures is { } texf && builder.Materials is { } mats)
        {
            var layout = I76.Formats.Cockpit.EltLayout.Parse(System.Text.Encoding.Latin1.GetString(eltData));
            var pit = new I76.Game.Sim.Cockpit
            {
                Car = vehicle, Node3D = node, Panels = new I76.Game.Sim.CockpitPanels(layout, res, texf, mats),
                Resolver = res, Textures = texf, Materials = mats, Combat = combat, Palette = builder.ActPalette, Audio = _audio, Name = "Cockpit",
                LightVariant = DriverVariant(), Instruments = () => _extInstruments,
                MirrorBackground = builder.ActPalette is { } ap ? Color.Color8(ap[0xea].R, ap[0xea].G, ap[0xea].B) : Colors.Black,
            };
            pit.MirrorSetting = I76.Game.Shell.ShellSettings.Load().Graphics[0xd];
            node.Dash3D = I76.Game.Shell.ShellSettings.Load().Dash3D;
            AddChild(pit);
            pit.Faithful = _mode1997;
            _pit = pit;
            _sim.InCockpit = () => node.CockpitCamera?.Current == true;
            _sim.ArmWanted = pit.WantArm;
            // the driver's handguns, one per arm the cockpit has
            if (handguns && interior is not null && builder.Vfs.TryRead(carDef) is { } vcfBytes && new I76.Formats.Bwd2.Bwd2File(vcfBytes).First("VCFC") is { } vcc
                && builder.Vfs.TryRead(I76.Formats.Bin.CStr(vcfBytes, vcc.PayloadOffset + 0x10, 13)) is { } vdfBytes)
            {
                bool Has(string code) => interior.Parts.Any(p => p.NodeName.Length >= 8 && p.NodeName[4..8].Equals(code, StringComparison.OrdinalIgnoreCase));
                vehicle.Armory ??= new I76.Sim.Weapons.Armory();
                vehicle.Armory.AddHandguns(new I76.Formats.Bwd2.Bwd2File(vdfBytes), Has("GUNR"), Has("GUNL"));
            }
        }
    }

    /// <summary>The views of the player's car: the HUD, the chase camera (the current one), the script's camera, and the cockpit if asked for.</summary>
    /// <summary>The mission the views' wheel zoom was last reset for (static: a retry rebuilds the scene): with View Zoom Reset off, a retry keeps it.</summary>
    private static string? _zoomMission;

    private void AddPlayerViews(I76.Sim.SimWorld world, I76.Sim.Weapons.Combat combat)
    {
        AddChild(new CabClip { Name = "CabClip", Player = () => _car, Cars = () => _carNodes.Values });
        var wiggle = new Wiggle { Name = "Wiggle", InCockpit = () => _car?.CockpitCamera?.Current == true, Player = () => _car?.Sim };
        AddChild(wiggle);
        if (combat.Damage is I76.Sim.Damage.DamageModel wdm) wdm.Damaging += wiggle.Damaging;
        AddChild(new Hud { Car = () => _car?.Sim, Debug = () => _overlay?.Visible == true, Combat = combat, Origin = () => _origin, CarOf = v => _carNodes.GetValueOrDefault(v), InCockpit = () => _car?.CockpitCamera?.Current == true, ExtInstruments = () => _extInstruments, Pit = () => _pit, Cutscene = () => _scriptCam?.Current == true, Transmitting = () => _netHud?.CbTransmitting == true, Network = () => _net is not null, HostAllowsCrosshair = () => _net?.Client.CrosshairAllowed == true, Script = _script, Objectives = _objectives, Name = "Hud", Visible = System.Environment.GetEnvironmentVariable("I76_NO_OVERLAY") != "1",
            Texture = n => _builder?.Resolver?.FindByStem(n) is { } m ? _builder.Textures?.Get(m)?.Texture : null, Faithful = () => _mode1997, Palette = _builder?.ActPalette, Variant = DriverVariant(), ReadFile = n => _builder?.Vfs.TryRead(n) });
        AddMirrorTarget(combat);
        // a mission (or, with View Zoom Reset, a respawn) starts every view at the original's framing
        if (!MouseCamera.KeepZoom || _zoomMission != _opt.Mission) { MouseCamera.ResetAllZooms(); _zoomMission = _opt.Mission; }
        _zoomView = null;
        _chase = new ChaseCamera { Target = _car, Far = 20000, Near = 0.25f, Name = "Chase",
            LocalCentre = Coords.ToGodot(_playerCollider.Centre), Radius = _playerCollider.Radius,
            // the camera works relative to the floating origin: the terrain is asked at the world point and answered relative again
            Ground = (x, z) => world.Ground.BareHeight(x + _origin.X, z + _origin.Z) - _origin.Y,
            // the death orbit; in a network game the winner's too (the original's start-up: camera 0xfefe, mode 8)
            Orbit = () => _car is { } c && ((c.Sim.Flags & I76.Sim.Vehicles.VehicleFlags.Dead) != 0 || _netWinOrbit) };
        AddChild(_chase);
        _preset = new PresetCamera { Target = _car, Name = "Preset", LocalCentre = _chase.LocalCentre, Radius = _chase.Radius, Ground = _chase.Ground, Head = () => _pit?.HeadAngles ?? default };
        AddChild(_preset);
        _tv = new TargetViewCamera
        {
            Name = "TargetView", PlayerNode = () => _car, CarOf = v => _carNodes.GetValueOrDefault(v), Origin = () => _origin, Alpha = () => _sim?.Alpha ?? 1f,
            Ground = _chase.Ground, RadarTarget = RadarTargetNow, LatestRound = () => _latestRound, InFlight = r => _sim?.Combat is { } cb && (cb.Rounds.Contains(r) || cb.PredictedRounds.Contains(r)),
            Expired = PopViewStack,
        };
        AddChild(_tv);
        _chase.Position = _car!.Position + new Vector3(0, 4, 9);
        _chase.MakeCurrent();
        // the engine's one projection (the original's camera projection, fov π/2 across a 4:3 view): 73.7° high
        _scriptCam = new Camera3D { Far = 20000, Near = 0.25f, Fov = 73.74f, Name = "ScriptCamera" };
        AddChild(_scriptCam);
        // The world camera rebuild starts every mission in view 0x1a, whose setup is the cockpit's;
        // the external-view choice is never kept (it is not saved), the 3D/2D dash is (I76PLYR.DEF, ShellSettings.Dash3D)
        if (!_opt.Chase && _car.HasInterior) { _car.SetCockpit(true); _cockpitFlag = true; }
    }

    /// <summary>The same brackets and health bar inside the rear-view mirror (inMirror = 1), on the cockpit there is.</summary>
    private void AddMirrorTarget(I76.Sim.Weapons.Combat combat) =>
        _pit?.AddMirrorOverlay(new TargetIndicator { Player = () => _car?.Sim, Combat = combat, Origin = () => _origin, CarOf = v => _carNodes.GetValueOrDefault(v), Name = "MirrorTarget", Mirror = true,
            Look1997 = new TargetIndicator1997 { On = () => _mode1997, Combat = combat, Origin = () => _origin, CarOf = v => _carNodes.GetValueOrDefault(v), Texture = n => _builder?.Resolver?.FindByStem(n) is { } m ? _builder.Textures?.Get(m)?.Texture : null, Palette = _builder?.ActPalette } });

    /// <summary>
    /// A dead car's view: the original turns the player's camera to the death orbit
    /// whatever view it was in, so the in-car view gives way to the chase camera's orbit round the wreck, and stays so.
    /// </summary>
    private void LeaveDeadCockpit()
    {
        if (_car is { } c && (c.Wrecked || _netWinOrbit) && (c.CockpitCamera?.Current == true || _preset?.Current == true || _tv?.Current == true) && _chase is not null)
        {
            // a network player who is coming back (not the winner's orbit) gets the view they had when they died back with the new car (#109)
            if (_net is not null && c.Wrecked && !_netWinOrbit) _viewBeforeDeath ??= CurrentViewCmd();
            c.SetCockpit(false); _chase.Fixed = null; _tvMode = 0; _chase.MakeCurrent(); _cockpitFlag = false;
        }
        if (_viewBeforeDeath is { } was && _car is { Wrecked: false } && !PlayerDead && !_netWinOrbit)
        {
            _viewBeforeDeath = null;
            SelectView(was is 0xd ? 0xe : was is 0x12 or 0x18 or 0x19 ? 0xf : was);
        }
    }

    /// <summary>The view (CurrentViewCmd) a network player was in when their car was destroyed, put back when the next one is up.</summary>
    private int? _viewBeforeDeath;

    /// <summary>
    /// The cockpit model carries each dashboard panel twice, a low-resolution
    /// part (CMP3, SPC3, SYS3, WEP3, RTC1: the 320 textures) and its '6' twin
    /// (the 640 ones) at the same place; only the high-resolution one is drawn.
    /// </summary>
    private static bool LowResTwin(I76.Formats.Assembly.AssembledModel interior, string node)
    {
        if (node.Length < 8 || node[7] == '6' || !char.IsDigit(node[7])) return false;
        var twin = node[..7] + "6";
        return interior.Parts.Any(p => p.NodeName.Equals(twin, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The script's camera, while it holds the view (pushCam ... popCam).</summary>
    private void UpdateScriptCamera()
    {
        if (_script is null || _scriptCam is null || System.Environment.GetEnvironmentVariable("I76_NO_SCRIPT_CAM") == "1") return;   // the capture hook: keep the cockpit through the intro cutscene
        var view = _script.Host.View;
        if (_car is not null) _car.SeenFromOutside = view.Active;
        if (!view.Active)
        {
            if (_scriptCam.Current) (_car?.CockpitCamera is { } k && _cockpitBeforeScript ? k : _presetBeforeScript ? _preset : _tvBeforeScript && _tvMode != 0 ? _tv : (Camera3D?)_chase)?.MakeCurrent();
            return;
        }
        if (!_scriptCam.Current)
        {
            // pushCam presses B first when the binoculars are up: the cockpit is what is saved
            if (_pit?.Binoculars == true) { _pit.SetBinoculars(false); _cockpitFlag = false; _lastPreset = 0xd; }
            _cockpitBeforeScript = _car?.CockpitCamera?.Current == true;
            _presetBeforeScript = _preset?.Current == true;
            _tvBeforeScript = _tv?.Current == true;
            _scriptCam.MakeCurrent();
        }
        float a = _sim?.Alpha ?? 1f;
        var eye = Coords.ToGodot(System.Numerics.Vector3.Lerp(view.PrevEye, view.Eye, a) - _origin);
        if (view.LookAt is { } at)
        {
            var target = Coords.ToGodot((view.PrevLookAt is { } pa ? System.Numerics.Vector3.Lerp(pa, at, a) : at) - _origin);
            if ((target - eye).LengthSquared() > 1e-4f) _scriptCam.LookAtFromPosition(eye, target, Vector3.Up);
            else _scriptCam.Position = eye;
        }
        else if (view.Basis is { } b) _scriptCam.Transform = new Transform3D(Coords.ToGodot(b.ToEngine()), eye);
        else _scriptCam.Position = eye;
    }

    private string _status = "";

    /// <summary>I76_BUILD_TRACE: stamps the next frames' draws, to see what the first frames after the build cost.</summary>
    private async void TraceFrames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            MissionBuilder.Trace($"frame drawn ({Engine.GetFramesDrawn()})");
        }
    }

    private void LoadMission()
    {
        var install = new GameInstall(_opt.Install!);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vfs = new AssetVfs(install);
        var msn = MissionPath(install);
        _builder = new MissionBuilder(vfs) { SkipVehicles = _opt.Drive is not null, DeferModern = _opt.Mode1997, EarlyHorizon = Horizon3D, WorldLoaded = I76.Game.Editor.EditorSession.Active ? w => I76.Game.Editor.EditorSession.Prepare(w, _builder!.Assembler!.ResolveDefinition) : null };
        var world = _builder.Build(msn);
        _worldRoot.AddChild(world);
        MissionBuilder.Trace("world in tree");
        _builder.Scenery!.Modern = !_mode1997;
        var w = _builder.World!;
        _status = $"{w.Mission.Name}: {sw.ElapsedMilliseconds} ms, {_builder.Terrain!.ChunkCount} chunks, " +
                  $"{_builder.Roads!.Quads} road quads ({_builder.RoadChunks} chunks painted, {_builder.RoadBakeMs} ms), {_builder.Scenery!.Loaded}/{w.Placements.Count + w.LinePlacements.Count} placements incl. {w.LinePlacements.Count} fence links ({_builder.Scenery.Keywords} keywords, {_builder.Scenery.Failed} failed)";
        GD.Print(_status);
        ApplySky();
        AddSoftwareLook(install, vfs);   // MainSoftware.cs
        MissionBuilder.Trace("sky");
        if (!_opt.NoMusic) PlayMusic(install, _builder.Settings);   // started after the card and the briefing

        // Start over the terrain centre, or where --cam says.
        var c = w.Centre();
        if (_opt.Cam is { Length: >= 3 } cam)
        {
            Rebase(new System.Numerics.Vector3(cam[0], cam[1], cam[2]));
            _cam.Position = Vector3.Zero;
            _cam.SetYawPitch(cam.Length > 3 ? cam[3] : 0, cam.Length > 4 ? cam[4] : 0);
        }
        else
        {
            Rebase(new System.Numerics.Vector3(c.X, c.Y + 60, c.Z));
            _cam.Position = Vector3.Zero;
            _cam.SetYawPitch(0, -15);
        }
        // I76_CAM_AT_LIGHT=1: looking at the mission's sun or moon instead (a capture of the sky's light)
        if (System.Environment.GetEnvironmentVariable("I76_CAM_AT_LIGHT") == "1")
        {
            GD.Print($"camera: at the light {_lightDir}");
            _cam.SetYawPitch(Mathf.RadToDeg(Mathf.Atan2(-_lightDir.X, -_lightDir.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(_lightDir.Y, -1, 1))));
        }
    }

    /// <summary>Plays the named movies from smk/ one after another (any missing are passed over), then runs <paramref name="then"/>.</summary>
    private void PlayMovies(GameInstall install, IReadOnlyList<string> names, System.Action then, int i = 0)
    {
        while (i < names.Count && MoviePlayer.Find(install, names[i]) is null) i++;
        if (i >= names.Count) { then(); BootSplash.Dismiss(); return; }
        DropMenuMusic();   // a briefing or cut-scene has its own sound; the menu track carried over the loading card ends here
        var player = new MoviePlayer { Path = MoviePlayer.Find(install, names[i])!, Faithful = _opt.Mode1997, Name = "Movie" };
        GD.Print($"movie: {names[i]}");
        player.Finished += () => { GD.Print($"movie: {names[i]} over"); PlayMovies(install, names, then, i + 1); };
        AddChild(player);
        BootSplash.Dismiss();
    }

    /// <summary>
    /// A mission script's movie request (opcode 51): the original's start-up plays it at
    /// the top of its next loop, so the mission stands still under it and
    /// the CD music pauses and resumes after.
    /// </summary>
    private void MissionMovie(GameInstall install, string name)
    {
        if (_opt.SkipMovies || MoviePlayer.Find(install, name) is null) return;
        var music = GetNodeOrNull<AudioStreamPlayer>("Music");
        if (music is not null) music.StreamPaused = true;
        // a network game's server does not stop for it: the movie plays over the running game, the car held (SimDriver.InputHeld)
        if (_net is null) GetTree().Paused = true;
        void Resume()
        {
            GetTree().Paused = false;
            if (music is null || !IsInstanceValid(music)) return;
            music.StreamPaused = false;
            if (!music.Playing) StartMissionMusic();
        }
        // a movie that cannot be put up leaves the mission going rather than held still under nothing
        try { PlayMovies(install, [name], Resume); }
        catch (System.Exception ex) { GD.PrintErr($"movie {name}: not played: {ex}"); Resume(); }
    }

    /// <summary>A co-op mission's briefing movie (WRLD +0x04) over the loading card, until it ends or is skipped; none when the mission has none.</summary>
    private async System.Threading.Tasks.Task CoopBriefing(string mission)
    {
        var install = new GameInstall(_opt.Install!);
        string? name = null;
        try
        {
            if (install.FindMission(mission) is { } path)
                name = I76.Formats.Mission.WorldSettings.Read(new I76.Formats.Mission.MissionFile(path))?.Briefing;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.ArgumentException) { }
        if (name is not { Length: > 0 } || MoviePlayer.Find(install, name) is null) return;
        var over = new System.Threading.Tasks.TaskCompletionSource();
        try { PlayMovies(install, [name], () => over.TrySetResult()); }
        catch (System.Exception ex) { GD.PrintErr($"movie {name}: not played: {ex}"); return; }
        await over.Task;
    }

    private string _musicNote = "";

    private string MissionPath(GameInstall install)
    {
        return install.FindMission(_opt.Mission!) ?? throw new System.IO.FileNotFoundException($"miss16/{_opt.Mission}");
    }

    /// <summary>
    /// The mission's CD track (WRLD +0x00, verbatim: a01 asks MCI for track 7), which the GOG install ships as music/N.mp3,
    /// four of them out of disc order (CdMusic). It is made here but starts in <see cref="StartMissionMusic"/>: the original
    /// calls the music start after the load and the briefing (the original's start-up), not while the card is up. What follows
    /// it is MissionMusic's (on to the next track at once, back to track 2 after the last; I76_MUSIC_SEQUENCE=retail or gog for the originals' 60 s poll).
    /// </summary>
    private void PlayMusic(GameInstall install, I76.Formats.Mission.WorldSettings? s)
    {
        if (s is null) return;
        if (GetNodeOrNull("Music") is not null) { _status += _musicNote; return; }
        _musicSeq = new I76.Formats.Audio.MissionMusic(I76.Game.Assets.CdMusic.LastTrack(install),
            I76.Formats.Audio.MissionMusic.PolicyFromEnvironment(System.Environment.GetEnvironmentVariable("I76_MUSIC_SEQUENCE")));
        _musicTrack = _musicSeq.First((int)s.CdTrack);
        if (I76.Game.Assets.CdMusic.Path(install, _musicTrack) is null) return;
        ApplyAudio(I76.Game.Shell.ShellSettings.Load());
        _musicInstall = install;
        var player = new AudioStreamPlayer { VolumeDb = -8, Name = "Music", Bus = "Music",
            ProcessMode = ProcessModeEnum.Always }; // the card's fade-out pauses the tree; the track plays on through it
        AddChild(player);
        player.Finished += OnMusicFinished;
        // the track's file read and parsed now, behind the loading card, not in the first frame of play
        if (I76.Game.Assets.CdMusic.Path(install, _musicTrack) is { } first)
            _musicReady = (_musicTrack, new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(first), Loop = false });
        _status += _musicNote = $"\nmusic: track {_musicTrack}";
    }

    private I76.Formats.Audio.MissionMusic? _musicSeq;
    private GameInstall? _musicInstall;
    private int _musicTrack;
    private (int Track, AudioStreamMP3 Stream)? _musicReady;
    private ulong _musicStartedMs;

    /// <summary>Starts the mission's track (once, with the mission under way) and drops the menu track still carried.</summary>
    private void StartMissionMusic(double from = 0)
    {
        if (GetNodeOrNull<AudioStreamPlayer>("Music") is { } p && !p.Playing && p.Stream is null) PlayMusicTrack(p, _musicTrack, from);
        I76.Game.Shell.Shell.DropCarriedMusic(GetTree());
    }

    /// <summary>Ends the menu track carried through the scene reload (a briefing or a cut-scene has taken over); logs when one was playing.</summary>
    private void DropMenuMusic()
    {
        if (GetTree().Root.GetNodeOrNull(I76.Game.Shell.Shell.CarriedMusicName) is null) return;
        GD.Print("music: the menu track stops");
        I76.Game.Shell.Shell.DropCarriedMusic(GetTree());
    }

    private void PlayMusicTrack(AudioStreamPlayer p, int track, double from = 0)
    {
        if (_musicInstall is null || I76.Game.Assets.CdMusic.Path(_musicInstall, track) is not { } file) return;
        _musicTrack = track;
        if (_musicReady is { } ready && ready.Track == track) { p.Stream = ready.Stream; _musicReady = null; }
        else p.Stream = new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(file), Loop = false };
        if (_musicStartedMs == 0) _musicStartedMs = Time.GetTicksMsec() - (ulong)(from * 1000);
        p.Play((float)from);
        GD.Print($"music: track {track}");
    }

    /// <summary>A track ended: the next one after the poller's wait (MissionMusic).</summary>
    private async void OnMusicFinished()
    {
        if (_musicSeq is null || GetNodeOrNull<AudioStreamPlayer>("Music") is not { } p) return;
        int next = _musicSeq.Next(_musicTrack);
        double wait = _musicSeq.Wait(_musicTrack, (Time.GetTicksMsec() - _musicStartedMs) / 1000.0);
        if (wait > 0.05)
        {
            await ToSignal(GetTree().CreateTimer(wait, processAlways: true), SceneTreeTimer.SignalName.Timeout);
            if (!IsInstanceValid(p) || p.Playing) return;
        }
        PlayMusicTrack(p, next);
    }

    /// <summary>The mission's sky texture on the engine's dome, in both modes; modern fogs toward its average colour.</summary>
    private void ApplySky()
    {
        if (_builder?.SkyTexture is not { } tex) return;
        var mat = _sky1997 = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sky_dome.gdshader") };
        mat.SetShaderParameter("clouds", tex);
        mat.SetShaderParameter("far_clip", Far1997);
        mat.SetShaderParameter("origin_y", _origin.Y);
        mat.SetShaderParameter("scroll_per_second", I76.Game.Sim.SimClock.SkyScrollPerSecond);   // the 1997 sky scrolls a fixed amount a frame, the sim tick
        var light = _builder.Light;
        var l = Coords.ToGodot(light.Direction).Normalized();
        if (_builder.Horizon is { } hz) { mat.SetShaderParameter("horizon", hz); mat.SetShaderParameter("has_horizon", true); }
        if (_builder.Sprite is { } sp)
        {
            mat.SetShaderParameter("sprite", sp);
            mat.SetShaderParameter("has_sprite", true);
            mat.SetShaderParameter("sprite_dir", l);
            mat.SetShaderParameter("sprite_radius", light.IsDay ? 0.06f : 0.08f);
        }
        // The mission's sun or moon: direction from the preset, strength from its intensity.
        _sun.LookAt(-l, Mathf.Abs(l.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        _sun.LightEnergy = 1.3f * light.Intensity;
        _sun.LightColor = light.IsDay ? new Color(1f, 0.96f, 0.88f) : new Color(0.6f, 0.7f, 1f);
        _modernEnv.AmbientLightEnergy = light.IsDay ? 0.6f : 0.25f;
        I76.Game.World.ObjectLight.Set("i76_light_dir", l);
        _lightDir = l;
        I76.Game.World.ObjectLight.Set("i76_light_intensity", light.Intensity);
        I76.Game.World.ObjectLight.Set("i76_ambient", light.Ambient);
        I76.Game.World.ObjectLight.Set("i76_night", !light.IsDay);
        I76.Game.World.ObjectLight.Set("i76_fade", new Vector2(I76.Formats.Mission.Lighting.FadeA, I76.Formats.Mission.Lighting.FadeB));
        I76.Game.World.ObjectLight.SetRamp(_cardRamp = LumaRamp());
        var sky = new Sky { SkyMaterial = mat, ProcessMode = Sky.ProcessModeEnum.Realtime };
        var modernSky = ModernSky(mat, light);
        AddHorizonTerrain(modernSky, light);
        // the modern sky's clouds move (TIME), so its radiance (ambient light and reflections) is redone all the
        // time: Incremental spreads each redo over a few frames (0.45 ms a frame less than Realtime's every frame);
        // the sky itself is drawn every frame either way. I76_SKY_REALTIME=1 for the old way.
        _modernEnv.Sky = new Sky { SkyMaterial = modernSky,
            ProcessMode = System.Environment.GetEnvironmentVariable("I76_SKY_REALTIME") == "1" ? Sky.ProcessModeEnum.Realtime : Sky.ProcessModeEnum.Incremental };
        AddSkyFog(modernSky);
        _env1997.BackgroundMode = Environment.BGMode.Sky;
        _env1997.Sky = sky;
        // no ambient: the 1997 shaders carry the engine's own light, and light() adds only the sun's shadow
        _env1997.AmbientLightSource = Environment.AmbientSource.Disabled;
        _env1997.ReflectedLightSource = Environment.ReflectionSource.Disabled;
    }

    /// <summary>
    /// The modern look's sky (sky_modern.gdshader): the same texture, horizon
    /// and sprite as the dome, with the colours measured from the texture.
    /// </summary>
    private ShaderMaterial ModernSky(ShaderMaterial dome, I76.Formats.Mission.LightPreset light)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sky_modern.gdshader") };
        foreach (var p in new[] { "clouds", "horizon", "has_horizon", "sprite", "has_sprite", "sprite_dir", "sprite_radius" })
            m.SetShaderParameter(p, dome.GetShaderParameter(p));
        if (_builder?.Palette is { } pal)
        {
            // by day the haze and the clouds are lifted toward white; a night sky keeps the texture's own dark blues (the original's
            // night is navy with violet clouds, and the lift greyed it)
            bool day = light.IsDay;
            m.SetShaderParameter("zenith_color", day ? pal.Sky.Darkened(0.12f) : pal.Sky);
            m.SetShaderParameter("horizon_color", day ? pal.Sky.Lerp(_builder.SkyAverage, 0.6f).Lerp(Colors.White, 0.25f) : pal.Sky.Lerp(_builder.SkyAverage, 0.5f));
            m.SetShaderParameter("cloud_color", day ? pal.Cloud.Lerp(Colors.White, 0.4f) : pal.Cloud);
            m.SetShaderParameter("sky_gain", day ? 1f : NightSkyGain);
            m.SetShaderParameter("cloud_shade", day ? pal.Shade.Lerp(pal.Sky, 0.35f).Darkened(0.1f) : pal.Shade.Lerp(pal.Cloud, 0.5f));
            m.SetShaderParameter("sky_lum", pal.SkyLum);
            m.SetShaderParameter("cloud_lum", pal.CloudLum);
        }
        m.SetShaderParameter("light_strength", light.IsDay ? light.Intensity : 0.3f);
        // by day a drawn sun in place of the sprite; by night a drawn moon (NASA's map of it) in place of the mission's moon sprite
        m.SetShaderParameter("procedural_sun", light.IsDay);
        if (!light.IsDay && System.Environment.GetEnvironmentVariable("I76_MODERN_MOON") != "0" && MoonTexture() is { } moon) { m.SetShaderParameter("moon_tex", moon); m.SetShaderParameter("procedural_moon", true); }
        m.SetShaderParameter("sprite_dir", Coords.ToGodot(light.Direction).Normalized());   // the sun's place, sprite or not
        return m;
    }

    /// <summary>The modern night sky's gain over its colours (sky_gain): the Filmic tonemap's toe darkens the texture's navy, which the
    /// original showed through the card's gamma ramp; matched on P04 (I76_NIGHT_SKY_GAIN to tune).</summary>
    private static readonly float NightSkyGain = float.TryParse(System.Environment.GetEnvironmentVariable("I76_NIGHT_SKY_GAIN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 2.5f;

    private static ImageTexture? _moon;
    /// <summary>The moon's surface for the modern sky: NASA SVS's CGI Moon Kit colour map (public domain), embedded in the assembly.</summary>
    private static ImageTexture? MoonTexture()
    {
        if (_moon is not null) return _moon;
        using var s = typeof(Main).Assembly.GetManifestResourceStream("moon_lroc_1k.jpg");
        if (s is null) return null;
        var b = new byte[s.Length];
        s.ReadExactly(b);
        var img = new Image();
        if (img.LoadJpgFromBuffer(b) != Error.Ok) return null;
        img.GenerateMipmaps();
        return _moon = ImageTexture.CreateFromImage(img);
    }

    private HorizonTerrain? _horizon;
    /// <summary>Whether the modern look draws 3D distant terrain (HorizonTerrain) in place of the sky's horizon panels.</summary>
    private static readonly bool Horizon3D = System.Environment.GetEnvironmentVariable("I76_HORIZON_PANELS") != "1";

    /// <summary>
    /// The modern look's distant terrain (HorizonTerrain), shaped and coloured
    /// from the mission's horizon panels, which the modern sky then leaves out;
    /// it hazes into that sky's horizon colour. The 1997 look keeps the panels.
    /// </summary>
    private void AddHorizonTerrain(ShaderMaterial sky, I76.Formats.Mission.LightPreset light)
    {
        if (!Horizon3D || _builder?.World is not { } world || world.Terrain.Huge) return;   // (a huge map keeps the panels' horizon)
        if (_builder.DeferModern) { _horizonSky = sky; _horizonLight = light; return; }   // ModernJob builds it in the background
        if (_builder.HorizonBuild is { } early)
        {
            // computed during the build (MissionBuilder.EarlyHorizon)
            _horizon?.QueueFree();
            _horizon = early.GetAwaiter().GetResult();
            _horizon.Visible = !_mode1997;
            _horizon.Finish();
            MissionBuilder.Trace("horizon built");
            PlaceHorizon(sky, light);
            return;
        }
        var profile = HorizonTerrain.Measure(_builder.Horizon) ?? HorizonTerrain.Default();
        var ground = new Color(0.5f, 0.32f, 0.2f);
        if (_builder.TerrainMaterial?.GetShaderParameter("ground_tex").As<Texture2D>() is { } gt && gt.GetImage() is { } gi)
        {
            if (gi.IsCompressed()) gi.Decompress();
            gi.Resize(1, 1, Image.Interpolation.Lanczos);
            ground = gi.GetPixel(0, 0);
        }
        _horizon?.QueueFree();
        _horizon = new HorizonTerrain { Name = "HorizonTerrain", Visible = !_mode1997 };
        _horizon.Build(world, profile, light.Direction, ground);
        MissionBuilder.Trace("horizon built");
        PlaceHorizon(sky, light);
    }

    private bool _horizonEyeSet;

    /// <summary>
    /// Fixes the 3D horizon ring's sink for the mission at a reference eye over the map's centre, at the map's average
    /// ground height plus the chase eye's few metres: the original moves its ring with the eye's height (foot 10.2 x camY
    /// below it), the modern look keeps one height for the mission. Once; nothing follows the camera after.
    /// </summary>
    private void SetHorizonEye()
    {
        if (_horizonEyeSet || _horizon?.Material is not { } m || _builder?.World is not { } world) return;
        _horizonEyeSet = true;
        var (low, eyeY) = I76.Formats.Assembly.HorizonEdge.Ring(world);   // one height for the map (HorizonEdge.Ring)
        m.SetShaderParameter("ref_eye", new Vector3(0, eyeY, 0));
        m.SetShaderParameter("sink_low", low);
        float footK = -I76.Formats.Mission.Lighting.HorizonFoot(1f) / I76.Formats.Mission.HorizonList.Radius;
        m.SetShaderParameter("foot_k", footK);
        if (System.Environment.GetEnvironmentVariable("I76_HORIZON_TINT") == "1") m.SetShaderParameter("debug_tint", 1f);
        _horizon.FitCullBounds(eyeY, footK, low);   // the sunk ring's own bounds, or chunks are culled that are in view
        if (System.Environment.GetEnvironmentVariable("I76_HORIZON_DEBUG") == "1") GD.Print($"horizon: ring sunk for an eye at game height {eyeY:0.0} (the map's average ground + 3)");
    }

    /// <summary>The finished ring into the scene, and the sky told it is there.</summary>
    private void PlaceHorizon(ShaderMaterial sky, I76.Formats.Mission.LightPreset light)
    {
        var _horizon = this._horizon!;
        _horizon.Position = Coords.ToGodot(_horizon.Centre.X, 0, _horizon.Centre.Y);
        _worldRoot.AddChild(_horizon);
        _horizonEyeSet = false;
        SetHorizonEye();
        foreach (var p in new[] { "horizon_color", "sprite_dir", "light_strength" })
            _horizon.Material.SetShaderParameter(p, sky.GetShaderParameter(p));
        // night air: the dark rock shows as silhouettes against the sky, as the night panels drew it
        if (!light.IsDay) _horizon.Material.SetShaderParameter("haze_reach", 20000f);
        // out of view, but still in the sky's light: the ring is lit by it as the panels' scene was (sky_modern.gdshaderinc)
        sky.SetShaderParameter("horizon_light", sky.GetShaderParameter("has_horizon"));
        sky.SetShaderParameter("has_horizon", false);
        sky.SetShaderParameter("ground_haze", true);
        ApplyDrawDistance();
        var msg = $"horizon: 3D, {_horizon.Vertices} vertices, {_horizon.InnerRadius:0}-{_horizon.OuterRadius:0} m, built in {_horizon.BuildMs} ms ({_horizon.Timing})";
        _status += "\n" + msg;
        if (_builder?.DeferModern == true || MissionBuilder.TraceOn) GD.Print(msg);
    }

    // ---- the modern look's deferred build (a mission that starts in the 1997 look) ----------------------------------------

    private ShaderMaterial? _horizonSky;
    private I76.Formats.Mission.LightPreset _horizonLight;
    private IEnumerator<object?>? _modernJob;
    private System.Threading.Tasks.Task? _modernWait;
    /// <summary>The modern look was asked for before it was built: the 1997 view stays until the build ends, then it switches.</summary>
    private bool _wantModern;
    private CanvasLayer? _standBy;
    private readonly System.Diagnostics.Stopwatch _modernClock = new();

    /// <summary>
    /// A mission that starts in the 1997 look builds none of the modern terrain: the first switch to the modern look does,
    /// a few milliseconds a frame on the main thread (the textures, meshes and nodes) and the rest on a thread of its own,
    /// with the 1997 view and a stand-by notice meanwhile; see MissionBuilder.ModernSteps.
    /// </summary>
    private void StartModernBuild()
    {
        if (_modernJob is not null || _builder is not { DeferModern: true, ModernReady: false }) return;
        _modernJob = ModernJob().GetEnumerator();
        _modernClock.Restart();
    }

    private IEnumerable<object?> ModernJob()
    {
        foreach (var step in _builder!.ModernSteps()) yield return step;
        if (_horizonSky is not { } sky || _builder.World is not { } world) yield break;
        var light = _horizonLight;
        var ground = new Color(0.5f, 0.32f, 0.2f);
        if (_builder.TerrainMaterial?.GetShaderParameter("ground_tex").As<Texture2D>() is { } gt && gt.GetImage() is { } gi)
        {
            if (gi.IsCompressed()) gi.Decompress();
            gi.Resize(1, 1, Image.Interpolation.Lanczos);
            ground = gi.GetPixel(0, 0);
        }
        yield return null;
        var panels = _builder.HorizonImages;
        var h = new HorizonTerrain { Name = "HorizonTerrain", Visible = false };
        var work = MissionBuilder.Background(() =>
        {
            var profile = (panels is null ? null : HorizonTerrain.Measure(panels)) ?? HorizonTerrain.Default();
            h.Compute(world, profile, light.Direction, ground);
        });
        while (!work.IsCompleted) yield return work;
        if (work.IsFaulted) throw work.Exception!.GetBaseException();
        while (h.Finish(3) > 0) yield return null;
        _horizon?.QueueFree();
        _horizon = h;
        PlaceHorizon(sky, light);
    }

    private void PumpModernBuild()
    {
        if (_modernJob is null || _modernWait is { IsCompleted: false }) return;
        // a few ms a frame, more when the player is waiting for it
        double budget = _wantModern ? 10 : 2;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                if (!_modernJob.MoveNext()) { EndModernBuild(); return; }
                if (_modernJob.Current is System.Threading.Tasks.Task t) { _modernWait = t; return; }
                if (sw.Elapsed.TotalMilliseconds >= budget) return;
            }
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"modern terrain build failed: {e}");
            _modernJob = null; _modernWait = null;
            _wantModern = false; _standBy?.QueueFree(); _standBy = null;   // the 1997 look stays
        }
        finally
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            _modernMainMs += ms; _modernMaxSlice = System.Math.Max(_modernMaxSlice, ms);
        }
    }
    private double _modernMainMs, _modernMaxSlice;

    private void EndModernBuild()
    {
        _modernJob = null; _modernWait = null;
        GD.Print($"modern terrain built on demand: {_modernClock.ElapsedMilliseconds} ms, {_modernMainMs:0} ms of it on the main thread, the longest frame's share {_modernMaxSlice:0.0} ms");
        if (!_wantModern) return;
        _standBy?.QueueFree(); _standBy = null;
        _wantModern = false;
        GD.Print($"modern look switched on, {_modernClock.ElapsedMilliseconds} ms after it was asked for");
        SetMode1997(false);
    }

    /// <summary>The modern look was asked for while it builds: the boot screen's look (black, "PLEASE STAND BY . . ." in Impact) until it can switch.</summary>
    private void ShowStandBy()
    {
        if (_standBy is not null) return;
        _standBy = new CanvasLayer { Layer = 90, Name = "StandBy" };
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _standBy.AddChild(black);
        var impact = new I76.Game.Shell.ImpactLayer { Name = "Impact" };
        _standBy.AddChild(impact);
        AddChild(_standBy);
        bool nitro = _opt.Install is { } dir && new GameInstall(dir).IsNitro;
        var (box, spec) = BootSplash.StandBy(nitro);
        if (impact.Available) { impact.Put(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y, spec, Colors.White); return; }
        // no Impact on this system: the words plainly, where the line would be
        impact.QueueFree();
        var l = new Label { Text = spec.Text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", 28);
        l.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        l.OffsetTop = -120; l.OffsetLeft = -300; l.OffsetRight = 300; l.OffsetBottom = -80;
        _standBy.AddChild(l);
    }

    private MeshInstance3D? _skyFog;
    /// <summary>
    /// Draw Distance on the scene: the modern look at Original fades into the sky by 560 m and shows the sky's horizon
    /// panels, in place of the 3D horizon ring, which the 600 m clip would cut; Far has the ring and no fade. The 1997 look
    /// only changes its camera's far plane (and keeps the panels).
    /// </summary>
    private void ApplyDrawDistance()
    {
        bool original = I76.Formats.Imaging.DrawDistance.ModernFog(_drawDistance);
        // a map that fills the grid has ground only as far as the terrain streams (TerrainNode.StreamRadius): at Far it fades into the sky there
        bool hugeMap = _builder?.World?.Terrain.Huge == true;
        if (_skyFog is not null)
        {
            _skyFog.Visible = !_mode1997 && (original || hugeMap);
            if (_skyFog.MaterialOverride is ShaderMaterial fm)
            {
                fm.SetShaderParameter("fog_begin", original ? I76.Formats.Imaging.DrawDistance.FogBegin : World.TerrainNode.StreamRadius * 0.6f);
                fm.SetShaderParameter("fog_end", original ? I76.Formats.Imaging.DrawDistance.FogEnd : World.TerrainNode.StreamRadius * 0.95f);
            }
        }
        if (_horizon is not null) _horizon.Visible = !_mode1997 && !original;
        if (_modernEnv?.Sky?.SkyMaterial is ShaderMaterial sky && _horizon is not null && _horizon.IsInsideTree())
        {
            // the panels stand in for the ring: the sky draws them itself, and the haze below the horizon is the ring's.
            // A mission with no panels of its own (Nitro's n01, n03, n35, n65) has none to draw, as in the original (an
            // unset texture array reads as white, a white band over the mountains' place), and the sky is plain to the ground
            bool panels = I76.Formats.Imaging.DrawDistance.ModernPanels(_drawDistance, _builder?.Horizon is not null);
            foreach (var m in new[] { sky, _skyFog?.MaterialOverride as ShaderMaterial })
            {
                m?.SetShaderParameter("has_horizon", panels);
                m?.SetShaderParameter("ground_haze", !panels);
            }
        }
    }

    /// <summary>I76_DRAW_DISTANCE=0|1, a capture hook: Original or Far whatever the settings say.</summary>
    private static int DrawDistanceHook(int setting) => int.TryParse(OS.GetEnvironment("I76_DRAW_DISTANCE"), out int dd) ? I76.Formats.Imaging.DrawDistance.Parse(dd) : setting;

    private int _drawDistance = I76.Formats.Imaging.DrawDistance.Far;

    /// <summary>
    /// The modern look's distance fog (sky_fog.gdshader). The original never
    /// shows the whole map: its far clip is 600 (the world camera rebuild, Visibility
    /// Range on) with the sky's horizon panels beyond, so the walls at the map's
    /// edges are seldom seen. Here the ground fades over the same reach into the
    /// very sky behind it, the same shader function with the same parameters, so
    /// by FogEnd the terrain is the sky and its horizon panels and no edge shows.
    /// </summary>
    private void AddSkyFog(ShaderMaterial sky)
    {
        var fog = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sky_fog.gdshader"), RenderPriority = (int)Material.RenderPriorityMin };
        foreach (var u in sky.Shader.GetShaderUniformList())
        {
            var name = u.AsGodotDictionary()["name"].AsString();
            fog.SetShaderParameter(name, sky.GetShaderParameter(name));
        }
        fog.SetShaderParameter("fog_begin", I76.Formats.Imaging.DrawDistance.FogBegin);
        fog.SetShaderParameter("fog_end", I76.Formats.Imaging.DrawDistance.FogEnd);
        _skyFog?.QueueFree();
        _skyFog = new MeshInstance3D
        {
            Name = "SkyFog", Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = fog, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
            Layers = 1 | World.TerrainNode.SkyFogLayer,
        };
        AddChild(_skyFog);
        ApplyDrawDistance();
    }

    /// <summary>Move the Godot origin to a game-space point, keeping everything else where it is.</summary>
    private void Rebase(System.Numerics.Vector3 gameOrigin)
    {
        _origin = gameOrigin;
        _worldRoot.Position = -Coords.ToGodot(gameOrigin);
        _sky1997?.SetShaderParameter("origin_y", gameOrigin.Y);   // the horizon ring sinks with the eye's game height
    }

    /// <summary>The 1997 look's sky (sky_dome.gdshader).</summary>
    private ShaderMaterial? _sky1997;

    private void SetupEnvironment()
    {
        // The sun shines along -L.
        var l = Coords.ToGodot(GameLight).Normalized();
        _sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.2f,
            DirectionalShadowMaxDistance = 800 };
        AddChild(_sun);
        _sun.LookAt(-l, l.Cross(Vector3.Right).LengthSquared() > 1e-6 ? Vector3.Up : Vector3.Forward);

        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.32f, 0.47f, 0.72f), SkyHorizonColor = new Color(0.78f, 0.72f, 0.62f),
            GroundHorizonColor = new Color(0.6f, 0.55f, 0.48f), GroundBottomColor = new Color(0.3f, 0.27f, 0.22f),
        } };
        _modernEnv = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky, Sky = sky,
            AmbientLightSource = Environment.AmbientSource.Sky, AmbientLightEnergy = 0.6f,
            TonemapMode = Environment.ToneMapper.Filmic,
            // no Godot fog: the modern look fades distance into the sky itself (AddSkyFog)
            SsaoEnabled = true, FogEnabled = false,
        };
        _env1997 = new Environment
        {
            BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color(0.55f, 0.6f, 0.7f),
            AmbientLightSource = Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.35f,
            TonemapMode = Environment.ToneMapper.Linear,
            // the frame as the 3D card showed it: through the Glide driver's default gamma ramp (ZGLIDE sets none of its
            // own), Monitor Brightness's palette curve before it (ApplyGraphics)
            AdjustmentEnabled = true, AdjustmentColorCorrection = CardCurve(I76.Formats.Imaging.MonitorBrightness.Default),
        };
        _env = new WorldEnvironment { Environment = _modernEnv };
        AddChild(_env);
    }


    /// <summary>The 1997 look's far clip: the world camera rebuild's 600 with Visibility Range on (DrawDistance.OriginalClip).</summary>
    private const float Far1997 = I76.Formats.Imaging.DrawDistance.OriginalClip;

    private DropShadows? _drops;
    private Vector3 _lightDir = new(0.7071f, 0.7071f, 0);
    private List<(Mesh, Transform3D, bool)> _sceneryCasters = [];
    private double _sceneryAt = -1;

    /// <summary>
    /// The 1997 look's drop shadows (DropShadows): the scenery's meshes (gathered
    /// once a second, so a destroyed building's remains take over) and the cars'
    /// parts, darkening the ground under them as the software renderer's shadow table did.
    /// </summary>
    private void AddDropShadows()
    {
        if (_builder?.World is not { } w) return;
        // a night preset: the cars' lights (NightLights), which also pick the shadows' light near them
        NightLights? night = null;
        if (!_builder.Light.IsDay)
        {
            float GroundGodot(float x, float z)
            {
                var g = Coords.ToGame(new Vector3(x, 0, z)) + _origin;
                return (float)w.BilinearHeight(g.X, g.Z) - _origin.Y;
            }
            AddChild(night = new NightLights { Name = "NightLights", Cars = () => _sim?.Cars ?? [], Ground = GroundGodot, MoonIntensity = _builder.Light.Intensity });
        }
        _drops = new DropShadows
        {
            Name = "DropShadows", Ground = (x, z) => (float)w.BilinearHeight(x, z), Origin = () => _origin,
            Casters = Casters, Light = () => _lightDir,
            Surface = (x, y, z) => _sim?.Session?.World.Ground is { } g ? g.Query(x, y, z).Height : (float)w.BilinearHeight(x, z), LightAt = night is null ? null : night.ShadowLight,
        };
        AddChild(_drops);
        UpdateDrops();
    }

    /// <summary>
    /// 1997: the flat polygon. Modern: the same projection feathered (DropShadows.Soft), for Shadow Detail Low
    /// and High, and the cars and scenery then stay out of the sun's shadow map (the terrain's shadows, which
    /// fall on them, stay there). I76_DROP_SHADOWS=0 brings back the shadow-map shadows for them.
    /// </summary>
    private void UpdateDrops()
    {
        if (_drops is null) return;
        bool mapShadows = System.Environment.GetEnvironmentVariable("I76_DROP_SHADOWS") == "0";
        _drops.Soft = !_mode1997;
        _drops.SoftReach = float.TryParse(System.Environment.GetEnvironmentVariable("I76_DROP_REACH"), out var dr) ? dr : _shadowDetail > 1 ? 500f : 250f;   // Shadow Detail High casts far, Low a moderate distance (the cost is the casters in reach)
        _drops.Visible = _mode1997 || (_shadowDetail > 0 && !mapShadows);
        _dropsOffMap = !mapShadows;
        _sceneryAt = -1;
        if (_builder?.Light is { } l) _drops.Strength = 0.9f * Mathf.Sqrt(Mathf.Clamp(l.Intensity, 0f, 1f));
    }

    private bool _dropsOffMap = true;

    private IEnumerable<(Mesh, Transform3D, bool)> Casters()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - _sceneryAt > 1 && _builder?.Scenery is { } sc)
        {
            _sceneryAt = now;
            _sceneryCasters = [];
            void Walk(Node n)
            {
                if (n == sc.Markers || n == sc.RoadPieces || n.IsInGroup(ScenerySpawner.NoDropShadow)) return;
                if (n is MeshInstance3D { Visible: true, Mesh: { } m } mi)
                {
                    _sceneryCasters.Add((m, mi.GlobalTransform, true));
                    if (_dropsOffMap) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                }
                else if (n is MultiMeshInstance3D { Visible: true, Multimesh: { Mesh: { } mm } multi } mmi)
                {
                    if (_dropsOffMap) mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    for (int i = 0; i < multi.InstanceCount; i++)
                    {
                        var t = multi.GetInstanceTransform(i);
                        if (t.Basis.Determinant() != 0) _sceneryCasters.Add((mm, mmi.GlobalTransform * t, true));
                    }
                }
                foreach (var ch in n.GetChildren()) Walk(ch);
            }
            Walk(sc);
        }
        foreach (var x in _sceneryCasters) yield return x;
        if (_sim is null) yield break;
        foreach (var car in _sim.Cars)
            foreach (var mi in car.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
            {
                if (_dropsOffMap && mi.CastShadow == GeometryInstance3D.ShadowCastingSetting.On) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (mi.IsVisibleInTree() && mi.Mesh is ArrayMesh m && (mi.Layers & CarNode.CockpitLayer) == 0 && !CarNode.IsLightPool(mi))
                    yield return (m, mi.GlobalTransform, false);
            }
    }

    /// <summary>The mission's light ramp (LumaTable), or null without its palette or .lum.</summary>
    private byte[]? LumaBytes() =>
        _builder?.ActPalette is { } pal && _builder.Settings?.Luminance is { Length: > 0 } ln && _builder.Vfs.TryRead(ln) is { } lum
            ? I76.Formats.Imaging.LumaTable.Build(pal, lum) : null;

    /// <summary>
    /// The mission's light ramp as a 32x1 texture (LumaTable, built from
    /// its palette and .lum); without them a plain light^0.4 curve.
    /// </summary>
    private float[] LumaRamp()
    {
        byte[]? table = LumaBytes();
        var ab = new float[64];
        for (int j = 0; j < 32; j++) ab[2 * j] = (table?[j] ?? (byte)(255 * Mathf.Pow(j / 31f, 0.4f))) / 255f;
        return ab;
    }

    /// <summary>The modern look's Monitor Brightness: 7% a step either side of the shipped 3.</summary>
    private static float ModernBrightness(int level) => 1f + (level - 3) * 0.07f;

    private readonly Dictionary<int, ImageTexture> _cardCurves = [];

    /// <summary>
    /// The 1997 look's (3D card) colour-correction curve for Monitor Brightness <paramref name="level"/>, the same for
    /// each channel: MonitorBrightness.CardCurve, the Glide driver's default ramp after the level's palette curve.
    /// </summary>
    private ImageTexture CardCurve(int level)
    {
        level = I76.Formats.Imaging.MonitorBrightness.Level(level);
        if (_cardCurves.TryGetValue(level, out var t)) return t;
        byte[] curve = I76.Formats.Imaging.MonitorBrightness.CardCurve(level);
        var img = Image.CreateEmpty(256, 1, false, Image.Format.Rgb8);
        for (int i = 0; i < 256; i++) img.SetPixel(i, 0, Color.Color8(curve[i], curve[i], curve[i]));
        return _cardCurves[level] = ImageTexture.CreateFromImage(img);
    }

    private void SetMode1997(bool on)
    {
        // the modern look not built yet (a 1997 start builds none of it): build it now, showing 1997 meanwhile
        _wantModern = false;
        if (!on && (_modernJob is not null || _builder is { DeferModern: true, ModernReady: false }))
        {
            StartModernBuild();
            _wantModern = true;
            GD.Print("modern look asked for while it builds: standing by");
            ShowStandBy();
            return;
        }
        _standBy?.QueueFree(); _standBy = null;
        _mode1997 = on;
        I76.Game.World.ObjectLight.Set("i76_mode_1997", on);
        _builder?.Terrain?.UseModern(!on);
        _builder?.Roads?.SetMode1997(on);
        _builder?.RoadsOffGround?.SetMode1997(on);
        // both looks paint the roads and the road pieces into the terrain (RoadRaster); their meshes stay hidden
        if (_builder?.Scenery is { } scn) scn.RoadPieces.Visible = false;
        if (_builder?.Roads is { } r) r.Visible = false;
        _env.Environment = on ? _env1997 : _modernEnv;
        // 1997: no shadow map (the original had none: its drop shadows are projected polygons, DropShadows)
        _sun.ShadowEnabled = !on;
        UpdateDrops();
        ApplyDrawDistance();
        // (each is a change of the renderer's shadow settings, which it pays for even when the value is the one it has: only when it is not)
        if (_shadowFilterSet != on) RenderingServer.DirectionalSoftShadowFilterSetQuality(on ? RenderingServer.ShadowQuality.Hard : RenderingServer.ShadowQuality.SoftLow);
        _shadowFilterSet = on;
        // crisp: a bigger atlas, no blur and a shorter reach, so the near texels are fine
        if (_atlasSet != on) RenderingServer.DirectionalShadowAtlasSetSize(on ? 8192 : 4096, true);
        _atlasSet = on;
        _sun.ShadowBlur = on ? 0f : 1f;
        _sun.DirectionalShadowMaxDistance = on ? 600 : 1500;
        MissionBuilder.Trace("look: shadows, draw distance");
        _builder?.Materials?.SetMode1997(on);
        if (_builder?.Scenery is { } scenery) scenery.Modern = !on;
        MissionBuilder.Trace("look: materials, scenery");
        if (_pit is not null) _pit.Faithful = on;
        if (_sim is not null) foreach (var c in _sim.Cars) { c.Modern = !on; if (c.Wrecked) _damageView?.Rebuild(c); }   // the wreck's shape: modern shell or the 1997 box
        GetViewport().Msaa3D = on ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa4X;
        MissionBuilder.Trace("look: msaa");
        ApplyModernQuality();
        MissionBuilder.Trace("look: modern quality");
        ApplySoftware();   // MainSoftware.cs
        if (on) UpdateVegetation();   // ApplyModernQuality leaves the 1997 look alone: hide the plants here
    }

    private DamageView? _damageView;
    /// <summary>The look the renderer's shadow filter and atlas were last set for (null: not yet this run).</summary>
    private static bool? _shadowFilterSet, _atlasSet;

    // ---- the keys ----------------------------------------------------------

    /// <summary>The original's V state: the eye is in the cockpit; and the last external preset (F2..F11 and B store it; the start stores the chase).</summary>
    private bool _cockpitFlag;
    private int _lastPreset = 0xf;
    private bool _gamePaused;
    private static int _shotCounter;

    /// <summary>
    /// Mouse Camera: the mouse is the chase view's while a mission runs in it, with no menu, pause, cut-scene or picture up and the window
    /// focused. The cursor is captured (hidden) only then, and let go for the menus and when the window loses focus; the buttons still reach the
    /// key bindings. Runs every frame, paused or not.
    /// </summary>
    private void UpdateMouseCapture()
    {
        if (_editor is not null) return;   // the editor camera captures the pointer for its own drags
        bool inMission = _sim is not null && !_inMenu && _menu is null && !_loading && _picture is null && !_installerOnly && _reconfig is null
            && _opt.Install is not null;
        bool chase = !CameraHeld && _chase is { Current: true, Fixed: null };
        bool cockpit = !CameraHeld && _car?.CockpitCamera?.Current == true && _pit is { Binoculars: false } && I76.Formats.Input.DashToggle.FreeLookAllowed(_pit.Dash3D);
        bool focused = DisplayServer.WindowIsFocused() || System.Environment.GetEnvironmentVariable("I76_MOUSE") is not null;   // (a capture's off-screen window is never focused)
        var mode = CursorPolicy.For(inMission, chase, MouseCamera.Level, focused, MouseCamera.Confine, cockpit, MouseCamera.CockpitLook);
        // the mouse orbits the chase view (or turns the head in the 3D cockpit) only while captured, unpaused and no orbit key is held
        MouseCamera.CockpitActive = mode == CursorMode.Captured && !_gamePaused && cockpit;
        MouseCamera.Active = mode == CursorMode.Captured && !_gamePaused && (cockpit || _chase!.Orbit?.Invoke() != true);
        // the wheel zooms the external view on screen, whether or not the mouse orbits it
        MouseCamera.WheelView = inMission && !CameraHeld && !_gamePaused && _chase!.Orbit?.Invoke() != true ? WheelViewNow() : null;
        TrackViewForZoom(inMission);
        ApplyCursor(mode);
    }

    /// <summary>The zoom the wheel turns in the view on screen; null in the cockpit (and binoculars), where the wheel stays free.</summary>
    private I76.Sim.Views.ViewZoom.Kind? WheelViewNow()
    {
        if (_car?.CockpitCamera?.Current == true) return null;
        if (_tv?.Current == true) return _tv.Mode == TargetViewCamera.View.Projectile ? I76.Sim.Views.ViewZoom.Kind.Projectile : I76.Sim.Views.ViewZoom.Kind.Target;
        if (_preset?.Current == true) return _preset.Mode switch { PresetCamera.View.Orbit => I76.Sim.Views.ViewZoom.Kind.Bumper, PresetCamera.View.Overhead => I76.Sim.Views.ViewZoom.Kind.Overhead, _ => I76.Sim.Views.ViewZoom.Kind.Side };
        return _chase?.Current == true ? I76.Sim.Views.ViewZoom.Kind.Chase : null;
    }

    private I76.Sim.Views.ViewZoom.Kind? _zoomView;
    /// <summary>The wheel's zoom lasts as long as the view it was made in: a switch into a view (or the hand-back from a cut-scene) starts it at the original's framing.</summary>
    private void TrackViewForZoom(bool inMission)
    {
        var now = inMission && !CameraHeld ? WheelViewNow() : null;
        if (now == _zoomView) return;
        _zoomView = now;
        if (now is not null && !MouseCamera.KeepZoom) MouseCamera.ResetAllZooms();
    }

    /// <summary>
    /// Sets the pointer for a mode, whatever it is now. Compared with the engine's own mode rather than a remembered one, so a mode left behind
    /// by a scene that is gone (a mission's capture) is undone. Static: any code may call <see cref="ReleaseCursor"/> on a path out of a mission.
    /// </summary>
    private static void ApplyCursor(CursorMode mode)
    {
        var want = mode switch
        {
            CursorMode.Captured => Input.MouseModeEnum.Captured,
            CursorMode.ConfinedHidden => Input.MouseModeEnum.ConfinedHidden,
            CursorMode.Hidden => Input.MouseModeEnum.Hidden,
            _ => Input.MouseModeEnum.Visible,
        };
        if (Input.MouseMode == want) return;
        if (System.Environment.GetEnvironmentVariable("I76_CURSOR_LOG") is not null) GD.Print($"[cursor] {Input.MouseMode} -> {want}");
        Input.MouseMode = want;
    }

    /// <summary>The pointer shown and free: for any path that leaves a mission (a failed or dropped game, an exception) to call; the per-frame state function does the same from the screen that is up.</summary>
    public static void ReleaseCursor() => ApplyCursor(CursorMode.Visible);

    /// <summary>
    /// Every way out of a mission (the menu, a lost connection, a failed host migration, the next round, an exception's reload) takes this scene
    /// out of the tree: the pointer is given back here, through the one cursor function. The mouse mode is the window's, not the scene's, and the
    /// next scene sets what its own screen wants on its first frame.
    /// </summary>
    public override void _ExitTree()
    {
        if (Controls.Held == _held) Controls.Held = null;   // (a reloaded scene may have set its own already)
        if (I76.Game.ForceFeedback.Held?.Target == this) I76.Game.ForceFeedback.Held = null;   // it would keep this scene, and all it built, alive
        MouseCamera.Active = false;
        MouseCamera.CockpitActive = false;
        MouseCamera.WheelView = null;
        ReleaseCursor();
    }

    private static void ToggleFullscreen() =>
        DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);

    /// <summary>The script holds the camera (pushCam ... popCam): no view can be selected (the camera view selection at mode 5).</summary>
    private bool CameraHeld => _scriptCam?.Current == true;

    /// <summary>Gate (1): the script polled isKeypress this tick (a skippable cut-scene): only Esc (the skip) and Pause act.</summary>
    /// (A network co-op mission's script is the host's: its cut-scene is skippable here while it holds the view, and the skip the
    /// host's alone, which the server decides.)
    private bool SkipPolled => _sim?.Script is { } sc && sc.Melee is null && (NetCoop ? sc.Host.View.Active && !sc.Host.View.Personal : sc.Host.SkipPolled >= sc.Host.FsmTick - 1);

    /// <summary>A network game of a co-op story mission (docs/multiplayer.md "Co-op over the network").</summary>
    private bool NetCoop => _net is not null && _netAccept?.Coop is not null;

    /// <summary>Esc (or the pad's B) skips a cut-scene from this game: a single player's, or the seat the server takes the skip from (the host's); another player's Esc opens the menu.</summary>
    private bool SkipsCutscenes => !NetCoop || _netHud is null || _netHud.CanSkip(_netAccept!.Slot);

    /// <summary>Gate (2): the script drives the player's car (the AI drive of its orders): every key does nothing.</summary>
    private bool ScriptDrivesPlayer => _sim?.Script is { } sc && _sim.World.Player is { } p && sc.Host.Entities.Any(x => x.Vehicle == p && x.ScriptDriven);

    /// <summary>Alt+Enter (the port's fullscreen), and the gates of a cut-scene: runs before every node's own input, so a swallowed key reaches the sim's commands no more than the views.</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, AltPressed: true, Keycode: Key.Enter or Key.KpEnter } ak)
        {
            if (!ak.Echo) ToggleFullscreen();
            GetViewport().SetInputAsHandled();   // so that weapon_cycle (Enter; the exclusions have no Alt) never sees it
            return;
        }
        if (e is InputEventMouseMotion mm) { MouseCamera.Motion(mm.Relative); return; }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wb && MouseCamera.WheelActive)
        {
            MouseCamera.Wheel((wb.ButtonIndex == MouseButton.WheelUp ? 1 : -1) * (wb.Factor > 0 ? wb.Factor : 1));
            GetViewport().SetInputAsHandled();
            return;
        }
        // a playtest from the map editor: F5 or Esc goes back to the editor, the camera where it was
        if (I76.Game.Editor.EditorSession.Playtest && _sim is not null && !_loading && e is InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, Keycode: Key.F5 or Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            LeavePlaytest();
            return;
        }
        if (_sim is null || _inMenu || _menu is not null || _loading || _picture is not null || _gamePaused) return;
        // the pad's B skips a skippable cut-scene as Esc does (Start already is the menu key, which skips there)
        if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B } skipB && GodotPads.IsStandard(skipB.Device) && SkipPolled && (_net is null || NetCoop) && SkipsCutscenes)
        {
            _sim.SkipLatch = true;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not (InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })) return;
        if (e is InputEventKey { CtrlPressed: true, Keycode: >= Key.F1 and <= Key.F12 }) return;   // the port's own debug keys
        // F12 flips the look in a cut-scene too (the port's: the original's renderer cycle obeyed the gates)
        if (e is InputEventKey vk && GameKeys.CommandOf(vk) == GameKey.ToggleVideoMode)
        {
            if (!vk.Echo) ToggleLook();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (ScriptDrivesPlayer) { GetViewport().SetInputAsHandled(); return; }
        if (SkipPolled && !(e is InputEventKey sk && GameKeys.CommandOf(sk) is GameKey.MainMenu or GameKey.PauseGame)) GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// A typed cheat word. None of them marks the player a cheater. wiggleburger is the
    /// view's; getdown and the flat tyres go to the sim with the next tick, in single player only; freelance and
    /// thirdnostril do nothing, in the original too.
    /// </summary>
    private void TypedCheat(I76.Sim.Session.Cheat cheat)
    {
        if (cheat == I76.Sim.Session.Cheat.None) return;
        GD.Print($"cheat: {cheat}");
        if (cheat == I76.Sim.Session.Cheat.WiggleBurger) Wiggle.Toggle();
        else if (I76.Sim.Session.CheatTyper.Command(cheat) is var c && c != 0 && _net is null) _sim?.Cheat(c);
    }

    /// <summary>The port's debug keys, behind Ctrl (unbound in the original). Returns whether it was one.</summary>
    private bool DebugKey(InputEventKey k)
    {
        if (!k.CtrlPressed || k.AltPressed || k.ShiftPressed || !k.Pressed || k.Echo) return false;
        switch (k.Keycode)
        {
            case Key.F3 when _overlay is not null: _overlay.Visible = !_overlay.Visible; return true;
            // the cars' material maps and glass against the old all-over clear coat
            case Key.F8: GD.Print($"car materials {(I76.Game.Assets.MaterialLibrary.ToggleCarMaterials() ? "on" : "off")}"); return true;
            case Key.F4:
                _lightOnly = !_lightOnly;
                _builder?.TerrainMaterial?.SetShaderParameter("show_light_only", _lightOnly);
                return true;
            // the free camera (a debug view; C is the original's POETRY)
            case Key.F5 when _chase is not null:
                _car?.SetCockpit(false);
                _cockpitFlag = false;
                if (_chase.Current) { _cam.GlobalTransform = _chase.GlobalTransform; _cam.MakeCurrent(); }
                else _chase.MakeCurrent();
                return true;
            // the port's modern physics for the player's car, on or off (kept with the settings)
            case Key.F7 when _sim is not null && _modernSetup is not null:
                bool modern = _modern?.Enabled != true;
                EnableModern(modern);
                var ms = I76.Game.Shell.ShellSettings.Load();
                ms.ModernPhysics = modern;
                ms.Save();
                return true;
            // a campaign trip mission won on the spot (debug): the script's own end (success, no failure message), so the
            // end-of-mission flow runs as for a real win: the outro, the trip result, the shell's salvage screen
            case Key.F9 when _net is null && !_ended && _launch is { Kind: I76.Game.Shell.GameKind.Trip } && _script is { Melee: null } ws:
                ws.Host.EndMission(true, 0, -2);
                return true;
            // a network melee's way out (F10 in the original is the overhead view)
            // (from the command line, BackFromNetwork ends the program, as F10 used to)
            case Key.F10 when _net is not null && !_ended: BackFromNetwork(NetRows(), null); return true;
        }
        return false;
    }

    /// <summary>
    /// The 1997 look on or off, kept with the settings (Graphic Detail's 1997 Mode): TOGGLE_VIDEOMODE (F12), the
    /// nearest thing to the original's renderer cycle.
    /// </summary>
    private void ToggleLook()
    {
        bool to1997 = _wantModern || !_mode1997;   // a second press while the modern look builds cancels it
        SetMode1997(to1997);
        var ls = I76.Game.Shell.ShellSettings.Load();
        ls.Look1997 = _lookSaved = to1997;
        ls.Save();
    }

    private bool PlayerDead => _car is { } c && (c.Sim.Flags & I76.Sim.Vehicles.VehicleFlags.Dead) != 0;

    public override void _UnhandledInput(InputEvent e)
    {
        if (_inMenu || _menu is not null || _loading || _picture is not null || _installerOnly || InstallerScreen.Busy || _reconfig is not null) return;
        // the typed cheats first, as the original hands every code to the cheat matcher before the menus and the game keys
        if (_sim is not null && e is InputEventKey { Pressed: true } ck && GameKeys.CodeOf(ck) is var cc && (cc & 0xff00) == 0x300)
            TypedCheat(_typer.Push(cc));
        if (e is InputEventKey dk && DebugKey(dk)) return;
        // a listen host on the standings: Next Round now, without waiting out the pause
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } && _net?.Server is { } hostServer && _netOverAt is not null && !_ended)
        {
            hostServer.SkipPause();
            GetViewport().SetInputAsHandled();
            return;
        }
        // a dead network player: Enter opens Reconfigure (I26's: the car to come back in)
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } && _net is not null && PlayerDead && _netHud?.CanComeBack == true && !_ended)
        {
            OpenReconfigure();
            GetViewport().SetInputAsHandled();
            return;
        }
        // the same two ways back on a pad (the port's, shown on the modern look's death card): A comes back, Y reconfigures
        if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.A or JoyButton.Y } pb && GodotPads.IsStandard(pb.Device)
            && _net is not null && PlayerDead && _netHud?.CanComeBack == true && !_ended)
        {
            if (pb.ButtonIndex == JoyButton.Y) OpenReconfigure();
            else _sim?.RaiseDead();
            GetViewport().SetInputAsHandled();
            return;
        }
        var g = e is InputEventKey gk ? GameKeys.CommandOf(gk) : e is InputEventJoypadButton gb ? GameKeys.CommandOf(gb) : GameKey.None;
        if (_sim is null)
        {
            if (_editor is not null) return;   // the map editor's own keys (MapEditor)
            // the bare viewer: Esc and F10 leave
            if (g == GameKey.MainMenu || e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10 }) GetTree().Quit();
            return;
        }
        bool held = CameraHeld;
        // group A (whenever the game runs): Esc, Pause, Ctrl+Q, Ctrl+P and the instrument toggle; group B (a living player) the rest
        switch (g)
        {
            case GameKey.MainMenu:
                // Esc is the menu key; while the script is asking isKeypress (a cut-scene) it is the skip instead (in a network
                // co-op game the host's alone: the other players' Esc is still the menu)
                if (SkipPolled && SkipsCutscenes) _sim.SkipLatch = true;
                else OpenMenu();
                return;
            case GameKey.PauseGame when _net is null && !_ended: TogglePause(); return;
            case GameKey.ExitSimFail when !_ended:
                // quit straight to the shell's main menu, no confirmation (the escape menu's Abort Mission)
                if (_net is null) MenuClosed(MenuOutcome.Abort);
                else BackFromNetwork(NetRows(), null);
                return;
            case GameKey.SaveScreen: SaveScreen(); return;
            // TOGGLE_2DGPIT_EXT ('[' in gamekey.map, command 0x5d): flips Ext. Instrum. Disp.'s own byte, so it is kept with the settings
            case GameKey.Toggle2DCockpitExt: ToggleExtInstruments(); return;
            // TOGGLE_GOSTPIT_DETAIL (']'): the ghost cockpit exists only in render mode 1; on the hardware renderers it is forced off
            case GameKey.ToggleGhostCockpit: return;
            case GameKey.ToggleFramerate: return;   // a no-op in the release too
            // RAISE_DEAD (case 0x26): a dead network player asks the server for a new car; the server refuses
            // it before 5 s and once the game is decided
            case GameKey.RaiseDead when _net is not null && PlayerDead && !_ended && _netHud?.Decided != true: _sim.RaiseDead(); return;
        }
        // the script camera alone refuses only the views (SelectView); the mirror and self-destruct still act (F12: _Input)
        // a network game's group B also needs the game undecided (no winner yet)
        if (g != GameKey.None && !PlayerDead && _netHud?.Decided != true)
        {
            if (GameKeys.ViewOf(g) is > 0 and var view) { SelectView(view); return; }
            switch (g)
            {
                case GameKey.ToggleRearview: _pit?.CycleMirror(); return;
                case GameKey.SelfDestruct: _sim.SelfDestruct(); return;
                // the port's RESET_UPRIGHT (U): once a press, not on the key's repeat; the session refuses it in a cut-scene and where the host turned it off
                case GameKey.ResetUpright: if (e is InputEventKey { Echo: false }) _sim.ResetUpright(); return;
                // the listen host's answers to an invite-only join and its boots (cases 0x29..0x32; the host alone)
                case GameKey.InviteAccept or GameKey.InviteDecline or GameKey.InviteSkip when _netHud is { Server: not null } hh: hh.HostKey(g); return;
                case >= GameKey.Boot1 and <= GameKey.Boot1 + 6 when _netHud is { Server: not null } hb: hb.HostKey(g, g - GameKey.Boot1 + 1); return;
            }
        }
        // the port's BOOT_8 and BOOT_9 (Ctrl+8, Ctrl+9: the rest of a score window page of nine numbered rows)
        if (e is InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, AltPressed: false, ShiftPressed: false, Keycode: Key.Key8 or Key.Key9 } bk
            && _netHud is { Server: not null } hk && !PlayerDead && !hk.Decided)
        {
            hk.HostKey(GameKey.None, bk.Keycode == Key.Key8 ? 8 : 9);
            return;
        }
        // M and N: the mission map and the objectives notepad, not while the script holds the view
        // (not in a network melee: the pictures pause the game, and the server does not stop; a co-op mission's go on over the
        // running game, the car held, as its objectives are the point)
        if (!held && !_ended && (_net is null || NetCoop))
        {
            if (Controls.Pressed(e, "SHOW_MAP")) { OpenPicture(MissionPicture.Kind.Map); return; }
            if (Controls.Pressed(e, "SHOW_NOTEPAD")) { OpenPicture(MissionPicture.Kind.Notepad); return; }
        }
        // toggle_binoculars (B): not while the script holds the camera
        if (!held && Controls.Pressed(e, "TOGGLE_BINOCULARS") && !PlayerDead) { SelectView(0xd); return; }
        // toggle_cmbt_view (V): the cockpit and the last external view (the combat-view toggle); no view changes while the script holds the camera
        if (!held && Controls.Pressed(e, "toggle_cmbt_view") && _car is not null && _chase is not null)
        {
            if (_cockpitFlag) SelectView(_lastPreset);
            else SelectView(0xe);
        }
    }

    /// <summary>Pause (PAUSE_GAME, single player): the world and the sound stop; only Pause starts them.</summary>
    private void TogglePause()
    {
        _gamePaused = !_gamePaused;
        GetTree().Paused = _gamePaused;
        if (GetNodeOrNull<AudioStreamPlayer>("Music") is { } music) music.StreamPaused = _gamePaused;
    }

    /// <summary>Ctrl+P: the frame as a picture, SCR0000.png ... (the original's SCR%04d.pcx), at the first free number, so an earlier run's pictures stay.</summary>
    private void SaveScreen()
    {
        DirAccess.MakeDirRecursiveAbsolute("user://screenshots");
        while (Godot.FileAccess.FileExists($"user://screenshots/SCR{_shotCounter:0000}.png")) _shotCounter++;
        var path = $"user://screenshots/SCR{_shotCounter++:0000}.png";
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"screen saved: {path}");
    }

    private readonly HashSet<int> _loggedViews = [];

    /// <summary>
    /// The camera view selection for a PRESET_VIEW command (0xe..0x19; 0xd is the binoculars): the V state is set as
    /// The original's game-key dispatch sets it (F1 the cockpit; F2..F11 an external view, remembered); refused while the script holds the camera.
    /// </summary>
    /// <summary>TOGGLE_2DGPIT_EXT ('[', command 0x5d; and F2 twice): Ext. Instrum. Disp.'s own byte, so it is kept with the settings.</summary>
    private void ToggleExtInstruments()
    {
        var gs = I76.Game.Shell.ShellSettings.Load();
        gs.Graphics[0xe] ^= 1;
        gs.Save();
        _extInstruments = gs.Graphics[0xe] != 0;
    }

    private void SelectView(int cmd)
    {
        if (CameraHeld || _car is null || _chase is null) return;
        if (cmd is 0x18 or 0x19 or 0x12) { EnterFollowView(cmd); return; }
        if (cmd is 0xe or 0xd && !_car.HasInterior) cmd = 0xf;   // no cockpit model (the Armadillo): the chase view stands in
        // every other view's setup first pops the camera stack when the camera is in mode 4 (a target view); the projectile camera
        // (mode 8) is just replaced, its stack entry stays
        if (_tvMode == 4 && _viewStack.Count > 0) _viewStack.RemoveAt(_viewStack.Count - 1);
        _tvMode = 0;
        if (cmd == 0xd)
        {
            // The binocular view set-up: the V state first (the cockpit next, the binoculars after), then the toggle, from the cockpit or the binoculars only
            _cockpitFlag = false;
            _lastPreset = 0xd;
            if (_pit is not null && _car.CockpitCamera?.Current == true && !_car.Wrecked) _pit.SetBinoculars(!_pit.Binoculars);
            return;
        }
        if (cmd == 0xe)
        {
            _cockpitFlag = true;
            if (_pit?.Binoculars == true && !_car.Wrecked) { _pit.SetBinoculars(false); return; }   // F1 / V from the binoculars: the cockpit
            // F1 while already in the cockpit toggles the 3D/2D dash, kept with the settings; from another view it enters in the mode it has
            if (_car.Wrecked) return;
            bool inCar = _car.CockpitCamera?.Current == true;
            bool dash = I76.Formats.Input.DashToggle.OnF1(inCar, _car.Dash3D);
            if (dash != _car.Dash3D)
            {
                _car.Dash3D = dash;
                var ds = I76.Game.Shell.ShellSettings.Load();
                ds.Dash3D = dash;
                ds.Save();
            }
            if (inCar) return;
            _car.SetCockpit(true);
            return;
        }
        // the port's: F2 while already in the chase view toggles the external instruments, as '[' does
        if (cmd == 0xf && _lastPreset == 0xf && _chase.Current && _chase.Fixed is null && _car.CockpitCamera?.Current != true) { ToggleExtInstruments(); return; }
        _cockpitFlag = false;
        _lastPreset = cmd;
        _car.SetCockpit(false);
        _chase.Fixed = null;
        ApplyExternalView(cmd);
    }


    // ---- F4, F5 and F11: the views that follow something, and the original's 8-deep camera stack ----

    /// <summary>The camera stack: the views these push (F4, F5, F11 when the camera is not already a target view) and pop when the thing is gone.</summary>
    private readonly List<int> _viewStack = [];
    /// <summary>The camera mode as far as these views go: 4 a target view, 8 the projectile camera, 0 anything else.</summary>
    private int _tvMode;
    /// <summary>The latest round the player fired that the projectile camera can follow, until it is gone.</summary>
    private I76.Sim.Weapons.Round? _latestRound;
    private readonly HashSet<I76.Sim.Weapons.Round> _seenRounds = [];

    /// <summary>The radar's target (none once it is dead; the radar drops it when it leaves its range).</summary>
    private I76.Sim.Vehicles.Vehicle? RadarTargetNow() =>
        _car is { } c && _sim?.Combat is { } cb && cb.RadarTarget.GetValueOrDefault(c.Sim) is { } t && !I76.Sim.Weapons.Combat.IsDead(t) ? t : null;

    private void TrackLatestRound()
    {
        if (_sim?.Combat is not { } cb || _car is null) return;
        var rounds = cb.Rounds;
        // a network client draws its own bullets and missiles from its prediction (PredictedRounds), not from the server's
        // rounds, so F11 follows those too (#109)
        var predicted = cb.PredictedRounds;
        if (_latestRound is not null && !rounds.Contains(_latestRound) && !predicted.Contains(_latestRound)) _latestRound = null;
        if (rounds.Count == 0 && predicted.Count == 0 && _seenRounds.Count == 0) return;
        foreach (var r in rounds.Concat(predicted))
            if (_seenRounds.Add(r) && r.Shooter == _car.Sim && I76.Sim.Views.TargetViews.Followable(r.Kind)) _latestRound = r;
        if (_seenRounds.Count > 256) { _seenRounds.IntersectWith(rounds); _seenRounds.UnionWith(predicted); }
    }

    /// <summary>The command of the view on screen, for the stack.</summary>
    private int CurrentViewCmd()
    {
        if (_car?.CockpitCamera?.Current == true) return _pit?.Binoculars == true ? 0xd : 0xe;
        if (_tv?.Current == true) return _tv.Mode switch { TargetViewCamera.View.Target => 0x19, TargetViewCamera.View.Reverse => 0x18, _ => 0x12 };
        if (_preset?.Current == true) return _preset.Mode switch { PresetCamera.View.Orbit => 0x11, PresetCamera.View.Side => 0x13, PresetCamera.View.SideBack => 0x14, _ => 0x17 };
        if (_chase?.Fixed is { } fx) return Mathf.Abs(fx.Yaw - Mathf.Pi) < 0.01f ? 0x10 : Mathf.Abs(fx.Yaw - Mathf.DegToRad(30)) < 0.01f ? 0x16 : 0x15;
        return 0xf;
    }

    /// <summary>
    /// F4 (0x19), F5 (0x18) and F11 (0x12). The V state is stored first, as the key dispatch does
    /// before the camera view selection, which refuses without a radar target (F4, F5) or a round (F11), when the stack is full, and for F11 when it
    /// is already the camera. Otherwise the view on screen is pushed (not when the camera is already a target view) and this one begins.
    /// </summary>
    private void EnterFollowView(int cmd)
    {
        if (_car is null || _chase is null || _tv is null) return;
        int current = CurrentViewCmd();
        _cockpitFlag = false;
        _lastPreset = cmd;
        if (cmd == 0x12 ? _latestRound is null || _tvMode == 8 : RadarTargetNow() is null) return;
        if (cmd == 0x12 || _tvMode != 4)
        {
            if (_viewStack.Count >= 8) return;
            _viewStack.Add(current);
        }
        _car.SetCockpit(false);
        _chase.Fixed = null;
        _tvMode = cmd == 0x12 ? 8 : 4;
        _tv.Start(cmd == 0x19 ? TargetViewCamera.View.Target : cmd == 0x18 ? TargetViewCamera.View.Reverse : TargetViewCamera.View.Projectile, cmd == 0x12 ? _latestRound : null);
        _tv.MakeCurrent();
    }

    /// <summary>The grace ran out: the camera stack pops and the view that was pushed comes back (a restored copy of its camera state).</summary>
    private void PopViewStack()
    {
        int cmd = 0xf;
        if (_viewStack.Count > 0) { cmd = _viewStack[^1]; _viewStack.RemoveAt(_viewStack.Count - 1); }
        _tvMode = 0;
        if (_car is null || _chase is null) return;
        if (cmd is 0xe or 0xd && !_car.Wrecked)
        {
            _car.SetCockpit(true);
            if (cmd == 0xd) _pit?.SetBinoculars(true);
            return;
        }
        _car.SetCockpit(false);
        _chase.Fixed = null;
        ApplyExternalView(cmd is 0xe or 0xd or 0x12 or 0x18 or 0x19 ? 0xf : cmd);
    }

    private void ApplyExternalView(int cmd)
    {
        if (_car is null || _chase is null) return;
        switch (cmd)
        {
            case 0xf: _chase.MakeCurrent(); break;                                                     // F2: chase
            case 0x10: FixedFront(15, 180); break;                                                     // F3: front
            case 0x15: FixedFront(15, 150); break;                                                     // F9: front quarter
            case 0x16: FixedFront(15, 30); break;                                                      // PRESET_VIEW_9: the same view at yaw 30; unbound in the shipped gamekey.map
            case 0x11: Preset(PresetCamera.View.Orbit); break;                                         // F6
            case 0x13: Preset(PresetCamera.View.Side); break;                                          // F7
            case 0x14: Preset(PresetCamera.View.SideBack); break;                                      // F8
            case 0x17: Preset(PresetCamera.View.Overhead); break;                                      // F10
            default:
                if (_loggedViews.Add(cmd)) GD.Print($"view 0x{cmd:x} is not implemented; showing the chase camera");
                _chase.MakeCurrent();
                break;
        }
        void FixedFront(float pitchDeg, float yawDeg) { _chase.Fixed = (Mathf.DegToRad(pitchDeg), Mathf.DegToRad(yawDeg), 2f); _chase.MakeCurrent(); }
        void Preset(PresetCamera.View v) { _preset!.Start(v); _preset.MakeCurrent(); }
    }

    private string OverlayText()
    {
        var g = CameraGame();
        var s = _status + $"\nmode: {(_mode1997 ? "1997 (F12)" : "modern (F12)")}  physics: {(_modern is { Enabled: true } mp ? (mp.Active ? "modern (Ctrl+F7)" : "modern, the script drives (Ctrl+F7)") : "original (Ctrl+F7)")}  hour {_builder?.Settings?.Hour} (preset {_builder?.Light.Index}, intensity {_builder?.Light.Intensity:0.###})\ncamera (game): {g.X:0.0} {g.Y:0.0} {g.Z:0.0}";
        if (_car is not null)
        {
            var c = _car.Sim;
            s += $"\ncar: {c.Speed:0.0} u/s ({c.Speed * 2.237f:0} mph if units are metres)  {(c.Gear < 0 ? "R" : c.Engine.Gear.ToString())} {c.Engine.Rpm:0} rpm  flags 0x{(uint)c.Flags:x}  surface {c.Surface}  mass {c.P.Mass:0}";
        }
        if (_builder?.World is { } w)
        {
            s += $"  ground {w.GroundHeight(g.X, g.Z):0.00}";
            s += $"\nground texture: {_builder.GroundTexture ?? "none"}";
            if (_builder.Meshes!.Unresolved.Count > 0) s += $"\nunresolved textures: {string.Join(" ", _builder.Meshes.Unresolved.Take(8))}";
            if (_builder.Scenery!.FailedNames.Count > 0) s += $"\nunresolved objects: {string.Join(" ", _builder.Scenery.FailedNames.Take(8))}";
        }
        return s;
    }

    /// <summary>
    /// I76_SPAWN_AI_AT=right,ahead[,yaw[,speed]][;right,ahead...][@frame[-until]]: the nearest other car moved there in the
    /// player's frame (game units; yaw in degrees off the player's heading; launched at that speed), at that sim tick
    /// (default 3, once a melee has spawned its opponents), for captures of a fight at close range. Several places: the
    /// nearest cars, one each, in order. Held up to `until`, a car stays where it was first put (in the player's frame as it
    /// was then), so a moving player drives past it.
    /// </summary>
    private void ApplySpawnAi()
    {
        if (_sim?.Session is not { } session || !session.Options.DebugHooks) return;
        var world = session.World;
        if (System.Environment.GetEnvironmentVariable("I76_SPAWN_AI_AT") is not { } ai || world.Player is not { } pp) return;
        if (ai.Split('@') is { Length: 2 } parts) ai = parts[0];
        _spawnAiFrame ??= (pp.Pos.ToVector3(), pp.Rot.Right, pp.Rot.Forward);
        var (at, right, fwd) = _spawnAiFrame.Value;
        var others = world.Vehicles.Where(v => v != pp && !v.IsEmplacement).OrderBy(v => System.Numerics.Vector3.Distance(v.Pos.ToVector3(), at)).ToList();
        _spawnAiCars ??= others;
        var places = ai.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < places.Length && i < _spawnAiCars.Count; i++)
        {
            var other = _spawnAiCars[i];
            if (places[i].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is not { Length: >= 2 } b) continue;
            var p = at + right * b[0] + fwd * b[1];
            float yaw = b.Length > 2 ? Mathf.DegToRad(b[2]) : 0;
            var f = fwd * Mathf.Cos(yaw) + right * Mathf.Sin(yaw);
            f = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(f.X, 0, f.Z));
            other.Pos = new I76.Sim.Math.Double3(p.X, world.Ground.BareHeight(p.X, p.Z), p.Z);
            other.Rot = new I76.Sim.Math.Rows(System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitY, f), System.Numerics.Vector3.UnitY, f);
            float speed = b.Length > 3 ? b[3] : 0;
            other.Vel = f * speed; other.Speed = speed;
        }
    }

    /// <summary>The player's place and heading when I76_SPAWN_AI_AT first applied, and the cars it took (nearest first).</summary>
    private (System.Numerics.Vector3 At, System.Numerics.Vector3 Right, System.Numerics.Vector3 Forward)? _spawnAiFrame;
    private System.Collections.Generic.List<I76.Sim.Vehicles.Vehicle>? _spawnAiCars;

    private static readonly int SpawnAiTick = System.Environment.GetEnvironmentVariable("I76_SPAWN_AI_AT")?.Split('@') is { Length: 2 } sp && int.TryParse(sp[1].Split('-')[0], out int sf) ? sf : 3;
    /// <summary>I76_SPAWN_AI_AT=...@from-to: the car held there, placed again every frame up to that tick (a target that stays put).</summary>
    private static readonly int SpawnAiUntil = System.Environment.GetEnvironmentVariable("I76_SPAWN_AI_AT")?.Split('@') is { Length: 2 } sp2 && sp2[1].Split('-') is [_, var until] && int.TryParse(until, out int su) ? su : -1;
    private bool _spawnAiDone;

    private void ApplyCapCam()
    {
        // I76_CAP_CAM="dx,dy,dz,yaw,pitch": a fixed capture camera at that offset from the player's car (Godot axes, degrees)
        // I76_CAP_CAM_ON=ODEF: the offset is from the first car of that definition (a prefix: vcpolic) instead of the player's
        var on = System.Environment.GetEnvironmentVariable("I76_CAP_CAM_ON") is { Length: > 0 } pre
            ? _sim?.Cars.FirstOrDefault(c => c.Definition.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) : null;
        if ((on ?? _car) is { } target && System.Environment.GetEnvironmentVariable("I76_CAP_CAM")?.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is { Length: 5 } cc)
        {
            _cam.Position = new Vector3(cc[0], cc[1], cc[2]) + target.Position;
            _cam.SetYawPitch(cc[3], cc[4]);
            // the cockpit's panels off, as I76_FREECAM does (a skipped intro hands the view back to the cockpit)
            _car?.SetCockpit(false);
            if (!_cam.Current) _cam.MakeCurrent();
        }
    }

    private System.Numerics.Vector3 CameraGame() => _origin + Coords.ToGame(ActiveCamera.GlobalPosition);
    private Camera3D ActiveCamera => _scriptCam is { Current: true } s ? s : _car?.CockpitCamera is { Current: true } k ? k : _preset is { Current: true } pc ? pc : _tv is { Current: true } tc ? tc : _chase is { } c && c.Current ? c : _cam;

    /// <summary>
    /// Runs while the tree is paused (the escape menu): the capture hooks keep
    /// counting frames, and I76_GAME_KEYS="escape@40;down@44;enter@48" presses
    /// keys at this node's own frame numbers, for captures of the menus.
    /// </summary>
    private sealed partial class PausedClock : Node
    {
        public required Main Owner { get; init; }
        // counted across scenes: after a replay the key script carries on rather than starting over
        private static int _n;
        // KEY@frame, or KEY@from-to to hold it; KEY may carry modifiers: ctrl+q, alt+x, ctrl+alt+x, shift+f1
        private readonly List<(int Frame, int Until, Key Key, int Mods)> _keys = (System.Environment.GetEnvironmentVariable("I76_GAME_KEYS") ?? "")
            .Split(';', System.StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('@'))
            .Where(p => p.Length == 2 && ParseKey(p[0]) is not null)
            .Select(p => (p[1].Split('-') is [var a, var b] ? (int.Parse(a), int.Parse(b)) : (int.Parse(p[1]), int.Parse(p[1])), ParseKey(p[0])!.Value))
            .Select(x => (x.Item1.Item1, x.Item1.Item2, x.Value.Key, x.Value.Mods)).ToList();

        // I76_MOUSE="frame@dx,dy;..." moves the mouse by that much at those frames (a capture of the Mouse Camera)
        private readonly List<(int Frame, float Dx, float Dy)> _mouse = (System.Environment.GetEnvironmentVariable("I76_MOUSE") ?? "")
            .Split(';', System.StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('@', ',')).Where(p => p.Length == 3)
            .Select(p => (int.Parse(p[0]), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture))).ToList();

        // I76_MOUSE also takes "frame@wN": N wheel notches at that frame (positive up, nearer), the external view on screen's zoom
        private readonly List<(int Frame, int Notches)> _wheel = (System.Environment.GetEnvironmentVariable("I76_MOUSE") ?? "")
            .Split(';', System.StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('@')).Where(p => p.Length == 2 && p[1].StartsWith('w') && int.TryParse(p[1][1..], out _))
            .Select(p => (int.Parse(p[0]), int.Parse(p[1][1..]))).ToList();

        private static (Key Key, int Mods)? ParseKey(string text)
        {
            var terms = text.Split('+');
            int mods = 0;
            foreach (var t in terms[..^1]) mods |= t.ToLowerInvariant() switch { "ctrl" or "control" => 1, "alt" => 2, "shift" => 4, _ => 8 };
            return mods < 8 && System.Enum.TryParse<Key>(terms[^1], true, out var k) ? (k, mods) : null;
        }

        private static void Post(Key k, int mods, bool pressed) =>
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, KeyLabel = k, Pressed = pressed, CtrlPressed = (mods & 1) != 0, AltPressed = (mods & 2) != 0, ShiftPressed = (mods & 4) != 0 });

        public override void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            // I76_AUDIO_REC=file.f32: the Master bus's left channel as little-endian float32 (44100 Hz), for checking a sound for clicks;
            // I76_AUDIO_REC_STEREO=1 writes left and right interleaved (for checking a sound's panning)
            if (System.Environment.GetEnvironmentVariable("I76_AUDIO_REC") is { Length: > 0 } path)
            {
                var cap = new AudioEffectCapture { BufferLength = 1f };
                AudioServer.AddBusEffect(0, cap);
                _rec = (cap, Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write));
            }
        }

        private (AudioEffectCapture Cap, Godot.FileAccess File)? _rec;

        private void Record()
        {
            if (_rec is not var (cap, file)) return;
            bool stereo = System.Environment.GetEnvironmentVariable("I76_AUDIO_REC_STEREO") == "1";
            foreach (var f in cap.GetBuffer(cap.GetFramesAvailable())) { file.StoreFloat(f.X); if (stereo) file.StoreFloat(f.Y); }
            file.Flush();
        }

        // while the tree is paused only these run: Alt+Enter, and Pause to start the game again (Esc, M and N do nothing then)
        public override void _Input(InputEvent e)
        {
            if (GetTree().Paused && e is InputEventKey { Pressed: true, AltPressed: true, Keycode: Key.Enter or Key.KpEnter } ak)
            {
                if (!ak.Echo) ToggleFullscreen();
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (Owner._gamePaused && Owner._menu is null && Owner._picture is null && e is InputEventKey k && GameKeys.CommandOf(k) == GameKey.PauseGame)
            {
                Owner.TogglePause();
                // handled: Main, a parent, gets the unhandled event after this node and would pause again now that it runs
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _Process(double delta)
        {
            _n++;
            Record();
            Owner.UpdateMouseCapture();
            foreach (var (f, dx, dy) in _mouse.Where(e => e.Frame == _n)) Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(dx, dy) });
            foreach (var (f, n) in _wheel.Where(e => e.Frame == _n))
                for (int i = 0; i < Math.Abs(n); i++) Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = n > 0 ? MouseButton.WheelUp : MouseButton.WheelDown, Pressed = true, Factor = 1 });
            // each key is down for one frame, so held-key checks see it too
            foreach (var (f, u, k, m) in _keys.Where(e => e.Until == _n - 1)) Post(k, m, false);
            foreach (var (f, u, k, m) in _keys.Where(e => e.Frame == _n)) Post(k, m, true);
            if (!GetTree().Paused || (Owner._menu is null && Owner._picture is null)) return;
            var o = Owner._opt;
            if (o.Screenshot is not null && ++Owner._frames == System.Math.Max(o.QuitAfter, 10) - 2) GetViewport().GetTexture().GetImage().SavePng(o.Screenshot);
            if (o.QuitAfter > 0 && Owner._frames >= o.QuitAfter) GetTree().Quit();
        }
    }

    /// <summary>The frame clock the animated textures step by (the rotors' blades, the default texture animation tick).</summary>
    private double _animClock;

    /// <summary>This scene shows only the game chooser (and an extraction started from it).</summary>
    private bool _chooser;

    private void ShowChooser()
    {
        GetNodeOrNull("Installer")?.QueueFree();
        AddChild(new GameChooser
        {
            Name = "Chooser", Games = _opt.Games.ToList(),
            OnChosen = g =>
            {
                GetNodeOrNull("Chooser")?.QueueFree();
                if (g.InstallDir is { } dir) { LaunchOptions.Extracted = dir; GetTree().ReloadCurrentScene(); return; }
                AddChild(new InstallerScreen { Name = "Installer", AutoInstaller = g.InstallerPath, OnInstalled = _ => GetTree().ReloadCurrentScene(), OnBack = ShowChooser });
            },
        });
    }

    /// <summary>This scene shows only the --from-installer extraction (the game follows on a scene reload).</summary>
    private bool _installerOnly;

    public override void _Process(double delta)
    {
        FrameLimiter.Pace();
        UpdateMouseCapture();
        if (_sim?.World.Player is { } ptp && OS.GetEnvironment("I76_PLAYTEST_LOG") is { Length: > 0 } && ++_ptFrames % 20 == 0)
            GD.Print($"playtest car: frame {_ptFrames} at {ptp.Pos.X:0.0}, {ptp.Pos.Z:0.0} speed {ptp.Vel.Length():0.0}");
        if (_installerOnly || _opt.Install is null)
        {
            // the installer screens: --from-installer's capture goes to OUT_installer.png and its --quit-after counts from
            // the game's start (a scene reload away); the no-install screen's are the usual ones
            if (_chooser && GameChooser.Loading) return;
            if (_opt.Screenshot is not null && ++_frames == System.Math.Max(_opt.QuitAfter, 10) - 2)
                GetViewport().GetTexture().GetImage().SavePng(_installerOnly && !_chooser ? _opt.Screenshot.Replace(".png", "_installer.png") : _opt.Screenshot);
            if ((!_installerOnly || _chooser) && !InstallerScreen.Busy && _opt.QuitAfter > 0 && _frames >= _opt.QuitAfter) GetTree().Quit();
            return;
        }
        if (_loading) return;
        if (!_switchFrameLogged) { _switchFrameLogged = true; I76.Game.Editor.SwitchTimer.Mark(I76.Game.Editor.EditorSession.Playtest ? "playtest: first interactive frame" : "editor: first interactive frame", true); }
        // I76_PLAYTEST_RETURN=N (capture): F5 is pressed N frames after a playtest opened
        if (I76.Game.Editor.EditorSession.Playtest && _sim is not null && _debugReturn > 0 && ++_playFrames == _debugReturn) Input.ParseInputEvent(new InputEventKey { Keycode = Key.F5, PhysicalKeycode = Key.F5, Pressed = true });
        PumpModernBuild();
        if (!_horizonEyeSet) SetHorizonEye();
        _animClock += delta;
        _builder?.Materials?.Animate(_animClock);
        if (_inMenu)
        {
            if (I76.Game.Shell.Shell.ScriptWaiting) return;
            if (_opt.Screenshot is not null && System.Environment.GetEnvironmentVariable("I76_SHOT_SERIES") is { } ser && _frames + 1 > System.Math.Max(_opt.QuitAfter, 10) - 2 - int.Parse(ser))
                SaveSeriesFrame(_opt.Screenshot.Replace(".png", $"_{_frames + 1}.png"));
            if (_opt.Screenshot is not null && ++_frames == System.Math.Max(_opt.QuitAfter, 10) - 2) GetViewport().GetTexture().GetImage().SavePng(_opt.Screenshot);
            if (_opt.QuitAfter > 0 && _frames >= _opt.QuitAfter) GetTree().Quit();
            return;
        }
        // Draw Distance: Original clips where the original did (the world camera rebuild's 600), in either look
        ActiveCamera.Far = I76.Formats.Imaging.DrawDistance.CameraFar(_drawDistance);
        UpdateSoftwareBand();   // MainSoftware.cs
        if (_pit?.Binoculars == true && ActiveCamera == _car?.CockpitCamera) ActiveCamera.Far = System.Math.Min(ActiveCamera.Far * 8, 100000f);   // Far = zoom * far, zoom 8
        bool free = ActiveCamera == _cam;   // the fly camera (--cam): the origin follows it, not the car, or a far view loses depth precision
        if (_car is not null && !free && _car.Position.Length() > RebaseDistance)
        {
            // keep the origin near the car; everything in Main space shifts with it
            var shift = Coords.ToGodot(_car.GamePosition - _origin);
            Rebase(_car.GamePosition);
            _cam.Position -= shift;
            if (_chase is not null) _chase.Position -= shift;
            _sim?.PlaceCars();   // the sim placed them against the old origin earlier this frame: the camera follows the car, the ground would be a rebase away
        }
        else if ((_car is null || free) && _editor is null && _cam.Position.Length() > RebaseDistance)
        {
            var g = CameraGame();
            Rebase(g);
            _cam.Position = Vector3.Zero;
        }
        UpdateScriptCamera();
        // capture hook: I76_FREECAM=x,y,z,yaw,pitch[,fov] (game units, degrees; fov vertical) holds the fly camera there while a mission
        // runs (--drive), so that cars elsewhere on the map are seen with the camera, not the player, deciding which are near
        if (_sim is not null && _scriptCam is not { Current: true } && System.Environment.GetEnvironmentVariable("I76_FREECAM") is { } fc
            && fc.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is { Length: 5 or 6 } fa)
        {
            if (fa.Length == 6) _cam.Fov = fa[5];
            _car?.SetCockpit(false);
            if (!_cam.Current) _cam.MakeCurrent();
            Rebase(new System.Numerics.Vector3(fa[0], fa[1], fa[2]));
            _cam.Position = Vector3.Zero;
            _cam.SetYawPitch(fa[3], fa[4]);
        }
        LeaveDeadCockpit();
        TrackLatestRound();
        CheckpointFrame();
        // test hook: I76_CHECKPOINT_RETRY=frame chooses "Retry from Checkpoint" at that frame (once: not again in the retry)
        if (!_resumed && int.TryParse(System.Environment.GetEnvironmentVariable("I76_CHECKPOINT_RETRY"), out int retryAt) && _frames == retryAt && CanRetry && _menu is null)
        {
            RetryCheckpoint();
            return;
        }
        // test hook: I76_REPLAY_AT=frame chooses Replay Mission from the menu at that frame (every load: a run of restarts)
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_REPLAY_AT"), out int replayAt) && _frames == replayAt && !_ended && _menu is null)
        {
            GD.Print($"replay: {I76.Game.Editor.SwitchTimer.Memory()}");
            MenuClosed(MenuOutcome.Replay);
            return;
        }
        if (_spawnStops.Count > 0 && _sim?.Session is { } stopSess && stopSess.World.Player is { } sp && stopSess.World.Time >= _spawnStops.Peek().At)
        {
            var (stopAt, stop) = _spawnStops.Dequeue();
            PlacePlayer(sp, stopSess.World.Ground, stop);
            GD.Print($"spawn stop at {stopAt} s: {stop[0]}, {stop[1]}");
        }
        if (_sim?.Session is { } sess && sess.Ticks >= SpawnAiTick && (!_spawnAiDone || sess.Ticks <= SpawnAiUntil)) { _spawnAiDone = true; ApplySpawnAi(); }
        if (_frames >= 4) ApplyCapCam();
        _frames++;
        if (_net is not null && _launch is null or { Kind: I76.Game.Shell.GameKind.Multiplayer } && !_ended) { NetWatch(); NetReconfigureAnswers(); }
        // the game ended (back to the shell, the next round): ReloadCurrentScene took this scene out of the tree at once
        if (!IsInsideTree()) return;
        if (_net?.Server is not null) NetHurt();
        // a melee ends by itself (state 1) five seconds after the player is destroyed
        if (_script?.Melee is { Over: true }) EndMission(true);
        // the script's end: a win goes on (the outro, the shell); a failure, or a win with cheats, gets the end menu
        else if (_script?.Result is { } result && !_ended && !_endMenuShown && _menu is null)
        {
            // a network game runs on under the map or the notepad: the end comes over them, so they go
            if (_net is not null && _picture is not null) { _picture.QueueFree(); _picture = null; }
            if (result && !_cheated) EndMission(true);
            else
            {
                _endMenuShown = true;
                int m = _script.Host.End?.FailMessage ?? -1;
                OpenMenu((result ? null : _objectives is { } t && m >= 0 && m < t.Failures.Count ? t.Failures[m] : null, result));
            }
        }
        // test hook: I76_SCRIPT_MOVIE=name.smk@frame asks for that movie as the script's playMovie does (T05 and T06 play one part way)
        if (_script is not null && System.Environment.GetEnvironmentVariable("I76_SCRIPT_MOVIE") is { } sm && sm.Split('@') is [var smName, var smAt]
            && int.TryParse(smAt, out int smFrame) && _frames == smFrame)
            _script.Host.PlayMovie(smName);
        // test hook: I76_END_MISSION=win@frame (or fail@frame) ends the mission as the script would
        if ((_launch is not null || _script is not null) && System.Environment.GetEnvironmentVariable("I76_END_MISSION") is { } end && end.Split('@') is [var how, var at]
            && int.TryParse(at, out int endFrame) && _frames == endFrame)
        {
            // through the script's own end where there is one (a failure with its first message), so the end menu comes up as it would;
            // a network co-op mission's is the listen server's script (a client's comes as the server's end)
            if (NetCoop) _net!.Server?.Invoke(h => h.Setup.Script!.Host.EndMission(how == "win", 0, how == "win" ? -2 : 0));
            else if (_script is { Melee: null } es) es.Host.EndMission(how == "win", 0, how == "win" ? -2 : 0);
            else EndMission(how == "win");
        }
        // test hook: I76_NET_TEST holds the capture's frame count through a network game (MainNet.cs)
        if (_net is not null && NetTest is not null) { NetTestFrame(); return; }
        // capture hook: I76_SHOT_EVERY=N saves every Nth frame as OUT_<frame>.png (frames as the sim counts them: ticks)
        if (_opt.Screenshot is not null && int.TryParse(System.Environment.GetEnvironmentVariable("I76_SHOT_EVERY"), out int every) && every > 0
            && _sim is { } sd && sd.Ticks != _lastShotTick && sd.Ticks % every == 0)
        {
            _lastShotTick = sd.Ticks;
            GetViewport().GetTexture().GetImage().SavePng(_opt.Screenshot.Replace(".png", $"_t{sd.Ticks:00000}.png"));
        }
        if (_opt.Screenshot is not null && System.Environment.GetEnvironmentVariable("I76_SHOT_SERIES") is { } series && _frames > System.Math.Max(_opt.QuitAfter, 10) - 2 - int.Parse(series))
            SaveSeriesFrame(_opt.Screenshot.Replace(".png", $"_{_frames}.png"));
        if (_opt.Screenshot is not null && _frames == System.Math.Max(_opt.QuitAfter, 10) - 2)
        {
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(_opt.Screenshot);
            GD.Print($"screenshot: {_opt.Screenshot}");
        }
        if (_opt.QuitAfter > 0 && _frames >= _opt.QuitAfter) GetTree().Quit();
    }
}
