using I76.Sim.Rng;
using I76.Sim.Weapons;

namespace I76.Sim.Vehicles;

/// <summary>
/// A gun emplacement's block (object type 8, "artillery"):
/// one fixed 600-HP pool, and the FSM's
/// switch and rate for the fire pass. The emplacement's creation.
/// </summary>
public sealed class EmplacementState
{
    public int Hp = 600;
    public int MaxHp = 600;
    public float InvMax = 1f / 600f;
    /// <summary>setArtillery: whether the fire pass tries it (1 from the ctor).</summary>
    public int Enabled = 1;
    /// <summary>setArtilleryRate: 1 asks the fire decision every frame, n one frame in 2n − 2 (5 from the ctor).</summary>
    public int Rate = 5;
    /// <summary>Its placement's collider (type 8 collides as a type-2 building: per-node boxes, on the static list).</summary>
    public Collision.StaticObject? Collider;

    /// <summary>The health percentage, type 8: hp·(1/max)·100 while max ≥ 1, else 100.</summary>
    public float HealthPercent => MaxHp >= 1 ? Hp * InvMax * 100f : 100f;
}

/// <summary>
/// The emplacements' per-frame work: the
/// radar's type-8 branch (the AI's radar-target update), which keeps every
/// live gun's turret on the player, and the fire pass (the emplacements' fire pass), which asks the AI's fire decision at skill and accuracy 1
/// whether the first weapon fires at the player. Both are single-player only.
/// </summary>
public static class Emplacements
{
    /// <summary>
    /// The AI's radar-target update, type 8: the player while the gun lives and the
    /// player's engine runs and it is not jamming; else none.
    /// </summary>
    public static Vehicle? RadarTarget(Vehicle gun, Vehicle? player, bool playerJams = false) =>
        !Combat.IsDead(gun) && player is not null && !playerJams && (player.Flags & VehicleFlags.EngineOn) != 0 ? player : null;

    /// <summary>The fire pass's rate gate: rate 1 always; else rand() % (2·rate − 2) == 0 (C's %, so rate 0 is one in two).</summary>
    public static bool RateGate(int rate, CrtRand rand) => rate == 1 || rand.Next() % (2 * rate - 2) == 0;

    /// <summary>The weapon the pass fires (the carrier's first): the first loaded.</summary>
    public static Weapon? Gun(Vehicle v) => v.Armory?.LoadOrder.FirstOrDefault();

    /// <summary>
    /// After the FSM, before the weapons (the original's start-up): the radar
    /// targets, then each live, enabled gun in load order through the rate
    /// gate; a yes pulls its trigger.
    /// </summary>
    public static void Frame(SimWorld world, Combat combat, bool network)
    {
        if (network) return;
        if (world.Coop) { CoopFrame(world, combat); return; }
        var player = world.Player;
        foreach (var v in world.Vehicles)
            if (v.Emplacement is not null) combat.RadarTarget[v] = RadarTarget(v, player, combat.IsJamming(player));
        if (player is null) return;
        foreach (var v in world.Vehicles)
        {
            if (v.Emplacement is not { } e || Combat.IsDead(v) || e.Enabled == 0 || Gun(v) is not { } w) continue;
            if (RateGate(e.Rate, world.Rand) && combat.AiShouldFire(v, player, w, 1f, 1f)) w.Trigger = true;
        }
    }

    /// <summary>A gun keeps the human it holds until another is nearer by this factor of distance.</summary>
    public const float Hysteresis = 1.25f;

    /// <summary>
    /// The frame in a co-op mission (docs/multiplayer.md "Co-op"): each gun's radar holds the nearest human it may see
    /// (engine running, not jamming), keeping the one it holds unless another is <see cref="Hysteresis"/> times nearer;
    /// the fire pass asks the decision at that human, or with none in sight at the nearest living one (as the
    /// original asks it at the player whatever its radar holds). The rate gate draws as the original's.
    /// </summary>
    private static void CoopFrame(SimWorld world, Combat combat)
    {
        var humans = new List<Vehicle>();
        if (world.Player is { } p) humans.Add(p);
        foreach (var h in world.Humans) if (h != world.Player) humans.Add(h);
        var aim = new Dictionary<Vehicle, Vehicle?>();
        foreach (var v in world.Vehicles)
            if (v.Emplacement is not null)
            {
                var seen = Nearest(v, humans.Where(h => RadarTarget(v, h, combat.IsJamming(h)) is not null), combat.RadarTarget.GetValueOrDefault(v));
                combat.RadarTarget[v] = seen;
                aim[v] = seen ?? Nearest(v, humans.Where(h => !Combat.IsDead(h)), null) ?? world.Player;
            }
        if (humans.Count == 0) return;
        foreach (var v in world.Vehicles)
        {
            if (v.Emplacement is not { } e || Combat.IsDead(v) || e.Enabled == 0 || Gun(v) is not { } w || aim.GetValueOrDefault(v) is not { } target) continue;
            if (RateGate(e.Rate, world.Rand) && combat.AiShouldFire(v, target, w, 1f, 1f)) w.Trigger = true;
        }
    }

    /// <summary>The nearest of <paramref name="cands"/> to the gun (3D), <paramref name="held"/> kept unless another is clearly nearer.</summary>
    private static Vehicle? Nearest(Vehicle gun, IEnumerable<Vehicle> cands, Vehicle? held)
    {
        var at = gun.Pos.ToVector3();
        Vehicle? best = null; float bd = float.MaxValue, hd = -1;
        foreach (var h in cands)
        {
            float d = (h.Pos.ToVector3() - at).LengthSquared();
            if (h == held) hd = d;
            if (d < bd) { bd = d; best = h; }
        }
        return hd >= 0 && hd <= bd * Hysteresis * Hysteresis ? held : best;
    }
}
