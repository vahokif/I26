using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A landing whose damage kills the car ends the substep, as the original's: the death it wrote stands, and the
/// grounded part (probe, attitude, skid marks, rough-terrain damage with its rand()) does not run.
/// </summary>
public class FatalLandingTests
{
    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>A landing kills the car as the damage model's does (dead, dying, thrown up); the rough damage draws one rand().</summary>
    private sealed class Damage(CrtRand rand, bool fatal) : IStepDamage
    {
        public int Roughs;
        public void Timer(Vehicle v, float dt) { }
        public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) { }
        public void Landing(Vehicle v, Vector3 impactVel, Vector3 n)
        {
            if (!fatal) return;
            v.Flags |= VehicleFlags.Dead | (VehicleFlags)0x80008000 | VehicleFlags.Airborne;
            v.Vel.Y = 6;
        }
        public void FallKill(Vehicle v) { }
        public void Rough(Vehicle v, float dt) { Roughs++; rand.Next(); }
        public void StaticContact(Vehicle v, StaticObject o, Contact rec) { }
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
        // falling fast onto flat ground, 0.2 m up: it lands in the first substep
        v.Pos = new Double3(0, 0.2, 0);
        v.Vel = new Vector3(0, -8, 20); v.Speed = v.Vel.Length();
        v.Flags = VehicleFlags.EngineOn | VehicleFlags.Airborne;
        v.HeightAboveGround = 0.2f;
        return v;
    }

    [Fact]
    public void AFatalLandingEndsTheSubstepAndTheCarStaysDead()
    {
        var rand = new CrtRand(3);
        var dmg = new Damage(rand, fatal: true);
        var step = new VehicleStep(new Flat(), rand) { Damage = dmg };
        var v = Car();
        step.Step(v, 0.04f, isPlayer: false);
        Assert.Single(step.Landings);
        Assert.True((v.Flags & VehicleFlags.Dead) != 0, $"flags {v.Flags}");
        Assert.True((v.Flags & VehicleFlags.Airborne) != 0, "the death's throw was undone");
        Assert.Equal(0, dmg.Roughs);
        Assert.Equal(new CrtRand(3).Seed, rand.Seed);
    }

    [Fact]
    public void ALandingThatDoesNotKillGoesOnToTheGround()
    {
        var rand = new CrtRand(3);
        var dmg = new Damage(rand, fatal: false);
        var step = new VehicleStep(new Flat(), rand) { Damage = dmg };
        var v = Car();
        step.Step(v, 0.04f, isPlayer: false);
        Assert.Single(step.Landings);
        Assert.True((v.Flags & VehicleFlags.Airborne) == 0);
        Assert.Equal(1, dmg.Roughs);   // over 7.65 m/s on the ground: one rough-terrain call
    }
}
