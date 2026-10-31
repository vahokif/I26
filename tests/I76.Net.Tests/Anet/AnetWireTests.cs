using System.Net;
using I76.Net.Anet;
using Xunit;

namespace I76.Net.Tests.Anet;

/// <summary>
/// The anet encodings against bytes the real server and a real anet client
/// (the LGPL source, built 32-bit) exchanged on 127.0.0.1: species 1234
/// (d2 04), host "HostGame" at 127.0.0.1:37286 (91 a6) with karma 3700
/// (74 0e), joiner at 127.0.0.1:46026 (b3 ca), server at 127.0.0.1:29157 (71 e5).
/// </summary>
public class AnetWireTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // the host's publish: DATA carrying a small packet into its MYSESSIONS table [2], the 83-byte raw record
    private const string Publish =
        "64 54 68 62 62 64 25 04 08 01 02 53 7f 00 00 01 91 a6 74 0e 00 7f 00 00 01 91 a6 00 00 00 00 00 00 d2 04 74 0e 04 00 00 00 84 00 " +
        "48 6f 73 74 47 61 6d 65 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 7f 00 00 01 34 12 00 00";

    // the server to the joiner: a gather of two DATA packets, the servers table row and the species table's two sessions (compact)
    private const string Listing =
        "64 47 1b 64 54 42 6b 16 64 25 04 06 01 06 09 7f 00 00 01 52 a5 6c 6f 63 61 6c 68 6f 73 74 3f 64 54 43 6b 3a 64 25 01 08 03 01 d2 04 " +
        "10 7f 00 00 01 71 e5 cb b6 1e 00 bc 09 54 65 73 74 20 52 6f 6f 6d 00 01 01 10 7f 00 00 01 91 a6 74 0e 04 00 84 08 48 6f 73 74 47 61 6d 65 00 02 34 12";

    [Fact]
    public void TheHostsRawRecordReadsAndWritesByteForByte()
    {
        var p = Hex(Publish);
        Assert.Equal((byte)'T', p[1]);
        var payload = p.AsSpan(5, p[4]).ToArray();
        var ch = Assert.Single(new DptabReader().Read(payload));
        Assert.Equal([AnetWire.KeyMySessions], ch.Key);
        var s = AnetSession.Unpack(ch.Subkey, ch.Value)!;
        Assert.Equal("HostGame", s.Name);
        Assert.Equal(0x0e74, s.Karma);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0x91a6), s.Master.ToEndPoint());
        Assert.Equal(s.Master, s.KeyAddress);
        Assert.Equal(4, s.MaxPlayers);
        Assert.Equal(0, s.Players);
        Assert.Equal(0x84, s.Flags);
        Assert.Equal(0x1234, BitConverter.ToInt32(s.User, 0));
        // ours writes the same record (dwReserved1: the game server's IP)
        Assert.Equal(ch.Value, s.PackRaw(1234, AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 29157))));
        Assert.Equal(payload, AnetWire.Small(4, [AnetWire.KeyMySessions], [(s.Subkey(), s.PackRaw(1234, AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 29157))))]));
    }

    [Fact]
    public void TheServersListingReadsThroughTheLink()
    {
        var sent = new List<byte[]>();
        var link = new DpioLink(sent.Add);
        // the server's SYN first (its first packet number 0x6b41: the listing's DATA are 0x6b42 and 0x6b43)
        link.Open(0, AnetWire.SynBody(AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 0xb3ca))));
        link.Receive(Hex("64 59 41 6b 15 05 06 7f 00 00 01 71 e5 7f 00 00 01 b3 ca 07 7f 00 00 01 71 e5"), 0.01);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0xb3ca), link.SeenAs!.Value.ToEndPoint());
        Assert.Equal(7, link.PeerCaps);
        link.Receive(Hex(Listing), 0.02);
        // both DATA acked as dpio does: the packet and "everything before the next" (offset 1)
        Assert.Contains(sent, d => d.AsSpan().SequenceEqual(Hex("64 55 42 6b 01")));
        Assert.Contains(sent, d => d.AsSpan().SequenceEqual(Hex("64 55 43 6b 01")));
        var tab = new DptabReader();
        var changes = link.Delivered.SelectMany(d => tab.Read(d)).ToList();
        Assert.Equal(3, changes.Count);
        Assert.Equal([6], changes[0].Key);                         // the servers table: not ours, ignored by the client
        var key = AnetWire.SessionsKey(1234);
        Assert.Equal(key, changes[1].Key);
        var room = AnetSession.Unpack(changes[1].Subkey, changes[1].Value)!;
        Assert.Equal("Test Room", room.Name);
        Assert.Equal(30, room.MaxPlayers);
        Assert.Null(room.Tag);
        var game = AnetSession.Unpack(changes[2].Subkey, changes[2].Value)!;
        Assert.Equal("HostGame", game.Name);
        Assert.Equal(0x0e74, game.Karma);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0x91a6), game.Master.ToEndPoint());
        Assert.Equal(changes[2].Value, game.PackCompact());
    }

    [Fact]
    public void LoginJoinAndRequestOpenMatchTheServer()
    {
        Assert.Equal(Hex("65 31 d2 04"), AnetWire.Login(1234));
        var s = new AnetSession(AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 0x91a6)), 0x0e74,
            AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 0x91a6)), 4, 0, 0x84, "HostGame", "", new byte[14]);
        Assert.Equal(Hex("64 49 01 7f 00 00 01 91 a6 74 0e 00 00 00 00"), AnetWire.IndirectJoinPacket(s.Subkey()));
        // the server's REQUEST_OPEN to the host: the joiner's address twice (the second slot's padding is garbage)
        var ro = AnetWire.ReadRequestOpen(Hex("64 58 7f 00 00 01 b3 ca 00 00 00 00 7f 00 00 01 b3 ca 00 00 01 00"))!.Value;
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0xb3ca), ro.Seen.ToEndPoint());
        Assert.Equal(ro.Seen, ro.Reported);
    }

    [Fact]
    public void SynAndAckLayouts()
    {
        var me = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 0x91a6));
        var srv = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 0x71e5));
        // the full SYN as the anet client sends it, and ours (no destination: see SynBody)
        Assert.Equal(Hex("64 59 64 62 15 05 06 7f 00 00 01 91 a6 7f 00 00 01 71 e5 07 7f 00 00 01 91 a6"),
            AnetWire.Windowed(AnetWire.Syn, 0x6264, AnetWire.SynBody(me, srv, 7, me)));
        Assert.Equal(Hex("64 59 64 62 08 05 06 7f 00 00 01 91 a6"), AnetWire.Windowed(AnetWire.Syn, 0x6264, AnetWire.SynBody(me)));
        Assert.Equal(Hex("64 55 64 62 80"), AnetWire.AckPacket(0x6264, AnetWire.NoOffset));
        Assert.Equal(Hex("64 54 65 62 04 65 31 d2 04"), AnetWire.Windowed(AnetWire.Data, 0x6265, AnetWire.Login(1234)));
    }

    [Fact]
    public void TheCrcIsDpCrc32()
    {
        // dp_crc32: polynomial 0x04C11DB7 not reflected, preset and complemented (CRC-32/BZIP2's check value)
        Assert.Equal(0xFC891918u, AnetWire.Crc32("123456789"u8));
        var key = AnetWire.SessionsKey(AnetSpecies.I26);
        var value = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var first = AnetWire.InitialPacket(7, 1, key, new byte[8], value, 200);
        var body = AnetWire.Packet(AnetWire.TabBody, [7, .. value[200..]]);
        var r = new DptabReader();
        Assert.Empty(r.Read(first));
        var ch = Assert.Single(r.Read(body));
        Assert.Equal(value, ch.Value);
        Assert.Equal(0, r.Bad);
    }

    [Fact]
    public void TheI26TagRoundTripsAndTheLinkSurvivesLoss()
    {
        var t = new I26SessionTag(Protocol.Version, true, true, true, false, I76.Sim.Missions.MeleeKind.Ctf, 3, I76.Sim.Missions.MeleeGoal.Captures, "N40.CF3");
        Assert.Equal(t, I26SessionTag.Read(t.Write()));
        Assert.Equal("m05", I26SessionTag.Read(new I26SessionTag(Protocol.Version, false, false, false, false, default, 2, default, "m05.msn").Write())!.Arena);
        Assert.Null(I26SessionTag.Read(new byte[14]));
        // Hidden Weapons & Cars rides in byte 13's top bits; a 0.5.0 host's tag leaves them clear: the rule unknown
        foreach (bool? h in new bool?[] { true, false, null })
            Assert.Equal(h, I26SessionTag.Read((t with { Hidden = h }).Write())!.Hidden);
        var old = (t with { Hidden = true }).Write();
        old[13] &= 0x3f;
        Assert.Null(I26SessionTag.Read(old)!.Hidden);
        Assert.Equal(t.Goal, I26SessionTag.Read((t with { Hidden = true }).Write())!.Goal);

        // two links, a third of the datagrams lost each way: everything arrives once, in order
        var rng = new Random(7);
        var aToB = new Queue<byte[]>(); var bToA = new Queue<byte[]>();
        var a = new DpioLink(d => { if (rng.NextDouble() > 0.33) aToB.Enqueue(d); }) { ConnectTimeout = 60 };
        var b = new DpioLink(d => { if (rng.NextDouble() > 0.33) bToA.Enqueue(d); }) { ConnectTimeout = 60 };
        var adrA = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 1)); var adrB = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 2));
        a.Open(0, AnetWire.SynBody(adrA));
        b.Open(0, AnetWire.SynBody(adrB, adrA, 0, adrB));
        for (int i = 0; i < 100; i++) a.Send([(byte)'x', (byte)i], 0);
        var got = new List<int>();
        for (double t2 = 0; t2 < 60 && got.Count < 100; t2 += 0.05)
        {
            while (aToB.TryDequeue(out var d)) b.Receive(d, t2);
            while (bToA.TryDequeue(out var d)) a.Receive(d, t2);
            a.Update(t2); b.Update(t2);
            while (b.Delivered.TryDequeue(out var p)) got.Add(p[1]);
        }
        Assert.Equal(Enumerable.Range(0, 100), got);
        Assert.Equal(adrA, a.SeenAs);
        Assert.False(a.Closed);
    }

    /// <summary>Garbage from a server (or anyone spoofing one) is dropped, never thrown on.</summary>
    [Fact]
    public void GarbageIsDroppedQuietly()
    {
        var rng = new Random(26);
        var r = new DptabReader();
        var link = new DpioLink(_ => { });
        link.Open(0, AnetWire.SynBody(AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 1))));
        for (int i = 0; i < 20000; i++)
        {
            var b = new byte[rng.Next(0, 300)];
            rng.NextBytes(b);
            if (b.Length > 1 && i % 2 == 0) { b[0] = (byte)'d'; b[1] = (byte)"%!@*TYUFGBCX"[rng.Next(12)]; }
            r.Read(b);
            AnetSession.Unpack(b.AsSpan(0, Math.Min(8, b.Length)), b.Length > 8 ? b.AsSpan(8) : []);
            I26SessionTag.Read(b);
            AnetWire.ReadRequestOpen(b);
            link.Receive(b, i * 0.001);
        }
        var name = AnetSession.Unpack(new byte[8], [4, 0, 4, 3, (byte)'a', 7, (byte)'b', 0, 0])!.Name;
        Assert.Equal("a?b", name);
    }
}
