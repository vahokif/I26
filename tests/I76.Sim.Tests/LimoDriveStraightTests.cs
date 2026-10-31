using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Limo (Nitro's Jefferson Dreadnought, vjdread1.vcf) driven straight forward after the Reset Car Upright key went round in fast
/// donuts: the reset cleared the car's CommandedYaw flag (its grip model, set at build), and on a slope the per-wheel force model
/// spun a car with a 4.9 m wheelbase. Every car keeps the flag through a reset and drives straight after it.
/// </summary>
public class LimoDriveStraightTests
{
    private sealed class Slope(float sx, float sz) : IGround
    {
        private float H(double x, double z) => (float)(sx * x + sz * z);
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (H(x, z), BareNormal(x, z));
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public float BareHeight(double x, double z) => H(x, z);
        public Vector3 BareNormal(double x, double z) => Vector3.Normalize(new Vector3(-sx, 1, -sz));
    }

    private static string? Nitro => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>Heading drift (rad) and the largest yaw rate (rad/s) over ten seconds of full throttle after a reset on the slope.</summary>
    private static (float Drift, float MaxYaw, VehicleFlags Flags) DriveAfterReset(string car, float sx, float sz, bool reset)
    {
        var vfs = new AssetVfs(new GameInstall(Nitro!));
        var g = new Slope(sx, sz);
        var world = new SimWorld(g, new CrtRand(1));
        var v = VehicleFactory.Create(vfs, car);
        world.Vehicles.Add(v); world.Player = v;
        var step = new VehicleStep(g, new CrtRand(1));
        v.Pos = new Double3(100, g.BareHeight(100, 40), 40);
        v.Rot = Rows.AboutY(0.6f);
        if (reset) UprightReset.Apply(world, v);
        float H() => MathF.Atan2(v.Rot.Forward.X, v.Rot.Forward.Z);
        float h0 = H(), maxYaw = 0;
        for (int i = 0; i < 200; i++)
        {
            v.Throttle = 1; v.Steer = 0;
            step.Frame(v, 0.05f);
            maxYaw = MathF.Max(maxYaw, MathF.Abs(v.AngVel.Y));
        }
        float d = H() - h0;
        d = MathF.Atan2(MathF.Sin(d), MathF.Cos(d));
        return (MathF.Abs(d), maxYaw, v.Flags);
    }

    [Theory]
    [InlineData("vjdread1.vcf", 0f, 0f)]
    [InlineData("vjdread1.vcf", 0.2f, 0f)]
    [InlineData("vjdread1.vcf", 0.2f, 0.1f)]
    [InlineData("vppirna1.vcf", 0.2f, 0.1f)]
    public void ACarDrivesStraightAfterTheReset(string car, float sx, float sz)
    {
        if (Nitro is null) return;
        var (drift, maxYaw, flags) = DriveAfterReset(car, sx, sz, reset: true);
        Assert.True(drift < 0.1f && maxYaw < 0.5f, $"{car} on slope {sx},{sz}: drifted {drift:0.00} rad, yaw up to {maxYaw:0.0} rad/s");
        Assert.True((flags & VehicleFlags.CommandedYaw) != 0, "the car's grip model is kept");
    }

    [Fact]
    public void TheResetKeepsTheCarsGripModel()
    {
        var world = new SimWorld(new Slope(0.2f, 0), new CrtRand(1));
        var v = new Vehicle { Pos = new Double3(100, 30, 40) };
        v.Flags |= VehicleFlags.CommandedYaw | VehicleFlags.Sliding | VehicleFlags.Airborne;
        UprightReset.Apply(world, v);
        Assert.True((v.Flags & VehicleFlags.CommandedYaw) != 0);
        Assert.Equal(0, (int)(v.Flags & (VehicleFlags.Sliding | VehicleFlags.Airborne)));
    }
}
