using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

public class InputMapTests
{
    private const string Shipped = "# Interstate '76 Input mapping file\nthrottle_up {\n   + keyboard   UpArrow\n\t- Keyboard\tShift\n\t- Keyboard\tControl\n}\npilot_glance_left {\n   + keyboard GreyLeftArrow\n}\n";

    [Fact]
    public void ParseKeepsTheMinusTerms()
    {
        var m = InputMapFile.Parse(Shipped);
        Assert.Equal("UpArrow", m["throttle_up"].Key);
        Assert.Equal(["Shift", "Control"], m["throttle_up"].Exclude);
        Assert.Empty(m["pilot_glance_left"].Exclude);
    }

    [Fact]
    public void WriteRoundTripsTheMinusTerms()
    {
        var m = InputMapFile.Parse(Shipped);
        var again = InputMapFile.Parse(InputMapFile.Write(m));
        Assert.Equal(m.Keys.Order(), again.Keys.Order());
        foreach (var (k, b) in m) { Assert.Equal(b.Key, again[k].Key); Assert.Equal(b.Exclude, again[k].Exclude); }
    }

    [Fact]
    public void TheShellRuleSkipsGlancesTwinsModifiersAndTheMouse()
    {
        Assert.True(InputMapFile.Excludes("weapon_fire", "keyboard", "Space"));
        Assert.False(InputMapFile.Excludes("pilot_glance_up", "keyboard", "GreyDownArrow"));
        Assert.False(InputMapFile.Excludes("track_yaw_minus", "keyboard", "GreyLeftArrow"));
        Assert.False(InputMapFile.Excludes("overview_x_plus", "keyboard", "GreyRightArrow"));
        Assert.False(InputMapFile.Excludes("e_brake", "keyboard", "LeftShift"));
        Assert.False(InputMapFile.Excludes("weapon_fire", "Mouse", "LeftBtn"));
    }

    [Fact]
    public void WriteAppendsInputDefOnceAndAFileWithoutMinusLinesMigrates()
    {
        const string def = "zoom_factor_reset {\n\t+ keyboard\tGreyEnd\n}\n";
        var m = InputMapFile.Parse("zoom_factor_reset {\n + keyboard GreyEnd\n}\nweapon_fire {\n + keyboard Space\n}\n");
        var text = InputMapFile.Write(m, def);
        Assert.Single(text.Split("zoom_factor_reset {").Skip(1));
        Assert.False(InputMapFile.NeedsMigration(InputMapFile.Write(InputMapFile.Migrate(m, def))));
        var old = InputMapFile.Header + "\n\nweapon_fire {\n   + keyboard   Space\n}\n";
        Assert.True(InputMapFile.NeedsMigration(old));
        Assert.Equal(["Shift", "Control"], InputMapFile.Migrate(InputMapFile.Parse(old))["weapon_fire"].Exclude);
    }

    [Fact]
    public void MigratingAnOldPortFileKeepsEveryCustomBindingAndOnlyAddsTheExclusions()
    {
        const string def = "zoom_factor_reset {\n\t+ keyboard\tGreyEnd\n}\nshow_team_scores {\n\t+ keyboard\tSemiColon\n}\n";
        // as the port's Save wrote it before the exclusions: the player's own keys, a mouse button, a glance and its twin
        var old = InputMapFile.Header + "\n\n"
            + "throttle_up {\n   + keyboard   W\n}\nweapon_fire {\n   + Mouse      LeftBtn\n}\ne_brake {\n   + keyboard   RightShift\n}\n"
            + "pilot_glance_left {\n   + keyboard   J\n}\ntrack_yaw_minus {\n   + keyboard   J\n}\n"
            + "zoom_factor_reset {\n   + keyboard   GreyEnd\n}\nshow_team_scores {\n   + keyboard   SemiColon\n}\n";
        Assert.True(InputMapFile.NeedsMigration(old));
        var before = InputMapFile.Parse(old);
        var m = InputMapFile.Migrate(before, def);
        Assert.Equal(before.Keys.Order(), m.Keys.Order());
        foreach (var (k, b) in before) { Assert.Equal(b.Device, m[k].Device); Assert.Equal(b.Key, m[k].Key); }
        Assert.Equal(["Shift", "Control"], m["throttle_up"].Exclude);
        Assert.Empty(m["weapon_fire"].Exclude);
        Assert.Empty(m["e_brake"].Exclude);
        Assert.Empty(m["pilot_glance_left"].Exclude);
        Assert.Empty(m["track_yaw_minus"].Exclude);
        Assert.Empty(m["zoom_factor_reset"].Exclude);
        // the next Save's file reads back to the same bindings, input.def once, and needs no migration again
        var text = InputMapFile.Write(m, def);
        Assert.False(InputMapFile.NeedsMigration(text));
        var again = InputMapFile.Parse(text);
        Assert.Equal(m.Keys.Order(), again.Keys.Order());
        foreach (var (k, b) in m) { Assert.Equal(b.Device, again[k].Device); Assert.Equal(b.Key, again[k].Key); Assert.Equal(b.Exclude, again[k].Exclude); }
        Assert.Single(text.Split("show_team_scores {").Skip(1));
    }

    /// <summary>The shipped JOYSTICK.MAP's shape: the keyboard blocks, then "2 button joystick entries".</summary>
    private const string Stick =
        "# Interstate '76 Input mapping file\n# 2 button joystick defaults\n"
        + "throttle_up {\n   + keyboard     UpArrow\n}\nthrottle_down {\n   + keyboard     DownArrow\n}\n"
        + "steer_right {\n   + keyboard     RightArrow\n}\nsteer_left {\n   + keyboard     LeftArrow\n}\n"
        + "weapon_fire {\n   + keyboard     Space\n   - keyboard     Shift\n   - keyboard     Control\n}\n"
        + "weapon_cycle {\n   + keyboard     Enter\n   - keyboard     Shift\n}\n"
        + "throttle             {\n   - joystick1    Down/Up\n}\nsteer                {\n   - joystick1    Left/Right\n}\n"
        + "weapon_fire          {\n   + joystick1    Button1\n   - joystick1    Button2\n}\n"
        + "weapon_cycle         {\n   + joystick1    Button2\n   - joystick1    Button1\n}\n"
        + "#target_nearest_enemy {\n#   + joystick1    Button3\n#}\n";

    [Fact]
    public void BlocksKeepEveryTermAndEveryBlockOfACommand()
    {
        var b = InputMapFile.ParseBlocks(Stick);
        Assert.Equal(2, b.Count(x => x.Command == "weapon_fire"));
        var fire = b.Last(x => x.Command == "weapon_fire");
        Assert.Equal([new InputTerm(true, "joystick1", "Button1"), new InputTerm(false, "joystick1", "Button2")], fire.Terms);
        Assert.Equal(new InputTerm(false, "joystick1", "Down/Up"), b.Single(x => x.Command == "throttle").Terms[0]);
        Assert.DoesNotContain(b, x => x.Command.Contains("target_nearest"));   // a commented block is not there
    }

    [Fact]
    public void ALoadedJoystickMapShowsWhatTheOriginalShowsAndSavesEveryBlock()
    {
        var t = BindingTable.Parse(Stick);
        Assert.Equal("joystick1 Up", t.Text("throttle_up"));
        Assert.Equal("joystick1 Down", t.Text("throttle_down"));
        Assert.Equal("joystick1 Left", t.Text("steer_left"));
        Assert.Equal("joystick1 Right", t.Text("steer_right"));
        Assert.Equal("Button1", t.Text("weapon_fire"));        // the last block's first term, no device
        Assert.Equal("Button2", t.Text("weapon_cycle"));
        Assert.Equal(BindingTable.None, t.Text("special1"));
        // Done: the original kept one binding per command and lost Space, Enter, the numpad keys and the "-" terms
        var again = BindingTable.Parse(t.ToText());
        Assert.Equal(t.Blocks.Count(), again.Blocks.Count());
        Assert.Equal(2, again.Of("weapon_fire").Count);
        Assert.Equal("Space", again.Of("weapon_fire")[0].Terms[0].Channel);
        Assert.Equal(new InputTerm(false, "joystick1", "Button2"), again.Of("weapon_fire")[1].Terms[1]);
        Assert.Equal("UpArrow", again.Of("throttle_up")[0].Terms[0].Channel);
        Assert.Equal("joystick1 Up", again.Text("throttle_up"));
        Assert.Equal(new InputTerm(false, "joystick1", "Down/Up"), again.Of("throttle")[0].Terms[0]);
    }

    [Fact]
    public void ABlockWrittenByTheShellsGrammarRoundTripsWithTabbedMinusLines()
    {
        // "\t- joystick1 <text>": the shell's own spacing for an axis such as Throttle or Rudder
        var t = BindingTable.Parse("throttle {\n\t- joystick1\tRudder\n}\n");
        Assert.Equal("joystick1 Rudder", t.Text("throttle_up"));
        Assert.Equal("joystick1 Rudder", t.Text("throttle_down"));
        Assert.Equal("joystick1 Rudder", BindingTable.Parse(t.ToText()).Text("throttle_up"));
    }

    [Fact]
    public void APadButtonOnAnAxisRowIsAHeldKeyNotABrokenBinding()
    {
        var t = BindingTable.Parse(Stick);
        t.Assign("throttle_up", "joystick1", "Button5");
        Assert.Equal("Button5", t.Text("throttle_up"));
        Assert.Empty(t.Of("throttle"));                         // off the axis, as the original does for any input on this row
        Assert.Equal([new InputTerm(true, "joystick1", "Button5")], t.Of("throttle_up").Single(BindingTable.IsPad).Terms);
        // the pad's button left weapon_fire's joystick block when it moved (Button1 was its first term)
        t.Assign("steer_left", "joystick1", "Button1");
        Assert.Equal("Button1", t.Text("steer_left"));
        Assert.Single(t.Of("weapon_fire"));
        // a keyboard key gets the shell's two exclusions
        t.Assign("special1", "keyboard", "Six");
        Assert.Equal(2, t.Of("special1").Single().Terms.Count(x => !x.Plus));
    }

    [Fact]
    public void PickingAnAxisSetsTheMinusBlockAndOneAxisCannotDriveBoth()
    {
        var t = BindingTable.Parse(Stick);
        t.AssignAxis("throttle", "joystick1", "Left/Right");   // the steer's axis
        Assert.Equal("joystick1 Left", t.Text("throttle_up"));
        Assert.Equal("joystick1 Right", t.Text("throttle_down"));
        Assert.Equal("LeftArrow", t.Text("steer_left"));          // the axis is gone from the pair, its own key shows
        Assert.Single(t.Of("throttle"));
        Assert.Empty(t.Of("steer"));
        Assert.Equal(new InputTerm(false, "joystick1", "Left/Right"), BindingTable.Parse(t.ToText()).Of("throttle")[0].Terms[0]);
        t.AssignAxis("steer", "joystick1", "Throttle");
        Assert.Equal("joystick1 Throttle", t.Text("steer_left"));
        Assert.Equal("joystick1 Throttle", t.Text("steer_right"));
    }

    [Fact]
    public void ThePadPresetIsTheKeyboardBlocksPlusTheStickAndRoundTrips()
    {
        var t = PadPreset.Build(Stick.Split("throttle             {")[0]);   // the keyboard half
        Assert.Equal("joystick1 Throttle", t.Text("throttle_up"));
        Assert.Equal("joystick1 Left", t.Text("steer_left"));
        Assert.Equal(new InputTerm(false, "joystick1", "Throttle"), t.Of("throttle").Single().Terms[0]);
        Assert.Equal("Button1", t.Text("weapon_fire"));
        Assert.Equal("Button2", t.Text("weapon_cycle"));
        Assert.Equal("Space", t.Of("weapon_fire")[0].Terms[0].Channel);       // the keyboard still fires
        Assert.Single(t.Of("weapon_fire")[^1].Terms);                          // no cross-exclusion
        foreach (var (cmd, ch) in new[] { ("e_brake", "Button5"), ("SHOW_MAP", "Button7"), ("HONK_HORN", "Button9"), ("toggle_cmbt_view", "Button10"), ("TARGET_NEAREST_ENEMY", "Button3"), ("frontal_target", "Button4") })
            Assert.Equal(ch, t.Text(cmd));
        var again = BindingTable.Parse(t.ToText());
        Assert.Equal(t.Blocks.Count(), again.Blocks.Count());
    }

    [Fact]
    public void ThePresetsRightStickIsTheCameraRbFiresAndTheDpadNoLongerGlances()
    {
        var t = PadPreset.Build("weapon_fire {\n   + keyboard Space\n}\npilot_glance_left {\n   + keyboard GreyLeftArrow\n}\n");
        // the right stick on the chase camera's axis rows: "-" on yaw (right looks right), "+" on pitch (up looks up)
        Assert.Equal(new InputTerm(false, "joystick1", "Rudder"), t.AxisOf("track_yaw_delta"));
        Assert.Equal(new InputTerm(true, "joystick1", "5thAxis"), t.AxisOf("track_pitch_delta"));
        // A and RB both fire; reverse is on the D-pad's down
        Assert.Equal(["Button6", "Button1"], t.Of("weapon_fire").Where(BindingTable.IsPad).Select(b => b.Terms[0].Channel));
        Assert.Equal("HatDown", t.ShownOn("reverse_direction", true)!.Channel);
        Assert.Equal("HatUp", t.ShownOn("weapon_link", true)!.Channel);
        Assert.Equal("HatLeft", t.ShownOn("RESET_TARGET", true)!.Channel);
        Assert.Equal("HatRight", t.ShownOn("NEXT_TARGET", true)!.Channel);
        // no glance or track key on the pad: the stick does it, and nothing is on two commands but Fire
        foreach (var c in new[] { "pilot_glance_up", "pilot_glance_down", "pilot_glance_left", "pilot_glance_right", "track_yaw_plus", "track_yaw_minus", "track_pitch_plus", "track_pitch_minus" })
            Assert.Null(t.ShownOn(c, true));
        Assert.Equal("GreyLeftArrow", t.KeyText("pilot_glance_left"));
        var pad = t.Blocks.Where(BindingTable.IsPad).Select(b => b.Terms[0].Channel.ToLowerInvariant()).ToList();
        Assert.Equal(pad.Count, pad.Distinct().Count());
        // saved with the shell's twins, the track keys stay the keyboard's
        var saved = BindingTable.Parse(t.WithTwins([("pilot_glance_left", ["track_yaw_minus", "overview_x_minus"])]).ToText());
        Assert.Equal("GreyLeftArrow", saved.KeyText("track_yaw_minus"));
        Assert.Equal(new InputTerm(false, "joystick1", "Rudder"), saved.AxisOf("track_yaw_delta"));
    }

    [Fact]
    public void ACameraAxisPickedTakesTheAxisOffTheOtherRowsWithTheCamerasSign()
    {
        var t = PadPreset.Build((string?)null);
        t.AssignAxis("track_pitch_delta", "joystick1", "Down/Up");
        Assert.Equal(new InputTerm(true, "joystick1", "Down/Up"), t.AxisOf("track_pitch_delta"));
        t.AssignAxis("track_yaw_delta", "joystick1", "Left/Right");      // the steer's axis
        Assert.Null(t.AxisOf("steer"));
        Assert.Equal(new InputTerm(false, "joystick1", "Left/Right"), t.AxisOf("track_yaw_delta"));
        t.AssignAxis("steer", "joystick1", "Left/Right");                 // and back
        Assert.Null(t.AxisOf("track_yaw_delta"));
        t.ClearInput("Down/Up");
        Assert.Null(t.AxisOf("track_pitch_delta"));
    }

    [Fact]
    public void APadPluggedInFillsTheMapsPadSideOnlyWhenItHasNone()
    {
        var keys = BindingTable.Parse("weapon_fire {\n   + keyboard Space\n}\n");
        Assert.True(keys.AddPadSideIfNone(PadPreset.Build((string?)null)));
        Assert.Equal("Button1", keys.ShownOn("weapon_fire", true)!.Channel);
        Assert.Equal("Space", keys.KeyText("weapon_fire"));
        Assert.NotNull(keys.AxisOf("steer"));
        // a map with a pad block of its own is the player's: left as it is
        var mine = BindingTable.Parse("weapon_fire {\n   + keyboard Space\n}\nHONK_HORN {\n   + joystick1 Button3\n}\n");
        Assert.False(mine.AddPadSideIfNone(PadPreset.Build((string?)null)));
        Assert.Null(mine.AxisOf("steer"));
        Assert.Single(mine.Of("weapon_fire"));
    }

    [Fact]
    public void AKeyReplacesOnlyTheKeyboardSideAndAPadButtonOnlyThePadSide()
    {
        var t = PadPreset.Build("weapon_fire {\n   + keyboard Space\n}\nweapon_cycle {\n   + keyboard Tab\n}\n");
        t.Assign("weapon_fire", "keyboard", "Q");
        Assert.Equal("Q", t.KeyText("weapon_fire"));
        Assert.Equal("Button1", t.ShownOn("weapon_fire", true)!.Channel);
        Assert.Equal(3, t.Of("weapon_fire").Count);         // Q, RB and A
        t.Assign("weapon_fire", "joystick1", "Button4");
        Assert.Equal("Q", t.KeyText("weapon_fire"));
        Assert.Equal("Button4", t.ShownOn("weapon_fire", true)!.Channel);
        Assert.Equal(2, t.Of("weapon_fire").Count);
        // the pad button left the command that had it (Button4 was frontal_target's)
        Assert.Null(t.ShownOn("frontal_target", true));
        // a key on the throttle's row does not take the axis away
        t.Assign("throttle_up", "keyboard", "W");
        Assert.NotNull(t.AxisOf("throttle"));
    }

    [Fact]
    public void ClearAndRestoreWorkOnOneSideOnly()
    {
        var kb = "weapon_fire {\n   + keyboard Space\n}\n";
        var t = PadPreset.Build(kb);
        t.Assign("weapon_fire", "keyboard", "Q");
        t.Assign("weapon_fire", "joystick1", "Button4");
        t.ClearOn("weapon_fire", true);
        Assert.Null(t.ShownOn("weapon_fire", true));
        Assert.Equal("Q", t.KeyText("weapon_fire"));
        t.ClearOn("weapon_fire", false);
        Assert.Equal(BindingTable.None, t.KeyText("weapon_fire"));
        // the pad's defaults back: the keys stay as they are
        t.Assign("weapon_fire", "keyboard", "Q");
        t.Assign("weapon_cycle", "joystick1", "Button8");
        t.ResetSide(PadPreset.Build(kb), true);
        Assert.Equal("Button1", t.ShownOn("weapon_fire", true)!.Channel);
        Assert.Equal("Button2", t.ShownOn("weapon_cycle", true)!.Channel);
        Assert.Equal("Q", t.KeyText("weapon_fire"));
        // the keys' defaults back: the pad stays
        t.Assign("weapon_fire", "joystick1", "Button4");
        t.ResetSide(BindingTable.Parse(kb), false);
        Assert.Equal("Space", t.KeyText("weapon_fire"));
        Assert.Equal("Button4", t.ShownOn("weapon_fire", true)!.Channel);
        Assert.NotNull(t.AxisOf("throttle"));
        // and the file still round-trips
        var again = BindingTable.Parse(t.ToText());
        Assert.Equal("Space", again.KeyText("weapon_fire"));
        Assert.Equal("Button4", again.ShownOn("weapon_fire", true)!.Channel);
    }

    [Fact]
    public void ClearingAPadInputTakesItOffEveryCommandAndLeavesTheKeys()
    {
        var t = PadPreset.Build("weapon_fire {\n   + keyboard Space\n}\n");
        t.ClearInput("Button1");
        Assert.Equal("Button6", t.ShownOn("weapon_fire", true)!.Channel);     // RB fires still
        t.ClearInput("Button6");
        Assert.Null(t.ShownOn("weapon_fire", true));
        Assert.Equal("Space", t.KeyText("weapon_fire"));
        t.ClearInput("Throttle");
        Assert.Null(t.AxisOf("throttle"));
        Assert.NotNull(t.AxisOf("steer"));
    }

    [Theory]
    [InlineData("Button1", "A")]
    [InlineData("Button4", "Y")]
    [InlineData("Button5", "LB")]
    [InlineData("Button6", "RB")]
    [InlineData("Button7", "Back")]
    [InlineData("Button8", "Start")]
    [InlineData("Button9", "L3")]
    [InlineData("Button10", "R3")]
    [InlineData("HatUp", "D-pad Up")]
    [InlineData("HatLeft", "D-pad Left")]
    [InlineData("Left/Right", "Left Stick X")]
    [InlineData("Down/Up", "Left Stick Y")]
    [InlineData("Throttle", "LT \u2212 RT")]
    [InlineData("Rudder", "Right Stick X")]
    [InlineData("5thAxis", "Right Stick Y")]
    public void ModernPadNamesOverTheSameMapNames(string channel, string shown) => Assert.Equal(shown, PadNames.Of(channel));

    private const string Stock = "throttle_up {\n   + keyboard UpArrow\n\t- Keyboard\tShift\n}\nsteer_left {\n   + keyboard LeftArrow\n}\nRADAR_CAMERA_TOGGLE {\n   + keyboard W\n\t- Keyboard\tShift\n}\nstart_engine {\n   + keyboard S\n}\n";

    [Fact]
    public void TheDefaultKeymapDrivesOnWasdAsWellAsTheNumpadAndMovesTheDisplacedKeys()
    {
        var t = BindingTable.Parse(Stock).WithWasd();
        Assert.Equal(["UpArrow", "W"], t.Of("throttle_up").Select(b => b.Terms[0].Channel));
        Assert.Equal(["LeftArrow", "A"], t.Of("steer_left").Select(b => b.Terms[0].Channel));
        Assert.Equal("S", t.Of("throttle_down").Single().Terms[0].Channel);
        Assert.Equal("D", t.Of("steer_right").Single().Terms[0].Channel);
        Assert.Contains(t.Of("throttle_down").Single().Terms, x => !x.Plus && x.Channel == "Shift");
        Assert.Equal("F", t.Of("RADAR_CAMERA_TOGGLE").Single().Terms[0].Channel);
        Assert.Equal("I", t.Of("start_engine").Single().Terms[0].Channel);
        Assert.Contains(t.Of("RADAR_CAMERA_TOGGLE").Single().Terms, x => !x.Plus && x.Channel == "Shift");
        Assert.Equal("W / UpArrow", t.KeyText("throttle_up"));
        // the source is not touched, and doing it twice adds nothing
        Assert.Single(BindingTable.Parse(Stock).Of("throttle_up"));
        Assert.Equal(t.ToText(), t.WithWasd().ToText());
        // the file reads back as it was written
        Assert.Equal(t.ToText(), BindingTable.Parse(t.ToText()).ToText());
    }

    [Fact]
    public void ASavedMapIsLeftAsItIs()
    {
        // Controls.Load applies the transform to the install's file only; a saved text parses to itself
        var saved = BindingTable.Parse(Stock);
        Assert.Equal("W", saved.KeyText("RADAR_CAMERA_TOGGLE"));
        Assert.Equal("UpArrow", saved.KeyText("throttle_up"));
    }

    [Fact]
    public void DoneInAMissionPutsTheEditedKeysInUse()
    {
        // what Controls.Save does with the screen's edit: written as text and parsed back as the table in use, the old key off the command
        var live = BindingTable.Parse(Shipped);
        var edit = live.Clone();
        edit.Assign("throttle_up", "keyboard", "Q");
        Assert.Equal("UpArrow", live.KeyText("throttle_up"));        // the running table is untouched until Done
        var inUse = BindingTable.Parse(edit.ToText());
        Assert.Equal("Q", inUse.KeyText("throttle_up"));
        Assert.DoesNotContain("UpArrow", inUse.KeyText("throttle_up"));
        Assert.Equal("GreyLeftArrow", inUse.KeyText("pilot_glance_left"));
    }

    private const string WithTab = "reverse_direction {\n   + keyboard   Tab\n\t- Keyboard\tShift\n\t- Keyboard\tControl\n}\nreverse_direction {\n   + joystick1 HatDown\n}\nshow_player_scores {\n\t+ keyboard\tQuote\n}\nshow_team_scores {\n\t+ keyboard\tSemiColon\n}\n";

    [Fact]
    public void ReverseLeavesTabForXAndKeepsItsExclusionsAndPad()
    {
        var t = BindingTable.Parse(WithTab).WithReverseOffTab(out bool changed);
        Assert.True(changed);
        var kb = t.Of("reverse_direction").First(b => !BindingTable.IsPad(b));
        Assert.Equal("X", kb.Terms[0].Channel);
        Assert.Equal(["Shift", "Control"], kb.Terms.Skip(1).Select(x => x.Channel));
        Assert.Contains(t.Of("reverse_direction"), BindingTable.IsPad);
        // idempotent
        t.WithReverseOffTab(out bool again);
        Assert.False(again);
    }

    [Fact]
    public void ACustomReverseKeyIsLeftAlone()
    {
        var t = BindingTable.Parse("reverse_direction {\n   + keyboard   B\n}\n").WithReverseOffTab(out bool changed);
        Assert.False(changed);
        Assert.Equal("B", t.Of("reverse_direction").Single().Terms[0].Channel);
    }

    [Fact]
    public void ReverseIsUnboundRatherThanTakeAnotherCommandsX()
    {
        var t = BindingTable.Parse("reverse_direction {\n   + keyboard   Tab\n}\nhonk_horn {\n   + keyboard   X\n}\n").WithReverseOffTab(out bool changed);
        Assert.True(changed);
        Assert.Empty(t.Of("reverse_direction"));
        Assert.Equal("X", t.Of("honk_horn").Single().Terms[0].Channel);
    }

    [Fact]
    public void TheScoreboardMovesToTabAndTheTeamKeyGoes()
    {
        var t = BindingTable.Parse(WithTab).WithScoreboardOnTab();
        Assert.Equal("Tab", t.Of("show_player_scores").Single().Terms[0].Channel);
        Assert.Empty(t.Of("show_team_scores"));
        // not the stock key: somebody's own, kept
        var own = BindingTable.Parse("show_player_scores {\n   + keyboard   F9\n}\n").WithScoreboardOnTab();
        Assert.Equal("F9", own.Of("show_player_scores").Single().Terms[0].Channel);
        // nothing in input.def at all: Tab all the same
        Assert.Equal("Tab", new BindingTable().WithScoreboardOnTab().Of("show_player_scores").Single().Terms[0].Channel);
    }

    [Fact]
    public void AStandardPadsStartAndBackAreBoundOnlyWhenTheTableHasThem()
    {
        var none = BindingTable.Parse("honk_horn {\n   + keyboard   X\n}\nfire_weapon_1 {\n   + joystick1   Button1\n}\n");
        Assert.False(none.PadButtonBound(PadButton.Start));
        Assert.False(none.PadButtonBound(PadButton.Back));
        var t = BindingTable.Parse("weapon_dropper {\n   + joystick1   Button8\n}\nSHOW_MAP {\n   + joystick1   Button7\n}\n");
        Assert.True(t.PadButtonBound(PadButton.Start));
        Assert.True(t.PadButtonBound(PadButton.Back));
        // the map sharing Back by design does not count
        Assert.False(t.PadButtonBound(PadButton.Back, "SHOW_MAP"));
        Assert.False(t.PadButtonBound(PadButton.A));
        // a chord's term counts too
        Assert.True(BindingTable.Parse("e_brake {\n   + joystick1   Button1\n   + joystick1   Button7\n}\n").PadButtonBound(PadButton.Back));
    }
}
