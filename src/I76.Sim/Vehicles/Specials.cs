using I76.Sim.Damage;
using I76.Sim.Weapons;

namespace I76.Sim.Vehicles;

/// <summary>
/// The keyed specials: the key dispatch, the radar jammer
/// (the radar jammer's activation, the jamming test) and nitro (the nitro's activation). The
/// passive ones are in <see cref="VehicleDamage"/>; the ones that act once, as the car is loaded (the blower, Heated
/// Seats, the Nitro Pack's Dashboard Mary and Tire Covers), in <see cref="AtLoad"/>.
/// </summary>
public static class Specials
{
    /// <summary>One use of the jammer lasts this long on the simulation clock.</summary>
    public const float JamSeconds = 8f;
    /// <summary>One use of nitro, on the shared vehicle timer.</summary>
    public const float NitroSeconds = 15f;

    /// <summary>The special in slot 0..2, or none.</summary>
    public static SpecialPart? Slot(Vehicle v, int i) => v.Health?.Slots[17 + i] as SpecialPart;

    /// <summary>The active-special lookup: the first slot of this type with a charge or switched on.</summary>
    public static SpecialPart? FindActive(Vehicle v, int type) => v.Health?.ActiveSpecial(type);

    /// <summary>On if it has a charge and is off (stamping the time), else off.</summary>
    public static void Toggle(SpecialPart sp, float now)
    {
        if (sp.Charges > 0 && sp.Unk_0x10 == 0) { sp.Unk_0x10 = 1; sp.OnSince = now; }
        else sp.Unk_0x10 = 0;
    }

    /// <summary>
    /// The key for slot <paramref name="slot"/> (the dispatch tests only that the slot has an object). Nitro and the
    /// jammer name their sound, which the caller plays on the car; null for none.
    /// </summary>
    public static string? Activate(Vehicle p, int slot, float now)
    {
        if (Slot(p, slot) is not { } sp) return null;
        switch (sp.Type)
        {
            case 1:
                // the lock warning (clockon.wav) is stopped here too: the view stops it on this click
                if (sp.Charges <= 0 || sp.Hp <= 0) return null;
                if (sp.Unk_0x10 == 0) Toggle(sp, now);   // a press during a jam uses a charge and does not restart the 8 s
                sp.Charges--;
                p.Flags |= VehicleFlags.Jamming;
                return "cjamm.wav";
            case 2:
                if (FindActive(p, 2) is not { } nitro) return null;   // the first active type-2 slot, not necessarily this one
                p.Timer = NitroSeconds;                                // the shared timer: oil slips and the engine start use it too
                p.Flags |= VehicleFlags.TurboDrive | (VehicleFlags)0x80000000;
                nitro.Charges--;
                return "bnitro.wav";
            case 4: p.Unk_0xf4 = 1; return null;                       // for good, no test at all
        }
        return null;
    }

    /// <summary>
    /// The vehicle post-load's walk of the three special slots, in slot order. It looks only at each slot's type, not at
    /// the special's HP or whether it is on, and a type in two slots applies twice. Whatever is applied stays when the
    /// special is later destroyed.
    /// <list type="bullet">
    /// <item>3, the blower: the engine scale 1.25.</item>
    /// <item>8, Heated Seats (every build): each mounted weapon's rounds × 1.1, rounded down (<see cref="PlusTenth"/>).</item>
    /// <item>10, Dashboard Mary (the Nitro Pack only; the base game has the plate and nothing else): each mounted weapon's
    /// damage per shot × 1.1, rounded down. Every round it fires carries it.</item>
    /// <item>11, Tire Covers (the Nitro Pack only): every wheel's HP and maximum × 2 (<paramref name="wheelHp"/>, the wheel
    /// files' HP by axle, which the damage state is built from). The caltrop rule comes on top of it.</item>
    /// </list>
    /// It runs before the player's rescale, and after a trip's carried state has been read (<see cref="Trip.CarState.Apply"/>
    /// gives carried rounds the same bonus). The driver's handguns are fitted later and are left alone.
    /// </summary>
    public static void AtLoad(Vehicle v, IReadOnlyList<int> specials, bool nitro, int[] wheelHp)
    {
        var weapons = v.Armory?.LoadOrder ?? [];
        foreach (int type in specials)
            switch (type)
            {
                case 3: v.P.EngineScale = 1.25f; break;
                case 8: foreach (var w in weapons) w.Ammo = PlusTenth(w.Ammo); break;
                case 10 when nitro: foreach (var w in weapons) w.Damage = PlusTenth(w.Damage); break;
                case 11 when nitro: for (int k = 0; k < wheelHp.Length; k++) wheelHp[k] *= 2; break;
            }
    }

    /// <summary>Heated Seats' bonus on a weapon's rounds, once per Heated Seats slot (a trip's carried rounds get it at load too).</summary>
    public static int HeatedSeatsAmmo(int ammo, IEnumerable<int> specials)
    {
        foreach (int type in specials) if (type == 8) ammo = PlusTenth(ammo);
        return ammo;
    }

    /// <summary>
    /// x × 1.1, rounded toward zero: the single-precision 1.1 widened and multiplied in double precision, as the original
    /// does. So 250 → 275, 40 → 44, 15 → 16, 25 → 27, while 3, 5 and 8 stay as they are.
    /// </summary>
    public static int PlusTenth(int x) => (int)(x * (double)1.1f);

    /// <summary>
    /// The jamming test, with its side effects: the one place a jam ends. <paramref name="trustFlag"/> is a
    /// network client, whose copy of a car has the flag from the server's snapshot and no on-time of its own.
    /// </summary>
    public static bool Jamming(Vehicle? v, float now, bool trustFlag = false)
    {
        if (v is null || (v.Flags & VehicleFlags.Jamming) == 0) return false;
        if (trustFlag) return true;
        if (FindActive(v, 1) is not { } sp) return false;   // the original returns "network game" here (docs/deviations.md)
        if (sp.Unk_0x10 != 0)
        {
            if (!Combat.IsDead(v) && sp.Hp > 0 && now <= sp.OnSince + JamSeconds) return true;
            v.Flags &= ~VehicleFlags.Jamming;
            Toggle(sp, now);
            return false;
        }
        v.Flags &= ~VehicleFlags.Jamming;
        return false;
    }
}
