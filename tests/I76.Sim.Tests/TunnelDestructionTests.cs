using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The Nitro Pack's tunnels (a community mission maker's report, checked against the original's rules). The short tunnel
/// betunnl1 is all top-level nodes: a mountain, its approaches and the inner tunnel ET1_TUN1, whose type (0x5c) takes the
/// constructor's 400 hit points, not the record's 500000. Shooting the inner tunnel down first is the death of a direct
/// child of the root, which flags the root: the root then refuses every splash, so mortar rounds leave the mountain
/// standing and solid for good. With the root's 400 emptied by splash first, the whole tunnel goes. The medium and long
/// tunnels given the Intersection class id have no collider at all.
/// </summary>
public class TunnelDestructionTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    internal static MissionSetup Build(string mission, params (string Name, uint Type, Vector2 Offset)[] extra)
    {
        var install = new GameInstall(NitroDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.Resolve("miss16", mission)!);
        var start = w.Placements.First(p => p.Object.IsPlayer).Object.Position;
        foreach (var (name, type, off) in extra) w.Placements.Add(ClassIdTests.Place(asm, name, type, start.X + off.X, start.Z + off.Y));
        return MissionSetup.Build(vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
    }

    private static void Blast(MissionSetup setup, StaticObject o, int count)
    {
        for (int i = 0; i < count; i++) setup.Combat.Explode("x1_gren1", o.Centre + new Vector3(10, 0, 0), null);
    }

    private static void Shoot(MissionSetup setup, StaticObject o, int node)
    {
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 1000;
        setup.Damage.Apply(o, p, Vector3.Zero, node);
    }

    [Fact]
    public void TheInnerTunnelHasTheConstructorsHitPoints()
    {
        if (NitroDir is null) return;
        var setup = Build("p18.msn", ("betunnl1", 2u, new Vector2(150, 0)));
        var o = setup.Statics.Objects.Where(x => x.Name == "betunnl1").MaxBy(x => x.Centre.X)!;
        var tun = o.Nodes.Single(n => n.Name == "ET1_TUN1");
        Assert.Equal(0x5cu, tun.Type);
        Assert.Equal(400, tun.MaxHp);
        Assert.All(o.Nodes.Where(n => n.Type == 2), n => Assert.Equal(500000, n.MaxHp));
    }

    [Fact]
    public void ShootingTheInnerTunnelFirstLeavesTheMountainStandingThroughMortarFire()
    {
        if (NitroDir is null) return;
        var setup = Build("p18.msn", ("betunnl1", 2u, new Vector2(150, 0)));
        var o = setup.Statics.Objects.Where(x => x.Name == "betunnl1").MaxBy(x => x.Centre.X)!;
        int tun = o.Nodes.FindIndex(n => n.Name == "ET1_TUN1"), mtn = o.Nodes.FindIndex(n => n.Name == "ET1_MTN1");
        Shoot(setup, o, tun);
        Assert.True(o.Nodes[tun].Destroyed);
        Assert.True(o.Destroyed, "the inner tunnel is a direct child of the root: its death flags the root");
        Assert.Equal(400, o.Hp);
        Blast(setup, o, 10);
        Assert.Equal(400, o.Hp);
        Assert.False(o.Nodes[mtn].Destroyed);
        Assert.True(o.Solid);
    }

    [Fact]
    public void MortarFireOnAnIntactTunnelTakesItAll()
    {
        if (NitroDir is null) return;
        var setup = Build("p18.msn", ("betunnl1", 2u, new Vector2(150, 0)));
        var o = setup.Statics.Objects.Where(x => x.Name == "betunnl1").MaxBy(x => x.Centre.X)!;
        Blast(setup, o, 4);
        Assert.False(o.Destroyed);
        Blast(setup, o, 1);
        Assert.True(o.Destroyed);
        Assert.All(o.Nodes, n => Assert.True(n.Destroyed));
        Assert.False(o.Solid);
    }

    [Theory]
    [InlineData("betunnl2")]
    [InlineData("betunnl3")]
    public void ATunnelPlacedAsAnIntersectionHasNoCollider(string name)
    {
        if (NitroDir is null) return;
        int before = Build("p18.msn").Statics.Objects.Count(x => x.Name == name);
        var setup = Build("p18.msn", (name, 0x50u, new Vector2(150, 0)));
        Assert.Equal(before, setup.Statics.Objects.Count(x => x.Name == name));
    }
}

/// <summary>
/// The sign slot's throw is not the destroyed flag (measured: a thrown sign's flags unchanged before, after and 28 frames
/// on). It takes the whole placement off the collider lists whatever the root's flag, so a Sign-class root already flagged
/// by its root test (a type-2 or tunnel node at its top died first) is still thrown by the next explosion; a script's isDead
/// stays false after a throw, its building tests no longer see it, and the FSM's destroy on a Sign-class placement throws it.
/// </summary>
public class SignThrowTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>M10's billboard (placed as a sign, type-2 posts): a post shot down flags the root; the next blast still throws the board.</summary>
    [Fact]
    public void M10sBillboardIsThrownAfterAPostIsShotDown()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("m10.msn");
        var board = ClassIdTests.At(setup, "aobilbd2", 2025, 47980);
        int post = board.Nodes.FindIndex(n => n.Name == "AO1_PS12");
        var round = new DamagePacket { Mask = 2 };
        round.Ch[1] = 60;
        setup.Damage.Apply(board, round, Vector3.Zero, post);
        Assert.True(board.Nodes[post].Destroyed);
        Assert.True(board.Destroyed, "a direct child died: the root is flagged");
        Assert.False(board.Thrown);
        Assert.True(board.Solid);
        setup.Combat.Explode("x1_gren1", board.Centre + new Vector3(8, 0, 0), null);
        Assert.True(board.Thrown);
        Assert.False(board.Solid);
        Assert.Contains(setup.Damage.Events, e => e is I76.Sim.Damage.SignKnocked k && k.Object == board);
    }

    [Fact]
    public void AThrownSignIsNotDeadToTheScriptAndNotABuilding()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("sdxin_1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "sdxin_1", 4100, 36700);
        var e = setup.Entities.Single(x => x.Static == o);
        setup.Combat.Explode("x1_gren1", o.Centre + new Vector3(10, 0, 0), null);
        Assert.True(o.Thrown);
        Assert.False(o.Destroyed);
        Assert.False(e.Dead);
        Assert.All(o.Nodes, n => Assert.False(n.Destroyed));
        var host = new ScriptHost(setup.World, setup.Entities, (Objectives)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);
        Assert.DoesNotContain(e, host.Structures);
    }

    [Fact]
    public void TheScriptsDestroyThrowsASignClassPlacement()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcbank1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcbank1", 4100, 36700);
        setup.Damage.DestroyStructure(o);
        Assert.True(o.Thrown);
        Assert.False(o.Destroyed);
        Assert.Contains(setup.Damage.Events, x => x is I76.Sim.Damage.SignKnocked k && k.Object == o);
    }

    /// <summary>The short tunnel given the Sign class id: its inner tunnel shot down first, mortar fire still blows the whole tunnel away.</summary>
    [Fact]
    public void TheShortTunnelPlacedAsASignIsBlownAwayAfterItsInnerTunnelIsShot()
    {
        if (NitroDir is null) return;
        var setup = TunnelDestructionTests.Build("p18.msn", ("betunnl1", 4u, new Vector2(150, 0)));
        var o = setup.Statics.Objects.Where(x => x.Name == "betunnl1").MaxBy(x => x.Centre.X)!;
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 1000;
        setup.Damage.Apply(o, p, Vector3.Zero, o.Nodes.FindIndex(n => n.Name == "ET1_TUN1"));
        Assert.True(o.Destroyed);
        setup.Combat.Explode("x1_gren1", o.Centre + new Vector3(10, 0, 0), null);
        Assert.True(o.Thrown);
        Assert.False(o.Solid);
    }

    [Fact]
    public void TheThrownStateRidesTheDestroyedMask()
    {
        var src = new StaticObject { Name = "s", Type = 4 };
        var dst = new StaticObject { Name = "s", Type = 4 };
        for (int i = 0; i < 7; i++) { src.Nodes.Add(new StructureNode { Name = $"n{i}", Type = 4 }); dst.Nodes.Add(new StructureNode { Name = $"n{i}", Type = 4 }); }
        Assert.True(src.Knock());
        Assert.False(src.Knock());
        Assert.True(dst.ApplyDestroyedMask(src.DestroyedMask()));
        Assert.True(dst.Thrown);
        Assert.False(dst.Destroyed);
        Assert.All(dst.Nodes, n => Assert.False(n.Destroyed));
    }
}
