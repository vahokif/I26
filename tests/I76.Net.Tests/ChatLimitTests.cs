using I76.Server;
using Xunit;

namespace I76.Net.Tests;

public class ChatLimitTests
{
    [Fact] public void FiveLinesInFiveSecondsThenTheExcessIsDropped()
    {
        var l = new ChatLimiter();
        for (int i = 0; i < 5; i++) Assert.True(l.Allow(i * 0.1, out _));
        Assert.False(l.Allow(1.0, out bool first));
        Assert.True(first);
        Assert.False(l.Allow(1.5, out first));
        Assert.False(first);                         // one log line per burst
        Assert.False(l.Allow(4.9, out _));
        Assert.True(l.Allow(5.0, out _));            // the first line has left the window
        Assert.False(l.Allow(5.05, out _));          // 0.1 is still inside
    }

    [Fact] public void ASlowPlayerIsNeverLimited()
    {
        var l = new ChatLimiter();
        for (int i = 0; i < 50; i++) Assert.True(l.Allow(i * 1.2, out _));
    }
}
