using I76.Net;
using I76.Server;
using I76.Sim.Missions;
using I76.Sim.Trip;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The server's network poll (its socket's events between ticks: joins, messages, leaves) and its faults: a players' report
/// of a host whose game ended with "network poll failed: System.OverflowException: Arithmetic operation resulted in an overflow"
/// and, on the host's own screen, "Migrating host...". The overflow is the car check's armour total (a join's or a respawn's own
/// variant whose armour and chassis add up past what an int holds); a join the checks fail on is turned away, not the game
/// ended; and a server that does stop on a fault says so to its players at once.
/// </summary>
public class ServerPollTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A Piranha variant whose armour and chassis total more than an int holds (each zone is a 32-bit field of the file).</summary>
    private static byte[] Overflowing(I76.Formats.Vfs.AssetVfs vfs) => VariantTests.Custom(vfs, c =>
    {
        c.Armour = [int.MaxValue, int.MaxValue, 1, 1];
        c.Chassis = [1, 1, 1, 1];
    });

    private static bool Wait(Func<bool> done, Action poll, int ms = 20000)
    {
        for (int t = 0; t < ms / 10 && !done(); t++) { poll(); Thread.Sleep(10); }
        return done();
    }

    /// <summary>The car check adds the zones as 64-bit numbers: a total past an int is refused as more armour than the model's, not an exception.</summary>
    [Fact]
    public void ACarWhoseArmourTotalOverflowsAnIntIsRefusedByTheCheck()
    {
        if (HostTests.InstallDir is null) return;
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir, Arena = "m01" });
        var vcf = Overflowing(host.Vfs);
        var why = host.CheckJoin("vppirna4.vcf", vcf);
        output.WriteLine($"refused: {why}");
        Assert.NotNull(why);
        Assert.Contains("armour beyond the model's", why);
        // a total at the most an int holds is refused the same way, as more armour than the model's
        var big = VariantTests.Custom(host.Vfs, c => { c.Armour = [int.MaxValue - 4, 0, 0, 0]; c.Chassis = [1, 1, 1, 1]; });
        CarConfigCheck.Check(host.Vfs, PartsDatabase.Build(host.Vfs), "vppirna", big, out var error);
        output.WriteLine($"at the limit: {error}");
        Assert.Contains("armour beyond the model's", error);
    }

    /// <summary>
    /// Over the wire: a joiner with that car is turned away with the reason, a seated player who asks for it at a respawn is
    /// told no, and the game plays on for everyone (it used to end there, in the poll).
    /// </summary>
    [Fact]
    public void AJoinOrRespawnWithAnOverflowingCarIsRefusedAndTheGamePlaysOn()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0, MaxPlayers = 8 });
        var lines = new List<string>();
        server.Log += m => { lock (lines) lines.Add(m); output.WriteLine($"[server] {m}"); };
        server.Start();
        var vcf = Overflowing(new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(install)));
        using var bad = new MeleeClient();
        bad.Connect("127.0.0.1", server.Port, "Armoured", "vppirna4.vcf", config: vcf);
        Assert.True(Wait(() => bad.State is ClientState.Rejected or ClientState.Disconnected, bad.Poll), "the join is answered");
        output.WriteLine($"the join: {bad.Reason}");
        Assert.Contains("armour beyond the model's", bad.Reason ?? "");
        Assert.True(server.Running, "the server plays on after the join");
        Assert.Null(server.Fault);
        // a player in the game asks for it while dead (Reconfigure): refused, the game goes on
        using var good = new MeleeClient();
        good.Connect("127.0.0.1", server.Port, "Plain", "vppirna1.vcf");
        Assert.True(Wait(() => good.State == ClientState.Joined && good.Snapshots.Count > 5, good.Poll));
        server.Invoke(h => h.Setup.Damage.Destroy(h.Seats.Single().Player.Car));
        Assert.True(Wait(() => good.Snapshots.Count > 0 && server.Invoke(h => h.Seats.Single().Player.Destroyed), good.Poll));
        good.SendReconfigure("vppirna4.vcf", vcf);
        Assert.True(Wait(() => good.ReconfigureAnswers.Count > 0, good.Poll), "the respawn's car is answered");
        var answer = good.ReconfigureAnswers.Dequeue();
        output.WriteLine($"the respawn's car: {answer.Taken} {answer.Text}");
        Assert.False(answer.Taken);
        Assert.True(server.Running, "the server plays on after the respawn's car");
        Assert.Null(server.Fault);
        int ticks = server.Invoke(h => (int)h.Tick);
        Assert.True(Wait(() => server.Invoke(h => (int)h.Tick) > ticks + 20, good.Poll), "the game goes on ticking");
        lock (lines) Assert.DoesNotContain(lines, l => l.Contains("network poll failed"));
    }

    /// <summary>
    /// A server that stops on a fault in a game nobody takes over tells its players why at once: they used to stay joined to a
    /// socket that still answered, with nothing more coming.
    /// </summary>
    [Fact]
    public void AServerThatStopsOnAFaultTellsItsPlayersAtOnce()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0 });
        server.Log += m => output.WriteLine($"[server] {m}");
        server.Log += m => { if (m.StartsWith("chat: Plain: fault", StringComparison.Ordinal)) throw new InvalidOperationException("injected"); };
        server.Start();
        using var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, "Plain", "vppirna1.vcf");
        Assert.True(Wait(() => c.State == ClientState.Joined && c.Snapshots.Count > 5, c.Poll));
        var cut = System.Diagnostics.Stopwatch.StartNew();
        c.SendChat("fault");
        Assert.True(Wait(() => c.State == ClientState.Disconnected, c.Poll, 10000), $"still {c.State}");
        output.WriteLine($"told after {cut.Elapsed.TotalSeconds:0.00} s: {c.Reason}");
        Assert.Equal("the server stopped (InvalidOperationException)", c.Reason);
        Assert.True(cut.Elapsed.TotalSeconds < 2);
        Assert.IsType<InvalidOperationException>(server.Fault);
    }
}
