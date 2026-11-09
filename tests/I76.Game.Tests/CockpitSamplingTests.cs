using System.Text.RegularExpressions;
using I76.Formats;
using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The cockpit's own art (the z* textures: dash, gauges, panels, wheel) is point-sampled from its full-size level when it is
/// the original's low-resolution art, and filtered when it is larger than any of the original's (a high-resolution
/// replacement): object.gdshaderinc's i76_cockpit, which tells the two apart by the art's size alone.
/// </summary>
public class CockpitSamplingTests
{
    private static string Repo(params string[] path)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine([dir, .. path]))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, .. path]));
    }

    private static string Object => Repo("game", "Shaders", "object.gdshaderinc");

    [Fact]
    public void TheCockpitArtIsPointSampledAtFullSizeUnlessLargerThanTheOriginals()
    {
        var sampler = Regex.Match(Object, @"^uniform sampler2D albedo_cockpit : .*$", RegexOptions.Multiline).Value;
        Assert.Contains("repeat_disable", sampler);   // a face's edge sampled a hair past 1.0 never wraps
        var fn = Regex.Match(Object, @"vec4 i76_cockpit\(vec2 uv\) \{.*?\n\}", RegexOptions.Singleline).Value;
        Assert.Contains("> 256) return texture(albedo_cockpit, uv)", fn);
        Assert.Contains("texelFetch(albedo_cockpit,", fn);
        Assert.Contains(", 0);", fn);   // the full-size level
        // both looks read the cockpit's art through it
        Assert.Equal(2, Regex.Matches(Object, @"prelit \? i76_cockpit\(tuv\)").Count);
        Assert.DoesNotContain("albedo_nearest", Object);
        Assert.DoesNotContain("albedo_nearest", Repo("game", "Scripts", "Assets", "MaterialLibrary.cs"));
    }

    /// <summary>
    /// The rule's line: no face of the game's 3D cockpits (every player car's interior, detail 5) carries cockpit art over 256
    /// texels a side, the 3D card's largest texture. (The larger z* pictures are the 2D dash's and the map's, never on a face.)
    /// </summary>
    [InstallFact]
    public void NoCockpitFaceCarriesArtOver256()
    {
        var vfs = new AssetVfs(new GameInstall(TestInstall.Dir!));
        var resolver = new TextureResolver(vfs);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var car in vfs.Names.Where(n => n.StartsWith("vp", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)))
        {
            if (new ModelAssembler(vfs) { Detail = '5' }.Assemble(car) is not { } interior) continue;
            foreach (var f in interior.Parts.SelectMany(p => p.Mesh.Faces))
                if (f.Texture.Length > 0 && resolver.ResolveFace(f.Texture, interior.ObjectCode, interior.TmtNames, vtfMaps: interior.VtfMaps) is { } member
                    && member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..] is ['z' or 'Z', ..] && seen.Add(member) && resolver.Load(member) is { } t)
                    Assert.True(t.Width <= 256 && t.Height <= 256, $"{car}: {member} {t.Width}x{t.Height}");
        }
        Assert.True(seen.Count > 10, $"only {seen.Count} cockpit textures on faces");
    }
}
