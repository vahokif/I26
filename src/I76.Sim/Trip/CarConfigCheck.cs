using System.Text.RegularExpressions;
using I76.Formats;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Sim.Trip;

/// <summary>
/// The check a server makes of a car configuration a player sends with a join (a variant CONFIGURE CHASSIS saved,
/// docs/multiplayer.md): the garage's own rules, since the garage is the only way to make one. The body (.vdf) is the
/// model's, every part is one the install has in the slot's kind, the slots are the body's hardpoints, and DONE's
/// checks (<see cref="TripInventory.Validate"/>) pass; armour and chassis are what the garage can make of the model: it moves
/// armour between the zones, the chassis and the unassigned pool but never adds any, so their total is at most that of
/// the model's heaviest stock variant. With the host's Hidden Weapons &amp; Cars on, the Phaedra Rattler's variant-name
/// secrets (the tank, the Huey, the balloon, the UFO) are accepted as the garage makes them (<see cref="HiddenContent.SecretBodies"/>).
/// </summary>
public static class CarConfigCheck
{
    public const int MaxBytes = 2048;
    private const int MaxWeapons = 16;

    /// <summary>
    /// Checks <paramref name="vcf"/> as a variant of the model whose first variant is <paramref name="stem"/>1.vcf.
    /// Returns the car in the form the garage writes it (nothing but what was checked), or null with
    /// <paramref name="error"/> saying what is wrong.
    /// </summary>
    public static byte[]? Check(AssetVfs vfs, PartsDatabase db, string stem, byte[] vcf, out string error, bool allowHidden = false, bool allowTurrets = false)
    {
        error = "";
        if (vcf.Length is 0 or > MaxBytes) { error = $"a car configuration of {vcf.Length} bytes"; return null; }
        TripCar car;
        try
        {
            var f = new Bwd2File(vcf);
            if (f.First("VCFC") is not { } c || c.PayloadSize < 0x7d) { error = "not a car file"; return null; }
            car = TripCar.FromVcf(vcf);
        }
        catch (Exception) { error = "not a car file"; return null; }

        if (vfs.TryRead(stem + "1.vcf") is not { } baseVcf || new Bwd2File(baseVcf).VcfcFields() is not { } stock) { error = "the model is not in this install"; return null; }
        var secret = HiddenContent.SecretBodyOf(vfs, stem, car.Vdf);
        if (secret is not null && !allowHidden) { error = $"a {car.Variant} body ({car.Vdf}) is a hidden car and {HiddenContent.Off}"; return null; }
        if (secret is null && !car.Vdf.Equals(stock.Vdf, StringComparison.OrdinalIgnoreCase)) { error = $"the body {car.Vdf} is not the {stem} model's"; return null; }
        if (!Regex.IsMatch(car.Vtf, @"^[A-Za-z0-9_]{1,8}\.vtf$") || vfs.TryRead(car.Vtf) is null) { error = $"no paint scheme {car.Vtf}"; return null; }
        if (car.Variant.Length == 0 || car.Variant.Any(char.IsControl)) { error = "a nameless variant"; return null; }

        // armour and chassis: the garage conserves their total (Defense: +-10 between a zone and the pool), so no more than the
        // heaviest stock variant carries, or than the secret body is given; none negative
        int Total(TripCar c) => c.Armour.Sum() + c.Chassis.Sum() + c.Ninth;
        int allowed = 0;
        for (int n = 1; n < 10 && vfs.TryRead($"{stem}{n}.vcf") is { } sd; n++) allowed = System.Math.Max(allowed, Total(TripCar.FromVcf(sd)));
        if (secret is { } body) allowed += body.Total;      // the secret replaces the zones it names, keeping the rest of the Rattler's
        if (car.Armour.Any(a => a < 0) || car.Chassis.Any(c => c < 0) || car.Ninth < 0 || Total(car) > allowed) { error = "armour beyond the model's"; return null; }

        // parts: components by ordinal, tyres and guns by file, each one the install has
        var engOrdinal = I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, car.Engine);
        car.Engine = engOrdinal;      // FromCar and ToVcf work in ordinals; the engine field of the file is an ENG NUM
        if (!db.Parts.Any(p => p.Type == 2 && !p.IsEmpty && p.Ordinal == car.Engine)
            || !db.Parts.Any(p => p.Type == 3 && !p.IsEmpty && p.Ordinal == car.Suspension)
            || !db.Parts.Any(p => p.Type == 4 && !p.IsEmpty && p.Ordinal == car.Brakes)) { error = "an engine, suspension or brakes this install does not have"; return null; }
        foreach (var w in car.Wdf)
            if (!w.Equals("null", StringComparison.OrdinalIgnoreCase) && w.Length > 0 && !(secret?.Wdf.Contains(w, StringComparer.OrdinalIgnoreCase) ?? false) && !db.Parts.Any(p => p.Type == 5 && !p.IsEmpty && p.Stem.Equals(w, StringComparison.OrdinalIgnoreCase))) { error = $"no tyre {w}"; return null; }

        var vdfData = vfs.TryRead(car.Vdf);
        var hps = vdfData is null ? [] : Hardpoint.Read(new Bwd2File(vdfData));
        if (car.Weapons.Count > MaxWeapons) { error = "too many weapons"; return null; }
        var used = new HashSet<int>();
        foreach (var (hp, gdf) in car.Weapons)
        {
            var h = hps.FirstOrDefault(x => x.Id == hp);
            if (h is null || !used.Add(hp)) { error = $"no free hardpoint {hp} on the {stem} body"; return null; }
            if (gdf.Length == 0 || gdf.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            int type = h.Style == 4 ? 8 : 7;
            if (db.ByStem(gdf) is not { } part || part.Type != type) { error = $"{gdf} does not fit hardpoint {hp}"; return null; }
        }
        foreach (var sp in car.Specials)
            if (sp < 0 || sp > PartsDatabase.SpecialCount(vfs.Install.IsNitro)) { error = "an unknown special"; return null; }

        // a secret body's tracks are no garage part: the garage checked the Rattler it was made from
        var inv = TripInventory.FromCar(car, db, vfs);
        if (secret is null && inv.Validate(allowTurrets) is { } box) { error = $"the car fails the garage's check {box}"; return null; }
        // the ENG NUM goes back into the file as it came
        car.Engine = TripCar.FromVcf(vcf).Engine;
        return car.ToVcf();
    }

    /// <summary>
    /// The strict mode's checks of a network car that the garage's own do not cover (the Nitro Pack's car check,
    /// docs/hidden-content.md §4): the same special twice (bits 0x80..0x200), a second
    /// turret (0x4000), a dropper off a dropper mount, a gun on one or a turret off a turret mount (0x8000). The tank cannons
    /// and the howitzer (0x2000) are <see cref="HiddenContent.Check"/>'s: the howitzer never, the cannons only with the host's
    /// Hidden Weapons &amp; Cars, which also lets a hidden car (<see cref="HiddenContent.CompleteVehicles"/>, a secret body) keep its own mounts.
    /// Null when the car passes, else why not. Every stock melee car passes.
    /// </summary>
    public static string? Strict(AssetVfs vfs, TripCar car, string? stem, bool allowTurrets = false)
    {
        var specials = car.Specials.Where(s => s != 0).ToList();
        if (specials.Count != specials.Distinct().Count()) return "the same special twice";
        // a hidden car (the helicopters' two turrets, a secret body) keeps its own mounts: it plays only with Hidden Weapons & Cars on
        if (stem is not null && (HiddenContent.SecretBodyOf(vfs, stem, car.Vdf) is not null || HiddenContent.CompleteVehicles.Contains(stem, StringComparer.OrdinalIgnoreCase))) return null;
        List<Hardpoint> hps;
        try { hps = vfs.TryRead(car.Vdf) is { } vd ? Hardpoint.Read(new Bwd2File(vd)) : []; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { hps = []; }
        int turrets = 0;
        foreach (var (hp, gdf) in car.Weapons)
        {
            if (WeaponRestrictions.KeyOf(vfs, gdf) is not { } key) continue;
            // a turret's tier is its gun's + 100; class 6 the droppers
            bool turret = key.Tier >= 100, dropper = key.Class == 6;
            string name = HiddenContent.WeaponName(vfs, gdf);
            if (turret && ++turrets > 1 && !allowTurrets) return $"a second turret ({name})";
            if (hps.FirstOrDefault(h => h.Id == hp) is not { } mount) continue;
            if (dropper && mount.Style != DropperMount) return $"{name} off a dropper mount";
            if (!dropper && mount.Style == DropperMount) return $"{name} on a dropper mount";
            if (turret && mount.Style != TurretMount) return $"{name} off a turret mount";
        }
        return null;
    }

    /// <summary>
    /// The car with every turret after the first taken off, for a host that does not allow several (a bot's car, as the restricted
    /// weapons are taken off it); null when it has at most one or is a hidden car, whose own mounts <see cref=Strict/> lets be.
    /// </summary>
    public static TripCar? WithoutExtraTurrets(AssetVfs vfs, TripCar car, string? stem)
    {
        if (stem is not null && (HiddenContent.SecretBodyOf(vfs, stem, car.Vdf) is not null || HiddenContent.CompleteVehicles.Contains(stem, StringComparer.OrdinalIgnoreCase))) return null;
        bool Turret(string gdf) => WeaponRestrictions.KeyOf(vfs, gdf) is { Tier: >= 100 };
        var turrets = car.Weapons.Where(w => Turret(w.Gdf)).ToList();
        if (turrets.Count < 2) return null;
        var drop = turrets.Skip(1).ToList();
        car.Weapons = [.. car.Weapons.Where(w => !drop.Contains(w))];
        return car;
    }

    /// <summary>The hardpoint styles of a roof turret mount and a dropper mount (the garage's slot kinds).</summary>
    public const uint TurretMount = 1, DropperMount = 4;
}
