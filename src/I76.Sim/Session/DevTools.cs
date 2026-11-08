using System.Numerics;
using I76.Sim.Damage;
using I76.Sim.Math;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Session;

/// <summary>A place the Developer panel offers to teleport to: a name, the game (x, z) and a heading in degrees from +Z toward +X.</summary>
public readonly record struct DevPoint(string Name, double X, double Z, float Heading);

/// <summary>
/// The Developer panel's actions on the simulation (docs/dev-hooks.md, "Developer panel"): none is in the original. They run
/// between ticks, on a session that allows the debug hooks (single player; never a network session), and are as
/// deterministic as the tick: no clock, no rand. A replay of the same inputs does not know about them, so a run that used
/// one is a debug run.
/// </summary>
public static class DevTools
{
    /// <summary>Whether the session takes the panel's actions: the debug hooks (a network session refuses them).</summary>
    public static bool Allowed(SimSession s) => s.Options.DebugHooks;

    /// <summary>
    /// The mission's own named places: each script path's first point (heading along its first leg), then each named car the
    /// script knows that is still in the world, 20 m behind it (facing it). Empty for a session without a script.
    /// </summary>
    public static IReadOnlyList<DevPoint> Points(SimSession s)
    {
        var list = new List<DevPoint>();
        if (s.Script?.Vm is { } vm)
            foreach (var p in vm.Program.Paths)
            {
                if (p.Points.Count == 0) continue;
                var a = p.Points[0];
                float heading = p.Points.Count > 1 ? HeadingOf(p.Points[1] - a) : 0;
                list.Add(new DevPoint($"path {p.Name}", a.X, a.Z, heading));
            }
        if (s.Script?.Host.Entities is { } ents)
            foreach (var e in ents)
            {
                if (e.Vehicle is not { } v || v == s.World.Player || !s.World.Vehicles.Contains(v) || (v.Flags & VehicleFlags.Dead) != 0) continue;
                var fwd = new Vector3(v.Rot.Forward.X, 0, v.Rot.Forward.Z);
                fwd = fwd.LengthSquared() > 1e-6f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
                list.Add(new DevPoint($"car {e.Name}", v.Pos.X - fwd.X * 20, v.Pos.Z - fwd.Z * 20, HeadingOf(fwd)));
            }
        return list;
    }

    /// <summary>The heading (degrees from +Z toward +X) of a direction in the ground plane.</summary>
    public static float HeadingOf(Vector3 d) => d.X * d.X + d.Z * d.Z < 1e-12f ? 0 : MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;

    /// <summary>
    /// The car to (x, z), facing <paramref name="heading"/>, on the bare terrain there, upright and at rest: the placement of
    /// <see cref="UprightReset"/> (the script's teleport), so contacts, wheels and the network generation start again.
    /// False (nothing done) for a dead car, a helicopter or an emplacement, or a session without the debug hooks.
    /// </summary>
    public static bool Teleport(SimSession s, Vehicle v, double x, double z, float heading)
    {
        if (!Allowed(s) || !UprightReset.Can(v) || !double.IsFinite(x) || !double.IsFinite(z) || !float.IsFinite(heading)) return false;
        float yaw = heading * MathF.PI / 180f;
        var f = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        v.Pos = new Double3(x, s.World.Ground.BareHeight(x, z), z);
        v.Rot = new Rows(Vector3.Cross(Vector3.UnitY, f), Vector3.UnitY, f);
        UprightReset.Apply(s.World, v);
        return true;
    }

    /// <summary>
    /// The car made whole and loaded again, in place: armour and chassis reinforcement full, every component's hit points at
    /// their maximum (and what a damaged one had lowered: grip and rolling, engine power, brake, cornering, back to nominal), the
    /// tyres unblown, every weapon (handguns too) whole and at its full ammunition, the specials' charges back. No rand() is
    /// drawn and the make-up is kept, so a snapshot taken after it still restores into the same mission. False for a dead car,
    /// one without a damage state, or a session without the debug hooks.
    /// </summary>
    public static bool Repair(SimSession s, Vehicle v)
    {
        if (!Allowed(s) || (v.Flags & VehicleFlags.Dead) != 0 || v.Health is not { } h) return false;
        for (int z = 0; z < 4; z++) { h.Armour[z] = h.ArmourMax[z]; h.Chassis[z] = h.ChassisMax[z]; }
        h.RoughCarry = 0;
        foreach (var part in h.Slots)
        {
            if (part is null) continue;
            part.Detached = false;
            if (part is WheelPart wp && wp.Wheel.Blown) { wp.Wheel.Blown = false; wp.Wheel.Rest = wp.Wheel.Offset.Y; }
            if (part is SpecialPart sp) { sp.Charges = SpecialPart.FullCharges(sp.Type); sp.Unk_0x10 = 0; }
            if (part.Hp >= part.Max) continue;
            part.Hp = part.Max;
            // an empty packet: no channel set, so Apply only recomputes what the part's health sets (as at full health)
            if (part is not WeaponPart) part.Apply(new DamagePacket());
        }
        if (v.Armory is { } a)
        {
            foreach (var w in a.Weapons.Concat(a.LoadOrder).Distinct()) Refill(w);
            foreach (var w in a.Handguns) if (w is not null) Refill(w);
        }
        return true;
    }

    private static void Refill(Weapon w)
    {
        w.Hp = w.MaxHp;
        w.Ammo = (int)w.Def.Ammo;
    }

    /// <summary>
    /// God mode: the Play Options cheats Unlim. Ammo, Armor and Chas. Rein. together (the player's side only, as those are).
    /// False for a session without the debug hooks.
    /// </summary>
    public static bool SetGodMode(SimSession s, bool on)
    {
        if (!Allowed(s) || s.Combat is not { } c) return false;
        c.UnlimitedAmmo = on;
        if (c.Damage is DamageModel dm) { dm.UnlimitedArmour = on; dm.UnlimitedChassis = on; }
        return true;
    }

    /// <summary>Whether all three of God mode's cheats are on.</summary>
    public static bool GodMode(SimSession s) => s.Combat is { UnlimitedAmmo: true, Damage: DamageModel { UnlimitedArmour: true, UnlimitedChassis: true } };

    /// <summary>
    /// The nearest other living car (not an emplacement or a helicopter) brought to <paramref name="ahead"/> units in front of
    /// <paramref name="to"/>, facing it, at rest: a fight at close range without driving to one (I76_SPAWN_AI_AT's move, as an
    /// action). The car keeps its AI and its orders. Returns the car moved, or null.
    /// </summary>
    public static Vehicle? BringNearest(SimSession s, Vehicle to, float ahead = 30f)
    {
        if (!Allowed(s)) return null;
        var at = to.Pos.ToVector3();
        var other = s.World.Vehicles.Where(v => v != to && !s.World.IsHuman(v) && UprightReset.Can(v))
            .OrderBy(v => Vector3.DistanceSquared(v.Pos.ToVector3(), at)).FirstOrDefault();
        if (other is null) return null;
        var fwd = new Vector3(to.Rot.Forward.X, 0, to.Rot.Forward.Z);
        fwd = fwd.LengthSquared() > 1e-6f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
        return Teleport(s, other, at.X + fwd.X * ahead, at.Z + fwd.Z * ahead, HeadingOf(-fwd)) ? other : null;
    }
}
