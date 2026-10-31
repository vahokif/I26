using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class MeleeDriversTests
{
    [Fact]
    public void BotsAndTheAiFillNeverShareANameIn26OfEach()
    {
        var bots = Enumerable.Range(0, 26).Select(MeleeDrivers.Bot).ToList();
        var fill = Enumerable.Range(0, 26).Select(MeleeDrivers.Fill).ToList();
        Assert.Equal(52, bots.Concat(fill).Distinct().Count());
        Assert.Equal(53, Enumerable.Range(0, 53).Select(MeleeDrivers.Bot).Distinct().Count());
    }

    [Fact]
    public void AnAiFillEntityIsNamedForItsRosterSlot()
    {
        Assert.Equal(MeleeDrivers.Fill(0), MeleeDrivers.ForFillEntity("nvcl0"));
        Assert.Equal(MeleeDrivers.Fill(10), MeleeDrivers.ForFillEntity("nvcla"));
        Assert.Null(MeleeDrivers.ForFillEntity("plyr0"));
        Assert.Null(MeleeDrivers.ForFillEntity("nvcl"));
        Assert.Null(MeleeDrivers.ForFillEntity("nvclzz"));
    }
}
