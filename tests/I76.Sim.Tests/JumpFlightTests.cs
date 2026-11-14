using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The car in the air, as docs/jumps.md describes it: gravity alone moves it (no drag), the keys do nothing until a wheel
/// is down again, and the pitch rate the lip gave it is kept if the nose is already falling faster than π/20 rad/s, else
/// eased toward that.
/// </summary>
public class JumpFlightTests
{
    private sealed class Far : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (-1000, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private const float Dt = 102f / 2048f;

    private static Vehicle Flying(float pitchRate)
    {
        var v = new Vehicle();
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, z);
            w.InitSuspension();
        }
        v.Pos = new Double3(0, 0, 0);
        v.Vel = new Vector3(0, 14f, 48f); v.Speed = v.Vel.Length();
        v.AngVel = new Vector3(pitchRate, 0, 0);
        v.Flags = VehicleFlags.EngineOn | VehicleFlags.Airborne;
        v.HeightAboveGround = 1000;
        return v;
    }

    private static Vehicle Fly(float pitchRate, float throttle, float steer, int ticks)
    {
        var step = new VehicleStep(new Far(), new CrtRand(1));
        var v = Flying(pitchRate);
        for (int i = 0; i < ticks; i++) { v.Throttle = throttle; v.Steer = steer; step.Step(v, Dt, isPlayer: true); }
        return v;
    }

    [Fact]
    public void GravityAloneMovesTheCar()
    {
        var v = Fly(0, 0, 0, 40);
        Assert.Equal(48f, v.Vel.Z, 3);                     // no drag along the path
        Assert.Equal(14f - 9.8f * Dt * 40, v.Vel.Y, 3);
        Assert.True((v.Flags & VehicleFlags.Airborne) != 0);
    }

    [Fact]
    public void TheKeysDoNothingInTheAir()
    {
        var coast = Fly(-0.316f, 0, 0, 40);
        foreach (var (t, s) in new[] { (1f, 0f), (-1f, 0f), (1f, 1f), (-1f, -1f) })
        {
            var v = Fly(-0.316f, t, s, 40);
            Assert.Equal(coast.Pos, v.Pos);
            Assert.Equal(coast.Vel, v.Vel);
            Assert.Equal(coast.Rot.Forward, v.Rot.Forward);
            Assert.Equal(coast.AngVel, v.AngVel);
        }
    }

    [Theory]
    [InlineData(-0.316f)]
    [InlineData(-0.626f)]
    public void ANoseFallingFasterThanTheFlightRateKeepsItsRate(float rate)
    {
        var v = Fly(rate, 0, 0, 60);
        Assert.Equal(rate, v.AngVel.X);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void ASlowerNoseEasesTowardTheFlightRate(float rate)
    {
        float target = -MathF.PI / 20;
        var v = Fly(rate, 0, 0, 1);
        Assert.Equal(rate + (target - rate) * Dt, v.AngVel.X, 5);   // a share dt of the gap a step
        v = Fly(rate, 0, 0, 200);
        Assert.Equal(target, v.AngVel.X, 3);
    }
}
