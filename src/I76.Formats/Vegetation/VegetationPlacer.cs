using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>The drawn ground the plants stand on, in the game frame (metres; Y up).</summary>
public interface IVegTerrain
{
    /// <summary>Whether there is terrain here at all (outside the mission's tiles: none).</summary>
    bool Has(double x, double z);
    /// <summary>The height of the drawn ground (where a plant's base goes).</summary>
    float Height(double x, double z);
    /// <summary>The ground's rise over run here (0 flat; 1 is 45 degrees), for the slope rules.</summary>
    float Slope(double x, double z);
    /// <summary>The ground a car can reach (the open cells): plants by the road are judged on it, cliffs and walls are not.</summary>
    bool Open(double x, double z);
    /// <summary>The ground's height, roughly (for the riparian strip of the sward map, read at every texel): <see cref="Height"/> unless cheaper.</summary>
    float Ground(double x, double z) => Height(x, z);
    /// <summary>The original's rough ground (terrain surface id 5, which it studs with big yuccas and agaves): a thicket ground here. Default: none.</summary>
    bool Rough(double x, double z) => false;
    /// <summary>The original's road surfaces (terrain ids 2 asphalt, 3 dirt road, 4 plank road, 6 parking lot, 7 dirt intersection), which grow no ground clutter there: bare, smooth, harmless ground. Default: none.</summary>
    bool RoadSurface(double x, double z) => false;
}

/// <summary>Reading a terrain sample's surface id (bits 13-15), as the ground does.</summary>
public static class VegSurface
{
    /// <summary>Whether a terrain sample's surface is a road surface (ids 2, 3, 4, 6, 7).</summary>
    public static bool IsRoad(int sample) => (sample >> 13 & 7) is 2 or 3 or 4 or 6 or 7;
}

/// <summary>One plant: which species (an index into <see cref="VegetationPlacer.Species"/>) and variant, where (game frame), turned and scaled how, and a phase for its sway.</summary>
public readonly record struct PlantInstance(int Species, int Variant, Vector3 Position, float Yaw, float Scale, float Phase);

/// <summary>The knobs the setting turns: how many plants of the biome's cover grow, and the clearances they keep.</summary>
public sealed record VegetationParams
{
    /// <summary>Scales the biome's cover (Graphic Detail's Vegetation: Low fewer, High all).</summary>
    public float Density { get; init; } = 1f;
    /// <summary>Clearance past a road's edge (metres): shrubs, and grasses (which may come nearer).</summary>
    public float ShrubRoadClearance { get; init; } = 2.5f;
    public float GroundRoadClearance { get; init; } = 0.8f;
    /// <summary>Clearance round an object's footprint, and round a driving line beyond its own radius.</summary>
    public float ShrubObjectClearance { get; init; } = 2.5f;
    public float GroundObjectClearance { get; init; } = 0.8f;
    public float ShrubLineClearance { get; init; } = 1.5f;
    public float GroundLineClearance { get; init; } = 0.3f;
    /// <summary>The mission's low ground (its river bottom), metres: the riparian strip is measured up from it.</summary>
    public float LowLevel { get; init; }
    /// <summary>Whether the low ground is a strip of its own (a river bottom, a salt flat: <see cref="VegRegion.LowGround"/>); without one every cell is upland.</summary>
    public bool LowGround { get; init; } = true;
    /// <summary>Within this far of a road's edge (metres) the ground counts as disturbed (<see cref="BiomeSpecies.Roadside"/>); at most the mask's reach.</summary>
    public float RoadsideBand { get; init; } = 8f;
}

/// <summary>
/// Places a biome's plants, deterministically: each layer's grid cells hold at most one plant, seeded by the
/// mission's seed and the cell's own coordinates (so any chunk, built in any order, grows the same plants). A cell
/// grows a plant with a probability from the biome's cover, the riparian strip (low ground near the mission's
/// river bottom), the patch noise and the slope; the species then by weight, each species' weight scaled by its
/// riparian affinity, its own patch noise and its slope limit. Masked ground (roads, objects, driving lines) and
/// land outside the terrain grow nothing.
/// </summary>
public sealed class VegetationPlacer
{
    public Biome Biome { get; }
    public VegetationParams Params { get; }
    public IVegTerrain Terrain { get; }
    public VegMask Mask { get; }
    public ulong Seed { get; }
    /// <summary>The biome's species in the order the instances index them: the shrubs, then the ground layer's.</summary>
    public IReadOnlyList<SpeciesDef> Species { get; }
    private readonly BiomeSpecies[] _entries;

    public VegetationPlacer(Biome biome, VegetationParams p, IVegTerrain terrain, VegMask mask, ulong seed)
    {
        Biome = biome; Params = p; Terrain = terrain; Mask = mask; Seed = seed;
        _entries = [.. biome.Shrubs, .. biome.Ground];
        Species = [.. _entries.Select(e => SpeciesCatalog.All.TryGetValue(e.Id, out var s) ? s : throw new ArgumentException($"biome {biome.Name}: no species {e.Id}"))];
        _roadside = [biome.Shrubs.Any(e => e.Roadside > 0), biome.Ground.Any(e => e.Roadside > 0)];
        _motte = [biome.Shrubs.Select(e => e.Motte).DefaultIfEmpty(0).Max(), biome.Ground.Select(e => e.Motte).DefaultIfEmpty(0).Max()];
    }

    private readonly bool[] _roadside;
    private readonly float[] _motte;

    public float CellSize(VegLayer layer) => layer == VegLayer.Shrub ? Biome.ShrubCell : Biome.GroundCell;

    /// <summary>The riparian strip's weight at a height: 1 on the river bottom, 0 above the band.</summary>
    public float Riparian(float height)
    {
        float h = height - Params.LowLevel;
        return 1 - SmoothStep(Biome.RiparianBand * 0.4f, Biome.RiparianBand, h);
    }

    /// <summary>Every plant of a layer whose cell's corner lies in [x0, x0 + size) x [z0, z0 + size).</summary>
    public List<PlantInstance> Place(VegLayer layer, float x0, float z0, float size)
    {
        var o = new List<PlantInstance>();
        float cell = CellSize(layer);
        int i0 = (int)MathF.Floor(x0 / cell), j0 = (int)MathF.Floor(z0 / cell);
        int i1 = (int)MathF.Ceiling((x0 + size) / cell), j1 = (int)MathF.Ceiling((z0 + size) / cell);
        for (int j = j0; j < j1; j++)
            for (int i = i0; i < i1; i++)
                if (PlaceCell(layer, i, j) is { } p) o.Add(p);
        return o;
    }

    /// <summary>The plant in grid cell (i, j) of a layer, if any.</summary>
    /// <summary>Debug counts (I76_VEG_STATS): cells tried, outside the terrain, too steep, lost to the cover roll, masked, placed.</summary>
    public static readonly long[] Counts = new long[6];

    public PlantInstance? PlaceCell(VegLayer layer, int i, int j)
    {
        Interlocked.Increment(ref Counts[0]);
        float cell = CellSize(layer);
        var rng = new VegRng(VegHash.Mix(Seed, (ulong)layer + 1, (ulong)(uint)i << 32 | (uint)j));
        float x = (i + rng.Range(0.1f, 0.9f)) * cell, z = (j + rng.Range(0.1f, 0.9f)) * cell;
        float pick = rng.Float(), which = rng.Float();
        if (!Terrain.Has(x, z)) { Interlocked.Increment(ref Counts[1]); return null; }
        float slope = Terrain.Slope(x, z);
        if (slope > 1.2f) { Interlocked.Increment(ref Counts[2]); return null; }
        float h = Terrain.Height(x, z);
        var site = Site(layer, x, z, slope, h);
        bool shrub = layer == VegLayer.Shrub;
        if (BareRoadSurface(x, z, site.Rip)) { Interlocked.Increment(ref Counts[3]); return null; }
        // the roll against the cover, as a share of the density (so Low's plants are High's, the same species)
        float u = Params.Density > 0 ? pick / Params.Density : float.MaxValue;
        if (u >= site.Cover + (1 - site.Cover) * _motte[(int)layer]) { Interlocked.Increment(ref Counts[3]); return null; }

        int first = shrub ? 0 : Biome.Shrubs.Length, count = shrub ? Biome.Shrubs.Length : Biome.Ground.Length;
        if (count == 0) { Interlocked.Increment(ref Counts[3]); return null; }
        Span<float> w = stackalloc float[count];
        var (total, motte, motteSp) = Weights(layer, x, z, slope, site, w);
        float threshold = site.Cover + (1 - site.Cover) * motte;
        if (u >= threshold) { Interlocked.Increment(ref Counts[3]); return null; }
        // the motte's share of the cell's chance goes to its species, the rest by weight (decided apart from the roll, so every density agrees)
        float pm = threshold > 0 ? (threshold - site.Cover) / threshold : 0;
        int sp = motteSp;
        if (which >= pm || motteSp < 0)
        {
            if (total <= 1e-6f) return null;
            float r = (which - pm) / MathF.Max(1e-6f, 1 - pm) * total;
            sp = count - 1;
            for (int k = 0; k < count; k++) { r -= w[k]; if (r < 0) { sp = k; break; } }
        }
        var def = Species[first + sp];
        float scale = rng.Range(def.ScaleMin, def.ScaleMax);
        // the grasses' tufts bigger in their dense patches, smaller in the thin ones
        if (!shrub && def.Grass && Biome.TuftClump > 0) scale *= 0.75f + 0.5f * site.Tufts;
        float clearance = def.Radius * scale;
        var p2 = new Vector2(x, z);
        if (Mask.Blocked(p2, (shrub ? Params.ShrubRoadClearance : Params.GroundRoadClearance) + clearance,
                (shrub ? Params.ShrubObjectClearance : Params.GroundObjectClearance) + clearance,
                (shrub ? Params.ShrubLineClearance : Params.GroundLineClearance) + clearance))
        { Interlocked.Increment(ref Counts[4]); return null; }
        Interlocked.Increment(ref Counts[5]);
        // sunk a little, more on a slope where one side of the base would lift off
        float y = h - def.Sink * scale - MathF.Min(0.4f, slope * def.Radius * scale * 0.5f);
        return new PlantInstance(first + sp, rng.Int(def.Variants), new Vector3(x, y, z), rng.Range(0, MathF.Tau), scale, rng.Float());
    }

    /// <summary>
    /// Plants follow how the ground drives: the original's road surfaces (dirt flats, plank, parking lots) grow no clutter.
    /// Bare there, except the roadside strip (within <see cref="RoadsideStrip"/> of a road's edge, where the biome's weeds
    /// and grasses grow as ever) and the low-ground strip (<paramref name="rip"/> above 0).
    /// </summary>
    private bool BareRoadSurface(float x, float z, float rip) =>
        rip <= 0 && Terrain.RoadSurface(x, z) && Mask.RoadDistance(new Vector2(x, z), RoadsideStrip) >= RoadsideStrip;

    /// <summary>How far past a road's edge (metres) the roadside plants stay on a road surface.</summary>
    public const float RoadsideStrip = 5f;

    /// <summary>What a layer's cell sees at a point: its cover, the riparian weight, open ground, how rocky, how near a road, and the grasses' clumping there (0..1).</summary>
    private readonly record struct SiteInfo(float Cover, float Rip, bool Open, float Rocky, float Near, float Tufts, bool Rough = false);

    private SiteInfo Site(VegLayer layer, float x, float z, float slope, float h)
    {
        float rip = Params.LowGround ? Riparian(h) : 0f;
        bool shrub = layer == VegLayer.Shrub;
        bool open = Terrain.Open(x, z);
        float cover = shrub ? Lerp(Biome.ShrubCover, Biome.RiparianShrubCover, rip) : Lerp(Biome.GroundCover, Biome.RiparianGroundCover, rip);
        float patch = Fbm(x / Biome.PatchScale, z / Biome.PatchScale, VegHash.Mix(Seed, 0xC0FE));
        cover *= MathF.Max(0, 1 + Biome.Patchiness * (2 * patch - 1) * 1.25f);
        // the riparian thickets come in stands, not a hedge along the contour
        if (rip > 0) cover *= Lerp(1, 0.35f + 1.3f * Fbm(x / 22f, z / 22f, VegHash.Mix(Seed, 0xB0B)), rip);
        // the grasses' own clumps: a sward of denser patches and thinner ground between, not an even scatter
        float tufts = 0.5f;
        if (!shrub && Biome.TuftClump > 0)
        {
            tufts = Fbm(x / Biome.TuftClump, z / Biome.TuftClump, VegHash.Mix(Seed, 0x7F7));
            cover *= MathF.Max(0, 1 + Biome.TuftClumpiness * (2 * tufts - 1) * 1.25f);
        }
        // ground nobody drives (the walls, mesa tops) is often bare rock: a little less there
        if (!open) cover *= 0.7f;
        cover *= 1 - SmoothStep(0.3f, 1.1f, slope);
        // the original's rough ground (surface 5): thickets of its big plants in clumps, the grasses a little sparser
        bool rough = Terrain.Rough(x, z);
        if (rough)
            cover *= shrub ? RoughShrubs * (0.1f + 1.8f * Fbm(x / RoughClump, z / RoughClump, VegHash.Mix(Seed, 0x40C6))) : RoughGround;
        // rocky: steep or broken ground (off the drivable cells)
        float rocky = MathF.Max(SmoothStep(0.12f, 0.5f, slope), open ? 0f : 0.5f);
        float near = _roadside[(int)layer] ? 1 - SmoothStep(1.5f, Params.RoadsideBand, Mask.RoadDistance(new Vector2(x, z), Params.RoadsideBand)) : 0f;
        return new SiteInfo(cover, rip, open, rocky, near, tufts, rough);
    }

    /// <summary>Each of a layer's species' weight at a point (into <paramref name="w"/>), their sum, and the strongest motte there and its species.</summary>
    private (float Total, float Motte, int MotteSp) Weights(VegLayer layer, float x, float z, float slope, SiteInfo site, Span<float> w)
    {
        bool shrub = layer == VegLayer.Shrub;
        int first = shrub ? 0 : Biome.Shrubs.Length;
        float total = 0, motte = 0;
        int motteSp = -1;
        for (int k = 0; k < w.Length; k++)
        {
            var e = _entries[first + k];
            float aff = e.Riparian >= 0 ? Lerp(1 - e.Riparian, 1, site.Rip) : Lerp(1, 1 + e.Riparian, site.Rip);
            float f = e.Clump > 0 ? Fbm(x / e.Clump, z / e.Clump, VegHash.Mix(Seed, (ulong)(first + k) + 100)) : 0.5f;
            float clump = e.Clump > 0 ? 0.25f + 1.5f * f : 1f;
            float sl = 1 - SmoothStep(e.MaxSlope * 0.6f, e.MaxSlope, slope);
            float rock = MathF.Max(0, 1 + e.Rocky * (2 * site.Rocky - 1));
            float road = e.Roadside > 0 ? 1 - e.Roadside + 3 * e.Roadside * site.Near : 1f;
            w[k] = MathF.Max(0, e.Weight * aff * clump * sl * rock * road * (site.Rough ? e.Rough : 1f));
            total += w[k];
            // a motte: where the species' own patches are densest, its stand fills the cells between
            if (e.Motte > 0)
            {
                float m = e.Motte * SmoothStep(0.62f, 0.85f, f) * sl * MathF.Min(1, rock) * aff;
                if (m > motte) { motte = m; motteSp = k; }
            }
        }
        return (total, motte, motteSp);
    }

    /// <summary>
    /// The sward at a point (0..1): the ground layer's cover there against the biome's, times the grasses' share of
    /// it, times the biome's <see cref="Biome.Sward"/>; nothing off the terrain, on steep ground or under an object (the
    /// roads paint over it). Independent of the setting's density (Low's ground is as grassy as High's).
    /// </summary>
    public float Sward(float x, float z)
    {
        if (Biome.Sward <= 0 || Biome.Ground.Length == 0 || !Terrain.Has(x, z)) return 0;
        var (cover, share) = SwardBroad(x, z);
        return SwardFine(x, z, cover, share);
    }

    /// <summary>
    /// The sward's broad part at a point, smooth over tens of metres (the <see cref="SwardMap"/> takes it on a coarser
    /// lattice): the ground layer's cover from the patches and the riparian strip, as a share of the biome's, and the
    /// grasses' share of the species' weights.
    /// </summary>
    public (float Cover, float Share) SwardBroad(float x, float z)
    {
        if (Biome.Sward <= 0 || Biome.Ground.Length == 0) return (0, 0);
        float slope = Terrain.Slope(x, z);
        float rip = Params.LowGround ? Riparian(Terrain.Ground(x, z)) : 0f;
        float cover = Lerp(Biome.GroundCover, Biome.RiparianGroundCover, rip);
        if (cover <= 0) return (0, 0);
        float k = MathF.Max(0, 1 + Biome.Patchiness * (2 * Fbm(x / Biome.PatchScale, z / Biome.PatchScale, VegHash.Mix(Seed, 0xC0FE)) - 1) * 1.25f);
        if (rip > 0) k *= Lerp(1, 0.35f + 1.3f * Fbm(x / 22f, z / 22f, VegHash.Mix(Seed, 0xB0B)), rip);
        // the base cover is the biome's: the share is against it
        k *= cover / Biome.GroundCover;
        if (Terrain.Rough(x, z)) k *= RoughGround;
        bool open = Terrain.Open(x, z);
        var site = new SiteInfo(0, rip, open, MathF.Max(SmoothStep(0.12f, 0.5f, slope), open ? 0f : 0.5f), 0, 0.5f);
        Span<float> w = stackalloc float[Biome.Ground.Length];
        var (total, _, _) = Weights(VegLayer.Ground, x, z, slope, site, w);
        if (total <= 1e-6f) return (k, 0);
        float grass = 0;
        for (int i = 0; i < w.Length; i++) if (Species[Biome.Shrubs.Length + i].Grass) grass += w[i];
        return (k, grass / total);
    }

    /// <summary>The sward from its broad part (<see cref="SwardBroad"/>) and what varies within metres: the grasses' clumps, open or broken ground, the slope, objects.</summary>
    public float SwardFine(float x, float z, float broad, float share)
    {
        if (broad * share <= 0) return 0;
        if (Terrain.RoadSurface(x, z) && !(Params.LowGround && Riparian(Terrain.Ground(x, z)) > 0)
            && Mask.RoadDistance(new Vector2(x, z), RoadsideStrip) >= RoadsideStrip) return 0;
        float slope = Terrain.Slope(x, z);
        if (slope > 1.2f) return 0;
        float k = broad;
        if (Biome.TuftClump > 0) k *= MathF.Max(0, 1 + Biome.TuftClumpiness * (2 * Fbm(x / Biome.TuftClump, z / Biome.TuftClump, VegHash.Mix(Seed, 0x7F7)) - 1) * 1.25f);
        if (!Terrain.Open(x, z)) k *= 0.7f;
        k *= 1 - SmoothStep(0.3f, 1.1f, slope);
        if (k <= 0 || Mask.Blocked(new Vector2(x, z), 0f, 0.5f, -1e9f, discs: false)) return 0;
        return Math.Clamp(Biome.Sward * k * share, 0, 1);
    }

    /// <summary>On rough ground: the shrub cover's factor, the size (metres) of its thickets, and the ground layer's factor (see <see cref="BiomeSpecies.Rough"/>).</summary>
    public const float RoughShrubs = 3.2f, RoughClump = 18f, RoughGround = 0.7f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Smooth value noise in [0, 1].</summary>
    public static float Value(float x, float z, ulong key)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
        float a = VegHash.Unit(ix, iz, key), b = VegHash.Unit(ix + 1, iz, key);
        float c = VegHash.Unit(ix, iz + 1, key), d = VegHash.Unit(ix + 1, iz + 1, key);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fz);
    }

    /// <summary>Three octaves of <see cref="Value"/>, in [0, 1] (mean 0.5).</summary>
    public static float Fbm(float x, float z, ulong key)
    {
        float s = 0, amp = 0.5f, norm = 0;
        for (int o = 0; o < 3; o++)
        {
            s += amp * Value(x, z, key + (ulong)o * 7919);
            norm += amp; amp *= 0.5f; x *= 2.03f; z *= 2.03f;
        }
        // spread the octave sum (which bunches about 0.5) back over most of [0, 1]
        return Math.Clamp(0.5f + (s / norm - 0.5f) * 1.8f, 0, 1);
    }
}
