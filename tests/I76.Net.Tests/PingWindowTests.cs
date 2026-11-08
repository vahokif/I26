using Xunit;

namespace I76.Net.Tests;

/// <summary>The ping shown in the score window and the HUD (<see cref="PingWindow"/>).</summary>
public class PingWindowTests
{
    /// <summary>
    /// A 60 ms link with 10 % loss, as the transport sampled it (measured: median 155 ms, up to 624): the ping reads the
    /// link, not the inflated samples.
    /// </summary>
    [Fact]
    public void ALossyLinkShowsItsRoundTrip()
    {
        var p = new PingWindow();
        Assert.Equal(0, p.Low);
        int[] samples = [66, 155, 624, 210, 62, 140, 180, 98, 155, 301, 66, 155, 470, 120, 64, 155];
        var shown = new List<int>();
        foreach (int s in samples) { p.Add(s); shown.Add(p.Low); }
        Assert.All(shown, s => Assert.InRange(s, 62, 66));
    }

    /// <summary>A link that gets slower shows it once the faster samples have left the window, ten samples on.</summary>
    [Fact]
    public void ASlowerLinkShowsAfterTheWindow()
    {
        var p = new PingWindow();
        for (int i = 0; i < 20; i++) p.Add(60);
        for (int i = 0; i < PingWindow.Size - 1; i++) { p.Add(300); Assert.Equal(60, p.Low); }
        p.Add(300);
        Assert.Equal(300, p.Low);
        p.Add(40);
        Assert.Equal(40, p.Low);
    }
}
