using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Weapons;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// Intersection pieces placed as structures (a community mission maker's sheet of every piece under every class id, checked
/// against the original game): what rounds, drops and mines do on them.
/// - A round's impact (effect, sound, the effect's splash) is picked by the struck node's own type: 1/4/8/9, 2/0xc, 3/0xb.
///   Any other type has none. The Nitro Pack's branch, start and finish pieces (iga2x_2, ila2x_2, ima2x_x: node type 0x50;
///   ijw29t1: 0x53) eat guns, rockets and mortars without a trace, where the other pieces (type-2 nodes) show the hit.
/// - The 10 m pieces and the runway stand their plane 1 mm over their origin: placed as a structure on flat ground, a mine
///   lying under it finds the piece's box with its upward probe and goes off at once (the structure's pair: xmine1, no
///   sound); a 0x50 piece takes it with no effect at all; a piece whose plane lies on its origin leaves it alone.
/// - The drop's probe looks for cars before structures: oil under such a piece still oils a car.
/// </summary>
public class RoadPieceRoundTests
{
    private static string? NitroDir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>The pieces and where they go from the player's start in P12: on 25 m squares of level ground.</summary>
    private static readonly (string Name, float Dx, float Dz)[] Pieces =
        [("idisect1", 300, -200), ("iga21_1", 300, -160), ("iga21_2", 300, -120), ("ima21_1", 0, -160), ("ijw29t1", 40, -120)];

    /// <summary>P12 with the pieces above placed as Struct1.</summary>
    private static (MissionSetup Setup, AssetVfs Vfs) Map()
    {
        var install = new GameInstall(NitroDir!);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("p12")!);
        var start = w.Placements.First(p => p.Object.IsPlayer).Object.Position;
        foreach (var (name, dx, dz) in Pieces) w.Placements.Add(ClassIdTests.Place(asm, name, 2, start.X + dx, start.Z + dz));
        return (MissionSetup.Build(vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1)), vfs);
    }

    /// <summary>A point on the piece, 1.3 m and 0.7 m in from its origin corner, where the ground is level with the origin.</summary>
    private static Vector3 OnIt(MissionSetup s, StaticObject o)
    {
        var at = o.Origin + new Vector3(1.3f, 0, 0.7f);
        Assert.Equal(o.Origin.Y, s.Ground.BareHeight(at.X, at.Z), 4);
        return at;
    }

    private static StaticObject Piece(MissionSetup s, string name) => s.Statics.Objects.Single(o => o.Name == name);

    private static GizmoDef Gizmo(AssetVfs vfs, string file) => GizmoDef.Read(file, vfs.Read(file))!;

    /// <summary>A round of the gizmo's 3 m over the point, flying straight down at 40 m/s; stepped until spent (at most 4 frames).</summary>
    private static Round Fire(MissionSetup s, GizmoDef g, Vector3 at)
    {
        var r = new Round
        {
            Kind = g.Kind, Shooter = s.World.Player!, Def = g, Ord = g.Ordnance!, Pos = at + new Vector3(0, 3, 0),
            Rot = new Rows(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY), LocalVel = new Vector3(0, 0, 40),
            Life = 5, Age = 3, Travelled = 100, Damage = 10,
        };
        s.Combat.Rounds.Clear(); s.Combat.Events.Clear();
        s.Combat.Rounds.Add(r);
        for (int k = 0; k < 4 && s.Combat.Rounds.Contains(r); k++) s.Combat.Frame(0.05f);
        return r;
    }

    /// <summary>A drop or a mine at rest on the ground at the point, or <paramref name="lift"/> over it (the dropper long past its 2 s), stepped one frame.</summary>
    private static Round Drop(MissionSetup s, GizmoDef g, Vector3 at, float lift = 0)
    {
        at.Y = s.Ground.Query(at.X, at.Y + 5, at.Z).Height + (g.Kind == 0xc ? 0.0001f : 0f) + lift;
        var r = new Round
        {
            Kind = g.Kind, Shooter = s.World.Player!, Def = g, Ord = g.Ordnance!, Pos = at, Rot = Rows.Identity,
            Life = 20, Age = 3, Launched = true, Settled = true, Damage = (int)g.Damage,
        };
        s.Combat.Rounds.Clear(); s.Combat.Events.Clear();
        s.Combat.Rounds.Add(r);
        s.Combat.Frame(0.05f);
        return r;
    }

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(4u, 1)]
    [InlineData(8u, 1)]
    [InlineData(9u, 1)]
    [InlineData(2u, 2)]
    [InlineData(0xcu, 2)]
    [InlineData(3u, 3)]
    [InlineData(0xbu, 3)]
    [InlineData(0u, -1)]
    [InlineData(7u, -1)]
    [InlineData(0xau, -1)]
    [InlineData(0xdu, -1)]
    [InlineData(0x50u, -1)]
    [InlineData(0x53u, -1)]
    [InlineData(0x5cu, -1)]
    public void TheImpactPairGoesByTheStruckType(uint type, int pair) => Assert.Equal(pair, Combat.ImpactClassOf(type));

    [Theory]
    [InlineData("gmlight.gdf")]
    [InlineData("gdumb.gdf")]
    [InlineData("ggrenade.gdf")]
    public void ARoundIntoAPieceOfType0x50LeavesNoTrace(string gdf)
    {
        if (NitroDir is null) return;
        var (s, vfs) = Map();
        var g = Gizmo(vfs, gdf);
        foreach (var name in new[] { "iga21_2", "ima21_1", "ijw29t1" })
        {
            var o = Piece(s, name);
            Assert.All(o.Nodes, n => Assert.True(n.Type is 0x50 or 0x53, $"{name} {n.Name} {n.Type:x}"));
            var r = Fire(s, g, o.Centre);
            Assert.DoesNotContain(r, s.Combat.Rounds);   // spent where it met the piece
            Assert.True(r.Pos.Y < o.Centre.Y + 0.1f, $"{name}: met at {r.Pos.Y:0.00}, piece at {o.Centre.Y:0.00}");
            Assert.DoesNotContain(s.Combat.Events, e => e is Impact or Explosion);
        }
        // the same branch piece with its type-2 node: the structure's pair
        var plain = Piece(s, "iga21_1");
        Fire(s, g, plain.Centre);
        var hit = Assert.Single(s.Combat.Events.OfType<Impact>());
        Assert.Same(plain, hit.Victim);
        Assert.Equal(g.Ordnance!.ImpactXdf[2], hit.Effect);
    }

    [Fact]
    public void AMineUnderATenMetrePiecesPlaneGoesOffAtOnce()
    {
        if (NitroDir is null) return;
        var (s, vfs) = Map();
        var mine = Gizmo(vfs, "glandmin.gdf");
        var blox = Gizmo(vfs, "gblox.gdf");
        // idisect1's plane stands 1 mm over its origin, on flat ground here: the mine's probe enters its box from below
        var ten = Piece(s, "idisect1");
        var at = OnIt(s, ten);
        Assert.Equal(ten.Origin.Y + 0.001f, ten.Boxes[0].Origin.Y, 5);
        var r = Drop(s, mine, at);
        Assert.DoesNotContain(r, s.Combat.Rounds);
        var hit = Assert.Single(s.Combat.Events.OfType<Impact>());
        Assert.Same(ten, hit.Victim);
        Assert.Equal(("xmine1.xdf", "null"), (hit.Effect.ToLowerInvariant(), hit.Sound.ToLowerInvariant()));   // seen, not heard
        Assert.True(ten.Nodes[0].Hp < ten.Nodes[0].MaxHp);   // the mine's packet goes to the piece
        Assert.DoesNotContain(Drop(s, blox, at), s.Combat.Rounds);
        // a 10 m start line, its node type 0x50: used up, nothing seen or heard
        var line = Piece(s, "ima21_1");
        Assert.DoesNotContain(Drop(s, mine, OnIt(s, line)), s.Combat.Rounds);
        Assert.Empty(s.Combat.Events);
        // a mine lying on the plane, not under it, is left alone (whether a plane on its origin, as the branch pieces', is over
        // or under the ground is the rounding of the last bit, here as in the original)
        Assert.Contains(Drop(s, mine, at, 0.002f), s.Combat.Rounds);
        Assert.Empty(s.Combat.Events);
    }

    [Fact]
    public void ACarOverASlickUnderAPieceIsOiledAndAPieceOfType0x50IsSilent()
    {
        if (NitroDir is null) return;
        var (s, vfs) = Map();
        var oil = Gizmo(vfs, "goilslck.gdf");
        var ten = Piece(s, "idisect1");
        var at = OnIt(s, ten);
        // alone, the slick's probe meets the piece every frame: its pair is silent
        var r = Drop(s, oil, at);
        Assert.Contains(r, s.Combat.Rounds);
        Assert.All(s.Combat.Events.OfType<Impact>(), i => Assert.Equal("null", i.Sound));
        // a car over it is found first, though the piece's box is nearer
        var car = s.World.Player!;
        car.Pos = new Double3(r.Pos.X, r.Pos.Y, r.Pos.Z);
        car.Rot = Rows.Identity;
        car.Flags &= ~(I76.Sim.Vehicles.VehicleFlags)0x80000400u;
        s.Combat.Events.Clear();
        s.Combat.Frame(0.05f);
        Assert.Equal(0x80000400u, (uint)car.Flags & 0x80000400u);
        Assert.Contains(s.Combat.Events.OfType<Impact>(), i => i.Victim == car && i.Sound.Equals("vcdoil.wav", StringComparison.OrdinalIgnoreCase));
        // under a start line (node type 0x50) with no car: no impact at all (it played vcdoil.wav every frame until the slick dried up)
        car.Pos = new Double3(0, 0, 0);
        var line = Piece(s, "ima21_1");
        var la = OnIt(s, line);
        for (int i = 0; i < 10; i++) { Assert.Contains(Drop(s, oil, la), s.Combat.Rounds); Assert.Empty(s.Combat.Events); }
    }

    [Fact]
    public void AMineDroppedOnABridgeRestsOnItsDeck()
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("p12")!);
        var start = w.Placements.First(p => p.Object.IsPlayer).Object.Position;
        w.Placements.Add(ClassIdTests.Place(asm, "bebridg1", 0xc, start.X + 80, start.Z));
        var s = MissionSetup.Build(vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
        var bridge = s.Statics.Objects.Single(o => o.Name == "bebridg1");
        var deck = bridge.Deck!;
        var top = deck.Origin + deck.Rot.ToWorld(new Vector3((deck.Min.X + deck.Max.X) / 2, deck.Max.Y, (deck.Min.Z + deck.Max.Z) / 2));
        float h = s.Ground.Query(top.X, top.Y + 0.5f, top.Z).Height;
        float terrain = s.Ground.BareHeight(top.X, top.Z);
        Assert.True(h > terrain + 1, $"deck {h:0.00} over terrain {terrain:0.00}");
        var g = Gizmo(vfs, "glandmin.gdf");
        var r = new Round
        {
            Kind = g.Kind, Shooter = s.World.Player!, Def = g, Ord = g.Ordnance!, Pos = new Vector3(top.X, h + 1.5f, top.Z),
            Rot = Rows.Identity, LocalVel = new Vector3(0, 0, 2), Life = 1000, Age = 3, Damage = (int)g.Damage,
        };
        s.World.Player!.Pos = new Double3(0, 0, 0);
        s.Combat.Rounds.Add(r);
        for (int i = 0; i < 100 && !r.Settled; i++) s.Combat.Frame(0.05f);
        Assert.True(r.Settled);
        Assert.Contains(r, s.Combat.Rounds);
        Assert.Equal(s.Ground.Query(r.Pos.X, r.Pos.Y + 0.5f, r.Pos.Z).Height, r.Pos.Y, 3);
        Assert.True(r.Pos.Y > terrain + 1, $"on the deck: {r.Pos.Y:0.00}, terrain {terrain:0.00}");
    }
}
