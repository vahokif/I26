using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The base game's session popup has one line for the host's rules: when they do not fit it shows them a page at a time, whole rules to a page.</summary>
public class SessionPopupRulesTests
{
    private static int Chars(string s) => s.Length;

    [Fact]
    public void RulesThatFitAreOnePage() =>
        Assert.Equal(["Sim rate 20 Hz. Ignition stays on"], SessionPopup.RulePages("Sim rate 20 Hz. Ignition stays on", 40, Chars));

    [Fact]
    public void LongRulesArePagedWithoutSplittingARule()
    {
        var pages = SessionPopup.RulePages("Sim rate 20 Hz. Ignition stays on. Turrets as on the 1997 CD. Co-op damage x0.5. Rolling start", 40, Chars);
        Assert.Equal(["Sim rate 20 Hz. Ignition stays on", "Turrets as on the 1997 CD", "Co-op damage x0.5. Rolling start"], pages);
        Assert.All(pages, p => Assert.True(p.Length <= 40));
    }
}
