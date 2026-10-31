using System.Numerics;
using I76.Editor;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Ai;
using I76.Sim.Math;
using I76.Sim.Fsm;
using I76.Sim.Session;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The map editor's roads in the sim: a road the road tool made is read by the engine's road routing (start and
/// goal within 10.5 of one road) and an AI car sent along it follows it; and the stock maps join roads and structures the way the road
/// tool snaps (a bridge deck's ends are road ends).
/// </summary>
public class EditorRoadTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-roadai-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? NitroDir => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>A scratch install: every entry of the real one linked, an addon folder of its own (nothing is written into the real install).</summary>
    private string Scratch(string real)
    {
        var dir = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(dir, "addon"));
        foreach (var e in Directory.EnumerateFileSystemEntries(real))
        {
            string name = Path.GetFileName(e);
            if (name.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(Path.Combine(dir, name), e); else File.CreateSymbolicLink(Path.Combine(dir, name), e);
        }
        return dir;
    }

    [Fact]
    public void AnAiCarRoutedAlongARoadTheToolMadeFollowsIt()
    {
        if (NitroDir is null) return;
        var dir = Scratch(NitroDir);
        var addon = Path.Combine(dir, "addon");
        var (_, files) = NewMap.CreateFlat(addon, "aitest", "AI Test", 2, 20, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        var store = RoadStore.FromMission(File.ReadAllBytes(mission));
        var doc = new TerrainDocument(project.Terrain, "aitest", "AI Test", mission, addon, roads: store);
        // an S-bend of 600 m on gently rolling ground, drawn as the tool does
        for (int sz = 72 * 128; sz < 72 * 128 + 256; sz++)
            for (int sx = 4 * 128; sx < 4 * 128 + 256; sx++)
                doc.SetHeight(sx, sz, 200 + (int)(30 * System.Math.Sin(sx * 0.05) + 25 * System.Math.Cos(sz * 0.04)));
        float ox = 4 * 128 * 5 + 100, oz = 72 * 128 * 5 + 100;
        var path = RoadCurves.Spline([new(ox, oz), new(ox + 200, oz + 80), new(ox + 400, oz), new(ox + 600, oz + 100)]);
        var plan = RoadPlanner.Plan(path, 0, null, null, store);
        Assert.Null(plan.Problem);
        Assert.NotNull(RoadPlanner.Commit(doc, store, plan, conform: true));
        doc.Save();

        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var world = new MissionWorld(vfs, asm, mission);
        Assert.Single(world.Mission.RoadSegments());
        var s = MissionSetup.Build(vfs, world, asm, "vppirna1.vcf", rand: new I76.Sim.Rng.CrtRand(1), melee: ["vppirna1.vcf"]);
        // the route: start and goal within 10.5 of the road, on that one road
        var net = RoadNetwork.Build(world.Mission, (x, z) => s.Ground.BareHeight(x, z));
        var c = RoadBuilder.Centres(store[0]);
        var start = new Vector2(c[3].X + 6, c[3].Y + 3);          // off the line by 6, as a car beside the road
        var goal = c[^3];
        var route = RoadRoute.TryRoute(net, start.X, start.Y, goal.X, goal.Y);
        Assert.NotNull(route);
        Assert.True(route!.Count > c.Count - 8, $"{route.Count} waypoints for {c.Count} road points");
        // the waypoints lie in a lane: 2.45 inside the edge, so about 2.5 from the centre line
        foreach (var n in route.Skip(1).Take(route.Count - 2))
        {
            float d = c.Min(p => Vector2.Distance(p, n.P));
            Assert.InRange(d, 1.5f, 3.5f);
        }
        // and a car sent from beside the road drives it: it stays on the road's 10 m and gets there
        float dt = SimRate.R20.TickSeconds;
        var h = s.Script!.Host;
        var v = VehicleFactory.Create(vfs, "vppirna1.vcf");
        var car = new Entity("ai", [], 1, 2u << 16) { Vehicle = v };
        s.World.Vehicles.Add(v);
        h.Add(car);
        var ahead = Vector2.Normalize(c[4] - c[3]);
        v.Pos = new Double3(start.X, s.Ground.BareHeight(start.X, start.Y), start.Y);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, new Vector3(ahead.X, 0, ahead.Y)), Vector3.UnitY, new Vector3(ahead.X, 0, ahead.Y));
        v.Vel = default; v.Speed = 0;
        var order = new List<Vector3> { new(goal.X, 0, goal.Y) };
        float worst = 0, onGroundWorst = 0;
        int onRoad = 0, total = 0;
        bool arrived = false;
        for (int i = 0; i < (int)(120f / dt) && !arrived; i++)
        {
            var hh = h;
            hh.Now += dt; hh.FsmTick++; hh.FrameDelta = dt;
            hh.Order(car, null, AiOrder.Goto, order, 25f);
            hh.Ptable.EndFrame();
            s.World.Frame(dt);
            var p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            if (Vector2.Distance(p, c[3]) > 40) { total++; float off = DistanceToRoad(c, p); worst = MathF.Max(worst, off); if (off <= 5f) onRoad++; }
            onGroundWorst = MathF.Max(onGroundWorst, MathF.Abs((float)v.Pos.Y - s.Ground.BareHeight(p.X, p.Y)) - 1.5f);
            arrived = Vector2.Distance(p, goal) < 30;
        }
        log.WriteLine($"arrived {arrived}, on the road {onRoad}/{total} ticks, worst {worst:0.0} off the centre line");
        Assert.True(arrived, "the car reached the end of the road");
        Assert.True(onRoad > total * 0.9, $"on the road {onRoad} of {total} ticks (worst {worst:0.0} off)");
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

    [Fact]
    public void StockBridgesAreRoadEndsAndTheStructureEndsAreFoundFromTheirGeometry()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        // T05's bebridg2: a deck 80 long and 10 wide level with the ground, whose two ends are the ends of two roads
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "t05.msn")!);
        var p = w.Placements.Single(x => x.Object.Name == "bebridg2");
        var so = MissionSetup.StructureCollider(vfs, asm, p.Object.Name, p.Definition!, p.Object.ObjectType, p.Object.Rotation, w.GroundedPosition(p));
        Assert.NotNull(so);
        var ends = StructureAttach.Ends(so!.Surfaces.Select(x => x.V));
        Assert.Equal(2, ends.Count);
        Assert.All(ends, e => { Assert.True(e.RoadLevel); Assert.Equal(10f, e.Width, 1); });
        Assert.InRange(Vector3.Distance(ends[0].Position, ends[1].Position), 79.5f, 80.5f);
        var roads = RoadStore.FromMission(File.ReadAllBytes(w.Mission.Path));
        foreach (var e in ends)
        {
            var at = new Vector2(e.Position.X, e.Position.Z);
            // a road runs up to the deck's end (T05's second road goes on a point or two past it before it stops)
            int hits = roads.Roads.Where(r => r.Points.Count > 0).Count(r => r.Points.Any(q => Vector2.Distance(q.Centre, at) < 2.5f)
                && (Vector2.Distance(r.Points[0].Centre, at) < 12 || Vector2.Distance(r.Points[^1].Centre, at) < 12));
            Assert.True(hits >= 1, $"a road ends at the deck end {at}");
            // and the snap finds it from near by
            var snap = RoadSnap.Snap(at + new Vector2(3, 2), new SnapOptions { Angle = false }, roads, ends, null, null);
            Assert.Equal(SnapKind.Structure, snap.Kind);
        }
    }

    [Fact]
    public void AJumpRampsFootIsARoadEndAndItsLipIsNot()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "m05.msn")!);
        var roads = RoadStore.FromMission(File.ReadAllBytes(w.Mission.Path));
        int feet = 0, footOnRoadEnd = 0;
        foreach (var p in w.Placements.Where(x => x.Definition is not null && x.Object.Name.StartsWith("aaramp")))
        {
            var so = MissionSetup.StructureCollider(vfs, asm, p.Object.Name, p.Definition!, p.Object.ObjectType, p.Object.Rotation, w.GroundedPosition(p));
            if (so is null) continue;
            var ends = StructureAttach.Ends(so.Surfaces.Select(x => x.V));
            Assert.Equal(2, ends.Count);
            var foot = ends.Single(e => e.Low); var lip = ends.Single(e => !e.Low);
            Assert.True(foot.RoadLevel); Assert.False(lip.RoadLevel);
            feet++;
            var at = new Vector2(foot.Position.X, foot.Position.Z);
            if (roads.Roads.Where(r => r.Points.Count > 0).Any(r => Vector2.Distance(r.Points[0].Centre, at) < 2.5f || Vector2.Distance(r.Points[^1].Centre, at) < 2.5f)) footOnRoadEnd++;
        }
        Assert.True(feet >= 3);
        Assert.True(footOnRoadEnd >= feet / 2, $"{footOnRoadEnd} of {feet} ramp feet are road ends");
    }
}
