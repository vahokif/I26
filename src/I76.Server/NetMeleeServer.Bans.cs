using I76.Net;
using LiteNetLib;

namespace I76.Server;

/// <summary>The host's bans (the listen host's /ban NAME and /unban NAME, i26-server's <c>ban</c> and <c>unban</c>): a boot that also refuses the player's rejoins for the rest of the game.</summary>
public sealed partial class NetMeleeServer
{
    private readonly BanList _bans = new();

    /// <summary>
    /// The host bans a player: <paramref name="who"/> as <see cref="Kick(string)"/> takes it (a slot, a name or a unique start of one).
    /// A player in the game (or a co-op lobby) is booted and refused by name and address from then on; a name not in the game is
    /// refused by name only. Bots are booted, not banned. Returns null when done, else why not.
    /// </summary>
    public string? Ban(string who)
    {
        lock (_lock)
        {
            who = who.Trim();
            if (who.Length == 0) return "Ban: whom? (/ban NAME)";
            (string Name, NetPeer? Peer)? target = null;
            if (_lobbyOpen && FindInLobby(who) is { } m)
            {
                if (m.Peer.Id == _hostPeer) return "Ban: not the host";
                target = (m.Request.Name, m.Peer);
            }
            else if (FindSeat(who) is { } s)
            {
                if (s.Slot == Host.HostSlot) return "Ban: not the host";
                target = (s.Name, s.Connection as NetPeer);
            }
            else if (int.TryParse(who, out _) || Host.Bots.Any(b => b.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase))) return Kick(who);     // a bot, or no such slot
            var (name, peer) = target ?? (who, null);
            if (!_bans.Add(name, peer?.Address) && peer is null)
                return $"Ban: {_bans.Names.First(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))} is banned already";
            Log?.Invoke($"{name} is banned{(peer is null ? " (by name: not in the game)" : $" (name and address {peer.Address})")}");
            Host.Chat(null, $"*** {name} has been banned by the host");
            if (peer is not null) Kick(who);
            return null;
        }
    }

    /// <summary>The host lifts a ban (a name, or a unique start of one). Returns null when done, else why not.</summary>
    public string? Unban(string who)
    {
        lock (_lock)
        {
            if (who.Trim().Length == 0) return "Unban: whom? (/unban NAME)";
            if (_bans.Remove(who) is not { } name) return $"Unban: nobody banned called \"{who.Trim()}\" (or more than one)";
            Log?.Invoke($"{name}'s ban is lifted");
            return null;
        }
    }

    /// <summary>The names banned in this game.</summary>
    public IReadOnlyList<string> Banned { get { lock (_lock) return _bans.Names; } }

    /// <summary>Why a join is turned away for a ban, or null.</summary>
    private string? CheckBan(System.Net.IPAddress from, string name) => _bans.Refuses(name, from) ? Protocol.Banned : null;
}
