using I76.Net;
using I76.Server;
using I76.Sim.Rng;
using I76.Sim.Session;
using Xunit;

namespace I76.Net.Tests;

/// <summary>The rand stream's seed: the clock's by default (srand(time(NULL)), as the original), fixed by --seed N.</summary>
public class SeedTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);

    [Fact]
    public void TheClockSeedsUnlessASeedIsFixed()
    {
        Assert.Equal(1_000_000_000u, RandSeed.FromClock(() => T0));
        Assert.NotEqual(RandSeed.Pick(null, () => T0), RandSeed.Pick(null, () => T0.AddSeconds(1)));
        Assert.Equal(42u, RandSeed.Pick(42, () => T0));
        Assert.Equal(RandSeed.Pick(42, () => T0), RandSeed.Pick(42, () => T0.AddHours(1)));
    }

    /// <summary>A server's melee (one scripted human, three AI cars) for 400 ticks: the cars' hash each tick.</summary>
    private static List<ulong> Melee(uint seed)
    {
        var host = new MeleeHost(new ServerOptions { Install = HostTests.InstallDir!, Arena = "m01", Ai = 3, Seed = unchecked((int)seed) });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var hashes = new List<ulong>();
        for (uint t = 1; t <= 400; t++)
        {
            seat.Inputs.Add(new InputMessage(t, [new CarInput { Buttons = CarButtons.ThrottleUp | CarButtons.Fire | (t % 100 < 30 ? CarButtons.SteerLeft : 0) }]));
            host.Step();
            var h = new StateHash();
            foreach (var v in host.Setup.World.Vehicles) h.Add(v);
            hashes.Add(h.Value);
        }
        return hashes;
    }

    /// <summary>Two runs with the same --seed are the same run; two started at different times are not.</summary>
    [Fact]
    public void TheSameSeedPlaysTheSameMeleeAndTwoClocksDoNot()
    {
        if (HostTests.InstallDir is null) return;
        Assert.Equal(Melee(7), Melee(7));
        Assert.NotEqual(Melee(RandSeed.Pick(null, () => T0)), Melee(RandSeed.Pick(null, () => T0.AddMinutes(1))));
    }
}
