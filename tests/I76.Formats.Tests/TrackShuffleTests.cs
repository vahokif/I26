using I76.Formats.Audio;
using Xunit;

namespace I76.Formats.Tests;

public class TrackShuffleTests
{
    [Fact]
    public void NeverRepeatsTheLastTrackAndReachesTheOthers()
    {
        var rand = new Random(7);
        foreach (int count in new[] { 2, 3, 16 })
        {
            var seen = new HashSet<int>();
            int last = -1;
            for (int i = 0; i < 2000; i++)
            {
                int k = TrackShuffle.Next(count, last, rand);
                Assert.InRange(k, 0, count - 1);
                Assert.NotEqual(last, k);
                seen.Add(k);
                last = k;
            }
            Assert.Equal(count, seen.Count);
        }
    }

    [Fact]
    public void EveryOtherTrackFollowsEachTrack()
    {
        var rand = new Random(1);
        for (int last = 0; last < 5; last++)
        {
            var next = Enumerable.Range(0, 500).Select(_ => TrackShuffle.Next(5, last, rand)).ToHashSet();
            Assert.Equal(Enumerable.Range(0, 5).Where(t => t != last).ToHashSet(), next);
        }
    }

    [Fact]
    public void OneTrackRepeatsNoneIsMinusOneAndAStaleLastIsIgnored()
    {
        var rand = new Random(3);
        Assert.Equal(0, TrackShuffle.Next(1, 0, rand));
        Assert.Equal(-1, TrackShuffle.Next(0, -1, rand));
        Assert.InRange(TrackShuffle.Next(4, 9, rand), 0, 3);
    }
}
