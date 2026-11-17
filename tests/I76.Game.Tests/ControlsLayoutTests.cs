using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Formats.Tests;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Control Configuration's rows on record 16's sheet: every row in a ruled row of its own, clear of the buttons, the standings' row clickable.</summary>
public class ControlsLayoutTests
{
    /// <summary>Font 0x44's height + 5, as the screen spaces its rows (the art's ruled rows are 17 apart: its command names from y 45, 62, 81, 98 ...).</summary>
    private const int RowH = 17;

    private static (int X, int Y) Cell(bool right, bool name, int row, int rowH = RowH) =>
        ((right ? 333 : 35) + (name ? 10 : 160), ControlsLayout.RowY(row, rowH) + 8);

    private static void LaidOut(int rowH)
    {
        // the left column: the commands, the hint, the standings, the left game keys and Mouse Options, one ruled row each, in order
        int[] left = [ControlsLayout.HintRow, ControlsLayout.ScoresRow, ControlsLayout.LeftGameRow, ControlsLayout.MouseRow];
        Assert.Equal(Enumerable.Range(ControlsLayout.LeftRows.Length, left.Length), left);
        Assert.Equal(GameKeyMap.Rebindable.Length - ControlsLayout.RightGameKeys, ControlsLayout.MouseRow - ControlsLayout.LeftGameRow);
        Assert.True(ControlsLayout.MouseRow < ControlsLayout.Rows, "the left column's rows fit the sheet");
        Assert.True(ControlsLayout.RightRows.Length + ControlsLayout.RightGameKeys <= ControlsLayout.Rows, "the right column's rows fit the sheet");
        // the last ruled row's hit box ends above the buttons
        Assert.True(ControlsLayout.RowY(ControlsLayout.Rows - 1, rowH) + ControlsLayout.HitH <= ControlsLayout.ButtonsY);

        // every row's name and binding cell hit its own entry, and no two rows share one
        var seen = new HashSet<int>();
        void Hits(bool right, int row, int entry)
        {
            foreach (var name in new[] { true, false })
            {
                var (x, y) = Cell(right, name, row, rowH);
                Assert.Equal(entry, ControlsLayout.RowAt(x, y, rowH));
            }
            Assert.True(seen.Add(entry), $"entry {entry} on two rows");
        }
        for (int k = 0; k < ControlsLayout.LeftRows.Length; k++) Hits(false, k, ControlsLayout.LeftRows[k]);
        for (int k = 0; k < ControlsLayout.RightRows.Length; k++) Hits(true, k, ControlsLayout.RightRows[k]);
        for (int g = 0; g < ControlsLayout.RightGameKeys; g++) Hits(true, ControlsLayout.RightRows.Length + g, ControlsLayout.GameEntry + g);
        for (int g = ControlsLayout.RightGameKeys; g < GameKeyMap.Rebindable.Length; g++) Hits(false, ControlsLayout.LeftGameRow + g - ControlsLayout.RightGameKeys, ControlsLayout.GameEntry + g);
        Hits(false, ControlsLayout.ScoresRow, ControlsLayout.ScoresEntry);
        Assert.DoesNotContain(ControlsLayout.ScoresEntry, ControlsLayout.LeftRows.Concat(ControlsLayout.RightRows));
        Assert.True(ControlsLayout.ScoresEntry < ControlsLayout.GameEntry, "the standings' row binds as a command, not a game key");

        // the hint and Mouse Options (its own button) bind nothing; nor do the gaps between rows, the gutter or the buttons
        foreach (var row in new[] { ControlsLayout.HintRow, ControlsLayout.MouseRow })
            foreach (var name in new[] { true, false }) { var (x, y) = Cell(false, name, row, rowH); Assert.Equal(-1, ControlsLayout.RowAt(x, y, rowH)); }
        Assert.Equal(-1, ControlsLayout.RowAt(100, ControlsLayout.RowY(ControlsLayout.ScoresRow, rowH) + ControlsLayout.HitH, rowH));
        Assert.Equal(-1, ControlsLayout.RowAt(320, ControlsLayout.RowY(ControlsLayout.ScoresRow, rowH) + 8, rowH));
        Assert.Equal(-1, ControlsLayout.RowAt(400, ControlsLayout.ButtonsY + 5, rowH));
    }

    [Fact]
    public void EveryRowHasARuledRowOfItsOwnAndTheStandingsRowIsHit() => LaidOut(RowH);

    [InstallFact]
    public void TheRowsFitAsTheInstallsFontSpacesThemAndTheWordsFitTheirCells()
    {
        var db = ShellDatabase.Open(TestInstall.Install.FindFile("DATABASE.MW2") ?? TestInstall.Install.FindFile("database.tvf")!);
        var f = db.Font(0x44);
        Assert.Equal(RowH, f.Height + 5);
        LaidOut(f.Height + 5);
        // the standings' name and the one-row hint are whole in their cells (Fit would cut them with dots)
        Assert.True(f.Measure(ControlsScreen.ScoresName) <= ControlsScreen.NameW);
        Assert.True(f.Measure(ControlsScreen.HintName) <= ControlsScreen.NameW);
        Assert.True(f.Measure(ControlsScreen.HintDo) <= ControlsScreen.LeftBindW);
        Assert.True(f.Measure("-----") <= ControlsScreen.LeftBindW);
    }
}
