using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>DamageTextures: a hit zone's face slots step through their .TMT, its parts through their states.</summary>
public class DamageTexturesTests
{
    [Theory]
    [InlineData("V1 FT FT.MAP", 0)]
    [InlineData("v1 ft lf.map", 0)]
    [InlineData("V5 FT TP.MAP", 0)]   // the cockpit's hood follows the front
    [InlineData("V1 MD LF.MAP", 1)]
    [InlineData("V1 MD RT.MAP", 2)]
    [InlineData("V1 BK UN.MAP", 3)]
    [InlineData("V1 TP TP.MAP", 4)]
    [InlineData("V1 MD TP.MAP", -1)]  // not in the exe's table: never changes
    [InlineData("V1 BO DY.MAP", -1)]
    [InlineData("V5 MD TP.MAP", -1)]
    [InlineData("HL1.MAP", -1)]
    public void TheExeTableSaysWhichZoneASlotFollows(string face, int zone) => Assert.Equal(zone, DamageTextures.Zone(face));

    [Fact]
    public void TexturesChangeAtTheFirstLevelAndShapesAtTheSecond()
    {
        // Levels 0,1,2,3 → entries 0,1,1,2; death → 3
        Assert.Equal([0, 1, 1, 2, 3], Enumerable.Range(0, 5).Select(DamageTextures.Frame));
        // Levels 0,1,2,3 → states 0,0,1,2 (condition digits 1,1,2,3); state 3 is the dead hull
        Assert.Equal("1123", new string(Enumerable.Range(0, 4).Select(DamageTextures.Condition).ToArray()));
    }

    [InstallFact]
    public void ADamagedFrontShowsTheTmtsLaterEntries()
    {
        var res = new TextureResolver(TestInstall.Vfs);
        var m = new ModelAssembler(TestInstall.Vfs) { SkipParts = [] }.Assemble("vppirna1.vcf")!;
        string? Face(string slot, int frame) => res.ResolveFace(slot, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps, frame: frame);
        var whole = Face("V1 FT TP.MAP", 0);
        Assert.NotNull(whole);
        Assert.Equal(whole, Face("V1 FT TP.MAP", 0));
        var hit = Face("V1 FT TP.MAP", 1);
        var worse = Face("V1 FT TP.MAP", 2);
        Assert.NotNull(hit);
        Assert.NotEqual(whole, hit);
        Assert.NotEqual(hit, worse);
        // the cockpit's hood: its own table (1pp5FTTP.TMT)
        var interior = new ModelAssembler(TestInstall.Vfs) { Detail = '5' }.Assemble("vppirna1.vcf")!;
        var hood0 = res.ResolveFace("V5 FT TP.MAP", interior.ObjectCode, interior.TmtNames, vtfMaps: interior.VtfMaps);
        var hood1 = res.ResolveFace("V5 FT TP.MAP", interior.ObjectCode, interior.TmtNames, vtfMaps: interior.VtfMaps, frame: 1);
        Assert.NotNull(hood0);
        Assert.NotEqual(hood0, hood1);
    }
}
