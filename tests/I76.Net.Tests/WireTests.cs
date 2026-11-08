using System.Numerics;
using I76.Net;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

public class WireTests
{
    private static readonly I76.Formats.Imaging.CarPaint Red = new(new I76.Formats.Imaging.PaintColour(200, 10, 20), new I76.Formats.Imaging.PaintColour(1, 2, 3), I76.Formats.Imaging.PaintFinish.Metallic);

    [Fact]
    public void TheProtocolIs23()
    {
        Assert.Equal(23, Protocol.Version);
        Assert.Equal(16, I76.Sim.Save.SnapshotHeader.CurrentVersion);
    }

    [Fact]
    public void ACarPaintRoundTripsAsBytesAndBadBytesAreNone()
    {
        Assert.Equal(Red, I76.Formats.Imaging.CarPaint.FromBytes(Red.ToBytes()));
        var one = new I76.Formats.Imaging.CarPaint(new I76.Formats.Imaging.PaintColour(1, 2, 3), null, I76.Formats.Imaging.PaintFinish.Matte);
        Assert.Equal(one, I76.Formats.Imaging.CarPaint.FromBytes(one.ToBytes()));
        Assert.Empty(new I76.Formats.Imaging.CarPaint(null).ToBytes());
        Assert.Null(I76.Formats.Imaging.CarPaint.FromBytes([]));
        Assert.Null(I76.Formats.Imaging.CarPaint.FromBytes([9, 9, 9]));
        Assert.Null(I76.Formats.Imaging.CarPaint.FromBytes(Red.ToBytes()[..^1]));
        Assert.Null(I76.Formats.Imaging.CarPaint.FromBytes([1, 0, 0]));
    }

    [Fact]
    public void AJoinRequestCarriesAPaintAndAnOlderOneEndsCleanly()
    {
        var req = new JoinRequest(Protocol.Version, "p", "vppirna1.vcf", 2, [1, 2], "pw", Red.ToBytes()) { Rejoin = (3, 0x1122334455667788) };
        var w = new NetWriter();
        req.Write(w);
        var r = new NetReader(w.Span);
        var back = JoinRequest.Read(ref r);
        Assert.Equal(Red.ToBytes(), back.Paint);
        Assert.Equal(((byte)3, 0x1122334455667788UL), back.Rejoin);
        Assert.Equal("pw", back.Password);
        // no rejoin, no paint
        w = new NetWriter();
        new JoinRequest(Protocol.Version, "p", "c.vcf", 0, []).Write(w);
        r = new NetReader(w.Span);
        back = JoinRequest.Read(ref r);
        Assert.Null(back.Paint);
        Assert.Null(back.Rejoin);
        // the shape before protocol 17 (nothing after the password, or a rejoin without a paint)
        w = new NetWriter();
        w.Str(Protocol.Magic); w.U16(16); w.Str("p"); w.Str("c.vcf"); w.U8(0); w.BlobU16([]); w.Str("pw");
        r = new NetReader(w.Span);
        back = JoinRequest.Read(ref r);
        Assert.Equal((16, "pw", null), ((int)back.Version, back.Password, back.Paint));
    }

    [Fact]
    public void ACarInfoCarriesItsPaint()
    {
        var w = new NetWriter();
        new CarInfo(5, "n1.vcf", 2, 3, "b", [9], Red.ToBytes()).Write(w);
        new CarInfo(6, "n2.vcf", -1, 0, "").Write(w);
        var r = new NetReader(w.Span);
        var a = CarInfo.Read(ref r);
        var b = CarInfo.Read(ref r);
        Assert.Equal(Red.ToBytes(), a.Paint);
        Assert.Equal(new byte[] { 9 }, a.Vcf);
        Assert.Null(b.Paint);
        Assert.True(r.End);
    }

    [Fact]
    public void NumbersRoundTrip()
    {
        var w = new NetWriter(4);
        uint[] us = [0, 1, 127, 128, 16383, 16384, 0xffffff, uint.MaxValue];
        int[] ints = [0, -1, 1, -64, 64, int.MinValue, int.MaxValue];
        foreach (var u in us) w.VarU(u);
        foreach (var i in ints) w.VarI(i);
        w.Str("Groove Champion");
        w.Half(12.5f);
        w.Pos(new Double3(1234.5, -7.25, 99999));
        var r = new NetReader(w.Span);
        foreach (var u in us) Assert.Equal(u, r.VarU());
        foreach (var i in ints) Assert.Equal(i, r.VarI());
        Assert.Equal("Groove Champion", r.Str());
        Assert.Equal(12.5f, r.Half());
        Assert.Equal(new Double3(1234.5, -7.25, 99999), r.Pos());
        Assert.True(r.End);
        Assert.Throws<WireException>(() => { var rr = new NetReader(w.Span[..3]); rr.U32(); });
    }

    [Fact]
    public void TheOwnCarsFlagsRoundTrip()
    {
        foreach (var (link, shortRange, cam) in new[] { (false, false, false), (true, false, true), (false, true, false), (true, true, true) })
        {
            var o = new OwnCar { Target = 5, Selected = 1, Link = link, RadarShort = shortRange, RadarCamera = cam };
            var w = new NetWriter();
            o.Write(w);
            var r = new NetReader(w.Span);
            var back = OwnCar.Read(ref r);
            Assert.True(r.End);
            Assert.Equal((5, (sbyte)1, link, shortRange, cam), (back.Target, back.Selected, back.Link, back.RadarShort, back.RadarCamera));
        }
    }

    [Fact]
    public void TheSpecialsChargesAndOnStateRoundTrip()
    {
        var o = new OwnCar();
        o.Charges[0] = 5 | 0x80; o.Charges[1] = 3; o.Charges[2] = 0;
        var w = new NetWriter();
        o.Write(w);
        var r = new NetReader(w.Span);
        var back = OwnCar.Read(ref r);
        Assert.Equal(new byte[] { 0x85, 3, 0 }, back.Charges);
    }

    [Fact]
    public void TheJammersOnStateRidesBit7OfItsChargesFromServerCarToClientCar()
    {
        static Vehicle Car()
        {
            var v = new Vehicle();
            v.Health = new I76.Sim.Damage.VehicleDamage(v, true);
            v.Health.Slots[17] = new I76.Sim.Damage.SpecialPart(1);
            v.Health.Slots[18] = new I76.Sim.Damage.SpecialPart(2);
            return v;
        }
        var server = Car();
        Assert.Equal("cjamm.wav", Specials.Activate(server, 0, 0f));   // 4 charges left, on
        var o = OwnCar.Capture(server, null);
        Assert.Equal(new byte[] { 4 | 0x80, 3, 0 }, o.Charges);
        var w = new NetWriter();
        o.Write(w);
        var r = new NetReader(w.Span);
        var client = Car();
        OwnCar.Read(ref r).ApplyTo(client, null, _ => null);
        var sp = (I76.Sim.Damage.SpecialPart)client.Health!.Slots[17]!;
        Assert.Equal((4, 1), (sp.Charges, sp.Unk_0x10));
        Assert.Equal(3, ((I76.Sim.Damage.SpecialPart)client.Health.Slots[18]!).Charges);
        Assert.Equal(0, ((I76.Sim.Damage.SpecialPart)client.Health.Slots[18]!).Unk_0x10);
    }

    [Fact]
    public void RotationsAndDirectionsKeepTheirShape()
    {
        var rng = new Random(5);
        for (int k = 0; k < 500; k++)
        {
            var q = Quaternion.Normalize(new Quaternion(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var rows = Wire.ToRows(q);
            var w = new NetWriter();
            w.Rot(rows);
            w.Dir(rows.Forward);
            Assert.Equal(10, w.Length);
            var r = new NetReader(w.Span);
            var back = r.Rot();
            var dir = r.Dir();
            Assert.True(Vector3.Distance(rows.Forward, back.Forward) < 2e-4f, $"{rows.Forward} {back.Forward}");
            Assert.True(Vector3.Distance(rows.Up, back.Up) < 2e-4f);
            Assert.True(Vector3.Distance(rows.Right, back.Right) < 2e-4f);
            Assert.True(Vector3.Distance(rows.Forward, dir) < 2e-4f);
        }
    }

    [Fact]
    public void MessagesRoundTrip()
    {
        var w = new NetWriter();
        new JoinRequest(Protocol.Version, "Taurus", "vppirna1.vcf", 3, [1, 2, 3]).Write(w);
        var r = new NetReader(w.Span);
        var j = JoinRequest.Read(ref r);
        Assert.Equal(("Taurus", "vppirna1.vcf", (byte)3, 3), (j.Name, j.Car, j.Team, j.Config.Length));

        w.Clear();
        var acc = new JoinAccept(2, 17, "m05", 1234, "vppirna1.vcf", true, 5000, 300, 5,
            [new CarInfo(17, "vppirna1.vcf", 2, 1, "Taurus"), new CarInfo(3, "vcgcour1.vcf", -1, 0, "nvcl0")], [new StructureState(5, [1]), new StructureState(900, [0x0a, 0x01])]);
        acc.Write(w);
        r = new NetReader(w.Span);
        Assert.Equal(MsgType.JoinAccept, (MsgType)r.U8());
        var acc2 = JoinAccept.Read(ref r);
        Assert.Equal(acc.Cars, acc2.Cars);
        Assert.Equal(acc.DestroyedStructures, acc2.DestroyedStructures);
        Assert.Equal((acc.Slot, acc.CarSerial, acc.Arena, acc.ServerTick, acc.MaxScore, acc.TimeLimit), (acc2.Slot, acc2.CarSerial, acc2.Arena, acc2.ServerTick, acc2.MaxScore, acc2.TimeLimit));

        // the host's Chase Crosshair rides in the accept (protocol 22): off unless the host allows it
        Assert.False(acc2.Crosshair);
        w.Clear();
        (acc with { Crosshair = true }).Write(w);
        r = new NetReader(w.Span); r.U8();
        Assert.True(JoinAccept.Read(ref r).Crosshair);

        // the arena's fingerprint rides in the accept (protocol 8): mission and terrain, size and CRC
        w.Clear();
        (acc with { MapSize = 11138, MapCrc = 0xdeadbeef, TerrainSize = 32768, TerrainCrc = 0x12345678 }).Write(w);
        r = new NetReader(w.Span); r.U8();
        var fp = JoinAccept.Read(ref r);
        Assert.Equal((11138u, 0xdeadbeefu, 32768u, 0x12345678u), (fp.MapSize, fp.MapCrc, fp.TerrainSize, fp.TerrainCrc));

        // the server's rate rides in the accept; one that is not offered is refused, and so is a client of the old protocol
        Assert.Equal(23, Protocol.Version);
        Assert.Equal(20, acc2.RateHz);
        w.Clear();
        (acc with { RateHz = 60 }).Write(w);
        r = new NetReader(w.Span); r.U8();
        Assert.Equal(SimRate.R60, JoinAccept.Read(ref r).Rate);
        w.Clear();
        (acc with { RateHz = 17 }).Write(w);
        r = new NetReader(w.Span); r.U8();
        var bad = r;
        try { JoinAccept.Read(ref bad); Assert.Fail("a rate that is not offered was accepted"); } catch (WireException) { }

        w.Clear();
        var h = new InputHistory(SimRate.R28);
        InputMessage m = null!;
        for (int t = 0; t < 10; t++) m = h.Push(new CarInput { Buttons = (CarButtons)t, Steer = (short)(t * 100) });
        m = m with { SnapshotAck = 12345 };
        m.Write(w);
        // ... and a byte for the motion hashes (none here)
        Assert.Equal(1 + 4 + 1 + Protocol.InputHistory(SimRate.R28) * CarInput.Size + 4 + 1, w.Length);
        r = new NetReader(w.Span);
        r.U8();
        var m2 = InputMessage.Read(ref r);
        Assert.Equal(10u, m2.Tick);
        Assert.Equal(m.Inputs, m2.Inputs);
        Assert.Equal((CarButtons)9, m2.Inputs[0].Buttons);
        // the predicted car's hashes, newest first from the tick they start at, and the ask for an exact motion
        w.Clear();
        (m with { Hashes = [11u, 22u, 33u], HashTick = 9, NeedExact = true }).Write(w);
        r = new NetReader(w.Span);
        r.U8();
        var m3 = InputMessage.Read(ref r);
        Assert.Equal([11u, 22u, 33u], m3.Hashes);
        Assert.Equal(9u, m3.HashTick);
        Assert.True(m3.NeedExact);
        Assert.Equal(12345u, m2.SnapshotAck);

        w.Clear();
        NetEvent[] evs =
        [
            new ShotEvent(4, 1, new(1, 2, 3), Vector3.UnitZ), new ImpactEvent("xbulc1.xdf", "wmgr1.wav", new(5, 6, 7), 1, 6, new(1, 1, 1), Vector3.UnitY, -Vector3.UnitZ),
            new ExplosionEvent("xcarx1.xdf", new(9, 9, 9)), new ZoneEvent(4, 3, 2), new KilledEvent(4, true), new SpawnEvent(new CarInfo(9, "a.vcf", -1, 0, "x")),
            new RemoveEvent(9), new ScoreEvent(1, 1500, 2, 1), new KillEvent(4, 5, 1066), new RenewedEvent(4, true), new ChatEvent(-1, "hello"),
            new GameOverEvent([(1, 5000), (0, 200)], 15), new MisfireEvent(4, 2), new SoundEvent(4, "vland.wav"), new StructureEvent(77, [0x0b], [-1, 2]), new SignEvent(78, Vector3.One),
        ];
        new EventBatch(99, evs).Write(w);
        r = new NetReader(w.Span);
        r.U8();
        var batch = EventBatch.Read(ref r);
        Assert.Equal(99u, batch.Tick);
        Assert.Equal(evs.Length, batch.Events.Count);
        for (int i = 0; i < evs.Length; i++)
            if (evs[i] is not GameOverEvent) Assert.Equal(evs[i], batch.Events[i]);
        Assert.Equal(((GameOverEvent)evs[11]).Standings, ((GameOverEvent)batch.Events[11]).Standings);
        Assert.Equal(15, ((GameOverEvent)batch.Events[11]).NextRoundSeconds);
    }

    [Fact]
    public void ACarStateRoundTripsWithinItsPrecision()
    {
        var v = new Vehicle { Pos = new Double3(1500.25, 31.5, -800.75), Vel = new(10, -2, 30), AngVel = new(0.1f, 2f, -0.3f), Steer = -0.5f, Throttle = 0.9f, Speed = 31.7f };
        v.Rot = Wire.ToRows(Quaternion.CreateFromYawPitchRoll(1.1f, 0.05f, -0.02f));
        v.Engine.Rpm = 4321; v.Engine.Gear = 3; v.Gear = -1; v.DriveState = 1; v.Flags = VehicleFlags.Lights | VehicleFlags.Dead; v.NetId = 1 << 24 | 300; v.NetGeneration = 2;
        v.FrontLeft.Travel = 0.12f; v.RearRight.Travel = -0.07f; v.FrontRight.SteerAngle = 0.3f; v.RearLeft.Blown = true;
        var s = CarState.Capture(v);
        var w = new NetWriter();
        Snapshot.WriteHeader(w, 1, 0, null);
        Snapshot.WriteBody(w, [s], []);
        var r = new NetReader(w.Span);
        r.U8();
        var d = SnapshotCodec.Read(ref r, 0, _ => null);
        var back = CarState.FromRec(300, d!.Frame.Cars[300]);
        Assert.True(r.End);
        var m = new Vehicle();
        back.ApplyTo(m);
        Assert.Equal(300, back.Serial);
        Assert.Equal(v.Flags, m.Flags);
        Assert.InRange(System.Math.Abs(m.Pos.X - v.Pos.X), 0, 1e-3);
        Assert.True(Vector3.Distance(m.Vel, v.Vel) < 0.05f);
        Assert.True(Vector3.Distance(m.Rot.Forward, v.Rot.Forward) < 2e-4f);
        Assert.Equal((4321f, 3, true), (m.Engine.Rpm, m.Engine.Gear, m.Gear < 0));
        Assert.Equal(1, m.DriveState);   // the dash's PRNDL strip
        Assert.InRange(m.FrontLeft.Travel, 0.1195f, 0.1205f);
        Assert.InRange(m.FrontRight.SteerAngle, 0.2995f, 0.3005f);
        Assert.True(m.RearLeft.Blown && !m.FrontLeft.Blown);
        Assert.Equal(2, m.NetGeneration);
        Assert.InRange(w.Length, 50, 90);
    }

    /// <summary>A part's health in a byte: whole and gone survive exactly (the bands' ends), the rest within a 255th.</summary>
    [Fact]
    public void APartsHealthFitsAByte()
    {
        foreach (int max in new[] { 1, 2, 7, 100, 255, 400, 1600, 6000 })
            for (int hp = 0; hp <= max; hp += Math.Max(1, max / 97))
            {
                var p = new I76.Sim.Damage.ChassisPart { Max = max, Hp = hp };
                int back = CarState.UnQPart(CarState.QPart(p), max);
                Assert.Equal(hp == 0, back == 0);
                Assert.Equal(hp == max, back == max);
                Assert.InRange(back, hp - max / 255 - 1, hp + max / 255 + 1);
            }
        Assert.Equal(255, CarState.QPart(null));
    }

    /// <summary>
    /// The own car's motion (protocol 13) crosses the wire at full precision: read back it is the same car bit for bit,
    /// the contact record's other party by id, the slow part only when it rides along; the snapshot's header carries
    /// it with the slack.
    /// </summary>
    [Fact]
    public void TheOwnCarsMotionCrossesTheWireExactly()
    {
        var other = new Vehicle { NetId = 1 << 24 | 9 };
        var v = new Vehicle
        {
            NetId = 1 << 24 | 4, NetGeneration = 300, Pos = new Double3(2086.123456789, 51.5, 49914.987654321), Rot = new Rows(new(0.6f, 0, -0.8f), Vector3.UnitY, new(0.8f, 0, 0.6f)),
            Vel = new(1.1f, -0.2f, 30.3f), AngVel = new(0, 0.31f, 0.01f), Accel = new(0.5f, 0, 4), Speed = 30.32f, Steer = -0.4f, Throttle = 0.9999f, Gear = -1,
            Handbrake = 1, StartEngineKey = 1, DriveState = 1, Flags = VehicleFlags.EngineOn | VehicleFlags.Sliding | (VehicleFlags)0x80000010, Surface = 3,
            GroundNormal = Vector3.Normalize(new(0.1f, 1, 0)), NormalLoad = 9.6f, HeightAboveGround = 0.25f, GroundHeight = 51.2f, Timer = 7.5f,
            Rec = new Contact { Hit = true, Other = other, OtherMass = 1500, OtherVel = new(3, 0, 1), T = 0.01f, C = new(0.9f, 0.5f, 2), RelVel = new(-2, 0, 29), N = Vector3.UnitX, Node = null },
        };
        v.Engine.Gear = 3; v.Engine.Rpm = 4321.5f; v.Engine.Speedo = 61.7f; v.Engine.Force = 123456.7f; v.Engine.Damage = 0.75f;
        v.P.BrakeStrength = 0.6f;
        v.RearLeft.Y1 = 0.123456789012; v.RearLeft.Y2 = -0.0987654321; v.RearLeft.Travel = 0.031f; v.FrontRight.Blown = true; v.FrontRight.Rest = 0.21f;
        var d = new Driver(v, 0) { State = new DriverState { HoldUp = 1.25f, HoldLeft = 0.5f } };
        var m = OwnMotion.Capture(v, d, slow: true);
        var w = new NetWriter();
        Snapshot.WriteHeader(w, 1000, 990, null, -1, m);
        Snapshot.WriteBody(w, [], []);
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.Snapshot, (MsgType)r.U8());
        var s = Snapshot.Read(ref r);
        Assert.Equal((1000u, 990u, (sbyte)-1), (s.Tick, s.AckedInput, s.Slack));
        Assert.Null(s.Own);
        var got = s.Motion!;
        Assert.Equal(m.Key(), got.Key());
        Assert.NotNull(got.Slow);
        Assert.True(got.Slow!.SameAs(m.Slow!));
        Assert.Equal(2, got.RecKind);
        Assert.Equal(9, got.RecSerial);

        // written into another copy of the car: the same car again, the record's other party found by its id
        var u = new Vehicle { NetId = v.NetId };
        var du = new Driver(u, 0);
        got.ApplyTo(u, du, null, id => id == 9 ? other : null, _ => null);
        Assert.Equal(m.Key(), OwnMotion.Capture(u, du).Key());
        Assert.Same(other, u.Rec.Other);
        Assert.Equal(0.75f, u.Engine.Damage);
        Assert.Equal(0.6f, u.P.BrakeStrength);
        Assert.Equal(0.21f, u.FrontRight.Rest);
        Assert.Equal(1.25f, du.State.HoldUp);
        Assert.Equal(u.NetGeneration, du.Generation);
        // a renewal the driver's keys have not caught up with: the next tick resets them, on the client as on the server
        d.Generation = 299;
        var stale = OwnMotion.Capture(v, d);
        stale.ApplyTo(u, du, null, _ => null, _ => null);
        Assert.NotEqual(u.NetGeneration, du.Generation);
        // without the slow part the motion is smaller by it, and the last one received stands in
        Assert.Null(stale.Slow);
        var w2 = new NetWriter();
        stale.Write(w2);
        var w3 = new NetWriter();
        m.Write(w3);
        Assert.True(w2.Length < w3.Length - 100, $"{w2.Length} against {w3.Length}");
    }

    [Fact]
    public void TheQueuesSlackIsWhatATickLeftWaiting()
    {
        var q = new InputQueue(SimRate.R20);
        q.Add(new InputMessage(3, [default, default, default]));
        q.Next();
        Assert.Equal(2, q.Slack);
        q.Next(); q.Next();
        Assert.Equal(0, q.Slack);
        q.Next();
        Assert.Equal(-1, q.Slack);
    }
}

public class InputQueueTests
{
    private static InputMessage Msg(uint tick, params CarInput[] newestFirst) => new(tick, newestFirst);

    /// <summary>The wheel's step back through the weapons (Mouse Options' Cycle Weapon) reaches the server as the forward step does.</summary>
    [Fact]
    public void TheWeaponCycleBothWaysIsCarried()
    {
        var q = new InputQueue(SimRate.R28);
        var i = new CarInput { Commands = CarCommands.WeaponCyclePrev | CarCommands.WeaponCycle };
        Assert.Equal(i, q.Check(i));
        Assert.Equal(0, q.Corrected);
    }

    [Fact]
    public void EachClientTickIsAppliedOnceInOrder_ALateOneHoldsTheKeys()
    {
        var q = new InputQueue(SimRate.R28);
        var a = new CarInput { Buttons = CarButtons.ThrottleUp };
        var b = new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.SteerLeft, Commands = CarCommands.WeaponCycle };
        var c = new CarInput { Buttons = CarButtons.Fire };
        q.Add(Msg(2, b, a));
        q.Add(Msg(2, b, a));                 // the same again: nothing new
        q.Add(Msg(3, c, b, a));
        Assert.Equal(3, q.Waiting);
        Assert.Equal(a, q.Next());
        Assert.Equal(b, q.Next());
        Assert.Equal(c, q.Next());
        Assert.Equal(3u, q.LastApplied);
        // nothing waiting: the held keys, without a repeat of any command
        q.Add(Msg(1, b));                     // old news
        var held = q.Next();
        Assert.Equal(CarButtons.Fire, held.Buttons);
        Assert.Equal(1, q.Starved);
        q.Add(Msg(4, b));
        Assert.Equal(CarCommands.WeaponCycle, q.Next().Commands);
        Assert.Equal(CarCommands.None, q.Next().Commands);
    }

    [Fact]
    public void AClientsAnalogPositionCrossesTheWireAndIsKeptByTheServer()
    {
        // the stick's throttle and steer ride in CarInput (the Analog button bit, two int16s): no protocol change
        var sent = new CarInput { Buttons = CarButtons.Analog | CarButtons.Fire, Throttle = 20000, Steer = -12345 };
        var h = new InputHistory(SimRate.R28);
        var w = new NetWriter();
        h.Push(sent).Write(w);
        var r = new NetReader(w.Span);
        r.U8();
        var got = InputMessage.Read(ref r).Inputs[0];
        Assert.Equal(sent, got);
        var q = new InputQueue(SimRate.R28);
        q.Add(Msg(1, got));
        var taken = q.Next();
        Assert.True(taken.Has(CarButtons.Analog));
        Assert.Equal((short)20000, taken.Throttle);
        Assert.Equal((short)-12345, taken.Steer);
    }

    [Fact]
    public void NonsenseIsCorrected()
    {
        var q = new InputQueue(SimRate.R28);
        var bad = new CarInput { Buttons = (CarButtons)0xffffffff, Commands = (CarCommands)0xffffffff, Steer = short.MinValue, Throttle = short.MinValue };
        q.Add(Msg(1, bad));
        var i = q.Next();
        Assert.Equal((CarButtons)0xffff, i.Buttons);
        Assert.False(i.Has(CarCommands.Skip) || i.Has(CarCommands.Poem));
        // the typed cheats are single player's: a server never applies them
        Assert.False(i.Has(CarCommands.CheatGetDown | CarCommands.CheatFlatFL | CarCommands.CheatFlatFR | CarCommands.CheatFlatRL | CarCommands.CheatFlatRR));
        Assert.Equal(InputQueue.Commands, i.Commands);
        Assert.Equal(-short.MaxValue, i.Steer);
        Assert.Equal(-short.MaxValue, i.Throttle);
        Assert.Equal(1, q.Corrected);
        // the analog positions mean nothing without the analog bit
        q.Add(Msg(2, new CarInput { Steer = 999 }));
        Assert.Equal(0, q.Next().Steer);
    }

    [Fact]
    public void AClientRunningAheadLosesItsOldestInputsButNotTheirCommands()
    {
        var q = new InputQueue(SimRate.R28);
        for (uint t = 1; t <= 20; t++)
            q.Add(Msg(t, new CarInput { Commands = t == 3 ? CarCommands.Lights : 0 }));
        Assert.Equal(Protocol.MaxQueuedInputs(SimRate.R28), q.Waiting);
        Assert.Equal(20 - Protocol.MaxQueuedInputs(SimRate.R28), q.Dropped);
        var first = q.Next();
        Assert.True(first.Has(CarCommands.Lights));
        // and at the server's tick, a backlog is caught up to the last few
        Assert.Equal(20u - Protocol.CatchUpInputs(SimRate.R28) + 1, q.LastApplied);
        Assert.Equal(Protocol.CatchUpInputs(SimRate.R28) - 1, q.Waiting);
    }
}

public class SnapshotPartsTests
{
    [Fact]
    public void ABigSnapshotGoesInPiecesAndComesBackWhole()
    {
        var snap = new NetWriter();
        Snapshot.WriteHeader(snap, 77, 70, null);
        var cars = Enumerable.Range(1, 40).Select(i => new CarState { Serial = i, Pos = new(i * 731.3, 2 + i, -3000 + i * 17), Rot = I76.Sim.Math.Rows.AboutY(i), Vel = new(i, -i, 2 * i), AngVel = new(0.1f * i, 0, 1), Rpm = 1000 + 50 * i, Speed = i, Travel = { [0] = 0.01f * i } }).ToList();
        var rounds = Enumerable.Range(1, 30).Select(i => new RoundState { Serial = i, Shooter = 1, Pos = new(i * 1001, 7, -i * 333), Rot = I76.Sim.Math.Rows.AboutY(i), Travelled = i * 9 }).ToList();
        Snapshot.WriteBody(snap, cars, rounds);
        Assert.True(snap.Length > 2000);
        var pieces = SnapshotParts.Split(77, snap.ToArray(), 1000).ToList();
        Assert.All(pieces, p => Assert.True(p.Length <= 1000));
        var asm = new SnapshotAssembler();
        byte[]? whole = null;
        // out of order, with a piece of another tick in between, and one piece repeated
        var order = Enumerable.Range(0, pieces.Count).Reverse().Append(0).ToList();
        foreach (int i in order)
        {
            var r = new NetReader(pieces[i]);
            Assert.Equal(MsgType.SnapshotPart, (MsgType)r.U8());
            whole ??= asm.Add(ref r);
            var other = SnapshotParts.Split(78, snap.ToArray(), 1000).First();
            var r2 = new NetReader(other);
            r2.U8();
            Assert.Null(asm.Add(ref r2));
        }
        Assert.NotNull(whole);
        Assert.Equal(snap.ToArray(), whole);
        var rr = new NetReader(whole);
        rr.U8();
        var back = Snapshot.Read(ref rr);
        Assert.Equal((77u, 40, 30), (back.Tick, back.Cars.Count, back.Rounds.Count));
    }
}

/// <summary>WEAPON RESTRICTIONS and Hidden Weapons &amp; Cars in a game's session answer (protocol 12, appended; docs/hidden-content.md).</summary>
public class RestrictionWireTests
{
    private static SessionInfo Info(I76.Sim.Trip.WeaponRestrictions? r, bool hidden) =>
        new(7, Protocol.Version, "Nitro night", "N01.CBT", 1, 8, false, false, 0, 0, false, [], Locked: true, Restrictions: r, Hidden: hidden);

    [Fact]
    public void TheRestrictionsAndTheHiddenFlagRoundTrip()
    {
        foreach (var (r, hidden) in new (I76.Sim.Trip.WeaponRestrictions?, bool)[]
                 {
                     (null, false), (null, true), (I76.Sim.Trip.WeaponRestrictions.Everything, false),
                     (I76.Sim.Trip.WeaponRestrictions.Normalize(false, [(2, 6), (2, 106), (4, -1), (6, 6)]), true),
                 })
        {
            var w = new NetWriter();
            Info(r, hidden).Write(w);
            var back = SessionInfo.TryRead(w.Span)!;
            Assert.NotNull(back);
            Assert.Equal(hidden, back.Hidden);
            Assert.Equal(r ?? I76.Sim.Trip.WeaponRestrictions.None, back.Restricted);
            Assert.True(back.Locked);
            Assert.Equal(Info(r, hidden) with { Drivers = [] }, back with { Drivers = [] });
        }
    }

    [Fact]
    public void AnAnswerWithoutThemReadsAsNone()
    {
        // an answer written before the fields (it ends at the password flag) has no restrictions and no hidden content
        var w = new NetWriter();
        Info(I76.Sim.Trip.WeaponRestrictions.Normalize(false, [(2, 6)]), true).Write(w);
        var cut = w.Span[..^(2 + 3 + 1 + 1 + 1 + 1 + 1 + 1 + DiscoveryTests.Names)].ToArray();     // the flags, the count, the pair, protocol 15's co-op byte, the rate's, protocol 20's lobby byte, the car reset's, the CB's, protocol 22's crosshair and names
        var back = SessionInfo.TryRead(cut)!;
        Assert.NotNull(back);
        Assert.False(back.Hidden);
        Assert.True(back.Restricted.IsEmpty);
        Assert.True(back.Locked);
        Assert.False(back.Coop);
    }

    [Fact]
    public void TooManyPairsAreNotAnAnswer()
    {
        var w = new NetWriter();
        Info(null, false).Write(w);
        var bytes = w.Span.ToArray();
        bytes[^(7 + DiscoveryTests.Names)] = 200;    // the pair count (before the co-op, rate, lobby, car reset, CB and crosshair bytes and the names), past the most a list carries
        Assert.Null(SessionInfo.TryRead(bytes));
    }
}
