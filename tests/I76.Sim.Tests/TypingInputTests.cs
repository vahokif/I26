using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

public class TypingInputTests
{
    [Fact] public void TypingDrivesAndFiresNothing()
    {
        var all = new CarInput
        {
            Buttons = (CarButtons)0x7fff, Commands = (CarCommands)0xffffffff, Steer = 20000, Throttle = 30000,
        };
        var t = all.WhileTyping();
        Assert.Equal(CarButtons.Cockpit | CarButtons.RaiseRightArm | CarButtons.RaiseLeftArm, t.Buttons & (CarButtons)0x7fff);
        Assert.False(t.Has(CarButtons.Fire) || t.Has(CarButtons.ThrottleUp) || t.Has(CarButtons.SteerLeft) || t.Has(CarButtons.Horn) || t.Has(CarButtons.Analog));
        Assert.Equal(CarCommands.None, t.Commands);
        Assert.Equal(0, t.Steer);
        Assert.Equal(0, t.Throttle);
    }
}
