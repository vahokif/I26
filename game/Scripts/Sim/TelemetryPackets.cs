using System.Buffers.Binary;
using System.Numerics;
using I76.Sim.Math;

namespace I76.Game.Sim;

/// <summary>The telemetry feed's wire formats (docs/dev-hooks.md, "Telemetry out").</summary>
public enum TelemetryFormat { I26, OutSim, OutGauge }

/// <summary>What one packet of the telemetry feed says about the player's car, at the frame drawn. Game axes and units.</summary>
public struct TelemetryFrame
{
    public uint Seq;
    public uint Tick;
    public float Alpha;
    public float Time;
    public TelemetryStatus Status;
    /// <summary>0 reverse, 1 to 4 forward.</summary>
    public byte Gear;
    /// <summary>Bit per wheel, FR FL RR RL: laying a skid mark / a blown tyre.</summary>
    public byte SkidMask, BlownMask;
    public Double3 Pos;
    public Rows Rot;
    public Vector3 Vel, Accel;
    /// <summary>Pitch (nose up), yaw (turning right) and roll (right side down) rates, rad/s.</summary>
    public Vector3 Rates;
    public float Speed, Rpm, Throttle, Brake, Steer, Health;
    /// <summary>Four characters for OutGauge's car field.</summary>
    public string Car;
}

[Flags]
public enum TelemetryStatus : uint
{
    None = 0,
    Present = 1 << 0,
    EngineOn = 1 << 1,
    Airborne = 1 << 2,
    Sliding = 1 << 3,
    Dead = 1 << 4,
    Cutscene = 1 << 5,
    Paused = 1 << 6,
    Network = 1 << 7,
    Lights = 1 << 8,
    Handbrake = 1 << 9,
}

public enum TelemetryEventKind : byte { Shot = 1, Hit = 2, Collision = 3, Explosion = 4 }

/// <summary>A thing that happened during a tick, sent with the next packet.</summary>
public record struct TelemetryEvent(TelemetryEventKind Kind, byte A, ushort B, uint Tick, Vector4 V);

/// <summary>
/// The packet encoders. All little-endian, no padding. The I26 packet is this port's own (versioned, the frame and the
/// events of the ticks since the last packet); OutSim (64 bytes) and OutGauge (92) are Live for Speed's classic layouts,
/// which motion-rig and dashboard software read, each 4 bytes longer with a non-zero id.
/// </summary>
public static class TelemetryPackets
{
    public const ushort Version = 1;
    public const int FrameSize = 176, EventSize = 24, MaxEvents = 32;
    public const int MaxSize = FrameSize + MaxEvents * EventSize;
    public const int OutSimSize = 64, OutGaugeSize = 92;

    /// <summary>Heading (from +z toward +x: turning right), pitch (nose up) and roll (right side down), radians.</summary>
    public static Vector3 Attitude(Rows r) => new(MathF.Atan2(r.Forward.X, r.Forward.Z),
        MathF.Asin(System.Math.Clamp(r.Forward.Y, -1f, 1f)), MathF.Atan2(-r.Right.Y, r.Up.Y));

    /// <summary>A game vector in Live for Speed's axes: x right, y forward, z up (the game's z is forward, y up).</summary>
    public static Vector3 ToLfs(Vector3 v) => new(v.X, v.Z, v.Y);

    /// <summary>
    /// The body rates from one tick's turn of the car (rows before and after, dt apart): pitch (nose up), yaw (turning right),
    /// roll (right side down), rad/s. Worked in Live for Speed's right-handed axes, where the angular velocity is half the sum of
    /// each axis before crossed with itself after, over dt.
    /// </summary>
    public static Vector3 BodyRates(Rows prev, Rows cur, float dt)
    {
        if (dt <= 0) return default;
        var w = 0.5f / dt * (Vector3.Cross(ToLfs(prev.Right), ToLfs(cur.Right)) + Vector3.Cross(ToLfs(prev.Up), ToLfs(cur.Up))
            + Vector3.Cross(ToLfs(prev.Forward), ToLfs(cur.Forward)));
        return new(Vector3.Dot(w, ToLfs(cur.Right)), -Vector3.Dot(w, ToLfs(cur.Up)), Vector3.Dot(w, ToLfs(cur.Forward)));
    }

    private static void F(Span<byte> b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b[at..], v);
    private static void V(Span<byte> b, int at, Vector3 v) { F(b, at, v.X); F(b, at + 4, v.Y); F(b, at + 8, v.Z); }

    /// <summary>The I26 packet: the frame, then up to <see cref="MaxEvents"/> events. Returns its length.</summary>
    public static int WriteI26(Span<byte> b, in TelemetryFrame f, ReadOnlySpan<TelemetryEvent> events)
    {
        int n = System.Math.Min(events.Length, MaxEvents);
        b[..FrameSize].Clear();
        "I26T"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], FrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], f.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], f.Tick);
        F(b, 16, f.Alpha);
        F(b, 20, f.Time);
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], (uint)f.Status);
        b[28] = f.Gear; b[29] = f.SkidMask; b[30] = f.BlownMask; b[31] = (byte)n;
        BinaryPrimitives.WriteDoubleLittleEndian(b[32..], f.Pos.X);
        BinaryPrimitives.WriteDoubleLittleEndian(b[40..], f.Pos.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(b[48..], f.Pos.Z);
        V(b, 56, f.Rot.Right); V(b, 68, f.Rot.Up); V(b, 80, f.Rot.Forward);
        V(b, 92, Attitude(f.Rot));
        V(b, 104, f.Vel);
        V(b, 116, f.Accel);
        V(b, 128, new Vector3(Vector3.Dot(f.Accel, f.Rot.Right), Vector3.Dot(f.Accel, f.Rot.Up), Vector3.Dot(f.Accel, f.Rot.Forward)));
        V(b, 140, f.Rates);
        F(b, 152, f.Speed); F(b, 156, f.Rpm); F(b, 160, f.Throttle); F(b, 164, f.Brake); F(b, 168, f.Steer); F(b, 172, f.Health);
        for (int i = 0; i < n; i++)
        {
            var e = b.Slice(FrameSize + i * EventSize, EventSize);
            var ev = events[i];
            e[0] = (byte)ev.Kind; e[1] = ev.A;
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], ev.B);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], ev.Tick);
            F(e, 8, ev.V.X); F(e, 12, ev.V.Y); F(e, 16, ev.V.Z); F(e, 20, ev.V.W);
        }
        return FrameSize + n * EventSize;
    }

    /// <summary>
    /// Live for Speed's OutSim (classic): time in ms, angular velocity, heading (anticlockwise from above), pitch, roll,
    /// acceleration and velocity in world axes (x right, y forward, z up), position in 1/65536 m. Returns its length.
    /// </summary>
    public static int WriteOutSim(Span<byte> b, in TelemetryFrame f, int id = 0)
    {
        var r = f.Rot;
        // the body rates back to a world vector about Live for Speed's axes (yaw there is anticlockwise)
        var w = f.Rates.X * ToLfs(r.Right) - f.Rates.Y * ToLfs(r.Up) + f.Rates.Z * ToLfs(r.Forward);
        var att = Attitude(r);
        BinaryPrimitives.WriteUInt32LittleEndian(b, unchecked((uint)(long)System.Math.Round(f.Time * 1000.0)));
        V(b, 4, w);
        F(b, 16, -att.X); F(b, 20, att.Y); F(b, 24, att.Z);
        V(b, 28, ToLfs(f.Accel));
        V(b, 40, ToLfs(f.Vel));
        static int Fixed(double m) => unchecked((int)(long)System.Math.Round(m * 65536.0));
        BinaryPrimitives.WriteInt32LittleEndian(b[52..], Fixed(f.Pos.X));
        BinaryPrimitives.WriteInt32LittleEndian(b[56..], Fixed(f.Pos.Z));
        BinaryPrimitives.WriteInt32LittleEndian(b[60..], Fixed(f.Pos.Y));
        if (id == 0) return OutSimSize;
        BinaryPrimitives.WriteInt32LittleEndian(b[64..], id);
        return OutSimSize + 4;
    }

    // OutGauge's dashboard light bits
    public const uint DlFullBeam = 1 << 1, DlHandbrake = 1 << 2, DlOilWarn = 1 << 8;

    /// <summary>
    /// Live for Speed's OutGauge: time, car, flags, gear (0 reverse, 1 neutral, 2 first), speed m/s, rpm, the dials the game
    /// does not have left at rest (no turbo, 90 C, a full tank), the lights, throttle and brake 0..1, two 16-character displays.
    /// </summary>
    public static int WriteOutGauge(Span<byte> b, in TelemetryFrame f, string display1 = "", string display2 = "", int id = 0)
    {
        b[..OutGaugeSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(b, unchecked((uint)(long)System.Math.Round(f.Time * 1000.0)));
        Ascii(b.Slice(4, 4), f.Car ?? "");
        BinaryPrimitives.WriteUInt16LittleEndian(b[8..], 0);
        bool on = (f.Status & TelemetryStatus.EngineOn) != 0;
        b[10] = (byte)(f.Gear == 0 ? 0 : on ? f.Gear + 1 : 1);
        b[11] = 0;
        F(b, 12, f.Speed); F(b, 16, f.Rpm); F(b, 20, 0f); F(b, 24, 90f); F(b, 28, 1f); F(b, 32, 0f); F(b, 36, 90f);
        uint show = ((f.Status & TelemetryStatus.Lights) != 0 ? DlFullBeam : 0) | ((f.Status & TelemetryStatus.Handbrake) != 0 ? DlHandbrake : 0)
            | (f.Health < 0.25f && (f.Status & TelemetryStatus.Present) != 0 ? DlOilWarn : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b[40..], DlFullBeam | DlHandbrake | DlOilWarn);
        BinaryPrimitives.WriteUInt32LittleEndian(b[44..], show);
        F(b, 48, f.Throttle); F(b, 52, f.Brake); F(b, 56, 0f);
        Ascii(b.Slice(60, 16), display1);
        Ascii(b.Slice(76, 16), display2);
        if (id == 0) return OutGaugeSize;
        BinaryPrimitives.WriteInt32LittleEndian(b[92..], id);
        return OutGaugeSize + 4;
    }

    /// <summary>Text into a fixed field, ASCII, cut to fit with a terminating zero for the 16-byte displays.</summary>
    private static void Ascii(Span<byte> field, string s)
    {
        field.Clear();
        int max = field.Length == 4 ? 4 : field.Length - 1;
        for (int i = 0; i < s.Length && i < max; i++) field[i] = s[i] is >= ' ' and < (char)127 ? (byte)s[i] : (byte)'?';
    }

    /// <summary>One packet in the given format. Returns its length.</summary>
    public static int Write(TelemetryFormat format, Span<byte> b, in TelemetryFrame f, ReadOnlySpan<TelemetryEvent> events, string display1 = "", int id = 0) => format switch
    {
        TelemetryFormat.OutSim => WriteOutSim(b, f, id),
        TelemetryFormat.OutGauge => WriteOutGauge(b, f, display1, "", id),
        _ => WriteI26(b, f, events),
    };

    public static TelemetryFormat? ParseFormat(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        null or "" or "i26" => TelemetryFormat.I26,
        "outsim" => TelemetryFormat.OutSim,
        "outgauge" => TelemetryFormat.OutGauge,
        _ => null,
    };
}
