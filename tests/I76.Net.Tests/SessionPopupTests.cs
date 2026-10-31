using I76.Net;
using I76.Sim.Missions;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The roster's game popup (a tester's crash: the Nitro Pack's arrow left a screen nothing closed). Its words for a game
/// as the Nitro Pack's lobby session info prints them, and the session answer surviving odd data from strangers.
/// </summary>
public class SessionPopupTests
{
    private static SessionInfo Game(MeleeKind kind = MeleeKind.Combat, MeleeGoal goal = MeleeGoal.None, int value = 0, float time = 0, bool team = false, byte teams = 2) =>
        new(1, Protocol.Version, "g", "N01.CBT", 0, 8, team, false, 0, time, false, [], kind, goal, value, teams);

    [Fact]
    public void The_game_type_is_the_originals()
    {
        Assert.Equal("Combat", NetLines.SessionType(Game()));
        Assert.Equal("Team Combat", NetLines.SessionType(Game(team: true)));
        Assert.Equal("Race", NetLines.SessionType(Game(MeleeKind.Race)));
        Assert.Equal("3 Team C.T.F.", NetLines.SessionType(Game(MeleeKind.Ctf, teams: 3)));
        Assert.Equal("Unknown", NetLines.SessionType(Game(MeleeKind.Ctf, teams: 9)));
    }

    [Fact]
    public void The_limit_is_the_originals()
    {
        Assert.Equal("3 Laps", NetLines.SessionLimit(Game(MeleeKind.Race, MeleeGoal.Laps, 3)));
        Assert.Equal("5 Captures", NetLines.SessionLimit(Game(MeleeKind.Ctf, MeleeGoal.Captures, 5)));
        Assert.Equal("20000 Points", NetLines.SessionLimit(Game(goal: MeleeGoal.Points, value: 20000)));
        Assert.Equal("20 Kills", NetLines.SessionLimit(Game(goal: MeleeGoal.Kills, value: 20)));
        Assert.Equal("10 Minutes", NetLines.SessionLimit(Game(time: 600)));
        Assert.Equal("No Limits!", NetLines.SessionLimit(Game(MeleeKind.Race, MeleeGoal.Laps, 0)));
        Assert.Equal("No Limits!", NetLines.SessionLimit(Game(time: float.NaN)));
        Assert.Equal("No Limits!", NetLines.SessionLimit(Game(time: float.PositiveInfinity)));
        Assert.Equal("No Limits!", NetLines.SessionLimit(Game(goal: MeleeGoal.Kills, value: -5)));
    }

    [Fact]
    public void A_full_answer_with_everything_set_round_trips()
    {
        var drivers = Enumerable.Range(0, 64).Select(i => new SessionDriver($"player {i}", i % 2 == 0 ? "vppirna1.vcf" : "", (byte)(i % 9))).ToArray();
        var info = new SessionInfo(7, Protocol.Version, "Big", "zz own.lvl", 64, 64, true, true, 0, 1800, false, drivers,
            MeleeKind.Combat, MeleeGoal.Kills, 50, 2, new byte[32], true, 1 << 20, true, I76.Sim.Trip.WeaponRestrictions.Normalize(true, []), true);
        var w = new NetWriter();
        info.Write(w);
        var back = SessionInfo.TryRead(w.Span);
        Assert.NotNull(back);
        Assert.True(back!.Locked && back.Hidden && back.Restricted.All && back.CustomMap);
        Assert.Equal("50 Kills", NetLines.SessionLimit(back));
    }

    [Fact]
    public void Garbage_and_cut_answers_are_no_answer_and_never_throw()
    {
        var info = new SessionInfo(7, Protocol.Version, "g", "m05", 1, 8, false, false, 0, 0, false, [new SessionDriver("a", "vppirna1.vcf", 1)],
            Restrictions: I76.Sim.Trip.WeaponRestrictions.Normalize(false, [(2, -1), (4, 1)]), Hidden: true);
        var w = new NetWriter();
        info.Write(w);
        var bytes = w.Span.ToArray();
        for (int n = 0; n <= bytes.Length; n++) SessionInfo.TryRead(bytes.AsSpan(0, n));
        var rand = new Random(1);
        for (int k = 0; k < 2000; k++)
        {
            var b = (byte[])bytes.Clone();
            b[rand.Next(b.Length)] = (byte)rand.Next(256);
            if (rand.Next(2) == 0) b[rand.Next(b.Length)] = (byte)rand.Next(256);
            SessionInfo.TryRead(b);
        }
    }
}
