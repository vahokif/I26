using Godot;
using I76.Formats.Input;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game;

/// <summary>
/// Godot's joypad vibration as the rumble sink: both motors on every connected pad. Godot applies a vibration later, in its
/// own event processing on the main thread; on Linux that is a force-feedback upload to the device, which some pads
/// (Bluetooth clones) answer only after seconds, freezing the game. So a frame that stalls right after a vibration was
/// sent switches rumble off for the rest of the session (<see cref="Tripped"/>), with a line in the log.
/// </summary>
public sealed class GodotRumble : IRumbleSink
{
    /// <summary>A frame this long (ms) just after a vibration counts as the pad stalling the game.</summary>
    public const double StallMs = 1500;
    /// <summary>Rumble has stalled the game this session: nothing more is sent.</summary>
    public static bool Tripped { get; private set; }
    /// <summary>When the last vibration went to the engine (Stopwatch ticks; 0 none yet).</summary>
    public static long LastCall { get; private set; }

    public void Rumble(float weak, float strong)
    {
        if (Tripped) return;
        using var _ = Hitch.Time(Hitch.Phase.Rumble);
        foreach (var d in Input.GetConnectedJoypads().Where(d => Input.IsJoyKnown(d)))
            Input.StartJoyVibration(d, weak, strong, 0.75f);     // ThrottledRumble renews it before it runs out
        LastCall = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Stop()
    {
        if (Tripped) return;
        using var _ = Hitch.Time(Hitch.Phase.Rumble);
        foreach (var d in Input.GetConnectedJoypads()) Input.StopJoyVibration(d);
        LastCall = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Every pad's motors off at once, past the rate limit (leaving the game, switching the setting off): a stop held back there would never go out.</summary>
    public static void StopNow()
    {
        if (Tripped) return;
        foreach (var d in Input.GetConnectedJoypads()) Input.StopJoyVibration(d);
    }

    /// <summary>HitchWatch's report of a frame that ran <paramref name="ms"/> and began at <paramref name="since"/>: a stall with a vibration sent since then trips the guard.</summary>
    public static void FrameEnded(double ms, long since)
    {
        if (Tripped || ms < StallMs || LastCall == 0 || LastCall < since) return;
        Tripped = true;
        GD.Print($"force feedback: the game stalled {ms:0} ms after a rumble was sent to the pad; rumble is off for this session (some Bluetooth pads block on it; Play Options > Force Feedback)");
    }
}

/// <summary>
/// The player's force feedback: each sim tick, the block the original handed
/// I7_SFRCE.DLL is filled from the player's car and the tick's shots and hits, and
/// <see cref="ForceModel"/> does what the DLL did with it, on the pad's motors. Presentation only: nothing here writes
/// the sim's state, and it is not in a headless run. The Play Options switch is <see cref="Enabled"/>; with no pad nothing
/// plays. Everything stops under anything that pauses the tree (the escape menu, the map, the notepad, a video), as the
/// original stopped its effects.
/// </summary>
public partial class ForceFeedback : Node
{
    /// <summary>Play Options' Force Feedback (ShellSettings.ForceFeedback); both looks.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) _instance?.Silence();     // switched off in the escape menu: quiet at once, not at the next frame
        }
    }
    private static bool _enabled = true;
    /// <summary>True while something is over the game that does not pause it (a network game's escape menu, the map and notepad, the game over): nothing plays.</summary>
    public static Func<bool>? Held { get; set; }
    private static ForceFeedback? _instance;
    /// <summary>The player's car (hits, shots and blasts of anyone else are not felt).</summary>
    public Func<Vehicle?>? Player { get; set; }

    private RumbleEngine _engine = null!;
    private ThrottledRumble _throttle = null!;
    private ForceModel _model = null!;
    private readonly ForceFrame _frame = new();
    private readonly List<(int Channel, int Amount)> _hits = [];
    private Armory? _arm;
    private int _selected = -1, _group;

    private static bool Live => _instance is { _model: not null } i && Enabled && !i.GetTree().Paused && Held?.Invoke() != true && Controls.HasPad;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;     // it is the one that notices the pause
        _instance = this;
        _engine = new RumbleEngine(_throttle = new ThrottledRumble(new GodotRumble(), () => Time.GetTicksMsec() / 1000.0));
        _model = new ForceModel(FrcLibrary.Load(Read), _engine);
    }

    public override void _ExitTree()
    {
        Silence();
        if (_instance == this) _instance = null;
    }

    /// <summary>Everything the model had playing forgotten and every pad's motors off.</summary>
    private void Silence()
    {
        _model?.StopAll();
        _hits.Clear();
        GodotRumble.StopNow();
    }

    private static byte[]? Read(string name)
    {
        if (Controls.InstallDir is not { } dir) return null;
        var force = System.IO.Directory.Exists(dir) ? System.IO.Directory.EnumerateDirectories(dir).FirstOrDefault(p => System.IO.Path.GetFileName(p).Equals("force", StringComparison.OrdinalIgnoreCase)) : null;
        var f = force is null ? null : System.IO.Directory.EnumerateFiles(force).FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".frc", StringComparison.OrdinalIgnoreCase));
        return f is null ? null : System.IO.File.ReadAllBytes(f);
    }

    public override void _Process(double delta)
    {
        _throttle.Pump();
        // the pause (+0x08 cleared): DISFFC_STOPALL, and the DLL forgets what was playing
        if (!Enabled || GetTree().Paused || Held?.Invoke() == true || !Controls.HasPad) { _model.StopAll(); _hits.Clear(); }
    }

    /// <summary>
    /// A damage packet reaching a car's slot (DamageModel.Damaging): the original queues the player's, one entry per
    /// channel that carries anything, for the DLL's next frame.
    /// </summary>
    public static void Damaging(Vehicle v, DamagePacket p)
    {
        if (!Live || v != _instance!.Player?.Invoke()) return;
        for (int ch = 0; ch < 4; ch++)
            if (p.Ch[ch] != 0) _instance._hits.Add((ch, p.Ch[ch]));
    }

    /// <summary>
    /// One sim tick has run: the block for it from the player's car, the shots among
    /// <paramref name="events"/> from <paramref name="from"/> on, and the hits queued since the last tick.
    /// </summary>
    public static void Tick(IReadOnlyList<CombatEvent>? events, int from, double dt)
    {
        if (!Live) return;
        using var _ = Hitch.Time(Hitch.Phase.Force);
        var me = _instance!;
        var f = me._frame;
        f.Clear();
        f.Hits.AddRange(me._hits);
        me._hits.Clear();
        if (me.Player?.Invoke() is { } v)
        {
            var flags = v.Flags;
            f.EngineOn = (flags & VehicleFlags.EngineOn) != 0;
            f.Cranking = (flags & VehicleFlags.Started) != 0;
            f.Airborne = (flags & VehicleFlags.Airborne) != 0;
            f.Rpm = v.Engine.Rpm;
            float mph = v.Engine.Speedo;
            f.Mph = MathF.Min(mph, 165f);
            f.Surface = mph > 0 ? v.Surface + 1 : 0;
            f.FlatFrontRight = v.FrontRight.Blown;
            f.FlatFrontLeft = v.FrontLeft.Blown;
            f.FlatRearRight = v.RearRight.Blown;
            f.FlatRearLeft = v.RearLeft.Blown;
            if (v.Armory is { } arm)
            {
                // the cycle, link and unlink flags are set where the selection or the group actually changed
                if (arm == me._arm)
                {
                    f.Cycle = arm.Selected != me._selected && arm.Selected >= 0;
                    f.Link = arm.Group.Count > me._group;
                    f.Unlink = arm.Group.Count == 0 && me._group > 0;
                }
                me._arm = arm;
                me._selected = arm.Selected;
                me._group = arm.Group.Count;
                if (events is not null)
                    for (int i = Math.Max(from, 0); i < events.Count; i++)
                        if (events[i] is ShotFired s && s.Shooter == v && arm.Weapons.IndexOf(s.Weapon) is var hp and >= 0)
                            f.Shots.Add(new WeaponShot(Math.Min(hp, 5), s.Weapon.Def.Class, s.Weapon.Def.Tier, s.Weapon.Def.Rate));
            }
        }
        me._model.Frame(f, dt);
    }
}
