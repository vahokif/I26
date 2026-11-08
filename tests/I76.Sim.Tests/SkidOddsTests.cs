using I76.Sim.Ai;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>tactic_skid's odds, 8 + trunc(40·(1 − skill)) in the original's extended precision: one in 31 at the default skill 0.4, not 32.</summary>
public class SkidOddsTests
{
    [Theory]
    [InlineData(1.0f, 8)]
    [InlineData(0.8f, 15)]
    [InlineData(0.6f, 23)]
    [InlineData(0.5f, 28)]
    [InlineData(0.4f, 31)]
    public void TheOddsAtEachDrivingSkill(float skill, int odds) => Assert.Equal(odds, Ptable.SkidOdds(skill));

    [Fact]
    public void TheDefaultSkillIsTheOneThatMoved()
    {
        Assert.Equal(0.4f, new AiBlock().Skill);
        Assert.Equal(32, (int)((1f - 0.4f) * 40f) + 8);   // float arithmetic: what the port drew before
    }
}
