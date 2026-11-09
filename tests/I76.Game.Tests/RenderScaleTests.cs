using Godot;
using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Graphic Detail's Render Scale and the Anti-Aliasing row's 8x step.</summary>
public class RenderScaleTests
{
    [Fact] public void ScalesAre100And150And200Percent()
    {
        Assert.Equal(1f, RenderScale.Scale(0));
        Assert.Equal(1.5f, RenderScale.Scale(1));
        Assert.Equal(2f, RenderScale.Scale(2));
        Assert.Equal(2f, RenderScale.Scale(9));   // clamped
        Assert.Equal(1f, RenderScale.Scale(-1));
    }

    [Fact] public void ParseKeepsTheDefaultForAnythingNotOffered()
    {
        Assert.Equal(0, RenderScale.Parse(null));
        Assert.Equal(0, RenderScale.Parse(100));
        Assert.Equal(0, RenderScale.Parse(175));
        Assert.Equal(1, RenderScale.Parse(150));
        Assert.Equal(2, RenderScale.Parse(200));
    }

    [Fact] public void TheAntiAliasingRowHasEightX()
    {
        Assert.Equal(["Off", "2x", "4x", "8x"], RenderScale.AntiAliasing);
        Assert.Equal(Viewport.Msaa.Disabled, RenderScale.Msaa(0, false));
        Assert.Equal(Viewport.Msaa.Msaa2X, RenderScale.Msaa(1, false));
        Assert.Equal(Viewport.Msaa.Msaa4X, RenderScale.Msaa(2, false));
        Assert.Equal(Viewport.Msaa.Msaa8X, RenderScale.Msaa(3, false));
    }

    [Fact] public void EightXIsFourXWhereTheRendererHasNoEightX() => Assert.Equal(Viewport.Msaa.Msaa4X, RenderScale.Msaa(3, true));
}
