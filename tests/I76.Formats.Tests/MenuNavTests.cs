using System.Collections.Generic;
using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The pad's menu navigation: the spatial picker, the stick repeat, and Start as the menu key.</summary>
public class MenuNavTests
{
    private static PadRaw Pad(float lx = 0, float ly = 0, params int[] buttons)
    {
        ulong b = 0;
        foreach (var k in buttons) b |= 1UL << k;
        return new PadRaw(lx, ly, 0, 0, 0, 0, b);
    }

    [Fact]
    public void ThePickerMovesToTheNearestRegionInTheDirection()
    {
        // a column of three, a button to the right of the middle one
        NavRect[] r = [new(100, 100, 100, 20), new(100, 140, 100, 20), new(100, 180, 100, 20), new(300, 140, 50, 20)];
        Assert.Equal(1, PadNav.Pick(r, 0, NavDir.Down));
        Assert.Equal(2, PadNav.Pick(r, 1, NavDir.Down));
        Assert.Equal(0, PadNav.Pick(r, 1, NavDir.Up));
        Assert.Equal(3, PadNav.Pick(r, 1, NavDir.Right));
        Assert.Equal(1, PadNav.Pick(r, 3, NavDir.Left));
        Assert.Equal(-1, PadNav.Pick(r, 0, NavDir.Up));          // the edge: stays
        Assert.Equal(-1, PadNav.Pick(r, 3, NavDir.Right));
    }

    [Fact]
    public void AnInLineNeighbourBeatsANearerOneOffToTheSide()
    {
        NavRect[] r = [new(100, 100, 40, 20), new(300, 130, 40, 20), new(100, 220, 40, 20)];
        Assert.Equal(2, PadNav.Pick(r, 0, NavDir.Down));
    }

    [Fact]
    public void ARegionWithinTheConeBeatsANearerOneOutsideIt()
    {
        // TRIP and MELEE on one line, three rows opening below TRIP (the main menu): right from TRIP is MELEE, not the row below-right
        NavRect[] r = [new(160, 286, 100, 40), new(380, 286, 100, 40), new(150, 380, 80, 20), new(250, 380, 40, 20)];
        Assert.Equal(1, PadNav.Pick(r, 0, NavDir.Right));
        Assert.Equal(2, PadNav.Pick(r, 0, NavDir.Down));
        Assert.Equal(3, PadNav.Pick(r, 2, NavDir.Right));
        Assert.Equal(0, PadNav.Pick(r, 2, NavDir.Up));
    }

    [Fact]
    public void NothingFocusedStartsAtTheFirstInReadingOrder()
    {
        NavRect[] r = [new(300, 300, 10, 10), new(50, 50, 10, 10), new(400, 50, 10, 10)];
        Assert.Equal(1, PadNav.Pick(r, -1, NavDir.Down));
        Assert.Equal(-1, PadNav.Pick([], -1, NavDir.Down));
    }

    [Fact]
    public void TheStickNeedsHalfDeflectionAndTheStrongerAxisWins()
    {
        Assert.Equal(NavDir.None, PadRepeater.Held(Pad(ly: -0.49f)));
        Assert.Equal(NavDir.Up, PadRepeater.Held(Pad(ly: -0.5f)));
        Assert.Equal(NavDir.Right, PadRepeater.Held(Pad(lx: 0.9f, ly: 0.6f)));
        Assert.Equal(NavDir.Down, PadRepeater.Held(Pad(0, 0, PadButton.DpadDown)));
    }

    [Fact]
    public void AHeldDirectionMovesOnceThenAfter350msEvery120ms()
    {
        var r = new PadRepeater();
        var up = Pad(ly: -1f);
        int moves = 0;
        double t = 0;
        // 60 Hz for two seconds
        for (int i = 0; i < 120; i++, t += 1 / 60.0)
            if (r.Update(up, 1 / 60.0).Dir == NavDir.Up) { moves++; if (moves == 1) Assert.Equal(0, i); if (moves == 2) Assert.InRange(t, 0.34, 0.37); }
        // 1 press + (2.0 - 0.35) / 0.12 repeats ~ 14
        Assert.InRange(moves, 13, 15);
        Assert.Equal(NavDir.None, r.Update(Pad(), 1 / 60.0).Dir);
        Assert.Equal(NavDir.Up, r.Update(up, 1 / 60.0).Dir);       // released and pressed again: at once
    }

    [Fact]
    public void ADirectionChangeMovesAtOnceAndButtonsAreOnThePress()
    {
        var r = new PadRepeater();
        Assert.Equal(NavDir.Up, r.Update(Pad(ly: -1f), 0.016).Dir);
        Assert.Equal(NavDir.Right, r.Update(Pad(lx: 1f), 0.016).Dir);
        var a = r.Update(Pad(0, 0, PadButton.A), 0.016);
        Assert.Equal(PadRepeater.Btn.Accept, a.Buttons);
        Assert.Equal(PadRepeater.Btn.None, r.Update(Pad(0, 0, PadButton.A), 0.016).Buttons);   // held: not again
        Assert.Equal(PadRepeater.Btn.Back | PadRepeater.Btn.Start, r.Update(Pad(0, 0, PadButton.A, PadButton.B, PadButton.Start), 0.016).Buttons);
    }

    [Fact]
    public void SettleSilencesThePadUntilEverythingIsUp()
    {
        var r = new PadRepeater();
        r.Settle();
        Assert.False(r.Update(Pad(0, 0, PadButton.Start), 0.016).Any);
        Assert.False(r.Update(Pad(0, 0, PadButton.Start), 0.016).Any);
        Assert.False(r.Update(Pad(), 0.016).Any);
        Assert.True(r.Update(Pad(0, 0, PadButton.B), 0.016).Any);
    }

    [Fact]
    public void StartIsTheMenuKeyAndNothingElseIs()
    {
        Assert.Equal((int)GameKey.MainMenu, GameKeyMap.PadLookup(PadButton.Start));
        foreach (int b in new[] { PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.Back, PadButton.LeftShoulder, PadButton.RightShoulder, PadButton.LeftStick, PadButton.RightStick })
            Assert.Equal(0, GameKeyMap.PadLookup(b));
    }

    [Fact]
    public void TheStockPadPresetBindsNoStart()
    {
        var t = PadPreset.Build((string?)null);
        Assert.DoesNotContain(t.Blocks.Where(BindingTable.IsPad), b => b.Terms.Any(x => VirtualStick.DiscreteId(x.Channel) == 7));
    }

    [Fact]
    public void DownStepsToTheNextLeftAlignedRowHoweverLongItsLabel()
    {
        // Joe's Fish Shack's options: labels at one x, of very different widths (Control Configuration is long)
        var rows = new List<NavRect> { new(202, 142, 90, 14), new(202, 165, 85, 14), new(202, 211, 230, 14), new(202, 280, 100, 14) };
        Assert.Equal(2, PadNav.Pick(rows, 1, NavDir.Down));
        Assert.Equal(1, PadNav.Pick(rows, 2, NavDir.Up));
        Assert.Equal(3, PadNav.Pick(rows, 2, NavDir.Down));
    }
}
