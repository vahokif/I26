using I76.Formats.Mission;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class HorizonEdgeTests
{
    /// <summary>
    /// #46: T17's ground is a finite plate, and at the Far draw distance the 1997 look drew it past the original's 600 m clip, leaving a
    /// strip of sky under the horizon panels. The panels' foot now follows the plate's end, bearing by bearing.
    /// </summary>
    [Fact]
    public void T17sGroundEndsWithinTheRingOnSomeBearingsAndRunsOnOnOthers()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var inst = new GameInstall(dir);
        var vfs = new AssetVfs(inst);
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), inst.Resolve("miss16", "t17.msn")!);
        var (dist, height) = HorizonEdge.Measure(w, 3037.5, 46142.5);
        Assert.Equal(HorizonEdge.Bins, dist.Length);
        Assert.All(dist, d => Assert.InRange(d, HorizonEdge.Clip, HorizonEdge.MaxDistance));
        Assert.Contains(dist, d => d < HorizonEdge.MaxDistance);   // the plate ends
        Assert.Contains(dist, d => d > 1000);                      // and runs well past the clip somewhere
        Assert.All(height, h => Assert.InRange(h, -50f, 1000f));
    }

    /// <summary>#87: the software sky band's foot stands on the lowest ground within the clip, which is no higher than the ground under the eye.</summary>
    [Fact]
    public void TheLowestGroundIsNoHigherThanUnderfoot()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var inst = new GameInstall(dir);
        var vfs = new AssetVfs(inst);
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), inst.Resolve("miss16", "t17.msn")!);
        double x = 3037.5, z = 46142.5;
        float low = HorizonEdge.Lowest(w, x, z);
        Assert.True(low <= w.GroundHeight(x, z) + 1e-3, $"lowest {low} above the ground underfoot {w.GroundHeight(x, z)}");
        Assert.InRange(low, -50f, 1000f);
        // a smaller disc never finds lower ground than the whole clip
        Assert.True(HorizonEdge.Lowest(w, x, z, 100f) >= low);
    }

    /// <summary>
    /// The 1997 look's panels stand on the ends Measure finds, and Main measures again every 60 m the eye moves (the shader reads the
    /// ends as points, so a measure from a new eye must put them where the last one did: the mountains jumped in blocks when the
    /// table held distances from the old eye). From two eyes 60 m apart, most bins' end points agree to within the bin's own width
    /// at that range plus the sampling step.
    /// </summary>
    [Fact]
    public void TheEndsAsPointsHoldStillWhenTheEyeMovesToTheNextMeasure()
    {
        if (Environment.GetEnvironmentVariable("I76_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var inst = new GameInstall(dir);
        var vfs = new AssetVfs(inst);
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), inst.Resolve("miss16", "t17.msn")!);
        double ax = 3037.5, az = 46142.5, bx = ax + 42.4, bz = az + 42.4;   // 60 m apart
        var (da, ha) = HorizonEdge.Measure(w, ax, az);
        var (db, hb) = HorizonEdge.Measure(w, bx, bz);
        var off = new List<double>();
        for (int i = 0; i < HorizonEdge.Bins; i++)
        {
            if (da[i] >= HorizonEdge.MaxDistance) continue;   // runs on past the ring: no end to move
            double a = (i + 0.5) / HorizonEdge.Bins * 2 * Math.PI;
            double ex = ax + Math.Cos(a) * da[i], ez = az + Math.Sin(a) * da[i];
            // the same end from the new eye: the bin looking at it
            double ang = Math.Atan2(ez - bz, ex - bx); if (ang < 0) ang += 2 * Math.PI;
            int j = Math.Min(HorizonEdge.Bins - 1, (int)(ang / (2 * Math.PI) * HorizonEdge.Bins));
            if (db[j] >= HorizonEdge.MaxDistance) { off.Add(double.MaxValue); continue; }
            double fx = bx + Math.Cos((j + 0.5) / HorizonEdge.Bins * 2 * Math.PI) * db[j], fz = bz + Math.Sin((j + 0.5) / HorizonEdge.Bins * 2 * Math.PI) * db[j];
            off.Add(Math.Sqrt((fx - ex) * (fx - ex) + (fz - ez) * (fz - ez)));
        }
        Assert.True(off.Count > 20, "the plate's end should be on some bearings");
        off.Sort();
        double p75 = off[off.Count * 3 / 4];
        Assert.True(p75 < 200, $"75th percentile of how far an end moved between measures: {p75:F0} m");
    }

    /// <summary>A tripwire for #46's smooth foot: the shader reads the ends as filtered points and keeps the lowest within the taps, not a nearest-sampled table of distances.</summary>
    [Fact]
    public void TheSkyShaderFiltersBetweenTheEndsAndKeepsTheLowestFoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "I76.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "game", "Shaders", "sky_dome.gdshader"));
        Assert.Contains("uniform sampler2D edge : filter_linear", src);
        Assert.Contains("for (int i = -EDGE_TAPS; i <= EDGE_TAPS; i++)", src);
        Assert.Contains("origin_xz", src);   // the distance is taken from the eye as it is now
    }

    /// <summary>The sinking rule: a stock map keeps the original's (the foot 10.2 x the eye's height below it), a custom map's is counted from its lowest ground and held to 70 m, so a map on high ground keeps its mountains.</summary>
    [Fact]
    public void ACustomMapsHorizonSinksFromItsLowestGroundAndStopsAtACap()
    {
        Assert.Equal(Lighting.HorizonFoot(121.15f), HorizonEdge.Foot(HorizonEdge.Level.Original, 121.15f), 3);
        var custom = new HorizonEdge.Level(40f, HorizonEdge.CustomCap);
        Assert.Equal(40f - 9.2f * 70f - 400f, HorizonEdge.Foot(custom, 400f), 2);          // far above: held at the cap
        Assert.Equal(Lighting.HorizonFoot(13f) + 40f - 40f, HorizonEdge.Foot(custom with { Low = 0 }, 13f), 3);
        Assert.True(HorizonEdge.Foot(custom, 400f) + HorizonList.PanelHeight > -400f);       // the panels' tops are not 15 degrees under the horizon
    }

    /// <summary>The sky's shader takes the same sinking rule (HorizonEdge.Foot), and the ring's does too.</summary>
    [Fact]
    public void TheShadersCountTheSinkingFromTheMapsLevel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "I76.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sky = File.ReadAllText(Path.Combine(dir!.FullName, "game", "Shaders", "sky_dome.gdshader"));
        Assert.Contains("min(cam_y - sink_low, sink_cap)", sky);
        var ring = File.ReadAllText(Path.Combine(dir.FullName, "game", "Shaders", "horizon_terrain.gdshader"));
        Assert.Contains("sink_low + (ref_eye.y - sink_low)", ring);
    }

    /// <summary>The map editor's view draws the 1997 horizon panels too (its camera, hundreds of metres up, counts as no higher than the cap), and a script that opens a map does not open it again on the reload.</summary>
    [Fact]
    public void TheEditorsViewDrawsThePanelsWithTheCap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "I76.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var main = File.ReadAllText(Path.Combine(dir!.FullName, "game", "Scripts", "Boot", "Main.cs"));
        Assert.DoesNotContain("SetShaderParameter(\"has_horizon\", false);\n        // no ambient", main.Replace("\r", ""));
        Assert.Contains("HorizonEdge.LevelOf(skyWorld, I76.Game.Editor.EditorSession.Active && !I76.Game.Editor.EditorSession.Playtest)", main);
        var editor = File.ReadAllText(Path.Combine(dir.FullName, "game", "Scripts", "Editor", "MapEditor.cs"));
        Assert.Contains("EditorSession.Mission.Equals(a[0], StringComparison.OrdinalIgnoreCase)) return;", editor);
    }
}
