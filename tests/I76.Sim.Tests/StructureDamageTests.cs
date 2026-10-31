using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Structures as the game was measured to treat them (measured on A01 in the original game): a round hits
/// the node it touches, a splash only the root's 400, a root emptied by
/// splash takes every node with no marker explosion, and the root goes up at
/// its marker when a direct child of it dies.
/// </summary>
public class StructureDamageTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static MissionSetup A01()
    {
        var install = new GameInstall(InstallDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        return MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "a01.msn")!), asm);
    }

    private static StaticObject Hopper(MissionSetup s) => s.Statics.Objects.Single(o => o.Name == "bcrhopr1");
    private static int Node(StaticObject o, string name) => o.Nodes.FindIndex(n => n.Name == name);

    private static DamagePacket Explosive(int amount)
    {
        var p = new DamagePacket { Mask = 2, Kind = DamageKind.Explosion };
        p.Ch[1] = amount;
        return p;
    }

    /// <summary>One of the player's 50cal rounds, flying along <paramref name="dir"/> to pass through <paramref name="at"/>, stepped until it is spent.</summary>
    private static void Shoot(MissionSetup s, Vector3 at, Vector3 dir)
    {
        var player = s.World.Player!;
        var mg = player.Armory!.Weapons.Single(w => w.Def.Name == "50cal MG");
        // the measuring game ran at difficulty 0, which doubles the player's rounds: 25 × 2, the 50 it logged
        Assert.Equal(25, mg.Damage);
        s.Combat.Difficulty = 0;
        var f = Vector3.Normalize(dir);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f));
        var start = at - f * 10f;
        var r = new Round
        {
            Kind = mg.Def.Kind, Shooter = player, Def = mg.Def, Ord = mg.Def.Ordnance!, Pos = start, PrevPos = start,
            Rot = new Rows(right, Vector3.Cross(f, right), f), LocalVel = new Vector3(0, 0, mg.Def.Speed), Life = 5f, Damage = mg.Damage,
        };
        s.Combat.Rounds.Add(r);
        for (int i = 0; i < 20 && s.Combat.Rounds.Contains(r); i++) s.Combat.Frame(0.036f);
        Assert.DoesNotContain(r, s.Combat.Rounds);
    }

    [Fact]
    public void TheNodesAreTheSgeoRecordsTheEngineMakes()
    {
        if (InstallDir is null) return;
        var s = A01();
        var hopper = Hopper(s);
        Assert.Equal(StaticObject.RootHp, hopper.Hp);
        Assert.Equal(["CR1_LEG1", "CR1_BIN1", "CR1_ZZZ1"], hopper.Nodes.Select(n => n.Name));
        Assert.Equal([(-1, 2u, 100), (0, 0xbu, 100), (-1, 0x37u, 400)], hopper.Nodes.Select(n => (n.Parent, n.Type, n.Hp)));
        // measured: X1_BLDX1 at the marker CR1_ZZZ1, (1547.69, 36.76, 49743.34)
        Assert.True(Vector3.Distance(hopper.Marker!.Value, new Vector3(1547.69f, 36.76f, 49743.34f)) < 0.05f, $"{hopper.Marker}");
        var cactus = s.Statics.Objects.First(o => o.Name == "nsaguar1");
        Assert.Equal(["NS1_TRK1", "NS1_ARL1", "NS1_ARR1"], cactus.Nodes.Select(n => n.Name));
        Assert.All(cactus.Nodes, n => Assert.Equal(5, n.Hp));
        Assert.Equal([-1, 0, 0], cactus.Nodes.Select(n => n.Parent));
        Assert.Null(cactus.Explosion);
    }

    /// <summary>
    /// The A01 hopper's leg shot through, the player's car 30 m east of it
    /// (as measured): one X1_BLDX1 at the marker, R 65, 15 to everything in
    /// reach, no falloff. The car's far side from the blast takes it (zone 2,
    /// 800 → 785); each of the six cacti in reach goes 400 → 385 on its root
    /// and keeps its 5-HP nodes.
    /// </summary>
    [Fact]
    public void TheHopperGoesUpOnceAndSplashesTheCarAndTheCactiRoundIt()
    {
        if (InstallDir is null) return;
        var s = A01();
        var hopper = Hopper(s);
        var player = s.World.Player!;
        player.Pos = new Double3(1580.0, 28.3, 49750.0);
        var d = player.Health!;
        int[] armour0 = [.. d.Armour];
        var cacti = s.Statics.Objects.Where(o => o.Name == "nsaguar1").ToList();
        s.Combat.Events.Clear(); s.Damage.Events.Clear();

        s.Damage.Apply(hopper, Explosive(100000), Vector3.Zero, Node(hopper, "CR1_LEG1"));

        var boom = Assert.Single(s.Combat.Events.OfType<Explosion>());
        Assert.Equal("xbldx1.xdf", boom.Effect.File, ignoreCase: true);
        Assert.Equal((65f, 15f), (boom.Effect.SplashRadius, boom.Effect.SplashDamage));
        Assert.Equal(hopper.Marker, boom.At);
        Assert.True(hopper.Destroyed);
        Assert.True(hopper.Nodes[Node(hopper, "CR1_BIN1")].Destroyed);
        Assert.Equal(400, hopper.Hp);       // the root test marks the root; its pool is not touched
        Assert.Equal([-1], s.Damage.Events.OfType<StructureDestroyed>().Single(e => e.Object == hopper).Booms);
        Assert.Equal(16, s.Damage.Events.OfType<Debris>().Count());   // the bin's 8, then the leg's

        Assert.Equal([0, 0, 15, 0], armour0.Zip(d.Armour, (a, b) => a - b));

        // the one at 66.65 m is in by its collider's 3D radius, 1.70 (65 + 1.70 = 66.70)
        var expect = new[] { (1530f, 49710f), (1530f, 49680f), (1560f, 49700f), (1580f, 49770f), (1490f, 49770f), (1575f, 49785f) };
        var hit = cacti.Where(c => c.Hp != StaticObject.RootHp).ToList();
        Assert.All(hit, c => Assert.Equal(385, c.Hp));
        Assert.All(cacti, c => Assert.All(c.Nodes, n => Assert.Equal((5, false), (n.Hp, n.Destroyed))));
        Assert.All(cacti, c => Assert.False(c.Destroyed));
        var pos = hit.Select(c => s.PlacementOf[c].Object.Position).Select(p => (p.X, p.Z)).OrderBy(p => p).ToList();
        Assert.Equal(expect.OrderBy(p => p).ToList(), pos);
    }

    /// <summary>
    /// The far side from the blast, in world axes, read by the car as its own
    /// (from the original): the measured n̂ and cLocal for
    /// the car west of the blast, and the one south of it.
    /// </summary>
    [Fact]
    public void ASplashHitsTheFarSideInWorldAxes()
    {
        var blast = new Vector3(1547.69f, 36.76f, 49743.34f);
        var c = Combat.SplashPoint(new Vector3(1580.1f, 29.1f, 49750.0f), 2.75f, blast);
        Assert.True(Vector3.Distance(c, new Vector3(2.628f, -0.620f, 0.539f)) < 0.01f, $"{c}");
        Assert.Equal(2, VehicleDamage.Zones(c).Zone);
        var n = Vector3.Normalize(new Vector3(-0.097f, 0.309f, 0.946f));
        c = Combat.SplashPoint(blast - n * 25f, 2.75f, blast);
        Assert.True(Vector3.Distance(c, new Vector3(0.268f, -0.852f, -2.604f)) < 0.01f, $"{c}");
        Assert.Equal(3, VehicleDamage.Zones(c).Zone);
        Assert.Equal(new Vector3(0, 0, -2f), Combat.SplashPoint(blast, 2f, blast));
    }

    /// <summary>A round on a saguaro's arm: the arm (5 HP) dies, 8 chunks from its base; the root's 400, its trunk and the other arm stay.</summary>
    [Fact]
    public void ARoundKillsTheCactusArmItHitsAndNotTheCactus()
    {
        if (InstallDir is null) return;
        var s = A01();
        var cactus = s.Statics.Objects.First(o => o.Name == "nsaguar1");
        int arm = Node(cactus, "NS1_ARL1"), trunk = Node(cactus, "NS1_TRK1");
        var armBox = cactus.Boxes.Single(b => b.Node == arm);
        var trunkBox = cactus.Boxes.Single(b => b.Node == trunk);
        var away = armBox.Centre - trunkBox.Centre; away.Y = 0;
        s.Damage.Events.Clear();
        Shoot(s, armBox.Centre, -away);
        Assert.True(cactus.Nodes[arm].Destroyed);
        Assert.False(cactus.Nodes[trunk].Destroyed);
        Assert.False(cactus.Nodes[Node(cactus, "NS1_ARR1")].Destroyed);
        Assert.Equal((400, false), (cactus.Hp, cactus.Destroyed));
        var debris = s.Damage.Events.OfType<Debris>().ToList();
        Assert.Equal(8, debris.Count);
        Assert.All(debris, x => Assert.Equal(cactus.Nodes[arm].Base, x.At));
        Assert.DoesNotContain(cactus.Boxes, b => b.Node == arm);
        Assert.True(cactus.Solid);
    }

    /// <summary>
    /// The hopper's bin (100 HP) takes 50, then 50 (HP 0, still up: the test
    /// is HP &lt; hit), then dies on the third round: 8 chunks from its base
    /// (measured (1547.7, 34.3, 49743.3)), no explosion, the root and the
    /// leg untouched (the bin is not a child of the root).
    /// </summary>
    [Fact]
    public void TheHopperBinTakesThreeRoundsAndLeavesTheHopperStanding()
    {
        if (InstallDir is null) return;
        var s = A01();
        var hopper = Hopper(s);
        var bin = hopper.Nodes[Node(hopper, "CR1_BIN1")];
        var mid = hopper.Polys.Where(p => p.Node == Node(hopper, "CR1_BIN1")).SelectMany(p => p.V).Aggregate(Vector3.Zero, (a, v) => a + v)
            / hopper.Polys.Where(p => p.Node == Node(hopper, "CR1_BIN1")).Sum(p => p.V.Length);
        var hps = new List<(int, bool)>();
        s.Damage.Events.Clear();
        for (int i = 0; i < 3; i++)
        {
            Shoot(s, mid, new Vector3(-1, 0, 0));
            hps.Add((bin.Hp, bin.Destroyed));
        }
        Assert.Equal([(50, false), (0, false), (0, true)], hps);
        Assert.Equal((400, false), (hopper.Hp, hopper.Destroyed));
        Assert.False(hopper.Nodes[Node(hopper, "CR1_LEG1")].Destroyed);
        Assert.Empty(s.Damage.Events.OfType<StructureDestroyed>().SelectMany(e => e.Booms));
        var debris = s.Damage.Events.OfType<Debris>().ToList();
        Assert.Equal(8, debris.Count);
        Assert.All(debris, x => Assert.True(Vector3.Distance(x.At, new Vector3(1547.7f, 34.3f, 49743.3f)) < 0.05f, $"{x.At}"));
    }

    /// <summary>
    /// Splash alone, 15 a time: the hopper's root goes on the 27th (400 →
    /// 10, then 10 &lt; 15) and takes every node, 16 chunks, no X1_BLDX1;
    /// a saguaro the same with its three nodes' 24 chunks.
    /// </summary>
    [Fact]
    public void ARootEmptiedBySplashTakesEveryNodeWithoutTheMarkerExplosion()
    {
        if (InstallDir is null) return;
        var s = A01();
        foreach (var (o, chunks) in new[] { (Hopper(s), 16), (s.Statics.Objects.First(o => o.Name == "nsaguar1"), 24) })
        {
            s.Combat.Events.Clear(); s.Damage.Events.Clear();
            var at = o.Centre + new Vector3(1, 0, 0);
            for (int i = 0; i < 26; i++) s.Combat.Splash(at, 1f, 15f, null);
            Assert.Equal((10, false), (o.Hp, o.Destroyed));
            Assert.All(o.Nodes, n => Assert.False(n.Destroyed));
            s.Combat.Splash(at, 1f, 15f, null);
            Assert.Equal((0, true), (o.Hp, o.Destroyed));
            Assert.All(o.Nodes, n => Assert.True(n.Destroyed));
            Assert.All(o.Nodes, n => Assert.Equal(n.MaxHp, n.Hp));      // marked, not emptied
            Assert.Empty(s.Combat.Events.OfType<Explosion>());
            Assert.Equal(chunks, s.Damage.Events.OfType<Debris>().Count());
        }
    }

    /// <summary>The destroyed state goes on the wire as bits (root, then the nodes) and comes back the same, quietly.</summary>
    [Fact]
    public void TheDestroyedStateRoundTripsThroughItsMask()
    {
        if (InstallDir is null) return;
        var a = A01(); var b = A01();
        var ha = Hopper(a); var hb = Hopper(b);
        a.Damage.Apply(ha, Explosive(100000), Vector3.Zero, Node(ha, "CR1_BIN1"));
        Assert.Equal([(byte)0b100], ha.DestroyedMask());
        Assert.True(hb.ApplyDestroyedMask(ha.DestroyedMask()));
        Assert.Equal(ha.Nodes.Select(n => n.Destroyed), hb.Nodes.Select(n => n.Destroyed));
        Assert.Equal(ha.Polys.Count, hb.Polys.Count);
        Assert.False(hb.ApplyDestroyedMask(ha.DestroyedMask()));
        a.Damage.Apply(ha, Explosive(100000), Vector3.Zero, Node(ha, "CR1_LEG1"));
        Assert.Equal([(byte)0b111], ha.DestroyedMask());
        Assert.True(hb.ApplyDestroyedMask(ha.DestroyedMask()));
        Assert.True(hb.Destroyed);
        Assert.Equal(ha.Surfaces.Count, hb.Surfaces.Count);
    }
}
