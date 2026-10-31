using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The joystick processing, the pad's mapping onto the virtual stick, and the bindings that read it.</summary>
public class JoystickTests
{
    private sealed class Pads : IPadSource
    {
        public List<PadRaw> List = [new PadRaw()];
        public IReadOnlyList<PadRaw> Read() => List;
        public PadRaw this[int i] { get => List[i]; set => List[i] = value; }
    }

    private static PadRaw Pad(float lx = 0, float ly = 0, float rx = 0, float ry = 0, float lt = 0, float rt = 0, params int[] buttons)
    {
        ulong b = 0;
        foreach (var k in buttons) b |= 1UL << k;
        return new PadRaw(lx, ly, rx, ry, lt, rt, b);
    }

    private static bool NoKey(InputTerm _) => false;

    [Fact]
    public void TheResponseIsTheSignedSquareTo65536()
    {
        Assert.Equal(65536, JoystickBank.Response(1f));
        Assert.Equal(-65536, JoystickBank.Response(-1f));
        Assert.Equal(16384, JoystickBank.Response(0.5f));
        Assert.Equal(-16384, JoystickBank.Response(-0.5f));
        Assert.Equal(0, JoystickBank.Response(0f));
        Assert.Equal(163, JoystickBank.Response(0.05f));       // truncated, no dead zone: a resting 5 % is 0.25 %
        Assert.Equal(65536, JoystickBank.Response(3f));        // kept inside the range
    }

    [Fact]
    public void ARestingPadReadsCentredAndTheChannelsAreTheNamedAxes()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        bank.Poll(true);
        foreach (var n in VirtualStick.AnalogNames) Assert.Equal(0, bank.Analog(0, n));
        p[0] = Pad(lx: 0.5f, ly: -1f, rx: -0.5f, ry: 1f);
        bank.Poll(true);
        Assert.Equal(16384, bank.Analog(0, "Left/Right"));
        Assert.Equal(-65536, bank.Analog(0, "Down/Up"));
        Assert.Equal(-16384, bank.Analog(0, "Rudder"));
        Assert.Equal(65536, bank.Analog(0, "5thAxis"));
        Assert.False(bank.HasAnalog(0, "6thAxis"));           // a pad has no sixth axis
        Assert.False(bank.HasAnalog(0, "HeadRoll"));
        Assert.Equal(0, bank.Analog(1, "Rudder"));            // no second pad
    }

    [Fact]
    public void ThrottleIsTheLeftTriggerMinusTheRightAndTheStockSignMakesTheRightOneAccelerate()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        var axes = new AxisControls(InputMapFile.ParseBlocks("throttle {\n - joystick1 Throttle\n}\n"));
        void Frame() { bank.Poll(true); axes.Update(bank, NoKey); }
        p[0] = Pad(rt: 1f); Frame();
        Assert.Equal(65536, axes.Throttle);                    // RT: accelerate
        p[0] = Pad(lt: 1f); Frame();
        Assert.Equal(-65536, axes.Throttle);                   // LT: brake
        p[0] = Pad(lt: 1f, rt: 1f); Frame();
        Assert.Equal(0, axes.Throttle);
        p[0] = Pad(lt: 0.5f); Frame();
        Assert.Equal(-16384, axes.Throttle);                   // the square
        Assert.Equal(-8192, AxisControls.Wire(axes.Throttle));
    }

    [Fact]
    public void TheSignOfTheBlockMultipliesAndPushingForwardAccelerates()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        var minus = new AxisControls(InputMapFile.ParseBlocks("throttle {\n - joystick1 Down/Up\n}\nsteer {\n - joystick1 Left/Right\n}\n"));
        var plus = new AxisControls(InputMapFile.ParseBlocks("throttle {\n + joystick1 Down/Up\n}\n"));
        p[0] = Pad(lx: 1f, ly: -1f);       // pushed forward and right
        bank.Poll(true); minus.Update(bank, NoKey); plus.Update(bank, NoKey);
        Assert.Equal(65536, minus.Throttle);
        Assert.Equal(-65536, minus.Steer);                     // right is negative: the engine's + is left
        Assert.Equal(-65536, plus.Throttle);
        Assert.Equal(32767, AxisControls.Wire(65536));
        Assert.Equal(-32767, AxisControls.Wire(-65536));
    }

    [Fact]
    public void AnAxisWritesItsRowOnlyWhenItsValueChanges()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        // two blocks on one row: the last to change wins
        var axes = new AxisControls(InputMapFile.ParseBlocks("steer {\n - joystick1 Left/Right\n}\nsteer {\n - joystick1 Rudder\n}\n"));
        void Frame() { bank.Poll(true); axes.Update(bank, NoKey); }
        p[0] = Pad(lx: -1f); Frame();
        Assert.Equal(65536, axes.Steer);
        p[0] = Pad(lx: -1f, rx: 0.5f); Frame();
        Assert.Equal(-16384, axes.Steer);                      // the rudder changed, so it wrote
        p[0] = Pad(lx: -1f, rx: 0.5f); Frame();
        Assert.Equal(-16384, axes.Steer);                      // nothing changed: the left/right axis does not take it back
        p[0] = Pad(lx: -0.5f, rx: 0.5f); Frame();
        Assert.Equal(16384, axes.Steer);
    }

    [Fact]
    public void AWindowWithoutFocusReadsCentredWithEveryButtonUp()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        var axes = new AxisControls(InputMapFile.ParseBlocks("throttle {\n - joystick1 Down/Up\n}\n"));
        p[0] = Pad(ly: -1f, buttons: [PadButton.A, PadButton.DpadUp]);
        bank.Poll(true); axes.Update(bank, NoKey);
        Assert.Equal(65536, axes.Throttle);
        Assert.True(bank.Held(0, "Button1"));
        Assert.True(bank.Held(0, "HatUp"));
        bank.Poll(false); axes.Update(bank, NoKey);
        Assert.Equal(0, axes.Throttle);                        // the change to centre is written
        Assert.False(bank.Held(0, "Button1"));
        Assert.False(bank.Held(0, "HatUp"));
        Assert.Equal(1, bank.Count);                           // the pad is still there
    }

    [Fact]
    public void TheButtonsAreInDirectInputOrderAndExtrasContinue()
    {
        var order = new[] { PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LeftShoulder, PadButton.RightShoulder, PadButton.Back, PadButton.Start, PadButton.LeftStick, PadButton.RightStick };
        for (int k = 0; k < order.Length; k++)
        {
            var s = VirtualStick.From(Pad(buttons: order[k]));
            Assert.Equal(1u << k, s.Buttons);
            Assert.Equal(k, VirtualStick.DiscreteId($"Button{k + 1}"));
        }
        Assert.Equal(1u << 10, VirtualStick.From(Pad(buttons: [PadButton.Guide])).Buttons);   // Button11
        Assert.Equal(-1, VirtualStick.DiscreteId($"Button{VirtualStick.ButtonCount + 1}"));
        Assert.Equal(VirtualStick.DiscreteId("button3"), VirtualStick.DiscreteId("2"));      // a digit is a raw index
    }

    [Fact]
    public void TheHatIsOneDirectionAndADiagonalIsNone()
    {
        Assert.Equal(1, VirtualStick.From(Pad(buttons: [PadButton.DpadUp])).Hat);
        Assert.Equal(2, VirtualStick.From(Pad(buttons: [PadButton.DpadRight])).Hat);
        Assert.Equal(4, VirtualStick.From(Pad(buttons: [PadButton.DpadDown])).Hat);
        Assert.Equal(8, VirtualStick.From(Pad(buttons: [PadButton.DpadLeft])).Hat);
        Assert.Equal(0, VirtualStick.From(Pad(buttons: [PadButton.DpadUp, PadButton.DpadRight])).Hat);
        Assert.Equal(0, VirtualStick.From(Pad()).Hat);
        Assert.Equal(VirtualStick.ButtonCount + 2, VirtualStick.DiscreteId("HatDown"));
        // a press of one direction while another is held is not a hat press
        Assert.Equal(-1, VirtualStick.DiscreteIdOfPadButton(PadButton.DpadUp, Pad(buttons: [PadButton.DpadUp, PadButton.DpadLeft])));
        Assert.Equal(VirtualStick.ButtonCount, VirtualStick.DiscreteIdOfPadButton(PadButton.DpadUp, Pad(buttons: [PadButton.DpadUp])));
    }

    [Fact]
    public void AChordHoldsWhileThePlusIsDownAndTheMinusIsUpAndOneOnADeadDeviceIsDropped()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        var fire = InputMapFile.ParseBlocks("weapon_fire {\n + joystick1 Button1\n - joystick1 Button2\n}\n")[0].Terms;
        p[0] = Pad(buttons: [PadButton.A]); bank.Poll(true);
        Assert.True(InputChord.Holds(fire, bank, NoKey));
        p[0] = Pad(buttons: [PadButton.A, PadButton.B]); bank.Poll(true);
        Assert.False(InputChord.Holds(fire, bank, NoKey));
        // without the pad the block is gone (its last term is dead), and a keyboard term elsewhere does not matter
        var none = new JoystickBank(new Pads { List = [] });
        none.Poll(true);
        Assert.False(InputChord.Holds(fire, none, _ => true));
        // a keyboard "-" term is asked of the keyboard
        var chord = InputMapFile.ParseBlocks("e_brake {\n + joystick1 Button5\n - keyboard Shift\n}\n")[0].Terms;
        p[0] = Pad(buttons: [PadButton.LeftShoulder]); bank.Poll(true);
        Assert.True(InputChord.Holds(chord, bank, _ => false));
        Assert.False(InputChord.Holds(chord, bank, t => t.Channel == "Shift"));
    }

    [Fact]
    public void AGatedAxisMovesOnlyWhileItsChordHolds()
    {
        var p = new Pads();
        var bank = new JoystickBank(p);
        var axes = new AxisControls(InputMapFile.ParseBlocks("throttle {\n - joystick1 Down/Up\n + joystick1 Button3\n}\n"));
        p[0] = Pad(ly: -1f); bank.Poll(true); axes.Update(bank, NoKey);
        Assert.Equal(0, axes.Throttle);
        p[0] = Pad(ly: -1f, buttons: [PadButton.X]); bank.Poll(true); axes.Update(bank, NoKey);
        Assert.Equal(65536, axes.Throttle);
    }

    [Fact]
    public void ABlockOnAMissingPadIsNotLive()
    {
        var bank = new JoystickBank(new Pads { List = [] });
        bank.Poll(true);
        var axes = new AxisControls(InputMapFile.ParseBlocks("throttle {\n - joystick1 Down/Up\n}\nsteer {\n - joystick2 Left/Right\n}\n"));
        Assert.False(axes.Active(bank));
        var one = new JoystickBank(new Pads());
        one.Poll(true);
        Assert.True(axes.Active(one, steer: false));
        Assert.False(axes.Active(one, steer: true));            // joystick2 is not there
    }

    [Fact]
    public void TheCameraRowsFollowTheStickEveryFrameAndDropToZeroWhenThePadGoes()
    {
        var pads = new Pads();
        var bank = new JoystickBank(pads);
        var axes = new AxisControls(PadPreset.Build((string?)null).Blocks);
        pads[0] = Pad(rx: 1f, ry: -0.5f);
        bank.Poll(true);
        axes.Update(bank, NoKey);
        Assert.Equal(-65536, axes.Yaw);            // "- Rudder": right is track_yaw_minus's way, which looks right
        Assert.Equal(-16384, axes.Pitch);          // "+ 5thAxis": up is track_pitch_minus's way, the camera down, looking up
        Assert.Equal(1f, AxisControls.Deflection(-axes.Yaw), 3);
        bank.Poll(true);
        axes.Update(bank, NoKey);                  // the same value again: a delta row is written every frame
        Assert.Equal(-65536, axes.Yaw);
        Assert.Equal(0, axes.Throttle);
        Assert.False(axes.Active(bank) && axes.Throttle != 0);
        pads.List.Clear();
        bank.Poll(true);
        axes.Update(bank, NoKey);
        Assert.Equal(0, axes.Yaw);
        Assert.Equal(0, axes.Pitch);
    }

    [Fact]
    public void FreeLookHasAGunDetentThenLooksRearAndDownLooksAtTheDash()
    {
        var look = new FreeLook();
        Assert.Equal(FreeLook.Pose.Free, look.Update(0.5f, 0));        // the free look's sideways range
        Assert.Equal(FreeLook.Pose.Right, look.Update(0.6f, 0));       // the detent: the arm and the .45 out of the window
        Assert.Equal(FreeLook.Pose.Right, look.Update(0.92f, 0));      // a wide one
        Assert.Equal(FreeLook.Pose.Right, look.Update(0.5f, 0));       // hysteresis: still the glance
        Assert.Equal(FreeLook.Pose.Rear, look.Update(1f, 0));          // all the way: on round to the rear, over the right shoulder
        Assert.False(look.RearLeft);
        Assert.Equal(FreeLook.Pose.Rear, look.Update(0.9f, 0));
        Assert.Equal(FreeLook.Pose.Right, look.Update(0.85f, 0));      // back off: the detent again
        Assert.Equal(FreeLook.Pose.Free, look.Update(0.4f, 0));
        Assert.Equal(FreeLook.Pose.Rear, look.Update(-0.96f, 0.15f));  // slammed left goes straight to the rear, over the left shoulder
        Assert.True(look.RearLeft);
        Assert.Equal(FreeLook.Pose.Left, look.Update(-0.8f, 0.1f));
        Assert.Equal(FreeLook.Pose.Free, look.Update(0, 0));
        Assert.Equal(FreeLook.Pose.Dash, look.Update(0.1f, 0.95f));    // pulled hard down: the dash and the wheel, never the rear
        Assert.Equal(FreeLook.Pose.Dash, look.Update(0, 0.8f));
        Assert.Equal(FreeLook.Pose.Free, look.Update(0, 0.5f));
        Assert.Equal(FreeLook.Pose.Free, look.Update(0, -1f));         // pushed up: free look, no pose
    }

    [Fact]
    public void FreeLookAimsInProportionWithinTheCockpitsReach()
    {
        Assert.Null(FreeLook.Head(0.05f, -0.1f));                      // a resting stick's drift
        var (yaw, pitch) = FreeLook.Head(1, 0)!.Value;
        Assert.Equal(FreeLook.MaxYaw, yaw, 3);
        Assert.Equal(FreeLook.Rest, pitch, 3);
        var half = FreeLook.Head(-0.31f, 0)!.Value;                    // halfway between the dead zone and FreeEdge
        Assert.Equal(-FreeLook.MaxYaw / 2, half.Yaw, 1);
        Assert.Equal(FreeLook.Rest - FreeLook.MaxUp, FreeLook.Head(0, -1)!.Value.Pitch, 3);   // up: the game's negative pitch
        Assert.Equal(FreeLook.Rest + FreeLook.MaxDown, FreeLook.Head(0, 1)!.Value.Pitch, 3);
    }

    [Fact]
    public void OnlyTheDpadNavigatesTheSticksAndOtherButtonsNever()
    {
        var menu = new PadMenu();
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(lx: 1f, ly: -1f)));             // the left stick: feedback only
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(rx: -1f, ry: 1f, lt: 1, rt: 1)));
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(buttons: [PadButton.X, PadButton.Y, PadButton.LeftShoulder, PadButton.RightShoulder, PadButton.Back])));
        Assert.Equal(PadMenu.Act.Next | PadMenu.Act.Down, menu.Update(Pad(buttons: [PadButton.DpadDown])));
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(buttons: [PadButton.DpadDown])));  // held: once
        Assert.Equal(PadMenu.Act.Prev | PadMenu.Act.Left, menu.Update(Pad(buttons: [PadButton.DpadLeft])));
        Assert.Equal(PadMenu.Act.Open, menu.Update(Pad(buttons: [PadButton.A])));
        // the list had the pad (capture first): the A that picked there does not open again, nor anything still held
        menu.Settle();
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(buttons: [PadButton.A])));
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad(buttons: [PadButton.A, PadButton.DpadUp])));
        Assert.Equal(PadMenu.Act.None, menu.Update(Pad()));
        Assert.Equal(PadMenu.Act.Back, menu.Update(Pad(buttons: [PadButton.B])));
        Assert.Equal(PadMenu.Act.Done, menu.Update(Pad(buttons: [PadButton.Start])));
    }

    [Fact]
    public void APadPluggedInMidSessionIsReadAtOnce()
    {
        var pads = new Pads();
        pads.List.Clear();
        var bank = new JoystickBank(pads);
        var axes = new AxisControls(PadPreset.Build((string?)null).Blocks);
        bank.Poll(true);
        Assert.Equal(0, bank.Count);
        pads.List.Add(Pad(lx: -1f, buttons: [PadButton.A]));
        bank.Poll(true);
        Assert.Equal(1, bank.Count);
        Assert.True(bank.Held(0, "Button1"));
        axes.Update(bank, NoKey);
        Assert.Equal(65536, axes.Steer);            // "- Left/Right": full left
    }
}
