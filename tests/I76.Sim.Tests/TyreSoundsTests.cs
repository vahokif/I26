using System.Numerics;
using I76.Sim.Rng;
using I76.Sim.Sound;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The player's tyre sounds. The real game (a t01 drive) asked for:
/// nothing on the road (surface 2) and vcddirt / vcdsand on surfaces 0 / 1 above 7 m/s; tturn3 while
/// blending on sand; tskid3 while sliding on sand at any speed (down to 0.3 m/s); nothing in the air.
/// </summary>
public class TyreSoundsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static List<string> Ask(Vehicle v, bool isPlayer = true, bool worn = false)
    {
        var l = new List<string>();
        TyreSounds.Wanted(v, isPlayer, worn, l);
        return l;
    }

    private static Vehicle At(int surface, float speed, VehicleFlags flags = VehicleFlags.EngineOn) =>
        new() { Surface = surface, Speed = speed, Flags = flags };

    [Fact]
    public void TheSurfaceRowsAreTheOriginalsTable()
    {
        string[] skid = ["tskid2", "tskid3", "tskid1", "tskid2", "tskid2", "tskid4", "tskid1", "tskid2"];
        string?[] roll = ["vcddirt", "vcdsand", null, "vcddirt", "vcddirt", "vcdgrav", null, "vcddirt"];
        for (int s = 0; s < 8; s++)
        {
            Assert.Equal(skid[s] + ".wav", TyreSounds.Rows[s].Skid);
            Assert.Equal(skid[s].Replace("skid", "turn") + ".wav", TyreSounds.Rows[s].Turn);
            Assert.Equal(roll[s] is null ? null : roll[s] + ".wav", TyreSounds.Rows[s].Roll);
        }
    }

    [Fact]
    public void ASlideSkidsAtAnySpeedAndHidesTheRollingSound()
    {
        Assert.Equal(["tskid3.wav"], Ask(At(1, 0.3f, VehicleFlags.Sliding)));
        Assert.Equal(["tskid3.wav"], Ask(At(1, 20f, VehicleFlags.Sliding)));
        Assert.Equal(["tskid3.wav"], Ask(At(1, 20f, VehicleFlags.Sliding | VehicleFlags.Blending)));
        Assert.Equal(["tskid1.wav"], Ask(At(2, 3f, VehicleFlags.Sliding)));
    }

    [Fact]
    public void AboveSevenTheTurnOrTheSurface()
    {
        Assert.Empty(Ask(At(1, 7f)));                                       // 7.0 is not above 7
        Assert.Equal(["vcdsand.wav"], Ask(At(1, 7.01f)));
        Assert.Equal(["tturn3.wav"], Ask(At(1, 20f, VehicleFlags.Blending)));
        Assert.Empty(Ask(At(1, 5f, VehicleFlags.Blending)));                // the turn sound needs speed too
        Assert.Empty(Ask(At(2, 30f)));                                      // the road rolls silent
        Assert.Equal(["tturn1.wav"], Ask(At(2, 30f, VehicleFlags.Blending)));
        Assert.Equal(["vcdgrav.wav"], Ask(At(5, 30f)));
    }

    [Fact]
    public void AirborneOrNotThePlayerIsSilent()
    {
        Assert.Empty(Ask(At(1, 20f, VehicleFlags.Sliding | VehicleFlags.Airborne)));
        Assert.Empty(Ask(At(1, 20f, VehicleFlags.Sliding), isPlayer: false));
    }

    [Fact]
    public void FlatTyreAndBrakeGrind()
    {
        var v = At(2, 20f);
        v.RearLeft.Blown = true;
        Assert.Equal(["tflat.wav"], Ask(v));
        v.Throttle = -0.6f;
        Assert.Equal(["tflat.wav"], Ask(v));
        Assert.Equal(["tflat.wav", "fdmg1.wav"], Ask(v, worn: true));
        v.Speed = 5;
        Assert.Empty(Ask(v, worn: true));
        Assert.Equal(TyreSounds.MixDb - 4f, TyreSounds.VolumeDb("tskid3.wav"));
        Assert.Equal(TyreSounds.MixDb - 4f, TyreSounds.VolumeDb("TTURN1.WAV"));
        Assert.Equal(TyreSounds.MixDb, TyreSounds.VolumeDb("vcdsand.wav"));
    }

    private sealed class Sand : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 1;
        public Surface Surface(int id) => new(1f, 0.03f, 0, 0, 0);
    }

    private static readonly float Tick = I76.Sim.Session.SimRate.R20.TickSeconds;

    private static Vehicle Car()
    {
        var v = new Vehicle();
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z);
            w.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        return v;
    }

    /// <summary>
    /// The sim's own flags drive it: a car rolling on sand gets the rolling sound, a hard turn at speed
    /// blends (tturn3) and then slides (tskid3), and straightening up stops the skid once it grips again.
    /// </summary>
    [Fact]
    public void TheSimsSlideStartsAndStopsTheSkid()
    {
        var step = new VehicleStep(new Sand(), new CrtRand(1));
        var v = Car();
        // rolling at 30 on sand (the test car's bare drive would spin its wheels from rest: a slide too)
        v.Vel = new Vector3(0, 0, 30); v.Speed = 30;
        string Now() => string.Join(" ", Ask(v));
        for (int i = 0; i < 10; i++) step.Frame(v, Tick);
        Assert.Equal("vcdsand.wav", Now());
        int skidAt = -1, turnAt = -1, stopAt = -1;
        for (int i = 0; i < 100; i++)
        {
            v.Steer = MathF.Min(1, i * 0.05f);
            step.Frame(v, Tick);
            var n = Now();
            if (n == "tturn3.wav" && turnAt < 0) turnAt = i;
            if (n == "tskid3.wav" && skidAt < 0) skidAt = i;
            output.WriteLine($"steer {v.Steer:0.00} speed {v.Speed:0.0} flags 0x{(uint)v.Flags:x} -> {n}");
            if (skidAt >= 0 && i > skidAt + 10) break;
        }
        Assert.True(turnAt >= 0, "never blended");
        Assert.True(skidAt > turnAt, $"blend at {turnAt}, skid at {skidAt}");
        v.Steer = 0;
        for (int i = 0; i < 200; i++)
        {
            step.Frame(v, Tick);
            if ((v.Flags & VehicleFlags.Sliding) == 0) { stopAt = i; break; }
            Assert.Equal("tskid3.wav", Now());
        }
        Assert.True(stopAt >= 0, "never gripped again");
        Assert.DoesNotContain("tskid3.wav", Ask(v));
    }
}
