using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Tests;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The distant mountains, on every kind of map: the stock ones and what the map editor makes (a new flat map, a real-world map, a stock
/// map with its ground raised). It has come back more than once as: no mountains at all over a map on high ground (the original's panels
/// sink 10.2 x the eye's height; an edited or real-world map lies far above the stock ones), the 1997 look's panels in jagged teeth along
/// the ground's end, and panels cutting in and out as the car drives. This builds each map's horizon as the game does (the sky's foot per
/// bearing from where the ground ends, the modern ring's reference eye) and holds: the mountains stand above the horizon, neighbouring
/// bearings agree to a fraction of a degree, and a few metres' drive moves no bearing's foot by more than a third of a degree.
/// </summary>
public class HorizonEverywhereTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("i26-horizon-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private MissionWorld Load(string path)
    {
        var vfs = TestInstall.Vfs;
        return new MissionWorld(vfs, new ModelAssembler(vfs), path);
    }

    /// <summary>What the editor makes, and the stock maps for company: (label, world).</summary>
    private List<(string Label, MissionWorld World)> Maps()
    {
        var maps = new List<(string, MissionWorld)>
        {
            ("new flat map, 1 tile", Load(EditorMaps.FlatMap(_dir, "flat1", 1, 10))),
            ("new flat map, 4 tiles", Load(EditorMaps.FlatMap(_dir, "flat4", 4, 10))),
            ("new flat map at 300 m", Load(EditorMaps.FlatMap(_dir, "flat4hi", 4, 300))),
            ("real-world map, rolling 40-340 m", Load(EditorMaps.RealWorldMap(_dir, "geohill", 6, 6, 40, 300))),
            ("real-world map, small and mild", Load(EditorMaps.RealWorldMap(_dir, "geosmall", 3, 3, 10, 60))),
            ("real-world map, 1000 m up and steep", Load(EditorMaps.RealWorldMap(_dir, "geohigh", 5, 5, 1000, 400))),
            ("canyon strip, 100 m rim over a basin", Load(EditorMaps.CanyonMap(_dir, "canyon", 5))),
        };
        // a stock map, its ground raised 150 m (what a player's edit can do), saved under a new name
        if (TestInstall.Install.FindMission("t01") is { } t01)
        {
            var proj = MapProject.Load(t01, TestInstall.Install).CloneAs("t01high");
            var (r0, r1, c0, c1) = proj.Terrain.Bounds();
            for (int sz = r0 * 128; sz < (r1 + 1) * 128; sz++)
                for (int sx = c0 * 128; sx < (c1 + 1) * 128; sx++)
                    if (proj.Terrain.Height(sx, sz) is { } h) proj.Terrain.SetHeight(sx, sz, Math.Min(4095, h + 1500));
            maps.Add(("t01 raised 150 m", Load(proj.SaveTo(_dir).First(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cbt", StringComparison.OrdinalIgnoreCase))))); 
        }
        foreach (var f in Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
            if (f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase)) maps.Add(("stock " + Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), Load(f)));
        return maps;
    }

    private const int Az = 720;   // azimuths looked along, half a degree apart

    /// <summary>
    /// The 1997 look's panel foot for an eye at (x, z), as the sky shader finds it for each azimuth (HorizonEdge's formulas: the end of
    /// the ground a bearing meets, filtered between bearings, the lowest foot within three degrees; the sinking rule when the end lies inside
    /// the clip): its height relative to the eye at the ring's radius. <paramref name="ends"/> are measured from where the eye was when
    /// the game last measured (every HorizonEdge.RemeasureCustom metres of driving), the eye's own place at each frame.
    /// </summary>
    private static float[] Feet(MissionWorld w, HorizonEdge.Level level, (float[] Dist, float[] Height) ends, double mx, double mz, double x, double z, float camY)
    {
        int n = HorizonEdge.Bins;
        var pts = new (float X, float Z, float H, float A)[n];
        for (int i = 0; i < n; i++)
        {
            double a = (i + 0.5) / n * Math.PI * 2;
            pts[i] = ((float)(mx + Math.Cos(a) * ends.Dist[i]), (float)(mz + Math.Sin(a) * ends.Dist[i]), ends.Height[i], 2f);   // a custom map's ends count at any distance
        }
        float Foot(double azimuth, int tap)
        {
            double u = azimuth / (Math.PI * 2) + tap * 0.5 / 360.0;
            u -= Math.Floor(u);
            double f = u * n - 0.5; int i0 = (int)Math.Floor(f); double t = f - i0;
            var p = pts[(i0 % n + n) % n]; var q = pts[((i0 + 1) % n + n) % n];
            float px = (float)(p.X + (q.X - p.X) * t), pz = (float)(p.Z + (q.Z - p.Z) * t), ph = (float)(p.H + (q.H - p.H) * t), pa = (float)(p.A + (q.A - p.A) * t);
            float de = MathF.Sqrt((float)((px - x) * (px - x) + (pz - z) * (pz - z)));
            return pa > 1.5f ? HorizonEdge.CustomFoot(level, camY, ph, de)
                : pa > 0.5f && de > HorizonEdge.Clip ? HorizonEdge.EndFoot(camY, ph, de) : HorizonEdge.Foot(level, camY);
        }
        var feet = new float[Az];
        for (int k = 0; k < Az; k++)
        {
            double a = (k + 0.5) / Az * Math.PI * 2;
            float low = float.MaxValue;
            for (int tap = -6; tap <= 6; tap++) low = MathF.Min(low, Foot(a, tap));
            feet[k] = low;
        }
        return feet;
    }

    /// <summary>The ground's own skyline from an eye: for each azimuth the highest elevation (radians) of the drawn ground along it, 0 where none rises over the horizontal; <paramref name="toEdge"/>: the ground at the eye's feet and its end count whatever their elevation, straight down (-90 degrees) where there is none (an eye at the map's edge looking out).</summary>
    private static double[] Skyline(MissionWorld w, double x, double z, float camY, bool toEdge = false)
    {
        var sil = new double[Az];
        for (int k = 0; k < Az; k++)
        {
            double a = (k + 0.5) / Az * Math.PI * 2, dx = Math.Cos(a), dz = Math.Sin(a), best = toEdge ? -Math.PI / 2 : 0;
            bool inside(double d) => w.Terrain.Sample(MissionWorld.ToSample(x + dx * d), MissionWorld.ToSample(z + dz * d)) is not null;
            double prev = 0;
            foreach (double t in Ts())
            {
                if (inside(t)) best = Math.Max(best, Math.Atan((w.GroundHeight(x + dx * t, z + dz * t) - camY) / t));
                else if (inside(prev))
                {
                    // the ground's last metres before its end are part of its skyline (a rim's top edge): found to a metre
                    double lo = prev, hi = t;
                    while (hi - lo > 1) { double mid = (lo + hi) / 2; if (inside(mid)) lo = mid; else hi = mid; }
                    best = Math.Max(best, Math.Atan((w.GroundHeight(x + dx * lo, z + dz * lo) - camY) / Math.Max(lo, 1)));
                }
                prev = t;
            }
            sil[k] = best;
        }
        return sil;

        IEnumerable<double> Ts()
        {
            if (toEdge) { yield return 2; yield return 6; yield return 12; yield return 20; }
            for (double t = 30; t <= HorizonEdge.MaxDistance; t += 30) yield return t;
        }
    }

    private static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>Eyes over a map: the centre, the spots a quarter of the way in from each of its four sides and the ones at its edge, at the car's eye height over the ground.</summary>
    private static List<(double X, double Z)> Eyes(MissionWorld w)
    {
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        double tile = 640, x0 = c0 * tile, x1 = (c1 + 1) * tile, z0 = r0 * tile, z1 = (r1 + 1) * tile, cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
        var eyes = new List<(double, double)>();
        foreach (var (x, z) in new[] { (cx, cz), (x0 * 0.75 + x1 * 0.25, cz), (x0 * 0.25 + x1 * 0.75, cz), (cx, z0 * 0.75 + z1 * 0.25), (cx, z0 * 0.25 + z1 * 0.75),
            (x0 + 12, cz), (x1 - 12, cz), (cx, z0 + 12), (cx, z1 - 12) })   // and at the very edge (a canyon's rim, where the ground ends under the car)
            if (w.Terrain.Sample(MissionWorld.ToSample(x), MissionWorld.ToSample(z)) is not null) eyes.Add((x, z));
        return eyes;
    }

    [InstallFact]
    public void TheMountainsStandAboveTheHorizonAndHoldStillOnEveryMap()
    {
        var problems = new List<string>();
        foreach (var (label, w) in Maps())
        {
            var level = HorizonEdge.LevelOf(w);
            bool stock = label.StartsWith("stock ");
            // a stock map keeps the original's rule (checked below); its panels and teeth are the original's to have
            if (stock) { Assert.Equal(HorizonEdge.Level.Original, level); continue; }
            double worstTeeth = 0, worstStep = 0, minPresent = 1;
            int eyeNo = -1;
            foreach (var (x, z) in Eyes(w))
            {
                eyeNo++;
                float camY = (float)w.GroundHeight(x, z) + 3f;
                var ends = HorizonEdge.Measure(w, x, z, inClip: true);
                var feet = Feet(w, level, ends, x, z, x, z, camY);
                // present: the panels' tops stand over the horizon (more than a degree) on nearly every azimuth
                int up = 0;
                for (int k = 0; k < Az; k++) if (Deg(Math.Atan((feet[k] + HorizonList.PanelHeight) / HorizonList.Radius)) > 1.0) up++;
                minPresent = Math.Min(minPresent, up / (double)Az);
                // teeth: where the panels' foot is what shows (above the ground's skyline), neighbouring azimuths agree to a fraction of a degree
                var sil = Skyline(w, x, z, camY, toEdge: eyeNo >= 5);
                for (int k = 0; k < Az; k++)
                {
                    int j = (k + 1) % Az;
                    double fk = Math.Atan(feet[k] / HorizonList.Radius), fj = Math.Atan(feet[j] / HorizonList.Radius);
                    if (fk > sil[k] && fj > sil[j]) worstTeeth = Math.Max(worstTeeth, Math.Abs(Deg(fj - fk)));
                }
                // popping: driving on in 10 m steps (the ends measured again every HorizonEdge.RemeasureCustom metres, as the game does) moves no visible foot by more than a fraction of a degree
                double mx = x, mz = z; var held = ends; var prev = feet; double dx = 0, dz = 0;
                // (not from the spots at the edge: a few metres off the ground's end the ground's own skyline swings by tens of degrees with a step)
                foreach (var (ax, az) in eyeNo >= 5 ? Array.Empty<(double, double)>() : new[] { (1.0, 0.0), (0.0, 1.0) })
                {
                    mx = x; mz = z; held = ends; prev = feet; var prevSil = sil;
                    for (int step = 1; step <= 12; step++)
                    {
                        double ex = x + ax * 10 * step, ez = z + az * 10 * step;
                        if (w.Terrain.Sample(MissionWorld.ToSample(ex), MissionWorld.ToSample(ez)) is null) break;
                        if (Math.Sqrt((ex - mx) * (ex - mx) + (ez - mz) * (ez - mz)) >= HorizonEdge.RemeasureCustom) { held = HorizonEdge.Measure(w, ex, ez, inClip: true); mx = ex; mz = ez; }
                        float cy = (float)w.GroundHeight(ex, ez) + 3f;
                        var now = Feet(w, level, held, mx, mz, ex, ez, cy);
                        var nowSil = Skyline(w, ex, ez, cy);
                        for (int k = 0; k < Az; k++)
                        {
                            // a foot that shows in both pictures (above the ground's skyline both times) stays where it was over that skyline: the
                            // skyline itself moves with the eye (parallax, a wall close by), and a foot standing on it moves with it
                            double f0 = Math.Atan(prev[k] / HorizonList.Radius), f1 = Math.Atan(now[k] / HorizonList.Radius);
                            if (f0 > prevSil[k] && f1 > nowSil[k]) worstStep = Math.Max(worstStep, Math.Abs(Deg((f1 - nowSil[k]) - (f0 - prevSil[k]))));
                        }
                        prev = now; prevSil = nowSil;
                    }
                }
                _ = dx; _ = dz;
            }
            // the modern ring: the cone it is sunk on leaves its peaks (as tall as the panels) standing over the horizon from the map's centre
            float ringEye = HorizonEdge.RingEyeY(w);
            double ringTop = Deg(Math.Atan((HorizonEdge.Foot(level, ringEye) + HorizonList.PanelHeight) / HorizonList.Radius));
            log.WriteLine($"{label,-36} avg {w.AverageGroundHeight(),6:0.0} m  present {minPresent:P0}  neighbour step {worstTeeth:0.00} deg  drive step {worstStep:0.00} deg  ring peaks {ringTop:0.0} deg over the horizon");
            if (minPresent < 0.9) problems.Add($"{label}: the panels stand over the horizon on only {minPresent:P0} of the azimuths");
            if (worstTeeth > 0.6) problems.Add($"{label}: neighbouring azimuths' feet differ by {worstTeeth:0.00} degrees (teeth)");
            if (worstStep > 0.3) problems.Add($"{label}: a 10 m drive moves a foot by {worstStep:0.00} degrees (popping)");
            if (ringTop < 6) problems.Add($"{label}: the modern ring's peaks reach only {ringTop:0.0} degrees over the horizon");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// The map editor's view: its camera hangs hundreds of metres over the ground (up to 3.5 km), where the original's rule would sink
    /// the 1997 look's panels 10 x that below the horizon (so the editor used to leave them out). With the cap they stand over the
    /// horizon and stay put wherever the camera is, on a stock map (edited or not) and on a map the editor made.
    /// </summary>
    [InstallFact]
    public void TheEditorsPanelsStandOverTheHorizonFromAnyHeight()
    {
        var problems = new List<string>();
        foreach (var (label, w) in Maps())
        {
            var level = HorizonEdge.LevelOf(w, capped: true);
            if (level.Cap != HorizonEdge.CustomCap || level.Low != (float)w.LowestGroundHeight()) problems.Add($"{label}: the editor's level is not the capped one from the lowest ground");
            float low = level.Low;
            float heldTop = HorizonEdge.Foot(level, low + HorizonEdge.CustomCap) + low + HorizonEdge.CustomCap;   // the foot's game height once the cap holds
            foreach (float up in new[] { 5f, 30f, 70f, 150f, 420f, 1500f, 3500f })
            {
                float foot = HorizonEdge.Foot(level, low + up);
                double top = Deg(Math.Atan((foot + HorizonList.PanelHeight) / HorizonList.Radius));
                if (up >= HorizonEdge.CustomCap && Math.Abs(foot + low + up - heldTop) > 0.01f) problems.Add($"{label}: the panels' foot moves with the camera above the cap ({up} m up)");
                if (up <= 150 && top < 1.0) problems.Add($"{label}: from {up} m over the lowest ground the panels top out only {top:0.0} degrees over the horizon");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
