using System.Text;

namespace I76.Formats.Input;

/// <summary>
/// The player's bindings as the Control Configuration edits them: every block of input.map, by command. The original's shell
/// kept one binding per command (the first term of the last block) and wrote only that back; here every block is kept and
/// written back, and the screen shows what the original shows.
/// </summary>
public sealed class BindingTable
{
    public const string None = "-----";
    private readonly List<string> _order = [];
    private readonly Dictionary<string, List<InputBlock>> _by = new(StringComparer.OrdinalIgnoreCase);

    public static BindingTable Parse(string text) => FromBlocks(InputMapFile.ParseBlocks(text));

    public static BindingTable FromBlocks(IEnumerable<InputBlock> blocks)
    {
        var t = new BindingTable();
        foreach (var b in blocks) t.Add(b);
        return t;
    }

    public BindingTable Clone() => FromBlocks(Blocks);

    private void Add(InputBlock b)
    {
        if (!_by.TryGetValue(b.Command, out var l)) { _by[b.Command] = l = []; _order.Add(b.Command); }
        l.Add(b);
    }

    /// <summary>Every block, a command's together in file order.</summary>
    public IEnumerable<InputBlock> Blocks => _order.SelectMany(c => _by[c]);

    public IReadOnlyList<InputBlock> Of(string command) => _by.TryGetValue(command, out var l) ? l : [];

    private void Set(string command, params InputBlock[] blocks)
    {
        if (_by.TryGetValue(command, out var l)) l.Clear();
        foreach (var b in blocks) Add(b);
    }

    public void Clear(string command) => Set(command);

    /// <summary>The binding the screen shows for a command: the first term of its last block (as the original's shell reads it).</summary>
    public InputTerm? Shown(string command) => Of(command).LastOrDefault()?.Terms.FirstOrDefault();

    public static bool IsAxis(InputTerm t) => JoystickBank.DeviceOf(t.Device) >= 0 && IsAnalogName(t.Channel);

    private static bool IsAnalogName(string c) => VirtualStick.AnalogNames.Any(n => n.Equals(c, StringComparison.OrdinalIgnoreCase)) || c.Equals("HeadRoll", StringComparison.OrdinalIgnoreCase);

    /// <summary>The axis the throttle or steer command is on (its shown term, when that is a joystick axis), or null.</summary>
    public InputTerm? AxisOf(string axisCommand) => Shown(axisCommand) is { } t && IsAxis(t) ? t : null;

    /// <summary>The pad blocks of the keyboard's defaults left out: a table of keys and mouse buttons alone.</summary>
    public BindingTable KeysOnly() => FromBlocks(Blocks.Where(b => !IsPad(b)));

    /// <summary>
    /// What a row shows: the key's or button's name; with the throttle or steer on an axis, the pair of rows
    /// show it as "joystick1 Up" and "joystick1 Down" (Down/Up), "Left" and "Right" (Left/Right), or the axis name on both.
    /// </summary>
    public string Text(string command)
    {
        var (axis, second) = command.ToLowerInvariant() switch
        {
            "throttle_up" => ("throttle", false), "throttle_down" => ("throttle", true),
            "steer_left" => ("steer", false), "steer_right" => ("steer", true),
            _ => (null, false),
        };
        if (axis is not null && AxisOf(axis) is { } a)
        {
            string name = a.Channel.ToLowerInvariant() switch
            {
                "down/up" => second ? "Down" : "Up",
                "left/right" => second ? "Right" : "Left",
                _ => a.Channel,
            };
            return $"{a.Device} {name}";
        }
        return Shown(command)?.Channel ?? None;
    }

    private static bool Same(InputTerm a, InputTerm b) =>
        a.Device.Equals(b.Device, StringComparison.OrdinalIgnoreCase) && Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A joystick channel by its id so that "Button1" and "0" are one; anything else by its name.</summary>
    private static string Norm(InputTerm t) =>
        JoystickBank.DeviceOf(t.Device) >= 0 && !IsAnalogName(t.Channel) && VirtualStick.DiscreteId(t.Channel) is >= 0 and var id ? "#" + id
        : JoystickBank.DeviceOf(t.Device) >= 0 && GenericStick.AxisNumber(t.Channel) is > 0 and var n ? "axis" + n : t.Channel;

    /// <summary>The block a new binding is: "+ device channel", and the shell's "- Shift", "- Control" for a keyboard key.</summary>
    public static InputBlock NewBlock(string command, string device, string channel)
    {
        var terms = new List<InputTerm> { new(true, device, channel) };
        if (InputMapFile.Excludes(command, device, channel)) foreach (var x in InputMapFile.ShellExclusions) terms.Add(new InputTerm(false, "Keyboard", x));
        return new InputBlock(command, [.. terms]);
    }

    /// <summary>Whether a block is a pad's (its first term on a joystick); the rest is the keyboard's and the mouse's.</summary>
    /// <summary>Whether a standard pad's button (a PadButton) is in any command's binding on a pad, as a button or a chord's term: the stock action it has outside the game's commands (Start the menu, Back the standings) then gives way to the command; <paramref name="except"/> is a command that does not count (the map on Back, which shares it by design).</summary>
    public bool PadButtonBound(int padButton, string? except = null)
    {
        int id = VirtualStick.DiscreteIdOfPadButton(padButton, default);
        if (id < 0) return false;
        return Blocks.Any(b => !b.Command.Equals(except, StringComparison.OrdinalIgnoreCase) && b.Terms.Any(t => JoystickBank.DeviceOf(t.Device) >= 0 && VirtualStick.DiscreteId(t.Channel) == id));
    }

    public static bool IsPad(InputBlock b) => b.Terms.Length > 0 && JoystickBank.DeviceOf(b.Terms[0].Device) >= 0;

    /// <summary>The binding a device's side shows for a command: the first term of the last block of that side, or null.</summary>
    public InputTerm? ShownOn(string command, bool pad) => Of(command).LastOrDefault(b => IsPad(b) == pad)?.Terms.FirstOrDefault();

    /// <summary>What the keyboard and mouse side of a row shows: the key's or button's name, or "-----" (the pad's blocks never show).</summary>
    public string KeyText(string command)
    {
        var keys = Of(command).Where(b => !IsPad(b) && b.Terms.Length > 0).Select(b => b.Terms[0].Channel).Distinct().Reverse().Take(2).ToArray();
        return keys.Length == 0 ? None : string.Join(" / ", keys);
    }

    /// <summary>
    /// The port's default keymap over the original's: WASD drives as well as the numpad (as further blocks, the numpad's first), with the
    /// two commands that were on W and S moved to F and I. Applied to the install's files in code; a saved map is never changed.
    /// </summary>
    public BindingTable WithWasd()
    {
        var t = Clone();
        foreach (var (cmd, from, to) in new[] { ("RADAR_CAMERA_TOGGLE", "W", "F"), ("start_engine", "S", "I") })
            t.Set(cmd, [.. t.Of(cmd).Select(b => !IsPad(b) && b.Terms.Length > 0 && b.Terms[0].Channel.Equals(from, StringComparison.OrdinalIgnoreCase) ? NewBlock(cmd, b.Terms[0].Device, to) : b)]);
        foreach (var (cmd, key) in new[] { ("throttle_up", "W"), ("throttle_down", "S"), ("steer_left", "A"), ("steer_right", "D") })
        {
            var want = new InputTerm(true, "keyboard", key);
            if (t.Blocks.Any(b => !IsPad(b) && b.Terms.Length > 0 && Same(b.Terms[0], want))) continue;     // already bound somewhere
            t.Set(cmd, [.. t.Of(cmd), NewBlock(cmd, "keyboard", key)]);
        }
        return t;
    }

    /// <summary>The key manual reverse sits on now that Tab is the scoreboard: X, beside Z (the handbrake) and in the same place on QWERTY, QWERTZ and AZERTY.</summary>
    public const string ReverseKey = "X";

    /// <summary>
    /// Reverse leaves Tab for <see cref="ReverseKey"/> (Tab holds the scoreboard). Only a keyboard block that is still on Tab moves (its
    /// exclusions kept); a custom key, and the pad's blocks, stay. If X is already another command's, reverse is left without a key
    /// rather than take it (and sharing Tab with the scoreboard). Idempotent. <paramref name="changed"/> says whether anything moved.
    /// </summary>
    public BindingTable WithReverseOffTab(out bool changed)
    {
        var t = Clone();
        bool moved = false;
        var want = new InputTerm(true, "keyboard", ReverseKey);
        bool taken = t.Blocks.Any(b => !b.Command.Equals("reverse_direction", StringComparison.OrdinalIgnoreCase) && !IsPad(b) && b.Terms.Length > 0 && Same(b.Terms[0], want));
        var blocks = new List<InputBlock>();
        foreach (var b in t.Of("reverse_direction"))
        {
            if (IsPad(b) || b.Terms.Length == 0 || !b.Terms[0].Channel.Equals("Tab", StringComparison.OrdinalIgnoreCase) || !b.Terms[0].Plus) { blocks.Add(b); continue; }
            moved = true;
            if (!taken) blocks.Add(b with { Terms = [.. b.Terms.Select((x, i) => i == 0 ? x with { Channel = ReverseKey } : x)] });
        }
        if (moved) t.Set("reverse_direction", [.. blocks]);
        changed = moved;
        return t;
    }

    /// <summary>
    /// The scoreboard is held on Tab in a network game (the original's ' and ; sit in different places on different keyboards): the
    /// install's input.def blocks for show_player_scores (Quote) and show_team_scores (SemiColon) become Tab and nothing, in memory
    /// (input.def is never edited; a block that is not the stock one is somebody's own and stays).
    /// </summary>
    public BindingTable WithScoreboardOnTab()
    {
        var t = Clone();
        bool Stock(string cmd, string key) => t.Of(cmd).All(b => b.Terms.Length == 1 && !IsPad(b) && b.Terms[0].Channel.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (Stock("show_player_scores", "Quote")) t.Set("show_player_scores", NewBlock("show_player_scores", "keyboard", "Tab"));
        if (Stock("show_team_scores", "SemiColon")) t.Clear("show_team_scores");
        return t;
    }

    /// <summary>
    /// A key, mouse button, pad button or hat direction goes to a command and off any other. It replaces only its own
    /// side of the command's blocks: a key keeps the command's pad blocks, a pad button its keys. On the accelerate or brake
    /// row (steer left or right) a pad button takes the throttle (steer) off its axis, as the original does.
    /// </summary>
    public void Assign(string command, string device, string channel)
    {
        bool pad = JoystickBank.DeviceOf(device) >= 0;
        var want = new InputTerm(true, device, channel);
        foreach (var other in _order.ToList())
        {
            if (other.Equals(command, StringComparison.OrdinalIgnoreCase)) continue;
            var keep = _by[other].Where(b => !(b.Terms.Length > 0 && Same(b.Terms[0], want))).ToArray();
            if (keep.Length != _by[other].Count) Set(other, keep);
        }
        var axis = command.ToLowerInvariant() switch { "throttle_up" or "throttle_down" => "throttle", "steer_left" or "steer_right" => "steer", _ => null };
        if (pad && axis is not null && AxisOf(axis) is not null) Clear(axis);
        Set(command, [.. Of(command).Where(b => IsPad(b) != pad), NewBlock(command, device, channel)]);
    }

    /// <summary>Takes a command's blocks on one side away (the other side stays).</summary>
    public void ClearOn(string command, bool pad) => Set(command, [.. Of(command).Where(b => IsPad(b) != pad)]);

    /// <summary>A pad input (a button, a hat direction or an axis, on any pad) off every command that has it.</summary>
    public void ClearInput(string channel)
    {
        string want = Norm(new InputTerm(true, "joystick1", channel));
        foreach (var c in _order.ToList())
            if (_by[c].Any(b => IsPad(b) && Norm(b.Terms[0]).Equals(want, StringComparison.OrdinalIgnoreCase)))
                Set(c, [.. _by[c].Where(b => !(IsPad(b) && Norm(b.Terms[0]).Equals(want, StringComparison.OrdinalIgnoreCase)))]);
    }

    /// <summary>
    /// One side back to a default table's: every command's blocks on that side become the defaults' on that side; the other side
    /// of every command is left as it is.
    /// </summary>
    public void ResetSide(BindingTable defaults, bool pad)
    {
        foreach (var c in _order.Concat(defaults._order).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            Set(c, [.. Of(c).Where(b => IsPad(b) != pad), .. defaults.Of(c).Where(b => IsPad(b) == pad)]);
    }

    /// <summary>
    /// An axis for an axis row: throttle or steer ("- joystick1 Down/Up"; the sign is always "-" as the shell writes it), or the
    /// chase camera's yaw or pitch (the port's: "-" on yaw, so that right looks right; "+" on pitch, so that up looks up). One
    /// axis drives one row: the other rows' blocks on the same axis are cleared.
    /// </summary>
    public void AssignAxis(string axisCommand, string device, string channel)
    {
        var want = new InputTerm(axisCommand.Equals("track_pitch_delta", StringComparison.OrdinalIgnoreCase), device, channel);
        foreach (var other in AxisControls.Rows)
            if (!other.Equals(axisCommand, StringComparison.OrdinalIgnoreCase))
                Set(other, [.. Of(other).Where(b => !(IsPad(b) && b.Terms.Length > 0 && Same(b.Terms[0], want)))]);
        Set(axisCommand, [.. Of(axisCommand).Where(b => !IsPad(b)), new InputBlock(axisCommand, [want])]);
    }

    /// <summary>The axis row (throttle, steer, track_yaw_delta, track_pitch_delta) a raw axis feeds, or null. The stick's two have two names ("Left/Right" is "Axis1"); both are the axis.</summary>
    public string? RowOfAxis(int axisNumber) => AxisControls.Rows.FirstOrDefault(r => AxisOf(r) is { } t && GenericStick.AxisNumber(t.Channel) == axisNumber);

    /// <summary>Whether an axis row's sign is the opposite of the one the port gives it when it is assigned ("-" on every row but the camera's pitch).</summary>
    public static bool IsInverted(string row, InputTerm t) => t.Plus != row.Equals("track_pitch_delta", StringComparison.OrdinalIgnoreCase);

    /// <summary>The raw axis the other way round: its row's sign flipped. False when no axis row has it.</summary>
    public bool InvertAxis(int axisNumber)
    {
        if (RowOfAxis(axisNumber) is not { } row || AxisOf(row) is not { } t) return false;
        Set(row, [.. Of(row).Where(b => !IsPad(b)), new InputBlock(row, [t with { Plus = !t.Plus }])]);
        return true;
    }

    /// <summary>Whether the pad (joystick) blocks are exactly those of <paramref name="other"/>, in any order.</summary>
    public bool PadSideIs(BindingTable other)
    {
        static string[] Lines(BindingTable t) => [.. t.Blocks.Where(IsPad).Select(b => b.Command.ToLowerInvariant() + ":" + string.Join(",", b.Terms.Select(x => (x.Plus ? "+" : "-") + x.Device.ToLowerInvariant() + " " + x.Channel.ToLowerInvariant()))).Order()];
        return Lines(this).SequenceEqual(Lines(other));
    }

    /// <summary>
    /// A pad plugged in while the bindings have no pad block at all (a map saved before any pad was seen): the pad's side of
    /// <paramref name="defaults"/>, so the pad plays at once. The keys are left alone, and nothing is written to the file.
    /// </summary>
    public bool AddPadSideIfNone(BindingTable defaults) => AddPadSideIfNone(() => defaults);

    /// <summary>As above, the defaults built (from disk, perhaps) only when there is no pad block to keep.</summary>
    public bool AddPadSideIfNone(Func<BindingTable> defaults)
    {
        if (Blocks.Any(IsPad)) return false;
        ResetSide(defaults(), true);
        return true;
    }

    /// <summary>
    /// The shell writes the chase camera's track keys and the map's alongside each glance: they take the glance's blocks.
    /// </summary>
    public BindingTable WithTwins(IEnumerable<(string Glance, string[] Also)> twins)
    {
        var t = Clone();
        foreach (var (glance, also) in twins)
            foreach (var a in also)
                t.Set(a, [.. t.Of(glance).Select(b => b with { Command = a })]);
        return t;
    }

    /// <summary>
    /// The file: every block with its terms as they are, then input.def verbatim, the blocks it holds not written twice.
    /// </summary>
    public string ToText(string? inputDef = null)
    {
        var inDef = inputDef is null ? [] : InputMapFile.ParseBlocks(inputDef).Select(b => b.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(InputMapFile.Header).Append("\n\n");
        foreach (var b in Blocks)
        {
            if (inDef.Contains(b.Command)) continue;
            sb.Append(b.Command).Append(" {\n");
            for (int i = 0; i < b.Terms.Length; i++)
            {
                var t = b.Terms[i];
                if (i == 0) sb.Append("   ").Append(t.Plus ? '+' : '-').Append(' ').Append(t.Device.PadRight(10)).Append(' ').Append(t.Channel).Append('\n');
                else sb.Append('\t').Append(t.Plus ? '+' : '-').Append(' ').Append(t.Device).Append('\t').Append(t.Channel).Append('\n');
            }
            sb.Append("}\n");
        }
        if (inputDef is not null) sb.Append(inputDef.Replace("\r", "").TrimEnd('\n')).Append('\n');
        return sb.ToString();
    }

    /// <summary>A file this port wrote before the exclusions gets the shell's rule on load (as <see cref="InputMapFile.Migrate"/>).</summary>
    public BindingTable Migrate(string? inputDef = null)
    {
        var inDef = inputDef is null ? [] : InputMapFile.ParseBlocks(inputDef).Select(b => b.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return FromBlocks(Blocks.Select(b =>
            !inDef.Contains(b.Command) && b.Terms is [{ Plus: true } one] && InputMapFile.Excludes(b.Command, one.Device, one.Channel)
                ? NewBlock(b.Command, one.Device, one.Channel) with { Command = b.Command } : b));
    }
}

/// <summary>
/// The port's joystick.map for a modern pad: the keyboard blocks, and on the virtual stick the
/// stock file's axes, A and B as its Button1 and Button2, the "4 button" block uncommented, and the rest of the face on the buttons
/// left. The right stick is the camera (the chase camera's track_yaw_delta and track_pitch_delta rows, which the original has but
/// never bound; in the car the port's free look reads them), RB fires as A does (A last, the one a row shows), so the right thumb can stay on the stick, and the
/// D-pad is link, reverse and the target keys rather than the stock "hat" glances. The stock file's "- ButtonN" cross-exclusions
/// are dropped: they only kept a 2-button stick from firing both.
/// </summary>
public static class PadPreset
{
    private static readonly (string Command, string Channel, bool Axis)[] Pad =
    [
        ("throttle", "Throttle", true), ("steer", "Left/Right", true),
        ("track_yaw_delta", "Rudder", true), ("track_pitch_delta", "5thAxis", true),
        ("weapon_fire", "Button6", false), ("weapon_fire", "Button1", false), ("weapon_cycle", "Button2", false),
        ("TARGET_NEAREST_ENEMY", "Button3", false), ("frontal_target", "Button4", false),
        ("e_brake", "Button5", false), ("SHOW_MAP", "Button7", false),
        ("HONK_HORN", "Button9", false), ("toggle_cmbt_view", "Button10", false),
        ("weapon_link", "HatUp", false), ("reverse_direction", "HatDown", false), ("RESET_TARGET", "HatLeft", false), ("NEXT_TARGET", "HatRight", false),
    ];

    /// <summary>The preset on top of KEYBOARD.MAP's text (null: the pad's blocks alone).</summary>
    public static BindingTable Build(string? keyboardMap) => Build(keyboardMap is null ? new BindingTable() : BindingTable.Parse(keyboardMap));

    public static BindingTable Build(BindingTable keys)
    {
        var t = keys;
        var all = t.Blocks.ToList();
        // an axis is "-" as the shell writes it, but the camera's pitch: up looks up
        foreach (var (cmd, ch, axis) in Pad) all.Add(new InputBlock(cmd, [new InputTerm(!axis || cmd == "track_pitch_delta", "joystick1", ch)]));
        return BindingTable.FromBlocks(all);
    }
}

/// <summary>
/// The port's preset for a device with no standard layout, a flight stick or the like (docs/joystick.md): the stock JOYSTICK.MAP's
/// stick (forward accelerates, back brakes, sideways steers) and hat glances, the trigger fires, the rest of the buttons on
/// the commands the gamepad's face buttons have. A throttle lever, a twist or a slider is bound from the Controller screen.
/// </summary>
public static class GenericPreset
{
    private static readonly (string Command, string Channel, bool Axis)[] Stick =
    [
        ("throttle", "Down/Up", true), ("steer", "Left/Right", true),
        ("weapon_fire", "Button1", false), ("weapon_cycle", "Button2", false), ("TARGET_NEAREST_ENEMY", "Button3", false),
        ("frontal_target", "Button4", false), ("e_brake", "Button5", false), ("weapon_link", "Button6", false), ("SHOW_MAP", "Button7", false),
        ("HONK_HORN", "Button8", false), ("toggle_cmbt_view", "Button9", false), ("reverse_direction", "Button10", false),
        ("pilot_glance_up", "HatUp", false), ("pilot_glance_down", "HatDown", false), ("pilot_glance_left", "HatLeft", false), ("pilot_glance_right", "HatRight", false),
    ];

    public static BindingTable Build(BindingTable keys)
    {
        var all = keys.Blocks.ToList();
        foreach (var (cmd, ch, axis) in Stick) all.Add(new InputBlock(cmd, [new InputTerm(!axis, "joystick1", ch)]));
        return BindingTable.FromBlocks(all);
    }
}

/// <summary>A pad's inputs by the names a modern pad's buttons have (the port's controller screen), over the same input.map names.</summary>
public static class PadNames
{
    private static readonly string[] Buttons = ["A", "B", "X", "Y", "LB", "RB", "Back", "Start", "L3", "R3"];

    /// <summary>"Button1" is "A", "HatUp" is "D-pad Up", an axis "Left Stick X"; a name it has none for stays as it is.</summary>
    public static string Of(string channel)
    {
        int id = VirtualStick.AnalogNames.Any(n => n.Equals(channel, StringComparison.OrdinalIgnoreCase)) ? -1 : VirtualStick.DiscreteId(channel);
        if (id >= 0 && id < Buttons.Length) return Buttons[id];
        if (id >= VirtualStick.ButtonCount) return "D-pad " + new[] { "Up", "Right", "Down", "Left" }[id - VirtualStick.ButtonCount];
        if (id >= 0) return $"Button {id + 1}";
        if (channel.StartsWith("Axis", StringComparison.OrdinalIgnoreCase) && int.TryParse(channel.AsSpan(4), out var n)) return $"Axis {n}";
        return channel.ToLowerInvariant() switch
        {
            "down/up" => "Left Stick Y", "left/right" => "Left Stick X", "throttle" => "LT \u2212 RT",
            "rudder" => "Right Stick X", "5thaxis" => "Right Stick Y", _ => channel,
        };
    }

    public static string Of(InputTerm? t) => t is null ? BindingTable.None : Of(t.Channel);
}
