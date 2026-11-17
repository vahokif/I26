using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The explosions and sounds that go with a structure's death, by the original's rules. Only a structure root (class 2,
/// 0xb or 0xc) reads its .sdf's explosion and sound: the root test of a Struct2 root never explodes, and a Sign- or
/// Gate-class root's explosion fields are never set, so a building given the Sign class id loses a top-level node with no
/// marker explosion. A node's own explosion (flag 0x400000 or 0x800000) plays vexplode.wav: the per-node sound is the
/// table's "null" for every stock node; only the marker's plays the .sdf's sound.
/// </summary>
public class StructureBoomTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static void Kill(Missions.MissionSetup setup, StaticObject o, int node)
    {
        var p = new DamagePacket { Mask = 2 };
        p.Ch[1] = 1_000_000;
        setup.Damage.Apply(o, p, Vector3.Zero, node);
    }

    [Theory]
    [InlineData(2u, true)]
    [InlineData(0xbu, true)]
    [InlineData(0xcu, true)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    public void TheMarkerExplodesOnlyForAStructureRoot(uint type, bool explodes)
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcbank1", type, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcbank1", 4100, 36700);
        Assert.NotNull(o.Marker);
        Kill(setup, o, o.Nodes.FindIndex(n => n.Name == "CB1_BLD1"));
        Assert.True(o.Destroyed, "the root test flags the root");
        var e = Assert.Single(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Object == o);
        Assert.Equal(explodes, e.Booms.Contains(-1));
    }

    /// <summary>The FSM's destroy on a building given the Sign class id throws it and sets off no marker explosion.</summary>
    [Fact]
    public void AScriptsDestroyOnASignClassBuildingSetsOffNoMarker()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcbank1", 4u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcbank1", 4100, 36700);
        setup.Damage.DestroyStructure(o);
        Assert.DoesNotContain(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Object == o && x.Booms.Contains(-1));
    }

    /// <summary>
    /// Autowerks building No. 600 (bcaik6): its shell CA1_06R1 carries 0x400000, its .sdf names xbuild.wav. Shot down, the
    /// shell's own explosion goes off at its base with vexplode.wav, and the marker's (the root test) with xbuild.wav.
    /// </summary>
    [Fact]
    public void ANodesOwnExplosionPlaysVexplodeTheMarkersTheSdfsSound()
    {
        if (InstallDir is null) return;
        var (setup, _) = ClassIdTests.Build("t01.msn", ("bcaik6", 2u, 4100, 36700, 0));
        var o = ClassIdTests.At(setup, "bcaik6", 4100, 36700);
        int shell = o.Nodes.FindIndex(n => n.Name == "CA1_06R1");
        Assert.Equal("x1_bldx1", o.Nodes[shell].Explosion);
        Kill(setup, o, shell);
        var e = Assert.Single(setup.Damage.Events.OfType<StructureDestroyed>(), x => x.Object == o);
        Assert.Contains(shell, e.Booms);
        Assert.Contains(-1, e.Booms);
        Assert.Equal("vexplode.wav", o.BoomSound(shell));
        Assert.Equal("xbuild.wav", o.BoomSound(-1), ignoreCase: true);
    }
}
