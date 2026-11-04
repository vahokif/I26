using System.Numerics;
using I76.Formats.Mission.Edit;
using I76.Server;
using I76.Sim.Missions;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Race bots on checkpoints set close together (docs/multiplayer.md "Bots"): a flat race map made as the map editor makes one, with
/// no roads and its checkpoints 45 m apart, closer than the AI's own reach for a point of its path. A bot has to drive into each
/// checkpoint's 30 m sphere, not turn for the next one first.
/// </summary>
public class RaceCheckpointTests(ITestOutputHelper output) : IDisposable
{
    private ScratchMapInstall? _install;
    public void Dispose() => _install?.Dispose();

    private static string? Nitro
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e ? e : Environment.GetEnvironmentVariable("I76_NITRO");
            return d is { Length: > 0 } && Directory.Exists(d) ? d : null;
        }
    }

    /// <summary>A flat race map in a scratch install: the checkpoints at those offsets from the middle (ground level), the spawns 60 m south of the first.</summary>
    private (string File, Vector3 Mid) MakeMap(string stem, Vector2[] checks, int spawns)
    {
        var inst = _install ??= new ScratchMapInstall(Nitro!, "race");
        var proj = MapProject.CreateBlank(stem, "Close checks", ".rac", 2, 2, 100);
        var b = proj.WorldBounds();
        var mid = new Vector3((b.X + b.Z) / 2, 10f, (b.Y + b.W) / 2);
        for (int i = 0; i < checks.Length; i++)
            proj.Mission.Objects.Add(MapObject.Checkpoint(i + 1, mid + new Vector3(checks[i].X, 0, checks[i].Y)));
        for (int i = 0; i < spawns; i++)
            proj.Mission.Objects.Add(MapObject.Spawn(mid + new Vector3(checks[0].X + 12 * (i - (spawns - 1) / 2f), 0, checks[0].Y - 60), 0));
        foreach (var (name, data) in proj.ToFiles()) inst.Put(name, data);
        return (proj.MissionFileName, mid);
    }

    public static IEnumerable<object[]> Courses() =>
    [
        ["two in a line", new[] { new Vector2(0, 0), new Vector2(0, 45) }],
        ["three in a line", new[] { new Vector2(0, 0), new Vector2(0, 45), new Vector2(0, 90) }],
        ["a square", new[] { new Vector2(0, 0), new Vector2(0, 45), new Vector2(45, 45), new Vector2(45, 0) }],
        ["a zigzag", new[] { new Vector2(0, 0), new Vector2(0, 45), new Vector2(35, 17), new Vector2(35, 62), new Vector2(70, 34) }],
    ];

    /// <summary>
    /// Each bot laps the course again and again: three laps in two minutes. (Two checkpoints 45 m apart give laps in plenty: a car in both
    /// spheres at once counts one every other frame, as in the original; the map check warns of it.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Courses))]
    public void RaceBotsTakeCheckpointsSet45MetresApart(string course, Vector2[] checks)
    {
        if (Nitro is null) return;
        var (file, mid) = MakeMap("close" + checks.Length + (checks.Length > 2 && checks[2].X != 0 ? "t" : ""), checks, 2);
        var host = new MeleeHost(new ServerOptions { Install = _install!.Root, Arena = file, Kind = MeleeKind.Race, GoalValue = 0, Bots = 2, MaxPlayers = 4, BotSkill = BotSkill.Hard, Seed = 3, BotCars = ["vppirna1.vcf"] });
        var game = host.Game;
        Assert.Equal(checks.Length, game.Checkpoints.Count);
        var laps = host.Bots.ToDictionary(b => b.Player, _ => new List<double>());
        double now = 0;
        game.LapCounted += p => laps[p].Add(now);
        int ticks = 0, n = (int)(120 / host.Session.TickSeconds), every = (int)(2 / host.Session.TickSeconds);
        while (ticks < n && !game.Ended)
        {
            host.Step(); ticks++; now = ticks * host.Session.TickSeconds;
            if (ticks % every == 0)
                output.WriteLine($"{now,5:0} s " + string.Join(" | ", host.Bots.Select(b => $"{b.Player.Car.Pos.X - mid.X:0},{b.Player.Car.Pos.Z - mid.Z:0} v {b.Player.Car.Vel.Length():0}")));
        }
        output.WriteLine($"{course}: laps " + string.Join(" | ", laps.Values.Select(l => l.Count + " (" + string.Join(", ", l.Select(x => x.ToString("0"))) + ")")));
        Assert.All(laps.Values, l => Assert.True(l.Count >= 3, $"{course}: a bot made {l.Count} laps in two minutes"));
    }
}
