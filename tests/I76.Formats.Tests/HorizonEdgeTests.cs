using I76.Formats.Mission;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class HorizonEdgeTests
{
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

    /// <summary>The original's rule: the foot is 10.2 x the eye's height below the eye at the ring's radius (-(6000 - 480)/600 x camY - camY), nothing else enters.</summary>
    [Fact]
    public void TheOriginalsFootIsTenPointTwoTimesTheEyesHeightBelowTheEye()
    {
        foreach (float camY in new[] { -5f, 0f, 13f, 121.15f, 420f, 3500f })
            Assert.Equal(-10.2f * camY, Lighting.HorizonFoot(camY), 2);
    }

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "I76.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// The 1997 look's sky stands its panels by the original's rule alone, in every look of the game: the foot is a function of the eye's height
    /// (F the original's 600 m clip, whatever the draw distance) and of nothing else, so no bearing, map, draw distance or editor view moves it. The
    /// shader takes no table of where the ground ends and no sinking from a map's level, and the game feeds it neither.
    /// </summary>
    [Fact]
    public void The1997SkyHasOnlyTheOriginalsFootRule()
    {
        var sky = File.ReadAllText(Path.Combine(Repo(), "game", "Shaders", "sky_dome.gdshader"));
        Assert.Contains("float foot = -((radius - 0.8 * far_clip) / far_clip) * cam_y - cam_y;", sky);
        foreach (var gone in new[] { "has_edge", "uniform sampler2D edge", "sink_low", "sink_cap", "origin_xz", "EDGE_" })
            Assert.DoesNotContain(gone, sky);
        var main = File.ReadAllText(Path.Combine(Repo(), "game", "Scripts", "Boot", "Main.cs"));
        foreach (var gone in new[] { "UpdateSkyEdge", "\"sink_low\", level", "\"sink_cap\"", "\"has_edge\"", "LevelOf" })
            Assert.DoesNotContain(gone, main);
        Assert.Contains("mat.SetShaderParameter(\"far_clip\", Far1997);", main);
        Assert.Equal(600f, I76.Formats.Imaging.DrawDistance.OriginalClip);
    }

    /// <summary>The modern ring's sinking is set once from the map: the ring's shader reads one reference eye and the game sets it once (a per-frame follow of the eye is gone).</summary>
    [Fact]
    public void TheModernRingTakesOneReferenceEyeSetOnce()
    {
        var ring = File.ReadAllText(Path.Combine(Repo(), "game", "Shaders", "horizon_terrain.gdshader"));
        Assert.Contains("sink_low + (ref_eye.y - sink_low)", ring);
        var main = File.ReadAllText(Path.Combine(Repo(), "game", "Scripts", "Boot", "Main.cs"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(main, "SetShaderParameter\\(\"ref_eye\"").Count);
        Assert.Contains("HorizonEdge.Ring(world)", main);
    }
}
