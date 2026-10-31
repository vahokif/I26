using Concentus;
using Concentus.Enums;

namespace I76.Net;

/// <summary>The CB voice's encoder: Opus (Concentus), 8 kHz mono, 20 ms frames, VOIP at <see cref="VoiceWire.Bitrate"/> with in-band redundancy for a lost packet.</summary>
public sealed class VoiceEncoder
{
    private readonly IOpusEncoder _enc;

    public VoiceEncoder()
    {
        _enc = OpusCodecFactory.CreateEncoder(VoiceWire.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _enc.Bitrate = VoiceWire.Bitrate;
        _enc.UseVBR = true;
        _enc.UseConstrainedVBR = true;
        _enc.UseInbandFEC = true;
        _enc.PacketLossPercent = VoiceWire.ExpectedLossPercent;
        _enc.Complexity = 5;
        _enc.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
    }

    private readonly byte[] _out = new byte[VoiceWire.MaxPayload];

    /// <summary>One frame of <see cref="VoiceWire.FrameSamples"/> samples (−1..1) as an Opus packet.</summary>
    public byte[] Encode(ReadOnlySpan<float> frame)
    {
        if (frame.Length != VoiceWire.FrameSamples) throw new ArgumentException($"a voice frame is {VoiceWire.FrameSamples} samples");
        Span<short> pcm = stackalloc short[VoiceWire.FrameSamples];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(MathF.Round(frame[i] * 32767f), -32768f, 32767f);
        int n = _enc.Encode(pcm, VoiceWire.FrameSamples, _out, _out.Length);
        return _out.AsSpan(0, n).ToArray();
    }
}

/// <summary>The CB voice's decoder, one to a talker: a frame, or the one before it from the redundancy in the next (a lost packet), or a guess at a lost one (PLC).</summary>
public sealed class VoiceDecoder
{
    private readonly IOpusDecoder _dec = OpusCodecFactory.CreateDecoder(VoiceWire.SampleRate, 1);
    private readonly short[] _pcm = new short[VoiceWire.FrameSamples];

    /// <summary>
    /// Decodes <paramref name="opus"/> into <paramref name="frame"/> (<see cref="VoiceWire.FrameSamples"/> samples). With
    /// <paramref name="fec"/> the packet is the NEXT one, and what comes out is the frame lost before it (its redundancy); with a null packet the
    /// decoder conceals the loss.
    /// </summary>
    public void Decode(byte[]? opus, bool fec, Span<float> frame)
    {
        try
        {
            if (opus is null) _dec.Decode(ReadOnlySpan<byte>.Empty, _pcm, VoiceWire.FrameSamples, false);
            else _dec.Decode(opus, _pcm, VoiceWire.FrameSamples, fec);
        }
        catch (OpusException)
        {
            // a damaged packet is a lost one
            if (opus is not null) { Decode(null, false, frame); return; }
            frame.Clear();
            return;
        }
        for (int i = 0; i < frame.Length; i++) frame[i] = _pcm[i] / 32768f;
    }
}

public enum JitterKind
{
    /// <summary>Nothing to play (idle, or the talk's first frames are still arriving).</summary>
    Wait,
    /// <summary>The next frame, in order.</summary>
    Frame,
    /// <summary>The next frame never came: <see cref="JitterResult.NextOpus"/> carries its redundancy when the frame after it did.</summary>
    Lost,
    /// <summary>The talk is over (its end frame played, or the frames stopped for <see cref="VoiceWire.SilenceSeconds"/>).</summary>
    End,
}

public readonly record struct JitterResult(JitterKind Kind, byte[]? Opus = null, byte[]? NextOpus = null, bool Started = false);

/// <summary>
/// One talker's jitter buffer. Frames are pushed as they arrive (any order, duplicates ignored) and pulled once every 20 ms of playing;
/// a talk starts playing when <see cref="VoiceWire.JitterFrames"/> frames are held (or its end has come), the frames come out in order,
/// a frame later than its turn is dropped, a gap is reported as lost (with the next frame's redundancy when it is held), and a buffer that
/// has grown too deep skips ahead rather than carry the delay.
/// </summary>
public sealed class JitterBuffer
{
    private readonly SortedDictionary<long, byte[]> _held = [];
    private readonly int _prebuffer, _maxDepth, _silenceFrames;
    private long _next;               // the sequence (unwrapped) to play next
    private long _newest;             // the highest sequence seen
    private long _end = long.MaxValue; // the end frame's sequence, once seen
    private bool _active, _playing, _started;
    private int _starved;

    /// <summary>Frames that came too late to play, or twice.</summary>
    public int Dropped { get; private set; }
    /// <summary>Frames played as lost (concealed or recovered from the next).</summary>
    public int Lost { get; private set; }

    public JitterBuffer(int prebuffer = VoiceWire.JitterFrames, int maxDepth = 12)
    {
        _prebuffer = prebuffer;
        _maxDepth = maxDepth;
        _silenceFrames = (int)(VoiceWire.SilenceSeconds * 1000 / VoiceWire.FrameMs);
    }

    public bool Active => _active;
    public int Depth => _held.Count;

    /// <summary>The sequence number as a count that does not wrap, nearest to the newest seen.</summary>
    private long Unwrap(ushort seq)
    {
        if (!_active) return seq;
        long baseSeq = _newest & ~0xffffL;
        long c = baseSeq | seq;
        if (c - _newest > 0x8000) c -= 0x10000;
        else if (_newest - c > 0x8000) c += 0x10000;
        return c;
    }

    public void Push(ushort seq, VoiceFlags flags, byte[] opus)
    {
        long n = Unwrap(seq);
        if (!_active)
        {
            // a late frame of a talk that ended is not a new talk; a new one begins with any frame once idle
            _active = true;
            _playing = false;
            _started = false;
            _next = n;
            _newest = n;
            _end = long.MaxValue;
            _starved = 0;
            _held.Clear();
        }
        // before the talk starts playing an earlier frame can still pull its start back
        if (!_playing && n < _next) _next = n;
        if (n < _next || _held.ContainsKey(n)) { Dropped++; return; }
        if (n > _newest) _newest = n;
        _held[n] = opus;
        if ((flags & VoiceFlags.End) != 0) _end = n;
    }

    /// <summary>The next 20 ms.</summary>
    public JitterResult Pull()
    {
        if (!_active) return new(JitterKind.Wait);
        if (!_playing)
        {
            if (_held.Count > 0 && _held.Keys.First() < _next) _next = _held.Keys.First();
            if (_held.Count < _prebuffer && !(_end != long.MaxValue && _held.Count > 0 && _held.ContainsKey(_end)))
            {
                // a talk whose frames stop before the buffer fills (a short squawk, the rest lost) must not hang
                if (++_starved > _silenceFrames) Reset();
                return new(JitterKind.Wait);
            }
            _playing = true;
            _starved = 0;
        }
        // too deep: a stall caught up with, play from near the newest rather than carry the delay
        if (_newest - _next >= _maxDepth)
        {
            long to = _newest - _prebuffer + 1;
            foreach (var k in _held.Keys.Where(k => k < to).ToList()) { _held.Remove(k); Dropped++; }
            _next = to;
        }
        if (_next > _end) { Reset(); return new(JitterKind.End); }
        bool first = !_started;
        if (_held.Remove(_next, out var frame))
        {
            _next++;
            _started = true;
            _starved = 0;
            return new(JitterKind.Frame, frame, Started: first);
        }
        // not here: the talk's end already passed, or a gap, or the sender went quiet
        if (_held.Count == 0 && _newest < _next)
        {
            if (++_starved > _silenceFrames) { Reset(); return new(JitterKind.End); }
            // a late frame: hold the place a frame or two rather than conceal at once
            return _starved <= 2 ? new(JitterKind.Wait) : Conceal(null);
        }
        _held.TryGetValue(_next + 1, out var after);
        return Conceal(after);
    }

    private JitterResult Conceal(byte[]? after)
    {
        _next++;
        Lost++;
        _started = true;
        return new(JitterKind.Lost, null, after);
    }

    private void Reset()
    {
        _active = _playing = false;
        _held.Clear();
    }
}
