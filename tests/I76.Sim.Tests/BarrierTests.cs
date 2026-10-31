using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #47: Nitro p12 ("Allies")'s striped barrier, swend_1, is placed as a type-2 structure whose .sdf nodes are all type
/// 4. In the Nitro Pack (teleported to 60,3180,49045,90 with a 25 u/s run up)
/// the car met node SW1ENDS1, kept its speed, and the sign slot threw the whole placement; no structure
/// slot was called.
/// </summary>
public class BarrierTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void AlliesBarrierFallsToACarDrivenThroughIt()
    {
        if (NitroDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var s = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("p12")!), asm, script: false);
        var barriers = s.Statics.Objects.Where(o => o.Name.Equals("swend_1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(5, barriers.Count);
        Assert.All(barriers, b => Assert.True(b.Type == 2 && b.Nodes.Count > 0 && b.Nodes.All(n => n.Sign) && b.Boxes.Count > 0));
        var hit = barriers.OrderBy(b => System.Math.Abs(b.Centre.Z - 49045)).First();

        // where the original test put the car: 40 west of the barrier, facing east, at 25 u/s
        var v = s.World.Player!;
        v.Pos = new Double3(3180, s.Ground.BareHeight(3180, 49045) + 0.5, 49045);
        v.Rot = new Rows(new Vector3(0, 0, -1), Vector3.UnitY, Vector3.UnitX);
        v.Vel = new Vector3(25, 0, 0); v.Speed = 25; v.AngVel = default;
        v.Throttle = 1;
        float dt = 102f / 2048f, slowest = 25;
        for (int f = 0; f < 60 && v.Pos.X < 3235; f++)
        {
            s.World.Frame(dt);
            if (v.Pos.X > 3210) slowest = MathF.Min(slowest, v.Speed);
        }
        Assert.True(hit.Destroyed, $"knocked off: car at {v.Pos.X:0.0}, {v.Pos.Z:0.0}");
        Assert.False(hit.Solid);
        Assert.Contains(s.Damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == hit);
        Assert.True(v.Pos.X >= 3235, $"the car drove through: x {v.Pos.X:0.0}, speed {v.Speed:0.0}");
        Assert.True(slowest > 15, $"without being stopped: slowest {slowest:0.0}");
    }
}
