using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>Velocity's (P17) intro: the bus and the other cars the cut-scene shows stay on the ground.</summary>
public class P17BusTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CarsInViewOfTheIntroStayOnTheGround(int humans)
    {
        if (SnapshotRig.NitroInstall is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "p17", coop: humans > 1 ? CoopSetup.Of(humans) : null), SimRate.R20, 1);
        var host = setup.Script!.Host;
        var worst = new List<string>();
        int shown = 0;
        for (int i = 0; i < 1200; i++)
        {
            s.Tick(new CarInput[humans]); SnapshotRig.TakeEvents(s);
            if (!host.View.Active) continue;
            foreach (var v in setup.World.Vehicles)
            {
                if (v.IsHeli || v.Wreck || (v.Pos.ToVector3() - host.View.Eye).Length() > 1500f) continue;
                shown++;
                float below = (float)setup.World.Ground.BareHeight(v.Pos.X, v.Pos.Z) - (float)v.Pos.Y;
                if (below > 2f) worst.Add($"far={setup.World.IsFar(v)} d={(v.Pos.ToVector3() - host.View.Eye).Length():0} y={v.Pos.Y:0.0} tick {i}: {host.Of(v)?.Name} {below:0.0} below the ground");
            }
        }
        Assert.True(shown > 0, "a car in view of the intro");
        Assert.True(worst.Count == 0, string.Join("\n", worst.Take(5)));
    }
}
