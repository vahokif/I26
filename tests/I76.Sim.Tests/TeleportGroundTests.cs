using System.Numerics;
using I76.Formats.Mission;
using I76.Sim.Fsm;
using I76.Sim.Rng;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// teleport / teleportOffset: the original writes the terrain height under the path's node 0 into the node itself, and
/// places the object at that height; the offset moves it across only.
/// </summary>
public class TeleportGroundTests
{
    private sealed class GroundHost : RecordingHost, IFsmHost
    {
        public readonly List<Vector3> At = [];
        public float? GroundHeight(float x, float z) => x * 0.1f + z * 0.01f;   // a slope: the height differs under an offset
        public new void Teleport(object o, Vector3 at, int angleDegrees, float speed) => At.Add(at);
    }

    private static (GroundHost, FsmVm, NavPath) Run(string action, params int[] extra)
    {
        var path = new NavPath("p", [new Vector3(100, 50, 200), new Vector3(150, 50, 200)]);
        int[] args = [0, 0, 20, 90, .. extra];
        var code = new List<(uint, uint)> { (6, 1) };
        foreach (int a in args) code.Add((1, unchecked((uint)a)));
        for (int i = 0; i < args.Length; i++) code.Add((4, (uint)(2 + i)));
        code.Add((13, 0));
        code.Add((7, (uint)args.Length));
        code.Add((10, (uint)code.Count));
        var host = new GroundHost();
        var prog = new FsmProgram([action], [("car", [1, 2, 3, 4])], [], [path], [(0, 0, [])], [], [.. code], true);
        var vm = new FsmVm(prog, host, new CrtRand());
        vm.Tick();
        return (host, vm, path);
    }

    [Fact]
    public void TeleportPutsNodeZeroOnTheGround()
    {
        var (host, _, path) = Run("teleport");
        Assert.Equal(10f + 2f, path.Points[0].Y, 4);       // written into the path
        Assert.Equal(50f, path.Points[1].Y);                // the other nodes as they were
        Assert.Equal(new Vector3(100, 12, 200), Assert.Single(host.At));
    }

    [Fact]
    public void AnOffsetMovesAcrossAtNodeZerosHeight()
    {
        var (host, _, path) = Run("teleportOffset", 2000, -250);
        Assert.Equal(12f, path.Points[0].Y, 4);
        var at = Assert.Single(host.At);
        Assert.Equal(120f, at.X, 4);
        Assert.Equal(197.5f, at.Z, 4);
        Assert.Equal(12f, at.Y, 4);                          // not the height under the offset point (14.0)
    }
}
