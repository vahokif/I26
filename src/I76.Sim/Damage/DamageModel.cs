using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Damage;

/// <summary>What a damage event was, for the mission script's recorder.</summary>
public enum DamageKind { Collision, Round, Explosion, Flame, Terrain }

public abstract record DamageEvent;
public sealed record VehicleKilled(Vehicle Vehicle, bool DriverKilled) : DamageEvent;
public sealed record VehicleWrecked(Vehicle Vehicle) : DamageEvent;
public sealed record TyreBlown(Vehicle Vehicle, int Slot) : DamageEvent;
public sealed record PartFlung(Vehicle Vehicle, int Slot, Vector3 Velocity) : DamageEvent;
public sealed record Debris(Vector3 At, Vector3 Velocity) : DamageEvent;
/// <summary>
/// A structure lost nodes, or its root (<see cref="StaticObject.Nodes"/>, <see cref="StaticObject.Destroyed"/>):
/// its look follows them. <paramref name="Booms"/> are the explosions that went off with it, each with its
/// sound: −1 the root's at its marker, n node n's at its base.
/// </summary>
public sealed record StructureDestroyed(StaticObject Object, IReadOnlyList<int> Booms) : DamageEvent
{
    public StructureDestroyed(StaticObject o) : this(o, []) { }
}
public sealed record ZoneDamaged(Vehicle Vehicle, int Zone, int Level) : DamageEvent;
public sealed record EngineStarted(Vehicle Vehicle) : DamageEvent;
/// <summary>The engine was switched off with S: its loop stops.</summary>
public sealed record EngineStopped(Vehicle Vehicle) : DamageEvent;
/// <summary>The ignition sound starts: the player's, 2D; <paramref name="Sound"/> is null for NONE.</summary>
public sealed record IgnitionStarted(Vehicle Vehicle, string? Sound) : DamageEvent;
/// <summary>A sign knocked off as one tumbling piece: launch velocity and spin.</summary>
public sealed record SignKnocked(StaticObject Object, Vector3 Velocity) : DamageEvent;

/// <summary>
/// The class damage slots: the vehicle slot with
/// its armour step and component loop, death and its 10 s timer, collision damage, rough terrain
/// and structures.
/// </summary>
public sealed partial class DamageModel : IDamageSink, IStepDamage, IHeliDamage
{
    public SimWorld World { get; }
    public Combat Combat { get; }
    private CrtRand Rand => World.Rand;
    public List<DamageEvent> Events { get; } = [];
    /// <summary>(victim, attacker, kind, packet mask) after a vehicle was damaged by another.</summary>
    public event Action<Vehicle, Vehicle, DamageKind, int>? Recorded;
    /// <summary>A vehicle has just died.</summary>
    public event Action<Vehicle>? Killed;
    /// <summary>
    /// A car's death where the original takes its field salvage (the salvage routine's calls; the caller checks it is not the
    /// player): in the death routine after the explosion and the AI's component wipe, so a wreck's parts arrive at what the
    /// wipe left (measured: tyres 3..50 of 100); in the driver-killed branch right after the death flags.
    /// </summary>
    public event Action<Vehicle>? Salvageable;
    /// <summary>
    /// A hit destroyed a vehicle: the victim and the hit's source (the original's
    /// end, where the melee's kill credit is; only the destroying hit, never
    /// one on a wreck already dead).
    /// </summary>
    public event Action<Vehicle, object?>? KilledBy;
    /// <summary>
    /// A packet reaching a vehicle's slot, before anything is taken from it: where the original queues the player's
    /// channels for the force-feedback DLL. For observers; it changes nothing.
    /// </summary>
    public event Action<Vehicle, DamagePacket>? Damaging;
    /// <summary>Whether an object counts as a vehicle for attribution (types 1, 7, 9) and its mass.</summary>
    public static float MassOf(object? o) => o is Vehicle v ? v.P.Mass : 0;

    /// <summary>Play Options Unlim. Armor (bit 8): the player's side armour never shrinks and absorbs every channel.</summary>
    public bool UnlimitedArmour { get; set; }
    /// <summary>Play Options Unlim. Chas. Rein. (bit 0x10): the player's side chassis never shrinks and impacts are absorbed whole.</summary>
    public bool UnlimitedChassis { get; set; }

    /// <summary>
    /// A co-op game's Co-op Damage (the port's; docs/deviations.md, <see cref="Missions.CoopSetup.DamageFactor"/>): what the
    /// humans' cars take is scaled by it. 1 everywhere else: single player, a melee, a co-op game left at the original's.
    /// </summary>
    public float CoopDamageFactor { get; set; } = 1f;

    /// <summary>The factor on what <paramref name="v"/> takes: <see cref="CoopDamageFactor"/> on a co-op human's car, else 1.</summary>
    private float IncomingFactor(Vehicle v) => CoopDamageFactor != 1f && World.Coop && World.IsHuman(v) ? CoopDamageFactor : 1f;

    /// <summary>A copy of the packet with each channel scaled and truncated (a splash's packet may go on to other victims).</summary>
    private static DamagePacket Scaled(DamagePacket p, float f)
    {
        var q = new DamagePacket { Attacker = p.Attacker, Mask = p.Mask, Flags = p.Flags, Kind = p.Kind };
        for (int i = 0; i < 4; i++) q.Ch[i] = (int)(p.Ch[i] * f);
        return q;
    }

    /// <summary>The cheats as the original tests them: the option, the victim is the player, no network session.</summary>
    private int ArmourStep(VehicleDamage d, int zone, DamagePacket p)
    {
        bool on = d.IsPlayer && !Combat.NetSession;
        return d.ArmourStep(zone, p, on && UnlimitedArmour, on && UnlimitedChassis);
    }

    public DamageModel(SimWorld world, Combat combat)
    {
        World = world;
        Combat = combat;
        combat.Damage = this;
        world.Step.Damage = this;
        world.HeliDamage = this;
        world.CarContact = CarContact;
    }

    public static bool IsDead(Vehicle v) => (v.Flags & VehicleFlags.Dead) != 0;

    // ---- the sink ----

    public void Apply(object victim, DamagePacket p, Vector3 hitLocal) => Apply(victim, p, hitLocal, -1);

    /// <param name="node">The structure node a round or ram touched (<see cref="StaticObject.Nodes"/>), −1 for the root, as a splash hits it.</param>
    public void Apply(object victim, DamagePacket p, Vector3 hitLocal, int node)
    {
        switch (victim)
        {
            case Vehicle v:
                VehicleSlot(v, p, hitLocal);
                if (p.Attacker is Vehicle a && a != v) Record(v, a, p.Kind, p.Mask);
                break;
            case StaticObject { Vehicle: { } gun }:
                // a hit on any node of an emplacement maps to its root: the type-8 damage slot
                EmplacementSlot(gun, p);
                if (p.Attacker is Vehicle ga && ga != gun) Record(gun, ga, p.Kind, p.Mask);
                break;
            case StaticObject sign when sign.Type == 4 && node < 0 || sign.SignNode(node):
                // Sends a type-4 root or node to the sign's own slot, which knocks off the
                // node's root for an explosive or impact packet (mask & 0xa) and ignores ballistic and fire (measured in the original game:
                // a 50-point round on swend_1's sign node left it standing)
                if ((p.Mask & 0xa) != 0) KnockSign(sign);
                break;
            case StaticObject o:
                StructureSlot(o, node, p);
                break;
        }
    }

    private void Record(Vehicle victim, Vehicle attacker, DamageKind kind, int mask) => Recorded?.Invoke(victim, attacker, kind, mask);

    /// <summary>The vehicle damage slot (class slot [10]). pkt null: straight to the death check (the vehicle death routine's call).</summary>
    public void VehicleSlot(Vehicle v, DamagePacket? p, Vector3 hitLocal)
    {
        if (p is not null && IncomingFactor(v) is var f && f != 1f) p = Scaled(p, f);
        if (p is not null) Damaging?.Invoke(v, p);
        if (v.Heli is { } heli) { HeliSlot(v, heli, p); return; }
        if (v.Emplacement is not null) { if (p is not null) EmplacementSlot(v, p); return; }
        if (v.Health is not { } d) return;
        bool deadAtEntry = IsDead(v);
        if (p is not null && deadAtEntry) return;
        if (p is not null)
        {
            var (zone, sub) = VehicleDamage.Zones(hitLocal);
            if ((p.Flags & 1) != 0 && d.HealthPercent() > 33f) { p.Mask = 1; p.Ch[0] = 5; }
            int rem = (p.Flags & 0x10) == 0 ? ArmourStep(d, zone, p) : VehicleDamage.Remaining(p);
            bool killed = false, driver = false;
            while (rem != 0 && !killed)
            {
                rem = d.ComponentLoop(sub, p, true, d.HealthPercent(), Rand, out int picked, out killed, out driver, Combat.NitroRules);
                if (picked is >= 0 and < 6 && d.Slots[picked] is WheelPart wp)
                {
                    float r = wp.Max == 0 ? 0 : (float)wp.Hp / wp.Max;
                    if (r <= 0.25f && wp.BlowOut()) Events.Add(new TyreBlown(v, picked));
                }
            }
            if (killed) v.Flags |= driver ? (VehicleFlags)0x4020 : VehicleFlags.Dead;
            if (!deadAtEntry) Events.Add(new ZoneDamaged(v, zone, d.ZoneLevel(zone)));
            if (!deadAtEntry && IsDead(v)) KilledBy?.Invoke(v, p.Attacker);
        }
        if (!IsDead(v) || deadAtEntry && p is not null) return;
        if (((uint)v.Flags & 0x4000) == 0) Die(v, true);
        else
        {
            v.Timer = 10f;
            v.Flags |= (VehicleFlags)0x80008000;
            Events.Add(new VehicleKilled(v, true));
            Killed?.Invoke(v);
            Salvageable?.Invoke(v);
        }
    }

    // ---- death ----

    private static readonly Vector3[] KickTable =
    [
        new(MathF.PI / 8, 0, MathF.PI / 16), new(MathF.PI / 8, 0, -MathF.PI / 16),
        new(-MathF.PI / 8, 0, MathF.PI / 16), new(-MathF.PI / 8, 0, -MathF.PI / 16),
    ];

    private float U() => Rand.Next() * 3.051851e-5f;

    /// <summary>
    /// With init, the 10 s timer, the x1_carx1 explosion (its
    /// splash lands at once), the kick, every tyre blown and, for an AI car,
    /// the component wipe; then, every call, one effect roll.
    /// </summary>
    public void Die(Vehicle v, bool init)
    {
        var pos = v.Pos.ToVector3();
        var d = v.Health;
        if (init)
        {
            v.Timer = 10f;
            v.Flags |= (VehicleFlags)0x80008000;
            v.Flags &= ~(VehicleFlags)0x40;
            v.Throttle = 0; v.Steer = 0;
            World.Ai.Remove(v);
            Events.Add(new VehicleKilled(v, false));
            Killed?.Invoke(v);
            Combat.Explode("x1_carx1", pos, v);
            float a = (U() - -1f) * 2f, b = U() - -1f;
            int q = Rand.Next() & 3;
            v.AngVel = KickTable[q] * a;
            v.Vel.Y = MathF.Min(v.Vel.Y - b * -6f, 20f);
            v.Speed = v.Vel.Length();
            v.Flags |= VehicleFlags.Airborne;
            if (d is not null)
                for (int i = 0; i < 6; i++)
                    if (d.Slots[i] is WheelPart wp && wp.BlowOut()) Events.Add(new TyreBlown(v, i));
            if (d is not null && !d.IsPlayer)
                for (int i = 0; i < 20; i++)
                {
                    if (d.Slots[i] is not { } part) continue;
                    int amt = ((Rand.Next() & 0x7f) + 0x81) * part.Hp >> 8;
                    var pk = new DamagePacket { Mask = 2 };
                    pk.Ch[1] = amt;
                    part.Apply(pk);
                }
            Salvageable?.Invoke(v);
        }
        // the effect roll
        int e = Rand.Next() & 15;
        switch (e)
        {
            case 0 when d is not null:
            {
                int s = Rand.Next() % 5;
                for (int k = 0; k < 5; k++)
                {
                    int i = (s + k) % 5;
                    if (d.Slots[i] is not { Detached: false } part) continue;
                    part.Detached = true;
                    var off = (part as WheelPart)?.Wheel.Offset ?? Vector3.UnitX;
                    var dir = new Vector3(off.X, 0, off.Z);
                    dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitX;
                    Events.Add(new PartFlung(v, i, v.Rot.ToWorld(new Vector3(8 * dir.X, 2, 8 * dir.Z))));
                    break;
                }
                break;
            }
            case 1 when d is not null:
            {
                int s = Rand.Next() % 6;
                for (int k = 0; k < 6; k++)
                {
                    int i = 10 + (s + k) % 6;
                    if (d.Slots[i] is not { Detached: false } part) continue;
                    part.Detached = true;
                    var mp = (part as WeaponPart)?.Weapon.MuzzlePos ?? Vector3.UnitY;
                    var dir = mp.LengthSquared() > 1e-8f ? Vector3.Normalize(mp) : Vector3.UnitY;
                    Events.Add(new PartFlung(v, i, v.Rot.ToWorld(dir * 15f)));
                    break;
                }
                break;
            }
            case 2 or 3:
                break;      // per child node, rand() & 1 to detach a chunk: child nodes are not modelled
            case 4 or 5:
                Rand.Next();
                Events.Add(new Debris(pos, new Vector3(0, 15, 0)));
                break;
            case 6 or 7:
            {
                float r = Combat.Collider(v).Radius;
                float x = pos.X - ((Rand.Next() - 0x7fff) >> 1) * r * -6.103888e-5f;
                float y = pos.Y - ((Rand.Next() - 0x7fff) >> 1) * r * -1.5259255e-5f;
                float z = pos.Z - ((Rand.Next() - 0x7fff) >> 1) * r * -6.103888e-5f;
                Combat.Explode("x1_cars1", new Vector3(x, y, z), v);
                break;
            }
        }
    }

    /// <summary>The FSM's destroy, the 35 m fall, the rollover. A helicopter: the helicopter's kill.</summary>
    public void Destroy(Vehicle v)
    {
        if (((uint)v.Flags & 0x8020) != 0) return;
        if (v.IsHeli) { ShootDown(v); if (IsDead(v)) KilledBy?.Invoke(v, null); return; }
        if (v.IsEmplacement) return;   // handles types 1 and 9 only: destroy does nothing to a gun
        v.Flags |= VehicleFlags.Dead;
        VehicleSlot(v, null, Vector3.Zero);
        // a destruction with no killer: the network game books it ("*** %s has been killed", −500)
        KilledBy?.Invoke(v, null);
    }

    /// <summary>
    /// The flat-tyre cheats: the wheel in damage slot 0..5 gets hit points 0 of 80 (which
    /// leaves grip and rolling as they were) and blows (once, rest height × 0.688, tblow.wav). Dead or
    /// alive, as the original does not look.
    /// </summary>
    public void FlattenTyre(Vehicle v, int slot)
    {
        if (v.Health?.Slots[slot] is not WheelPart wp) return;
        wp.Hp = 0;
        wp.Max = 80;
        if (wp.BlowOut()) Events.Add(new TyreBlown(v, slot));
    }

    /// <summary>
    /// CaltropTyreHit (Nitro Pack), from the caltrop's probe on a car (object type
    /// 1 only: not a helicopter or a gun). The wheel nearest the caltrop (its centre less its rest height, 
    /// over the six slots, the first on a tie) is set to 25 of 100 hit points (which sets the max too and
    /// leaves grip and rolling as they were), and 25 of 100 is the damage art's level 3, so the tyre blows
    /// (once, rest height × 0.688, tblow.wav). With Tire Covers (special type 11 in any slot, whatever its
    /// state) the wheel keeps three quarters of its hit points instead (truncated) and its art is looked at only below
    /// 26, so a covered tyre blows only once that leaves it at a quarter of its max. It happens every frame the car is
    /// over the caltrop; the caltrop is not used up.
    /// </summary>
    public void CaltropTyreHit(Vehicle v, Vector3 at)
    {
        if (v.ObjectClass != 1 || v.IsHeli || v.IsEmplacement || v.Health is not { } d) return;
        WheelPart? near = null;
        int slot = -1;
        float best = float.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            if (d.Slots[i] is not WheelPart wp) continue;
            var c = v.Pos.ToVector3() + v.Rot.ToWorld(wp.Wheel.Mount) - new Vector3(0, wp.Wheel.Rest, 0);
            float d2 = (at - c).LengthSquared();
            if (d2 < best) { best = d2; near = wp; slot = i; }
        }
        if (near is null) return;
        bool covers = false;
        for (int i = 17; i < 20; i++) if (d.Slots[i] is SpecialPart { Type: 11 }) covers = true;
        if (covers)
        {
            near.Hp = (int)(near.Hp * 0.75f);
            if (near.Hp >= 26) return;
        }
        else { near.Hp = 25; near.Max = 100; }
        // For parts 5..10: level 3 (Hp/Max ≤ 0.25) blows the tyre
        if (near.Ratio <= 0.25f && near.BlowOut()) Events.Add(new TyreBlown(v, slot));
    }

    // ---- gun emplacements ----

    /// <summary>The emplacement's damage: one pool, every set channel, no armour; a hit larger than what is left kills it.</summary>
    public void EmplacementSlot(Vehicle v, DamagePacket p)
    {
        if (v.Emplacement is not { } e || IsDead(v)) return;
        for (int i = 0; i < 4; i++)
        {
            if ((p.Mask & (1 << i)) == 0) continue;
            if (e.Hp < p.Ch[i])
            {
                KillEmplacement(v);
                KilledBy?.Invoke(v, p.Attacker);
                return;
            }
            e.Hp -= p.Ch[i];
        }
    }

    /// <summary>
    /// The gun's death, inlined in the emplacement's damage: off the radar and dead,
    /// the x1_bldx1 explosion (its splash) and vexplode.wav, the zones' burnt
    /// state-3 geometry with the gun hidden (the collider kept, from that
    /// geometry), ten chunks of debris. It stays where it is.
    /// </summary>
    public void KillEmplacement(Vehicle v)
    {
        if (v.Emplacement is not { } e || IsDead(v)) return;
        e.Hp = 0;
        v.Flags |= VehicleFlags.Dead;
        v.Throttle = 0; v.Steer = 0;
        World.Ai.Remove(v);
        e.Collider?.Collapse();
        var pos = v.Pos.ToVector3();
        Combat.Explode("x1_bldx1", pos, v);
        Events.Add(new VehicleKilled(v, false));
        Killed?.Invoke(v);
        for (int i = 0; i < 10; i++) { Rand.Next(); Events.Add(new Debris(pos, Vector3.Zero)); }
    }

    // ---- helicopters ----

    /// <summary>The helicopter's damage: one pool, every set channel; a hit larger than what is left brings it down.</summary>
    private void HeliSlot(Vehicle v, HeliState h, DamagePacket? p)
    {
        if (p is null || ((uint)v.Flags & 0x8020) != 0) return;
        for (int i = 0; i < 4; i++)
        {
            if ((p.Mask & (1 << i)) == 0) continue;
            if (h.Hp < p.Ch[i])
            {
                h.Hp = 0;
                ShootDown(v);
                KilledBy?.Invoke(v, p.Attacker);
                return;
            }
            h.Hp -= p.Ch[i];
        }
    }

    /// <summary>The kill, inlined in the helicopter's update, the helicopter's damage and the helicopter's kill: falling, turning (0.2, 0, −0.5), an x1_bldx1 where it was.</summary>
    public void ShootDown(Vehicle v)
    {
        v.Flags |= HeliStep.Falling;
        v.AngVel = new Vector3(0.2f, 0, -0.5f);
        World.Ai.Remove(v);
        Combat.Explode("x1_bldx1", v.Pos.ToVector3(), null);
    }

    /// <summary>
    /// The helicopter's crash: it met the ground. Two x1_bldx1 explosions, the
    /// burnt hull, vexplode.wav, ten chunks; the wreck's collider unlinked.
    /// </summary>
    public void Crash(Vehicle v)
    {
        var pos = v.Pos.ToVector3();
        v.Throttle = 0; v.Steer = 0;
        World.Ai.Remove(v);
        Combat.Explode("x1_bldx1", pos, null);
        Combat.Explode("x1_bldx1", pos, v);
        Events.Add(new VehicleKilled(v, false));
        Killed?.Invoke(v);
        for (int i = 0; i < 10; i++) { Rand.Next(); Events.Add(new Debris(pos, Vector3.Zero)); }
        if (!World.IsHuman(v))
        {
            v.Wreck = true;
            Events.Add(new VehicleWrecked(v));
        }
    }

    // ---- IStepDamage ----

    /// <summary>The effect roll while dying, and the timer: engine start completes, or a death ends.</summary>
    public void Timer(Vehicle v, float dt) => Timer(v, dt, effects: true);

    /// <summary>The timer; without <paramref name="effects"/> a dying car's effect roll is left out (a network client's prediction: the server's).</summary>
    public void Timer(Vehicle v, float dt, bool effects)
    {
        uint f = (uint)v.Flags;
        if (effects && (f & 0x8000) != 0 && (f & 0x4000) == 0) Die(v, false);
        f = (uint)v.Flags;
        if (v.StartEngineKey != 0)
        {
            // the ignition key (S): the engine off at once, or cranking when it is off and not already starting
            v.StartEngineKey = 0;
            if ((f & (uint)VehicleFlags.Dead) == 0)
            {
                if ((f & 1) == 0)
                {
                    if ((f & 9) == 0) { Ignite(v); f = (uint)v.Flags; }
                }
                else { v.Flags &= ~VehicleFlags.EngineOn; Events.Add(new EngineStopped(v)); f = (uint)v.Flags; }
            }
        }
        if ((f & 0x80000000) == 0) return;
        v.Timer -= dt;
        if (v.Timer > 0) return;
        v.Timer = 0;
        uint g = f & 0x7fffffff;
        if ((f & 0x8c08) == 8) { g |= 1; Events.Add(new EngineStarted(v)); }
        else if ((f & 0x8c08) == 0x8000)
        {
            if (!World.IsHuman(v))
            {
                v.Wreck = true;
                World.Ai.Remove(v);
                v.Flags = (VehicleFlags)(f & 0x7fff73f7);
                Events.Add(new VehicleWrecked(v));
                return;
            }
        }
        v.Flags = (VehicleFlags)(g & 0xffff73f7);
    }

    /// <summary>The ignition sound's name and length by the engine's health; nothing for any car but a human's.</summary>
    public (string? Sound, float Length) Ignition(Vehicle v)
    {
        if (!World.IsHuman(v) || v.Ignition is not { } row) return (null, 0);
        int i = row.Pick(v.Health?.Engine?.Ratio ?? 1);
        return (row.Sounds[i], row.Sounds[i] is null ? 0 : row.Times[i]);
    }

    /// <summary>Start cranking: the sound and its length on the timer (the key's inline copy).</summary>
    public void Ignite(Vehicle v)
    {
        var (sound, length) = Ignition(v);
        v.Timer = length;
        v.Flags |= VehicleFlags.Started | (VehicleFlags)0x80000000;
        if (sound is not null) Events.Add(new IgnitionStarted(v, sound));
    }

    /// <summary>The original's K when the other object is a car (types 1, 7, 9), times the other's mass: the float 6.673040e-5.</summary>
    internal static readonly float KVehicle = BitConverter.Int32BitsToSingle(0x388bf19c);

    /// <summary>The original's K for everything else, times the object's own mass: the float 1.000956e-4 (not 1e-4).</summary>
    internal static readonly float KOther = BitConverter.Int32BitsToSingle(0x38d1ea6a);

    /// <summary>
    /// the impact amount, trunc(m · d · d · K) with d = (−n)·v. The game stores d as a float,
    /// multiplies on the x87 at double precision (control word 0x027f) and truncates through a 64-bit
    /// fistp, so this does the same.
    /// </summary>
    internal static int ImpactAmount(float m, float k, Vector3 n, Vector3 v)
    {
        float d = (float)((double)-n.X * v.X + (double)v.Z * -n.Z + (double)v.Y * -n.Y);
        return unchecked((int)(long)((double)m * d * d * k));
    }

    /// <summary>
    /// the impact packet: channel 3 only, the other car as the attacker. An impact never
    /// makes the kill roll (the original skips it for mask 8), so however hard the hit and however
    /// damaged the car, a collision by itself cannot destroy it.
    /// </summary>
    private static DamagePacket Impact(object? attacker, float m, float k, Vector3 n, Vector3 impactVel)
    {
        var p = new DamagePacket { Attacker = attacker, Mask = 8, Kind = DamageKind.Collision };
        p.Ch[3] = ImpactAmount(m, k, n, impactVel);
        return p;
    }

    public void TerrainImpact(Vehicle v, Vector3 relVel, Vector3 n, Vector3 cLocal) =>
        VehicleSlot(v, Impact(null, v.P.Mass, KOther, n, relVel), cLocal);

    public void Landing(Vehicle v, Vector3 impactVel, Vector3 n) =>
        VehicleSlot(v, Impact(null, v.P.Mass, KOther, n, impactVel), new Vector3(0, -0.2f, 0));

    public void FallKill(Vehicle v) => Destroy(v);

    /// <summary>The surface's damage per second, carried fractionally, one rand() per call for the zone.</summary>
    public void Rough(Vehicle v, float dt)
    {
        if (IsDead(v) || v.Health is not { } d) return;
        float amount = World.Ground.Surface(v.Surface).Damage * dt * IncomingFactor(v);
        d.RoughCarry += amount;
        int n = (int)d.RoughCarry;
        d.RoughCarry -= n;
        var p = new DamagePacket { Mask = 8, Kind = DamageKind.Terrain };
        p.Ch[3] = n;
        int r = Rand.Next() % 6;
        int zone = r switch { 0 => 0, 1 or 2 => 1, 3 or 4 => 2, _ => 3 };
        int rem = ArmourStep(d, zone, p);
        while (rem != 0) rem = d.ComponentLoop(r, p, false, 100f, Rand, out _, out _, out _);
    }

    /// <summary>
    /// The vehicle's collide callback against scenery: the car
    /// takes trunc(m_self·d²·KOther) unless the node is type 3; the node takes
    /// trunc(m_car·((−n)·v_car)²·KVehicle) through its own callback.
    /// A type-4 node, the root's or one of a structure's, does
    /// the car nothing, and its impact packet knocks its placement off.
    /// </summary>
    public void StaticContact(Vehicle v, StaticObject o, Contact rec)
    {
        if (o.Type == 4 || o.SignNode(rec.Node ?? -1)) { KnockSign(o); return; }
        var impact = -v.Vel;
        if (o.Type != 3) VehicleSlot(v, Impact(null, v.P.Mass, KOther, rec.N, impact), rec.C);
        if (o.Vehicle is not null) return;   // an emplacement has no collide callback: rams do it no harm
        var sp = Impact(v, v.P.Mass, KVehicle, -rec.N, v.Vel);
        StructureSlot(o, rec.Node ?? NearestNode(o, v.Pos.ToVector3() + v.Rot.ToWorld(rec.C)), sp);
    }

    /// <summary>A contact that does not say which node it touched (the modern physics' hull): the live node whose base is nearest.</summary>
    private static int NearestNode(StaticObject o, Vector3 at)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < o.Nodes.Count; i++)
            if (o.Nodes[i] is { Destroyed: false, Base: { } b, Damageable: true } && Vector3.DistanceSquared(b, at) is var d && d < bd) { bd = d; best = i; }
        return best;
    }

    /// <summary>
    /// Car against car: each car's callback damages itself with
    /// the other's mass and KVehicle, from the pre-response velocities, and
    /// records the other as its rammer.
    /// </summary>
    public void CarContact(Vehicle self, Vehicle other, Contact rec)
    {
        if (self.Wreck || other.Wreck) return;
        var p = Impact(other, other.P.Mass, KVehicle, rec.N, other.Vel - self.Vel);
        VehicleSlot(self, p, rec.C);
        if (AiRammedHuman(self, other, rec)) return;
        Record(self, other, DamageKind.Collision, 8);
    }

    /// <summary>
    /// Co-op only (the port's own; docs/deviations.md): an AI car that drove into a human does not name that human its
    /// rammer. The original records each car as the other's rammer whoever moved, so a friend driving
    /// into the one player counts as the player's ram in its friendly-fire routine, and single player keeps that; in co-op
    /// a friend's way through the group's cars is not any player's fault. The AI drove into it when its own speed into
    /// the contact (along the normal) is the greater: the human's car was the slower to close or stood still.
    /// </summary>
    private bool AiRammedHuman(Vehicle self, Vehicle other, Contact rec) =>
        World.Coop && World.IsHuman(other) && !World.IsHuman(self) && Vector3.Dot(self.Vel, -rec.N) > Vector3.Dot(other.Vel, rec.N);

    // ---- structures ----

    /// <summary>
    /// The sign's damage slot (the caller tests the mask): it zeroes
    /// the root's HP, unregisters its collider and detaches the whole placement
    /// as one piece launched at
    /// ((u−0.5)·10, u·20, (u−0.5)·10), whatever the car's speed.
    /// </summary>
    public void KnockSign(StaticObject o)
    {
        if (!o.Knock()) return;
        float x = (U() - 0.5f) * 10f, y = U() * 20f, z = (U() - 0.5f) * 10f;
        Events.Add(new SignKnocked(o, new Vector3(x, y, z)));
    }

    /// <summary>
    /// On the object hit: a node (a round's or a ram's), or the
    /// root (a splash's, a script's). "flag" objects are indestructible, type
    /// 0xc ignores fire, type 2 quarters impacts (once per set channel). A hit
    /// larger than the HP destroys it (one equal to it leaves it at 0); smaller
    /// ones chip it. Measured in the original game: a 50cal round
    /// on a saguaro's arm (5 HP) kills the arm and leaves the root's 400; the
    /// grain hopper's bin (100) takes 50, 50, then dies on the third.
    /// </summary>
    public void StructureSlot(StaticObject o, int node, DamagePacket p)
    {
        var n = node >= 0 && node < o.Nodes.Count ? o.Nodes[node] : null;
        if (n is null) node = -1;
        else if (!n.Damageable) return;      // has no slot for its type (a CTF flag's 0x5a / 0x5b)
        if ((n?.Destroyed ?? o.Destroyed) || (n?.Name ?? o.Name).Contains("flag", StringComparison.OrdinalIgnoreCase)) return;
        uint type = n?.Type ?? o.Type;
        if (type == 0xc && (p.Mask & 4) != 0) { p.Ch[2] = 0; p.Mask &= unchecked((ushort)~4); }
        ref int hp = ref n is null ? ref o.Hp : ref n.Hp;
        for (int i = 0; i < 4; i++)
        {
            if ((p.Mask & (1 << i)) == 0) continue;
            if ((p.Mask & 8) != 0 && type == 2) p.Ch[3] >>= 2;
            if (hp < p.Ch[i])
            {
                var booms = new List<int>();
                Destroy(o, node, booms);
                Events.Add(new StructureDestroyed(o, booms));
                return;
            }
            hp -= p.Ch[i];
        }
    }

    /// <summary>Kept for the callers that hit a whole placement: the root's slot.</summary>
    public void StructureSlot(StaticObject o, DamagePacket p) => StructureSlot(o, -1, p);

    /// <summary>
    /// The script's destroy-object action forces this: an impact of HP + 1 on the root
    /// (which type 2's quartering can survive), then the root's explosion at
    /// its marker whatever that did. The script host never calls it twice.
    /// </summary>
    public void DestroyStructure(StaticObject o)
    {
        var p = new DamagePacket { Mask = 8 };
        p.Ch[3] = o.Hp + 1;
        StructureSlot(o, -1, p);
        var booms = new List<int>();
        RootExplosion(o, booms);
        if (booms.Count > 0) Events.Add(new StructureDestroyed(o, booms));
    }

    /// <summary>
    /// the destruction of what was hit (−1 the root): it and its
    /// subtree marked, each child's subtree destroyed
    /// (last record first, as the engine prepends them), the
    /// root test, and its own explosion and chunks. A root emptied by splash
    /// marks itself first, so no child's root test fires: every node goes,
    /// with no marker explosion (measured: the grain hopper, 16 chunks and
    /// no x1_bldx1).
    /// </summary>
    private void Destroy(StaticObject o, int node, List<int> booms)
    {
        if (node < 0)
        {
            o.Hp = 0;
            o.Destroyed = true;
            foreach (var n in o.Nodes) n.Destroyed = true;
        }
        else
        {
            o.Nodes[node].Hp = 0;
            Mark(o, node);
        }
        for (int c = o.Nodes.Count - 1; c >= 0; c--)
            if (o.Nodes[c].Parent == node) Subtree(o, c, booms);
        if (node >= 0)
        {
            RootTest(o, node, booms);
            Own(o, node, booms);
        }
        else if (o.Nodes.Count == 0)
        {
            // a collider made without its nodes: the whole placement as one
            o.Collapse();
            if (o.Type != 3 && o.Explosion is { } fx) { Combat.Explode(fx, o.Centre, null); booms.Add(-1); }
            for (int i = 0; i < 8; i++) { Rand.Next(); Events.Add(new Debris(o.Centre, new Vector3(0, 15, 0))); }
        }
        o.Refresh();
    }

    private static void Mark(StaticObject o, int node)
    {
        o.Nodes[node].Destroyed = true;
        for (int c = 0; c < o.Nodes.Count; c++) if (o.Nodes[c].Parent == node) Mark(o, c);
    }

    /// <summary>A node's subtree destruction, without the HP test.</summary>
    private void Subtree(StaticObject o, int node, List<int> booms)
    {
        Mark(o, node);
        for (int c = o.Nodes.Count - 1; c >= 0; c--)
            if (o.Nodes[c].Parent == node) Subtree(o, c, booms);
        RootTest(o, node, booms);
        Own(o, node, booms);
    }

    /// <summary>
    /// A node's link is the previous sibling, or the parent for a
    /// first child, so the walk ends at the parent: the root goes when a
    /// direct child of it dies and it is not already down (measured: the
    /// hopper's CR1_LEG1, the root's second child, does it; CR1_BIN1 below it
    /// does not). Then the root's explosion at its marker.
    /// </summary>
    private void RootTest(StaticObject o, int node, List<int> booms)
    {
        if (o.Nodes[node].Parent != -1 || o.Destroyed) return;
        o.Destroyed = true;
        RootExplosion(o, booms);
    }

    /// <summary>The root's explosion block: the first 0x1000000 node's, unless the root is type 3.</summary>
    private void RootExplosion(StaticObject o, List<int> booms)
    {
        if (o.Marker is not { } at || o.Explosion is not { } fx || o.Type == 3) return;
        Combat.Explode(fx, at, null);
        booms.Add(-1);
    }

    /// <summary>A destroyed node's own explosion (its flags', not for type 3) and 8 chunks at its base, one rand() each; nothing without geometry.</summary>
    private void Own(StaticObject o, int node, List<int> booms)
    {
        var n = o.Nodes[node];
        if (!n.Damageable || n.Base is not { } at) return;
        if (n.Explosion is { } fx) { Combat.Explode(fx, at, null); booms.Add(node); }
        for (int i = 0; i < 8; i++) { Rand.Next(); Events.Add(new Debris(at, new Vector3(0, 15, 0))); }
    }
}
