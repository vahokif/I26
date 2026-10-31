namespace I76.Net;

/// <summary>
/// The CB voice chat's wire and rules (protocol 20): push-to-talk voice over the game's unreliable channel, one Opus frame to a packet.
/// The client sends <see cref="MsgType.Voice"/> to the server, which relays it as <see cref="MsgType.VoiceFrom"/> (the sender's slot
/// added) to the players who may hear it, without decoding; nothing goes back to the sender.
/// </summary>
public static class VoiceWire
{
    /// <summary>Narrowband: the radio's band is 300..3000 Hz, and Opus codes 8 kHz speech in about 12 kbps.</summary>
    public const int SampleRate = 8000;
    /// <summary>One frame is 20 ms: 160 samples.</summary>
    public const int FrameMs = 20, FrameSamples = SampleRate * FrameMs / 1000;
    /// <summary>The encoder's target (VOIP, constrained to this) and the loss it expects, for its in-band redundancy.</summary>
    public const int Bitrate = 12000, ExpectedLossPercent = 10;
    /// <summary>The longest payload the server relays (a 20 ms frame at 12 kbps is about 30 bytes; the encoder is capped well below this).</summary>
    public const int MaxPayload = 120;
    /// <summary>The receiver holds this many frames before it starts playing a talk, to put a jittery link's frames in order.</summary>
    public const int JitterFrames = 4;
    /// <summary>A talk that goes this long without a frame is over (the end packet was lost).</summary>
    public const double SilenceSeconds = 0.4;
}

[Flags]
public enum VoiceFlags : byte
{
    None = 0,
    /// <summary>The first frame of a talk (the key went down).</summary>
    Start = 1,
    /// <summary>The last frame of a talk (the key came up; it may carry no sound).</summary>
    End = 2,
}

/// <summary>One CB voice frame. <see cref="Slot"/> is the sender's seat: the server writes it into what it relays and ignores what a client puts there.</summary>
public readonly record struct VoicePacket(byte Slot, ushort Seq, VoiceFlags Flags, byte[] Opus)
{
    /// <summary>The client's packet: type, sequence, flags, then the frame to the end of the datagram.</summary>
    public void WriteUp(NetWriter w)
    {
        w.U8((byte)MsgType.Voice);
        w.U16(Seq);
        w.U8((byte)Flags);
        w.Bytes(Opus);
    }

    /// <summary>The server's relay: type, the sender's slot, sequence, flags, the frame.</summary>
    public void WriteDown(NetWriter w)
    {
        w.U8((byte)MsgType.VoiceFrom);
        w.U8(Slot);
        w.U16(Seq);
        w.U8((byte)Flags);
        w.Bytes(Opus);
    }

    /// <summary>A client's packet, after the type byte.</summary>
    public static VoicePacket ReadUp(ref NetReader r, byte slot)
    {
        ushort seq = r.U16();
        var flags = (VoiceFlags)r.U8();
        int n = r.Remaining;
        if (n > VoiceWire.MaxPayload) throw new WireException($"voice frame of {n} bytes");
        return new VoicePacket(slot, seq, flags & (VoiceFlags.Start | VoiceFlags.End), r.Bytes(n).ToArray());
    }

    /// <summary>The server's relay, after the type byte.</summary>
    public static VoicePacket ReadDown(ref NetReader r)
    {
        byte slot = r.U8();
        ushort seq = r.U16();
        var flags = (VoiceFlags)r.U8();
        int n = r.Remaining;
        if (n > VoiceWire.MaxPayload) throw new WireException($"voice frame of {n} bytes");
        return new VoicePacket(slot, seq, flags & (VoiceFlags.Start | VoiceFlags.End), r.Bytes(n).ToArray());
    }
}

/// <summary>
/// Who hears a player's voice: everyone but the sender in a free-for-all and in co-op, the sender's own team in team play (CB is a
/// shared band; a team game keeps its plans to itself). Bots are not seats and never talk.
/// </summary>
public static class VoiceRouting
{
    public static bool Hears(bool teamPlay, int senderSlot, int senderTeam, int listenerSlot, int listenerTeam) =>
        listenerSlot != senderSlot && (!teamPlay || listenerTeam == senderTeam);
}

public static class VoiceNames
{
    /// <summary>The one name a typed word means: exact, else the only name it starts, else the only one it is in (case does not matter); null for none or several.</summary>
    public static string? Match(IEnumerable<string> names, string typed)
    {
        var all = names.ToList();
        var exact = all.Where(n => n.Equals(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var starts = all.Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count == 1) return starts[0];
        var inside = all.Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return inside.Count == 1 ? inside[0] : null;
    }
}

/// <summary>
/// The server's limit on one player's voice: a steady 50 frames a second (the 20 ms frames' own rate) with a small burst for a
/// bunched-up link, a talk cut off after <see cref="MaxTalkSeconds"/> of transmission without a pause of <see cref="PauseSeconds"/>
/// (a stuck key or a held-open client cannot hold the band), and a frame longer than <see cref="VoiceWire.MaxPayload"/> refused.
/// Server time throughout.
/// </summary>
public sealed class VoiceLimiter
{
    public const double FramesPerSecond = 60;
    public const double Burst = 12;
    public const double MaxTalkSeconds = 30, PauseSeconds = 1.0;
    private double _credit = Burst, _last = double.NegativeInfinity, _talkStart = double.NegativeInfinity, _lastFrame = double.NegativeInfinity;
    private bool _dropping;

    /// <summary>Whether a frame of <paramref name="payload"/> bytes arriving at <paramref name="now"/> (seconds) is relayed; <paramref name="firstDrop"/> is set on the first drop of a burst, for the log.</summary>
    public bool Allow(double now, int payload, out bool firstDrop)
    {
        firstDrop = false;
        if (_last > double.NegativeInfinity) _credit = Math.Min(Burst, _credit + (now - _last) * FramesPerSecond);
        _last = now;
        if (now - _lastFrame >= PauseSeconds) _talkStart = now;
        bool ok = payload <= VoiceWire.MaxPayload && _credit >= 1 && now - _talkStart <= MaxTalkSeconds;
        if (ok) _credit -= 1;
        // a frame refused still counts as talk: a client that goes on sending stays cut until it really pauses
        _lastFrame = now;
        if (!ok) { firstDrop = !_dropping; _dropping = true; }
        else _dropping = false;
        return ok;
    }
}

/// <summary>
/// The players a client does not want to hear on the CB (protocol 20, <see cref="MsgType.VoiceIgnore"/>, reliable): <c>All</c> (CB off
/// for this client) or their slots. The server does not relay their voice to this client, so a /mute also saves the bandwidth.
/// </summary>
public sealed record VoiceIgnore(bool All, IReadOnlyList<byte> Slots)
{
    public const int MaxSlots = 64;

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.VoiceIgnore);
        w.Bool(All);
        var s = Slots.Take(MaxSlots).ToList();
        w.U8((byte)s.Count);
        foreach (var x in s) w.U8(x);
    }

    /// <summary>Reads the body (the type byte already read).</summary>
    public static VoiceIgnore Read(ref NetReader r)
    {
        bool all = r.Bool();
        int n = r.U8();
        if (n > MaxSlots) throw new WireException($"{n} ignored voices");
        var slots = new byte[n];
        for (int i = 0; i < n; i++) slots[i] = r.U8();
        return new VoiceIgnore(all, slots);
    }
}
