namespace I76.Formats.Input;

/// <summary>Where the rumble goes: the pads' two motors. The game's is Godot's joypad vibration; tests have their own.</summary>
public interface IRumbleSink
{
    /// <summary>Both motors at 0..1 for the next moments (called every frame while anything plays).</summary>
    void Rumble(float weak, float strong);
    void Stop();
}

/// <summary>
/// Puts a rate limit between the engine (which says its levels every sim tick) and a pad driver (SDL / evdev / DirectInput), where every
/// call is an effect upload that can block for milliseconds: the levels are rounded to <see cref="Steps"/> and passed on only when they
/// change (and not oftener than <see cref="MinGap"/> seconds), or as a renewal before the driver's own timeout. Stop goes once.
/// </summary>
public sealed class ThrottledRumble(IRumbleSink inner, Func<double> clock) : IRumbleSink
{
    public const int Steps = 16;
    public const double MinGap = 0.1, Renew = 0.5;
    private float _weak, _strong;
    private double _at = double.NegativeInfinity;
    private bool _on;
    /// <summary>The calls passed on to the driver (tests).</summary>
    public int Calls { get; private set; }

    public void Rumble(float weak, float strong)
    {
        float w = MathF.Round(Math.Clamp(weak, 0f, 1f) * Steps) / Steps, s = MathF.Round(Math.Clamp(strong, 0f, 1f) * Steps) / Steps;
        if (w == 0 && s == 0 && weak + strong > 0) w = 1f / Steps;     // faint stays faint, not off
        double now = clock();
        _stopPending = false;
        bool changed = !_on || w != _weak || s != _strong;
        if (changed ? now - _at < MinGap : now - _at < Renew) return;
        _on = true; _weak = w; _strong = s; _at = now;
        Calls++;
        inner.Rumble(w, s);
    }

    private bool _stopPending;

    public void Stop()
    {
        if (!_on) return;
        _stopPending = true;
        Pump();
    }

    /// <summary>Lets a stop held back by the rate limit go once the gap has passed (call it every frame).</summary>
    public void Pump()
    {
        if (!_stopPending) return;
        if (clock() - _at < MinGap) return;
        _stopPending = false; _on = false; _at = double.NegativeInfinity;
        Calls++;
        inner.Stop();
    }
}

/// <summary>
/// One force\*.FRC file playing: what the original's DLL got back from the SideWinder driver for a file effect
/// (the force-feedback DLL). <see cref="Gain"/> is the DLL's DIEP_GAIN over the file's own levels;
/// <see cref="Loop"/> is its DIEP_DURATION = INFINITE (the weapon files), which the port reads as the file over again.
/// </summary>
public sealed class FrcVoice
{
    internal FrcVoice(IReadOnlyList<FrcEffect> effects, double gain, bool loop)
    {
        Effects = effects;
        Gain = gain;
        Loop = loop;
        Length = effects.Count == 0 ? 0 : effects.Max(e => e.DurationMs);
    }

    public IReadOnlyList<FrcEffect> Effects { get; }
    public double Gain { get; set; }
    public bool Loop { get; }
    /// <summary>The longest effect of the file, ms.</summary>
    public double Length { get; }
    /// <summary>Where in the file it is, ms.</summary>
    public double Ms { get; internal set; }
    private bool _stopped;

    public bool Playing => !_stopped && Length > 0 && (Loop || Ms < Length);

    /// <summary>IDirectInputEffect::Start on an effect that is already playing starts it over.</summary>
    public void Restart() { Ms = 0; _stopped = false; }
    public void Stop() => _stopped = true;

    internal (double Weak, double Strong) Now()
    {
        double weak = 0, strong = 0, t = Loop && Length > 0 ? Ms % Length : Ms;
        foreach (var e in Effects)
        {
            double l = e.Level(t) * Gain;
            var (w, s) = RumbleEngine.Motors(e.Hz);
            weak += l * w;
            strong += l * s;
        }
        return (weak, strong);
    }
}

/// <summary>
/// The pad's two motors: the force files playing (<see cref="FrcVoice"/>) plus the levels of the effects the DLL made
/// itself (<see cref="Output"/>). Slow shakes and the user-defined kicks of a file drive the big motor, fast ones the
/// small one. Everything adds up to the motors' limit.
/// </summary>
public sealed class RumbleEngine(IRumbleSink sink)
{
    private readonly List<FrcVoice> _voices = [];
    private bool _on;

    /// <summary>What the strong motor does for an effect that shakes at <paramref name="hz"/> (and the weak one): slow shakes are the big motor's.</summary>
    public static (double Weak, double Strong) Motors(double hz) => hz <= 20 ? (0.35, 1.0) : (1.0, 0.35);

    public bool Playing => _voices.Any(v => v.Playing);

    /// <summary>Starts a file at <paramref name="gain"/> 0..1 of its strength; null if there is no such file (the driver's create failed).</summary>
    public FrcVoice? Play(IReadOnlyList<FrcEffect> effects, double gain = 1, bool loop = false)
    {
        if (effects.Count == 0) return null;
        var v = new FrcVoice(effects, gain, loop);
        _voices.Add(v);
        return v;
    }

    /// <summary>One step: the files' levels now plus <paramref name="weak"/> / <paramref name="strong"/>, out to the motors; then the files move on <paramref name="seconds"/>.</summary>
    public void Output(double seconds, double weak = 0, double strong = 0)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            var v = _voices[i];
            if (!v.Playing) { _voices.RemoveAt(i); continue; }
            var (w, s) = v.Now();
            weak += w;
            strong += s;
            v.Ms += seconds * 1000;
        }
        if (weak > 0.001 || strong > 0.001) { _on = true; sink.Rumble((float)Math.Min(weak, 1), (float)Math.Min(strong, 1)); }
        else if (_on) { _on = false; sink.Stop(); }
    }

    /// <summary>Everything off at once (the escape menu, the map, the notepad, a video).</summary>
    public void StopAll()
    {
        foreach (var v in _voices) v.Stop();
        _voices.Clear();
        if (_on) { _on = false; sink.Stop(); }
    }
}

/// <summary>The effect files by name ("CANNON1"), read from the install's force directory.</summary>
public sealed class FrcLibrary
{
    private readonly Dictionary<string, List<FrcEffect>> _by = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The twelve files I7_SFRCE.DLL names. TIREBLWL and TIREBLWR ship in the force directory too, but nothing loads them:
    /// the DLL makes its flat-tyre shake itself.
    /// </summary>
    public static readonly string[] Names =
        ["CANNON1", "CANNON2", "CANNON3", "CANNON4", "ENGSTART", "EXPLOSN", "MISSIL45", "MISSILE2", "MISSILE5", "WPNCYCLE", "WPNLINK", "WPNUNLNK"];

    /// <summary>Loads every known file through <paramref name="read"/> (name without extension → file bytes, or null).</summary>
    public static FrcLibrary Load(Func<string, byte[]?> read)
    {
        var lib = new FrcLibrary();
        foreach (var n in Names)
            if (read(n) is { } bytes && FrcFile.Parse(bytes) is { Count: > 0 } fx) lib._by[n] = fx;
        return lib;
    }

    public IReadOnlyList<FrcEffect> Get(string name) => _by.TryGetValue(name, out var l) ? l : [];
    public int Count => _by.Count;
}

/// <summary>A hardpoint that fired this frame: its index in the sorted hardpoint list (0..5) and the weapon's class, tier and rate (shots/s).</summary>
public readonly record struct WeaponShot(int Hardpoint, uint Class, uint Tier, float Rate);

/// <summary>
/// One frame as the game hands it to I7_SFRCE.DLL: the 0x16c-byte block that it fills for the
/// player's car. Only what the DLL acts on and a pad can show is here.
/// </summary>
public sealed class ForceFrame
{
    /// <summary>+0x10: veh+0x454 bit 1, the engine running.</summary>
    public bool EngineOn;
    /// <summary>+0x14: veh+0x454 bit 8, the starter cranking (set until the ignition sound's time runs out).</summary>
    public bool Cranking;
    /// <summary>+0x0c: the engine's rpm; the DLL's vibration rate follows it, which a pad's level does not show.</summary>
    public float Rpm;
    /// <summary>+0x104: the speedometer (mph), held at 165.</summary>
    public float Mph;
    /// <summary>+0x148: veh+0x45c + 1 while the speedometer reads above 0, else 0; 9 is in the air.</summary>
    public int Surface;
    /// <summary>+0x128: veh+0x454 bit 4.</summary>
    public bool Airborne;
    /// <summary>+0x108..+0x114: the tyres blown front right, front left, rear right, rear left.</summary>
    public bool FlatFrontRight, FlatFrontLeft, FlatRearRight, FlatRearLeft;
    /// <summary>+0x24 / +0x28 / +0x2c: a weapon cycle, link or unlink happened this frame.</summary>
    public bool Cycle, Link, Unlink;
    /// <summary>+0x30 + 0x1c·i: the hardpoints that fired this frame.</summary>
    public readonly List<WeaponShot> Shots = [];
    /// <summary>The four impact queues +0x150..+0x15c: a damage packet's channel (0 ballistic, 1 explosive, 2 fire, 3 impact) and amount.</summary>
    public readonly List<(int Channel, int Amount)> Hits = [];

    public void Clear()
    {
        EngineOn = Cranking = Airborne = Cycle = Link = Unlink = false;
        FlatFrontRight = FlatFrontLeft = FlatRearRight = FlatRearLeft = false;
        Rpm = Mph = 0;
        Surface = 0;
        Shots.Clear();
        Hits.Clear();
    }
}

/// <summary>
/// I7FF_SIM_Effect (I7_SFRCE.DLL) on a pad: what the original's force-feedback DLL does with each frame, with
/// its tables and constants as the force-feedback DLL reads them, mapped onto two rumble motors.
/// Per frame, in the DLL's order: the hits (an explosive one plays EXPLOSN by its damage, ballistic and impact ones are
/// a jolt that dies away), the weapons (an effect per hardpoint, on only in frames with a shot), the engine start, the
/// engine's vibration, the flat-tyre shake and the road. What a stick does and a pad cannot (the centring spring,
/// friction, the car's acceleration pushing the stick, which way anything pushes) is left out (deviations.md, Force Feedback).
/// </summary>
public sealed class ForceModel
{
    /// <summary>
    /// The effect id per weapon, a row of seven per class, by tier (a turret's tier less 100). The shipped exe
    /// reads it at 7·class + tier, one row early, so its machine guns send the .45's id, its cannons nothing and its
    /// flame weapons the missiles' files (measured with a stand-in DLL). The port reads the row each class was meant to:
    /// the .45 a rattle, machine guns rattles, cannons CANNON1..4 at 70..100%, mortars a thump, rockets and missiles the
    /// MISSILE files, flame weapons a jittered triangle, droppers their own (deviations.md, Force Feedback).
    /// </summary>
    private static readonly int[] FireIds =
    [
        0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0,
        8, 8, 8, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7,
        17, 18, 19, 20, 20, 0, 0,
        9, 10, 11, 12, 11, 7, 0,
        13, 14, 15, 16, 0, 0, 0,
        21, 0, 22, 23, 25, 22, 24,
    ];

    /// <summary>The effect id a shot of this weapon sends (0: none).</summary>
    public static int WeaponId(uint cls, uint tier)
    {
        // the intended row (the exe's 7·cls is one early); a tier past the row (the tank cannon's 15) takes its heaviest
        long t = tier >= 100 ? tier - 100 : tier;
        long i = 7L * (cls + 1) + Math.Min(t, 6);
        return i >= 0 && i < FireIds.Length ? FireIds[i] : 0;
    }

    private enum Fx { None, Rattle, CannonFile, MissileFile, Triangle, Thump }

    /// <summary>Per id, the effect and its gain (percent).</summary>
    private static (Fx Kind, int Gain) Effect(int id) => id switch
    {
        1 or 3 => (Fx.Rattle, 50),
        2 => (Fx.Rattle, 60),
        8 => (Fx.Rattle, 70),
        4 => (Fx.CannonFile, 70), 5 => (Fx.CannonFile, 80), 6 => (Fx.CannonFile, 90), 7 => (Fx.CannonFile, 100),
        >= 9 and <= 12 => (Fx.MissileFile, 70),
        >= 13 and <= 16 => (Fx.Triangle, 70),
        >= 17 and <= 20 => (Fx.Thump, 100),
        21 or 22 => (Fx.Triangle, 65),
        >= 23 and <= 25 => (Fx.CannonFile, 65),
        _ => (Fx.None, 0),
    };

    /// <summary>The road's strength (percent) by +0x148: packed dirt, light vegetation, paved, dirt road, wash, rocky, parking lot, dirt intersection.</summary>
    private static readonly int[] RoadGain = [0, 25, 35, 15, 20, 25, 40, 15, 30, 0];

    /// <summary>The thump (ids 17..20): 0.2 s of a 47 Hz triangle at full strength, 2.1 ms in from nothing and 6.3 ms out.</summary>
    public const double ThumpSeconds = 0.2;

    private sealed class Hardpoint
    {
        public bool Active, Made;
        public Fx Kind;
        public int Gain, Rate;
        public FrcVoice? Voice;
        public double T;
    }

    /// <summary>A ballistic or impact hit: a push on the stick that dies away toward −1510 and ends near there.</summary>
    private sealed class Jolt
    {
        public int Mag;
        public required int Rate;
        public required bool Ordnance;
        public bool Over => Ordnance ? Mag <= -1500 : Mag < -1500;
    }

    private readonly FrcLibrary _lib;
    private readonly RumbleEngine _out;
    private readonly Func<int> _rand;
    private readonly Hardpoint[] _hp = [.. Enumerable.Range(0, 6).Select(_ => new Hardpoint())];
    private readonly List<FrcVoice> _concussions = [];   // newest first, as the exe links them
    private readonly List<Jolt> _jolts = [];
    private FrcVoice? _start, _cycle, _link, _unlink;
    private double _tyreT;
    private bool _tyre;
    /// <summary>Hits being felt. While any is, the weapons are halved and the engine and road silent.</summary>
    private int _hits;
    /// <summary>Hardpoint effects playing. While any is, the engine and road are halved.</summary>
    private int _firing;

    /// <param name="rand">The C library's rand() (0..32767), for the road's and the droppers' jitter.</param>
    public ForceModel(FrcLibrary library, RumbleEngine output, Func<int>? rand = null)
    {
        _lib = library;
        _out = output;
        var r = new Random();
        _rand = rand ?? (() => r.Next(32768));
    }

    /// <summary>What the model itself added to the motors last frame (the files not counted), for tests.</summary>
    public (double Weak, double Strong) Synth { get; private set; }

    /// <summary>One frame: the block in, the motors out. <paramref name="dt"/> is the frame time (+0x160).</summary>
    public void Frame(ForceFrame f, double dt)
    {
        double weak = 0, strong = 0;

        // ---- the hits: the concussion queue, then impact, then ordnance; fire (channel 2) has no effect
        foreach (var (ch, amount) in f.Hits)
            switch (ch)
            {
                case 1:
                    if (Play("EXPLOSN", Clamp((int)((35f + 0.6666667f * amount) * 100f)) / 10000.0) is { } v) { _concussions.Insert(0, v); _hits++; }
                    break;
                case 3: _jolts.Add(new Jolt { Mag = Clamp((int)((55f + 0.5625f * amount) * 100f)), Rate = 3, Ordnance = false }); _hits++; break;
                case 0: _jolts.Add(new Jolt { Mag = Clamp((int)((40f + 0.6666667f * amount) * 100f)), Rate = 5, Ordnance = true }); _hits++; break;
            }
        for (int i = 0, n = 0; i < _concussions.Count; i++)
        {
            // over when the file is, or when three newer ones are going
            if (!_concussions[i].Playing || ++n > 3) { _concussions[i].Stop(); _concussions.RemoveAt(i--); _hits--; }
        }
        for (int i = 0; i < _jolts.Count; i++)
        {
            var j = _jolts[i];
            // this frame's push; once it has swung past nothing it is a steady pull the other way, which
            // a rumble motor has no way to show
            strong += Math.Max(j.Mag, 0) / 10000.0;
            int was = j.Mag;
            j.Mag = dt is < 0 or > 0.95 || Math.Abs(-1510f - j.Mag) < 0.005f ? 0 : (int)(j.Mag - (float)dt * j.Rate * (j.Mag + 1510f) + 0.5f);
            // the truncation stops an impact's at about −1497 at this frame rate, short of its −1500, and the DLL would
            // feel it (and keep the engine and road silent) for good; the port ends it there
            if (j.Over || (j.Mag == was && j.Mag < 0)) { _jolts.RemoveAt(i--); _hits--; }
        }

        // ---- the weapon triggers and the hardpoints
        if (f.Cycle) _cycle = Tick(_cycle, "WPNCYCLE");
        if (f.Link) _link = Tick(_link, "WPNLINK");
        if (f.Unlink) _unlink = Tick(_unlink, "WPNUNLNK");
        for (int i = 0; i < _hp.Length; i++)
        {
            var h = _hp[i];
            WeaponShot? shot = null;
            foreach (var s in f.Shots) if (Math.Clamp(s.Hardpoint, 0, 5) == i) { shot = s; break; }
            int id = shot is { } sh ? WeaponId(sh.Class, sh.Tier) : 0;
            if (id == 0)
            {
                if (h.Active) { h.Active = false; h.Voice?.Stop(); if (_firing > 0) _firing--; }
                continue;
            }
            int rate = (int)(shot!.Value.Rate + 0.5f);   // the exe's ftol(rate + 0.5)
            if (rate == 0) rate = 1;
            var (kind, gain) = Effect(id);
            if (!h.Made)
            {
                // made once per hardpoint, for the first id it fires (the DLL keeps the effect object); a file that
                // is not there (CANNON5, say) fails and is tried again at the next shot
                h.Kind = kind;
                h.Rate = rate;
                h.Made = kind switch
                {
                    Fx.CannonFile => _lib.Get($"CANNON{rate}").Count > 0,
                    Fx.MissileFile => _lib.Get(MissileFile(id)).Count > 0,
                    Fx.None => false,
                    _ => true,
                };
                if (!h.Made) continue;
            }
            if (id is >= 13 and <= 16) gain += (int)(_rand() * (30.0 / 32767) - 15);
            if (_hits != 0) gain >>= 1;
            h.Gain = gain;
            if (!h.Active)
            {
                _firing++;
                h.T = 0;
                h.Voice?.Stop();
                h.Voice = h.Kind switch
                {
                    Fx.CannonFile => _out.Play(_lib.Get($"CANNON{h.Rate}"), gain / 100.0, loop: true),
                    Fx.MissileFile => _out.Play(_lib.Get(MissileFile(id)), gain / 100.0, loop: true),
                    _ => null,
                };
            }
            else if (h.Voice is { } hv) hv.Gain = gain / 100.0;
            h.Active = true;
        }
        foreach (var h in _hp)
        {
            if (!h.Active) continue;
            double g = h.Gain / 100.0, l = h.Kind switch
            {
                // a square at the rate, half strength, falling from full over its first period
                Fx.Rattle => h.T < 1.0 / h.Rate ? 1 - 0.5 * h.T * h.Rate : 0.5,
                // a triangle at the rate, half strength about a quarter's offset: its peak
                Fx.Triangle => 0.75,
                // full for its 0.2 s: the 2.1 ms in and 6.3 ms out are far inside a frame
                Fx.Thump => h.T < ThumpSeconds ? 1 : 0,
                _ => 0,
            };
            var (mw, ms) = RumbleEngine.Motors(h.Kind == Fx.Thump ? 47 : h.Rate);
            weak += l * g * mw;
            strong += l * g * ms;
            h.T += dt;
        }

        // ---- the engine: its start, then its vibration
        if (_start is { Playing: false }) _start = null;
        if (f.Cranking) _start = _start is null ? Play("ENGSTART", 1) : Restarted(_start);
        if (f.EngineOn)
        {
            uint half = (uint)(int)f.Mph >> 1;
            weak += Busy(20 - (int)Math.Min(half, 20u)) / 100.0;
        }

        // ---- the flat tyre: a 4 Hz side-to-side shake by speed, none in the air
        bool front = f.FlatFrontRight || f.FlatFrontLeft, flat = front || f.FlatRearRight || f.FlatRearLeft;
        if (flat && !f.Airborne)
        {
            if (!_tyre) { _tyre = true; _tyreT = 0; }
            double g = Math.Min(Math.Abs((front ? 6000.0 : 3000.0) * 0.02 * f.Mph), 10000) / 10000;
            strong += g * Math.Abs(Math.Cos(2 * Math.PI * 4 * _tyreT));
            _tyreT += dt;
        }
        else _tyre = false;

        // ---- the road: a triangle by surface, faster with speed, nothing when stopped or in the air
        int hz = f.Mph <= 1 ? 0 : 2 - (int)(f.Mph / 140f * -20f);
        if (f.Surface is <= 0 or >= 9) hz = 0;
        if (hz != 0)
        {
            int b = RoadGain[f.Surface];
            weak += Busy(b - (int)(_rand() / 32768.0 * (b >> 1))) / 100.0;
        }

        Synth = (weak, strong);
        _out.Output(dt, weak, strong);
    }

    /// <summary>Everything off and forgotten: the escape menu, the map, the notepad or a video (+0x08 cleared: DISFFC_STOPALL).</summary>
    public void StopAll()
    {
        _out.StopAll();
        foreach (var h in _hp) { h.Active = h.Made = false; h.Voice = null; }
        _concussions.Clear();
        _jolts.Clear();
        _start = _cycle = _link = _unlink = null;
        _tyre = false;
        _hits = _firing = 0;
        Synth = (0, 0);
    }

    private static string MissileFile(int id) => id switch { 9 => "MISSIL45", 10 => "MISSILE5", _ => "MISSILE2" };

    private static int Clamp(int m) => Math.Clamp(m, -10000, 10000);

    /// <summary>The engine's and road's strength while hits are felt (none) or weapons play (half; 10 for anything above 21).</summary>
    private int Busy(int gain) => _hits != 0 ? 0 : _firing == 0 ? gain : (gain & ~1) > 20 ? 10 : gain >> 1;

    private FrcVoice? Play(string name, double gain) => _out.Play(_lib.Get(name), gain);

    /// <summary>The weapon cycle, link and unlink ticks: at 40%, from the start again if one is still going.</summary>
    private FrcVoice? Tick(FrcVoice? v, string name) => v is { Playing: true } ? Restarted(v) : Play(name, 0.4);

    private static FrcVoice Restarted(FrcVoice v) { v.Restart(); return v; }
}
