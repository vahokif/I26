using I76.Server;
using I76.Sim.Session;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The host's Co-op Scaling (i26-server --coop-scale, the host form's) on the server: the copies are made there and named to the clients.</summary>
public class CoopScalingNetTests
{
    [Fact]
    public void TheServersCopiesAreSpawnEventsWithTheirFiles()
    {
        if (HostTests.InstallDir is not { } dir) return;
        var host = new MeleeHost(new ServerOptions { Install = dir, Arena = "t01", Coop = "t01", MaxPlayers = 3, Seed = 7, CoopScale = 3 });
        Assert.Equal(3, ((CoopMode)host.Mode).Build.Coop!.ExtraEnemies);
        var seats = new[] { host.Join("Hal", Protocol.MissionCar, 0), host.Join("Ann", Protocol.MissionCar, 0), host.Join("Bob", Protocol.MissionCar, 0) };
        var events = new List<NetEvent>();
        for (uint t = 1; t <= 10; t++)
        {
            foreach (var s in seats) s.Inputs.Add(new InputMessage(t, [default(CarInput)]));
            host.Step();
            events.AddRange(host.TakeEvents());
        }
        var script = ((CoopMode)host.Mode).Setup.Script!.Host;
        var copies = script.Entities.Where(e => e.ExtraOf is not null).ToList();
        Assert.Equal(6, copies.Count);   // T01's three enemy cars, two copies each for three players
        var spawned = events.OfType<SpawnEvent>().Select(e => e.Car).ToList();
        foreach (var x in copies)
        {
            var info = Assert.Single(spawned, c => c.Serial == (x.Vehicle!.NetId & 0xffffff));
            Assert.Equal(x.Vehicle.Definition, info.Def);
            Assert.Equal(2, info.Team);
        }
    }

    /// <summary>The cap is listed for the session popup (a melee lists none) and goes across a host migration with the options (JSON in the savestate's extras).</summary>
    [Fact]
    public void TheCapIsListedAndKeptByAMigration()
    {
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var o = new ServerOptions { Install = "", Coop = "t01", CoopScale = 2 };
        Assert.Equal(2, System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o, json), json)!.CoopScale);
        if (HostTests.InstallDir is not { } dir) return;
        Assert.Equal(2, new MeleeHost(o with { Install = dir, Arena = "t01", MaxPlayers = 2 }).Info().CoopScale);
        Assert.Equal(0, new MeleeHost(new ServerOptions { Install = dir, Arena = "m01", CoopScale = 2 }).Info().CoopScale);
    }
}
