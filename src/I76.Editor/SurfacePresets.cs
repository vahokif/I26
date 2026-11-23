using System.Globalization;
using I76.Formats.Mission;

namespace I76.Editor;

/// <summary>
/// A ready row for the surface table (docs/map-editor.md "Surface table"): the fields it sets, the rest of the row kept. Values are the stock
/// maps' where a stock map has one.
/// </summary>
/// <param name="Name">The button's name.</param>
/// <param name="Tip">What it is and where its values come from.</param>
/// <param name="Stock">Puts the id's row of <see cref="SurfaceTable.Stock"/> back whole (the other fields are ignored).</param>
public sealed record SurfacePreset(string Name, string Tip, float? Grip = null, float? Rolling = null, float? Bump = null, int? Damage = null, bool Stock = false)
{
    /// <summary>Row <paramref name="id"/> as this preset makes it from <paramref name="row"/>.</summary>
    public SurfaceRow Apply(int id, SurfaceRow row) => Stock ? SurfaceTable.Stock[id]
        : row with { Grip = Grip ?? row.Grip, Rolling = Rolling ?? row.Rolling, Bump = Bump ?? row.Bump, Damage = Damage ?? row.Damage };
}

/// <summary>The surface table's presets, and the names the surface tool shows for rows that are not the stock ones.</summary>
public static class SurfacePresets
{
    /// <summary>
    /// Grip below this is ice: lower than any stock map's row (the least, 1.0, is S02's and Mojave Sprint's rough ground, Pitchfork's sand and
    /// Vigilantes Peak's rough ground).
    /// </summary>
    public const float IceBelow = 1.0f;
    /// <summary>Damage a second from this up is named for it: the stock maps' ordinary rows take at most 15 (Matterhorn Run's rough ground).</summary>
    public const int DamagingFrom = 100;
    /// <summary>
    /// Damage a second from this up strips a car at once (the race tracks' 999999999 off the track): terrain damage goes under the armour, into
    /// the chassis reinforcement and then the parts. It does not kill: the original's terrain damage has no kill roll, so the stripped car is
    /// wrecked by the next hit it takes.
    /// </summary>
    public const int StrippingFrom = 10000;

    public static readonly SurfacePreset Stock = new("STOCK", "The row most stock maps have for this id (92 of the 114 missions of both games)", Stock: true);
    public static readonly SurfacePreset Ice = new("ICE",
        "Grip 0.5, rolling 0.01: the car spins out in a hard turn and gets going slowly (the Piranha reaches 13 m/s in 6 s, 31 on stock dirt). No stock map is this slippery. Damage kept",
        Grip: 0.5f, Rolling: 0.01f, Bump: 0);
    public static readonly SurfacePreset BlackIce = new("BLACK ICE",
        "Grip 0.2, rolling 0.01: barely any traction; the wheels spin, the car slides and slips down slopes (4.5 m/s in 6 s). 0.2 is the least grip the original's oil slick leaves alone (it zeroes only grip above 0.2). Damage kept",
        Grip: 0.2f, Rolling: 0.01f, Bump: 0);
    public static readonly SurfacePreset Snow = new("SNOW", "Grip 1.2: Matterhorn Run's snowy dirt roads (N24, ids 3 and 7), the least grip of a stock road. Damage kept", Grip: 1.2f, Rolling: 0.03f, Bump: 0);
    public static readonly SurfacePreset Rough = new("ROUGH", "S02's rough ground (id 5): grip 1.0, rolling 0.5, bump 0.5, 14 damage a second", Grip: 1.0f, Rolling: 0.5f, Bump: 0.5f, Damage: 14);
    public static readonly SurfacePreset Damaging = new("DAMAGING",
        "200 damage a second (Piece B with U's rough ground, P07 id 5): it wears through the chassis reinforcement in a few seconds, then the tyres, engine, brakes and weapons. Grip kept",
        Damage: 200);
    public static readonly SurfacePreset Stripping = new("STRIPPING",
        "999999999 damage a second (The Noose and Cactus Mountain off the track, N29 and N21 id 5): the chassis reinforcement and every part but the chassis gone at once, the car left without tyres, engine or weapons. Terrain damage never kills: the next hit does. Grip kept",
        Damage: 999999999);
    public static readonly SurfacePreset Harmless = new("NO DAMAGE", "0 damage a second. Grip kept", Damage: 0);

    public static readonly SurfacePreset[] All = [Stock, Ice, BlackIce, Snow, Rough, Damaging, Stripping, Harmless];

    /// <summary>
    /// What the surface tool calls a row: ICE, DAMAGING or STRIPPING as the row is (joined with " + "), else the id's own name (the
    /// stock maps' own variations, a little less grip or a few points of damage, keep it).
    /// </summary>
    public static string Label(int id, SurfaceRow row)
    {
        string name = SurfaceTypes.All[id & 7].Name;
        var tags = new List<string>(2);
        if (row.Grip < IceBelow) tags.Add("ICE");
        if (row.Damage >= StrippingFrom) tags.Add("STRIPPING");
        else if (row.Damage >= DamagingFrom) tags.Add("DAMAGING");
        return tags.Count > 0 ? string.Join(" + ", tags) : name;
    }

    /// <summary>The label of id <paramref name="id"/> in a table (the id's own name without one).</summary>
    public static string Label(int id, SurfaceTable? table) => table is null ? SurfaceTypes.All[id & 7].Name : Label(id, table[id]);

    /// <summary>"grip 1.9, rolling 0.03, bump 0, damage 0/s".</summary>
    public static string Values(SurfaceRow r) => string.Create(CultureInfo.InvariantCulture,
        $"grip {r.Grip:0.###}, rolling {r.Rolling:0.###}, bump {r.Bump:0.###}, damage {r.Damage}/s");
}
