using System.Text.RegularExpressions;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's soft drop shadow (drop_shadow_soft.gdshader) leaves out upward faces within 0.6 m of the ground under
/// a scenery caster (a lot's asphalt would fight the ground it lies on). A car's low upward faces stand above the ground
/// and shade it. The Hacienda's pickup bed floor lies half a metre above the car's base: where the shadow's plane rises
/// under a car on a slope, the body's underside falls below the plane (clipped) and the bed floor inside the band, and
/// leaving it out cut a hole in its shadow behind the cab (#123).
/// </summary>
public class CarDropShadowTests
{
    private const float SkipBand = 0.6f;

    private static string Shader()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", "drop_shadow_soft.gdshader"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "game", "Shaders", "drop_shadow_soft.gdshader"));
    }

    [Fact]
    public void TheGroundFaceRuleIsTheScenerysOnly()
    {
        var rule = Regex.Match(Shader(), @"if \((.*)\) v_skip = 1\.0;");
        Assert.True(rule.Success);
        Assert.Contains($"< {SkipBand:0.0}", rule.Groups[1].Value);
        // a car's casters come with all_faces 1 (DropShadows: every caster but the static scenery)
        Assert.Contains("all_faces < 0.5", rule.Groups[1].Value);
    }

    [Fact]
    public void TheHaciendasBedLiesInsideTheBand()
    {
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } nd || !Directory.Exists(nd)) return;
        var model = new ModelAssembler(new AssetVfs(new GameInstall(nd))) { SkipParts = [] }.Assemble("vchacnd1.vcf")!;
        // the bed's floor: faces of the bed part turned up (the file's winding: a top face's cross product points down)
        var bed = model.Parts.Where(p => p.NodeName.EndsWith("CBED", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Mesh.Faces.Select(f => f.Vertices.Select(v => p.ToModel.Apply(p.Mesh.Vertices[v.Index])).ToArray()))
            .Where(vs => vs.Length >= 3 && System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(vs[1] - vs[0], vs[2] - vs[0])).Y < -0.7f)
            .ToList();
        Assert.NotEmpty(bed);
        // the floor, most of the bed's upward area (the wheel arches' tops beside it sit a little higher), is half a metre above
        // the model's base
        static float Area(System.Numerics.Vector3[] vs)
        {
            float a = 0;
            for (int i = 1; i + 1 < vs.Length; i++) a += System.Numerics.Vector3.Cross(vs[i] - vs[0], vs[i + 1] - vs[0]).Length() / 2;
            return a;
        }
        float inBand = bed.Where(vs => vs.Average(v => v.Y) is > 0.3f and < SkipBand).Sum(Area);
        Assert.True(inBand > 1.5f && inBand > 0.6f * bed.Sum(Area), $"{inBand:0.00} m² of {bed.Sum(Area):0.00}");
    }
}
