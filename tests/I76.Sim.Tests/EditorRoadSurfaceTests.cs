using System.Numerics;
using I76.Editor;
using I76.Editor.Roads;
using I76.Formats.Mission;
using I76.Sim.Sound;
using I76.Sim.Terrain;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The driving sim reads the surface nibble the map editor's road tool lays (the surface id of a car on the road, its grip and tyre sounds, against the ground off it).</summary>
public class EditorRoadSurfaceTests
{
    [Fact]
    public void ACarOnAnEditorRoadSitsOnTheRoadSurfaceAndOffItOnTheGround()
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * TerrainTiles.Tile * TerrainTiles.Tile * 2];
        for (int i = 0; i < ter.Length; i += 2) { ter[i] = 0xc8; ter[i + 1] = 0x00; }
        var doc = new TerrainDocument(new TerrainTiles(ter, 4, grid), "flat");
        // the grip table of a stock world: dirt-like 0, road 2 (dirt 3, planks 4)
        var surfaces = new Surface[8];
        for (int i = 0; i < 8; i++) surfaces[i] = new Surface(1.2f + 0.1f * i, 0.03f, 0, 0, 0);
        var ground = new TerrainGround(doc.Tiles, surfaces);

        foreach (var (kind, id) in new[] { (0u, 2), (1u, 3), (2u, 4) })
        {
            var d = new TerrainDocument(new TerrainTiles((byte[])ter.Clone(), 4, grid), "flat");
            var plan = RoadPlanner.Plan(RoadCurves.Resample([new Vector2(300, 400), new Vector2(700, 400)]), kind, null, null, d.Roads);
            RoadPlanner.Commit(d, d.Roads, plan, conform: true);
            var g = new TerrainGround(d.Tiles, surfaces);
            var onRoad = new Vehicle { Surface = g.SurfaceId(500, 400) };
            var off = new Vehicle { Surface = g.SurfaceId(500, 440) };
            Assert.Equal(id, onRoad.Surface);
            Assert.Equal(0, off.Surface);
            Assert.NotEqual(g.Surface(off.Surface).Grip, g.Surface(onRoad.Surface).Grip);
            // asphalt rolls silent, dirt and planks do not; the bare ground rolls with the dirt sound
            var l = new List<string>();
            onRoad.Speed = off.Speed = 20; onRoad.Flags = off.Flags = VehicleFlags.EngineOn;
            TyreSounds.Wanted(onRoad, true, false, l);
            var roadSound = l.ToArray();
            TyreSounds.Wanted(off, true, false, l);
            Assert.Equal(new[] { "vcddirt.wav" }, l.ToArray());
            Assert.Equal(kind == 0 ? [] : new[] { "vcddirt.wav" }, roadSound);
        }
    }

    [Fact]
    public void ACarOnPaintedGravelSitsOnSurface5AndRollsWithTheGravelSound()
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int i = 0; i < 4; i++) grid[i] = (byte)i;
        var ter = new byte[4 * TerrainTiles.Tile * TerrainTiles.Tile * 2];
        for (int i = 0; i < ter.Length; i += 2) { ter[i] = 0xc8; ter[i + 1] = 0x00; }
        var doc = new TerrainDocument(new TerrainTiles(ter, 4, grid), "flat");
        var stroke = new BrushStroke(doc, new BrushSettings { Kind = BrushKind.Surface, Radius = 100, SurfaceId = 5 });
        stroke.Stamp(100, 100, 0.1);   // sample coordinates: world (500, 500)
        var g = new TerrainGround(doc.Tiles, new Surface[8]);
        var on = new Vehicle { Surface = g.SurfaceId(500, 500), Speed = 20, Flags = VehicleFlags.EngineOn };
        Assert.Equal(5, on.Surface);
        Assert.Equal(0, g.SurfaceId(1500, 500));
        var l = new List<string>();
        TyreSounds.Wanted(on, true, false, l);
        Assert.Equal(new[] { "vcdgrav.wav" }, l.ToArray());
    }
}
