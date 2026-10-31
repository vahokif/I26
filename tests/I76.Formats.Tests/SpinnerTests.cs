using I76.Formats.Bwd2;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The scenery that turns on its own: type-10 nodes.</summary>
public class SpinnerTests
{
    [InstallFact]
    public void TheGasStationSignsSpinAboutTheirUpAxis()
    {
        var expect = new Dictionary<string, (string Node, float Revs)>
        {
            ["bfgastf1.sdf"] = ("FG1_SGN1", 0.2f), ["bfsince1.sdf"] = ("FS1_SGN1", 0.2f),
            ["bdwgnwl1.sdf"] = ("DW1_SGN1", 0.25f), ["bflgila1.sdf"] = ("FL1_SGN1", 0.05f),
        };
        foreach (var (sdf, (node, revs)) in expect)
        {
            var s = new Bwd2File(TestInstall.Vfs.Read(sdf)).Spinners();
            // one per detail level (FG1_, FG2_, FG3_), all at the same rate
            Assert.Contains(node, s.Keys);
            Assert.All(s, kv => Assert.Equal(new System.Numerics.Vector3(0, revs, 0), kv.Value));
        }
    }

    [InstallFact]
    public void TheOilPumpsDoNotMove()
    {
        // the walking beam AM1_ARM1 is a plain type-2 node: nothing turns it
        Assert.Empty(new Bwd2File(TestInstall.Vfs.Read("amoilpm2.sdf")).Spinners());
    }
}
