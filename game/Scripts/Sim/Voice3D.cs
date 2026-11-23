using Godot;
using I76.Sim.Sound;

namespace I76.Game.Sim;

/// <summary>
/// A positional voice heard the way the original's DirectSound3D buffers
/// were (<see cref="Ds3d"/>): full volume within the min distance of the
/// listener, min/d beyond, held at min/400 past 400 m. Godot's own models
/// cannot hold at a max distance (its max distance silences, and fades
/// linearly on the way), so the volume is worked out here each frame from
/// the distance to the listener and Godot only pans; its distance low-pass,
/// which DirectSound did not have, is off. The Doppler shift is worked out here
/// too (<see cref="Ds3d.DopplerPitch"/>), from the sim's velocities rather than
/// Godot's tracking of the nodes' drawn motion: a voice on a car moves with the
/// car's velocity (a network client's other cars: the snapshots'), any other
/// stands still, and the listener moves with the camera (<see cref="ListenerMotion"/>).
/// </summary>
public partial class Voice3D : AudioStreamPlayer3D
{
    /// <summary>The DirectSound min distance: <see cref="Ds3d.MinDistance"/>, or <see cref="Ds3d.LoudMinDistance"/> for the 0x200 voices.</summary>
    public float MinDistance { get; set; } = Ds3d.MinDistance;

    /// <summary>The voice's own volume, before the distance.</summary>
    public float BaseDb { get; set; }

    /// <summary>The voice's own pitch (an engine loop's, from its rpm), before the Doppler shift: what <see cref="AudioStreamPlayer3D.PitchScale"/> is set to, times the shift.</summary>
    public float BasePitch { get; set; } = 1f;

    public Voice3D()
    {
        Bus = "SFX";
        AttenuationModel = AttenuationModelEnum.Disabled;
        MaxDistance = 0;
        MaxDb = 0;
        AttenuationFilterCutoffHz = 20500;
        AttenuationFilterDb = 0;
        DopplerTracking = DopplerTrackingEnum.Disabled;
    }

    /// <summary>A one-shot: plays once, then frees itself.</summary>
    public static Voice3D OneShot(AudioStream s, float minDistance = Ds3d.MinDistance, float db = 0)
    {
        var p = new Voice3D { Stream = s, MinDistance = minDistance, BaseDb = db };
        p.Finished += p.QueueFree;
        return p;
    }

    /// <summary>The voices of each sound name now playing, with their priority: the admission's count.</summary>
    private static readonly Dictionary<string, List<Voice3D>> Active = new(StringComparer.OrdinalIgnoreCase);
    private string? _limitName;
    private int _priority = 50;

    /// <summary>
    /// A one-shot that obeys its sound's voice limits: at most <paramref name="max"/> voices of one name play at once (the GAS0
    /// header's field, 1 for a plain .wav). Over that, the new voice stops a playing one of strictly lower priority, else it is
    /// dropped. Four linked cluster bombs' bomblets sound as the original's one or two voices, not fifty.
    /// </summary>
    public static Voice3D OneShot(AudioStream s, string name, (int Priority, int MaxVoices) limits, float minDistance = Ds3d.MinDistance, float db = 0)
    {
        var p = OneShot(s, minDistance, db);
        p._limitName = name;
        p._priority = limits.Priority;
        p._max = limits.MaxVoices;
        return p;
    }

    private int _max = int.MaxValue;

    private bool Admit()
    {
        if (_limitName is null) return true;
        if (!Active.TryGetValue(_limitName, out var list)) Active[_limitName] = list = [];
        list.RemoveAll(v => !IsInstanceValid(v) || v.IsQueuedForDeletion() || !v.Playing);
        if (list.Count >= _max)
        {
            var weakest = list.OrderBy(v => v._priority).First();
            if (weakest._priority >= _priority) return false;
            weakest.Stop(); weakest.QueueFree(); list.Remove(weakest);
        }
        list.Add(this);
        return true;
    }

    /// <summary>Where the camera's object is (Godot's frame), set by the SimDriver: distances are measured from it (the player's car, or F11's round), not from the listener.</summary>
    public static Func<Vector3?>? Followed { get; set; }

    /// <summary>Whether the cockpit view is up (the original's hud flag), set by the SimDriver.</summary>
    public static Func<bool>? InCockpit { get; set; }

    /// <summary>The player's car node, set by the SimDriver: its voices are placed at the listener in the cockpit (<see cref="Ds3d.AtListener"/>).</summary>
    public static Func<Node?>? PlayerCar { get; set; }

    /// <summary>The floating origin (the game-frame point at Godot's 0), set by the SimDriver: the listener's motion is followed in the game's frame, so a rebase is not a step.</summary>
    public static Func<System.Numerics.Vector3>? Origin { get; set; }

    private static readonly ListenerMotion Listener = new();

    /// <summary>The camera jumped (a teleport, a restore, a new session): the listener stands still for a frame rather than flying the distance.</summary>
    public static void ListenerCut() => Listener.Cut();

    /// <summary>The listener's velocity (game frame), as <see cref="TrackListener"/> last found it.</summary>
    public static System.Numerics.Vector3 ListenerVelocity => Listener.Velocity;

    /// <summary>What the voices are heard from: the current AudioListener3D, else the camera.</summary>
    private static Node3D? Ear(Viewport? vp) => vp?.GetAudioListener3D() is { } l && l.IsCurrent() ? l : vp?.GetCamera3D();

    /// <summary>
    /// The listener's motion over the frame now ending (<see cref="ListenerTracker"/>, after every other node's _Process, so the
    /// camera has been placed): its position in the game's frame, so a rebase of the floating origin is not a step.
    /// </summary>
    public static void TrackListener(Viewport? vp, double delta)
    {
        if (Ear(vp) is not { } ear || !ear.IsInsideTree()) { Listener.Cut(); return; }
        var at = Coords.ToGame(ear.GlobalPosition) + (Origin?.Invoke() ?? default);
        var v = Listener.Update(at, ear, (float)delta);
        if (LogEnabled && Godot.Engine.GetProcessFrames() % 30 == 0) Trace($"listener v {v.X:0.0} {v.Y:0.0} {v.Z:0.0} ({v.Length():0.0} m/s) {ear.Name}");
    }

    /// <summary>The voice was parked 600 m or more from the followed car (a looping one waits to resume; a one-shot is dropped).</summary>
    private bool _parked;

    /// <summary>I76_AUDIO_LOG=1: every start and stop of the voices this port's sound rules make is printed (audio cannot be heard in a capture).</summary>
    public static readonly bool LogEnabled = System.Environment.GetEnvironmentVariable("I76_AUDIO_LOG") == "1";

    /// <summary>The voice in the log: its owner's node name with its own (a car's "t11ug01/Engine").</summary>
    public string Who => GetParent() is { } p ? $"{p.Name}/{Name}" : Name;

    public static void Trace(string what) { if (LogEnabled) GD.Print($"[audio] {what}"); }

    /// <summary>
    /// Stops the voice for good until its owner starts it again: also a loop the 600 m cull parked, which would otherwise start
    /// again by itself when the object came back inside 600 m (an idle gun's loop must not).
    /// </summary>
    public void Release()
    {
        _parked = false;
        if (Playing) Stop();
    }

    /// <summary>For tests and logs: a start or stop the cull made.</summary>
    public static Action<string>? CullLog { get; set; }

    /// <summary>
    /// The admission check before a voice starts: one whose object is 600 m or more from the followed
    /// car is not started (state 3, parked at once), so a far one-shot is never heard at all -- not even
    /// the first frame before <see cref="Cull"/> would stop it (measured in the original game: the Nitro Pack loads P19's vexplode.wav for the helicopter
    /// destroyed 1.8 km away and never plays it).
    /// </summary>
    public override void _EnterTree()
    {
        Cull();
        Attenuate();
    }

    /// <summary>Plays the voice unless the admission refused it (<see cref="_EnterTree"/>): callers start one-shots with this.</summary>
    public void Start()
    {
        if (_refused) { Trace($"not started: {Who}, out of range"); return; }
        if (!Admit()) { Trace($"not started: {_limitName}, {_max} already playing"); _refused = true; QueueFree(); return; }
        Play();
    }

    /// <summary>A one-shot the admission refused: it is freed without a sound.</summary>
    private bool _refused;

    public override void _Process(double delta)
    {
        Cull();
        Attenuate();
    }

    /// <summary>
    /// Every frame from the voice pump: a voice whose object is 600 m or more from the followed car is stopped. A
    /// looping one (modes 1 and 2) is parked and starts again from its start when the object is back inside 600 m;
    /// a one-shot (mode 0) is dropped for good.
    /// </summary>
    private void Cull()
    {
        if (Followed?.Invoke() is not { } at || !IsInsideTree()) return;
        bool far = !Ds3d.LoopStarts(GlobalPosition.DistanceTo(at));
        bool loops = Assets.AudioBank.Loops(Stream);
        if (far && !_parked)
        {
            if (!loops) { _refused = true; CullLog?.Invoke("drop"); Trace($"cull: drop {Who} at {GlobalPosition.DistanceTo(at):0} m"); Stop(); QueueFree(); return; }
            // only a sounding voice is parked: a loop its owner stopped (an idle gun) must not come back by itself
            if (!Playing) return;
            _parked = true;
            Stop(); CullLog?.Invoke("park"); Trace($"cull: park {Who} at {GlobalPosition.DistanceTo(at):0} m");
        }
        else if (!far && _parked)
        {
            _parked = false;
            Play();
            CullLog?.Invoke("resume");
            Trace($"cull: resume {Who}");
        }
    }

    private void Attenuate()
    {
        var ear = Ear(GetViewport());
        // The player's own car's voices in the cockpit sit at the listener: centred, at full volume
        bool atEar = IsInsideTree() && GetParent() is { } car && PlayerCar?.Invoke() == car && Ds3d.AtListener(true, InCockpit?.Invoke() == true);
        PanningStrength = atEar ? 0 : 1;
        float db = BaseDb + (ear is null || atEar || !IsInsideTree() ? 0 : Ds3d.AttenuationDb(GlobalPosition.DistanceTo(ear.GlobalPosition), MinDistance));
        // a car at a NaN position (a respawn or a lost snapshot) would set a NaN volume: the engine logs an error for every voice, every frame
        if (!float.IsNaN(db)) VolumeDb = db;
        float pitch = BasePitch * (ear is null || atEar || !IsInsideTree() ? 1f : Doppler(ear));
        if (pitch > 0f && float.IsFinite(pitch) && MathF.Abs(pitch - PitchScale) > 1e-4f) PitchScale = pitch;
    }

    /// <summary>The Doppler shift for this frame: the voice's car's sim velocity (none for a voice not on a car), the listener's from the camera's motion.</summary>
    private float Doppler(Node3D ear)
    {
        var lv = ListenerVelocity;
        var sv = GetParent() is CarNode { Sim: { } car } ? car.Vel : default;
        float k = Ds3d.DopplerPitch(Coords.ToGame(GlobalPosition), sv, Coords.ToGame(ear.GlobalPosition), lv);
        if (LogEnabled && Godot.Engine.GetProcessFrames() % 30 == 0 && MathF.Abs(k - 1f) > 0.005f) Trace($"doppler {Who} x{k:0.000} src v {sv.Length():0.0} m/s at {GlobalPosition.DistanceTo(ear.GlobalPosition):0} m");
        return k;
    }
}
