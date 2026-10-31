using System.Globalization;
using System.Numerics;
using System.Text;

namespace I76.Formats.Vegetation;

/// <summary>Which placement grid a species grows on: shrubs on the coarse one (one plant a cell at most), grasses and forbs on the fine one.</summary>
public enum VegLayer { Shrub, Ground }

/// <summary>
/// One species: how big it grows, how stiff it is in the wind, and how one of its variants is built (a seed and a
/// detail level: 0 the full mesh, 1 a lighter one for the middle distance, 2 the lightest for far away).
/// </summary>
public sealed class SpeciesDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required VegLayer Layer { get; init; }
    /// <summary>Each instance is scaled by a factor in this range (the meshes are built at a typical adult's size).</summary>
    public float ScaleMin { get; init; } = 0.8f;
    public float ScaleMax { get; init; } = 1.25f;
    /// <summary>0: sways freely (grass); 1: rigid (cactus).</summary>
    public float Stiffness { get; init; } = 0.5f;
    /// <summary>The ground a plant covers (metres, radius): neighbours on the shrub grid keep this far off.</summary>
    public float Radius { get; init; } = 0.6f;
    public int Variants { get; init; } = 3;
    /// <summary>Sunk this far into the ground (metres) at scale 1, so a base on a slope never floats.</summary>
    public float Sink { get; init; } = 0.06f;
    /// <summary>A grass: its cover makes the sward between the plants (<see cref="SwardMap"/>).</summary>
    public bool Grass { get; init; }
    /// <summary>
    /// A variant's mesh from its seed at a detail, with its ambient occlusion baked in (<see cref="PlantShading"/>,
    /// against the full mesh: a lighter one builds that too; <see cref="BuildTiers"/> builds it once for all three).
    /// </summary>
    public required Func<ulong, int, PlantMesh> Build
    {
        get => (s, d) => d == 0 ? Full(s).Mesh : BuildTiers(s)[d];
        init => _generate = value;
    }
    private readonly Func<ulong, int, PlantMesh> _generate = null!;

    private (PlantMesh Mesh, PlantShading.Occluders Grid) Full(ulong seed)
    {
        var m = _generate(seed, 0);
        var g = PlantShading.Grid(m);
        PlantShading.Bake(m, g);
        PlantShading.GroundShade(m, g);
        return (m, g);
    }

    /// <summary>
    /// The full, middle and far meshes of a variant, shaded alike: the lighter ones take the full one's occlusion
    /// (<see cref="PlantShading.Transfer"/>) and its shade on the ground.
    /// </summary>
    public PlantMesh[] BuildTiers(ulong seed)
    {
        var (full, g) = Full(seed);
        var tiers = new[] { full, PlantShading.Transfer(full, g, _generate(seed, 1)), PlantShading.Transfer(full, g, _generate(seed, 2)) };
        foreach (var t in tiers) { t.GroundShade = full.GroundShade; t.GroundRadius = full.GroundRadius; }
        return tiers;
    }
}

/// <summary>
/// The plants of the modern look's vegetation (docs/modern-vegetation.md): the branching ones grown by stochastic
/// L-systems (<see cref="LSystem"/>, read by the <see cref="Turtle"/>), the rosettes, grasses and tumbleweeds by
/// parametric generators. Sizes are real-world adults'; colours are sRGB of the living plant, tinted per mission
/// by the renderer.
/// </summary>
public static class SpeciesCatalog
{
    public static readonly IReadOnlyDictionary<string, SpeciesDef> All = new[]
    {
        new SpeciesDef { Id = "creosote", Name = "Creosote bush (Larrea tridentata)", Layer = VegLayer.Shrub, Stiffness = 0.45f, Radius = 0.7f, Variants = 4, Build = Creosote },
        new SpeciesDef { Id = "saltbush", Name = "Four-wing saltbush (Atriplex canescens)", Layer = VegLayer.Shrub, Stiffness = 0.5f, Radius = 0.8f, Build = Saltbush },
        new SpeciesDef { Id = "mesquite", Name = "Honey mesquite, shrub form (Prosopis glandulosa)", Layer = VegLayer.Shrub, Stiffness = 0.4f, Radius = 1.4f, ScaleMin = 0.75f, ScaleMax = 1.2f, Build = Mesquite },
        new SpeciesDef { Id = "cholla", Name = "Tree cholla (Cylindropuntia imbricata)", Layer = VegLayer.Shrub, Stiffness = 0.95f, Radius = 0.6f, Build = Cholla },
        new SpeciesDef { Id = "pricklypear", Name = "Prickly pear (Opuntia phaeacantha)", Layer = VegLayer.Shrub, Stiffness = 1f, Radius = 0.7f, Build = PricklyPear },
        new SpeciesDef { Id = "soaptree", Name = "Soaptree yucca (Yucca elata)", Layer = VegLayer.Shrub, Stiffness = 0.85f, Radius = 0.7f, ScaleMin = 0.75f, ScaleMax = 1.2f, Build = (s, d) => Yucca(s, d, YuccaKind.Soaptree) },
        new SpeciesDef { Id = "soapweed", Name = "Soapweed yucca (Yucca glauca)", Layer = VegLayer.Shrub, Stiffness = 0.85f, Radius = 0.5f, Build = (s, d) => Yucca(s, d, YuccaKind.Soapweed) },
        new SpeciesDef { Id = "tamarisk", Name = "Salt cedar, shrub (Tamarix chinensis)", Layer = VegLayer.Shrub, Stiffness = 0.3f, Radius = 1.3f, ScaleMin = 0.75f, ScaleMax = 1.25f, Build = Tamarisk },
        new SpeciesDef { Id = "willow", Name = "Coyote willow (Salix exigua)", Layer = VegLayer.Shrub, Stiffness = 0.3f, Radius = 1.1f, Build = Willow },
        new SpeciesDef { Id = "grama", Grass = true, Name = "Blue grama (Bouteloua gracilis)", Layer = VegLayer.Ground, Stiffness = 0.1f, Radius = 0.2f, Sink = 0.02f, ScaleMin = 0.6f, ScaleMax = 1.35f, Build = (s, d) => Grass(s, d, GrassKind.Grama) },
        new SpeciesDef { Id = "tobosa", Grass = true, Name = "Tobosa (Pleuraphis mutica)", Layer = VegLayer.Ground, Stiffness = 0.15f, Radius = 0.3f, Sink = 0.02f, ScaleMin = 0.75f, ScaleMax = 1.3f, Build = (s, d) => Grass(s, d, GrassKind.Tobosa) },
        new SpeciesDef { Id = "sacaton", Grass = true, Name = "Alkali sacaton (Sporobolus airoides)", Layer = VegLayer.Ground, Stiffness = 0.15f, Radius = 0.45f, Sink = 0.03f, Build = (s, d) => Grass(s, d, GrassKind.Sacaton) },
        new SpeciesDef { Id = "tumbleweed", Name = "Russian thistle, tumbleweed (Salsola tragus)", Layer = VegLayer.Ground, Stiffness = 0.6f, Radius = 0.35f, Sink = 0.08f, ScaleMin = 0.6f, ScaleMax = 1.3f, Build = Tumbleweed },
        new SpeciesDef { Id = "snakeweed", Name = "Broom snakeweed (Gutierrezia sarothrae)", Layer = VegLayer.Ground, Stiffness = 0.35f, Radius = 0.3f, Sink = 0.03f, Build = Snakeweed },
        // the South Plains and the sand country of south-eastern New Mexico
        new SpeciesDef { Id = "sandsage", Name = "Sand sagebrush (Artemisia filifolia)", Layer = VegLayer.Shrub, Stiffness = 0.45f, Radius = 0.6f, Build = SandSage },
        new SpeciesDef { Id = "shinnery", Name = "Shinnery oak (Quercus havardii)", Layer = VegLayer.Shrub, Stiffness = 0.5f, Radius = 1.3f, ScaleMin = 0.75f, ScaleMax = 1.25f, Variants = 4, Build = Shinnery },
        new SpeciesDef { Id = "buffalograss", Grass = true, Name = "Buffalograss (Bouteloua dactyloides)", Layer = VegLayer.Ground, Stiffness = 0.2f, Radius = 0.35f, Sink = 0.01f, ScaleMin = 0.75f, ScaleMax = 1.3f, Build = Buffalograss },
        new SpeciesDef { Id = "dropseed", Grass = true, Name = "Sand dropseed (Sporobolus cryptandrus)", Layer = VegLayer.Ground, Stiffness = 0.15f, Radius = 0.3f, Sink = 0.02f, ScaleMin = 0.75f, ScaleMax = 1.25f, Build = (s, d) => Grass(s, d, GrassKind.Dropseed) },
        new SpeciesDef { Id = "indianblanket", Name = "Indian blanket, firewheel (Gaillardia pulchella)", Layer = VegLayer.Ground, Stiffness = 0.35f, Radius = 0.25f, Sink = 0.02f, Build = (s, d) => Wildflower(s, d, AtlasCell.IndianBlanket, 0.28f, 0.45f, 1.4f) },
        new SpeciesDef { Id = "pricklypoppy", Name = "Prickly poppy (Argemone polyanthemos)", Layer = VegLayer.Ground, Stiffness = 0.4f, Radius = 0.3f, Sink = 0.02f, Build = (s, d) => Wildflower(s, d, AtlasCell.PricklyPoppy, 0.5f, 0.8f, 1.0f) },
        new SpeciesDef { Id = "horsemint", Name = "Purple horsemint (Monarda citriodora)", Layer = VegLayer.Ground, Stiffness = 0.35f, Radius = 0.22f, Sink = 0.02f, Build = (s, d) => Wildflower(s, d, AtlasCell.Horsemint, 0.32f, 0.55f, 0.9f) },
        // the Trans-Pecos
        new SpeciesDef { Id = "lechuguilla", Name = "Lechuguilla (Agave lechuguilla)", Layer = VegLayer.Ground, Stiffness = 0.95f, Radius = 0.35f, Sink = 0.03f, Variants = 4, Build = Lechuguilla },
        new SpeciesDef { Id = "ocotillo", Name = "Ocotillo (Fouquieria splendens)", Layer = VegLayer.Shrub, Stiffness = 0.55f, Radius = 1.0f, ScaleMin = 0.75f, ScaleMax = 1.2f, Variants = 4, Build = Ocotillo },
        new SpeciesDef { Id = "sotol", Name = "Sotol (Dasylirion leiophyllum)", Layer = VegLayer.Shrub, Stiffness = 0.75f, Radius = 0.8f, Build = Sotol },
        new SpeciesDef { Id = "torrey", Name = "Torrey yucca (Yucca treculeana)", Layer = VegLayer.Shrub, Stiffness = 0.85f, Radius = 0.8f, ScaleMin = 0.75f, ScaleMax = 1.2f, Build = (s, d) => Yucca(s, d, YuccaKind.Torrey) },
        new SpeciesDef { Id = "candelilla", Name = "Candelilla (Euphorbia antisyphilitica)", Layer = VegLayer.Ground, Stiffness = 0.7f, Radius = 0.3f, Sink = 0.02f, Build = Candelilla },
        new SpeciesDef { Id = "chinograma", Grass = true, Name = "Chino grama (Bouteloua ramosa)", Layer = VegLayer.Ground, Stiffness = 0.1f, Radius = 0.2f, Sink = 0.02f, ScaleMin = 0.7f, ScaleMax = 1.3f, Build = (s, d) => Grass(s, d, GrassKind.ChinoGrama) },
        // the Davis Mountains
        new SpeciesDef { Id = "sideoats", Grass = true, Name = "Sideoats grama (Bouteloua curtipendula)", Layer = VegLayer.Ground, Stiffness = 0.12f, Radius = 0.25f, Sink = 0.02f, ScaleMin = 0.75f, ScaleMax = 1.25f, Build = (s, d) => Grass(s, d, GrassKind.Sideoats) },
        new SpeciesDef { Id = "bluestem", Grass = true, Name = "Little bluestem (Schizachyrium scoparium)", Layer = VegLayer.Ground, Stiffness = 0.15f, Radius = 0.3f, Sink = 0.02f, ScaleMin = 0.75f, ScaleMax = 1.25f, Build = (s, d) => Grass(s, d, GrassKind.Bluestem) },
        new SpeciesDef { Id = "beargrass", Name = "Beargrass (Nolina microcarpa)", Layer = VegLayer.Shrub, Stiffness = 0.35f, Radius = 0.7f, Build = Beargrass },
        new SpeciesDef { Id = "agave", Name = "Havard agave, century plant (Agave havardiana)", Layer = VegLayer.Shrub, Stiffness = 0.95f, Radius = 0.75f, Build = Agave },
        new SpeciesDef { Id = "grayoak", Name = "Gray oak, shrub form (Quercus grisea)", Layer = VegLayer.Shrub, Stiffness = 0.45f, Radius = 1.3f, ScaleMin = 0.75f, ScaleMax = 1.2f, Build = GrayOak },
        // salt flats
        new SpeciesDef { Id = "pickleweed", Name = "Pickleweed, iodine bush (Allenrolfea occidentalis)", Layer = VegLayer.Ground, Stiffness = 0.5f, Radius = 0.35f, Sink = 0.03f, Build = (s, d) => Wildflower(s, d, AtlasCell.Pickleweed, 0.25f, 0.45f, 1.6f) },
    }.ToDictionary(s => s.Id);

    // ---- colours -----------------------------------------------------------------------------------------

    /// <summary>An sRGB colour (0..255) as the linear RGBA the mesh carries.</summary>
    public static Vector4 Srgb(int r, int g, int b) => new(Lin(r), Lin(g), Lin(b), 1f);
    private static float Lin(int c) { float s = c / 255f; return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f); }

    private static Vector4 Vary(Vector4 c, ref VegRng rng, float amount)
    {
        float k = 1 + amount * rng.Bell();
        float h = amount * 0.6f * rng.Bell();
        return new Vector4(c.X * k * (1 + h), c.Y * k, c.Z * k * (1 - h), 1);
    }

    private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- shared builder from a turtle's skeleton ---------------------------------------------------------

    private sealed class Style
    {
        public Vector4 BarkBase, BarkTip;
        public AtlasCell Leaf = AtlasCell.Creosote;
        public Vector4 LeafColour = Vector4.One;
        public float LeafVary = 0.08f;
        public int LeafPlanes = 2;
        public float DrawMinRadius = 0.003f;   // thinner stems than this are left to the foliage
        public float Lod1MinRadius = 0.012f;
        public float FarMinRadius = -1f;     // the far mesh's thinnest stem (default: twice the middle one's, 2 cm at least)
        public Vector4 JointColour, JointTip;
        public Vector4 PadColour, KnobColour = Srgb(200, 180, 60);
        public float KnobScale = 1f;
        public float JointBulge = 0.12f;
        public float Roundness = 0.75f;
        public bool Caps = true;   // a card across upright twigs (off for the sprays seen from the side)
    }

    private static PlantMesh FromSkeleton(PlantSkeleton sk, Style st, int detail, ref VegRng rng)
    {
        var m = new PlantMesh();
        var (mn, mx) = sk.Bounds();
        float height = MathF.Max(0.2f, mx.Y);
        // the crown's middle, which the foliage normals point away from
        var crown = Vector3.Zero;
        if (sk.Leaves.Count > 0) { foreach (var l in sk.Leaves) crown += l.Pos; crown /= sk.Leaves.Count; crown.Y *= 0.85f; }
        else crown = new Vector3(0, height * 0.5f, 0);
        float Sway(Vector3 p) => MathF.Pow(Math.Clamp(p.Y / height, 0f, 1.2f), 1.4f) + 0.15f * MathF.Min(1f, new Vector2(p.X, p.Z).Length() / height);

        var ends = new Dictionary<Vector3, (int Ring, int Sides)>();
        foreach (var s in sk.Segments)
        {
            if (s.Kind == SegmentKind.Joint)
            {
                int sides = detail == 0 ? 7 : detail == 1 ? 4 : 3;
                var col = Vary(st.JointColour, ref rng, 0.06f);
                var tip = Vector4.Lerp(col, st.JointTip, 0.5f);
                Shapes.Tube(m, s.A, s.B, s.Ra, s.Rb, sides, s.Side, col, tip, Sway(s.A), Sway(s.B), AtlasCell.Cholla,
                    rings: detail == 0 ? 2 : 1, bulge: st.JointBulge, cap: detail < 2, wobble: detail == 0 ? 0.12f : 0f, seed: rng.Next());
                continue;
            }
            float minR = detail == 0 ? st.DrawMinRadius : detail == 1 ? st.Lod1MinRadius : st.FarMinRadius >= 0 ? st.FarMinRadius : MathF.Max(0.02f, st.Lod1MinRadius * 2);
            if (s.Ra < minR) continue;
            int n = detail == 2 ? 3 : s.Ra > 0.03f ? (detail == 0 ? 6 : 4) : s.Ra > 0.01f ? (detail == 0 ? 4 : 3) : 3;
            float t0 = Math.Clamp(s.A.Y / height, 0, 1), t1 = Math.Clamp(s.B.Y / height, 0, 1);
            int from = ends.TryGetValue(s.A, out var e) && e.Sides == n ? e.Ring : -1;
            if (from >= 0) ends.Remove(s.A);   // a fork's second child starts its own ring
            int ring = Shapes.Tube(m, s.A, s.B, s.Ra, s.Rb, n, s.Side, Vector4.Lerp(st.BarkBase, st.BarkTip, t0), Vector4.Lerp(st.BarkBase, st.BarkTip, t1),
                Sway(s.A), Sway(s.B), AtlasCell.Bark, from: from);
            if (ring >= 0) ends[s.B] = (ring, n);
        }
        var leaves = sk.Leaves;
        if (detail > 0 && leaves.Count > 12)
        {
            // the lighter meshes: half (a quarter of) the clusters, each larger, so the crown keeps its mass
            // (a third and a sixth for the leafiest crowns)
            bool leafy = leaves.Count > 60;
            int every = detail == 1 ? (leafy ? 3 : 2) : (leafy ? 6 : 4);
            float grow = detail == 1 ? (leafy ? 1.45f : 1.3f) : (leafy ? 2.0f : 1.75f);
            var kept = new List<Leaf>();
            for (int i = 0; i < leaves.Count; i++) if (i % every == 0) kept.Add(leaves[i] with { Size = leaves[i].Size * grow });
            leaves = kept;
        }
        foreach (var leaf in leaves)
        {
            var l = leaf.Pos.Y < leaf.Size * 0.5f ? leaf with { Pos = leaf.Pos with { Y = leaf.Size * 0.5f } } : leaf;
            var col = Vary(st.LeafColour, ref rng, st.LeafVary);
            // a little darker inside the crown, lighter at its rim (the leaves themselves: older, shaded leaves are darker; the light lost in there is the baked occlusion's)
            float inner = Math.Clamp(Vector3.Distance(l.Pos, crown) / (height * 0.6f), 0.75f, 1.05f);
            col = new Vector4(col.X * inner, col.Y * inner, col.Z * inner, 1);
            var up = Turtle.Rotate(l.Up, l.Dir, rng.Range(0, MathF.Tau));
            Shapes.Cards(m, l.Pos, l.Dir, up, l.Size, st.Leaf, col, Sway(l.Pos) * 1.1f, crown, st.LeafPlanes, roundness: st.Roundness, cap: st.Caps && detail == 0 && MathF.Abs(l.Dir.Y) > 0.5f);
        }
        foreach (var p in sk.Pads)
        {
            var col = Vary(st.PadColour, ref rng, 0.07f);
            var centre = p.Base + p.Dir * (p.Length * 0.5f);
            var side = Vector3.Cross(p.Normal, p.Dir);
            Shapes.Ellipsoid(m, centre, p.Dir, side, p.Length * 0.5f, p.Width * 0.5f, MathF.Max(0.012f, p.Length * 0.07f),
                detail == 0 ? 4 : 3, detail == 0 ? 8 : detail == 1 ? 6 : 4, col, Sway(centre) * 0.3f, AtlasCell.PadSkin);
        }
        if (detail == 0 || detail == 1 && sk.Knobs.Count < 8)
            foreach (var k in sk.Knobs)
            {
                var col = Vary(st.KnobColour, ref rng, 0.1f);
                float r = k.Size * st.KnobScale;
                Shapes.Ellipsoid(m, k.Pos + k.Dir * r * 0.8f, k.Dir, Shapes.AnyPerpendicular(k.Dir), r * 1.25f, r, r, 3, 5, col * 1.2f, Sway(k.Pos), AtlasCell.Bark);
            }
        PressOntoGround(m);
        return m;
    }

    /// <summary>Whatever reaches under the ground (a drooping twig's cards, a hanging blade) is pressed flat just below it.</summary>
    private static void PressOntoGround(PlantMesh m, float floor = -0.08f)
    {
        for (int i = 0; i < m.Positions.Count; i++)
            if (m.Positions[i].Y < floor) m.Positions[i] = m.Positions[i] with { Y = floor + (m.Positions[i].Y - floor) * 0.2f };
    }

    /// <summary>An axiom of <paramref name="n"/> stems spread round the base: each rolled by the golden angle, leaned by a random amount in [lean0, lean1], then <paramref name="stem"/>.</summary>
    private static string Fan(int n, float lean0, float lean1, string stem, ref VegRng rng, float spread = 0f)
    {
        var sb = new StringBuilder();
        float roll = rng.Range(0, 360);
        for (int i = 0; i < n; i++)
        {
            roll += 137.5f + rng.Range(-15, 15);
            sb.Append('[').Append(LSystem.Cmd('/', roll % 360));
            if (spread > 0)
            {
                // start the stem off the centre: out along the ground, then up again
                sb.Append(LSystem.Cmd('&', 90)).Append(LSystem.Cmd('f', spread * MathF.Sqrt(rng.Float()))).Append(LSystem.Cmd('^', 90));
            }
            sb.Append(LSystem.Cmd('&', rng.Range(lean0, lean1))).Append(stem).Append(']');
        }
        return sb.ToString();
    }

    // ---- the L-system species ---------------------------------------------------------------------------

    /// <summary>The creosote's grammar: a vase of thin upright stems, forking now and then, foliage toward the ends.</summary>
    public static LSystem CreosoteSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(5 + rng.Int(4), 8, 34, "!(1)FB", ref rng),
        Productions =
        [
            new('B', 0.24f, "F'[+(26)!B]/(90)B"),
            new('B', 0.21f, "F'[-(26)!B]/(90)B"),
            new('B', 0.25f, "F'B"),
            new('B', 0.30f, "F[&(55)~L]'B"),
        ],
        Finish = { ['B'] = "F[&(35)L]L" },
    };

    private static PlantMesh Creosote(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var sys = CreosoteSystem(ref rng);
        var program = sys.Rewrite(4, ref rng);
        var t = new Turtle { Angle = 22, Jitter = 9, Length = 0.17f, LengthDecay = 0.93f, Radius = 0.011f, WidthDecay = 0.8f, Taper = 0.9f,
            MinRadius = 0.0025f, LeafSize = 0.26f, LeafDecay = 0.85f, Tropism = Vector3.UnitY, Susceptibility = 0.5f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(128, 118, 104), BarkTip = Srgb(146, 134, 110), Leaf = AtlasCell.Creosote, LeafColour = Srgb(245, 245, 236), LeafVary = 0.1f,
            DrawMinRadius = 0.0035f, Lod1MinRadius = 0.008f, Roundness = 0.6f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The four-wing saltbush's grammar: many stems out at wide angles curling up into a dense rounded mound, foliage all along.</summary>
    public static LSystem SaltbushSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(9 + rng.Int(5), 25, 70, "!(1)B", ref rng),
        Productions =
        [
            new('B', 0.4f, "F[+(38)!'L B]'B"),
            new('B', 0.35f, "F[-(38)!'L B]/(90)'B"),
            new('B', 0.25f, "FL'B"),
        ],
        Finish = { ['B'] = "L" },
    };

    private static PlantMesh Saltbush(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = SaltbushSystem(ref rng).Rewrite(3, ref rng);
        var t = new Turtle { Angle = 35, Jitter = 12, Length = 0.19f, LengthDecay = 0.86f, Radius = 0.012f, Taper = 0.86f, MinRadius = 0.003f,
            LeafSize = 0.42f, LeafDecay = 0.95f, Tropism = Vector3.UnitY, Susceptibility = 1.1f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(150, 140, 118), BarkTip = Srgb(170, 162, 140), Leaf = AtlasCell.Saltbush, LeafColour = Srgb(250, 250, 246), LeafVary = 0.07f,
            DrawMinRadius = 0.006f, Lod1MinRadius = 0.02f, Roundness = 0.85f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The mesquite's grammar: a few stout stems spreading wide, zig-zagging at each node, the ends drooping under feathery tufts.</summary>
    public static LSystem MesquiteSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(3 + rng.Int(4), 28, 55, "!(1)F'B", ref rng),
        Productions =
        [
            new('B', 0.40f, "F+(17)[&(42)!'B]F-(17)~L'B"),
            new('B', 0.30f, "F-(17)[&(38)/(180)!'B]F+(17)'B"),
            new('B', 0.30f, "F+(15)LF-(15)'B"),
        ],
        Finish = { ['B'] = "[&(25)L]F(0.12)L" },
    };

    private static PlantMesh Mesquite(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = MesquiteSystem(ref rng).Rewrite(4, ref rng);
        var t = new Turtle { Angle = 28, Jitter = 8, Length = 0.3f, LengthDecay = 0.87f, Radius = 0.045f, WidthDecay = 0.68f, Taper = 0.9f, MinRadius = 0.004f,
            LeafSize = 0.42f, LeafDecay = 0.92f, Tropism = -Vector3.UnitY, Susceptibility = 0.22f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(92, 78, 66), BarkTip = Srgb(120, 100, 80), Leaf = AtlasCell.Mesquite, LeafColour = Srgb(246, 250, 236), LeafVary = 0.08f,
            DrawMinRadius = 0.005f, Lod1MinRadius = 0.014f, Roundness = 0.7f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The tree cholla's grammar: a short woody trunk, then joints off joints in whorls of two or three, turning upward, yellow fruit at some ends.</summary>
    public static LSystem ChollaSystem(ref VegRng rng) => new()
    {
        Axiom = $"F({Num(rng.Range(0.12f, 0.3f))})/(40)F(0.14)!(0.75)'(1)C",
        Productions =
        [
            new('C', 0.34f, "J'/(137)[&(58)C]/(137)[&(62)C]"),
            new('C', 0.24f, "J'/(120)[&(60)C]/(120)[&(60)C]/(120)C"),
            new('C', 0.32f, "J'/(90)[&(65)C]C"),
            new('C', 0.10f, "J'K"),
        ],
        Finish = { ['C'] = "J" },
    };

    private static PlantMesh Cholla(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = ChollaSystem(ref rng).Rewrite(5, ref rng);
        var t = new Turtle { Angle = 45, Jitter = 10, Length = 0.2f, LengthDecay = 0.96f, LengthJitter = 0.2f, Radius = 0.06f, Taper = 0.95f, MinRadius = 0.035f,
            Tropism = Vector3.UnitY, Susceptibility = 0.7f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(150, 140, 120), BarkTip = Srgb(160, 150, 128), JointColour = Srgb(240, 242, 232), JointTip = Srgb(252, 248, 232),
            KnobColour = Srgb(206, 180, 64), KnobScale = 0.45f, JointBulge = 0.1f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The prickly pear's grammar: a few pads leaning out from the base, more pads growing off their rims in the same plane, rolled a little; purple fruit on some.</summary>
    public static LSystem PricklyPearSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(3 + rng.Int(3), 28, 62, "P Q", ref rng),
        Productions =
        [
            new('Q', 0.26f, "[+(32)/(22)P Q]"),
            new('Q', 0.26f, "[-(32)\\(22)P Q]"),
            new('Q', 0.30f, "[+(36)/(18)P Q][-(36)\\(18)P Q]"),
            new('Q', 0.10f, "[+(10)K][-(25)K]"),
            new('Q', 0.08f, ""),
        ],
        Finish = { ['Q'] = "" },
    };

    private static PlantMesh PricklyPear(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = PricklyPearSystem(ref rng).Rewrite(4, ref rng);
        var t = new Turtle { Angle = 32, Jitter = 9, Length = 0.25f, LengthDecay = 0.97f, PadWidth = 0.8f, Floor = 0.02f, Tropism = Vector3.UnitY, Susceptibility = 1.2f };
        var sk = t.Run(program, ref rng);
        // fruit sit on a pad's rim, small
        for (int i = 0; i < sk.Knobs.Count; i++) sk.Knobs[i] = sk.Knobs[i] with { Size = 0.028f };
        var st = new Style { PadColour = Srgb(242, 246, 238), KnobColour = Srgb(118, 34, 64), KnobScale = 1f };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The salt cedar's grammar: a broom of many slender reddish stems, arching at the ends under feathery sprays.</summary>
    public static LSystem TamariskSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(8 + rng.Int(4), 6, 30, "!(1)FB", ref rng, spread: 0.35f),
        Productions =
        [
            new('B', 0.35f, "F[+(30)!'BL]'B"),
            new('B', 0.35f, "F[-(30)!'BL]/(90)'B"),
            new('B', 0.30f, "FL'B"),
        ],
        Finish = { ['B'] = "LF(0.15)L" },
    };

    private static PlantMesh Tamarisk(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = TamariskSystem(ref rng).Rewrite(4, ref rng);
        var t = new Turtle { Angle = 30, Jitter = 10, Length = 0.36f, LengthDecay = 0.9f, Radius = 0.022f, WidthDecay = 0.7f, Taper = 0.9f, MinRadius = 0.003f,
            LeafSize = 0.62f, LeafDecay = 0.95f, Tropism = -Vector3.UnitY, Susceptibility = 0.12f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(96, 52, 40), BarkTip = Srgb(130, 70, 54), Leaf = AtlasCell.Tamarisk, LeafColour = Srgb(248, 248, 244), LeafVary = 0.08f,
            DrawMinRadius = 0.006f, Lod1MinRadius = 0.014f, Roundness = 0.7f, Caps = false,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The coyote willow's grammar: a clonal clump of slender stems standing nearly upright off a small patch of ground, short twigs, narrow leaves on the upper part.</summary>
    public static LSystem WillowSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(7 + rng.Int(5), 0, 18, "!(1)F(0.25)A", ref rng, spread: 0.9f),
        Productions =
        [
            new('A', 0.30f, "F[+(28)L]/(137)'A"),
            new('A', 0.30f, "F[-(28)L][+(20)/(90)L]/(137)'A"),
            new('A', 0.25f, "F[+(35)!F(0.2)L]/(137)'A"),
            new('A', 0.15f, "FL'A"),
        ],
        Finish = { ['A'] = "LF(0.15)L" },
    };

    private static PlantMesh Willow(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = WillowSystem(ref rng).Rewrite(6, ref rng);
        var t = new Turtle { Angle = 24, Jitter = 6, Length = 0.32f, LengthDecay = 0.97f, Radius = 0.013f, Taper = 0.94f, MinRadius = 0.003f,
            LeafSize = 0.46f, LeafDecay = 0.95f, Tropism = Vector3.UnitY, Susceptibility = 0.3f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(110, 84, 54), BarkTip = Srgb(150, 110, 62), Leaf = AtlasCell.Willow, LeafColour = Srgb(246, 248, 240), LeafVary = 0.07f,
            DrawMinRadius = 0.005f, Lod1MinRadius = 0.01f, Roundness = 0.6f, Caps = false,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    // ---- the parametric species --------------------------------------------------------------------------

    public enum YuccaKind { Soaptree, Soapweed, Torrey }

    /// <summary>
    /// A yucca: a head of narrow stiff blades radiating over most of a sphere (shorter toward the top, the oldest
    /// hanging), on a trunk shagged with dead leaves (the soaptree; the Torrey yucca's taller, its blades fewer and
    /// wider), and sometimes a flower stalk.
    /// </summary>
    private static PlantMesh Yucca(ulong seed, int detail, YuccaKind kind)
    {
        var rng = new VegRng(seed);
        // whether it flowers, decided apart from the blades (whose number changes with the detail), so every detail agrees
        var bloom = rng.Fork(0xF10);
        var m = new PlantMesh();
        bool soaptree = kind == YuccaKind.Soaptree, torrey = kind == YuccaKind.Torrey;
        float trunk = soaptree ? rng.Range(0.4f, 1.4f) : torrey ? rng.Range(0.9f, 2.1f) : 0f;
        var green = soaptree ? Srgb(150, 164, 96) : torrey ? Srgb(104, 124, 66) : Srgb(132, 152, 112);
        var tip = soaptree ? Srgb(120, 140, 80) : torrey ? Srgb(92, 112, 60) : Srgb(110, 132, 100);
        var dead = Srgb(150, 136, 110);
        var bark = Srgb(132, 120, 100);
        int heads = soaptree && trunk > 1.1f && rng.Float() < 0.45f ? 2 : torrey && trunk > 1.3f && rng.Float() < 0.6f ? 2 : 1;
        float top = trunk + 0.7f;
        if (trunk > 0)
        {
            Shapes.Tube(m, Vector3.Zero, new Vector3(0, trunk, 0), torrey ? 0.12f : 0.085f, torrey ? 0.09f : 0.07f, detail == 0 ? 6 : 4, Vector3.UnitX, bark, bark, 0, 0.3f, AtlasCell.Bark);
            // the skirt of dead leaves hanging round the upper trunk
            int skirt = (detail == 0 ? 34 : detail == 1 ? 14 : 0) * (torrey ? 5 : 2) / 2;
            float skirtW = torrey ? 0.05f : 0.02f;
            for (int i = 0; i < skirt; i++)
            {
                float y = trunk - rng.Range(0.02f, torrey ? trunk * 0.85f : MathF.Min(0.6f, trunk * 0.6f));
                float az = i * 2.39996f + rng.Range(-0.2f, 0.2f);
                var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
                var dir = Vector3.Normalize(outv * (torrey ? 0.7f : 0.35f) - Vector3.UnitY);
                var dc = torrey ? Srgb(168, 156, 128) : dead;
                Shapes.Blade(m, new Vector3(0, y, 0) + outv * (torrey ? 0.1f : 0.08f), dir, outv * 0.08f, torrey ? rng.Range(0.4f, 0.6f) : rng.Range(0.3f, 0.45f), skirtW, 1,
                    Vary(dc, ref rng, 0.08f), Vary(dc, ref rng, 0.1f) * 0.85f, y / top * 0.3f, y / top * 0.3f, AtlasCell.DeadLeaves, crown: new Vector3(0, y + 0.1f, 0));
            }
        }
        for (int h = 0; h < heads; h++)
        {
            var centre = new Vector3(0, MathF.Max(0.1f, trunk), 0);
            if (heads > 1)
            {
                // a branched old plant: two heads on short limbs out of the trunk's top
                float az = rng.Range(0, MathF.Tau) + h * MathF.PI;
                var limbEnd = centre + new Vector3(MathF.Cos(az) * 0.3f, 0.35f, MathF.Sin(az) * 0.3f);
                Shapes.Tube(m, centre - new Vector3(0, 0.1f, 0), limbEnd, 0.07f, 0.06f, detail == 0 ? 5 : 3, Vector3.UnitX, bark, bark, 0.25f, 0.35f, AtlasCell.Bark);
                centre = limbEnd;
            }
            int n = (soaptree ? 160 : torrey ? 140 : 64) / heads;
            if (detail > 0) n = n * (detail == 1 ? 2 : 1) / 5;
            float len0 = soaptree ? 0.7f : torrey ? 0.85f : 0.52f, width = torrey ? (detail == 0 ? 0.048f : 0.062f) : 0.024f;
            for (int i = 0; i < n; i++)
            {
                // directions spread evenly over the sphere from 50 degrees below the horizon up (a Fibonacci spiral)
                float u = (i + 0.5f) / n;
                float low = trunk > 0 ? (torrey ? -0.6f : -0.77f) : -0.12f;   // a trunkless rosette's lowest blades lie on the ground
                float elev = MathF.Asin(low + (1 - low) * u);
                float az = i * 2.39996f + rng.Range(-0.1f, 0.1f);
                var dir = new Vector3(MathF.Cos(elev) * MathF.Cos(az), MathF.Sin(elev), MathF.Cos(elev) * MathF.Sin(az));
                float len = len0 * rng.Range(0.8f, 1.15f) * (1.05f - 0.35f * MathF.Max(0, dir.Y));
                bool old = dir.Y < -0.2f;
                var c0 = old ? Vector4.Lerp(green, dead, 0.55f) : green;
                var c1 = old ? Vector4.Lerp(tip, dead, 0.7f) : tip;
                float sw = (centre.Y + 0.2f) / (top + 0.2f);
                Shapes.Blade(m, centre + dir * 0.04f, dir, new Vector3(0, torrey ? -0.03f : -0.06f, 0), len, width, detail == 0 ? 2 : 1,
                    Vary(c0, ref rng, 0.06f), Vary(c1, ref rng, 0.06f), sw * 0.7f, sw, AtlasCell.Blade, keel: torrey ? 0.25f : 0f, tipWidth: 0.002f, belly: torrey ? 0.35f : 0f,
                    crown: trunk > 0 ? centre : centre - new Vector3(0, 0.25f, 0), roundness: 0.7f);
            }
            // the flower stalk: a bare stem, then a panicle of creamy bells (the Torrey yucca's short and dense, in the head)
            if (h == 0 && bloom.Float() < (soaptree ? 0.45f : torrey ? 0.35f : 0.35f))
            {
                float stalk = soaptree ? bloom.Range(1.0f, 1.8f) : torrey ? bloom.Range(0.6f, 0.95f) : bloom.Range(0.5f, 0.9f);
                var s0 = centre + new Vector3(0, 0.15f, 0);
                var lean = new Vector3(bloom.Range(-0.08f, 0.08f), 1, bloom.Range(-0.08f, 0.08f));
                var s1 = s0 + Vector3.Normalize(lean) * stalk;
                var stalkCol = Srgb(130, 120, 80);
                Shapes.Tube(m, s0, s1, 0.018f, 0.008f, 4, Vector3.UnitX, stalkCol, stalkCol, 0.6f, 1.1f, AtlasCell.Bark);
                int cards = detail == 0 ? 5 : detail == 1 ? 2 : 1;
                float from = torrey ? 0.25f : 0.5f, size = torrey ? 0.55f : 0.32f;
                for (int k = 0; k < cards; k++)
                {
                    float t = from + (1 - from) * k / cards;
                    var p = Vector3.Lerp(s0, s1, t);
                    float az = k * 2.4f;
                    var d = Vector3.Normalize(new Vector3(MathF.Cos(az) * 0.6f, 1, MathF.Sin(az) * 0.6f));
                    Shapes.Cards(m, p, d, Vector3.UnitX, stalk * size * (1.2f - 0.4f * t), AtlasCell.YuccaFlowers, Srgb(250, 246, 226), 0.9f, s1 - new Vector3(0, stalk * 0.25f, 0), 2, roundness: 0.5f);
                }
            }
        }
        PressOntoGround(m);
        return m;
    }

    public enum GrassKind { Grama, Tobosa, Sacaton, Dropseed, ChinoGrama, Sideoats, Bluestem }

    /// <summary>A bunchgrass's make-up: its blades (count, length, width, how upright), its living and dead colours, how much of it is dead, its crown.</summary>
    private readonly record struct GrassForm(int Blades, float Length, float Width, float Upright, Vector4 Green, Vector4 Dry, float Dead, float Crown, int Stems);

    private static GrassForm FormOf(GrassKind kind) => kind switch
    {
        // blue grama: a low curly tuft, grey-green going straw, its leaves arching over to the ground
        GrassKind.Grama => new(54, 0.22f, 0.010f, 0.4f, Srgb(124, 132, 92), Srgb(190, 172, 126), 0.4f, 0.06f, 7),
        // tobosa: coarse, stiff, upright, much of it grey dead stuff
        GrassKind.Tobosa => new(54, 0.36f, 0.011f, 0.75f, Srgb(126, 134, 100), Srgb(178, 166, 132), 0.5f, 0.07f, 0),
        GrassKind.Dropseed => new(48, 0.3f, 0.010f, 0.55f, Srgb(128, 138, 94), Srgb(192, 178, 132), 0.38f, 0.06f, 5),
        GrassKind.ChinoGrama => new(44, 0.15f, 0.009f, 0.35f, Srgb(126, 136, 96), Srgb(194, 178, 134), 0.45f, 0.05f, 9),
        GrassKind.Sideoats => new(44, 0.28f, 0.012f, 0.55f, Srgb(116, 136, 86), Srgb(182, 162, 118), 0.38f, 0.06f, 6),
        // little bluestem: an upright blue-green bunch turning bronze
        GrassKind.Bluestem => new(58, 0.44f, 0.010f, 0.85f, Srgb(108, 130, 118), Srgb(180, 126, 84), 0.45f, 0.07f, 7),
        // alkali sacaton: a big tussock, pale
        _ => new(70, 0.62f, 0.010f, 0.65f, Srgb(136, 140, 98), Srgb(198, 182, 138), 0.45f, 0.11f, 9),
    };

    /// <summary>
    /// A bunchgrass: one tuft, or a big one with small ones beside it, or a little group (the variants differ). Each tuft
    /// is a crown of old dead thatch and blades of every length from it, the inner ones upright, the outer arching out
    /// and over under their weight, some lying on the ground; a mix of grey-green living blades with paler dry tips and
    /// straw dead ones. Blue and chino grama add their eyebrow seed flags, sacaton and dropseed airy panicles, sideoats
    /// its stems hung on one side with small spikes, little bluestem its fluffy silver seed heads. Farther away each tuft
    /// is crossed cards of a painted tuft, sized to the full tuft.
    /// </summary>
    private static PlantMesh Grass(ulong seed, int detail, GrassKind kind)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        var f = FormOf(kind);
        // the tufts of this variant: (where, how big)
        var tufts = new List<(Vector3 At, float Scale)>();
        float layout = rng.Float();
        if (layout < 0.4f) tufts.Add((Vector3.Zero, 1f));
        else if (layout < 0.75f)
        {
            tufts.Add((Vector3.Zero, 1f));
            int k = 1 + rng.Int(2);
            float a0 = rng.Range(0, MathF.Tau);
            for (int i = 0; i < k; i++)
            {
                float a = a0 + i * rng.Range(1.6f, 2.6f), r = f.Length * rng.Range(0.9f, 1.5f);
                tufts.Add((new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r), rng.Range(0.35f, 0.6f)));
            }
        }
        else
        {
            float a0 = rng.Range(0, MathF.Tau);
            for (int i = 0; i < 3; i++)
            {
                float a = a0 + i * 2.1f + rng.Range(-0.3f, 0.3f), r = f.Length * rng.Range(0.45f, 0.8f);
                tufts.Add((new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r), i == 0 ? 0.85f : rng.Range(0.5f, 0.75f)));
            }
        }
        foreach (var (at, sc) in tufts)
        {
            var trng = new VegRng(rng.Next());
            if (detail == 0) { Tuft(m, ref trng, f, kind, at, sc); continue; }
            // the far tiers: the tuft's own size from its full mesh, cards of the painted tuft that fill it
            var full = new PlantMesh();
            var probe = trng;
            Tuft(full, ref probe, f, kind, Vector3.Zero, sc, blades: true);
            // most of it, not the odd blade lying out on the ground
            var horiz = full.Positions.Select(p => new Vector2(p.X, p.Z).Length()).Order().ToList();
            var high = full.Positions.Select(p => p.Y).Order().ToList();
            float w = 2.1f * horiz[(int)(horiz.Count * 0.9f)], h = high[(int)(high.Count * 0.95f)] * 1.1f;
            // the main tuft crossed, the small ones beside it a single card (in the far tier too: they would vanish at its start)
            bool main = sc >= 0.7f;
            int cards = main ? (detail == 1 ? 3 : 2) : 1;
            // the painted tuft's hues about this (its straw and green blades)
            var col = Vector4.Lerp(f.Green, f.Dry, 0.3f) * 0.95f;
            col.W = 1;
            float az0 = trng.Range(0, MathF.PI);
            for (int i = 0; i < cards; i++)
            {
                float az = az0 + i * MathF.PI / cards + trng.Range(-0.15f, 0.15f);
                Shapes.SplitCard(m, at, new Vector3(MathF.Cos(az), 0, MathF.Sin(az)), Vector3.UnitY, w, h, AtlasCell.GrassTuft,
                    Vary(col, ref trng, 0.06f), 0.9f, at - new Vector3(0, h * 0.8f, 0), 0.45f);
            }
        }
        // the dead thatch and the arching blades may reach below the ground: laid on it
        for (int i = 0; i < m.Positions.Count; i++)
            if (m.Positions[i].Y < 0.006f) m.Positions[i] = m.Positions[i] with { Y = 0.006f + (m.Positions[i].Y - 0.006f) * 0.04f };
        return m;
    }

    /// <summary>One tuft of a bunchgrass at <paramref name="at"/>, scaled by <paramref name="sc"/> (<see cref="Grass"/>); only its blades with <paramref name="blades"/> (for its size).</summary>
    private static void Tuft(PlantMesh m, ref VegRng rng, GrassForm f, GrassKind kind, Vector3 at, float sc, bool blades = false)
    {
        float crown = f.Crown * MathF.Sqrt(sc), len = f.Length * sc;
        int n = Math.Max(8, (int)(f.Blades * MathF.Sqrt(sc)));
        var grey = Srgb(150, 144, 128);
        for (int i = 0; i < n; i++)
        {
            float az = i * 2.39996f + rng.Range(-0.4f, 0.4f);
            float rr = MathF.Sqrt(rng.Float());
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            // lying over: some blades low and flat on the ground; the rest steeper toward the middle of the crown
            bool lying = rng.Float() < 0.13f;
            float elev = lying ? rng.Range(0.2f, 0.5f)
                : (1.45f - rr * (0.85f - f.Upright * 0.45f)) + rng.Range(-0.18f, 0.18f);
            elev = MathF.Min(elev, 1.5f);
            // the body of the tuft, short blades packed into a cushion, and the long ones arching out of it; the
            // outer, longer blades bend over most
            bool body = !lying && rng.Float() < 0.6f;
            float l = len * (body ? rng.Range(0.28f, 0.65f) : rng.Range(0.7f, 1.15f)) * (lying ? 1.1f : 1f);
            if (body) elev = MathF.Min(1.5f, elev + 0.15f);
            float bend = rng.Range(0.25f, 0.85f) * (1.1f - f.Upright * 0.7f) * MathF.Pow(l / len, 0.7f) * (0.6f + 0.6f * rr) + (lying ? 0.35f : 0f);
            var side = Vector3.Cross(outv, Vector3.UnitY);
            var dir = Vector3.Normalize(outv * MathF.Cos(elev) + Vector3.UnitY * MathF.Sin(elev));
            var droop = -Vector3.UnitY * bend + outv * (bend * 0.25f) + side * rng.Range(-0.25f, 0.25f);
            bool dead = lying || rng.Float() < f.Dead * (0.55f + 0.9f * rr);
            Vector4 c0, c1;
            if (dead)
            {
                var d = Vary(f.Dry, ref rng, 0.09f);
                if (rng.Float() < 0.4f) d = Vector4.Lerp(d, grey, 0.5f);
                c0 = d * 0.82f; c1 = d * 1.06f;
            }
            else
            {
                var g = Vary(f.Green, ref rng, 0.08f);
                c0 = g * 0.9f; c1 = Vector4.Lerp(g, f.Dry, rng.Range(0.35f, 0.75f)) * 1.05f;
            }
            c0.W = c1.W = 1;
            Shapes.Blade(m, at + outv * (crown * rr), dir, droop, l, f.Width * rng.Range(0.75f, 1.3f) * MathF.Sqrt(sc), body ? 2 : 3,
                c0, c1, 0f, 1f, AtlasCell.Grass, twist: rng.Range(-1.4f, 1.4f), foliage: Shapes.ThinBlade, bladeId: rng.Float());
        }
        if (blades) return;
        // the crown: last years' leaves, short, broken and grey-brown, packed round the base
        int thatch = (int)(n * 0.4f);
        for (int i = 0; i < thatch; i++)
        {
            float az = rng.Range(0, MathF.Tau), rr = MathF.Sqrt(rng.Float()) * 1.25f;
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            float elev = rng.Range(0.3f, 1.25f);
            var dir = Vector3.Normalize(outv * MathF.Cos(elev) + Vector3.UnitY * MathF.Sin(elev));
            var col = Vary(Vector4.Lerp(f.Dry, grey, rng.Range(0.3f, 0.8f)), ref rng, 0.08f) * 0.78f;
            col.W = 1;
            Shapes.Blade(m, at + outv * (crown * rr), dir, -Vector3.UnitY * rng.Range(0.1f, 0.5f), len * rng.Range(0.15f, 0.35f), f.Width * 1.8f * MathF.Sqrt(sc), 2,
                col * 0.85f, col, 0f, 0.3f, AtlasCell.Grass, twist: rng.Range(-1f, 1f), foliage: Shapes.ThinBlade, bladeId: rng.Float());
        }
        if (sc < 0.5f) return;
        // the flowering stems, from the crown, slightly arching under the head
        bool flags = kind is GrassKind.Grama or GrassKind.ChinoGrama;
        var stemCol = kind == GrassKind.Bluestem ? Srgb(150, 112, 84) : Srgb(162, 148, 110);
        int stems = (int)MathF.Round(f.Stems * sc);
        for (int i = 0; i < stems; i++)
        {
            float az = rng.Range(0, MathF.Tau);
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            var dir = Vector3.Normalize(outv * rng.Range(0.12f, 0.4f) + Vector3.UnitY);
            float h = sc * kind switch
            {
                GrassKind.Grama => rng.Range(0.28f, 0.42f), GrassKind.ChinoGrama => rng.Range(0.2f, 0.3f),
                GrassKind.Dropseed => rng.Range(0.45f, 0.72f), GrassKind.Sideoats => rng.Range(0.5f, 0.78f),
                GrassKind.Bluestem => rng.Range(0.6f, 0.92f), _ => rng.Range(0.8f, 1.05f),
            };
            var droop = outv * 0.1f - Vector3.UnitY * rng.Range(0.05f, 0.15f);
            var start = at + outv * (crown * 0.4f);
            Vector3 Along(float t) => start + dir * (h * t) + droop * (h * t * t);
            var top = Along(1);
            var tipDir = Vector3.Normalize(dir + droop * 2);
            // widened far off like the blades but never thinned out (id 0): the seed heads stay on their stems
            Shapes.Blade(m, start, dir, droop, h, 0.0045f, 3, stemCol * 0.85f, stemCol, 0, 1.1f, AtlasCell.Grass, tipWidth: 0.003f, foliage: Shapes.ThinBlade, bladeId: 0f);
            var sideV = Vector3.Normalize(Vector3.Cross(tipDir, Vector3.UnitY) + new Vector3(1e-4f));
            if (flags)
            {
                // the "eyebrow": a curved one-sided spike leaving the stem's tip sideways (chino grama: two, one below the other)
                for (int k = 0; k < (kind == GrassKind.ChinoGrama ? 2 : 1); k++)
                {
                    var p = Along(1 - 0.12f * k);
                    var sd = k == 0 ? sideV : -sideV;
                    Shapes.Blade(m, p, Vector3.Normalize(sd + tipDir * 0.5f), new Vector3(0, -0.5f, 0), 0.045f, 0.011f, 2,
                        Srgb(132, 104, 82), Srgb(168, 140, 104), 1.1f, 1.2f, AtlasCell.Grass, twist: 0.6f, foliage: Shapes.ThinBlade, bladeId: rng.Float());
                }
            }
            else if (kind == GrassKind.Sideoats)
            {
                // the oats: small spikes hanging from one side of the upper stem, all turned the same way
                for (int k = 0; k < 12; k++)
                {
                    float t = 0.5f + 0.5f * k / 12f;
                    var hang = Vector3.Normalize(sideV * 0.55f - Vector3.UnitY + dir * 0.1f);
                    Shapes.Blade(m, Along(t), hang, Vector3.Zero, 0.024f, 0.01f, 1, Srgb(156, 124, 100), Srgb(182, 146, 108), 1.1f, 1.15f, AtlasCell.Grass, foliage: Shapes.ThinBlade);
                }
            }
            else if (kind == GrassKind.Bluestem)
            {
                for (int k = 0; k < 2; k++)
                    Shapes.Cards(m, Along(0.8f - 0.16f * k), tipDir, Vector3.UnitX, 0.12f * sc, AtlasCell.Panicle, Srgb(236, 232, 222), 1.1f, Along(0.5f), 1, roundness: 0.3f);
            }
            else
            {
                float pan = (kind == GrassKind.Dropseed ? 0.22f : 0.32f) * sc;
                Shapes.Cards(m, top - tipDir * (pan * 0.78f), tipDir, Vector3.UnitX, pan, AtlasCell.Panicle, kind == GrassKind.Dropseed ? Srgb(232, 220, 192) : Srgb(244, 238, 222), 1.1f, top - tipDir * (pan * 1.25f), 1, roundness: 0.3f);
            }
        }
    }

    /// <summary>A tumbleweed: a round tangle on crossed alpha cards, three or four of them about a centre sitting on the ground.</summary>
    private static PlantMesh Tumbleweed(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float r = rng.Range(0.25f, 0.4f);
        var centre = new Vector3(0, r * 0.85f, 0);
        int cards = detail == 0 ? 4 : 2;
        bool green = rng.Float() < 0.4f;
        var col = green ? Srgb(214, 226, 196) : Srgb(250, 244, 230);
        for (int i = 0; i < cards; i++)
        {
            var axis = Vector3.Normalize(new Vector3(rng.Bell(), 1.5f, rng.Bell()));
            var up = Turtle.Rotate(Vector3.UnitX, axis, i * MathF.PI / cards);
            Shapes.Cards(m, centre - axis * r, axis, up, 2 * r, AtlasCell.Tumbleweed, Vary(col, ref rng, 0.06f), 0.6f, centre - new Vector3(0, r, 0), 1, lift: 0.5f, roundness: 0.8f);
        }
        return m;
    }

    /// <summary>
    /// A tumbleweed broken off and rolling (World/Tumbleweeds.cs): a ball of radius 1 of the same tangle, six cards
    /// whose faces point every way (an icosahedron's axes, turned at random), so it reads round from any side as it
    /// turns; straw, dead and dry. Its occlusion is baked in the air (no ground under it: it rolls), centred 2 m up.
    /// </summary>
    public static PlantMesh RollingTumbleweed(ulong seed)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        var centre = new Vector3(0, 2, 0);
        const float phi = 1.618034f;
        Vector3[] axes = [new(0, 1, phi), new(0, 1, -phi), new(1, phi, 0), new(-1, phi, 0), new(phi, 0, 1), new(-phi, 0, 1)];
        var turn = Quaternion.Normalize(new Quaternion(rng.Bell(), rng.Bell(), rng.Bell(), 1));
        var col = Srgb(222, 208, 186);
        foreach (var a0 in axes)
        {
            var n = Vector3.Normalize(Vector3.Transform(a0, turn));
            var dir = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            float size = 2f * rng.Range(0.92f, 1.05f);
            Shapes.Cards(m, centre - dir * (size * 0.5f), dir, n, size, AtlasCell.Tumbleweed, Vary(col, ref rng, 0.07f), 0.6f, centre, 1, lift: 0.5f, roundness: 0.8f);
        }
        PlantShading.Bake(m);
        return m;
    }

    /// <summary>Broom snakeweed: a low dome of fine upright stems topped with yellow flower heads, on crossed alpha cards.</summary>
    private static PlantMesh Snakeweed(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float h = rng.Range(0.3f, 0.5f);
        int cards = detail == 0 ? 3 : 2;
        var col = Srgb(246, 248, 238);
        for (int i = 0; i < cards; i++)
        {
            float az = i * MathF.PI / cards + rng.Range(-0.2f, 0.2f);
            var up = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            Shapes.Cards(m, Vector3.Zero, Vector3.UnitY, up, h * 1.6f, AtlasCell.Forb, Vary(col, ref rng, 0.06f), 0.8f, new Vector3(0, -h * 0.4f, 0), 1, lift: 0.5f, roundness: 0.6f);
        }
        return m;
    }

    // ---- the regions' species (docs/modern-vegetation.md, Regions) ---------------------------------------------

    /// <summary>The sand sagebrush's grammar: many fine stems from the base, forking at narrow angles into an airy rounded shrub, feathery tufts all along.</summary>
    public static LSystem SandSageSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(9 + rng.Int(5), 10, 42, "!(1)B", ref rng),
        Productions =
        [
            new('B', 0.36f, "F[+(30)!'L B]'B"),
            new('B', 0.36f, "F[-(30)!' B]/(90)'B"),
            new('B', 0.28f, "F'B"),
        ],
        Finish = { ['B'] = "[&(20)L]L" },
    };

    private static PlantMesh SandSage(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = SandSageSystem(ref rng).Rewrite(3, ref rng);
        var t = new Turtle { Angle = 28, Jitter = 10, Length = 0.21f, LengthDecay = 0.9f, Radius = 0.009f, Taper = 0.86f, MinRadius = 0.0025f,
            LeafSize = 0.36f, LeafDecay = 0.95f, Tropism = Vector3.UnitY, Susceptibility = 0.6f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(110, 102, 88), BarkTip = Srgb(132, 126, 110), Leaf = AtlasCell.SandSage, LeafColour = Srgb(240, 246, 248), LeafVary = 0.08f,
            DrawMinRadius = 0.004f, Lod1MinRadius = 0.012f, Roundness = 0.65f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The shinnery oak's grammar: a clone of short stems coming up off spreading rhizomes, each forking once or twice under a dense head of lobed leaves.</summary>
    public static LSystem ShinnerySystem(ref VegRng rng) => new()
    {
        Axiom = Fan(14 + rng.Int(9), 10, 42, "!(1)F(0.06)L A", ref rng, spread: 1.2f),
        Productions =
        [
            new('A', 0.3f, "F[+(48)!'L A][&(60)L]'A"),
            new('A', 0.3f, "F[-(48)!'L A]/(90)L'A"),
            new('A', 0.4f, "F[&(55)L]L'A"),
        ],
        Finish = { ['A'] = "[&(30)L]L" },
    };

    private static PlantMesh Shinnery(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = ShinnerySystem(ref rng).Rewrite(3, ref rng);
        var t = new Turtle { Angle = 35, Jitter = 12, Length = 0.16f, LengthDecay = 0.9f, Radius = 0.01f, Taper = 0.88f, MinRadius = 0.003f,
            LeafSize = 0.3f, LeafDecay = 0.95f, Tropism = Vector3.UnitY, Susceptibility = 0.35f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(100, 90, 78), BarkTip = Srgb(120, 108, 90), Leaf = AtlasCell.ShinOak, LeafColour = Srgb(246, 248, 240), LeafVary = 0.1f,
            DrawMinRadius = 0.005f, Lod1MinRadius = 0.011f, Roundness = 0.8f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>The gray oak's grammar (its shrub form on rocky slopes): a few crooked stems, forking often, under a dense crown of small dull leaves.</summary>
    public static LSystem GrayOakSystem(ref VegRng rng) => new()
    {
        Axiom = Fan(4 + rng.Int(4), 12, 40, "!(1)F[&(60)L]'B", ref rng, spread: 0.35f),
        Productions =
        [
            new('B', 0.32f, "F[+(38)!'B]/(137)~L'B"),
            new('B', 0.28f, "F[-(38)!'B]/(137)'B"),
            new('B', 0.22f, "F[&(55)~L]+(18)'B"),
            new('B', 0.18f, "F[+(32)!'B][-(32)!'B]'B"),
        ],
        Finish = { ['B'] = "[&(30)L]F(0.1)L" },
    };

    private static PlantMesh GrayOak(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var program = GrayOakSystem(ref rng).Rewrite(4, ref rng);
        var t = new Turtle { Angle = 30, Jitter = 12, Length = 0.3f, LengthDecay = 0.84f, Radius = 0.04f, WidthDecay = 0.68f, Taper = 0.88f, MinRadius = 0.004f,
            LeafSize = 0.46f, LeafDecay = 0.9f, Tropism = Vector3.UnitY, Susceptibility = 0.25f };
        var sk = t.Run(program, ref rng);
        var st = new Style
        {
            BarkBase = Srgb(92, 86, 80), BarkTip = Srgb(112, 104, 94), Leaf = AtlasCell.GrayOak, LeafColour = Srgb(240, 246, 236), LeafVary = 0.08f,
            DrawMinRadius = 0.006f, Lod1MinRadius = 0.016f, Roundness = 0.8f,
        };
        return FromSkeleton(sk, st, detail, ref rng);
    }

    /// <summary>
    /// The ocotillo's grammar: 8-20 long slender canes from one crown, leaning out a little each, near unbranched
    /// (a rare low fork); a node every step, with a tuft of small leaves when the plant is in leaf.
    /// </summary>
    public static LSystem OcotilloSystem(ref VegRng rng)
    {
        var sb = new StringBuilder();
        int n = 8 + rng.Int(13);
        float roll = rng.Range(0, 360);
        for (int i = 0; i < n; i++)
        {
            roll += 137.5f + rng.Range(-15, 15);
            // each cane its own length (2 to 5 m), out of a crown about 0.25 m across
            sb.Append('[').Append(LSystem.Cmd('/', roll % 360)).Append(LSystem.Cmd('&', 90)).Append(LSystem.Cmd('f', 0.12f * MathF.Sqrt(rng.Float())))
              .Append(LSystem.Cmd('^', 90)).Append(LSystem.Cmd('&', rng.Range(3, 24))).Append(LSystem.Cmd('\'', rng.Range(0.6f, 1.15f))).Append("!(1)C]");
        }
        return new LSystem
        {
            Axiom = sb.ToString(),
            Productions =
            [
                new('C', 0.96f, "FLC"),
                new('C', 0.04f, "F[+(16)!(0.8)FLC]LC"),
            ],
            Finish = { ['C'] = "FL" },
        };
    }

    private static PlantMesh Ocotillo(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var sys = OcotilloSystem(ref rng);
        var program = sys.Rewrite(9, ref rng);
        var t = new Turtle { Angle = 20, Jitter = 3, Length = 0.36f, LengthDecay = 1f, LengthJitter = 0.12f, Radius = 0.022f, Taper = 0.93f, MinRadius = 0.008f,
            LeafSize = 0.3f, LeafDecay = 1f, Tropism = Vector3.UnitY, Susceptibility = 0.03f };
        var sk = t.Run(program, ref rng);
        // in leaf after rain, or bare grey canes; red flower tips in spring
        bool leafy = rng.Float() < 0.6f, flowering = rng.Float() < 0.5f;
        if (!leafy) sk.Leaves.Clear();
        var tips = new List<(Vector3 P, Vector3 D)>();
        if (flowering)
        {
            var starts = new HashSet<Vector3>(sk.Segments.Select(x => x.A));
            foreach (var seg in sk.Segments)
                if (!starts.Contains(seg.B) && rng.Float() < 0.7f) tips.Add((seg.B, Vector3.Normalize(seg.B - seg.A)));
        }
        // the lighter meshes join each cane's segments two (three) at a time: the canes barely bend
        if (detail > 0) MergeChains(sk, detail + 1);
        var st = new Style
        {
            BarkBase = leafy ? Srgb(110, 102, 86) : Srgb(128, 122, 110), BarkTip = leafy ? Srgb(128, 118, 96) : Srgb(146, 140, 126),
            Leaf = AtlasCell.OcotilloLeaf, LeafColour = Srgb(240, 246, 230), LeafVary = 0.08f,
            DrawMinRadius = 0.004f, Lod1MinRadius = 0.006f, FarMinRadius = 0.008f, Roundness = 0.5f, Caps = false,
        };
        var m = FromSkeleton(sk, st, detail, ref rng);
        foreach (var (p, d) in tips)
            Shapes.Cards(m, p - d * 0.05f, d, Shapes.AnyPerpendicular(d), 0.24f, AtlasCell.RedFlowers, Srgb(250, 244, 240), 1.1f, p - d * 0.6f, detail == 2 ? 1 : 2, lift: 0.45f, roundness: 0.4f);
        return m;
    }

    /// <summary>Joins runs of up to <paramref name="n"/> wood segments that follow on from each other into one (a lighter mesh of a stem that barely bends).</summary>
    private static void MergeChains(PlantSkeleton sk, int n)
    {
        var o = new List<Segment>();
        var segs = sk.Segments;
        for (int i = 0; i < segs.Count;)
        {
            var a = segs[i];
            int j = i + 1;
            while (j < segs.Count && j - i < n && segs[j].Kind == SegmentKind.Wood && a.Kind == SegmentKind.Wood && segs[j].A == segs[j - 1].B) j++;
            o.Add(a with { B = segs[j - 1].B, Rb = segs[j - 1].Rb });
            i = j;
        }
        segs.Clear();
        segs.AddRange(o);
    }

    /// <summary>
    /// A sotol: a dense ball of 150-200 long thin serrated blades from a short hidden trunk, radiating every way (the
    /// lowest hanging and dead), and sometimes a tall straight flower stalk with a narrow straw-coloured spike.
    /// </summary>
    private static PlantMesh Sotol(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var bloom = rng.Fork(0xF10);
        var m = new PlantMesh();
        float trunk = rng.Range(0.3f, 0.5f);
        var centre = new Vector3(0, trunk, 0);
        var green = Srgb(122, 142, 96); var tip = Srgb(150, 154, 112); var dead = Srgb(160, 146, 116);
        int n = detail == 0 ? 200 : detail == 1 ? 80 : 34;
        float width = detail == 0 ? 0.028f : detail == 1 ? 0.042f : 0.06f;   // fewer blades farther away, each wider: the ball keeps its mass
        for (int i = 0; i < n; i++)
        {
            float u = (i + 0.5f) / n;
            const float low = -0.45f;
            float elev = MathF.Asin(low + (1 - low) * u);
            float az = i * 2.39996f + rng.Range(-0.15f, 0.15f);
            var dir = new Vector3(MathF.Cos(elev) * MathF.Cos(az), MathF.Sin(elev), MathF.Cos(elev) * MathF.Sin(az));
            float len = rng.Range(0.65f, 0.95f) * (1.0f - 0.2f * MathF.Max(0, dir.Y));
            bool old = dir.Y < -0.15f;
            var c0 = old ? Vector4.Lerp(green, dead, 0.6f) : green;
            var c1 = old ? dead : tip;
            // long thin blades bow a little under their length
            var droop = new Vector3(0, -0.1f - 0.12f * (1 - MathF.Abs(dir.Y)), 0);
            Shapes.Blade(m, centre + dir * 0.05f, dir, droop, len, width, detail == 0 ? 2 : 1,
                Vary(c0, ref rng, 0.06f), Vary(c1, ref rng, 0.08f), 0.3f, 1f, AtlasCell.Blade, twist: rng.Range(-0.6f, 0.6f), tipWidth: width * 0.3f,
                crown: centre, roundness: 0.7f);
        }
        if (bloom.Float() < 0.3f)
        {
            float stalk = bloom.Range(2.0f, 3.8f);
            var s0 = centre + new Vector3(0, 0.2f, 0);
            var s1 = s0 + Vector3.Normalize(new Vector3(bloom.Range(-0.04f, 0.04f), 1, bloom.Range(-0.04f, 0.04f))) * stalk;
            var stalkCol = Srgb(150, 132, 92);
            Shapes.Tube(m, s0, s1, 0.025f, 0.01f, detail == 0 ? 5 : 3, Vector3.UnitX, stalkCol, stalkCol, 0.4f, 1f, AtlasCell.Bark);
            int cards = detail == 0 ? 7 : detail == 1 ? 3 : 2;
            for (int k = 0; k < cards; k++)
            {
                float t = 0.52f + 0.44f * k / cards;
                var p = Vector3.Lerp(s0, s1, t);
                Shapes.Cards(m, p, s1 - s0, Vector3.UnitX, stalk * (0.5f / cards + 0.08f), AtlasCell.Panicle, Srgb(236, 222, 180), 1f, p - (s1 - s0) * 0.1f, 2, lift: 0.5f, roundness: 0.3f);
            }
        }
        PressOntoGround(m, -0.03f);
        return m;
    }

    /// <summary>Beargrass: a fountain of long narrow grass-like leaves arching out and over from a tight base, and sometimes a creamy flower plume above it.</summary>
    private static PlantMesh Beargrass(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var bloom = rng.Fork(0xF10);
        var m = new PlantMesh();
        var green = Srgb(110, 132, 72); var tip = Srgb(146, 148, 96); var dead = Srgb(172, 158, 120);
        int n = detail == 0 ? 120 : detail == 1 ? 50 : 22;
        float width = detail == 0 ? 0.013f : detail == 1 ? 0.02f : 0.03f;
        for (int i = 0; i < n; i++)
        {
            float az = i * 2.39996f + rng.Range(-0.2f, 0.2f);
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            float lean = rng.Range(0.05f, 0.6f);   // the inner leaves near upright, the outer ones out
            var dir = Vector3.Normalize(outv * lean + Vector3.UnitY);
            float len = rng.Range(0.9f, 1.3f);
            // arching over, the tip out and down
            var droop = outv * 0.15f - Vector3.UnitY * (0.18f + 0.45f * lean);
            bool dry = rng.Float() < 0.12f;
            Shapes.Blade(m, outv * rng.Range(0, 0.06f), dir, droop, len, width, detail == 0 ? 4 : detail == 1 ? 3 : 2,
                Vary(dry ? dead : green, ref rng, 0.07f), Vary(dry ? dead : tip, ref rng, 0.08f), 0f, 1f, AtlasCell.Grass, twist: rng.Range(-1f, 1f), tipWidth: width * 0.2f);
        }
        if (bloom.Float() < 0.3f)
        {
            float stalk = bloom.Range(1.1f, 1.7f);
            var s1 = Vector3.Normalize(new Vector3(bloom.Range(-0.15f, 0.15f), 1, bloom.Range(-0.15f, 0.15f))) * stalk;
            var col = Srgb(170, 160, 112);
            Shapes.Tube(m, Vector3.Zero, s1, 0.01f, 0.005f, 3, Vector3.UnitX, col, col, 0f, 1f, AtlasCell.Bark);
            int cards = detail == 0 ? 3 : 1;
            for (int k = 0; k < cards; k++)
                Shapes.Cards(m, s1 * (0.62f + 0.12f * k), s1, Vector3.UnitX, stalk * 0.4f, AtlasCell.Panicle, Srgb(244, 236, 206), 1.1f, s1 * 0.4f, 2, lift: 0.45f, roundness: 0.3f);
        }
        PressOntoGround(m, -0.03f);
        return m;
    }

    /// <summary>
    /// A Havard agave: a rosette of 30-45 broad, thick, toothed blue-grey leaves in a spiral, the outer ones spread
    /// low and curving up, the inner ones closing into a bud; rarely the century plant's one tall flower stalk with
    /// short side branches of yellow flower clusters.
    /// </summary>
    private static PlantMesh Agave(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var bloom = rng.Fork(0xF10);
        var m = new PlantMesh();
        var blue = Srgb(150, 172, 166); var tipC = Srgb(136, 158, 148);
        float k0 = rng.Range(0.85f, 1.15f);
        int n = detail == 0 ? 40 : detail == 1 ? 24 : 14;
        float spin = rng.Range(0, MathF.Tau);
        for (int i = 0; i < n; i++)
        {
            float u = (i + 0.5f) / n;          // 0 the outermost (oldest) leaf, 1 the bud
            float az = spin + i * 2.39996f + rng.Range(-0.08f, 0.08f);
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            float elev = (10 + 72 * MathF.Pow(u, 0.75f)) * MathF.PI / 180f;
            var dir = outv * MathF.Cos(elev) + Vector3.UnitY * MathF.Sin(elev);
            float len = k0 * (0.66f - 0.18f * u * u) * rng.Range(0.92f, 1.08f);
            float width = len * (detail == 0 ? 0.26f : 0.3f);
            var droop = new Vector3(0, 0.22f * (1 - u), 0) - outv * 0.04f;
            Shapes.Blade(m, outv * 0.05f + new Vector3(0, 0.05f, 0), dir, droop, len, width, detail == 0 ? 3 : 2,
                Vary(blue, ref rng, 0.05f), Vary(tipC, ref rng, 0.05f), 0.1f, 0.3f, AtlasCell.AgaveBlade, keel: 0.3f, tipWidth: 0.012f, belly: 0.55f);
        }
        if (bloom.Float() < 0.1f)
        {
            float stalk = bloom.Range(3.0f, 4.5f);
            var s1 = Vector3.Normalize(new Vector3(bloom.Range(-0.06f, 0.06f), 1, bloom.Range(-0.06f, 0.06f))) * stalk;
            var col = Srgb(140, 120, 82);
            Shapes.Tube(m, new Vector3(0, 0.2f, 0), s1, 0.05f, 0.02f, detail == 0 ? 5 : 3, Vector3.UnitX, col, col, 0.1f, 1f, AtlasCell.Bark);
            int branches = detail == 2 ? 3 : 7;
            for (int b = 0; b < branches; b++)
            {
                float t = 0.6f + 0.38f * b / branches;
                float az = b * 2.39996f;
                var p = s1 * t;
                var outv = new Vector3(MathF.Cos(az), 0.35f, MathF.Sin(az));
                var end = p + Vector3.Normalize(outv) * (0.35f * (1.2f - t));
                if (detail < 2) Shapes.Tube(m, p, end, 0.012f, 0.008f, 3, Vector3.UnitY, col, col, 1f, 1.1f, AtlasCell.Bark);
                Shapes.Cards(m, end - new Vector3(0, 0.08f, 0), Vector3.UnitY, outv, 0.32f, AtlasCell.YuccaFlowers, Srgb(240, 206, 90), 1.1f, p, detail == 2 ? 1 : 2, lift: 0.4f, roundness: 0.5f);
            }
        }
        PressOntoGround(m, -0.03f);
        return m;
    }

    /// <summary>
    /// Lechuguilla: a small rosette of stiff upcurved yellow-green blades, toothed, each ending in a dark spine; in a
    /// colony of one to four rosettes off the mother plant's rhizomes, and now and then a slender 2-3 m flower stalk.
    /// </summary>
    private static PlantMesh Lechuguilla(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var layout = rng.Fork(0x1A1);   // the colony and the stalk, the same at every detail
        var m = new PlantMesh();
        var green = Srgb(150, 166, 94); var tipC = Srgb(160, 160, 98);
        int rosettes = 1 + layout.Int(4);
        for (int r = 0; r < rosettes; r++)
        {
            float ang = layout.Range(0, MathF.Tau), dist = r == 0 ? 0 : layout.Range(0.25f, 0.5f);
            var at = new Vector3(MathF.Cos(ang) * dist, 0, MathF.Sin(ang) * dist);
            float k = r == 0 ? 1f : layout.Range(0.55f, 0.85f);
            var br = rng.Fork((ulong)r + 1);
            int n = detail == 0 ? 26 : detail == 1 ? 14 : 8;
            for (int i = 0; i < n; i++)
            {
                float u = (i + 0.5f) / n;
                float az = i * 2.39996f + br.Range(-0.2f, 0.2f);
                var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
                float elev = (28 + 52 * u) * MathF.PI / 180f;
                var dir = outv * MathF.Cos(elev) + Vector3.UnitY * MathF.Sin(elev);
                float len = k * br.Range(0.32f, 0.46f) * (1 - 0.25f * u);
                float width = len * (detail == 0 ? 0.1f : 0.13f);
                // upcurved: the blade bends up toward the vertical
                var droop = new Vector3(0, 0.22f, 0) - outv * 0.14f;
                Shapes.Blade(m, at + outv * 0.02f, dir, droop, len, width, detail == 0 ? 2 : 1, Vary(green, ref br, 0.07f), Vary(tipC, ref br, 0.07f),
                    0.1f, 0.4f, AtlasCell.AgaveBlade, keel: 0.4f, tipWidth: 0.002f, belly: 0.35f);
            }
        }
        if (layout.Float() < 0.12f)
        {
            float stalk = layout.Range(2.0f, 3.0f);
            var s1 = Vector3.Normalize(new Vector3(layout.Range(-0.08f, 0.08f), 1, layout.Range(-0.08f, 0.08f))) * stalk;
            var col = Srgb(140, 128, 84);
            Shapes.Tube(m, new Vector3(0, 0.1f, 0), s1, 0.018f, 0.006f, 3, Vector3.UnitX, col, col, 0.1f, 1f, AtlasCell.Bark);
            int cards = detail == 0 ? 4 : 2;
            for (int c = 0; c < cards; c++)
                Shapes.Cards(m, s1 * (0.55f + 0.42f * c / cards), s1, Vector3.UnitX, stalk * 0.18f, AtlasCell.Panicle, Srgb(220, 196, 120), 1f, s1 * 0.4f, 2, lift: 0.5f, roundness: 0.3f);
        }
        PressOntoGround(m, -0.03f);
        return m;
    }

    /// <summary>Candelilla: a clump of thin, waxy, grey-green upright stems, near leafless; farther away crossed cards of a painted clump.</summary>
    private static PlantMesh Candelilla(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float h = rng.Range(0.32f, 0.55f);
        var wax = Srgb(138, 154, 128);
        if (detail > 0)
        {
            int cards = detail == 1 ? 3 : 2;
            float az0 = rng.Range(0, MathF.PI);
            for (int i = 0; i < cards; i++)
            {
                float az = az0 + i * MathF.PI / cards;
                Shapes.Card(m, Vector3.Zero, new Vector3(MathF.Cos(az), 0, MathF.Sin(az)), Vector3.UnitY, h * 1.1f, h * 1.05f, AtlasCell.Candelilla,
                    Vary(Srgb(236, 240, 236), ref rng, 0.05f), 0.9f, new Vector3(0, -h * 0.5f, 0), 0.5f);
            }
            return m;
        }
        int n = 40 + rng.Int(24);
        for (int i = 0; i < n; i++)
        {
            float az = i * 2.39996f + rng.Range(-0.3f, 0.3f);
            var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
            float r = 0.16f * MathF.Sqrt(rng.Float());
            var a = outv * r;
            float l = h * rng.Range(0.55f, 1.1f);
            var dir = Vector3.Normalize(outv * rng.Range(0.02f, 0.25f) * (0.5f + r * 4) + Vector3.UnitY);
            var mid = a + dir * (l * 0.5f);
            var b = mid + Vector3.Normalize(dir + outv * rng.Range(-0.08f, 0.12f)) * (l * 0.5f);
            var col = Vary(wax, ref rng, 0.06f);
            int ring = Shapes.Tube(m, a, mid, 0.0055f, 0.005f, 3, outv, col, col, 0f, 0.5f, AtlasCell.Blade);
            Shapes.Tube(m, mid, b, 0.005f, 0.0042f, 3, outv, col, col * 1.05f, 0.5f, 1f, AtlasCell.Blade, cap: true, from: ring);
        }
        return m;
    }

    /// <summary>
    /// Buffalograss: a low curly mat of short fine blades spread over a patch of ground (it runs by stolons), the blades
    /// curling over sideways; farther away wide low crossed cards of a painted mat.
    /// </summary>
    private static PlantMesh Buffalograss(ulong seed, int detail)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float h = rng.Range(0.1f, 0.15f), w = rng.Range(0.55f, 0.8f);
        var col = Srgb(146, 146, 106);
        if (detail > 0)
        {
            int cards = detail == 1 ? 3 : 2;
            float az0 = rng.Range(0, MathF.PI);
            for (int i = 0; i < cards; i++)
            {
                float az = az0 + i * MathF.PI / cards + rng.Range(-0.15f, 0.15f);
                // the turf as the full mesh grows it: lower than its blades are long, its edge thin
                Shapes.Card(m, Vector3.Zero, new Vector3(MathF.Cos(az), 0, MathF.Sin(az)), Vector3.UnitY, w * 0.85f, h * 0.7f, AtlasCell.Buffalograss,
                    Vary(col, ref rng, 0.07f), 0.6f, new Vector3(0, -0.4f, 0), 0.5f);
            }
            return m;
        }
        // a turf of little tufts on the runners, their short blades curling out and over, grey-green and straw
        var green = Srgb(126, 138, 96); var dry = Srgb(184, 170, 126);
        int tufts = 11 + rng.Int(5);
        for (int k = 0; k < tufts; k++)
        {
            float ta = rng.Range(0, MathF.Tau), tr = w * 0.42f * MathF.Sqrt(rng.Float());
            var at = new Vector3(MathF.Cos(ta) * tr, 0, MathF.Sin(ta) * tr);
            // the middle of the mat taller, its edge low
            float th = h * (1.1f - 0.6f * tr / (w * 0.5f));
            int blades = 10 + rng.Int(5);
            for (int i = 0; i < blades; i++)
            {
                float ba = i * 2.39996f + rng.Range(-0.4f, 0.4f);
                var outv = new Vector3(MathF.Cos(ba), 0, MathF.Sin(ba));
                float elev = rng.Range(0.75f, 1.4f);
                var dir = Vector3.Normalize(outv * MathF.Cos(elev) + Vector3.UnitY * MathF.Sin(elev));
                float l = th * rng.Range(0.7f, 1.5f);
                var side = Vector3.Cross(outv, Vector3.UnitY);
                var droop = outv * 0.3f + side * rng.Range(-0.5f, 0.5f) - Vector3.UnitY * rng.Range(0.35f, 0.8f);
                bool dead = rng.Float() < 0.35f;
                var c = Vary(dead ? dry : green, ref rng, 0.08f);
                var tip = dead ? c * 1.05f : Vector4.Lerp(c, dry, 0.5f);
                c.W = tip.W = 1;
                Shapes.Blade(m, at + outv * 0.01f, dir, droop, l, rng.Range(0.008f, 0.011f), 3, c * 0.85f, tip, 0f, 1f, AtlasCell.Grass,
                    twist: rng.Range(-1.2f, 1.2f), tipWidth: 0.002f, foliage: Shapes.ThinBlade, bladeId: rng.Float());
            }
        }
        for (int i = 0; i < m.Positions.Count; i++)
            if (m.Positions[i].Y < 0.006f) m.Positions[i] = m.Positions[i] with { Y = 0.006f + (m.Positions[i].Y - 0.006f) * 0.04f };
        return m;
    }

    /// <summary>A forb (a wildflower, pickleweed): two or three crossed cards of its painted cell, height h0..h1, width a multiple of the height.</summary>
    private static PlantMesh Wildflower(ulong seed, int detail, AtlasCell cell, float h0, float h1, float widthK)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float h = rng.Range(h0, h1);
        int cards = detail == 0 ? 3 : 2;
        float az0 = rng.Range(0, MathF.PI);
        for (int i = 0; i < cards; i++)
        {
            float az = az0 + i * MathF.PI / cards + rng.Range(-0.2f, 0.2f);
            Shapes.Card(m, Vector3.Zero, new Vector3(MathF.Cos(az), 0, MathF.Sin(az)), Vector3.UnitY, h * widthK, h, cell,
                Vary(Srgb(248, 248, 244), ref rng, 0.05f), 0.9f, new Vector3(0, -h * 0.4f, 0), 0.55f);
        }
        return m;
    }
}
