using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Terrain;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The map editor's playtest puts the car where the cursor's ray meets the ground, and refuses a spot a car cannot stand on.</summary>
public class PlaytestSpawnTests
{
    private sealed class Flat(float h, Func<double, double, bool>? blocked = null) : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (h, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => throw new NotSupportedException();
        public bool Impassable(double x, double z) => blocked?.Invoke(x, z) ?? false;
    }

    private static bool OnMap(double x, double z) => x >= 0 && x <= 1000 && z >= 0 && z <= 1000;

    // a camera 100 m up at (500, 100, 400) looking toward +Z and down by 45 degrees: it meets the flat ground at z = 500
    private static readonly Vector3 Eye = new(500, 100, 400), Down45 = Vector3.Normalize(new(0, -1, 1));

    private static StaticObject Building(float x, float z, float half, float height)
    {
        var o = new StaticObject { Name = "bldg", Type = 3, Centre = new Vector3(x, height / 2, z), Radius = half * 1.5f };
        o.BoundRadius = half * 2;
        o.Boxes.Add(new NodeBox(new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), new Vector3(x, 0, z), new Vector3(-half, 0, -half), new Vector3(half, height, half), half));
        return o;
    }

    [Fact]
    public void GroundUnderTheCursor()
    {
        var plan = PlaytestSpawn.FromRay(new Flat(12), null, OnMap, Eye, Down45);
        Assert.True(plan.Ok, plan.Why);
        Assert.Equal(500, plan.At.X, 0.1f);
        Assert.Equal(488, plan.At.Z, 0.1f);   // 12 m up, the 45 degree ray is 12 m short of the ground at 0
        Assert.Equal(12, plan.At.Y, 0.05f);
    }

    [Fact]
    public void SkyHasNoSpawn()
    {
        var plan = PlaytestSpawn.FromRay(new Flat(0), null, OnMap, Eye, Vector3.Normalize(new(0, 0.2f, 1)));
        Assert.False(plan.Ok);
        Assert.Contains("sky", plan.Why);
    }

    [Fact]
    public void OffTheMapHasNoSpawn()
    {
        var plan = PlaytestSpawn.FromRay(new Flat(0), null, OnMap, Eye, Vector3.Normalize(new(0, -1, 4)));   // 400 m on: z = 800 is on the map
        Assert.True(plan.Ok);
        plan = PlaytestSpawn.FromRay(new Flat(0), null, OnMap, Eye, Vector3.Normalize(new(0, -1, 8)));   // z = 1200
        Assert.False(plan.Ok);
        Assert.Contains("off the map", plan.Why);
    }

    [Fact]
    public void WaterAndCliffsAreRefused()
    {
        var plan = PlaytestSpawn.FromRay(new Flat(0, (x, z) => z > 495 && z < 520), null, OnMap, Eye, Down45);
        Assert.False(plan.Ok);
        Assert.Contains("impassable", plan.Why);
    }

    [Fact]
    public void ABuildingIsRefusedFromAboveAndFromTheSide()
    {
        var statics = new StaticWorld();
        statics.Objects.Add(Building(500, 500, 20, 15));
        // down onto its roof and into its footprint
        var plan = PlaytestSpawn.FromRay(new Flat(0), statics, OnMap, Eye, Down45);
        Assert.False(plan.Ok);
        // the camera low, the ray on its wall: it meets the wall before the ground behind it
        plan = PlaytestSpawn.FromRay(new Flat(0), statics, OnMap, new Vector3(500, 5, 300), Vector3.Normalize(new(0, -0.01f, 1)));
        Assert.False(plan.Ok);
        Assert.Contains("building", plan.Why);
        // beside it: fine
        plan = PlaytestSpawn.FromRay(new Flat(0), statics, OnMap, new Vector3(600, 100, 400), Down45);
        Assert.True(plan.Ok, plan.Why);
    }

    [Fact]
    public void ADriveOnDeckIsTheSpot()
    {
        var ground = new Deck();
        var statics = new StaticWorld();
        var plan = PlaytestSpawn.FromRay(ground, statics, OnMap, Eye, Down45);
        Assert.True(plan.Ok, plan.Why);
        Assert.Equal(8, plan.At.Y, 0.05f);   // the deck, not the ground 8 m under it
    }

    /// <summary>Ground at 0, with a deck at 8 m over z 480..520 (found within 3 m of the query height, as the structures' surfaces are).</summary>
    private sealed class Deck : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (z is > 480 and < 520 && System.Math.Abs(y - 8) <= 3 ? 8 : 0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => throw new NotSupportedException();
    }

    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void T05BridgeDeckAcceptsTheCursor()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var s = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, install.Resolve("miss16", "t05.msn")!), asm, script: false);
        var bridge = s.Statics.Objects.Single(o => o.Name == "bebridg2");
        var deck = bridge.Deck!;
        var mid = deck.Origin + deck.Rot.ToWorld((deck.Min + deck.Max) * 0.5f);
        // from 30 m straight above the deck's middle: the car stands on the deck, not on the cliff's floor
        var plan = PlaytestSpawn.FromRay(s.Ground, s.Statics, (_, _) => true, mid + new Vector3(0, 30, 0), -Vector3.UnitY);
        Assert.True(plan.Ok, plan.Why);
        Assert.True(System.Math.Abs(plan.At.Y - mid.Y) < 4f, $"{plan.At.Y} against the deck at {mid.Y}");
        // and a building of the same map refuses the cursor on its roof
        var house = s.Statics.Objects.FirstOrDefault(o => o.Polys.Count > 0 && o.Type == 3 && !o.IsStructure);
        if (house is not null)
        {
            var top = house.Polys.SelectMany(p => p.V).MaxBy(v => v.Y);
            var roof = PlaytestSpawn.FromRay(s.Ground, s.Statics, (_, _) => true, new Vector3(house.Centre.X, top.Y + 40, house.Centre.Z), -Vector3.UnitY);
            Assert.False(roof.Ok);
        }
    }
}
