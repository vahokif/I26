using I76.Sim.Rng;
using Xunit;

namespace I76.Sim.Tests;

public class CrtRandTests
{
    [Fact]
    public void MatchesMsvcSequenceFromDefaultSeed()
    {
        var r = new CrtRand();
        Assert.Equal([41, 18467, 6334, 26500, 19169], Enumerable.Range(0, 5).Select(_ => r.Next()));
    }

    [Fact]
    public void OracleReplaysThenRefuses()
    {
        var r = CrtRand.Oracle([7, 9]);
        Assert.Equal(7, r.Next());
        Assert.Equal(9, r.Next());
        Assert.Throws<InvalidOperationException>(() => r.Next());
    }
}
