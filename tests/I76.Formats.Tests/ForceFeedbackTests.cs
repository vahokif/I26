using System.Text;
using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The force\*.FRC reader and the rumble the pad's events make of them.</summary>
public class ForceFeedbackTests
{
    private sealed class Sink : IRumbleSink
    {
        public readonly List<(float Weak, float Strong)> Frames = [];
        public int Stops;
        public void Rumble(float weak, float strong) => Frames.Add((weak, strong));
        public void Stop() => Stops++;
    }

    /// <summary>A force file of one periodic effect, built as the shipped ones are laid out.</summary>
    private static byte[] Periodic(string name, int waveform, int ms, int cycles, int high = 100, int low = 0, int max = 100, int min = -100, int mag = 100)
    {
        var rec = new List<byte>();
        void I(int v) => rec.AddRange(BitConverter.GetBytes(v));
        I(104);
        var n = new byte[64];
        Encoding.ASCII.GetBytes(name).CopyTo(n, 0);
        rec.AddRange(n);
        foreach (var v in new[] { 2, waveform, 3, 0, 0, ms, 100, 100, 0, 32, 0, 0, high, low, 0, 0, mag, 20, cycles, 100, max, min }) I(v);
        return Riff(rec.ToArray());
    }

    private static byte[] Custom(string name, int ms, params int[] samples)
    {
        var rec = new List<byte>();
        void I(int v) => rec.AddRange(BitConverter.GetBytes(v));
        I(104);
        var n = new byte[64];
        Encoding.ASCII.GetBytes(name).CopyTo(n, 0);
        rec.AddRange(n);
        foreach (var v in new[] { 3, 201, 3, 180, 0, ms, 100, 100, 0, 32, 0, 0, 100, 0, 0, 0, 100, 12, samples.Length, 7824832 }) I(v);
        foreach (var s in samples) I(s);
        return Riff(rec.ToArray());
    }

    private static byte[] Riff(byte[] effectData)
    {
        byte[] Chunk(string id, byte[] body) => [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(body.Length), .. body, .. (body.Length % 2 == 1 ? new byte[1] : [])];
        byte[] List(string type, byte[] body) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. body]);
        var efct = List("efct", [.. Chunk("id  ", new byte[4]), .. Chunk("data", effectData)]);
        var trak = List("trak", efct);
        var all = List("FORC", [.. Chunk("INAM", new byte[2]), .. Chunk("trgt", new byte[16]), .. trak]);
        // a RIFF file: "RIFF", size, then the FORC list's type and children
        return [.. Encoding.ASCII.GetBytes("RIFF"), .. BitConverter.GetBytes(all.Length - 8 + 4), .. Encoding.ASCII.GetBytes("FORC"), .. all[12..]];
    }

    [Fact]
    public void AnEffectIsReadFromItsRecord()
    {
        var e = Assert.Single(FrcFile.Parse(Periodic("SawtoothDown1", FrcEffect.SawtoothDown, 945, 12)));
        Assert.Equal("SawtoothDown1", e.Name);
        Assert.Equal(FrcEffect.SawtoothDown, e.Waveform);
        Assert.Equal(945, e.DurationMs);
        Assert.Equal(12, e.Cycles);
        Assert.Equal(100, e.MaxLevel);
        Assert.Equal(-100, e.MinLevel);
        Assert.Equal(12 * 1000.0 / 945, e.Hz, 6);
    }

    [Fact]
    public void ACustomEffectCarriesItsCurve()
    {
        var e = Assert.Single(FrcFile.Parse(Custom("UserDefined1", 500, 100, -93, -50, 0)));
        Assert.True(e.Custom);
        Assert.Equal([100, -93, -50, 0], e.Samples);
        Assert.Equal(1.0, e.Level(10), 6);                 // the kick
        Assert.Equal(0.93, e.Level(130), 6);               // by size, not by direction
        Assert.Equal(0.0, e.Level(499), 6);
        Assert.Equal(0.0, e.Level(500), 6);                // over
    }

    [Fact]
    public void NonsenseIsNoEffects()
    {
        Assert.Empty(FrcFile.Parse([]));
        Assert.Empty(FrcFile.Parse(Encoding.ASCII.GetBytes("RIFF....WAVEfmt ")));
        var cut = Periodic("Sine1", FrcEffect.Sine, 100, 1);
        Assert.Empty(FrcFile.Parse(cut.AsSpan(0, cut.Length - 40)));
    }

    [Fact]
    public void TheWaveformsGiveTheirStrengthOverTheDuration()
    {
        var saw = FrcFile.Parse(Periodic("s", FrcEffect.SawtoothDown, 1000, 1))[0];
        Assert.Equal(1.0, saw.Level(0), 3);                // from full ...
        Assert.Equal(0.0, saw.Level(500), 3);              // ... through the middle ...
        Assert.Equal(1.0, saw.Level(999), 1);              // ... to full the other way
        var sq = FrcFile.Parse(Periodic("q", FrcEffect.SquareHigh, 1000, 1, high: 60, low: 40, max: 100, min: 0))[0];
        Assert.Equal(1.0, sq.Level(300), 3);
        Assert.Equal(0.0, sq.Level(700), 3);
        var half = FrcFile.Parse(Periodic("c", FrcEffect.Constant, 1000, 1, max: 100, min: 100, mag: 50))[0];
        Assert.Equal(0.5, half.Level(10), 3);
        // too quick to follow: the cycle's average
        var fast = FrcFile.Parse(Periodic("f", FrcEffect.SquareHigh, 100, 35, high: 50, low: 50, max: 100, min: 0))[0];
        Assert.Equal(fast.Level(1), fast.Level(2), 6);
        Assert.InRange(fast.Level(1), 0.4, 0.6);
    }

    [Fact]
    public void SlowEffectsDriveTheBigMotorAndFastOnesTheSmall()
    {
        Assert.True(RumbleEngine.Motors(5).Strong > RumbleEngine.Motors(5).Weak);
        Assert.True(RumbleEngine.Motors(100).Weak > RumbleEngine.Motors(100).Strong);
    }

    private static (ForceModel Model, RumbleEngine Engine, Sink Sink) Rig(int rand = 0)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["CANNON1"] = Custom("a", 1000, 100, 100), ["CANNON2"] = Custom("b", 500, 50, 50), ["CANNON3"] = Custom("c", 300, 40, 40), ["CANNON4"] = Custom("d", 200, 30, 30),
            ["MISSIL45"] = Periodic("e", FrcEffect.Constant, 200, 1, max: 50, min: 50), ["MISSILE2"] = Periodic("f", FrcEffect.Constant, 250, 1, max: 60, min: 60),
            ["MISSILE5"] = Periodic("g", FrcEffect.Constant, 260, 1, max: 70, min: 70),
            ["EXPLOSN"] = Periodic("h", FrcEffect.Constant, 900, 1, max: 100, min: 100), ["ENGSTART"] = Periodic("i", FrcEffect.Constant, 80, 1, max: 100, min: 100),
            ["WPNCYCLE"] = Periodic("l", FrcEffect.Constant, 70, 1, max: 100, min: 100), ["WPNLINK"] = Periodic("m", FrcEffect.Constant, 100, 1, max: 100, min: 100),
            ["WPNUNLNK"] = Periodic("n", FrcEffect.Constant, 115, 1, max: 100, min: 100),
        };
        var lib = FrcLibrary.Load(n => files.GetValueOrDefault(n));
        var sink = new Sink();
        var eng = new RumbleEngine(sink);
        return (new ForceModel(lib, eng, () => rand), eng, sink);
    }

    private const double Dt = 0.036;   // the sim tick, the original's frame

    private static ForceFrame Shot(uint cls, uint tier, float rate, int hardpoint = 0)
    {
        var f = new ForceFrame();
        f.Shots.Add(new WeaponShot(hardpoint, cls, tier, rate));
        return f;
    }

    private static float Strong(Sink s) => s.Frames.Count == 0 ? 0 : s.Frames[^1].Strong;

    [Fact]
    public void ShotsReadTheRowEachClassWasMeantFor()
    {
        // the shipped exe reads one row early (measured: 50cal MG 8, 25mm cannon 0, Gas Launcher 10, Landmines 16); the port does not
        Assert.Equal(8, ForceModel.WeaponId(1, 1));          // the .45: a rattle
        Assert.Equal(1, ForceModel.WeaponId(2, 0));          // machine guns: rattles
        Assert.Equal(2, ForceModel.WeaponId(2, 1));
        Assert.Equal(3, ForceModel.WeaponId(2, 2));
        Assert.Equal(5, ForceModel.WeaponId(2, 4));          // cannons: CANNON files
        Assert.Equal(7, ForceModel.WeaponId(2, 6));          // HADES
        Assert.Equal(4, ForceModel.WeaponId(2, 103));        // a turret is its tier less 100
        Assert.Equal(7, ForceModel.WeaponId(2, 15));         // the tank cannon: the row's heaviest
        Assert.Equal(17, ForceModel.WeaponId(3, 0));         // mortars: a thump
        Assert.Equal(20, ForceModel.WeaponId(3, 3));         // EZK
        Assert.Equal(20, ForceModel.WeaponId(3, 103));       // the howitzer
        Assert.Equal(9, ForceModel.WeaponId(4, 0));          // FireRite: MISSIL45
        Assert.Equal(12, ForceModel.WeaponId(4, 3));         // Cherub: MISSILE2
        Assert.Equal(13, ForceModel.WeaponId(5, 0));         // flame weapons: a triangle
        Assert.Equal(14, ForceModel.WeaponId(5, 1));         // the Gas Launcher
        Assert.Equal(21, ForceModel.WeaponId(6, 0));         // oil
        Assert.Equal(23, ForceModel.WeaponId(6, 3));         // landmines
        Assert.Equal(0, ForceModel.WeaponId(0, 1));
        Assert.Equal(0, ForceModel.WeaponId(7, 0));
        Assert.Equal(0, ForceModel.WeaponId(8, 0));
    }

    [Fact]
    public void AMachineGunRattlesInFramesWithAShotOnly()
    {
        var (m, _, _) = Rig();
        m.Frame(Shot(1, 1, 10), Dt);                         // the .45, id 8: gain 70
        Assert.Equal(0.7, m.Synth.Strong, 3);                // the square's attack from full
        m.Frame(Shot(1, 1, 10), Dt);
        Assert.Equal(0.7 * (1 - 0.5 * Dt * 10), m.Synth.Strong, 3);
        m.Frame(new ForceFrame(), Dt);
        Assert.Equal(0, m.Synth.Strong, 3);                  // stopped in the first frame without a shot
        m.Frame(Shot(1, 1, 10), Dt);
        Assert.Equal(0.7, m.Synth.Strong, 3);                // and from the start again
        var (m2, _, _) = Rig();
        m2.Frame(Shot(2, 1, 10), Dt);                        // a machine gun, id 2: gain 60
        Assert.Equal(0.6, m2.Synth.Strong, 3);
    }

    [Fact]
    public void ACannonPlaysItsCannonFile()
    {
        var (m, eng, sink) = Rig();
        m.Frame(Shot(2, 3, 3), Dt);                          // id 4: CANNON{rate} at 70%
        Assert.True(eng.Playing);
        Assert.Equal(0.4 * 0.7, Strong(sink), 3);
        var (m2, eng2, _) = Rig();
        m2.Frame(Shot(2, 3, 5), Dt);                         // there is no CANNON5
        Assert.False(eng2.Playing);
        var (m3, eng3, _) = Rig();
        m3.Frame(Shot(3, 3, 3), Dt);                         // the EZK mortar: a thump, no file
        Assert.False(eng3.Playing);
        Assert.Equal(1.0, m3.Synth.Weak, 3);
    }

    [Fact]
    public void AMissileLoopsItsFileWhileFiringAndStopsWithTheTrigger()
    {
        var (m, eng, sink) = Rig();
        for (int i = 0; i < 12; i++) m.Frame(Shot(4, 0, 20), Dt);   // FireRite, id 9: 432 ms, past MISSIL45's 200
        Assert.True(eng.Playing);
        Assert.Equal(0.5 * 0.7, Strong(sink), 3);
        m.Frame(new ForceFrame(), Dt);
        Assert.False(eng.Playing);
        Assert.Equal(1, sink.Stops);
        var (m2, eng2, _) = Rig();
        m2.Frame(Shot(5, 0, 20), Dt);                        // a flamethrower: the triangle, no file
        Assert.False(eng2.Playing);
        Assert.True(m2.Synth.Strong + m2.Synth.Weak > 0);
    }

    [Fact]
    public void MortarsThumpForAFifthOfASecond()
    {
        var (m, _, _) = Rig();
        m.Frame(Shot(3, 0, 4.5f), Dt);
        Assert.Equal(1.0, m.Synth.Weak, 3);                  // 47 Hz at full: the small motor
        for (int i = 0; i < 5; i++) m.Frame(Shot(3, 0, 4.5f), Dt);
        Assert.Equal(1.0, m.Synth.Weak, 3);                  // 180 ms
        m.Frame(Shot(3, 0, 4.5f), Dt);
        Assert.Equal(0, m.Synth.Weak, 3);                    // 216 ms: over
    }

    [Fact]
    public void AnExplosiveHitPlaysExplosnByItsDamageAndThreeAtMost()
    {
        var (m, eng, sink) = Rig();
        var f = new ForceFrame();
        f.Hits.Add((1, 45));
        m.Frame(f, Dt);
        Assert.Equal(0.65, Strong(sink), 3);                 // 35 + 45·2/3 percent
        m.StopAll();
        f.Hits.Clear();
        f.Hits.Add((1, 400));
        m.Frame(f, Dt);
        Assert.Equal(1.0, Strong(sink), 3);                  // at most full
        m.StopAll();
        f.Hits.Clear();
        for (int i = 0; i < 4; i++) f.Hits.Add((1, 0));
        m.Frame(f, Dt);
        Assert.Equal(1.0, Strong(sink), 3);                  // three of 0.35 add up past the limit ...
        m.StopAll();
        f.Hits.Clear();
        f.Hits.Add((1, 0)); f.Hits.Add((1, 0));
        m.Frame(f, Dt);
        Assert.Equal(0.7, Strong(sink), 3);                  // ... two make 0.7
        f.Hits.Clear();
        f.Hits.Add((2, 50));                                 // fire: nothing
        m.Frame(f, Dt);
        Assert.Equal(0.7, Strong(sink), 3);
        Assert.True(eng.Playing);
    }

    [Fact]
    public void BulletAndImpactHitsAreAJoltThatDiesAway()
    {
        var (m, _, _) = Rig();
        var f = new ForceFrame { EngineOn = true };
        f.Hits.Add((0, 15));
        m.Frame(f, Dt);
        Assert.Equal(0.5, m.Synth.Strong, 3);                // 40 + 15·2/3 percent
        Assert.Equal(0, m.Synth.Weak, 3);                    // the engine is silent while a hit is felt
        f.Hits.Clear();
        int push = 1, all = 1;
        double last = m.Synth.Strong;
        for (; all < 200; all++)
        {
            m.Frame(f, Dt);
            if (m.Synth.Weak > 0) break;
            if (m.Synth.Strong > 0) { Assert.True(m.Synth.Strong < last); last = m.Synth.Strong; push++; }
        }
        Assert.InRange(push, 6, 10);                         // the push dies away in about 0.3 s ...
        Assert.InRange(all, 30, 40);                         // ... and the hit is over at −1500, about 1.3 s
        var (m2, _, _) = Rig();
        var g = new ForceFrame { EngineOn = true };
        g.Hits.Add((3, 12));
        m2.Frame(g, Dt);
        Assert.Equal(0.6175, m2.Synth.Strong, 4);            // an impact: 55 + 12·0.5625 percent, dying at 3/s
        g.Hits.Clear();
        int n = 1;
        while (m2.Synth.Weak == 0 && n < 200) { m2.Frame(g, Dt); n++; }
        Assert.InRange(n, 40, 80);                           // over where it stops moving, short of −1500
    }

    [Fact]
    public void TheEngineHumsStandingAndFadesWithSpeed()
    {
        var (m, _, _) = Rig();
        var f = new ForceFrame { EngineOn = true };
        m.Frame(f, Dt);
        Assert.Equal(0.20, m.Synth.Weak, 3);
        f.Mph = 20;
        m.Frame(f, Dt);
        Assert.Equal(0.10, m.Synth.Weak, 3);
        f.Mph = 41;
        m.Frame(f, Dt);
        Assert.Equal(0, m.Synth.Weak, 3);
        f.Mph = 0;
        f.Shots.Add(new WeaponShot(0, 1, 1, 10));            // a weapon playing (the .45, 70%) halves it
        m.Frame(f, Dt);
        Assert.Equal(0.10 + 0.7 * 0.35, m.Synth.Weak, 3);
        f.Shots.Clear();
        f.Hits.Add((3, 1));                                  // a hit being felt silences it
        m.Frame(f, Dt);
        Assert.Equal(0, m.Synth.Weak, 3);
        Assert.Equal(0, new ForceModel(FrcLibrary.Load(_ => null), new RumbleEngine(new Sink())).Synth.Weak);
    }

    [Fact]
    public void TheRoadFollowsTheSurface()
    {
        var (m, _, _) = Rig();
        var f = new ForceFrame { Mph = 30, Surface = 3 };   // paved
        m.Frame(f, Dt);
        Assert.Equal(0.15, m.Synth.Weak, 3);
        f.Surface = 6;                                       // rocky
        m.Frame(f, Dt);
        Assert.Equal(0.40, m.Synth.Weak, 3);
        f.Surface = 9;                                       // in the air
        m.Frame(f, Dt);
        Assert.Equal(0, m.Synth.Weak, 3);
        f.Surface = 6; f.Mph = 0.5f;                         // all but stopped
        m.Frame(f, Dt);
        Assert.Equal(0, m.Synth.Weak, 3);
        var (j, _, _) = Rig(rand: 32767);
        j.Frame(new ForceFrame { Mph = 30, Surface = 6 }, Dt);
        Assert.Equal(0.21, j.Synth.Weak, 3);                 // the jitter takes up to half off
    }

    [Fact]
    public void AFlatTyreShakesBySpeedAndNotInTheAir()
    {
        var (m, _, _) = Rig();
        m.Frame(new ForceFrame { FlatFrontLeft = true, Mph = 50 }, Dt);
        Assert.Equal(0.6, m.Synth.Strong, 3);                // 120 per mph for a front one
        var (r, _, _) = Rig();
        r.Frame(new ForceFrame { FlatRearRight = true, Mph = 50 }, Dt);
        Assert.Equal(0.3, r.Synth.Strong, 3);                // 60 for a rear one
        var (q, _, _) = Rig();
        q.Frame(new ForceFrame { FlatFrontRight = true, Mph = 100 }, Dt);
        Assert.Equal(1.0, q.Synth.Strong, 3);
        q.Frame(new ForceFrame { FlatFrontRight = true, Mph = 100, Airborne = true }, Dt);
        Assert.Equal(0, q.Synth.Strong, 3);
    }

    [Fact]
    public void EngstartHoldsItsStartWhileCrankingThenPlaysOut()
    {
        var (m, eng, sink) = Rig();
        var crank = new ForceFrame { Cranking = true };
        for (int i = 0; i < 10; i++) m.Frame(crank, Dt);    // 360 ms, the file is 80
        Assert.True(eng.Playing);
        Assert.Equal(1.0, Strong(sink), 3);
        m.Frame(new ForceFrame(), Dt);
        m.Frame(new ForceFrame(), Dt);
        m.Frame(new ForceFrame(), Dt);
        Assert.False(eng.Playing);
    }

    [Fact]
    public void TheWeaponTicksPlayAtFortyPercent()
    {
        var (m, eng, sink) = Rig();
        m.Frame(new ForceFrame { Cycle = true }, Dt);
        Assert.Equal(0.4, Strong(sink), 3);
        m.Frame(new ForceFrame { Link = true }, Dt);
        Assert.Equal(0.8, Strong(sink), 3);
        m.Frame(new ForceFrame { Unlink = true }, Dt);
        Assert.True(eng.Playing);
    }

    [Fact]
    public void StoppingAllSilencesAndForgets()
    {
        var (m, eng, sink) = Rig();
        var f = Shot(4, 0, 20);
        f.Hits.Add((0, 100));
        f.EngineOn = true;
        m.Frame(f, Dt);
        Assert.True(eng.Playing);
        m.StopAll();
        Assert.False(eng.Playing);
        Assert.Equal(1, sink.Stops);
        m.StopAll();
        Assert.Equal(1, sink.Stops);                         // nothing was on: no second stop
        m.Frame(new ForceFrame { EngineOn = true }, Dt);
        Assert.Equal(0.20, m.Synth.Weak, 3);                 // the hit and the weapon are forgotten
        // a library with no files (no force directory) plays no file
        var (none, ne, _) = (new ForceModel(FrcLibrary.Load(_ => null), eng), eng, sink);
        none.Frame(Shot(4, 0, 20), Dt);
        Assert.False(ne.Playing);
    }

    [InstallFact]
    public void TheShippedFilesAllParse()
    {
        var dir = Directory.EnumerateDirectories(TestInstall.Dir!).First(d => Path.GetFileName(d).Equals("force", StringComparison.OrdinalIgnoreCase));
        var all = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".frc", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(14, all.Count);                        // the DLL's twelve and TIREBLWL / TIREBLWR, which nothing loads
        Assert.All(all, f => Assert.NotEmpty(FrcFile.Parse(File.ReadAllBytes(f))));
        var lib = FrcLibrary.Load(n => all.Where(f => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllBytes).FirstOrDefault());
        Assert.Equal(FrcLibrary.Names.Length, lib.Count);
        var cannon = lib.Get("CANNON1").Single();
        Assert.True(cannon.Custom);
        Assert.Equal(940, cannon.DurationMs);
        Assert.Equal(94, cannon.Samples.Length);
        Assert.Equal(945, lib.Get("EXPLOSN").Single().DurationMs);
        Assert.Equal(FrcEffect.Cosine, lib.Get("MISSIL45").Single().Waveform);
    }
}

/// <summary>The pad's rumble under mashing: bounded driver calls, nothing stuck on.</summary>
public class RumbleStressTests
{
    private sealed class Sink : IRumbleSink
    {
        public int Rumbles, Stops;
        public void Rumble(float weak, float strong) => Rumbles++;
        public void Stop() => Stops++;
    }

    [Fact]
    public void ThrottleLimitsDriverCallsOverLongPlay()
    {
        var sink = new Sink();
        double t = 0;
        var throttle = new ThrottledRumble(sink, () => t);
        var engine = new RumbleEngine(throttle);
        var jitter = new Random(11);
        var model = new ForceModel(new FrcLibrary(), engine, () => jitter.Next(32768));   // seeded: the road jitter made the stop count vary run to run
        var f = new ForceFrame();
        var rng = new Random(7);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long mid = 0;
        const double dt = 1.0 / 60;
        for (int i = 0; i < 60 * 60; i++)          // a minute of play at the sim rate
        {
            t += dt;
            f.Clear();
            f.EngineOn = true;
            f.Rpm = 800 + rng.Next(4000);
            f.Mph = rng.Next(165);
            f.Surface = rng.Next(1, 9);
            f.FlatFrontLeft = i % 600 < 300;
            if (rng.Next(20) == 0) f.Hits.Add((rng.Next(4), rng.Next(1, 40)));
            if (rng.Next(10) == 0) f.Shots.Add(new WeaponShot(rng.Next(6), (uint)rng.Next(8), (uint)rng.Next(7), 5));
            model.Frame(f, dt);
            if (i == 600) mid = GC.GetAllocatedBytesForCurrentThread();
        }
        long perTickLate = (GC.GetAllocatedBytesForCurrentThread() - mid) / (60 * 60 - 600);
        model.StopAll();
        Assert.True(sink.Rumbles <= 60 * 60 / 3, $"{sink.Rumbles} driver calls for 3600 ticks");
        Assert.True(sink.Stops <= 5, $"{sink.Stops} stops");
        Assert.True(perTickLate < 32768, $"{perTickLate} bytes allocated per tick");   // a leak check, not a budget: the JIT tiering up mid-run allocates on this thread too
        Assert.True(before >= 0);
    }

    [Fact]
    public void SteadyLevelsAreRenewedNotRepeated()
    {
        var sink = new Sink();
        double t = 0;
        var th = new ThrottledRumble(sink, () => t);
        for (int i = 0; i < 600; i++) { t += 1.0 / 60; th.Rumble(0.5f, 0.25f); }     // ten seconds, unchanged
        Assert.InRange(sink.Rumbles, 15, 25);
        th.Stop(); th.Stop(); th.Stop();
        Assert.Equal(1, sink.Stops);
    }

    [Fact]
    public void ChangesAreRateLimited()
    {
        var sink = new Sink();
        double t = 0;
        var th = new ThrottledRumble(sink, () => t);
        for (int i = 0; i < 600; i++) { t += 1.0 / 60; th.Rumble(i % 2 == 0 ? 0.1f : 0.9f, 0); }
        Assert.True(sink.Rumbles <= 10 / ThrottledRumble.MinGap / 1 + 1, $"{sink.Rumbles}");
        Assert.True(sink.Rumbles <= 210);
    }

    [Fact]
    public void PadFlapDoesNotRebuildOrWrite()
    {
        // the bank sees a pad come and go many times: counts follow, nothing else
        var src = new Flap();
        var bank = new JoystickBank(src);
        var rng = new Random(3);
        for (int i = 0; i < 20000; i++)
        {
            src.N = rng.Next(3);
            src.Mash = rng.NextSingle();
            bank.Poll(i % 7 != 0);
            Assert.Equal(src.N, bank.Count);
        }
        long a = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) { src.N = 1; bank.Poll(true); }
        Assert.True((GC.GetAllocatedBytesForCurrentThread() - a) / 5000 < 1024);
    }

    private sealed class Flap : IPadSource
    {
        public int N;
        public float Mash;
        public IReadOnlyList<PadRaw> Read() => [.. Enumerable.Range(0, N).Select(i => new PadRaw(Mash, -Mash, Mash * 0.01f, 0, Mash, 0, (ulong)(Mash * 1e9)))];
    }
}
