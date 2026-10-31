using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A cut-scene drives the player's car with script orders (the player has no control): the opening scenes of the stock missions
/// send it along a path (goto) or after another car (follow), and the car must really go. Measured against 0.7.0 and 0.8.0, whose
/// scenes the sim reproduces move for move; a player left standing in a scene (T01's Taurus drives off ahead of it) is how a
/// held script camera's near/far change, a co-op change or a car change shows up in single player.
/// </summary>
public class ScenePlayerOrdersTests
{
    [Fact]
    public void ThePlayerGoesWhereASceneOrdersIt()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var bad = new List<string>();
        int checkedScenes = 0;
        foreach (var m in SnapshotRig.Missions(dir))
        {
            var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, m), SimRate.R20, 1);
            if (setup.Script?.Host is not { } host) continue;
            var p = setup.World.Player!;
            var pe = setup.Entities.First(e => e.Vehicle == p);
            bool was = false;
            int start = 0;
            System.Numerics.Vector3 p0 = default, t0 = default;
            Entity? target = null;
            AiOrder? order = null;
            float command = 0;
            for (int i = 0; i < 1600; i++)
            {
                s.Tick([new CarInput()]); SnapshotRig.TakeEvents(s);
                bool on = host.View.Active;
                if (on && !was) { start = i; p0 = p.Pos.ToVector3(); }
                if (on && i == start + 10) { order = pe.Order; target = pe.FollowRef ?? pe.OrderTarget; t0 = target?.Position ?? default; command = pe.Ai.CommandSpeed; }
                if (!on && was && i - start > 40 && order is AiOrder.Goto or AiOrder.Follow && command >= 10)
                {
                    checkedScenes++;
                    float seconds = (i - start) * 0.05f, moved = (p.Pos.ToVector3() - p0).Length();
                    float targetMoved = target is null ? 0 : (target.Position - t0).Length();
                    if (moved < 0.1f * command * seconds) bad.Add($"{m}: the player moved {moved:0} m in a {seconds:0} s scene ordered {order} at {command:0}");
                    else if (order == AiOrder.Follow && moved < 0.5f * targetMoved) bad.Add($"{m}: the player moved {moved:0} m after {target?.Name}'s {targetMoved:0}");
                }
                if (!on) order = null;
                was = on;
            }
        }
        Assert.True(checkedScenes >= 15, $"{checkedScenes} scenes with the player ordered");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
