using System.Net;

namespace I76.Server;

/// <summary>
/// The host's bans for the rest of a game (the port's, #117; the original only boots). A ban holds the player's name (case does
/// not matter) and, when the player was connected, the address they joined from: a join is refused when either matches. The
/// identity is what the server has, so it has limits: a player can come back under another name from another address (a new
/// connection, a VPN), and everyone behind one public address (a household, a campus) is refused with them. A loopback
/// address (players on the server's own machine) is never held, as it is every local player's.
/// </summary>
public sealed class BanList
{
    private readonly List<(string Name, IPAddress? Address)> _bans = [];

    /// <summary>The banned names, in the order they were banned.</summary>
    public IReadOnlyList<string> Names => [.. _bans.Select(b => b.Name)];

    public int Count => _bans.Count;

    /// <summary>Bans a name, and the address it joined from (none: by name only). Returns false if that name was banned already (its address is then added).</summary>
    public bool Add(string name, IPAddress? address)
    {
        if (address is not null && IPAddress.IsLoopback(address)) address = null;
        int i = _bans.FindIndex(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            if (address is not null && _bans[i].Address is null) _bans[i] = (_bans[i].Name, address);
            return false;
        }
        _bans.Add((name, address));
        return true;
    }

    /// <summary>Lifts the ban on a name (exact, else a unique start of one, case does not matter); returns the name lifted, or null.</summary>
    public string? Remove(string who)
    {
        who = who.Trim();
        int i = _bans.FindIndex(b => b.Name.Equals(who, StringComparison.OrdinalIgnoreCase));
        if (i < 0 && _bans.Select((b, k) => (b, k)).Where(x => x.b.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one) i = one[0].k;
        if (i < 0 || who.Length == 0) return null;
        var name = _bans[i].Name;
        _bans.RemoveAt(i);
        return name;
    }

    /// <summary>Whether a join under this name from this address is refused.</summary>
    public bool Refuses(string name, IPAddress? from)
    {
        if (from is not null && from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        foreach (var (n, a) in _bans)
        {
            if (n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            if (a is not null && from is not null && (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(from)) return true;
        }
        return false;
    }
}
