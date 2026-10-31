using System.Net;
using I76.Net.Anet;
using Xunit;

namespace I76.Net.Tests.Anet;

/// <summary>64 seats: the listing's counts and the direct answer's driver list, in their fields and one datagram.</summary>
public class ManyPlayersTests
{
    private static readonly AnetAddress Host = AnetAddress.From(new IPEndPoint(IPAddress.Loopback, 7676));

    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 37)]
    [InlineData(127, 100)]
    public void SeatsAndPlayersSurviveBothRecordForms(int max, int players)
    {
        var tag = new I26SessionTag(Protocol.Version, true, true, false, false, I76.Sim.Missions.MeleeKind.Ctf, 4, I76.Sim.Missions.MeleeGoal.Captures, "CTF1.CF4");
        var s = new AnetSession(Host, 0x1234, Host, max, players, AnetSession.FlagsOpen, "Big game", "", tag.Write());
        var raw = AnetSession.Unpack(s.Subkey(), s.PackRaw(0x4926))!;
        Assert.Equal((max, players), (raw.MaxPlayers, raw.Players));
        var compact = AnetSession.Unpack(s.Subkey(), s.PackCompact())!;
        Assert.Equal((max, players), (compact.MaxPlayers, compact.Players));
        Assert.Equal(tag, I26SessionTag.Read(compact.User));
    }

    private static SessionInfo Info(int drivers, string name = "driver") => new(1, Protocol.Version, "Big game", "m01.msn", drivers, 64, true, false, 20000, 0, false,
        [.. Enumerable.Range(0, drivers).Select(i => new SessionDriver($"{name}{i}", "vppirna1.vcf", (byte)(1 + i % 7)))]);

    [Fact]
    public void SixtyFourSeatsFitTheDirectAnswer()
    {
        var w = new NetWriter(2048);
        Info(64).Write(w);
        Assert.True(w.Length <= SessionInfo.AnswerBytes, $"{w.Length} bytes");
        var back = SessionInfo.TryRead(w.ToArray())!;
        // the counts are the game's; the list is what fits one datagram: most of 64 with short names
        Assert.Equal((64, 64), (back.Players, back.MaxPlayers));
        Assert.InRange(back.Drivers.Count, 48, 64);
        Assert.Equal(Info(64).Drivers.Take(back.Drivers.Count), back.Drivers);
    }

    [Fact]
    public void LongNamesCutTheDriverListInsteadOfBreakingTheDatagram()
    {
        var w = new NetWriter(4096);
        var info = Info(64, new string('x', 22));
        info.Write(w);
        Assert.True(w.Length <= SessionInfo.AnswerBytes, $"{w.Length} bytes");
        var back = SessionInfo.TryRead(w.ToArray())!;
        Assert.Equal(64, back.Players);                            // the count is the game's, the list is what fit
        Assert.InRange(back.Drivers.Count, 16, 63);
        Assert.Equal(info.Drivers.Take(back.Drivers.Count), back.Drivers);
    }
}
