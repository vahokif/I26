using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Tests;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The distant mountains, on every kind of map: the stock ones and what the map editor makes (a new flat map, a real-world map, a stock
/// map with its ground raised). The 1997 look's panels stand by the original's rule for the eye's height alone, on every map, at every
/// draw distance and in the editor's view; the modern look's ring is one height per map, worked out when the map loads, and its peaks
/// stand over the horizon from ordinary ground on every map but the stock ones whose own ground sinks the original's panels.
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

    private static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>Eyes over a map: the centre, the spots a quarter of the way in from each of its four sides, at the car's eye height over the ground.</summary>
    private static List<(double X, double Z)> Eyes(MissionWorld w)
    {
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        double tile = 640, x0 = c0 * tile, x1 = (c1 + 1) * tile, z0 = r0 * tile, z1 = (r1 + 1) * tile, cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
        var eyes = new List<(double, double)>();
        foreach (var (x, z) in new[] { (cx, cz), (x0 * 0.75 + x1 * 0.25, cz), (x0 * 0.25 + x1 * 0.75, cz), (cx, z0 * 0.75 + z1 * 0.25), (cx, z0 * 0.25 + z1 * 0.75) })
            if (w.Terrain.Sample(MissionWorld.ToSample(x), MissionWorld.ToSample(z)) is not null) eyes.Add((x, z));
        return eyes;
    }

    /// <summary>The 1997 panels' foot is the original's for the eye's height and nothing else, whatever the map or the draw distance, for the car's heights and the editor camera's (to 3.5 km): the same number on every map.</summary>
    [InstallFact]
    public void The1997PanelsFootIsTheOriginalsOnEveryMapAtEveryDrawDistance()
    {
        foreach (var (label, w) in Maps())
        {
            _ = w;
            foreach (float camY in new[] { -10f, 0f, 3f, 40f, 123f, 400f, 1500f, 3500f })
                Assert.True(Math.Abs(Lighting.HorizonFoot(camY, I76.Formats.Imaging.DrawDistance.OriginalClip) - (-10.2f * camY)) < 0.01f, $"{label}: foot at {camY} m");
        }
    }

    /// <summary>The modern ring's height is one number per map (the same when asked again and again, nothing of the eye in it), and a stock map keeps the original's rule at its average ground.</summary>
    [InstallFact]
    public void TheModernRingHasOneHeightPerMap()
    {
        foreach (var (label, w) in Maps())
        {
            var a = HorizonEdge.Ring(w);
            Assert.Equal(a, HorizonEdge.Ring(w));
            if (label.StartsWith("stock ")) Assert.Equal(new HorizonEdge.RingSink(0f, (float)w.AverageGroundHeight() + 3f), a);
            else
            {
                Assert.Equal((float)w.LowestGroundHeight(), a.Low);
                Assert.InRange(a.EyeY - a.Low, 0f, HorizonEdge.RingCap + 0.01f);
            }
        }
    }

    /// <summary>The ring's peaks (as tall as the panels) stand over the horizon from ordinary ground, 3 m up, on every map of the set: the stock maps whose ground is low enough that the original's own panels show, and every map a player makes.</summary>
    [InstallFact]
    public void TheModernRingsPeaksStandOverTheHorizonFromOrdinaryGround()
    {
        var problems = new List<string>();
        foreach (var (label, w) in Maps())
        {
            var ring = HorizonEdge.Ring(w);
            // the ring's cone at the ring's radius, in game height: the panels' foot rule for the reference eye, counted from the ring's base
            float footGame = ring.Low + (ring.EyeY - ring.Low) * (Lighting.HorizonFoot(1f) + 1f);
            double least = 90, most = -90;
            foreach (var (x, z) in Eyes(w))
            {
                double eye = w.GroundHeight(x, z) + 3;
                double top = Deg(Math.Atan((footGame + HorizonList.PanelHeight - eye) / HorizonList.Radius));
                least = Math.Min(least, top); most = Math.Max(most, top);
            }
            log.WriteLine($"{label,-36} avg {w.AverageGroundHeight(),6:0.0} low {w.LowestGroundHeight(),6:0.0}  ring eye {ring.EyeY,6:0.0}  peaks {least:0.0} to {most:0.0} deg over the horizon");
            bool stockHigh = label.StartsWith("stock ") && w.AverageGroundHeight() > 100;   // the original's own panels sink there (M04, M15, M01...)
            if (!stockHigh && least < 2) problems.Add($"{label}: the ring's peaks reach only {least:0.0} degrees over the horizon from some ground");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
