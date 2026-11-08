using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The frame's contact pass in the original's order: each mover against every later mover, then against the statics.
/// A car keeps the last record written, so a wall met in the same pass as a car wins.
/// </summary>
public class ContactOrderTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class FlatWorld(StaticWorld w) : IGround, IHasStatics
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public StaticWorld World => w;
    }

    private static Vehicle Car(float z, float speed)
    {
        var v = new Vehicle();
        foreach (var (w, x, wz) in new[] { (v.FrontRight, 0.8f, 1.4f), (v.FrontLeft, -0.8f, 1.4f), (v.RearRight, 0.8f, -1.3f), (v.RearLeft, -0.8f, -1.3f) })
        {
            w.Grip = 0.5f; w.Rolling = 1f; w.Offset = new Vector3(x, 0.33f, wz);
            w.InitSuspension();
        }
        v.P.Wheelbase = 2.7f; v.P.Track = 1.6f; v.P.Mass = 1500;
        v.BoxX = [-0.9f, -0.5f, 0.5f, 0.9f]; v.BoxY = [0, 0.3f, 1f, 1.4f]; v.BoxZ = [-2.1f, -1f, 1f, 2.1f];
        v.Pos = new Double3(0, 0, z);
        v.Vel = new Vector3(0, 0, speed); v.Speed = speed;
        return v;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACarPushedIntoAWallBouncesOffTheWall(bool frontCarFirst)
    {
        var walls = new StaticWorld();
        walls.AddBox("wall", 3, new Vector3(-5, 0, 40), new Vector3(5, 4, 44));
        var sim = new SimWorld(new FlatWorld(walls), new CrtRand(1));
        // the front car rolls into the wall; the one behind closes on it within the same frame
        var front = Car(37.8f, 4f);
        var back = Car(33.0f, 30f);
        if (frontCarFirst) { sim.Vehicles.Add(front); sim.Vehicles.Add(back); }
        else { sim.Vehicles.Add(back); sim.Vehicles.Add(front); }
        bool both = false;
        int cars = 0;
        sim.CarContact = (a, b, rec) => { if (a == front) cars++; };
        for (int i = 0; i < 60 && !both; i++)
        {
            cars = 0;
            sim.Step.ObjectHits.Clear();
            sim.Frame(102f / 2048f);
            bool walled = sim.Step.ObjectHits.Exists(h => h.Self == front);
            if (walled && cars > 0)
            {
                both = true;
                output.WriteLine($"frame {i}: front vz {front.Vel.Z:0.00}, z {front.Pos.Z:0.00}");
                // the wall's record was the last one written: the car is thrown back off the wall, not on into it
                Assert.True(front.Vel.Z < 0, $"the car's record won: vz {front.Vel.Z}");
            }
        }
        Assert.True(both, "no frame met the car and the wall together");
        for (int i = 0; i < 60; i++) sim.Frame(102f / 2048f);
        Assert.True(front.Pos.Z + 2.1 < 40.05, $"the front car is in the wall: front at {front.Pos.Z + 2.1}");
    }
}
