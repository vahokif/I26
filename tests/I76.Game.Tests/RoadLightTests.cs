using System.Text.RegularExpressions;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The 1997 look's road light: the original lights a road segment with one flat value, max(0, I*Ly) + ambient, and no
/// depth term (the terrain and the objects take the depth fade, the roads do not). Both road paths (the road strips and
/// the road layer baked into the terrain) must form it that way.
/// </summary>
public class RoadLightTests
{
    private static string Shader(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", name))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "game", "Shaders", name));
    }

    [Theory]
    [InlineData("road.gdshader", "v_intensity", "light_y")]
    [InlineData("terrain_faithful.gdshader", "v_road", "road_light_y")]
    public void TheRoadLightHasNoDepthFade(string shader, string varying, string light)
    {
        var line = Regex.Match(Shader(shader), @"^\s*" + varying + @"\s*=.*$", RegexOptions.Multiline);
        Assert.True(line.Success);
        Assert.Contains(light, line.Value);
        Assert.DoesNotContain("fade", line.Value);
    }
}
