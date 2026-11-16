using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Auto Reverse on a real car (the original's physics, the Piranha on Auto Melee m04's flat run): the brake held from
/// 30 mph, from a crawl and from rest, and accelerate held while backing up, through <see cref="AutoReverse"/> as the
/// game feeds it (the keys read before the tick, the car as the last tick left it).
/// </summary>
public class AutoReverseDriveTests(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public enum Start { Speed30, Crawl, Rest, Backing10 }

    /// <summary>Seconds from the key's press: the car at rest, the shift, the throttle pulling the new way, 0.5 and 2 m/s the new way.</summary>
    public sealed record Run(float Stop, float Shift, float Pulling, float Half, float Two, int Shifts, float Tick);

    private sealed class Car
    {
        public required SimSession S;
        public required Vehicle V;
        public required MissionSetup Setup;
        public readonly List<int> Gears = [];
        public float Fwd => Vector3.Dot(V.Vel, V.Rot.Forward);
        public CarInput Last;
        public void Tick(CarInput i) { Last = i; S.Tick([i]); Setup.Damage?.Events.Clear(); Gears.Add(V.Gear < 0 ? -1 : 1); }
    }

    private static Car Build(string dir, SimRate rate)
    {
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("m04")!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, rate);
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        v.Pos = new Double3(4172.5, 119.9, 49552.5);
        v.Rot = new Rows(new Vector3(0, 0, -1), Vector3.UnitY, Vector3.UnitX);
        v.Vel = Vector3.Zero;
        var c = new Car { S = s, V = v, Setup = setup };
        float dt = rate.TickSeconds;
        // the engine on, then a second at rest
        for (int t = 0; t < (int)(4 / dt); t++) c.Tick(new CarInput { Commands = t * dt is >= 2.5f and < 2.9f ? CarCommands.StartEngine : CarCommands.None });
        Assert.NotEqual(0, (int)(v.Flags & VehicleFlags.EngineOn));
        return c;
    }

    private static CarInput Pedal(bool accelerate, bool pad) => pad
        ? new CarInput { Buttons = CarButtons.Analog, Throttle = (short)(accelerate ? 32767 : -32767) }
        : new CarInput { Buttons = accelerate ? CarButtons.ThrottleUp : CarButtons.ThrottleDown };

    /// <param name="lift">The tick with neither key where the keys turn round (passing the car's throttle); off, the rule before it.</param>
    internal static Run Drive(string dir, SimRate rate, Start start, bool pad, bool lift = true, Action<string>? log = null)
    {
        var c = Build(dir, rate);
        var v = c.V;
        float dt = rate.TickSeconds;
        var ar = new AutoReverse(rate);
        CarInput Through(CarInput raw) => ar.Apply(raw, v.Gear < 0, c.Fwd, lift ? v.Throttle : 0f);
        if (start == Start.Speed30)
            for (int t = 0; t < (int)(20 / dt) && c.Fwd < 13.41f; t++) c.Tick(Through(Pedal(true, false)));
        else if (start == Start.Crawl)
        {
            // up to 2.5 m/s and let go: the car rolls on just under 1 m/s (no resistance below it)
            for (int t = 0; t < (int)(5 / dt) && c.Fwd < 2.5f; t++) c.Tick(Through(Pedal(true, false)));
            for (int t = 0; t < (int)(20 / dt) && c.Fwd >= 1f; t++) c.Tick(Through(default));
            for (int t = 0; t < (int)(0.5f / dt); t++) c.Tick(Through(default));
        }
        else if (start == Start.Backing10)
            for (int t = 0; t < (int)(20 / dt) && c.Fwd > -4.47f; t++) c.Tick(Through(Pedal(false, false)));
        bool forward = start == Start.Backing10;
        float sign = forward ? 1 : -1;
        log?.Invoke($"{(forward ? "accelerate" : "brake")} from {c.Fwd:0.00} m/s, throttle {v.Throttle:0.00}, gear {v.Gear}");
        float stop = float.NaN, shift = float.NaN, pulling = float.NaN, half = float.NaN, two = float.NaN;
        int shifts = 0;
        for (int t = 1; t <= (int)(10 / dt) && float.IsNaN(two); t++)
        {
            var i = Through(Pedal(forward, pad));
            if ((i.Commands & CarCommands.Reverse) != 0) { shifts++; if (float.IsNaN(shift)) shift = t * dt; }
            c.Tick(i);
            float now = t * dt, f = c.Fwd * sign;
            if (float.IsNaN(stop) && f > -0.5f) stop = now;
            if (float.IsNaN(pulling) && v.Gear * sign > 0 && v.Throttle > 1e-4f) pulling = now;
            if (float.IsNaN(half) && f > 0.5f) half = now;
            if (float.IsNaN(two) && f > 2f) two = now;
            log?.Invoke($"  {now:0.000}s fwd {c.Fwd,7:0.00} gear {v.Gear,2} throttle {v.Throttle,6:0.00} {(i.Has(CarButtons.ThrottleUp) ? "up" : i.Has(CarButtons.ThrottleDown) ? "down" : i.Has(CarButtons.Analog) ? $"axis {i.Throttle}" : "-")}{((i.Commands & CarCommands.Reverse) != 0 ? " SHIFT" : "")}");
        }
        return new Run(stop, shift, pulling, half, two, shifts, dt);
    }

    [Theory]
    [InlineData(20, Start.Speed30, false)]
    [InlineData(20, Start.Crawl, false)]
    [InlineData(20, Start.Rest, false)]
    [InlineData(20, Start.Backing10, false)]
    [InlineData(20, Start.Speed30, true)]
    [InlineData(20, Start.Backing10, true)]
    [InlineData(60, Start.Speed30, false)]
    [InlineData(60, Start.Crawl, false)]
    [InlineData(60, Start.Rest, false)]
    [InlineData(60, Start.Backing10, false)]
    [InlineData(60, Start.Speed30, true)]
    [InlineData(60, Start.Backing10, true)]
    public void TheHeldKeyTurnsTheCarRoundOnceAndAtOnce(int hz, Start start, bool pad)
    {
        if (InstallDir is not { } dir) return;
        var rate = SimRate.FromLabel(hz);
        var before = Drive(dir, rate, start, pad, lift: false);
        var r = Drive(dir, rate, start, pad, log: output.WriteLine);
        static string Row(Run r) => $"at rest {r.Stop:0.000} shift {r.Shift:0.000} pulling {r.Pulling:0.000} 0.5 m/s {r.Half:0.000} 2 m/s {r.Two:0.000}";
        output.WriteLine($"{hz} Hz {start}{(pad ? " pad" : "")}\n  before {Row(before)}\n  now    {Row(r)}");
        // one shift, the car going the new way, never later than before
        Assert.Equal(1, r.Shifts);
        Assert.False(float.IsNaN(r.Two));
        Assert.True(r.Half <= before.Half + 1e-4f && r.Two <= before.Two + 1e-4f);
        // the shift comes on the tick after the car is at rest (the brake's own stop), and the throttle pulls the new
        // way on the next: the keys' ramp is not left to climb back from a held brake
        Assert.InRange(r.Shift - r.Stop, -1e-4f, r.Tick + 1e-4f);
        Assert.InRange(r.Pulling - r.Shift, -1e-4f, r.Tick + 1e-4f);
    }

    /// <summary>Braked to rest and then accelerate, by the keys and by the triggers: one shift each way, no flapping between them.</summary>
    [Theory]
    [InlineData(20, false, 0.1f)]
    [InlineData(20, true, 0.1f)]
    [InlineData(60, false, 0.1f)]
    [InlineData(60, true, 0.1f)]
    [InlineData(20, false, 0.5f)]
    [InlineData(60, true, 0.5f)]
    public void BrakedToRestThenAccelerateDoesNotFlipFlop(int hz, bool pad, float wait)
    {
        if (InstallDir is not { } dir) return;
        var rate = SimRate.FromLabel(hz);
        var c = Build(dir, rate);
        float dt = rate.TickSeconds;
        var ar = new AutoReverse(rate);
        CarInput Through(CarInput raw) => ar.Apply(raw, c.V.Gear < 0, c.Fwd, c.V.Throttle);
        for (int t = 0; t < (int)(20 / dt) && c.Fwd < 13.41f; t++) c.Tick(Through(Pedal(true, pad)));
        int n0 = c.Gears.Count;
        // the brake held to rest and a moment after (the shift into reverse comes), then accelerate
        for (int t = 0; t < (int)(10 / dt) && c.Fwd > 0.01f; t++) c.Tick(Through(Pedal(false, pad)));
        for (int t = 0; t < (int)(wait / dt); t++) c.Tick(Through(Pedal(false, pad)));
        for (int t = 0; t < (int)(3 / dt); t++) c.Tick(Through(Pedal(true, pad)));
        var gears = c.Gears.Skip(n0).ToList();
        int changes = gears.Zip(gears.Skip(1)).Count(p => p.First != p.Second);
        output.WriteLine($"{hz} Hz{(pad ? " pad" : "")} wait {wait}: {changes} gear changes, ends at {c.Fwd:0.00} m/s in {c.V.Gear}");
        Assert.Equal(2, changes);
        Assert.Equal(1, c.V.Gear);
        Assert.True(c.Fwd > 5f);
    }
}
