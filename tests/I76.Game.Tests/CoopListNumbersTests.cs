using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The host form's mission list rows (#234: the co-op trip list showed some missions without their numbers).</summary>
public class CoopListNumbersTests
{
    // a handwriting 9 px a letter and a smaller one 7 px a letter, in the list's 182 px
    private static int Big(string s) => 9 * s.Length;
    private static int Small(string s) => 7 * s.Length;

    [Fact]
    public void ATripMissionTooLongForTheRowKeepsItsNumber()
    {
        // it fits: as it is
        Assert.Equal(("1. Short Name", false), MeleeScreen.ListRowText("1. Short Name", Big, Small, 182));
        // too long for the handwriting: the smaller one, number and all (the row used to drop the "7. " instead)
        Assert.Equal(("7. A Rather Longer Title", true), MeleeScreen.ListRowText("7. A Rather Longer Title", Big, Small, 189));
        // too long even so: cut at the end, never at the number
        var (text, smaller) = MeleeScreen.ListRowText("11. A Title Far Too Long For Any Row", Big, Small, 182);
        Assert.True(smaller);
        Assert.StartsWith("11. A Title", text);
        Assert.True(Small(text) <= 182);
        // a name that is not a trip mission's is drawn the same way
        Assert.Equal(("Some Arena", false), MeleeScreen.ListRowText("Some Arena", Big, Small, 182));
    }

    [Fact]
    public void EveryTripRowShowsItsNumberFirstHoweverLongItsTitle()
    {
        // titles of every length up to far past the row (the trip's own are read from the install, not kept here)
        for (int n = 1; n <= 17; n++)
            for (int len = 1; len <= 60; len += 7)
            {
                Assert.StartsWith($"{n}. ", MeleeScreen.ListRowText($"{n}. {new string('x', len)}", Big, Small, 182).Text);
                Assert.StartsWith($"{n}. ", MeleeScreen.ListRowText($"{n}. {new string('x', len)}", Big, Small, 182, smaller: true).Text);
            }
    }

    [Fact]
    public void AListDrawnSmallerIsSmallerInEveryRow()
    {
        // a co-op list with a title too wide for the handwriting draws every row in the smaller one, so the trip's rows read alike
        Assert.Equal(("1. Short Name", true), MeleeScreen.ListRowText("1. Short Name", Big, Small, 182, smaller: true));
        Assert.Equal(("Some Arena", true), MeleeScreen.ListRowText("Some Arena", Big, Small, 182, smaller: true));
    }
}
