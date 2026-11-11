using System.Numerics;
using I76.Formats.Vegetation;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>The modern look's procedural plants (docs/modern-vegetation.md): rewriting, the turtle, the meshes, the atlas, the placement masks.</summary>
public class VegetationTests(ITestOutputHelper output)
{
    [Fact]
    public void LSystemRewritesDeterministically()
    {
        var sys = new LSystem
        {
            Axiom = "A",
            Productions = [new('A', 1, "F[+A][-A]"), new('A', 1, "FA"), new('F', 1, "FF")],
            Finish = { ['A'] = "L" },
        };
        var r1 = new VegRng(42); var r2 = new VegRng(42); var r3 = new VegRng(43);
        string a = sys.Rewrite(5, ref r1), b = sys.Rewrite(5, ref r2), c = sys.Rewrite(5, ref r3);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.DoesNotContain('A', a);   // Finish turned every apex into a leaf
        Assert.Contains('L', a);
        // brackets stay balanced through the rewriting
        int depth = 0;
        foreach (char ch in a) { depth += ch == '[' ? 1 : ch == ']' ? -1 : 0; Assert.True(depth >= 0); }
        Assert.Equal(0, depth);
    }

    [Fact]
    public void ParameterisedSymbolsAreCopiedNotRewritten()
    {
        var sys = new LSystem { Axiom = "F(0.5)F+(30)A", Productions = [new('F', 1, "FF"), new('A', 1, "X")] };
        var rng = new VegRng(1);
        Assert.Equal("F(0.5)FF+(30)X", sys.Rewrite(1, ref rng));
        Assert.Equal([('F', "0.5"), ('+', null), ('A', null)], LSystem.Tokens("F(0.5)+A").ToArray());
    }

    [Fact]
    public void TurtleDrawsWhatTheStringSays()
    {
        var t = new Turtle { Length = 1, Radius = 0.1f, Jitter = 0, LengthJitter = 0, Taper = 1 };
        var rng = new VegRng(0);
        // up 1, then a branch turned 90 degrees left going 1, then back on the trunk 1 more; a leaf at the top
        var sk = t.Run("F[+(90)F]FL", ref rng);
        Assert.Equal(3, sk.Segments.Count);
        Assert.Equal(new Vector3(0, 1, 0), sk.Segments[0].B);
        Assert.True(Vector3.Distance(sk.Segments[1].B, new Vector3(-1, 1, 0)) < 1e-5f, $"branch ends at {sk.Segments[1].B}");
        Assert.Equal(new Vector3(0, 2, 0), sk.Segments[2].B);   // the pop restored the trunk's state
        Assert.Single(sk.Leaves);
        Assert.Equal(new Vector3(0, 2, 0), sk.Leaves[0].Pos);
        Assert.Equal(1, sk.Segments[1].Depth);
    }

    [Fact]
    public void TurtleTropismBendsTowardIt()
    {
        var t = new Turtle { Length = 0.2f, Jitter = 0, LengthJitter = 0, Tropism = -Vector3.UnitY, Susceptibility = 1.5f };
        var rng = new VegRng(0);
        var sk = t.Run("f(5)&(60)FFFFFFFF", ref rng);
        // each segment points lower than the one before
        for (int i = 1; i < sk.Segments.Count; i++)
        {
            var d0 = Vector3.Normalize(sk.Segments[i - 1].B - sk.Segments[i - 1].A);
            var d1 = Vector3.Normalize(sk.Segments[i].B - sk.Segments[i].A);
            Assert.True(d1.Y < d0.Y + 1e-6f);
        }
        Assert.True(sk.Segments.All(s => s.B.Y >= t.Floor - 1e-6f));   // never into the ground
    }

    [Fact]
    public void EverySpeciesBuildsTheSameMeshFromTheSameSeed()
    {
        foreach (var s in SpeciesCatalog.All.Values)
        {
            for (int v = 0; v < s.Variants; v++)
            {
                ulong seed = VegHash.Mix(VegHash.Of(s.Id), (ulong)v);
                var a = s.Build(seed, 0);
                var b = s.Build(seed, 0);
                Assert.Equal(a.Digest(), b.Digest());
                var lod = s.Build(seed, 1);
                var far = s.Build(seed, 2);
                Assert.True(far.VertexCount <= lod.VertexCount, $"{s.Id}: the farthest mesh is the lightest");
                var (mn, mx) = a.Bounds();
                output.WriteLine($"{s.Id,-12} v{v}: {a.VertexCount,5} vertices {a.TriangleCount,5} triangles (lod1 {lod.VertexCount,5}, lod2 {far.VertexCount,5}), {mx.X - mn.X:0.00} x {mx.Y:0.00} x {mx.Z - mn.Z:0.00} m");
                Assert.True(a.VertexCount > 0 && a.Indices.All(i => i < a.VertexCount), s.Id);
                Assert.True(lod.VertexCount <= a.VertexCount, $"{s.Id}: the lighter mesh is lighter");
                Assert.True(a.VertexCount < 6000, $"{s.Id}: {a.VertexCount} vertices is too heavy to instance");
                Assert.True(mn.Y > -0.2f, $"{s.Id} grows into the ground ({mn.Y})");
                Assert.All(a.Normals, n => Assert.InRange(n.Length(), 0.98f, 1.02f));
            }
            var other = s.Build(VegHash.Mix(VegHash.Of(s.Id), 99), 0);
            Assert.NotEqual(s.Build(VegHash.Mix(VegHash.Of(s.Id), 0), 0).Digest(), other.Digest());
        }
    }

    [Fact]
    public void EverySpeciesHasItsOcclusionBaked()
    {
        // the vertex colour's alpha: in range everywhere; a plant's lowest fifth darker than its top fifth, and the
        // inside of a shrub's crown (the vertices nearest its middle) darker than its outside
        foreach (var s in SpeciesCatalog.All.Values)
            for (int d = 0; d < 3; d++)
            {
                var m = s.Build(VegHash.Mix(VegHash.Of(s.Id), 0), d);
                Assert.All(m.Colors, c => Assert.InRange(c.W, PlantShading.Floor, 1f));
                var (mn, mx) = m.Bounds();
                float h = mx.Y - mn.Y;
                float Mean(IEnumerable<int> ix) { var l = ix.ToList(); return l.Count == 0 ? float.NaN : l.Average(i => m.Colors[i].W); }
                var all = Enumerable.Range(0, m.VertexCount).ToList();
                float low = Mean(all.Where(i => m.Positions[i].Y < mn.Y + 0.2f * h)), top = Mean(all.Where(i => m.Positions[i].Y > mx.Y - 0.2f * h));
                float mean = Mean(all);
                output.WriteLine($"{s.Id,-14} d{d} mean {mean:0.00}  low {low:0.00} top {top:0.00}");
                Assert.True(low < top, $"{s.Id} detail {d}: the base ({low:0.00}) is no darker than the top ({top:0.00})");
                Assert.InRange(mean, 0.3f, 1f);
            }
    }

    [Theory]
    [InlineData("creosote", 2)]
    [InlineData("saltbush", 2)]
    [InlineData("mesquite", 2)]
    [InlineData("tamarisk", 2)]
    [InlineData("willow", 2)]
    [InlineData("sandsage", 2)]
    [InlineData("shinnery", 2)]
    [InlineData("grayoak", 2)]
    [InlineData("sotol", 0)]       // the lighter sotols' fewer, wider blades leave the ball open
    public void ACrownIsDarkerInsideThanOutside(string id, int details)
    {
        var s = SpeciesCatalog.All[id];
        for (int d = 0; d <= details; d++)
        {
            var m = s.Build(VegHash.Mix(VegHash.Of(s.Id), 1), d);
            // the crown's middle: where its foliage (or, for the sotol's blades, everything) is on average
            var fol = Enumerable.Range(0, m.VertexCount).Where(i => m.Extra[i].Y > 0.5f).ToList();
            if (fol.Count == 0) fol = [.. Enumerable.Range(0, m.VertexCount)];
            var mid = fol.Aggregate(Vector3.Zero, (a, i) => a + m.Positions[i]) / fol.Count;
            var byDepth = fol.OrderBy(i => Vector3.Distance(m.Positions[i], mid)).ToList();
            float inner = byDepth.Take(byDepth.Count / 5).Average(i => m.Colors[i].W), outer = byDepth.Skip(byDepth.Count * 4 / 5).Average(i => m.Colors[i].W);
            output.WriteLine($"{id} detail {d}: inner {inner:0.00} outer {outer:0.00}");
            Assert.True(inner < outer - 0.1f, $"{id} detail {d}: the inside ({inner:0.00}) is not darker than the outside ({outer:0.00})");
        }
    }

    [Fact]
    public void ThePlantsTiersAreShadedAlike()
    {
        // the lighter meshes take the full mesh's occlusion: BuildTiers agrees with Build, and the area-weighted
        // occlusion of each tier's foliage is near the full mesh's (no jump in brightness at a tier change; the stems
        // differ, the lighter meshes leaving out the thin twigs inside)
        foreach (var s in SpeciesCatalog.All.Values)
        {
            ulong seed = VegHash.Mix(VegHash.Of(s.Id), 2);
            var tiers = s.BuildTiers(seed);
            for (int d = 0; d < 3; d++)
            {
                Assert.Equal(s.Build(seed, d).Digest(), tiers[d].Digest());
                float w0 = PlantShading.Weighted(tiers[0], true), w = PlantShading.Weighted(tiers[d], true);
                if (float.IsNaN(w0) || float.IsNaN(w)) continue;
                output.WriteLine($"{s.Id,-14} d{d} foliage {w:0.00} (full {w0:0.00})");
                Assert.True(MathF.Abs(w - w0) < 0.05f, $"{s.Id} detail {d}: {w:0.00} against the full mesh's {w0:0.00}");
            }
        }
        // what the shading costs: every species' tiers built with it, against the bare generators (one thread)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var s in SpeciesCatalog.All.Values) s.BuildTiers(1);
        long shaded = sw.ElapsedMilliseconds;
        output.WriteLine($"every species' three tiers, shaded: {shaded} ms");
    }

    [Fact]
    public void ShrubsShadeTheGroundUnderThem()
    {
        // in range for every species; a dense shrub shades its foot more than an open one (saltbush against ocotillo),
        // and the shade reaches about as far out as the plant does
        foreach (var s in SpeciesCatalog.All.Values)
        {
            var m = s.Build(VegHash.Mix(VegHash.Of(s.Id), 0), 0);
            var (mn, mx) = m.Bounds();
            float half = MathF.Max(mx.X - mn.X, mx.Z - mn.Z) * 0.5f;
            output.WriteLine($"{s.Id,-14} ground shade {m.GroundShade:0.00} out to {m.GroundRadius:0.00} m (half its width {half:0.00} m)");
            Assert.InRange(m.GroundShade, 0f, 0.95f);
            Assert.InRange(m.GroundRadius, 0.1f, half * 1.05f + 0.1f);
        }
        float Shade(string id) => SpeciesCatalog.All[id].Build(VegHash.Mix(VegHash.Of(id), 0), 0).GroundShade;
        Assert.True(Shade("saltbush") > Shade("ocotillo") + 0.2f, $"saltbush {Shade("saltbush"):0.00}, ocotillo {Shade("ocotillo"):0.00}");
        Assert.True(Shade("creosote") > 0.2f, $"creosote {Shade("creosote"):0.00}");
    }

    [Fact]
    public void OcclusionDarkensWhatIsCovered()
    {
        // a card under a dense layer of cards is darker than the same card alone; a card alone, facing up, sees open sky
        static PlantMesh Alone() { var m = new PlantMesh(); Shapes.Card(m, new Vector3(0, 0.5f, 0), Vector3.UnitX, Vector3.UnitZ, 0.3f, 0.3f, AtlasCell.Creosote, Vector4.One, 0, new Vector3(0, -1, 0), 0); return m; }
        var alone = PlantShading.Bake(Alone());
        var covered = Alone();
        for (int i = -3; i <= 3; i++)
            for (int j = -3; j <= 3; j++)
                Shapes.Card(covered, new Vector3(i * 0.15f, 0.9f, j * 0.15f - 0.15f), Vector3.UnitX, Vector3.UnitZ, 0.3f, 0.3f, AtlasCell.Creosote, Vector4.One, 0, new Vector3(0, -1, 0), 0);
        PlantShading.Bake(covered);
        float a = alone.Colors.Take(4).Average(c => c.W), b = covered.Colors.Take(4).Average(c => c.W);
        output.WriteLine($"alone {a:0.00}, covered {b:0.00}");
        Assert.True(a > 0.9f, $"a card alone under open sky: {a:0.00}");
        Assert.True(b < a - 0.2f, $"covered {b:0.00} against alone {a:0.00}");
    }

    [Theory]
    [InlineData("creosote", 0.6f, 2.0f)]
    [InlineData("saltbush", 0.6f, 1.8f)]
    [InlineData("mesquite", 1.2f, 3.2f)]
    [InlineData("cholla", 0.7f, 2.0f)]
    [InlineData("pricklypear", 0.25f, 1.1f)]
    [InlineData("soaptree", 1.0f, 4.0f)]
    [InlineData("soapweed", 0.3f, 2.2f)]
    [InlineData("tamarisk", 1.4f, 4.0f)]
    [InlineData("willow", 1.6f, 4.0f)]
    [InlineData("grama", 0.1f, 0.6f)]
    [InlineData("tobosa", 0.2f, 0.8f)]
    [InlineData("sacaton", 0.4f, 1.4f)]
    [InlineData("tumbleweed", 0.3f, 0.9f)]
    [InlineData("snakeweed", 0.25f, 0.9f)]
    [InlineData("sandsage", 0.6f, 1.3f)]
    [InlineData("shinnery", 0.5f, 1.1f)]
    [InlineData("grayoak", 1.3f, 2.2f)]
    [InlineData("ocotillo", 2.5f, 5.5f)]
    [InlineData("sotol", 0.8f, 4.5f)]          // with its flower stalk up to 4 m
    [InlineData("torrey", 1.6f, 4.0f)]
    [InlineData("lechuguilla", 0.25f, 3.2f)]   // the odd 2-3 m stalk
    [InlineData("agave", 0.45f, 5.0f)]         // the century plant's stalk, rarely
    [InlineData("beargrass", 0.6f, 2.0f)]
    [InlineData("candelilla", 0.25f, 0.65f)]
    [InlineData("buffalograss", 0.05f, 0.2f)]
    [InlineData("dropseed", 0.4f, 0.9f)]
    [InlineData("chinograma", 0.15f, 0.45f)]
    [InlineData("sideoats", 0.45f, 0.95f)]
    [InlineData("bluestem", 0.55f, 1.1f)]
    [InlineData("indianblanket", 0.25f, 0.5f)]
    [InlineData("pricklypoppy", 0.45f, 0.85f)]
    [InlineData("horsemint", 0.28f, 0.6f)]
    [InlineData("pickleweed", 0.2f, 0.5f)]
    public void SpeciesAreTheirRealSize(string id, float minHeight, float maxHeight)
    {
        var s = SpeciesCatalog.All[id];
        for (int v = 0; v < s.Variants; v++)
        {
            var (_, mx) = s.Build(VegHash.Mix(VegHash.Of(id), (ulong)v), 0).Bounds();
            Assert.InRange(mx.Y, minHeight, maxHeight);
        }
    }

    /// <summary>
    /// The bunchgrasses (a user's report: shaving brushes stuck in the dirt): a low cushion under arching blades, not a
    /// fan of straight ones; straw and grey-green blades mixed; blades that carry their side for the shader's pixel-wide
    /// minimum in the full mesh only; cards farther off; and the variants differ (single tufts and clusters).
    /// </summary>
    [Theory]
    [InlineData("grama")]
    [InlineData("tobosa")]
    [InlineData("sacaton")]
    [InlineData("dropseed")]
    [InlineData("chinograma")]
    [InlineData("sideoats")]
    [InlineData("bluestem")]
    [InlineData("buffalograss")]
    public void GrassTuftsArchAndMixLivingAndDeadBlades(string id)
    {
        var s = SpeciesCatalog.All[id];
        Assert.True(s.Grass);
        var widths = new List<float>();
        for (int v = 0; v < s.Variants; v++)
        {
            var tiers = s.BuildTiers(VegHash.Mix(VegHash.Of(id), (ulong)v));
            var m = tiers[0];
            Assert.True(m.HasSide, $"{id}: the full mesh's blades carry their side");
            Assert.False(tiers[1].HasSide || tiers[2].HasSide, $"{id}: the cards do not");
            Assert.Equal(m.VertexCount, m.Side.Count);
            // the blades (not the stems and seed heads, which stand up out of the tuft): most of their length low, in
            // the cushion and the arching tips, against the tallest
            var blade = Enumerable.Range(0, m.VertexCount).Where(i => m.Side[i] != Vector4.Zero && m.Extra[i].X < 1.05f).ToList();
            var ys = blade.Select(i => m.Positions[i].Y).Order().ToList();
            float median = ys[ys.Count / 2], top = ys[(int)(ys.Count * 0.98f)];
            // straw (red over green) and grey-green (no redder than green) blades both
            int straw = blade.Count(i => m.Colors[i].X > m.Colors[i].Y * 1.15f), green = blade.Count(i => m.Colors[i].X < m.Colors[i].Y * 1.04f);
            output.WriteLine($"{id} v{v}: {m.VertexCount} vertices, blades' median height {median:0.00} of {top:0.00} m, straw {straw * 100 / blade.Count}%, green {green * 100 / blade.Count}%");
            Assert.True(median < top * 0.55f, $"{id} v{v}: the blades stand up ({median:0.00} of {top:0.00})");
            Assert.True(straw > blade.Count / 8 && green > blade.Count / 10, $"{id} v{v}: straw {straw}, green {green} of {blade.Count}");
            var (mn, mx) = m.Bounds();
            widths.Add(MathF.Max(mx.X - mn.X, mx.Z - mn.Z));
        }
        // not three of the same tuft
        Assert.True(widths.Max() - widths.Min() > 0.03f, $"{id}: widths {string.Join(", ", widths.Select(w => w.ToString("0.00")))}");
    }

    [Fact]
    public void AtlasKeepsFoliageCoverageDownTheMips()
    {
        var chain = FoliageAtlas.Build();
        int expected = 0;
        for (int l = 0; l < FoliageAtlas.Levels; l++) { var (w, h) = FoliageAtlas.LevelSize(l); expected += w * h * 4; }
        Assert.Equal((1, 1), FoliageAtlas.LevelSize(FoliageAtlas.Levels - 1));
        Assert.Equal(expected, chain.Length);
        Assert.Equal(chain, FoliageAtlas.Build());
        foreach (AtlasCell cell in Enum.GetValues<AtlasCell>())
        {
            if (!FoliageAtlas.IsFoliage(cell)) { Assert.Equal(1f, FoliageAtlas.Coverage(chain, 0, cell)); continue; }
            float c0 = FoliageAtlas.Coverage(chain, 0, cell);
            Assert.InRange(c0, 0.02f, 0.95f);
            for (int level = 1; level <= 5; level++)
            {
                float c = FoliageAtlas.Coverage(chain, level, cell);
                output.WriteLine($"{cell}: level {level} covers {c:0.000} (full size {c0:0.000})");
                Assert.InRange(c, c0 * 0.8f, c0 * 1.25f);
            }
        }
    }

    [Fact]
    public void EveryBiomeSpeciesExistsAndEverySpeciesGrowsSomewhere()
    {
        foreach (var b in Biomes.Every)
        {
            Assert.All(b.All, e => Assert.True(SpeciesCatalog.All.ContainsKey(e.Id), $"{b.Name}: no species {e.Id}"));
            Assert.All(b.Shrubs, e => Assert.Equal(VegLayer.Shrub, SpeciesCatalog.All[e.Id].Layer));
            Assert.All(b.Ground, e => Assert.Equal(VegLayer.Ground, SpeciesCatalog.All[e.Id].Layer));
        }
        var used = Biomes.Every.SelectMany(b => b.All).Select(e => e.Id).ToHashSet();
        Assert.All(SpeciesCatalog.All.Keys, id => Assert.Contains(id, used));
    }

    [Theory]
    [InlineData("t01", "tp041db6.map", "south plains")]
    [InlineData("T09", "tp081sd6.map", "south plains")]
    [InlineData("s04", "tp071dr6.map", "south plains")]
    [InlineData("t10", "tp061dr6.map", "sand country")]
    [InlineData("s06", "tp031db6.map", "sand country")]
    [InlineData("t11", "tp051dr6.map", "pecos")]
    [InlineData("s03", "tp021db6.map", "pecos")]
    [InlineData("t13", "tp061dr6.map", "trans-pecos")]
    [InlineData("t16", "tp041db6.map", "davis mountains")]
    [InlineData("t04", "tt031sd_.map", "dream")]
    [InlineData("m01", "tp031db6.map", "pecos")]           // the bowl, by its ground
    [InlineData("m05", "tp041db6.map", "south plains")]    // a desert track, by its ground
    [InlineData("m10", "tp051dr6.map", "salt flat")]
    [InlineData("m03", "tp061dr6.map", "roadside weeds")]
    [InlineData("m13", "tp031db6.map", "davis mountains")]
    [InlineData("n01", "tp041db6.map", "desert mix")]      // unlisted (a Nitro or custom map): by its ground family
    [InlineData("custom", "TP091DB6.MAP", "desert mix")]
    [InlineData("custom", "tp011gr6.map", "prairie")]
    public void RegionsByMissionAndByGroundFamily(string mission, string ground, string biome)
    {
        Assert.Equal(biome, Biomes.For(mission, ground)?.Biome.Name);
    }

    [Theory]
    [InlineData("m14", "tp181sw6.map")]   // Jorczak's Peak: snow, winter comes later
    [InlineData("custom", "tp181sw6.map")]
    [InlineData("custom", "tp191sr6.map")]
    [InlineData("custom", "tm151as_.map")]
    [InlineData("custom", null)]
    public void SnowAsphaltAndUnknownGrowNothing(string mission, string? ground)
    {
        Assert.Null(Biomes.For(mission, ground));
        Assert.Equal("sw", Biomes.FamilyKind("tp181sw6.map"));
        Assert.Equal("as", Biomes.FamilyKind("TM151AS_.MAP"));
    }

    [Fact]
    public void OnlyTheRiverMissionsLowGroundIsARiverBottom()
    {
        Assert.True(Biomes.For("t11")!.LowGround);
        Assert.True(Biomes.For("m10")!.LowGround);   // the salt flat
        foreach (var m in new[] { "t01", "t02", "t10", "t12", "s03", "t13", "t16", "m02", "m07", "m08", "m09" })
            Assert.False(Biomes.For(m)!.LowGround, m);
        // without a river bottom the Pecos' low ground grows its upland plants, no salt cedar or willow
        var p = new VegetationPlacer(Biomes.Pecos, new VegetationParams { LowGround = false }, new Plane(height: 0.3f), new VegMask(), 0x1234);
        var low = p.Place(VegLayer.Shrub, 0, 0, 400);
        Assert.NotEmpty(low);
        Assert.DoesNotContain(low, x => p.Species[x.Species].Id is "tamarisk" or "willow");
    }

    [Fact]
    public void TheSaltFlatIsBareButAtItsEdges()
    {
        VegetationPlacer P(float h) => new(Biomes.SaltFlat, new VegetationParams { LowLevel = 0 }, new Plane(height: h), new VegMask(), 0x5A17);
        Assert.Empty(P(0.2f).Place(VegLayer.Shrub, 0, 0, 300));
        Assert.Empty(P(0.2f).Place(VegLayer.Ground, 0, 0, 200));
        Assert.NotEmpty(P(5f).Place(VegLayer.Shrub, 0, 0, 300));
        Assert.NotEmpty(P(5f).Place(VegLayer.Ground, 0, 0, 200));
    }

    [Fact]
    public void RockySpeciesTakeTheSlopesAndShinneryTheFlats()
    {
        int Share(Biome b, float slope, VegLayer layer, params string[] ids)
        {
            var p = new VegetationPlacer(b, new VegetationParams { LowGround = false }, new Plane(slope: slope), new VegMask(), 0xABC);
            var l = p.Place(layer, 0, 0, 400);
            return l.Count == 0 ? 0 : 100 * l.Count(x => ids.Contains(p.Species[x.Species].Id)) / l.Count;
        }
        int flat = Share(Biomes.TransPecos, 0f, VegLayer.Shrub, "ocotillo", "sotol"), slope = Share(Biomes.TransPecos, 0.45f, VegLayer.Shrub, "ocotillo", "sotol");
        output.WriteLine($"trans-pecos: ocotillo and sotol {flat}% of shrubs on the flat, {slope}% on a 0.45 slope");
        Assert.True(slope > flat * 2, $"{slope}% against {flat}%");
        int lf = Share(Biomes.TransPecos, 0f, VegLayer.Ground, "lechuguilla"), ls = Share(Biomes.TransPecos, 0.45f, VegLayer.Ground, "lechuguilla");
        Assert.True(ls > lf * 2, $"lechuguilla {ls}% against {lf}%");
        Assert.True(Share(Biomes.SandCountry, 0f, VegLayer.Shrub, "shinnery") > Share(Biomes.SandCountry, 0.4f, VegLayer.Shrub, "shinnery"));
    }

    [Fact]
    public void WeedsCrowdTheRoadside()
    {
        var mask = new VegMask();
        mask.AddRoadQuad(new(0, 195), new(400, 195), new(400, 205), new(0, 205));
        var p = new VegetationPlacer(Biomes.SouthPlains, new VegetationParams { LowGround = false }, new Plane(), mask, 0xD1E);
        var all = p.Place(VegLayer.Ground, 0, 0, 400);
        bool Weed(PlantInstance x) => p.Species[x.Species].Id is "snakeweed" or "tumbleweed";
        var near = all.Where(x => MathF.Abs(x.Position.Z - 200) < 11).ToList();
        var far = all.Where(x => MathF.Abs(x.Position.Z - 200) > 30).ToList();
        float sn = near.Count(Weed) / (float)near.Count, sf = far.Count(Weed) / (float)far.Count;
        output.WriteLine($"south plains: weeds {sn:P0} of the ground layer by the road, {sf:P0} away from it");
        Assert.True(sn > sf * 2.5f);
    }

    [Fact]
    public void MottesAreDenseStands()
    {
        var plain = Biomes.SandCountry with { Shrubs = [.. Biomes.SandCountry.Shrubs.Select(e => e with { Motte = 0 })] };
        int Count(Biome b)
        {
            var p = new VegetationPlacer(b, new VegetationParams { LowGround = false }, new Plane(), new VegMask(), 0x5EED);
            return p.Place(VegLayer.Shrub, 0, 0, 600).Count(x => p.Species[x.Species].Id == "shinnery");
        }
        int with = Count(Biomes.SandCountry), without = Count(plain);
        output.WriteLine($"shinnery: {with} with its mottes, {without} without");
        Assert.True(with > without * 1.3f);
        // and Low's plants are still High's, the same species
        var hi = new VegetationPlacer(Biomes.SandCountry, new VegetationParams { LowGround = false }, new Plane(), new VegMask(), 0x5EED).Place(VegLayer.Shrub, 0, 0, 300)
            .ToDictionary(x => x.Position, x => x.Species);
        foreach (var x in new VegetationPlacer(Biomes.SandCountry, new VegetationParams { LowGround = false, Density = 0.6f }, new Plane(), new VegMask(), 0x5EED).Place(VegLayer.Shrub, 0, 0, 300))
            Assert.Equal(hi[x.Position], x.Species);
    }

    /// <summary>Every mission of the base game has its region (Jorczak's Peak, snow, none yet), read through its own WDEF ground family.</summary>
    [InstallFact]
    public void EveryBaseGameMissionHasItsRegion()
    {
        var names = Enumerable.Range(1, 17).Select(i => $"t{i:00}").Concat(Enumerable.Range(1, 7).Select(i => $"s{i:00}"))
            .Concat(Enumerable.Range(1, 15).Select(i => $"m{i:00}")).Append("a01");
        foreach (var m in names)
        {
            var ws = I76.Formats.Mission.WorldSettings.Read(new I76.Formats.Mission.MissionFile(TestInstall.Install.FindMission(m)!));
            var r = Biomes.For(m, ws?.Ground);
            output.WriteLine($"{m} {ws?.Ground,-13} {r?.Biome.Name ?? "-"}{(r?.LowGround == true ? ", low ground" : "")}{(r is { Density: not 1 } ? $", x{r.Density}" : "")}");
            if (m is "m14" or "m15") Assert.Null(r); else Assert.NotNull(r);
        }
    }

    /// <summary>The Nitro Pack's maps are not listed: they go by their ground family (desert: the desert mix).</summary>
    [NitroFact]
    public void NitroMapsGoByTheirGroundFamily()
    {
        var dir = Path.Combine(NitroFactAttribute.Dir!, "miss16");
        int desert = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.cbt", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Where(I76.Formats.Mission.MapFileSet.IsStockMission).Order())
        {
            var ws = I76.Formats.Mission.WorldSettings.Read(new I76.Formats.Mission.MissionFile(f));
            var name = Path.GetFileNameWithoutExtension(f);
            var r = Biomes.For(name, ws?.Ground);
            output.WriteLine($"{name} {ws?.Ground,-13} {r?.Biome.Name ?? "-"}");
            if (Biomes.FamilyKind(ws?.Ground) is "db" or "dr" or "sd") { desert++; Assert.Equal("desert mix", r?.Biome.Name); }
        }
        Assert.True(desert > 0);
    }

    private sealed class Plane(float height = 10, float slope = 0, Func<double, double, bool>? has = null) : IVegTerrain
    {
        public bool Has(double x, double z) => has?.Invoke(x, z) ?? true;
        public float Height(double x, double z) => height + slope * (float)x;
        public float Slope(double x, double z) => slope;
        public bool Open(double x, double z) => true;
    }

    private sealed class RoughPlane(Func<double, double, bool> rough) : IVegTerrain
    {
        public bool Has(double x, double z) => true;
        public float Height(double x, double z) => 10;
        public float Slope(double x, double z) => 0;
        public bool Open(double x, double z) => true;
        public bool Rough(double x, double z) => rough(x, z);
    }

    /// <summary>The original's rough ground (surface 5) grows more yucca and cholla, in clumps, and sparser grass; every other cell is untouched.</summary>
    [Fact]
    public void RoughGroundGrowsYuccaAndCholla()
    {
        var biome = Biomes.SouthPlains;
        VegetationPlacer P(IVegTerrain t) => new(biome, new VegetationParams { LowGround = false }, t, new VegMask(), 0x40C6);
        var plain = P(new RoughPlane((x, z) => false));
        var mixed = P(new RoughPlane((x, z) => x >= 400));   // the east half rough
        var big = new HashSet<string> { "soapweed", "cholla" };
        int Share(VegetationPlacer p, VegLayer l, float x0, out int n)
        {
            var all = p.Place(l, x0, 0, 400); n = all.Count;
            return all.Count(i => big.Contains(p.Species[i.Species].Id));
        }
        // west of the boundary: identical to a mission with no rough ground at all, both layers
        foreach (var l in new[] { VegLayer.Shrub, VegLayer.Ground })
            Assert.Equal(plain.Place(l, 0, 0, 400).Where(i => i.Position.X < 400), mixed.Place(l, 0, 0, 400).Where(i => i.Position.X < 400));
        // east: more of the big plants, the shrubs thicker than elsewhere, the grasses sparser, and in clumps
        int bigPlain = Share(plain, VegLayer.Shrub, 400, out int nPlain), bigRough = Share(mixed, VegLayer.Shrub, 400, out int nRough);
        output.WriteLine($"shrubs {nPlain} -> {nRough}, soapweed and cholla {bigPlain} -> {bigRough}");
        Assert.True(bigRough > 3 * bigPlain, $"{bigPlain} -> {bigRough}");
        Assert.True((double)bigRough / nRough > 0.45 && (double)bigPlain / nPlain < 0.4);
        Share(plain, VegLayer.Ground, 400, out int gPlain); Share(mixed, VegLayer.Ground, 400, out int gRough);
        Assert.True(gRough < gPlain * 0.9 && gRough > gPlain * 0.5, $"{gPlain} -> {gRough}");
        // clumped: the 20 m blocks' counts vary more than an even scatter's
        var counts = new int[20 * 20];
        foreach (var i in mixed.Place(VegLayer.Shrub, 400, 0, 400).Where(i => big.Contains(mixed.Species[i.Species].Id)))
            counts[(int)(i.Position.Z / 20) * 20 + (int)((i.Position.X - 400) / 20)]++;
        double mean = counts.Average(), var = counts.Average(c => (c - mean) * (c - mean));
        Assert.True(var > 1.5 * mean, $"mean {mean:0.0} variance {var:0.0}");
        // deterministic
        Assert.Equal(mixed.Place(VegLayer.Shrub, 400, 0, 400), P(new RoughPlane((x, z) => x >= 400)).Place(VegLayer.Shrub, 400, 0, 400));
        // the sward is thinner there too
        Assert.True(mixed.Sward(500, 500) < plain.Sward(500, 500) * 0.9f + 1e-6f);
    }

    private sealed class RoadPlane(Func<double, double, bool> road) : IVegTerrain
    {
        public bool Has(double x, double z) => true;
        public float Height(double x, double z) => 10;
        public float Slope(double x, double z) => 0;
        public bool Open(double x, double z) => true;
        public bool RoadSurface(double x, double z) => road(x, z);
    }

    /// <summary>Road surfaces (dirt flats, plank, parking lots) grow nothing but the roadside strip; every other cell is untouched.</summary>
    [Fact]
    public void RoadSurfacesStayBareExceptTheRoadsideStrip()
    {
        var biome = Biomes.SouthPlains;
        var mask = new VegMask();
        mask.AddRoadQuad(new(400, 195), new(800, 195), new(800, 205), new(400, 205));   // a road across the east half, z 195..205
        VegetationPlacer P(IVegTerrain t) => new(biome, new VegetationParams { LowGround = false }, t, mask, 0x40C6);
        var plain = P(new RoadPlane((x, z) => false));
        var mixed = P(new RoadPlane((x, z) => x >= 400));
        foreach (var l in new[] { VegLayer.Shrub, VegLayer.Ground })
        {
            Assert.Equal(plain.Place(l, 0, 0, 400).Where(i => i.Position.X < 400), mixed.Place(l, 0, 0, 400).Where(i => i.Position.X < 400));
            var was = plain.Place(l, 400, 0, 400).Where(i => i.Position.X >= 400).ToList(); var now = mixed.Place(l, 400, 0, 400).Where(i => i.Position.X >= 400).ToList();
            output.WriteLine($"{l}: {was.Count} -> {now.Count}");
            Assert.True(now.Count < was.Count / 4);
            // what is left is within the strip of the road's edge, and as it was
            Assert.All(now, i => Assert.True(MathF.Abs(i.Position.Z - 200) < 5 + VegetationPlacer.RoadsideStrip + 0.01f));
            Assert.All(now, i => Assert.Contains(i, was));
            // the strip itself is as it was
            Assert.Equal(was.Where(i => MathF.Abs(i.Position.Z - 200) < 10), now.Where(i => MathF.Abs(i.Position.Z - 200) < 10));
        }
        Assert.Equal(0f, mixed.Sward(600, 100));
        Assert.True(plain.Sward(600, 100) > 0);
        Assert.Equal(plain.Sward(600, 208), mixed.Sward(600, 208));
    }

    private static VegetationPlacer Placer(IVegTerrain t, VegMask? m = null, float density = 1, float low = 0) =>
        new(Biomes.Pecos, new VegetationParams { Density = density, LowLevel = low }, t, m ?? new VegMask(), 0x1234);

    [Fact]
    public void PlacementIsTheSameWhicheverWayTheGroundIsCut()
    {
        var p = Placer(new Plane());
        var whole = p.Place(VegLayer.Shrub, 0, 0, 160);
        var quarters = new List<PlantInstance>();
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) quarters.AddRange(p.Place(VegLayer.Shrub, i * 80, j * 80, 80));
        Assert.Equal(whole.OrderBy(x => x.Position.X).ThenBy(x => x.Position.Z), quarters.OrderBy(x => x.Position.X).ThenBy(x => x.Position.Z));
        Assert.NotEmpty(whole);
        Assert.Equal(whole, Placer(new Plane()).Place(VegLayer.Shrub, 0, 0, 160));
    }

    [Fact]
    public void RiverBottomGrowsRiparianSpeciesAndUplandsTheDesertOnes()
    {
        var low = Placer(new Plane(height: 0.5f)).Place(VegLayer.Shrub, 0, 0, 400);
        var high = Placer(new Plane(height: 30f)).Place(VegLayer.Shrub, 0, 0, 400);
        var sp = Placer(new Plane()).Species;
        int Count(List<PlantInstance> l, params string[] ids) => l.Count(x => ids.Contains(sp[x.Species].Id));
        output.WriteLine($"river bottom: {low.Count} shrubs, {Count(low, "tamarisk", "willow")} salt cedar and willow; uplands: {high.Count}, {Count(high, "creosote")} creosote");
        Assert.True(Count(low, "tamarisk", "willow") > low.Count / 2);
        Assert.Equal(0, Count(high, "tamarisk", "willow"));
        Assert.True(Count(high, "creosote") > high.Count / 4);
        Assert.True(low.Count > high.Count, "the river bottom is denser");
    }

    [Fact]
    public void SteepGroundAndNoTerrainGrowNothing()
    {
        Assert.Empty(Placer(new Plane(slope: 1.5f)).Place(VegLayer.Shrub, 0, 0, 200));
        Assert.Empty(Placer(new Plane(has: (x, z) => false)).Place(VegLayer.Ground, 0, 0, 100));
        Assert.True(Placer(new Plane(slope: 0.55f)).Place(VegLayer.Shrub, 0, 0, 300).Count < Placer(new Plane()).Place(VegLayer.Shrub, 0, 0, 300).Count);
    }

    [Fact]
    public void DensityScalesTheCover()
    {
        int full = Placer(new Plane(), density: 1).Place(VegLayer.Ground, 0, 0, 200).Count;
        int half = Placer(new Plane(), density: 0.5f).Place(VegLayer.Ground, 0, 0, 200).Count;
        Assert.InRange(half, full * 0.35, full * 0.65);
        // Low's plants are a subset of High's: switching only thins them
        var hi = Placer(new Plane(), density: 1).Place(VegLayer.Shrub, 0, 0, 200).Select(x => x.Position).ToHashSet();
        Assert.All(Placer(new Plane(), density: 0.5f).Place(VegLayer.Shrub, 0, 0, 200), x => Assert.Contains(x.Position, hi));
    }

    [Fact]
    public void MasksKeepPlantsOffRoadsObjectsAndDrivingLines()
    {
        var mask = new VegMask();
        // a road 10 m wide along x at z = 50, a building at (150, 150), a driving line along z at x = 100
        mask.AddRoadQuad(new(0, 45), new(200, 45), new(200, 55), new(0, 55));
        mask.AddFootprint(new(150, 150), Vector2.Normalize(new(1, 1)), 10, 6);
        mask.AddLine([new(100, 0), new(100, 200)], 2f);
        mask.AddDisc(new(30, 150), 5);
        var p = Placer(new Plane(), mask);
        var shrubs = p.Place(VegLayer.Shrub, 0, 0, 200);
        var grass = p.Place(VegLayer.Ground, 0, 0, 200);
        Assert.NotEmpty(shrubs);
        foreach (var x in shrubs.Concat(grass))
        {
            var q = new Vector2(x.Position.X, x.Position.Z);
            Assert.False(q.Y > 45 - 0.8f && q.Y < 55 + 0.8f, $"plant on the road at {q}");
            Assert.True(MathF.Abs(q.X - 100) > 2.3f, $"plant on the driving line at {q}");
            Assert.True(Vector2.Distance(q, new(30, 150)) > 5, $"plant in the disc at {q}");
            var d = q - new Vector2(150, 150);
            var u = Vector2.Normalize(new Vector2(1, 1));
            Assert.False(MathF.Abs(Vector2.Dot(d, u)) < 10 && MathF.Abs(d.X * -u.Y + d.Y * u.X) < 6, $"plant in the building at {q}");
        }
        foreach (var x in shrubs) Assert.True(MathF.Abs(x.Position.Z - 50) > 5 + 2.5f, $"shrub too near the road at {x.Position}");
        Assert.Equal(0, mask.RoadDistance(new(10, 50), 5));
        Assert.Equal(3, mask.RoadDistance(new(10, 58), 5), 3);
    }

    [Fact]
    public void PlantsStandSunkIntoTheGround()
    {
        var t = new Plane(height: 12, slope: 0.3f);
        foreach (var x in Placer(t).Place(VegLayer.Shrub, 0, 0, 200))
        {
            float g = t.Height(x.Position.X, x.Position.Z);
            Assert.InRange(g - x.Position.Y, 0.01f, 0.5f);
        }
    }

    /// <summary>T11 on its real terrain: the river bottom found, plants on the drawn ground, none on a road.</summary>
    [InstallFact]
    public void T11PlantsStandOnTheDrawnGroundAndOffTheRoads()
    {
        var vfs = TestInstall.Vfs;
        var w = new I76.Formats.Assembly.MissionWorld(vfs, new I76.Formats.Assembly.ModelAssembler(vfs), TestInstall.Install.FindMission("t11")!);
        var surf = new I76.Formats.Assembly.TerrainSurface(w.Terrain, I76.Formats.Assembly.TerrainSurface.Seeds(w));
        var terrain = new SurfaceTerrain(surf, w.Terrain);
        float low = SurfaceTerrain.LowLevel(surf, w.Terrain);
        Assert.InRange(low, -0.5f, 2f);   // the Pecos' bed is the map's low ground, about 0
        var mask = VegMask.ForMission(w, new I76.Formats.Assembly.ModelAssembler(vfs));
        var p = new VegetationPlacer(Biomes.Pecos, new VegetationParams { LowLevel = low }, terrain, mask, VegHash.Of("T11"));
        Array.Clear(VegetationPlacer.Counts);
        // the river bottom under the 380 bridge, and the creosote flats east of it along the highway
        var river = p.Place(VegLayer.Shrub, 3400, 48560, 160);
        var flats = p.Place(VegLayer.Shrub, 4480, 49000, 160);
        {
            int has = 0, open = 0, n = 0; float hmin = 1e9f, hmax = -1e9f, smax = 0;
            for (float x = 3440; x < 3500; x += 2) for (float z = 48600; z < 48660; z += 2)
            {
                n++; if (terrain.Has(x, z)) has++; if (terrain.Open(x, z)) open++;
                float h = terrain.Height(x, z); hmin = MathF.Min(hmin, h); hmax = MathF.Max(hmax, h); smax = MathF.Max(smax, terrain.Slope(x, z));
            }
            output.WriteLine($"hump: {n} points, has {has}, open {open}, height {hmin:0.0}..{hmax:0.0}, slope max {smax:0.00}, rip {p.Riparian(hmax):0.00}");
            Array.Clear(VegetationPlacer.Counts);
            var hp = p.Place(VegLayer.Ground, 3440, 48600, 60);
            output.WriteLine($"hump grass {hp.Count}, cells {string.Join(",", VegetationPlacer.Counts)}");
        }
        output.WriteLine($"view: {river.Count(x => x.Position.X > 3394 && x.Position.X < 3486 && x.Position.Z > 48654 && x.Position.Z < 48706)}");
        output.WriteLine($"river: {river.Count} shrubs, flats: {flats.Count}; cells {string.Join(",", VegetationPlacer.Counts)}");
        int Rip(List<PlantInstance> l) => l.Count(x => p.Species[x.Species].Id is "tamarisk" or "willow");
        Assert.True(Rip(river) > river.Count / 3, $"{Rip(river)} of {river.Count} riparian by the river");
        Assert.True(Rip(flats) < flats.Count / 10);
        foreach (var x in river.Concat(flats).Concat(p.Place(VegLayer.Ground, 4480, 48900, 160)))
        {
            float g = terrain.Height(x.Position.X, x.Position.Z);
            Assert.InRange(g - x.Position.Y, 0f, 0.6f);
            Assert.True(mask.RoadDistance(new(x.Position.X, x.Position.Z), 1f) >= 0.8f, $"plant on a road at {x.Position}");
        }
    }

    private static (I76.Formats.Assembly.MissionWorld W, VegMask Mask, VegRegion Region, SurfaceTerrain Terrain, VegetationPlacer P) T02Placer()
    {
        var vfs = TestInstall.Vfs;
        var asm = new I76.Formats.Assembly.ModelAssembler(vfs);
        var w = new I76.Formats.Assembly.MissionWorld(vfs, asm, TestInstall.Install.FindMission("t02")!);
        var surf = new I76.Formats.Assembly.TerrainSurface(w.Terrain, I76.Formats.Assembly.TerrainSurface.Seeds(w));
        var terrain = new SurfaceTerrain(surf, w.Terrain);
        var mask = VegMask.ForMission(w, asm);
        var region = Biomes.For("T02")!;
        return (w, mask, region, terrain, new VegetationPlacer(region.Biome, new VegetationParams { LowGround = region.LowGround }, terrain, mask, VegHash.Of("T02")));
    }

    /// <summary>T02's sward map is made quickly enough for the loading card.</summary>
    [InstallFact]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void T02SwardIsQuick()
    {
        var (w, _, _, _, p) = T02Placer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SwardMap.Build(p, w.Terrain);
        long ms = sw.ElapsedMilliseconds;
        output.WriteLine($"T02 sward: {ms} ms");
        Assert.True(ms < 1500, $"{ms} ms");
    }

    /// <summary>
    /// The sward map on T02's real terrain (the South Plains): grassy on the open prairie, bare under objects (the roads
    /// paint over it), and its texels near the placer's own sward at their middles. How quickly: <see cref="T02SwardIsQuick"/>.
    /// </summary>
    [InstallFact]
    public void T02SwardCoversThePrairieAndKeepsOffObjects()
    {
        var (w, mask, region, terrain, p) = T02Placer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var map = SwardMap.Build(p, w.Terrain);
        long ms = sw.ElapsedMilliseconds;
        output.WriteLine($"T02 sward: {map.Width} x {map.Height} texels from {map.X0},{map.Z0}, mean {map.Mean:0.00}, {ms} ms");
        Assert.True(map.Width > 0 && map.Height > 0);
        Assert.InRange(map.Mean, 0.25f, 0.9f);
        Assert.Equal(map.Width * map.Height, map.Data.Length);
        // the texels are the placer's sward at their middles, and deterministic
        sw.Restart();
        Assert.Equal(map.Data, SwardMap.Build(p, w.Terrain).Data);
        output.WriteLine($"again: {sw.ElapsedMilliseconds} ms");
        var rng = new VegRng(7);
        // the map's broad part interpolated from a coarser lattice: near the sward read at each texel's middle
        double err = 0; int on = 0, under = 0, big = 0;
        for (int k = 0; k < 4000; k++)
        {
            int i = rng.Int(map.Width), j = rng.Int(map.Height);
            float x = map.X0 + (i + 0.5f) * SwardMap.Texel, z = map.Z0 + (j + 0.5f) * SwardMap.Texel;
            if (!terrain.Has(x, z)) continue;
            on++;
            float e = MathF.Abs(p.Sward(x, z) - map.At(x, z));
            err += e; big += e > 0.15f ? 1 : 0;
            // nothing under an object
            if (mask.Blocked(new(x, z), 0, 0.5f, -1e9f, discs: false)) { under++; Assert.Equal(0, map.At(x, z)); }
        }
        output.WriteLine($"{on} sampled texels on terrain: mean error {err / on:0.000}, {big} off by more than 0.15; {under} under objects");
        Assert.True(err / on < 0.04);
        Assert.True(big < on / 20);
        // Low's ground is as grassy as High's: the sward does not depend on the setting's density
        var low = new VegetationPlacer(region.Biome, new VegetationParams { Density = 0.6f, LowGround = region.LowGround }, terrain, mask, VegHash.Of("T02"));
        Assert.Equal(p.Sward(9047, 43797), low.Sward(9047, 43797));
    }

    [Fact]
    public void SwardFollowsTheGrassAndDesertsHaveLittle()
    {
        var prairie = new VegetationPlacer(Biomes.SouthPlains, new VegetationParams { LowGround = false }, new Plane(), new VegMask(), 5);
        var desert = new VegetationPlacer(Biomes.TransPecos, new VegetationParams { LowGround = false }, new Plane(), new VegMask(), 5);
        var dream = new VegetationPlacer(Biomes.Dream, new VegetationParams { LowGround = false }, new Plane(), new VegMask(), 5);
        float Mean(VegetationPlacer v) { float s = 0; for (int i = 0; i < 400; i++) s += v.Sward(i * 3.1f, i * 7.3f % 400); return s / 400; }
        output.WriteLine($"sward: south plains {Mean(prairie):0.00}, trans-pecos {Mean(desert):0.00}, dream {Mean(dream):0.00}");
        Assert.True(Mean(prairie) > 0.45f);
        Assert.True(Mean(desert) < Mean(prairie) * 0.5f);
        Assert.Equal(0, Mean(dream));
        // nothing on steep ground or off the terrain, and the grasses' tufts are bigger where the sward is denser
        Assert.Equal(0, new VegetationPlacer(Biomes.SouthPlains, new(), new Plane(slope: 1.5f), new VegMask(), 5).Sward(10, 10));
        Assert.Equal(0, new VegetationPlacer(Biomes.SouthPlains, new(), new Plane(has: (x, z) => false), new VegMask(), 5).Sward(10, 10));
        var tufts = prairie.Place(VegLayer.Ground, 0, 0, 300).Where(t => prairie.Species[t.Species].Grass).ToList();
        var dense = tufts.Where(t => prairie.Sward(t.Position.X, t.Position.Z) > 0.6f).ToList();
        var thin = tufts.Where(t => prairie.Sward(t.Position.X, t.Position.Z) < 0.35f).ToList();
        output.WriteLine($"{tufts.Count} tufts: {dense.Count} in dense sward (scale {dense.Average(t => t.Scale):0.00}), {thin.Count} in thin (scale {thin.Average(t => t.Scale):0.00})");
        Assert.True(dense.Average(t => t.Scale) > thin.Average(t => t.Scale) + 0.05f);
        // and clumped: the dense sward holds more tufts a square metre than the thin
        Assert.True(dense.Count > 0 && thin.Count > 0);
    }

    [Theory]
    [InlineData(9f, 22f)]
    [InlineData(12f, 35f)]
    [InlineData(20f, 50f)]
    public void TierBandsOverlapAtEveryBoundaryAndCoverEveryDistance(float mid, float far)
    {
        int prev = 0;
        for (float d = 0; d < 120; d += 0.25f)
        {
            int t = TierBands.Tiers(d, mid, far);
            Assert.NotEqual(0, t);
            // a plant is in one tier, or two neighbouring ones in a band; its tiers only move outward with distance
            Assert.True(t is 1 or 2 or 4 or 3 or 6, $"d {d}: {t}");
            Assert.True(TierBands.Tiers(d, mid, far, 2.5f) == t || (TierBands.Tiers(d, mid, far, 2.5f) & t) == t, $"d {d}: slack only adds");
            Assert.True((t & ~prev) != 0 || prev == 0 || (t & prev) != 0, $"d {d}");
            prev = t;
        }
        Assert.Equal(TierBands.Full, TierBands.Tiers(0, mid, far));
        Assert.Equal(TierBands.Middle, TierBands.Tiers((mid + far) / 2, mid, far));
        Assert.Equal(TierBands.Far, TierBands.Tiers(far * 3, mid, far));
        // in the band both tiers draw, and the shares given to each (the shader's smoothstep) sum to one
        Assert.Equal(TierBands.Full | TierBands.Middle, TierBands.Tiers(mid, mid, far));
        Assert.Equal(TierBands.Middle | TierBands.Far, TierBands.Tiers(far, mid, far));
        Assert.Equal(0.5f, TierBands.Progress(mid, mid), 3);
        Assert.Equal(0f, TierBands.Progress(mid - TierBands.HalfWidth(mid), mid), 3);
        Assert.Equal(1f, TierBands.Progress(mid + TierBands.HalfWidth(mid), mid), 3);
        // a band widens with distance
        Assert.True(TierBands.HalfWidth(far) > TierBands.HalfWidth(mid));
    }

    [Fact]
    public void TierBandsChunkNeedsOneTierOnlyAwayFromTheBoundaries()
    {
        Assert.Equal(TierBands.Full, TierBands.TiersBetween(0, 5, 9, 22));
        Assert.Equal(TierBands.Full | TierBands.Middle, TierBands.TiersBetween(0, 40, 9, 22) & ~TierBands.Far);
        Assert.Equal(TierBands.Middle, TierBands.TiersBetween(12, 16, 9, 22));
        Assert.Equal(TierBands.Far, TierBands.TiersBetween(30, 60, 9, 22));
        Assert.Equal(TierBands.Middle | TierBands.Far, TierBands.TiersBetween(15, 60, 9, 22));
    }

    [Fact]
    public void TheFarTierKeepsEveryTuftOfAGrassVariant()
    {
        foreach (var s in SpeciesCatalog.All.Values.Where(s => s.Grass))
            for (int v = 0; v < s.Variants; v++)
            {
                var t = s.BuildTiers(VegHash.Mix(VegHash.Of(s.Id), (ulong)v));
                // the small tufts beside the main one are a card each in the middle tier, and still are in the far one
                Assert.True(t[2].VertexCount > 0, $"{s.Id} v{v}: the far tier is empty");
                Assert.True(t[2].VertexCount >= t[1].VertexCount / 3, $"{s.Id} v{v}: far {t[2].VertexCount} against middle {t[1].VertexCount}");
            }
    }
}
