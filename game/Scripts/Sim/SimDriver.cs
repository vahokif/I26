using Godot;
using I76.Sim;
using I76.Sim.Session;

namespace I76.Game.Sim;

/// <summary>
/// The Godot host of a <see cref="SimSession"/>: runs it at its fixed tick
/// (the session's rate, 20 Hz by default: 102/2048 s) and draws every car interpolated. Play mode's fixed
/// tick is a recorded deviation: the original stepped with its frame delta;
/// reference mode replays a trace's deltas. The player's car takes the
/// commands of input.map (Controls), gathered into one <see cref="CarInput"/>
/// a tick: throttle_up and _down, steer_left and _right (+1 is left in the
/// engine), e_brake, the weapons and targeting keys, and from the cockpit
/// the view and the glance that raises an arm. Keys pressed between ticks
/// wait for the next one (<see cref="CommandQueue"/>).
/// </summary>
/// <summary>
/// What runs the cars instead of a local tick (a network client): it takes
/// the driver's input each tick and moves the cars to what it has.
/// </summary>
public interface IRemoteSim
{
    /// <summary>The driver's input for the next tick (sent to the server).</summary>
    void SendInput(CarInput input);
    /// <summary>
    /// The frame: brings the cars' states and poses up to date; returns how far the view is between the two poses.
    /// <paramref name="tickAlpha"/> is how far the input clock is into its next tick (a predicted own car is drawn there).
    /// </summary>
    float Advance(double delta, float tickAlpha);
    /// <summary>How fast the input clock runs against the tick (the server keeps an input or two of ours in hand).</summary>
    double ClockScale { get; }
    /// <summary>The tick the view shows (the server's).</summary>
    int Tick { get; }
    /// <summary>A co-op mission's movie has the game stand still for everyone: no input goes out, nothing is predicted or extrapolated.</summary>
    bool Frozen => false;
}

public partial class SimDriver : Node
{
    /// <summary>A network client's view: no local tick, the cars as the server sends them, the own car predicted (docs/multiplayer.md).</summary>
    public IRemoteSim? Remote { get; set; }
    /// <summary>A network co-op mission: the mirror's script host is driven by the host's events, and its radio and notes are played here as a local game's.</summary>
    public bool HookRemoteScript { get; set; }
    /// <summary>A network game's menu is open over the running game: the car is sent neutral input (the menu's arrow keys do not drive it).</summary>
    public Func<bool>? InputHeld { get; set; }
    /// <summary>The session's step (a network client's is the server's, from its join accept).</summary>
    public float Tick => Session.TickSeconds;
    public SimSession Session { get; set; } = null!;
    public SimWorld World => Session.World;
    public List<CarNode> Cars { get; } = [];
    public CarNode? Player { get; set; }
    public bool HoldThrottle { get; set; }
    public bool HoldFire { get; set; }
    /// <summary>How far the display is between the last two ticks (what the cars are drawn at).</summary>
    public float Alpha { get; private set; }
    /// <summary>After each tick (a network client's: after a frame's ticks): the combat events and the index of the first of this tick's.</summary>
    public event System.Action<IReadOnlyList<I76.Sim.Weapons.CombatEvent>?, int>? TickRan;

    /// <summary>
    /// The cars set down again against the origin as it now stands. The sim places them first in the frame (before Main), so
    /// when Main moves the origin that frame (Main.Rebase) they stand a whole rebase distance off the world until the next one:
    /// the chase camera follows the car and the ground is 2 km away, which is no ground at all inside the 600 m Draw Distance.
    /// </summary>
    public void PlaceCars()
    {
        var o = Origin();
        foreach (var c in Cars) c.Place(Alpha, o);
    }
    public Func<System.Numerics.Vector3> Origin { get; set; } = () => default;
    private double _acc;
    private bool _started;
    /// <summary>The eye is in the player's car (its arms are live), and which arm the glance raises by crew channel (1 right, 2 left).</summary>
    public Func<bool>? InCockpit { get; set; }
    /// <summary>The camera's object when it is not the player's car (F11's round), for the 600 m voice cull.</summary>
    public Func<Vector3?>? CameraSubject { get; set; }
    public Func<int, bool>? ArmWanted { get; set; }
    /// <summary>The debug fly camera is current: its keys (WASDQE) are not commands.</summary>
    public Func<bool>? FlyCameraActive { get; set; }
    public Assets.AudioBank? Audio { get; set; }
    /// <summary>The mission script, stepped after each sim frame; it drives the cars and holds the camera in cut-scenes.</summary>
    public I76.Sim.Missions.MissionScript? Script => Session.Script;
    /// <summary>Weapons and rounds; run by the script's frame, or by the session when there is no script.</summary>
    public I76.Sim.Weapons.Combat? Combat => Session.Combat;
    private readonly CommandQueue _commands = new();
    /// <summary>The skip key (Esc): the script sees it on the next tick (isKeypress).</summary>
    public bool SkipLatch { set { if (value) _commands.Push(CarCommands.Skip); } }
    private AudioStreamPlayer? _cb;
    private readonly bool _debugPlayer = System.Environment.GetEnvironmentVariable("I76_DEBUG_PLAYER") is not null;
    private int _ticks;
    /// <summary>Sim ticks run since the mission started (the original's frames, at the fixed step).</summary>
    public int Ticks => Remote?.Tick ?? Session.Ticks;
    private readonly bool _debugCam = System.Environment.GetEnvironmentVariable("I76_DEBUG_CAM") is not null;
    private readonly bool _holdBrake = System.Environment.GetEnvironmentVariable("I76_HOLD_BRAKE") is not null;
    /// <summary>I76_HOLD_STEER: <c>left</c> or <c>right</c> held throughout, or <c>left@A-B;right@C-D</c> over those sim ticks, for captures.</summary>
    private readonly (int Dir, int A, int B)[] _holdSteer = ParseHoldSteer(System.Environment.GetEnvironmentVariable("I76_HOLD_STEER"));

    private static (int Dir, int A, int B)[] ParseHoldSteer(string? v)
    {
        if (string.IsNullOrEmpty(v)) return [];
        var list = new List<(int, int, int)>();
        foreach (var part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.Split('@');
            int dir = at[0] == "left" ? 1 : at[0] == "right" ? -1 : 0;
            if (dir == 0) continue;
            if (at.Length > 1 && at[1].Split('-') is [var a, var b] && int.TryParse(a, out int ia) && int.TryParse(b, out int ib)) list.Add((dir, ia, ib));
            else list.Add((dir, int.MinValue, int.MaxValue));
        }
        return [.. list];
    }

    /// <summary>The steer key I76_HOLD_STEER holds now: +1 left, −1 right, 0 none.</summary>
    private int HeldSteer()
    {
        foreach (var (dir, a, b) in _holdSteer)
            if (Ticks >= a && Ticks <= b) return dir;
        return 0;
    }
    private readonly bool _autoTarget = System.Environment.GetEnvironmentVariable("I76_AUTO_TARGET") is not null;
    // I76_HOLD_HARDPOINT=N: keep hardpoint key N (1-5) down, for off-screen captures
    private readonly int _holdHardpoint = int.TryParse(System.Environment.GetEnvironmentVariable("I76_HOLD_HARDPOINT"), out int hk) && hk is >= 1 and <= 5 ? hk : 0;
    private bool _hooked;
    private readonly DriveTest? _test = DriveTest.FromEnvironment();
    private readonly Dictionary<I76.Sim.Vehicles.Vehicle, CarNode> _byVehicle = [];

    /// <summary>
    /// Collision sounds as the original picks them: car against car by
    /// relative speed² (vvch2 above 2025, vvcre2 above 500, else vvcbb3), one
    /// per pair; car against scenery by type (4 vnvco3, 3 vnvcs5) or speed²
    /// (above 500 vnvcs3, else vnvcs1); and every ground impact the bounce
    /// passes (rebound above 4 u/s) vtcoll. All play
    /// on the car that hit -- positional voices at that
    /// car, min distance 10, started at any range (<see cref="Voice3D"/>).
    /// The player's own keep their old voice.
    /// </summary>
    private void PlayImpacts()
    {
        if (Audio is null) return;
        if (_byVehicle.Count != Cars.Count) { _byVehicle.Clear(); foreach (var c in Cars) _byVehicle[c.Sim] = c; }
        void At(I76.Sim.Vehicles.Vehicle v, string name)
        {
            if (!_byVehicle.TryGetValue(v, out var car)) return;
            if (car == Player) car.OneShot(Audio.Get(name));
            else car.Voice(Audio.Get(name));
        }
        foreach (var (v, name, type, n, speed) in World.Step.ObjectHits)
        {
            float s2 = speed * speed;
            At(v, type == 4 ? "vnvco3.wav" : type == 3 ? "vnvcs5.wav" : s2 > 500 ? "vnvcs3.wav" : "vnvcs1.wav");
        }
        _pairs.Clear();
        foreach (var (v, other, rel, n, c) in World.Step.Impacts)
        {
            if (other is not I76.Sim.Vehicles.Vehicle o) { At(v, "vtcoll.wav"); continue; }
            if (!_pairs.Add((v, o)) || !_pairs.Add((o, v))) continue;     // the other car's half of the same contact
            float s2 = rel.LengthSquared();
            At(o == Player?.Sim ? o : v, s2 > 2025 ? "vvch2.wav" : s2 > 500 ? "vvcre2.wav" : "vvcbb3.wav");
        }
    }

    private readonly HashSet<(I76.Sim.Vehicles.Vehicle, I76.Sim.Vehicles.Vehicle)> _pairs = [];

    /// <summary>CB lines and objective notes are the script's sounds; the radio queue times them.</summary>
    private void HookScript()
    {
        _hooked = true;
        // the player's hits for the force feedback, by channel, as the original queued them (a network game's come by its own way)
        if (Remote is null && Combat?.Damage is I76.Sim.Damage.DamageModel dm) dm.Damaging += ForceFeedback.Damaging;
        // DriveTest (I76_TEST_*, I76_TELEMETRY): scripted controls over the sampled ones, and a line a tick after the frame
        if (_test is { } test)
        {
            Session.FrameStarting += () => { if (Player?.Sim is { } p && !Session.Cutscene) test.Controls(p, Session.Ticks); };
            Session.FrameDone += () => { if (Player?.Sim is { } p) test.Log(p, World.Ground, Session.Ticks, Session.TickSeconds); };
        }
        if (Script is null) return;
        var h = Script.Host;
        h.Logged += m => GD.Print($"[script {h.Now:0.00}] {m}");
        _cb = new AudioStreamPlayer { Name = "CbRadio", Bus = "Voice" };
        AddChild(_cb);
        h.Cb.Started += l => { if (Audio?.Get(l.Name) is { } s) { _cb.Stream = s; _cb.Play(); } };
        h.Cb.Cut += _ => _cb.Stop();
        h.Objectives.Sound += n => Player?.OneShot(Audio?.Get(n));
    }

    private CarVoices? _voicesBacking;
    private CarVoices _voices => _voicesBacking ??= new CarVoices(this);

    public override void _EnterTree()
    {
        Voice3D.Followed = () => CameraSubject?.Invoke() ?? (Player is { } p && IsInstanceValid(p) && p.IsInsideTree() ? p.GlobalPosition : null);
        Voice3D.PlayerCar = () => Player is { } p && IsInstanceValid(p) ? p : null;
        Voice3D.InCockpit = () => InCockpit?.Invoke() == true;
        SimClock.Rate = Session.Rate;
        SimClock.Ticks = () => Ticks;
    }

    public override void _ExitTree() { _inputLog?.Dispose(); _inputLog = null; Voice3D.Followed = null; Voice3D.PlayerCar = null; Voice3D.InCockpit = null; if (SimClock.Ticks is not null) SimClock.Ticks = null; }

    public override void _Ready()
    {
        // the pad's force feedback (presentation only; none in a headless run)
        if (DisplayServer.GetName() != "headless") AddChild(new ForceFeedback { Name = "ForceFeedback", Player = () => Player?.Sim });
        // the telemetry feed (I76_TELEMETRY_UDP, [port] telemetry): off unless set
        if (TelemetryOut.Configured() is { } telemetry) AddChild(telemetry);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // the commands of input.map (Controls): each goes to the sim with the next tick
        if (Controls.Suspended && e is not InputEventJoypadButton) return;   // typing a chat line: the pad still plays
        if (e is not (InputEventKey { Pressed: true, Echo: false } or InputEventMouseButton { Pressed: true } or InputEventJoypadButton { Pressed: true }) || Player?.Sim is null) return;
        if (FlyCameraActive?.Invoke() != true) LatchPress(e);
        if (FlyCameraActive?.Invoke() == true) return;   // WASDQE fly the debug camera; they are also the radar camera, targets...
        if (MouseCamera.WheelCycleWeapon && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp })
            _commands.Push(CarCommands.WeaponCyclePrev);
        else if ((MouseCamera.WheelCycleWeapon && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }) || Controls.Pressed(e, "weapon_cycle"))
            _commands.Push(CarCommands.WeaponCycle);   // cammo.wav is the sim's, when the selection changes
        else if (Controls.Pressed(e, "weapon_link")) _commands.Push(CarCommands.WeaponLink);
        else if (Controls.Pressed(e, "toggle_lights")) _commands.Push(CarCommands.Lights);
        else if (Controls.Pressed(e, "POETRY") && Remote is null) _commands.Push(CarCommands.Poem);
        else if (Controls.Pressed(e, "reverse_direction")) _commands.Push(CarCommands.Reverse);
        else if (Controls.Pressed(e, "shift_up")) _commands.Push(CarCommands.ShiftUp);
        else if (Controls.Pressed(e, "shift_down")) _commands.Push(CarCommands.ShiftDown);
        else if (Controls.Pressed(e, "start_engine")) _commands.Push(CarCommands.StartEngine);
        else if (Controls.Pressed(e, "special1")) _commands.Push(CarCommands.Special1);
        else if (Controls.Pressed(e, "special2")) _commands.Push(CarCommands.Special2);
        else if (Controls.Pressed(e, "special3")) _commands.Push(CarCommands.Special3);
        else if (Combat is null) return;
        else if (Controls.Pressed(e, "RADAR_RANGE_TOGGLE")) _commands.Push(CarCommands.RadarRange);
        else if (Controls.Pressed(e, "radar_range_reset")) _commands.Push(CarCommands.RadarRangeReset);
        else if (Controls.Pressed(e, "RADAR_CAMERA_TOGGLE")) _commands.Push(CarCommands.RadarCamera);
        else if (Controls.Pressed(e, "prev_target")) _commands.Push(CarCommands.TargetPrev);
        else if (Controls.Pressed(e, "TARGET_NEAREST_ENEMY")) _commands.Push(CarCommands.TargetNearest);
        else if (Controls.Pressed(e, "NEXT_TARGET")) _commands.Push(CarCommands.TargetNext);
        else if (Controls.Pressed(e, "frontal_target")) _commands.Push(CarCommands.TargetFrontal);
        else if (Controls.Pressed(e, "RESET_TARGET")) _commands.Push(CarCommands.TargetReset);
    }

    /// <summary>A typed cheat that changes the simulation (getdown, the flat tyres), with the next tick.</summary>
    public void Cheat(CarCommands c) => _commands.Push(c);

    /// <summary>Ctrl+Del / Ctrl+Alt+X (SELF_DESTRUCT): the player's car destroys itself.</summary>
    public void SelfDestruct()
    {
        _commands.Push(CarCommands.SelfDestruct);
    }

    /// <summary>RESET_UPRIGHT (U): the car back on the terrain under it, upright and still, with the next tick (in a network game the server's to allow).</summary>
    public void ResetUpright() => _commands.Push(CarCommands.ResetUpright);

    /// <summary>RAISE_DEAD (Space): a dead network player asks the server for a new car on the next tick.</summary>
    public void RaiseDead() => _commands.Push(CarCommands.RaiseDead);

    /// <summary>Held keys pressed since the last tick sampled: at 20 Hz a tap can come and go between two ticks, and still counts for the next one.</summary>
    private CarButtons _latch;

    private void LatchPress(InputEvent e)
    {
        if (Controls.Pressed(e, "throttle_up")) _latch |= CarButtons.ThrottleUp;
        if (Controls.Pressed(e, "throttle_down")) _latch |= CarButtons.ThrottleDown;
        if (Controls.Pressed(e, "steer_left")) _latch |= CarButtons.SteerLeft;
        if (Controls.Pressed(e, "steer_right")) _latch |= CarButtons.SteerRight;
        if (Controls.Pressed(e, "e_brake")) _latch |= CarButtons.EBrake;
        if (Controls.Pressed(e, "weapon_fire")) _latch |= CarButtons.Fire;
        if (Controls.Pressed(e, "HONK_HORN")) _latch |= CarButtons.Horn;
        for (int i = 0; i < 5; i++)
            if (Controls.Pressed(e, HardpointKeys[i])) _latch |= (CarButtons)((uint)CarButtons.Hardpoint1 << i);
    }

    private static readonly string[] HardpointKeys = [.. Enumerable.Range(1, 5).Select(n => $"hardpoint{n}_fire")];

    /// <summary>
    /// The driving keys or the pad as held this instant, for one of Modern Physics' own steps of <paramref name="dt"/> seconds (null
    /// while a test script drives or the script holds the camera). Auto Reverse's swap of the brake and accelerate keys (or its tick
    /// with neither) follows the sim's last decision; Keyboard Steering Assist runs at the step's length.
    /// </summary>
    public CarInput? LiveDriving(float dt)
    {
        if (_test is { Drives: true } || Session.Cutscene || Remote is not null) return null;
        var input = DrivingKeys();
        if (_arNeutral && !input.Has(CarButtons.Analog)) input.Buttons &= ~(CarButtons.ThrottleUp | CarButtons.ThrottleDown);
        else if (_arSwapped && input.Has(CarButtons.Analog)) input.Throttle = (short)System.Math.Clamp(-input.Throttle, -32767, 32767);
        else if (_arSwapped)
        {
            bool up = input.Has(CarButtons.ThrottleUp), down = input.Has(CarButtons.ThrottleDown);
            if (up != down) input.Buttons = (input.Buttons & ~(CarButtons.ThrottleUp | CarButtons.ThrottleDown)) | (up ? CarButtons.ThrottleDown : CarButtons.ThrottleUp);
        }
        // its own ramp, from rest if Modern Physics did not read the keys through the last tick (the tick's ramp is Sample's)
        if (_liveTick < Ticks - 1) _keysLive.Reset();
        _liveTick = Ticks;
        if (!AssistedSteering) return input;
        return _keysLive.Apply(input, dt, Session.Rate.TickSeconds, Player?.Sim is { } car ? KeyStick.MotionOf(car, World.Ground) : default);
    }

    /// <summary>The input as the keys and the pad made it, before Keyboard Steering Assist (for the input log).</summary>
    private CarInput _rawInput;

    /// <summary>I76_INPUT_LOG=file: a line a sim tick of the player's keys, Keyboard Steering Assist's parts and the car (docs/dev-hooks.md); appended, a header at each mission start.</summary>
    private static readonly string? InputLogPath = System.Environment.GetEnvironmentVariable("I76_INPUT_LOG") is { Length: > 0 } ilp ? ilp : null;
    /// <summary>The log stops growing past this many bytes (some 15 hours of play at 20 Hz).</summary>
    private const long InputLogCap = 256L << 20;
    private System.IO.StreamWriter? _inputLog;
    private readonly List<string> _sounds = [];

    private void LogInput(CarInput sent)
    {
        if (Player?.Sim is not { } car) return;
        try
        {
            if (_inputLog is null)
            {
                var dir = System.IO.Path.GetDirectoryName(InputLogPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                _inputLog = new System.IO.StreamWriter(InputLogPath!, append: true) { AutoFlush = false };
                if (_inputLog.BaseStream.Length > InputLogCap) { _inputLog.Dispose(); _inputLog = null; return; }
                _inputLog.WriteLine(FormattableString.Invariant($"# i26-input 1\tmission {Session.Build?.Mission}\tcar {car.Definition}\trate {Session.Rate.Label}\tassisted {(AssistedSteering ? 1 : 0)}\tstarted {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
                _inputLog.WriteLine("tick\ttime\tkeys\tcmd\tmode\tramp\tcounter\tlimit\tsent_steer\tsent_thr\tsteer\tthrottle\tspeed\tslip\tyaw\tx\ty\tz\tfx\tfz\tvx\tvz\tgear\tsurface\tgrip\tload\tair\tslide\tblend\tsound");
            }
            if (_inputLog.BaseStream.Length > InputLogCap) return;
            var raw = _rawInput.Buttons;
            string keys = $"{((raw & CarButtons.SteerLeft) != 0 ? "L" : "")}{((raw & CarButtons.SteerRight) != 0 ? "R" : "")}{((raw & CarButtons.ThrottleUp) != 0 ? "U" : "")}{((raw & CarButtons.ThrottleDown) != 0 ? "D" : "")}{((raw & CarButtons.EBrake) != 0 ? "E" : "")}";
            if (keys.Length == 0) keys = "-";
            var m = KeyStick.MotionOf(car, World.Ground);
            I76.Sim.Sound.TyreSounds.Wanted(car, true, false, _sounds);
            string sound = string.Join(",", _sounds.Where(n => n.StartsWith("tturn", System.StringComparison.OrdinalIgnoreCase) || n.StartsWith("tskid", System.StringComparison.OrdinalIgnoreCase)));
            bool ramped = AssistedSteering;
            var f = car.Flags;
            _inputLog.WriteLine(FormattableString.Invariant(
                $"{Session.Ticks}\t{Session.Ticks * Session.TickSeconds:0.000}\t{keys}\t{(uint)_rawInput.Commands:x}\t{(AssistedSteering ? "Assisted" : "Original")}\t{(ramped ? _keysTick.LastRamp : float.NaN):0.0000}\t{(ramped ? _keysTick.LastCountersteer : float.NaN):0.0000}\t{(ramped ? _keysTick.LastLimit : float.NaN):0.0000}\t{(sent.Has(CarButtons.Analog) ? sent.Steer / 32767f : float.NaN):0.0000}\t{(sent.Has(CarButtons.Analog) ? sent.Throttle / 32767f : float.NaN):0.0000}\t{car.Steer:0.0000}\t{car.Throttle:0.0000}\t{car.Speed:0.000}\t{m.Slip * 57.29578f:0.00}\t{car.AngVel.Y:0.0000}\t{car.Pos.X:0.00}\t{car.Pos.Y:0.00}\t{car.Pos.Z:0.00}\t{car.Rot.Forward.X:0.0000}\t{car.Rot.Forward.Z:0.0000}\t{car.Vel.X:0.000}\t{car.Vel.Z:0.000}\t{car.Gear:0}\t{car.Surface}\t{KeyStick.SurfaceGrip(car, World.Ground):0.00}\t{car.NormalLoad:0.00}\t{((f & I76.Sim.Vehicles.VehicleFlags.Airborne) != 0 ? 1 : 0)}\t{((f & I76.Sim.Vehicles.VehicleFlags.Sliding) != 0 ? 1 : 0)}\t{((f & I76.Sim.Vehicles.VehicleFlags.Blending) != 0 ? 1 : 0)}\t{(sound.Length == 0 ? "-" : sound)}"));
            if (Session.Ticks % 100 == 0) _inputLog.Flush();
        }
        catch (System.Exception e) { GD.PushWarning($"input log: {e.Message}"); _inputLog = null; }
    }

    /// <summary>
    /// Play Options' Keyboard Steering Assist (shell.cfg [port] assisted_steering, applied as the settings load and as the row
    /// changes): on, the steer keys go out as the pad's input (<see cref="KeyStick"/>), so the sim and the wire are the
    /// original's; off, the keys are left be, the original's ramp.
    /// </summary>
    public static bool AssistedSteering = true;
    /// <summary>Assisted's state: one a tick for the input that goes to the sim, one for Modern Physics' own steps.</summary>
    private readonly KeyStick _keysTick = new(), _keysLive = new();
    private int _liveTick = int.MinValue;

    private bool _arSwapped, _arNeutral;

    private CarInput DrivingKeys()
    {
        var b = CarButtons.None;
        if (HoldThrottle || Controls.Down("throttle_up")) b |= CarButtons.ThrottleUp;
        if (Controls.Down("throttle_down") || _holdBrake) b |= CarButtons.ThrottleDown;
        if (Controls.Down("steer_left") || HeldSteer() > 0) b |= CarButtons.SteerLeft;
        if (Controls.Down("steer_right") || HeldSteer() < 0) b |= CarButtons.SteerRight;
        if (Controls.Down("e_brake")) b |= CarButtons.EBrake;
        Controls.PollAxes();
        var input = new CarInput { Buttons = b };
        if (Controls.AxesActive && (b & (CarButtons.ThrottleUp | CarButtons.ThrottleDown | CarButtons.SteerLeft | CarButtons.SteerRight)) == 0)
        { input.Buttons |= CarButtons.Analog; input.Throttle = Controls.AxisThrottle; input.Steer = Controls.AxisSteer; }
        return input;
    }

    /// <summary>The player's input for the tick about to run: the keys held now, the cockpit's arms, and one of each pending command.</summary>
    private CarInput Sample()
    {
        using var _ = Hitch.Time(Hitch.Phase.Input);
        var b = CarButtons.None;
        if (HoldThrottle || Controls.Down("throttle_up")) b |= CarButtons.ThrottleUp;
        if (Controls.Down("throttle_down") || _holdBrake) b |= CarButtons.ThrottleDown;   // I76_HOLD_BRAKE: the brake held, for captures
        if (Controls.Down("steer_left") || HeldSteer() > 0) b |= CarButtons.SteerLeft;
        if (Controls.Down("steer_right") || HeldSteer() < 0) b |= CarButtons.SteerRight;   // I76_HOLD_STEER: for captures
        if (Controls.Down("e_brake")) b |= CarButtons.EBrake;
        if (HoldFire || Controls.Down("weapon_fire")) b |= CarButtons.Fire;
        for (int i = 0; i < 5; i++)
            if (Controls.Down(HardpointKeys[i]) || _holdHardpoint == i + 1) b |= (CarButtons)((uint)CarButtons.Hardpoint1 << i);
        if (Controls.Down("HONK_HORN")) b |= CarButtons.Horn;
        if (InCockpit?.Invoke() == true)
        {
            b |= CarButtons.Cockpit;
            if (ArmWanted?.Invoke(1) == true) b |= CarButtons.RaiseRightArm;
            if (ArmWanted?.Invoke(2) == true) b |= CarButtons.RaiseLeftArm;
        }
        // the stick: the throttle and steer rows are the pad's axes unless a driving key is held,
        // when the keys have both; with no stick bound the keys alone are read
        Controls.PollAxes();
        b |= _latch;   // a key tapped and let go between two ticks still counts for the tick
        _latch = CarButtons.None;
        bool analog = Controls.AxesActive && (b & (CarButtons.ThrottleUp | CarButtons.ThrottleDown | CarButtons.SteerLeft | CarButtons.SteerRight)) == 0;
        var c = _commands.Take();
        // I76_AUTO_TARGET: the nearest enemy whenever there is no target, for captures
        if (_autoTarget && Combat is { } ac && Player?.Sim is { } ap && ac.RadarTarget.GetValueOrDefault(ap) is null) c |= CarCommands.TargetNearest;
        var input = new CarInput { Buttons = b, Commands = c };
        if (analog) { input.Buttons |= CarButtons.Analog; input.Throttle = Controls.AxisThrottle; input.Steer = Controls.AxisSteer; }
        // the port's Auto Reverse (Play Options): the keys rewritten here, so the sim and the wire are the original's
        if (AutoReverse && Player?.Sim is { ObjectClass: 1 } car && (car.Flags & I76.Sim.Vehicles.VehicleFlags.Dead) == 0)
        {
            var raw = input;
            input = (_autoReverse ??= new I76.Sim.Session.AutoReverse(Session.Rate)).Apply(input, car.Gear < 0, System.Numerics.Vector3.Dot(car.Vel, car.Rot.Forward), car.Throttle);
            const CarButtons pedals = CarButtons.ThrottleUp | CarButtons.ThrottleDown;
            _arNeutral = !input.Has(CarButtons.Analog) && (raw.Buttons & pedals) != 0 && (input.Buttons & pedals) == 0;
            _arSwapped = !_arNeutral && (input.Has(CarButtons.Analog) ? raw.Throttle != input.Throttle : (raw.Buttons & pedals) != (input.Buttons & pedals));
        }
        else _arSwapped = _arNeutral = false;
        _rawInput = input;
        // Keyboard Steering Assist: the keys as the pad's input (after Auto Reverse, which reads and swaps the keys)
        // (it reads the own car's motion as this machine has it: the sim's car, or a network game's predicted one)
        if (AssistedSteering)
        {
            input = _keysTick.Apply(input, Session.TickSeconds, null, Player?.Sim is { } own ? KeyStick.MotionOf(own, World.Ground) : default);
        }
        else _keysTick.Reset();
        return input;   // while a chat line is typed Controls reads the pad alone
    }

    /// <summary>Play Options' port-only Auto Reverse (ShellSettings.AutoReverse).</summary>
    public bool AutoReverse;
    private I76.Sim.Session.AutoReverse? _autoReverse;

    /// <summary>Runs tick 0 now (from Main._Ready), before anything is drawn.</summary>
    public void FirstTick()
    {
        if (!_started && Remote is null) _Process(0);
    }

    /// <summary>
    /// A session restored from a checkpoint goes on from where it was: no tick 0, the cars drawn where they are (no slide from the
    /// build's places), the script's sounds hooked, and the CB line that was on the air picked up where it had got to.
    /// </summary>
    public void Resume()
    {
        _started = true;
        _acc = 0;
        if (!_hooked) HookScript();
        foreach (var c in Cars) c.SetPoses(c.Sim.Rot, c.Sim.Pos, c.Sim.Rot, c.Sim.Pos);
        if (Script?.Host.Cb is { Current: { } line } cb && _cb is not null && Audio?.Get(line.Name) is { } stream)
        {
            _cb.Stream = stream;
            _cb.Play((float)System.Math.Max(0, (cb.NowMs - line.StartedMs) / 1000.0));
        }
    }

    /// <summary>A capture hook (I76_CHECKPOINT_HOLD): no ticks while set, so a restored scene holds still to be seen.</summary>
    public bool Hold { get; set; }

    // I76_FPS_LOG=1: every two seconds, the frames drawn, the ticks run and the frame times (to see the limiter leave the sim's rate alone)
    private static readonly bool FpsLog = System.Environment.GetEnvironmentVariable("I76_FPS_LOG") == "1";
    private double _logT, _logMax, _logSum, _logSq; private int _logFrames, _logTicks0 = -1;

    private void LogFrames(double delta)
    {
        if (_logTicks0 < 0) _logTicks0 = Ticks;
        _logT += delta; _logFrames++; _logSum += delta; _logSq += delta * delta; _logMax = System.Math.Max(_logMax, delta);
        if (_logT < 2) return;
        double mean = _logSum / _logFrames, sd = System.Math.Sqrt(System.Math.Max(0, _logSq / _logFrames - mean * mean));
        GD.Print($"fps-log: {_logFrames / _logT:0.0} fps, {(Ticks - _logTicks0) / _logT:0.0} ticks/s (tick {Tick * 1000:0.0} ms, {1 / Tick:0.0} Hz), frame ms mean {mean * 1000:0.0} sd {sd * 1000:0.0} max {_logMax * 1000:0.0}, alpha {Alpha:0.00}");
        _logT = _logSum = _logSq = _logMax = 0; _logFrames = 0; _logTicks0 = Ticks;
    }

    public override void _Process(double delta)
    {
        if (FpsLog) LogFrames(delta);
        if (Remote is { } remote)
        {
            // a co-op mission's script presentation (the radio, the objectives' note) from the host's events, played as a local game's
            if (HookRemoteScript && !_hooked) HookScript();
            // the input goes out on the tick's clock (nudged to keep the server an input in hand); the cars are where the
            // server says, the own car where its prediction has it
            // a co-op movie stands the game still (the server does not tick): no inputs, no tick clock
            if (remote.Frozen) _acc = 0;
            else _acc += System.Math.Min(delta, 0.25) * remote.ClockScale;
            int ticks = 0;
            // from before the inputs go out: the own shots are predicted with them (the server's own shot events are then not drawn)
            int shots = Combat?.Events.Count ?? 0;
            while (_acc >= Tick) { remote.SendInput(InputHeld?.Invoke() == true ? default : Sample()); _acc -= Tick; ticks++; }
            Alpha = remote.Advance(delta, (float)(_acc / Tick));
            // the force feedback a tick at a time; the server's hits do not reach a client, its shots and car state do
            for (int i = 0; i < ticks; i++) ForceFeedback.Tick(i == 0 ? Combat?.Events : null, shots, Tick);
            if (ticks > 0) TickRan?.Invoke(Combat?.Events, shots);
            var ro = Origin();
            foreach (var c in Cars) c.Place(Alpha, ro);
            TyreSounds();
            _voices.Frame();
            return;
        }
        if (!_hooked) HookScript();
        if (Hold) { Alpha = 1; foreach (var c in Cars) c.Place(1, Origin()); return; }
        // the Developer panel's Freeze: no ticks (Step runs them one at a time); its time scale paces the ticks against the wall clock
        if (DevFrozen) { Alpha = 1; foreach (var c in Cars) c.Place(1, Origin()); return; }
        _acc += System.Math.Min(delta, 0.25) * DevTimeScale;
        if (!_started) { _started = true; _acc = System.Math.Max(_acc, Tick); }   // tick 0 runs before the first frame is drawn
        while (_acc >= Tick)
        {
            RunTick();
            _acc -= Tick;
        }
        float alpha = Alpha = (float)(_acc / Tick);
        var o = Origin();
        foreach (var c in Cars) c.Place(alpha, o);
    }

    private static readonly int StateHashUntil = int.TryParse(System.Environment.GetEnvironmentVariable("I76_STATE_HASH"), out int sh) ? sh : 0;

    /// <summary>One tick of the session and what the game does after each: force feedback, impacts, sounds, the debug lines.</summary>
    private void RunTick()
    {
        int shots = Combat?.Events.Count ?? 0;
        var sampled = Sample();
        using (Hitch.Time(Hitch.Phase.Sim)) Session.Tick([sampled]);
        if (InputLogPath is not null) LogInput(sampled);
        // I76_STATE_HASH=N: the session's state hash every 100 ticks up to tick N (two starts compared: the same game?)
        if (StateHashUntil > 0 && Session.Ticks <= StateHashUntil && (Session.Ticks % 100 == 0 || Session.Ticks == StateHashUntil))
            GD.Print($"state hash: tick {Session.Ticks} {Session.StateHash():x16}");
        ForceFeedback.Tick(Combat?.Events, shots, Tick);   // the block the original filled at the frame's end
        TickRan?.Invoke(Combat?.Events, shots);
        if (_debugPlayer)
            foreach (var (_, hn, ht, hnorm, hs) in World.Step.ObjectHits) GD.Print($"  hit {hn} type {ht} n {hnorm.X:0.00},{hnorm.Y:0.00},{hnorm.Z:0.00} at speed {hs:0.0}");
        foreach (var (car, _) in Session.Misfires) if (car == Player?.Sim) Player.OneShotSingle("wmiss.wav", Audio?.Get("wmiss.wav"));
        // I76_DEBUG_CAM: the scripted camera every 10 ticks, to lay over the original game's track camera
        if (_debugCam && Ticks % 10 == 0 && Script?.Host.View is { Active: true } view)
            GD.Print($"camera: tick {Ticks} pos {view.Eye.X:0.00} {view.Eye.Y:0.00} {view.Eye.Z:0.00} op {view.Op}");
        // sound event 4, vland.wav: the player's touchdowns and settles
        if (Player?.Sim is { } lp && World.Step.Landings.Contains(lp)) Player.OneShot(Audio?.Get("vland.wav"));
        if (_debugPlayer && Combat is { } dc && Player?.Sim is { } rp)
            foreach (var r in dc.Rounds.Where(r => r.Shooter == rp && r.Age < 0.04f).Take(2))
                GD.Print($"  round kind {r.Kind} age {r.Age:0.00} fwd {r.Rot.Forward.X:0.00},{r.Rot.Forward.Y:0.00},{r.Rot.Forward.Z:0.00} vel {r.Rot.ToWorld(r.LocalVel).X:0.0},{r.Rot.ToWorld(r.LocalVel).Y:0.0},{r.Rot.ToWorld(r.LocalVel).Z:0.0} car fwd {rp.Rot.Forward.X:0.00},{rp.Rot.Forward.Y:0.00},{rp.Rot.Forward.Z:0.00} up {rp.Rot.Up.X:0.00},{rp.Rot.Up.Y:0.00},{rp.Rot.Up.Z:0.00}");
        if (_debugPlayer && Player?.Sim is { } dp)
            GD.Print($"tick {_ticks++} pos {dp.Pos.X:0.00},{dp.Pos.Y:0.00},{dp.Pos.Z:0.00} fwd {dp.Rot.Forward.X:0.000},{dp.Rot.Forward.Y:0.000},{dp.Rot.Forward.Z:0.000} slope {(World.Ground.BareHeight(dp.Pos.X + dp.Rot.Forward.X * 2, dp.Pos.Z + dp.Rot.Forward.Z * 2) - World.Ground.BareHeight(dp.Pos.X - dp.Rot.Forward.X * 2, dp.Pos.Z - dp.Rot.Forward.Z * 2)) / 4:0.000} up {dp.Rot.Up.X:0.000},{dp.Rot.Up.Z:0.000} v {dp.Speed:0.00} steer {dp.Steer:0.000} thr {dp.Throttle:0.000} w {dp.AngVel.X:0.000},{dp.AngVel.Y:0.000},{dp.AngVel.Z:0.000} flags 0x{(uint)dp.Flags:x} far {World.IsFar(dp)} cut {Session.Cutscene}");
        foreach (var c in Cars) c.Snapshot();
        PlayImpacts();
        TyreSounds();
        _voices.Frame();
    }

    /// <summary>The Developer panel's Freeze (docs/dev-hooks.md): the session runs no ticks until it is cleared or <see cref="DevStep"/> runs one.</summary>
    public bool DevFrozen { get; set; }
    /// <summary>The Developer panel's time scale: ticks per wall-clock second over the rate's. The ticks themselves are the same at any scale.</summary>
    public double DevTimeScale { get; set; } = 1;

    /// <summary>The Developer panel's Step: one tick now (frozen or paused), the cars drawn at its end.</summary>
    public void DevStep()
    {
        if (Remote is not null || !_started) return;
        RunTick();
        _acc = 0;
        Alpha = 1;
        foreach (var c in Cars) c.Place(1, Origin());
    }

    /// <summary>After an outside write to the cars (a teleport, a restore): each drawn where it is now, with no slide from where it was.</summary>
    public void Jumped()
    {
        foreach (var c in Cars) c.SetPoses(c.Sim.Rot, c.Sim.Pos, c.Sim.Rot, c.Sim.Pos);
        foreach (var c in Cars) c.Place(1, Origin());
    }

    private readonly Dictionary<string, Voice3D> _tyre = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tyreWanted = [];

    /// <summary>
    /// The player's tyre sounds (I76.Sim.Sound.TyreSounds), once a tick
    /// as the original's once a frame: each name it asks for is a looping voice on the car, kept
    /// while it is asked for again and stopped by the frame-end pump the first frame it is not.
    /// </summary>
    private void TyreSounds()
    {
        var v = Player?.Sim;
        if (v is not null)
        {
            bool worn = v.Health?.Brake is { } b && v.P.BrakeStrength < b.Nominal * 0.5f;
            I76.Sim.Sound.TyreSounds.Wanted(v, true, worn, _tyreWanted);
        }
        else _tyreWanted.Clear();
        if (_debugPlayer && !_tyreWanted.SequenceEqual(_tyre.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            GD.Print($"tyre sounds: tick {Ticks} speed {v?.Speed:0.0} flags 0x{(uint)(v?.Flags ?? 0):x} surface {v?.Surface} -> {string.Join(" ", _tyreWanted)}");
        foreach (var (name, p) in _tyre.ToList())
        {
            if (IsInstanceValid(p) && p.GetParent() == Player && _tyreWanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsInstanceValid(p)) p.QueueFree();
            _tyre.Remove(name);
        }
        foreach (var name in _tyreWanted)
        {
            if (_tyre.ContainsKey(name) || Audio?.Get(name, loop: true) is not { } s) continue;
            var p = new Voice3D { Stream = s, Autoplay = true, BaseDb = I76.Sim.Sound.TyreSounds.VolumeDb(name) };
            Player!.AddChild(p);
            _tyre[name] = p;
        }
    }
}
