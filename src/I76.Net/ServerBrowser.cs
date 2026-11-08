using I76.Net.Anet;

namespace I76.Net;

/// <summary>
/// One address book entry as the internet roster searches it: its name, its address as written, and what the address
/// names. "server" or "server:21157" is an anet game server whose list is read; "host:port" (another port) is a game at
/// that address, asked directly; "host" alone is both (the anet server there, and a game on the default port).
/// </summary>
public sealed record ServerSource(string Name, string Address, (string Host, int Port)? Anet, (string Host, int Port)? Direct)
{
    public static ServerSource Parse(string name, string address)
    {
        if (!Discovery.TryParseAddress(address, out var host, out var port)) return new(name, address, null, null);
        bool hasPort = AnetClient.HasPort(address), anetPort = !hasPort || port == AnetWire.ServerPort;
        return new(name, address, anetPort ? (host, AnetWire.ServerPort) : null, anetPort && hasPort ? null : (host, port));
    }

    /// <summary>The address names something to ask.</summary>
    public bool Valid => Anet is not null || Direct is not null;
}

/// <summary>Where an entry of the book stands.</summary>
public enum ServerState { Connecting, Online, NotAnswering, NotFound, NoAddress }

/// <summary>One entry's part of the roster: the entry, where it stands, and the games it found, in the order they were first seen.</summary>
public sealed record ServerGroup(ServerSource Source, ServerState State, IReadOnlyList<SessionBrowser.Found> Games)
{
    /// <summary>The short status the roster's header row shows beside the entry's name.</summary>
    public string Status => Games.Count > 0 ? (Games.Count == 1 ? "1 game" : $"{Games.Count} games") : State switch
    {
        ServerState.Online => "no games",
        ServerState.NotAnswering => "not answering",
        ServerState.NotFound => "not found",
        ServerState.NoAddress => "no address",
        _ => "connecting...",
    };
}

/// <summary>
/// The internet roster's search (docs/multiplayer.md "From the main menu"): every entry of the address book at once, each
/// on its own <see cref="SessionBrowser"/> (its own socket and anet link), so one server that is slow, silent or not found
/// holds up none of the others, and a game is always joined through the server that listed it. A game two servers list
/// shows under both. <see cref="Watch"/> gives the book (again after it is edited: running entries go on), <see cref="Refresh"/>
/// asks again (every second), <see cref="Poll"/> takes the answers (every frame).
/// </summary>
public sealed class ServerBrowser : IDisposable
{
    private sealed class Slot(ServerSource source)
    {
        public ServerSource Source = source;
        public SessionBrowser? Browser;
        public double Since = -1;
        public (int Version, ServerState State) Seen = (-1, ServerState.Connecting);
    }

    private readonly List<Slot> _slots = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private List<ServerGroup>? _groups;

    /// <summary>A Nitro Pack install lists only Nitro games, the base game only its own.</summary>
    public bool Nitro { get; init; }
    /// <summary>
    /// How long an entry may stay silent before it is "not answering": a game's own address, or an anet server still being
    /// reached (its link keeps trying for half a minute, and again after; the row says "connecting..." only for this long).
    /// </summary>
    public double AnswerWait { get; init; } = 6;
    /// <summary>Bumped whenever a group's games or state change (the roster redraws).</summary>
    public int Version { get; private set; }

    /// <summary>The book's entries, in its order. Entries already searched keep their search; the rest start, the gone ones stop.</summary>
    public void Watch(IReadOnlyList<ServerSource> sources)
    {
        var old = _slots.ToList();
        _slots.Clear();
        foreach (var s in sources)
        {
            int i = old.FindIndex(o => o.Source.Anet == s.Anet && o.Source.Direct == s.Direct);
            if (i >= 0) { old[i].Source = s; _slots.Add(old[i]); old.RemoveAt(i); }
            else _slots.Add(new Slot(s));
        }
        foreach (var o in old) o.Browser?.Dispose();
        _groups = null;
        Version++;
    }

    /// <summary>Asks every entry again: the game at its address, its anet server's list (the server's link stays up between).</summary>
    public void Refresh()
    {
        foreach (var s in _slots)
        {
            if (!s.Source.Valid) continue;
            s.Browser ??= new SessionBrowser { Nitro = Nitro };
            if (s.Since < 0) s.Since = _clock.Elapsed.TotalSeconds;
            if (s.Source.Direct is { } d) s.Browser.Ask(d.Host, d.Port);
            if (s.Source.Anet is { } a) s.Browser.List(a.Host, a.Port);
        }
    }

    public void Poll()
    {
        bool changed = false;
        foreach (var s in _slots)
        {
            s.Browser?.Poll();
            var now = (s.Browser?.Version ?? 0, StateOf(s));
            if (now != s.Seen) { s.Seen = now; changed = true; }
        }
        if (changed) { _groups = null; Version++; }
    }

    private ServerState StateOf(Slot s)
    {
        if (!s.Source.Valid) return ServerState.NoAddress;
        if (s.Browser is not { } b) return ServerState.Connecting;
        bool waited = _clock.Elapsed.TotalSeconds - s.Since >= AnswerWait;
        if (s.Source.Anet is not null && b.Server is { } a)
            return a.State switch
            {
                AnetState.Online => ServerState.Online,
                AnetState.Offline => a.Problem == "was not found" ? ServerState.NotFound : ServerState.NotAnswering,
                _ => waited ? ServerState.NotAnswering : ServerState.Connecting,
            };
        if (s.Source.Direct is { } d)
        {
            if (b.NotFound(d.Host)) return ServerState.NotFound;
            if (b.Sessions.Count > 0) return ServerState.Online;
            if (waited) return ServerState.NotAnswering;
        }
        return ServerState.Connecting;
    }

    /// <summary>One group per entry of the book, in its order.</summary>
    public IReadOnlyList<ServerGroup> Groups => _groups ??= [.. _slots.Select(s => new ServerGroup(s.Source, s.Seen.State, s.Browser is { } b ? [.. b.Sessions] : []))];

    /// <summary>Every game found, group after group (a game two servers list, twice).</summary>
    public IEnumerable<SessionBrowser.Found> Games => Groups.SelectMany(g => g.Games);

    public void Dispose()
    {
        foreach (var s in _slots) s.Browser?.Dispose();
        _slots.Clear();
        _groups = null;
    }
}
