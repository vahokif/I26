namespace I76.Sim.Views;

/// <summary>
/// The software renderer's end-of-frame double image: the finished frame blended 50/50 with itself moved up and left by (x, y) pixels. Two
/// modes share it. The wiggleburger cheat eases x and y towards a new rand() and 15 every frame; the
/// crash jolt (started from the player's impact handler) holds 19 px and eases it to
/// 0 at 2*dt, x and half of it in y, and when it is under 1 px switches the whole effect off, cheat included.
/// </summary>
public sealed class ScreenWobble
{
    /// <summary>The impact channel amount a player hit must exceed (channel 3 greater than 20).</summary>
    public const int ImpactThreshold = 20;
    /// <summary>As the original sets them.</summary>
    public const float JoltTarget = 20f, JoltStart = 19f;

    private readonly Func<int> _rand;
    private float _x, _y, _target, _cur;

    public ScreenWobble(Func<int>? rand = null) { _rand = rand ?? (() => System.Random.Shared.Next()); }

    /// <summary>The effect is on.</summary>
    public bool On { get; private set; }
    /// <summary>The jolt is running.</summary>
    public bool Jolting { get; private set; }

    /// <summary>Wiggleburger on or off.</summary>
    public void Toggle() => On = !On;

    /// <summary>
    /// Whether a damage packet to the player starts the jolt: a hit on the impact channel (mask bit 8, the collision
    /// path) of more than 20, with the cockpit view up (which the original asks).
    /// </summary>
    public static bool IsJoltHit(bool isPlayer, int mask, int impact, bool cockpit) =>
        isPlayer && (mask & 8) != 0 && impact > ImpactThreshold && cockpit;

    /// <summary>The jolt starts (and with it the effect, cheat or not).</summary>
    public void StartJolt()
    {
        _target = JoltTarget;
        _cur = JoltStart;
        Jolting = true;
        On = true;
    }

    /// <summary>
    /// One frame of real time dt: the offset to blend by, in pixels of the 640x480 frame, truncated to
    /// whole pixels as the original's ftol does when whole is set.
    /// </summary>
    public (float X, float Y) Step(float dt, bool whole = true)
    {
        if (!On) return (0, 0);
        float sx, sy;
        if (Jolting)
        {
            if (_cur >= _target - 1f) _target = 0f;
            _cur = Ease(_cur, _target, 2f * dt);
            if (_cur < 1f && _target == 0f) { Jolting = false; On = false; }
            sx = _cur;
            sy = _cur * 0.5f;
        }
        else
        {
            _x = Ease(_x, _rand() & 15, dt);
            _y = Ease(_y, _rand() & 15, dt);
            sx = _x;
            sy = _y;
        }
        return whole ? ((int)sx, (int)sy) : (sx, sy);
    }

    /// <summary>The original's easing: by k when k is within [0, 0.95] and the gap at least 0.005, else straight there.</summary>
    public static float Ease(float from, float to, float k) =>
        k >= 0f && k <= 0.95f && MathF.Abs(to - from) >= 0.005f ? (to - from) * k + from : to;
}

/// <summary>The Crash Blur setting (shell.cfg [port] crash_blur): on or off. On is the original's double image in the 1997
/// look (both renderers: whole pixels, point-sampled) and a smooth, filtered one in the modern look.</summary>
public static class CrashBlurMode
{
    public const int Off = 0, On = 1;
    public static readonly string[] Names = ["Off", "On"];

    /// <summary>Parse the cfg word: off is off; on, the old "auto" and anything unknown are on.</summary>
    public static int Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" => Off,
        _ => On,
    };

    public static string Word(int mode) => Names[System.Math.Clamp(mode, 0, 1)].ToLowerInvariant();

    public static bool Resolve(int mode) => mode == On;
}
