namespace I76.Sim.Trip;

/// <summary>
/// The wheel files as a player thinks of them: a <b>style</b> (the family, the first seven letters of the stem, "wauto_3": the look of the rim
/// and tyre a car came with) and a <b>size</b> (the eighth letter, a to d: 13in Stock, 14in Rally, 15in Kragers, 16in Billets, the tyre grades the
/// original's tyre picker offers, which differ in nothing but the numbers in the file). Every family's file of a size carries the same
/// numbers (grip 1.0 / 1.2 / 1.4 / 1.6, mass 10, 100 hits; read from all of both games' files), so the size alone decides how the car
/// drives and the style only how it looks. The garage writes the one file the pair names, family + size + ".wdf", as the original's own
/// pickers would have, so a variant stays readable by the original game.
/// </summary>
public static class WheelStyles
{
    /// <summary>
    /// What each style looks like, for the garage's list (the first line), and which of the game's cars wear it is worked out from the
    /// install's own cars. Edit the names here; a family missing from the table is shown by its file name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wauto_0"] = "Dark Star Mag",        // dark spokes in a chrome lip
        ["wauto_1"] = "Vented Hubcap",        // silver, a ring of holes round a centre cap
        ["wauto_2"] = "Whitewall Cover",      // a full silver wheel cover in a white sidewall
        ["wauto_3"] = "Rusty Steel",          // bare black steel
        ["wauto_4"] = "Silver Mag",           // five wide silver spokes
        ["wauto_5"] = "Plain Hubcap",         // a small silver cap on steel
        ["wauto_6"] = "Wire Spokes",          // fine spokes with a gold stripe
        ["wauto_7"] = "Blue Whitewall",       // blue steel, white sidewall, chrome cap
        ["wauto_8"] = "Chrome Dog-Dish",      // a chrome dish cap
        ["wauto_9"] = "Alloy Spokes",         // slim blue-grey spokes
        ["wauto_a"] = "Red Whitewall",        // red steel, white sidewall, chrome cap
        ["wbtck_1"] = "Truck Duals",          // the bus's rear: a pair of fat tyres on a silver disc
        ["wftck_1"] = "Truck Disc",           // the truck's front: a silver disc
        ["wxtcf_1"] = "Red Truck Rim",        // a red rim with lug nuts
        ["wytcb_1"] = "Red Truck Duals",      // the same red, in a pair
        ["wctnk_1"] = "Tank Wheel",           // the tanks' (nothing is drawn)
        ["wdtnk_1"] = "Tank Tread",           // a tread plate
        ["wetnk_1"] = "Tank Tread B",
        ["wdilo_1"] = "Not Drawn",            // a wheel that has no model
    };

    /// <summary>The style of a wheel file: its first seven letters in lower case ("wauto_3a.wdf" gives "wauto_3").</summary>
    public static string Family(string stem) => (stem.Length >= 7 ? stem[..7] : stem).ToLowerInvariant();

    /// <summary>The size letter of a wheel file (the eighth character), 'a' for a name too short to have one.</summary>
    public static char SizeOf(string stem) => stem.Length >= 8 ? char.ToLowerInvariant(stem[7]) : 'a';

    /// <summary>The name shown for a style: the table's, else the family itself.</summary>
    public static string NameOf(string family) => Names.TryGetValue(family, out var n) ? n : family;

    /// <summary>The styles of a set of wheel parts, each once, in file order.</summary>
    public static List<string> Families(IEnumerable<PartDef> wheels) => [.. wheels.Select(w => Family(w.Stem)).Distinct()];

    /// <summary>The sizes a style has, smallest first.</summary>
    public static List<PartDef> Sizes(IEnumerable<PartDef> wheels, string family) =>
        [.. wheels.Where(w => Family(w.Stem) == family).OrderBy(w => SizeOf(w.Stem))];

    /// <summary>
    /// The file of a style at a size: exactly family + size where the style has it, else the style's nearest size (the smaller of two equally
    /// near), <paramref name="exact"/> false. Null for a style without any wheel.
    /// </summary>
    public static PartDef? Pick(IEnumerable<PartDef> wheels, string family, char size, out bool exact)
    {
        var all = Sizes(wheels, family);
        exact = false;
        if (all.Count == 0) return null;
        size = char.ToLowerInvariant(size);
        var best = all.OrderBy(w => System.Math.Abs(SizeOf(w.Stem) - size)).ThenBy(w => SizeOf(w.Stem)).First();
        exact = SizeOf(best.Stem) == size;
        return best;
    }
}
