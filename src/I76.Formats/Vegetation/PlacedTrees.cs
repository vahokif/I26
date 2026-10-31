using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>
/// The trees the maps place as camera-facing cards (npinetr1/2, ntree1-4), as the modern look's
/// real plants: drawn where the card stands, at its placement's yaw, as tall as the card (docs/modern-vegetation.md,
/// Placed trees). Only the look changes: the object, its collision and its damage stay the card's. Not in
/// <see cref="SpeciesCatalog.All"/>: no biome grows them.
/// </summary>
public static class PlacedTrees
{
    /// <summary>The share of a pine card's width its crown fills at its widest (NG_2HV_1: 0.81, NG_2HV_2: 0.94).</summary>
    public const float PineCrownShare = 0.85f;

    /// <summary>
    /// The species a card definition becomes, for a card <paramref name="height"/> tall (its top over the placement) and
    /// <paramref name="width"/> wide, or null for one that stays a card. The pines are grown to the card's size; the
    /// scrub trees and the Joshua trees (placed by no stock map) take the nearest species the land has, the honey
    /// mesquite and the soaptree yucca (a tree yucca like the Joshua tree), which the caller scales to the card's height.
    /// </summary>
    public static SpeciesDef? For(string definition, float height, float width)
    {
        var stem = Path.GetFileNameWithoutExtension(definition).ToLowerInvariant();
        if (stem.StartsWith("npinetr", StringComparison.Ordinal))
            return new SpeciesDef
            {
                Id = stem, Name = "Pine (the maps' placed pines)", Layer = VegLayer.Shrub, Stiffness = 0.85f, Radius = width * 0.45f,
                ScaleMin = 1, ScaleMax = 1, Sink = 0.05f, Build = (s, d) => Pine(s, d, height, width * PineCrownShare),
            };
        return stem switch
        {
            "ntree1" or "ntree2" => SpeciesCatalog.All["mesquite"],
            "ntree3" or "ntree4" => SpeciesCatalog.All["soaptree"],
            _ => null,
        };
    }

    /// <summary>
    /// A pine <paramref name="height"/> tall with a crown <paramref name="crown"/> across at its widest, shaped after the
    /// card's painting: a bare trunk up its lowest sixth, then loose whorls of branches, each at its own height, bearing,
    /// length and angle (down from the trunk below, out in the middle, up near the top), sagging and turning their tips
    /// up, some missing; the crown widest a little below its middle and narrowing to a leader; the needles in tufts of
    /// crossed sprays (<see cref="AtlasCell.PineSpray"/>) along each branch's outer two thirds. The tree's structure comes from the seed
    /// alone; <paramref name="detail"/> only thins what is drawn (1: fewer, bigger sprays, short branches as sprays
    /// alone; 2: a few big sprays a branch, only the trunk as a tube).
    /// </summary>
    public static PlantMesh Pine(ulong seed, int detail, float height, float crown)
    {
        var rng = new VegRng(seed);
        var m = new PlantMesh();
        float h = height, rMax = crown * 0.5f, k = h / 8f;
        float baseY = h * rng.Range(0.14f, 0.19f);
        var bark = SpeciesCatalog.Srgb(86, 68, 56);
        var barkTop = SpeciesCatalog.Srgb(74, 62, 52);
        // the atlas carries the needles' colours: the mesh only varies them a little
        var needles = SpeciesCatalog.Srgb(236, 242, 234);
        float Sway(Vector3 p) => MathF.Pow(Math.Clamp(p.Y / h, 0f, 1.1f), 1.4f) + 0.12f * MathF.Min(1f, new Vector2(p.X, p.Z).Length() / rMax);
        float TrunkR(float y) => MathF.Max(0.02f, 0.14f * k * (1 - 0.9f * y / h));
        // the trunk, a little crooked, in a few segments
        var lean = new Vector3(rng.Range(-0.04f, 0.04f), 0, rng.Range(-0.04f, 0.04f));
        Vector3 Axis(float y) => new Vector3(0, y, 0) + lean * (y / h) * h * 0.1f + new Vector3(MathF.Sin(y * 1.3f + seed % 7), 0, MathF.Cos(y * 0.9f)) * 0.02f * k;
        int segs = detail == 0 ? 6 : 3, sides = detail == 0 ? 7 : detail == 1 ? 5 : 4;
        int ring = -1;
        for (int i = 0; i < segs; i++)
        {
            float y0 = -0.2f + (h * 0.97f + 0.2f) * i / segs, y1 = -0.2f + (h * 0.97f + 0.2f) * (i + 1) / segs;
            var a = Axis(y0); var b = Axis(y1);
            ring = Shapes.Tube(m, a, b, TrunkR(y0), TrunkR(y1), sides, Vector3.UnitX, Vector4.Lerp(bark, barkTop, y0 / h), Vector4.Lerp(bark, barkTop, y1 / h),
                Sway(a), Sway(b), AtlasCell.Bark, vRepeat: (y1 - y0) / 0.6f, from: ring);
        }
        // the foliage's normals point out from the trunk at their own height (a little up), not from one centre: from a
        // point the top's sprays faced the sky and came out pale, the bottom's the ground and came out dark
        Vector3 From(Vector3 p) => Axis(p.Y) - new Vector3(0, 0.6f * k, 0);
        // the whorls, a year's growth apart, a branch structure decided whatever the detail
        float az0 = rng.Range(0, MathF.Tau);
        for (float y = baseY; y < h * 0.93f; y += rng.Range(0.36f, 0.52f) * k)
        {
            var w = rng.Fork((ulong)(y * 1000));
            float t = (y - baseY) / (h - baseY);
            float envelope = t < 0.4f ? 0.42f + 0.58f * MathF.Pow(t / 0.4f, 0.75f) : MathF.Pow(1 - (t - 0.4f) / 0.6f, 1.15f);
            int n = 4 + w.Int(4);
            bool gap = w.Float() < 0.12f;
            az0 += 2.39996f;
            for (int j = 0; j < n; j++)
            {
                var r = w.Fork((ulong)j + 1);
                if (gap || r.Float() < 0.2f) continue;
                // no flat discs: each branch leaves the trunk at its own height and bearing, its own length and angle
                float by = MathF.Min(h * 0.93f, y + r.Range(-0.16f, 0.16f) * k);
                float az = az0 + j * MathF.Tau / n + r.Range(-0.5f, 0.5f);
                float len = MathF.Max(0.25f * k, rMax * envelope * r.Range(0.7f, 1.3f));
                // down from the trunk below, out in the middle, up near the top; each sags and turns its tip up
                float pitch = 0.95f * (t - 0.62f) + r.Range(-0.18f, 0.12f);
                float sag = len * (0.18f + 0.12f * (1 - t)) * r.Range(0.6f, 1.3f), lift = len * r.Range(0.12f, 0.3f);
                var outv = new Vector3(MathF.Cos(az), 0, MathF.Sin(az));
                var dir = Vector3.Normalize(outv * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch));
                var root = Axis(by) + outv * TrunkR(by) * 0.7f;
                // a quadratic sweep: through a sagging middle to a tip turned up
                var ctrl = root + dir * len * 0.55f - new Vector3(0, sag, 0);
                var end = root + dir * len - new Vector3(0, sag * 0.6f, 0) + new Vector3(0, lift, 0);
                Vector3 At(float u) => (1 - u) * (1 - u) * root + 2 * (1 - u) * u * ctrl + u * u * end;
                Vector3 Tangent(float u) => Vector3.Normalize(2 * (1 - u) * (ctrl - root) + 2 * u * (end - ctrl));
                float rb = MathF.Max(0.012f, 0.035f * MathF.Sqrt(len / 1.2f)) * MathF.Sqrt(k);
                bool tube = detail == 0 || (detail == 1 && len > 0.7f * k);
                if (tube)
                {
                    int bs = detail == 0 ? 4 : 3, pieces = detail == 0 ? 3 : 2, br = -1;
                    for (int q = 0; q < pieces; q++)
                    {
                        float u0 = (float)q / pieces, u1 = (float)(q + 1) / pieces;
                        br = Shapes.Tube(m, At(u0), At(u1), rb * (1 - 0.7f * u0), rb * (1 - 0.7f * u1), bs, Vector3.UnitY, Vector4.Lerp(bark, barkTop, u0), barkTop,
                            Sway(At(u0)), Sway(At(u1)), AtlasCell.Bark, from: br);
                    }
                }
                // the needles in clumps along the outer part, the biggest toward the tip, each a tuft of crossed sprays along it, turned up a little
                int clumps = detail switch { 0 => Math.Max(2, (int)MathF.Ceiling(len / (0.3f * k))), 1 => Math.Max(1, (int)MathF.Ceiling(len / (0.55f * k))), _ => len > 0.9f * k ? 2 : 1 };
                for (int q = 0; q < clumps; q++)
                {
                    float u = clumps == 1 ? 0.8f : 0.4f + 0.6f * q / (clumps - 1);
                    var p = At(u);
                    var tan = Tangent(u);
                    float size = (detail == 0 ? 0.64f : detail == 1 ? 0.92f : 1.3f) * k * r.Range(0.85f, 1.15f) * MathF.Min(1f, 0.6f + len / (rMax + 0.01f)) * (0.8f + 0.3f * u);
                    int cards = detail == 0 ? 3 : 2;
                    float roll0 = r.Range(0, MathF.PI);
                    for (int c = 0; c < cards; c++)
                    {
                        // each spray of the tuft leans its own way off the branch, a little up
                        var sd = Vector3.Normalize(tan + new Vector3(r.Range(-0.3f, 0.3f), r.Range(0.05f, 0.35f), r.Range(-0.3f, 0.3f)));
                        var side = Turtle.Rotate(Shapes.AnyPerpendicular(sd), sd, roll0 + c * MathF.PI / cards + r.Range(-0.2f, 0.2f));
                        float sz = size * (c == 0 ? 1f : r.Range(0.7f, 0.9f));
                        Spray(m, p - sd * sz * 0.35f, side, sd, sz, sz, Vary(needles, ref r, 0.07f), Sway(p), From(p));
                    }
                }
            }
            // short shoots off the trunk between the branches fill the crown's inside
            int inner = envelope > 0.2f ? (detail == 0 ? 3 : detail == 1 ? 2 : 1) : 0;
            for (int j = 0; j < inner; j++)
            {
                var r = w.Fork(0x100 + (ulong)j);
                float az = r.Range(0, MathF.Tau), yy = y + r.Range(0.05f, 0.3f) * k;
                var along = Vector3.Normalize(new Vector3(MathF.Cos(az), r.Range(-0.3f, 0.3f), MathF.Sin(az)));
                float size = (detail == 0 ? 0.7f : 1f) * k * MathF.Max(0.6f, envelope);
                var side = Turtle.Rotate(Shapes.AnyPerpendicular(along), along, r.Range(0, MathF.PI));
                Spray(m, Axis(yy), side, along, size, size, Vary(needles, ref r, 0.07f), Sway(Axis(yy)), From(Axis(yy)));
            }
        }
        // the leader: a spray pointing up at the top, and one or two about it
        var top = Axis(h * 0.93f);
        Shapes.Cards(m, top, Vector3.UnitY, Vector3.UnitX, 0.8f * k, AtlasCell.PineSpray, Vary(needles, ref rng, 0.05f), 1f, From(top), planes: 2, lift: 0.3f, roundness: 0.6f);
        if (detail < 2)
            for (int i = 0; i < 2; i++)
            {
                float az = rng.Range(0, MathF.Tau);
                var d = Vector3.Normalize(new Vector3(MathF.Cos(az), 1.4f, MathF.Sin(az)));
                Shapes.Cards(m, Axis(h * 0.88f), d, Vector3.UnitY, 0.6f * k, AtlasCell.PineSpray, Vary(needles, ref rng, 0.06f), 0.95f, From(Axis(h * 0.88f)), planes: 2, roundness: 0.6f);
            }
        return m;
    }

    /// <summary>
    /// A needle spray: one card, <paramref name="width"/> along <paramref name="side"/> and <paramref name="length"/> along
    /// <paramref name="along"/> from the middle of its bottom edge at <paramref name="pos"/>, swaying as one with its branch,
    /// its normals bent out of the crown (as <see cref="Shapes.Card"/>, whose bottom edge stays put).
    /// </summary>
    private static void Spray(PlantMesh m, Vector3 pos, Vector3 side, Vector3 along, float width, float length, Vector4 col, float sway, Vector3 crown)
    {
        var faceN = Vector3.Normalize(Vector3.Cross(side, along));
        var c0 = pos - side * (width * 0.5f);
        Vector3[] corners = [c0, c0 + side * width, c0 + side * width + along * length, c0 + along * length];
        Vector2[] uv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
        int i0 = m.VertexCount;
        for (int q = 0; q < 4; q++)
        {
            var outward = corners[q] - crown;
            outward = outward.LengthSquared() < 1e-8f ? faceN : Vector3.Normalize(outward);
            var fn = Vector3.Dot(faceN, outward) >= 0 ? faceN : -faceN;
            m.Vertex(corners[q], Vector3.Normalize(Vector3.Lerp(fn, outward, 0.7f)), col, Shapes.Uv(AtlasCell.PineSpray, uv[q].X, uv[q].Y), sway * (q >= 2 ? 1.08f : 1f), 1f);
        }
        m.Quad(i0, i0 + 1, i0 + 2, i0 + 3);
    }

    private static Vector4 Vary(Vector4 c, ref VegRng rng, float amount)
    {
        float s = 1 + amount * rng.Bell();
        float hue = amount * 0.5f * rng.Bell();
        return new Vector4(c.X * s * (1 + hue), c.Y * s, c.Z * s * (1 - hue), 1);
    }
}
