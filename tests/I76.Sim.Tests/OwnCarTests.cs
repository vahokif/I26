using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The player's own car is not drawn from the cockpit or the hood view (F6); a script camera always shows it.</summary>
public class OwnCarTests
{
    [Theory]
    [InlineData(false, false, false, true)]    // F2 and the other outside views
    [InlineData(true, false, false, false)]    // the cockpit
    [InlineData(false, true, false, false)]    // the hood view: glancing back shows the road, not the body
    [InlineData(true, false, true, true)]      // a cut-scene from the cockpit
    [InlineData(false, true, true, true)]      // a cut-scene from the hood view
    public void TheOwnCarIsSkippedInTheCockpitAndTheHoodView(bool cockpit, bool hood, bool script, bool drawn) =>
        Assert.Equal(drawn, OwnCar.Drawn(cockpit, hood, script));
}
