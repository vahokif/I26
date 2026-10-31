using I76.Net.Hud;
using I76.Sim.Missions;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The network HUD's logic, shared by both looks: the feed's rate cap, the score window's order, pages and boot numbers, the 1997 look's places.</summary>
public class HudTests
{
    private static List<PlayerRow> Rows(int n, Func<int, int>? team = null) =>
        [.. Enumerable.Range(0, n).Select(i => new PlayerRow((byte)i, $"p{i}", (byte)(team?.Invoke(i) ?? 0), i * 10, i, 0, 50, 100 + i, true))];

    private static string Team(int t) => $"t{t}";

    [Fact] public void FeedShowsFourBulkLinesEveryTwoSecondsAndCountsTheRest()
    {
        var f = new HudFeed();
        for (int i = 0; i < 10; i++) f.Add(new FeedEntry(FeedKind.Join, $"j{i}", 0.1 * i));
        Assert.Equal(4, f.Lines.Count);
        Assert.Equal(6, f.More);
        // chat and notices are never capped
        for (int i = 0; i < 10; i++) Assert.True(f.Add(new FeedEntry(FeedKind.Chat, $"c{i}", 1)));
        Assert.True(f.Add(new FeedEntry(FeedKind.Notice, "*** flag", 1)));
        // a new window two seconds after the first line opens
        Assert.True(f.Add(new FeedEntry(FeedKind.Kill, "k", 2.0)));
        // the "more" count goes after ten seconds without another
        f.Expire(0.9 + HudFeed.Life + 0.01);
        Assert.Equal(0, f.More);
    }

    [Fact] public void FeedKeepsChatForTheHistoryAndDropsOldEvents()
    {
        var f = new HudFeed();
        f.Add(new FeedEntry(FeedKind.Kill, "k", 0));
        for (int i = 0; i < HudFeed.KeepChat + 5; i++) f.Add(new FeedEntry(FeedKind.Chat, $"c{i}", 0));
        f.Expire(100);
        Assert.Equal(HudFeed.KeepChat, f.Lines.Count);
        Assert.All(f.Lines, l => Assert.Equal(FeedKind.Chat, l.Kind));
        Assert.Equal("c5", f.Lines[0].Text);
        // the 1997 look's console: the last seven younger than ten seconds
        Assert.Empty(f.Recent(100, HudFeed.Life, 7));
        Assert.Equal(8, f.Recent(100, double.MaxValue, 8).Count);
    }

    [Fact] public void ChatStaysLongerThanTheConsolesOtherLines()
    {
        var f = new HudFeed();
        f.Add(new FeedEntry(FeedKind.Kill, "k", 0));
        f.Add(new FeedEntry(FeedKind.Chat, "hello", 0));
        // after the other lines' ten seconds only the chat is still shown, and it is until its own span is over
        var shown = f.Recent(HudFeed.Life + 1, HudFeed.ChatLife, 30, l => l.Kind == FeedKind.Chat || HudFeed.Life + 1 - l.At <= HudFeed.Life);
        Assert.Equal(["hello"], shown.Select(l => l.Text));
        Assert.True(HudFeed.ChatLife > HudFeed.Life);
        Assert.Empty(f.Recent(HudFeed.ChatLife + 1, HudFeed.ChatLife, 30));
    }

    [Fact] public void FadeIsWholeUntilTheLastPartOfALinesLife()
    {
        Assert.Equal(1f, HudFeed.Alpha(0, 10));
        Assert.Equal(1f, HudFeed.Alpha(8.5, 10));
        Assert.Equal(0.5f, HudFeed.Alpha(9.25, 10), 3);
        Assert.Equal(0f, HudFeed.Alpha(10, 10));
    }

    [Fact] public void RanksAreCompetitionRanks() => Assert.Equal([1, 2, 2, 4], ScoreTable.Ranks([9, 5, 5, 1]));

    [Fact] public void ClassicWindowIsSlotOrderTenRowsAPageWithOwnRowKept()
    {
        var c = new ScoreContext(MeleeKind.Combat, false, 25, false, ScoreOrder.Slot, ScoreTable.ClassicRows);
        var v = ScoreTable.Players(Rows(32), c, 0, Team);
        Assert.Equal(4, v.Pages);
        Assert.Equal(11, v.Lines.Count);                     // ten and one's own, kept in view
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 25], v.Lines.Select(l => l.Slot));
        Assert.True(v.Lines[^1].Pinned);
        Assert.Equal(">", v.Lines[^1].Mark);
        Assert.Empty(v.Numbered);
        // a page past the end is clamped
        Assert.Equal(3, ScoreTable.Players(Rows(32), c, 9, Team).Page);
    }

    [Fact] public void HostNumbersNineOthersAPageAndMarksItsOwnH()
    {
        var c = new ScoreContext(MeleeKind.Combat, false, 0, true, ScoreOrder.Slot, ScoreTable.ClassicRows);
        var v = ScoreTable.Players(Rows(20), c, 1, Team);
        Assert.Equal(3, v.Pages);                            // 19 others, nine a page
        Assert.Equal("H", v.Lines.Single(l => l.Slot == 0).Mark);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17, 18], v.Numbered.Select(n => (int)n.Slot));
        Assert.Equal("1", v.Lines.Single(l => l.Slot == 10).Mark);
        Assert.Equal("9", v.Lines.Single(l => l.Slot == 18).Mark);
        Assert.True(v.Lines.Single(l => l.Slot == 0).IsHost);
    }

    [Fact] public void ModernWindowRanksBestFirstAndNumbersInThatOrder()
    {
        var c = new ScoreContext(MeleeKind.Combat, false, 0, true, ScoreOrder.Ranked, ScoreTable.ModernRows);
        var v = ScoreTable.Players(Rows(12), c, 0, Team);
        Assert.Equal([11, 10, 9, 8, 7, 6, 5, 4, 3, 0], v.Lines.Select(l => l.Slot));   // the host's own row in its ranked place
        Assert.Equal(11, v.Numbered[0].Slot);
        Assert.Equal(1, v.Lines[0].Rank);
        // a client's page is two columns of sixteen
        var cc = c with { Host = false, MySlot = 0 };
        var p = ScoreTable.Players(Rows(48), cc, 0, Team);
        Assert.Equal(2, p.Pages);
        Assert.Equal(33, p.Lines.Count);
        Assert.Equal(0, p.Lines[^1].Slot);
    }

    [Fact] public void TeamPlayGroupsByTeamWithTotalsAndCtfCapturesAreNotSummed()
    {
        var rows = Rows(8, i => 1 + i % 2);
        var c = new ScoreContext(MeleeKind.Combat, true, 0, false, ScoreOrder.Ranked, ScoreTable.ModernRows);
        var v = ScoreTable.Players(rows, c, 0, Team);
        Assert.Equal(ScoreLineKind.Team, v.Lines[0].Kind);
        Assert.Equal(2, v.Lines[0].Team);                   // odd slots score more
        Assert.Equal(10 + 30 + 50 + 70, v.Lines[0].Score);
        Assert.Equal(10, v.Lines.Count);
        Assert.Equal(1, v.Lines[1].Rank);
        var ctf = rows.Select(r => r with { Kills = r.Team == 1 ? 3 : 1 }).ToList();
        var t = ScoreTable.Teams(ctf, MeleeKind.Ctf, Team);
        Assert.Equal(3, t[0].Kills);
        Assert.Equal(1, t[1].Kills);
    }

    [Fact] public void ClassicPlacesAreTheOriginalsFromTheLeftEdge()
    {
        var c = HudLayout.ClassicConsole(1920, 1080, 5);
        Assert.Equal(50 * 2.25f, c.X);
        Assert.Equal(150 * 2.25f, c.Y);
        var e = HudLayout.ClassicEntry(1920, 1080);
        Assert.Equal(360 * 2.25f, e.Y);
        Assert.Equal(c.X, e.X);
        // a 4K window doubles the modern look; a narrow one scales by its width
        Assert.Equal(2f, HudLayout.ModernScale(3840, 2160));
        Assert.Equal(1280 / 1440f, HudLayout.ModernScale(1280, 1024), 3);
        Assert.Equal([16, 16], HudLayout.Columns(32, 16));
        Assert.Equal([9], HudLayout.Columns(9, 16));
        Assert.Equal([17, 16], HudLayout.Columns(33, 16));
    }

    // #120: a 2560x1440 window on a second monitor right of a 1920 one: the platform reports its safe area in desktop coordinates
    [Fact]
    public void PlatformSafeAreaIsTakenInTheWindowsOwnCoordinates()
    {
        var onSecond = HudLayout.PlatformSafe(new HudRect(1920, 0, 2560, 1400), 1920, 0, 2560, 1440);
        Assert.Equal(new HudRect(0, 0, 2560, 1400), onSecond);
        Assert.Equal(0f, HudLayout.Safe(2560, 1440, onSecond).X - 36f);   // the frame's own 2.5 % margin, not 1920
        Assert.Null(HudLayout.PlatformSafe(new HudRect(0, 0, 1920, 1080), 1920, 0, 2560, 1440));   // another monitor's
        Assert.Null(HudLayout.PlatformSafe(new HudRect(0, 0, 0, 0), 0, 0, 2560, 1440));
        Assert.Equal(new HudRect(10, 30, 1900, 1050), HudLayout.PlatformSafe(new HudRect(10, 30, 1900, 1050), 0, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(1920, 1080)] [InlineData(2560, 1080)] [InlineData(1440, 1080)] [InlineData(1280, 720)] [InlineData(3440, 1440)]
    public void SideColumnsClearTheInstrumentsOrGoToTheTop(float w, float h)
    {
        var safe = HudLayout.Safe(w, h);
        var inst = HudLayout.TopInstruments(w, h, false);
        float s = HudLayout.ModernScale(w, h), colW = 340 * s;
        float left = HudLayout.ColumnTop(inst, safe.X, safe.X + colW, safe.Y, 12 * s);
        float right = HudLayout.ColumnTop(inst, safe.Right - 300 * s, safe.Right, safe.Y, 12 * s);
        float k = h / 480f, ox = (w - 640 * k) / 2;
        foreach (var (top, x0, x1) in new[] { (left, safe.X, safe.X + colW), (right, safe.Right - 300 * s, safe.Right) })
        {
            Assert.True(top >= safe.Y);
            foreach (var b in inst)
                if (b.X < x1 && b.Right > x0) Assert.True(top >= b.Bottom, "overlaps an instrument");
            Assert.True(top <= 130 * k + 12 * s + 0.01f, "never lower than the instruments' foot");
        }
        // a window wide enough that the radar starts right of the left column: that column is at the top
        if (ox > safe.X + colW) Assert.Equal(safe.Y, left);
    }

    [Fact] public void NoInstrumentsMeansTheSafeTop()
    {
        var safe = HudLayout.Safe(1920, 1080);
        Assert.Equal(safe.Y, HudLayout.ColumnTop([], 0, 1920, safe.Y, 10));
        Assert.Equal(safe.Y, HudLayout.UnderAll([], safe.Y, 10));
    }

    [Fact] public void TheThreeDDashReachesFurtherLeft()
    {
        var two = HudLayout.TopInstruments(1920, 1080, true);
        var three = HudLayout.TopInstruments(1920, 1080, true, dash3d: true);
        Assert.True(three[0].X < two[0].X);
        Assert.Equal(two[0].Right, three[0].Right, 2);
        Assert.Equal(two[1], three[1]);
    }

    [Fact] public void TheMirrorOnlyJoinsTheTwoDDash()
    {
        Assert.Equal(3, HudLayout.TopInstruments(1920, 1080, false).Length);
        var all = HudLayout.TopInstruments(1920, 1080, true);
        Assert.Equal(4, all.Length);
        Assert.Equal(189 * 2.25f, HudLayout.UnderAll(all, 0, 0), 2);
        // the radar's foot at 1080 lines: 119 of 480 lines
        Assert.Equal(119 * 2.25f, HudLayout.TopInstruments(1920, 1080, false)[0].Bottom, 2);
    }

    [Fact] public void RacePlaceSharesTies() => Assert.Equal(2, ScoreTable.Place(3, [4, 3, 3, 1]));
}
