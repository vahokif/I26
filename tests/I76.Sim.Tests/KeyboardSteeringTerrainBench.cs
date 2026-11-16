using System.Globalization;
using System.Numerics;
using System.Text;
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
/// Keyboard Steering on a mission's own ground (the 1997 model through a session, the terrain's slopes, bumps and surfaces):
/// scripted small corrections and a keyboard driver holding a heading (I76_KBD_TERRAIN=1), and the replay of a recording made
/// with I76_INPUT_LOG through every mode (I76_INPUT_REPLAY=file[@from-tick]). Each prints the share of time the turn squeal and
/// the skid play, where they start, and Keyboard Steering's parts there; I76_KBD_STEER_OUT=dir also writes the traces.
/// </summary>
public class KeyboardSteeringTerrainBench(ITestOutputHelper output)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? OutDir => Environment.GetEnvironmentVariable("I76_KBD_STEER_OUT") is { Length: > 0 } d ? d : null;
    private const float Mph = 0.44704f;

    /// <summary>The original's ramp (no tuning) and Assisted, and Assisted as it first was (0.25 s, no hold, the countersteer always on).</summary>
    public static readonly (string Name, KeyStick.Assist? Tuning)[] Modes =
    [
        ("Original", null), ("Assisted", KeyStick.Default),
        ("Assisted first", new KeyStick.Assist(0.25f, 0.1f, 2f, 1f, 0, float.PositiveInfinity, 0, CsSlidingOnly: false)),
    ];

    /// <summary>The modes, and candidates for Assisted.</summary>
    private static IEnumerable<(string Name, KeyStick.Assist? Tuning)> Variants() => Modes;

    /// <summary>One tick's record.</summary>
    public readonly record struct Line(int Tick, CarButtons Keys, float Ramp, float Counter, float Limit, float Sent, float Steer, float Speed,
        float Slip, float Yaw, float Load, int Surface, float Grip, bool Air, bool Slide, bool Blend, string Sound, Vector3 Pos);

    /// <summary>A car on a mission's ground in a session, its keys turned into input by one mode.</summary>
    private sealed class Drive
    {
        public readonly SimSession Session;
        public readonly Vehicle Car;
        public readonly MissionSetup Setup;
        private readonly KeyStick.Assist? _tuning;
        private readonly KeyStick _keys = new();
        public float? Cruise;
        public readonly List<Line> Lines = [];
        private readonly List<string> _sounds = [];

        public Drive(GameInstall install, AssetVfs vfs, ModelAssembler asm, string mission, string car, KeyStick.Assist? tuning, Vector2 at, Vector2 fwd, float speed)
        {
            // the F9 list's OP Piranha is built from the parts, as the menu builds it
            if (car.Equals("dbgop.vcf", StringComparison.OrdinalIgnoreCase) && !AssetVfs.Addon.ContainsKey(car))
                AssetVfs.Addon[car] = I76.Sim.Trip.DebugCar.Build(I76.Sim.Trip.TripCar.FromVcf(vfs.Read("vppirna1.vcf")), I76.Sim.Trip.PartsDatabase.Build(vfs), vfs).ToVcf();
            var w = new MissionWorld(vfs, asm, install.FindMission(mission)!);
            Setup = MissionSetup.Build(vfs, w, asm, car, script: false, rand: new CrtRand(1), seed: 1);
            Session = new SimSession(Setup, SessionOptions.SinglePlayer, SimRate.R20);
            Car = Setup.World.Player!;
            Session.AddDriver(Car);
            Setup.World.Vehicles.RemoveAll(x => x != Car);
            _tuning = tuning;
            if (tuning is not null) _keys = new KeyStick(tuning);
            Session.FrameStarting += () => { if (Cruise is { } c) Car.Throttle = System.Math.Clamp((c - Car.Speed) * 0.5f, -1f, 0.9999f); };
            Place(at, fwd, 0);
            bool off = (Car.Flags & VehicleFlags.EngineOn) == 0;
            for (int f = 0; f < 90; f++) Session.Tick([new CarInput { Commands = off && f is >= 70 and <= 80 ? CarCommands.StartEngine : CarCommands.None }]);
            if (speed <= 0) return;
            // up to the speed on the way (the wheels and the engine with it; a velocity alone is not kept), a second at it, then back to the start
            Cruise = speed;
            float settled = 0;
            for (int f = 0; f < 1200 && settled < 1; f++)
            {
                Session.Tick([default]);
                Setup.Damage?.Events.Clear();
                settled = MathF.Abs(Car.Speed - speed) < 0.03f * speed ? settled + Session.TickSeconds : 0;
            }
            Place(at, fwd, Car.Speed);
        }

        public void Place(Vector2 at, Vector2 fwd, float speed)
        {
            var f = Vector3.Normalize(new Vector3(fwd.X, 0, fwd.Y));
            Car.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
            Car.Pos = new Double3(at.X, Setup.Ground.BareHeight(at.X, at.Y) + 0.5, at.Y);
            Car.Vel = f * speed;
            Car.AngVel = Vector3.Zero;
            VehicleStep.PrimeWheels(Car, Setup.Ground);
        }

        public float Heading => MathF.Atan2(-Car.Rot.Forward.X, Car.Rot.Forward.Z);

        public void Tick(CarButtons keys, CarCommands cmd = CarCommands.None)
        {
            var input = new CarInput { Buttons = keys, Commands = cmd };
            if (_tuning is not null)
            {
                input = _keys.Apply(input, Session.TickSeconds, null, KeyStick.MotionOf(Car, Setup.Ground));
            }
            Session.Tick([input]);
            Setup.Damage?.Events.Clear();
            var m = KeyStick.MotionOf(Car, Setup.Ground);
            I76.Sim.Sound.TyreSounds.Wanted(Car, true, false, _sounds);
            string sound = string.Join(",", _sounds.Where(n => n.StartsWith("tturn") || n.StartsWith("tskid")));
            bool ramped = _tuning is not null;
            Lines.Add(new Line(Session.Ticks, keys, ramped ? _keys.LastRamp : float.NaN, ramped ? _keys.LastCountersteer : 0, ramped ? _keys.LastLimit : float.NaN,
                input.Has(CarButtons.Analog) ? input.Steer / 32767f : float.NaN, Car.Steer, Car.Speed, m.Slip * 57.29578f, Car.AngVel.Y, Car.NormalLoad,
                Car.Surface, KeyStick.SurfaceGrip(Car, Setup.Ground), (Car.Flags & VehicleFlags.Airborne) != 0, (Car.Flags & VehicleFlags.Sliding) != 0,
                (Car.Flags & VehicleFlags.Blending) != 0, sound, Car.Pos.ToVector3()));
        }
    }

    /// <summary>The share of ticks with the squeal and the skid, the first of each after <paramref name="from"/>, and what Keyboard Steering sent there.</summary>
    private static string Summary(List<Line> lines, int from = 0)
    {
        var l = lines.Skip(from).ToList();
        if (l.Count == 0) return "no ticks";
        float sq = l.Count(x => x.Sound.Contains("tturn")) / (float)l.Count, sk = l.Count(x => x.Sound.Contains("tskid")) / (float)l.Count, air = l.Count(x => x.Air) / (float)l.Count;
        var lim = l.Where(x => !float.IsNaN(x.Limit)).Select(x => x.Limit).OrderBy(x => x).ToList();
        string limits = lim.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" limit min {lim[0]:0.00} p10 {lim[lim.Count / 10]:0.00} median {lim[lim.Count / 2]:0.00}");
        // at each squeal or skid onset: the parts
        var onsets = new List<string>();
        for (int i = 1; i < l.Count && onsets.Count < 4; i++)
            if (l[i].Sound != "" && l[i - 1].Sound == "")
                onsets.Add(string.Create(CultureInfo.InvariantCulture, $"[{l[i].Sound} t{l[i].Tick} keys {l[i].Keys & (CarButtons.SteerLeft | CarButtons.SteerRight)} ramp {l[i].Ramp:0.00} cs {l[i].Counter:0.00} lim {l[i].Limit:0.00} steer {l[i].Steer:0.00} slip {l[i].Slip:0.0} load {l[i].Load:0.0} grip {l[i].Grip:0.0} {l[i].Speed / Mph:0}mph{(l[i].Air ? " air" : "")}]"));
        float rmsSteer = MathF.Sqrt(l.Average(x => x.Steer * x.Steer));
        return string.Create(CultureInfo.InvariantCulture, $"squeal {sq,5:P0} skid {sk,5:P0} air {air,4:P0} | steer rms {rmsSteer:0.00} |{limits} | {string.Join(" ", onsets)}");
    }

    /// <summary>Per steer-key press over 7 m/s: how many set off the squeal (those of 0.4 s or less, and longer), the heading's and the path's turn rate while a key is held (median rad/s).</summary>
    private static string Presses(List<Line> l)
    {
        int shortN = 0, shortSq = 0, longN = 0, longSq = 0;
        var heading = new List<float>(); var path = new List<float>();
        for (int i = 0; i < l.Count;)
        {
            var k = l[i].Keys & (CarButtons.SteerLeft | CarButtons.SteerRight);
            int j = i;
            while (j < l.Count && (l[j].Keys & (CarButtons.SteerLeft | CarButtons.SteerRight)) == k) j++;
            if (k != 0 && l[i].Speed > 7)
            {
                bool sq = l.Skip(i).Take(j - i).Any(x => x.Sound.Contains("tturn"));
                if (j - i <= 8) { shortN++; if (sq) shortSq++; } else { longN++; if (sq) longSq++; }
                for (int t = i + 1; t < j; t++)
                {
                    heading.Add(MathF.Abs(l[t].Yaw));
                    var v0 = l[t - 1].Pos; var v1 = l[t].Pos;
                    if (t + 1 < l.Count)
                    {
                        var a = new Vector2(v1.X - v0.X, v1.Z - v0.Z); var b = new Vector2(l[t + 1].Pos.X - v1.X, l[t + 1].Pos.Z - v1.Z);
                        if (a.Length() > 0.2f && b.Length() > 0.2f) path.Add(MathF.Abs(MathF.Atan2(a.X * b.Y - a.Y * b.X, Vector2.Dot(a, b))) / 0.05f);
                    }
                }
            }
            i = j;
        }
        float Med(List<float> x) { if (x.Count == 0) return float.NaN; x.Sort(); return x[x.Count / 2]; }
        // the turn going on after a release (0.5 s, degrees, mean of |.|), the yaw rate against its 0.5 s running mean (RMS), airborne spells
        var after = new List<float>();
        for (int i = 1; i < l.Count; i++)
            if ((l[i - 1].Keys & (CarButtons.SteerLeft | CarButtons.SteerRight)) != 0 && (l[i].Keys & (CarButtons.SteerLeft | CarButtons.SteerRight)) == 0 && i + 10 < l.Count && l[i].Speed > 7)
                after.Add(MathF.Abs(l.Skip(i).Take(10).Sum(x => x.Yaw) * 0.05f * 57.29578f));
        double jit = 0; int jn = 0;
        for (int i = 5; i + 5 < l.Count; i++) { float mean = l.Skip(i - 5).Take(11).Average(x => x.Yaw); jit += (l[i].Yaw - mean) * (l[i].Yaw - mean); jn++; }
        int spells = 0, longest = 0, run = 0;
        foreach (var x in l) { if (x.Air) { run++; if (run == 1) spells++; longest = System.Math.Max(longest, run); } else run = 0; }
        return string.Create(CultureInfo.InvariantCulture, $"presses ≤0.4 s squeal {shortSq}/{shortN}, longer {longSq}/{longN} | held: heading {Med(heading):0.00} path {Med(path):0.00} rad/s | turn on after release {(after.Count > 0 ? after.Average() : float.NaN):0.0}° | yaw jitter {System.Math.Sqrt(jit / System.Math.Max(1, jn)):0.00} | airborne {spells} spells, longest {longest * 0.05f:0.00} s");
    }

    private static void Csv(string name, List<Line> lines)
    {
        if (OutDir is not { } o) return;
        Directory.CreateDirectory(o);
        var sb = new StringBuilder("tick,keys,ramp,counter,limit,sent,steer,speed,slip,yaw,load,surface,grip,air,slide,blend,sound,x,y,z\n");
        foreach (var l in lines)
            sb.Append(CultureInfo.InvariantCulture, $"{l.Tick},{(int)(l.Keys & (CarButtons.SteerLeft | CarButtons.SteerRight | CarButtons.ThrottleUp | CarButtons.ThrottleDown | CarButtons.EBrake))},{l.Ramp:0.0000},{l.Counter:0.0000},{l.Limit:0.0000},{l.Sent:0.0000},{l.Steer:0.0000},{l.Speed:0.00},{l.Slip:0.00},{l.Yaw:0.0000},{l.Load:0.00},{l.Surface},{l.Grip:0.00},{(l.Air ? 1 : 0)},{(l.Slide ? 1 : 0)},{(l.Blend ? 1 : 0)},{l.Sound.Replace(',', ' ')},{l.Pos.X:0.00},{l.Pos.Y:0.00},{l.Pos.Z:0.00}\n");
        File.WriteAllText(Path.Combine(o, name.Replace('/', '_').Replace(' ', '_') + ".csv"), sb.ToString());
    }

    /// <summary>Taps of 0.05, 0.1, 0.2 and 0.3 s, left then right, a second apart, at the cruise.</summary>
    private static CarButtons Taps(int tick)
    {
        int[] lens = [1, 2, 4, 6];
        int slot = tick / 20, k = slot % 8;
        if (tick % 20 >= lens[k / 2]) return CarButtons.None;
        return k % 2 == 0 ? CarButtons.SteerLeft : CarButtons.SteerRight;
    }

    /// <summary>A keyboard driver holding the heading it starts on (bang-bang on the yaw rate, a tenth of a second late), with a gentle curve of <paramref name="curve"/> rad/s from tick 100.</summary>
    private sealed class Holder(float curve)
    {
        private float? _h0;
        private int _held;
        private readonly Queue<int> _late = new([0, 0]);

        public CarButtons Key(Drive d, int tick)
        {
            _h0 ??= d.Heading;
            float want = (tick >= 100 ? curve : 0), target = _h0.Value + want * System.Math.Max(0, tick - 100) * 0.05f;
            float err = MathF.Atan2(MathF.Sin(target - d.Heading), MathF.Cos(target - d.Heading));
            float e = want + 1.5f * err - d.Car.AngVel.Y;
            int key = _held != 0 && e * _held > 0 ? _held : e > 0.1f ? 1 : e < -0.1f ? -1 : 0;
            _held = key;
            _late.Enqueue(key);
            int k = _late.Dequeue();
            return k > 0 ? CarButtons.SteerLeft : k < 0 ? CarButtons.SteerRight : CarButtons.None;
        }
    }

    /// <summary>I76_KBD_TERRAIN=find: straight 400 m runs over each mission's ground with small bumps (the height's bend at a metre's step) and no steep slope or cliff.</summary>
    [Fact]
    public void FindBumpyRuns()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_KBD_TERRAIN") != "find") return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        foreach (var mission in new[] { "t01", "m05", "t03", "a02" })
        {
            var mw = install.FindMission(mission);
            if (mw is null) continue;
            var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, mw), asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), seed: 1);
            var g = setup.Ground;
            var p0 = setup.World.Player!.Pos;
            var found = new List<(float Rough, Vector2 At, Vector2 Fwd, string Surf)>();
            for (int dx = -2000; dx <= 2000; dx += 250)
                for (int dz = -2000; dz <= 2000; dz += 250)
                    for (int a = 0; a < 8; a++)
                    {
                        var fwd = new Vector2(MathF.Sin(a * MathF.PI / 4), MathF.Cos(a * MathF.PI / 4));
                        var at = new Vector2((float)p0.X + dx, (float)p0.Z + dz);
                        float bend = 0, maxSlope = 0; double prev = g.BareHeight(at.X, at.Y), prevD = 0;
                        var surf = new int[8];
                        for (int k = 1; k <= 400; k++)
                        {
                            var q = at + fwd * k;
                            double h = g.BareHeight(q.X, q.Y), d = h - prev;
                            if (k > 1) bend += (float)((d - prevD) * (d - prevD));
                            maxSlope = MathF.Max(maxSlope, MathF.Abs((float)d));
                            prev = h; prevD = d;
                            surf[g.SurfaceId(q.X, q.Y) & 7]++;
                        }
                        if (maxSlope > 0.25f) continue;
                        found.Add((MathF.Sqrt(bend / 400), at, fwd, string.Join(" ", Enumerable.Range(0, 8).Where(i => surf[i] > 0).Select(i => $"{i}:{surf[i]}"))));
                    }
            foreach (var f in found.OrderByDescending(f => f.Rough).Take(5))
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{mission} bend {f.Rough:0.000} at {f.At.X:0},{f.At.Y:0} fwd {f.Fwd.X:0.00},{f.Fwd.Y:0.00} surfaces {f.Surf}"));
            var med = found.OrderBy(f => f.Rough).ToList();
            if (med.Count > 0) output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{mission}: {found.Count} runs, median bend {med[med.Count / 2].Rough:0.000}"));
        }
    }

    [Fact]
    public void CorrectionsOnMissionGround()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_KBD_TERRAIN") != "1") return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var sb = new StringBuilder();
        // T01's road out of the start, and m05's open desert
        var places = new (string Mission, Vector2 At, Vector2 Fwd)[] { ("t01", new(3361.56f, 35906.2f), new(0.566f, 0.824f)), ("m05", new(3300, 48950), new(0, 1)) };
        foreach (var (mission, at, fwd) in places)
            foreach (var car in new[] { "vppirna1.vcf", "vdstag3.vcf" })
                foreach (var mph in new[] { 35f, 55f })
                    foreach (var (script, ticks) in new[] { ("taps", 160), ("hold", 300), ("curve", 300) })
                        foreach (var (mode, tuning) in Modes)
                        {
                            var d = new Drive(install, vfs, asm, mission, car, tuning, at, fwd, mph * Mph) { Cruise = mph * Mph };
                            var holder = new Holder(script == "curve" ? 0.12f : 0);
                            for (int t = 0; t < ticks; t++) d.Tick(script == "taps" ? Taps(t) : holder.Key(d, t));
                            sb.AppendLine(CultureInfo.InvariantCulture, $"{mission} {car[..^4],-9} {mph}mph {script,-5} {mode,-12} | {Summary(d.Lines)}");
                            Csv($"terrain-{mission}-{car[..^4]}-{mph}-{script}-{mode}", d.Lines);
                        }
        output.WriteLine(sb.ToString());
        if (OutDir is { } o) File.WriteAllText(Path.Combine(o, "terrain.txt"), sb.ToString());
    }

    /// <summary>I76_KBD_LAUNCH=mission,car,x,z,fx,fz,mph: from that pose at that speed with no steer key (and a held key of each mode): ticks to the first airborne one and the airtime.</summary>
    [Fact]
    public void Launch()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_KBD_LAUNCH") is not { Length: > 0 } spec) return;
        var f = spec.Split(',');
        float F(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
        var install = new GameInstall(dir); var vfs = new AssetVfs(install); var asm = new ModelAssembler(vfs);
        foreach (var (name, tuning, key) in new (string, KeyStick.Assist?, CarButtons)[] { ("no key", null, CarButtons.None), ("Original L", null, CarButtons.SteerLeft), ("Assisted L", KeyStick.Default, CarButtons.SteerLeft) })
        {
            var d = new Drive(install, vfs, asm, f[0], f[1], tuning, new Vector2(F(2), F(3)), new Vector2(F(4), F(5)), F(6) * Mph) { Cruise = F(6) * Mph };
            for (int t = 0; t < 60; t++) d.Tick(key | CarButtons.ThrottleUp);
            int first = d.Lines.FindIndex(x => x.Air);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-11}: speed at start {d.Lines[0].Speed / Mph:0} mph, first airborne tick {first}, airborne ticks {d.Lines.Count(x => x.Air)}, max height step {d.Lines.Zip(d.Lines.Skip(1), (a, b) => b.Pos.Y - a.Pos.Y).Max():0.00} m"));
        }
    }

    /// <summary>A recording's header and lines (I76_INPUT_LOG): the mission, the car, and per tick the keys, the commands and the car's pose.</summary>
    public sealed record Recording(string Mission, string Car, string Mode, List<(int Tick, CarButtons Keys, CarCommands Cmd, Vector3 Pos, Vector2 Fwd, Vector2 Vel, string Sound, float Steer)> Ticks);

    public static List<Recording> ReadRecording(string path)
    {
        var all = new List<Recording>();
        Recording? cur = null;
        string[]? cols = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("# i26-input"))
            {
                var f = line.Split('\t').Skip(1).Select(x => x.Split(' ', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                cur = new Recording(f.GetValueOrDefault("mission", ""), f.GetValueOrDefault("car", ""), f.GetValueOrDefault("mode", ""), []);
                all.Add(cur);
                cols = null;
                continue;
            }
            if (cur is null) continue;
            if (cols is null) { cols = line.Split('\t'); continue; }
            var v = line.Split('\t');
            string C(string name) => v[Array.IndexOf(cols, name)];
            float F(string name) => float.Parse(C(name), CultureInfo.InvariantCulture);
            var keys = CarButtons.None;
            foreach (char ch in C("keys"))
                keys |= ch switch { 'L' => CarButtons.SteerLeft, 'R' => CarButtons.SteerRight, 'U' => CarButtons.ThrottleUp, 'D' => CarButtons.ThrottleDown, 'E' => CarButtons.EBrake, _ => CarButtons.None };
            var cmd = (CarCommands)uint.Parse(C("cmd"), NumberStyles.HexNumber) & (CarCommands.Reverse | CarCommands.ShiftUp | CarCommands.ShiftDown | CarCommands.StartEngine);
            cur.Ticks.Add((int.Parse(C("tick")), keys, cmd, new Vector3(F("x"), F("y"), F("z")), new Vector2(F("fx"), F("fz")), new Vector2(F("vx"), F("vz")), C("sound"), F("steer")));
        }
        return all;
    }

    /// <summary>
    /// I76_INPUT_REPLAY=file[@from]: each mission of the recording (from that tick on, else from the first tick the car moves
    /// under the player's keys) driven again headless from the same pose and velocity on the same mission and car, with the
    /// recorded keys, through every mode: the squeal and skid shares, and how far the path drifts from the recorded one.
    /// </summary>
    [Fact]
    public void ReplayRecording()
    {
        if (InstallDir is not { } dir || Environment.GetEnvironmentVariable("I76_INPUT_REPLAY") is not { Length: > 0 } spec) return;
        var parts = spec.Split('@');
        int? from = parts.Length > 1 ? int.Parse(parts[1]) : null;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var sb = new StringBuilder();
        int n = 0;
        foreach (var rec in ReadRecording(parts[0]))
        {
            n++;
            int i0 = from is { } ft ? rec.Ticks.FindIndex(t => t.Tick >= ft) : rec.Ticks.FindIndex(t => (t.Keys & (CarButtons.SteerLeft | CarButtons.SteerRight | CarButtons.ThrottleUp)) != 0);
            if (i0 < 0 || rec.Mission.Length == 0) continue;
            var start = rec.Ticks[i0];
            var ticks = rec.Ticks.Skip(i0).ToList();
            float recSq = ticks.Count(t => t.Sound.Contains("tturn")) / (float)ticks.Count, recSk = ticks.Count(t => t.Sound.Contains("tskid")) / (float)ticks.Count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"recording {n}: {rec.Mission} {rec.Car} as {rec.Mode}, ticks {start.Tick}..{ticks[^1].Tick}: recorded squeal {recSq:P0} skid {recSk:P0}");
            foreach (var (mode, tuning) in Variants())
            {
                var d = new Drive(install, vfs, asm, rec.Mission, rec.Car, tuning, new Vector2(start.Pos.X, start.Pos.Z), start.Fwd, 0);
                d.Car.Pos = new Double3(start.Pos.X, start.Pos.Y, start.Pos.Z);
                d.Car.Vel = new Vector3(start.Vel.X, 0, start.Vel.Y);
                float drift = 0;
                // I76_INPUT_REPLAY_NOSTEER=1: the steer keys left out (does the ground throw the car up on its own?)
                bool noSteer = Environment.GetEnvironmentVariable("I76_INPUT_REPLAY_NOSTEER") == "1";
                for (int k = 0; k < ticks.Count; k++)
                {
                    d.Tick(noSteer ? ticks[k].Keys & ~(CarButtons.SteerLeft | CarButtons.SteerRight) : ticks[k].Keys, ticks[k].Cmd);
                    if (k + 1 < ticks.Count) drift = MathF.Max(drift, Vector2.Distance(new Vector2(d.Car.Pos.ToVector3().X, d.Car.Pos.ToVector3().Z), new Vector2(ticks[k + 1].Pos.X, ticks[k + 1].Pos.Z)));
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {mode,-18} | {Presses(d.Lines)} | {Summary(d.Lines)} | path off the recording by up to {drift:0} m");
                Csv($"replay{n}-{mode}", d.Lines);
            }
        }
        output.WriteLine(sb.ToString());
        if (OutDir is { } o) File.WriteAllText(Path.Combine(o, "replay.txt"), sb.ToString());
    }
}
