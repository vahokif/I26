using I76.Net;

namespace I76.Server;

/// <summary>
/// The successor's takeover (<see cref="IHostSuccessor"/>; docs/multiplayer.md "Host migration"): when a savestate comes it
/// builds the game it is of (the arena from this install, seconds) in the background, once per round, so that a takeover
/// only restores, replays and opens the port. The server it starts is <see cref="Server"/>, the game's to run and close
/// from then on, as a listen server's.
/// </summary>
public sealed class MigrationStandby(string install, Action<string>? log = null) : IHostSuccessor, IDisposable
{
    /// <summary>A downloaded custom map's folder this client plays from, for the game it would host (null: the install's own arena).</summary>
    public string? MapDir { get; set; }
    /// <summary>The server started by the takeover (null until then).</summary>
    public NetMeleeServer? Server { get; private set; }
    /// <summary>Called with the server once it listens (on the takeover's thread), before the client rejoins it.</summary>
    public Action<NetMeleeServer>? Started { get; set; }
    /// <summary>A packet layer under the server's socket, as the client's had (the tests' NAT); null for none.</summary>
    public Func<LiteNetLib.Layers.PacketLayerBase?>? Layer { get; set; }

    /// <summary>The game of the latest savestate is built and waiting.</summary>
    public bool Ready { get { lock (_lock) return _build is { IsCompletedSuccessfully: true }; } }

    private readonly object _lock = new();
    private string? _key;
    private Task<MeleeHost>? _build;

    private static string KeyOf(ServerOptions o, int round) => $"{o.Arena}|{o.Coop}|{o.MaxPlayers}|{round}|{o.Rate.Label}|{o.Seed}|{o.Ai}|{o.Kind}|{o.TeamPlay}";

    public void Prepare(byte[] state)
    {
        ServerOptions o;
        int round;
        try { (o, round, _) = NetMeleeServer.ReadMigrationOptions(state, install); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or WireException or I76.Sim.Save.SnapshotMismatchException or I76.Sim.Save.SnapshotFormatException) { log?.Invoke($"host migration: an unreadable savestate ({e.Message})"); return; }
        o = o with { MapDir = MapDir };
        var key = KeyOf(o, round);
        lock (_lock)
        {
            if (key == _key) return;
            _key = key;
            log?.Invoke($"host migration: this client takes over if the host leaves; building {o.Coop ?? o.Arena} ahead");
            _build = Task.Run(() => new MeleeHost(o));
        }
    }

    public Task TakeOver(MigrationHandoff handoff) => Task.Run(async () =>
    {
        Prepare(handoff.State);
        Task<MeleeHost> build;
        lock (_lock) build = _build!;
        var host = await build.ConfigureAwait(false);
        var (_, _, anet) = NetMeleeServer.ReadMigrationOptions(handoff.State, install);
        var server = NetMeleeServer.Resume(host, handoff, log, Layer?.Invoke());
        Server = server;
        Started?.Invoke(server);
        server.Start();
        if (anet.Count > 0) server.ListOn(anet);
        lock (_lock) { _key = null; _build = null; }
    });

    public void Dispose()
    {
        lock (_lock) { _key = null; _build = null; }
    }
}
