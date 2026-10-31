using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Car panels meet edge to edge, and nothing may push them apart: the 1.2 cm overlay lift once caught every panel
/// lying in the plane of its neighbour (bounding boxes overlapping) and drew a dark seam down the Piranha's fender,
/// behind its door and under its stripe.
/// </summary>
public class CarSeamsTests
{
    private static bool Body(ModelPart p) => p.NodeName.Length >= 8 && p.NodeName[4..7].Equals("BDY", StringComparison.OrdinalIgnoreCase);

    [InstallFact]
    public void ThePiranhasFenderMeetsItsDoorExactly()
    {
        var m = new ModelAssembler(TestInstall.Vfs).Assemble("vppirna1.vcf")!;
        var near = PartSeams.Measure(m.Parts.Where(Body).ToList(), 0.001f);
        // the fender's diagonal, top to wheel arch: on a vertex of the door's part, within float rounding
        foreach (var (y, z) in new[] { (1.0074f, 0.773f), (0.7484f, 1.1726f), (0.6797f, 1.1504f) })
        {
            var at = near.Where(n => n.Part == "PP11BDYF" && MathF.Abs(n.At.Y - y) < 1e-3f && MathF.Abs(n.At.Z - z) < 1e-3f && n.At.X > 0).ToList();
            Assert.NotEmpty(at);
            Assert.All(at, n => Assert.True(n.ToVertex < 1e-5f, $"{n.At} is {n.ToVertex} from {n.VertexOf}"));
        }
    }

    [InstallFact]
    public void NoStockCarPanelIsLiftedOffItsNeighbour()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var cars = TestInstall.Vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        Assert.True(cars.Count > 50);
        var bad = new List<string>();
        foreach (var def in cars)
        {
            if (asm.Assemble(def) is not { } m) continue;
            foreach (var (pi, f) in CoplanarFaces.Overlays(m))
                if (Body(m.Parts[pi])) bad.Add($"{def} {m.Parts[pi].NodeName} {f.Texture}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
    }

    [InstallFact]
    public void OverlaysStillSitOffTheFacesUnderThem()
    {
        // what the lift is for: faces drawn over another in its plane -- bcywhse3's panels on its wall, a car's tail lamps on its back
        var asm = new ModelAssembler(TestInstall.Vfs);
        var shed = asm.Assemble("bcywhse3.sdf")!;
        Assert.Equal(4, CoplanarFaces.Overlays(shed).Count);
        var piranha = asm.Assemble("vppirna1.vcf")!;
        var lifted = CoplanarFaces.Overlays(piranha).Select(x => piranha.Parts[x.Item1].NodeName).ToHashSet();
        Assert.Contains("PP11TLGT", lifted);
    }

    [Fact]
    public void FacesSharingAnEdgeDoNotCover()
    {
        static CoplanarFaces.Planar Quad(float x0, float x1) =>
            new(0, new Geo.GeoFace(), System.Numerics.Vector3.UnitZ, 0, new(x0, 0, 0), new(x1, 1, 0),
                [new(x0, 0, 0), new(x1, 0, 0), new(x1, 1, 0), new(x0, 1, 0)]);
        Assert.False(CoplanarFaces.Cover(Quad(0, 1), Quad(1, 2)));     // side by side
        Assert.True(CoplanarFaces.Cover(Quad(0, 1), Quad(0.9f, 2)));   // a tenth over
        Assert.True(CoplanarFaces.Cover(Quad(0, 2), Quad(0.5f, 1)));   // a window in its wall
    }
}
