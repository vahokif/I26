using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// A co-op lobby's start held for games still loading (#196): the scoreboard's trailer that names whom it waits for, and the notice a
/// game shows from it until the mission starts.
/// </summary>
public class CoopStartWaitTests
{
    private static readonly List<PlayerRow> Rows =
    [
        new(0, "Hal", 0, 0, 0, 0, 30, 1, true), new(1, "Ann", 0, 0, 0, 0, 30, 2, true), new(2, "Bob", 0, 0, 0, 0, 30, 3, true),
    ];

    private static (Scoreboard Board, byte[] Bytes) Through(Scoreboard s)
    {
        var w = new NetWriter();
        s.Write(w);
        var bytes = w.Span.ToArray();
        var r = new NetReader(bytes);
        r.U8();
        var back = Scoreboard.Read(ref r);
        Assert.Equal(0, r.Remaining);
        return (back, bytes);
    }

    [Fact]
    public void TheWaitIsATrailerOnlyWhileTheStartIsHeld()
    {
        var (plain, plainBytes) = Through(new Scoreboard(0, 0, Rows));
        Assert.Null(plain.StartWait);
        var (held, heldBytes) = Through(new Scoreboard(0, 0, Rows) { StartWait = new CoopStartWait([1, 2], 87) });
        Assert.Equal([1, 2], held.StartWait!.Slots);
        Assert.Equal(87, held.StartWait.SecondsLeft);
        // the board itself is byte for byte the plain one: a reader that does not know the trailer stops before it
        Assert.Equal(plainBytes, heldBytes[..plainBytes.Length]);
        // an empty wait is no wait: nothing is written
        Assert.Equal(plainBytes, Through(new Scoreboard(0, 0, Rows) { StartWait = new CoopStartWait([], 87) }).Bytes);
    }

    [Fact]
    public void TheNoticeNamesTheOthersUntilTheMissionStarts()
    {
        var board = new Scoreboard(5, 0, Rows) { StartWait = new CoopStartWait([1, 2], 87) };
        // Hal's game: Ann and Bob are still loading
        var n = CoopStartNotice.Of(board, 5, 0)!;
        Assert.Equal(["Ann", "Bob"], n.Names);
        Assert.Equal("*** Waiting for Ann and Bob to finish loading", n.Line);
        Assert.Equal("Starting without them in 87 s", n.Countdown);
        // Ann's game draws the mission, so it has loaded: only Bob (her own slot is the server's news a moment late)
        Assert.Equal(["Bob"], CoopStartNotice.Of(board, 5, 1)!.Names);
        Assert.Equal("*** Waiting for Bob to finish loading", CoopStartNotice.Of(board, 5, 1)!.Line);
        // the server's tick has moved on past the board's: the mission started, the notice goes before the next board
        Assert.Null(CoopStartNotice.Of(board, 6, 0));
        // nothing held, or only for oneself
        Assert.Null(CoopStartNotice.Of(new Scoreboard(5, 0, Rows), 5, 0));
        Assert.Null(CoopStartNotice.Of(new Scoreboard(5, 0, Rows) { StartWait = new CoopStartWait([0], 87) }, 5, 0));
        Assert.Null(CoopStartNotice.Of(null, 5, 0));
        // three names, and a slot the board does not list yet
        var three = CoopStartNotice.Of(new Scoreboard(5, 0, Rows) { StartWait = new CoopStartWait([0, 2, 6], 3) }, 5, 1)!;
        Assert.Equal("Hal, Bob and player 7", three.Who);
    }
}
