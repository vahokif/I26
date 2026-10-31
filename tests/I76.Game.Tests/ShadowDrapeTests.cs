using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Tests;
using I76.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The drop shadow of a big static caster (DropShadows.Draped, both looks) lands on the terrain under it, vertex by vertex. The 1997
/// look had one plane through the ground under the bridge's centre for the whole 100 m node; Seminole's truss bridge (s04, bebridg1)
/// spans a canyon, the plane under its centre is the canyon wall, and the trusses' shadow stood up on it as a black slab in the
/// portal at the bridge's far end.
/// </summary>
public class ShadowDrapeTests(ITestOutputHelper log)
{
    private static readonly Vector3 ToLight = Vector3.Normalize(new(1, 1, 0));   // S04's sun, as the game's i76_light_dir

    [InstallFact]
    public void SeminolesBridgeShadowLiesOnTheGroundNotUpTheCanyonWall()
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, TestInstall.Install.FindMission("s04")!);
        Func<float, float, float> ground = (x, z) => (float)w.BilinearHeight(x, z);
        var bridges = w.Placements.Where(p => p.Definition is not null && p.Definition.StartsWith("bebridg1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(bridges);
        foreach (var p in bridges)
        {
            var model = asm.Assemble(p.Definition!)!;
            var place = Affine.FromEngine(p.Object.Rotation, w.GroundedPosition(p));
            var verts = model.Parts.SelectMany(part => part.Mesh.Vertices.Select(v => place.Apply(part.ToModel.Apply(v)))).ToList();
            var centre = (verts.Aggregate(Vector3.Min) + verts.Aggregate(Vector3.Max)) / 2;
            // why the drape: the one plane under the centre is far from level (the canyon wall)
            var plane = ShadowDrape.Plane(centre, ToLight, (x, _, z) => ground(x, z))!.Value;
            log.WriteLine($"{p.Definition} at {p.Object.Position.X:0},{p.Object.Position.Z:0}: plane under its centre has normal.y {plane.Y:0.00}");
            Assert.True(plane.Y < 0.7f, "the plane under the bridge's centre is no longer steep: this test no longer shows what the drape is for");

            int lit = 0; float worst = 0;
            foreach (var v in verts)
            {
                var (point, height) = ShadowDrape.Of(v, ToLight, ground);
                if (height <= 0) continue;
                lit++;
                // (the ray steps 1 m, so on a cliff face the interpolated hit is off by a metre or two) the shadow point is the terrain's, not a point in the air or under it, and the light's own direction away from the vertex
                worst = MathF.Max(worst, MathF.Abs(point.Y - ground(point.X, point.Z)));
                Assert.True(point.Y <= v.Y + 0.01f, $"shadow point {point} above its vertex {v}");
            }
            log.WriteLine($"  {lit} of {verts.Count} vertices above the ground; the shadow points are within {worst:0.000} m of the terrain");
            Assert.True(lit > 0);
            Assert.True(worst < 3f, $"a shadow point lies {worst:0.0} m off the terrain");
        }
    }

    /// <summary>
    /// A car on the bridge's deck casts onto the deck, not onto the terrain far below it: the plane from the terrain under a car on
    /// Seminole's bridge was the canyon wall, and the car's shadow fanned from it up into the sky between the trusses.
    /// </summary>
    [InstallFact]
    public void ACarOnTheDeckHasTheDecksPlane()
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, TestInstall.Install.FindMission("s04")!);
        var setup = I76.Sim.Missions.MissionSetup.Build(vfs, w, asm, "vhelif.vcf", script: false);
        var ground = setup.World.Ground;
        var p = w.Placements.First(p => p.Definition is not null && p.Definition.StartsWith("bebridg1", StringComparison.OrdinalIgnoreCase));
        var place = Affine.FromEngine(p.Object.Rotation, w.GroundedPosition(p));
        foreach (float along in new[] { 25f, 50f, 75f })
        {
            var onDeck = place.Apply(new Vector3(5, 1, along));
            float deck = ground.Query(onDeck.X, onDeck.Y + 1, onDeck.Z).Height;
            Assert.True(deck > ground.BareHeight(onDeck.X, onDeck.Z) + 0.5f, "the query finds no deck here");
            var centre = new Vector3(onDeck.X, deck + 0.8f, onDeck.Z);   // a car's middle
            var plane = ShadowDrape.Plane(centre, ToLight, (x, y, z) => ground.Query(x, y, z).Height)!.Value;
            float off = MathF.Abs(plane.X * centre.X + plane.Y * (deck) + plane.Z * centre.Z - plane.W);
            log.WriteLine($"deck at {along} m: height {deck:0.00}, plane normal.y {plane.Y:0.00}, deck {off:0.000} m off it");
            var bare = ShadowDrape.Plane(centre, ToLight, (x, _, z) => ground.BareHeight(x, z))!.Value;
            log.WriteLine($"  the terrain's plane there: normal.y {bare.Y:0.00}");
            Assert.True(plane.Y > 0.9f, $"the plane under a car on the deck stands at {plane.Y:0.00}");
            Assert.True(off < 0.5f, $"the plane lies {off:0.0} m from the deck");
            // beside the deck the surface drops to the canyon: the plane is held to a 45 degree slope whatever it samples
            var edge = new Vector3(onDeck.X, deck + 0.8f, onDeck.Z);
            var steep = ShadowDrape.Plane(edge, ToLight, (x, y, z) => Vector3.Distance(new(x, 0, z), new(edge.X, 0, edge.Z)) < 0.5f ? deck : deck - 80)!.Value;
            Assert.True(steep.Y >= 0.7f, $"a plane over a drop stands at {steep.Y:0.00}");
        }
    }

    [Fact]
    public void ACliffsRimAndFloorAreNotConnectedButASlopeIs()
    {
        // a plateau at 100 m for x < 0, a canyon floor at 10 m for x > 5
        Func<float, float, float> canyon = (x, _) => x < 0 ? 100f : x > 5 ? 10f : 100f - 90f * x / 5f;
        Assert.False(ShadowDrape.Connected(new(-3, 100, 0), new(12, 10, 0), canyon), "a sheet across the rim, over the air");
        Assert.True(ShadowDrape.Connected(new(8, 10, 0), new(30, 10, 5), canyon), "along the floor");
        Func<float, float, float> hill = (x, _) => 0.5f * x;
        Assert.True(ShadowDrape.Connected(new(0, 0, 0), new(20, 10, 0), hill), "up a steady slope");
    }

    [Fact]
    public void SplitCutsEveryEdgeShortAndKeepsTheArea()
    {
        var up = Vector3.UnitY;
        var tris = ShadowDrape.Split((new(0, 0, 0), up), (new(40, 0, 0), up), (new(0, 0, 10), up), 4f);
        Assert.True(tris.Count > 20);
        float area = 0;
        foreach (var t in tris)
        {
            for (int k = 0; k < 3; k++) Assert.True(Vector3.Distance(t[k].P, t[(k + 1) % 3].P) <= 4.0001f);
            area += Vector3.Cross(t[1].P - t[0].P, t[2].P - t[0].P).Length() / 2;
        }
        Assert.Equal(200f, area, 1);
    }
}
