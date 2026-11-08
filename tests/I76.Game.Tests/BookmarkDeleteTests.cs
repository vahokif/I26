using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Deleting a Load Bookmark row: the others keep their order and stems, the selection lands on a neighbour.</summary>
public class BookmarkDeleteTests
{
    private static Bookmarks Three()
    {
        var b = new Bookmarks();
        b.Entries.Add(new Bookmarks.Entry(1, "A", "save000", 0, false));
        b.Entries.Add(new Bookmarks.Entry(2, "B", "save001", 0, false));
        b.Entries.Add(new Bookmarks.Entry(3, "C", "save002", 1, true));
        return b;
    }

    [Fact]
    public void DeletingTheMiddleOfThreeKeepsTheOtherTwoWithTheirStems()
    {
        var b = Three();
        int sel = b.RemoveAt(1);
        Assert.Equal(["save000", "save002"], b.Entries.Select(e => e.Stem));
        Assert.Equal(["A", "C"], b.Entries.Select(e => e.Name));
        Assert.Equal(1, sel);                       // C moved up into the picked row
        Assert.Equal("save003", b.NextStem());      // the numbering still goes past the largest
    }

    [Fact]
    public void DeletingTheLastSelectedRowSelectsTheNewLastAndTheOnlyRowLeavesNone()
    {
        var b = Three();
        Assert.Equal(1, b.RemoveAt(2));
        Assert.Equal(0, b.RemoveAt(0));
        Assert.Equal(-1, b.RemoveAt(0));
        Assert.Empty(b.Entries);
    }
}
