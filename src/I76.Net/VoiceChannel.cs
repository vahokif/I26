namespace I76.Net;

/// <summary>
/// The sending end of the CB voice, with no sound card in it: mono samples at the voice rate go in, Opus packets come out. A talk is
/// the key held: its first frame is marked Start and its last End (a final packet of its own, so the receiver closes the squelch at once).
/// </summary>
public sealed class VoiceSender
{
    private readonly VoiceEncoder _enc = new();
    private readonly float[] _frame = new float[VoiceWire.FrameSamples];
    private int _fill;
    private ushort _seq;
    private bool _talking;

    public bool Talking => _talking;

    /// <summary>Samples while the key is down; each full frame goes to <paramref name="send"/> as a packet (the slot is the server's to fill).</summary>
    public void Feed(ReadOnlySpan<float> samples, Action<VoicePacket> send)
    {
        foreach (float s in samples)
        {
            _frame[_fill++] = s;
            if (_fill < _frame.Length) continue;
            _fill = 0;
            send(new VoicePacket(0, _seq++, _talking ? VoiceFlags.None : VoiceFlags.Start, _enc.Encode(_frame)));
            _talking = true;
        }
    }

    /// <summary>The key came up: the rest of the frame (padded with silence) and the end mark.</summary>
    public void End(Action<VoicePacket> send)
    {
        if (!_talking && _fill == 0) return;
        if (_fill > 0)
        {
            Array.Clear(_frame, _fill, _frame.Length - _fill);
            _fill = 0;
            send(new VoicePacket(0, _seq++, _talking ? VoiceFlags.None : VoiceFlags.Start, _enc.Encode(_frame)));
            _talking = true;
        }
        // the end frame: silence, so the packet is small, with the end mark on it
        Array.Clear(_frame);
        send(new VoicePacket(0, _seq++, VoiceFlags.End, _enc.Encode(_frame)));
        _talking = false;
    }
}

/// <summary>
/// The receiving end: packets by sender slot in, 8 kHz mono PCM out one 20 ms frame at a time, each talker with a jitter buffer and a
/// decoder. <see cref="Pull"/> reports what a talker's next frame is: sound, the talk's start (the squelch opening) or its end.
/// </summary>
public sealed class VoiceReceiver
{
    private sealed class Talker
    {
        public readonly JitterBuffer Jitter = new();
        public readonly VoiceDecoder Decoder = new();
        public bool Open;
    }

    private readonly Dictionary<byte, Talker> _talkers = [];

    public IReadOnlyCollection<byte> Slots => _talkers.Keys;

    public void Push(in VoicePacket p)
    {
        if (!_talkers.TryGetValue(p.Slot, out var t)) _talkers[p.Slot] = t = new Talker();
        t.Jitter.Push(p.Seq, p.Flags, p.Opus);
    }

    public enum PullKind { None, Frame, End }

    /// <summary>The next 20 ms of a talker; <paramref name="started"/> is set on the first frame of a talk.</summary>
    public PullKind Pull(byte slot, Span<float> frame, out bool started)
    {
        started = false;
        if (!_talkers.TryGetValue(slot, out var t)) return PullKind.None;
        var r = t.Jitter.Pull();
        switch (r.Kind)
        {
            case JitterKind.Frame:
                t.Decoder.Decode(r.Opus, false, frame);
                started = r.Started;
                t.Open = true;
                return PullKind.Frame;
            case JitterKind.Lost:
                // the redundancy in the frame after, or a guess
                if (r.NextOpus is not null) t.Decoder.Decode(r.NextOpus, true, frame);
                else t.Decoder.Decode(null, false, frame);
                t.Open = true;
                return PullKind.Frame;
            case JitterKind.End:
                bool was = t.Open;
                t.Open = false;
                return was ? PullKind.End : PullKind.None;
            default:
                return PullKind.None;
        }
    }

    public void Forget(byte slot) => _talkers.Remove(slot);
}
