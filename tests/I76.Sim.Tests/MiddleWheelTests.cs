using System.Numerics;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A six-wheeler's middle pair has its own suspension, as the original's: the ground contact steps it after the four
/// others with no weight transfer, and in flight it droops with them. The forces, the push-out and the attitude still
/// read only the four corner wheels.
/// </summary>
public class MiddleWheelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Flat ground at <paramref name="h"/>, raised by <paramref name="ridge"/> where |z| &lt; 0.5 (under the middle axle only).</summary>
    private sealed class Ground(float h, float ridge = 0) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (h + (System.Math.Abs(z) < 0.5 ? ridge : 0), Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    private static Vehicle SixWheeler()
    {
        var v = new Vehicle { MiddleRight = new Wheel(), MiddleLeft = new Wheel() };
        foreach (var (w, x, z) in new[] { (v.FrontRight, 0.9f, 2.4f), (v.FrontLeft, -0.9f, 2.4f), (v.MiddleRight!, 0.9f, 0f), (v.MiddleLeft!, -0.9f, 0f), (v.RearRight, 0.9f, -2.4f), (v.RearLeft, -0.9f, -2.4f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.5f, z);
            w.InitSuspension();
        }
        v.P.Wheelbase = 4.8f; v.P.Track = 1.8f;
        return v;
    }

    private static IEnumerable<Wheel> All(Vehicle v) => v.Wheels.Concat(v.MiddleWheels);

    [Fact]
    public void AtRestOnFlatGroundAllSixWheelsStandAtTheirRestTravel()
    {
        var v = SixWheeler();
        v.Pos = new Double3(0, 3, 0);
        var step = new VehicleStep(new Ground(3), new CrtRand());
        for (int i = 0; i < 100; i++) step.Frame(v, 102f / 2048f);
        foreach (var w in All(v))
        {
            Assert.Equal(3f, w.Ground);
            Assert.InRange(w.Travel, -1e-4f, 1e-4f);
            Assert.False(w.AtLimit);
        }
    }

    [Fact]
    public void OverARidgeTheMiddleWheelsFollowTheGround()
    {
        var v = SixWheeler();
        var step = new VehicleStep(new Ground(0, 0.05f), new CrtRand());
        float mid = 0, corner = 0;
        for (int i = 0; i < 20; i++)
        {
            step.Frame(v, 102f / 2048f);
            mid = MathF.Max(mid, MathF.Abs(v.MiddleRight!.Travel));
            corner = MathF.Max(corner, MathF.Abs(v.FrontRight.Travel));
        }
        Assert.Equal(0.05f, v.MiddleRight!.Ground);
        Assert.Equal(0.05f, v.MiddleLeft!.Ground);
        Assert.Equal(0f, v.FrontRight.Ground);
        Assert.True(mid > 0.01f, $"the middle wheel did not take the ridge: travel {mid}");
        Assert.True(corner < 1e-4f, $"a corner wheel moved: travel {corner}");
        // the push-out reads the corner wheels only: the body stays on them
        Assert.InRange(v.Pos.Y, -1e-3, 1e-3);
    }

    [Fact]
    public void InTheAirTheMiddlePairDroopsWithTheOthers()
    {
        var v = SixWheeler();
        v.Pos = new Double3(0, 10, 0);
        var step = new VehicleStep(new Ground(0), new CrtRand());
        for (int i = 0; i < 10; i++) step.Frame(v, 102f / 2048f);
        Assert.True((v.Flags & VehicleFlags.Airborne) != 0);
        foreach (var w in All(v)) Assert.True(w.AtLimit);
        Assert.True(v.MiddleRight!.Travel < -0.05f, $"travel {v.MiddleRight.Travel}");
    }

    /// <summary>Every stock car with a middle pair, parked on flat ground: its six wheels rest at their rest travel.</summary>
    [Fact]
    public void EveryStockSixWheelerStandsOnAllSixAtRest()
    {
        static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;
        foreach (var dir in new[] { Dir("I76_INSTALL"), Dir("I76_NITRO_INSTALL") }.OfType<string>())
        {
            var vfs = new AssetVfs(new GameInstall(dir));
            var six = new List<string>();
            foreach (var n in vfs.Names.Where(n => n.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)).Order())
            {
                Vehicle v;
                try { v = VehicleFactory.Create(vfs, n); } catch (Exception) { continue; }   // a stray .vcf whose .vdf is not shipped
                if (v.MiddleRight is null || v.Heli is not null) continue;
                six.Add(n);
                v.Pos = new Double3(0, 2, 0);
                var step = new VehicleStep(new Ground(2), new CrtRand());
                for (int i = 0; i < 100; i++) step.Frame(v, 102f / 2048f);
                foreach (var w in All(v))
                {
                    Assert.Equal(2f, w.Ground);
                    Assert.True(MathF.Abs(w.Travel) < 1e-3f, $"{n}: travel {w.Travel}");
                }
            }
            output.WriteLine($"{dir}: {six.Count} with a middle pair: {string.Join(" ", six)}");
        }
    }
}
