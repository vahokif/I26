using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;
using I76.Sim.Collision;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Which parts of a structure are drive-on: the original registers only the parts that own a polygon mesh (class
/// 0x3000). A box part (0x2000), such as aaramp5's side rails, is solid only: a car bounces off it and is never carried.
/// </summary>
public class DriveOnPartsTests
{
    /// <summary>A part with one upward face, the top of a 1 m box at y 1.</summary>
    private static AssembledModel Rail()
    {
        Vector3[] v = [new(-0.5f, 0, -2), new(0.5f, 0, -2), new(0.5f, 1, -2), new(-0.5f, 1, -2), new(-0.5f, 1, 2), new(0.5f, 1, 2)];
        var top = new GeoFace { VertexCount = 4, Plane = new Vector4(0, -1, 0, 1), Vertices = [new(3, 0, 0, 0), new(2, 0, 0, 0), new(5, 0, 0, 0), new(4, 0, 0, 0)] };
        var m = new AssembledModel { Definition = "rail.sdf", Kind = "sdf" };
        m.Parts.Add(new ModelPart("rail", new GeoMesh { Vertices = v, Faces = [top] }, Affine.Identity));
        return m;
    }

    private static readonly float[] Level = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    [Theory]
    [InlineData(0xbu)]
    [InlineData(0xcu)]
    public void AStructuresBoxPartIsSolidButNotDriveOn(uint type)
    {
        var o = StaticWorld.Build("ramp", type, Level, new Vector3(10, 0, 10), Rail(), nodeClass: _ => 0x2000)!;
        Assert.Single(o.Boxes);
        Assert.Empty(o.Surfaces);
        Assert.Empty(o.Polys);
    }

    [Fact]
    public void AStructuresMeshPartIsDriveOn()
    {
        var o = StaticWorld.Build("ramp", 0xb, Level, new Vector3(10, 0, 10), Rail(), nodeClass: _ => 0x3000)!;
        Assert.Empty(o.Boxes);
        var top = Assert.Single(o.Surfaces);
        Assert.True(top.N.Y > 0.99f);
        Assert.Single(o.Polys);
    }
}
