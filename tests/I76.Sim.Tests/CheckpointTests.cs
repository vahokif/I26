using I76.Sim.Missions;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The port's Checkpoints option on the simulation's side (docs/savestate.md): when one is kept, and what a retry gets back.</summary>
public class CheckpointTests
{
    /// <summary>
    /// T14 driven by the corpus's inputs: a checkpoint when the intro is over and one when the first objective is
    /// done (tick 1007); the mission then fails. A mission rebuilt from the install (another seed) and given the
    /// last checkpoint is the state the checkpoint was taken in, and from there plays to the same failure.
    /// </summary>
    [Fact]
    public void ARetryFromTheObjectivesCheckpointIsTheStateItWasTakenIn()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var b = SnapshotRig.BuildOf(dir, "t14");
        var (s, _) = SnapshotRig.Make(l, b, SimRate.R20, 1);   // a seed whose run gets the first objective done
        var policy = CheckpointPolicy.For(s, enabled: true)!;
        var taken = new List<(string Why, int Tick, byte[] Snap, string Dump)>();
        while (s.Script!.Result is null && s.Ticks < 6000)
        {
            SnapshotRig.Tick(s);
            if (policy.Due(s) is { } why) taken.Add((why, s.Ticks, s.Snapshot(), StateDump.Of(s)));
            SnapshotRig.TakeEvents(s);
        }
        Assert.False(s.Script.Result);
        Assert.Equal(["start", "objective 1"], taken.Select(t => t.Why));
        Assert.True(taken[0].Tick > 0 && taken[0].Tick < taken[1].Tick);
        int failedAt = s.Ticks;
        var failed = StateDump.Of(s);

        var cp = taken[^1];
        var (fresh, _) = SnapshotRig.Make(l, b, SimRate.R20, 77);
        fresh.Restore(cp.Snap);
        Assert.Null(StateDump.FirstDifference(cp.Dump, StateDump.Of(fresh)));
        Assert.Equal(Objectives.Succeeded, fresh.Script!.Host.Objectives.Flags[0] & Objectives.Succeeded);
        // the retry's own policy keeps no second checkpoint of the same moment
        var again = CheckpointPolicy.For(fresh, enabled: true, resumed: true)!;
        Assert.Null(again.Due(fresh));
        SnapshotRig.TakeEvents(fresh);
        while (fresh.Ticks < failedAt)
        {
            SnapshotRig.Tick(fresh);
            Assert.Null(again.Due(fresh));
            SnapshotRig.TakeEvents(fresh);   // as the first run did after each tick, its last included (its dump has no events)
        }
        Assert.False(fresh.Script.Result);
        Assert.Null(StateDump.FirstDifference(failed, StateDump.Of(fresh)));
    }

    [Fact]
    public void CheckpointsAreForAScriptedSinglePlayerMissionWithTheOptionOn()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (t14, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t14"), SimRate.R20, 1);
        Assert.Null(CheckpointPolicy.For(t14, enabled: false));
        Assert.NotNull(CheckpointPolicy.For(t14, enabled: true));
        var (melee, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "m01", "vppirna1.vcf", melee: ["vcgcour1.vcf"]), SimRate.R20, 1);
        Assert.Null(CheckpointPolicy.For(melee, enabled: true));
        var (net, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "m02", melee: [], rules: new MeleeRules { Network = true }), SimRate.R20, 1, [("vppirna1.vcf", 0)]);
        Assert.Null(CheckpointPolicy.For(net, enabled: true));
        var (bare, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t14") with { Script = false }, SimRate.R20, 1);
        Assert.Null(CheckpointPolicy.For(bare, enabled: true));
    }

    /// <summary>No checkpoint of a lost cause: an objective done with the player's car dead, or with the mission's end decided, keeps none.</summary>
    [Fact]
    public void NoCheckpointWithThePlayerDeadOrTheEndDecided()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t14"), SimRate.R20, 1);
        var policy = CheckpointPolicy.For(s, enabled: true)!;
        for (int i = 0; i < 400; i++) { SnapshotRig.Tick(s); policy.Due(s); }
        s.Script!.Host.EndMission(false, 5, 0);
        s.Script.Host.ObjectiveSuccess(1);
        Assert.Null(policy.Due(s));
        var (s2, setup2) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t14"), SimRate.R20, 1);
        var p2 = CheckpointPolicy.For(s2, enabled: true)!;
        for (int i = 0; i < 400; i++) { SnapshotRig.Tick(s2); p2.Due(s2); }
        setup2.Damage.Destroy(s2.World.Player!);
        s2.Script!.Host.ObjectiveSuccess(1);
        Assert.Null(p2.Due(s2));
        s2.Script.Host.ObjectiveSuccess(2);
        Assert.Null(p2.Due(s2));
    }

    [Fact]
    public void TheSalvagePoolGoesWithACheckpoint()
    {
        var pool = new SalvagePool("t05");
        pool.Add("eng03", 120, false);
        pool.Add("gmmedium.gdf", 40, true);
        pool.Add("eng03", 60, false);
        var copy = new SalvagePool("t05");
        copy.Add("bra01", 1, false);
        copy.Load(pool.Save());
        Assert.Equal(pool.Items, copy.Items);
    }
}
