using System.Numerics;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The map editor's object tools against the real install: the catalogue the stock maps make, and the stock maps' bridges as the object tool places them.</summary>
public class EditorObjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-objcat-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string? NitroDir => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void TheCatalogueIsBuiltFromTheStockMapsAndCachedAsJson()
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var asm = new ModelAssembler(new AssetVfs(install));
        var cache = Path.Combine(_root, "catalogue.json");
        var items = ObjectCatalogue.LoadOrScan(install, asm, cache);
        Assert.InRange(items.Count, 200, 1500);
        CatalogueItem Item(string n) => Assert.Single(items, i => i.Name == n);
        var house = Item("bhjhous1");
        Assert.Equal(ObjectCategory.Buildings, house.Category);
        Assert.Equal(35, house.Width, 0); Assert.Equal(30, house.Depth, 0);
        Assert.Equal(2u, house.Type);
        Assert.True(house.InMelee);
        Assert.Equal(ObjectCategory.Structures, Item("bebridg2").Category);
        Assert.Equal(0xcu, Item("bebridg2").Type);
        Assert.True(Item("bebridg2").AxisAligned);
        Assert.Equal(ObjectCategory.Roadside, Item("sstop_1").Category);
        Assert.Equal(ObjectCategory.Fences, Item("afence4").Category);
        Assert.Equal(ObjectCategory.Markers, Item("a1flag1").Category);
        Assert.Equal(MarkerKind.Flag, Item("a1flag1").Marker);
        Assert.Contains(items, i => i.Marker == MarkerKind.Spawn);
        Assert.Contains(items, i => i.Marker == MarkerKind.Checkpoint);
        // vehicles, gun emplacements and gates need a script; road pieces are not placed by hand
        Assert.All(items.Where(i => i.Definition.EndsWith(".vcf")), i => Assert.Equal(ObjectCategory.Scripted, i.Category));
        Assert.DoesNotContain(items, i => (i.Type & 0xf0) == 0x50);
        // a type a name was seen with is the type it gets, and the others are offered
        var pole = Item("atpole1");
        Assert.Contains(pole.Type, pole.TypesSeen);
        // the second time it comes from the file
        Assert.True(File.Exists(cache));
        Assert.Equal(items.Count, ObjectCatalogue.LoadOrScan(install, asm, cache).Count);
    }

    [Fact]
    public void AStockWoodenBridgeHasLevelRoadEndsAndTheStockRoadsEndOnThem()
    {
        if (NitroDir is null) return;
        var install = new GameInstall(NitroDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var doc = MapDocument.Load(install.FindMission("n11")!);
        var bridge = doc.Objects.Single(o => o.Name == "bewdbrg1");
        // the deck's ends from the sim's collider, as the object tool reads them (here in the placement's own frame)
        var so = MissionSetup.StructureCollider(vfs, asm, "bewdbrg1", "bewdbrg1.sdf", 0xc, bridge.Rotation, bridge.Position with { Y = 0 });
        Assert.NotNull(so);
        var ends = StructureAttach.Ends(so!.Surfaces.Select(p => p.V));
        Assert.Equal(2, ends.Count);
        Assert.All(ends, e => Assert.True(e.RoadLevel));                 // a deck laid level with the ground: both ends are road ends
        Assert.InRange(Vector2.Distance(new(ends[0].Position.X, ends[0].Position.Z), new(ends[1].Position.X, ends[1].Position.Z)), 79.5, 80.7);
        var deck = new Deck(new(ends[0].Position.X, ends[0].Position.Z), new(ends[1].Position.X, ends[1].Position.Z), ends[0].Width);
        // the stock roads end on those two points, so cutting them for this bridge changes nothing
        var store = RoadStore.FromMission(File.ReadAllBytes(install.FindMission("n11")!));
        RoadSplit.Ops(store.Roads, deck, out int cuts);
        Assert.Equal(0, cuts);
        var polys = RoadGeometry.Build(store.Roads);
        foreach (var at in new[] { deck.Start, deck.End })
            Assert.True(RoadGeometry.NearestEnd(polys, at, 1.5f) is not null, $"no road ends at {at}");
    }

    private static IEnumerable<string> StockPlaced(GameInstall install) =>
        Directory.EnumerateFiles(install.MissionDir!).Where(MapFileSet.IsStockMission)
            .SelectMany(f => { try { return MapDocument.Load(f).Objects.Select(o => o.Name.TrimStart('#').ToLowerInvariant()); } catch (Exception) { return []; } })
            .Distinct();

    [Theory]
    [InlineData("I76_INSTALL")]
    [InlineData("I76_NITRO_INSTALL")]
    public void TheModelsNoStockMapPlacesAreInTheCatalogueExceptTheBrokenOnes(string env)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new GameInstall(dir);
        var asm = new ModelAssembler(new AssetVfs(install));
        var items = ObjectCatalogue.LoadOrScan(install, asm, Path.Combine(_root, env + ".json"));
        foreach (var n in new[] { "aaramp8", "agstlgt1", "axlot1", "axlot2", "ngscrng1", "ngscrng2", "swarn_1" })
            Assert.DoesNotContain(items, i => i.Name == n);
        foreach (var n in ObjectCatalogue.Excluded.Keys) Assert.DoesNotContain(items, i => i.Name == n);
        // what is added is flagged, has its model's size and a category
        var added = items.Where(i => i.NotInStockMaps).ToList();
        Assert.InRange(added.Count, 45, 60);
        Assert.All(added, i => { Assert.True(i.Width * i.Depth * i.Height > 0 || i.Category == ObjectCategory.Roadside, i.Name); Assert.Equal(ObjectCatalogue.Unplaced[i.Name], i.Type); Assert.Contains("not in any stock map", i.Tooltip()); });
        CatalogueItem Item(string n) => Assert.Single(items, i => i.Name == n);
        Assert.Equal(ObjectCategory.Buildings, Item("bcaik1").Category);
        Assert.Equal(ObjectCategory.Fences, Item("afence2").Category);
        Assert.Equal(ObjectCategory.Fences, Item("bhyceme1").Category);
        Assert.Equal(ObjectCategory.Props, Item("bhyceme5").Category);
        Assert.Equal(ObjectCategory.Vegetation, Item("ntree3").Category);
        Assert.Equal(ObjectCategory.Roadside, Item("sj13l_1").Category);
        Assert.Equal(ObjectCategory.Structures, Item("aaramp6").Category);
        Assert.Equal(ObjectCategory.Structures, Item("betunnl2").Category);
        // the tunnels know their tube, in either install; the Nitro Pack's own bridges are there for the base game too
        foreach (var t in new[] { "betunnl1", "betunnl2", "betunnl3" })
            Assert.True(Item(t).IsTunnel, t);
        Assert.Equal(ObjectCategory.Structures, Item("bebridga").Category);
        Assert.Equal(0xcu, Item("bebridga").Type);
        Assert.Equal(env == "I76_INSTALL", Item("bebridga").NitroMapsOnly);
        Assert.Equal("Oil pipeline", Item("a3pline1").Description);
        Assert.Equal("Oil pipeline", Item("a3pline1").Title);
        // stock objects keep their learned numbers and gain a label
        Assert.False(Item("bhjhous1").NotInStockMaps);
        Assert.Equal("House (1-story ranch, tan)", Item("bhjhous1").Description);
        // every model the data holds that no stock map of either install places is added or set aside
        var other = Environment.GetEnvironmentVariable(env == "I76_INSTALL" ? "I76_NITRO_INSTALL" : "I76_INSTALL");
        if (other is { Length: > 0 } && Directory.Exists(other))
        {
            var both = StockPlaced(install).Concat(StockPlaced(new GameInstall(other))).ToHashSet();
            var loose = ObjectCatalogue.UnplacedNames(new AssetVfs(install), both).ToList();
            Assert.All(loose, n => Assert.True(ObjectCatalogue.Unplaced.ContainsKey(n) || ObjectCatalogue.Excluded.ContainsKey(n), $"{n} is in no stock map and neither in the catalogue nor set aside"));
        }
    }
}
