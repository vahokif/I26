using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Shell;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The weapons and vehicles the game data holds and the shells never offer (docs/hidden-content.md), and the port's
/// Hidden Weapons &amp; Cars setting that brings back the complete ones: in CONFIGURE CHASSIS's pickers, on MAKE / MODEL, and
/// in a network game when the host allows them.
/// </summary>
public static class HiddenContent
{
    /// <summary>
    /// The melee pickers' bans, file-name prefixes compared on the part's stem (in both front ends):
    /// always out, and out unless the variant's name begins "natk".
    /// </summary>
    private static readonly string[] BaseAlways = ["gh45", "gtktank", "gtptank", "tthowitz"];
    private static readonly string[] BaseNatk = ["gchades", "tchades", "gscherub", "tscherub", "ggchem", "gs4get", "gsball", "tsball", "tm125mg", "tm125lmg", "gfpyro", "tfpyro"];
    private static readonly string[] NitroNatk = ["gchades", "tchades", "gscherub", "tscherub", "tm125mg", "tm125lmg", "gfpyro", "tfpyro"];
    private static readonly string[] BaseDroppers = ["gcal", "gsmo", "gceracer"];
    private static readonly string[] NitroDroppers = ["gceracer"];

    /// <summary>
    /// The hidden weapons that are complete (model, sound, projectile, cockpit plate). The tank cannons', the Howitzer's and the
    /// gunner MGs' plate reads EMPTY; the cannons are the tank's own gun, and the Nitro emplacements' three (the 12.5mm MG and
    /// Light m/g "with gunner", a figure standing at a gun on a pole, and the Howitzer) were the all-weapon shell's favourites.
    /// </summary>
    public static readonly string[] CompleteWeapons = ["gchades", "tchades", "gscherub", "tscherub", "gfpyro", "tfpyro", "gceracer", "gtktank", "gtptank", "tm125mg", "tm125lmg", "tthowitz"];

    /// <summary>
    /// The hidden vehicles that are complete: a model the melee list does not offer, that builds, drives or flies. Those an
    /// install's list does offer (Nitro's tanker and Skeeter's van) or does not have (the balloon, UFO and Phantom on the base
    /// game) drop out by themselves (<see cref="HiddenModels"/>).
    /// </summary>
    public static readonly string[] CompleteVehicles = ["vstank", "vuchuey", "vugcobr", "vxktrac", "vxmilk", "vsvan", "vubalon", "vxufo", "vuphant", "vdillo"];

    /// <summary>
    /// A body the Phaedra Rattler's variant-name secrets make (GarageScreens.Secrets): its .vdf, the tracks it may carry and the
    /// armour and chassis total the garage gives it (the Rattler's own stock total at most for the rest).
    /// </summary>
    public sealed record SecretBody(string Vdf, string[] Wdf, int Total);

    /// <summary>The secrets: the tank (<c>natk</c> 8 x 6000 of armour and chassis, <c>knat</c> 8 x 1500), the Huey, the balloon and the UFO (front 3000 each).</summary>
    public static readonly SecretBody[] SecretBodies =
    [
        new("vstank.vdf", ["wctnk_1a.wdf", "wdtnk_1a.wdf"], 8 * 6000),
        new("vuhuey.vdf", [], 6000), new("vuchuey.vdf", [], 6000), new("vuballon.vdf", [], 6000), new("vxufo.vdf", [], 6000),
    ];

    /// <summary>The secret body a Rattler variant (<paramref name="stem"/>'s model) can have, by its .vdf; the install must have it.</summary>
    public static SecretBody? SecretBodyOf(AssetVfs vfs, string stem, string vdf)
    {
        if (!ModelName(vfs, stem).Equals("phaedra rattler", StringComparison.OrdinalIgnoreCase)) return null;
        return SecretBodies.FirstOrDefault(b => b.Vdf.Equals(vdf, StringComparison.OrdinalIgnoreCase) && vfs.TryRead(b.Vdf) is not null);
    }

    private static bool Prefixed(string stem, IEnumerable<string> prefixes) => prefixes.Any(b => stem.StartsWith(b, StringComparison.OrdinalIgnoreCase));

    private static string Stem(string gdf) => gdf.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase) ? gdf[..^4] : gdf;

    /// <summary>Whether the stock pickers leave this weapon out (a gun or a dropper; <paramref name="natk"/> opens the second half of the list).</summary>
    public static bool ShellHides(string gdf, bool nitro, bool natk = false)
    {
        var s = Stem(gdf);
        if (Prefixed(s, nitro ? NitroDroppers : BaseDroppers)) return true;
        return Prefixed(s, BaseAlways) || (!natk && Prefixed(s, nitro ? NitroNatk : BaseNatk));
    }

    /// <summary>A hidden weapon that comes back with the setting.</summary>
    public static bool IsComplete(string gdf) => CompleteWeapons.Contains(Stem(gdf), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the pickers list a weapon: the stock rule, and with <paramref name="hidden"/> the complete hidden ones too.</summary>
    public static bool Offered(string gdf, bool nitro, bool natk, bool hidden) => !ShellHides(gdf, nitro, natk) || (hidden && IsComplete(gdf));

    /// <summary>The complete hidden models this install has and its list does not offer, in <see cref="CompleteVehicles"/>' order.</summary>
    public static List<string> HiddenModels(AssetVfs vfs, ISet<string> offered) =>
        [.. CompleteVehicles.Where(s => !offered.Contains(s) && vfs.TryRead(s + "1.vcf") is not null)];

    /// <summary>
    /// How many stock variants a hidden model has: the <c>&lt;stem&gt;N.vcf</c> files the install's archive ships, counted up to the
    /// first gap. Saved variants (the player's overlay, the install's addon\) are not stock, however they are served.
    /// </summary>
    public static int StockVariants(AssetVfs vfs, string stem)
    {
        int n = 0;
        while (n < 9 && vfs.InArchive($"{stem}{n + 1}.vcf")) n++;
        return n;
    }

    /// <summary>A .gdf's display name (GDFC +0), or the file's stem.</summary>
    public static string WeaponName(AssetVfs vfs, string gdf)
    {
        var f = gdf.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase) ? gdf : gdf + ".gdf";
        try
        {
            if (vfs.TryRead(f) is { } d && new Bwd2File(d).First("GDFC") is { } c) return Bin.CStr(d, c.PayloadOffset, 16);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        return Stem(gdf);
    }

    /// <summary>
    /// Why a hidden car or weapon is refused, and what to change: the host's Hidden Weapons &amp; Cars is taken when the game
    /// starts (the host form's row, the same setting as Play Options'), so turning it on later does not change a running game.
    /// </summary>
    public const string Off = "this game has Hidden Weapons & Cars off (the host's setting when it started the game): "
        + "pick a stock car, or the host turns Hidden Wpns & Cars on in the host form and hosts again";

    /// <summary>
    /// A network game's rule for a joiner's car (the server's, docs/hidden-content.md §5): its model must be one the melee list
    /// offers, or with <paramref name="allow"/> a complete hidden one; its weapons ones the pickers offer, or with
    /// <paramref name="allow"/> complete hidden ones. A weapon file this install lacks is not built, so it does not count.
    /// Null when the car passes, else why not.
    /// </summary>
    public static string? Check(AssetVfs vfs, ISet<string> offered, bool nitro, bool allow, string? stem, IEnumerable<string> weapons)
    {
        const string off = Off;
        if (stem is null || !offered.Contains(stem))
        {
            bool hiddenModel = stem is not null && CompleteVehicles.Contains(stem, StringComparer.OrdinalIgnoreCase) && vfs.TryRead(stem + "1.vcf") is not null;
            if (!hiddenModel) return stem is not null && CompleteVehicles.Contains(stem, StringComparer.OrdinalIgnoreCase)
                ? $"the host's install has no {stem}: pick another car"
                : $"{stem ?? "that car"} is not a melee car";
            if (!allow) return $"the {ModelName(vfs, stem!)} is a hidden car and {off}";
        }
        // a weapon file this install lacks is not built (the base game's milk truck names Nitro's gsball.gdf)
        var hidden = weapons.Where(w => w.Length > 0 && !w.Equals("null", StringComparison.OrdinalIgnoreCase) && ShellHides(w, nitro)
                                        && WeaponRestrictions.KeyOf(vfs, w) is not null).ToList();
        var never = hidden.Where(w => !IsComplete(w)).Select(w => WeaponName(vfs, w)).Distinct().ToList();
        if (never.Count > 0) return $"weapons no melee car may carry: {string.Join(", ", never)}";
        if (!allow && hidden.Count > 0) return $"hidden weapons ({string.Join(", ", hidden.Select(w => WeaponName(vfs, w)).Distinct())}) and {off}";
        return null;
    }

    /// <summary>A model's MAKE / MODEL (its first variant's .vdf name), or the stem.</summary>
    public static string ModelName(AssetVfs vfs, string stem)
    {
        try
        {
            if (vfs.TryRead(stem + "1.vcf") is { } d && new Bwd2File(d).VcfcFields() is { } f && vfs.TryRead(f.Vdf) is { } vd
                && new Bwd2File(vd).First("VDFC") is { } c)
                return Bin.CStr(vd, c.PayloadOffset, 16).Trim() is { Length: > 0 } n ? n : stem;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        return stem;
    }

    /// <summary>
    /// The checklist rows the port adds for the complete hidden weapons, in the art's empty lines (docs/hidden-content.md §5):
    /// Cherub and Pyro-Tomic under missiles and flamethrowers (STD, TRT), HADES, the Tank Cannon, the Car-E-Racer and the
    /// Howitzer (TRT) at the foot of the right half, which then is full: the gunner MGs have no line of their own (All Slug
    /// Throwers and All Weapons cover them). Named from the install's .gdf files; those it lacks are left out.
    /// </summary>
    public static List<RestrictionRow> ChecklistRows(AssetVfs vfs)
    {
        var rows = new List<RestrictionRow>();
        void Add(string gdf, int group, int column, int row)
        {
            if (WeaponRestrictions.KeyOf(vfs, gdf) is not { } k) return;
            rows.Add(new RestrictionRow(group, k.Class, k.Tier, column, row, WeaponName(vfs, gdf)));
        }
        Add("gscherub", 3, 1, 5); Add("tscherub", 3, 2, 5);
        Add("gfpyro", 4, 1, 11); Add("tfpyro", 4, 2, 11);
        Add("gchades", 1, 4, 13); Add("tchades", 1, 5, 13);
        Add("gtktank", 1, 4, 14);
        Add("gceracer", 5, 4, 15);
        Add("tthowitz", 2, 5, 16);
        return rows;
    }
}
