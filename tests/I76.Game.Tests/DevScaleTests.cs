using I76.Game.Ui;
using Xunit;

namespace I76.Game.Tests;

public class DevScaleTests
{
    [Theory]
    [InlineData(480, 1)]
    [InlineData(720, 1)]
    [InlineData(960, 1)]
    [InlineData(1080, 1)]
    [InlineData(1440, 1)]
    [InlineData(2159, 1)]
    [InlineData(2160, 2)]
    [InlineData(2880, 2)]
    [InlineData(4320, 3)]
    public void Factor_FollowsWindowHeight(float height, int factor) => Assert.Equal(factor, DevScale.Factor(height));
}
