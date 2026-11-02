using System.Numerics;
using I76.Formats.Bwd2;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>What a round fired now would meet first along its weapon's line of fire (<see cref="Combat.LineOfFire"/>).</summary>
/// <param name="Distance">From the muzzle along its forward, in metres: to what it meets, or the weapon's reach when it meets nothing.</param>
/// <param name="Victim">The car (<see cref="Vehicle"/>) or structure (<see cref="StaticObject"/>) met, or null for the ground or nothing.</param>
/// <param name="Car">The round would hit a car (a gun emplacement included) that is not dead.</param>
public readonly record struct FireLine(float Distance, object? Victim, bool Car);

public sealed partial class Combat
{
    /// <summary>Whether a weapon's rounds fly a line from the muzzle: no gravity in the ordnance.</summary>
    public static bool FliesStraight(GizmoDef def) => def.Ordnance is { Gravity: 0 };

    /// <summary>
    /// How far a round flies before its life runs out (<see cref="Spawn"/>, <see cref="Step"/>): it leaves at the weapon's speed
    /// and approaches the ordnance's terminal speed by <c>k·(vT − v)</c> a step, k the step's dt (half of it for the rocket),
    /// so v(t) = vT + (v0 − vT)·e^(−t) (e^(−t/2) for the rocket), integrated over the life (5 s when the weapon says 0).
    /// </summary>
    public static float Reach(GizmoDef def)
    {
        if (def.Ordnance is not { } ord) return 0;
        float life = def.Life == 0 ? 5f : def.Life;
        float v0 = def.Kind is 0xf or 0x10 or 0x13 ? 5f : def.Speed, vT = ord.TerminalSpeed;
        float c = def.Kind == 2 ? 0.5f : 1f;
        return MathF.Max(0, vT * life + (v0 - vT) * (1 - MathF.Exp(-c * life)) / c);
    }

    /// <summary>
    /// Where a round of <paramref name="w"/> on <paramref name="shooter"/> would go if it left the muzzle now, asked read-only: no
    /// round is made, rand() is not drawn and nothing is sent or changed. Only for a weapon that <see cref="FliesStraight"/> (else
    /// null). The line is the shot's own (<see cref="Shot"/>: the muzzle's place and its forward, a turret's or a fixed gun's aim
    /// included) out to the weapon's <see cref="Reach"/>, tested as the flight tests its steps (<see cref="HitTest"/>): the cars'
    /// boxes (not the shooter's, no wreck; a gun emplacement through its structure), the solid structures' meshes and boxes, and
    /// the terrain, the nearest of them winning. The cars stand where they are now (the round's flight time is not allowed for)
    /// and the muzzle's random spread (a fraction of a degree) is left out.
    /// </summary>
    public FireLine? LineOfFire(Vehicle shooter, Weapon w)
    {
        if (!FliesStraight(w.Def)) return null;
        float reach = Reach(w.Def);
        if (reach <= 0) return null;
        var rot = Rows.Concat(w.MuzzleRot, shooter.Rot);
        var p0 = shooter.Pos.ToVector3() + shooter.Rot.ToWorld(w.MuzzlePos);
        var d = rot.Forward * reach;
        var p1 = p0 + d;
        float best = TerrainHit(p0, p1, reach) is { } g ? Vector3.Distance(p0, g) / reach : 1f;
        object? victim = null;
        foreach (var v in World.Vehicles)
        {
            if (v == shooter || v.Wreck || v.IsEmplacement) continue;
            if (SegmentVsVehicle(v, p0, d) is { } h && h.T < best) { best = h.T; victim = v; }
        }
        if (World.Ground is IHasStatics hs)
        {
            float dd = Vector3.Dot(d, d);
            foreach (var o in hs.World.Objects)
            {
                if (!o.Solid || (o.Vehicle is { } ov && ov == shooter)) continue;
                // the line's nearest approach to the object's bounding sphere (the flight's own box test is per step; this is the whole line at once)
                var m = o.Centre - p0;
                float tc = System.Math.Clamp(Vector3.Dot(m, d) / dd, 0f, 1f);
                float r = MathF.Max(o.Radius, o.BoundRadius);
                if ((m - d * tc).LengthSquared() > r * r) continue;
                var (t, poly) = StaticWorld.RayMesh(o.Polys, p0, d, 1f);
                if (poly is not null && t < best) { best = t; victim = o; }
                if (StaticWorld.RayBoxes(o.Boxes, p0, d, 1f) is { } bh && bh.T < best) { best = bh.T; victim = o; }
            }
        }
        bool car = victim switch
        {
            Vehicle v => !IsDead(v),
            StaticObject { Vehicle: { } e } => !IsDead(e),
            _ => false,
        };
        return new FireLine(best * reach, victim, car);
    }
}
