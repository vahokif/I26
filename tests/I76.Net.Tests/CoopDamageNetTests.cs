using I76.Server;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The host's Co-op Damage (i26-server --coop-damage, the host form's) reaches the mission the server builds.</summary>
public class CoopDamageNetTests
{
    [Fact]
    public void TheServersCoopDamageIsTheMissionsAndItsBuilds()
    {
        if (HostTests.InstallDir is not { } dir) return;
        using var server = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "t01", Coop = "t01", MaxPlayers = 2, Port = 0, CoopDamage = 0.5f });
        var mode = (CoopMode)server.Host.Mode;
        Assert.Equal(0.5f, mode.Setup.Damage.CoopDamageFactor);
        Assert.Equal(0.5f, mode.Build.Coop!.DamageFactor);
        // the default is the original's
        using var plain = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "t01", Coop = "t01", MaxPlayers = 2, Port = 0 });
        Assert.Equal(1f, ((CoopMode)plain.Host.Mode).Setup.Damage.CoopDamageFactor);
        // listed for the session popup; a melee lists the original's whatever the option says
        Assert.Equal(0.5f, server.Host.Info().CoopDamage);
        using var melee = new NetMeleeServer(new ServerOptions { Install = dir, Arena = "m01", Port = 0, CoopDamage = 0.5f });
        Assert.Equal(1f, melee.Host.Info().CoopDamage);
        Assert.Equal(1f, melee.Host.Setup.Damage.CoopDamageFactor);
    }

    /// <summary>A host migration's successor reads the options from the savestate's extras (JSON): the factor goes across.</summary>
    [Fact]
    public void AHostMigrationKeepsTheFactor()
    {
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var o = new ServerOptions { Install = "", Coop = "t01", CoopDamage = 1.5f };
        var back = System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o, json), json)!;
        Assert.Equal(1.5f, back.CoopDamage);
    }
}
