using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Tests;
using I76.Game.World;
using I76.Sim.Missions;
using Xunit;
using Xunit.Abstractions;
using Corner = I76.Game.World.GroundSplit.Corner;

namespace I76.Game.Tests;

/// <summary>
/// A painted face on a slope (GroundSplit). The loader stands a placement on the ground at its origin and keeps its rotation, so a flat
/// piece on a hillside is a level plane: the original drew it so, whole, over the terrain, whatever its class id (a mission maker's
/// intersection pieces as Struct1, Struct2, Sign, Ramp, Bridge and Intersection on a hillside: all level), and a ramp's drive-on deck
/// is that plane. Painted into the terrain it lay on the slope. Now what of it stands clear of the ground is drawn level, as a mesh,
/// and the rest painted.
/// </summary>
public class GroundSplitTests(ITestOutputHelper log)
{
    private static double Area(Corner[] q)
    {
        double a = 0;
        for (int i = 1; i + 1 < q.Length; i++) a += GroundSplit.Cross(q[0].P, q[i].P, q[i + 1].P);
        return Math.Abs(a) / 2;
    }

    private static Corner C(float x, float y, float z) => new(new Vector3(x, y, z), new Vector2((x - 100) / 10, (z - 200) / 10));

    /// <summary>A 10 m square at (100..110, 200..210), at height <paramref name="y"/>, as two triangles; u, v run 0..1 across it.</summary>
    private static (Corner, Corner, Corner)[] Square(float y) =>
        [(C(100, y, 200), C(110, y, 200), C(110, y, 210)), (C(100, y, 200), C(110, y, 210), C(100, y, 210))];

    [Theory]
    [InlineData(10f)]       // on the ground
    [InlineData(10.2f)]     // a little over it
    [InlineData(4f)]        // under it
    public void AFaceOnLevelGroundIsPaintedWhole(float y)
    {
        foreach (var (a, b, c) in Square(y))
        {
            var (painted, raised) = GroundSplit.Split(a, b, c, (_, _) => 10f);
            Assert.Empty(raised);
            var only = Assert.Single(painted);
            Assert.Equal([a, b, c], only);
        }
    }

    /// <summary>
    /// Ground rising 0.3 m a metre along +x (17°), the square level at the ground's height under its middle (31.5 m): over the
    /// downhill side it stands up to 1.5 m clear, the uphill side lies up to 1.5 m under the ground.
    /// </summary>
    [Fact]
    public void ALevelFaceOnASlopeIsDrawnLevelWhereItStandsClearAndPaintedElsewhere()
    {
        const float slope = 0.3f;
        float Ground(int sx, int sz) => slope * 5 * sx;
        double raisedArea = 0, paintedArea = 0;
        foreach (var (a, b, c) in Square(31.5f))
        {
            var (painted, raised) = GroundSplit.Split(a, b, c, Ground);
            foreach (var q in raised)
            {
                raisedArea += Area(q);
                foreach (var k in q)
                {
                    Assert.Equal(31.5f, k.P.Y, 3);                                   // level, where the collider and a ramp's deck are
                    Assert.True(k.P.Y - slope * k.P.X >= GroundSplit.Near - 1e-3);   // clear of the ground
                    Assert.Equal((k.P.X - 100) / 10, k.Uv.X, 3);                     // the face's own texture coordinates
                    Assert.Equal((k.P.Z - 200) / 10, k.Uv.Y, 3);
                }
            }
            foreach (var q in painted)
            {
                paintedArea += Area(q);
                foreach (var k in q)
                {
                    Assert.True(k.P.Y - slope * k.P.X <= GroundSplit.Lift + 1e-3);
                    Assert.Equal((k.P.X - 100) / 10, k.Uv.X, 3);
                }
            }
        }
        // drawn where it stands more than Near over the ground (x < 105 - Near / slope), painted where no more than Lift (x > 105 - Lift / slope)
        Assert.Equal((5 - GroundSplit.Near / slope) * 10, raisedArea, 1);
        Assert.Equal((5 + GroundSplit.Lift / slope) * 10, paintedArea, 1);
        // together they cover it, overlapping in a band (no bare ground under the mesh's edge)
        Assert.True(raisedArea + paintedArea > 100 + 1);
    }

    /// <summary>The level mesh of a turned placement is in its model's frame: placed by the placement, its corners are where they were cut.</summary>
    [InstallFact]
    public void ARaisedModelPlacedByItsPlacementIsWhereItWasCut()
    {
        var w = SlopeWorld(out var asm, ("ixisect1", 2u, 30f));
        var faces = MissionBuilder.SplitTops(w, asm, true, split: true).Where(t => t.P.Object.Name == "ixisect1")
            .SelectMany(t => t.Raised.Select(q => new RaisedFace(t.P, t.Model, t.Part, t.Face, q))).ToList();
        Assert.NotEmpty(faces);
        var p = faces[0].P;
        var m = GroundSplit.Model(p, w, faces);
        var place = Affine.FromEngine(p.Object.Rotation, w.GroundedPosition(p));
        var back = m.Parts.SelectMany(x => x.Mesh.Vertices.Select(v => place.Apply(x.ToModel.Apply(v)))).ToList();
        var cut = faces.SelectMany(f => f.Corners.Select(c => c.P)).ToList();
        Assert.Equal(cut.Count, back.Count);
        for (int i = 0; i < cut.Count; i++) Assert.True(Vector3.Distance(cut[i], back[i]) < 1e-3f, $"{cut[i]} placed back at {back[i]}");
    }

    /// <summary>T13 with intersection pieces added on a hillside east of the junction (ground rising 0.3 a metre).</summary>
    private static MissionWorld SlopeWorld(out ModelAssembler asm, params (string Name, uint Type, float Yaw)[] pieces)
    {
        asm = new ModelAssembler(TestInstall.Vfs);
        var w = new MissionWorld(TestInstall.Vfs, asm, TestInstall.Install.Resolve("miss16", "t13.msn")!);
        for (int i = 0; i < pieces.Length; i++)
        {
            // along the slope's contour, 24 m apart
            var at = Spot + new Vector2(-0.394f, -0.919f) * (24 * i);
            w.Placements.Add(Place(asm, pieces[i].Name, pieces[i].Type, at.X, at.Y, pieces[i].Yaw));
        }
        return w;
    }

    private static readonly Vector2 Spot = new(5518.6f, 45925.2f);

    private static Placement Place(ModelAssembler asm, string name, uint type, float x, float z, float yawDeg)
    {
        var raw = new byte[0x78];
        BitConverter.GetBytes(type).CopyTo(raw, 0x5c);
        float a = yawDeg * MathF.PI / 180;
        float[] rot = [MathF.Cos(a), 0, -MathF.Sin(a), 0, 1, 0, MathF.Sin(a), 0, MathF.Cos(a)];
        return new Placement(new PlacedObject(name, 0, rot, new Vector3(x, 0, z), raw), asm.ResolveDefinition(name));
    }

    /// <summary>
    /// The mission maker's test: the same piece on a hillside as Struct1, Struct2, Sign, Ramp, Bridge and Intersection. Every one
    /// is drawn level over the downhill side, at its placement's height; a Ramp's drawn deck is where the cars stand on it (the
    /// sim's ground query), a Bridge's carries none (the original registers a bridge's drive-on faces only from its deck nodes).
    /// </summary>
    [InstallFact]
    public void IntersectionPiecesOnAHillsideAreLevelWhateverTheirClass()
    {
        uint[] types = [2, 3, 4, 0xb, 0xc, 0x50];
        var w = SlopeWorld(out var asm, types.Select(t => ("ixisect1", t, 0f)).ToArray());
        var added = w.Placements.TakeLast(types.Length).ToHashSet();
        var tops = MissionBuilder.SplitTops(w, asm, true, split: true).Where(t => added.Contains(t.P)).ToList();
        var setup = MissionSetup.Build(TestInstall.Vfs, w, asm, script: false, rand: new I76.Sim.Rng.CrtRand(1));
        foreach (uint type in types)
        {
            var mine = tops.Where(t => t.P.Object.ObjectType == type).ToList();
            var raised = mine.SelectMany(t => t.Raised).ToList();
            Assert.True(raised.Count > 0, $"class 0x{type:x}: nothing drawn level");
            // level: every corner at one height, the piece's top where the placement stands it (its origin on the ground)
            var ys = raised.SelectMany(q => q).Select(k => k.P.Y).ToList();
            float y = ys.Average(), ground = w.GroundedPosition(mine[0].P).Y;
            Assert.True(ys.Max() - ys.Min() < 0.01f, $"class 0x{type:x}: drawn between {ys.Min():0.00} and {ys.Max():0.00}");
            Assert.True(Math.Abs(y - ground) < 0.2f, $"class 0x{type:x}: drawn at {y:0.00}, its origin on the ground at {ground:0.00}");
            double up = raised.Sum(Area), down = mine.SelectMany(t => t.Painted).Sum(Area);
            log.WriteLine($"class 0x{type:x}: level {up:0.0} m², painted {down:0.0} m², at {y:0.00} (origin {ground:0.00})");
            // the cars' ground over the drawn deck, from just above it
            var mid = raised.OrderByDescending(Area).First();
            var c = mid.Aggregate(Vector3.Zero, (s, k) => s + k.P) / mid.Length;
            var (h, _) = setup.Ground.Query(c.X, c.Y + 0.5, c.Z);
            if (type == 0xb) Assert.Equal(c.Y, h, 2);
            else Assert.True(h < c.Y - GroundSplit.Near + 1e-3, $"class 0x{type:x}: a car stands at {h:0.00} over a deck drawn at {c.Y:0.00}");
        }
    }

    /// <summary>
    /// The stock maps (both games): what of the painted faces stands clear of the ground and is now drawn level is little, the corners
    /// of lots and pieces over dips; everything else is painted as it was.
    /// </summary>
    [InstallFact]
    public void OnTheStockMapsAlmostEverythingStaysPainted()
    {
        var sets = new List<(string, Formats.Vfs.AssetVfs, Formats.Vfs.GameInstall)> { ("", TestInstall.Vfs, TestInstall.Install) };
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } nd && Directory.Exists(nd))
        {
            var ni = new Formats.Vfs.GameInstall(nd);
            sets.Add(("nitro ", new Formats.Vfs.AssetVfs(ni), ni));
        }
        double all = 0, level = 0;
        int placements = 0, maps = 0;
        foreach (var (tag, vfs, install) in sets)
            foreach (var f in Directory.EnumerateFiles(install.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
            {
                var asm = new ModelAssembler(vfs);
                var w = new MissionWorld(vfs, asm, f);
                bool groundList = RoadPieceObjects.GroundListDrawn(w.Mission);
                var tops = MissionBuilder.SplitTops(w, asm, groundList, split: true).ToList();
                double mapAll = MissionBuilder.SplitTops(w, asm, groundList, split: false).Sum(t => t.Painted.Sum(Area));
                double mapLevel = tops.Sum(t => t.Raised.Sum(Area));
                int n = tops.Where(t => t.Raised.Count > 0).Select(t => t.P).Distinct().Count();
                all += mapAll; level += mapLevel; placements += n; maps++;
                if (n > 0) log.WriteLine($"{tag}{Path.GetFileNameWithoutExtension(f)}: {n} placements drawn partly level, {mapLevel:0} of {mapAll:0} m²");
            }
        log.WriteLine($"{maps} maps: {placements} placements, {level:0} of {all:0} m² ({level / all:P2}) drawn level");
        Assert.True(level / all < 0.02, $"{level / all:P2} of the painted area drawn level");
    }
}
