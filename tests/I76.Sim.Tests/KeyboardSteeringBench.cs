using System.Globalization;
using System.Numerics;
using System.Text;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Keyboard Steering Assist against the original's ramp, with a stick at full lock for reference, on the 1997 vehicle model (the
/// Piranha on flat ground, the speed held by the same cruise control for every driver):
/// <list type="bullet">
/// <item>sharp turns: a 90° junction turn at 30, 50 and 70 mph and a U-turn at 15 and 25 mph, the key (or the stick) held until
/// the heading is round, then let go: the time it takes, how far the car runs on along its approach (wide) and whether it
/// slides or spins;</item>
/// <item>a slalom: the keys swapped every half second at 40 mph, the swing against the stick's;</item>
/// <item>a course for precision at speed (a sweeping bend and a lane change at 25 m/s, a tight turn at 7 m/s) driven by a
/// keyboard driver that can only press left, right or nothing each tick (bang-bang on the yaw rate a pure-pursuit point asks
/// for, a tenth of a second late) and a stick driver.</item>
/// </list>
/// The numbers are printed; I76_KBD_STEER_OUT=dir also writes traces as CSV, I76_KBD_STEER_SWEEP=1 tries other rates.
/// </summary>
public class KeyboardSteeringBench(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? OutDir => Environment.GetEnvironmentVariable("I76_KBD_STEER_OUT") is { Length: > 0 } d ? d : null;
    private const float Mph = 0.44704f;

    /// <summary>Flat ground of one surface; its grip 2 unless given (the stock missions' dirt is 1.9, road 2.1, sand 1.5).</summary>
    private sealed class Flat(float grip = 2f) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(grip, 0.03f, 0, 0, 0);
        public float BareHeight(double x, double z) => 0;
        public Vector3 BareNormal(double x, double z) => Vector3.UnitY;
    }

    /// <summary>
    /// Flat ground with a kicker across the way once <see cref="On"/>: from z = <paramref name="z0"/> a straight ramp up to
    /// <paramref name="height"/> m at z = <paramref name="z1"/>, then a drop back to the flat.
    /// </summary>
    private sealed class Kicker(float z0, float z1, float height, float grip = 2f) : IGround
    {
        public bool On;
        private float Slope => height / (z1 - z0);
        private bool OnRamp(double z) => On && z >= z0 && z < z1;
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (BareHeight(x, z), BareNormal(x, z));
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(grip, 0.03f, 0, 0, 0);
        public float BareHeight(double x, double z) => OnRamp(z) ? (float)(z - z0) * Slope : 0;
        public Vector3 BareNormal(double x, double z) => OnRamp(z) ? Vector3.Normalize(new Vector3(0, 1, -Slope)) : Vector3.UnitY;
    }

    /// <summary>How the steer is made: the original's ramp, a port ramp (with these rates), or a stick.</summary>
    public sealed record Mode(string Name, bool Keys, KeyStick.Assist? Tuning = null, bool Assisted = false)
    {
        public static readonly Mode Original = new("Original", true);
        public static readonly Mode AssistedKeys = new("Assisted", true, KeyStick.Default, true);
        /// <summary>Assisted without its countersteer, and as it first was (0.25 s, no hold, the countersteer always on), for comparison.</summary>
        public static readonly Mode AssistedNoCs = new("Assisted-cs", true, KeyStick.Default with { CsGain = 0, CsLimit = 0 }, true);
        public static readonly Mode AssistedFirst = new("Assisted first", true, new KeyStick.Assist(0.25f, 0.1f, 2f, 1f, 0, float.PositiveInfinity, 0, CsSlidingOnly: false), true);
        public static readonly Mode Stick = new("Stick", false);
        public static Mode Assist(KeyStick.Assist a, string name) => new(name, true, a, true);
    }

    /// <summary>A car on the flat at speed, and the steer path of one mode.</summary>
    private sealed class Run
    {
        public readonly Vehicle V;
        private readonly VehicleStep _step;
        private readonly Mode _mode;
        private readonly KeyStick _stick;
        private DriverState _ramp;
        private float _thr, _steer, _prevHeading;
        public readonly float Dt;
        public float T, Yaw;
        public bool Slid;
        /// <summary>This tick's tyre sound by the original's rule (TyreSounds): skid while sliding, the turn squeal while the grip model blends, over 7 m/s.</summary>
        public bool Skid => (V.Flags & VehicleFlags.Sliding) != 0;
        public bool Squeal => !Skid && (V.Flags & VehicleFlags.Blending) != 0 && V.Speed > I76.Sim.Sound.TyreSounds.FastSpeed;

        private readonly float _grip;

        public Run(AssetVfs vfs, Mode mode, SimRate rate, float speed, float grip = 2f, string car = "vppirna1.vcf", IGround? ground = null)
        {
            _grip = grip;
            var g = ground ?? new Flat(grip);
            V = VehicleFactory.Create(vfs, car);
            _step = new VehicleStep(g, new CrtRand(1));
            V.Pos = new Double3(0, 0, 0);
            V.Rot = Rows.Identity;
            VehicleStep.PrimeWheels(V, g);
            V.Flags |= VehicleFlags.EngineOn;   // as a player's car starts (some of the scenery's are built with it off)
            _mode = mode;
            _stick = new KeyStick(mode.Tuning);
            Dt = rate.TickSeconds;
            _prevHeading = Heading;
            // up to speed from rest on the straight, a second at it, and then from the origin
            float settled = 0;
            for (int i = 0; i < (int)(60 / Dt) && settled < 1; i++)
            {
                V.Steer = 0;
                V.Throttle = System.Math.Clamp((speed - Speed) * 0.5f, -1f, 1f);
                _step.Frame(V, Dt);
                // a heavy car's cruise sits a little under its speed: past 30 s, within 8%
                settled = MathF.Abs(Speed - speed) < (i * Dt < 30 ? 0.02f : 0.08f) * speed ? settled + Dt : 0;
            }
            Assert.True(settled >= 1, $"the car never held {speed / Mph:0} mph");
            V.Pos = new Double3(0, V.Pos.Y, 0);
            V.Rot = Rows.Identity;
            V.Vel = new Vector3(0, V.Vel.Y, Speed);
            _prevHeading = Heading;
        }

        /// <summary>+ turns left; 0 along +z.</summary>
        public float Heading => MathF.Atan2(-V.Rot.Forward.X, V.Rot.Forward.Z);
        public float Speed => new Vector2(V.Vel.X, V.Vel.Z).Length();

        /// <summary>One tick: key −1, 0 or +1 (left), or the stick at <paramref name="analog"/>; the speed held at <paramref name="cruise"/>.</summary>
        public void Tick(int key, float analog, float cruise, bool handbrake = false, float grip = float.NaN)
        {
            var input = new CarInput();
            if (!_mode.Keys) input = new CarInput { Buttons = CarButtons.Analog, Steer = (short)MathF.Round(System.Math.Clamp(analog, -1, 1) * 32767) };
            else if (key > 0) input.Buttons = CarButtons.SteerLeft;
            else if (key < 0) input.Buttons = CarButtons.SteerRight;
            if (handbrake) input.Buttons |= CarButtons.EBrake;
            if (_mode.Assisted) input = _stick.Apply(input, Dt, null, KeyStick.MotionOf(V, _grip));   // the run's ground's grip
            KeyRamp.Apply(ref _ramp, input, Dt, null, ref _thr, ref _steer);
            V.Steer = _steer;
            V.Throttle = System.Math.Clamp((cruise - Speed) * 0.5f, -1f, 1f);
            V.Handbrake = handbrake ? 1 : 0;
            _step.Frame(V, Dt);
            float h = Heading;
            Yaw = MathF.Atan2(MathF.Sin(h - _prevHeading), MathF.Cos(h - _prevHeading)) / Dt;
            _prevHeading = h;
            T += Dt;
            if ((V.Flags & VehicleFlags.Sliding) != 0) Slid = true;
        }
    }

    public sealed record Turn(Mode Mode, float Mph, float Angle, float Time, float Advance, float Lateral, float Overshoot, float ExitSpeed, bool Slid, bool Done);

    /// <summary>
    /// From straight at the speed, the key (or the stick at full lock) held until the heading has turned <paramref name="degrees"/>,
    /// then let go and 1.5 s more. Advance: how far on along the approach the car got by then (the wide line); lateral: how
    /// far to the side; overshoot: the heading past the turn 1.5 s later (a spin keeps going).
    /// </summary>
    private static Turn TurnRun(AssetVfs vfs, Mode mode, float mph, float degrees, SimRate rate, StringBuilder? csv = null, float grip = 2f)
    {
        var r = new Run(vfs, mode, rate, mph * Mph, grip);
        float goal = degrees * MathF.PI / 180;
        for (int i = 0; i < (int)(0.5f / r.Dt); i++) r.Tick(0, 0, mph * Mph);
        var p0 = r.V.Pos; float t0 = r.T, turned = 0, prev = r.Heading;
        r.Slid = false;
        float time = float.NaN, adv = float.NaN, lat = float.NaN;
        void Log() => csv?.Append(CultureInfo.InvariantCulture, $"{mode.Name},{mph},{degrees},{r.T - t0:0.000},{r.V.Pos.X - p0.X:0.00},{r.V.Pos.Z - p0.Z:0.00},{r.V.Steer:0.000},{r.Yaw:0.000}\n");
        while (r.T - t0 < 15)
        {
            r.Tick(1, 1, mph * Mph);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
            Log();
            if (turned >= goal) { time = r.T - t0; adv = (float)(r.V.Pos.Z - p0.Z); lat = (float)-(r.V.Pos.X - p0.X); break; }
        }
        float exit = r.Speed;
        for (int i = 0; i < (int)(1.5f / r.Dt); i++)
        {
            r.Tick(0, 0, mph * Mph);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
            Log();
        }
        return new Turn(mode, mph, degrees, time, adv, lat, (turned - goal) * 180 / MathF.PI, exit / Mph, r.Slid, !float.IsNaN(time));
    }

    /// <summary>
    /// From <paramref name="mph"/>, the brake to <paramref name="turnMph"/> straight on, then the key held until the heading has
    /// turned 90°: the time and the way on along the approach from the brake.
    /// </summary>
    private static (float Time, float Advance) BrakeThenTurn(AssetVfs vfs, Mode mode, float mph, float turnMph, SimRate rate)
    {
        var r = new Run(vfs, mode, rate, mph * Mph);
        var p0 = r.V.Pos; float t0 = r.T;
        while (r.Speed > turnMph * Mph && r.T - t0 < 10) r.Tick(0, 0, 0);
        float turned = 0, prev = r.Heading;
        while (r.T - t0 < 20)
        {
            r.Tick(1, 1, turnMph * Mph);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
            if (turned >= MathF.PI / 2) return (r.T - t0, (float)(r.V.Pos.Z - p0.Z));
        }
        return (float.NaN, float.NaN);
    }

    public sealed record Recovery(Mode Mode, float Seconds, float TurnAfter, float SlipAtRelease, bool Spun);

    /// <summary>
    /// A slide provoked at <paramref name="mph"/>: the handbrake and the left key until the slip is <paramref name="slipDeg"/>, then
    /// every key let go (the throttle at the cruise). Seconds: until the slide model lets go (the slip under 3 m/s); TurnAfter: how far
    /// the heading went on turning after the release (degrees); spun: more than 90° of it.
    /// </summary>
    private static Recovery SlideRun(AssetVfs vfs, Mode mode, SimRate rate, float mph = 40, float slipDeg = 20, StringBuilder? csv = null, float grip = 2f)
    {
        var r = new Run(vfs, mode, rate, mph * Mph, grip);
        float t0 = r.T;
        while (r.T - t0 < 3 && MathF.Abs(KeyStick.MotionOf(r.V).Slip) < slipDeg * MathF.PI / 180) { r.Tick(1, 1, mph * Mph, handbrake: true); csv?.Append(CultureInfo.InvariantCulture, $"{mode.Name},{r.T - t0:0.000},{r.V.Steer:0.000},{r.Yaw:0.000},{KeyStick.MotionOf(r.V).Slip:0.000},{((r.V.Flags & VehicleFlags.Sliding) != 0 ? 1 : 0)}\n"); }
        float slip0 = KeyStick.MotionOf(r.V).Slip * 180 / MathF.PI;
        float t1 = r.T, turned = 0, prev = r.Heading, seconds = float.NaN;
        while (r.T - t1 < 6)
        {
            r.Tick(0, 0, mph * Mph);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
            csv?.Append(CultureInfo.InvariantCulture, $"{mode.Name},{r.T - t0:0.000},{r.V.Steer:0.000},{r.Yaw:0.000},{KeyStick.MotionOf(r.V).Slip:0.000},{((r.V.Flags & VehicleFlags.Sliding) != 0 ? 1 : 0)}\n");
            if (float.IsNaN(seconds) && (r.V.Flags & VehicleFlags.Sliding) == 0) seconds = r.T - t1;
        }
        float deg = turned * 180 / MathF.PI;
        return new Recovery(mode, seconds, deg, slip0, MathF.Abs(deg) > 90);
    }

    /// <summary>From <paramref name="mph"/> on ground of <paramref name="grip"/>, a key held 3 s: seconds to the turn squeal and to the skid (NaN: none).</summary>
    private static (float Squeal, float Skid, float SteerAtSqueal) SoundOnset(AssetVfs vfs, Mode mode, float mph, float grip, SimRate rate)
    {
        var r = new Run(vfs, mode, rate, mph * Mph, grip);
        float t0 = r.T, squeal = float.NaN, skid = float.NaN, at = float.NaN;
        while (r.T - t0 < 3)
        {
            r.Tick(1, 1, mph * Mph);
            if (float.IsNaN(squeal) && r.Squeal) { squeal = r.T - t0; at = r.V.Steer; }
            if (float.IsNaN(skid) && r.Skid) skid = r.T - t0;
        }
        return (squeal, skid, at);
    }

    /// <summary>
    /// I76_KBD_STEER_SWEEP=press: one key press of 0.1 to 1 s at 35 and 55 mph on dirt (grip 1.9) with the A01 car (vppa01, whose grip gives
    /// way at about 0.6 of lock) and the Piranha: whether it squeals, and how far the heading and the path have turned half a second after the release.
    /// </summary>
    [Fact]
    public void PressResponse()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_KBD_STEER_SWEEP") != "press") return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var a = KeyStick.Default;
        var modes = new List<Mode> { Mode.Original, Mode.AssistedKeys };
        foreach (var cap in new[] { 25f, 30f }) modes.Add(Mode.Assist(a with { LatCap = cap }, $"lat cap {cap}"));
        modes.Add(Mode.Assist(a with { LatCap = 25, CentreTau = 0.05f }, "cap 25 ctr .05"));
        if (!AssetVfs.Addon.ContainsKey("dbgop.vcf"))
            AssetVfs.Addon["dbgop.vcf"] = I76.Sim.Trip.DebugCar.Build(I76.Sim.Trip.TripCar.FromVcf(vfs.Read("vppirna1.vcf")), I76.Sim.Trip.PartsDatabase.Build(vfs), vfs).ToVcf();
        var sb = new StringBuilder();
        var cars = Environment.GetEnvironmentVariable("I76_KBD_CARS")?.Split(',') ?? ["vppa01", "vppirna1"];
        var speeds = Environment.GetEnvironmentVariable("I76_KBD_MPH") is { Length: > 0 } ms ? ms.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray() : [35f, 55f];
        foreach (var car in cars.Select(c => c + ".vcf"))
            foreach (var mph in speeds)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{car} {mph} mph, dirt: press s -> squeal? skid? heading° 0.5 s after the release (+ of it after the release)");
                sb.Append("  mode           ");
                var presses = new[] { 0.1f, 0.2f, 0.4f, 0.8f };
                foreach (var p in presses) sb.Append(CultureInfo.InvariantCulture, $" | {p,4:0.0} s          ");
                sb.AppendLine();
                foreach (var m in modes)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {m.Name,-15}");
                    foreach (var p in presses)
                    {
                        var r = new Run(vfs, m, SimRate.R20, mph * Mph, 1.9f, car);
                        float h0 = r.Heading; var v0 = new Vector2(r.V.Vel.X, r.V.Vel.Z);
                        bool sq = false;
                        int n = (int)MathF.Round(p / r.Dt), after = (int)MathF.Round(0.5f / r.Dt);
                        float hr = h0;
                        for (int i = 0; i < n + after; i++) { r.Tick(i < n ? 1 : 0, 0, mph * Mph, grip: 1.9f); sq |= r.Squeal; if (i == n - 1) hr = r.Heading; }
                        float dh = (r.Heading - h0) * 180 / MathF.PI, on = (r.Heading - hr) * 180 / MathF.PI;
                        sb.Append(CultureInfo.InvariantCulture, $" | {(sq ? "SQ" : "  ")}{(r.Slid ? "SK" : "  ")} {dh,5:0.0} +{on,4:0.0}");
                    }
                    sb.AppendLine();
                }
            }
        output.WriteLine(sb.ToString());
        if (OutDir is { } o) { Directory.CreateDirectory(o); File.WriteAllText(Path.Combine(o, "press.txt"), sb.ToString()); }
    }

    /// <summary>
    /// I76_KBD_STEER_SWEEP=cars: every car file of both installs on dirt and road at 35 and 55 mph: Assisted's grip limit (share of
    /// lock), and presses of 0.2 / 0.4 / 0.8 / 1.2 s on Assisted, Assisted Free and the original's ramp: the heading turned
    /// 0.5 s after the release, and whether the press squealed.
    /// </summary>
    [Fact]
    public void EveryCar()
    {
        if (Environment.GetEnvironmentVariable("I76_KBD_STEER_SWEEP") != "cars") return;
        var sb = new StringBuilder("car           grip mph | limit | Assisted 0.2 0.4 0.8 1.2 s       | Assisted Free                   | Original\n");
        var modes = new[] { Mode.AssistedKeys, Mode.AssistedFirst, Mode.Original };
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var vfs = new AssetVfs(new GameInstall(dir));
            var only = Environment.GetEnvironmentVariable("I76_KBD_CARS")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var speeds = Environment.GetEnvironmentVariable("I76_KBD_MPH") is { Length: > 0 } ms ? ms.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray() : [35f, 55f];
            foreach (var car in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase) && (only is null || only.Contains(n[..^4], StringComparer.OrdinalIgnoreCase))).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                Vehicle probe;
                try { probe = VehicleFactory.Create(vfs, car); } catch (Exception e) { sb.AppendLine($"{car,-13} cannot build: {e.GetType().Name}"); continue; }
                if (probe.IsHeli || probe.ObjectClass != 1) { sb.AppendLine($"{car,-13} not a car (class {probe.ObjectClass}{(probe.IsHeli ? ", heli" : "")})"); continue; }
                foreach (var grip in new[] { 1.9f, 2.1f })
                    foreach (var mph in speeds)
                    {
                        string row = string.Create(CultureInfo.InvariantCulture, $"{car[..^4],-13} {grip,4} {mph,3} |");
                        try
                        {
                            var r0 = new Run(vfs, Mode.AssistedKeys, SimRate.R20, mph * Mph, grip, car);
                            float lim = KeyStick.MotionOf(r0.V, grip).BlendLock * KeyStick.Default.GripLimit;
                            row += string.Create(CultureInfo.InvariantCulture, $" {lim,5:0.00} |");
                            foreach (var m in modes)
                            {
                                foreach (var press in new[] { 0.2f, 0.4f, 0.8f, 1.2f })
                                {
                                    var r = new Run(vfs, m, SimRate.R20, mph * Mph, grip, car);
                                    float h0 = r.Heading; bool sq = false;
                                    int n = (int)MathF.Round(press / r.Dt), after = (int)MathF.Round(0.5f / r.Dt);
                                    for (int i = 0; i < n + after; i++) { r.Tick(i < n ? 1 : 0, 0, mph * Mph); if (i < n + 2) sq |= r.Squeal || r.Skid; }
                                    float dh = (r.Heading - h0) * 180 / MathF.PI;
                                    if (float.IsNaN(dh) || float.IsNaN(r.V.Steer)) row += "  NaN ";
                                    else row += string.Create(CultureInfo.InvariantCulture, $" {dh,4:0}{(sq ? "*" : " ")}");
                                }
                                row += " |";
                            }
                        }
                        catch (Exception e)
                        {
                            // a car too slow for the speed: its limit there all the same, from its numbers
                            var still = VehicleFactory.Create(vfs, car);
                            still.Vel = new Vector3(0, 0, mph * Mph);
                            row += string.Create(CultureInfo.InvariantCulture, $" {KeyStick.MotionOf(still, grip).BlendLock * KeyStick.Default.GripLimit,5:0.00} | {e.Message.Split('\n')[0]}");
                        }
                        sb.AppendLine(row);
                    }
            }
        }
        output.WriteLine(sb.ToString());
        if (OutDir is { } o) { Directory.CreateDirectory(o); File.WriteAllText(Path.Combine(o, "cars.txt"), sb.ToString()); }
    }

    /// <summary>I76_KBD_STEER_SWEEP=sound: when the tyre sounds start under each mode, held keys, the slalom and the course.</summary>
    [Fact]
    public void SoundSweep()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_KBD_STEER_SWEEP") != "sound") return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var sb = new StringBuilder();
        var modes = new List<Mode> { Mode.Original, Mode.AssistedKeys, Mode.AssistedNoCs, Mode.AssistedFirst, Mode.Stick };
        foreach (var grip in new[] { 1.9f, 2.1f, 1.5f })
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"grip {grip}: key held, s to squeal (steer then) / s to skid, at 20 35 50 70 mph");
            foreach (var m in modes)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {m.Name,-14}");
                foreach (var mph in new[] { 20f, 35f, 50f, 70f })
                {
                    var (q, k, at) = SoundOnset(vfs, m, mph, grip, SimRate.R20);
                    sb.Append(CultureInfo.InvariantCulture, $" | {q,5:0.00} ({at,4:0.00}) / {k,5:0.00}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("  share of ticks squealing / skidding: slalom at 40 mph (keys every 0.5 s); the course (keyboard driver, 56 mph and 16 mph)");
            foreach (var m in modes)
            {
                var sl = SlalomRun(vfs, m, SimRate.R20, grip: grip); var c = CourseRun(vfs, m, SimRate.R20, grip: grip);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name,-14} | slalom {sl.SquealShare,5:P0} / {sl.SkidShare,5:P0} | course {c.SquealShare,5:P0} / {c.SkidShare,5:P0} (rms {c.RmsErr[0]:0.00} m)");
            }
            sb.AppendLine("  90° turn time / m on at 35, 50, 70 mph on this ground; U-turn 15 mph; slide s / turn");
            foreach (var m in modes)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {m.Name,-14}");
                foreach (var mph in new[] { 35f, 50f, 70f }) { var t = TurnRun(vfs, m, mph, 90, SimRate.R20, grip: grip); sb.Append(CultureInfo.InvariantCulture, $" | {t.Time,5:0.00} {t.Advance,5:0.0}"); }
                var u = TurnRun(vfs, m, 15, 180, SimRate.R20, grip: grip);
                var sl = SlideRun(vfs, m, SimRate.R20, grip: grip);
                sb.AppendLine(CultureInfo.InvariantCulture, $" | {u.Time,5:0.00} | {sl.Seconds,5:0.00} {sl.TurnAfter,4:0}");
            }
        }
        output.WriteLine(sb.ToString());
    }

    public sealed record Slalom(Mode Mode, float SwingDeg, float YawAmp, bool Slid, float SquealShare = 0, float SkidShare = 0);

    /// <summary>The keys (or the stick at full lock) swapped every <paramref name="half"/> seconds at 40 mph; the heading's swing (peak to peak) and the largest yaw rate over the last cycles.</summary>
    private static Slalom SlalomRun(AssetVfs vfs, Mode mode, SimRate rate, float half = 0.5f, float mph = 40, float grip = 2f)
    {
        var r = new Run(vfs, mode, rate, mph * Mph, grip);
        int squeal = 0, skid = 0, ticks = 0;
        int n = (int)MathF.Round(half / r.Dt), cycles = 8;
        float minH = float.MaxValue, maxH = float.MinValue, yawAmp = 0;
        for (int c = 0; c < cycles; c++)
        {
            int key = c % 2 == 0 ? 1 : -1;
            int len = c == 0 ? n / 2 : n;   // half a swing first, so the swings centre on the line
            for (int i = 0; i < len; i++)
            {
                r.Tick(key, key, mph * Mph);
                ticks++; if (r.Squeal) squeal++; if (r.Skid) skid++;
                if (c >= cycles - 4)
                {
                    minH = MathF.Min(minH, r.Heading); maxH = MathF.Max(maxH, r.Heading);
                    yawAmp = MathF.Max(yawAmp, MathF.Abs(r.Yaw));
                }
            }
        }
        return new Slalom(mode, (maxH - minH) * 180 / MathF.PI, yawAmp, r.Slid, squeal / (float)ticks, skid / (float)ticks);
    }

    [Fact]
    public void SharpTurnsOnTheKeysComeCloseToTheStick()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var modes = new[] { Mode.Original, Mode.AssistedKeys, Mode.AssistedNoCs, Mode.AssistedFirst, Mode.Stick };
        var sb = new StringBuilder("turn             mode             | time s | wide m | side m | over ° | exit mph | slid\n");
        var csv = OutDir is null ? null : new StringBuilder("mode,mph,turn,t,x,z,steer,yaw\n");
        var turns = new List<Turn>();
        foreach (var (mph, deg) in new[] { (30f, 90f), (50f, 90f), (70f, 90f), (15f, 180f), (25f, 180f) })
            foreach (var m in modes)
            {
                var t = TurnRun(vfs, m, mph, deg, SimRate.R20, csv);
                turns.Add(t);
                sb.AppendLine(CultureInfo.InvariantCulture, $"{deg,3}° at {mph,2} mph  {m.Name,-16} | {t.Time,6:0.00} | {t.Advance,6:0.0} | {t.Lateral,6:0.0} | {t.Overshoot,6:0} | {t.ExitSpeed,8:0.0} | {(t.Slid ? "yes" : "no")}{(t.Done ? "" : " (never turned)")}");
            }
        sb.AppendLine("slalom 40 mph, 0.5 s a side       | swing ° | yaw rad/s | slid");
        foreach (var m in modes)
        {
            var s = SlalomRun(vfs, m, SimRate.R20);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name,-16}                 | {s.SwingDeg,7:0.0} | {s.YawAmp,9:0.00} | {(s.Slid ? "yes" : "no")}");
        }
        // a slide provoked with the handbrake, every key let go: how long the slide model holds and how far the car turns on
        sb.AppendLine("slide at 40 mph, handbrake + left to 20° slip, let go | s to grip | slip at release ° | turn after ° | spun");
        var slideCsv = new StringBuilder("mode,t,steer,yaw,slip,sliding\n");
        foreach (var m in modes)
        {
            var sl = SlideRun(vfs, m, SimRate.R20, csv: slideCsv);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name,-16}                              | {sl.Seconds,9:0.00} | {sl.SlipAtRelease,17:0} | {sl.TurnAfter,12:0} | {(sl.Spun ? "yes" : "no")}");
        }
        if (OutDir is { } so) { Directory.CreateDirectory(so); File.WriteAllText(Path.Combine(so, "slides.csv"), slideCsv.ToString()); }
        // braking to 30 mph on the straight, then the turn (what a driver does with the lock cut at speed)
        sb.AppendLine("brake to 30 mph, then 90°           | from 50 mph: s, m on | from 70 mph: s, m on");
        foreach (var m in modes)
        {
            var b50 = BrakeThenTurn(vfs, m, 50, 30, SimRate.R20); var b70 = BrakeThenTurn(vfs, m, 70, 30, SimRate.R20);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name,-16}                    | {b50.Time,5:0.00} {b50.Advance,6:0.0}       | {b70.Time,5:0.00} {b70.Advance,6:0.0}");
        }
        // the turn squeal (the grip model's blend) on dirt, key held: Assisted holds its lock short of it for the press's first half second
        foreach (var mph in new[] { 35f, 50f, 70f })
        {
            var sq = SoundOnset(vfs, Mode.AssistedKeys, mph, 1.9f, SimRate.R20).Squeal;
            Assert.True(float.IsNaN(sq) || sq > KeyStick.Default.LimitHold, $"Assisted squeals at {mph} mph after {sq:0.00} s");
            Assert.False(float.IsNaN(SoundOnset(vfs, Mode.AssistedFirst, mph, 1.9f, SimRate.R20).Squeal));
        }
        // Assisted's countersteer gets out of the slide much sooner than letting go without it
        var slides = modes.ToDictionary(m => m, m => SlideRun(vfs, m, SimRate.R20));
        Assert.True(slides[Mode.AssistedKeys].Seconds < slides[Mode.AssistedNoCs].Seconds / 2,
            $"to grip: Assisted {slides[Mode.AssistedKeys].Seconds:0.00} s, without the countersteer {slides[Mode.AssistedNoCs].Seconds:0.00} s");
        // the same at 60 Hz: the ramp is by time
        foreach (var m in new[] { Mode.Original, Mode.AssistedKeys, Mode.Stick })
        {
            var t = TurnRun(vfs, m, 50, 90, SimRate.FromLabel(60));
            sb.AppendLine(CultureInfo.InvariantCulture, $" 90° at 50 mph 60 Hz {m.Name,-13} | {t.Time,6:0.00} | {t.Advance,6:0.0} | {t.Lateral,6:0.0} | {t.Overshoot,6:0} | {t.ExitSpeed,8:0.0} | {(t.Slid ? "yes" : "no")}");
        }
        output.WriteLine(sb.ToString());
        if (OutDir is { } o) { Directory.CreateDirectory(o); File.WriteAllText(Path.Combine(o, "turns.csv"), csv!.ToString()); File.WriteAllText(Path.Combine(o, "turns.txt"), sb.ToString()); }
        // Assisted turns within a few tenths of a second of the stick; the original's runs wide
        foreach (var g in turns.GroupBy(t => (t.Mph, t.Angle)))
        {
            var stick = g.Single(t => t.Mode == Mode.Stick);
            var orig = g.Single(t => t.Mode == Mode.Original);
            var resp = g.Single(t => t.Mode == Mode.AssistedKeys);
            Assert.True(resp.Done && stick.Done);
            Assert.True(resp.Time < orig.Time && resp.Advance < orig.Advance, $"{g.Key}: Assisted {resp.Time:0.00} s / {resp.Advance:0.0} m, Original {orig.Time:0.00} s / {orig.Advance:0.0} m");
            Assert.InRange(resp.Time - stick.Time, -0.01f, 0.35f);
        }
    }

    /// <summary>The course for precision, as points 0.25 m apart: (x, z), the speed wanted there, and its part (0 straight, 1 bend, 2 lane change, 3 tight turn).</summary>
    private sealed class Course
    {
        public readonly List<(Vector2 P, float Speed, int Part)> Pts = [];
        private Vector2 _at;
        private float _heading;   // + turns left; 0 along +z, the car's forward

        private static Vector2 Dir(float h) => new(-MathF.Sin(h), MathF.Cos(h));
        private static Vector2 Left(float h) => Dir(h + MathF.PI / 2);
        private void Add(Vector2 p, float speed, int part) => Pts.Add((p, speed, part));

        public Course Straight(float len, float speed, int part = 0)
        {
            for (float s = 0.25f; s <= len; s += 0.25f) Add(_at + Dir(_heading) * s, speed, part);
            _at += Dir(_heading) * len;
            return this;
        }

        public Course Arc(float radius, float angle, float speed, int part)
        {
            // angle + is a left turn
            float len = MathF.Abs(angle) * radius, h0 = _heading;
            var centre = _at + Left(h0) * MathF.Sign(angle) * radius;
            for (float s = 0.25f; s <= len; s += 0.25f)
            {
                float h = h0 + MathF.Sign(angle) * s / radius;
                Add(centre - Left(h) * MathF.Sign(angle) * radius, speed, part);
            }
            _heading = h0 + angle;
            _at = Pts[^1].P;
            return this;
        }

        public Course LaneChange(float offset, float len, float speed, int part)
        {
            // a half-cosine to the side (+ to the left) over len
            var f = Dir(_heading);
            var l = Left(_heading);
            for (float s = 0.25f; s <= len; s += 0.25f)
                Add(_at + f * s + l * (offset * (1 - MathF.Cos(MathF.PI * s / len)) / 2), speed, part);
            _at += f * len + l * offset;
            return this;
        }

        public static Course Standard() => new Course()
            .Straight(60, 25)
            .Arc(110, MathF.PI / 2, 25, 1).Straight(40, 25, 1)
            .Straight(60, 25)
            .LaneChange(-3.5f, 35, 25, 2).Straight(40, 25, 2)
            .Straight(100, 7)
            .Arc(10, -MathF.PI, 7, 3).Straight(20, 7, 3)
            .Straight(30, 7);
    }

    public sealed record CourseResult(Mode Mode, int Hz, float[] RmsErr, float[] MaxErr, float[] Jitter, float[] Time, bool Finished, float PressesPerSecond, string Csv, float SquealShare = 0, float SkidShare = 0);

    /// <summary>One run through the course. The arrays are indexed 0 for the whole course, 1..3 its parts.</summary>
    private static CourseResult CourseRun(AssetVfs vfs, Mode mode, SimRate rate, float delaySeconds = 0.1f, float deadband = 0.1f, float grip = 2f)
    {
        var course = Course.Standard();
        var r = new Run(vfs, mode, rate, 25, grip);   // the course's speed at its start
        float dt = r.Dt;
        int delay = System.Math.Max(1, (int)MathF.Round(delaySeconds / dt));
        var decisions = new Queue<(int Key, float Analog)>();
        for (int i = 0; i < delay; i++) decisions.Enqueue((0, 0));
        int held = 0, presses = 0, squeal = 0, skid = 0;
        float integral = 0;
        int k = 0, n = course.Pts.Count;
        var err = new List<(int Part, float E, float Yaw)>();
        var csv = new StringBuilder("t,x,z,err,part,steer,yaw,key\n");
        bool finished = false;
        for (int tick = 0; tick < (int)(120 / dt); tick++)
        {
            var p2 = new Vector2((float)r.V.Pos.X, (float)r.V.Pos.Z);
            // the nearest course point, searched forward from the last
            float best = float.MaxValue;
            for (int j = k; j < System.Math.Min(n, k + 400); j++)
            {
                float d = Vector2.DistanceSquared(course.Pts[j].P, p2);
                if (d < best) { best = d; k = j; }
            }
            if (k >= n - 2) { finished = true; break; }
            var (cp, wantSpeed, part) = course.Pts[k];
            var fwd2 = new Vector2(r.V.Rot.Forward.X, r.V.Rot.Forward.Z);
            var tangent = Vector2.Normalize(course.Pts[k + 1].P - cp);
            float lateral = Vector2.Dot(p2 - cp, new Vector2(-tangent.Y, tangent.X));
            float speed = r.Speed;
            // pure pursuit: the yaw rate that arcs onto the point L ahead (+ to the left)
            float look = 4 + 0.5f * speed;
            var target = course.Pts[System.Math.Min(n - 1, k + (int)(look * 4))].P - p2;
            float alpha = MathF.Atan2(fwd2.X * target.Y - fwd2.Y * target.X, Vector2.Dot(fwd2, target));
            float want = 2 * speed * MathF.Sin(alpha) / look;
            float e = want - r.Yaw;
            // the keyboard driver: presses toward the yaw wanted once it is a deadband off, and lets go once it is there
            int key = held != 0 && e * held > 0 ? held : e > deadband ? 1 : e < -deadband ? -1 : 0;
            if (key != 0 && key != held) presses++;
            held = key;
            // the stick driver: the steer that gives that yaw below the grip, and an integrator for the rest
            float plant = MathF.Max(0.5f, speed * r.V.P.SteerGain);
            integral = System.Math.Clamp(integral + e / plant * dt * 2, -1f, 1f);
            decisions.Enqueue((key, System.Math.Clamp(want / plant + integral, -1f, 1f)));
            var (dk, da) = decisions.Dequeue();
            r.Tick(dk, da, wantSpeed);
            if (r.Squeal) squeal++;
            if (r.Skid) skid++;
            err.Add((part, lateral, r.Yaw));
            csv.Append(CultureInfo.InvariantCulture, $"{r.T:0.000},{r.V.Pos.X:0.00},{r.V.Pos.Z:0.00},{lateral:0.000},{part},{r.V.Steer:0.0000},{r.Yaw:0.0000},{dk}\n");
        }
        // jitter: the yaw rate against its own 0.5 s running mean (centred), RMS
        int w = System.Math.Max(1, (int)MathF.Round(0.25f / dt));
        float[] rms = new float[4], max = new float[4], jit = new float[4], time = new float[4];
        int[] cnt = new int[4];
        for (int i = 0; i < err.Count; i++)
        {
            float mean = 0; int m = 0;
            for (int j = System.Math.Max(0, i - w); j <= System.Math.Min(err.Count - 1, i + w); j++) { mean += err[j].Yaw; m++; }
            mean /= m;
            void Add(int pt)
            {
                rms[pt] += err[i].E * err[i].E;
                max[pt] = MathF.Max(max[pt], MathF.Abs(err[i].E));
                jit[pt] += (err[i].Yaw - mean) * (err[i].Yaw - mean);
                time[pt] += dt;
                cnt[pt]++;
            }
            Add(0);
            if (err[i].Part != 0) Add(err[i].Part);
        }
        for (int i = 0; i < 4; i++) { rms[i] = MathF.Sqrt(rms[i] / System.Math.Max(1, cnt[i])); jit[i] = MathF.Sqrt(jit[i] / System.Math.Max(1, cnt[i])); }
        return new CourseResult(mode, rate.Label, rms, max, jit, time, finished, presses / MathF.Max(r.T, 1e-3f), csv.ToString(), squeal / (float)System.Math.Max(1, err.Count), skid / (float)System.Math.Max(1, err.Count));
    }

    private static string Row(CourseResult r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.Mode.Name,-16} {r.Hz,2} | {r.PressesPerSecond,6:0.0} | {r.RmsErr[0],5:0.00} {r.MaxErr[0],5:0.00} {r.Jitter[0],5:0.00} {r.Time[0],5:0.0} | {r.RmsErr[1],5:0.00} {r.MaxErr[1],5:0.00} {r.Jitter[1],5:0.00} | {r.RmsErr[2],5:0.00} {r.MaxErr[2],5:0.00} {r.Jitter[2],5:0.00} | {r.RmsErr[3],5:0.00} {r.MaxErr[3],5:0.00} {r.Jitter[3],5:0.00} {r.Time[3],5:0.0}{(r.Finished ? "" : " DNF")}");
    private const string CourseHeader = "mode             Hz | keys/s | whole: rms  max  jit  time | bend: rms  max  jit | lane: rms  max  jit | tight: rms  max  jit  time";

    [Fact]
    public void AssistedHoldsTheLineAtSpeed()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var table = new StringBuilder(CourseHeader + "\n");
        var results = new List<CourseResult>();
        foreach (var hz in new[] { 20, 60 })
            foreach (var m in new[] { Mode.Original, Mode.AssistedKeys, Mode.AssistedFirst, Mode.Stick })
            {
                var r = CourseRun(vfs, m, SimRate.FromLabel(hz));
                results.Add(r);
                table.AppendLine(Row(r));
                if (OutDir is { } o) { Directory.CreateDirectory(o); File.WriteAllText(Path.Combine(o, $"course-{m.Name.Replace(' ', '_').Replace('/', '_')}-{hz}.csv"), r.Csv); }
            }
        foreach (var delay in new[] { 0.05f, 0.2f })
            foreach (var m in new[] { Mode.Original, Mode.AssistedKeys })
                table.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{delay:0.00} s late: ") + Row(CourseRun(vfs, m, SimRate.R20, delay)));
        output.WriteLine(table.ToString());
        if (OutDir is { } od) File.WriteAllText(Path.Combine(od, "course.txt"), table.ToString());
        // over the course and in the sweeping bend the keyboard driver keeps closer to the line than on the original's ramp (the lane change is about even)
        foreach (var hz in new[] { 20, 60 })
        {
            var o = results.Single(r => r.Mode == Mode.Original && r.Hz == hz);
            var s = results.Single(r => r.Mode == Mode.AssistedKeys && r.Hz == hz);
            Assert.True(o.Finished && s.Finished);
            foreach (int part in new[] { 0, 1 })
                Assert.True(s.RmsErr[part] <= o.RmsErr[part], $"{hz} Hz, part {part}: Assisted {s.RmsErr[part]:0.00} m, Original {o.RmsErr[part]:0.00} m");
        }
    }

    public sealed record Flick(Mode Mode, float Mph, float Lead, float Seconds, float Turned, float YawRate, float TurnedAfter, float YawAfter);

    /// <summary>
    /// A handbrake flick at <paramref name="mph"/>: the left key alone for <paramref name="lead"/> s, then the handbrake with it for
    /// <paramref name="seconds"/> (the stick at full lock throughout). Turned: the heading's turn from the first press to the end of
    /// the flick (degrees); yaw rate: the car's at the end (rad/s), what a car leaving a ramp then takes into the air. Then
    /// <paramref name="after"/> s with the handbrake let go, the key held (<paramref name="keyAfter"/>) or not: how far the
    /// heading turns on, and the yaw rate at its end.
    /// </summary>
    private static Flick FlickRun(AssetVfs vfs, Mode mode, float mph, float lead, float seconds, SimRate rate, float after = 0, bool keyAfter = false)
    {
        var r = new Run(vfs, mode, rate, mph * Mph);
        for (int i = 0; i < (int)(0.5f / r.Dt); i++) r.Tick(0, 0, mph * Mph);
        float turned = 0, prev = r.Heading;
        void Step(int key, bool hb)
        {
            r.Tick(key, key, mph * Mph, handbrake: hb);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
        }
        for (int i = 0; i < (int)MathF.Round(lead / r.Dt); i++) Step(1, false);
        for (int i = 0; i < (int)MathF.Round(seconds / r.Dt); i++) Step(1, true);
        float at = turned * 180 / MathF.PI, yaw = r.V.AngVel.Y;
        for (int i = 0; i < (int)MathF.Round(after / r.Dt); i++) Step(keyAfter ? 1 : 0, false);
        return new Flick(mode, mph, lead, seconds, at, yaw, turned * 180 / MathF.PI - at, r.V.AngVel.Y);
    }

    /// <summary>Assisted as 0.13.0 had it: the countersteer and the hold also with the handbrake held.</summary>
    private static readonly Mode Assisted0130 = Mode.Assist(KeyStick.Default with { Handbrake = false }, "Assisted 0.13.0");

    /// <summary>
    /// The stunt drivers' handbrake flick (the handbrake and a key just before a jump, so the car takes its spin into the air)
    /// at 50, 60 and 70 mph, with the key pressed with the handbrake or 0.3 s before it. At the end of the flick (the takeoff)
    /// Assisted has turned the car and left it spinning at least as much as the original's keys, and with the handbrake held
    /// half a second or more its yaw rate is within 60% of a stick's at full lock (the key's ease-in is the difference); with the
    /// handbrake let go and the key held on, it keeps spinning at least as fast as on the original's keys. Hands off, the
    /// countersteer recovers the slide, as it is meant to (printed, not held to the original's).
    /// </summary>
    [Fact]
    public void HandbrakeFlickSpins()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var modes = new[] { Mode.Original, Assisted0130, Mode.AssistedKeys, Mode.Stick };
        var sb = new StringBuilder("handbrake flick: s key alone + s with the handbrake | turned ° / yaw rad/s at the end | 0.3 s on, handbrake let go: key held | both let go (turned on °, yaw)\n");
        var fails = new List<string>();
        foreach (var mph in new[] { 50f, 60f, 70f })
            foreach (var (lead, secs) in new[] { (0f, 0.3f), (0f, 0.5f), (0f, 0.8f), (0f, 1.2f), (0.3f, 0.3f), (0.3f, 0.5f) })
            {
                var flicks = modes.ToDictionary(m => m, m => FlickRun(vfs, m, mph, lead, secs, SimRate.R20));
                var keyHeld = modes.ToDictionary(m => m, m => FlickRun(vfs, m, mph, lead, secs, SimRate.R20, 0.3f, true));
                foreach (var m in modes)
                {
                    var (f, k, n) = (flicks[m], keyHeld[m], FlickRun(vfs, m, mph, lead, secs, SimRate.R20, 0.3f, false));
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{mph} mph {lead:0.0}+{secs:0.0} s {m.Name,-16} | {f.Turned,6:0.0} {f.YawRate,5:0.00} | {k.TurnedAfter,6:0.0} {k.YawAfter,5:0.00} | {n.TurnedAfter,6:0.0} {n.YawAfter,5:0.00}");
                }
                string at = string.Create(CultureInfo.InvariantCulture, $"{mph} mph, {lead:0.0} s + {secs:0.0} s");
                Flick o = flicks[Mode.Original], s = flicks[Mode.AssistedKeys], st = flicks[Mode.Stick];
                if (s.Turned < o.Turned || s.YawRate < o.YawRate) fails.Add($"{at}: Assisted {s.Turned:0.0}° {s.YawRate:0.00} rad/s, Original {o.Turned:0.0}° {o.YawRate:0.00}");
                if (secs >= 0.5f && s.YawRate < 0.6f * st.YawRate) fails.Add($"{at}: Assisted {s.YawRate:0.00} rad/s, the stick {st.YawRate:0.00}");
                if (keyHeld[Mode.AssistedKeys].YawAfter < keyHeld[Mode.Original].YawAfter)
                    fails.Add($"{at}, the handbrake let go, the key held: Assisted {keyHeld[Mode.AssistedKeys].YawAfter:0.00} rad/s, Original {keyHeld[Mode.Original].YawAfter:0.00}");
            }
        output.WriteLine(sb.ToString());
        Assert.True(fails.Count == 0, string.Join("\n", fails));
    }

    public sealed record Jump(Mode Mode, float TakeoffYaw, float ApexYaw, float Turned);

    /// <summary>
    /// At <paramref name="mph"/> toward a 3 m kicker (15 m long): the handbrake and the left key (the stick at full lock) from
    /// <paramref name="from"/> m before its lip, the handbrake let go <paramref name="early"/> m before the lip (the key held on),
    /// everything let go once the car is in the air. The car's yaw rate at takeoff and at the top of the flight (rad/s), and the
    /// heading's turn from the handbrake to the top (degrees).
    /// </summary>
    private static Jump JumpRun(AssetVfs vfs, Mode mode, float mph, float from, float early = 0)
    {
        const float z0 = 40, z1 = 55;
        var k = new Kicker(z0, z1, 3);
        var r = new Run(vfs, mode, SimRate.R20, mph * Mph, ground: k);
        k.On = true;
        float turned = 0, prev = r.Heading, takeoff = float.NaN;
        bool started = false;
        while (r.T < 20)
        {
            double z = r.V.Pos.Z;
            bool flying = !float.IsNaN(takeoff);
            bool spin = !flying && z >= z1 - from;
            if (spin && !started) { started = true; prev = r.Heading; }
            float vy = r.V.Vel.Y;
            r.Tick(spin ? 1 : 0, spin ? 1 : 0, mph * Mph, handbrake: spin && z < z1 - early);
            turned += MathF.Atan2(MathF.Sin(r.Heading - prev), MathF.Cos(r.Heading - prev)); prev = r.Heading;
            bool air = (r.V.Flags & VehicleFlags.Airborne) != 0;
            if (air && !flying) takeoff = r.V.AngVel.Y;
            if (flying && (!air || r.V.Vel.Y <= 0)) break;
        }
        Assert.False(float.IsNaN(takeoff), "the car never left the kicker");
        return new Jump(mode, takeoff, r.V.AngVel.Y, turned * 180 / MathF.PI);
    }

    /// <summary>
    /// A handbrake spin taken off a kicker at 60 mph keeps its yaw rate through the air (no slide in the air, so no countersteer),
    /// and Assisted takes off spinning at least as fast as the original's keys: the handbrake held to the lip, or let go 4 m
    /// before it with the key held on (the handbrake from 8 to 20 m before the lip, 0.3 to 0.8 s).
    /// </summary>
    [Fact]
    public void HandbrakeSpinCarriesIntoTheAir()
    {
        if (InstallDir is not { } dir) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        var sb = new StringBuilder("kicker at 60 mph: handbrake + key from m before the lip | yaw rad/s at takeoff, at the top | turned ° to the top\n");
        foreach (var (from, early) in new[] { (8f, 0f), (12f, 0f), (16f, 0f), (20f, 0f), (12f, 4f), (16f, 6f), (20f, 6f) })
        {
            var jumps = new[] { Mode.Original, Assisted0130, Mode.AssistedKeys, Mode.Stick }.ToDictionary(m => m, m => JumpRun(vfs, m, 60, from, early));
            foreach (var j in jumps.Values)
                sb.AppendLine(CultureInfo.InvariantCulture, $"from {from,2} m, handbrake let go {early} m before | {j.Mode.Name,-16} | {j.TakeoffYaw,5:0.00} {j.ApexYaw,5:0.00} | {j.Turned,6:0.0}");
            var s = jumps[Mode.AssistedKeys];
            Assert.InRange(s.ApexYaw - s.TakeoffYaw, -0.01f, 0.01f);
            Assert.True(s.TakeoffYaw >= jumps[Mode.Original].TakeoffYaw, $"from {from} m, let go {early} m before: Assisted {s.TakeoffYaw:0.00} rad/s, Original {jumps[Mode.Original].TakeoffYaw:0.00}");
        }
        output.WriteLine(sb.ToString());
    }

    /// <summary>I76_KBD_STEER_OUT=dir: steer against time for three key patterns, Original and Assisted (on the flat, no slide), at 20 and 60 Hz.</summary>
    [Fact]
    public void SteerTraces()
    {
        if (OutDir is not { } o) return;
        Directory.CreateDirectory(o);
        var patterns = new (string Name, Func<float, int> Key)[]
        {
            ("hold 2 s", t => t < 2 ? 1 : 0),
            ("taps at 5 Hz half the time", t => t < 3 && t % 0.2f < 0.1f ? 1 : 0),
            ("left 1 s then right 2 s", t => t < 1 ? 1 : t < 3 ? -1 : 0),
        };
        var sb = new StringBuilder("pattern,hz,mode,t,steer\n");
        foreach (var (name, key) in patterns)
            foreach (var hz in new[] { 20, 60 })
            {
                float dt = SimRate.FromLabel(hz).TickSeconds;
                foreach (var mode in new[] { Mode.Original, Mode.AssistedKeys })
                {
                    var ramp = new DriverState(); var stick = new KeyStick(mode.Tuning);
                    float thr = 0, st = 0;
                    sb.Append(CultureInfo.InvariantCulture, $"{name},{hz},{mode.Name},0,0\n");
                    for (int i = 0; i * dt < 4; i++)
                    {
                        int k = key(i * dt);
                        var input = new CarInput { Buttons = k > 0 ? CarButtons.SteerLeft : k < 0 ? CarButtons.SteerRight : CarButtons.None };
                        if (mode.Assisted) input = stick.Apply(input, dt, null);
                        KeyRamp.Apply(ref ramp, input, dt, null, ref thr, ref st);
                        sb.Append(CultureInfo.InvariantCulture, $"{name},{hz},{mode.Name},{(i + 1) * dt:0.0000},{st:0.0000}\n");
                    }
                }
            }
        File.WriteAllText(Path.Combine(o, "steer-traces.csv"), sb.ToString());
    }
}
