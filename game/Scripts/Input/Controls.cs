using System.Text;
using Godot;
using I76.Formats.Input;

namespace I76.Game;

/// <summary>
/// The player's controls as the original keeps them: input.map, one block per
/// command (`name {` / `   + device   Key` / `}`), the engine's DirectInput key
/// names ("UpArrow" is the numpad's 8, "GreyUpArrow" the arrow key; scan-code
/// order from the name table), and on a pad joystickN's Button1.. and HatUp.. and axes.
/// The shell's Control
/// Configuration edits it; the
/// game asks it which commands are held or were pressed. Kept in
/// user://input.map; until one is saved the install's own input.map is read,
/// and Restore goes back to its KEYBOARD.MAP.
/// </summary>
public static class Controls
{
    /// <summary>The shell's binding table, in order; 0 and 3 are the analog axes, with no row.</summary>
    public static readonly string[] Commands =
    [
        "throttle", "throttle_up", "throttle_down", "steer", "steer_left", "steer_right", "shift_down", "shift_up",
        "frontal_target", "NEXT_TARGET", "TARGET_NEAREST_ENEMY", "RESET_TARGET", "RADAR_RANGE_TOGGLE", "RADAR_CAMERA_TOGGLE",
        "start_engine", "SHOW_MAP", "SHOW_NOTEPAD", "TOGGLE_BINOCULARS", "HONK_HORN", "e_brake", "toggle_lights",
        "pilot_glance_down", "pilot_glance_left", "pilot_glance_right", "pilot_glance_up", "pilot_glance_target",
        "weapon_fire", "weapon_cycle", "weapon_link", "hardpoint1_fire", "hardpoint2_fire", "hardpoint3_fire", "hardpoint4_fire",
        "hardpoint5_fire", "special1", "special2", "special3", "POETRY", "toggle_cmbt_view", "reverse_direction",
    ];

    /// <summary>What an unbound command shows.</summary>
    public const string None = "-----";

    /// <summary>The commands the shell writes alongside each glance: the chase camera's track keys and the map's.</summary>
    public static readonly (string Glance, string[] Also)[] GlanceTwins =
    [
        ("pilot_glance_down", ["track_pitch_minus", "overview_z_minus"]),
        ("pilot_glance_left", ["track_yaw_minus", "overview_x_minus"]),
        ("pilot_glance_right", ["track_yaw_plus", "overview_x_plus"]),
        ("pilot_glance_up", ["track_pitch_plus", "overview_z_plus"]),
    ];

    private const string UserPath = "user://input.map";
    public static string? InstallDir { get; set; }

    /// <summary>Every block of the file (the zoom keys and the rest the screen does not show included).</summary>
    private static BindingTable? _table;
    private static BindingTable Table => _table ??= Load();
    private static AxisControls _axes = new([]);

    /// <summary>Where the pads come from: Godot's joypads (tests and tools may put their own).</summary>
    public static IPadSource PadSource { get; set; } = new GodotPads();
    /// <summary>Whether the window has focus; without it the pads read centred and with every button up.</summary>
    public static Func<bool> Focused { get; set; } = () => PadsAlwaysFocused || DisplayServer.WindowIsFocused();
    /// <summary>I76_PAD_FOCUS=1 (a dev hook): the pads read as if the window had focus, for captures in a compositor with no seat (docs/dev-hooks.md).</summary>
    private static readonly bool PadsAlwaysFocused = System.Environment.GetEnvironmentVariable("I76_PAD_FOCUS") == "1";
    private static JoystickBank? _pads;
    private static JoystickBank Pads => _pads ??= new JoystickBank(PadSource);
    private static ulong _polled = ulong.MaxValue;

    /// <summary>The pads read once a frame, the first time they are asked.</summary>
    private static void Refresh(bool force = false)
    {
        ulong f = Engine.GetProcessFrames();
        if (!force && f == _polled) return;
        _polled = f;
        using (Hitch.Time(Hitch.Phase.Input)) Pads.Poll(Focused());
        if (Pads.Count != _seen) { int was = _seen; _seen = Pads.Count; if (was == 0 && _seen > 0) { using var _ = Hitch.Time(Hitch.Phase.PadPlug); Plugged(); } }
    }

    private static int _seen;

    /// <summary>A game switch: the bindings (input.map, else the install's), KEYBOARD.MAP and the axes are read again for the next install. The pads stay.</summary>
    public static void ResetForGameSwitch()
    {
        _table = null; _keyBase = null; _keyBaseDir = null; InstallDir = null;
        _axes = new AxisControls([]);
    }

    /// <summary>
    /// A pad came in mid-session. Load already gives a map with no pad blocks the preset's pad side; this covers a map changed since
    /// (e.g. Control Configuration's Done with every pad binding cleared). A map that has pad blocks is used as it is. Nothing is written.
    /// </summary>
    private static void Plugged()
    {
        if (_table is null) return;     // the first load chooses (the preset, with a pad and no saved map)
        if (_table.AddPadSideIfNone(PadDefaults)) _axes = new AxisControls(_table.Blocks);
    }

    /// <summary>Whether the player has bound this pad button (Start, Back) to a command in Control Configuration; its stock action then does not happen. The pad preset has Back on the map, which does not count (the standings there are in network games, where the map is shut).</summary>
    public static bool PadButtonBound(JoyButton b) => Table.PadButtonBound((int)b, b == JoyButton.Back ? "SHOW_MAP" : null);

    /// <summary>Whether a pad is connected.</summary>
    public static bool HasPad { get { Refresh(); return Pads.Count > 0; } }
    /// <summary>The connected pads' joystick names: "joystick1" and on.</summary>
    public static IEnumerable<string> PadDevices { get { Refresh(); return Enumerable.Range(1, Pads.Count).Select(n => $"joystick{n}"); } }

    /// <summary>The engine's key names in scan-code order, each with the key it is here (null where Godot has none).</summary>
    private static readonly (string Name, Key Key)[] KeyTable =
    [
        ("Esc", Key.Escape), ("One", Key.Key1), ("Two", Key.Key2), ("Three", Key.Key3), ("Four", Key.Key4), ("Five", Key.Key5),
        ("Six", Key.Key6), ("Seven", Key.Key7), ("Eight", Key.Key8), ("Nine", Key.Key9), ("Zero", Key.Key0), ("Minus", Key.Minus),
        ("Equal", Key.Equal), ("Backspace", Key.Backspace), ("Tab", Key.Tab), ("Q", Key.Q), ("W", Key.W), ("E", Key.E), ("R", Key.R),
        ("T", Key.T), ("Y", Key.Y), ("U", Key.U), ("I", Key.I), ("O", Key.O), ("P", Key.P), ("LeftBracket", Key.Bracketleft),
        ("RightBracket", Key.Bracketright), ("Enter", Key.Enter), ("LeftControl", Key.Ctrl), ("A", Key.A), ("S", Key.S), ("D", Key.D),
        ("F", Key.F), ("G", Key.G), ("H", Key.H), ("J", Key.J), ("K", Key.K), ("L", Key.L), ("Semicolon", Key.Semicolon),
        ("Quote", Key.Apostrophe), ("BackQuote", Key.Quoteleft), ("LeftShift", Key.Shift), ("BackSlash", Key.Backslash),
        ("Z", Key.Z), ("X", Key.X), ("C", Key.C), ("V", Key.V), ("B", Key.B), ("N", Key.N), ("M", Key.M), ("Comma", Key.Comma),
        ("Period", Key.Period), ("Slash", Key.Slash), ("GreyStar", Key.KpMultiply), ("Space", Key.Space), ("CAPSLock", Key.Capslock),
        ("F1", Key.F1), ("F2", Key.F2), ("F3", Key.F3), ("F4", Key.F4), ("F5", Key.F5), ("F6", Key.F6), ("F7", Key.F7), ("F8", Key.F8),
        ("F9", Key.F9), ("F10", Key.F10), ("NUMLock", Key.Numlock), ("ScrollLock", Key.Scrolllock), ("Home", Key.Kp7),
        ("UpArrow", Key.Kp8), ("PageUp", Key.Kp9), ("GreyMinus", Key.KpSubtract), ("LeftArrow", Key.Kp4), ("Keypad5", Key.Kp5),
        ("RightArrow", Key.Kp6), ("GreyPlus", Key.KpAdd), ("End", Key.Kp1), ("DownArrow", Key.Kp2), ("PageDown", Key.Kp3),
        ("Insert", Key.Kp0), ("Delete", Key.KpPeriod), ("F11", Key.F11), ("F12", Key.F12), ("KeypadEnter", Key.KpEnter),
        ("GreySlash", Key.KpDivide), ("GreyHome", Key.Home), ("GreyUpArrow", Key.Up), ("GreyPageUp", Key.Pageup),
        ("GreyLeftArrow", Key.Left), ("GreyRightArrow", Key.Right), ("GreyEnd", Key.End), ("GreyDownArrow", Key.Down),
        ("GreyPageDown", Key.Pagedown), ("GreyInsert", Key.Insert), ("GreyDelete", Key.Delete),
    ];

    /// <summary>
    /// The virtual names (either Shift, either Ctrl) and the sides and Alt the name table also has. They
    /// are read from a file but not offered by the screen. Godot's Shift/Ctrl/Alt keys do not tell the side, so a side is either.
    /// </summary>
    private static readonly (string Name, Key Key)[] ModifierNames =
    [
        ("Shift", Key.Shift), ("Control", Key.Ctrl), ("RightShift", Key.Shift), ("RightControl", Key.Ctrl),
        ("LeftAlt", Key.Alt), ("RightAlt", Key.Alt),
    ];

    private static readonly (string Name, MouseButton Button)[] MouseTable =
        [("LeftBtn", MouseButton.Left), ("RightBtn", MouseButton.Right), ("MiddleBtn", MouseButton.Middle), ("Mouse4Btn", MouseButton.Xbutton1), ("Mouse5Btn", MouseButton.Xbutton2)];

    /// <summary>The names the input list offers, as the original's device lists: the keyboard, then the mouse.</summary>
    public static IEnumerable<string> KeyboardNames => KeyTable.Select(k => k.Name);
    public static IEnumerable<string> MouseNames => MouseTable.Select(m => m.Name);

    public static bool IsMouse(string name) =>
        MouseTable.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        || name.Equals("Button4", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Button5", StringComparison.OrdinalIgnoreCase);

    /// <summary>The key a name of the keyboard list is ("GreyDelete", "Backspace"), or null: for the game keys, which Control Configuration binds by the key itself.</summary>
    public static Key? KeyNamed(string name) => KeyOf(name);

    /// <summary>A name that is a modifier alone (either Shift or Control): a game key's capture waits for the key it goes with.</summary>
    public static bool IsModifierName(string name) => ModifierNames.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || name is "LeftControl" or "LeftShift";

    private static Key? KeyOf(string name)
    {
        foreach (var (n, k) in KeyTable) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
        foreach (var (n, k) in ModifierNames) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    private static MouseButton? ButtonOf(string name)
    {
        foreach (var (n, b) in MouseTable) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return b;
        if (name.Equals("Button4", StringComparison.OrdinalIgnoreCase)) return MouseButton.Xbutton1;
        if (name.Equals("Button5", StringComparison.OrdinalIgnoreCase)) return MouseButton.Xbutton2;
        return null;
    }

    /// <summary>The input an event is, by the original's name (a key by where it is on the keyboard; a pad button as "joystick1 Button3"), or null.</summary>
    public static string? NameOf(InputEvent e)
    {
        if (e is InputEventKey k)
        {
            var key = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
            foreach (var (n, kk) in KeyTable) if (kk == key) return n;
            return null;
        }
        if (e is InputEventMouseButton mb)
            foreach (var (n, b) in MouseTable) if (b == mb.ButtonIndex) return n;
        if (e is InputEventJoypadButton jb && PadChannel(jb) is { } ch) return $"joystick{GodotPads.IndexOf(jb.Device) + 1} {VirtualStick.DiscreteName(ch)}";
        return null;
    }

    /// <summary>The discrete channel a pad button event is on the virtual stick, or null.</summary>
    private static int? PadChannel(InputEventJoypadButton jb)
    {
        if (GodotPads.IndexOf(jb.Device) < 0) return null;
        int id = VirtualStick.DiscreteIdOfPadButton(GodotPads.Translate(jb.Device, (int)jb.ButtonIndex), GodotPads.Raw(jb.Device));
        return id < 0 ? null : id;
    }

    /// <summary>A captured or listed input as (device, channel): the mouse's buttons, "joystick1 Button3" / "Button3" / "HatUp" for the first pad, else a key.</summary>
    public static (string Device, string Channel) InputOf(string name)
    {
        if (name.StartsWith("joystick", StringComparison.OrdinalIgnoreCase) && name.IndexOf(' ') is > 0 and var sp) return (name[..sp], name[(sp + 1)..]);
        if (MouseTable.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return ("Mouse", name);
        if (VirtualStick.DiscreteId(name) >= 0) return ("joystick1", name);
        return (IsMouse(name) ? "Mouse" : "keyboard", name);
    }

    // ---- the file --------------------------------------------------------------------------------

    private static BindingTable Load()
    {
        Refresh();
        BindingTable t;
        if (Godot.FileAccess.FileExists(UserPath) && Godot.FileAccess.GetFileAsString(UserPath) is { Length: > 0 } user)
        {
            t = BindingTable.Parse(user);
            // a file from before the exclusions: the shell's rule applied here, the file itself rewritten on the next Save
            if (InputMapFile.NeedsMigration(user)) t = t.Migrate(Install("input.def"));
            t = MoveReverseOffTab(t, user);
        }
        else if (Pads.Count > 0) t = Defaults();    // the shell copies JOYSTICK.MAP over a missing input.map when a stick is present
        else if (Install("input.map") is { } inst) t = BindingTable.Parse(inst).WithWasd();
        else t = Defaults().Migrate(Install("input.def"));
        // a map with no pad blocks at all (saved by the original screen, an older version, or the install's) still lets a pad play:
        // the preset's pad side, in memory only (the keys stay as saved, nothing is written)
        t.AddPadSideIfNone(PadDefaults);
        // a stick plugged in over a map the gamepad preset made (a gamepad's Throttle, triggers and right stick mean nothing on it): the stick's, in memory
        if (PrimaryIsGeneric && t.PadSideIs(PadPreset.Build(KeyBase()))) t.ResetSide(GenericPreset.Build(KeyBase()), true);
        t = t.WithScoreboardOnTab();
        _axes = new AxisControls(t.Blocks);
        return t;
    }

    /// <summary>The backup of the player's input.map taken before reverse left Tab (the scoreboard's key); its presence also means the move was done, once.</summary>
    private static string ScoreboardBackupPath => UserPath + ".pre-scoreboard";

    /// <summary>
    /// Tab holds the scoreboard now, so a saved map whose reverse is still on Tab gets it on X instead: once (a backup of the map is
    /// kept beside it first, and its presence ends any further move, so a later choice of Tab for reverse stays), written back as
    /// the Control Configuration writes it. A map with reverse on any other key is not touched.
    /// </summary>
    private static BindingTable MoveReverseOffTab(BindingTable t, string savedText)
    {
        if (Godot.FileAccess.FileExists(ScoreboardBackupPath)) return t;
        var moved = t.WithReverseOffTab(out bool changed);
        try
        {
            using (var b = Godot.FileAccess.Open(ScoreboardBackupPath, Godot.FileAccess.ModeFlags.Write)) b?.StoreString(savedText);
            if (changed)
            {
                using var f = Godot.FileAccess.Open(UserPath, Godot.FileAccess.ModeFlags.Write);
                f?.StoreString(moved.ToText(Install("input.def")));
            }
        }
        catch (Exception) { }
        return moved;
    }

    /// <summary>With a pad connected the port's joystick.map (KEYBOARD.MAP and the pad's blocks), else KEYBOARD.MAP: what Restore loads.</summary>
    public static BindingTable Defaults()
    {
        return HasPad ? PadDefaults() : KeyboardDefaults();
    }

    /// <summary>KEYBOARD.MAP's keys and mouse buttons alone: what the original screen's Restore loads (the pad's blocks stay as they are).</summary>
    public static BindingTable KeyboardDefaults() => KeyBase().KeysOnly();

    private static BindingTable? _keyBase;
    private static string? _keyBaseDir;
    /// <summary>KEYBOARD.MAP read once per install (a pad coming and going must not read the disk each time).</summary>
    private static BindingTable KeyBase()
    {
        if (_keyBase is null || _keyBaseDir != InstallDir)
        {
            _keyBase = (Install("KEYBOARD.MAP") is { } kb ? BindingTable.Parse(kb) : new BindingTable()).WithWasd().WithReverseOffTab(out _);
            _keyBaseDir = InstallDir;
        }
        return _keyBase.Clone();
    }

    /// <summary>The pad preset, whether or not a pad is connected: what the controller screen's Restore loads.</summary>
    public static BindingTable PadDefaults() => PrimaryIsGeneric ? GenericPreset.Build(KeyBase()) : PadPreset.Build(KeyBase());

    /// <summary>The first device is a flight stick or the like (no standard gamepad layout): its preset is the stick's, not the pad's (docs/joystick.md).</summary>
    public static bool PrimaryIsGeneric { get { Refresh(); return Pads.IsGeneric(0); } }

    private static string? Install(string name)
    {
        if (InstallDir is not { } dir || !System.IO.Directory.Exists(dir)) return null;
        var f = System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(p => System.IO.Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
        return f is null ? null : System.IO.File.ReadAllText(f, Encoding.Latin1);
    }

    /// <summary>A copy of the bindings, for the screen to edit.</summary>
    public static BindingTable Edit() => Table.Clone();

    /// <summary>The binding a command shows: its key's name, or "-----".</summary>
    public static string Binding(string command) => Table.Text(command);

    /// <summary>
    /// The bindings written back as input.map, every block kept, the glances' camera
    /// twins following their glance, then the install's input.def verbatim; then in use.
    /// </summary>
    public static void Save(BindingTable edited)
    {
        var text = edited.WithTwins(GlanceTwins).ToText(Install("input.def"));
        using (var f = Godot.FileAccess.Open(UserPath, Godot.FileAccess.ModeFlags.Write)) f?.StoreString(text);
        // in use as the file has it (input.def's blocks verbatim), as the next run reads it
        _table = BindingTable.Parse(text).WithScoreboardOnTab();
        _axes = new AxisControls(_table.Blocks);
    }

    // ---- the game's questions --------------------------------------------------------------------

    private static bool ModifierDown(string name) =>
        name.EndsWith("Shift", StringComparison.OrdinalIgnoreCase) ? Godot.Input.IsKeyPressed(Key.Shift)
        : name.EndsWith("Control", StringComparison.OrdinalIgnoreCase) ? Godot.Input.IsKeyPressed(Key.Ctrl)
        : KeyOf(name) is { } k && Godot.Input.IsKeyPressed(k);

    private static bool IsModifier(string name) => ModifierNames.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every input event, first: a Shift, Control or Alt the engine still holds while the event's own flags (the system's, of the
    /// moment) say it is up has lost its release (Control+F2 on macOS opens the menu bar, which keeps it); it is let go, or every
    /// binding with "- Shift" / "- Control" would act on its press only (<see cref="StaleModifiers"/>).
    /// </summary>
    public static void Observe(InputEvent e)
    {
        if (e is not InputEventWithModifiers m) return;
        bool own = e is InputEventKey k && (k.Keycode is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta || k.PhysicalKeycode is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta);
        var held = (Godot.Input.IsKeyPressed(Key.Shift) ? StaleModifiers.Mods.Shift : 0) | (Godot.Input.IsKeyPressed(Key.Ctrl) ? StaleModifiers.Mods.Control : 0)
            | (Godot.Input.IsKeyPressed(Key.Alt) ? StaleModifiers.Mods.Alt : 0);
        var flags = (m.ShiftPressed ? StaleModifiers.Mods.Shift : 0) | (m.CtrlPressed ? StaleModifiers.Mods.Control : 0) | (m.AltPressed ? StaleModifiers.Mods.Alt : 0);
        var stale = StaleModifiers.Stale(held, flags, own);
        if (stale == StaleModifiers.Mods.None) return;
        GD.Print($"[controls] {stale} was still down in the engine with its release lost: let go");
        foreach (var (mod, key) in new[] { (StaleModifiers.Mods.Shift, Key.Shift), (StaleModifiers.Mods.Control, Key.Ctrl), (StaleModifiers.Mods.Alt, Key.Alt) })
            if (stale.HasFlag(mod)) Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }

    /// <summary>A keyboard or mouse term's input is held now (by position only: with NumLock off the numpad's 8 is a logical Up, which must not glance as well as drive).</summary>
    private static bool TermDown(InputTerm t)
    {
        if (KeyOf(t.Channel) is { } k) return IsModifier(t.Channel) ? ModifierDown(t.Channel) : Godot.Input.IsPhysicalKeyPressed(k);
        if (ButtonOf(t.Channel) is { } m) return Godot.Input.IsMouseButtonPressed(m);
        return false;
    }

    /// <summary>Set while a chat line is being typed: the keyboard (and mouse) holds and presses no command, so no game key drives or
    /// fires while the letters go into the line; a pad or joystick still drives (its buttons and sticks type nothing).</summary>
    public static bool Suspended { get; set; }

    // a term counts while a chat line is typed only if it is a pad's
    private static bool TermLive(InputTerm t) => (!Suspended || JoystickBank.DeviceOf(t.Device) >= 0) && TermDown(t);

    /// <summary>A network game's menu, picture or movie is up over the running game: the sim does not wait, so the player's own
    /// keys are held back (no glance, no steering) while it is, but the menus still read the keyboard.</summary>
    public static Func<bool>? Held { get; set; }

    /// <summary>Whether the command's input is held now: any of its blocks, every "+" down and every "-" up (the chord).</summary>
    public static bool Down(string command)
    {
        if (Held?.Invoke() == true) return false;
        Refresh();
        foreach (var b in Table.Of(command))
            if (InputChord.Holds(b.Terms, Pads, TermLive)) return true;
        return false;
    }

    /// <summary>The term, as it stands while this event happens (Shift and Control by the event's own flags).</summary>
    private static bool TermUnder(InputTerm t, InputEvent e)
    {
        if (e is InputEventWithModifiers m && IsModifier(t.Channel))
            return t.Channel.EndsWith("Shift", StringComparison.OrdinalIgnoreCase) ? m.ShiftPressed : t.Channel.EndsWith("Control", StringComparison.OrdinalIgnoreCase) ? m.CtrlPressed : ModifierDown(t.Channel);
        return TermDown(t);
    }

    /// <summary>Whether the event is this term's input going down (not a repeat).</summary>
    private static bool Goes(InputEvent e, InputTerm t)
    {
        if (JoystickBank.DeviceOf(t.Device) is >= 0 and var dev)
            return e is InputEventJoypadButton { Pressed: true } jb && GodotPads.IndexOf(jb.Device) == dev
                && PadChannel(jb) is { } id && id == VirtualStick.DiscreteId(t.Channel);
        if (e is InputEventKey { Pressed: true, Echo: false } k && KeyOf(t.Channel) is { } key)
            return (k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode) == key;
        return e is InputEventMouseButton { Pressed: true } mb && ButtonOf(t.Channel) is { } btn && mb.ButtonIndex == btn;
    }

    /// <summary>Whether the event is the command's input going down: a "+" term of one of its blocks, the rest of the chord holding.</summary>
    public static bool Pressed(InputEvent e, string command)
    {
        if (Held?.Invoke() == true || Suspended && e is not InputEventJoypadButton) return false;
        if (e is InputEventJoypadButton) Refresh(force: true);
        foreach (var b in Table.Of(command))
        {
            if (b.Terms.Length == 0 || InputChord.IsDead(b.Terms[^1], Pads)) continue;
            for (int i = 0; i < b.Terms.Length; i++)
            {
                if (!b.Terms[i].Plus || !Goes(e, b.Terms[i])) continue;
                bool ok = true;
                for (int j = 0; j < b.Terms.Length && ok; j++)
                {
                    if (j == i || InputChord.IsDead(b.Terms[j], Pads)) continue;
                    bool held = JoystickBank.DeviceOf(b.Terms[j].Device) >= 0 ? InputChord.Held(b.Terms[j], Pads, TermDown) : !Suspended && TermUnder(b.Terms[j], e);
                    ok = held == b.Terms[j].Plus;
                }
                if (ok) return true;
            }
        }
        return false;
    }

    // ---- the stick -------------------------------------------------------------------------------

    /// <summary>The stick's frame (once a tick): the throttle and steer rows take what the pad's axes have changed to.</summary>
    public static void PollAxes()
    {
        Refresh();
        _ = Table;
        _axes.Update(Pads, TermDown);
    }

    /// <summary>A live stick channel feeds the throttle or steer row (else the keys alone are read).</summary>
    public static bool AxesActive { get { Refresh(); _ = Table; return _axes.Active(Pads); } }

    /// <summary>The throttle row's position on the wire's scale (+ accelerates).</summary>
    public static short AxisThrottle => AxisControls.Wire(_axes.Throttle);
    /// <summary>The steer row's position on the wire's scale (+ is left, the engine's sign).</summary>
    public static short AxisSteer => AxisControls.Wire(_axes.Steer);

    /// <summary>The chase camera's track_yaw_delta and track_pitch_delta rows, −1..+1 (the response's square kept): + turns as track_yaw_plus and track_pitch_plus do.</summary>
    public static (float Yaw, float Pitch) CameraAxes { get { PollAxes(); return (_axes.Yaw / 65536f, _axes.Pitch / 65536f); } }

    /// <summary>The same rows as the look stick's deflection, for the free look: x right, y down, −1..+1.</summary>
    public static (float X, float Y) LookStick { get { PollAxes(); return (-AxisControls.Deflection(_axes.Yaw), AxisControls.Deflection(_axes.Pitch)); } }
}
