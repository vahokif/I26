using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>Co-op Rolling Start (docs/deviations.md): a respawn or a joiner beside a teammate driving along comes in at its speed and heading.</summary>
public class CoopRollingStartTests
{
    private static Vehicle Moving(Vector3 vel, float yaw = 0, bool airborne = false) =>
        new() { Rot = Rows.AboutY(yaw), Vel = vel, Flags = airborne ? VehicleFlags.Airborne : 0 };

    [Fact]
    public void ATeammateDrivingAlongIsMatchedAlongItsHeading()
    {
        var fwd = Rows.AboutY(0.3f).Forward;
        var r = ScriptHost.Rolling(Moving(fwd * 20 + new Vector3(0, -1.5f, 0), 0.3f));
        Assert.NotNull(r);
        Assert.Equal(20, r!.Value.Speed, 3);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(fwd.X, 0, fwd.Z)) * 20, r.Value.Vel) < 1e-3f);
        // a slight drift is still driving along: the fresh car goes along the heading at the speed over the ground
        var drift = Rows.AboutY(10 * MathF.PI / 180).Forward * 12;
        Assert.Equal(12, ScriptHost.Rolling(Moving(drift))!.Value.Speed, 3);
    }

    [Fact]
    public void ATeammateNotDrivingAlongIsNotMatched()
    {
        Assert.Null(ScriptHost.Rolling(Moving(new Vector3(0, 0, 0.8f))));                        // all but still
        Assert.Null(ScriptHost.Rolling(Moving(new Vector3(0, 0, 20), airborne: true)));          // in the air
        Assert.Null(ScriptHost.Rolling(Moving(new Vector3(0, 0, -8))));                          // reversing
        Assert.Null(ScriptHost.Rolling(Moving(Rows.AboutY(30 * MathF.PI / 180).Forward * 15)));  // sliding 30 degrees off its heading
    }

    private static (SimSession S, MissionSetup Setup) Make(SnapshotRig.Loaded l, int humans, bool rolling) =>
        SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, "t05", coop: CoopSetup.Of(humans) with { RollingStart = rolling }), SimRate.R20, 1);

    private static void Idle(SimSession s, int ticks)
    {
        for (int i = 0; i < ticks; i++) { s.Tick(new CarInput[s.Drivers.Count]); SnapshotRig.TakeEvents(s); }
    }

    /// <summary>The lead driving along its heading at 15 m/s each tick until <paramref name="until"/>.</summary>
    private static void Cruise(SimSession s, Vehicle lead, Func<bool> until, int most)
    {
        for (int i = 0; i < most && !until(); i++)
        {
            lead.Vel = lead.Rot.Forward * 15;
            lead.Speed = 15;
            Idle(s, 1);
        }
    }

    /// <summary>T05, two humans: seat 1 destroyed while the lead drives; it comes back at the lead's speed with the option, standing still without.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARespawnBesideADrivingTeammate(bool rolling)
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), 2, rolling);
        var host = setup.Script!.Host;
        Assert.Equal(rolling, host.Coop!.RollingStart);
        Idle(s, 200);
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        CoopRespawned? back = null;
        host.Coop.Happened += e => back ??= e as CoopRespawned;
        setup.Damage.Destroy(b);
        Cruise(s, a, () => back is not null, s.Rate.TicksFor(7f));
        Assert.Equal(new CoopRespawned(1, CoopReturn.Died, 0), back);
        Assert.False(Damage.DamageModel.IsDead(b));
        if (rolling)
        {
            // the same tick: the lead's speed over the ground, along its heading, which the fresh car faces too
            var want = ScriptHost.Rolling(a)!.Value;
            Assert.InRange(want.Speed, 10f, 16f);
            Assert.True(Vector3.Distance(want.Vel, b.Vel) < 1e-3f, $"{b.Vel} against {want.Vel}");
            Assert.Equal(want.Speed, b.Speed, 3);
            Assert.True(Vector3.Dot(a.Rot.Forward, b.Rot.Forward) > 0.999f);
        }
        else Assert.Equal(Vector3.Zero, b.Vel);
    }

    /// <summary>A joiner's fresh car (a seat nobody held) beside the driving lead: at its speed with the option.</summary>
    [Fact]
    public void AJoinerBesideADrivingTeammate()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), 2, true);
        var host = setup.Script!.Host;
        host.CoopVacate(1);
        Idle(s, 200);
        var a = setup.Humans[0];
        Cruise(s, a, () => false, 10);
        Assert.True(host.CoopJoin(1));
        var b = host.Coop!.Seats[1].Car;
        Assert.InRange(b.Speed, 10f, 16f);
        Assert.True(Vector3.Dot(Vector3.Normalize(b.Vel), a.Rot.Forward) > 0.99f);
        Idle(s, s.Rate.TicksFor(0.5f));
        Assert.True(b.Speed > 5f, $"the joiner slowed to {b.Speed:0.0} m/s");
    }
}
