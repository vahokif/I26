using I76.Formats.Geo;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Faces with texture coordinates beyond ±32, which the original's renderer draws untextured in the face's own colour, lit.
/// Of the stock faces only those the exporter left unwritten (±1.7e38) are such: Santana's Auto Repair's broken front fence
/// (CG1AFN11; Krager Mags' and the DOT garage's are the same model) is mostly made of them and draws as a dark grey board,
/// where sampling the garbage hit the fence texture's keyed rows and it all but vanished.
/// </summary>
public class GeoUnsetUvTests
{
    [InstallFact]
    public void TheBrokenFenceHasFacesWithUnsetUvs()
    {
        var broken = GeoMesh.Parse(TestInstall.Vfs.Read("CG1AFN11.geo"));
        Assert.True(broken.Faces.Count(f => f.UvsUnset) > broken.Faces.Length / 2);
        // drawn in their own colour: a dark grey
        Assert.All(broken.Faces.Where(f => f.UvsUnset), f => Assert.Equal((60, 60, 60), (f.R, f.G, f.B)));
        // the faces it keeps whole are mapped as the standing fence is
        Assert.Contains(broken.Faces, f => !f.UvsUnset);
        Assert.DoesNotContain(GeoMesh.Parse(TestInstall.Vfs.Read("CG1_FN11.geo")).Faces, f => f.UvsUnset);
    }

    /// <summary>
    /// Every stock face's coordinates are within the original's ±32 or garbage, but for the two faces of a spinner's
    /// level-3 detail (EW3_SPN1, EW3ASPN1) that no view draws: the limit leaves every face drawn as it was.
    /// </summary>
    [InstallFact]
    public void OnlyGarbageIsBeyondTheLimit()
    {
        var vfs = TestInstall.Vfs;
        float most = 0;
        var between = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in vfs.Names.Where(n => n.EndsWith(".geo", StringComparison.OrdinalIgnoreCase)))
        {
            GeoMesh g;
            try { g = GeoMesh.Parse(vfs.Read(n)); } catch (InvalidDataException) { continue; }
            foreach (var f in g.Faces)
                foreach (var v in f.Vertices)
                    foreach (var x in new[] { MathF.Abs(v.U), MathF.Abs(v.V) })
                        if (x <= GeoFace.UvLimit) most = MathF.Max(most, x);
                        else if (x < 1e30f) between.Add(Path.GetFileNameWithoutExtension(n));
        }
        Assert.True(most <= 32);
        Assert.Subset(new HashSet<string>(["EW3_SPN1", "EW3ASPN1"], StringComparer.OrdinalIgnoreCase), between);
    }
}
