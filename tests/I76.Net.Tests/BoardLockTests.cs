using I76.Net.Hud;
using I76.Sim.Missions;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The score window locked up by a tap of its key, its cursor and its rows under the pointer (#107).</summary>
public class BoardLockTests
{
    private static List<PlayerRow> Rows(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new PlayerRow((byte)i, $"p{i}", 0, i * 10, i, 0, 50, 100 + i, true))];

    [Fact] public void HoldingShowsTheWindowOnlyWhileHeld()
    {
        var b = new BoardLock();
        Assert.False(b.Update(false, 0));
        Assert.True(b.Update(true, 1));
        Assert.True(b.Update(true, 2));
        Assert.False(b.Update(false, 2.5));     // held 1.5 s: shut on release, as the original's
        Assert.False(b.Locked);
    }

    [Fact] public void ATapLocksAndTheNextTapUnlocks()
    {
        var b = new BoardLock();
        b.Update(true, 1);
        Assert.True(b.Update(false, 1 + BoardLock.Tap - 0.01));
        Assert.True(b.Locked);
        Assert.True(b.Update(false, 30));       // stays up with the key up
        Assert.True(b.Update(true, 31));        // the next press unlocks, up while held
        Assert.False(b.Locked);
        Assert.False(b.Update(false, 31.1));    // and a tap shuts it rather than locking again
        Assert.False(b.Locked);
    }

    [Fact] public void ALongPressWhileLockedShutsOnRelease()
    {
        var b = new BoardLock();
        b.Update(true, 0); b.Update(false, 0.1);
        Assert.True(b.Update(true, 5));
        Assert.True(b.Update(true, 7));
        Assert.False(b.Update(false, 8));
    }

    [Fact] public void EscapeClosesTheLock()
    {
        var b = new BoardLock();
        b.Update(true, 0); b.Update(false, 0.1);
        b.Close();
        Assert.False(b.Update(false, 1));
    }

    [Fact] public void ThePointerFindsTheRowUnderIt()
    {
        var rows = new List<(HudRect, int)> { (new HudRect(10, 100, 300, 30), 4), (new HudRect(10, 130, 300, 30), 7) };
        Assert.Equal(4, BoardCursor.RowAt(rows, 20, 100));
        Assert.Equal(7, BoardCursor.RowAt(rows, 309, 130));     // the edge between rows is the lower row's
        Assert.Equal(-1, BoardCursor.RowAt(rows, 311, 140));
        Assert.Equal(-1, BoardCursor.RowAt(rows, 20, 160));
    }

    [Fact] public void TheOneNineSevenLookRowsSitUnderTheHeads()
    {
        // 480 lines: the window at (50, 150), 20-pixel lines; line 0 is the heads
        var r = HudLayout.ClassicRow(640, 480, 1);
        Assert.Equal(new HudRect(50, 170, 590, 20), r);
        var big = HudLayout.ClassicRow(1920, 960, 3);
        Assert.Equal(150 * 2 + 3 * 40, big.Y, 3);
        Assert.Equal(40, big.H, 3);
    }

    [Fact] public void TheCursorStepsOverPlayerRowsAndOffThePage()
    {
        var c = new ScoreContext(MeleeKind.Combat, true, 0, true, ScoreOrder.Ranked, ScoreTable.ModernRows);
        var teams = Rows(12).Select(p => p with { Team = (byte)(p.Slot % 2 + 1) }).ToList();
        var v = ScoreTable.Players(teams, c, 0, t => $"t{t}");
        Assert.Contains(v.Lines, l => l.Kind == ScoreLineKind.Team);
        var players = v.Lines.Where(l => l.Kind == ScoreLineKind.Player).Select(l => l.Slot).ToList();
        // no cursor yet: down takes the first row, up the last
        Assert.Equal((players[0], 0), BoardCursor.Step(v.Lines, -1, 1));
        Assert.Equal((players[^1], 0), BoardCursor.Step(v.Lines, -1, -1));
        // team rows are stepped over
        for (int i = 0; i + 1 < players.Count; i++) Assert.Equal((players[i + 1], 0), BoardCursor.Step(v.Lines, players[i], 1));
        // past the ends: the next or the previous page
        Assert.Equal((-1, 1), BoardCursor.Step(v.Lines, players[^1], 1));
        Assert.Equal((-1, -1), BoardCursor.Step(v.Lines, players[0], -1));
        Assert.Equal(players[0], BoardCursor.End(v.Lines, last: false));
        Assert.Equal(players[^1], BoardCursor.End(v.Lines, last: true));
    }

    [Fact] public void OnlyTheHostBootsAndNeverItself()
    {
        var host = new ScoreContext(MeleeKind.Combat, false, 0, true, ScoreOrder.Slot, ScoreTable.ClassicRows);
        var v = ScoreTable.Players(Rows(5), host, 0, t => "");
        var own = v.Lines.Single(l => l.Slot == 0);
        var other = v.Lines.Single(l => l.Slot == 3);
        Assert.False(BoardCursor.CanBoot(true, own));
        Assert.True(BoardCursor.CanBoot(true, other));
        Assert.False(BoardCursor.CanBoot(false, other));
        var team = new ScoreLine(ScoreLineKind.Team, "t1", 1, 0, 0, 0);
        Assert.False(BoardCursor.CanBoot(true, team));
    }
}
