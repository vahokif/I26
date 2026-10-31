using System.Numerics;
using I76.Sim.Session;

namespace I76.Net;

/// <summary>
/// Interest management (docs/multiplayer.md, "Bandwidth"): how often a client is sent each other car. Only what a client
/// is sent changes; the server's game is the same for everyone, and every car stays in every client's mirror (the radar
/// and the target box read it), only with an older state while it is far.
/// <list type="bullet">
/// <item>Every tick: a car within <see cref="NearMetres"/>, or within <see cref="ViewMetres"/> ahead of the client's car
/// (±60°), the client's radar target, a car targeting the client (its radar target, or an AI attacking it), the own car.</item>
/// <item>Every <see cref="HalfRateSeconds"/>: the rest within <see cref="RadarMetres"/> (the radar's long range, and
/// where the original drew its last car).</item>
/// <item>Every <see cref="LowRateSeconds"/> and without its wheels: beyond.</item>
/// </list>
/// A car's ticks are staggered by its serial, so the far ones do not all come in the same snapshot. Rounds are sent every
/// tick within <see cref="RoundMetres"/> (and the client's own, wherever), else at the low rate.
/// </summary>
public static class Interest
{
    public const float NearMetres = 200, ViewMetres = 600, ViewCos = 0.5f, RadarMetres = 600, RoundMetres = 800;
    /// <summary>Rounds farther than this from the client's car are not sent at all (past the radar's range, where the original drew nothing).</summary>
    public const float RoundCullMetres = 650;
    public const float HalfRateSeconds = 0.1f, LowRateSeconds = 0.2f;

    public enum Tier : byte { Full, Half, Low }

    /// <summary>The ticks between a tier's updates at this rate (2 and 4 at 20 Hz, 6 and 12 at 60).</summary>
    public static int Interval(Tier t, SimRate rate) => t switch
    {
        Tier.Full => 1,
        Tier.Half => Ticks(HalfRateSeconds, rate),
        _ => Ticks(LowRateSeconds, rate),
    };

    private static int Ticks(float seconds, SimRate rate) => Math.Max(1, (int)MathF.Round(seconds / rate.TickSeconds));

    /// <param name="related">The car is the client's own, its target, or targets it.</param>
    public static Tier Of(Vector3 viewer, Vector3 viewerForward, Vector3 car, bool related)
    {
        if (related) return Tier.Full;
        var d = car - viewer;
        float dist = d.Length();
        if (dist < NearMetres) return Tier.Full;
        if (dist < ViewMetres && Vector3.Dot(d, viewerForward) > ViewCos * dist) return Tier.Full;
        return dist < RadarMetres ? Tier.Half : Tier.Low;
    }

    /// <summary>A car's priority growth a tick at its tier (<see cref="SnapshotPlanner"/>): due every <see cref="Interval"/> ticks.</summary>
    public static float Growth(Tier t, SimRate rate) => 1f / Interval(t, rate);

    /// <summary>A round's growth a tick by its distance: every tick within 200 m, every 0.1 s within 400 m, every 0.2 s beyond.</summary>
    public static float RoundGrowth(float distance, SimRate rate) =>
        distance < NearMetres ? 1f : 1f / Interval(distance < 2 * NearMetres ? Tier.Half : Tier.Low, rate);

    /// <summary>
    /// How far (metres) a client's copy of a car (<paramref name="b"/>, moved on along its velocity for at most
    /// <see cref="CarTrack.ExtrapolateSeconds"/>, as the client draws it) is from where the car is (<paramref name="cur"/>).
    /// </summary>
    public static float Drift(EntityRec b, EntityRec cur, uint tick, float tickSeconds)
    {
        double dt = Math.Min((long)tick - b.SampleTick, CarTrack.ExtrapolateSeconds / tickSeconds) * tickSeconds;
        double sum = 0;
        for (int i = 0; i < 3; i++)
        {
            double p = b.Hot[CarState.PosChannel + i] / CarState.PosScale + b.Hot[CarState.VelChannel + i] / CarState.VelScale * Math.Max(dt, 0);
            double e = cur.Hot[CarState.PosChannel + i] / CarState.PosScale - p;
            sum += e * e;
        }
        return (float)Math.Sqrt(sum);
    }

    /// <summary>What goes of a car of this tier this tick.</summary>
    public static CarSend Send(Tier t, int serial, uint tick, SimRate rate)
    {
        int n = Interval(t, rate);
        if (n > 1 && (tick + (uint)serial) % (uint)n != 0) return CarSend.Skip;
        return t == Tier.Low ? CarSend.Coarse : CarSend.Full;
    }

    /// <summary>Whether a round goes this tick.</summary>
    public static bool SendRound(Vector3 viewer, Vector3 round, bool own, int serial, uint tick, SimRate rate) =>
        own || Vector3.DistanceSquared(viewer, round) < RoundMetres * RoundMetres || (tick + (uint)serial) % (uint)Interval(Tier.Low, rate) == 0;
}

/// <summary>
/// Which of a tick's events a client is sent (docs/multiplayer.md, "Events at scale"). What changes the game for the
/// client (damage, deaths, cars coming and going, structures, players, scores, chat) goes to everyone. What is only seen
/// and heard goes to a client within its range of it: shots, impacts, debris and parts flung within
/// <see cref="CosmeticMetres"/> (the radar's long range, where the original drew its last car), collision and click
/// sounds within <see cref="SoundMetres"/> (a car's sounds are silent past 300 m), explosions within
/// <see cref="ExplosionMetres"/>; a misfire only to its own driver; the client's own car's always.
/// </summary>
public static class EventInterest
{
    public const float CosmeticMetres = 600, SoundMetres = 400, ExplosionMetres = 2000;

    /// <summary>The car an event is about, if only seen and heard near it (0: none).</summary>
    public static int CarOf(NetEvent e) => e switch
    {
        ShotEvent s => s.Car, ClickEvent c => c.Car, SoundEvent s => s.Car, FlungEvent f => f.Car, MisfireEvent m => m.Car, _ => 0,
    };

    /// <summary>
    /// Whether a client whose car is <paramref name="own"/> at <paramref name="viewer"/> is sent the event;
    /// <paramref name="carAt"/> finds a car's position (null: not in the arena).
    /// </summary>
    public static bool Relevant(NetEvent e, Vector3 viewer, int own, Func<int, Vector3?> carAt)
    {
        int car = CarOf(e);
        if (car != 0 && car == own) return true;
        (Vector3? at, float range) = e switch
        {
            ShotEvent s => (s.Muzzle, CosmeticMetres),
            ImpactEvent i => (i.At, CosmeticMetres),
            DebrisEvent d => (d.At, CosmeticMetres),
            FlungEvent f => (carAt(f.Car), CosmeticMetres),
            ExplosionEvent x => (x.At, ExplosionMetres),
            ClickEvent c => (carAt(c.Car), SoundMetres),
            SoundEvent so => (carAt(so.Car), SoundMetres),
            MisfireEvent => ((Vector3?)null, 0f),
            _ => ((Vector3?)null, float.PositiveInfinity),
        };
        if (float.IsPositiveInfinity(range)) return true;
        return at is { } p && Vector3.DistanceSquared(p, viewer) < range * range;
    }
}
