using I76.Formats.Bwd2;
using I76.Formats.Geo;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The effect quads' orientation (issue #105: the WP mortar's burst drawn upside down in the 1997 look).</summary>
public class EffectQuadTests
{
    private static GeoMesh Quad(string xdf)
    {
        var node = new Bwd2File(TestInstall.Vfs.Read(xdf)).SceneNodes().Where(t => t.Tag == "XGEO").SelectMany(t => t.Nodes).First();
        return GeoMesh.Parse(TestInstall.Vfs.Read(node.Name + ".geo"));
    }

    [InstallFact]
    public void TheWhitePhosphorusBurstStandsOnTheGroundWithItsFirstRowAtTheFoot()
    {
        var (flip, lift) = EffectQuad.Of(Quad("xwhph1.xdf"));
        Assert.True(flip);
        Assert.InRange(lift, 2.4f, 2.6f);
    }

    [InstallFact]
    public void TheMissileHitQuadWithItsVTheOtherWayIsNotTurned()
    {
        Assert.False(EffectQuad.Of(Quad("xmslc1.xdf")).FirstRowAtBottom);
        Assert.True(EffectQuad.Of(Quad("xmslb1.xdf")).FirstRowAtBottom);
    }

    [Fact]
    public void ASyntheticQuadReadsItsVAndHeight()
    {
        var g = new GeoMesh
        {
            Vertices = [new(-1, 0, 0), new(1, 0, 0), new(1, 4, 0), new(-1, 4, 0)],
            Faces = [new GeoFace { Texture = "X.TMT", Vertices = [new(0, 0, 0, 0), new(1, 0, 1, 0), new(2, 0, 1, 1), new(3, 0, 0, 1)] }],
        };
        Assert.Equal((true, 2f), EffectQuad.Of(g));
    }
}
