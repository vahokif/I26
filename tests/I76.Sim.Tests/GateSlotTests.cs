using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Gates (type-7 nodes) and spinners (type 10) have damage slots of their own in the original: their record's hit points,
/// any packet, no quartering. A gate's death swaps it and what hangs under it to the destroyed look (every stock door and
/// spinner: nothing), marks it dead, flags the root when it is a direct child (no marker explosion), rebuilds the
/// colliders and throws no chunks. So an Autowerks garage door is shot open, and a community mission maker's three shots
/// through bcaik_s7 (its shell, door and interior all top-level) are three separate deaths. The original has no dead test
/// in this slot (every later hit kills it again, and a Gate-class root's own explosion recurses until the game crashes);
/// here a gate dies once.
/// </summary>
public class GateSlotTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static DamagePacket Packet(int mask, int amount)
    {
        var p = new DamagePacket { Mask = (ushort)mask };
        for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) p.Ch[i] = amount;
        return p;
    }

    private static int Node(StaticObject o, string name) => o.Nodes.FindIndex(n => n.Name == name);

    [Fact]
    public void AnAutowerksDoorIsShotOpen()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcaik_s7", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcaik_s7", 4100, 36700);
        int door = Node(o, "CA1_DR76"), shell = Node(o, "CA1_07L6");
        Assert.Equal(400, o.Nodes[door].MaxHp);
        Assert.Contains(o.Boxes, b => b.Node == door);
        // a bullet counts: any packet
        setup.Damage.Apply(o, Packet(1, 150), Vector3.Zero, door);
        setup.Damage.Apply(o, Packet(1, 150), Vector3.Zero, door);
        Assert.Equal(100, o.Nodes[door].Hp);
        Assert.False(o.Nodes[door].Destroyed);
        setup.Damage.Apply(o, Packet(1, 150), Vector3.Zero, door);
        Assert.True(o.Nodes[door].Destroyed);
        Assert.True(o.Destroyed, "a direct child of the root died: the root is flagged");
        var e = Assert.Single(setup.Damage.Events.OfType<StructureDestroyed>());
        Assert.Empty(e.Booms);   // no marker explosion, nothing of its own
        Assert.DoesNotContain(setup.Damage.Events, x => x is Debris);
        Assert.DoesNotContain(o.Boxes, b => b.Node == door);
        Assert.False(o.Nodes[shell].Destroyed);
        Assert.True(o.Solid);
        // dead once: the next round does nothing
        setup.Damage.Events.Clear();
        setup.Damage.Apply(o, Packet(1, 150), Vector3.Zero, door);
        Assert.Empty(setup.Damage.Events);
    }

    [Fact]
    public void ARamOnADoorIsNotQuartered()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcaik_s7", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcaik_s7", 4100, 36700);
        int door = Node(o, "CA1_DR76");
        setup.Damage.StaticSlot(o, door, Packet(8, 399));
        Assert.Equal(1, o.Nodes[door].Hp);
    }

    /// <summary>bcaik_s7's shell, door and interior are all top-level: each dies on its own, the first one's death flagging the root.</summary>
    [Fact]
    public void ThreeShotsThroughBcaikS7AreThreeDeaths()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcaik_s7", 0xbu, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcaik_s7", 4100, 36700);
        foreach (var name in new[] { "CA1_DR76", "CA1_07L6", "CA1_IN76" })
        {
            setup.Damage.Apply(o, Packet(2, 100_000), Vector3.Zero, Node(o, name));
            Assert.True(o.Nodes[Node(o, name)].Destroyed, name);
        }
        Assert.Equal(3, setup.Damage.Events.OfType<StructureDestroyed>().Count());
        // the marker explodes at the first death of a type-2 or 3 node's root test only if that death is the first: the door's was
        Assert.DoesNotContain(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Booms.Contains(-1));
    }

    /// <summary>The Gila truck stop's turning sign FL1_SGN1 (type 10, 50 HP, a box) under its post: shot down alone.</summary>
    [Fact]
    public void ASpinnerIsShotDownAlone()
    {
        if (InstallDir is null || Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } nitro || !Directory.Exists(nitro)) return;
        var install = new GameInstall(nitro);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", "p01.msn")!);
        var at = w.Placements.First(p => p.Object.IsPlayer).Object.Position;
        w.Placements.Add(ClassIdTests.Place(asm, "bflgila1", 2u, at.X + 150, at.Z));
        var setup = MissionSetup.Build(vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
        var o = setup.Statics.Objects.Where(x => x.Name == "bflgila1").MinBy(x => new Vector2(x.Origin.X - at.X - 150, x.Origin.Z - at.Z).Length())!;
        int sign = Node(o, "FL1_SGN1"), post = Node(o, "FL1_PST1");
        Assert.Equal(10u, o.Nodes[sign].Type);
        Assert.Equal(50, o.Nodes[sign].MaxHp);
        setup.Damage.Apply(o, Packet(1, 51), Vector3.Zero, sign);
        Assert.True(o.Nodes[sign].Destroyed);
        Assert.False(o.Nodes[post].Destroyed);
        Assert.False(o.Destroyed, "the sign is no direct child of the root");
    }

    /// <summary>
    /// T07's roadblock (afence5), its gate slid open at the start and then shot: the original swaps the gate node's geometry to
    /// its wreck AF1AGAT5 and leaves the node where it slid, beside the road (measured: the node's translation unchanged by the
    /// death, the wreck drawn there, and a car pushed along the road through where the gate first stood met nothing). Its
    /// collider is the wreck's, where the gate had slid to.
    /// </summary>
    [Fact]
    public void ASlidGateShotDownLeavesItsWreckWhereItSlidTo()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t07.msn");
        var o = setup.Statics.Objects.Single(x => x.Name == "afence5" && x.Centre.X > 1000);
        int gate = Node(o, "AF1_GAT5");
        var g = o.Nodes[gate].Gate!;
        var rest = o.Boxes.Single(b => b.Node == gate).Centre;
        Assert.True(o.TriggerGate());
        for (int i = 0; i < 1000 && (g.Moving || g.X < 1); i++) o.TickGates(0.05f);
        Assert.Equal(1f, g.X);
        setup.Damage.Apply(o, Packet(2, 100_000), Vector3.Zero, gate);
        Assert.True(o.Nodes[gate].Destroyed);
        var wreck = Assert.Single(o.Boxes, b => b.Node == gate);
        Assert.True(Vector3.Distance(wreck.Centre, rest + g.World) < 0.5f, $"the wreck's box at {wreck.Centre}, the gate slid to {rest + g.World}");
        Assert.DoesNotContain(o.Boxes, b => Vector3.Distance(b.Centre, rest) < 1f);
    }

    /// <summary>afence5's gate AF1_GAT5 has its record's 5000 hit points, not the constructor's 400.</summary>
    [Fact]
    public void ARoadblocksGateHasItsRecordsHitPoints()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("afence5", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "afence5", 4100, 36700);
        int gate = Node(o, "AF1_GAT5");
        Assert.Equal(5000, o.Nodes[gate].MaxHp);
        setup.Damage.Apply(o, Packet(2, 4999), Vector3.Zero, gate);
        Assert.False(o.Nodes[gate].Destroyed);
        setup.Damage.Apply(o, Packet(2, 2), Vector3.Zero, gate);
        Assert.True(o.Nodes[gate].Destroyed);
    }
}

/// <summary>
/// A Gate-class root (class id 7) takes its first type-7 node's block at init: that node's hit points and slide, the
/// node stripped of its slot (no damage from rounds or rams). T12's Autowerks compound gate bcaikgat, the only stock
/// class-7 placement, so holds 40000: out of reach in play. Splash goes to the gate slot on the root; its death swaps the
/// whole placement and sets off x1_bldx1 at the root, whose splash in the original comes back into the same slot and
/// recurses until the game crashes (measured: a road sign placed as class 7, 25-point blasts, 400 to 0 by the
/// sixteenth, the seventeenth killed the game). Here it dies once.
/// </summary>
public class GateClassTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void T12sCompoundGateHoldsItsLeafsHitPointsAndTheLeafTakesNoHits()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, new ModelAssembler(vfs), install.Resolve("miss16", "t12.msn")!), new ModelAssembler(vfs));
        var gate = setup.Statics.Objects.Single(o => o.Name == "bcaikgat");
        Assert.Equal(40000, gate.Hp);
        Assert.Equal(40000, gate.MaxHp);
        var leaf = gate.Nodes.Single();
        Assert.True(leaf.Stripped);
        var round = new DamagePacket { Mask = 2 };
        round.Ch[1] = 100_000;
        setup.Damage.Apply(gate, round, Vector3.Zero, 0);
        Assert.False(leaf.Destroyed);
        Assert.Equal(40000, gate.Hp);
        // five HE mortar blasts: 475 off the root's 40000
        for (int i = 0; i < 5; i++) setup.Combat.Explode("x1_gren1", gate.Centre + new Vector3(5, 0, 0), null);
        Assert.Equal(40000 - 5 * 95, gate.Hp);
        Assert.False(gate.Destroyed);
        // the original slides the root itself, by the leaf's offset in the placement's frame: the leaf's own frame is the same
        var place = I76.Formats.Assembly.Affine.FromEngine(setup.PlacementOf[gate].Object.Rotation, Vector3.Zero);
        Assert.True(Vector3.Distance(place.Rotate(leaf.Gate!.Offset), leaf.Gate.World) < 1e-3f);
        // and the script's triggerGate still slides it, 10 m south
        Assert.True(gate.TriggerGate());
        for (int i = 0; i < 400; i++) gate.TickGates(0.05f);
        Assert.InRange(leaf.Gate!.Shift.Z, -10.01f, -9.99f);
    }

    [Fact]
    public void AGateClassSignDiesOnceAtTheSeventeenthBlast()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", 7u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        Assert.Equal(400, o.Hp);
        var at = o.Centre + new Vector3(1.5f, 0, 0);
        for (int i = 0; i < 16; i++) setup.Combat.Explode("x1_carx1", at, null);
        Assert.Equal(0, o.Hp);
        Assert.False(o.Destroyed);
        setup.Combat.Explode("x1_carx1", at, null);
        Assert.True(o.Destroyed);
        Assert.All(o.Nodes, n => Assert.True(n.Destroyed));
        var e = Assert.Single(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Object == o);
        Assert.Equal([-2], e.Booms);
        Assert.DoesNotContain(setup.Damage.Events, x => x is Debris);
        Assert.False(o.Solid);
        setup.Damage.Events.Clear();
        for (int i = 0; i < 5; i++) setup.Combat.Explode("x1_carx1", at, null);
        Assert.DoesNotContain(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Object == o);
    }
}
