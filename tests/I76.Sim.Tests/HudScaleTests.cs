using I76.Sim.Views;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Play Options' Chase HUD Top / Bottom Part (docs/playing.md): the sizes offered, how a settings file keeps one, what each look draws them at (HudScale.ForLook, docs/deviations.md), and how a
/// panel of the 640x480 instrument frame shrinks about the frame's middle line and its own row's edge, so a row stays one row.
/// The slots are ExtInstruments'.
/// </summary>
public class HudScaleTests
{
    /// <summary>The frame's eight slots as ExtInstruments lays them out (radar, damage, compass, speedo, tacho, gear, specials, weapons).</summary>
    public static readonly (int X, int Y, int W, int H)[] Slots =
    [
        (0, 0, 174, 119), (392, 0, 241, 123), (15, 433, 64, 64), (220, 373, 80, 80),
        (350, 373, 80, 80), (220, 452, 213, 30), (458, 404, 183, 79), (190, 0, 183, 127),
    ];

    private static bool Top(int y, int h) => y + h / 2 < HudScale.FrameHeight / 2;

    [Fact] public void ThreeSizesAreOffered() => Assert.Equal([0.25f, 0.5f, 1f], HudScale.Fractions);
    [Fact] public void TheyAreNamedAsFractions() => Assert.Equal(["1/4", "1/2", "1/1"], HudScale.Names);

    [Fact] public void TheTopRowDefaultsToHalfTheOriginal() => Assert.Equal(0.5f, HudScale.Of(HudScale.TopDefault));
    [Fact] public void TheBottomRowDefaultsToTheOriginalsOwnSize() => Assert.Equal(1f, HudScale.Of(HudScale.BottomDefault));

    /// <summary>The 1997 look draws the instrument frame as the original laid it out — both rows at their own size whatever Play Options
    /// chose, as it has neither cloud shadows nor haze (docs/deviations.md, "Chase instruments' size").</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void The1997LookDrawsTheOriginalsOwnSizes(int top, int bottom)
        => Assert.Equal((1f, 1f), HudScale.ForLook(true, top, bottom));

    /// <summary>The modern look draws what was chosen, both rows of it.</summary>
    [Theory]
    [InlineData(0, 0, 0.25f, 0.25f)]
    [InlineData(2, 0, 1f, 0.25f)]
    [InlineData(1, 2, 0.5f, 1f)]
    public void TheModernLookDrawsTheChosenSizes(int top, int bottom, float chosenTop, float chosenBottom)
        => Assert.Equal((chosenTop, chosenBottom), HudScale.ForLook(false, top, bottom));

    /// <summary>A look never changes the choice: the same choices read for the 1997 look and then for the modern one still give the sizes
    /// that were set, which is what a F12 back finds.</summary>
    [Fact]
    public void The1997LookLeavesTheChoicesAlone()
    {
        int top = 1, bottom = 0;                                    // 1/2 over 1/4
        Assert.Equal((1f, 1f), HudScale.ForLook(true, top, bottom));
        Assert.Equal((0.5f, 0.25f), HudScale.ForLook(false, top, bottom));
    }

    [Theory]
    [InlineData(0, 4, "1/4")]
    [InlineData(1, 2, "1/2")]
    [InlineData(2, 1, "1/1")]
    public void AChoiceKeepsItsDenominatorAndName(int choice, int denominator, string name)
    {
        Assert.Equal(denominator, HudScale.Denominator(choice));
        Assert.Equal(name, HudScale.Names[choice]);
    }

    [Theory]
    [InlineData(4, 0)]       // a file written by Chase HUD ... = 1/4
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 1)]       // not offered: back to the default
    [InlineData(0, 1)]       // the key absent, which is read as 0
    [InlineData(-2, 1)]
    public void AFileKeepsItsChoiceOrTheDefault(int denominator, int choice) => Assert.Equal(choice, HudScale.Parse(denominator, 1));

    [Theory]
    [MemberData(nameof(AllSlots))]
    public void FullSizeIsWhereTheOriginalDrewIt((int X, int Y, int W, int H) slot)
        => Assert.Equal(((float)slot.X, (float)slot.Y, (float)slot.W, (float)slot.H), HudScale.Shrink(slot.X, slot.Y, slot.W, slot.H, 1f));

    public static TheoryData<(int X, int Y, int W, int H)> AllSlots()
    {
        var d = new TheoryData<(int, int, int, int)>();
        foreach (var s in Slots) d.Add(s);
        return d;
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    public void EveryPanelShrinksAboutTheMiddleLineAndItsRowsEdge(float s)
    {
        foreach (var (x, y, w, h) in Slots)
        {
            var (X, Y, W, H) = HudScale.Shrink(x, y, w, h, s);
            Assert.Equal(w * s, W, 3);
            Assert.Equal(h * s, H, 3);
            Assert.Equal(320 + (x - 320) * s, X, 3);       // one line for the whole row, so the row keeps its shape
            Assert.Equal(Top(y, h) ? y * s : 480 + (y - 480) * s, Y, 3);   // hung from the top, or standing on the bottom
            // Two panels reach a pixel past the frame (zbk_, zbks_), so the frame's own box is the bound, not the panel's.
            Assert.True(X >= System.Math.Min(0, x) - 0.001f && X + W <= System.Math.Max(640, x + w) + 0.001f, "never further out sideways");
            Assert.True(Y >= System.Math.Min(0, y) - 0.001f && Y + H <= System.Math.Max(480, y + h) + 0.001f, "never further out up or down");
        }
    }

    /// <summary>A smaller row is a smaller picture of the same row: each panel holds its place and the gap between two neighbours
    /// shrinks by the same fraction, which is what the user asked for over panels that spread out to the corners.</summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void TheGapsShrinkWithThePanels(float s)
    {
        var radar = HudScale.Shrink(0, 0, 174, 119, s);           // the top row, left to right: radar, weapons housing, damage
        var weapons = HudScale.Shrink(190, 0, 183, 127, s);
        var damage = HudScale.Shrink(392, 0, 241, 123, s);
        Assert.Equal((190 - 174) * s, weapons.X - (radar.X + radar.W), 3);
        Assert.Equal((392 - 373) * s, damage.X - (weapons.X + weapons.W), 3);
        var compass = HudScale.Shrink(15, 433, 64, 64, s);         // the bottom row: compass, dials and strip, specials
        var speedo = HudScale.Shrink(220, 373, 80, 80, s);
        var specials = HudScale.Shrink(458, 404, 183, 79, s);
        Assert.Equal((220 - 79) * s, speedo.X - (compass.X + compass.W), 3);
        Assert.Equal((458 - 430) * s, specials.X - (speedo.X + 210 * s), 3);   // measured from the tacho's own right end
        Assert.True(weapons.X - (radar.X + radar.W) < 190 - 174, "a small row is closer together than the original's");
    }

    /// <summary>Where each of the eight panels ends up at a half: the row hangs from its own edge of the frame and every panel is
    /// the same distance from the middle line as it was, times the size.</summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void EachPanelHoldsItsPlaceInItsRow(float s)
    {
        // the top row hangs from the top of the frame, so it grows downwards out of it and no further
        Assert.Equal(0f, HudScale.Shrink(0, 0, 174, 119, s).Y, 3);
        Assert.Equal(0f, HudScale.Shrink(392, 0, 241, 123, s).Y, 3);
        Assert.Equal(0f, HudScale.Shrink(190, 0, 183, 127, s).Y, 3);
        Assert.Equal(320f - 320 * s, HudScale.Shrink(0, 0, 174, 119, s).X, 3);        // the radar, the frame's left edge at full size
        Assert.Equal(320f - 130 * s, HudScale.Shrink(190, 0, 183, 127, s).X, 3);     // the weapons housing, left of the line
        Assert.Equal(320f + 72 * s, HudScale.Shrink(392, 0, 241, 123, s).X, 3);      // the damage panel, right of it
        // the bottom row stands on the bottom of the frame. The compass's foot and the two zbk_ panels hang past it in their own
        // art, so they end the same fraction of that way out.
        Assert.Equal(480f - 47 * s, HudScale.Shrink(15, 433, 64, 64, s).Y, 3);
        Assert.Equal(480f + (497 - 480) * s, HudScale.Shrink(15, 433, 64, 64, s).Y + 64 * s, 3);
        Assert.Equal(480f - 76 * s, HudScale.Shrink(458, 404, 183, 79, s).Y, 3);
        Assert.Equal(320f - 305 * s, HudScale.Shrink(15, 433, 64, 64, s).X, 3);
        Assert.Equal(320f - 100 * s, HudScale.Shrink(220, 373, 80, 80, s).X, 3);
        Assert.Equal(320f - 100 * s, HudScale.Shrink(220, 452, 213, 30, s).X, 3);     // the strip stays under the dials
        Assert.Equal(320f + 138 * s, HudScale.Shrink(458, 404, 183, 79, s).X, 3);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    public void ARowNeverCrossesTheFramesMiddle(float s)
    {
        foreach (var (x, y, w, h) in Slots)
        {
            var (X, Y, W, H) = HudScale.Shrink(x, y, w, h, s);
            Assert.True(Top(y, h) ? Y + H <= 240 : Y >= 240, $"row of slot ({x},{y}) crossed the middle");
            Assert.True(X >= System.Math.Min(0, x) - 0.001f && X + W <= System.Math.Max(640, x + w) + 0.001f, "never further out than the full-size panel");
        }
    }
}
