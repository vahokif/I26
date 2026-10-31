using I76.Net;
using LiteNetLib;

namespace I76.Server;

/// <summary>
/// The CB voice chat's relay (protocol 20, docs/multiplayer.md "CB voice"): a seat's voice frame goes, undecoded and unreliable, to every
/// other seat that may hear it (<see cref="VoiceRouting"/>): all of them in a free-for-all and in co-op, the sender's team in team play.
/// A seat's frames are limited (<see cref="VoiceLimiter"/>); the sender's slot is the server's, whatever the client wrote.
/// </summary>
public sealed partial class NetMeleeServer
{
    private readonly NetWriter _voice = new(VoiceWire.MaxPayload + 8);
    /// <summary>Frames relayed (one count per listener), and frames refused by the rate limit.</summary>
    public long VoiceRelayed, VoiceRefused;

    private void RelayVoice(Seat from, VoicePacket p)
    {
        // the host turned the CB off for the game, or muted this player: nothing is relayed (and no limiter state is spent)
        if (!Options.AllowCb || Host.CbMuted.Contains(from.Name)) return;
        if (!from.VoiceRate.Allow(Now, p.Opus.Length, out bool first))
        {
            VoiceRefused++;
            if (first) Log?.Invoke($"voice: {from.Name} is talking too much; the excess is dropped");
            return;
        }
        bool teams = Host.Mode.Rules.TeamPlay;
        _voice.Clear();
        p.WriteDown(_voice);
        foreach (var to in Host.Seats)
        {
            if (to.Connection is not NetPeer peer || !VoiceRouting.Hears(teams, from.Slot, from.Actor.Team, to.Slot, to.Actor.Team)) continue;
            if (to.IgnoreAll || to.Ignored.Contains(from.Slot)) continue;
            peer.Send(_voice.Span, DeliveryMethod.Unreliable);
            VoiceRelayed++;
        }
    }

    /// <summary>A client's list of the players it does not want to hear: they are no longer relayed to it.</summary>
    private static void SetIgnored(Seat s, VoiceIgnore v)
    {
        s.IgnoreAll = v.All;
        s.Ignored.Clear();
        foreach (var x in v.Slots) s.Ignored.Add(x);
    }

    /// <summary>
    /// The host mutes (or unmutes) a player's CB for everyone (the listen host's /cbmute NAME and /cbunmute NAME, i26-server's
    /// <c>cbmute</c> and <c>cbunmute</c>): <paramref name="who"/> is a name, or a unique start or part of one, case does not matter. The
    /// server stops relaying that player's voice, everyone is told in the chat, and the player's own screen says so. Held by name, so it
    /// survives the player leaving and coming back, and a host migration. Returns null when done, else why not.
    /// </summary>
    public string? CbMute(string who, bool mute)
    {
        lock (_lock)
        {
            who = who.Trim();
            if (who.Length == 0) return mute ? "CB mute: whom? (/cbmute NAME)" : "CB unmute: whom? (/cbunmute NAME)";
            if (!Options.AllowCb) return "CB is off in this game";
            var names = Host.Seats.Where(x => x.Slot != Host.HostSlot).Select(x => x.Name).ToList();
            if (!mute) names.AddRange(Host.CbMuted.Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)));
            if (VoiceNames.Match(names, who) is not { } name)
                return $"CB: no player called \"{who}\" (or more than one)";
            if (mute == Host.CbMuted.Contains(name)) return mute ? $"CB: {name} is already muted" : $"CB: {name} is not muted";
            if (mute) Host.CbMuted.Add(name); else Host.CbMuted.Remove(name);
            foreach (var s in Host.Seats.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) TellMuted(s, mute);
            Host.Chat(null, mute ? $"CB: {name} muted for everyone by the host" : $"CB: {name} unmuted by the host");
            Log?.Invoke($"CB: {name} was {(mute ? "muted" : "unmuted")} for everyone");
            return null;
        }
    }

    /// <summary>The players muted for everyone now.</summary>
    public IReadOnlyList<string> CbMuted { get { lock (_lock) return [.. Host.CbMuted.Order(StringComparer.OrdinalIgnoreCase)]; } }

    /// <summary>A muted player is told (their key does nothing and their screen says so); a seat that just came is told too if it was muted before.</summary>
    private void TellMuted(Seat s, bool muted)
    {
        if (s.Connection is not NetPeer peer) return;
        _msg.Clear();
        _msg.U8((byte)MsgType.VoiceMuted);
        _msg.Bool(muted);
        peer.Send(_msg.Span, DeliveryMethod.ReliableOrdered);
    }
}
