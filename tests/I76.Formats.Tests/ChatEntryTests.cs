using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class ChatEntryTests
{
    private static void Type(ChatEntry c, string s) { foreach (var ch in s) c.Press(ChatKey.Other, ch); }

    [Fact] public void ClosedTakesNothing() => Assert.Equal(ChatResult.None, new ChatEntry().Press(ChatKey.Enter, '\0'));

    [Fact] public void EnterSendsTheLine()
    {
        var c = new ChatEntry();
        c.Open();
        Type(c, "hi: there");
        Assert.Equal("hi: there", c.Text);
        Assert.Equal(ChatResult.Sent, c.Press(ChatKey.Enter, '\0'));
        Assert.Equal("hi: there", c.Sent);
        Assert.False(c.Active);
    }

    [Fact] public void EscCancelsAndEmptyLinesAreNotSent()
    {
        var c = new ChatEntry();
        c.Open(); Type(c, "oops");
        Assert.Equal(ChatResult.Cancelled, c.Press(ChatKey.Escape, '\0'));
        Assert.False(c.Active);
        c.Open();
        Assert.Equal(ChatResult.Cancelled, c.Press(ChatKey.Enter, '\0'));
        c.Open(); Type(c, "   ");
        Assert.Equal(ChatResult.Cancelled, c.Press(ChatKey.Enter, '\0'));
        Assert.Null(c.Sent);
    }

    [Fact] public void BackspaceAndTheLengthLimit()
    {
        var c = new ChatEntry { MaxLength = 5 };
        c.Open(); Type(c, "abcdefgh");
        Assert.Equal("abcde", c.Text);
        c.Press(ChatKey.Backspace, '\0'); c.Press(ChatKey.Backspace, '\0');
        Assert.Equal("abc", c.Text);
        c.Press(ChatKey.Other, '\0');   // a key with no character types nothing
        Assert.Equal("abc", c.Text);
    }

    [Fact] public void ColonTypesAsTheLayoutPrintsIt() => Assert.Equal(':', TextEntry.Typed(':', ';', shift: true, false));
}
