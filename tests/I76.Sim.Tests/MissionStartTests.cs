using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>The player's car at the start of a mission: it stands where it settles, it does not drop onto the ground in the first moments.</summary>
public class MissionStartTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("I76_INSTALL", "miss16", "t01.msn", 20)]
    [InlineData("I76_INSTALL", "miss16", "t01.msn", 30)]
    [InlineData("I76_NITRO_INSTALL", "miss8", "b01.msn", 20)]
    public void ThePlayersCarDoesNotDropOntoTheGroundInTheFirstSecond(string env, string dir, string msn, int hz)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } root || !Directory.Exists(root)) return;
        var install = new GameInstall(root);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve(dir, msn)!), asm, null, rand: new I76.Sim.Rng.CrtRand(1));
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(hz));
        s.AddDriver(setup.World.Player!);
        var p = setup.World.Player!;
        double Range(bool settle)
        {
            if (settle) s.SettleCars();
            double lo = p.Pos.Y, hi = p.Pos.Y;
            for (int i = 0; i < hz; i++) { s.Tick([new CarInput()]); lo = System.Math.Min(lo, p.Pos.Y); hi = System.Math.Max(hi, p.Pos.Y); }
            output.WriteLine($"{msn} {hz} Hz: first second spans {hi - lo:0.000} m");
            return hi - lo;
        }
        Assert.True(Range(settle: true) < 0.06, "the car hops or drops in the first second");
    }
}
