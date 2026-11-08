using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Load Bookmark's Delete stands on the sheet: inside its 186..452 columns, clear of the list's rows and of the other
/// buttons. Load Bookmark has no other button of the port's (Import Saves and All Scenes are on the shell's Developer panel).
/// </summary>
public class BookmarkDeletePlaceTests
{
    // the small font's widths are not needed exactly: a generous 6 px a character
    private static int Measure(string s) => 6 * s.Length;

    [Theory]
    [InlineData(false)]   // Interstate '76
    [InlineData(true)]    // Nitro's
    public void DeleteStandsOnTheSheet(bool nitro)
    {
        var (x, y) = LoadBookmarkScreen.DeletePlace(nitro, Measure);
        Assert.InRange(x, 186, 452 - Measure("Delete"));
        if (nitro) Assert.Equal(420, y);                                         // the buttons' line, left of Load at 328
        else Assert.Equal(158, y);                                               // the scroll-up arrow's line, above the first row at 184
        if (nitro) Assert.True(x + Measure("Delete") <= 328 - 25);
        else Assert.Equal(250, x);                                               // beside the scroll-up arrow (201, 20 wide)
    }
}
