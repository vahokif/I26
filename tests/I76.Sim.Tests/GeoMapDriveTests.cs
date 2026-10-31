using System.Numerics;
using I76.Editor;
using I76.Editor.Geo;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Ai;
using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Maps made by `i76 geomap` in the sim: an AI car routed along a long road stays on the road and on the ground (not under it, not flying), on a
/// small map built here from a made-up hillside, and on a real one when I76_GEOMAP_MAP names its .cbt or .lvl (the Billings map: the interstate).
/// </summary>
public class GeoMapDriveTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-geodrive-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private string Scratch(string real, string[] mapFiles)
    {
        var dir = Path.Combine(_root, "install");
        var addon = Path.Combine(dir, "addon");
        Directory.CreateDirectory(addon);
        foreach (var e in Directory.EnumerateFileSystemEntries(real))
        {
            string name = Path.GetFileName(e);
            if (name.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(Path.Combine(dir, name), e); else File.CreateSymbolicLink(Path.Combine(dir, name), e);
        }
        foreach (var f in mapFiles) File.Copy(f, Path.Combine(addon, Path.GetFileName(f)), true);
        return dir;
    }

    private sealed class Hill(GeoGrid g) : IElevation
    {
        public double At(double lat, double lon)
        {
            var (x, z) = g.ToMap(lat, lon);
            return 1000 + 12 * System.Math.Sin(x * 0.011) + 9 * System.Math.Cos(z * 0.013) + 0.01 * x;
        }
    }

    [Fact]
    public void ACarDrivesAGeoMapRoadOnTheRoadAndTheGround()
    {
        if (Dir("I76_NITRO_INSTALL") is not { } real) return;
        // 2 x 2 tiles with one long curving road across it
        var g = GeoGrid.Plan(45.83, -108.34, 45.83 + 1250 / 111_200.0, -108.34 + 1250 / 77_700.0);
        string Node(double x, double z) { var (la, lo) = g.ToLatLon(x, z); return FormattableString.Invariant($"{{\"lat\":{la:0.0000000},\"lon\":{lo:0.0000000}}}"); }
        var pts = new (double, double)[] { (80, 150), (400, 220), (700, 500), (900, 900), (1150, 1100) };
        string json = "{\"elements\":[{\"type\":\"way\",\"id\":1,\"nodes\":[1,2,3,4,5],\"tags\":{\"highway\":\"primary\"},\"geometry\":[" + string.Join(",", pts.Select(p => Node(p.Item1, p.Item2))) + "]}]}";
        var opt = new GeoMapOptions(45.83, -108.34, 45.84, -108.33, "geodrv", "Geo Drive", Nitro: true);
        var (project, rep) = GeoMapBuilder.Build(opt, g, new Hill(g), OsmRoads.Parse(json));
        Assert.Equal(1, rep.Roads);
        var files = project.SaveTo(Path.Combine(_root, "out"));
        Drive(real, files.ToArray(), "geodrv.cbt", "vppirna1.vcf", minRoadMetres: 600);
    }

    [Fact]
    public void ACarDrivesTheRealGeoMap()
    {
        if (Environment.GetEnvironmentVariable("I76_GEOMAP_MAP") is not { Length: > 0 } map || !File.Exists(map)) return;
        bool nitro = map.EndsWith(".cbt", StringComparison.OrdinalIgnoreCase);
        if (Dir(nitro ? "I76_NITRO_INSTALL" : "I76_INSTALL") is not { } real) return;
        var dirName = Path.GetDirectoryName(map)!;
        var files = Directory.GetFiles(dirName).Where(f => f.EndsWith(".ter", StringComparison.OrdinalIgnoreCase) || f == map).ToArray();
        Drive(real, files, Path.GetFileName(map), nitro ? "vppirna1.vcf" : "valepre1.vcf", minRoadMetres: 5000, roadPick: Environment.GetEnvironmentVariable("I76_GEOMAP_ROAD"));
    }

    private void Drive(string realInstall, string[] mapFiles, string missionFile, string car, float minRoadMetres, string? roadPick = null)
    {
        var dir = Scratch(realInstall, mapFiles);
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        string mission = Path.Combine(dir, "addon", missionFile);
        var world = new MissionWorld(vfs, asm, mission);
        var roads = world.Mission.RoadSegments();
        // the longest road as the sim holds it (a long one is the interstate); an index from I76_GEOMAP_ROAD picks another
        int pick = roadPick is not null ? int.Parse(roadPick) : Enumerable.Range(0, roads.Count).OrderByDescending(i => Len(roads[i])).First();
        var c = roads[pick].Points.Select(p => new Vector2(p.X, p.Z)).ToList();
        Assert.True(Len(roads[pick]) >= minRoadMetres, $"road {pick} is {Len(roads[pick]):0} m");
        var start = c[3]; var goal = c[^3];

        // the AI's road routing reads the road: start and goal on it give a route of about a waypoint a point
        var setup = MissionSetup.Build(vfs, world, asm, car, script: false);
        var net = RoadNetwork.Build(world.Mission, (x, z) => setup.Ground.BareHeight(x, z));
        var route = RoadRoute.TryRoute(net, start.X, start.Y, goal.X, goal.Y);
        Assert.NotNull(route);
        Assert.True(route!.Count > c.Count - 12, $"{route.Count} waypoints for {c.Count} road points");

        // and the player's car, driven by the original game's follow rule, goes along the road on real ground
        var v = setup.World.Player!;
        setup.World.Vehicles.RemoveAll(x => x != v);
        var session = new SimSession(setup, rate: SimRate.R20);
        session.AddDriver(v);
        var f0 = Vector2.Normalize(c[4] - c[3]);
        var fwd = new Vector3(f0.X, 0, f0.Y);
        v.Pos = new Double3(start.X, setup.World.Ground.Query(start.X, 1e4, start.Y).Height + 1, start.Y);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        v.Vel = default;
        int k = 4, onRoad = 0, total = 0, airborne = 0, flipped = 0;
        float worst = 0, worstAbove = -1e9f, worstBelow = 1e9f;
        bool arrived = false;
        int ticks = (int)(Len(roads[pick]) / 12f / session.TickSeconds) + 600;
        int t;
        for (t = 0; t < ticks && !arrived; t++)
        {
            var p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            while (k < c.Count - 1 && Vector2.Distance(p, c[k]) < 20) k++;
            var to = c[k] - p;
            float err = MathF.Atan2(to.X, to.Y) - MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z);
            while (err > MathF.PI) err -= 2 * MathF.PI;
            while (err < -MathF.PI) err += 2 * MathF.PI;
            var input = new CarInput();
            if (err > 0.03f) input.Buttons |= CarButtons.SteerRight; else if (err < -0.03f) input.Buttons |= CarButtons.SteerLeft;
            if (v.Speed < 25f) input.Buttons |= CarButtons.ThrottleUp; else if (v.Speed > 28f) input.Buttons |= CarButtons.ThrottleDown;
            session.Tick([input]);
            p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            float off = DistanceToRoad(c, p);
            if (Vector2.Distance(p, c[3]) > 20) { total++; worst = MathF.Max(worst, off); if (off <= 5f) onRoad++; }
            float above = (float)v.Pos.Y - setup.Ground.BareHeight(p.X, p.Y);
            worstAbove = MathF.Max(worstAbove, above); worstBelow = MathF.Min(worstBelow, above);
            if ((v.Flags & VehicleFlags.Airborne) != 0) airborne++;
            if (v.Rot.Up.Y < 0.3f) flipped++;
            arrived = Vector2.Distance(p, goal) < 25;
        }
        log.WriteLine($"road {pick} ({Len(roads[pick]):0} m, {c.Count} points): arrived {arrived} after {t * session.TickSeconds:0} s; on the road {onRoad}/{total} ticks, worst {worst:0.0} m off the centre line; the car's origin {worstBelow:0.00}..{worstAbove:0.00} m over the ground; airborne ticks {airborne}; flipped ticks {flipped}");
        Assert.True(arrived, "the car reached the end of the road");
        Assert.True(onRoad > total * 0.97, $"on the road {onRoad} of {total} ticks (worst {worst:0.0} off)");
        Assert.InRange(worstBelow, -1f, 5f);
        Assert.InRange(worstAbove, 0f, 6f);
        Assert.Equal(0, flipped);
    }

    private static float Len(RoadSegment r)
    {
        float l = 0;
        for (int i = 1; i < r.Points.Count; i++) l += Vector2.Distance(new(r.Points[i - 1].X, r.Points[i - 1].Z), new(r.Points[i].X, r.Points[i].Z));
        return l;
    }

    private static float DistanceToRoad(List<Vector2> c, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < c.Count; i++)
        {
            var ab = c[i + 1] - c[i]; float t = System.Math.Clamp(Vector2.Dot(p - c[i], ab) / ab.LengthSquared(), 0, 1);
            best = MathF.Min(best, Vector2.Distance(p, c[i] + ab * t));
        }
        return best;
    }
}
