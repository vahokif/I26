using I76.Net;
using I76.Server;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>The CB voice chat (protocol 20): codec, packets, jitter buffer, radio filter, and the server's relay rules.</summary>
public class VoiceTests(ITestOutputHelper output)
{
    private const int Rate = VoiceWire.SampleRate, Frame = VoiceWire.FrameSamples;

    private static float[] Tone(double seconds, params (double Hz, double Amp)[] parts)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
            foreach (var (hz, a) in parts) x[i] += (float)(a * Math.Sin(2 * Math.PI * hz * i / Rate));
        return x;
    }

    /// <summary>The amplitude of one frequency in a signal (Goertzel).</summary>
    private static double Level(ReadOnlySpan<float> x, double hz, int rate = Rate)
    {
        double w = 2 * Math.PI * hz / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (float v in x) { double s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) * 2 / x.Length;
    }

    private static List<VoicePacket> Send(float[] pcm)
    {
        var s = new VoiceSender();
        var sent = new List<VoicePacket>();
        s.Feed(pcm, sent.Add);
        s.End(sent.Add);
        return sent;
    }

    /// <summary>The packets arriving one to a 20 ms tick, the listener pulling a frame every tick (as the sound card does), for a while after.</summary>
    private static List<float> Play(VoiceReceiver rx, IReadOnlyList<VoicePacket> arriving, int extra = 20)
    {
        var o = new List<float>();
        var f = new float[Frame];
        for (int i = 0; i < arriving.Count + extra; i++)
        {
            if (i < arriving.Count) rx.Push(arriving[i]);
            if (rx.Pull(0, f, out _) == VoiceReceiver.PullKind.Frame) o.AddRange(f);
        }
        return o;
    }

    [Fact]
    public void OpusCarriesASpeechBandToneAtTwelveKilobitsAndCodesALossAsAGap()
    {
        var pcm = Tone(1.0, (440, 0.3), (1200, 0.2));
        var packets = Send(pcm);
        Assert.True(packets.Count >= 50);
        Assert.All(packets, p => Assert.InRange(p.Opus.Length, 1, VoiceWire.MaxPayload));
        double kbps = packets.Take(50).Sum(p => p.Opus.Length) * 8 / 1000.0;
        output.WriteLine($"{kbps:0.0} kbps over the first second");
        Assert.InRange(kbps, 4, 20);
        var o = Play(new VoiceReceiver(), packets);
        Assert.True(o.Count >= pcm.Length);
        var mid = o.Skip(Rate / 4).Take(Rate / 2).ToArray();
        double a440 = Level(mid, 440), a1200 = Level(mid, 1200), other = Level(mid, 2600);
        output.WriteLine($"440 Hz {a440:0.000}, 1200 Hz {a1200:0.000}, 2600 Hz {other:0.000}");
        Assert.InRange(a440, 0.2, 0.4);
        Assert.InRange(a1200, 0.12, 0.3);
        Assert.True(other < 0.03);
    }

    [Fact]
    public void ThePacketsRoundTripTheWireAndAClientsSlotIsNotTrusted()
    {
        var p = new VoicePacket(0, 65535, VoiceFlags.Start | VoiceFlags.End, [1, 2, 3, 4]);
        var w = new NetWriter();
        p.WriteUp(w);
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.Voice, (MsgType)r.U8());
        var up = VoicePacket.ReadUp(ref r, 7);                    // the server's slot, whatever the client sent
        Assert.Equal((7, 65535, p.Flags), ((int)up.Slot, (int)up.Seq, up.Flags));
        Assert.Equal(p.Opus, up.Opus);
        w.Clear();
        up.WriteDown(w);
        r = new NetReader(w.Span);
        Assert.Equal(MsgType.VoiceFrom, (MsgType)r.U8());
        Assert.Equal(up, VoicePacket.ReadDown(ref r), new PacketComparer());
        // an oversize frame is refused, not relayed
        w.Clear();
        new VoicePacket(0, 1, 0, new byte[VoiceWire.MaxPayload + 1]).WriteUp(w);
        var big = w.ToArray();
        Assert.Throws<WireException>(() => { var rr = new NetReader(big); rr.U8(); VoicePacket.ReadUp(ref rr, 0); });
    }

    private sealed class PacketComparer : IEqualityComparer<VoicePacket>
    {
        public bool Equals(VoicePacket a, VoicePacket b) => (a.Slot, a.Seq, a.Flags) == (b.Slot, b.Seq, b.Flags) && a.Opus.AsSpan().SequenceEqual(b.Opus);
        public int GetHashCode(VoicePacket p) => p.Seq;
    }

    [Fact]
    public void WhoHearsWhoIsEveryoneButTheSenderOrTheSendersTeam()
    {
        Assert.True(VoiceRouting.Hears(false, 0, 1, 1, 2));
        Assert.False(VoiceRouting.Hears(false, 3, 1, 3, 1));        // never the sender's own voice
        Assert.True(VoiceRouting.Hears(true, 0, 2, 5, 2));
        Assert.False(VoiceRouting.Hears(true, 0, 2, 5, 3));         // team play: the team alone
        Assert.False(VoiceRouting.Hears(true, 0, 2, 0, 2));
    }

    [Fact]
    public void TheLimiterLetsThroughTheFrameRateAndCutsAFloodAndALongTalk()
    {
        var l = new VoiceLimiter();
        for (int i = 0; i < 500; i++) Assert.True(l.Allow(i * 0.02, 30, out _));       // a normal talk, 50 frames a second, 10 s
        // a flood: 300 frames in a tenth of a second
        var f = new VoiceLimiter();
        int ok = 0;
        for (int i = 0; i < 300; i++) if (f.Allow(1 + i * 0.0003, 30, out _)) ok++;
        Assert.InRange(ok, 12, 20);
        Assert.False(f.Allow(1.1, VoiceWire.MaxPayload + 1, out _));
        // a talk that does not stop is cut at 30 s, and stays cut while the frames go on, until a pause of a second
        var t = new VoiceLimiter();
        double now = 0;
        for (; now < 29.9; now += 0.02) Assert.True(t.Allow(now, 30, out _));
        bool cut = false, first = false;
        for (; now < 40; now += 0.02) if (!t.Allow(now, 30, out bool fd)) { cut = true; first |= fd; }
        Assert.True(cut && first);
        Assert.False(t.Allow(now, 30, out _));
        Assert.True(t.Allow(now + 1.5, 30, out _));       // a real pause: a new talk
        Assert.True(t.Allow(now + 1.52, 30, out _));
    }

    private static byte[] B(int i) => [(byte)i];

    [Fact]
    public void TheJitterBufferPlaysInOrderWaitsForItsPrebufferAndEndsATalk()
    {
        var j = new JitterBuffer();
        // frames 10..14 arrive out of order; nothing plays before four are held
        j.Push(11, 0, B(11)); j.Push(10, VoiceFlags.Start, B(10));
        Assert.Equal(JitterKind.Wait, j.Pull().Kind);
        j.Push(13, 0, B(13)); j.Push(12, 0, B(12));
        var got = new List<int>();
        var r = j.Pull();
        Assert.True(r.Started);
        got.Add(r.Opus![0]);
        j.Push(14, VoiceFlags.End, B(14));
        for (int i = 0; i < 4; i++) { r = j.Pull(); Assert.Equal(JitterKind.Frame, r.Kind); got.Add(r.Opus![0]); }
        Assert.Equal([10, 11, 12, 13, 14], got);
        Assert.Equal(JitterKind.End, j.Pull().Kind);
        Assert.Equal(JitterKind.Wait, j.Pull().Kind);
        Assert.False(j.Active);
    }

    [Fact]
    public void ALateFrameIsDroppedAGapIsLostWithTheNextOnesRedundancyAndSequencesWrap()
    {
        var j = new JitterBuffer();
        foreach (int s in new[] { 65533, 65534, 0, 2 }) j.Push((ushort)s, 0, B(s & 0xff));       // 65535 (and 1) missing across the wrap
        Assert.Equal(65533 & 0xff, j.Pull().Opus![0]);
        Assert.Equal(65534 & 0xff, j.Pull().Opus![0]);
        var gap = j.Pull();
        Assert.Equal(JitterKind.Lost, gap.Kind);                 // 65535: the next held is 0, with its redundancy
        Assert.Equal(B(0), gap.NextOpus);
        Assert.Equal(0, j.Pull().Opus![0]);
        var gap2 = j.Pull();                                     // 1 is lost; 2 is held
        Assert.Equal((JitterKind.Lost, 2), (gap2.Kind, gap2.NextOpus![0]));
        // 65535 turns up now: too late
        j.Push(65535, 0, B(1));
        Assert.Equal(1, j.Dropped);
        Assert.Equal(2, j.Pull().Opus![0]);
        Assert.Equal(2, j.Lost);
    }

    [Fact]
    public void ATalkWhoseEndWasLostEndsWhenTheFramesStop()
    {
        var j = new JitterBuffer();
        for (int i = 0; i < 6; i++) j.Push((ushort)i, 0, B(i));
        for (int i = 0; i < 6; i++) Assert.Equal(JitterKind.Frame, j.Pull().Kind);
        JitterKind k = default;
        int n = 0;
        while (n++ < 100 && (k = j.Pull().Kind) != JitterKind.End) { }
        Assert.Equal(JitterKind.End, k);
        Assert.InRange(n * VoiceWire.FrameMs, VoiceWire.SilenceSeconds * 1000 - 100, VoiceWire.SilenceSeconds * 1000 + 100);
    }

    [Fact]
    public void AStalledBufferSkipsAheadRatherThanCarryTheDelay()
    {
        var j = new JitterBuffer(prebuffer: 4, maxDepth: 12);
        for (int i = 0; i < 30; i++) j.Push((ushort)i, 0, B(i));
        var r = j.Pull();
        Assert.Equal(JitterKind.Frame, r.Kind);
        Assert.True(r.Opus![0] >= 20, $"played {r.Opus[0]}");
    }

    [Fact]
    public void ATenPercentLossStillComesOutAsAFullLengthTalkAndTheStartAndEndAreMarked()
    {
        var pcm = Tone(2.0, (500, 0.3));
        var packets = Send(pcm);
        var rx = new VoiceReceiver();
        var rng = new Random(5);
        // lose a tenth (never the end); every seventh packet arrives a tick late, behind its successor
        var at = packets.Select((p, i) => (Tick: i % 7 == 0 ? i + 1 : i, P: p)).Where((x, i) => i == packets.Count - 1 || rng.NextDouble() > 0.1).ToList();
        var f = new float[Frame];
        int frames = 0, starts = 0, ends = 0;
        for (int i = 0; i < packets.Count + 20; i++)
        {
            foreach (var x in at.Where(x => x.Tick == i)) rx.Push(x.P);
            switch (rx.Pull(0, f, out bool st))
            {
                case VoiceReceiver.PullKind.Frame: frames++; if (st) starts++; break;
                case VoiceReceiver.PullKind.End: ends++; break;
            }
        }
        output.WriteLine($"{packets.Count} sent, {at.Count} kept, {frames} frames played");
        Assert.Equal((1, 1), (starts, ends));
        Assert.InRange(frames, packets.Count - 3, packets.Count);       // reordered neighbours are inside the buffer, so only the lost ones are concealed
    }

    [Fact]
    public void TheRadioPassesTheBandAndCutsTheRest()
    {
        const int sr = VoiceWire.SampleRate;
        double GainDb(double hz)
        {
            var r = new RadioVoice { Hiss = 0 };
            var x = Tone(1.0, (hz, 0.02));
            r.Process(x);
            return 20 * Math.Log10(Level(x.AsSpan(sr / 2), hz) / 0.02);
        }
        double g1k = GainDb(1000);
        var sweep = new[] { 60, 100, 150, 200, 300, 500, 1000, 2000, 3000, 3500, 3900 }.ToDictionary(f => f, f => GainDb(f) - g1k);
        foreach (var (f, g) in sweep) output.WriteLine($"{f,5} Hz {g,6:0.0} dB");
        Assert.True(sweep[100] < -12 && sweep[60] < -24);
        Assert.InRange(sweep[300], -12, -2);                     // the edges, near -6 dB
        Assert.InRange(sweep[500], -3, 3);
        Assert.InRange(sweep[2000], -4, 3);
        Assert.InRange(sweep[3000], -12, -2);
        Assert.True(sweep[3900] < -10);
        // the compressor evens a loud and a quiet talker out; the soft clip never passes full scale
        var loud = Tone(1.0, (800, 0.9)); var quiet = Tone(1.0, (800, 0.05));
        var rl = new RadioVoice { Hiss = 0 }; var rq = new RadioVoice { Hiss = 0 };
        rl.Process(loud); rq.Process(quiet);
        double ratioIn = 0.9 / 0.05, ratioOut = Level(loud.AsSpan(sr / 2), 800) / Level(quiet.AsSpan(sr / 2), 800);
        output.WriteLine($"level ratio in {ratioIn:0.0}, out {ratioOut:0.0}");
        Assert.True(ratioOut < ratioIn / 2);
        Assert.True(loud.All(v => Math.Abs(v) <= 1f));
    }

    [Fact]
    public void TheRadioHissIsInTheBandAndTheSquelchIsABoundedBurstThatDiesAway()
    {
        var r = new RadioVoice();
        var silence = new float[Rate];
        r.Process(silence);
        double rms = Math.Sqrt(silence.Skip(Rate / 2).Sum(v => v * v) / (Rate / 2));
        Assert.InRange(20 * Math.Log10(rms), -50, -25);
        foreach (bool open in new[] { true, false })
        {
            var s = r.Squelch(open);
            Assert.InRange(s.Length, Rate / 20, Rate / 5);
            Assert.True(s.Max(Math.Abs) is > 0.05f and <= 1f);
            double head = s.Take(s.Length / 4).Sum(v => v * v), tail = s.Skip(3 * s.Length / 4).Sum(v => v * v);
            Assert.True(tail < head);
        }
    }

    [Fact]
    public void TheResamplerKeepsAToneAndKeepsAliasesOut()
    {
        // capture: 48 kHz down to 8 kHz
        var down = new VoiceResampler(48000, 8000);
        var inp = new float[48000];
        for (int i = 0; i < inp.Length; i++) inp[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000) + 0.5 * Math.Sin(2 * Math.PI * 7000 * i / 48000));
        var o = new List<float>();
        down.Process(inp, o);
        Assert.InRange(o.Count, 7990, 8010);
        var mid = o.Skip(2000).Take(4000).ToArray();
        Assert.InRange(Level(mid, 1000), 0.45, 0.55);
        Assert.True(Level(mid, 1000, 8000) > 20 * Level(mid, 3000), "the 7 kHz tone aliased into the band");   // 7 kHz folds to 1 kHz at 8 kHz: it must be filtered first
        // playback: 8 kHz up to 44.1 kHz
        var up = new VoiceResampler(8000, 44100);
        var o2 = new List<float>();
        up.Process(Tone(1.0, (700, 0.5)), o2);
        Assert.InRange(o2.Count, 44090, 44110);
        Assert.InRange(Level(o2.Skip(8000).Take(22050).ToArray(), 700, 44100), 0.45, 0.52);
    }

    [Fact]
    public void ANameTypedInTheMuteCommandMeansTheOnePlayerItFits()
    {
        string[] names = ["Taurus", "Tarantula", "Groove", "groovy Bob", "Jade"];
        Assert.Equal("Taurus", VoiceNames.Match(names, "taurus"));
        Assert.Equal("Tarantula", VoiceNames.Match(names, "tara"));
        Assert.Null(VoiceNames.Match(names, "ta"));              // two
        Assert.Equal("Jade", VoiceNames.Match(names, "ad"));     // a part of one
        Assert.Equal("Groove", VoiceNames.Match(names, "groove"));      // exact wins over "groovy..." starts
        Assert.Null(VoiceNames.Match(names, "nobody"));
    }

    [Fact]
    public void AWavRoundTripsAndEightBitStereoIsMixedToMono()
    {
        var x = Tone(0.1, (440, 0.5));
        var (back, rate) = WavIo.Read(WavIo.Write(x, 8000));
        Assert.Equal((8000, x.Length), (rate, back.Length));
        Assert.True(x.Zip(back).All(p => Math.Abs(p.First - p.Second) < 1e-3));
        // 8-bit stereo: left 128+64, right 128-64 averages to silence
        var raw = new byte[44 + 4];
        WavIo.Write(new float[2], 11025).AsSpan(0, 44).CopyTo(raw);
        BitConverter.GetBytes((short)2).CopyTo(raw, 22);
        BitConverter.GetBytes((short)8).CopyTo(raw, 34);
        BitConverter.GetBytes(4).CopyTo(raw, 40);
        raw[44] = 192; raw[45] = 64; raw[46] = 128; raw[47] = 128;
        var (m, r2) = WavIo.Read(raw);
        Assert.Equal(11025, r2);
        Assert.Equal(2, m.Length);
        Assert.All(m, v => Assert.Equal(0f, v, 3));
    }

    // ---- the server's relay, with real clients (needs the game install) ----

    private static (NetMeleeServer, MeleeClient[]) Table(string install, bool teams, params (string Name, int Team)[] players)
    {
        var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, TeamPlay = teams, Seed = 3 });
        server.Log += m => output_(m);
        server.Start();
        var cars = new[] { "vcgcour1.vcf", "valepre1.vcf", "vppirna1.vcf" };
        var clients = players.Select((p, i) => { var c = new MeleeClient(); c.Connect("127.0.0.1", server.Port, p.Name, cars[i % 3], (byte)p.Team); return c; }).ToArray();
        return (server, clients);
    }
    private static void output_(string _) { }

    private static void Pump(MeleeClient[] cs, int ms, Func<bool>? done = null)
    {
        for (int t = 0; t < ms / 10 && !(done?.Invoke() ?? false); t++) { foreach (var c in cs) c.Poll(); Thread.Sleep(10); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheServerRelaysAVoiceFrameToEveryoneButTheSenderOrToTheSendersTeam(bool teams)
    {
        if (HostTests.InstallDir is not { } install) return;
        var (server, cs) = Table(install, teams, ("Ann", 2), ("Bob", 2), ("Cy", 3));
        try
        {
            using var _ = server;
            Pump(cs, 20000, () => cs.All(c => c.State == ClientState.Joined));
            Assert.All(cs, c => Assert.Equal(ClientState.Joined, c.State));
            var tx = new VoicePacket(200, 41, VoiceFlags.Start, [9, 8, 7]);        // the slot a client claims is ignored
            cs[0].SendVoice(tx);
            Pump(cs, 2000, () => cs[1].VoiceIn.Count > 0 && (teams || cs[2].VoiceIn.Count > 0));
            Pump(cs, 300);
            var got = Assert.Single(cs[1].VoiceIn);
            Assert.Equal((cs[0].Accept!.Slot, (ushort)41, VoiceFlags.Start), (got.Slot, got.Seq, got.Flags));
            Assert.Equal([9, 8, 7], got.Opus);
            Assert.Empty(cs[0].VoiceIn);                                              // never echoed
            Assert.Equal(teams ? 0 : 1, cs[2].VoiceIn.Count);                         // Cy is on the other team
            // a flood is limited: 400 frames at once reach the listener as no more than the burst
            for (int i = 0; i < 400; i++) cs[0].SendVoice(new VoicePacket(0, (ushort)i, 0, new byte[30]));
            Pump(cs, 1500);
            Assert.InRange(cs[1].VoiceIn.Count, 2, 40);
            Assert.True(server.VoiceRefused > 300, $"{server.VoiceRefused} refused");
        }
        finally { foreach (var c in cs) c.Dispose(); }
    }

    private static bool Told(MeleeClient c, string text) => c.Events.ToArray().SelectMany(b => b.Events).OfType<ChatEvent>().Any(e => e.Text.Contains(text));

    [Fact]
    public void TheHostsMuteStopsAPlayersVoiceForEveryoneAndTellsThemAll()
    {
        if (HostTests.InstallDir is not { } install) return;
        var (server, cs) = Table(install, false, ("Ann", 0), ("Bob", 0), ("Cy", 0));
        try
        {
            using var _ = server;
            Pump(cs, 20000, () => cs.All(c => c.State == ClientState.Joined));
            Assert.All(cs, c => Assert.Equal(ClientState.Joined, c.State));
            cs[0].SendVoice(new VoicePacket(0, 1, VoiceFlags.Start, [1]));
            Pump(cs, 2000, () => cs[1].VoiceIn.Count > 0 && cs[2].VoiceIn.Count > 0);
            Assert.Single(cs[1].VoiceIn);
            cs[1].VoiceIn.Clear(); cs[2].VoiceIn.Clear();
            long relayed = server.VoiceRelayed;

            // names are matched by a unique start or part, case does not matter; a name nobody has, or several, is refused
            Assert.Contains("no player", server.CbMute("zed", true));
            Assert.NotNull(server.CbMute("", true));
            Assert.Null(server.CbMute("AN", true));
            Assert.Contains("already muted", server.CbMute("ann", true));
            Assert.Equal(["Ann"], server.CbMuted);
            Pump(cs, 2000, () => cs[0].CbMuted && Told(cs[1], "Ann muted for everyone"));
            Assert.True(cs[0].CbMuted);                                                // the muted player is told
            Assert.False(cs[1].CbMuted);
            Assert.True(Told(cs[0], "Ann muted for everyone") && Told(cs[1], "Ann muted for everyone") && Told(cs[2], "Ann muted for everyone"));   // and so is everybody, the muted player too
            cs[0].SendVoice(new VoicePacket(0, 2, VoiceFlags.Start, [2]));
            Pump(cs, 800);
            Assert.Empty(cs[1].VoiceIn);
            Assert.Empty(cs[2].VoiceIn);
            Assert.Equal(relayed, server.VoiceRelayed);

            // the others still talk
            cs[1].SendVoice(new VoicePacket(0, 3, VoiceFlags.Start, [3]));
            Pump(cs, 2000, () => cs[2].VoiceIn.Count > 0 && cs[0].VoiceIn.Count > 0);
            Assert.Single(cs[0].VoiceIn);

            // held by name: leaving and coming back does not undo it
            cs[0].Dispose();
            Pump(cs[1..], 1000);
            var back = new MeleeClient();
            back.Connect("127.0.0.1", server.Port, "Ann", "vcgcour1.vcf", 0);
            cs[0] = back;
            Pump(cs, 20000, () => back.State == ClientState.Joined && back.CbMuted);
            Assert.True(back.CbMuted);

            // unmuting (the muted may be named even while away) lets the voice through again
            Assert.Null(server.CbMute("ann", false));
            Assert.Contains("is not muted", server.CbMute("ann", false));
            Assert.Empty(server.CbMuted);
            Pump(cs, 2000, () => !back.CbMuted && Told(cs[1], "Ann unmuted"));
            Assert.False(back.CbMuted);
            cs[1].VoiceIn.Clear();
            back.SendVoice(new VoicePacket(0, 4, VoiceFlags.Start, [4]));
            Pump(cs, 2000, () => cs[1].VoiceIn.Count > 0);
            Assert.Single(cs[1].VoiceIn);
        }
        finally { foreach (var c in cs) c.Dispose(); }
    }

    [Fact]
    public void APlayerWhoIgnoresAnotherIsNotSentTheirVoice()
    {
        if (HostTests.InstallDir is not { } install) return;
        var (server, cs) = Table(install, false, ("Ann", 0), ("Bob", 0), ("Cy", 0));
        try
        {
            using var _ = server;
            Pump(cs, 20000, () => cs.All(c => c.State == ClientState.Joined));
            Assert.All(cs, c => Assert.Equal(ClientState.Joined, c.State));
            cs[1].SendVoiceIgnore(false, [cs[0].Accept!.Slot]);                     // Bob mutes Ann for himself
            Pump(cs, 500);
            cs[0].SendVoice(new VoicePacket(0, 1, VoiceFlags.Start, [1]));
            Pump(cs, 2000, () => cs[2].VoiceIn.Count > 0);
            Pump(cs, 300);
            Assert.Single(cs[2].VoiceIn);                                           // Cy hears her
            Assert.Empty(cs[1].VoiceIn);                                            // Bob is not sent it
            Assert.Empty(server.CbMuted);                                           // nothing was said to the others
            // CB off for Cy: nothing of anyone
            cs[2].VoiceIn.Clear();
            cs[2].SendVoiceIgnore(true, []);
            Pump(cs, 500);
            cs[0].SendVoice(new VoicePacket(0, 2, VoiceFlags.None, [2]));
            cs[1].SendVoice(new VoicePacket(0, 3, VoiceFlags.None, [3]));
            Pump(cs, 1500, () => cs[0].VoiceIn.Count > 0);
            Pump(cs, 300);
            Assert.Empty(cs[2].VoiceIn);
            Assert.Single(cs[0].VoiceIn);                                           // Ann hears Bob
            // and a list that is lifted lets them through again
            cs[1].SendVoiceIgnore(false, []);
            Pump(cs, 500);
            cs[0].SendVoice(new VoicePacket(0, 4, VoiceFlags.None, [4]));
            Pump(cs, 2000, () => cs[1].VoiceIn.Count > 0);
            Assert.Single(cs[1].VoiceIn);
        }
        finally { foreach (var c in cs) c.Dispose(); }
    }

    [Fact]
    public void AGameWithTheCbRadioOffRelaysNoVoiceAndTellsItsPlayers()
    {
        if (HostTests.InstallDir is not { } install) return;
        var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, Seed = 3, AllowCb = false });
        server.Start();
        var cs = new[] { "Ann", "Bob" }.Select(n => { var c = new MeleeClient(); c.Connect("127.0.0.1", server.Port, n, "vcgcour1.vcf", 0); return c; }).ToArray();
        try
        {
            using var _ = server;
            Pump(cs, 20000, () => cs.All(c => c.State == ClientState.Joined));
            Assert.All(cs, c => { Assert.Equal(ClientState.Joined, c.State); Assert.False(c.Accept!.Cb); Assert.False(c.CbAllowed); });
            Assert.Equal("CB is off in this game", server.CbMute("Ann", true));
            Assert.Equal(0, server.VoiceRelayed);
            Assert.False(server.Host.Info().Cb);
            // a client of this build does not send; one that does is not relayed (the server's own rule)
            cs[0].SendVoice(new VoicePacket(0, 1, VoiceFlags.Start, [1]));
            Pump(cs, 500);
            Assert.Empty(cs[1].VoiceIn);
        }
        finally { foreach (var c in cs) c.Dispose(); }
    }

    [Fact]
    public void ASuccessorKeepsTheHostsMutes()
    {
        if (HostTests.InstallDir is not { } install) return;
        var o = new ServerOptions { Install = install, Arena = "m01", Port = 0, Seed = 3 };
        var host = new MeleeHost(o);
        host.CbMuted.Add("Ann");
        host.CbMuted.Add("Bob");
        var w = new NetWriter();
        host.WriteMigrationExtras(w);
        var fresh = new MeleeHost(o);
        var r = new NetReader(w.ToArray());
        fresh.ReadMigrationExtras(ref r);
        Assert.Equal(["Ann", "Bob"], fresh.CbMuted.Order().ToArray());
    }

    [Fact]
    public void TheIgnoreListAndTheAcceptRoundTrip()
    {
        var w = new NetWriter();
        new VoiceIgnore(true, [3, 1]).Write(w);
        var r = new NetReader(w.Span);
        Assert.Equal(MsgType.VoiceIgnore, (MsgType)r.U8());
        var back = VoiceIgnore.Read(ref r);
        Assert.True(back.All);
        Assert.Equal<byte>([3, 1], back.Slots);
    }
}
