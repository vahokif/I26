using I76.Formats.Input;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The scrolling list of the notepad sheets (Graphic Detail, Play Options): which rows show where, the keys, the pad's edges and the scroll mark.</summary>
public class ScrollRowsTests
{
    private sealed class Row(string name) { public override string ToString() => name; }

    /// <summary>Eleven rows (a label and a value each, the value 2 px lower) in a window of six lines from 277.</summary>
    private static (ScrollRows<Row> List, Row[] Labels, Row[] Values) Eleven()
    {
        var list = new ScrollRows<Row> { Top = 277, Shown = 6 };
        var labels = new Row[11];
        var values = new Row[11];
        for (int i = 0; i < 11; i++)
        {
            labels[i] = list.Add(new Row($"label {i}"), i);
            values[i] = list.Add(new Row($"value {i}"), i, 2);
        }
        return (list, labels, values);
    }

    private static Dictionary<Row, (int Y, bool Shows)> Placed(ScrollRows<Row> list) => list.Layout().ToDictionary(p => p.It, p => (p.Y, p.Shows));

    [Fact]
    public void The_first_rows_show_from_the_top_line()
    {
        var (list, labels, values) = Eleven();
        var at = Placed(list);
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(i < 6, at[labels[i]].Shows);
            Assert.Equal(277 + 23 * i, at[labels[i]].Y);
            Assert.Equal(277 + 23 * i + 2, at[values[i]].Y);
        }
        Assert.Equal(5, list.MaxScroll);
    }

    [Fact]
    public void Scrolling_stops_at_either_end()
    {
        var (list, labels, _) = Eleven();
        Assert.False(list.ScrollBy(-1));
        Assert.True(list.ScrollBy(100));
        Assert.Equal(5, list.Scroll);
        Assert.False(list.ScrollBy(1));
        var at = Placed(list);
        Assert.False(at[labels[4]].Shows);
        Assert.True(at[labels[5]].Shows);
        Assert.Equal(277, at[labels[5]].Y);
        Assert.True(at[labels[10]].Shows);
        Assert.Equal(277 + 23 * 5, at[labels[10]].Y);
        Assert.Equal(10, list.SlotOn(5));
        Assert.Equal(-1, list.SlotOn(6));
    }

    [Fact]
    public void Keys_step_a_row_or_a_window()
    {
        var (list, _, _) = Eleven();
        Assert.Equal(1, list.KeyStep(up: false, down: true, pageUp: false, pageDown: false));
        Assert.Equal(-1, list.KeyStep(up: true, down: false, pageUp: false, pageDown: false));
        Assert.Equal(6, list.KeyStep(up: false, down: false, pageUp: false, pageDown: true));
        Assert.Equal(-6, list.KeyStep(up: false, down: false, pageUp: true, pageDown: false));
        Assert.Equal(0, list.KeyStep(false, false, false, false));
    }

    [Fact]
    public void The_pad_scrolls_past_the_first_or_last_visible_row_only()
    {
        var (list, labels, values) = Eleven();
        Assert.Equal(-1, list.EdgeStep(values[0], NavDir.Up));      // past the top: asks for a row up (there is none yet, so ScrollBy refuses)
        Assert.Equal(0, list.EdgeStep(values[0], NavDir.Down));
        Assert.Equal(1, list.EdgeStep(values[5], NavDir.Down));
        Assert.Equal(0, list.EdgeStep(values[3], NavDir.Down));
        Assert.Equal(0, list.EdgeStep(values[5], NavDir.Left));
        Assert.Equal(0, list.EdgeStep(new Row("not listed"), NavDir.Down));
        list.ScrollBy(1);
        Assert.Equal(-1, list.EdgeStep(labels[1], NavDir.Up));
        Assert.Equal(1, list.EdgeStep(labels[6], NavDir.Down));
    }

    [Fact]
    public void The_mark_shows_only_when_there_is_more_and_its_arrows_say_where()
    {
        var short_ = new ScrollRows<Row> { Top = 256, Shown = 8 };
        for (int i = 0; i < 8; i++) short_.Add(new Row($"{i}"), i);
        Assert.Null(short_.Mark());

        var (list, _, _) = Eleven();
        var top = list.Mark()!.Value;
        Assert.False(top.MoreAbove);
        Assert.True(top.MoreBelow);
        Assert.Equal(top.Top, top.ThumbAt);
        list.ScrollBy(100);
        var bottom = list.Mark()!.Value;
        Assert.True(bottom.MoreAbove);
        Assert.False(bottom.MoreBelow);
        Assert.Equal(bottom.Top + bottom.H, bottom.ThumbAt + bottom.Thumb);
    }

    [Theory]
    [InlineData(false, new[] { 139, 162, 185, 208, 231, 254, 415 })]    // the original's six check rows (closed up) and Difficulty
    [InlineData(true, new[] { 139, 185, 323 })]                         // Nitro's Arcade Phys., Unlim. Wiper Fluid and Difficulty
    public void Play_options_scroll_between_the_originals_rows_on_the_sheet(bool nitro, int[] original)
    {
        var (top, shown) = PlayOptionsScreen.ListLines(nitro);
        var lines = Enumerable.Range(0, shown).Select(i => top + 23 * i).ToArray();
        Assert.Empty(lines.Intersect(original));
        Assert.All(lines, y => Assert.True(y >= 116 && y <= 415 && (y - 116) % 23 == 0));    // on the sheet's fourteen ruled lines
        Assert.True(shown < PlayOptionsScreen.PortRows.Length);                              // so it scrolls
        Assert.Contains(lines[^1] + 23, original);                                           // the list ends on Difficulty's line
    }

    [Fact]
    public void Play_options_keep_their_order_with_the_former_second_page_last()
    {
        Assert.Equal(
            ["Hidden Wpns & Cars", "Modern Physics", "Simulation Rate", "Checkpoints", "Force Feedback", "Auto Reverse", "Map Downloads",
             "Unlim. Turrets", "CB Voice", "Check for Updates", "Chase Crosshair"],
            PlayOptionsScreen.PortRows);
    }
}
