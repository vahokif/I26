using System.Numerics;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Geo;

public sealed record GeoMapOptions(
    double LatA, double LonA, double LatB, double LonB,
    string Name, string DisplayName,
    bool AllRoads = false, bool Nitro = false,
    MapLook? Look = null, RoadGrade.Settings? Grade = null, RoadTerrain.Settings? Shoulder = null,
    int Spawns = 8, int Regens = 4);

/// <summary>What the build did, for the log and the README.</summary>
public sealed class GeoMapReport
{
    public GeoGrid Grid = null!;
    public PackedHeights Heights = null!;
    public int Roads, RoadPoints, Structures, Bridges, Tunnels, Closed;
    public double MaxBridgeFill;
    public double RoadMetres;
    public RoadImportStats Import = new();
    public double MaxCut, MaxFill, MaxGrade;
    /// <summary>The finished ground along the roads, read as the game reads it (bilinear at the road points): the steepest step, the steps steeper than 20 %, the largest edge-to-edge drop across a road, and where the worst grade is.</summary>
    public double FinalMaxGrade, MaxCrossFall;
    public int StepsOver20, Steps;
    public string WorstGrade = "";
    public List<string> Steep = [];
    public int Spawns, Regens;
    public Dictionary<string, (int Roads, double Metres)> ByHighway = [];
}

/// <summary>A map from real terrain and roads: the heightfield resampled onto the 5 m grid, the roads cut into it as the road tool does.</summary>
public static class GeoMapBuilder
{
    public const int SamplesPerTile = TerrainTiles.Tile;

    /// <summary>Heights in metres at every sample of the grid: sample (i, j) is the point (5i, 5j) metres from the map's south-west corner.</summary>
    public static double[] SampleElevation(IElevation elev, GeoGrid grid)
    {
        int w = grid.Cols * SamplesPerTile, h = grid.Rows * SamplesPerTile;
        var o = new double[w * h];
        Parallel.For(0, h, j =>
        {
            for (int i = 0; i < w; i++)
            {
                var (lat, lon) = grid.ToLatLon(i * Units.SampleSize, j * Units.SampleSize);
                o[j * w + i] = elev.At(lat, lon);
            }
        });
        return o;
    }

    public static (MapProject Project, GeoMapReport Report) Build(GeoMapOptions opt, GeoGrid grid, IElevation elev, IReadOnlyList<OsmWay> ways)
    {
        var rep = new GeoMapReport { Grid = grid };
        var metres = SampleElevation(elev, grid);
        var packed = PackedHeights.Pack(metres);
        rep.Heights = packed;

        var project = MapProject.CreateBlank(opt.Name, opt.DisplayName, opt.Nitro ? ".cbt" : ".lvl", grid.Cols, grid.Rows, 0, grid.Row0, grid.Col0);
        int w = grid.Cols * SamplesPerTile;
        for (int j = 0; j < grid.Rows * SamplesPerTile; j++)
            for (int i = 0; i < w; i++) project.Terrain.SetSample(grid.Col0 * SamplesPerTile + i, grid.Row0 * SamplesPerTile + j, packed.Raw[j * w + i]);

        var doc = new TerrainDocument(project.Terrain, opt.Name);
        var planned = RoadImport.Convert(ways, grid, opt.AllRoads, rep.Import);
        var roads = LayRoads(doc, grid, planned, opt, rep);
        Audit(doc, roads, planned, grid, rep);
        roads.WriteTo(project.Mission);
        if (opt.Look is { } look) project.Mission.Look = look;
        PlaceSpawns(project, doc, grid, planned, opt, rep);
        return (project, rep);
    }

    /// <summary>Cuts and fills the ground along each road, biggest first, and makes the RSEGs; the roads' ground surface is set last, smallest first.</summary>
    private static RoadStore LayRoads(TerrainDocument doc, GeoGrid grid, List<PlannedRoad> planned, GeoMapOptions opt, GeoMapReport rep)
    {
        var store = new RoadStore([]);
        var done = new HashSet<(int, int)>();          // samples a road's flat zone has levelled: a later road follows their height there
        var edits = new List<(RoadEdit Edit, PlannedRoad Plan)>();
        foreach (var r in planned)
        {
            var c = r.Centres;
            if (!r.Tunnel)
            {
                var world = c.Select(p => new Vector2((float)WorldX(grid, p.X), (float)WorldZ(grid, p.Y))).ToList();
                var pins = new Dictionary<int, float>();
                for (int i = 0; i < c.Count; i++)
                    if (done.Contains((Round(world[i].X), Round(world[i].Y)))) pins[i] = RoadTerrain.Ground(doc, world[i].X, world[i].Y);
                float[] h;
                var shoulder = opt.Shoulder;
                if (r.Bridge)
                {
                    // a bridge: the deck is a straight, grade-limited line between its ends (and wherever it meets a road laid already); the
                    // ground under it is not cut, only filled up to it, with a skirt wide enough to stand on (the engine puts a road on the ground, so the deck is earth)
                    pins.TryAdd(0, RoadTerrain.Ground(doc, world[0].X, world[0].Y));
                    pins.TryAdd(c.Count - 1, RoadTerrain.Ground(doc, world[^1].X, world[^1].Y));
                    h = Deck(doc, world, pins, opt.Grade ?? new RoadGrade.Settings());
                    float fill = 0;
                    for (int i = 0; i < world.Count; i++) fill = Math.Max(fill, h[i] - RoadTerrain.Ground(doc, world[i].X, world[i].Y));
                    rep.MaxBridgeFill = Math.Max(rep.MaxBridgeFill, fill);
                    var flat = opt.Shoulder ?? new RoadTerrain.Settings();
                    shoulder = flat with { Shoulder = Math.Clamp(fill * 2.5f, flat.Shoulder, 50f) };
                }
                else h = RoadGrade.Profile(world, p => RoadTerrain.Ground(doc, p.X, p.Y), opt.Grade, pins);
                rep.MaxGrade = Math.Max(rep.MaxGrade, RoadGrade.MaxAbs(RoadGrade.Grades(world, h)));
                var res = RoadTerrain.Conform(doc, new HeightRecorder(doc), world, h, shoulder);
                rep.MaxCut = Math.Max(rep.MaxCut, res.MaxCut); rep.MaxFill = Math.Max(rep.MaxFill, res.MaxFill);
                foreach (var p in world)
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                            if (dx * dx + dz * dz <= 5) done.Add((Round(p.X) + dx, Round(p.Y) + dz));
            }
            var world2 = c.Select(p => new Vector2((float)WorldX(grid, p.X), (float)WorldZ(grid, p.Y))).ToList();
            var edit = RoadBuilder.Build(world2, r.Kind, closed: r.Closed);
            edits.Add((edit, r));
            store.Insert(store.Count, edit);
            rep.Roads++; rep.RoadPoints += c.Count; rep.RoadMetres += RoadCurves.Length(c);
            if (r.Structure) rep.Structures++;
            if (r.Bridge) rep.Bridges++;
            if (r.Tunnel) rep.Tunnels++;
            if (r.Closed) rep.Closed++;
            var key = r.Highway.EndsWith("_link") ? r.Highway[..^5] + " links" : r.Highway;
            rep.ByHighway[key] = rep.ByHighway.TryGetValue(key, out var t) ? (t.Roads + 1, t.Metres + RoadCurves.Length(c)) : (1, RoadCurves.Length(c));
        }
        // the ground under the roads: the smallest first, so a main road's surface wins where they cross
        for (int i = edits.Count - 1; i >= 0; i--) RoadSurface.Update(doc, new HeightRecorder(doc), null, edits[i].Edit, []);
        return store;
    }

    /// <summary>A bridge's deck heights: straight between the pinned points by distance along the road, never under the ground, held to the grade limit.</summary>
    public static float[] Deck(IMapDocument doc, IReadOnlyList<Vector2> world, IReadOnlyDictionary<int, float> pins, RoadGrade.Settings grade)
    {
        int n = world.Count;
        var along = new float[n];
        for (int i = 1; i < n; i++) along[i] = along[i - 1] + Math.Max(0.5f, Vector2.Distance(world[i - 1], world[i]));
        var keys = pins.Keys.OrderBy(k => k).ToArray();
        var h = new float[n];
        for (int i = 0; i < n; i++)
        {
            int a = -1, b = -1;
            foreach (int k in keys) { if (k <= i) a = k; if (k >= i) { b = k; break; } }
            float ha = a >= 0 ? pins[a] : pins[b], hb = b >= 0 ? pins[b] : pins[a];
            float t = a < 0 || b < 0 || a == b ? 0 : (along[i] - along[a]) / (along[b] - along[a]);
            h[i] = pins.TryGetValue(i, out float pv) ? pv : ha + (hb - ha) * t;
        }
        var ds = new float[n - 1];
        for (int i = 0; i < n - 1; i++) ds[i] = along[i + 1] - along[i];
        RoadGrade.Limit(h, ds, grade.MaxGrade, i => pins.ContainsKey(i));
        return h;
    }

    /// <summary>Reads the ground back along every road the way the game will (the engine puts each road point on the ground) and notes the grades and the cross fall.</summary>
    private static void Audit(TerrainDocument doc, RoadStore store, List<PlannedRoad> planned, GeoGrid grid, GeoMapReport rep)
    {
        for (int k = 0; k < store.Count; k++)
        {
            var pts = store[k].Points;
            float prev = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var c = pts[i].Centre;
                float h = RoadTerrain.Ground(doc, c.X, c.Y);
                rep.MaxCrossFall = Math.Max(rep.MaxCrossFall, Math.Abs(RoadTerrain.Ground(doc, pts[i].Left.X, pts[i].Left.Y) - RoadTerrain.Ground(doc, pts[i].Right.X, pts[i].Right.Y)));
                if (i > 0)
                {
                    double g = Math.Abs(h - prev) / Math.Max(0.5f, Vector2.Distance(pts[i - 1].Centre, c));
                    rep.Steps++;
                    if (g > 0.2) { rep.StepsOver20++; rep.Steep.Add($"{(planned[k].Structure ? "structure " : "")}way {planned[k].WayId} ({planned[k].Highway}) ({c.X:0}, {c.Y:0}) grade {g:0.00}"); }
                    if (g > rep.FinalMaxGrade) { rep.FinalMaxGrade = g; rep.WorstGrade = $"way {planned[k].WayId} ({planned[k].Highway}) at world ({c.X:0}, {c.Y:0})"; }
                }
                prev = h;
            }
        }
    }

    private static double WorldX(GeoGrid g, double x) => g.WorldX(x);
    private static double WorldZ(GeoGrid g, double z) => g.WorldZ(z);
    private static int Round(double world) => (int)Math.Round(world / Units.SampleSize);
    private static int Round(float world) => (int)Math.Round(world / Units.SampleSize);

    /// <summary>
    /// Spawn points and regen pads on the main roads (motorway to tertiary, not bridges): spread out by always taking the point farthest
    /// from the ones chosen, the first nearest the map's middle, facing along the road.
    /// </summary>
    private static void PlaceSpawns(MapProject project, TerrainDocument doc, GeoGrid grid, List<PlannedRoad> planned, GeoMapOptions opt, GeoMapReport rep)
    {
        var cand = new List<(Vector2 P, Vector2 Dir)>();
        foreach (var r in planned)
        {
            if (r.Structure || r.Rank > 4 || r.Closed) continue;
            var c = r.Centres;
            for (int i = 8; i < c.Count - 8; i += 20)
            {
                var p = c[i];
                if (p.X < 600 || p.Y < 600 || p.X > grid.Width - 600 || p.Y > grid.Height - 600) continue;
                var d = c[i + 1] - c[i - 1];
                if (d.LengthSquared() > 1e-6f) cand.Add((p, Vector2.Normalize(d)));
            }
        }
        var chosen = new List<(Vector2 P, Vector2 Dir)>();
        var centre = new Vector2((float)grid.Width / 2, (float)grid.Height / 2);
        int want = opt.Spawns + opt.Regens;
        while (chosen.Count < want && cand.Count > 0)
        {
            int best = 0; float bd = -1;
            for (int i = 0; i < cand.Count; i++)
            {
                float d = chosen.Count == 0 ? -Vector2.DistanceSquared(cand[i].P, centre) : chosen.Min(q => Vector2.DistanceSquared(q.P, cand[i].P));
                if (chosen.Count == 0 ? (bd < 0 || d > bd) : d > bd) { best = i; bd = d; }
            }
            chosen.Add(cand[best]); cand.RemoveAt(best);
        }
        int n = 0;
        foreach (var (p, dir) in chosen)
        {
            double x = grid.WorldX(p.X), z = grid.WorldZ(p.Y);
            var pos = new Vector3((float)x, RoadTerrain.Ground(doc, x, z), (float)z);
            float yaw = MathF.Atan2(dir.X, dir.Y);
            if (n < opt.Spawns) { project.Mission.Objects.Add(MapObject.Spawn(pos, yaw)); rep.Spawns++; }
            else { project.Mission.Objects.Add(MapObject.Regen(pos, yaw)); rep.Regens++; }
            n++;
        }
    }
}
