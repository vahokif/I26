using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// #57: Nitro P18 ("Break In") opens on two Hueys flying east over a 60 m plateau. Measured in the Nitro Pack:
/// at 28 fps both fly straight (lateral wobble 0.3 and 1.1 m rms between x 2000 and 2400) and
/// the first clears the plateau's rim by 0.8 m; at 40 fps they weave (2.5 and 3.5 m rms, steering at full lock both
/// ways) and the first clears by 0.4 m; at 60 fps they weave harder (4.6 and 5.5 m) and the first flies into the rim.
/// The AI's helicopter controls close a share of their error per frame, so their gain grows with the frame rate; the
/// port keeps 28 fps' gain at faster ticks (HeliStep.ControlFrame).
/// </summary>
public class HeliRateTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Theory]
    [InlineData(20)]
    [InlineData(28)]
    [InlineData(40)]
    [InlineData(60)]
    public void BreakInsHueysFlyStraightOverTheRidgeAtEveryRate(int hz)
    {
        if (NitroDir is not { } dir) return;
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.FindMission("p18")!), asm, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup, rate: SimRate.FromLabel(hz));
        s.AddDriver(setup.World.Player!);
        var hueys = setup.Entities.Where(e => e.Name == "vuchuey3" && e.Vehicle is not null).Select(e => e.Vehicle!).ToList();
        Assert.Equal(2, hueys.Count);
        var track = hueys.ToDictionary(v => v, _ => new List<(double X, double Z)>());
        var h = setup.Script!.Host;
        var input = new CarInput();
        // past the ridge (x 2475..2535), before the script hides them at 38 s
        while (h.Now < 34)
        {
            s.Tick([input]);
            foreach (var v in hueys)
            {
                Assert.True((v.Flags & (VehicleFlags.Dead | HeliStep.Falling)) == 0, $"{hz} Hz: a Huey came down at x {v.Pos.X:0.0}, t {h.Now:0.00}");
                if (v.Pos.X is >= 2000 and <= 2400) track[v].Add((v.Pos.X, v.Pos.Z));
            }
        }
        foreach (var (v, pts) in track)
        {
            Assert.True(v.Pos.X > 2540, $"{hz} Hz: over the ridge (x {v.Pos.X:0.0})");
            // lateral wobble about the straight line through the leg
            double mx = pts.Average(p => p.X), mz = pts.Average(p => p.Z);
            double b = pts.Sum(p => (p.X - mx) * (p.Z - mz)) / pts.Sum(p => (p.X - mx) * (p.X - mx));
            double rms = System.Math.Sqrt(pts.Average(p => System.Math.Pow(p.Z - (mz + b * (p.X - mx)), 2)));
            Assert.True(rms < 1.5, $"{hz} Hz: lateral wobble {rms:0.00} m rms (the original at 28 fps: 0.3 and 1.1)");
        }
    }
}
