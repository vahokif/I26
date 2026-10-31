using System.Runtime.InteropServices;
using Godot;
using I76.Formats.Input;

namespace I76.Game;

/// <summary>
/// The operating system's own view of a stick that Godot reads raw, for the one thing Godot cannot say: a hat and a real button 12 to 15 reach
/// the game as the same events (issue 22; <see cref="HatSplit"/>). Windows is asked through winmm, Linux through the joydev device; anywhere
/// else, or when the stick is not found there, <see cref="Read"/> is null and the Joystick screen's switch decides, as before.
/// </summary>
public static class NativeSticks
{
    private abstract class Source
    {
        public abstract NativeStick? Poll();
    }

    private static readonly Dictionary<int, Source?> Sources = [];
    private static readonly Dictionary<int, (ulong Frame, NativeStick Now, NativeStick Before)> Last = [];

    /// <summary>The OS's state of Godot device <paramref name="device"/> as of this frame, with the state at the frame before, or null when it cannot be asked.</summary>
    public static (NativeStick Now, NativeStick Before)? Read(int device)
    {
        if (System.Environment.GetEnvironmentVariable("I76_NO_NATIVE_STICK") == "1") return null;
        ulong f = Engine.GetProcessFrames();
        if (Last.TryGetValue(device, out var l) && l.Frame == f) return (l.Now, l.Before);
        if (!Sources.TryGetValue(device, out var src))
        {
            try { src = Open(device); }
            catch (Exception e) { GD.Print($"native stick: {e.Message}"); src = null; }
            Sources[device] = src;
        }
        if (src is null) return null;
        NativeStick? now;
        try { now = src.Poll(); } catch { Sources[device] = null; return null; }
        if (now is null) return null;
        var before = Last.TryGetValue(device, out l) ? l.Now : default;
        Last[device] = (f, now.Value, before);
        return (now.Value, before);
    }

    /// <summary>Forgets the sticks (a pad was plugged or unplugged: the numbering of the OS's devices may have moved).</summary>
    public static void Reset() { Sources.Clear(); Last.Clear(); }

    private static Source? Open(int device)
    {
        var info = Input.GetJoyInfo(device);
        uint vid = info.TryGetValue("vendor_id", out var v) && uint.TryParse(v.AsString(), out var vv) ? vv : 0;
        uint pid = info.TryGetValue("product_id", out var p) && uint.TryParse(p.AsString(), out var pp) ? pp : 0;
        // the nth generic device of this id is the nth the OS lists with that id
        int nth = 0;
        foreach (var d in Input.GetConnectedJoypads())
        {
            if ((int)d == device) break;
            if (Input.IsJoyKnown((int)d)) continue;
            var i2 = Input.GetJoyInfo((int)d);
            if (i2.TryGetValue("vendor_id", out var v2) && i2.TryGetValue("product_id", out var p2) && v2.AsString() == v.AsString() && p2.AsString() == p.AsString()) nth++;
        }
        if (OperatingSystem.IsWindows()) return Winmm.Open(vid, pid, nth);
        if (OperatingSystem.IsLinux()) return Joydev.Open(vid, pid, nth);
        return null;
    }

    // ---- Windows: winmm joyGetPosEx ---------------------------------------------------------------

    private sealed class Winmm : Source
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JoyCaps
        {
            public ushort Mid, Pid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, NumButtons, PeriodMin, PeriodMax, Rmin, Rmax, Umin, Umax, Vmin, Vmax, Caps, MaxAxes, NumAxes, MaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxD;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
        }

        [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")] private static extern uint NumDevs();
        [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)] private static extern uint DevCaps(UIntPtr id, ref JoyCaps caps, uint size);
        [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")] private static extern uint PosEx(uint id, ref JoyInfoEx info);

        private readonly uint _id;
        private Winmm(uint id) { _id = id; }

        public static Source? Open(uint vid, uint pid, int nth)
        {
            uint n = Math.Min(NumDevs(), 16);
            var found = new List<uint>();
            for (uint id = 0; id < n; id++)
            {
                var caps = new JoyCaps();
                if (DevCaps((UIntPtr)id, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0) continue;
                var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = 0xFF };
                if (PosEx(id, ref info) != 0) continue;       // not plugged in
                if (vid != 0 && (caps.Mid != vid || caps.Pid != pid)) continue;
                found.Add(id);
            }
            return nth < found.Count ? new Winmm(found[nth]) : null;
        }

        public override NativeStick? Poll()
        {
            var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = 0xFF };
            if (PosEx(_id, ref info) != 0) return null;
            return new NativeStick(HatSplit.HatOfPov(info.Pov), info.Buttons);
        }
    }

    // ---- Linux: /dev/input/jsN --------------------------------------------------------------------

    private sealed class Joydev : Source
    {
        [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr read(int fd, byte[] buf, UIntPtr count);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint req, byte[] arg);

        private const int ONonblock = 0x800;
        private const uint GetAxes = 0x80016a11, GetAxMap = 0x80406a32;      // JSIOCGAXES, JSIOCGAXMAP (u8[64])
        private readonly int _fd;
        private readonly int _hatX = -1, _hatY = -1;
        private readonly short[] _axes = new short[64];
        private uint _buttons;
        private readonly byte[] _buf = new byte[8 * 64];

        private Joydev(int fd)
        {
            _fd = fd;
            var n = new byte[1];
            var map = new byte[64];
            if (ioctl(fd, GetAxes, n) >= 0 && ioctl(fd, GetAxMap, map) >= 0)
                for (int a = 0; a < n[0] && a < 64; a++) { if (map[a] == 0x10) _hatX = a; else if (map[a] == 0x11) _hatY = a; }   // ABS_HAT0X, ABS_HAT0Y
        }

        public static Source? Open(uint vid, uint pid, int nth)
        {
            int seen = 0;
            for (int i = 0; i < 32; i++)
            {
                string dir = $"/sys/class/input/js{i}/device/id/";
                if (!System.IO.Directory.Exists(dir)) continue;
                if (vid != 0 && (Hex(dir + "vendor") != vid || Hex(dir + "product") != pid)) continue;
                if (seen++ < nth) continue;
                int fd = open($"/dev/input/js{i}", ONonblock);
                return fd < 0 ? null : new Joydev(fd);
            }
            return null;
        }

        private static uint Hex(string path)
        {
            try { return Convert.ToUInt32(System.IO.File.ReadAllText(path).Trim(), 16); } catch { return uint.MaxValue; }
        }

        public override NativeStick? Poll()
        {
            for (;;)
            {
                long got = (long)read(_fd, _buf, (UIntPtr)_buf.Length);
                if (got <= 0) break;
                for (int o = 0; o + 8 <= got; o += 8)
                {
                    short value = BitConverter.ToInt16(_buf, o + 4);
                    int type = _buf[o + 6] & 0x7f, number = _buf[o + 7];      // the init flag (0x80) says the same thing
                    if (type == 1 && number < 32) _buttons = value != 0 ? _buttons | 1u << number : _buttons & ~(1u << number);
                    else if (type == 2 && number < 64) _axes[number] = value;
                }
            }
            if (_hatX < 0 && _hatY < 0) return new NativeStick(0, _buttons);
            return new NativeStick(HatSplit.HatOfAxes(_hatX >= 0 ? _axes[_hatX] : 0, _hatY >= 0 ? _axes[_hatY] : 0), _buttons);
        }
    }
}
