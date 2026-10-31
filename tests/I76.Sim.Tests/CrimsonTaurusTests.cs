using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Crimson (Nitro P06): skipping the intro makes the script teleport Taurus to the nav point on the crest of the hill ahead (7360, 43790,
/// 28.1 m up), at 55 m/s along its heading (teleportOffset, speed 55). The hill falls away under that speed, so he leaves the ground and
/// flies down the far side, off the road's line. The original does the same: the Nitro Pack (28 fps, seed 1)
/// has him at x 7360.0 y 28.10 on the teleport frame, x 7393.3 y 32.44 nineteen frames later, then a fall at up to
/// 17 m/s; this port: x 7394.6 y 32.77 (the sideways drift after the jump differs, the AI's first steering).
/// </summary>
public class CrimsonTaurusTests
{
    [Fact]
    public void SkippingTheIntroLaunchesTaurusOffTheCrestAsTheOriginalDoes()
    {
        if (SnapshotRig.NitroInstall is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "p06"), SimRate.R28, 1);
        var taurus = (Entity)setup.Script!.Vm!.Actors.First(a => a.Label == "taurus").Handle!;
        var v = taurus.Vehicle!;
        int jumped = -1;
        float peakAbove = 0, vxAt19 = 0, yAt19 = 0, xAt19 = 0;
        for (int i = 0; i < 28 * 8; i++)
        {
            s.Tick([new CarInput { Commands = i >= 60 ? CarCommands.Skip : 0 }]); SnapshotRig.TakeEvents(s);
            if (jumped < 0 && v.Pos.X > 7000)
            {
                jumped = i;
                Assert.InRange((float)v.Pos.X, 7355f, 7365f);
                Assert.InRange((float)v.Pos.Y, 27.5f, 28.7f);
                Assert.InRange(v.Vel.Length(), 50f, 55.5f);
            }
            if (jumped < 0) continue;
            peakAbove = MathF.Max(peakAbove, (float)(v.Pos.Y - setup.World.Ground.BareHeight(v.Pos.X, v.Pos.Z)));
            if (i == jumped + 19) { xAt19 = (float)v.Pos.X; yAt19 = (float)v.Pos.Y; vxAt19 = v.Vel.X; }
        }
        Assert.True(jumped >= 0, "the skip teleported Taurus onto the crest");
        Assert.InRange(xAt19, 7390f, 7398f);           // the original: 7393.3
        Assert.InRange(yAt19, 32.0f, 33.2f);           // the original: 32.44
        Assert.InRange(vxAt19, 44f, 47f);              // the original: 45.6
        Assert.True(peakAbove > 5f, $"he flies off the crest (peak {peakAbove:0.0} m above the ground), as the original's Taurus does");
    }
}
