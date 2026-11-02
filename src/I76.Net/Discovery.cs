using System.Diagnostics;
using System.Net;
using I76.Net.Anet;
using LiteNetLib;
using LiteNetLib.Layers;

namespace I76.Net;

/// <summary>One driver in a game as the session list shows it: name, car file and team (0 without team play).</summary>
public sealed record SessionDriver(string Name, string Car, byte Team);

/// <summary>
/// What a server says about its game to anyone who asks (LAN discovery, or
/// a query to an address from the address book): the join form's list row
/// ("name (n/max)") and the session popup (bank 36 sprite 13: game name,
/// type, score limit, teams, area of play, and each driver's car and team).
/// </summary>
public sealed record SessionInfo(ulong Id, ushort Version, string Name, string Arena, int Players, int MaxPlayers,
    bool TeamPlay, bool InviteOnly, int MaxScore, float TimeLimit, bool Over, IReadOnlyList<SessionDriver> Drivers,
    I76.Sim.Missions.MeleeKind Kind = I76.Sim.Missions.MeleeKind.Combat, I76.Sim.Missions.MeleeGoal Goal = I76.Sim.Missions.MeleeGoal.None, int GoalValue = 0, byte Teams = 2,
    byte[]? MapHash = null, bool MapShared = false, uint MapSize = 0, bool Locked = false,
    I76.Sim.Trip.WeaponRestrictions? Restrictions = null, bool Hidden = false, bool HiddenKnown = true, bool Coop = false, byte RateHz = 0, bool Lobby = false, bool Turrets = false, bool CarReset = false, bool Cb = true, bool Crosshair = false)
{
    /// <summary>
    /// Whether a joiner's own check lets hidden cars and weapons through: the game allows them, or it does not say
    /// (<c>HiddenKnown</c> false: a listing alone from a 0.5.0 host that has not answered directly), and the host decides.
    /// </summary>
    public bool MayBringHidden => Hidden || !HiddenKnown;

    /// <summary>The weapons the game does not allow (protocol 12; docs/hidden-content.md), none when the host set none.</summary>
    public I76.Sim.Trip.WeaponRestrictions Restricted => Restrictions ?? I76.Sim.Trip.WeaponRestrictions.None;

    /// <summary>The arena is a custom map (its hash is known): a player without it downloads it on joining if <see cref="MapShared"/>.</summary>
    public bool CustomMap => MapHash is { Length: 32 };

    public void Write(NetWriter w)
    {
        w.Str(Discovery.InfoMagic);
        w.U16(Version);
        w.U32((uint)Id); w.U32((uint)(Id >> 32));
        w.Str(Clip(Name, Protocol.MaxName));
        w.Str(Arena);
        w.U8((byte)Players);
        w.U8((byte)MaxPlayers);
        w.Bool(TeamPlay);
        w.Bool(InviteOnly);
        w.VarU((uint)Math.Max(0, MaxScore));
        w.F32(TimeLimit);
        w.Bool(Over);
        // the tail (after the drivers) is written first to know its size: the drivers get what is left of one datagram
        var t = new NetWriter(512);
        // the Nitro Pack's game type, appended: an older reader stops before it, a newer one without it reads a combat melee
        t.U8((byte)Kind); t.U8((byte)Goal); t.VarU((uint)Math.Max(0, GoalValue)); t.U8(Teams);
        // a custom arena's hash, whether it is shared and its size (protocol 11), appended the same way
        t.U8((byte)(CustomMap ? 32 : 0));
        if (CustomMap) { t.Bytes(MapHash); t.Bool(MapShared); t.VarU(MapSize); }
        // the game has a password (protocol 12), appended the same way; the password itself is never sent
        t.Bool(Locked);
        // WEAPON RESTRICTIONS and Hidden Weapons & Cars (protocol 12), appended the same way: flags (1 hidden content allowed,
        // 2 every weapon restricted, 4 several turrets allowed: an older reader never looks at that bit), then the (class, tier) pairs, tier −1 the whole class
        var rs = Restricted;
        t.U8((byte)((Hidden ? 1 : 0) | (rs.All ? 2 : 0) | (Turrets ? 4 : 0)));
        var pairs = rs.All ? [] : rs.Pairs.Take(I76.Sim.Trip.WeaponRestrictions.MaxPairs).ToList();
        t.U8((byte)pairs.Count);
        foreach (var (c, tier) in pairs) { t.I8((sbyte)c); t.I16((short)tier); }
        // a co-op story mission (protocol 15), appended the same way: the arena is the mission
        t.Bool(Coop);
        // the game's simulation rate in Hz (the host's choice, for everyone in it), appended the same way; 0 when not said
        t.U8(RateHz);
        // a co-op game still in its lobby (protocol 20), appended the same way
        t.Bool(Lobby);
        // the host lets players reset their car upright (the reset-upright key), appended the same way; an older host says nothing: no
        t.Bool(CarReset);
        // the CB radio is on in this game (protocol 20), appended the same way; an older host says nothing: on
        t.Bool(Cb);
        // the host allows the chase crosshair, an aiming aid of the port's (protocol 22), appended the same way; an older host says nothing: not allowed
        t.Bool(Crosshair);
        var shown = Shown(AnswerBytes - w.Length - 1 - t.Length);
        w.U8((byte)shown.Count);
        foreach (var d in shown) { w.Str(Clip(d.Name, Protocol.MaxName)); w.Str(Clip(d.Car, 32)); w.U8(d.Team); }
        w.Bytes(t.ToArray());
    }

    /// <summary>The answer, or null for anything else (a stray datagram, another program's).</summary>
    public static SessionInfo? TryRead(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new NetReader(data);
            if (r.Str() != Discovery.InfoMagic) return null;
            ushort version = r.U16();
            ulong id = r.U32() | (ulong)r.U32() << 32;
            string name = r.Str(), arena = r.Str();
            int players = r.U8(), max = r.U8();
            bool team = r.Bool(), invite = r.Bool();
            int score = (int)r.VarU();
            float limit = r.F32();
            bool over = r.Bool();
            var drivers = new SessionDriver[r.U8()];
            for (int i = 0; i < drivers.Length; i++) drivers[i] = new SessionDriver(r.Str(), r.Str(), r.U8());
            var kind = I76.Sim.Missions.MeleeKind.Combat; var goal = I76.Sim.Missions.MeleeGoal.None; int value = 0; byte teams = 2;
            if (r.Remaining >= 4)
            {
                var k = (I76.Sim.Missions.MeleeKind)r.U8(); var g = (I76.Sim.Missions.MeleeGoal)r.U8();
                int v = (int)r.VarU(); byte t = r.U8();
                if (k <= I76.Sim.Missions.MeleeKind.Ctf && g <= I76.Sim.Missions.MeleeGoal.Kills) (kind, goal, value, teams) = (k, g, v, t);
            }
            byte[]? hash = null; bool shared = false; uint size = 0;
            if (r.Remaining >= 1 && r.U8() == 32) { hash = r.Bytes(32).ToArray(); shared = r.Bool(); size = r.VarU(); }
            bool locked = r.Remaining >= 1 && r.Bool();
            bool hidden = false, turrets = false;
            var restricted = I76.Sim.Trip.WeaponRestrictions.None;
            if (r.Remaining >= 2)
            {
                byte flags = r.U8();
                int n = r.U8();
                if (n > I76.Sim.Trip.WeaponRestrictions.MaxPairs) return null;
                var pairs = new List<(int, int)>();
                for (int i = 0; i < n; i++) { int c = r.I8(); int t = r.I16(); pairs.Add((c, t)); }
                hidden = (flags & 1) != 0; turrets = (flags & 4) != 0;
                restricted = I76.Sim.Trip.WeaponRestrictions.Normalize((flags & 2) != 0, pairs);
            }
            bool coop = r.Remaining >= 1 && r.Bool();
            byte rate = r.Remaining >= 1 ? r.U8() : (byte)0;
            if (!I76.Sim.Session.SimRate.IsOffered(rate)) rate = 0;
            bool lobby = r.Remaining >= 1 && r.Bool();
            bool carReset = r.Remaining >= 1 && r.Bool();
            bool cb = r.Remaining < 1 || r.Bool();
            bool crosshair = r.Remaining >= 1 && r.Bool();
            return new SessionInfo(id, version, name, arena, players, max, team, invite, score, limit, over, drivers, kind, goal, value, teams, hash, shared, size, locked,
                restricted.IsEmpty ? null : restricted, hidden, Coop: coop, RateHz: rate, Lobby: lobby, Turrets: turrets, CarReset: carReset, Cb: cb, Crosshair: crosshair);
        }
        catch (WireException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// The drivers that fit the answer: one datagram (unconnected messages are not fragmented), so the answer is kept to
    /// <see cref="AnswerBytes"/> and the list cut to what fits after the rest (about 50 drivers with short names, fewer with long ones).
    /// </summary>
    public const int AnswerBytes = 1300;

    private List<SessionDriver> Shown(int budget)
    {
        var shown = new List<SessionDriver>();
        int bytes = 0;
        foreach (var d in Drivers.Take(Protocol.MaxSeats))
        {
            int n = 3 + System.Text.Encoding.UTF8.GetByteCount(Clip(d.Name, Protocol.MaxName)) + System.Text.Encoding.UTF8.GetByteCount(Clip(d.Car, 32));
            if (bytes + n > budget) break;
            bytes += n;
            shown.Add(d);
        }
        return shown;
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Whether a join would be turned away: the melee is over or every seat is taken.</summary>
    public bool Full => Over || Players >= MaxPlayers;
}

/// <summary>
/// The session queries, sent outside any connection (LiteNetLib's
/// unconnected messages): "I76Q" and the protocol version, broadcast on the
/// LAN or sent to one address; a server answers with its
/// <see cref="SessionInfo"/>. A game in progress keeps answering, so a
/// server is found whenever it is up.
/// </summary>
public static class Discovery
{
    public const string QueryMagic = "I76Q", InfoMagic = "I76S";
    /// <summary>The ports a LAN search asks on: the default and the next few (a second server on one machine).</summary>
    public const int PortsSearched = 4;

    public static byte[] Query()
    {
        var w = new NetWriter(16);
        w.Str(QueryMagic);
        w.U16(Protocol.Version);
        return w.ToArray();
    }

    public static bool IsQuery(ReadOnlySpan<byte> data)
    {
        try { var r = new NetReader(data); return r.Str() == QueryMagic; }
        catch (WireException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>"host:port", "host" (the default port) or "[v6]:port" into its parts; false if it is not one.</summary>
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        text = text.Trim();
        host = text;
        port = Protocol.DefaultPort;
        if (text.Length == 0) return false;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            return rest.Length == 0 || rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and < 65536;
        }
        int colon = text.LastIndexOf(':');
        if (colon >= 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or >= 65536) return false;
        }
        return host.Length > 0 && !host.Any(char.IsWhiteSpace);
    }
}

/// <summary>A game listed on an anet server: which server ("host", port) and its record there (the join goes through it).</summary>
public sealed record AnetListing(string Server, int Port, AnetSession Session);

/// <summary>
/// Finds games: on the LAN by broadcast (and on this machine, which a
/// broadcast does not always reach), at the addresses it is given, or in an
/// anet server's list (<see cref="List"/>). Each <see cref="Refresh"/> asks
/// again; <see cref="Poll"/> takes the answers. A game that has not answered
/// for <see cref="Timeout"/> seconds drops out of <see cref="Sessions"/>; a
/// listed one stays while the server lists it. One answer per game (by its
/// id), at the address it first answered from. A listed game is also asked
/// directly at its public address, for its drivers and the ping (a host
/// behind a NAT that filters strangers does not answer: no ping then).
/// </summary>
public sealed class SessionBrowser : IDisposable
{
    public sealed record Found(SessionInfo Info, IPEndPoint EndPoint, double Seen, AnetListing? Anet = null, int Ping = -1);

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly AnetLayer _layer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Found> _found = [];
    private readonly byte[] _query = Discovery.Query();
    private readonly Dictionary<string, IPAddress?> _resolved = [];
    private readonly Dictionary<IPEndPoint, double> _asked = [];
    private AnetClient? _anet;
    private int _anetSeen = -1;
    /// <summary>Listed games (by id) that answered directly, and when each listed one was first seen and last introduced.</summary>
    private readonly HashSet<ulong> _answered = [];
    private readonly Dictionary<ulong, double> _listedAt = [], _introducedAt = [];
    /// <summary>
    /// A listed game that has not answered directly after this long is asked for through the server (INDIRECT_JOIN, as a join
    /// does): its host then opens its NAT toward us for a while, and our next question gets its drivers and the ping.
    /// Again every <see cref="IntroduceEvery"/> seconds while it stays silent.
    /// </summary>
    public double IntroduceAfter { get; init; } = 2;
    public double IntroduceEvery { get; init; } = 15;
    public double Timeout { get; init; } = 3.5;
    /// <summary>A Nitro Pack install lists only Nitro games from a server, the base game only its own.</summary>
    public bool Nitro { get; init; }

    /// <summary>The games found, in the order they were first seen.</summary>
    public IReadOnlyList<Found> Sessions => _found;
    /// <summary>Bumped whenever the list or a game's answer changes (the form redraws).</summary>
    public int Version { get; private set; }

    public SessionBrowser(PacketLayerBase? inner = null)
    {
        _layer = new AnetLayer(inner);
        _net = new NetManager(_listener, _layer) { UnconnectedMessagesEnabled = true, IPv6Enabled = false, AutoRecycle = true };
        _listener.NetworkReceiveUnconnectedEvent += (ep, reader, type) =>
        {
            if (SessionInfo.TryRead(reader.GetRemainingBytesSpan()) is not { } info) return;
            if (info.Version != Protocol.Version) return;
            var now = _clock.Elapsed.TotalSeconds;
            ep = AnetLayer.Normal(ep);
            int ping = _asked.TryGetValue(ep, out var at) ? (int)((now - at) * 1000) : -1;
            // a listed game answering at its public or own address: its own answer fills in the listing
            int listed = _found.FindIndex(f => f.Anet is { } a && (a.Session.Master.ToEndPoint().Equals(ep) || a.Session.KeyAddress.ToEndPoint().Equals(ep)));
            if (listed >= 0)
            {
                var f = _found[listed];
                var merged = info with { Id = f.Info.Id };
                _answered.Add(f.Info.Id);
                if (!SameInfo(f.Info, merged) || Math.Abs(f.Ping - ping) > 20) Version++;
                _found[listed] = f with { Info = merged, Ping = ping >= 0 ? ping : f.Ping, EndPoint = ep };
                return;
            }
            int i = _found.FindIndex(f => f.Info.Id == info.Id);
            if (i < 0) { _found.Add(new Found(info, ep, now, Ping: ping)); Version++; }
            else
            {
                if (!SameInfo(_found[i].Info, info)) Version++;
                _found[i] = _found[i] with { Info = info, Seen = now, Ping = ping >= 0 ? ping : _found[i].Ping };
            }
        };
        _net.Start();
    }

    private static bool SameInfo(SessionInfo a, SessionInfo b) =>
        a with { Drivers = [], MapHash = null } == b with { Drivers = [], MapHash = null } && a.Drivers.SequenceEqual(b.Drivers)
        && (a.MapHash ?? []).AsSpan().SequenceEqual(b.MapHash ?? []);

    /// <summary>Asks the LAN: a broadcast to the default port and the next few, and the same at 127.0.0.1.</summary>
    public void SearchLan(int basePort = Protocol.DefaultPort)
    {
        for (int p = basePort; p < basePort + Discovery.PortsSearched; p++)
        {
            _net.SendBroadcast(_query, p);
            _net.SendUnconnectedMessage(_query, new IPEndPoint(IPAddress.Loopback, p));
        }
        Expire();
    }

    /// <summary>Asks one address (the address book's): host is a name or an IP.</summary>
    public bool Ask(string host, int port)
    {
        if (!_resolved.TryGetValue(host, out var addr) && !IPAddress.TryParse(host, out addr))
        {
            // a name is looked up once (the form asks every second)
            try { addr = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
            catch (System.Net.Sockets.SocketException) { addr = null; }
            catch (ArgumentException) { addr = null; }
            _resolved[host] = addr;
        }
        Expire();
        if (addr is null) return false;
        AskAt(new IPEndPoint(addr, port));
        return true;
    }

    private void AskAt(IPEndPoint ep)
    {
        _asked[ep] = _clock.Elapsed.TotalSeconds;
        _net.SendUnconnectedMessage(_query, ep);
    }

    /// <summary>
    /// Lists the I26 games on an anet server (port 21157 by default), from now on and again after it goes quiet, and
    /// asks each one directly (call it every second, as <see cref="Ask"/>). Another server given replaces the first.
    /// </summary>
    public void List(string host, int port = AnetWire.ServerPort)
    {
        if (_anet is { } a && (a.Host != host || a.Port != port)) { a.Dispose(); _anet = null; DropListed(); }
        _anet ??= new AnetClient(new LiteNetAnetTransport(_net, _layer), host, port, AnetSpecies.I26);
        foreach (var f in _found)
            if (f.Anet is { } l)
            {
                AskAt(l.Session.Master.ToEndPoint());
                if (l.Session.KeyAddress != l.Session.Master) AskAt(l.Session.KeyAddress.ToEndPoint());
            }
    }

    /// <summary>The anet server's state for the roster: listing, or why not (null when no server is being listed).</summary>
    public string? ServerStatus => _anet switch
    {
        null => null,
        { State: AnetState.Online } a => $"Listing {a.Name}",
        { State: AnetState.Offline } a => $"{a.Name} {a.Problem ?? "is not answering"}",
        var a => $"Contacting {a.Name}...",
    };

    /// <summary>The anet server being listed, if any.</summary>
    public AnetClient? Server => _anet;

    private void DropListed()
    {
        if (_found.RemoveAll(f => f.Anet is not null) > 0) Version++;
        _anetSeen = -1;
    }

    private void SyncListed()
    {
        if (_anet is not { } a || a.Version == _anetSeen) return;
        _anetSeen = a.Version;
        var now = _clock.Elapsed.TotalSeconds;
        var keep = new HashSet<ulong>();
        foreach (var s in a.Sessions.Values)
        {
            // only I26 games of this protocol and this game (I76 or the Nitro Pack) are listed
            if (s.Tag is not { } t || t.Protocol != Protocol.Version || t.Nitro != Nitro) continue;
            keep.Add(s.Id);
            var listing = new AnetListing(a.Host, a.Port, s);
            int i = _found.FindIndex(f => f.Anet is not null && f.Info.Id == s.Id);
            // the same game found directly first (the entry's address is the host's too): it becomes the listing
            int direct = i >= 0 ? -1 : _found.FindIndex(f => f.Anet is null && (f.EndPoint.Equals(s.Master.ToEndPoint()) || f.EndPoint.Equals(s.KeyAddress.ToEndPoint())));
            if (direct >= 0)
            {
                _found[direct] = _found[direct] with { Info = _found[direct].Info with { Id = s.Id }, Anet = listing, Seen = now };
                continue;
            }
            if (i >= 0)
            {
                var old = _found[i];
                // the record's numbers win over an older direct answer; the drivers and limits only come directly
                var info = old.Info with { Name = s.Name, Players = s.Players, MaxPlayers = s.MaxPlayers, TeamPlay = t.TeamPlay, InviteOnly = t.InviteOnly, Over = t.Over, Locked = t.Locked, Lobby = t.Lobby };
                if (t.Hidden is { } h) info = info with { Hidden = h, HiddenKnown = true };
                _found[i] = old with { Info = info, Anet = listing, Seen = now };
            }
            else
            {
                var info = new SessionInfo(s.Id, t.Protocol, s.Name, t.Arena, s.Players, s.MaxPlayers, t.TeamPlay, t.InviteOnly, 0, 0, t.Over, [],
                    t.Kind, t.Goal, 0, (byte)t.CtfTeams, Locked: t.Locked, Hidden: t.Hidden ?? false, HiddenKnown: t.Hidden is not null, Coop: t.Coop, Lobby: t.Lobby);
                _found.Add(new Found(info, s.Master.ToEndPoint(), now, listing));
                _listedAt.TryAdd(s.Id, now);
                AskAt(s.Master.ToEndPoint());
            }
        }
        _found.RemoveAll(f => f.Anet is not null && !keep.Contains(f.Info.Id));
        Version++;
    }

    private void Expire()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_found.RemoveAll(f => f.Anet is null && now - f.Seen > Timeout) > 0) Version++;
    }

    public void Poll()
    {
        _net.PollEvents();
        if (_anet is null) return;
        _anet.Update();
        SyncListed();
        Introduce();
    }

    /// <summary>The listed games that stay silent (a host behind a NAT that filters strangers) asked for through the server.</summary>
    private void Introduce()
    {
        if (_anet is not { State: AnetState.Online } a) return;
        var now = _clock.Elapsed.TotalSeconds;
        foreach (var f in _found)
        {
            if (f.Anet is not { } l || _answered.Contains(f.Info.Id)) continue;
            if (!_listedAt.TryGetValue(f.Info.Id, out var seen) || now - seen < IntroduceAfter) continue;
            if (_introducedAt.TryGetValue(f.Info.Id, out var at) && now - at < IntroduceEvery) continue;
            _introducedAt[f.Info.Id] = now;
            a.IndirectJoin(l.Session);
        }
    }

    public void Dispose()
    {
        _anet?.Dispose();
        _net.Stop();
    }
}
