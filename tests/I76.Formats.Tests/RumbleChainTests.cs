using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The force model's rumble through the Rumble setting's mix: Original at 100 % sends the pad exactly what the chain before
/// the setting did (the engine, the throttle, the driver), Rumble Strength scales every level, and Enhanced only adds.
/// </summary>
public class RumbleChainTests
{
    private sealed class Sink(Func<double> clock) : IRumbleSink
    {
        public readonly List<(double T, float W, float S)> Calls = [];
        public void Rumble(float weak, float strong) => Calls.Add((clock(), weak, strong));
        public void Stop() => Calls.Add((clock(), -1, -1));
    }

    private static FrcLibrary Library() => FrcLibrary.Load(n => n switch
    {
        "EXPLOSN" => ForceFeedbackTests.Periodic("Explosion", FrcEffect.Sine, 945, 12),
        "ENGSTART" => ForceFeedbackTests.Periodic("Start", FrcEffect.Sine, 400, 8, mag: 60),
        _ => null,
    });

    /// <summary>
    /// Seven seconds of play at 60 Hz: the starter, then idling, a jump and its landing (an impact hit), a round (ballistic),
    /// a blast (explosive), a burst of machine-gun fire, and a drive on a dirt road.
    /// </summary>
    private static IEnumerable<(ForceFrame F, RumbleEvents E)> Play()
    {
        var f = new ForceFrame();
        var e = new RumbleEvents();
        for (int t = 0; t < 420; t++)
        {
            f.Clear(); e.Clear();
            f.Cranking = t is >= 5 and < 30;
            f.EngineOn = t >= 30;
            f.Rpm = f.EngineOn ? 900 : 0;
            if (t is >= 100 and < 130) { f.Airborne = true; f.Surface = 9; f.Mph = 30; }
            if (t == 130) f.Hits.Add((3, 20));                      // the landing
            if (t == 170) f.Hits.Add((0, 10));                      // a round
            if (t == 220) f.Hits.Add((1, 30));                      // a blast
            if (t is >= 280 and < 310) f.Shots.Add(new WeaponShot(0, 1, 1, 8));   // a machine gun at 8 shots/s
            if (t >= 330) { f.Mph = 40; f.Surface = 4; f.Rpm = 3000; }
            e.EngineOn = f.EngineOn; e.Cranking = f.Cranking; e.Rpm = f.Rpm;
            yield return (f, e);
        }
    }

    private static List<(double T, float W, float S)> Run(Func<IRumbleSink, IRumbleSink> front, bool throttled, Action<EnhancedRumble, RumbleEvents, double>? enhanced = null)
    {
        double now = 0;
        var sink = new Sink(() => now);
        IRumbleSink pad = throttled ? new ThrottledRumble(sink, () => now) : sink;
        var mixed = front(pad);
        var jitter = new Random(5);
        var model = new ForceModel(Library(), new RumbleEngine(mixed), () => jitter.Next(32768));
        var enh = new EnhancedRumble();
        const double dt = 1.0 / 60;
        foreach (var (f, e) in Play())
        {
            (pad as ThrottledRumble)?.Pump();
            model.Frame(f, dt);
            enhanced?.Invoke(enh, e, dt);
            now += dt;
        }
        return sink.Calls;
    }

    [Fact]
    public void Original_at_full_strength_sends_what_the_chain_before_the_setting_sent()
    {
        var before = Run(pad => pad, throttled: true);                                  // RumbleEngine → ThrottledRumble → driver
        var original = Run(pad => new RumbleMix(pad) { Strength = RumbleStrength.Factor(100) }, throttled: true);
        Assert.Equal(before, original);
        // and it is play's rumble, not silence: the idling engine is 20 % on the small motor, the landing, the round and the blast
        // are on the big one
        Assert.Contains(before, c => c.T is > 1.0 and < 1.6 && c.W == 0.1875f && c.S == 0);
        bool Felt(double tick) => before.Any(c => c.T >= tick / 60 - 1e-6 && c.T < tick / 60 + ThrottledRumble.MinGap + 0.02 && c.S > 0.3f);
        Assert.True(Felt(130), "the landing");
        Assert.True(Felt(170), "the round");
        Assert.True(Felt(220), "the blast");
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(125)]
    [InlineData(150)]
    public void Strength_scales_every_level_the_effects_send(int percent)
    {
        var raw = Run(pad => pad, throttled: false);
        float k = RumbleStrength.Factor(percent);
        var scaled = Run(pad => new RumbleMix(pad) { Strength = k }, throttled: false);
        // every level times the factor, held to the motors' top; one the factor takes under their threshold is a stop, and a
        // stop after a stop is not sent again
        var expected = new List<(double T, float W, float S)>();
        foreach (var (t, w, s) in raw)
        {
            var c = w < 0 || MathF.Max(w, s) * k <= 0.001f ? (t, -1f, -1f) : (t, MathF.Min(1, w * k), MathF.Min(1, s * k));
            if (c.Item2 < 0 && (expected.Count == 0 || expected[^1].W < 0)) continue;
            expected.Add(c);
        }
        Assert.Equal(expected.Count, scaled.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].T, scaled[i].T);
            Assert.Equal(expected[i].W, scaled[i].W, 5);
            Assert.Equal(expected[i].S, scaled[i].S, 5);
        }
    }

    [Fact]
    public void Strength_reaches_the_pad_through_the_rate_limit()
    {
        // the idling engine's 20 % on the small motor, in sixteenths: 50 % → 0.1 → 2/16, 150 % → 0.3 → 5/16
        Assert.Contains(Run(pad => new RumbleMix(pad) { Strength = 0.5f }, true), c => c.W == 2f / 16 && c.S == 0);
        Assert.Contains(Run(pad => new RumbleMix(pad) { Strength = 1.5f }, true), c => c.W == 5f / 16 && c.S == 0);
    }

    [Fact]
    public void Enhanced_adds_its_pulses_to_the_original_effects()
    {
        RumbleMix? mix = null;
        var original = Run(pad => mix = new RumbleMix(pad), throttled: false);
        var enhanced = Run(pad => mix = new RumbleMix(pad), throttled: false, (enh, e, dt) => { var (w, s) = enh.Step(e, dt); mix!.SetExtra(w, s); });
        // the starter's thump and the drive's growl are there; nothing the original sends is ever quieter
        Assert.True(enhanced.Count >= original.Count);
        bool Window((double T, float W, float S) c) => c.T >= 5.0 / 60 && c.T < 5.0 / 60 + EnhancedRumble.Crank.Seconds / 2;
        Assert.True(enhanced.Where(Window).Max(c => c.S) > original.Where(Window).DefaultIfEmpty().Max(c => c.S), "the starter's thump");
        double Level(List<(double T, float W, float S)> calls, double at, bool strong) =>
            calls.LastOrDefault(c => c.T <= at + 1e-9) is var c && c.W >= 0 ? (strong ? c.S : c.W) : 0;
        for (double at = 0; at < 7; at += 0.05)
        {
            Assert.True(Level(enhanced, at, false) >= Level(original, at, false) - 1e-5, $"weak at {at}");
            Assert.True(Level(enhanced, at, true) >= Level(original, at, true) - 1e-5, $"strong at {at}");
        }
    }
}
