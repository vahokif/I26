using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

public class FrameLimitTests
{
    [Fact]
    public void AMissingKeyIsUnlimited() => Assert.Equal(FrameLimit.Unlimited, FrameLimit.Parse(null));

    [Fact]
    public void AValueThatIsNotACapIsUnlimited()
    {
        Assert.Equal(FrameLimit.Unlimited, FrameLimit.Parse(59));
        Assert.Equal(FrameLimit.Unlimited, FrameLimit.Parse(-5));
    }

    [Fact]
    public void EveryChoiceRoundTrips()
    {
        for (int i = 0; i < FrameLimit.Caps.Length; i++) Assert.Equal(i, FrameLimit.Parse(FrameLimit.Fps(i)));
        Assert.Equal(0, FrameLimit.Fps(FrameLimit.Unlimited));
        Assert.Equal(FrameLimit.Caps.Length, FrameLimit.Names.Length);
        Assert.Equal(30, FrameLimit.Fps(0));
        Assert.Equal(240, FrameLimit.Fps(6));
    }
}
