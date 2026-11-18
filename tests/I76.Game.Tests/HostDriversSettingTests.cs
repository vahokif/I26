using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The host forms' NO. DRIVERS in melee.cfg (#204): kept as it was left, where it used to open on 4 after every race.</summary>
public class HostDriversSettingTests
{
    [Fact]
    public void ANewPlayerHasFour() => Assert.Equal(4, new MeleeSetup().HostDrivers);

    [Theory]
    [InlineData(null, 4)]   // a file from before it was kept
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(MeleeScreen.MaxDrivers, MeleeScreen.MaxDrivers)]
    [InlineData(0, 1)]
    [InlineData(MeleeScreen.MaxDrivers + 5, MeleeScreen.MaxDrivers)]
    public void AFileKeepsItsCount(int? stored, int drivers) => Assert.Equal(drivers, MeleeSetup.HostDriversFrom(stored));
}
