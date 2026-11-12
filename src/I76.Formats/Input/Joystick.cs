namespace I76.Formats.Input;

/// <summary>
/// A pad as the platform reports it, before the 1997 stick is made of it: the six standard axes (sticks −1..+1, down and
/// right positive; triggers 0..1) and the buttons as a bit set by the platform's standard-button number
/// (<see cref="PadButton"/>, Godot's JoyButton order).
/// </summary>
public readonly record struct PadRaw(float LeftX, float LeftY, float RightX, float RightY, float TriggerLeft, float TriggerRight, ulong Buttons,
    float[]? Axes = null, bool Generic = false, bool HatIsButtons = false)
{
    public bool Has(int button) => (Buttons >> button & 1) != 0;

    /// <summary>
    /// A device the platform has no standard gamepad layout for (a flight stick, a wheel, a throttle quadrant): <see cref="Axes"/> are its
    /// axes as the platform numbers them (−1..+1, <see cref="GenericStick.MaxAxes"/> at most), <see cref="Buttons"/> its buttons by raw
    /// number, LeftX and LeftY are axes 0 and 1 (the stick) and the rest of the standard fields are 0. A hat arrives as raw buttons 11..14.
    /// </summary>
    public static PadRaw OfGeneric(float[] axes, ulong buttons, bool hatIsButtons = false)
    {
        float A(int i) => i < axes.Length ? axes[i] : 0;
        return new PadRaw(A(0), A(1), 0, 0, 0, 0, buttons, axes, true, hatIsButtons);
    }
}

/// <summary>What the port does with a device that has no standard layout (docs/joystick.md).</summary>
public static class GenericStick
{
    /// <summary>The most raw axes read (Godot's JoyAxis count).</summary>
    public const int MaxAxes = 10;
    /// <summary>The raw button numbers read: 0..10 are Button1..11, 11..14 the hat (Godot's D-pad numbers), 15..35 are Button12...</summary>
    public const int MaxButtons = 36;
    public const int HatFirst = 11, HatLast = 14;
    /// <summary>The dead zone a generic stick starts with, and the steps the screen offers.</summary>
    public const float DefaultDeadZone = 0.10f;
    public static readonly float[] DeadZones = [0f, 0.05f, 0.10f, 0.15f, 0.20f, 0.30f];

    /// <summary>
    /// The button slot (Button1 is 0) a raw button number is, or −1 for a hat direction or a number out of range. Godot cannot say whether raw
    /// 11..14 is the hat or a stick's buttons 12..15 (SDL hat motion and SDL button 11..14 both arrive as the same D-pad button events), so the
    /// player says: with <paramref name="hatIsButtons"/> those four are Button12..15 and the slot is the raw number (Button N is physical button N).
    /// </summary>
    public static int ButtonSlot(int raw, bool hatIsButtons) => hatIsButtons ? (raw >= 0 && raw < 32 ? raw : -1) : ButtonSlot(raw);

    public static int ButtonSlot(int raw) => raw < 0 || raw >= MaxButtons || raw is >= HatFirst and <= HatLast ? -1 : raw < HatFirst ? raw : raw - 4;

    /// <summary>The hat direction (0 up, 1 right, 2 down, 3 left) of a raw button number, or −1.</summary>
    public static int HatDirection(int raw) => raw switch { 11 => 0, 14 => 1, 12 => 2, 13 => 3, _ => -1 };

    /// <summary>An axis with the dead zone cut out and the rest stretched back to full: |v| under it is 0, full deflection stays 1.</summary>
    public static float Dead(float v, float zone)
    {
        float a = Math.Abs(v);
        if (zone <= 0) return Math.Clamp(v, -1f, 1f);
        if (a <= zone) return 0;
        return MathF.Sign(v) * MathF.Min(1f, (a - zone) / (1f - zone));
    }

    /// <summary>The raw axis number (1-based) an input.map analog name is on a generic device: "Left/Right" is 1, "Down/Up" 2, "Axis3" 3; 0 for any other name.</summary>
    public static int AxisNumber(string channel)
    {
        if (channel.Equals("Left/Right", StringComparison.OrdinalIgnoreCase)) return 1;
        if (channel.Equals("Down/Up", StringComparison.OrdinalIgnoreCase)) return 2;
        return channel.StartsWith("Axis", StringComparison.OrdinalIgnoreCase) && int.TryParse(channel.AsSpan(4), out var n) && n >= 1 && n <= MaxAxes ? n : 0;
    }

    /// <summary>The name input.map uses for raw axis n: the original's own for the stick's two (so a stock JOYSTICK.MAP line is the same), "AxisN" for the rest.</summary>
    public static string AxisName(int n) => n == 1 ? "Left/Right" : n == 2 ? "Down/Up" : $"Axis{n}";

    /// <summary>The next dead zone in the screen's steps (wrapping).</summary>
    public static float NextDeadZone(float zone)
    {
        int i = Array.FindIndex(DeadZones, z => z > zone + 0.001f);
        return i < 0 ? DeadZones[0] : DeadZones[i];
    }
}

/// <summary>The standard buttons' numbers (Godot's JoyButton).</summary>
public static class PadButton
{
    public const int A = 0, B = 1, X = 2, Y = 3, Back = 4, Guide = 5, Start = 6, LeftStick = 7, RightStick = 8,
        LeftShoulder = 9, RightShoulder = 10, DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14,
        Misc1 = 15, Paddle1 = 16, Paddle2 = 17, Paddle3 = 18, Paddle4 = 19, Touchpad = 20;
}

/// <summary>Where the pads come from: connected pads in order (joystick1 is the first). The game's is Godot's Input; tests use their own.</summary>
public interface IPadSource
{
    IReadOnlyList<PadRaw> Read();
}

/// <summary>
/// What winmm would have shown for a pad: the channels by the names input.map
/// has for them, axes −1..+1 (Y and Z as winmm signs them), buttons as bits, the hat as one of four directions.
/// </summary>
public sealed class VirtualStick
{
    /// <summary>The analog channels in the original's order (Y before X; HeadRoll is a tracker's Z, never present).</summary>
    public static readonly string[] AnalogNames =
        ["Down/Up", "Left/Right", "Throttle", "Rudder", "5thAxis", "6thAxis", .. Enumerable.Range(1, GenericStick.MaxAxes).Select(n => $"Axis{n}")];
    /// <summary>The channels a pad has (no sixth axis); "Axis1".. are the device's raw axes, after them.</summary>
    public const int PresentAnalog = 5;
    /// <summary>Where "Axis1" is in <see cref="AnalogNames"/> and <see cref="Axes"/>.</summary>
    public const int RawFirst = 6;
    public static readonly string[] HatNames = ["HatUp", "HatRight", "HatDown", "HatLeft"];

    /// <summary>Button1.. in DirectInput/winmm order for an XInput pad; the rest continue as Button11+.</summary>
    public static readonly int[] ButtonOrder =
    [
        PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LeftShoulder, PadButton.RightShoulder, PadButton.Back, PadButton.Start,
        PadButton.LeftStick, PadButton.RightStick, PadButton.Guide, PadButton.Misc1, PadButton.Paddle1, PadButton.Paddle2, PadButton.Paddle3,
        PadButton.Paddle4, PadButton.Touchpad,
    ];
    /// <summary>The buttons a stick has: a pad's <see cref="ButtonOrder"/> and a generic device's raw buttons both fit.</summary>
    public const int ButtonCount = 32;

    public readonly float[] Axes = new float[RawFirst + GenericStick.MaxAxes];
    /// <summary>Bit k is Button(k+1).</summary>
    public uint Buttons;
    /// <summary>Bit 0 up, 1 right, 2 down, 3 left.</summary>
    public byte Hat;

    /// <summary>The pad as the stick it stands for. Exactly one D-pad direction is a hat direction; none, or a diagonal, is none.</summary>
    public static VirtualStick From(in PadRaw p)
    {
        var s = new VirtualStick();
        if (p.Axes is { } raw) for (int i = 0; i < raw.Length && i < GenericStick.MaxAxes; i++) s.Axes[RawFirst + i] = raw[i];
        if (p.Generic)
        {
            s.Axes[0] = p.LeftY;
            s.Axes[1] = p.LeftX;
            for (int r = 0; r < GenericStick.MaxButtons; r++)
                if (p.Has(r) && GenericStick.ButtonSlot(r, p.HatIsButtons) is >= 0 and var slot) s.Buttons |= 1u << slot;
            if (!p.HatIsButtons) s.Hat = HatOf(p.Has(11), p.Has(14), p.Has(12), p.Has(13));
            return s;
        }
        s.Axes[0] = p.LeftY;
        s.Axes[1] = p.LeftX;
        s.Axes[2] = p.TriggerLeft - p.TriggerRight;   // RT is -1, which "- Throttle" turns into accelerate
        s.Axes[3] = p.RightX;
        s.Axes[4] = p.RightY;
        for (int k = 0; k < ButtonOrder.Length; k++) if (p.Has(ButtonOrder[k])) s.Buttons |= 1u << k;
        s.Hat = HatOf(p.Has(PadButton.DpadUp), p.Has(PadButton.DpadRight), p.Has(PadButton.DpadDown), p.Has(PadButton.DpadLeft));
        return s;
    }

    public static byte HatOf(bool up, bool right, bool down, bool left)
    {
        int n = (up ? 1 : 0) + (right ? 1 : 0) + (down ? 1 : 0) + (left ? 1 : 0);
        return n != 1 ? (byte)0 : (byte)((up ? 1 : 0) | (right ? 2 : 0) | (down ? 4 : 0) | (left ? 8 : 0));
    }

    /// <summary>
    /// The channel a discrete input.map name is, as the original reads it: 0..ButtonCount-1 a button, then the four hat
    /// directions; a token that starts with a digit is a raw index. −1 for a name the stick does not have.
    /// </summary>
    public static int DiscreteId(string token)
    {
        if (token.Length > 0 && char.IsAsciiDigit(token[0]))
            return int.TryParse(new string(token.TakeWhile(char.IsAsciiDigit).ToArray()), out var i) && i < ButtonCount + 4 ? i : -1;
        if (token.StartsWith("Button", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.AsSpan(6), out var b) && b >= 1 && b <= ButtonCount) return b - 1;
        for (int h = 0; h < 4; h++) if (HatNames[h].Equals(token, StringComparison.OrdinalIgnoreCase)) return ButtonCount + h;
        return -1;
    }

    /// <summary>The channel an analog input.map name is, or −1: only the channels a pad has.</summary>
    public static int AnalogId(string token)
    {
        // names first: "5thAxis" starts with a digit too
        for (int a = 0; a < AnalogNames.Length; a++) if (a != PresentAnalog && AnalogNames[a].Equals(token, StringComparison.OrdinalIgnoreCase)) return a;
        if (token.Length > 0 && char.IsAsciiDigit(token[0]))
            return int.TryParse(new string(token.TakeWhile(char.IsAsciiDigit).ToArray()), out var i) && i < PresentAnalog ? i : -1;
        return -1;
    }

    /// <summary>The discrete channel a standard button is (a button's number, or a hat direction when the D-pad shows exactly one), or −1.</summary>
    public static int DiscreteIdOfPadButton(int padButton, in PadRaw now)
    {
        if (now.Generic)
        {
            if (GenericStick.ButtonSlot(padButton, now.HatIsButtons) is >= 0 and var slot) return slot;
            int d = now.HatIsButtons ? -1 : GenericStick.HatDirection(padButton);
            byte h = HatOf(now.Has(11), now.Has(14), now.Has(12), now.Has(13));
            return d >= 0 && (h >> d & 1) != 0 ? ButtonCount + d : -1;
        }
        int k = Array.IndexOf(ButtonOrder, padButton);
        if (k >= 0) return k;
        byte hat = HatOf(now.Has(PadButton.DpadUp), now.Has(PadButton.DpadRight), now.Has(PadButton.DpadDown), now.Has(PadButton.DpadLeft));
        int dir = padButton switch { PadButton.DpadUp => 0, PadButton.DpadRight => 1, PadButton.DpadDown => 2, PadButton.DpadLeft => 3, _ => -1 };
        return dir >= 0 && (hat >> dir & 1) != 0 ? ButtonCount + dir : -1;
    }

    /// <summary>The input.map name of a discrete channel id ("Button3", "HatUp").</summary>
    public static string DiscreteName(int id) => id < ButtonCount ? $"Button{id + 1}" : HatNames[id - ButtonCount];

    /// <summary>The names the Control Configuration list offers: every button, then the hat.</summary>
    public static IEnumerable<string> DiscreteNames => Enumerable.Range(0, ButtonCount + 4).Select(DiscreteName);
    /// <summary>The analog names the list offers (only for the rows that take an axis).</summary>
    public static IEnumerable<string> AnalogChoices => AnalogNames.Take(PresentAnalog);
}

/// <summary>
/// The joystick devices as the game sees them: each frame every
/// device's analog channels are the signed square of the stick, ±65536 at full deflection (no dead zone); buttons
/// and the hat are bits; a window without focus reads centred with every button up.
/// </summary>
public sealed class JoystickBank(IPadSource source)
{
    private int[][] _analog = [];
    private uint[] _buttons = [];
    private byte[] _hat = [];

    private const int RawEnd = VirtualStick.RawFirst + GenericStick.MaxAxes;
    private bool[] _generic = [];

    public int Count => _analog.Length;

    /// <summary>The device has no standard layout (it is a flight stick, a wheel...: its buttons and axes are the raw ones).</summary>
    public bool IsGeneric(int device) => device >= 0 && device < _generic.Length && _generic[device];

    /// <summary>The response of an axis at n (−1..+1): sign(n)·n²·65536, truncated, kept inside ±65536.</summary>
    public static int Response(float n)
    {
        double d = Math.Clamp((double)n, -1.0, 1.0);
        double v = d * d * 65536.0;
        if (d < 0) v = -v;
        return (int)Math.Clamp(Math.Truncate(v), -65536.0, 65536.0);
    }

    /// <summary>The frame's reading of every pad.</summary>
    public void Poll(bool focused)
    {
        var pads = source.Read();
        if (_analog.Length != pads.Count)
        {
            _analog = new int[pads.Count][];
            for (int i = 0; i < pads.Count; i++) _analog[i] = new int[RawEnd];
            _buttons = new uint[pads.Count];
            _hat = new byte[pads.Count];
        }
        _generic = [.. pads.Select(p => p.Generic)];
        for (int i = 0; i < pads.Count; i++)
        {
            Array.Clear(_analog[i]);
            _buttons[i] = 0;
            _hat[i] = 0;
            if (!focused) continue;
            var s = VirtualStick.From(pads[i]);
            for (int a = 0; a < RawEnd; a++) _analog[i][a] = Response(s.Axes[a]);
            _buttons[i] = s.Buttons;
            _hat[i] = s.Hat;
        }
    }

    /// <summary>The device's analog channel (by input.map name or raw index), or 0 when there is no such device or channel.</summary>
    public int Analog(int device, string channel) =>
        device >= 0 && device < Count && VirtualStick.AnalogId(channel) is >= 0 and var a ? _analog[device][a] : 0;

    public bool HasAnalog(int device, string channel) => device >= 0 && device < Count && VirtualStick.AnalogId(channel) >= 0;
    public bool HasDiscrete(int device, string channel) => device >= 0 && device < Count && VirtualStick.DiscreteId(channel) >= 0;

    /// <summary>The discrete channel is on now.</summary>
    public bool Held(int device, string channel)
    {
        if (device < 0 || device >= Count || VirtualStick.DiscreteId(channel) is not (>= 0 and var id)) return false;
        return id < VirtualStick.ButtonCount ? (_buttons[device] >> id & 1) != 0 : (_hat[device] >> (id - VirtualStick.ButtonCount) & 1) != 0;
    }

    /// <summary>The device a term names ("joystick1" is 0), or −1 for any other device.</summary>
    public static int DeviceOf(string device) =>
        device.StartsWith("joystick", StringComparison.OrdinalIgnoreCase) && int.TryParse(device.AsSpan(8), out var n) && n >= 1 ? n - 1 : -1;
}

/// <summary>
/// The analog controls of the axis rows the port reads (the input poll):
/// throttle and steer, and the chase camera's track_yaw_delta and track_pitch_delta. Each axis block feeds its row's sink with
/// direction · channel, the lines after the first gating it as a chord. A plain row is written only when the value differs from
/// the control's last and keeps its position between frames; a "_delta" row is written every frame (and reads 0 once its
/// device is gone, so a camera never keeps turning on a pad that was pulled out).
/// </summary>
public sealed class AxisControls
{
    /// <summary>The rows, in the sink table's order: throttle, steer, then the chase camera's two.</summary>
    public static readonly string[] Rows = ["throttle", "steer", "track_yaw_delta", "track_pitch_delta"];
    public const int ThrottleRow = 0, SteerRow = 1, YawRow = 2, PitchRow = 3;

    private sealed class Control(int device, string channel, int direction, InputTerm[] chord, int row)
    {
        public readonly int Device = device;
        public readonly string Channel = channel;
        public readonly int Direction = direction;
        public readonly InputTerm[] Chord = chord;
        public readonly int Row = row;
        public int Last;
    }

    private readonly List<Control> _controls = [];
    private readonly int[] _pos = new int[Rows.Length];

    /// <summary>The throttle row's position, ±65536 (+ accelerates).</summary>
    public int Throttle => _pos[ThrottleRow];
    /// <summary>The steer row's position, ±65536 (+ is left).</summary>
    public int Steer => _pos[SteerRow];
    /// <summary>The chase camera's yaw row, ±65536 (+ is track_yaw_plus's way).</summary>
    public int Yaw => _pos[YawRow];
    /// <summary>The chase camera's pitch row, ±65536 (+ is track_pitch_plus's way: the camera up, looking down).</summary>
    public int Pitch => _pos[PitchRow];

    /// <summary>The row an axis command is, or −1.</summary>
    public static int RowOf(string command) => Array.FindIndex(Rows, r => r.Equals(command, StringComparison.OrdinalIgnoreCase));

    public AxisControls(IEnumerable<InputBlock> blocks)
    {
        foreach (var b in blocks)
        {
            int row = RowOf(b.Command);
            if (row < 0) continue;
            if (b.Terms.Length == 0 || JoystickBank.DeviceOf(b.Terms[0].Device) < 0) continue;
            var t = b.Terms[0];
            _controls.Add(new Control(JoystickBank.DeviceOf(t.Device), t.Channel, t.Plus ? 1 : -1, b.Terms[1..], row));
        }
    }

    private static bool Drives(Control c) => c.Row is ThrottleRow or SteerRow;

    /// <summary>Whether a live stick channel feeds the throttle or steer row: the car's stick is read at all.</summary>
    public bool Active(JoystickBank bank) => _controls.Any(c => Drives(c) && bank.HasAnalog(c.Device, c.Channel));

    /// <summary>Whether the stick feeds this row.</summary>
    public bool Active(JoystickBank bank, bool steer) => _controls.Any(c => c.Row == (steer ? SteerRow : ThrottleRow) && bank.HasAnalog(c.Device, c.Channel));

    /// <summary>One frame: each live control whose chord holds writes its sink (a plain row only if its value changed). <paramref name="down"/> says whether a non-stick term's input is held.</summary>
    public void Update(JoystickBank bank, Func<InputTerm, bool> down)
    {
        _pos[YawRow] = _pos[PitchRow] = 0;
        foreach (var c in _controls)
        {
            if (!bank.HasAnalog(c.Device, c.Channel)) continue;          // a dead device or channel: the block is not there
            if (!InputChord.Holds(c.Chord, bank, down)) continue;
            int v = c.Direction * bank.Analog(c.Device, c.Channel);
            bool delta = !Drives(c);
            if (v == c.Last && !delta) continue;
            // several controls on one delta row: the one pushed furthest this frame
            if (!delta || Math.Abs(v) >= Math.Abs(_pos[c.Row])) _pos[c.Row] = v;
            c.Last = v;
        }
    }

    /// <summary>A sink position on the wire's scale (±32767).</summary>
    public static short Wire(int pos) => (short)Math.Clamp((int)Math.Round(pos / 65536.0 * 32767.0), -32767, 32767);

    /// <summary>A sink position back as the stick's deflection, −1..+1 (the response squares it).</summary>
    public static float Deflection(int pos) => MathF.Sign(pos) * MathF.Sqrt(Math.Abs(pos) / 65536f);
}

/// <summary>
/// How low the port's free head (the mouse, the pad's look stick, the head tracker) may look at each heading in the 3D cockpit, so
/// the cut-off ends of the driver's arms never come into view (#165). The arms (the .45s, raised or at rest) and the steering
/// wheel with its hands are pictures on flat quads, cut off at their lower edges; the original's glances never look low enough to
/// see those edges. The table was measured from every car's interior in both stock games: at each degree of yaw, the highest pitch at
/// which the lower edges of the two arm quads and the wheel's quad still lie under the view's lower border, at the original's height
/// of view and in any window from 4:3 to 32:9, less 1° for the head's sway, then kept under that at every degree between its 5° steps.
/// A taller view (a wide Field of View in a narrow window) looks that much less low, a shorter one that much lower.
/// </summary>
public static class CockpitReach
{
    /// <summary>The main view's height in degrees (the game layer keeps it current: the Field of View setting in this window).</summary>
    public static float ViewHeight { get; set; } = OriginalHeight;
    public const float OriginalHeight = 73.74f;

    /// <summary>The lowest pitch (game degrees, down positive) every 5° of yaw from −160° (left) to 160° (right), at the original's height of view.</summary>
    private static readonly float[] Lowest =
    [
        10.1f, 7.5f, 5.3f, 3.4f, 1.9f, 0.7f, -0.2f, -0.9f, -1.4f, -1.6f, -1.6f, -1.4f, -1.0f, -0.4f, -0.8f, -2.0f,
        -2.9f, -3.6f, -4.1f, -4.4f, -4.4f, -4.4f, -4.1f, -3.6f, -2.9f, -2.0f, -0.8f, 0.4f, 1.6f, 3.1f, 4.8f, 6.9f,
        9.4f,
        6.9f, 4.8f, 3.0f, 1.5f, 0.4f, -0.6f, -1.3f, -1.8f, -2.1f, -2.2f, -2.1f, -1.8f, -1.3f, -0.6f, 0.4f, 1.6f,
        3.1f, 4.4f, 3.8f, 2.5f, 1.5f, 0.6f, 0.1f, -0.3f, -0.4f, -0.4f, -0.1f, 0.4f, 1.1f, 2.1f, 3.4f, 4.9f,
    ];

    /// <summary>The lowest the head may look (game degrees, down positive) at <paramref name="yaw"/> (degrees, right positive) with a view <paramref name="viewHeight"/> degrees high.</summary>
    public static float MaxPitch(float yaw, float viewHeight)
    {
        float i = (Math.Clamp(yaw, -160f, 160f) + 160f) / 5f;
        int a = Math.Min((int)i, Lowest.Length - 2);
        return Lowest[a] + (Lowest[a + 1] - Lowest[a]) * (i - a) + (OriginalHeight - viewHeight) / 2;
    }

    /// <summary>The same at the view's height in force (<see cref="ViewHeight"/>).</summary>
    public static float MaxPitch(float yaw) => MaxPitch(yaw, ViewHeight);
}

/// <summary>
/// The port's in-car free look on a pad (not the original's, which has only the held glances): the look
/// stick turns the head in proportion to its deflection, and eases back when let go. Sideways the free look reaches its full yaw at
/// <see cref="FreeEdge"/>; past <see cref="GunEnter"/> a wide detent holds the glance-left or glance-right pose (the glance key held:
/// the arm and the .45 out of the window), and pushed all the way (<see cref="RearEnter"/>) it goes on round to the look rear,
/// over the shoulder on that side. Pulled hard down (<see cref="DashEnter"/>) it is glance-down, the look at the dash and the wheel. Each pose holds until
/// the stick falls back under its own leave threshold.
/// </summary>
public sealed class FreeLook
{
    public const float FreeEdge = 0.5f, GunEnter = 0.55f, GunLeave = 0.45f, RearEnter = 0.95f, RearLeave = 0.88f, DashEnter = 0.9f, DashLeave = 0.75f;
    /// <summary>Under this the stick is centred (the free look's own dead zone: a resting stick's drift must not turn the head).</summary>
    public const float DeadZone = 0.12f;
    /// <summary>The free head's reach in degrees: yaw at <see cref="FreeEdge"/>, pitch up (negative, the game's sign) and down from the forward −13°.</summary>
    public const float MaxYaw = 80, Rest = -13, MaxUp = 22, MaxDown = 25;

    public enum Pose { Free, Left, Right, Rear, Dash }

    public Pose Current { get; private set; }
    /// <summary>The look rear came from the left: over the left shoulder (the original's own rear glance is over the right).</summary>
    public bool RearLeft { get; private set; }

    /// <summary>One frame of the stick (x right, y down, −1..+1): the pose it holds now.</summary>
    public Pose Update(float x, float y)
    {
        float ax = Math.Abs(x);
        Pose side = x < 0 ? Pose.Left : Pose.Right;
        Current = Current switch
        {
            Pose.Left or Pose.Right when Current == side && ax >= RearEnter => Pose.Rear,
            Pose.Left or Pose.Right when Current != side || ax < GunLeave => Pose.Free,
            Pose.Rear when ax < RearLeave => ax >= GunLeave ? side : Pose.Free,
            Pose.Dash when y < DashLeave => Pose.Free,
            var p => p,
        };
        var was = Current;
        if (Current == Pose.Free)
        {
            if (ax >= Math.Abs(y)) Current = ax >= RearEnter ? Pose.Rear : ax >= GunEnter ? side : Pose.Free;
            else if (y >= DashEnter) Current = Pose.Dash;
        }
        if (Current == Pose.Rear && was != Pose.Rear || Current == Pose.Rear && ax >= RearEnter) RearLeft = x < 0;
        return Current;
    }

    public void Reset() => Current = Pose.Free;

    /// <summary>The dead zone off, then 0..1 at <paramref name="top"/>.</summary>
    private static float Shape(float v, float top)
    {
        float a = Math.Abs(v);
        return a <= DeadZone ? 0 : MathF.Sign(v) * MathF.Min(1, (a - DeadZone) / (top - DeadZone));
    }

    /// <summary>The free head's target (yaw right-positive, pitch the game's: negative looks up), in degrees; null while the stick is centred. Never lower than <see cref="CockpitReach"/> allows at that yaw.</summary>
    public static (float Yaw, float Pitch)? Head(float x, float y)
    {
        float sx = Shape(x, FreeEdge), sy = Shape(y, y > 0 ? DashEnter : 1);
        if (sx == 0 && sy == 0) return null;
        return (sx * MaxYaw, Math.Min(Rest + sy * (sy < 0 ? MaxUp : MaxDown), CockpitReach.MaxPitch(sx * MaxYaw)));
    }
}

/// <summary>
/// A screen's pad navigation (the port's controller screen): the D-pad steps, A opens, B goes back, Start is done, each on the press
/// (the edge). The sticks, the triggers and every other button never navigate: they only show on the drawing. Taken apart from
/// the drawing so the rule can be tested: no pad input both navigates and does something else.
/// </summary>
public sealed class PadMenu
{
    [Flags]
    public enum Act { None = 0, Prev = 1, Next = 2, Open = 4, Back = 8, Done = 16, Up = 32, Down = 64, Left = 128, Right = 256 }

    private PadRaw _prev;
    private bool _settle;

    private static Act Held(in PadRaw p) =>
        (p.Has(PadButton.DpadUp) ? Act.Up | Act.Prev : 0) | (p.Has(PadButton.DpadLeft) ? Act.Left | Act.Prev : 0)
        | (p.Has(PadButton.DpadDown) ? Act.Down | Act.Next : 0) | (p.Has(PadButton.DpadRight) ? Act.Right | Act.Next : 0)
        | (p.Has(PadButton.A) ? Act.Open : 0) | (p.Has(PadButton.B) ? Act.Back : 0) | (p.Has(PadButton.Start) ? Act.Done : 0);

    /// <summary>What was pressed this frame. After <see cref="Settle"/>, nothing until every button is up again.</summary>
    public Act Update(in PadRaw now)
    {
        var held = Held(now);
        var edge = held & ~Held(_prev);
        _prev = now;
        if (_settle) { if (held == Act.None) _settle = false; return Act.None; }
        return edge;
    }

    /// <summary>Something else had the pad (a list picked from, a pop-up): the buttons still down do nothing when it comes back.</summary>
    public void Settle() => _settle = true;

}

/// <summary>Chord evaluation for a block's terms: "+" held, "-" not held; joystick terms on a dead device or channel are dropped, and a block whose last term is dead is gone.</summary>
public static class InputChord
{
    public static bool Holds(IReadOnlyList<InputTerm> terms, JoystickBank bank, Func<InputTerm, bool> down)
    {
        if (terms.Count == 0) return true;
        if (IsDead(terms[^1], bank)) return false;
        foreach (var t in terms)
        {
            if (IsDead(t, bank)) continue;
            if (Held(t, bank, down) != t.Plus) return false;
        }
        return true;
    }

    public static bool IsDead(InputTerm t, JoystickBank bank) =>
        JoystickBank.DeviceOf(t.Device) is >= 0 and var d && !bank.HasDiscrete(d, t.Channel);

    public static bool Held(InputTerm t, JoystickBank bank, Func<InputTerm, bool> down) =>
        JoystickBank.DeviceOf(t.Device) is >= 0 and var d ? bank.Held(d, t.Channel) : down(t);
}
