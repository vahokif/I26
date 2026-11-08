using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>An explosion's effect node name to its .xdf, shared by the blast and the loading card's warm-up of its look (#116).</summary>
public class ExplosionFileTests
{
    [Theory]
    [InlineData("x1_carx1", "xcarx1.xdf")]
    [InlineData("x1_bldx1", "xbldx1.xdf")]
    [InlineData("xbldx2.xdf", "xbldx2.xdf")]
    [InlineData("xfire1", "xfire1.xdf")]
    public void NodeNamesNameTheirEffectFile(string node, string file) => Assert.Equal(file, Combat.ExplosionFile(node));

    [Fact] public void TheEndExplosionsAreACarsItsBurstAndABuildings() =>
        Assert.Equal(["xcarx1.xdf", "xcars1.xdf", "xbldx1.xdf"], Combat.EndExplosions.Select(Combat.ExplosionFile));
}
