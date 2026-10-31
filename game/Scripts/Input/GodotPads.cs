using Godot;
using I76.Formats.Input;

namespace I76.Game;

/// <summary>Godot's connected joypads, in order, as <see cref="PadRaw"/>: pad N is joystickN. Pads Godot has a standard mapping for come first; any other device (a flight stick) follows as a generic one, with raw axes and buttons.</summary>
public sealed class GodotPads : IPadSource
{
    // the standard-button numbers PadButton has are Godot's (a wrong one stops the build)
    private const int Check = 1 / ((int)JoyButton.A == PadButton.A && (int)JoyButton.Start == PadButton.Start && (int)JoyButton.LeftShoulder == PadButton.LeftShoulder
        && (int)JoyButton.DpadUp == PadButton.DpadUp && (int)JoyButton.DpadLeft == PadButton.DpadLeft && (int)JoyButton.Misc1 == PadButton.Misc1
        && (int)JoyButton.Touchpad == PadButton.Touchpad ? 1 : 0);

    private static List<int> _devices = [];
    private static int _standard;
    private static float _dead = -1;
    private static ulong _devicesAt = ulong.MaxValue;

    /// <summary>The known pads, asked of Godot once a frame (it is a fresh Array and a lookup per pad each time).</summary>
    private static List<int> Devices()
    {
        ulong f = Engine.GetProcessFrames();
        if (f != _devicesAt)
        {
            _devicesAt = f;
            using var _ = Hitch.Time(Hitch.Phase.PadEnum);
            // the pads Godot has a standard layout for come first (joystick1 stays the pad when a stick is also plugged in), then every other
            // device: a flight stick or wheel has no mapping and arrives with raw axis and button numbers
            var all = Input.GetConnectedJoypads().Select(d => (int)d).ToList();
            var known = all.Where(d => Input.IsJoyKnown(d)).ToList();
            _standard = known.Count;
            _devices = [.. known, .. all.Where(d => !known.Contains(d))];
        }
        return _devices;
    }

    /// <summary>The joystick index (0 is joystick1) of a Godot device id, or -1.</summary>
    public static int IndexOf(int device) => Devices().IndexOf(device);

    /// <summary>Whether the device is a pad with a standard layout (its buttons mean A, B, Start...), not a raw stick: only these navigate the menus and the movies by button.</summary>
    public static bool IsStandard(int device) { int i = Devices().IndexOf(device); return i >= 0 && i < _standard; }

    /// <summary>Whether a connected device has no standard layout (docs/joystick.md).</summary>
    public static bool IsGeneric(int device) => Devices().Contains(device) && !IsStandard(device);

    /// <summary>The generic sticks' dead zone, 0..0.3, kept in user://joystick.cfg.</summary>
    public static float DeadZone
    {
        get
        {
            if (_dead < 0)
            {
                var cf = new ConfigFile();
                _dead = cf.Load("user://joystick.cfg") == Error.Ok ? Math.Clamp((float)cf.GetValue("generic", "dead_zone", GenericStick.DefaultDeadZone).AsDouble(), 0f, 0.5f) : GenericStick.DefaultDeadZone;
            }
            return _dead;
        }
        set
        {
            _dead = Math.Clamp(value, 0f, 0.5f);
            var cf = new ConfigFile();
            cf.Load("user://joystick.cfg");
            cf.SetValue("generic", "dead_zone", _dead);
            cf.Save("user://joystick.cfg");
        }
    }

    private static PadDeadZones? _zones;

    /// <summary>The game pads' stick and trigger dead zones (<see cref="PadDeadZones"/>), kept in section "pad" of user://joystick.cfg; a key missing there is its default.</summary>
    public static PadDeadZones Zones
    {
        get
        {
            if (_zones is null)
            {
                var cf = new ConfigFile();
                bool ok = cf.Load("user://joystick.cfg") == Error.Ok;
                _zones = PadDeadZones.Load(k => ok && cf.HasSectionKey(PadDeadZones.Section, k) ? cf.GetValue(PadDeadZones.Section, k).AsDouble() : null);
            }
            return _zones.Value;
        }
        set
        {
            _zones = new(PadDeadZones.Snap(value.Left), PadDeadZones.Snap(value.Right), PadDeadZones.Snap(value.Trigger));
            var cf = new ConfigFile();
            cf.Load("user://joystick.cfg");
            cf.SetValue(PadDeadZones.Section, PadDeadZones.LeftKey, _zones.Value.Left);
            cf.SetValue(PadDeadZones.Section, PadDeadZones.RightKey, _zones.Value.Right);
            cf.SetValue(PadDeadZones.Section, PadDeadZones.TriggerKey, _zones.Value.Trigger);
            cf.Save("user://joystick.cfg");
        }
    }

    /// <summary>The first pad as the platform reports it, before the dead zones: what the dead zone screen shows against them.</summary>
    public static PadRaw? FirstUnfiltered() =>
        Fake is { } f ? f : Devices().Count > 0 ? Raw(Devices()[0], false) : null;

    /// <summary>I76_PAD_AXES="lx,ly,rx,ry,lt,rt" (a dev hook): the dead zone screen shows these in place of a pad, for captures (docs/dev-hooks.md).</summary>
    private static readonly PadRaw? Fake = System.Environment.GetEnvironmentVariable("I76_PAD_AXES") is { } e
        && e.Split(',').Select(t => float.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f).ToArray() is { Length: 6 } a
        ? new PadRaw(a[0], a[1], a[2], a[3], a[4], a[5], 0) : null;

    private static int _hat = -1;

    /// <summary>
    /// Whether a generic stick's raw buttons 11 to 14 are its hat (the default) or its buttons 12 to 15, kept in user://joystick.cfg. Godot
    /// (SDL) delivers a hat as the D-pad's button events, 11 to 14, the very events and pressed state a real button with those numbers
    /// gives, so a stick with a hat and 12 or more buttons cannot be told apart: the player picks which it is.
    /// </summary>
    public static bool HatOnButtons
    {
        get
        {
            if (_hat < 0)
            {
                var cf = new ConfigFile();
                _hat = cf.Load("user://joystick.cfg") == Error.Ok && !cf.GetValue("generic", "hat", true).AsBool() ? 0 : 1;
            }
            return _hat == 1;
        }
        set
        {
            _hat = value ? 1 : 0;
            var cf = new ConfigFile();
            cf.Load("user://joystick.cfg");
            cf.SetValue("generic", "hat", value);
            cf.Save("user://joystick.cfg");
        }
    }

    /// <summary>Whether the OS tells this generic device's hat from its buttons (so the hat switch means nothing for it).</summary>
    public static bool HatKnown(int device) => IsGeneric(device) && NativeSticks.Read(device) is not null;

    /// <summary>Whether the OS splits the hat from the buttons for every generic stick connected (and there is one).</summary>
    public static bool HatsKnown() { var g = Devices().Where(IsGeneric).ToList(); return g.Count > 0 && g.All(HatKnown); }

    /// <summary>A button event's number as the numbering <see cref="Raw"/> uses: Godot's own, unless the OS splits the hat from the buttons for this stick.</summary>
    public static int Translate(int device, int godotButton) =>
        IsGeneric(device) && NativeSticks.Read(device) is { } n ? HatSplit.Translate(godotButton, n.Now, n.Before) : godotButton;

    public static PadRaw Raw(int d, bool filtered = true)
    {
        if (IsGeneric(d))
        {
            float z = DeadZone;
            var native = NativeSticks.Read(d);
            var axes = new float[GenericStick.MaxAxes];
            for (int a = 0; a < axes.Length; a++) axes[a] = GenericStick.Dead(Input.GetJoyAxis(d, (JoyAxis)a), z);
            // the OS says which is the hat and which a button 12 to 15 (Godot cannot); Button N is then physical button N
            if (native is { } n) return PadRaw.OfGeneric(axes, HatSplit.RawBits(n.Now), false);
            ulong raw = 0;
            for (int b = 0; b < GenericStick.MaxButtons; b++) if (Input.IsJoyButtonPressed(d, (JoyButton)b)) raw |= 1UL << b;
            return PadRaw.OfGeneric(axes, raw, !HatOnButtons);
        }
        ulong bits = 0;
        for (int b = 0; b <= PadButton.Touchpad; b++) if (Input.IsJoyButtonPressed(d, (JoyButton)b)) bits |= 1UL << b;
        var pad = new PadRaw(Input.GetJoyAxis(d, JoyAxis.LeftX), Input.GetJoyAxis(d, JoyAxis.LeftY), Input.GetJoyAxis(d, JoyAxis.RightX), Input.GetJoyAxis(d, JoyAxis.RightY),
            Input.GetJoyAxis(d, JoyAxis.TriggerLeft), Input.GetJoyAxis(d, JoyAxis.TriggerRight), bits);
        // the one place every consumer (driving, camera, free look, menus, network input) reads a game pad's sticks and triggers
        return filtered ? Zones.Apply(pad) : pad;
    }

    public IReadOnlyList<PadRaw> Read() => [.. Devices().Select(d => Raw(d))];
}
