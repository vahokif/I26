using I76.Net;
using I76.Server;
using Xunit;

namespace I76.Net.Tests;

public partial class CoopNetTests
{
    /// <summary>A gun emplacement killed keeps its burnt shape as its collider.</summary>
    [Fact]
    public void AnEmplacementTheHostKillsCollapsesOnTheClientToo()
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined("t11");
        using var _ = g;
        var o = g.Server.Invoke(h => h.Setup.Statics.Objects.First(x => x.Vehicle?.Emplacement is not null && x.Remains is not null));
        g.Server.Invoke(h => { h.Setup.Damage.KillEmplacement(o.Vehicle!); return 0; });
        Assert.True(g.Server.Invoke(h => o.Collapsed));
        AssertSame(g, hal, $"{o.Name} killed");
    }

    /// <summary>A player who joins after the damage is done holds the scenery as it now stands (structures, signs, a dead gun).</summary>
    [Fact]
    public void AClientWhoJoinsLateHoldsTheDamagedSceneryToo()
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined("t11");
        using var _ = g;
        var (st, sign, gun) = g.Server.Invoke(h => (h.Setup.Statics.Objects.First(x => x.Vehicle is null && x.Type != 4 && x.Nodes.Any(n => n.Damageable)),
            h.Setup.Statics.Objects.First(x => x.Type == 4), h.Setup.Statics.Objects.First(x => x.Vehicle?.Emplacement is not null && x.Remains is not null)));
        g.Server.Invoke(h => { h.Setup.Damage.DestroyStructure(st); h.Setup.Damage.KnockSign(sign); h.Setup.Damage.KillEmplacement(gun.Vehicle!); return 0; });
        g.Wait(() => false, 500);
        var ann = g.Join("Ann");
        AssertSame(g, ann, "a late joiner");
    }
}
