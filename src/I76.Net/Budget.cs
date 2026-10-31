namespace I76.Net;

/// <summary>
/// One client's snapshot budget (docs/multiplayer.md, "The snapshot budget"): a priority accumulator per car and round,
/// grown each tick by how much the client needs it (<see cref="Interest"/>'s rate for its distance and view, how far its
/// last state has drifted, a change of its looks) and reset when it is sent. Each snapshot carries, within its byte budget
/// (one datagram, or the client's share of the host's upload), the essential entries (the own car, removals, a respawn,
/// anything not sent for twice <see cref="MaxAgeSeconds"/>), then what has waited <see cref="MaxAgeSeconds"/>, oldest
/// first, then the due ones by priority; the rest wait,
/// their accumulators still growing, so they rank higher next tick.
/// </summary>
public sealed class SnapshotPlanner
{
    /// <summary>A car or round not sent for this long goes before anything only due; for twice this, whatever the budget: the bound on how stale anything gets.</summary>
    public const float MaxAgeSeconds = 1f;

    private readonly Dictionary<int, (float Acc, uint Sent)> _cars = [], _rounds = [];
    private uint _swept;

    /// <summary>Entries due but not carried for want of room, all told (the load test's measure).</summary>
    public long Deferred;

    /// <summary>
    /// Grows the accumulator of a car or round by <paramref name="growth"/> (1 a tick: due every tick) and returns it, with
    /// the ticks since it was last sent. A new one starts at a fraction set by its serial, so those of one rate do not all
    /// fall due in the same tick.
    /// </summary>
    public float Grow(bool car, int serial, float growth, uint tick, out uint age)
    {
        var d = car ? _cars : _rounds;
        if (!d.TryGetValue(serial, out var a)) a = (Stagger(serial), tick);
        a.Acc += growth;
        d[serial] = a;
        age = tick - a.Sent;
        return a.Acc;
    }

    /// <summary>The golden-ratio fraction of a serial: neighbouring serials spread evenly over [0, 1).</summary>
    public static float Stagger(int serial) => (float)(serial * 0.6180339887498949 % 1.0);

    /// <summary>The snapshot went: the entries it carried start again from nothing.</summary>
    public void Sent(EntryPlan plan, uint tick)
    {
        var d = plan.Car ? _cars : _rounds;
        foreach (var e in plan.Entries)
            if (e.Selected) d[e.Serial] = (0, tick);
            else if (e.Sendable) Deferred++;
    }

    /// <summary>Forgets cars and rounds no longer in the game (now and then).</summary>
    public void Sweep(uint tick, IReadOnlyList<(int Serial, EntityRec Rec)> cars, IReadOnlyList<(int Serial, EntityRec Rec)> rounds)
    {
        if (tick - _swept < 64) return;
        _swept = tick;
        Drop(_cars, cars);
        Drop(_rounds, rounds);
        static void Drop(Dictionary<int, (float, uint)> d, IReadOnlyList<(int Serial, EntityRec Rec)> live)
        {
            if (d.Count <= live.Count) return;
            var keep = new HashSet<int>(live.Select(x => x.Serial));
            foreach (var k in d.Keys.Where(k => !keep.Contains(k)).ToList()) d.Remove(k);
        }
    }

    /// <summary>
    /// Picks what a snapshot carries of the cars and rounds within <paramref name="budget"/> bytes (the counts and serial
    /// gaps included): every essential entry, then the others by priority while they fit. Returns the bytes the picked
    /// entries take (more than the budget only when the essential ones alone are).
    /// </summary>
    public static int Select(EntryPlan cars, EntryPlan rounds, int budget)
    {
        // everything that may go, and whether it all fits as it is (the common case below the most players)
        int all = 0, n = 0;
        foreach (var plan in new[] { cars, rounds })
            foreach (var e in plan.Entries)
                if (e.Sendable) { all += e.Length + 2; if (!e.Essential) n++; }
        if (all + 10 <= budget)
        {
            foreach (var plan in new[] { cars, rounds })
            {
                var l = plan.Entries;
                for (int i = 0; i < l.Count; i++) if (l[i].Sendable) l[i] = l[i] with { Selected = true };
            }
            return SnapshotCodec.Size(cars) + SnapshotCodec.Size(rounds);
        }
        var order = new Pick[n];
        int k = 0;
        foreach (var plan in new[] { cars, rounds })
        {
            var l = plan.Entries;
            for (int i = 0; i < l.Count; i++)
            {
                if (!l[i].Sendable) continue;
                if (l[i].Essential) l[i] = l[i] with { Selected = true };
                else order[k++] = new Pick(l[i].Priority, l[i].Serial, plan == cars, i, l[i].Length);
            }
        }
        int used = SnapshotCodec.Size(cars) + SnapshotCodec.Size(rounds);
        // the most needed first; between equals the lower serial, for the same choice on every run
        Array.Sort(order, static (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Serial.CompareTo(b.Serial));
        int picked = 0;
        for (int j = 0; j < order.Length; j++)
        {
            var o = order[j];
            // the serial's gap is a byte or two: counted as one here, exactly below
            if (used + o.Length + 1 > budget) continue;
            var l = (o.Car ? cars : rounds).Entries;
            l[o.Index] = l[o.Index] with { Selected = true };
            used += o.Length + 1;
            order[picked++] = o;
        }
        used = SnapshotCodec.Size(cars) + SnapshotCodec.Size(rounds);
        // over by the gaps' second bytes: the least needed of those picked wait
        for (int j = picked - 1; j >= 0 && used > budget; j--)
        {
            var o = order[j];
            var l = (o.Car ? cars : rounds).Entries;
            l[o.Index] = l[o.Index] with { Selected = false };
            used -= o.Length + 1;
            if (used <= budget) used = SnapshotCodec.Size(cars) + SnapshotCodec.Size(rounds);
        }
        return used;
    }

    private readonly record struct Pick(float Priority, int Serial, bool Car, int Index, int Length);
}
