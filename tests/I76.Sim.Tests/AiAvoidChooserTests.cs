using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// avoid clsn's input search, as the original's chooser, read from the original (and the first-candidate order checked
/// on a captured T05 avoid clsn, t05fp03, idle: with the wheel straight it took +0.25 first, every time):
/// <list type="bullet">
/// <item>wheel centred: +0.25, −0.25, +0.5, −0.5 ... to the steering limit, the positive side always first; none clear,
/// the highest score, a tie going to the later candidate;</item>
/// <item>off centre: the throttle sweep (none clear: its first throttle), then the steer sweep, one side and then the
/// other; none clear, the far end of the side searched first, whatever the scores.</item>
/// </list>
/// </summary>
public class AiAvoidChooserTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Flat ground, open, or no-go everywhere (every candidate blocked, every score alike).</summary>
    private sealed class Ground(bool noGo) : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public bool Impassable(double x, double z) => noGo;
    }

    /// <summary>The input search for a Piranha rolling at 10 facing +Z with this steer, its hold run out; the steer it chose and the steering limit.</summary>
    private static (float Steer, float Limit) Search(bool noGo, float steer)
    {
        var v = VehicleFactory.Create(new AssetVfs(new GameInstall(InstallDir!)), "vppirna1.vcf");
        v.Pos = new Double3(0, 0, 0); v.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        v.Vel = new Vector3(0, 0, 10); v.Speed = 10; v.Gear = 1; v.Steer = steer;
        var e = new Entity("ai", [], 1, 0) { Vehicle = v };
        var world = new SimWorld(new Ground(noGo), new I76.Sim.Rng.CrtRand(1));
        world.Vehicles.Add(v);
        var host = new ScriptHost(world, new List<Entity> { e }, (Objectives)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        host.FrameDelta = SimRate.R20.TickSeconds;
        host.Now = 10f; e.EscapeTimer = 5f;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        float limit = (float)typeof(Ptable).GetMethod("SteerLimit", flags)!.Invoke(host.Ptable, [e])!;
        typeof(Ptable).GetMethod("InputSearch", flags)!.Invoke(host.Ptable, [e, null]);
        return (v.Steer, limit);
    }

    [Fact]
    public void ACentredWheelTriesThePositiveSideFirst()
    {
        if (InstallDir is null) return;
        // the hold has run out and the wheel reads 0: a side chosen by the hold would have tried -0.25 first
        var (steer, _) = Search(false, 0);
        Assert.Equal(0.25f, steer);
    }

    [Fact]
    public void ACentredWheelWithNothingClearTakesTheLastOfTheBestScores()
    {
        if (InstallDir is null) return;
        var (steer, limit) = Search(true, 0);
        Assert.True(limit > 0.25f);
        Assert.Equal(-limit, steer);            // +0.25, -0.25 ... +L, -L all score alike: the last
    }

    [Fact]
    public void AnOffCentreWheelWithNothingClearGoesToTheEndOfTheSideSearchedFirst()
    {
        if (InstallDir is null) return;
        // hold run out, steer positive: the negative side first
        var (steer, limit) = Search(true, 0.5f);
        Assert.Equal(-limit, steer);
        (steer, limit) = Search(true, -0.5f);
        Assert.Equal(limit, steer);
    }
}
