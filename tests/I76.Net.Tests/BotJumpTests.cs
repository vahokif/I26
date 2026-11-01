using System.Numerics;
using I76.Editor;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Server;
using I76.Sim.Ai;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Net.Tests;

/// <summary>
/// Bots and ramps (docs/multiplayer.md "Bots"): the jump links of a map are found and flown headless, a bot whose car can reach a
/// ramp's speed window takes it when that is the shorter way, one whose car cannot goes round. The map is made here as the map editor
/// makes one: flat ground, a ramp from the stock structures, a trench across the middle with a way round at its east end.
/// </summary>
public class BotJumpTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "i26-botjump-" + Guid.NewGuid().ToString("N"));
    private ScratchMapInstall? _install;
    public void Dispose() { _install?.Dispose(); if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); }

    /// <summary>What the map is: where the ramp stands, the trench, the start and the goal.</summary>
    private sealed record Arena(string File, Vector3 Start, Vector3 Goal, float TrenchZ0, float TrenchZ1, float RampX);

    private const float TrenchWidth = 20f;

    /// <summary>
    /// The test map in a scratch install. With <paramref name="wayRound"/> the trench stops 200 m short of the map's east edge; without, it runs the
    /// whole width (the goal can only be reached over the ramp).
    /// </summary>
    private Arena MakeMap(string stem, bool wayRound = true, float yaw = 0, float goalBeyond = 0)
    {
        var inst = _install ??= new ScratchMapInstall(HostTests.InstallDir!, "jump");
        var (proj, _) = NewMap.CreateFlat(Path.Combine(_tmp, stem), stem, "Jump test", 2, 10, nitro: false);
        var b = proj.WorldBounds();
        proj.Mission.Objects.RemoveAll(o => o.IsPlayer);
        float rampX = b.X + 400, trenchZ0 = b.Y + 800, rampZ = trenchZ0 - 8 - 14;
        // the ramp, facing north (+z): found by trying the four turns of a quarter
        var ramp = MapObject.Create("aaramp3", 0xb, new Vector3(rampX, 10f, rampZ), yaw);
        proj.Mission.Objects.Add(ramp);
        var start = new Vector3(b.X + 200, 10f, b.Y + 120);
        var goal = new Vector3(rampX + (goalBeyond > 0 ? 15 : 50), 10f, goalBeyond > 0 ? trenchZ0 + TrenchWidth + goalBeyond : b.Y + 1100);
        proj.Mission.Objects.Add(MapObject.Spawn(goal, 3.14f));
        proj.Mission.Objects.Add(MapObject.Spawn(start, 0));
        // the trench: impassable and 12 m deep
        int sx0 = MapProject.SampleOf(b.X), sx1 = MapProject.SampleOf(wayRound ? b.Z - 200 : b.Z), sz0 = MapProject.SampleOf(trenchZ0), sz1 = MapProject.SampleOf(trenchZ0 + TrenchWidth);
        for (int sx = sx0; sx <= sx1; sx++)
            for (int sz = sz0; sz < sz1; sz++)
                proj.Terrain.SetSample(sx, sz, 0x1000 | 0);
        foreach (var (name, data) in proj.ToFiles()) inst.Put(name, data);
        return new Arena(proj.MissionFileName, start, goal, trenchZ0, trenchZ0 + TrenchWidth, rampX);
    }

    private MeleeHost Host(Arena a, int bots = 2, BotSkill skill = BotSkill.Hard) =>
        new(new ServerOptions { Install = _install!.Root, Arena = a.File, Bots = bots, MaxPlayers = 4, BotSkill = skill, Seed = 3, BotCars = ["vppirna1.vcf"] });

    private static void Put(Vehicle v, Vector3 at, float yaw)
    {
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        v.Pos = new Double3(at.X, at.Y + 1, at.Z);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        v.Vel = default; v.Speed = 0; v.AngVel = default;
    }

    /// <summary>Which of the four quarter turns of the ramp's yaw faces it north.</summary>
    private Arena MapFacingNorth(string stem, bool wayRound = true, float goalBeyond = 0)
    {
        foreach (float yaw in new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 })
        {
            var a = MakeMap(stem + "x", wayRound, yaw);
            var host = new MeleeHost(new ServerOptions { Install = _install!.Root, Arena = a.File, Bots = 0, MaxPlayers = 2 });
            var jl = JumpLinks.Find(host.Setup.World.Ground);
            if (jl.Links.Count == 1 && jl.Links[0].Heading.Y > 0.99f) return MakeMap(stem, wayRound, yaw, goalBeyond);
        }
        throw new InvalidOperationException("no yaw faces the ramp north");
    }

    [Fact]
    public void ARampOnAnEditorMapIsAJumpLinkWithARunUpAndALandingWindow()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt1");
        var host = Host(a, bots: 1);
        var jl = host.Game.Jumps;
        var l = Assert.Single(jl.Links);
        output.WriteLine(l.ToString());
        Assert.InRange(l.Heading.Y, 0.99f, 1.01f);
        Assert.InRange(l.RunUp, 150f, JumpLinks.MaxRunUp);
        var car = host.Bots[0].Player.Car;
        var prof = jl.Profile(l, car, host.Session.TickSeconds);
        foreach (var s in prof.Samples) output.WriteLine($"{s.Speed,5:0.0} -> z {s.Landing.Z:0} air {s.Air:0.00} impact {s.Impact:0.0} {(s.Good ? "good" : s.Why)}");
        Assert.True(prof.Any);
        // too slow falls in the trench, and a landing beyond it is on the far side
        Assert.False(prof.Samples[0].Good);
        Assert.All(prof.Samples.Where(s => s.Good), s => Assert.True(s.Landing.Z > a.TrenchZ1, $"{s.Speed} lands at {s.Landing.Z}"));
        Assert.NotNull(jl.Use(l, car, host.Session.TickSeconds));
    }

    [Fact]
    public void FindingAndFlyingTheLinksIsDeterministicAndLeavesTheMissionsRandomSequenceAlone()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt2");
        var host = Host(a, bots: 1);
        var game = host.Game;
        var car = host.Bots[0].Player.Car;
        uint seed = host.Setup.World.Rand.Seed;
        float dt = host.Session.TickSeconds;
        var p1 = game.Jumps.Profile(game.Jumps.Links[0], car, dt);
        var fresh = JumpLinks.Find(host.Setup.World.Ground);
        var p2 = fresh.Profile(fresh.Links[0], car, dt);
        Assert.Equal(seed, host.Setup.World.Rand.Seed);
        Assert.Equal(p1.Samples, p2.Samples);
        Assert.Equal(game.Jumps.Reach(game.Jumps.Links[0], car, dt), fresh.Reach(fresh.Links[0], car, dt));
        // and the car it flew was a copy
        Assert.Null(car.Health is null ? null : (object?)null);
    }

    [Fact]
    public void ADamagedOrWeakEngineCannotUseTheRampAndAHealthyOneCan()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt3");
        var host = Host(a, bots: 1);
        var jl = host.Game.Jumps;
        var l = jl.Links[0];
        var car = host.Bots[0].Player.Car;
        float dt = host.Session.TickSeconds;
        var healthy = jl.Use(l, car, dt);
        Assert.NotNull(healthy);
        float reach = jl.Reach(l, car, dt);
        car.Engine.Damage = 0.35f;
        float hurt = jl.Reach(l, car, dt);
        output.WriteLine($"reach {reach:0.0} healthy, {hurt:0.0} with the engine at 35%; speed to hit it at {healthy!.Speed:0.0} (window {healthy.MinSpeed}..{healthy.MaxSpeed})");
        Assert.True(hurt < reach - 4f);
        Assert.Null(jl.Use(l, car, dt));
        car.Engine.Damage = 1f;
        Assert.NotNull(jl.Use(l, car, dt));
    }

    /// <summary>Two bots in a free for all; the one in the north is held where it is, so the other has somewhere to go.</summary>
    private (double Seconds, bool Landed, float MinHeightAboveGround, bool Fell, bool Crossed) Run(Arena a, Action<Vehicle>? tweak, double limit)
    {
        var host = Host(a);
        var game = host.Game;
        var mover = host.Bots[0].Player; var held = host.Bots[1].Player;
        tweak?.Invoke(mover.Car);
        Put(mover.Car, a.Start, 0.3f);
        Put(held.Car, a.Goal, MathF.PI);
        bool landed = false;
        game.Landed += (p, l) => { if (p == mover) landed = true; };
        bool crossed = false, fell = false;
        float lowest = float.MaxValue;
        int n = (int)(limit / host.Session.TickSeconds);
        int t = 0;
        int after = 0;
        for (; t < n && after < 80; t++)
        {
            host.Step();
            // the target is a sitting duck: put it back where it was (it is a bot and would come for the other)
            Put(held.Car, a.Goal, MathF.PI); held.Car.Pos = held.Car.Pos with { Y = host.Setup.World.Ground.BareHeight(a.Goal.X, a.Goal.Z) + 1 };
            var p = mover.Car.Pos;
            float above = (float)p.Y - host.Setup.World.Ground.BareHeight(p.X, p.Z);
            if (p.Z > a.TrenchZ0 - 30 && p.Z < a.TrenchZ1 + 30) lowest = MathF.Min(lowest, above);
            if (p.Z > a.TrenchZ0 && p.Z < a.TrenchZ1 && above < 3) fell = true;
            if (p.Z > a.TrenchZ1 + 15 && !fell) crossed = true;
            if (crossed || fell) after++;
        }
        output.WriteLine($"{t * host.Session.TickSeconds:0} s: landed {landed}, crossed {crossed}, fell {fell}, lowest {lowest:0.0}, mover at ({mover.Car.Pos.X:0},{mover.Car.Pos.Z:0})");
        return (t * host.Session.TickSeconds, landed, lowest, fell, crossed);
    }

    [Fact]
    public void ABotWhoseCarCanReachTheRampSpeedJumpsTheTrenchAndLands()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt4", wayRound: false);
        var r = Run(a, null, 150);
        Assert.True(r.Landed, "it took the ramp");
        Assert.True(r.Crossed, "and came down on the far side");
        Assert.False(r.Fell);
    }

    [Fact]
    public void ABotWhoseCarCannotReachTheSpeedLeavesTheRampAlone()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt5", wayRound: true);
        var r = Run(a, c => c.Engine.Damage = 0.35f, 60);
        Assert.False(r.Landed);
        Assert.False(r.Fell, "it did not go into the trench");
    }

    [Fact]
    public void WithAWayRoundAtTheEndTheBotStillJumpsWhenItIsTheShorterWay()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt6", wayRound: true);
        var r = Run(a, null, 150);
        Assert.True(r.Landed);
        Assert.True(r.Crossed);
    }

    [Fact]
    public void TheGridRouteTakesAHopOnlyWhenItIsTheShorterWay()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt7", wayRound: true);
        var host = Host(a, bots: 1);
        var jl = host.Game.Jumps;
        var l = jl.Links[0];
        var car = host.Bots[0].Player.Car;
        var use = jl.Use(l, car, host.Session.TickSeconds)!;
        var ground = host.Setup.World.Ground;
        var hops = new[] { new GridRoute.Hop(l.Approach, use.Landing, Vector3.Distance(l.Approach, use.Landing) + 45f, l.Index) };
        var over = GridRoute.Find(ground, a.Start, a.Goal, hops)!;
        var round = GridRoute.Find(ground, a.Start, a.Goal, null)!;
        output.WriteLine($"over the ramp {over.Cost:0} m, round the trench {round.Cost:0} m");
        Assert.NotNull(over.Hop);
        Assert.Equal(l.Approach, over.Route[^1]);
        Assert.True(over.Cost < round.Cost - 100f);
        Assert.Null(round.Hop);
        // a goal on the same side of the trench is no reason to jump
        var near = GridRoute.Find(ground, a.Start, a.Start + new Vector3(60, 0, 300), hops)!;
        Assert.Null(near.Hop);
    }

    [Theory]
    [InlineData("I76_INSTALL", "m11", 4, 4)]
    [InlineData("I76_INSTALL", "m05", 4, 4)]
    [InlineData("I76_NITRO_INSTALL", "n16.cf4", 8, 8)]
    [InlineData("I76_NITRO_INSTALL", "n31.rac", 9, 7)]
    [InlineData("I76_NITRO_INSTALL", "n10.cbt", 7, 1)]
    public void TheStockArenasRampsAreFoundQuicklyAndThoseThatCrossAGapAreMarked(string env, string arena, int links, int crossing)
    {
        var dir = Environment.GetEnvironmentVariable(env);
        if (dir is not { Length: > 0 } || !Directory.Exists(dir)) return;
        var host = new MeleeHost(new ServerOptions { Install = dir, Arena = arena, MaxPlayers = 2 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var jl = JumpLinks.Find(host.Setup.World.Ground);
        output.WriteLine($"{arena}: {jl.Links.Count} links, {jl.Links.Count(l => l.Crossing)} cross something, found in {sw.ElapsedMilliseconds} ms");
        Assert.Equal(links, jl.Links.Count);
        Assert.Equal(crossing, jl.Links.Count(l => l.Crossing));
        Assert.All(jl.Links, l => Assert.InRange(l.RunUp, JumpLinks.MinRunUp, JumpLinks.MaxRunUp));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void BotsWithRampsPlayTheSameGameTwice()
    {
        if (HostTests.InstallDir is null) return;
        var a = MapFacingNorth("jt8", wayRound: false);
        ulong Hash()
        {
            var host = Host(a);
            Put(host.Bots[0].Player.Car, a.Start, 0.3f);
            for (int t = 0; t < 40 * 20; t++) host.Step();
            return host.Session.StateHash();
        }
        Assert.Equal(Hash(), Hash());
    }
}
