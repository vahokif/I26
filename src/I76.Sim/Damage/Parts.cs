using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Damage;

/// <summary>
/// A damageable component in one of a vehicle's 20 slots.
/// Apply walks the packet's set
/// channels in order and returns true when the component overflowed (the
/// caller re-picks with the remainder).
/// </summary>
public abstract class Part
{
    public abstract int Hp { get; set; }
    public abstract int Max { get; set; }
    /// <summary>Flung off as debris by the death sequence.</summary>
    public bool Detached;
    public abstract bool Apply(DamagePacket p);
    public float Ratio => Max == 0 ? 0 : (float)Hp / Max;
    protected static bool Set(DamagePacket p, int i) => (p.Mask & (1 << i)) != 0;
}

/// <summary>Wheel (0x1e): grip (nominal 1.0, never loaded) and rolling follow its health.</summary>
public sealed class WheelPart(Wheel w, int hp) : Part
{
    public Wheel Wheel { get; } = w;
    public override int Hp { get; set; } = hp;
    public override int Max { get; set; } = hp;
    public float GripNominal = 1f;
    public float RollingNominal = 1f;

    public override bool Apply(DamagePacket p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if (Hp < p.Ch[i]) { p.Ch[i] -= Hp; Hp = 0; Wheel.Grip = 0.5f; Wheel.Rolling = 2f; return true; }
            Hp -= p.Ch[i]; p.Ch[i] = 0;
        }
        Wheel.Grip = MathF.Max((float)Hp / Max * GripNominal, 0.5f);
        Wheel.Rolling = (2f - RollingNominal) * ((float)(Max - Hp) / Max) + RollingNominal;
        return false;
    }

    /// <summary>Once, the tyre blows: rest height × 0.688, tblow.wav.</summary>
    public bool BlowOut()
    {
        if (Wheel.Blown) return false;
        Wheel.Blown = true;
        Wheel.Rest *= 0.688f;
        return true;
    }
}

/// <summary>Chassis (0x14): plain hit points, 400.</summary>
public sealed class ChassisPart : Part
{
    public override int Hp { get; set; } = 400;
    public override int Max { get; set; } = 400;
    public override bool Apply(DamagePacket p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if (Hp < p.Ch[i]) { p.Ch[i] -= Hp; Hp = 0; return true; }
            Hp -= p.Ch[i]; p.Ch[i] = 0;
        }
        return false;
    }
}

/// <summary>Engine (0x15): the power factor = max(HP/max, f), f 0.275 for AI cars, 0.4 for the player.</summary>
public sealed class EnginePart(Engine e, int hp, float floor) : Part
{
    public override int Hp { get; set; } = hp;
    public override int Max { get; set; } = hp;
    public override bool Apply(DamagePacket p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if (Hp < p.Ch[i]) { p.Ch[i] -= Hp; Hp = 0; e.Damage = floor; return true; }
            Hp -= p.Ch[i]; p.Ch[i] = 0;
        }
        float r = (float)Hp / Max;
        e.Damage = r < floor ? floor : r;
        return false;
    }
}

/// <summary>Brake (0x16): the brake strength = max(HP/max · nominal, 0.2 · nominal).</summary>
public sealed class BrakePart(VehicleParams P, int hp) : Part
{
    public override int Hp { get; set; } = hp;
    public override int Max { get; set; } = hp;
    public float Nominal = P.BrakeStrength;
    public override bool Apply(DamagePacket p)
    {
        float floor = Nominal * 0.2f;
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if (Hp < p.Ch[i]) { p.Ch[i] -= Hp; P.BrakeStrength = floor; Hp = 0; return true; }
            Hp -= p.Ch[i]; p.Ch[i] = 0;
        }
        float v = (float)Hp / Max * Nominal;
        P.BrakeStrength = v < floor ? floor : v;
        return false;
    }
}

/// <summary>
/// Suspension (0x17): the cornering factor falls toward
/// 0.5. An impact packet is compared scaled by the impact scale but subtracted raw.
/// </summary>
public sealed class SuspensionPart(VehicleParams P, int hp, float impactScale) : Part
{
    public override int Hp { get; set; } = hp;
    public override int Max { get; set; } = hp;
    public float Nominal = P.ChassisGrip;
    public float ImpactScale = impactScale;
    public float Unk_0x0c = 1f;
    public float Unk_0x10 = 1f;
    public override bool Apply(DamagePacket p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            int raw = p.Ch[i];
            int eff = (p.Mask & 8) != 0 ? (int)(raw * ImpactScale - -0.5f) : raw;
            if (Hp < eff) { p.Ch[i] = eff - Hp; Hp = 0; Unk_0x0c = 5f; P.ChassisGrip = 0.5f; return true; }
            Hp -= raw; p.Ch[i] = 0;
        }
        float r = (float)Hp / Max;
        P.ChassisGrip = (Nominal - 0.5f) * r + 0.5f;
        Unk_0x0c = 5f - (5f - Unk_0x10) * r;
        return false;
    }
}

/// <summary>A special (0x18): 10 HP; absorbs without consuming the packet.</summary>
public sealed class SpecialPart(int type) : Part
{
    public int Type { get; } = type;
    public override int Hp { get; set; } = 10;
    public override int Max { get; set; } = 10;
    public int Charges = type == 2 ? 3 : type == 1 ? 5 : 1;
    public int Unk_0x10;
    /// <summary>When the jammer went on: the simulation clock, <see cref="SimWorld.Time"/>.</summary>
    public float OnSince;
    public bool Active => Charges >= 1 || Unk_0x10 != 0;
    public override bool Apply(DamagePacket p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if (Hp < p.Ch[i]) { Hp = 0; Charges = 0; Unk_0x10 = 0; return true; }
            Hp -= p.Ch[i];
        }
        return false;
    }
}

/// <summary>A weapon (0x32): the nominal absorbs each channel; an overflow does not stop the loop.</summary>
public sealed class WeaponPart(Weapon w) : Part
{
    public Weapon Weapon { get; } = w;
    public override int Hp { get => Weapon.Hp; set => Weapon.Hp = value; }
    public override int Max { get => (int)Weapon.Def.HitPoints; set { } }
    public override bool Apply(DamagePacket p)
    {
        bool ret = false;
        for (int i = 0; i < 4; i++)
        {
            if (!Set(p, i)) continue;
            if ((uint)Weapon.Hp < (uint)p.Ch[i]) { p.Ch[i] -= Weapon.Hp; Weapon.Hp = 0; ret = true; }
            else { Weapon.Hp -= p.Ch[i]; p.Ch[i] = 0; }
        }
        return ret;
    }
}
