using System.Numerics;
using I76.Sim.Rng;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Damage;

/// <summary>
/// A vehicle's damage state: the armour and
/// chassis-reinforcement pools per zone, the 20 component slots, and the
/// pick table rows the component loop draws from.
/// </summary>
public sealed class VehicleDamage
{
    public Vehicle V { get; }
    public bool IsPlayer { get; }
    // zones 0 front, 1 left (x < 0), 2 right (x > 0), 3 rear; [4] of the mirrors is the top
    public int[] Armour = new int[4];
    public int[] Chassis = new int[4];
    public int[] ArmourMax = new int[4];
    public int[] ChassisMax = new int[4];
    public int[] ArmourMirror = new int[5];
    public int[] ChassisMirror = new int[5];
    /// <summary>The slots: wheels FR, FL, mid R, mid L, RR, RL; chassis; engine; suspension; brake; 7 weapons; 3 specials.</summary>
    public Part?[] Slots { get; } = new Part?[20];
    /// <summary>Rows by sub-zone (0 front, 1 front-left, 2 rear-left, 3 front-right, 4 rear-right, 5 rear, 6 top).</summary>
    public List<int>[] Rows { get; } = Enumerable.Range(0, 7).Select(_ => new List<int>()).ToArray();
    /// <summary>The rough-terrain damage's fractional carry.</summary>
    public float RoughCarry;

    /// <summary>A network melee's rules: the player's single-player advantages are off: no rescale, the kill roll's 2.5.</summary>
    public bool Network { get; }

    public VehicleDamage(Vehicle v, bool isPlayer, bool network = false) { V = v; IsPlayer = isPlayer; Network = network; }

    public EnginePart? Engine => Slots[7] as EnginePart;
    public SuspensionPart? Suspension => Slots[8] as SuspensionPart;
    public BrakePart? Brake => Slots[9] as BrakePart;
    public IEnumerable<WheelPart> Wheels => Slots.Take(6).OfType<WheelPart>();

    private void Add(int idx, int mask)
    {
        for (int r = 0; r < 7; r++) if ((mask & (1 << r)) != 0) Rows[r].Add(idx);
    }

    /// <summary>The mask switch: which rows a weapon sits in.</summary>
    public static int WeaponMask(Weapon w)
    {
        float x = w.Hardpoint.Transform.Translation[0];
        if (w.Style == 3) return 0x7f;
        if (w.Style == 1) return x < 0 ? 0x67 : x > 0 ? 0x79 : 0x7f;
        return w.Hardpoint.Facing switch
        {
            1 => x < 0 ? 0x03 : x > 0 ? 0x09 : 0x01,
            2 => w.Style == 4 ? 0x20 : x < 0 ? 0x24 : x > 0 ? 0x30 : 0x20,
            3 => 0x10,
            4 => 0x04,
            _ => 0,
        };
    }

    /// <summary>
    /// The vehicle post-load's damage part: the pick table in
    /// its exact order (one rand() per present special), then the player's
    /// rescale (armour ×2, components ×4).
    /// </summary>
    public static VehicleDamage Create(Vehicle v, VehicleBuild b, bool isPlayer, CrtRand rand, bool network = false)
    {
        var d = new VehicleDamage(v, isPlayer, network);
        for (int z = 0; z < 4; z++)
        {
            d.Armour[z] = d.ArmourMax[z] = d.ArmourMirror[z] = b.Armour[z];
            d.Chassis[z] = d.ChassisMax[z] = d.ChassisMirror[z] = b.Chassis[z];
        }
        d.ArmourMirror[4] = d.ChassisMirror[4] = 100;
        d.Slots[0] = new WheelPart(v.FrontRight, b.WheelHp[0]);
        d.Slots[1] = new WheelPart(v.FrontLeft, b.WheelHp[0]);
        if (v.MiddleRight is { } mr && v.MiddleLeft is { } ml)
        {
            d.Slots[2] = new WheelPart(mr, b.WheelHp[1]);
            d.Slots[3] = new WheelPart(ml, b.WheelHp[1]);
        }
        d.Slots[4] = new WheelPart(v.RearRight, b.WheelHp[2]);
        d.Slots[5] = new WheelPart(v.RearLeft, b.WheelHp[2]);
        d.Slots[6] = new ChassisPart();
        d.Slots[7] = new EnginePart(v.Engine, b.EngineHp, isPlayer || network ? 0.4f : 0.275f);
        d.Slots[8] = new SuspensionPart(v.P, b.SuspensionHp, b.SuspensionImpactScale);
        d.Slots[9] = new BrakePart(v.P, b.BrakeHp);
        var weapons = v.Armory?.LoadOrder ?? [];
        for (int i = 0; i < weapons.Count && i < 7; i++) d.Slots[10 + i] = new WeaponPart(weapons[i]);
        // each SPEC takes the next slot; type 0 takes one but makes nothing
        for (int i = 0; i < b.Specials.Count && i < 3; i++) if (b.Specials[i] != 0) d.Slots[17 + i] = new SpecialPart(b.Specials[i]);

        d.Add(0, 0x09); d.Add(1, 0x03); d.Add(4, 0x30); d.Add(5, 0x24);
        if (d.Slots[3] is not null) { d.Add(2, 0x10); d.Add(3, 0x04); }   // the middle pair joins rows 4 and 2
        d.Add(6, 0x40); d.Add(7, 0x0b); d.Add(8, 0x3f); d.Add(9, 0x3f);
        for (int i = 10; i <= 15; i++) if (d.Slots[i] is WeaponPart wp) d.Add(i, WeaponMask(wp.Weapon));
        for (int i = 17; i <= 18; i++) if (d.Slots[i] is not null) d.Add(i, 1 << (rand.Next() % 7));
        d.Add(-1, 0x7f); d.Add(-1, 0x40); d.Add(-1, 0x40);

        if (isPlayer && !network)   // isPlayer(obj) && not a network game
        {
            for (int z = 0; z < 4; z++) { d.ArmourMax[z] *= 2; d.Armour[z] *= 2; d.ChassisMax[z] *= 2; d.Chassis[z] *= 2; }
            for (int k = 0; k < 5; k++) { d.ArmourMirror[k] *= 2; d.ChassisMirror[k] *= 2; }
            foreach (var p in d.Slots)
                if (p is not null and not WeaponPart) { p.Hp *= 4; p.Max *= 4; }
        }
        return d;
    }

    /// <summary>
    /// A helicopter player's part of the damage state: the special parts (the helicopter's post-load runs the cockpit's panels, and the
    /// jammers are keyed for any type) and the unrescaled armour for the plate, with none of the car's post-load's rand() draws.
    /// </summary>
    public static VehicleDamage CreateSpecialsOnly(Vehicle v, VehicleBuild b)
    {
        var d = new VehicleDamage(v, true);
        for (int z = 0; z < 4; z++)
        {
            d.Armour[z] = d.ArmourMax[z] = d.ArmourMirror[z] = b.Armour[z];
            d.Chassis[z] = d.ChassisMax[z] = d.ChassisMirror[z] = b.Chassis[z];
        }
        d.ArmourMirror[4] = d.ChassisMirror[4] = 100;
        for (int i = 0; i < b.Specials.Count && i < 3; i++) if (b.Specials[i] != 0) d.Slots[17 + i] = new SpecialPart(b.Specials[i]);
        return d;
    }

    // ---- reading ----

    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <summary>
    /// The health percentage: a percentage 28–100 from the
    /// weakest armour/chassis zone while engine, suspension and brake are
    /// whole; otherwise the weakest of those three as a 0–1 ratio (a quirk
    /// kept); 100 when all three are below 2 %.
    /// </summary>
    public float HealthPercent()
    {
        float[] r = [Engine?.Ratio ?? 1, Suspension?.Ratio ?? 1, Brake?.Ratio ?? 1];
        float m = float.MaxValue; int n = 0;
        foreach (var x in r)
            if (x < 0.02f) n++;
            else m = MathF.Min(m, Clamp01(x));
        float v;
        if (m < 0.9999f || n > 0) v = m;
        else
        {
            float a = float.MaxValue, c = float.MaxValue;
            for (int z = 0; z < 4; z++)
            {
                if (ArmourMax[z] > 0) a = MathF.Min(a, Clamp01((float)Armour[z] / ArmourMax[z]));
                if (ChassisMax[z] > 0) c = MathF.Min(c, Clamp01((float)Chassis[z] / ChassisMax[z]));
            }
            v = (a < c ? a : c) * 72f - -28f;
        }
        return v < 0 ? 0 : v > 100 ? 100 : v;
    }

    /// <summary>
    /// What the target box's health bar shows, as a 0–1 fraction: the health percentage (the original reads it,
    /// clamped 0.1–100, and colours it by thirds: the same value the .45's "&gt; 33" rule and the scripts' hpLesser read).
    /// So a car whose engine, suspension or brake is scratched (a flamer goes straight to those) is in the red, and the
    /// .45 can finish it.
    /// </summary>
    public float GaugeFraction() => HealthPercent() / 100f;

    /// <summary>
    /// The damage smoke's stage for a gauge fraction (fed the health percentage * 0.01): -1 none (0.75
    /// and up), 0 white (above 0.6), 1 grey (above 0.4), 2 black. It is the gauge's value, not the armour alone: a car
    /// whose engine, suspension or brake is scratched (a flamer goes straight to those) reads red and smokes black.
    /// </summary>
    public static int SmokeStage(float gauge) => gauge >= 0.75f ? -1 : gauge > 0.6f ? 0 : gauge > 0.4f ? 1 : 2;

    /// <summary>The stage the car smokes at now, from what its health bar reads.</summary>
    public int SmokeStage() => SmokeStage(GaugeFraction());

    /// <summary>
    /// The weakest side's armour + chassis, or the weakest of engine, suspension and brake if lower (our own measure,
    /// not the gauge; nothing drives the smoke from it any more).
    /// </summary>
    public float BarRatio()
    {
        float m = 1;
        for (int z = 0; z < 4; z++)
        {
            int max = ArmourMax[z] + ChassisMax[z];
            if (max > 0) m = MathF.Min(m, (float)(System.Math.Max(Armour[z], 0) + System.Math.Max(Chassis[z], 0)) / max);
        }
        foreach (var p in (Part?[])[Engine, Suspension, Brake])
            if (p is not null) m = MathF.Min(m, p.Ratio);
        return System.Math.Clamp(m, 0, 1);
    }

    /// <summary>The weaker of a zone's armour and chassis, as a 0–1 ratio (also the lamp test).</summary>
    public float ZoneRatio(int z)
    {
        float Ratio(int[] cur, int[] max) => max.Length <= z || z == 4 || max[z] == 0 ? cur[z] * 0.01f : (float)cur[z] / max[z];
        return MathF.Min(Ratio(ArmourMirror, ArmourMax), Ratio(ChassisMirror, ChassisMax));
    }

    /// <summary>The damage level a zone shows (0 whole … 3 wrecked).</summary>
    public int ZoneLevel(int z)
    {
        float r = ZoneRatio(z);
        return r > 0.75f ? 0 : r > 0.5f ? 1 : r > 0.25f ? 2 : 3;
    }

    public SpecialPart? ActiveSpecial(int type) =>
        Slots.Skip(17).OfType<SpecialPart>().FirstOrDefault(s => s.Type == type && s.Active);

    public static int Remaining(DamagePacket p)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) if ((p.Mask & (1 << i)) != 0) s += p.Ch[i];
        return s;
    }

    // ---- the steps of the vehicle slot ----

    /// <summary>
    /// Chassis reinforcement absorbs impacts, armour the ballistic
    /// and explosive channels; the top only shrinks. <paramref name="unlimArmour"/>
    /// and <paramref name="unlimChassis"/> are the Play Options cheats (bits 8 and
    /// 0x10, already gated on the player and no network session): on the four
    /// sides the cheated pool never shrinks and its channels are zeroed, the
    /// armour cheat zeroing all four (fire and an impact's remainder too); the
    /// top (zone 4) ignores both.
    /// <para>The multipliers are single-precision values (Structo 2, Mud Flaps
    /// +0.1, Curb Feelers 1.1), but the original multiplies and divides by them
    /// in double precision before truncating, with the channels taken as
    /// unsigned: so with Curb Feelers 11 wears 9 (11 / 1.100000024), not 10.</para>
    /// </summary>
    public int ArmourStep(int zone, DamagePacket p, bool unlimArmour = false, bool unlimChassis = false)
    {
        if (zone > 4) return 0;
        float mc = 1f, ma = 1f;
        if (zone is 0 or 3) mc = ActiveSpecial(5) is not null ? 2f : 1f;
        if (ActiveSpecial(7) is not null) mc = mc - -0.1f;
        if (ActiveSpecial(6) is not null) ma = 1.1f;
        if (zone != 4)
        {
            if ((p.Mask & 8) != 0 && !unlimChassis)
            {
                int cap = Cap(Chassis[zone], mc);
                if ((uint)p.Ch[3] > (uint)cap) { p.Ch[3] -= cap; Chassis[zone] = 0; ChassisMirror[zone] = 0; }
                else { int d = Wear(p.Ch[3], mc); Chassis[zone] -= d; ChassisMirror[zone] -= d; p.Ch[3] = 0; }
            }
            else if (unlimChassis) p.Ch[3] = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((p.Mask & 3) != 0 && !unlimArmour)
                {
                    int cap = Cap(Armour[zone], ma);
                    if ((uint)p.Ch[i] > (uint)cap) { p.Ch[i] -= cap; Armour[zone] = 0; ArmourMirror[zone] = 0; }
                    else { int d = Wear(p.Ch[i], ma); Armour[zone] -= d; ArmourMirror[zone] -= d; p.Ch[i] = 0; }
                }
                else if (unlimArmour) p.Ch[i] = 0;
            }
            return Remaining(p);
        }
        if ((p.Mask & 8) != 0)
        {
            if ((uint)p.Ch[3] > (uint)Cap(ChassisMirror[4], mc)) ChassisMirror[4] = 0;
            else ChassisMirror[4] -= Wear(p.Ch[3], mc);
        }
        for (int i = 0; i < 4; i++)
        {
            if ((p.Mask & 3) == 0) break;
            if ((uint)p.Ch[i] > (uint)Cap(ArmourMirror[4], ma)) ArmourMirror[4] = 0;
            else ArmourMirror[4] -= Wear(p.Ch[i], ma);
        }
        return Remaining(p);
    }

    /// <summary>The most a pool absorbs: trunc(pool × m), in double precision.</summary>
    public static int Cap(int pool, float m) => Trunc(pool * (double)m);

    /// <summary>What a channel wears off a pool: trunc(ch / m), the channel unsigned, in double precision.</summary>
    public static int Wear(int ch, float m) => Trunc((uint)ch / (double)m);

    /// <summary>Truncation toward zero to 64 bits, keeping the low 32 (as the original's conversion does).</summary>
    private static int Trunc(double x) => (int)(long)x;

    /// <summary>
    /// Pick a component from the sub-zone's row (re-rolling
    /// empty slots and the chassis), let it absorb; on a dead pick, the kill
    /// roll (not for impacts) or a soak of up to max/4. Returns what is left
    /// for another pick, 0 when the hit is over. Sets Dead / DriverKilled.
    /// With <paramref name="nitro"/> (the Nitro Pack's copy), a Chemical Mortar packet (flag 0x10) kills the
    /// driver outright after the Cup Holders' roll (the driver-killed and dead flags); the base game only skips the armour for it.
    /// </summary>
    public int ComponentLoop(int sub, DamagePacket p, bool killRoll, float health, CrtRand rand, out int picked, out bool killed, out bool driverKilled, bool nitro = false)
    {
        picked = -1; killed = driverKilled = false;
        if (ActiveSpecial(9) is not null && rand.Next() % 100 < 10) { p.Mask = 0; return 0; }
        if (nitro && (p.Flags & 0x10) != 0) { killed = driverKilled = true; return 0; }
        int idx;
        var row = Rows[sub];
        if ((p.Flags & 1) != 0 && health < 33f)
        {
            idx = 6;
            p.Ch[2] = (rand.Next() & 0x32) + 0x4b;
        }
        else
        {
            idx = row[rand.Next() % row.Count];
            while (true)
            {
                if (idx == -1) { p.Mask = 0; return 0; }
                if (Slots[idx] is not null && idx != 6) break;
                idx = row[rand.Next() % row.Count];
            }
        }
        picked = idx;
        var part = Slots[idx]!;
        if (part.Hp != 0)
        {
            bool over = part.Apply(p);
            return over ? Remaining(p) : 0;
        }
        if (killRoll)
        {
            float k = IsPlayer && !Network ? 0.1f : 2.5f;   // (not a network game and the victim is the player)
            if ((p.Mask & 8) == 0)
            {
                long sumHp = 0, sumMax = 0;
                foreach (var s in Slots) if (s is not null) { sumHp += (uint)s.Hp; sumMax += (uint)s.Max; }
                int r100 = rand.Next() % 100;
                float x = (float)((double)sumHp / sumMax * r100);
                int rem = Remaining(p);
                if (rem * (double)k > x)   // the product in double precision, as the original compares it
                {
                    killed = true;
                    driverKilled = idx == 6;
                    return 0;
                }
            }
        }
        int soak = part.Max >> 2;
        for (int i = 0; i < 4; i++)
        {
            if ((p.Mask & (1 << i)) == 0) continue;
            if ((uint)soak < (uint)p.Ch[i]) { p.Ch[i] -= soak; return 0; }
            soak -= p.Ch[i]; p.Ch[i] = 0;
        }
        return Remaining(p);
    }

    /// <summary>The armour zone and the pick-table sub-zone of a hit point.</summary>
    public static (int Zone, int Sub) Zones(Vector3 hitLocal)
    {
        float len = hitLocal.Length();
        if (len < 1e-9f) return (3, 5);
        var h = hitLocal / len;
        if (h.Y > 0.8f) return (4, 6);
        float l2 = MathF.Sqrt(h.X * h.X + h.Z * h.Z);
        float x = l2 > 0 ? h.X / l2 : 0, z = l2 > 0 ? h.Z / l2 : 0;
        int zone = x > 0.707f ? 2 : x < -0.707f ? 1 : z > 0.707f ? 0 : 3;
        int sub = z > 0.707f ? 0 : z < -0.707f ? 5 : z > 0 ? (x > 0 ? 3 : 1) : (x > 0 ? 4 : 2);
        return (zone, sub);
    }
}

/// <summary>What the vehicle's files say about its damage (VCFC armour, component HP, specials).</summary>
public sealed record VehicleBuild(int[] Armour, int[] Chassis, int[] WheelHp, int EngineHp, int BrakeHp, int SuspensionHp, float SuspensionImpactScale, List<int> Specials);
