using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// Co-op Scaling (the port's; docs/deviations.md, docs/multiplayer.md "Co-op Scaling"): with N humans in the mission, each enemy
/// car the script sets going is joined by up to min(N − 1, <see cref="CoopSetup.ExtraEnemies"/>) copies of it beside it. A copy
/// takes every order and setting the script gives its original, aimed at the human nearest to the copy; once the original is
/// dead it attacks the nearest human until it is destroyed. The script never sees the copies: they have no instance id, are
/// left out of its vehicle lists (allEnemyDead, nearestEnemy, isWithinEnemy) and hold no actor slot, so its counters and
/// objectives follow the originals alone.
/// </summary>
public sealed partial class ScriptHost
{
    /// <summary>The copies of an original (living or not), in the order they were made.</summary>
    public IEnumerable<Entity> ExtrasOf(Entity original) => Entities.Where(x => x.ExtraOf == original && !x.Removed);

    /// <summary>The entity and, for an original under Co-op Scaling, its living copies: what a setting the script gives it applies to.</summary>
    private IEnumerable<Entity> WithExtras(object o)
    {
        var e = E(o);
        yield return e;
        if (Coop is not { ExtraEnemies: > 0 } || e.ExtraOf is not null) yield break;
        foreach (var x in ExtrasOf(e).ToList()) if (!x.Dead) yield return x;
    }

    /// <summary>A car Co-op Scaling copies: an AI ground car (no helicopter or gun emplacement) on a team neither the humans' nor nobody's.</summary>
    private bool Scalable(Entity e) =>
        e.Vehicle is { IsHeli: false, IsEmplacement: false } && e.ExtraOf is null && !IsHumanEntity(e) && e.Team != 0 && Lead is { } lead && e.Team != lead.Team;

    /// <summary>
    /// An order to an original reaches its copies: made the first time the script orders it (as many as the humans then present
    /// call for), then each living copy given the same order, a human target or reference taken as the human nearest the copy.
    /// </summary>
    private void OrderExtras(Entity e, Entity? target, AiOrder order, IReadOnlyList<System.Numerics.Vector3>? path, float speed, int followFlag, float f1, float f2, object? reference)
    {
        if (Coop is not { ExtraEnemies: > 0 } c || e.ExtraOf is not null) return;
        if (!e.Scaled)
        {
            e.Scaled = true;
            if (!Scalable(e)) return;
            int n = System.Math.Min(c.Seats.Count(s => s.Present) - 1, c.ExtraEnemies);
            for (int k = 0; k < n; k++) SpawnExtra(c, e, k + 1);
        }
        foreach (var x in ExtrasOf(e).ToList())
            if (!x.Dead)
                Order(x, Aimed(target, x), order, path, speed, followFlag, f1, f2, reference is Entity r ? Aimed(r, x) : reference);
    }

    /// <summary>A human the original was aimed at is the human nearest the copy (with its hysteresis); anyone else stays.</summary>
    private Entity? Aimed(Entity? target, Entity copy) => target is not null && IsHumanEntity(target) ? NearestHuman(copy) as Entity : target;

    /// <summary>A copy of the original's car beside it, facing and moving as it does, its AI settings the original's.</summary>
    private void SpawnExtra(CoopGroup c, Entity e, int k)
    {
        var o = e.Vehicle!;
        if (c.BuildEnemy is null || o.Definition is not { Length: > 0 } def) return;
        var v = c.BuildEnemy(def);
        v.Paint = o.Paint;
        v.Rot = o.Rot;
        v.Pos = SpawnSpace.Beside(World, o, v, c.Paths);
        v.Vel = o.Vel;
        v.Speed = o.Speed;
        v.Flags = (v.Flags & ~VehicleFlags.Lights) | (o.Flags & VehicleFlags.Lights);
        World.Vehicles.Add(v);
        var x = new Entity(e.Name, [], e.Type, e.ObjectFlags) { Vehicle = v, ExtraOf = e, Scaled = true };
        Add(x);
        SetSkill(x, e.SkillA, e.SkillB);
        x.Aggression = e.Aggression; x.Avoid = e.Avoid; x.MaxAttackers = e.MaxAttackers; x.Lights = e.Lights; x.Siren = e.Siren;
        Log($"co-op: {e.Name} has extra {k} beside it");
        c.RaiseSpawned(v, def);
    }

    /// <summary>The script teleported an original: its living copies come along beside it.</summary>
    private void TeleportExtras(Entity e)
    {
        if (Coop is not { ExtraEnemies: > 0 } c || e.ExtraOf is not null || e.Vehicle is not { } o) return;
        foreach (var x in ExtrasOf(e).ToList())
            if (!x.Dead && x.Vehicle is { } v)
            {
                v.Rot = o.Rot;
                v.Pos = SpawnSpace.Beside(World, o, v, c.Paths);
                v.Vel = o.Vel; v.Speed = o.Speed; v.AngVel = System.Numerics.Vector3.Zero;
                v.Steer = v.Throttle = 0;
            }
    }

    /// <summary>
    /// Co-op Scaling's frame, after the script's: a living copy whose original is dead (or gone) attacks the human nearest it, as
    /// its original's orders no longer come.
    /// </summary>
    internal void ExtrasFrame()
    {
        if (Coop is not { ExtraEnemies: > 0 }) return;
        foreach (var x in Entities.Where(x => x.ExtraOf is { } o && (o.Dead || o.Removed) && !x.Dead && !x.Removed).ToList())
            if (NearestHuman(x) is Entity h) Order(x, h, AiOrder.Attack, null, 0);
    }
}
