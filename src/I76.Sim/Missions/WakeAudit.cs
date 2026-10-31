using I76.Sim.Fsm;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Missions;

/// <summary>
/// A check of what the scripts imply about their sleeping cars (i76 wakecheck, issue #43). A car is asleep while its
/// standing order is none, sit or hide (hide also switches its engine off, which takes it off the radar). Fed once a tick, it records any car that drives before the script first wakes it, fires while asleep,
/// or (hidden) is a radar contact anyway, and each car's first waking order and first shot. A sitting car with its engine on is a radar contact in the
/// original too (measured: S02's enemies at 433 m before the cut-scene), so that is not a finding.
/// </summary>
public sealed class WakeAudit(MissionSetup setup)
{
    public sealed class Car(Entity e)
    {
        public Entity Entity { get; } = e;
        public float? Woke, FirstShot, MovedAsleep, FiredAsleep, LiveHidden;
        public AiOrder? WokeBy;
        public string? MovedOrder;
        internal float MoveRun, LiveRun;
    }

    public List<Car> Cars { get; } = setup.Entities
        .Where(e => e.Vehicle is { IsEmplacement: false } && e.Vehicle != setup.World.Player).Select(e => new Car(e)).ToList();

    public static bool Asleep(AiOrder? o) => o is null or AiOrder.Sit or AiOrder.Hide;

    /// <summary>One tick: after the session's tick, with its shots still in the combat's event list.</summary>
    public void Tick(float now, float dt)
    {
        foreach (var c in Cars)
        {
            var e = c.Entity;
            var v = e.Vehicle!;
            if (e.Removed || e.Dead) continue;
            bool asleep = Asleep(e.Order);
            if (!asleep && c.Woke is null) { c.Woke = now; c.WokeBy = e.Order; }
            // before the script first wakes it (a car put back to sit brakes from speed): a push from another car can move it a
            // little, so a sustained second above 2 u/s is the finding
            // and under its own throttle: a car no script has ordered rolls down a slope in the original too (T06's middle cop)
            c.MoveRun = asleep && c.Woke is null && v.Speed > 2f && MathF.Abs(v.Throttle) > 0.05f ? c.MoveRun + dt : 0;
            if (c.MoveRun >= 1f && c.MovedAsleep is null) { c.MovedAsleep = now; c.MovedOrder = e.Order?.ToString() ?? "none"; }
            c.LiveRun = e.Order == AiOrder.Hide && !v.IsHeli && Combat.IsLive(v) ? c.LiveRun + dt : 0;
            if (c.LiveRun >= 0.5f && c.LiveHidden is null) c.LiveHidden = now;
        }
        foreach (var s in setup.Combat.Events.OfType<ShotFired>())
            if (Cars.FirstOrDefault(c => c.Entity.Vehicle == s.Shooter) is { } c)
            {
                c.FirstShot ??= now;
                if (Asleep(c.Entity.Order) && c.FiredAsleep is null) c.FiredAsleep = now;
            }
    }

    public IEnumerable<Car> Findings => Cars.Where(c => c.MovedAsleep is not null || c.FiredAsleep is not null || c.LiveHidden is not null);
}
