using I76.Game.Shell;
using I76.Sim.Views;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Play Options' Original Chase Framing (docs/deviations.md): the F2 chase view opens at, and End puts it back to, the original's
/// 30 degrees and zoom 3 rather than the port's 16 and 1.7. Off for a new player, and off for a file without
/// <c>[port] chase_framing_original</c> — the port's framing is what every settings file so far meant, which is why the row
/// costs no settings version step.
/// </summary>
public class ChaseFramingSettingTests
{
    [Fact]
    public void ANewPlayerHasItOff() => Assert.False(new ShellSettings().OriginalChaseFraming);

    /// <summary>The row's two framings, as the row's own numbers: the original's, and the port's it is measured against.</summary>
    [Fact]
    public void TheRowOffersTheOriginalsFramingAgainstThePorts() =>
        Assert.Equal([(0.5235988f, 3f), (0.2839724f, 1.7f)],
            [(ChaseFraming.OriginalPitch, ChaseFraming.OriginalZoom), (ChaseFraming.PortPitch, ChaseFraming.PortZoom)]);

    /// <summary>One label for both places the row is: the shell sheet's scrolling list and the mission menu's Play Options.</summary>
    [Fact]
    public void ItIsAPlayOptionsRow() => Assert.Contains(PlayOptionsScreen.ChaseFramingRow, PlayOptionsScreen.PortRows);
}
