using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The camera-facing cards: which nodes the engine turns, and the turn.</summary>
public class BillboardTests
{
    [Fact]
    public void TheCardFacesBackAlongTheCamerasHorizontalForward()
    {
        foreach (var f in new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0.6f, -0.5f, -0.62f), new Vector3(-3, 1, 2) })
        {
            var r = Billboard.Rotation(f);
            var h = Vector3.Normalize(new Vector3(f.X, 0, f.Z));
            Assert.True(Vector3.Distance(r.Rotate(Vector3.UnitZ), -h) < 1e-5f, $"{f}: +z");
            Assert.True(Vector3.Distance(r.Rotate(Vector3.UnitY), Vector3.UnitY) < 1e-6f, $"{f}: +y");
            // the viewer's left: forward (0,0,1) has its right at +x in the game's left-handed frame
            Assert.True(Vector3.Distance(r.Rotate(Vector3.UnitX), new Vector3(-h.Z, 0, h.X)) < 1e-5f, $"{f}: +x");
            var e = Billboard.EngineRows(f);
            float det = e[0] * (e[4] * e[8] - e[5] * e[7]) - e[1] * (e[3] * e[8] - e[5] * e[6]) + e[2] * (e[3] * e[7] - e[4] * e[6]);
            Assert.Equal(1f, det, 5);
        }
        // straight down: the engine's fallback, b = -1
        Assert.Equal([-1, 0, 0, 0, 1, 0, 0, 0, -1], Billboard.EngineRows(new Vector3(0, -1, 0.001f)));
    }

    [InstallFact]
    public void OnlyTheTreesFiresAndATowerAreCards()
    {
        var vfs = TestInstall.Vfs;
        var found = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in vfs.Names.Where(Bwd2File.IsDefinition))
        {
            Bwd2File f;
            try { f = new Bwd2File(vfs.Read(n)); } catch (InvalidDataException) { continue; }
            if (f.BillboardNodes() is { Count: > 0 } b) found[n.ToLowerInvariant()] = string.Join(" ", b.Order(StringComparer.OrdinalIgnoreCase));
        }
        Assert.Equal(["akwreck2.sdf", "amoilpm2.sdf", "bfgastf1.sdf", "bhwtowr4.sdf", "ngscrng1.sdf", "ngscrng2.sdf",
            "npinetr1.sdf", "npinetr2.sdf", "ntree1.sdf", "ntree2.sdf", "ntree3.sdf", "ntree4.sdf"], found.Keys);
        // the pine's card and its destroyed twin, the stump
        Assert.Equal("NP1ABDY1 NP1_BDY1", found["npinetr1.sdf"]);
        Assert.Equal("AK1AFIR2 AK1_FIR2 AK2_FIR2 AK3_FIR2", found["akwreck2.sdf"]);
    }
}

/// <summary>The modern look's real trees for the placed tree cards (I76.Formats.Vegetation.PlacedTrees).</summary>
public class PlacedTreeTests
{
    [Fact]
    public void ThePinesAreTreesTheOtherCardsStayCards()
    {
        Assert.NotNull(I76.Formats.Vegetation.PlacedTrees.For("npinetr1.sdf", 8f, 3.5f));
        Assert.NotNull(I76.Formats.Vegetation.PlacedTrees.For("NPINETR2.SDF", 6.8f, 4.1f));
        Assert.Equal("mesquite", I76.Formats.Vegetation.PlacedTrees.For("ntree1.sdf", 1.8f, 1.1f)?.Id);
        Assert.Equal("soaptree", I76.Formats.Vegetation.PlacedTrees.For("ntree4.sdf", 2.5f, 2.2f)?.Id);
        foreach (var d in new[] { "akwreck2.sdf", "amoilpm2.sdf", "bfgastf1.sdf", "bhwtowr4.sdf" })
            Assert.Null(I76.Formats.Vegetation.PlacedTrees.For(d, 5f, 5f));
    }

    [Fact]
    public void APineIsTheCardsSizeTheSameFromTheSameSeedAndLighterFarOff()
    {
        var s = I76.Formats.Vegetation.PlacedTrees.For("npinetr1.sdf", 8.0f, 3.52f)!;
        var tiers = s.BuildTiers(7);
        var (mn, mx) = tiers[0].Bounds();
        Assert.InRange(mx.Y, 7.6f, 8.6f);
        float across = MathF.Max(mx.X - mn.X, mx.Z - mn.Z);
        Assert.InRange(across, 2.2f, 4.4f);
        Assert.True(tiers[0].VertexCount > tiers[1].VertexCount && tiers[1].VertexCount > tiers[2].VertexCount);
        Assert.Equal(tiers[0].Digest(), s.BuildTiers(7)[0].Digest());
        Assert.NotEqual(tiers[0].Digest(), s.BuildTiers(8)[0].Digest());
    }
}
