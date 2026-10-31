using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// The Sheriff's light bar (CP*1SIRN) stood a hair above the cambered roof under it, up to 5 cm at its ends (#74: the bar and the turret
/// "not touching the top of the police car"); <see cref="MountSeating"/> lowers it onto the roof.
/// </summary>
public class MountSeatingTests(ITestOutputHelper log)
{
    private static GeoMesh Slab(float x0, float x1, float z0, float z1, params float[] y)
    {
        // an upward quad with the heights y at its four corners (x0 z0, x1 z0, x1 z1, x0 z1)
        var v = new[] { new Vector3(x0, y[0], z0), new Vector3(x1, y[1], z0), new Vector3(x1, y[2], z1), new Vector3(x0, y[3], z1) };
        var face = new GeoFace { VertexCount = 4, Vertices = [new(0, 0, 0, 0), new(1, 0, 0, 0), new(2, 0, 0, 0), new(3, 0, 0, 0)] };
        return new GeoMesh { Vertices = v, Faces = [face] };
    }

    [Fact]
    public void ALightBarOverACamberedRoofIsLoweredByTheLargestGap()
    {
        // the roof 1 cm under the bar's underside along its middle and 5 cm at the ends, as vcpolic1's
        var model = new AssembledModel { Definition = "test", Kind = "vcf" };
        model.Parts.Add(new ModelPart("CR11BDYT", Slab(-1, 1, -1, 1, 1.34f, 1.34f, 1.34f, 1.34f), Affine.Identity));
        model.Parts.Add(new ModelPart("CR11CNTR", Slab(-0.2f, 0.2f, -0.2f, 0.2f, 1.378f, 1.378f, 1.378f, 1.378f), Affine.Identity));
        model.Parts.Add(new ModelPart("CP11SIRN", Slab(-0.7f, 0.7f, -0.1f, 0.1f, 1.388f, 1.388f, 1.388f, 1.388f), Affine.Identity));
        var sinks = MountSeating.Sinks(model, MountSeating.IsBar);
        Assert.Equal(0.048f, sinks.Values.Single(), 3);
        MountSeating.Seat(model);
        Assert.Equal(1.34f, model.Parts[2].Mesh.Vertices.Min(v => model.Parts[2].ToModel.Apply(v).Y), 3);
        Assert.Empty(MountSeating.Sinks(model, MountSeating.IsBar));
    }

    [Fact]
    public void AnythingButABarIsLeftWhereTheDataPutsIt()
    {
        var model = new AssembledModel { Definition = "test", Kind = "vcf" };
        model.Parts.Add(new ModelPart("CR11BDYT", Slab(-1, 1, -1, 1, 1.34f, 1.34f, 1.34f, 1.34f), Affine.Identity));
        model.Parts.Add(new ModelPart("CR11MIRL", Slab(-0.2f, 0.2f, -0.2f, 0.2f, 1.388f, 1.388f, 1.388f, 1.388f), Affine.Identity));
        MountSeating.Seat(model);
        Assert.Equal(1.388f, model.Parts[1].Mesh.Vertices.Min(v => model.Parts[1].ToModel.Apply(v).Y), 3);
    }

    [NitroFact]
    public void ThePoliceCarsLightBarSitsOnTheRoof()
    {
        var vfs = new AssetVfs(new GameInstall(NitroFactAttribute.Dir!));
        foreach (var def in new[] { "vcpolic1.vcf", "vcpolic2.vcf", "vcpolic3.vcf" })
        {
            var model = new ModelAssembler(vfs).Assemble(def)!;
            var bar = model.Parts.First(MountSeating.IsBar);
            Assert.Empty(MountSeating.Sinks(model, MountSeating.IsBar));   // seated: nothing left to lower
            var roof = model.Parts.First(p => p.NodeName.EndsWith("BDYT", StringComparison.OrdinalIgnoreCase));
            float barLow = bar.Mesh.Vertices.Min(v => bar.ToModel.Apply(v).Y), roofTop = roof.Mesh.Vertices.Max(v => roof.ToModel.Apply(v).Y);
            log.WriteLine($"{def}: bar's underside {barLow:0.000}, roof's highest vertex {roofTop:0.000}");
            Assert.True(barLow < roofTop, "the bar's underside is under the roof's highest point");
        }
    }
}
