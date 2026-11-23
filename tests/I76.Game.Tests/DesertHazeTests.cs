using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The haze over the distance (docs/desert-haze.md): the row in Graphic Detail is on for a new player and a file
/// keeps the choice, and the air the shader counts is the air these numbers describe — more of it down in a
/// valley, a third of it up a ridge, and none of it asking for the draw distance's fade. The maths is the
/// shader's own (Shaders/haze.gdshaderinc), so the numbers a capture should show are checkable without a GPU.
/// </summary>
public class DesertHazeTests
{
    [Fact]
    public void ANewPlayerHasItOn() => Assert.True(new ShellSettings().DesertHaze);

    [Theory]
    [InlineData(null, true)]      // never written: the feature ships on
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void AFileKeepsItsChoice(string? stored, bool on) => Assert.Equal(on, ShellSettings.DesertHazeFrom(stored));

    /// <summary>
    /// A kilometre of level air at the reference level is worth a tenth: the distance haze that does not touch the
    /// car in front but does go for the colour of a range of hills.
    /// </summary>
    [Fact]
    public void AKilometreOfLevelAirIsATenth()
    {
        Assert.Equal(0.1f, I76.Game.World.DesertHaze.SlantDepth(0f, 0f, 1000f), 4);
        Assert.Equal(0.095f, I76.Game.World.DesertHaze.Alpha(0f, 0f, 1000f), 3);
    }

    /// <summary>A ray along the layer's own level has no spread to divide by; the series must give the plain product.</summary>
    [Theory]
    [InlineData(200f)]
    [InlineData(1000f)]
    [InlineData(6000f)]
    public void ALevelRayIsDensityTimesDistance(float dist) =>
        Assert.Equal(I76.Game.World.DesertHaze.Density * dist, I76.Game.World.DesertHaze.SlantDepth(0f, 0f, dist), 5);

    /// <summary>Farther is more of it, and smoothly: what makes the horizon read as distance and not as a wall.</summary>
    [Fact]
    public void TheDistanceGrows()
    {
        float near = I76.Game.World.DesertHaze.Alpha(0f, 0f, 500f);
        float mid = I76.Game.World.DesertHaze.Alpha(0f, 0f, 2000f);
        float far = I76.Game.World.DesertHaze.Alpha(0f, 0f, 8000f);
        Assert.True(near < mid);
        Assert.True(mid < far);
        Assert.True(far < 1f);      // and it never goes to a flat wash of colour over the whole map
    }

    /// <summary>
    /// Thin on the high ground: the same kilometre of level air seen from one scale height up (400 m, more than
    /// these maps' own relief) is worth a third of what it is worth from the level — the peaks keep their colour.
    /// </summary>
    [Fact]
    public void HighGroundIsThinner()
    {
        float h = I76.Game.World.DesertHaze.ScaleHeight;
        float level = I76.Game.World.DesertHaze.SlantDepth(0f, 0f, 1000f);
        float up = I76.Game.World.DesertHaze.SlantDepth(h, 0f, 1000f);
        Assert.Equal(level * MathF.Exp(-1f), up, 4);
        // and looking up a slope is thinner still than along it, since the ray leaves the layer
        Assert.True(I76.Game.World.DesertHaze.SlantDepth(0f, 0.5f, 2000f) <
                    I76.Game.World.DesertHaze.SlantDepth(0f, 0f, 2000f));
    }

    /// <summary>
    /// Strong in the valleys: what the eye looks along is the thick low air, so an eye one scale height under the
    /// level (400 m down) is worth e times the level — and the law is only trusted down to 1.2 scale heights, so a
    /// ray that leaves the map cannot ask for an ocean of it.
    /// </summary>
    [Fact]
    public void AValleyIsThickerAndCapped()
    {
        float h = I76.Game.World.DesertHaze.ScaleHeight;
        float level = I76.Game.World.DesertHaze.SlantDepth(0f, 0f, 1000f);
        Assert.True(I76.Game.World.DesertHaze.SlantDepth(-h, 0f, 1000f) > level * 2.7f);
        Assert.Equal(level * MathF.Exp(I76.Game.World.DesertHaze.Climb),
                     I76.Game.World.DesertHaze.SlantDepth(-40f * h, 0f, 1000f), 4);
    }

    /// <summary>
    /// A ray down into the ground, or a ray a huge map runs to the far clip, gathers the most air there is — and
    /// stays a number: the exponential over a long downward run is inf on its own, which the shader caps rather
    /// than draws as a NaN.
    /// </summary>
    [Theory]
    [InlineData(-0.5f, 2000f)]
    [InlineData(-1f, 20000f)]
    [InlineData(-1f, 100000f)]
    public void ADownwardRayStaysANumber(float dirY, float dist)
    {
        float tau = I76.Game.World.DesertHaze.SlantDepth(0f, dirY, dist);
        Assert.False(float.IsNaN(tau) || float.IsInfinity(tau));
        Assert.InRange(tau, 0f, 8f);
    }

    /// <summary>No air, no haze: the switch the density env var is, and the same for a zero-length ray.</summary>
    [Fact]
    public void NothingForClearAir() => Assert.Equal(0f, I76.Game.World.DesertHaze.Alpha(0f, 0f, 0f));

    /// <summary>
    /// The lobe: 1 straight into the sun, a fortieth of it at right angles, and still falling past that — which is
    /// the whole of "gold toward the sun, the plain sky colour away from it", and it has to peak at 1 for the
    /// tint's own share to mean what the menu's number says.
    /// </summary>
    [Fact]
    public void TheLobePeaksAtOne()
    {
        Assert.Equal(1f, I76.Game.World.DesertHaze.Phase(1f), 5);
        Assert.Equal(0.04f, I76.Game.World.DesertHaze.Phase(0f), 3);
        Assert.True(I76.Game.World.DesertHaze.Phase(0f) > I76.Game.World.DesertHaze.Phase(-1f));   // and it keeps falling past a right angle, the small share a hazy sky does keep behind you
        float last = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float v = I76.Game.World.DesertHaze.Phase(i / 20f);
            Assert.True(v > last);     // one way only, toward the sun
            last = v;
        }
    }

    /// <summary>
    /// A global uniform is not registered by declaring it: an unregistered name drops every push to it, silently,
    /// and the haze would simply never appear. So the shader's declarations and project.godot's list are checked
    /// against each other.
    /// </summary>
    [Fact]
    public void EveryHazeGlobalIsRegistered()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "project.godot"))) dir = Path.GetDirectoryName(dir)!;
        var declared = new HashSet<string>();
        foreach (var shader in Directory.GetFiles(Path.Combine(dir!, "game", "Shaders"), "*.gdshader*"))
            foreach (Match m in Regex.Matches(File.ReadAllText(shader), @"global uniform\s+\w+\s+(i76_haze_\w+)"))
                declared.Add(m.Groups[1].Value);
        Assert.NotEmpty(declared);
        var registered = File.ReadAllText(Path.Combine(dir!, "game", "project.godot"));
        foreach (var name in declared)
            Assert.True(registered.Contains($"\n{name}={{"), $"{name} is declared by a shader but not in [shader_globals]");
    }
}
