using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The blown-front-tyre pull acts on the player's car only while the keys drive it: while the script drives it (the
/// original's "AI drove the player" latch) there is no pull, and with both fronts flat no rand() per substep.
/// </summary>
public class BlownTyrePullTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Vehicle Car()
    {
        var v = new Vehicle();
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z);
            w.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f;
        v.P.SteerGain = 1f / 5.4f; v.P.YawRateLimit = 2.65618f / 2.7f;
        v.Flags |= VehicleFlags.EngineOn | VehicleFlags.CommandedYaw;
        v.FrontRight.Blown = v.FrontLeft.Blown = true;
        v.Vel = new Vector3(0, 0, 5); v.Speed = 5;
        return v;
    }

    /// <summary>The rand draws ten frames make, and the heading they leave.</summary>
    private static (int Draws, float Heading) Drive(bool scripted)
    {
        var rand = new CrtRand(7);
        var step = new VehicleStep(new Flat(), rand) { ScriptDrives = _ => scripted };
        var v = Car();
        for (int i = 0; i < 10; i++) step.Frame(v, 102f / 2048f, isPlayer: true);
        var probe = new CrtRand(7);
        int draws = 0;
        while (probe.Seed != rand.Seed && draws < 10000) { probe.Next(); draws++; }
        return (draws, MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z));
    }

    [Fact]
    public void WhileTheScriptDrivesThePlayerThereIsNoPullAndNoDraw()
    {
        var (draws, heading) = Drive(scripted: true);
        Assert.Equal(0, draws);
        Assert.Equal(0f, heading, 5);
    }

    [Fact]
    public void OnTheKeysBothFlatFrontsDrawOncePerSubstep()
    {
        var (draws, _) = Drive(scripted: false);
        Assert.True(draws > 0, "no rand() drawn");
    }
}
