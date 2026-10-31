using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

public class DashToggleTests
{
    [Fact]
    public void F1InTheCockpitFlipsTheDashEachPress()
    {
        bool d = true;
        d = DashToggle.OnF1(true, d); Assert.False(d);
        d = DashToggle.OnF1(true, d); Assert.True(d);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F1FromAnotherViewKeepsTheMode(bool dash3d) => Assert.Equal(dash3d, DashToggle.OnF1(false, dash3d));

    [Fact]
    public void FreeLookOnlyWithTheFlatDashOff() { Assert.True(DashToggle.FreeLookAllowed(true)); Assert.False(DashToggle.FreeLookAllowed(false)); }
}
