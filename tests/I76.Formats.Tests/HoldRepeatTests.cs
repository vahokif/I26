using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

public class HoldRepeatTests
{
    [Fact]
    public void AHeldArrowActsAfter350msThenTenTimesASecond()
    {
        var r = new HoldRepeat();
        r.Begin(1);
        int acts = 0;
        double t = 0, first = -1;
        for (int i = 0; i < 120; i++, t += 1 / 60.0)      // 60 Hz for two seconds
        {
            int n = r.Update(true, 1, 1 / 60.0);
            if (n > 0 && first < 0) first = t;
            acts += n;
        }
        Assert.InRange(first, 0.34, 0.37);
        Assert.InRange(acts, 16, 17);                      // (2.0 - 0.35) / 0.1
    }

    [Fact]
    public void LettingGoOrLeavingTheArrowEndsTheRepeat()
    {
        var r = new HoldRepeat();
        r.Begin(0);
        Assert.Equal(0, r.Update(false, 0, 1.0));
        Assert.Equal(-1, r.Id);
        r.Begin(0);
        Assert.Equal(0, r.Update(true, 1, 1.0));           // slid onto the other arrow
        Assert.Equal(-1, r.Id);
        Assert.Equal(0, r.Update(true, 1, 1.0));
    }

    [Fact]
    public void ASlowFrameOwesTheStepsItSkipped()
    {
        var r = new HoldRepeat();
        r.Begin(0);
        Assert.Equal(0, r.Update(true, 0, 0.3));
        Assert.Equal(4, r.Update(true, 0, 0.36));          // 0.31 s past the delay: the step due then and three more
    }

    [Fact]
    public void ShrinkingAboutTheEyeKeepsWhereAPointShowsButBringsItNearer()
    {
        var eye = new Vector3(-0.4f, 1.15f, -0.23f);
        var place = new Affine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(-0.43f, 1.29f, -0.07f));
        var v = new Vector3(0.05f, 0.03f, 0.01f);
        var before = place.Apply(v) - eye;
        var after = place.ScaledAbout(eye, PanelPriority.Scale).Apply(v) - eye;
        Assert.Equal(before.X / before.Z, after.X / after.Z, 4);
        Assert.Equal(before.Y / before.Z, after.Y / after.Z, 4);
        Assert.Equal(before.Z * PanelPriority.Scale, after.Z, 4);
        Assert.True(after.Length() > 0.05f);               // outside the cockpit camera's near plane
    }

    [Theory]
    [InlineData("GO51WEP6", true)]
    [InlineData("CV51WEP3", true)]
    [InlineData("GO51SPC6", true)]
    [InlineData("GO51SYS6", true)]
    [InlineData("GO51RADR", true)]
    [InlineData("GO51BDYT", false)]
    [InlineData("GO51DASH", false)]
    [InlineData("GO51MIRI", false)]
    public void TheRoofPanelsAreWeaponsSpecialsDamageAndRadar(string node, bool panel) => Assert.Equal(panel, PanelPriority.IsRoofPanel(node));

    [InstallFact]
    public void TheSpecialsPanelOnTheDashStaysBehindTheSteeringWheel()
    {
        // the Piranha: weapons, damage and radar on the roof above the eye; the specials on the dash below it, behind the wheel (#36)
        var m = new ModelAssembler(TestInstall.Vfs) { SkipParts = [] }.Assemble("vppirna1.vcf")!;
        var interior = new ModelAssembler(TestInstall.Vfs) { Detail = '5' }.Assemble("vppirna1.vcf")!;
        var t = m.Locations.First(l => l.Tag == "VLOC" && l.Index == 40).Transform.Translation;
        var eye = new Vector3(t[0], t[1], t[2]);
        bool Pulled(string code) => PanelPriority.PullForward(interior.Parts.First(p => p.NodeName.EndsWith(code)), eye);
        Assert.True(Pulled("WEP6"));
        Assert.True(Pulled("SYS6"));
        Assert.True(Pulled("RADR"));
        Assert.False(Pulled("SPC6"));
        Assert.False(Pulled("SWHL"));
    }
}
