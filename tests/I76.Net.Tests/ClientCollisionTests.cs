using System.Numerics;
using I76.Net;
using I76.Server;
using I76.Sim.Collision;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Net.Tests;

/// <summary>
/// Host-vs-client parity of what a car collides with or drives on: when the host changes a structure at run time, the client's
/// mirror (the world its own car is predicted against) must hold the same colliders, or the prediction meets scenery that is
/// gone, or drives through scenery that is there, and is snapped back.
/// </summary>
public partial class CoopNetTests
{
    /// <summary>A placement's colliders as a comparable list: every polygon's and surface's first vertex, every box, the circle and whether it is solid.</summary>
    private static List<string> Colliders(StaticObject o) =>
    [
        $"solid {o.Solid} centre {o.Centre.X:0.00},{o.Centre.Y:0.00},{o.Centre.Z:0.00} r {o.Radius:0.00}",
        .. o.Polys.Select(p => $"poly {p.Node} {p.V[0].X:0.00},{p.V[0].Y:0.00},{p.V[0].Z:0.00} n{p.V.Length}"),
        .. o.Surfaces.Select(p => $"surface {p.Node} {p.V[0].X:0.00},{p.V[0].Y:0.00},{p.V[0].Z:0.00} n{p.V.Length}"),
        .. o.Boxes.Select(b => $"box {b.Node} {b.Origin.X:0.00},{b.Origin.Y:0.00},{b.Origin.Z:0.00} {b.Min} {b.Max}"),
    ];

    /// <summary>The whole mirror against the host: every placement's colliders the same. Returns the first difference or null.</summary>
    private static string? FirstDifference(Game g, Player p)
    {
        var host = g.Server.Invoke(h => h.Setup.Statics.Objects.ToDictionary(o => o.NetId, o => Colliders(o)));
        foreach (var m in p.Mirror!.Setup.Statics.Objects)
        {
            if (!host.TryGetValue(m.NetId, out var want)) return $"{m.Name} {m.NetId}: not on the host";
            var got = Colliders(m);
            if (!want.SequenceEqual(got)) return $"{m.Name} {m.NetId}: host [{string.Join("; ", want)}] client [{string.Join("; ", got)}]";
        }
        return null;
    }

    private static void AssertSame(Game g, Player p, string what)
    {
        g.Wait(() => false, 600);
        Assert.True(FirstDifference(g, p) is null, $"{what}: {FirstDifference(g, p)}");
    }

    private (Game Game, Player Hal) Joined(string mission)
    {
        var install = HostTests.InstallDir!;
        var g = Start(install, mission);
        var hal = g.Join("Hal");
        Assert.True(g.Wait(() => g.Server.Invoke(h => h.Setup.Script!.Host.Now) > 1));
        AssertSame(g, hal, "at the start");
        return (g, hal);
    }

    private static void Hit(Game g, StaticObject o, int node)
    {
        g.Server.Invoke(h =>
        {
            var p = new DamagePacket { Mask = 8 };
            p.Ch[3] = 100000;
            h.Setup.Damage.StructureSlot(o, node, p);
            return 0;
        });
    }

    /// <summary>A node shot down (a bridge's span, a tower's bin): the client's colliders drop it and take its wreck as the host's do.</summary>
    [Theory]
    [InlineData("t11", 2)]
    public void ANodeTheHostDestroysIsGoneFromTheClientsCollidersToo(string mission, uint type)
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined(mission);
        using var _ = g;
        var pick = g.Server.Invoke(h => h.Setup.Statics.Objects.Where(o => o.Vehicle is null && o.Nodes.Count > 1 && o.Nodes.Any(n => n.Damageable && n.Type == type) || o.Vehicle is null && o.Type == type && o.Nodes.Any(n => n.Damageable))
            .Select(o => (o.NetId, Node: o.Nodes.FindIndex(n => n.Damageable && n.Type == type))).FirstOrDefault(x => x.NetId != 0 && x.Node >= 0));
        if (pick.NetId == 0) { output.WriteLine($"no damageable node of type {type} in {mission}"); return; }
        var o = g.Server.Invoke(h => h.Setup.Statics.Objects.First(x => x.NetId == pick.NetId));
        Hit(g, o, pick.Node);
        Assert.True(g.Server.Invoke(h => o.Nodes[pick.Node].Destroyed));
        AssertSame(g, hal, $"{o.Name} node {pick.Node} destroyed");
    }

    /// <summary>A whole placement the script destroys (destroyObject).</summary>
    [Fact]
    public void ARootTheHostDestroysIsGoneFromTheClientsCollidersToo()
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined("t11");
        using var _ = g;
        var o = g.Server.Invoke(h => h.Setup.Statics.Objects.First(x => x.Vehicle is null && x.IsStructure && x.Nodes.Count > 0 && x.Name.Length > 0));
        g.Server.Invoke(h => { h.Setup.Damage.DestroyStructure(o); return 0; });
        Assert.True(g.Server.Invoke(h => o.Destroyed));
        AssertSame(g, hal, $"{o.Name} destroyed");
    }

    /// <summary>A sign driven into (or shot) is knocked off: it stops colliding.</summary>
    [Fact]
    public void ASignTheHostKnocksIsGoneFromTheClientsCollidersToo()
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined("t11");
        using var _ = g;
        var o = g.Server.Invoke(h => h.Setup.Statics.Objects.First(x => x.Type == 4 && !x.Destroyed));
        g.Server.Invoke(h => { h.Setup.Damage.KnockSign(o); return 0; });
        Assert.True(g.Server.Invoke(h => o.Destroyed));
        AssertSame(g, hal, $"{o.Name} knocked");
    }

    /// <summary>A bridge the script destroys: its span stops carrying cars on the client as on the host.</summary>
    [Fact]
    public void ABridgeTheHostDestroysIsGoneFromTheClientsCollidersToo()
    {
        if (HostTests.InstallDir is null) return;
        var (g, hal) = Joined("t11");
        using var _ = g;
        var o = g.Server.Invoke(h => h.Setup.Statics.Objects.First(x => x.Type == 0xc && x.Surfaces.Count > 0));
        g.Server.Invoke(h => { h.Setup.Damage.DestroyStructure(o); return 0; });
        output.WriteLine($"bridge {o.Name}: destroyed {g.Server.Invoke(h => o.Destroyed)}, {g.Server.Invoke(h => o.Surfaces.Count)} surfaces left");
        AssertSame(g, hal, $"{o.Name} destroyed");
    }

}

public partial class CombatEffectsTests
{
    /// <summary>A melee arena: structures shot down, a sign knocked, then a second client joining; both mirrors hold the host's colliders.</summary>
    [Fact]
    public void AMeleeClientsCollidersFollowTheArenasDamage()
    {
        if (HostTests.InstallDir is not { } install) return;
        var host = new MeleeHost(new ServerOptions { Install = install, Arena = "m01", Seed = 3 });
        var seat = host.Join("a", "vppirna1.vcf", 0);
        var c = Connect(host, seat, HostTests.Mirror("m01"));
        var rig = new Rig(host, late: 3);
        rig.Clients.Add(c);
        for (int i = 0; i < 10; i++) rig.Tick(_ => default);
        var objs = host.Setup.Statics.Objects;
        var node = objs.Select(o => (o, n: o.Nodes.FindIndex(n => n.Damageable))).Where(x => x.n >= 0).Take(3).ToList();
        var sign = objs.FirstOrDefault(o => o.Type == 4);
        output.WriteLine($"{node.Count} damageable placements, sign {sign?.Name}");
        Assert.NotEmpty(node);
        foreach (var (o, n) in node)
        {
            var p = new DamagePacket { Mask = 8 };
            p.Ch[3] = 100000;
            host.Setup.Damage.StructureSlot(o, n, p);
        }
        if (sign is not null) host.Setup.Damage.KnockSign(sign);
        for (int i = 0; i < 30; i++) rig.Tick(_ => default);
        string? Diff(Client k) =>
            k.Mirror.Setup.Statics.Objects.Select(m => (m, h: objs.First(o => o.NetId == m.NetId))).FirstOrDefault(x => !x.h.Polys.Select(p => p.V[0]).SequenceEqual(x.m.Polys.Select(p => p.V[0]))
                || !x.h.Boxes.Select(b => b.Origin).SequenceEqual(x.m.Boxes.Select(b => b.Origin)) || x.h.Surfaces.Count != x.m.Surfaces.Count || x.h.Solid != x.m.Solid).m?.Name;
        Assert.Null(Diff(c));
        var late = Connect(host, host.Join("b", "vppirna1.vcf", 1), HostTests.Mirror("m01"));
        Assert.Null(Diff(late));
    }
}
