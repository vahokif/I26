namespace I76.Formats.Vegetation;

/// <summary>
/// A species' share of a biome: its <see cref="Weight"/> among the plants of its layer, where it grows between the
/// uplands and the low-ground strip (<see cref="Riparian"/>: -1 uplands only, 0 anywhere, 1 by the river only), the
/// steepest ground it takes (rise over run), the size of its own patches (metres; 0: none), how it takes to rocky
/// slopes (<see cref="Rocky"/>: -1 flats only, 0 either, 1 slopes and broken ground only), how it crowds the
/// disturbed ground along roads (<see cref="Roadside"/>: 0 not at all, 1 only there), and whether its patches are
/// dense stands of their own (<see cref="Motte"/>: 0 no, 1 thickets: the cells inside one fill with it), and how
/// much more it grows on the original's rough ground (<see cref="Rough"/>, surface id 5: a factor on its weight there).
/// </summary>
public sealed record BiomeSpecies(string Id, float Weight, float Riparian = 0f, float MaxSlope = 0.7f, float Clump = 0f,
    float Rocky = 0f, float Roadside = 0f, float Motte = 0f, float Rough = 1f);

/// <summary>
/// The plant community of a mission's land: two placement grids (shrubs, and grasses and forbs), the chance a cell
/// holds a plant on flat open uplands and in the low-ground strip, and the species of each.
/// </summary>
public sealed record Biome
{
    public required string Name { get; init; }
    /// <summary>Shrub grid cell (metres): one shrub a cell at most.</summary>
    public float ShrubCell { get; init; } = 4f;
    public float ShrubCover { get; init; } = 0.15f;
    public float RiparianShrubCover { get; init; } = 0.5f;
    public float GroundCell { get; init; } = 2f;
    public float GroundCover { get; init; } = 0.3f;
    public float RiparianGroundCover { get; init; } = 0.4f;
    /// <summary>The low-ground strip: ground up to this many metres above the mission's low ground (its river bottom, a salt flat), fading out over the top half.</summary>
    public float RiparianBand { get; init; } = 3.5f;
    /// <summary>The size of the patches the cover comes in (metres): bare stretches between denser stands.</summary>
    public float PatchScale { get; init; } = 60f;
    /// <summary>How strongly the patches vary the cover: 0 even, 1 from bare to twice the mean.</summary>
    public float Patchiness { get; init; } = 0.8f;
    /// <summary>
    /// How grassy the ground between the plants is where the grasses are at the biome's cover (0 bare, 1 a closed
    /// sward): the modern ground takes the dry grass's colour by this much (<see cref="SwardMap"/>), so a prairie
    /// reads as covered rather than dotted.
    /// </summary>
    public float Sward { get; init; }
    /// <summary>The grasses' own clumping (metres; 0 none): denser patches of bigger tufts and thin ones between.</summary>
    public float TuftClump { get; init; }
    public float TuftClumpiness { get; init; } = 0.6f;
    /// <summary>
    /// How many tumbleweeds roll here, 0..1 of the most (<see cref="TumbleweedField.CountFor"/>: 4 on Low, 7 on High):
    /// open, dry, disturbed or grassy flats only; 0 (none) for most.
    /// </summary>
    public float Tumbleweeds { get; init; }
    public required BiomeSpecies[] Shrubs { get; init; }
    public required BiomeSpecies[] Ground { get; init; }

    public IEnumerable<BiomeSpecies> All => Shrubs.Concat(Ground);
}

/// <summary>
/// What a mission grows: its biome, whether its low ground is a strip of its own (a river bottom, where the riparian
/// species grow, or a salt flat), and a factor on the biome's cover.
/// </summary>
public sealed record VegRegion(Biome Biome, bool LowGround = false, float Density = 1f);

/// <summary>
/// Which missions grow what (docs/modern-vegetation.md, Regions). The trip follows the real road: the South Plains of
/// west Texas, Highway 380 into the sand country of south-eastern New Mexico, across the Pecos, south through the
/// Trans-Pecos to Fort Davis in the Davis Mountains. Missions not listed (custom maps, the Nitro Pack's) go by their
/// ground family (WDEF): a desert one grows a modest Chihuahuan / South Plains mix, the grass one a green prairie,
/// snow and asphalt nothing.
/// </summary>
public static class Biomes
{
    /// <summary>
    /// The South Plains of west Texas (Seagraves, Levelland, Plains): shortgrass prairie, blue grama and buffalograss,
    /// honey mesquite scattered and in mottes, soapweed yucca, sand sagebrush, a few cholla and prickly pear;
    /// snakeweed and tumbleweeds on the disturbed ground by the roads, wildflowers here and there.
    /// </summary>
    public static readonly Biome SouthPlains = new()
    {
        Name = "south plains",
        Tumbleweeds = 0.6f,
        Sward = 0.8f, TuftClump = 11f,
        ShrubCell = 4f, ShrubCover = 0.09f, RiparianShrubCover = 0.09f,
        GroundCell = 1.5f, GroundCover = 0.62f, RiparianGroundCover = 0.62f,
        PatchScale = 70f, Patchiness = 0.7f,
        Shrubs =
        [
            new("mesquite", 0.7f, MaxSlope: 0.5f, Clump: 45f, Rocky: -0.3f, Motte: 0.12f),
            new("soapweed", 0.9f, MaxSlope: 0.7f, Clump: 30f, Rough: 3.5f),
            new("sandsage", 0.9f, MaxSlope: 0.6f, Clump: 35f, Rocky: -0.4f),
            new("pricklypear", 0.4f, MaxSlope: 0.9f, Clump: 25f),
            new("cholla", 0.18f, MaxSlope: 0.8f, Clump: 40f, Rough: 5f),
        ],
        Ground =
        [
            new("grama", 1.0f, MaxSlope: 0.8f, Clump: 20f),
            new("buffalograss", 0.9f, MaxSlope: 0.5f, Clump: 25f, Rocky: -0.5f),
            new("snakeweed", 0.22f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.7f),
            new("tumbleweed", 0.1f, MaxSlope: 0.6f, Roadside: 0.8f),
            new("indianblanket", 0.05f, MaxSlope: 0.6f, Clump: 15f),
            new("horsemint", 0.04f, MaxSlope: 0.6f, Clump: 15f),
            new("pricklypoppy", 0.03f, MaxSlope: 0.5f, Clump: 20f, Roadside: 0.5f),
        ],
    };

    /// <summary>The same land gone to weeds (a deserted town): snakeweed, tumbleweeds and prickly poppy everywhere, less grass.</summary>
    public static readonly Biome SouthPlainsWeedy = SouthPlains with
    {
        Name = "south plains, weedy",
        Tumbleweeds = 1f,
        Sward = 0.55f,
        GroundCover = 0.5f, RiparianGroundCover = 0.5f,
        Ground =
        [
            new("grama", 0.7f, MaxSlope: 0.8f, Clump: 20f),
            new("buffalograss", 0.4f, MaxSlope: 0.5f, Clump: 25f),
            new("snakeweed", 0.8f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.3f),
            new("tumbleweed", 0.5f, MaxSlope: 0.6f, Roadside: 0.4f),
            new("pricklypoppy", 0.1f, MaxSlope: 0.5f, Clump: 20f),
            new("indianblanket", 0.06f, MaxSlope: 0.6f, Clump: 15f),
        ],
    };

    /// <summary>
    /// The sand country of south-eastern New Mexico (Highway 380 at Tatum, the state line): shinnery oak in low clonal
    /// thickets on the sandy flats, sand sagebrush, mesquite, soapweed yucca, grama and sand dropseed.
    /// </summary>
    public static readonly Biome SandCountry = new()
    {
        Name = "sand country",
        Tumbleweeds = 0.5f,
        Sward = 0.45f, TuftClump = 12f,
        ShrubCell = 4f, ShrubCover = 0.2f, RiparianShrubCover = 0.2f,
        GroundCell = 2f, GroundCover = 0.45f, RiparianGroundCover = 0.45f,
        PatchScale = 60f, Patchiness = 0.8f,
        Shrubs =
        [
            new("shinnery", 1.2f, MaxSlope: 0.45f, Clump: 30f, Rocky: -0.7f, Motte: 0.55f),
            new("sandsage", 0.7f, MaxSlope: 0.6f, Clump: 35f, Rocky: -0.3f),
            new("mesquite", 0.3f, MaxSlope: 0.5f, Clump: 50f, Motte: 0.2f),
            new("soapweed", 0.45f, MaxSlope: 0.7f, Clump: 30f, Rough: 3.5f),
            new("pricklypear", 0.12f, MaxSlope: 0.9f, Clump: 25f),
        ],
        Ground =
        [
            new("grama", 0.7f, MaxSlope: 0.8f, Clump: 20f),
            new("dropseed", 0.8f, MaxSlope: 0.6f, Clump: 22f, Rocky: -0.3f),
            new("buffalograss", 0.25f, MaxSlope: 0.5f, Clump: 25f),
            new("snakeweed", 0.15f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.6f),
            new("tumbleweed", 0.1f, MaxSlope: 0.6f, Roadside: 0.7f),
            new("indianblanket", 0.04f, MaxSlope: 0.6f, Clump: 15f),
        ],
    };

    /// <summary>
    /// Highway 380 at the Pecos, south-eastern New Mexico: Chihuahuan desert scrub (creosote flats, saltbush, mesquite,
    /// cholla and prickly pear, yuccas, grama and tobosa grass, snakeweed and tumbleweeds) and, along the river
    /// bottom, salt cedar thickets, coyote willow, mesquite and alkali sacaton.
    /// </summary>
    public static readonly Biome Pecos = new()
    {
        Name = "pecos",
        Tumbleweeds = 0.4f,
        Sward = 0.3f, TuftClump = 12f,
        ShrubCell = 4f, ShrubCover = 0.26f, RiparianShrubCover = 0.65f,
        GroundCell = 2f, GroundCover = 0.45f, RiparianGroundCover = 0.6f,
        RiparianBand = 3.5f, PatchScale = 55f, Patchiness = 0.85f,
        Shrubs =
        [
            new("creosote", 1.0f, Riparian: -0.9f, MaxSlope: 0.6f, Clump: 40f),
            new("saltbush", 0.35f, Riparian: 0.2f, MaxSlope: 0.5f, Clump: 30f),
            new("mesquite", 0.3f, Riparian: 0.5f, MaxSlope: 0.45f, Clump: 50f),
            new("cholla", 0.2f, Riparian: -0.8f, MaxSlope: 0.8f, Clump: 35f, Rough: 5f),
            new("pricklypear", 0.24f, Riparian: -0.6f, MaxSlope: 0.9f, Clump: 25f),
            new("soaptree", 0.1f, Riparian: -0.9f, MaxSlope: 0.5f, Clump: 60f, Rough: 3f),
            new("soapweed", 0.13f, Riparian: -0.7f, MaxSlope: 0.7f, Clump: 30f, Rough: 3.5f),
            new("tamarisk", 1.2f, Riparian: 1f, MaxSlope: 0.4f, Clump: 25f),
            new("willow", 0.55f, Riparian: 1f, MaxSlope: 0.35f, Clump: 20f),
        ],
        Ground =
        [
            new("grama", 1.0f, Riparian: -0.6f, MaxSlope: 0.7f, Clump: 20f),
            new("tobosa", 0.6f, Riparian: -0.2f, MaxSlope: 0.5f, Clump: 25f),
            new("sacaton", 1.2f, Riparian: 1f, MaxSlope: 0.4f, Clump: 15f),
            new("tumbleweed", 0.1f, Riparian: 0f, MaxSlope: 0.6f),
            new("snakeweed", 0.3f, Riparian: -0.7f, MaxSlope: 0.7f, Clump: 18f),
        ],
    };

    /// <summary>
    /// The Trans-Pecos south of the river: creosote flats, and on the rocky slopes lechuguilla in colonies, ocotillo,
    /// sotol and candelilla; Torrey yucca, prickly pear, tobosa and chino grama.
    /// </summary>
    public static readonly Biome TransPecos = new()
    {
        Name = "trans-pecos",
        Sward = 0.25f,
        ShrubCell = 4f, ShrubCover = 0.26f, RiparianShrubCover = 0.26f,
        GroundCell = 2f, GroundCover = 0.42f, RiparianGroundCover = 0.42f,
        PatchScale = 55f, Patchiness = 0.85f,
        Shrubs =
        [
            new("creosote", 1.0f, MaxSlope: 0.6f, Clump: 40f, Rocky: -0.3f),
            new("ocotillo", 0.45f, MaxSlope: 1.0f, Clump: 40f, Rocky: 0.5f),
            new("sotol", 0.4f, MaxSlope: 1.0f, Clump: 30f, Rocky: 0.5f, Rough: 2.5f),
            new("torrey", 0.22f, MaxSlope: 0.6f, Clump: 50f, Rocky: -0.1f, Rough: 3f),
            new("pricklypear", 0.2f, MaxSlope: 0.9f, Clump: 25f),
            new("mesquite", 0.05f, MaxSlope: 0.4f, Clump: 50f, Rocky: -0.6f),
        ],
        Ground =
        [
            new("tobosa", 0.7f, MaxSlope: 0.5f, Clump: 25f, Rocky: -0.5f),
            new("chinograma", 0.8f, MaxSlope: 0.8f, Clump: 20f),
            new("lechuguilla", 0.6f, MaxSlope: 1.1f, Clump: 18f, Rocky: 0.6f, Motte: 0.35f),
            new("candelilla", 0.2f, MaxSlope: 1.0f, Clump: 15f, Rocky: 0.5f),
            new("snakeweed", 0.12f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.5f),
            new("tumbleweed", 0.06f, MaxSlope: 0.6f, Roadside: 0.7f),
        ],
    };

    /// <summary>
    /// The Davis Mountains round Fort Davis: grassland (sideoats and blue grama, little bluestem), sotol, beargrass,
    /// agave and shrubby gray oak on the slopes, prickly pear, a little creosote on the low flats.
    /// </summary>
    public static readonly Biome DavisMountains = new()
    {
        Name = "davis mountains",
        Sward = 0.85f, TuftClump = 10f,
        ShrubCell = 4f, ShrubCover = 0.2f, RiparianShrubCover = 0.2f,
        GroundCell = 1.5f, GroundCover = 0.7f, RiparianGroundCover = 0.7f,
        PatchScale = 65f, Patchiness = 0.75f,
        Shrubs =
        [
            new("grayoak", 0.9f, MaxSlope: 1.0f, Clump: 45f, Rocky: 0.4f, Motte: 0.3f),
            new("sotol", 0.55f, MaxSlope: 1.0f, Clump: 30f, Rocky: 0.25f, Rough: 2.5f),
            new("beargrass", 0.7f, MaxSlope: 0.9f, Clump: 25f, Rocky: 0.1f),
            new("agave", 0.35f, MaxSlope: 1.0f, Clump: 20f, Rocky: 0.4f, Rough: 3f),
            new("pricklypear", 0.25f, MaxSlope: 0.9f, Clump: 25f),
            new("creosote", 0.12f, MaxSlope: 0.3f, Clump: 40f, Rocky: -0.9f),
        ],
        Ground =
        [
            new("sideoats", 1.0f, MaxSlope: 0.9f, Clump: 25f),
            new("grama", 0.9f, MaxSlope: 0.8f, Clump: 20f),
            new("bluestem", 0.6f, MaxSlope: 0.9f, Clump: 30f, Rocky: 0.2f),
            new("indianblanket", 0.03f, MaxSlope: 0.6f, Clump: 15f),
            new("horsemint", 0.03f, MaxSlope: 0.6f, Clump: 15f),
            new("snakeweed", 0.06f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.5f),
        ],
    };

    /// <summary>T04, the dream race: a sparse stylised garden of the trip's showiest plants, flowering ocotillo and yuccas over drifts of wildflowers.</summary>
    public static readonly Biome Dream = new()
    {
        Name = "dream",
        ShrubCell = 4f, ShrubCover = 0.06f, RiparianShrubCover = 0.06f,
        GroundCell = 2f, GroundCover = 0.25f, RiparianGroundCover = 0.25f,
        PatchScale = 45f, Patchiness = 1f,
        Shrubs =
        [
            new("ocotillo", 0.6f, MaxSlope: 1.0f, Clump: 30f),
            new("soaptree", 0.5f, MaxSlope: 0.6f, Clump: 40f, Rough: 3f),
            new("beargrass", 0.4f, MaxSlope: 0.9f, Clump: 25f),
            new("agave", 0.4f, MaxSlope: 1.0f, Clump: 25f, Rough: 3f),
            new("sotol", 0.3f, MaxSlope: 1.0f, Clump: 30f, Rough: 2.5f),
        ],
        Ground =
        [
            new("indianblanket", 1.0f, MaxSlope: 0.8f, Clump: 18f, Motte: 0.4f),
            new("horsemint", 0.6f, MaxSlope: 0.8f, Clump: 18f, Motte: 0.3f),
            new("pricklypoppy", 0.4f, MaxSlope: 0.8f, Clump: 20f),
            new("bluestem", 0.5f, MaxSlope: 0.9f, Clump: 25f),
        ],
    };

    /// <summary>Roadside weeds only (an airport's infield, an asphalt track's verges): snakeweed, tumbleweeds, a little grama.</summary>
    public static readonly Biome Weeds = new()
    {
        Name = "roadside weeds",
        Tumbleweeds = 0.9f,
        Sward = 0.15f,
        ShrubCell = 4f, ShrubCover = 0f, RiparianShrubCover = 0f,
        GroundCell = 2f, GroundCover = 0.22f, RiparianGroundCover = 0.22f,
        PatchScale = 50f, Patchiness = 0.9f,
        Shrubs = [],
        Ground =
        [
            new("snakeweed", 1.0f, MaxSlope: 0.8f, Clump: 18f),
            new("tumbleweed", 0.6f, MaxSlope: 0.7f),
            new("grama", 0.8f, MaxSlope: 0.8f, Clump: 20f),
        ],
    };

    /// <summary>Suburbs: weeds at the lawns' edges and a few planted desert shrubs.</summary>
    public static readonly Biome Suburb = new()
    {
        Name = "suburb",
        Tumbleweeds = 0.25f,
        Sward = 0.3f,
        ShrubCell = 4f, ShrubCover = 0.04f, RiparianShrubCover = 0.04f,
        GroundCell = 2f, GroundCover = 0.2f, RiparianGroundCover = 0.2f,
        PatchScale = 40f, Patchiness = 0.9f,
        Shrubs =
        [
            new("soaptree", 0.3f, MaxSlope: 0.5f, Rough: 3f),
            new("pricklypear", 0.4f, MaxSlope: 0.8f),
            new("agave", 0.3f, MaxSlope: 0.8f, Rough: 3f),
            new("sotol", 0.2f, MaxSlope: 0.8f, Rough: 2.5f),
            new("beargrass", 0.2f, MaxSlope: 0.8f),
            new("mesquite", 0.3f, MaxSlope: 0.5f),
        ],
        Ground =
        [
            new("buffalograss", 0.6f, MaxSlope: 0.6f, Clump: 20f),
            new("grama", 0.6f, MaxSlope: 0.8f, Clump: 20f),
            new("snakeweed", 0.4f, MaxSlope: 0.8f, Clump: 15f),
            new("tumbleweed", 0.3f, MaxSlope: 0.7f),
            new("indianblanket", 0.15f, MaxSlope: 0.7f, Clump: 15f),
            new("pricklypoppy", 0.05f, MaxSlope: 0.7f),
        ],
    };

    /// <summary>A salt flat: bare on the flat itself (the low ground), saltbush and pickleweed only round its edges and on the higher ground.</summary>
    public static readonly Biome SaltFlat = new()
    {
        Name = "salt flat",
        ShrubCell = 4f, ShrubCover = 0.08f, RiparianShrubCover = 0f,
        GroundCell = 2f, GroundCover = 0.2f, RiparianGroundCover = 0f,
        RiparianBand = 1.5f, PatchScale = 50f, Patchiness = 0.9f,
        Shrubs = [new("saltbush", 1.0f, Riparian: -1f, MaxSlope: 0.6f, Clump: 30f)],
        Ground = [new("pickleweed", 1.0f, Riparian: -0.4f, MaxSlope: 0.6f, Clump: 20f)],
    };

    /// <summary>Custom maps on a desert ground: a modest Chihuahuan / South Plains mix.</summary>
    public static readonly Biome DesertMix = new()
    {
        Name = "desert mix",
        Tumbleweeds = 0.3f,
        Sward = 0.3f,
        ShrubCell = 4f, ShrubCover = 0.14f, RiparianShrubCover = 0.14f,
        GroundCell = 2f, GroundCover = 0.35f, RiparianGroundCover = 0.35f,
        PatchScale = 60f, Patchiness = 0.8f,
        Shrubs =
        [
            new("creosote", 0.6f, MaxSlope: 0.6f, Clump: 40f),
            new("mesquite", 0.5f, MaxSlope: 0.5f, Clump: 50f),
            new("soapweed", 0.4f, MaxSlope: 0.7f, Clump: 30f, Rough: 3.5f),
            new("sandsage", 0.3f, MaxSlope: 0.6f, Clump: 35f),
            new("saltbush", 0.3f, MaxSlope: 0.5f, Clump: 30f),
            new("pricklypear", 0.3f, MaxSlope: 0.9f, Clump: 25f),
            new("cholla", 0.15f, MaxSlope: 0.8f, Clump: 35f, Rough: 5f),
        ],
        Ground =
        [
            new("grama", 1.0f, MaxSlope: 0.8f, Clump: 20f),
            new("tobosa", 0.4f, MaxSlope: 0.5f, Clump: 25f),
            new("snakeweed", 0.2f, MaxSlope: 0.7f, Clump: 18f, Roadside: 0.5f),
            new("tumbleweed", 0.1f, MaxSlope: 0.6f, Roadside: 0.6f),
        ],
    };

    /// <summary>Custom maps on the grass ground: a green prairie of tall and short grasses and forbs.</summary>
    public static readonly Biome Prairie = new()
    {
        Name = "prairie",
        Sward = 0.85f, TuftClump = 10f,
        ShrubCell = 4f, ShrubCover = 0.04f, RiparianShrubCover = 0.04f,
        GroundCell = 2f, GroundCover = 0.6f, RiparianGroundCover = 0.6f,
        PatchScale = 60f, Patchiness = 0.7f,
        Shrubs =
        [
            new("soapweed", 0.5f, MaxSlope: 0.7f, Clump: 30f, Rough: 3.5f),
            new("sandsage", 0.5f, MaxSlope: 0.6f, Clump: 35f),
        ],
        Ground =
        [
            new("bluestem", 0.8f, MaxSlope: 0.9f, Clump: 30f),
            new("sideoats", 0.8f, MaxSlope: 0.9f, Clump: 25f),
            new("grama", 0.6f, MaxSlope: 0.8f, Clump: 20f),
            new("buffalograss", 0.5f, MaxSlope: 0.5f, Clump: 25f),
            new("indianblanket", 0.1f, MaxSlope: 0.7f, Clump: 15f),
            new("horsemint", 0.1f, MaxSlope: 0.7f, Clump: 15f),
            new("pricklypoppy", 0.04f, MaxSlope: 0.6f, Clump: 20f),
        ],
    };

    /// <summary>Every biome, for the tests.</summary>
    public static readonly Biome[] Every = [SouthPlains, SouthPlainsWeedy, SandCountry, Pecos, TransPecos, DavisMountains, Dream, Weeds, Suburb, SaltFlat, DesertMix, Prairie];

    private static readonly Dictionary<string, VegRegion> ByMission = new(StringComparer.OrdinalIgnoreCase)
    {
        // the trip: the South Plains of west Texas
        ["t01"] = new(SouthPlains), ["t02"] = new(SouthPlains), ["t03"] = new(SouthPlains), ["t05"] = new(SouthPlains),
        ["t06"] = new(SouthPlains), ["t07"] = new(SouthPlains), ["t08"] = new(SouthPlains), ["t09"] = new(SouthPlains),
        ["s01"] = new(SouthPlains), ["s02"] = new(SouthPlains), ["s04"] = new(SouthPlains), ["s05"] = new(SouthPlains), ["s07"] = new(SouthPlains),
        // Highway 380 into New Mexico: Tatum, the state line
        ["t10"] = new(SandCountry), ["s06"] = new(SandCountry),
        // the Pecos (T11 crosses its river bottom) and the oilfield
        ["t11"] = new(Pecos, LowGround: true), ["t12"] = new(Pecos), ["s03"] = new(Pecos),
        // south through the Trans-Pecos to Fort Davis
        ["t13"] = new(TransPecos), ["t14"] = new(TransPecos),
        ["t15"] = new(DavisMountains), ["t16"] = new(DavisMountains), ["t17"] = new(DavisMountains),
        ["t04"] = new(Dream),
        ["a01"] = new(SouthPlains, Density: 0.5f),
        // melee: the arenas named in the notepads, else by their ground
        ["m02"] = new(Pecos), ["m03"] = new(Weeds), ["m04"] = new(Suburb), ["m06"] = new(SouthPlainsWeedy),
        ["m07"] = new(Pecos), ["m08"] = new(Pecos), ["m09"] = new(Pecos), ["m10"] = new(SaltFlat, LowGround: true),
        ["m13"] = new(DavisMountains),
        // M15 is a painted parking lot (GroundMarkings): asphalt, which grows nothing
    };

    /// <summary>The melee arenas that go by their ground family between the South Plains and the Pecos (the bowl, the desert tracks, the highway).</summary>
    private static readonly HashSet<string> ByFamily = new(StringComparer.OrdinalIgnoreCase) { "m01", "m05", "m11", "m12" };

    /// <summary>
    /// The kind of a ground family (WDEF's ground, "tp041db6.map": TP, a number, a level digit, the kind, the level
    /// count): db, dr, sd desert; gr grass; sw, sr snow; as asphalt. Null when the name does not read so.
    /// </summary>
    public static string? FamilyKind(string? ground)
    {
        if (ground is null) return null;
        var stem = Path.GetFileNameWithoutExtension(ground.Trim());
        return stem.Length >= 7 ? stem.Substring(5, 2).ToLowerInvariant() : null;
    }

    /// <summary>A ground family's number (tp<b>05</b>1dr6), or -1.</summary>
    private static int FamilyNumber(string? ground)
    {
        var stem = ground is null ? "" : Path.GetFileNameWithoutExtension(ground.Trim());
        return stem.Length >= 7 && int.TryParse(stem.AsSpan(2, 2), out int n) ? n : -1;
    }

    /// <summary>
    /// What a mission grows, or null: nothing. Listed missions by name; the rest by their ground family
    /// (<paramref name="ground"/>, WDEF's): desert families the desert mix, the grass family the prairie, snow and
    /// asphalt nothing (winter comes later).
    /// </summary>
    public static VegRegion? For(string mission, string? ground = null)
    {
        if (ByMission.TryGetValue(mission, out var r)) return r;
        string? kind = FamilyKind(ground);
        bool desert = kind is "db" or "dr" or "sd";
        if (ByFamily.Contains(mission))
            // the campaign's Pecos and Davis grounds (02, 03, 05) to the Pecos, the South Plains' (04, 06-09) to it
            return desert ? new(FamilyNumber(ground) is 2 or 3 or 5 ? Pecos : SouthPlains) : null;
        return kind switch
        {
            "db" or "dr" or "sd" => new(DesertMix),
            "gr" => new(Prairie),
            _ => null,
        };
    }
}
