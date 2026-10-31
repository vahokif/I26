using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The game pad's stick and trigger dead zones: radial, rescaled, and their settings (docs/joystick.md).</summary>
public class PadDeadZoneTests
{
    [Fact]
    public void DriftInsideTheZoneIsNothingOnBothAxes()
    {
        Assert.Equal((0f, 0f), PadDeadZones.Stick(0.10f, 0.10f, 0.15f));     // length 0.141: inside, though neither axis alone is far out
        Assert.Equal((0f, 0f), PadDeadZones.Stick(-0.15f, 0f, 0.15f));       // the edge itself
    }

    [Fact]
    public void ZoneIsRadialSoADiagonalDriftOfEachAxisUnderTheZoneStillCountsWhenItsLengthIsOver()
    {
        var (x, y) = PadDeadZones.Stick(0.14f, 0.14f, 0.15f);                // length 0.198 > 0.15
        Assert.True(x > 0 && y > 0);
        Assert.Equal(x, y, 5);
    }

    [Fact]
    public void ResponseStartsFromNothingAndReachesOne()
    {
        var (x, y) = PadDeadZones.Stick(0.575f, 0f, 0.15f);                  // half way between the edge and full
        Assert.Equal(0.5f, x, 4);
        Assert.Equal(0f, y);
        Assert.Equal((1f, 0f), PadDeadZones.Stick(1f, 0f, 0.15f));
        Assert.Equal((0f, -1f), PadDeadZones.Stick(0f, -1f, 0.5f));
        var (cx, cy) = PadDeadZones.Stick(1f, 1f, 0.2f);                     // a square gate's corner is held to the circle
        Assert.Equal(1f, MathF.Sqrt(cx * cx + cy * cy), 4);
    }

    [Fact]
    public void DirectionIsKeptAndNoJumpAtTheEdge()
    {
        var (x, y) = PadDeadZones.Stick(0.6f, -0.3f, 0.2f);
        Assert.Equal(0.6f / -0.3f, x / y, 4);
        float just = PadDeadZones.Stick(0.2001f, 0f, 0.2f).X;
        Assert.InRange(just, 0f, 0.001f);
        float prev = 0;
        for (float v = 0.2f; v <= 1f; v += 0.01f)
        {
            float o = PadDeadZones.Stick(v, 0, 0.2f).X;
            Assert.True(o >= prev);
            prev = o;
        }
    }

    [Fact]
    public void ZeroZoneLeavesTheStickAlone()
    {
        Assert.Equal((0.03f, -0.02f), PadDeadZones.Stick(0.03f, -0.02f, 0f));
    }

    [Fact]
    public void TriggersCutTheZoneAndStretchTheRest()
    {
        Assert.Equal(0f, PadDeadZones.Trig(0.04f, 0.05f));
        Assert.Equal(0f, PadDeadZones.Trig(0.05f, 0.05f));
        Assert.Equal(0.5f, PadDeadZones.Trig(0.525f, 0.05f), 4);
        Assert.Equal(1f, PadDeadZones.Trig(1f, 0.05f));
        Assert.Equal(0.3f, PadDeadZones.Trig(0.3f, 0f));
    }

    [Fact]
    public void ApplyFiltersASticksAndTriggersButNotAGenericDevice()
    {
        var z = new PadDeadZones(0.2f, 0.3f, 0.1f);
        var p = z.Apply(new PadRaw(0.1f, 0.1f, 0.2f, -0.2f, 0.05f, 1f, 5));
        Assert.Equal(0f, p.LeftX); Assert.Equal(0f, p.LeftY);
        Assert.Equal(0f, p.RightX); Assert.Equal(0f, p.RightY);       // length 0.28 < 0.3
        Assert.Equal(0f, p.TriggerLeft); Assert.Equal(1f, p.TriggerRight);
        Assert.Equal(5UL, p.Buttons);
        var g = PadRaw.OfGeneric([0.05f, 0.05f], 0);
        Assert.Equal(g, z.Apply(g));
    }

    [Fact]
    public void DefaultsAreAFewPercentAndAMissingKeyIsItsDefaultWithoutTouchingTheOthers()
    {
        Assert.Equal(new PadDeadZones(0.15f, 0.15f, 0.05f), PadDeadZones.Default);
        Assert.Equal(PadDeadZones.Default, PadDeadZones.Load(_ => null));         // a settings file from before this setting
        var z = PadDeadZones.Load(k => k == PadDeadZones.RightKey ? 0.30 : null);
        Assert.Equal(new PadDeadZones(0.15f, 0.30f, 0.05f), z);
        var all = PadDeadZones.Load(k => k switch { PadDeadZones.LeftKey => 0.0, PadDeadZones.RightKey => 0.5, PadDeadZones.TriggerKey => 0.2, _ => null });
        Assert.Equal(new PadDeadZones(0f, 0.5f, 0.2f), all);
        Assert.Equal(0.5f, PadDeadZones.Load(_ => 9.0).Left);                     // held to the range
        Assert.Equal(PadDeadZones.DefaultLeft, PadDeadZones.Load(_ => double.NaN).Left);
    }

    [Fact]
    public void StepsAreFivePercentFromZeroToFifty()
    {
        Assert.Equal(0.20f, PadDeadZones.Next(0.15f, 1), 4);
        Assert.Equal(0.10f, PadDeadZones.Next(0.15f, -1), 4);
        Assert.Equal(0f, PadDeadZones.Next(0f, -1));
        Assert.Equal(0.5f, PadDeadZones.Next(0.5f, 1), 4);
        Assert.Equal(0f, PadDeadZones.Next(0.5f, 1, wrap: true), 4);
        Assert.Equal(0.5f, PadDeadZones.Next(0f, -1, wrap: true), 4);
    }

    [Fact]
    public void MenuNavigationStillTakesAStickPushedPastHalf()
    {
        var z = PadDeadZones.Default;
        var held = PadRepeater.Held(z.Apply(new PadRaw(0.65f, 0.1f, 0, 0, 0, 0, 0)));
        Assert.Equal(NavDir.Right, held);
        Assert.Equal(NavDir.None, PadRepeater.Held(z.Apply(new PadRaw(0.1f, 0.12f, 0, 0, 0, 0, 0))));
    }
}
