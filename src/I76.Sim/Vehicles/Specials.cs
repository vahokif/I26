using I76.Sim.Damage;
using I76.Sim.Weapons;

namespace I76.Sim.Vehicles;

/// <summary>
/// The keyed specials: the key dispatch, the radar jammer
/// (the radar jammer's activation, the jamming test) and nitro (the nitro's activation). The
/// passive ones are in <see cref="VehicleDamage"/>, the blower in the vehicle factory.
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
