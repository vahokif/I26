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

    // a rift like Rift's (N05): plateaus at 70 m, a floor at 0 between x = 5 and x = 30, the walls 5 m across
    private static float Rift(float x, float _) => x <= 0 || x >= 35 ? 70f : x < 5 ? 70f - 14f * x : x > 30 ? 14f * (x - 30) : 0f;
    private static readonly Vector3 EveningSun = new(0.9396926f, 0.34202015f, 0);   // Lighting.ForHour(18), N05's: the rays run toward -x

    /// <summary>
    /// A ramp's face rising from its foot on the east rim: the light passes over the rim and the face's shadow falls on the far wall, 40 m
    /// away and 10 m down. Drawn from the foot (just under the ground, so its edge was not tested) to the far wall, it hung across the
    /// canyon as a dark sheet (#245); it is left out. The same face on level ground is kept.
    /// </summary>
    [Fact]
    public void ARampsFootOnARimHangsNoSheetAcrossTheCanyon()
    {
        Vector3 foot = new(40, 69.9f, 0), top1 = new(40, 74, 1), top2 = new(40, 74, -1);
        var far = ShadowDrape.Of(top1, EveningSun, Rift);
        Assert.True(far.Point.X < 5, $"the top's shadow lands at x {far.Point.X:0.0}, not on the far wall");
        Assert.True(ShadowDrape.Of(foot, EveningSun, Rift).Height < 0, "the foot is not under the ground");
        Assert.Null(ShadowDrape.Drape(foot, top1, top2, EveningSun, Rift));
        Assert.NotNull(ShadowDrape.Drape(foot, top1, top2, EveningSun, (_, _) => 70f));
        // both tops alone land side by side on the wall: kept
        Assert.NotNull(ShadowDrape.Drape(top1, top2, new Vector3(40, 74.5f, 0), EveningSun, Rift));
    }

    /// <summary>
    /// A face coming out of a wall (N05's bunkers, dug into the canyon's side) has its buried corner under the plateau, 20 m below its
    /// surface: its shadow starts where the face leaves the wall, on the floor, and is kept.
    /// </summary>
    [Fact]
    public void AFaceOutOfAWallKeepsItsShadowOnTheFloor()
    {
        static float Dug(float x, float _) => x <= 0 ? 25f : x < 1 ? 25f - 25f * x : 0f;
        var toLight = new Vector3(-EveningSun.X, EveningSun.Y, 0);   // the rays run away from the wall, toward +x
        Vector3 inWall = new(-3, 2, 0), out1 = new(3, 4, 1), out2 = new(3, 4, -1);
        Assert.True(ShadowDrape.Of(inWall, toLight, Dug).Height < -20);
        Assert.NotNull(ShadowDrape.Drape(inWall, out1, out2, toLight, Dug));
    }

    /// <summary>
    /// A corner whose ray meets no ground within <see cref="ShadowDrape.Ray"/> falls back to a point at the height of the ground under it:
    /// over a flat canyon floor that is the floor (kept); a bridge's end on the rim at a low sun put it at the rim's height in the air over
    /// the canyon (T15's bridge, 46 m up), and the triangle is left out.
    /// </summary>
    [Fact]
    public void AFallbackPointInTheAirIsLeftOut()
    {
        var lowSun = Vector3.Normalize(new Vector3(0.966f, 0.259f, 0));
        static float Rim(float x, float _) => x >= 0 ? 50f : x > -2 ? 50f + 25f * x : 0f;
        // a sliver, as T15's were: its points less than a metre apart, so no edge of it is long enough to test
        Vector3 a = new(10, 60, 0), b = new(10, 60, 0.5f), c = new(10, 60.2f, 0);
        var fallback = ShadowDrape.Of(a, lowSun, Rim);
        Assert.True(fallback.Point.X < -2 && fallback.Point.Y > 40, $"the fallback lands at {fallback.Point}: not in the air over the canyon");
        Assert.Null(ShadowDrape.Drape(a, b, c, lowSun, Rim));
        // a deck 60 m over a flat floor: the same fallback lies on the floor
        Assert.NotNull(ShadowDrape.Drape(a, b, c, lowSun, (_, _) => 0f));
    }

    /// <summary>
    /// The ramps on Rift's rims (N05, aaramp3) at its evening sun, cut and draped as DropShadows does: every point of their shadows that the
    /// shaders draw lies on the terrain. The east ramp's foot on the rim hung its shadow 67 m above the canyon's floor (#245).
    /// </summary>
    [InstallFact]
    public void RiftsRampsShadowsLieOnTheGround()
    {
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } nd || !Directory.Exists(nd)) return;
        var install = new I76.Formats.Vfs.GameInstall(nd);
        var vfs = new I76.Formats.Vfs.AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission("n05")!);
        Func<float, float, float> ground = (x, z) => (float)w.BilinearHeight(x, z);
        var light = I76.Formats.Mission.Lighting.ForHour(I76.Formats.Mission.WorldSettings.Read(w.Mission)!.Hour).Direction;
        var ramps = w.Placements.Where(p => p.Definition is not null && p.Definition.StartsWith("aaramp3", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, ramps.Count);
        foreach (var p in ramps)
        {
            var model = asm.Assemble(p.Definition!)!;
            var place = Affine.FromEngine(p.Object.Rotation, w.GroundedPosition(p));
            int drawn = 0; float worst = 0;
            foreach (var part in model.Parts)
                foreach (var f in part.Mesh.Faces)
                    for (int k = 1; k + 1 < f.Vertices.Length; k++)
                    {
                        (Vector3, Vector3) C(int i)
                        {
                            var v = f.Vertices[i];
                            var n = (uint)v.NormalIndex < part.Mesh.Normals.Length ? part.Mesh.Normals[v.NormalIndex] : new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z);
                            return (place.Apply(part.ToModel.Apply(part.Mesh.Vertices[v.Index])), Vector3.Normalize(place.Rotate(-part.ToModel.Rotate(n))));
                        }
                        foreach (var t in ShadowDrape.Split(C(0), C(k + 1), C(k), ShadowDrape.MaxEdge))
                        {
                            if (ShadowDrape.Drape(t[0].P, t[1].P, t[2].P, light, ground) is not { } hits) continue;
                            // the shaders' points across the triangle: drawn where the height is above the clip and the face is turned to the light
                            for (int a = 0; a <= 4; a++)
                                for (int b = 0; a + b <= 4; b++)
                                {
                                    float u = a / 4f, v = b / 4f, s = 1 - u - v;
                                    if (hits[0].H * s + hits[1].H * u + hits[2].H * v < 0.05f || Vector3.Dot(t[0].N * s + t[1].N * u + t[2].N * v, light) < 0) continue;
                                    var q = hits[0].P * s + hits[1].P * u + hits[2].P * v;
                                    drawn++;
                                    worst = MathF.Max(worst, q.Y - ground(q.X, q.Z));
                                }
                        }
                    }
            log.WriteLine($"{p.Definition} at {p.Object.Position.X:0},{p.Object.Position.Z:0}: {drawn} points drawn, the highest {worst:0.00} m above the terrain");
            Assert.True(drawn > 1000, "the ramp casts next to nothing");
            Assert.True(worst < 2f, $"a point of the ramp's shadow hangs {worst:0.0} m above the terrain");
        }
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
