using I76.Game.Sim;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// A car's drop shadow over the skid marks it lays (#217). The modern look's shadow is layers blended over what is drawn before
/// them, so the marks go first and the shadow darkens a mark as it darkens the ground; drawn after the shadow, a mark came out
/// at its full sunlit colour inside it. The 1997 look's shadow writes the opaque frame back darkened, so it goes before the marks,
/// which would otherwise be wiped out under it.
/// </summary>
public class SkidMarkShadowOrderTests
{
    [Fact]
    public void TheMarksDrawBetweenThe1997ShadowAndTheModernOnes()
    {
        Assert.True(DropShadows.FlatPriority < SkidMarks.Priority);
        Assert.True(SkidMarks.Priority < DropShadows.SoftPriority);
    }

    [Fact]
    public void EveryShadowLayerDrawsAfterTheMarks()
    {
        // the layers take SoftPriority + layer; all within Godot's range of priorities
        Assert.InRange(DropShadows.SoftPriority + 9, SkidMarks.Priority + 1, (int)Godot.Material.RenderPriorityMax);
        Assert.InRange(DropShadows.FlatPriority, (int)Godot.Material.RenderPriorityMin, SkidMarks.Priority - 1);
    }
}
