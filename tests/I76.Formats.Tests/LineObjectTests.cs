using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>LDEF strings (fences, tyre walls): where each link's piece stands (issue #137).</summary>
public class LineObjectTests(ITestOutputHelper log)
{
    /// <summary>
    /// A 5 m barrier piece (afence4's part: a centred mesh, its file offset 0.5, 0.65, 2.5) on a 5.6 m link along +z:
    /// the original sets the mesh's centre on the link's midpoint, lifted by its half height, so the piece runs from
    /// 0.3 to 5.3 m with its sides 0.5 m either side of the line and its foot on it; not from 0 to 5 beside the line.
    /// </summary>
    [Fact]
    public void APieceIsCentredOnItsLinkWithItsFootOnTheLine()
    {
        var line = new LineObject("afence4", new byte[0x64], [new(10, 0, 20), new(10, 0, 25.6f)]);
        var (rot, pos) = Assert.Single(LineObjects.Placements(line, (_, _) => 3, new Vector3(0.5f, 0.65f, 2.5f), 0.65f));
        var place = Affine.FromEngine(rot, pos);
        var toModel = new Affine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(0.5f, 0.65f, 2.5f));
        Vector3 World(Vector3 mesh) => place.Apply(toModel.Apply(mesh));
        Assert.Equal(new Vector3(10, 3.65f, 22.8f), World(Vector3.Zero), new Vec3Comparer());
        Assert.Equal(new Vector3(9.5f, 3, 20.3f), World(new Vector3(-0.5f, -0.65f, -2.5f)), new Vec3Comparer());
        Assert.Equal(new Vector3(10.5f, 3, 25.3f), World(new Vector3(0.5f, -0.65f, 2.5f)), new Vec3Comparer());
    }

    /// <summary>
    /// S07's compound: every piece of the LDEF rows is centred on its link, foot on the line, lifted by its half height:
    /// the original's node transforms there have afence4's pieces 0.65 up, the tyre wall's (aitire1) 0.60 and afence3's 0.50.
    /// </summary>
    [InstallTheory]
    [InlineData("afence4", 0.65f)]
    [InlineData("aitire1", 0.60f)]
    [InlineData("afence3", 0.50f)]
    public void S07BarriersAreCentredOnTheirLinks(string name, float lift)
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var w = new MissionWorld(TestInstall.Vfs, asm, TestInstall.Install.Resolve("miss16", "s07.msn")!);
        var m = asm.Assemble(asm.ResolveDefinition(name)!)!;
        var part = m.Parts[0];
        var pieces = w.LinePlacements.Where(p => p.Object.Name == name).ToList();
        log.WriteLine($"{pieces.Count} {name} pieces");
        Assert.NotEmpty(pieces);
        var posts = LineObjects.Read(w.Mission).Where(l => l.Name == name)
            .SelectMany(l => l.Points.Zip(l.Points.Skip(1), (a, b) => (a, b))).ToList();
        Assert.Equal(posts.Count, pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            var place = Affine.FromEngine(pieces[i].Object.Rotation, w.GroundedPosition(pieces[i]));
            var centre = place.Apply(part.ToModel.Apply(Vector3.Zero));
            var (a, b) = posts[i];
            var mid = new Vector3((a.X + b.X) / 2, (float)(w.BilinearHeight(a.X, a.Z) + w.BilinearHeight(b.X, b.Z)) / 2 + lift, (a.Z + b.Z) / 2);
            Assert.True(Vector3.Distance(centre, mid) < 0.01f, $"piece {i}: centre {centre}, link midpoint {mid}");
        }
    }

    private sealed class Vec3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
