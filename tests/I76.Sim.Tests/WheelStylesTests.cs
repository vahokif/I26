using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The garage's wheel pick as a style and a size: the file written is the style's family plus the size letter.</summary>
public class WheelStylesTests
{
    private static PartDef Wheel(string stem) => new("w", 5, 0, "whe01", stem, 0, 100, 20);

    private static readonly PartDef[] All =
    [
        .. new[] { "wauto_0", "wauto_3", "wbtck_1" }.SelectMany(f => "abcd".Select(s => Wheel($"{f}{s}.wdf"))),
        Wheel("wdilo_1a.wdf"), Wheel("wetnk_1a.wdf"),
    ];

    [Fact]
    public void FamilyAndSizeAreTheStemsParts()
    {
        Assert.Equal("wauto_3", WheelStyles.Family("wauto_3b.wdf"));
        Assert.Equal('b', WheelStyles.SizeOf("wauto_3b.wdf"));
        Assert.Equal("wauto_a", WheelStyles.Family("WAUTO_AC.WDF"));
        Assert.Equal('c', WheelStyles.SizeOf("WAUTO_AC.WDF"));
    }

    [Fact]
    public void EachStyleOnceInFileOrder() =>
        Assert.Equal(["wauto_0", "wauto_3", "wbtck_1", "wdilo_1", "wetnk_1"], WheelStyles.Families(All));

    [Fact]
    public void EveryStyleAtEverySizeItHasIsThatFile()
    {
        foreach (var fam in WheelStyles.Families(All))
            foreach (var have in WheelStyles.Sizes(All, fam))
            {
                var pick = WheelStyles.Pick(All, fam, WheelStyles.SizeOf(have.Stem), out bool exact);
                Assert.Equal(have.Stem, pick!.Stem);
                Assert.True(exact);
                Assert.Equal(fam + WheelStyles.SizeOf(have.Stem) + ".wdf", pick.Stem);
            }
    }

    [Theory]
    [InlineData("wdilo_1", 'd', "wdilo_1a.wdf")]    // a style with one size only: that one, whatever was asked
    [InlineData("wetnk_1", 'b', "wetnk_1a.wdf")]
    [InlineData("wauto_3", 'c', "wauto_3c.wdf")]
    public void AMissingSizeFallsToTheNearest(string family, char size, string file)
    {
        var pick = WheelStyles.Pick(All, family, size, out bool exact);
        Assert.Equal(file, pick!.Stem);
        Assert.Equal(file == family + size + ".wdf", exact);
    }

    [Fact]
    public void TheSmallerOfTwoEquallyNear()
    {
        PartDef[] odd = [Wheel("wxxxx_1a.wdf"), Wheel("wxxxx_1c.wdf")];
        Assert.Equal("wxxxx_1a.wdf", WheelStyles.Pick(odd, "wxxxx_1", 'b', out bool exact)!.Stem);
        Assert.False(exact);
        Assert.Null(WheelStyles.Pick(odd, "wnone_1", 'a', out _));
    }

    [Fact]
    public void EveryStyleOfBothGamesHasANameOrItsFileName()
    {
        foreach (var fam in new[] { "wauto_0", "wauto_9", "wauto_a", "wbtck_1", "wctnk_1", "wdilo_1", "wdtnk_1", "wetnk_1", "wftck_1", "wxtcf_1", "wytcb_1" })
            Assert.False(string.IsNullOrWhiteSpace(WheelStyles.NameOf(fam)));
    }
}
