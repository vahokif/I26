using System.Numerics;
using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Co-op Scaling (docs/deviations.md): each enemy car the script sets going gets a copy per human past the first, up to the
/// host's cap; the copies take the original's orders, the script never sees them, and they fight on once the original is dead.
/// </summary>
public class CoopScalingTests(ITestOutputHelper output)
{
    private static (SimSession S, MissionSetup Setup) Make(SnapshotRig.Loaded l, string mission, int humans, int extra) =>
        SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, mission, coop: CoopSetup.Of(humans) with { ExtraEnemies = extra }), SimRate.R20, 1);

    private static void Idle(SimSession s, int ticks)
    {
        for (int i = 0; i < ticks; i++) { s.Tick(new CarInput[s.Drivers.Count]); SnapshotRig.TakeEvents(s); }
    }

    private static List<Entity> Enemies(ScriptHost h) =>
        h.Entities.Where(e => e.ExtraOf is null && e.Vehicle is { IsHeli: false, IsEmplacement: false } && e.Team == 2).ToList();

    /// <summary>T01 with three humans and the cap at 3: each of the three enemy cars, ordered from the first tick, has two copies beside it, none of them the script's.</summary>
    [Fact]
    public void ThreeHumansGiveEachEnemyCarTwoCopies()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t01", 3, 3);
        var h = setup.Script!.Host;
        int cars = setup.World.Vehicles.Count;
        Idle(s, 20);
        var enemies = Enemies(h);
        Assert.Equal(3, enemies.Count);
        foreach (var e in enemies)
        {
            var copies = h.ExtrasOf(e).ToList();
            Assert.Equal(2, copies.Count);
            foreach (var x in copies)
            {
                Assert.Equal(e.Vehicle!.Definition, x.Vehicle!.Definition);
                Assert.Equal(e.Team, x.Team);
                Assert.Empty(x.Id);                                   // no instance id: the script cannot name it
                Assert.Equal(e.Order, x.Order);                       // the original's order (T01's opening sit)
                Assert.InRange(Vector3.Distance(e.Position, x.Position), 3f, 60f);
                Assert.Contains(x.Vehicle, setup.World.Vehicles);
                Assert.DoesNotContain(x, h.AiVehicles);               // nor see it: allEnemyDead, nearestEnemy, isWithinEnemy
                Assert.DoesNotContain(x, h.Vehicles);
            }
        }
        Assert.Equal(cars + 6, setup.World.Vehicles.Count);
        // the friend and the neutral tractor are not enemies
        Assert.All(h.Entities.Where(e => e.Vehicle is not null && e.Team != 2 && e.ExtraOf is null), e => Assert.Empty(h.ExtrasOf(e)));
    }

    /// <summary>One human (or the option off) changes nothing: no copies, every car where the plain mission has it.</summary>
    [Fact]
    public void OneHumanOrTheOptionOffChangesNothing()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (plain, ps) = Make(l, "t01", 1, 0);
        var (one, os) = Make(l, "t01", 1, 3);
        var (off, fs) = Make(l, "t01", 3, 0);
        Idle(plain, 600); Idle(one, 600); Idle(off, 600);
        Assert.DoesNotContain(os.Script!.Host.Entities, e => e.ExtraOf is not null);
        Assert.DoesNotContain(fs.Script!.Host.Entities, e => e.ExtraOf is not null);
        Assert.Equal(ps.World.Vehicles.Count, os.World.Vehicles.Count);
        Assert.Equal(ps.World.Vehicles.Select(v => v.Pos), os.World.Vehicles.Select(v => v.Pos));
    }

    /// <summary>
    /// T02, three humans: the objective that follows the three enemy cars' deaths succeeds when the originals die, with their six
    /// copies alive, as without the option; the copies then go for the nearest human.
    /// </summary>
    [Fact]
    public void TheObjectiveFollowsTheOriginalsAndTheCopiesFightOn()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        int[] Run(int extra, out List<Entity> copies, out ScriptHost host)
        {
            var (s, setup) = Make(l, "t02", 3, extra);
            var h = host = setup.Script!.Host;
            setup.Damage.UnlimitedArmour = setup.Damage.UnlimitedChassis = true;   // the humans stand still: their survival is not the test's
            Idle(s, 100);
            foreach (var e in Enemies(h)) { h.Destroy(e); Idle(s, 60); }
            copies = [.. h.Entities.Where(e => e.ExtraOf is not null)];
            return [.. h.Objectives.Flags];
        }
        var plain = Run(0, out _, out _);
        var scaled = Run(3, out var copies, out var h);
        output.WriteLine($"objectives {string.Join(",", plain)} / scaled {string.Join(",", scaled)}");
        Assert.Equal(3, plain[2]);                 // succeeded
        Assert.Equal(plain, scaled);
        Assert.Equal(6, copies.Count);
        Assert.All(copies, x =>
        {
            Assert.False(x.Dead);
            Assert.Equal(AiOrder.Attack, x.Order);
            Assert.True(h.IsHumanEntity(x.OrderTarget));
        });
    }

    /// <summary>The copies are in the savestate: a snapshot restored into a fresh build of the same co-op game carries on the same.</summary>
    [Fact]
    public void TheCopiesAreSavedAndRestored()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (a, sa) = Make(l, "t01", 3, 3);
        Idle(a, 40);
        var snap = a.Snapshot();
        var (b, sb) = Make(l, "t01", 3, 3);
        b.Restore(snap);
        Assert.Equal(sa.Script!.Host.Entities.Count(e => e.ExtraOf is not null), sb.Script!.Host.Entities.Count(e => e.ExtraOf is not null));
        Idle(a, 100); Idle(b, 100);
        Assert.Equal(a.StateHash(), b.StateHash());
        // another cap is another build
        var (c, _) = Make(l, "t01", 3, 1);
        Assert.Contains("scaling", Assert.Throws<I76.Sim.Save.SnapshotMismatchException>(() => c.Restore(snap)).Message);
    }
}
