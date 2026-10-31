using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The port's debug cars (the F9 mission list; not in the original): a stock car rebuilt from the parts catalogue
/// with the best of everything, so a tester who jumps ahead in the campaign is not stuck in the first Piranha.
/// Built from the install's own data: the catalogue's engines, suspensions, brakes and tyres, and the weapons'
/// .gdf numbers, on the hardpoints of the base car's body.
/// </summary>
public static class DebugCar
{
    /// <summary>Each armour and chassis zone's value (the stock tank has 2000, the T17 Piranha 600; the garage shows tenths).</summary>
    public const int ArmourPerZone = 1500;

    // the garage's own catalogue bans (GarageScreens.Catalogue): no tank or turret ordnance, no armour-piercing extras
    private static readonly string[] BannedWeapons = ["gh45", "gtktank", "gtptank", "tthowitz", "gchades", "tchades", "gscherub", "tscherub", "ggchem", "gs4get", "gsball", "tsball", "tm125mg", "tm125lmg", "gfpyro", "tfpyro"];
    private static readonly string[] BannedDroppers = ["gcal", "gsmo", "gceracer"];

    /// <summary>
    /// <paramref name="basis"/> (a stock .vcf, e.g. vppirna1.vcf) with the strongest engine, suspension, brakes and tyres, every
    /// weapon mount filled with the heaviest gun / missile (a dropper mount with the hardest mine), the T17 car's specials and
    /// <see cref="ArmourPerZone"/> in all eight zones. Turrets are left out (one per car, roof only), so any body works.
    /// </summary>
    public static TripCar Build(TripCar basis, PartsDatabase db, AssetVfs vfs)
    {
        var car = new TripCar
        {
            Variant = "OP " + basis.Variant, Vdf = basis.Vdf, Vtf = basis.Vtf, Ninth = 0,
            Armour = [.. Enumerable.Repeat(ArmourPerZone, 4)], Chassis = [.. Enumerable.Repeat(ArmourPerZone, 4)],
            Specials = [5, 2, 3],           // Structo Bmpr, NitrousOxide, Blower, as the T17 car
        };
        int Best(int type) => db.Parts.Where(p => p.Type == type && !p.IsEmpty).OrderBy(p => p.MaxHp).ThenBy(p => p.Ordinal).Select(p => p.Ordinal).LastOrDefault();
        car.Engine = Best(2); car.Suspension = Best(3); car.Brakes = Best(4);
        // the tyres: the sturdiest of the basis's own size ("wauto_1" + grade), the same for both axles so the pairs match
        string Tyre(string stem) => stem.Length < 7 || stem.Equals("null", StringComparison.OrdinalIgnoreCase) ? stem
            : db.Parts.Where(p => p.Type == 5 && !p.IsEmpty && p.Stem.StartsWith(stem[..7], StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.MaxHp).ThenBy(p => p.Stem, StringComparer.OrdinalIgnoreCase).Select(p => p.Stem).LastOrDefault() ?? stem;
        car.Wdf = [.. basis.Wdf.Select(Tyre)];

        var gizmos = db.Parts.Where(p => p.Type is 7 or 8 && !p.IsEmpty && !p.IsTurret)
            .Select(p => (p, g: GizmoDef.Read(p.Stem, vfs.TryRead(p.Stem) ?? [])))
            .Where(x => x.g is not null).Select(x => (x.p, g: x.g!)).ToList();
        string? Pick(Func<GizmoDef, bool> cls, Func<GizmoDef, double> score, string[] banned) =>
            gizmos.Where(x => cls(x.g) && !banned.Any(b => x.p.Stem.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => score(x.g)).Select(x => x.p.Stem).LastOrDefault();
        string? cannon = Pick(g => g.Class == 2, g => g.Damage, BannedWeapons);                       // heaviest shot: gcheavy
        string? gun = Pick(g => g.Class == 2, g => g.Damage * g.Rate, BannedWeapons);                  // most damage a second: gmheavy
        string? missile = Pick(g => g.Class == 4, g => g.Damage, BannedWeapons);                       // gsradar
        string? mine = Pick(g => g.Class == 6, g => g.Damage, BannedDroppers);                         // glandmin
        var hps = Hardpoint.Read(new Bwd2File(vfs.TryRead(basis.Vdf) ?? []));
        // the front mounts take the cannon, then the missile, then the cannon again...; the rear ones the machine gun
        string?[] front = [cannon, missile, cannon, gun];
        int f = 0;
        foreach (var (hp, _) in basis.Weapons)
        {
            var h = hps.FirstOrDefault(x => x.Id == hp);
            string? pick = h is null ? null : h.Style == 4 ? mine : h.Facing == 2 ? gun : front[f++ % front.Length];
            car.Weapons.Add((hp, pick ?? "null"));
        }
        return car;
    }
}
