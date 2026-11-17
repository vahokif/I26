using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// A script's hpLesser on a structure reads the original's object health percentage: for a structure root the least
/// HP / max of the root's direct children that are structures with a collider, not the root's own 400 (which only splash
/// takes). Stock scripts read it on buildings: T03 sets the Bar-D's guards on the player once the Bar-D is below 99 %,
/// S02 plays its cut-scene and destroys the Mondo Burger below 10 %, S01 counts a trailer hit below 100 %, T14 the
/// truck stop below 50 %. So rounds on the building count, as in the original; a splash on the root alone does not.
/// </summary>
public class StructureHealthTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static void Hit(Missions.MissionSetup setup, StaticObject o, int node, int amount)
    {
        var p = new DamagePacket { Mask = 1 };
        p.Ch[0] = amount;
        setup.Damage.Apply(o, p, Vector3.Zero, node);
    }

    [Fact]
    public void T03sBarDIsBelow99OnceItsBuildingIsShot()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t03.msn");
        var bar = setup.Statics.Objects.Single(o => o.Name == "bdbard1");
        var e = setup.Entities.Single(x => x.Static == bar);
        Assert.Equal(100f, e.Health);
        // a splash on the root is not part of it
        var splash = new DamagePacket { Mask = 2 };
        splash.Ch[1] = 100;
        setup.Damage.Apply(bar, splash, Vector3.Zero, -1);
        Assert.Equal(300, bar.Hp);
        Assert.Equal(100f, e.Health);
        // DB1_BLD1, 1500 HP, the only top-level structure node with a collider: 20 off it is 98.7 %
        Hit(setup, bar, bar.Nodes.FindIndex(n => n.Name == "DB1_BLD1"), 20);
        Assert.Equal(100f * 1480 / 1500, e.Health, 3);
        Assert.True(e.Health < 99);
    }

    /// <summary>
    /// bcaik_s7: the shell CA1_07L6 (wreck CA1A07L6) and the interior CA1_IN76 (no wreck) are the top-level structure nodes,
    /// the door a gate. A dead child with no wreck no longer counts; one with a wreck counts 0.
    /// </summary>
    [Fact]
    public void ADeadChildCountsZeroWhileItsWreckStandsAndNotAtAllWithout()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcaik_s7", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcaik_s7", 4100, 36700);
        int shell = o.Nodes.FindIndex(n => n.Name == "CA1_07L6"), inside = o.Nodes.FindIndex(n => n.Name == "CA1_IN76");
        Assert.True(o.Nodes[shell].Wreck);
        Assert.False(o.Nodes[inside].Wreck);
        Hit(setup, o, shell, 400);   // 800 → 400
        Assert.Equal(50f, o.HealthPercent());
        Hit(setup, o, inside, 1000);
        Assert.Equal(50f, o.HealthPercent());
        Hit(setup, o, shell, 1000);
        Assert.Equal(0f, o.HealthPercent());
    }

    /// <summary>A road sign placed as Struct1 (two type-4 nodes, empty destroyed slots): once its root dies nothing counts, and nothing is less than what it reads.</summary>
    [Fact]
    public void WithNoChildLeftNothingIsLess()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        Assert.Equal(100f, o.HealthPercent());
        setup.Damage.DestroyStructure(o);
        for (int i = 0; i < 5; i++) setup.Combat.Explode("x1_gren1", o.Centre + new Vector3(10, 0, 0), null);
        Assert.True(o.Destroyed);
        Assert.False(o.HealthPercent() < 100);
        Assert.True(o.HealthPercent() > 1e30f);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(7u)]
    public void ASignOrGateClassPlacementReads100(uint type)
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcbank1", type, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcbank1", 4100, 36700);
        Hit(setup, o, o.Nodes.FindIndex(n => n.Name == "CB1_BLD1"), 200);
        Assert.Equal(100f, o.HealthPercent());
    }
}
