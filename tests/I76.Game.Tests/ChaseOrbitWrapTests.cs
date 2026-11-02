using Xunit;

namespace I76.Game.Tests;

/// <summary>The chase view's turn wraps its angles as the engine's wrap did (the camera used Godot's before it moved to <see cref="I76.Sim.Views.ChaseOrbit"/>).</summary>
public class ChaseOrbitWrapTests
{
    [Fact]
    public void TheWrapIsTheEnginesWrap()
    {
        var rng = new System.Random(16);
        float[] edges = [0, MathF.PI, -MathF.PI, MathF.Tau, -MathF.Tau, 3 * MathF.PI, 1e-7f, -1e-7f, MathF.PI - 1e-6f, -MathF.PI + 1e-6f];
        foreach (float a in edges.Concat(Enumerable.Range(0, 20000).Select(_ => (float)(rng.NextDouble() * 80 - 40))))
            Assert.Equal(Godot.Mathf.Wrap(a, -Godot.Mathf.Pi, Godot.Mathf.Pi), I76.Sim.Views.ChaseOrbit.Wrap(a));
    }
}
