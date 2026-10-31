using I76.Net;
using I76.Server;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// The radar's target keys in a network melee. Everyone in a free-for-all is on team 0 (the players and the AI fill),
/// and T and E took only another team, so nobody could target anybody (0.4.0 too); the target the server picks reaches
/// the client's predicted car.
/// </summary>
public class TargetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static int Id(Vehicle? v) => v is null ? 0 : v.NetId & 0xffffff;

    /// <summary>
    /// A free-for-all with three AI cars: T and E on the client pick the nearest AI car, and the client's copy of its car
    /// (predicted, the snapshots 4 ticks late) has that target, as the target box and the radar read it.
    /// </summary>
    [Fact]
    public void TheTargetKeysPickTheAiCarsInAFreeForAll()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7, Ai = 3 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var mirror = new ClientWorld(HostTests.Mirror("m01"), host.AcceptFor(seat));
        var client = new MeleeClient();
        var predictor = new OwnCarPredictor(mirror, client);
        var late = new Queue<Snapshot>();
        var real = seat.Player.Car;
        int picked = 0, checkedTicks = 0;
        for (uint t = 1; t <= 400; t++)
        {
            var input = new CarInput { Commands = t % 40 == 10 ? CarCommands.TargetNearest : t % 40 == 30 ? CarCommands.TargetNext : 0 };
            seat.Inputs.Add(new InputMessage(t, [input]));
            host.Step();
            host.TakeEvents();
            predictor.Step(t, input);
            late.Enqueue(PredictionTests.Through(host, seat));
            while (late.Count > 4) { client.Inject(late.Dequeue()); predictor.Reconcile(); mirror.Advance(0.05, client); }
            if (t % 40 != 15 && t % 40 != 35) continue;
            var contact = host.Setup.Combat.Contacts(real).FirstOrDefault();
            var server = host.Setup.Combat.RadarTarget.GetValueOrDefault(real);
            output.WriteLine($"tick {t}: nearest contact {Id(contact)}, server's target {Id(server)}, client's {Id(mirror.Setup.Combat.RadarTarget.GetValueOrDefault(mirror.Own!.Vehicle))}");
            if (contact is null) continue;
            checkedTicks++;
            Assert.NotNull(server);
            Assert.NotEqual(real, server);
            picked++;
            // the client has the server's target from the snapshot 4 ticks on (the key was pressed 5 ticks before)
            Assert.Equal(Id(server), Id(mirror.Setup.Combat.RadarTarget.GetValueOrDefault(mirror.Own!.Vehicle)));
        }
        Assert.True(checkedTicks >= 4, $"the AI cars were never radar contacts ({checkedTicks})");
        Assert.Equal(checkedTicks, picked);
    }

    /// <summary>Players target each other in a free-for-all; with team play a teammate is skipped and the AI fill (team 0) taken.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayersTargetEnemiesNotTeammates(bool teams)
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 7, Ai = teams ? 1 : 0, TeamPlay = teams });
        var a = host.Join("a", "vppirna1.vcf", teams ? (byte)1 : (byte)0);
        var b = host.Join("b", "vppirna1.vcf", teams ? (byte)1 : (byte)0);
        for (uint t = 1; t <= 3; t++) { a.Inputs.Add(new InputMessage(t, [default])); b.Inputs.Add(new InputMessage(t, [default])); host.Step(); }
        // b parked beside a, any AI car farther off
        var ca = a.Player.Car;
        var cb = b.Player.Car;
        cb.Pos = new I76.Sim.Math.Double3(ca.Pos.X + 12, ca.Pos.Y, ca.Pos.Z);
        // with team play, the AI car 40 m off: farther than the teammate
        var ai = host.Setup.World.Vehicles.FirstOrDefault(v => v != ca && v != cb);
        if (ai is not null) ai.Pos = new I76.Sim.Math.Double3(ca.Pos.X - 40, ca.Pos.Y, ca.Pos.Z);
        a.Inputs.Add(new InputMessage(4, [new CarInput { Commands = CarCommands.TargetNearest }]));
        b.Inputs.Add(new InputMessage(4, [default]));
        host.Step();
        var target = host.Setup.Combat.RadarTarget.GetValueOrDefault(ca);
        output.WriteLine($"teams {teams}: a's target {Id(target)} (b {Id(cb)})");
        if (!teams) Assert.Same(cb, target);
        else
        {
            Assert.NotNull(ai);
            Assert.Same(ai, target);
        }
    }
}
