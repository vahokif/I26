using System.Text.RegularExpressions;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The 1997 look's roads on the 3D card: bilinear with no mip chain, as the original's road strips are (Glide, no mip
/// levels made for them), so a far road keeps its near texels; the modern look keeps its mipmapped sampling.
/// </summary>
public class RoadSamplingTests
{
    private static string Shader(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", name))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "game", "Shaders", name));
    }

    [Fact]
    public void TheRoadStripsTakeTheTopLevelIn1997Mode()
    {
        var line = Regex.Match(Shader("road.gdshader"), @"^\s*vec4 t = .*$", RegexOptions.Multiline).Value;
        Assert.Contains("mode_1997 ? textureLod(road_tex, UV, 0.0)", line);
        Assert.EndsWith(": texture(road_tex, UV);", line.TrimEnd());
    }

    [Fact]
    public void TheBakedRoadLayerTakesTheTopLevelIn1997Mode()
    {
        var line = Regex.Match(Shader("terrain_faithful.gdshader"), @"^\s*vec4 r = .*$", RegexOptions.Multiline).Value;
        Assert.Contains("textureLod(road_layer, road_uv, 0.0)", line);
        Assert.DoesNotContain("texture(road_layer", line);
    }
}
