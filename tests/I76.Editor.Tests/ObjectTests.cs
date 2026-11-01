using I76.Formats.Assembly;
using System.Numerics;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

public class ObjectTests
{
    private static CatalogueItem House(float w = 20, float d = 10, ObjectCategory c = ObjectCategory.Buildings, uint type = 2) =>
        new() { Name = "bhouse", Category = c, Type = type, MinX = 0, MaxX = w, MinZ = 0, MaxZ = d, MaxY = 5 };

    private static CatalogueItem Bridge(float w = 10, float len = 80) =>
        new() { Name = "bebridg2", Category = ObjectCategory.Structures, Type = 0xc, MinX = 0, MaxX = w, MinZ = 0, MaxZ = len, MaxY = 4 };

    private static RoadEdit Road(Vector2 from, Vector2 to, float width = 10, float step = 5)
    {
        var dir = Vector2.Normalize(to - from);
        var n = new Vector2(-dir.Y, dir.X) * width / 2;
        int count = (int)(Vector2.Distance(from, to) / step) + 1;
        var pts = new List<RoadEditPoint>();
        for (int i = 0; i < count; i++) { var c = from + dir * (i * step); pts.Add(new RoadEditPoint(c - n, c + n)); }
        return new RoadEdit(1, pts);
    }

    /// <summary>The roads after cutting them at a deck: the ops applied to a store.</summary>
    private static (List<RoadEdit> Roads, int Cuts) Cut(IEnumerable<RoadEdit> roads, Deck deck)
    {
        var store = new RoadStore(roads);
        var ops = RoadSplit.Ops(store.Roads, deck, out int cuts);
        foreach (var o in ops) o.Do(store);
        return ([.. store.Roads], cuts);
    }

    // ---- footprints ----

    [Fact]
    public void FootprintTurnsWithTheYawLikeTheMapObject()
    {
        var f = Footprint.Of(House(20, 10), new Vector3(100, 0, 100), MathF.PI / 2);   // yaw 90: forward (+z of the model) is world +x
        var far = f.ToWorld(new(0, 10));
        Assert.Equal(110, far.X, 3); Assert.Equal(100, far.Y, 3);
        var o = MapObject.Create("x", 2, new Vector3(100, 0, 100), MathF.PI / 2);
        var r = o.Rotation;                                                          // row 2 is the forward axis (sin, 0, cos)
        Assert.Equal(f.AxisZ.X, r[6], 4); Assert.Equal(f.AxisZ.Y, r[8], 4);
        Assert.Equal(f.AxisX.X, r[0], 4); Assert.Equal(f.AxisX.Y, r[2], 4);
    }

    [Fact]
    public void FootprintsOverlapOnlyWhenTheyShareGround()
    {
        var a = Footprint.Of(House(20, 10), new Vector3(0, 0, 0), 0);
        Assert.True(a.Overlaps(Footprint.Of(House(20, 10), new Vector3(10, 0, 5), 0)));
        Assert.False(a.Overlaps(Footprint.Of(House(20, 10), new Vector3(25, 0, 0), 0)));
        // a turned box that reaches over the corner by SAT, and one that does not though their circles do
        var turned = Footprint.Of(House(20, 10), new Vector3(11.5f, 0, 8.5f), MathF.PI / 4);
        Assert.True(a.Overlaps(turned));
        Assert.False(a.Overlaps(Footprint.Of(House(20, 10), new Vector3(13.5f, 0, 23.5f), MathF.PI / 4)));   // circles overlap, boxes do not
        Assert.True(a.Overlaps(Footprint.Of(House(20, 10), new Vector3(21, 0, 0), 0), margin: 4));   // within the margin
        Assert.False(a.Overlaps(Footprint.Of(House(20, 10), new Vector3(19.7f, 0, 0), 0), margin: -1));
    }

    [Fact]
    public void ReliefIsHowFarTheGroundStraysFromTheOriginHeight()
    {
        var f = Footprint.Of(House(20, 10), new Vector3(0, 0, 0), 0);
        Assert.Equal(0, PlaceRules.Relief(f, (_, _) => 50), 4);
        Assert.Equal(4, PlaceRules.Relief(f, (x, _) => 50 + x * 0.2f), 3);     // a 4 m rise over the 20 m width
        var item = House();
        Assert.Equal(PlaceProblem.TooSteep, PlaceRules.Check(item, f, [], (x, _) => 50 + x * 0.2f, (_, _) => true));
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(item, f, [], (x, _) => 50 + x * 0.05f, (_, _) => true));
        // a sign or a cactus stands on any slope
        var sign = House(1, 1, ObjectCategory.Roadside, 4);
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(sign, Footprint.Of(sign, default, 0), [], (x, _) => x * 5, (_, _) => true));
    }

    [Fact]
    public void ThePlacementCheckSeesOverlapsRoadsAndTheEdgeOfTheMap()
    {
        var item = House();
        var at = Footprint.Of(item, new Vector3(0, 0, 0), 0);
        var other = Footprint.Of(item, new Vector3(10, 0, 0), 0);
        Assert.Equal(PlaceProblem.OverlapsObject, PlaceRules.Check(item, at, [other], (_, _) => 0, (_, _) => true));
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(item, at, [Footprint.Of(item, new Vector3(40, 0, 0), 0)], (_, _) => 0, (_, _) => true));
        Assert.Equal(PlaceProblem.OffMap, PlaceRules.Check(item, at, [], (_, _) => 0, (x, _) => x > 5));
        var roads = RoadGeometry.Build([Road(new(-50, 5), new(60, 5))]);
        Assert.Equal(PlaceProblem.OnRoad, PlaceRules.Check(item, at, [], (_, _) => 0, (_, _) => true, roads));
        var off = Footprint.Of(item, new Vector3(0, 0, 30), 0);
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(item, off, [], (_, _) => 0, (_, _) => true, roads));
        // roadside things and bridges may stand on a road
        var pole = House(1, 1, ObjectCategory.Roadside, 3);
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(pole, Footprint.Of(pole, new Vector3(0, 0, 5), 0), [], (_, _) => 0, (_, _) => true, roads));
    }

    // ---- snapping ----

    [Fact]
    public void ABuildingSnapsBesideTheRoadFacingIt()
    {
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 400))]);     // along +z at x = 100, 10 m wide
        var item = House(20, 10);
        var r = Snapping.Place(item, new Vector2(140, 200), 0.3f, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoad);
        // on the +x side, facing -x (toward the road)
        Assert.Equal(-MathF.PI / 2, r.Yaw, 3);
        var f = r.Footprint(item);
        // the front face (local z = depth) is 6 m from the road's edge: 5 + 6 = 11 m from the centre line
        var front = f.ToWorld(new(10, 10));
        Assert.Equal(111, front.X, 2);
        // the other side flips it
        var l = Snapping.Place(item, new Vector2(60, 200), 0, SnapMode.Roads, 5, roads);
        Assert.Equal(MathF.PI / 2, l.Yaw, 3);
        Assert.Equal(89, l.Footprint(item).ToWorld(new(10, 10)).X, 2);
        // the object slides along the road with the cursor
        Assert.Equal(300, Snapping.Place(item, new Vector2(140, 300), 0, SnapMode.Roads, 5, roads).Footprint(item).Centre.Y, 2);
    }

    [Fact]
    public void FarFromRoadsItSnapsToTheGridAndOffDoesNot()
    {
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 400))]);
        var item = House(20, 10);
        var g = Snapping.Place(item, new Vector2(503, 207), 0, SnapMode.Roads, 5, roads);
        Assert.False(g.OnRoad);
        Assert.Equal(505, g.Footprint(item).Centre.X, 3); Assert.Equal(205, g.Footprint(item).Centre.Y, 3);
        var o = Snapping.Place(item, new Vector2(503, 207), 0, SnapMode.Off, 5, roads);
        Assert.Equal(503, o.Footprint(item).Centre.X, 3);
        Assert.Equal(15f * MathF.PI / 180, Snapping.SnapAngle(0.28f, Snapping.AngleStep), 4);
    }

    [Fact]
    public void ABridgeLiesAlongTheRoadCentredOnItAndStaysOnTheAxes()
    {
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 400))]);
        var b = Bridge();
        var r = Snapping.Place(b, new Vector2(104, 200), 0, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoad);
        Assert.Equal(0, r.Yaw, 4);
        var deck = r.Deck!.Value;
        Assert.Equal(100, deck.Start.X, 2); Assert.Equal(160, deck.Start.Y, 2); Assert.Equal(240, deck.End.Y, 2);   // centred on the road point (z 200)
        // turned half way round it is the same bridge the other way
        var back = Snapping.Place(b, new Vector2(104, 200), MathF.PI, SnapMode.Roads, 5, roads);
        Assert.Equal(MathF.PI, MathF.Abs(back.Yaw), 3);
        // a road 30 degrees off the axis: the bridge keeps to the axis only within 25 degrees, so it falls to free placement
        var slanted = RoadGeometry.Build([Road(new(0, 0), new(200, 200 * MathF.Tan(60 * MathF.PI / 180)))]);
        Assert.False(Snapping.Place(b, new Vector2(100, 170), 0, SnapMode.Roads, 5, slanted).OnRoad);
        // a ramp (not a bridge) does follow a slanted road
        var ramp = Bridge(20, 15); ramp.Type = 0xb; ramp.Name = "aaramp3";
        Assert.True(Snapping.Place(ramp, new Vector2(100, 170), 0, SnapMode.Roads, 5, slanted).OnRoad);
    }

    [Fact]
    public void CuttingTheRoadAtABridgeEndsItAtTheDeckEnds()
    {
        var road = Road(new(100, 0), new(100, 400));
        var deck = new Deck(new Vector2(100, 160), new Vector2(100, 240), 10);
        var (cut, cuts) = Cut([road], deck);
        Assert.Equal(1, cuts);
        Assert.Equal(2, cut.Count);
        Assert.Equal(160, cut[0].Points[^1].Centre.Y, 2);     // the first road ends where the deck begins
        Assert.Equal(240, cut[1].Points[0].Centre.Y, 2);      // the next starts where it ends
        Assert.All(cut[0].Points, p => Assert.True(p.Centre.Y <= 160.01f));
        Assert.All(cut[1].Points, p => Assert.True(p.Centre.Y >= 239.99f));
        Assert.Equal(10, Vector2.Distance(cut[0].Points[^1].Left, cut[0].Points[^1].Right), 2);   // the width is kept
        // nothing was changed in the input
        Assert.Equal(81, road.Points.Count);
        // a road crossing under the bridge is left alone
        var cross = Road(new(0, 200), new(300, 200));
        var (both, n2) = Cut([road, cross], deck);
        Assert.Equal(1, n2); Assert.Equal(3, both.Count); Assert.Same(cross, both[2]);
        // a road that ends inside the deck keeps the part outside and ends at the deck's end
        var stub = Road(new(100, 100), new(100, 200));
        var (one, _) = Cut([stub], deck);
        Assert.Single(one);
        Assert.Equal(160, one[0].Points[^1].Centre.Y, 2);
    }

    private static CatalogueItem Ramp()
    {
        // a 10 m wide jump ramp 30 m long along z: the foot at z 0 at ground level (a road end), the lip at z 30, 7 m up (a drop)
        var r = new CatalogueItem { Name = "aaramp1", Category = ObjectCategory.Structures, Type = 0xb, MinX = 0, MaxX = 10, MinZ = 0, MaxZ = 30, MaxY = 7 };
        r.Ends =
        [
            new StructureAttach.Attach(new Vector3(5, 0, 0), new Vector2(0, -1), 10, true) { Slope = 0.23f },
            new StructureAttach.Attach(new Vector3(5, 7, 30), new Vector2(0, 1), 10, false) { Slope = -0.23f },
        ];
        return r;
    }

    [Fact]
    public void ARampAtTheOpenEndOfARoadHasItsFootOnTheEnd()
    {
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 200))]);
        var ramp = Ramp();
        // the cursor past the road's end: the foot (the end whose outward direction faces back along the road) is put on the end, the ramp goes on from it
        var r = Snapping.Place(ramp, new Vector2(103, 208), 0, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoad);
        Assert.True(r.OnRoadEnd);
        Assert.Null(r.Deck);                                           // nothing to cut: the road already ends there
        var (foot, outDir) = Snapping.EndAt(ramp.Ends![0], r.Origin, r.Yaw);
        Assert.Equal(100, foot.X, 2); Assert.Equal(200, foot.Y, 2);
        Assert.Equal(-1, outDir.Y, 3);                                // the foot faces the road
        var (lip, _) = Snapping.EndAt(ramp.Ends![1], r.Origin, r.Yaw);
        Assert.Equal(230, lip.Y, 2);                                  // and the lip is 30 m on, past the end
    }

    /// <summary>The 2 m wood ramp's drive-on face as the game's data has it: 20 m across, 15 m long, climbing 2 m along z.</summary>
    private static CatalogueItem WoodRamp()
    {
        var r = new CatalogueItem { Name = "aaramp3", Category = ObjectCategory.Structures, Type = 0xb, MinX = 0, MaxX = 20, MinZ = 0, MaxZ = 15, MaxY = 2 };
        r.Ends = StructureAttach.Ends([[new(20, 2, 15), new(0, 2, 15), new(0, 0, 0), new(20, 0, 0)]]);
        return r;
    }

    [Fact]
    public void AWideLowRampRunsTheWayItClimbsNotAcrossIt()
    {
        var ramp = WoodRamp();
        Assert.Equal(2, ramp.Ends!.Count);
        var foot = ramp.Ends.Single(e => e.Low); var lip = ramp.Ends.Single(e => !e.Low);
        Assert.Equal(0, foot.Position.Z, 3); Assert.Equal(-1, foot.Direction.Y, 3); Assert.Equal(20, foot.Width, 3);
        Assert.Equal(15, lip.Position.Z, 3); Assert.Equal(1, lip.Direction.Y, 3);
        // on a road along +x its foot faces back down the road and the ramp climbs the way the road goes, not across it
        var roads = RoadGeometry.Build([Road(new(0, 100), new(300, 100))]);
        var r = Snapping.Place(ramp, new Vector2(150, 102), MathF.PI / 2, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoad);
        var (f, fOut) = Snapping.EndAt(foot, r.Origin, r.Yaw);
        var (l, _) = Snapping.EndAt(lip, r.Origin, r.Yaw);
        Assert.Equal(100, f.Y, 2); Assert.Equal(100, l.Y, 2);          // both ends on the road's line
        Assert.Equal(15, MathF.Abs(l.X - f.X), 2);                     // 15 m apart along it
        Assert.True(MathF.Abs(fOut.X) > 0.999f);
        // and at a road's open end the foot is the end that meets it
        var open = Snapping.Place(ramp, new Vector2(310, 100), 0, SnapMode.Roads, 5, roads);
        Assert.True(open.OnRoadEnd);
        var (f2, _) = Snapping.EndAt(foot, open.Origin, open.Yaw); var (l2, _) = Snapping.EndAt(lip, open.Origin, open.Yaw);
        Assert.Equal(300, f2.X, 2); Assert.Equal(315, l2.X, 2);
    }

    [Fact]
    public void ARampOnTheMiddleOfARoadFlipsWithTheTurnAndCutsBetweenItsEnds()
    {
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 400))]);
        var ramp = Ramp();
        var up = Snapping.Place(ramp, new Vector2(104, 200), 0, SnapMode.Roads, 5, roads);
        var down = Snapping.Place(ramp, new Vector2(104, 200), MathF.PI, SnapMode.Roads, 5, roads);
        Assert.NotNull(up.Deck); Assert.NotNull(down.Deck);
        // the same stretch of road, the foot at the other end
        Assert.Equal(185, up.Deck!.Value.Start.Y, 2); Assert.Equal(215, up.Deck.Value.End.Y, 2);
        Assert.Equal(215, down.Deck!.Value.Start.Y, 2); Assert.Equal(185, down.Deck.Value.End.Y, 2);
        Assert.True(up.Footprint(ramp).Contains(new Vector2(100, 200)));
        var (cut, cuts) = Cut(new[] { Road(new(100, 0), new(100, 400)) }, up.Deck.Value);
        Assert.Equal(1, cuts);
        Assert.Equal(185, cut[0].Points[^1].Centre.Y, 2);              // the road ends at the foot
        Assert.Equal(215, cut[1].Points[0].Centre.Y, 2);               // the next piece starts at the lip
    }

    [Fact]
    public void ABridgeAtARoadEndStaysOnTheAxesAndMeetsIt()
    {
        var roads = RoadGeometry.Build([Road(new(0, 100), new(300, 100))]);      // along +x, ending at x = 300
        var b = Bridge();
        var r = Snapping.Place(b, new Vector2(310, 103), 0, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoadEnd);
        var deck = Deck.Of(r.Footprint(b));
        float near = MathF.Min(deck.Start.X, deck.End.X);
        Assert.Equal(300, near, 2);                                    // one end of the deck on the road's end
        Assert.Equal(100, deck.Start.Y, 2);
        Assert.Equal(0, MathF.Abs(Snapping.Wrap(r.Yaw)) % (MathF.PI / 2), 3);   // on an axis
    }

    private static CatalogueItem Tunnel() =>
        new() { Name = "betunnl2", Category = ObjectCategory.Structures, Type = 2, MinX = -300, MaxX = 0, MinZ = 0, MaxZ = 170, MaxY = 129, TunnelX = -150, TunnelZ0 = 21, TunnelZ1 = 129, TunnelWidth = 12 };

    [Fact]
    public void ATunnelLiesAlongTheRoadAtAnyHeadingAndTheRoadIsNotCut()
    {
        var t = Tunnel();
        Assert.True(t.IsTunnel); Assert.True(t.CarriesRoad); Assert.False(t.CutsRoad);
        var ends = t.DeckEnds();
        Assert.Equal(21, ends[0].Position.Z); Assert.Equal(129, ends[1].Position.Z); Assert.Equal(-150, ends[0].Position.X);
        Assert.Equal(12, ends[0].Width);
        // a road along z: the tube's middle on the road point, the tube on the road
        var roads = RoadGeometry.Build([Road(new(100, 0), new(100, 400))]);
        var r = Snapping.Place(t, new Vector2(104, 200), 0, SnapMode.Roads, 5, roads);
        Assert.True(r.OnRoad); Assert.False(r.OnRoadEnd);
        var deck = r.Deck!.Value;
        Assert.Equal(100, deck.Start.X, 2); Assert.Equal(146, deck.Start.Y, 2); Assert.Equal(254, deck.End.Y, 2);    // 108 m, centred on z 200
        Assert.Equal(0, DeckCover.Gap(deck, roads), 1);
        // a road at a slant is followed (a tunnel is not held to the axes like a bridge)
        var slanted = RoadGeometry.Build([Road(new(0, 0), new(300, 300 * MathF.Tan(60 * MathF.PI / 180)))]);
        var rs = Snapping.Place(t, new Vector2(150, 260), 0, SnapMode.Roads, 5, slanted);
        Assert.True(rs.OnRoad);
        var axis = rs.Deck!.Value.Axis;
        Assert.Equal(MathF.Cos(60 * MathF.PI / 180), MathF.Abs(axis.X), 2);
        // beside an open end of a road it still lies on the road (no end mode), and a road that stops short leaves a gap
        var short1 = RoadGeometry.Build([Road(new(100, 0), new(100, 190))]);
        var rr = Snapping.Place(t, new Vector2(104, 180), 0, SnapMode.Roads, 5, short1);
        Assert.False(rr.OnRoadEnd);
        Assert.InRange(DeckCover.Gap(rr.Deck!.Value, short1), 30, 70);
        // the road is not cut by it, a bridge's would be
        Assert.False(t.CutsRoad); Assert.True(Bridge().CutsRoad);
        // it is not turned down for steep ground (it is placed on levelled ground)
        var f = Footprint.Of(t, new Vector3(0, 0, 0), 0);
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(t, f, [], (x, z) => x * 0.3f, (_, _) => true));
        var bridge = Bridge();
        Assert.Equal(PlaceProblem.TooSteep, PlaceRules.Check(bridge, Footprint.Of(bridge, default, 0), [], (x, z) => x * 0.8f, (_, _) => true));
    }

    // ---- the layer, commands, undo ----

    private static ObjectLayer Layer(out MapProject project)
    {
        project = MapProject.CreateBlank("lyr", "Layer", ".cbt", 2, 2, 100);
        project.Mission.Objects.Add(MapObject.Spawn(new Vector3(500, 100, 500)));
        project.Mission.Objects.Add(MapObject.Create("bhouse", 2, new Vector3(800, 100, 800), 0.5f));
        project.Mission.Roads.Add(Road(new(100, 100), new(100, 500)));
        return new ObjectLayer(project.Mission, n => n == "bhouse" ? "bhouse.sdf" : null, new RoadStore(project.Mission.Roads)) { Ground = (_, _) => 100 };
    }

    [Fact]
    public void PlacingMovingAndBulldozingGoThroughUndoAndRedo()
    {
        var layer = Layer(out _);
        var undo = new UndoStack(new TerrainDocument(MapProject.CreateBlank("x", "x").Terrain, "x"));
        Assert.Equal(2, layer.Items.Count);
        var added = layer.Add(MapObject.Create("bhouse", 2, new Vector3(900, 0, 900)));
        undo.Push(new ObjectCommands.Place(layer, [added], "Place bhouse"));
        Assert.Equal(3, layer.Items.Count);
        layer.SetPose(added, new Vector3(910, 0, 905), 1f);
        Assert.Equal(100, added.Position.Y);                       // stands on the ground
        undo.Push(new ObjectCommands.Transform(layer, [(added, new Pose(new(900, 100, 900), 0), new Pose(added.Position, 1f))]));
        var gone = new ObjectCommands.Remove(layer, [layer.Items[0]]);
        undo.Push(gone);
        Assert.Equal(2, layer.Items.Count);
        Assert.Equal("bhouse", layer.Items[0].Name);
        undo.Undo();                                                // the spawn comes back at the front
        Assert.Equal(3, layer.Items.Count); Assert.Equal(MapRole.Spawn, layer.Items[0].Role);
        undo.Undo();
        Assert.Equal(900, added.Position.X); Assert.Equal(0, added.Yaw, 4);
        undo.Undo();
        Assert.Equal(2, layer.Items.Count);
        undo.Redo(); undo.Redo(); undo.Redo();
        Assert.Equal(2, layer.Items.Count);
        Assert.Equal(910, layer.Items.Single(i => i.Id == added.Id).Position.X);
    }

    [Fact]
    public void ChangesReachTheGameSideAsNewPlacements()
    {
        var layer = Layer(out _);
        var seen = new List<LayerChange>();
        layer.Changed += seen.Add;
        var house = layer.Items[1];
        var before = house.Placement;
        layer.SetPose(house, new Vector3(820, 0, 800), 0.5f);
        Assert.Single(seen);
        Assert.Equal(ChangeKind.Changed, seen[0].Kind);
        Assert.Same(before, seen[0].Old);
        Assert.NotSame(before, house.Placement);
        Assert.Equal(820, house.Placement.Object.Position.X);
        Assert.Equal("bhouse.sdf", house.Placement.Definition);
        layer.SetPose(house, new Vector3(820, 0, 800), 0.5f);           // nothing moved: quiet
        Assert.Single(seen);
        Assert.Null(layer.Items[0].Placement.Definition);               // a spawn has no model
    }

    [Fact]
    public void ASavedMapHasTheEditedObjectsAndRoadsAndTheRestAsItWas()
    {
        var layer = Layer(out var project);
        var spawnBytes = project.Mission.Objects[0].Payload.ToArray();
        layer.Add(MapObject.Spawn(new Vector3(600, 0, 600), 1f, team: 2));
        layer.Add(MapObject.Checkpoint(3, new Vector3(700, 0, 700)));
        layer.Add(MapObject.Flag(2, new Vector3(710, 0, 710)));
        layer.Remove(layer.Items[1]);
        var deck = new Deck(new Vector2(100, 200), new Vector2(100, 280), 10);
        new RoadCommand("Cut", layer.Roads, RoadSplit.Ops(layer.Roads.Roads, deck, out _)).Apply();
        layer.ApplyTo(project.Mission);
        layer.Roads.WriteTo(project.Mission);      // what MapSaver does: the objects from the layer, the roads from the store
        var bytes = project.Mission.ToBytes();
        var back = MapDocument.Parse(bytes);
        Assert.Equal(4, back.Objects.Count);
        Assert.Equal(spawnBytes, back.Objects[0].Payload);              // an untouched record is written as it was
        Assert.Equal(2, back.Spawns.Count());
        Assert.Equal(2, back.Spawns.Single(s => s.Number == 2).Number);
        Assert.Equal(3, back.Checkpoints.Single().Number);
        Assert.Equal(2, back.Flags.Single().Number);
        Assert.Equal(2, back.Roads.Count);
        Assert.Equal(200, back.Roads[0].Points[^1].Centre.Y, 2);
        Assert.Equal(280, back.Roads[1].Points[0].Centre.Y, 2);
        Assert.Equal(project.Mission.ArenaName, back.ArenaName);
        // the layer's version marks the document modified until it is saved
        var doc = new TerrainDocument(project.Terrain, "lyr") { Layer = layer };
        Assert.True(doc.Modified);
        doc.MarkSaved();
        Assert.False(doc.Modified);
        layer.SetPose(layer.Items[0], new Vector3(1, 0, 1), 0);
        Assert.True(doc.Modified);
    }

    [Fact]
    public void RoadCutsUndoToTheOriginalRoads()
    {
        var layer = Layer(out _);
        var before = layer.Roads.Roads.ToList();
        var ops = RoadSplit.Ops(layer.Roads.Roads, new Deck(new Vector2(100, 200), new Vector2(100, 280), 10), out _);
        var cmd = new RoadCommand("Cut", layer.Roads, ops).Apply();
        Assert.Equal(2, layer.Roads.Count);
        Assert.Equal(2, layer.Polylines.Count);
        cmd.Undo(null!);
        Assert.Equal(1, layer.Roads.Count);
        Assert.Same(before[0], layer.Roads[0]);
        Assert.Single(layer.Polylines);
        cmd.Redo(null!);
        Assert.Equal(2, layer.Roads.Count);
    }

    [Fact]
    public void TheDocumentSavesObjectsAndCutRoadsTogetherThroughTheMapWriters()
    {
        string dir = Path.Combine(Path.GetTempPath(), "i26-objsave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = MapProject.CreateBlank("objsv", "Object Save", ".cbt", 2, 2, 100);
            project.Mission.Roads.Add(Road(new(500, 400), new(500, 900)));
            project.Mission.Objects.Add(MapObject.Spawn(new Vector3(700, 100, 700)));
            string path = project.SaveTo(dir).First(f => f.EndsWith(".cbt"));
            var map = MapDocument.Load(path);
            var store = RoadStore.FromMission(File.ReadAllBytes(path));
            var layer = new ObjectLayer(map, _ => null, store) { Ground = (_, _) => 100 };
            var tiles = MapProject.Load(path).Terrain;
            var doc = new TerrainDocument(tiles, "objsv", "Object Save", path, dir, roads: store) { Layer = layer };
            Assert.False(doc.Modified);
            // a bridge placed on the road: the road cut, the object added, one undo step
            var undo = new UndoStack(doc);
            var deck = new Deck(new Vector2(500, 600), new Vector2(500, 680), 10);
            var cut = new RoadCommand("Cut", store, RoadSplit.Ops(store.Roads, deck, out _)).Apply();
            var bridge = layer.Add(MapObject.Create("bebridg2", 0xc, new Vector3(495, 0, 600), 0));
            layer.Add(MapObject.Flag(1, new Vector3(1, 0, 1)));
            undo.Push(new ObjectCommands.Group("Place bridge", [cut, new ObjectCommands.Place(layer, [bridge], "Place")]));
            Assert.True(doc.Modified);
            doc.Save();
            Assert.False(doc.Modified);
            var back = MapDocument.Load(path);
            Assert.Equal(3, back.Objects.Count);
            Assert.Contains(back.Objects, o => o.Name == "bebridg2" && o.ObjectType == 0xc);
            Assert.Equal(2, back.Roads.Count);
            Assert.Equal(600, back.Roads[0].Points[^1].Centre.Y, 2);
            Assert.Equal(680, back.Roads[1].Points[0].Centre.Y, 2);
            // undo takes the bridge and the cut away together, and the document is modified again
            undo.Undo();
            Assert.Equal(2, layer.Items.Count);               // the spawn and the flag: the flag was not part of the step
            Assert.Equal(1, store.Count);
            Assert.True(doc.Modified);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void FlatteningTheFootprintLevelsTheGroundAsOneCommandAndUndoesCleanly()
    {
        var project = MapProject.CreateBlank("lvl", "Level", ".cbt", 2, 2, 100);
        var doc = new TerrainDocument(project.Terrain, "lvl");
        const int sx0 = 4 * 128, sz0 = 72 * 128;
        for (int sz = sz0; sz < sz0 + 256; sz++)
            for (int sx = sx0; sx < sx0 + 256; sx++) doc.SetHeight(sx, sz, 1000 + (sx - sx0) * 8);       // a slope of 16 % in x
        float H(float x, float z) => doc.GetHeight((int)MathF.Floor(x / 5), (int)MathF.Floor(z / 5)) * 0.1f;
        var item = House(20, 10);
        var f = Footprint.Of(item, new Vector3(sx0 * 5 + 200, 0, sz0 * 5 + 200), 0);
        Assert.Equal(PlaceProblem.TooSteep, PlaceRules.Check(item, f, [], H, (_, _) => true));
        var before = doc.Snapshot();
        var cmd = Leveling.Flatten(doc, f, H);
        Assert.NotNull(cmd);
        Assert.Equal("Level ground", cmd!.Name);
        // flat under the footprint (corners, edges, centre within a tenth of a metre of each other), and the rule is now satisfied
        var spots = f.Corners().Append(f.Centre).Select(p => H(p.X, p.Y)).ToList();
        Assert.True(spots.Max() - spots.Min() < 0.15f, $"{spots.Max() - spots.Min()} m of relief left");
        Assert.Equal(PlaceProblem.None, PlaceRules.Check(item, f, [], H, (_, _) => true));
        // the skirt is soft: a point just past the core still lies between the slope and the level, and far away nothing changed
        float far = H(f.Origin.X + 120, f.Origin.Y);
        Assert.Equal((1000 + (sx0 * 5 + 320 - sx0 * 5) / 5 * 8) * 0.1f, far, 1);
        // one command: undoing it puts every sample back, redoing levels again
        cmd.Undo(doc);
        Assert.Equal(before, doc.Snapshot());
        cmd.Redo(doc);
        Assert.True(Enumerable.Range(0, 4).All(i => MathF.Abs(H(f.Corners()[i].X, f.Corners()[i].Y) - spots[0]) < 0.15f));
    }

    // ---- the catalogue ----

    [Theory]
    [InlineData("bhjhous1", 2u, false, 35f, 30f, ObjectCategory.Buildings)]
    [InlineData("bebridg1", 0xcu, false, 10f, 100f, ObjectCategory.Structures)]
    [InlineData("aaramp3", 0xbu, false, 20f, 15f, ObjectCategory.Structures)]
    [InlineData("sstop_1", 4u, false, 0.8f, 0f, ObjectCategory.Roadside)]
    [InlineData("atpole1", 3u, false, 3.5f, 0.5f, ObjectCategory.Roadside)]
    [InlineData("nsaguar1", 3u, false, 1.6f, 0.8f, ObjectCategory.Vegetation)]
    [InlineData("afence4", 2u, false, 1f, 5f, ObjectCategory.Fences)]
    [InlineData("amoilpm2", 2u, false, 3.1f, 9.6f, ObjectCategory.Props)]
    [InlineData("vxturr1", 8u, false, 2f, 2f, ObjectCategory.Scripted)]
    [InlineData("noal01", 1u, true, 2f, 5f, ObjectCategory.Scripted)]
    [InlineData("bcaikgat", 7u, false, 10f, 0.1f, ObjectCategory.Scripted)]
    public void ObjectsAreGroupedByTheirTypeNameAndSize(string name, uint type, bool vehicle, float w, float d, ObjectCategory expect) =>
        Assert.Equal(expect, ObjectCatalogue.Classify(name, type, vehicle, w, d));

    [Fact]
    public void RoadPiecesAreNotPlacedByHand() => Assert.Null(ObjectCatalogue.Classify("ita21_1", 0x50, false, 10, 10));

    [Fact]
    public void MarkerItemsNameWhatTheLoadersRecognise()
    {
        var items = ObjectCatalogue.MarkerItems().ToList();
        Assert.Contains(items, i => i.Marker == MarkerKind.Spawn && MapObject.Spawn(default).Role == MapRole.Spawn);
        Assert.Equal(4, items.Count(i => i.Marker == MarkerKind.Flag));
        Assert.All(items.Where(i => i.Marker == MarkerKind.Flag), i => Assert.Equal(MapRole.Flag, MapObject.Create(i.Name, i.Type, default).Role));
    }

    // ---- the map check ----

    private static List<MapObject> Objs(params MapObject[] o) => [.. o];

    [Fact]
    public void AMapWithNoSpawnIsAnError()
    {
        var i = MapChecks.Validate([], ".cbt");
        Assert.Contains(i, x => x.Severity == Severity.Error && x.Message.Contains("spawn"));
        Assert.Equal(Severity.Warning, MapChecks.Worst(MapChecks.Validate(Objs(MapObject.Spawn(default)), ".cbt")));
        Assert.Equal(Severity.Info, MapChecks.Worst(MapChecks.Validate(Objs(MapObject.Spawn(default), MapObject.Spawn(Vector3.One)), ".cbt")));
    }

    [Fact]
    public void ACtfMapNeedsOneFlagAndASpawnForEveryTeam()
    {
        var ok = Objs(MapObject.Spawn(default, 0, 1), MapObject.Spawn(default, 0, 2), MapObject.Flag(1, default), MapObject.Flag(2, default));
        Assert.Equal(Severity.Info, MapChecks.Worst(MapChecks.Validate(ok, ".cf2")));
        // a 3-team map from the same objects lacks team 3's flag and spawn
        var three = MapChecks.Validate(ok, ".cf3");
        Assert.Equal(2, three.Count(x => x.Severity == Severity.Error && x.Subject == 3));
        // a second flag for a team is a warning; a flag for an unused team too
        var extra = MapChecks.Validate([.. ok, MapObject.Flag(1, Vector3.One), MapObject.Flag(4, Vector3.One)], ".cf2");
        Assert.Contains(extra, x => x.Severity == Severity.Warning && x.Subject == 1);
        Assert.Contains(extra, x => x.Severity == Severity.Warning && x.Subject == 4);
        // a plain spawn counts for team 1 (the engine puts it in the first table)
        Assert.DoesNotContain(MapChecks.Validate([MapObject.Spawn(default), MapObject.Spawn(default, 0, 2), MapObject.Flag(1, default), MapObject.Flag(2, default)], ".cf2"), x => x.Severity == Severity.Error);
        // flags mean nothing in a melee
        Assert.Contains(MapChecks.Validate([MapObject.Spawn(default), MapObject.Spawn(default), MapObject.Flag(1, default)], ".cbt"), x => x.Message.Contains("CTF"));
    }

    [Fact]
    public void ARaceNeedsCheckpointsNumberedWithoutGaps()
    {
        var spawns = new[] { MapObject.Spawn(default), MapObject.Spawn(default) };
        Assert.Contains(MapChecks.Validate(spawns, ".rac"), x => x.Severity == Severity.Error && x.Message.Contains("needs checkpoints"));
        var gap = MapChecks.Validate([.. spawns, MapObject.Checkpoint(1, new(0, 0, 0)), MapObject.Checkpoint(3, new(0, 0, 200))], ".rac");
        Assert.Single(gap, x => x.Severity == Severity.Error);
        Assert.Equal(2, gap.Single(x => x.Severity == Severity.Error).Subject);
        var good = MapChecks.Validate([.. spawns, MapObject.Checkpoint(1, new(0, 0, 0)), MapObject.Checkpoint(2, new(0, 0, 200)), MapObject.Checkpoint(99, new(0, 0, 400))], ".rac");
        Assert.DoesNotContain(good, x => x.Severity >= Severity.Warning);
        var close = MapChecks.Validate([.. spawns, MapObject.Checkpoint(1, new(0, 0, 0)), MapObject.Checkpoint(2, new(0, 0, 10))], ".rac");
        Assert.Contains(close, x => x.Severity == Severity.Warning && x.Message.Contains("overlap"));
        var dup = MapChecks.Validate([.. spawns, MapObject.Checkpoint(1, new(0, 0, 0)), MapObject.Checkpoint(1, new(0, 0, 100)), MapObject.Checkpoint(2, new(0, 0, 200))], ".rac");
        Assert.Contains(dup, x => x.Severity == Severity.Warning && x.Subject == 1);
    }

    [Fact]
    public void TheModeComesFromTheExtension()
    {
        Assert.Equal((MapMode.Ctf, 3), MapChecks.ModeOf(".CF3"));
        Assert.Equal((MapMode.Race, 0), MapChecks.ModeOf(".rac"));
        Assert.Equal((MapMode.Melee, 0), MapChecks.ModeOf(".cbt"));
        Assert.Equal(MapMode.Other, MapChecks.ModeOf(".msn").Mode);
    }
}
