using I76.Server;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The host's Co-op Rolling Start (i26-server --coop-rolling-start, the host form's) reaches the mission the server builds.</summary>
public class CoopRollingStartNetTests
{
    [Fact]
    public void TheServersRollingStartIsTheGroupsAndItsBuilds()
    {
        if (HostTests.InstallDir is not { } dir) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "t01", Coop = "t01", MaxPlayers = 2, Port = 0, CoopRollingStart = true });
        var mode = (CoopMode)server.Host.Mode;
        Assert.True(mode.Group.RollingStart);
        Assert.True(mode.Build.Coop!.RollingStart);
        using var plain = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "t01", Coop = "t01", MaxPlayers = 2, Port = 0 });
        Assert.False(((CoopMode)plain.Host.Mode).Group.RollingStart);
        // listed for the session popup (a melee lists it off), and kept by a host migration's options (JSON in the savestate's extras)
        Assert.True(server.Host.Info().RollingStart);
        Assert.False(plain.Host.Info().RollingStart);
        using var melee = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "m01", Port = 0, CoopRollingStart = true });
        Assert.False(melee.Host.Info().RollingStart);
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(server.Options, json), json)!.CoopRollingStart);
    }
}
