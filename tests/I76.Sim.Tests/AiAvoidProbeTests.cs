using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The avoidance probe of the candidate test (avoid clsn's entry tests, its re-tests and its input search): two tracks
/// 2.5 either side of the predicted position, stepped 2.5 at a time to max(20, 1.5 × speed). The original tests the
/// height cell's no-go bit on the first track only, the car's left, and gives the second track no no-go test; read from
/// the original and checked on a captured T05 avoid clsn entry (t05fp03, idle, 37.591 s: the left-first probe hits on the
/// captured frame, a right-first one 0.14 s earlier).
/// </summary>
public class AiAvoidProbeTests
{
    /// <summary>Flat ground with no-go cells 1 wide centred on x, from 5 to 15 ahead of the origin and behind it.</summary>
    private sealed class NoGoStrip(float x) : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double qx, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double qx, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
        public bool Impassable(double qx, double z) => System.Math.Abs(qx - x) < 0.5 && System.Math.Abs(z) is > 5 and < 15;
    }

    /// <summary>The candidate test, static checks on, for a car at rest at the origin facing +Z in this gear.</summary>
    private static bool Blocked(float stripX, float gear)
    {
        var v = new Vehicle { Pos = new Double3(0, 0, 0), Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), Gear = gear };
        var e = new Entity("ai", [], 1, 0) { Vehicle = v };
        var world = new SimWorld(new NoGoStrip(stripX), new I76.Sim.Rng.CrtRand(1));
        world.Vehicles.Add(v);
        var host = new ScriptHost(world, new List<Entity> { e }, (Objectives)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        host.FrameDelta = SimRate.R20.TickSeconds;
        var m = typeof(Ptable).GetMethod("Candidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object?[] args = [e, 0f, 0f, null, 0f, 0f, false, true, 0f];
        return (bool)m.Invoke(host.Ptable, args)!;
    }

    [Fact]
    public void TheNoGoBitIsTestedOnTheLeftTrackOnly()
    {
        // +X is the car's right (the right row of a car facing +Z)
        Assert.True(Blocked(-2.5f, 1f));     // under the left track
        Assert.False(Blocked(0f, 1f));       // under the centre line: between the tracks
        Assert.False(Blocked(2.5f, 1f));     // under the right track: it has no no-go test
        // reversing, the tracks run backwards and the first is still the car's left
        Assert.True(Blocked(-2.5f, -1f));
        Assert.False(Blocked(0f, -1f));
        Assert.False(Blocked(2.5f, -1f));
    }
}
