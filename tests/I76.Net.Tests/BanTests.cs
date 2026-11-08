using System.Net;
using I76.Net;
using I76.Server;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The host's bans (#117): a boot that refuses the player's joins for the rest of the game, by name and address.</summary>
public class BanTests
{
    private static readonly IPAddress A = IPAddress.Parse("203.0.113.7"), B = IPAddress.Parse("198.51.100.20");

    [Fact] public void ABanRefusesTheNameOrTheAddress()
    {
        var b = new BanList();
        Assert.True(b.Add("Mad Max", A));
        Assert.True(b.Refuses("mad max", B));          // the name, whatever the case and the address
        Assert.True(b.Refuses("Someone Else", A));     // the address, whatever the name
        Assert.True(b.Refuses("Someone Else", A.MapToIPv6()));
        Assert.False(b.Refuses("Someone Else", B));
        Assert.False(b.Add("MAD MAX", B));             // banned already
        Assert.Equal(["Mad Max"], b.Names);
    }

    [Fact] public void ALoopbackAddressIsNeverHeld()
    {
        var b = new BanList();
        b.Add("Local", IPAddress.Loopback);
        Assert.True(b.Refuses("Local", IPAddress.Loopback));
        Assert.False(b.Refuses("Other", IPAddress.Loopback));
        Assert.False(b.Refuses("Other", IPAddress.IPv6Loopback));
    }

    [Fact] public void UnbanLiftsByNameOrAUniqueStart()
    {
        var b = new BanList();
        b.Add("Groove Champion", A);
        b.Add("Groovy", null);
        Assert.Null(b.Remove("Groov"));                // two start so
        Assert.Equal("Groovy", b.Remove("groovy"));
        Assert.Equal("Groove Champion", b.Remove("Groo"));
        Assert.False(b.Refuses("Groove Champion", A));
        Assert.Equal(0, b.Count);
    }

    private static MeleeClient Connect(NetMeleeServer server, string name)
    {
        var c = new MeleeClient();
        c.Connect("127.0.0.1", server.Port, name, "vppirna1.vcf");
        return c;
    }

    private static void Pump(Func<bool> until, params MeleeClient[] clients)
    {
        for (int t = 0; t < 500 && !until(); t++) { foreach (var c in clients) c.Poll(); Thread.Sleep(10); }
    }

    /// <summary>The server's rule: the banned player is booted, refused on joining again under the same name, let in once the ban is lifted.</summary>
    [Fact]
    public void ABannedPlayerCannotRejoinUntilUnbanned()
    {
        if (HostTests.InstallDir is not { } install) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = install, Arena = "m01", Port = 0 });
        server.Start();
        using var a = Connect(server, "Alice");
        using var b = Connect(server, "Bob");
        Pump(() => a.State == ClientState.Joined && b.State == ClientState.Joined, a, b);
        Assert.Equal("Ban: whom? (/ban NAME)", server.Ban(" "));
        Assert.Null(server.Ban("bo"));
        Pump(() => b.State == ClientState.Disconnected, a, b);
        Assert.Equal(Protocol.Booted, b.Reason);
        Assert.Equal(["Bob"], server.Banned);
        Pump(() => a.Events.SelectMany(x => x.Events).OfType<ChatEvent>().Any(c => c.Text.Contains("banned")), a);
        // the same name again: refused
        using var b2 = Connect(server, "BOB");
        Pump(() => b2.State == ClientState.Rejected, b2);
        Assert.Equal((ClientState.Rejected, Protocol.Banned), (b2.State, b2.Reason));
        // another name from the same machine (loopback is never held) is let in
        using var c = Connect(server, "Carol");
        Pump(() => c.State == ClientState.Joined, c);
        Assert.Equal(ClientState.Joined, c.State);
        // a name not in the game is banned by name alone
        Assert.Null(server.Ban("Dave"));
        Assert.Equal("Ban: Dave is banned already", server.Ban("dave"));
        Assert.Null(server.Unban("bob"));
        Assert.StartsWith("Unban: nobody banned", server.Unban("bob"));
        using var b3 = Connect(server, "Bob");
        Pump(() => b3.State == ClientState.Joined, b3);
        Assert.Equal(ClientState.Joined, b3.State);
    }
}
