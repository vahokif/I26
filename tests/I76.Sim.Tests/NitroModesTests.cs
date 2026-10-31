using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The Nitro race's lap counting and CTF's flag rules.</summary>
public class NitroModesTests
{
    private static Dictionary<int, Double3> Checks(bool finish = false)
    {
        var d = new Dictionary<int, Double3>
        {
            [1] = new(0, 0, 100), [2] = new(0, 0, 400), [3] = new(300, 0, 400),
        };
        if (finish) d[99] = new(0, 0, 0);
        return d;
    }

    private static Double3 At(Dictionary<int, Double3> c, int n) => c[n];

    [Fact]
    public void TheFirstPassOfCheck1OnlyArmsAndALapCountsOnTheWrap()
    {
        var c = Checks();
        var r = new RaceTracker(c);
        Assert.False(r.Update(new Double3(0, 0, 0)));          // behind check1: nothing
        Assert.False(r.Update(At(c, 1)));                      // the first pass arms
        Assert.Equal(0, r.Laps);
        Assert.False(r.Update(At(c, 2)));
        Assert.False(r.Update(At(c, 3)));
        Assert.Equal(0, r.Laps);
        Assert.True(r.Update(At(c, 1)));                       // the wrap: lap 1
        Assert.Equal(1, r.Laps);
        r.Update(At(c, 2)); r.Update(At(c, 3));
        Assert.True(r.Update(At(c, 1)));
        Assert.Equal(2, r.Laps);
    }

    [Fact]
    public void CheckpointsAreTakenInOrderWithA30MetreSphere()
    {
        var c = Checks();
        var r = new RaceTracker(c);
        r.Update(At(c, 1));
        // check3 before check2 does not count, and a missed one blocks until it is taken
        Assert.False(r.Update(At(c, 3)));
        Assert.Equal(0, r.Current);
        Assert.False(r.Update(At(c, 1)));
        Assert.Equal(0, r.Laps);
        // 29 m away (3-D) counts, 31 m does not; height matters
        Assert.False(r.Update(new Double3(31, 0, 400)));
        Assert.False(r.Update(new Double3(0, 31, 400)));
        Assert.False(r.Update(new Double3(0, 0, 400 - 25)) ? true : r.Current != 1);   // within: advances to check2 (index 1)
        Assert.Equal(1, r.Current);
    }

    [Fact]
    public void Check99IsAnOptionalFinishThatReplacesCheck1OnTheWrap()
    {
        var c = Checks(finish: true);
        var r = new RaceTracker(c);
        r.Update(At(c, 1));
        r.Update(At(c, 2)); r.Update(At(c, 3));
        // the wrap now goes through the finish: check1 does not count
        Assert.False(r.Update(At(c, 1)));
        Assert.Equal(0, r.Laps);
        Assert.True(r.Update(At(c, 99)));
        Assert.Equal(1, r.Laps);
        // the finish stands for check1: next is check2
        Assert.False(r.Update(At(c, 2)));
        Assert.False(r.Update(At(c, 3)));
        Assert.True(r.Update(At(c, 99)));
        Assert.Equal(2, r.Laps);
    }

    private static MeleePlayer Player(int team, Double3 pos)
    {
        var car = new Vehicle { Pos = pos };
        return new MeleePlayer { Car = car, Entity = new Entity("plyr", [], 1, 0x10u | (uint)team << 16), CarFile = "x.vcf", Name = "p" + team };
    }

    private static (CtfGame Game, List<string> Log) Ctf()
    {
        var g = new CtfGame();
        g.AddFlag(1, new Double3(0, 0, 0));
        g.AddFlag(2, new Double3(1000, 0, 0));
        var log = new List<string>();
        g.Message += log.Add;
        return (g, log);
    }

    private static void Tick(CtfGame g, params MeleePlayer[] ps) => g.Update(ps, _ => true, _ => 2f);

    [Fact]
    public void AFlagIsInactiveUntilItsTeamHasAPlayer()
    {
        var (g, _) = Ctf();
        var a = Player(1, new Double3(0, 0, 0));
        var b = Player(2, new Double3(1000, 0, 0));
        Tick(g, a);
        Assert.Equal(CtfFlag.Home, g.Flags[0].State);
        Assert.Equal(CtfFlag.Inactive, g.Flags[1].State);
        // team 1 reaches the stand of team 2, which has nobody: nothing to take
        a.Car.Pos = new Double3(1000, 0, 0);
        Tick(g, a);
        Assert.Null(g.Carried(a));
        a.Car.Pos = new Double3(500, 0, 0);
        Tick(g, a, b);                                         // team 2 arrives: its flag is home
        Assert.Equal(CtfFlag.Home, g.Flags[1].State);
    }

    [Fact]
    public void PickupByAnEnemyFromTheHomeStandOnly_AndYourOwnFlagIsNeverReturnedByTouch()
    {
        var (g, log) = Ctf();
        var a = Player(1, new Double3(0, 0, 0));
        var b = Player(2, new Double3(1000, 0, 0));
        Tick(g, a, b);                                         // both at their own stands: nothing
        Assert.All(g.Flags, f => Assert.Equal(CtfFlag.Home, f.State));
        a.Car.Pos = new Double3(1000, 0, 0);                   // the enemy stand
        b.Car.Pos = new Double3(500, 0, 0);
        Tick(g, a, b);
        Assert.Equal(CtfFlag.Carried, g.Flags[1].State);
        Assert.Same(a, g.Flags[1].Carrier);
        Assert.Contains(log, l => l.Contains("flag has been taken"));
        // the enemy touches the carried flag's stand again: it is no longer there to take
        b.Car.Pos = new Double3(1000, 0, 0);
        Tick(g, a, b);
        Assert.Same(a, g.Flags[1].Carrier);
        // the flag follows its carrier
        a.Car.Pos = new Double3(600, 0, 0);
        Tick(g, a, b);
        Assert.Equal(600, g.Flags[1].Pos.X);
    }

    [Fact]
    public void CaptureAtAnyStandOfYourTeam_ScoresEveryMemberAndCostsTheLosersADeath()
    {
        var (g, log) = Ctf();
        var a = Player(1, new Double3(1000, 0, 0));
        var a2 = Player(1, new Double3(500, 0, 500));
        var b = Player(2, new Double3(500, 0, -500));
        Tick(g, a, a2, b);                                     // a takes team 2's flag
        Assert.Same(a, g.Flags[1].Carrier);
        a.Car.Pos = new Double3(0, 0, 0);                      // home with its own flag at home: capture
        Tick(g, a, a2, b);
        Assert.Equal(CtfFlag.Home, g.Flags[1].State);
        Assert.Null(g.Carried(a));
        Assert.Equal(1, a.Captures);
        Assert.Equal(1, a2.Captures);
        Assert.Equal(0, b.Captures);
        Assert.Equal(1, b.Deaths);
        Assert.Equal(1, g.TeamCaptures[1]);
        Assert.Contains(log, l => l.Contains("has captured"));
    }

    [Fact]
    public void ACaptureCountsEvenIfYourOwnFlagIsAway()
    {
        var (g, _) = Ctf();
        var a = Player(1, new Double3(1000, 0, 0));
        var b = Player(2, new Double3(0, 0, 0));
        Tick(g, a, b);                                         // a takes flag 2; b takes flag 1 at the same time
        Assert.Same(b, g.Flags[0].Carrier);
        a.Car.Pos = new Double3(0, 0, 0);                      // a at its own stand while its flag is carried off
        Tick(g, a, b);
        Assert.Equal(1, a.Captures);
    }

    [Fact]
    public void TheFlagStaysOnTheWreckAndGoesHomeWhenTheCarIsGone()
    {
        var (g, log) = Ctf();
        var a = Player(1, new Double3(1000, 0, 0));
        var b = Player(2, new Double3(2000, 0, 0));
        Tick(g, a, b);
        Assert.Same(a, g.Flags[1].Carrier);
        // the carrier is destroyed: not alive, nobody can take it, it stays
        a.Car.Pos = new Double3(1200, 0, 0);
        g.Update([a, b], p => p == b, _ => 2f);
        Assert.Equal(CtfFlag.Carried, g.Flags[1].State);
        Assert.Equal(1200, g.Flags[1].Pos.X);
        b.Car.Pos = new Double3(1200, 0, 0);
        g.Update([a, b], p => p == b, _ => 2f);
        Assert.Same(a, g.Flags[1].Carrier);
        // the respawn (or removal) returns it
        g.CarrierGone(a);
        Assert.Equal(CtfFlag.Home, g.Flags[1].State);
        Assert.Equal(1000, g.Flags[1].Pos.X);
        Assert.Contains(log, l => l.Contains("returned to its base"));
    }

    [Fact]
    public void ATeamThatLeavesDeactivatesItsFlag()
    {
        var (g, log) = Ctf();
        var a = Player(1, new Double3(0, 0, 0));
        var b = Player(2, new Double3(1000, 0, 0));
        Tick(g, a, b);
        Tick(g, a);
        Assert.Equal(CtfFlag.Inactive, g.Flags[1].State);
        Assert.Contains(log, l => l.Contains("has left the game"));
    }

    [Fact]
    public void FlagTeamIsTheNumberInTheName()
    {
        Assert.Equal(1, CtfGame.FlagTeam(1));
        Assert.Equal(4, CtfGame.FlagTeam(4));
    }

    [Fact]
    public void TheGoalCountIsTheLeadersPerPlayerOrPerTeam()
    {
        (int, int, int)[] rows = [(1, 100, 2), (1, 50, 3), (2, 400, 1)];
        Assert.Equal(3, GoalCount.Leader(MeleeGoal.Laps, false, rows));
        Assert.Equal(5, GoalCount.Leader(MeleeGoal.Laps, true, rows));        // a team race sums the laps
        Assert.Equal(3, GoalCount.Leader(MeleeGoal.Captures, true, rows));    // captures: every member holds the team's count
        Assert.Equal(400, GoalCount.Leader(MeleeGoal.Points, false, rows));
        Assert.Equal(0, GoalCount.Leader(MeleeGoal.Laps, false, []));
    }

    [Fact]
    public void TheGoalFallsBackToTheKindsOwn()
    {
        Assert.Equal(MeleeGoal.Laps, new MeleeRules { Kind = MeleeKind.Race }.ResolvedGoal);
        Assert.Equal(MeleeGoal.Captures, new MeleeRules { Kind = MeleeKind.Ctf }.ResolvedGoal);
        Assert.Equal(MeleeGoal.Points, new MeleeRules { Network = true, MaxScore = 5000 }.ResolvedGoal);
        Assert.Equal(5000, new MeleeRules { MaxScore = 5000 }.ResolvedValue);
        Assert.Equal(3, new MeleeRules { MaxScore = 5000, GoalValue = 3, Goal = MeleeGoal.Kills }.ResolvedValue);
        // unlimited laps: a points limit does not leak into a race
        Assert.Equal(0, new MeleeRules { Kind = MeleeKind.Race, MaxScore = 5000, GoalValue = 0 }.ResolvedValue);
        Assert.Equal(0, new MeleeRules { Kind = MeleeKind.Ctf, MaxScore = 5000 }.ResolvedValue);
    }
}
