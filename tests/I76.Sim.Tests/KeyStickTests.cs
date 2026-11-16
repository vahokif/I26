using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>Keyboard Steering Assist (<see cref="KeyStick"/>): the keys as the pad's input, by time.</summary>
public class KeyStickTests
{
    private static readonly CarInput Left = new() { Buttons = CarButtons.SteerLeft };
    private static readonly CarInput Right = new() { Buttons = CarButtons.SteerRight };

    /// <summary>The steer the sim takes from the output (KeyRamp's analog path), after <paramref name="seconds"/> of each input in turn, the car going straight.</summary>
    private static float Steer(float dt, params (CarInput Input, float Seconds)[] script)
    {
        var k = new KeyStick();
        var ramp = new DriverState();
        float thr = 0, st = 0;
        foreach (var (input, seconds) in script)
            for (int i = 0, n = (int)MathF.Round(seconds / dt); i < n; i++)
                KeyRamp.Apply(ref ramp, k.Apply(input, dt, null), dt, null, ref thr, ref st);
        return st;
    }

    public static TheoryData<int> Rates() => [.. SimRate.Offered.Select(r => r.Label)];

    [Theory]
    [MemberData(nameof(Rates))]
    public void ItEasesTowardTheKeyByTime(int hz)
    {
        float dt = SimRate.FromLabel(hz).TickSeconds;
        float t = MathF.Round(0.2f / dt) * dt;
        // straight on (no slip, no grip known): 1 − e^(−t/0.2 s) of lock, the same at every rate
        Assert.Equal(1 - MathF.Exp(-t / KeyStick.Default.SteerTau), Steer(dt, (Left, 0.2f)), 2e-3f);
        Assert.Equal(-(1 - MathF.Exp(-t / KeyStick.Default.SteerTau)), Steer(dt, (Right, 0.2f)), 2e-3f);
        // let go, back toward the centre in a few hundredths of a second, and then the keys' input again
        Assert.InRange(Steer(dt, (Left, 0.2f), (default, 0.05f)), 0.05f, 0.4f);
        Assert.Equal(0, Steer(dt, (Left, 0.2f), (default, 1f)));
        // the other key goes back through the centre
        Assert.True(Steer(dt, (Left, 1f), (Right, 0.3f)) < 0);
        // both held: left, as the original's
        Assert.True(Steer(dt, (new CarInput { Buttons = CarButtons.SteerLeft | CarButtons.SteerRight }, 1f)) > 0.99f);
    }

    [Fact]
    public void ItSteersIntoASlideOnly()
    {
        var k = new KeyStick();
        // the car slides with its velocity 0.2 rad to its right: no key, and the wheels go right, 2 lock a radian, up to full lock
        var o = k.Apply(default, 0.05f, null, new KeyStick.Motion(20, -0.2f, Sliding: true));
        Assert.True(o.Has(CarButtons.Analog));
        Assert.Equal(-0.4f, o.Steer / 32767f, 1e-3f);
        Assert.Equal(-1f, k.Apply(default, 0.05f, null, new KeyStick.Motion(20, -1f, Sliding: true)).Steer / 32767f, 1e-3f);
        // gripping, a turn's own slip is not steered on
        Assert.Equal(new CarInput(), new KeyStick().Apply(default, 0.05f, null, new KeyStick.Motion(20, 0.03f, Sliding: false)));
        // the slip gone and no key: the input as it came
        Assert.Equal(new CarInput(), k.Apply(default, 0.05f, null, new KeyStick.Motion(20, 0)));
        // a stick with no key held is left alone
        var pad = new CarInput { Buttons = CarButtons.Analog, Steer = 1000, Throttle = -999 };
        Assert.Equal(pad, k.Apply(pad, 0.05f, null, new KeyStick.Motion(20, -0.3f, Sliding: true)));
    }

    [Fact]
    public void ItHoldsAPressInsideTheGripThenLetsGo()
    {
        // the Piranha's numbers: chassis grip 1.55, four wheels of grip 1, gain 1/(2 · wheelbase); dirt's grip 1.9
        var v = new I76.Sim.Vehicles.Vehicle { Rot = I76.Sim.Math.Rows.Identity, Vel = new System.Numerics.Vector3(0, 0, 22) };
        v.P.ChassisGrip = 1.55f; v.P.SteerGain = 0.188f;
        foreach (var w in v.Wheels) w.Grip = 1;
        float load = 9.8f * 1.55f * 1.9f * 4 * 0.25f, cap = 4 * 7.84f;
        // past the cap's speed the lateral pull is lock · cap: the grip gives way at load / cap of lock; slower, no limit under full lock
        Assert.Equal(load / cap, KeyStick.BlendLock(v, 22, 1.9f), 1e-4f);
        Assert.True(KeyStick.BlendLock(v, 8, 1.9f) > 1);
        var k = new KeyStick();
        var m = KeyStick.MotionOf(v, 1.9f);
        // for the press's first LimitHold seconds
        int hold = (int)(KeyStick.Default.LimitHold / 0.05f);
        float held = MathF.Min(0.98f * load / cap, KeyStick.Default.LatCap / cap);   // the grip's limit, or the cornering cap's
        for (int i = 0; i < hold; i++) k.Apply(Left, 0.05f, null, m);
        Assert.Equal(held, k.Steer, 1e-3f);
        // then the hold lets go: the whole lock
        for (int i = 0; i < 30; i++) k.Apply(Left, 0.05f, null, m);
        Assert.Equal(1f, k.Steer, 0.02f);
        // and a new press is held again
        k.Apply(default, 0.05f, null, m);
        for (int i = 0; i < hold; i++) k.Apply(Left, 0.05f, null, m);
        Assert.Equal(held, k.Steer, 1e-3f);
        // the countersteer is not held back
        Assert.Equal(-1f, new KeyStick().Apply(default, 0.05f, null, m with { Slip = -1f, Sliding = true }).Steer / 32767f, 1e-3f);
    }

    [Fact]
    public void ItsHoldLeavesAQuarterLockAndMakesRoomForABlownTyresPull()
    {
        var k = new KeyStick();
        // a light load (a steep slope) puts the grip's limit near nothing: the keys keep a quarter of lock
        for (int i = 0; i < 8; i++) k.Apply(Left, 0.05f, null, new KeyStick.Motion(25, 0, 0.05f));
        Assert.Equal(KeyStick.MinHold, k.Steer, 1e-3f);
        // the front left tyre blown pulls the steer 0.3 left: the left key has 0.3 less room, the right 0.3 more
        var p = new KeyStick();
        for (int i = 0; i < 8; i++) p.Apply(Left, 0.05f, null, new KeyStick.Motion(25, 0, 0.7f, 0.3f));
        Assert.Equal(0.98f * 0.7f - 0.3f, p.Steer, 1e-3f);
        var q = new KeyStick();
        for (int i = 0; i < 10; i++) q.Apply(Right, 0.05f, null, new KeyStick.Motion(25, 0, 0.5f, 0.3f));
        Assert.Equal(-(0.98f * 0.5f + 0.3f), q.Steer, 2e-3f);
    }

    [Fact]
    public void TheSlipIsMeasuredFromTheHeadingAndFoldedBackward()
    {
        var v = new I76.Sim.Vehicles.Vehicle { Rot = I76.Sim.Math.Rows.Identity };
        // heading +z; moving +z and toward −x (the left): + slip
        v.Vel = new System.Numerics.Vector3(-1, 0, 10);
        Assert.Equal(MathF.Atan2(1, 10), KeyStick.MotionOf(v).Slip, 1e-4f);
        Assert.Equal(10, KeyStick.MotionOf(v).ForwardSpeed, 1e-4f);
        // rolling backward and to the left: measured from the tail, the other way round
        v.Vel = new System.Numerics.Vector3(-1, 0, -10);
        Assert.Equal(-MathF.Atan2(1, 10), KeyStick.MotionOf(v).Slip, 1e-4f);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void TheThrottleKeysKeepTheOriginalsRamp(int hz)
    {
        // per tick (the 1997 model) and by time (Modern Physics' 120 Hz steps): the throttle the sim takes is the original's, to the wire's step
        var r = SimRate.FromLabel(hz);
        foreach (var (dt, timeBased) in new (float, float?)[] { (r.TickSeconds, null), (1 / 120f, r.TickSeconds) })
        {
            var k = new KeyStick();
            DriverState orig = default, viaStick = default;
            float ot = 0, os = 0, st = 0, ss = 0;
            var keys = new[] { CarButtons.ThrottleUp | CarButtons.SteerLeft, CarButtons.ThrottleUp, CarButtons.ThrottleDown | CarButtons.SteerRight, CarButtons.None, CarButtons.ThrottleUp };
            foreach (var b in keys)
                for (int i = 0; i < (int)(0.4f / dt); i++)
                {
                    var input = new CarInput { Buttons = b };
                    KeyRamp.Apply(ref orig, input, dt, timeBased, ref ot, ref os);
                    KeyRamp.Apply(ref viaStick, k.Apply(input, dt, timeBased), dt, timeBased, ref st, ref ss);
                    Assert.Equal(ot, st, 1e-4f);
                }
        }
    }

    [Fact]
    public void TheKeysGoOutAsThePadsInput()
    {
        var k = new KeyStick();
        var o = k.Apply(new CarInput { Buttons = CarButtons.SteerRight | CarButtons.EBrake, Commands = CarCommands.ShiftUp }, 0.05f, null);
        Assert.True(o.Has(CarButtons.Analog) && o.Has(CarButtons.EBrake));
        Assert.Equal(CarCommands.ShiftUp, o.Commands);
        Assert.Equal((short)MathF.Round(-(1 - MathF.Exp(-0.05f / KeyStick.Default.SteerTau)) * 32767), o.Steer);
        Assert.Equal(0, o.Throttle);
    }

    [Theory]
    [InlineData("original", false)]
    [InlineData("assisted", true)]
    [InlineData(null, true)]
    public void TheOlderSettingsWordReadsAsOnUnlessOriginal(string? word, bool on) => Assert.Equal(on, KeyStick.AssistedFromKeyboardSteering(word));
}
