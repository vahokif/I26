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

    [Fact] public void TheSupersampledViewIsNoBiggerThanFourK()
    {
        Assert.Equal(2f, RenderScale.Capped(2f, new Vector2I(1920, 1080)));     // 3840x2160
        Assert.Equal(1.5f, RenderScale.Capped(1.5f, new Vector2I(2560, 1440)));  // 3840x2160
        Assert.Equal(1.5f, RenderScale.Capped(2f, new Vector2I(2560, 1440)), 3);  // 200 % lowered to 4K's pixels
        Assert.Equal(1f, RenderScale.Capped(2f, new Vector2I(3840, 2160)));      // a 4K desktop: drawn as it is
        Assert.Equal(1f, RenderScale.Capped(2f, new Vector2I(5120, 2880)));      // never under 100 %
        Assert.Equal(1f, RenderScale.Capped(1f, new Vector2I(1280, 720)));
        Assert.Equal(2f, RenderScale.Capped(2f, new Vector2I(0, 0)));           // no size yet: as chosen
    }
}
