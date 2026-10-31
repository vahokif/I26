using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class DriverVariantTests
{
    [Theory]
    [InlineData("p01", 2)] [InlineData("P04", 2)] [InlineData("p17", 2)] [InlineData("p19", 2)]
    [InlineData("p05", 3)] [InlineData("p08", 3)] [InlineData("p18", 3)] [InlineData("b01", 3)]
    [InlineData("p09", 4)] [InlineData("p12", 4)]
    [InlineData("p13", 5)] [InlineData("p16", 5)]
    [InlineData("n01", 2)] [InlineData("N65", 2)]
    [InlineData("t01", 1)] [InlineData("a01", 1)] [InlineData("zzz", 1)] [InlineData("", 1)]
    public void NitroTable(string mission, int v) => Assert.Equal(v, GameInstall.DriverVariant(mission, true));

    [Theory]
    [InlineData("p01")] [InlineData("n01")] [InlineData("p13")] [InlineData("a01")]
    public void BaseIsAlwaysOne(string mission) => Assert.Equal(1, GameInstall.DriverVariant(mission, false));
}
