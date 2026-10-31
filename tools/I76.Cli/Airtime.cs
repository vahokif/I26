using System.Globalization;
using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Cli;

/// <summary>
/// How often the player's car leaves the ground on a route: the car alone (no script, no other cars), put at rest on the
/// route's first point and driven round it by a fixed rule, one row per tick.
///
///   i76 airtime MISSION "x,z x,z ..." [--speed S] [--rate HZ] [--seconds N] [--car X.vcf] [--look R]
///
/// The rule, the same as the original game's follow rule: aim at route point k, take the next one within R (default 20) units;
/// steer left or right while the heading is more than 0.03 rad off; throttle below S, brake above S + 3.
/// </summary>
public static class Airtime
{
    public static int Run(GameInstall install, List<string> args)
    {
        string mission = args[1];
        var route = args[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray())
            .Select(a => new Vector2(a[0], a[1])).ToList();
        float Opt(string n, float d) => args.IndexOf(n) is var i and >= 0 ? float.Parse(args[i + 1], CultureInfo.InvariantCulture) : d;
        float speed = Opt("--speed", 1000), seconds = Opt("--seconds", 120), look = Opt("--look", 20);
        int rate = (int)Opt("--rate", 20);
        string? car = args.IndexOf("--car") is var ci and >= 0 ? args[ci + 1] : null;
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(mission) ?? throw new FileNotFoundException(mission));
        var setup = MissionSetup.Build(vfs, w, asm, car, script: false);
        var world = setup.World;
        var v = world.Player!;
        world.Vehicles.RemoveAll(x => x != v);
        var session = new SimSession(setup, rate: SimRate.FromLabel(rate));
        session.AddDriver(v);
        var f0 = Vector2.Normalize(route[1] - route[0]);
        var fwd = new Vector3(f0.X, 0, f0.Y);
        v.Pos = new Double3(route[0].X, world.Ground.Query(route[0].X, 1e4, route[0].Y).Height + 1, route[0].Y);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        v.Vel = default;
        int k = 1;
        var o = Console.Out;
        o.WriteLine("t,x,y,z,vy,speed,airborne,k");
        int ticks = (int)(seconds / session.TickSeconds);
        for (int t = 0; t < ticks; t++)
        {
            var p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            while (Vector2.Distance(p, route[k]) < look) k = (k + 1) % route.Count;
            var to = route[k] - p;
            float err = MathF.Atan2(to.X, to.Y) - MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z);
            while (err > MathF.PI) err -= 2 * MathF.PI;
            while (err < -MathF.PI) err += 2 * MathF.PI;
            var input = new CarInput();
            if (err > 0.03f) input.Buttons |= CarButtons.SteerRight;
            else if (err < -0.03f) input.Buttons |= CarButtons.SteerLeft;
            if (v.Speed < speed) input.Buttons |= CarButtons.ThrottleUp;
            else if (v.Speed > speed + 3) input.Buttons |= CarButtons.ThrottleDown;
            session.Tick([input]);
            string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
            o.WriteLine($"{F((t + 1) * session.TickSeconds)},{F(v.Pos.X)},{F(v.Pos.Y)},{F(v.Pos.Z)},{F(v.Vel.Y)},{F(v.Speed)},{((v.Flags & VehicleFlags.Airborne) != 0 ? 1 : 0)},{k}");
        }
        return 0;
    }

    /// <summary>
    ///   i76 ground MISSION FILE.csv: the ground under each row's x,y,z (as WorldGround has it), as a column added
    ///   to the csv on stdout. For traces from the original game, which carry the position and not the ground.
    /// </summary>
    public static int Ground(GameInstall install, List<string> args)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(args[1]) ?? throw new FileNotFoundException(args[1]));
        var setup = MissionSetup.Build(vfs, w, asm, null, script: false);
        var lines = File.ReadAllLines(args[2]);
        var col = lines[0].Split(',').Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        Console.WriteLine(lines[0] + ",gq,terrain");
        foreach (var l in lines.Skip(1))
        {
            var f = l.Split(',');
            if (f.Length < col.Count) continue;
            double D(string n) => double.Parse(f[col[n]], CultureInfo.InvariantCulture);
            var (h, _) = setup.World.Ground.Query(D("x"), D("y"), D("z"));
            Console.WriteLine(l + "," + h.ToString("0.000", CultureInfo.InvariantCulture) + "," + setup.World.Ground.BareHeight(D("x"), D("z")).ToString("0.000", CultureInfo.InvariantCulture));
        }
        return 0;
    }
}
