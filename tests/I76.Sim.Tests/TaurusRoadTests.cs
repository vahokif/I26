using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// T03: Taurus (t03js01) leads the player along the road until the script sends him after the hidden ambusher
/// (about 57 s); until then he stays on the road at every sim rate. After that he leaves it by design: the dirt tactics
/// chase a target parked off the road, and the original does the same (measured in the original at 20 fps: 1042,49205 at 60 s,
/// off the road from 64 s, circling the ambusher).
/// A player right behind or beside him must not matter either: the on-road collision test only sees cars in
/// its forward cone (vehOnly 0), so a tailgater did not send him straight on into the cliff.
/// </summary>
public class TaurusRoadTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Runs T03 for 45 s; the player's car, once the intro is over, is kept at (right, ahead) in Taurus's frame.</summary>
    private static (float Worst, int OffRoad) Run(int hz, Vector2? glue)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t03.msn")!), asm);
        var session = new SimSession(setup, rate: SimRate.FromLabel(hz));
        var taurus = setup.Entities.Single(e => e.Name == "t03js01");
        var v = taurus.Vehicle!;
        var player = setup.World.Player!;
        session.AddDriver(player);
        float worst = 0;
        int offRoad = 0;
        int ticks = (int)(45f / session.TickSeconds);
        for (int i = 0; i < ticks; i++)
        {
            session.Tick([new CarInput()]);
            if (glue is { } g && setup.Script!.Host.Now >= 10.4f)
            {
                player.Rot = v.Rot; player.Vel = v.Vel;
                player.Pos = v.Pos + v.Rot.ToWorld(new Vector3(g.X, 0, g.Y));
            }
            if (setup.World.Ground.SurfaceId(v.Pos.X, v.Pos.Z) != 2) offRoad++;
            var p = new Vector2((float)v.Pos.X, (float)v.Pos.Z);
            float best = float.MaxValue;
            for (int k = 0; k + 1 < taurus.Waypoints.Count; k++)
            {
                var a = new Vector2(taurus.Waypoints[k].X, taurus.Waypoints[k].Z);
                var ab = new Vector2(taurus.Waypoints[k + 1].X, taurus.Waypoints[k + 1].Z) - a;
                float t = ab.LengthSquared() > 0 ? System.Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                best = System.Math.Min(best, Vector2.Distance(p, a + ab * t));
            }
            if (best < float.MaxValue) worst = System.Math.Max(worst, best);
        }
        return (worst, offRoad);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(28)]
    public void TaurusKeepsToTheRoadUntilTheAttackOrder(int hz)
    {
        if (InstallDir is null) return;
        var (worst, offRoad) = Run(hz, null);
        Assert.Equal(0, offRoad);
        Assert.True(worst < 8f, $"Taurus strayed {worst:0.0} from his route at {hz} Hz");
    }

    [Theory]
    [InlineData(20, 0f, -9f)]       // a tailgater
    [InlineData(20, -4f, 0f)]       // abreast on his left
    [InlineData(28, -4f, 0f)]
    public void ACarBehindOrBesideDoesNotSendHimOffTheRoad(int hz, float right, float ahead)
    {
        if (InstallDir is null) return;
        var (worst, offRoad) = Run(hz, new Vector2(right, ahead));
        Assert.Equal(0, offRoad);
        Assert.True(worst < 8f, $"Taurus strayed {worst:0.0} from his route at {hz} Hz with a car at ({right}, {ahead})");
    }
}
