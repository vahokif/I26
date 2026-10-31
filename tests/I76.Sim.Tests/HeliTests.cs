using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The helicopter's flight model and controls, on made-up ground.</summary>
public class HeliTests
{
    private const float Dt = 0.036f;

    /// <summary>A ground of height f(x, z).</summary>
    private sealed class Ground(Func<double, double, float> f) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (f(x, z), Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static readonly Ground Flat = new((_, _) => 0f);

    private static Vehicle Heli(float y = 0, bool landed = true)
    {
        var v = new Vehicle { Heli = new HeliState(), Pos = new Double3(0, y, 0) };
        v.Flags = VehicleFlags.EngineOn | (landed ? HeliStep.Landed : 0);
        return v;
    }

    private sealed class Recorder : IHeliDamage
    {
        public int Downs, Crashes;
        public void ShootDown(Vehicle v) { Downs++; v.Flags |= HeliStep.Falling; v.AngVel = new Vector3(0.2f, 0, -0.5f); }
        public void Crash(Vehicle v) => Crashes++;
    }

    [Fact]
    public void ALandedHelicopterSitsStillUntilGivenThrottle()
    {
        var v = Heli(y: 5);
        for (int i = 0; i < 50; i++) HeliStep.Frame(v, Flat, Dt, false, null);
        Assert.Equal(5, v.Pos.Y, 5);
        Assert.Equal(Vector3.Zero, v.Vel);
        Assert.True((v.Flags & HeliStep.Landed) != 0);
        // throttle: off the ground to 20 (the helicopter's update's take-off)
        v.Throttle = 0.5f;
        HeliStep.Frame(v, Flat, Dt, false, null);
        Assert.True((v.Flags & HeliStep.Landed) == 0);
        Assert.Equal(20f, v.Heli!.Height);
    }

    [Fact]
    public void ItClimbsStraightUpBelowWorldTen_AndEasesToItsHeight()
    {
        var v = Heli(y: 0, landed: false);
        v.Heli!.Height = 20;
        v.Throttle = 1;
        // below y 10 the controls are held at zero: it rises without moving off
        HeliStep.Frame(v, Flat, Dt, false, null);
        Assert.Equal(0, v.Heli.Thrust);
        Assert.Equal(0, v.Pos.X, 6);
        Assert.Equal(0, v.Pos.Z, 6);
        // each frame closes 0.5·dt of the gap: y_n = 20·(1 − (1 − 0.018)^n)
        Assert.Equal(20 * 0.018, v.Pos.Y, 4);
        var w = Heli(y: 0, landed: false);
        w.Heli!.Height = 20;
        for (int i = 0; i < 400; i++) HeliStep.Frame(w, Flat, Dt, false, null);
        Assert.Equal(20, w.Pos.Y, 1);
    }

    [Fact]
    public void ItHoldsItsHeightOverTheGroundTwoSecondsAhead()
    {
        // a cliff 40 high from x = 100 on; flying +x at 25 it starts climbing 50 out (2 s ahead), and clears the edge
        var cliff = new Ground((x, _) => x >= 100 ? 40f : 0f);
        var v = Heli(y: 20, landed: false);
        v.Heli!.Height = 20;
        v.Rot = Rows.Identity with { Right = -Vector3.UnitZ, Forward = Vector3.UnitX };
        v.Vel = new Vector3(25, 0, 0); v.Speed = 25;
        float yAt50 = 0;
        for (int i = 0; i < 200 && (v.Flags & VehicleFlags.Dead) == 0; i++)
        {
            HeliStep.Throttle(v, 25, Dt);
            HeliStep.Frame(v, cliff, Dt, false, null);
            if (yAt50 == 0 && v.Pos.X > 52) yAt50 = (float)v.Pos.Y;
        }
        Assert.True(yAt50 > 20.5f, $"climbing before the cliff: {yAt50}");
        Assert.True((v.Flags & VehicleFlags.Dead) == 0, "a cliff the look-ahead saw is cleared");
        Assert.True(v.Pos.X > 150);
        Assert.InRange(v.Pos.Y, 55, 60.01);     // easing up to 40 + 20
    }

    [Fact]
    public void FlyingIntoAWallItCannotSeeCrashesIt()
    {
        // a wall 40 high just ahead and no time to climb: y below the ground = crashed, no fall
        var wall = new Ground((x, _) => x >= 1 ? 40f : 0f);
        var v = Heli(y: 20, landed: false);
        v.Heli!.Height = 20;
        v.Rot = Rows.Identity with { Right = -Vector3.UnitZ, Forward = Vector3.UnitX };
        v.Vel = new Vector3(40, 0, 0); v.Speed = 40;
        var rec = new Recorder();
        HeliStep.Frame(v, wall, Dt, false, rec);
        Assert.True((v.Flags & VehicleFlags.Dead) != 0);
        Assert.Equal(1, rec.Crashes);
        Assert.Equal(Vector3.Zero, v.Vel);
    }

    [Fact]
    public void TheAiThrottleHoldsTheOrderedSpeed()
    {
        var v = Heli(y: 20, landed: false);
        v.Heli!.Height = 20;
        for (int i = 0; i < 1000; i++)
        {
            HeliStep.Throttle(v, 25, Dt);
            HeliStep.Frame(v, Flat, Dt, false, null);
        }
        Assert.Equal(25, v.Speed, 0);
        // the nose follows the velocity; the velocity is the forward it started with
        Assert.True(Vector3.Dot(Vector3.Normalize(v.Vel), Vector3.UnitZ) > 0.999f);
        Assert.True(Vector3.Dot(v.Rot.Forward, Vector3.UnitZ) > 0.99f);
    }

    [Fact]
    public void TheAiSteeringTurnsItOntoAHeadingAndItBanksIntoTheTurn()
    {
        var v = Heli(y: 20, landed: false);
        v.Heli!.Height = 20;
        v.Vel = new Vector3(0, 0, 25); v.Speed = 25;
        float target = MathF.PI / 2;    // +x
        float maxBank = 0;
        for (int i = 0; i < 1000; i++)
        {
            HeliStep.Steer(v, target, Dt, MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z));
            HeliStep.Throttle(v, 25, Dt);
            HeliStep.Frame(v, Flat, Dt, false, null);
            maxBank = MathF.Max(maxBank, MathF.Abs(v.Rot.Right.Y));
        }
        float heading = MathF.Atan2(v.Vel.X, v.Vel.Z);
        Assert.Equal(target, heading, 2);
        Assert.True(maxBank > 0.05f, $"banked into the turn: {maxBank}");
        Assert.True(v.Pos.X > 100);
    }

    [Fact]
    public void ShotDownItFallsKeepingItsMomentumAndCrashesOnTheGround()
    {
        var v = Heli(y: 30, landed: false);
        v.Heli!.Height = 20;
        v.Vel = new Vector3(0, 0, 20); v.Speed = 20;
        var rec = new Recorder();
        rec.ShootDown(v);
        int frames = 0;
        while ((v.Flags & VehicleFlags.Dead) == 0 && frames < 1000) { HeliStep.Frame(v, Flat, Dt, false, rec); frames++; }
        Assert.Equal(1, rec.Crashes);
        Assert.Equal(0, v.Pos.Y, 4);
        // about sqrt(2·30/9.8) = 2.5 s down, at 20 forward
        Assert.InRange(frames * Dt, 2.2f, 2.8f);
        Assert.InRange(v.Pos.Z, 44, 56);
        Assert.NotEqual(Vector3.UnitY, v.Rot.Up);
    }

    [Fact]
    public void AContactBringsItDown()
    {
        var v = Heli(y: 20, landed: false);
        v.Heli!.Height = 20;
        v.Heli.Touched = true;
        var rec = new Recorder();
        HeliStep.Frame(v, Flat, Dt, false, rec);
        Assert.Equal(1, rec.Downs);
        Assert.Equal(0, v.Heli.Hp);
        Assert.True((v.Flags & HeliStep.Falling) != 0);
    }

    [Fact]
    public void TheDamageSlotIsOnePoolAndTheFsmHeightTakesItOff()
    {
        var world = new SimWorld(Flat, new CrtRand(1));
        var combat = new Combat(world, _ => null);
        var damage = new DamageModel(world, combat);
        var v = Heli(y: 0);
        v.Heli!.SetPool(800);
        world.Vehicles.Add(v);
        var p = new DamagePacket { Mask = 0b1010 };
        p.Ch[1] = 100; p.Ch[3] = 50;
        damage.Apply(v, p, Vector3.Zero);
        Assert.Equal(650, v.Heli.Hp);
        Assert.Equal(650f / 800f * 100f, v.Heli.HealthPercent, 3);
        var big = new DamagePacket { Mask = 2 };
        big.Ch[1] = 1000;
        damage.Apply(v, big, Vector3.Zero);
        Assert.Equal(0, v.Heli.Hp);
        Assert.True((v.Flags & HeliStep.Falling) != 0);
        Assert.Equal(new Vector3(0.2f, 0, -0.5f), v.AngVel);
        // falling, further hits are ignored; on the ground it is dead and a wreck
        damage.Apply(v, big, Vector3.Zero);
        world.Frame(Dt);
        Assert.True((v.Flags & VehicleFlags.Dead) != 0);
        Assert.True(v.Wreck);

        var u = Heli(y: 0);
        HeliStep.SetHeight(u, 0);
        Assert.True((u.Flags & HeliStep.Landed) != 0);
        HeliStep.SetHeight(u, 35);
        Assert.True((u.Flags & HeliStep.Landed) == 0);
        Assert.Equal(35f, u.Heli!.Height);
    }

    [Fact]
    public void TheBasisTiltsUpIntoTheAcceleration()
    {
        var r = HeliStep.Basis(Vector3.Normalize(new Vector3(0, 19.6f, 20.2f)), Vector3.UnitZ);
        Assert.Equal(1f, r.Right.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(r.Right, r.Up), 4);
        Assert.Equal(0f, Vector3.Dot(r.Up, r.Forward), 4);
        Assert.True(r.Forward.Y < -0.6f, "nose down when accelerating forward");
        Assert.Equal(Vector3.UnitX, r.Right);
    }
}
