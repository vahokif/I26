using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A device with no standard gamepad layout (a flight stick): raw axes and buttons, its preset and its bindings (docs/joystick.md).</summary>
public class GenericJoystickTests
{
    private sealed class Sticks : IPadSource
    {
        public List<PadRaw> List = [];
        public IReadOnlyList<PadRaw> Read() => List;
    }

    private static PadRaw Stick(float[] axes, params int[] raw)
    {
        ulong b = 0;
        foreach (var k in raw) b |= 1UL << k;
        return PadRaw.OfGeneric(axes, b);
    }

    private static bool NoKey(InputTerm _) => false;

    [Fact]
    public void RawButtonsAreButtonsInOrderAndTheHatIsTheirElevenToFourteen()
    {
        Assert.Equal(0, GenericStick.ButtonSlot(0));
        Assert.Equal(10, GenericStick.ButtonSlot(10));
        Assert.Equal(-1, GenericStick.ButtonSlot(11));        // the hat: Godot's D-pad numbers
        Assert.Equal(-1, GenericStick.ButtonSlot(14));
        Assert.Equal(11, GenericStick.ButtonSlot(15));        // a stick's twelfth button follows the eleventh
        Assert.Equal(31, GenericStick.ButtonSlot(35));
        Assert.Equal(-1, GenericStick.ButtonSlot(36));
        Assert.Equal(0, GenericStick.HatDirection(11));       // up
        Assert.Equal(1, GenericStick.HatDirection(14));       // right
        Assert.Equal(2, GenericStick.HatDirection(12));
        Assert.Equal(3, GenericStick.HatDirection(13));
    }

    [Fact]
    public void TheVirtualStickOfAGenericDeviceHasItsStickAxesItsRawAxesItsButtonsAndItsHat()
    {
        var s = VirtualStick.From(Stick([0.25f, -0.5f, 0.1f, 0.75f], 0, 5, 11, 15));
        Assert.Equal(0.25f, s.Axes[1]);                       // Left/Right is axis 1
        Assert.Equal(-0.5f, s.Axes[0]);                       // Down/Up is axis 2
        Assert.Equal(0.75f, s.Axes[VirtualStick.RawFirst + 3]);
        Assert.Equal(0, s.Axes[2]);                           // the gamepad's Throttle and Rudder are not a stick's
        Assert.Equal(1u << 0 | 1u << 5 | 1u << 11, s.Buttons);
        Assert.Equal(1, s.Hat);                               // up
    }

    [Fact]
    public void AGamepadHasNoRawAxesAndItsButtonsAreTheOldOrder()
    {
        var s = VirtualStick.From(new PadRaw(0.5f, 0, 0, 0, 0, 0, 1UL << PadButton.A));
        Assert.Equal(1u, s.Buttons);
        for (int i = 0; i < GenericStick.MaxAxes; i++) Assert.Equal(0, s.Axes[VirtualStick.RawFirst + i]);
    }

    [Fact]
    public void ARawAxisIsABindableChannelOnTheBank()
    {
        var src = new Sticks { List = [Stick([0, 0, 0, -1f, 0, 0.5f])] };
        var bank = new JoystickBank(src);
        bank.Poll(true);
        Assert.True(bank.IsGeneric(0));
        Assert.Equal(-65536, bank.Analog(0, "Axis4"));
        Assert.Equal(16384, bank.Analog(0, "Axis6"));
        Assert.Equal(0, bank.Analog(0, "Axis10"));
        Assert.True(bank.HasAnalog(0, "Axis10"));
        Assert.False(bank.HasAnalog(0, "Axis11"));
        Assert.False(bank.HasAnalog(0, "6thAxis"));
        src.List = [new PadRaw()];
        bank.Poll(true);
        Assert.False(bank.IsGeneric(0));
    }

    [Fact]
    public void ADeadZoneCutsTheCentreAndStretchesTheRest()
    {
        Assert.Equal(0, GenericStick.Dead(0.09f, 0.1f));
        Assert.Equal(0, GenericStick.Dead(-0.1f, 0.1f));
        Assert.Equal(1f, GenericStick.Dead(1f, 0.1f), 5);
        Assert.Equal(-1f, GenericStick.Dead(-1f, 0.1f), 5);
        Assert.Equal(0.5f, GenericStick.Dead(0.55f, 0.1f), 4);
        Assert.Equal(0.3f, GenericStick.Dead(0.3f, 0), 5);                 // none: as it is
        Assert.Equal(0.05f, GenericStick.NextDeadZone(0f));
        Assert.Equal(0f, GenericStick.NextDeadZone(0.3f));                 // wraps
        Assert.Equal(0.15f, GenericStick.NextDeadZone(0.10f));
    }

    [Fact]
    public void ADiscreteChannelOfAGenericButtonEventFollowsTheSameSlots()
    {
        var held = Stick([], 11);
        Assert.Equal(3, VirtualStick.DiscreteIdOfPadButton(3, held));
        Assert.Equal(VirtualStick.ButtonCount, VirtualStick.DiscreteIdOfPadButton(11, held));         // HatUp, held
        Assert.Equal(-1, VirtualStick.DiscreteIdOfPadButton(12, held));                              // HatDown is not
        Assert.Equal(11, VirtualStick.DiscreteIdOfPadButton(15, held));                              // Button12
        Assert.Equal("Button12", VirtualStick.DiscreteName(11));
        // the gamepad's A is still button 0, its Start the eighth
        Assert.Equal(7, VirtualStick.DiscreteIdOfPadButton(PadButton.Start, new PadRaw()));
    }

    [Fact]
    public void WhenTheHatIsButtonsRawElevenToFourteenAreButtons12To15AndThereIsNoHat()
    {
        // Godot gives a hat and a real button 11..14 as the same D-pad button events, so the player says which (issue 22)
        PadRaw Buttons(params int[] raw) { ulong b = 0; foreach (var k in raw) b |= 1UL << k; return PadRaw.OfGeneric([], b, hatIsButtons: true); }
        Assert.Equal(11, GenericStick.ButtonSlot(11, true));
        Assert.Equal(14, GenericStick.ButtonSlot(14, true));
        Assert.Equal(15, GenericStick.ButtonSlot(15, true));          // physical button 16 is Button16
        Assert.Equal(-1, GenericStick.ButtonSlot(32, true));
        Assert.Equal(-1, GenericStick.ButtonSlot(11, false));         // the default is as before
        var held = Buttons(11, 13);
        var v = VirtualStick.From(held);
        Assert.Equal(0, v.Hat);
        Assert.Equal((1u << 11) | (1u << 13), v.Buttons);
        Assert.Equal(11, VirtualStick.DiscreteIdOfPadButton(11, held));    // the event for "button 12" is Button12, not HatUp
        Assert.Equal(12, VirtualStick.DiscreteIdOfPadButton(12, Buttons(12)));
        Assert.Equal("Button12", VirtualStick.DiscreteName(VirtualStick.DiscreteIdOfPadButton(11, held)));
        // and in the default mode the same event is the hat, a real hat still works
        var hat = Stick([], 11);
        Assert.Equal(VirtualStick.ButtonCount, VirtualStick.DiscreteIdOfPadButton(11, hat));
        Assert.Equal(1, VirtualStick.From(hat).Hat);
    }

    [Fact]
    public void TheStickPresetDrivesAndShootsOnAGenericStick()
    {
        var table = BindingTable.Parse(GenericPreset.Build(new BindingTable()).ToText());     // through a file, as it is saved
        var src = new Sticks { List = [Stick([-1f, 0, 0, 0], 0)] };         // pushed to the left, trigger down
        var bank = new JoystickBank(src);
        var axes = new AxisControls(table.Blocks);
        bank.Poll(true);
        axes.Update(bank, NoKey);
        Assert.True(axes.Active(bank));
        Assert.Equal(65536, axes.Steer);                                    // "- Left/Right" turns the left-hand push into steer left
        Assert.Equal(0, axes.Throttle);
        src.List = [Stick([0, -1f], 0)];                                    // pushed forward
        bank.Poll(true);
        axes.Update(bank, NoKey);
        Assert.Equal(65536, axes.Throttle);
        Assert.True(InputChord.Holds(table.Of("weapon_fire").Single(BindingTable.IsPad).Terms, bank, NoKey));
        Assert.False(InputChord.Holds(table.Of("weapon_cycle").Single(BindingTable.IsPad).Terms, bank, NoKey));
        src.List = [Stick([0, 0], 14)];                                     // the hat to the right
        bank.Poll(true);
        Assert.True(InputChord.Holds(table.Of("pilot_glance_right").Single(BindingTable.IsPad).Terms, bank, NoKey));
        Assert.False(InputChord.Holds(table.Of("pilot_glance_left").Single(BindingTable.IsPad).Terms, bank, NoKey));
    }

    [Fact]
    public void AnyRawAxisCanBeBoundInvertedAndKeepsItsNameInTheFile()
    {
        var t = GenericPreset.Build(new BindingTable());
        Assert.Equal("throttle", t.RowOfAxis(2));                           // Down/Up is axis 2
        Assert.Equal("steer", t.RowOfAxis(1));
        t.AssignAxis("throttle", "joystick1", "Axis4");                     // a lever
        Assert.Null(t.RowOfAxis(2));
        Assert.Equal("throttle", t.RowOfAxis(4));
        Assert.False(BindingTable.IsInverted("throttle", t.AxisOf("throttle")!));
        Assert.True(t.InvertAxis(4));
        Assert.True(BindingTable.IsInverted("throttle", t.AxisOf("throttle")!));
        Assert.False(t.InvertAxis(7));                                      // not bound
        var back = BindingTable.Parse(t.ToText());
        Assert.Equal("throttle", back.RowOfAxis(4));
        Assert.True(BindingTable.IsInverted("throttle", back.AxisOf("throttle")!));
        // the stick's own name and "Axis1" are one axis: it feeds one row
        t.AssignAxis("track_yaw_delta", "joystick1", "Axis1");
        Assert.Equal("track_yaw_delta", t.RowOfAxis(1));
        // the camera's pitch is "+" as assigned, so that is not inverted
        t.AssignAxis("track_pitch_delta", "joystick1", "Axis5");
        Assert.False(BindingTable.IsInverted("track_pitch_delta", t.AxisOf("track_pitch_delta")!));
    }

    [Fact]
    public void ARawAxisClearsOffEveryRowWhateverItsName()
    {
        var t = GenericPreset.Build(new BindingTable());
        t.ClearInput("Axis2");
        Assert.Null(t.RowOfAxis(2));
        Assert.Equal("steer", t.RowOfAxis(1));
    }

    [Fact]
    public void ThePadSideComparisonIgnoresOrderAndTheKeys()
    {
        var a = GenericPreset.Build(new BindingTable());
        var b = BindingTable.Parse(a.ToText());
        Assert.True(a.PadSideIs(b));
        Assert.False(a.PadSideIs(PadPreset.Build(new BindingTable())));
        b.Assign("weapon_fire", "joystick1", "Button9");
        Assert.False(a.PadSideIs(b));
    }

    [Fact]
    public void AxisNamesAreTheOriginalsForTheStickAndAxisNForTheRest()
    {
        Assert.Equal(1, GenericStick.AxisNumber("Left/Right"));
        Assert.Equal(2, GenericStick.AxisNumber("down/up"));
        Assert.Equal(7, GenericStick.AxisNumber("Axis7"));
        Assert.Equal(0, GenericStick.AxisNumber("Axis11"));
        Assert.Equal(0, GenericStick.AxisNumber("Rudder"));
        Assert.Equal("Left/Right", GenericStick.AxisName(1));
        Assert.Equal("Axis3", GenericStick.AxisName(3));
        Assert.Equal("Axis 3", PadNames.Of("Axis3"));
    }

    [Fact]
    public void WithTheOsSayingWhichTheHatAndButton12AreDistinct()
    {
        var none = new NativeStick(0, 0);
        var hatUp = new NativeStick(1, 0);
        var b12 = new NativeStick(0, 1u << 11);                     // physical button 12 (Godot raw 11, the very number of HatUp)
        Assert.Equal(11, HatSplit.Translate(11, hatUp, none));       // stays the hat
        Assert.Equal(15, HatSplit.Translate(11, b12, none));         // a button: Button12
        Assert.Equal(3, HatSplit.Translate(3, b12, none));
        Assert.Equal(19, HatSplit.Translate(15, none, none));        // physical 16 is Button16
        // the numbers the rest of the code reads
        var pad = PadRaw.OfGeneric([], HatSplit.RawBits(b12));
        Assert.Equal(0, VirtualStick.From(pad).Hat);
        Assert.Equal(1u << 11, VirtualStick.From(pad).Buttons);
        var pad2 = PadRaw.OfGeneric([], HatSplit.RawBits(new NativeStick(1, 1u << 11)));   // both at once
        Assert.Equal(1, VirtualStick.From(pad2).Hat);
        Assert.Equal(1u << 11, VirtualStick.From(pad2).Buttons);
        Assert.Equal("Button12", VirtualStick.DiscreteName(VirtualStick.DiscreteIdOfPadButton(HatSplit.Translate(11, b12, none), pad)));
        Assert.Equal("HatUp", VirtualStick.DiscreteName(VirtualStick.DiscreteIdOfPadButton(HatSplit.Translate(11, hatUp, none), PadRaw.OfGeneric([], HatSplit.RawBits(hatUp)))));
        // physical 13..15 are Button13..15, 16 and up as before
        var many = PadRaw.OfGeneric([], HatSplit.RawBits(new NativeStick(0, (1u << 12) | (1u << 14) | (1u << 20))));
        Assert.Equal((1u << 12) | (1u << 14) | (1u << 20), VirtualStick.From(many).Buttons);
        // both held: the one that just came on; a release (nothing held) is the one that was
        var both = new NativeStick(1, 1u << 11);
        Assert.Equal(15, HatSplit.Translate(11, both, hatUp));       // hat was already up, the button came
        Assert.Equal(11, HatSplit.Translate(11, both, b12));         // button was held, the hat came
        Assert.Equal(15, HatSplit.Translate(11, none, b12));
        Assert.Equal(11, HatSplit.Translate(11, none, hatUp));
        // directions: Godot 12 down, 13 left, 14 right
        Assert.Equal(12, HatSplit.Translate(12, new NativeStick(4, 0), none));
        Assert.Equal(17, HatSplit.Translate(13, new NativeStick(0, 1u << 13), none));
        Assert.Equal(14, HatSplit.Translate(14, new NativeStick(2, 0), none));
    }

    [Fact]
    public void TheOsHatValuesBecomeDirections()
    {
        Assert.Equal(0, HatSplit.HatOfPov(0xFFFF));
        Assert.Equal(1, HatSplit.HatOfPov(0));
        Assert.Equal(2, HatSplit.HatOfPov(9000));
        Assert.Equal(4, HatSplit.HatOfPov(18000));
        Assert.Equal(8, HatSplit.HatOfPov(27000));
        Assert.Equal(1 | 8, HatSplit.HatOfPov(31500));
        Assert.Equal(1 | 2, HatSplit.HatOfPov(4500));
        Assert.Equal(1 | 2, (int)HatSplit.HatOfAxes(1, -1));
        Assert.Equal(4, HatSplit.HatOfAxes(0, 1));
    }
}
