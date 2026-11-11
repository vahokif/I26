using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Tests;
using I76.Formats.Vfs;
using I76.Game.World;
using I76.Sim.Missions;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// A model placed with a road-piece type (ODEF 0x50-0x5f, the intersections) in one placement and as a ramp or a bridge in another
/// (a custom map's doing): each placement goes by its own type. The scenery grouped placements by model and took the first one's
/// type for all, so a ramp placed after a piece of its model was hidden with the painted pieces while its collider stayed, until a
/// mortar wrecked it and its remains showed. A road-piece placement of a model that is not a flat piece shows its last top-level
/// node, as the original's loader keeps it, and has no collider (the original makes no world object of it).
/// </summary>
public class RoadPieceTypeTests(ITestOutputHelper log)
{
    private static readonly string[] Placed = ["aaramp1:50", "aaramp1:b", "bebridg2:50", "bebridg2:c"];

    /// <summary>T01 with the placements above (the piece-typed ones first in the file), 150 to 450 m from the player's start.</summary>
    private static (MissionWorld World, ModelAssembler Asm, AssetVfs Vfs, Vector3[] At) Map()
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var doc = MapDocument.Load(TestInstall.Install.FindMission("t01")!);
        var start = doc.Objects.First(o => o.IsPlayer).Position;
        var at = new Vector3[Placed.Length];
        for (int i = 0; i < Placed.Length; i++)
        {
            var q = Placed[i].Split(':');
            at[i] = start + new Vector3(150 + 100 * i, 0, 0);
            doc.Objects.Insert(i, MapObject.Create(q[0], Convert.ToUInt32(q[1], 16), at[i]));
        }
        var dir = Directory.CreateTempSubdirectory("i26-roadpiece-");
        var path = Path.Combine(dir.FullName, "rpt01.msn");
        File.WriteAllBytes(path, doc.ToBytes());
        return (new MissionWorld(vfs, asm, path), asm, vfs, at);
    }

    private static Placement At(MissionWorld w, string name, uint type) =>
        w.Placements.Single(p => p.Object.Name == name && p.Object.ObjectType == type);

    [InstallFact]
    public void ARampSharingAPiecesModelIsDrawnAndSolid()
    {
        var (w, asm, vfs, at) = Map();
        var piece = At(w, "aaramp1", 0x50);
        var ramp = At(w, "aaramp1", 0xb);
        Assert.True(w.Placements.IndexOf(piece) < w.Placements.IndexOf(ramp), "the piece comes first, as in the report");
        // the ramp is grouped with the ordinary scenery, not with the pieces (whose painted ones are not drawn)
        Assert.NotEqual(SceneryGroup.Key(piece), SceneryGroup.Key(ramp));
        Assert.DoesNotContain(SceneryGroup.Piece, SceneryGroup.Key(ramp));
        Assert.DoesNotContain(SceneryGroup.Piece, SceneryGroup.Key(At(w, "bebridg2", 0xc)));

        var setup = MissionSetup.Build(vfs, w, asm, rand: new I76.Sim.Rng.CrtRand(1));
        var near = (Vector3 p) => setup.Statics.Objects.Where(o => new Vector2(o.Centre.X - p.X, o.Centre.Z - p.Z).Length() < 60).ToList();
        foreach (var o in setup.Statics.Objects.Where(o => o.Name is "aaramp1" or "bebridg2"))
            log.WriteLine($"{o.Name} type {o.Type:x} centre {o.Centre}");
        Assert.Contains(near(at[1]), o => o.Name == "aaramp1" && o.Type == 0xb);
        Assert.Contains(near(at[3]), o => o.Name == "bebridg2" && o.Type == 0xc);
        // the road-piece placements are no objects of the world: nothing to hit or to wreck
        Assert.DoesNotContain(setup.Statics.Objects, o => RoadPieceObjects.IsRoadPiece(o.Type));
        Assert.DoesNotContain(near(at[0]), o => o.Name == "aaramp1");
        Assert.DoesNotContain(near(at[2]), o => o.Name == "bebridg2");
    }

    [InstallFact]
    public void APieceTypedRampShowsItsBodyAndAPieceTypedBridgeItsMarker()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var ramp = asm.Assemble("aaramp1.sdf")!;
        Assert.False(RoadPieceObjects.Painted(0x50, ramp));
        Assert.Equal(["AA1_RMP1"], RoadPieceObjects.Drawn(ramp).Parts.Select(p => p.NodeName));
        // the bridge's last top-level node is its marker: the original shows nothing of it (seen in the game: T01, 2026-11)
        var bridge = asm.Assemble("bebridg2.sdf")!;
        Assert.Equal(["EB1_ZZZ2"], RoadPieceObjects.Drawn(bridge).Parts.Select(p => p.NodeName));
        Assert.False(RoadPieceObjects.Painted(0x50, bridge));
        // a fence of three sections shows its last, the gate (seen in the game)
        Assert.Equal(["AF1_GAT5"], RoadPieceObjects.Drawn(asm.Assemble("afence5.sdf")!).Parts.Select(p => p.NodeName));
        // the same models as a ramp, a bridge: whole
        Assert.False(RoadPieceObjects.Painted(0xb, ramp));
        Assert.True(bridge.Parts.Count > 1);
    }

    /// <summary>
    /// The stock maps' pieces are all painted as before: every road-piece placement a flat piece, its one node its whole model.
    /// The stock maps are miss16\'s: neither GOG install ships a mission in addon\, so a level there is the player's (a played
    /// install with a pack of community levels in it counted hundreds more pieces).
    /// </summary>
    [InstallFact]
    public void EveryStockPieceIsStillPainted()
    {
        foreach (var (env, total) in new[] { ("I76_INSTALL", 392), ("I76_NITRO_INSTALL", 642) })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var install = new GameInstall(dir);
            var vfs = new AssetVfs(install);
            var asm = new ModelAssembler(vfs);
            var models = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
            int pieces = 0, painted = 0, maps = 0;
            Assert.NotNull(install.MissionDir);
            foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(GameInstall.IsMissionFile).Order())
            {
                MissionWorld w;
                try { w = new MissionWorld(vfs, asm, f); } catch (FileNotFoundException) { continue; }
                maps++;
                foreach (var p in w.Placements.Concat(w.LinePlacements).Where(p => p.Definition is not null && RoadPieceObjects.IsRoadPiece(p.Object.ObjectType)))
                {
                    pieces++;
                    if (!models.TryGetValue(p.Definition!, out var m)) models[p.Definition!] = m = asm.Assemble(p.Definition!);
                    Assert.NotNull(m);
                    Assert.True(RoadPieceObjects.Painted(p.Object.ObjectType, m), $"{Path.GetFileName(f)} {p.Object.Name} ({p.Object.ObjectType:x}) is no longer painted");
                    Assert.Equal(m!.Parts.Count, RoadPieceObjects.Drawn(m).Parts.Count);
                    painted++;
                }
            }
            log.WriteLine($"{env}: {maps} maps, {pieces} road-piece placements, {painted} painted");
            Assert.Equal(total, pieces);
        }
    }
}
