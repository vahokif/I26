using I76.Formats.Geo;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Faces whose texture coordinates the exporter left unwritten (±1.7e38). Santana's Auto Repair's broken front fence
/// (CG1AFN11; Krager Mags' and the DOT garage's are the same model) is mostly such faces: the original fills each with one
/// texel and draws a dark board, where sampling the garbage hit the fence texture's keyed rows and it all but vanished.
/// </summary>
public class GeoUnsetUvTests
{
    [InstallFact]
    public void TheBrokenFenceHasFacesWithUnsetUvs()
    {
        var broken = GeoMesh.Parse(TestInstall.Vfs.Read("CG1AFN11.geo"));
        Assert.True(broken.Faces.Count(f => f.UvsUnset) > broken.Faces.Length / 2);
        // the faces it keeps whole are mapped as the standing fence is
        Assert.Contains(broken.Faces, f => !f.UvsUnset);
        Assert.DoesNotContain(GeoMesh.Parse(TestInstall.Vfs.Read("CG1_FN11.geo")).Faces, f => f.UvsUnset);
    }

    /// <summary>Every stock face's coordinates are either within a hundred (a texture repeated along a wall) or garbage: the limit has room both ways.</summary>
    [InstallFact]
    public void UnsetIsFarFromAnyRealRepeat()
    {
        var vfs = TestInstall.Vfs;
        float most = 0, leastUnset = float.PositiveInfinity;
        foreach (var n in vfs.Names.Where(n => n.EndsWith(".geo", StringComparison.OrdinalIgnoreCase)))
        {
            GeoMesh g;
            try { g = GeoMesh.Parse(vfs.Read(n)); } catch (InvalidDataException) { continue; }
            foreach (var f in g.Faces)
                foreach (var v in f.Vertices)
                    foreach (var x in new[] { MathF.Abs(v.U), MathF.Abs(v.V) })
                        if (x < GeoFace.UvLimit) most = MathF.Max(most, x);
                        else leastUnset = MathF.Min(leastUnset, x);
        }
        Assert.True(most < 1000, $"a real coordinate of {most}");
        Assert.True(leastUnset > 1e30f, $"an unset one of {leastUnset}");
    }
}
