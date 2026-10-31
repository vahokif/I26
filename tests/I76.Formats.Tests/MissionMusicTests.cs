using I76.Formats.Audio;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>Which track follows which in a mission (issue #23).</summary>
public class MissionMusicTests
{
    private static List<int> Walk(MissionMusic m, int wrld, int n)
    {
        var seen = new List<int>();
        int t = m.First(wrld);
        for (int i = 0; i < n; i++) { seen.Add(t); t = m.Next(t); }
        return seen;
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(2, 2)]
    [InlineData(17, 17)]
    [InlineData(1, 2)]    // the data track: the CD version starts at the first audio track
    [InlineData(0, 2)]
    [InlineData(40, 2)]
    public void TheMissionStartsOnItsTrackOrTheFirstAudioTrack(int wrld, int first) =>
        Assert.Equal(first, new MissionMusic(17).First(wrld));

    [Fact]
    public void Retail1997PlaysOnFromTheMissionsTrackThrough14ThenFrom2()
    {
        var m = new MissionMusic(17, MissionMusic.Policy.Retail1997);
        Assert.Equal([12, 13, 14, 2, 3, 4], Walk(m, 12, 6));
        Assert.Equal(Enumerable.Range(2, 13).Concat([2]), Walk(m, 2, 14));
    }

    [Fact]
    public void Retail1997PlaysTrack15AndUpAlone()
    {
        var m = new MissionMusic(17, MissionMusic.Policy.Retail1997);
        Assert.Equal([15, 2, 3], Walk(m, 15, 3));
        Assert.False(m.Continues(15));
        Assert.False(m.Continues(17));
        Assert.True(m.Continues(14 - 1));
        Assert.False(m.Continues(14));
    }

    [Fact]
    public void Retail1997WaitsForTheNext60SecondPollOnlyBetweenPlays()
    {
        var m = new MissionMusic(17, MissionMusic.Policy.Retail1997);
        Assert.Equal(0, m.Wait(7, 25), 6);        // 7 runs into 8
        Assert.Equal(60 - 25, m.Wait(14, 25), 6);
        Assert.Equal(60 - 1, m.Wait(14, 181), 6);
        Assert.Equal(0, m.Wait(14, 180), 6);
    }

    [Fact]
    public void NitrosRangeRunsToTheDiscsEnd()
    {
        var m = new MissionMusic(11, MissionMusic.Policy.Retail1997);
        Assert.Equal([9, 10, 11, 2, 3], Walk(m, 9, 5));
        Assert.False(m.Continues(11));
    }

    [Fact]
    public void GogPlaysTheMissionsTrackOnceThenTrack2()
    {
        var m = new MissionMusic(17, MissionMusic.Policy.Gog);
        Assert.Equal([5, 2, 2, 2], Walk(m, 5, 4));
        Assert.Equal(35, m.Wait(5, 25), 6);
    }

    [Fact]
    public void TheDefaultGoesOnToTheNextTrackAtOnceAndNeverWaits()
    {
        var m = new MissionMusic(17);
        Assert.Equal(MissionMusic.Policy.Disc, m.Mode);
        Assert.Equal([14, 15, 16, 17, 2, 3], Walk(m, 14, 6));
        Assert.Equal(0, m.Wait(17, 25));
        Assert.Equal(0, m.Wait(14, 25));
        Assert.Equal([9, 10, 11, 2], Walk(new MissionMusic(11), 9, 4));
    }

    [Fact]
    public void TheDiscPolicyWalksEveryAudioTrackAndWraps()
    {
        var m = new MissionMusic(17, MissionMusic.Policy.Disc);
        Assert.Equal([5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 2], Walk(m, 5, 14));
        Assert.Equal(0, m.Wait(17, 37));
        Assert.Equal(0, m.Wait(9, 37));
    }

    [Theory]
    [InlineData(null, MissionMusic.Policy.Disc)]
    [InlineData("disc", MissionMusic.Policy.Disc)]
    [InlineData("GOG", MissionMusic.Policy.Gog)]
    [InlineData("Retail", MissionMusic.Policy.Retail1997)]
    [InlineData("x", MissionMusic.Policy.Disc)]
    public void ThePolicyComesFromTheEnvironment(string? v, MissionMusic.Policy p) => Assert.Equal(p, MissionMusic.PolicyFromEnvironment(v));
}
