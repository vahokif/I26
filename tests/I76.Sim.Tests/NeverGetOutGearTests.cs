using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// Never Get Out (Nitro P11, #191): the opening holds the player's car with hide (drive selector P). A skip does startCar, a
/// teleport at 25 m/s and a one-second goto, after the script has let the camera go. The original runs that goto on the
/// player's car (the keys wait while the script drives it), so the car leaves in D and coasts down from 25 m/s: 13 m/s some
/// 16 s after the skip, 1 m/s at 28 s (here about 13 m/s 17 s after the camera lets go). Dropping orders to the player
/// outside a cut-scene left it in P, braking to a stop in 3 s.
/// </summary>
public class NeverGetOutGearTests(ITestOutputHelper output)
{
    [Fact]
    public void AfterASkippedOpeningThePlayersCarIsInDriveAndCoasts()
    {
        if (SnapshotRig.NitroInstall is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "p11"), SimRate.R28, 1);
        var host = setup.Script!.Host;
        var p = setup.World.Player!;
        int released = -1;
        for (int i = 0; i < 28 * 30 && (released < 0 || i < released + 28 * 5); i++)
        {
            // Esc once at 3 s, then no keys at all
            s.Tick([new CarInput { Commands = i == 28 * 3 ? CarCommands.Skip : 0 }]); SnapshotRig.TakeEvents(s);
            if (released < 0 && i > 28 * 3 && !host.View.Active) released = i;
            if (released >= 0 && (i - released) % 28 == 0)
                output.WriteLine($"{(i - released) / 28f:0.0} s after the release: v {p.Speed:0.0} ds {p.DriveState} thr {p.Throttle:0.00} gear {p.Gear}");
        }
        Assert.True(released >= 0, "the skip let the camera go");
        Assert.Equal(3, p.DriveState);
        Assert.InRange(p.Throttle, -0.05f, 0.05f);
        Assert.True(p.Speed > 15f, $"the car coasts (it does not brake): {p.Speed:0.0} m/s 5 s after the release");
        Assert.False(host.ScriptDrove(p), "the keys have the car again once the goto is over");
    }
}
