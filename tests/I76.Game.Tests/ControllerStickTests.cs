using Godot;
using I76.Formats.Input;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The controller screen's sticks: each axis of each stick can be picked, by a click on the drawing, by a push of the stick, or from the
/// stick's one row, so the throttle can go on a stick's up / down (a player's right stick up / down to drive, the left stick's left / right to steer).
/// </summary>
public class ControllerStickTests
{
    private static string? Clicked(Vector2 stick, Vector2 d) => ControllerScreen.At(ControllerScreen.Make(), stick + d)?.Key;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AClickOnAStickIsTheAxisThatWayAndItsMiddleThePress(bool left)
    {
        var at = left ? ControllerScreen.LeftStick : ControllerScreen.RightStick;
        string s = left ? "L" : "R";
        // on the cap, above and below its middle: up / down; beside it: left / right
        foreach (var d in new Vector2[] { new(0, -10), new(0, 10), new(2, -12), new(0, -18) })
            Assert.Equal(s + "SY", Clicked(at, d));
        foreach (var d in new Vector2[] { new(-10, 0), new(10, 0), new(12, 2), new(18, 0) })
            Assert.Equal(s + "SX", Clicked(at, d));
        Assert.Equal(s + "3", Clicked(at, Vector2.Zero));
        Assert.Equal(s + "3", Clicked(at, new(3, -4)));
    }

    [Fact]
    public void TheOtherInputsAreStillFoundByTheirOutlines()
    {
        var els = ControllerScreen.Make();
        foreach (var (key, at) in ControllerScreen.Face) Assert.Equal(key, ControllerScreen.At(els, at)?.Key);
        Assert.Equal("DUp", ControllerScreen.At(els, ControllerScreen.DPad + new Vector2(0, -10))?.Key);
        Assert.Equal("Start", ControllerScreen.At(els, ControllerScreen.StartAt)?.Key);
        Assert.Null(ControllerScreen.At(els, new Vector2(0, 30)));
    }

    private static PadRaw Pad(float lx = 0, float ly = 0, float rx = 0, float ry = 0, float lt = 0, float rt = 0) => new(lx, ly, rx, ry, lt, rt, 0);

    [Fact]
    public void PushingAStickPicksTheAxisItWentAlong()
    {
        var rest = Pad();
        Assert.Equal("RSY", ControllerScreen.Pushed(rest, Pad(rx: 0.2f, ry: -0.9f)));
        Assert.Equal("RSY", ControllerScreen.Pushed(rest, Pad(ry: 0.7f)));
        Assert.Equal("RSX", ControllerScreen.Pushed(rest, Pad(rx: 0.8f, ry: 0.3f)));
        Assert.Equal("LSY", ControllerScreen.Pushed(rest, Pad(ly: -1)));
        Assert.Equal("LSX", ControllerScreen.Pushed(rest, Pad(lx: -1)));
        Assert.Equal("LT", ControllerScreen.Pushed(rest, Pad(lt: 1)));
        Assert.Equal("RT", ControllerScreen.Pushed(rest, Pad(rt: 1)));
        // held out, or a small nudge, picks nothing
        Assert.Null(ControllerScreen.Pushed(Pad(ry: -0.9f), Pad(rx: 0.7f, ry: -0.7f)));
        Assert.Null(ControllerScreen.Pushed(rest, Pad(rx: 0.3f, ry: 0.4f)));
    }

    [Fact]
    public void TheRightSticksOneCameraRowPutsTheThrottleOnItsUpAndDown()
    {
        // the pad's preset: the right stick's two axes are the camera, one row on the sheet (the stick's whole)
        var t = PadPreset.Build((string?)null);
        ControllerScreen.PickAxis(t, 1, "Rudder", "5thAxis");
        Assert.Equal(new InputTerm(false, "joystick1", "5thAxis"), t.AxisOf("throttle"));
        Assert.Equal(new InputTerm(false, "joystick1", "Rudder"), t.AxisOf("track_yaw_delta"));      // its left / right still turns the camera
        Assert.Null(t.AxisOf("track_pitch_delta"));
        Assert.Equal(new InputTerm(false, "joystick1", "Left/Right"), t.AxisOf("steer"));
        // a stick's left / right picked on its own is that axis, as before
        ControllerScreen.PickAxis(t, 1, "Rudder");
        Assert.Equal(new InputTerm(false, "joystick1", "Rudder"), t.AxisOf("throttle"));
        // the whole stick's steer is its left / right, its camera both, its none clears both
        var u = PadPreset.Build((string?)null);
        ControllerScreen.PickAxis(u, 2, "Rudder", "5thAxis");
        Assert.Equal(new InputTerm(false, "joystick1", "Rudder"), u.AxisOf("steer"));
        ControllerScreen.PickAxis(u, 3, "Left/Right", "Down/Up");
        Assert.Equal(new InputTerm(false, "joystick1", "Left/Right"), u.AxisOf("track_yaw_delta"));
        Assert.Equal(new InputTerm(true, "joystick1", "Down/Up"), u.AxisOf("track_pitch_delta"));
        ControllerScreen.PickAxis(u, 0, "Left/Right", "Down/Up");
        Assert.Null(u.AxisOf("track_yaw_delta"));
        Assert.Null(u.AxisOf("track_pitch_delta"));
    }
}
