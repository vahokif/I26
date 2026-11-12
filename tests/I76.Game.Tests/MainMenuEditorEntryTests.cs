using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>#156: the main menu's MAP EDITOR entry lies clear of every hover banner, so lighting a button never cuts into it.</summary>
public class MainMenuEditorEntryTests
{
    [Fact]
    public void TheEditorEntrySitsAboveTheBanners()
    {
        var (x, y, w, h) = MainMenuScreen.EditorEntry;
        // the banners span x 123 (Nitro's 400-wide ones) to 530 from y 452 (Nitro) and 460 (base) down
        Assert.True(y + h <= MainMenuScreen.BannerTop, $"entry bottom {y + h}");
        Assert.True(x >= 0 && x + w <= 640 && y >= 0);
        // below the rows of buttons (the lowest, the connections, at y 395, about 20 high)
        Assert.True(y >= 395 + 16, $"entry top {y}");
    }
}
