using System.Buffers.Binary;
using System.Numerics;
using I76.Game.Sim;
using I76.Sim.Math;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The telemetry feed's packets, byte for byte (docs/dev-hooks.md, "Telemetry out").</summary>
public class TelemetryPacketTests
{
    /// <summary>Rows for a heading (turning right from +z), nose up by pitch, right side down by roll.</summary>
    private static Rows Pose(float heading, float pitch = 0, float roll = 0)
    {
        var m = Matrix4x4.CreateRotationZ(-roll) * Matrix4x4.CreateRotationX(-pitch) * Matrix4x4.CreateRotationY(heading);
        // the game's axes: x right, y up, z forward; rotations built so the attitude reads back as given
        Vector3 T(Vector3 v) => Vector3.TransformNormal(v, m);
        return new Rows(T(Vector3.UnitX), T(Vector3.UnitY), T(Vector3.UnitZ));
    }

    private static TelemetryFrame Frame() => new()
    {
        Seq = 7, Tick = 400, Alpha = 0.25f, Time = 19.9f,
        Status = TelemetryStatus.Present | TelemetryStatus.EngineOn | TelemetryStatus.Lights,
        Gear = 3, SkidMask = 0b0101, BlownMask = 0b1000,
        Pos = new Double3(1812.5, 100.25, 50532.5), Rot = Pose(MathF.PI / 2),
        Vel = new Vector3(20, -1, 0.5f), Accel = new Vector3(2, 0, -3), Rates = new Vector3(0.1f, 0.2f, 0.3f),
        Speed = 20.03f, Rpm = 3500, Throttle = 0.75f, Brake = 0, Steer = -0.5f, Health = 0.6f, Car = "vppirna1",
    };

    private static float F(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));

    [Fact]
    public void TheI26PacketIsItsHeaderFrameAndEvents()
    {
        var b = new byte[TelemetryPackets.MaxSize];
        var f = Frame();
        TelemetryEvent[] ev = [new(TelemetryEventKind.Shot, 3, 1, 399, new Vector4(0, 0, 1, 0)), new(TelemetryEventKind.Explosion, 0, 0, 400, new Vector4(12, 3, 4, 0))];
        int n = TelemetryPackets.WriteI26(b, f, ev);
        Assert.Equal(176 + 2 * 24, n);
        Assert.Equal("I26T"u8.ToArray(), b[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)));
        Assert.Equal(176, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(400u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
        Assert.Equal(0.25f, F(b, 16));
        Assert.Equal(19.9f, F(b, 20));
        Assert.Equal(1u | 2u | 256u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)));
        Assert.Equal(new byte[] { 3, 0b0101, 0b1000, 2 }, b[28..32]);
        Assert.Equal(1812.5, BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(32)));
        Assert.Equal(100.25, BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(40)));
        Assert.Equal(50532.5, BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(48)));
        Assert.Equal(f.Rot.Forward.X, F(b, 80));       // facing +x
        Assert.Equal(MathF.PI / 2, F(b, 92), 5);        // heading
        Assert.Equal(20f, F(b, 104));                   // velocity x
        Assert.Equal(-3f, F(b, 124));                   // acceleration z
        Assert.Equal(3f, F(b, 128), 5);                 // sway: the world's -z is the car's right when it faces +x
        Assert.Equal(2f, F(b, 136), 5);                 // surge
        Assert.Equal((0.1f, 0.2f, 0.3f), (F(b, 140), F(b, 144), F(b, 148)));
        Assert.Equal((20.03f, 3500f, 0.75f, 0f, -0.5f, 0.6f), (F(b, 152), F(b, 156), F(b, 160), F(b, 164), F(b, 168), F(b, 172)));
        Assert.Equal(new byte[] { 1, 3, 1, 0, 143, 1, 0, 0 }, b[176..184]);
        Assert.Equal(1f, F(b, 176 + 16));
        Assert.Equal(4, b[200]);
        Assert.Equal(12f, F(b, 200 + 8));
        // no more than MaxEvents go in one packet
        Assert.Equal(TelemetryPackets.MaxSize, TelemetryPackets.WriteI26(b, f, Enumerable.Repeat(ev[0], 50).ToArray()));
        Assert.Equal(TelemetryPackets.MaxEvents, b[31]);
    }

    [Fact]
    public void OutSimIsLiveForSpeedsSixtyFourBytes()
    {
        var b = new byte[68];
        var f = Frame();
        Assert.Equal(64, TelemetryPackets.WriteOutSim(b, f));
        Assert.Equal(19900u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(-MathF.PI / 2, F(b, 16), 5);       // facing east: anticlockwise heading -90 degrees
        Assert.Equal(0f, F(b, 20), 5);
        Assert.Equal(0f, F(b, 24), 5);
        Assert.Equal((2f, -3f, 0f), (F(b, 28), F(b, 32), F(b, 36)));   // x right, y forward (the game's z), z up
        Assert.Equal((20f, 0.5f, -1f), (F(b, 40), F(b, 44), F(b, 48)));
        Assert.Equal(1812.5 * 65536, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(52)));
        Assert.Equal((int)(50532.5 * 65536 % 4294967296.0 - 4294967296.0), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(56)));   // wraps past 32767 m
        Assert.Equal((int)(100.25 * 65536), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(60)));
        // the yaw rate (turning right) is a negative rotation about up
        var still = Frame() with { Rot = Rows.Identity, Rates = new Vector3(0, 1, 0) };
        TelemetryPackets.WriteOutSim(b, still);
        Assert.Equal((0f, 0f, -1f), (F(b, 4), F(b, 8), F(b, 12)));
        Assert.Equal(68, TelemetryPackets.WriteOutSim(b, f, id: 5));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(64)));
    }

    [Fact]
    public void OutGaugeIsLiveForSpeedsNinetyTwoBytes()
    {
        var b = new byte[96];
        var f = Frame() with { Status = TelemetryStatus.Present | TelemetryStatus.EngineOn | TelemetryStatus.Handbrake, Brake = 0.4f };
        Assert.Equal(92, TelemetryPackets.WriteOutGauge(b, f, "RKT LNCHR"));
        Assert.Equal(19900u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal("vppi"u8.ToArray(), b[4..8]);
        Assert.Equal(4, b[10]);                         // third gear: 0 reverse, 1 neutral, 2 first
        Assert.Equal((20.03f, 3500f), (F(b, 12), F(b, 16)));
        Assert.Equal(TelemetryPackets.DlFullBeam | TelemetryPackets.DlHandbrake | TelemetryPackets.DlOilWarn, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
        Assert.Equal(TelemetryPackets.DlHandbrake, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44)));
        Assert.Equal((0.75f, 0.4f, 0f), (F(b, 48), F(b, 52), F(b, 56)));
        Assert.Equal("RKT LNCHR\0"u8.ToArray(), b[60..70]);
        Assert.All(b[76..92], x => Assert.Equal(0, x));
        TelemetryPackets.WriteOutGauge(b, f with { Status = TelemetryStatus.Present });
        Assert.Equal(1, b[10]);                         // engine off: neutral
        TelemetryPackets.WriteOutGauge(b, f with { Gear = 0 });
        Assert.Equal(0, b[10]);                         // reverse
        Assert.Equal(96, TelemetryPackets.WriteOutGauge(b, f, id: 9));
    }

    [Fact]
    public void TheBodyRatesFollowTheTurn()
    {
        const float dt = 0.05f;
        var r = TelemetryPackets.BodyRates(Pose(0), Pose(0.01f), dt);       // turning right
        Assert.Equal(0.2f, r.Y, 3);
        Assert.Equal(0f, r.X, 4);
        r = TelemetryPackets.BodyRates(Pose(1, 0), Pose(1, 0.01f), dt);     // nose rising
        Assert.Equal(0.2f, r.X, 3);
        r = TelemetryPackets.BodyRates(Pose(1, 0, 0), Pose(1, 0, 0.01f), dt);   // right side going down
        Assert.Equal(0.2f, r.Z, 3);
        var att = TelemetryPackets.Attitude(Pose(0.5f, 0.2f, 0.1f));
        Assert.Equal((0.5f, 0.2f, 0.1f), (MathF.Round(att.X, 4), MathF.Round(att.Y, 4), MathF.Round(att.Z, 4)));
    }

    [Fact]
    public void TargetsParseWithTheirFormats()
    {
        Assert.Equal(("127.0.0.1:4123", TelemetryFormat.OutSim), Show(TelemetryOut.ParseTarget("outsim@127.0.0.1:4123", TelemetryFormat.I26)));
        Assert.Equal(("127.0.0.1:7676", TelemetryFormat.I26), Show(TelemetryOut.ParseTarget("localhost:7676", TelemetryFormat.I26)));
        Assert.Equal(("192.0.2.20:4124", TelemetryFormat.OutGauge), Show(TelemetryOut.ParseTarget("192.0.2.20:4124", TelemetryPackets.ParseFormat("OutGauge"))));
        Assert.Null(TelemetryOut.ParseTarget("example.com:4123", TelemetryFormat.I26));   // no name lookups
        Assert.Null(TelemetryOut.ParseTarget("127.0.0.1", TelemetryFormat.I26));
        Assert.Null(TelemetryOut.ParseTarget("127.0.0.1:0", TelemetryFormat.I26));
        Assert.Null(TelemetryOut.ParseTarget("forza@127.0.0.1:5300", TelemetryFormat.I26));
        Assert.Null(TelemetryPackets.ParseFormat("sled"));
    }

    private static (string, TelemetryFormat)? Show((System.Net.IPEndPoint To, TelemetryFormat Format)? t) => t is { } x ? (x.To.ToString(), x.Format) : null;
}
