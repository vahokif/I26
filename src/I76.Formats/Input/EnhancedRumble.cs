namespace I76.Formats.Input;

/// <summary>Play Options' Rumble row: the game's own force-feedback effects as they are (Original), those and short pulses for the events they do not cover (Enhanced), or no rumble at all.</summary>
public enum RumbleMode { Original, Enhanced, Off }

/// <summary>Rumble Strength: a percentage on every motor level the game sends, 25 to 150 (100: as the effects say; the pad's top is 1).</summary>
public static class RumbleStrength
{
    public const int Min = 25, Max = 150, Default = 100;
    /// <summary>What a click on the row steps through.</summary>
    public static readonly int[] Steps = [25, 50, 75, 100, 125, 150];

    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);
    public static int Next(int percent) => Steps.FirstOrDefault(s => s > percent) is var n && n > 0 ? n : Steps[0];
    public static float Factor(int percent) => Clamp(percent) / 100f;
}

/// <summary>What <see cref="EnhancedRumble"/> reads each tick from the player's car.</summary>
public sealed class RumbleEvents
{
    public bool EngineOn, Cranking, Nitro, Handbrake;
    public int Gear;
    public float Rpm;
    public readonly List<WeaponShot> Shots = [];

    public void Clear()
    {
        EngineOn = Cranking = Nitro = Handbrake = false;
        Gear = 0;
        Rpm = 0;
        Shots.Clear();
    }
}

/// <summary>
/// The Enhanced rumble: short pulses for what the original's force-feedback effects do not cover, on the two motors (the big one
/// at slow beats, the small one at buzz): the nitrous kick, the handbrake thud, a gear change, the engine cranking, a shot by
/// the kind of weapon, and a light engine growl by rpm. Each is a level held a moment, then fading; the motors take the loudest.
/// Presentation only: it reads the car and writes nothing.
/// </summary>
public sealed class EnhancedRumble
{
    /// <summary>A level on each motor held for <see cref="Seconds"/>, fading over the last half.</summary>
    public readonly record struct Pulse(float Weak, float Strong, float Seconds);

    public static readonly Pulse NitroKick = new(0.4f, 1.0f, 0.35f);
    public static readonly Pulse HandbrakeThud = new(0.3f, 0.6f, 0.08f);
    public static readonly Pulse GearClick = new(0.5f, 0f, 0.05f);
    public static readonly Pulse Crank = new(0.2f, 0.6f, 0.15f);

    /// <summary>The pulse of a shot by weapon class (the rows of the force model's table: 1 machine guns, 2 cannons, 3 mortars, 4 rockets and missiles, 5 flame weapons, 6 droppers); none for any other.</summary>
    public static Pulse? ShotPulse(uint weaponClass) => weaponClass switch
    {
        1 => new Pulse(0.35f, 0f, 0.06f),
        2 => new Pulse(0.2f, 0.7f, 0.12f),
        3 => new Pulse(0.1f, 0.8f, 0.15f),
        4 => new Pulse(0.3f, 0.5f, 0.18f),
        5 => new Pulse(0.4f, 0f, 0.10f),
        6 => new Pulse(0.2f, 0.35f, 0.08f),
        _ => null,
    };

    /// <summary>The engine growl on the small motor: nothing at idle and up to 0.2 at 4500 rpm.</summary>
    public static float Growl(float rpm)
    {
        float t = Math.Clamp((rpm - 1200f) / 3300f, 0f, 1f);
        return 0.2f * t * t;
    }

    private readonly List<(Pulse P, float Left)> _live = [];
    private bool _nitro, _hand, _cranking, _seen;
    private int _gear;

    /// <summary>The motors' levels after the last <see cref="Step"/>.</summary>
    public (float Weak, float Strong) Level { get; private set; }

    /// <summary>One sim tick of <paramref name="dt"/> seconds with the car as <paramref name="e"/> has it.</summary>
    public (float Weak, float Strong) Step(RumbleEvents e, double dt)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var (p, left) = _live[i];
            left -= (float)dt;
            if (left <= 0) _live.RemoveAt(i); else _live[i] = (p, left);
        }
        if (_seen)
        {
            if (e.Nitro && !_nitro) Add(NitroKick);
            if (e.Handbrake && !_hand) Add(HandbrakeThud);
            if (e.Cranking && !_cranking) Add(Crank);
            if (e.Gear != _gear && e.EngineOn) Add(GearClick);
        }
        _seen = true;
        _nitro = e.Nitro; _hand = e.Handbrake; _cranking = e.Cranking; _gear = e.Gear;
        foreach (var s in e.Shots)
            if (ShotPulse(s.Class) is { } sp) Add(sp);

        float weak = e.EngineOn ? Growl(e.Rpm) : 0f, strong = 0f;
        foreach (var (p, left) in _live)
        {
            float k = left >= p.Seconds / 2 ? 1f : left * 2 / p.Seconds;
            weak = Math.Max(weak, p.Weak * k);
            strong = Math.Max(strong, p.Strong * k);
        }
        return Level = (weak, strong);
    }

    private void Add(Pulse p) => _live.Add((p, p.Seconds));

    /// <summary>Everything forgotten (a pause, the setting changed).</summary>
    public void Reset()
    {
        _live.Clear();
        _seen = false;
        Level = (0, 0);
    }
}

/// <summary>
/// The sink in front of the pad: the force model's levels (its <see cref="RumbleEngine"/> speaks to this as to any sink) plus
/// the Enhanced pulses, each motor the sum held to 1, all times the strength factor. The rate limit (<see cref="ThrottledRumble"/>)
/// and the pad's driver are behind it.
/// </summary>
public sealed class RumbleMix(IRumbleSink inner) : IRumbleSink
{
    private float _bw, _bs, _ew, _es;
    private bool _on;

    /// <summary>The factor on every level (<see cref="RumbleStrength.Factor"/>).</summary>
    public float Strength { get; set; } = 1f;

    public void Rumble(float weak, float strong) { _bw = weak; _bs = strong; Send(); }

    public void Stop() { _bw = _bs = 0; Send(); }

    /// <summary>The Enhanced pulses' levels; called every tick, which renews a level held longer than the driver's timeout.</summary>
    public void SetExtra(float weak, float strong)
    {
        if (weak <= 0 && strong <= 0 && _ew <= 0 && _es <= 0) return;
        _ew = weak; _es = strong;
        Send();
    }

    private void Send()
    {
        float w = Math.Min(1f, (_bw + _ew) * Strength), s = Math.Min(1f, (_bs + _es) * Strength);
        if (w <= 0.001f && s <= 0.001f)
        {
            if (_on) { _on = false; inner.Stop(); }
            return;
        }
        _on = true;
        inner.Rumble(w, s);
    }
}
