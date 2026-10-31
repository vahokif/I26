using I76.Formats.Input;
using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// #79: the game chooser read only Godot's own ui_accept / ui_cancel, which a pad's A and B never reach here, so a pad could move
/// between the games but not start one or quit. It polls the pad as the shell does, through <see cref="PadRepeater"/>.
/// </summary>
public class GameChooserPadTests
{
    private static PadRaw Pad(params int[] buttons)
    {
        ulong b = 0;
        foreach (var k in buttons) b |= 1UL << k;
        return new PadRaw(0, 0, 0, 0, 0, 0, b);
    }

    [Fact]
    public void AStartsTheFocusedGameBAndStartQuit()
    {
        var r = new PadRepeater();
        Assert.Equal(GameChooser.PadAct.None, GameChooser.Act(r.Update(Pad(), 0.016)));
        Assert.Equal(GameChooser.PadAct.Accept, GameChooser.Act(r.Update(Pad(PadButton.A), 0.016)));
        Assert.Equal(GameChooser.PadAct.None, GameChooser.Act(r.Update(Pad(PadButton.A), 0.016)));   // held: not again
        r.Update(Pad(), 0.016);
        Assert.Equal(GameChooser.PadAct.Quit, GameChooser.Act(r.Update(Pad(PadButton.B), 0.016)));
        r.Update(Pad(), 0.016);
        Assert.Equal(GameChooser.PadAct.Quit, GameChooser.Act(r.Update(Pad(PadButton.Start), 0.016)));
        r.Update(Pad(), 0.016);
        Assert.Equal(GameChooser.PadAct.None, GameChooser.Act(r.Update(Pad(PadButton.DpadLeft), 0.016)));   // moving is the focus's own
    }
}
