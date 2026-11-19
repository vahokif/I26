using System.Numerics;
using I76.Formats.Mission.Edit;
using I76.Server;
using I76.Sim.Damage;
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

    /// <summary>
    /// A race bot killed mid-lap on a course that comes back past the start (docs/multiplayer.md "Bots"): two lanes 100 m up with a
    /// 50 m drop between them, check1 to check3 up the first, check4 across the top, check5 to check7 down the second to beside the
    /// start, and a finish far up the second lane (so check1 is not in the lap after it). Back at a spawn point and wanting check3, the
    /// bot drives the course from check1, up its own lane, and takes check3 without dying again. (The nearest leg to the spawn
    /// points is the second lane's, across the drop: from there it went off the edge, came back and went off it again.)
    /// </summary>
    [Fact]
    public void AKilledRaceBotTakesTheCourseFromCheck1NotTheNearestLaneAcrossADrop()
    {
        if (Nitro is null) return;
        var inst = _install ??= new ScratchMapInstall(Nitro!, "race");
        var proj = MapProject.CreateBlank("twolanes", "Two lanes", ".rac", 2, 2, 1000);
        var b = proj.WorldBounds();
        var mid = new Vector3((b.X + b.Z) / 2, 100f, (b.Y + b.W) / 2);
        // the drop: a trench 50 m wide and 660 m long between x 35 and 85, from 160 m behind the start to the top's band
        for (int sx = MapProject.SampleOf(mid.X + 35); sx <= MapProject.SampleOf(mid.X + 85); sx++)
            for (int sz = MapProject.SampleOf(mid.Z - 160); sz <= MapProject.SampleOf(mid.Z + 500); sz++)
                proj.Terrain.SetHeight(sx, sz, 0);
        Vector2[] checks = [new(0, 100), new(0, 250), new(0, 450), new(60, 580), new(120, 450), new(120, 250), new(120, 20)];
        for (int i = 0; i < checks.Length; i++) proj.Mission.Objects.Add(MapObject.Checkpoint(i + 1, mid + new Vector3(checks[i].X, 0, checks[i].Y)));
        proj.Mission.Objects.Add(MapObject.Checkpoint(99, mid + new Vector3(120, 0, 400)));
        proj.Mission.Objects.Add(MapObject.Spawn(mid + new Vector3(-8, 0, 40), 0));
        proj.Mission.Objects.Add(MapObject.Spawn(mid + new Vector3(8, 0, 40), 0));
        foreach (var (name, data) in proj.ToFiles()) inst.Put(name, data);

        var host = new MeleeHost(new ServerOptions { Install = inst.Root, Arena = proj.MissionFileName, Kind = MeleeKind.Race, GoalValue = 0, Bots = 1, MaxPlayers = 2, BotSkill = BotSkill.Hard, Seed = 3, BotCars = ["vppirna1.vcf"] });
        var game = host.Game;
        var bot = host.Bots[0].Player;
        RaceTracker Tracker() => game.RaceOf(bot)!;
        double dt = host.Session.TickSeconds, t = 0;
        while (t < 120 && !(game.RaceOf(bot) is { Current: 1 })) { host.Step(); t += dt; }
        Assert.True(t < 120, "the bot took check1 and check2");
        Assert.Equal(0, bot.Deaths);
        host.Setup.Damage.Destroy(bot.Car);
        while (t < 200 && (bot.Destroyed || DamageModel.IsDead(bot.Car))) { host.Step(); t += dt; }
        double back = t;
        output.WriteLine($"killed by check3, back at {bot.Car.Pos.X - mid.X:0},{bot.Car.Pos.Z - mid.Z:0} after {back:0} s");
        Assert.Equal(1, bot.Deaths);
        Assert.Equal(1, Tracker().Current);
        double took = 0, lowest = double.MaxValue;
        while (t - back < 90 && took == 0)
        {
            host.Step(); t += dt;
            lowest = System.Math.Min(lowest, bot.Car.Pos.Y);
            if (Tracker().Current != 1) took = t - back;
        }
        output.WriteLine($"took check3 {took:0} s after; lowest {lowest:0.0} m");
        Assert.True(took > 0, "took check3 within 90 s of coming back");
        Assert.Equal(2, Tracker().Current);
        Assert.Equal(1, bot.Deaths);
        Assert.True(lowest > 90, "stayed up on the lane");
    }

    /// <summary>
    /// A course in two parts joined by a regen pad, as some maps make a long course (a pad sends a car to a spawn point, with the race's
    /// count kept): check1 to check4 out and back round the west of a flat map, a pad by check4, check5 by the spawn points, then
    /// check6; a road from check4 round the south to check5. A bot driven onto the pad after taking check4 comes back at a spawn point
    /// and drives straight on to check5. (It kept the route the AI had planned along the road from check4 and drove from the spawn
    /// point back to it, into a wall on the map it was reported on, until bumped; and a bot back at a spawn point drove the course
    /// from check1, round the first part and onto its pad again.)
    /// </summary>
    [Fact]
    public void ARaceBotSentToTheStartByARegenPadDrivesOnToTheNextCheckpoint()
    {
        if (Nitro is null) return;
        var inst = _install ??= new ScratchMapInstall(Nitro!, "race");
        var proj = MapProject.CreateBlank("regenrun", "Regen run", ".rac", 2, 2, 100);
        var b = proj.WorldBounds();
        var mid = new Vector3((b.X + b.Z) / 2, 10f, (b.Y + b.W) / 2);
        // a road from check4's end round the south of the map to check5: the AI's route from there to check5 goes along it, away from the
        // spawn points
        Vector2[] bends = [new(-400, 140), new(-400, -200), new(80, -200), new(80, 100)];
        var road = new List<RoadEditPoint>();
        for (int k = 0; k + 1 < bends.Length; k++)
        {
            var dir = Vector2.Normalize(bends[k + 1] - bends[k]);
            var side = new Vector2(-dir.Y, dir.X) * 5;
            float len = Vector2.Distance(bends[k], bends[k + 1]);
            for (float d = 0; d < len || k + 2 == bends.Length && d <= len; d += 5)
            {
                var c = new Vector2(mid.X, mid.Z) + bends[k] + dir * d;
                road.Add(new RoadEditPoint(c - side, c + side));
            }
        }
        proj.Mission.Roads.Add(new RoadEdit(1, road));
        Vector2[] checks = [new(0, 100), new(0, 550), new(-400, 550), new(-400, 100), new(80, 100), new(80, 450)];
        for (int i = 0; i < checks.Length; i++) proj.Mission.Objects.Add(MapObject.Checkpoint(i + 1, mid + new Vector3(checks[i].X, 0, checks[i].Y)));
        var pad = mid + new Vector3(-400, 0, 80);
        proj.Mission.Objects.Add(MapObject.Regen(pad));
        proj.Mission.Objects.Add(MapObject.Spawn(mid + new Vector3(-8, 0, 40), 0));
        proj.Mission.Objects.Add(MapObject.Spawn(mid + new Vector3(8, 0, 40), 0));
        foreach (var (name, data) in proj.ToFiles()) inst.Put(name, data);

        var host = new MeleeHost(new ServerOptions { Install = inst.Root, Arena = proj.MissionFileName, Kind = MeleeKind.Race, GoalValue = 0, Bots = 1, MaxPlayers = 2, BotSkill = BotSkill.Hard, Seed = 3, BotCars = ["vppirna1.vcf"] });
        var game = host.Game;
        var bot = host.Bots[0].Player;
        RaceTracker Tracker() => game.RaceOf(bot)!;
        double dt = host.Session.TickSeconds, t = 0;
        // on its way to check5 with its route planned, 30 s after the start (a pad renews a car once every 30 s)
        var c5 = mid + new Vector3(checks[4].X, 0, checks[4].Y);
        bool ToCheck5() => bot.Entity is { PlanState: 0, Waypoints: [.., var last] } && new Vector2(last.X - c5.X, last.Z - c5.Z).Length() < 15;
        while (t < 150 && !(game.RaceOf(bot) is { Current: 3 } && ToCheck5())) { host.Step(); t += dt; }
        for (double s = t; t < s + 1; t += dt) host.Step();
        output.WriteLine($"took check4 and on its way to check5 at {t:0.0} s, at {bot.Car.Pos.X - mid.X:0},{bot.Car.Pos.Z - mid.Z:0}");
        Assert.True(t is > 31 and < 150, "the bot took check4 at least 30 s after the start");
        Assert.Equal(0, bot.Deaths);
        var at = bot.Car.Pos;
        output.WriteLine($"the AI's route to check5: waypoint {bot.Entity.WaypointIndex} of {bot.Entity.Waypoints.Count}");
        bot.Car.Pos = new(pad.X, at.Y, pad.Z);
        host.Step(); t += dt;
        Assert.Equal(400, bot.Score);
        Assert.True(System.Math.Abs(bot.Car.Pos.Z - (mid.Z + 40)) < 20, "back at a spawn point");
        double back = t, took = 0, west = 0;
        while (t - back < 30 && took == 0)
        {
            host.Step(); t += dt;
            west = System.Math.Min(west, bot.Car.Pos.X - mid.X);
            if (Tracker().Current != 3) took = t - back;
        }
        output.WriteLine($"took check{Tracker().Current + 1} {took:0.0} s after the pad; furthest west {west:0}");
        Assert.Equal(4, Tracker().Current);
        Assert.True(took is > 0 and < 12, "took check5 within 12 s of the pad");
        Assert.True(west > -100, "went nowhere near the pad's end of the old route");
    }
}
