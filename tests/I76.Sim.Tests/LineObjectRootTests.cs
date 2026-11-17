using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// LDEF lines (guard rails, fences, tyre walls) as the original's loader makes them: a root of the record's class id (stock:
/// 2, and 3 for S02's rails and the tyre walls), the first link the .sdf's own node (AF1_RAL3: type 2, 75 HP), every further
/// link type 3 with the constructor's 400, the chain cut into roots of collider radius at most 20, each with 400 of its own.
/// Measured on T05: each of its nine guard rails is one root of type 2, its first link 75 HP, the others type 3 with 400, all
/// boxes.
/// </summary>
public class LineObjectRootTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static MissionSetup Build(string mission)
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        return MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", mission)!), asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
    }

    private static List<StaticObject> Lines(MissionSetup s, string name) => [.. s.LinksOf.Keys.Where(o => o.Name == name)];

    [Fact]
    public void EachOfT05sGuardRailsIsOneRootAsMeasured()
    {
        if (InstallDir is null) return;
        var s = Build("t05.msn");
        var rails = Lines(s, "afence3");
        Assert.Equal(9, rails.Count);
        foreach (var o in rails)
        {
            Assert.Equal(2u, o.Type);
            Assert.Equal(400, o.MaxHp);
            Assert.Equal(("AF1_RAL3", 2u, 75), (o.Nodes[0].Name, o.Nodes[0].Type, o.Nodes[0].MaxHp));
            Assert.All(o.Nodes.Skip(1), n => Assert.Equal((3u, 400), (n.Type, n.MaxHp)));
            Assert.Equal(o.Nodes.Count, o.Boxes.Count);   // the root type's default: a box per link
            Assert.Equal(o.Nodes.Count, s.LinksOf[o].Count);
        }
    }

    /// <summary>Every stock line's roots: within the radius, the first link of each line its record's node, the class id the record's.</summary>
    [Theory]
    [InlineData("s01.msn")]
    [InlineData("s02.msn")]
    [InlineData("s06.msn")]
    [InlineData("s07.msn")]
    public void ALineIsCutIntoRootsOfRadius20(string mission)
    {
        if (InstallDir is null) return;
        var s = Build(mission);
        var roots = s.LinksOf.Keys.ToList();
        Assert.NotEmpty(roots);
        foreach (var o in roots)
        {
            Assert.True(o.BoundRadius <= MissionSetup.LineRootRadius + 1e-3f || o.Nodes.Count == 1, $"{mission} {o.Name}: radius {o.BoundRadius:0.0} with {o.Nodes.Count} links");
            Assert.Contains(o.Type, new[] { 2u, 3u });
        }
        // a line's first root holds its record's node; the roots cut from it hold type-3 links only
        Assert.Contains(roots, o => o.Nodes[0].Type == 2);
        Assert.Contains(roots, o => o.Nodes.All(n => n.Type == 3));
    }

    [Fact]
    public void S02HasGuardRailsOfClass3AndTheTyreWallsAreClass3()
    {
        if (InstallDir is null) return;
        Assert.Contains(Lines(Build("s02.msn"), "afence3"), o => o.Type == 3);
        var tyres = Lines(Build("s06.msn"), "aitire1");
        Assert.NotEmpty(tyres);
        Assert.All(tyres, o => Assert.Equal(3u, o.Type));
        Assert.Contains(tyres, o => o.Nodes[0].MaxHp == 100);
    }

    /// <summary>A ram on a type-3 link is not quartered; on the record's type-2 link it is.</summary>
    [Fact]
    public void ARamOnALinkPastTheFirstIsNotQuartered()
    {
        if (InstallDir is null) return;
        var s = Build("s07.msn");
        var o = Lines(s, "afence4").First(x => x.Nodes.Count > 1 && x.Nodes[0].Type == 2);
        var p = new DamagePacket { Mask = 8 };
        p.Ch[3] = 399;
        s.Damage.StaticSlot(o, 1, p);
        Assert.Equal(1, o.Nodes[1].Hp);
        var q = new DamagePacket { Mask = 8 };
        q.Ch[3] = 800;
        s.Damage.StaticSlot(o, 0, q);
        Assert.Equal(1000 - 200, o.Nodes[0].Hp);
    }

    /// <summary>A splash takes a root's 400 and every link of that root with it, and no other root's.</summary>
    [Fact]
    public void ASplashTakesARootsLinksAndNoOthers()
    {
        if (InstallDir is null) return;
        var s = Build("t05.msn");
        var rails = Lines(s, "afence3");
        var o = rails[0];
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 401;
        s.Damage.Apply(o, p, Vector3.Zero, -1);
        Assert.True(o.Destroyed);
        Assert.All(o.Nodes, n => Assert.True(n.Destroyed));
        Assert.All(rails.Skip(1), r => Assert.False(r.Destroyed));
        // a dead type-3 link is driven through; its wreck still stops rounds
        Assert.True(o.Unfelt(1));
        Assert.False(o.Unfelt(0));
    }

    /// <summary>A link shot down is a direct child of its root: the root is flagged and refuses splash from then on.</summary>
    [Fact]
    public void ALinkShotDownFlagsItsRoot()
    {
        if (InstallDir is null) return;
        var s = Build("t05.msn");
        var o = Lines(s, "afence3").First(x => x.Nodes.Count > 2);
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 401;
        s.Damage.Apply(o, p, Vector3.Zero, 2);
        Assert.True(o.Nodes[2].Destroyed);
        Assert.True(o.Destroyed);
        Assert.False(o.Nodes[1].Destroyed);
        Assert.Equal(400, o.Hp);
    }
}
