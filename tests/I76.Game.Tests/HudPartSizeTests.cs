using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Play Options' Chase HUD Top Part and Chase HUD Bottom Part (docs/playing.md, docs/deviations.md): how big each half of the
/// 2D instruments over a chase view is drawn. The original draws both at full size; the top row defaults to half, the bottom
/// row stays as the original. The cockpit's own dash is never scaled.
/// </summary>
public class HudPartSizeTests
{
    [Fact] public void TheTopRowStartsAtHalf() => Assert.Equal(1, new ShellSettings().HudTopRow);
    [Fact] public void TheBottomRowStartsAtTheOriginalsOwnSize() => Assert.Equal(2, new ShellSettings().HudBottomRow);
    [Fact] public void HalfAndWholeAreWhatTheySay()
    {
        Assert.Equal(0.5f, I76.Sim.Views.HudScale.Of(new ShellSettings().HudTopRow));
        Assert.Equal(1f, I76.Sim.Views.HudScale.Of(new ShellSettings().HudBottomRow));
    }

    /// <summary>The offered sizes, in the order a click steps through them.</summary>
    [Fact] public void ThreeSizesAreOfferedToBothRows() => Assert.Equal(["1/4", "1/2", "1/1"], I76.Sim.Views.HudScale.Names);

    [Fact] public void TheTopRowIsAPlayOptionsRow() => Assert.Contains(PlayOptionsScreen.HudTopPartRow, PlayOptionsScreen.PortRows);
    [Fact] public void TheBottomRowIsAPlayOptionsRow() => Assert.Contains(PlayOptionsScreen.HudBottomPartRow, PlayOptionsScreen.PortRows);

    /// <summary>The file keeps the denominator the row is named for (1/2 is 2), and the default is not written.</summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void AFileKeepsItsDenominator(int denominator, int choice)
    {
        Assert.Equal(choice, I76.Sim.Views.HudScale.Parse(denominator, 1));
        Assert.Equal(denominator, I76.Sim.Views.HudScale.Denominator(choice));
    }
}
