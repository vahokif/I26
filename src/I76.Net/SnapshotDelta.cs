namespace I76.Net;

/// <summary>
/// A car or round as a client has it after a snapshot: its hot channels (ints, <see cref="CarState.Hot"/>), its cold
/// bytes (<see cref="CarState.WriteCold"/>) and the server tick they are of. Never changed once made, so frames share them.
/// </summary>
public sealed record EntityRec(int[] Hot, byte[] Cold, uint SampleTick);

/// <summary>
/// Everything a client has after one snapshot (protocol 13's delta snapshots): the own car's bytes, its motion's (the
/// fast part and the latest slow part), and every car and round it knows. The server keeps the frames it sent each
/// client and the client the frames it decoded; a snapshot is written against the frame of the newest tick the client
/// acknowledged, so both ends hold the same baseline.
/// </summary>
public sealed class SnapFrame
{
    public uint Tick;
    public byte[] Own = [], Motion = [], Slow = [], Fire = [];
    public Dictionary<int, EntityRec> Cars { get; } = [];
    public Dictionary<int, EntityRec> Rounds { get; } = [];
}

/// <summary>What a snapshot does with a car or round this tick (<see cref="Interest"/>, <see cref="SnapshotPlanner"/>).</summary>
public enum CarSend : byte
{
    /// <summary>Not this tick: the client keeps what it has.</summary>
    Skip,
    /// <summary>Everything that changed.</summary>
    Full,
    /// <summary>
    /// A far car: everything that changed but the wheels and the driving values (its suspension cannot be seen, its
    /// engine not heard), at a coarser precision (<see cref="SnapshotCodec.CoarseUnits"/>).
    /// </summary>
    Coarse,
    /// <summary>Not sent at all: the client forgets it (a round too far to be seen); it comes again, new, when near.</summary>
    Cull,
}

/// <summary>One car's or round's entry as a snapshot may carry it (<see cref="EntryPlan"/>).</summary>
public struct Entry
{
    public int Serial;
    /// <summary>The entry's bytes after its serial (control, groups, cold) in the plan's buffer; none when it cannot go this tick.</summary>
    public int Start, Length;
    /// <summary>What the client has of it if this entry goes (null: removed), and if not (null: nothing).</summary>
    public EntityRec? Rec, Base;
    /// <summary>Higher goes first when a snapshot cannot carry every entry; an essential one always goes.</summary>
    public float Priority;
    public bool Essential, Selected;
    public readonly bool Sendable => Length > 0;
}

/// <summary>
/// The cars' or the rounds' possible entries of one client's snapshot, in serial order, each encoded once against the
/// client's baseline: the snapshot then carries those its budget picks (<see cref="SnapshotPlanner.Select"/>) and the
/// client keeps the baseline's state of the rest.
/// </summary>
public sealed class EntryPlan
{
    public readonly List<Entry> Entries = [];
    public readonly NetWriter Bytes = new(4096);
    public bool Car;
    public void Clear() { Entries.Clear(); Bytes.Clear(); }
    public ReadOnlySpan<byte> Body(in Entry e) => Bytes.Span.Slice(e.Start, e.Length);
}

/// <summary>
/// The snapshot's encoding (docs/multiplayer.md, "The wire"). A snapshot names its baseline (ticks back; 0 for none,
/// a full snapshot). The own car and its motion go as <see cref="XorDelta"/>s of their bytes, exact. Each car is an
/// entry: its serial (as the gap from the last), a control varint (bit 0: removed; then a bit per channel group that
/// changed), then the groups' changes as zigzag varints; the position as the difference from where the baseline's
/// velocity (the mean of the old and new) puts it, the cold bytes as a byte delta. The own weapons' fire control
/// (<see cref="OwnFire"/>) is a byte delta too; the CTF flags and the flame streams go whole. Rounds the same, their position from
/// the distance they travelled along their heading; a new round's baseline is its shooter's car in the same snapshot
/// (its cold bytes, which name the shooter, then come first). A car the snapshot does not list is as in the baseline, and has no
/// new state this tick (interest management); a round it does not list is as in the baseline. Against no baseline every entry is new and the whole state goes.
/// </summary>
public static class SnapshotCodec
{
    private const int CarCold = 6, RoundCold = 3, MaxDt = 64;
    private static readonly int[] CarZero = new int[CarState.HotCount], RoundZero = new int[RoundState.HotCount];

    /// <param name="confirm">
    /// The motion is the one the client hashed for the acked input (<see cref="OwnMotion.Hash"/>, its low 16 bits here):
    /// only that is sent, not the motion (protocol 13). Against a baseline only; the frame keeps the baseline's motion bytes.
    /// </param>
    public static void WriteHead(NetWriter w, uint tick, uint acked, sbyte slack, SnapFrame? b, byte[]? own, byte[]? motion, byte[]? slow, SnapFrame into,
        byte[]? fire = null, ushort? confirm = null)
    {
        into.Tick = tick;
        w.U8((byte)MsgType.Snapshot);
        w.U32(tick);
        w.U32(acked);
        w.I8(slack);
        w.VarU(b is null ? 0 : tick - b.Tick);
        bool confirmed = confirm is not null && b is not null && motion is not null;
        bool sendSlow = slow is not null && motion is not null && (b is null || !slow.AsSpan().SequenceEqual(b.Slow));
        w.U8((byte)((own is not null ? 1 : 0) | (motion is not null && !confirmed ? 2 : 0) | (sendSlow ? 4 : 0) | (fire is not null ? 8 : 0) | (confirmed ? 16 : 0)));
        if (own is not null) XorDelta.Write(w, own, b?.Own ?? []);
        if (confirmed) w.U16(confirm!.Value);
        else if (motion is not null) XorDelta.Write(w, motion, b?.Motion ?? []);
        if (sendSlow) XorDelta.Write(w, slow, b?.Slow ?? []);
        if (fire is not null) XorDelta.Write(w, fire, b?.Fire ?? []);
        into.Own = own ?? [];
        into.Fire = fire ?? [];
        into.Motion = confirmed ? b!.Motion : motion ?? [];
        into.Slow = sendSlow ? slow! : motion is null ? [] : b?.Slow ?? [];
    }

    /// <summary>The cars, sorted by serial, against the baseline; <paramref name="send"/> says what goes of each (null: everything).</summary>
    public static void WriteCars(NetWriter w, uint tick, float tickSeconds, SnapFrame? b, IReadOnlyList<(int Serial, EntityRec Rec)> cars, Func<int, CarSend>? send, SnapFrame into)
    {
        var plan = new EntryPlan();
        // (a car the client does not have goes whatever send says)
        Plan(plan, tick, tickSeconds, b, cars, true, send is null ? null : x => (send(x) is CarSend.Skip && b?.Cars.ContainsKey(x) != true ? CarSend.Full : send(x), 0, false));
        SelectAll(plan);
        Emit(w, plan, into.Cars);
    }

    public static void WriteRounds(NetWriter w, uint tick, float tickSeconds, SnapFrame? b, IReadOnlyList<(int Serial, EntityRec Rec)> rounds, Func<int, CarSend>? send, SnapFrame into)
    {
        var plan = new EntryPlan();
        Plan(plan, tick, tickSeconds, b, rounds, false, send is null ? null : x => (send(x) is CarSend.Skip && b?.Rounds.ContainsKey(x) != true ? CarSend.Full : send(x), 0, false));
        SelectAll(plan);
        Emit(w, plan, into.Rounds);
    }

    private static void SelectAll(EntryPlan plan)
    {
        var l = plan.Entries;
        for (int i = 0; i < l.Count; i++) if (l[i].Sendable) l[i] = l[i] with { Selected = true };
    }

    /// <summary>
    /// The coarse precision of a far car's channels (<see cref="CarSend.Coarse"/>), in units of the full precision:
    /// position 1/32 m, rotation 32 steps of the smallest three (0.16°), velocity 1/16 m/s, angular velocity 1/64 rad/s. A
    /// coarse entry's control has bit 0 set with its groups (alone, bit 0 is a removal).
    /// </summary>
    public static readonly int[] CoarseUnits = [32, 32, 32, 1, 32, 32, 32, 16, 16, 16, 16, 16, 16, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    /// <summary>
    /// Each car's or round's possible entry against the baseline, in serial order with those gone from it (removals);
    /// <paramref name="how"/> says what may go of each and how it ranks (null: everything, as much as changed). A car
    /// not due (Skip) or a round unchanged has no entry to send: the client keeps the baseline's.
    /// </summary>
    public static void Plan(EntryPlan plan, uint tick, float ts, SnapFrame? b, IReadOnlyList<(int Serial, EntityRec Rec)> cur, bool car,
        Func<int, (CarSend Mode, float Priority, bool Essential)>? how)
    {
        plan.Clear();
        plan.Car = car;
        var baseline = car ? b?.Cars : b?.Rounds;
        var e = plan.Bytes;
        // the baseline's entries that are gone, merged in serial order
        List<int>? gone = null;
        if (baseline is not null)
        {
            int kept = 0;
            foreach (var (serial, _) in cur) if (baseline.ContainsKey(serial)) kept++;
            if (kept < baseline.Count) gone = [.. baseline.Keys.Where(k => !Has(cur, k)).Order()];
        }
        int gi = 0;
        void Gone(int serial, EntityRec b0)
        {
            int at = e.Length;
            e.VarU(1);
            plan.Entries.Add(new Entry { Serial = serial, Start = at, Length = e.Length - at, Rec = null, Base = b0, Essential = true });
        }
        foreach (var (serial, rec) in cur)
        {
            while (gone is not null && gi < gone.Count && gone[gi] < serial) { Gone(gone[gi], baseline![gone[gi]]); gi++; }
            var b0 = baseline?.GetValueOrDefault(serial);
            var (mode, priority, essential) = how?.Invoke(serial) ?? (CarSend.Full, 0, false);
            if (mode == CarSend.Cull) { if (b0 is not null) Gone(serial, b0); continue; }
            if (mode == CarSend.Coarse && !car) mode = CarSend.Full;
            if (mode == CarSend.Skip) { plan.Entries.Add(new Entry { Serial = serial, Base = b0 }); continue; }
            int start = e.Length;
            var hot = EncodeBody(e, tick, ts, b0, rec, car, mode == CarSend.Coarse, b?.Cars);
            if (hot is null) { plan.Entries.Add(new Entry { Serial = serial, Base = b0 }); continue; }
            plan.Entries.Add(new Entry
            {
                Serial = serial, Start = start, Length = e.Length - start, Rec = new EntityRec(hot, rec.Cold, tick), Base = b0,
                Priority = priority, Essential = essential,
            });
        }
        while (gone is not null && gi < gone.Count) { Gone(gone[gi], baseline![gone[gi]]); gi++; }
    }

    /// <summary>
    /// One entry's bytes after its serial: the control (bit 0 removed, or coarse with groups; a bit per group that
    /// changed), the groups' changes as zigzag varints (in coarse units for a coarse one) and the cold bytes as a byte
    /// delta. Returns the channels the client then has, or null for a round unchanged since the baseline (no entry).
    /// </summary>
    private static int[]? EncodeBody(NetWriter e, uint tick, float ts, EntityRec? b0, EntityRec rec, bool car, bool coarse, Dictionary<int, EntityRec>? baseCars)
    {
        var groups = car ? CarState.Groups : RoundState.Groups;
        int coldBit = car ? CarCold : RoundCold, posGroup = car ? 0 : 1;
        var shooter = b0 is null && !car ? FromShooter(rec.Cold, baseCars, tick, ts) : null;
        var baseHot = b0?.Hot ?? shooter ?? (car ? CarZero : RoundZero);
        var hot = (int[])baseHot.Clone();
        Span<int> k = stackalloc int[CarState.HotCount];
        int mask = 0;
        for (int g = 0; g < groups.Length; g++)
        {
            if (g == posGroup) continue;
            // a far car's wheels and driving values are not sent: the client keeps the baseline's
            if (coarse && (g == CarState.WheelGroup || g == CarState.DrivingGroup)) continue;
            var (start, n) = groups[g];
            bool any = false;
            for (int i = start; i < start + n; i++)
            {
                int unit = coarse ? CoarseUnits[i] : 1;
                k[i] = Steps((long)rec.Hot[i] - baseHot[i], unit);
                hot[i] = Apply(baseHot[i], k[i], unit, car, i);
                any |= k[i] != 0;
            }
            if (any) mask |= 1 << g;
        }
        // the position from where the baseline's state, moved on by the mean of its old and the new velocity, puts it
        var (ps, pn) = groups[posGroup];
        for (int i = ps; i < ps + pn; i++)
        {
            int unit = coarse ? CoarseUnits[i] : 1;
            int pred = shooter is not null ? shooter[i] : Pred(b0, hot, tick, ts, i - ps, car);
            k[i] = Steps((long)rec.Hot[i] - pred, unit);
            hot[i] = Clamp(pred + (double)k[i] * unit);
            if (k[i] != 0) mask |= 1 << posGroup;
        }
        var baseCold = b0?.Cold ?? [];
        if (!rec.Cold.AsSpan().SequenceEqual(baseCold)) mask |= 1 << coldBit;
        // a round that has not moved needs no entry (a car's entry says it is of this tick, for its clock)
        if (!car && mask == 0 && b0 is not null) return null;
        e.VarU((uint)mask << 1 | (coarse && mask != 0 ? 1u : 0));
        // a new round's cold bytes first: they name the shooter its baseline is taken from
        bool coldFirst = b0 is null && !car;
        if (coldFirst) XorDelta.Write(e, rec.Cold, baseCold);
        for (int g = 0; g < groups.Length; g++)
        {
            if ((mask & 1 << g) == 0 || g == coldBit) continue;
            var (start, n) = groups[g];
            for (int i = start; i < start + n; i++) e.VarI(k[i]);
        }
        if ((mask & 1 << coldBit) != 0 && !coldFirst) XorDelta.Write(e, rec.Cold, baseCold);
        return hot;
    }

    /// <summary>A change in whole units, rounded to the nearest (halves away from zero).</summary>
    private static int Steps(long d, int unit) =>
        (int)Math.Clamp(unit == 1 ? d : d >= 0 ? (d + unit / 2) / unit : -((-d + unit / 2) / unit), int.MinValue, int.MaxValue);

    /// <summary>A channel moved by <paramref name="k"/> units: a rotation's smallest three stay 15-bit.</summary>
    private static int Apply(int v, int k, int unit, bool car, int channel)
    {
        long x = v + (long)k * unit;
        bool comp = car ? channel is >= 4 and <= 6 : channel is >= 5 and <= 7;
        return comp && unit > 1 ? (int)Math.Clamp(x, 0, 0x7fff) : (int)Math.Clamp(x, int.MinValue, int.MaxValue);
    }

    /// <summary>The bytes <see cref="Emit"/> would write for the plan's selected entries (their serial gaps counted exactly).</summary>
    public static int Size(EntryPlan plan)
    {
        int n = 0, count = 0, prev = 0;
        foreach (var x in plan.Entries)
        {
            if (!x.Selected) continue;
            n += VarSize((uint)(x.Serial - prev)) + x.Length;
            prev = x.Serial;
            count++;
        }
        return n + VarSize((uint)count);
    }

    public static int VarSize(uint v) => v < 1u << 7 ? 1 : v < 1u << 14 ? 2 : v < 1u << 21 ? 3 : v < 1u << 28 ? 4 : 5;

    /// <summary>Writes the plan's selected entries (count, then each one's serial as the gap from the last and its bytes) and what the client then has.</summary>
    public static void Emit(NetWriter w, EntryPlan plan, Dictionary<int, EntityRec> into)
    {
        int count = 0, prev = 0;
        foreach (var x in plan.Entries) if (x.Selected) count++;
        w.VarU((uint)count);
        foreach (var x in plan.Entries)
        {
            if (x.Selected)
            {
                w.VarU((uint)(x.Serial - prev));
                prev = x.Serial;
                w.Bytes(plan.Body(x));
                if (x.Rec is { } rec) into[x.Serial] = rec;
            }
            else if (x.Base is { } b0) into[x.Serial] = b0;
        }
    }

    /// <summary>
    /// A new round's baseline: where its shooter's car is now by the baseline (its state there moved on by its velocity),
    /// headed as the car is. A round leaves its gun a few metres from the car, so this costs a few bytes where the whole
    /// round would cost twenty. Taken from the baseline, not this snapshot, so a round's entry does not depend on which
    /// cars this snapshot carries; none in a full snapshot.
    /// </summary>
    private static int[]? FromShooter(byte[] cold, Dictionary<int, EntityRec>? cars, uint tick, float ts)
    {
        if (cold.Length == 0 || cars is null) return null;
        var r = new NetReader(cold);
        if (!cars.TryGetValue((int)Math.Min(r.VarU(), int.MaxValue), out var car)) return null;
        var v = new int[RoundState.HotCount];
        double dt = Math.Clamp((long)tick - car.SampleTick, 0, MaxDt) * (double)ts;
        for (int i = 0; i < 3; i++)
        {
            double m = car.Hot[CarState.PosChannel + i] / CarState.PosScale + car.Hot[CarState.VelChannel + i] / CarState.VelScale * dt;
            v[1 + i] = Clamp(Math.Round(m * RoundState.PosScale));
        }
        Array.Copy(car.Hot, 3, v, 4, 4);
        return v;
    }

    private static bool Has(IReadOnlyList<(int Serial, EntityRec Rec)> list, int serial)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int s = list[mid].Serial;
            if (s == serial) return true;
            if (s < serial) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }

    /// <summary>
    /// Where the baseline puts position channel <paramref name="i"/> now: a car moved on by the mean of its old and new
    /// velocity, a round along its heading by the distance it travelled since; 0 against nothing. Doubles in a fixed
    /// order, so both ends get the same.
    /// </summary>
    private static int Pred(EntityRec? b, int[] cur, uint tick, float ts, int i, bool car)
    {
        if (b is null) return 0;
        double moved;
        if (car)
        {
            long dt = Math.Clamp((long)tick - b.SampleTick, 0, MaxDt);
            moved = (b.Hot[CarState.VelChannel + i] + (double)cur[CarState.VelChannel + i]) * (CarState.PosScale / CarState.VelScale / 2) * dt * ts;
            return Clamp(b.Hot[CarState.PosChannel + i] + Math.Round(moved));
        }
        double d = (cur[0] - (double)b.Hot[0]) * (RoundState.PosScale / RoundState.TravelScale);
        moved = Forward(cur, 4, i) * d;
        return Clamp(b.Hot[1 + i] + Math.Round(moved));
    }

    private static int Clamp(double v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    /// <summary>Component <paramref name="axis"/> of the forward row of a smallest-three rotation (channels from <paramref name="at"/>), in doubles.</summary>
    private static double Forward(int[] q, int at, int axis)
    {
        int big = q[at] & 3;
        Span<double> c = stackalloc double[4];
        double sum = 0;
        for (int k = 0, j = 1; k < 4; k++)
        {
            if (k == big) continue;
            c[k] = ((q[at + j++] & 0x7fff) - 16383) / 16383.0 / Math.Sqrt(2);
            sum += c[k] * c[k];
        }
        c[big] = Math.Sqrt(Math.Max(0, 1 - sum));
        double len = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3]);
        double x = c[0] / len, y = c[1] / len, z = c[2] / len, w = c[3] / len;
        return axis switch { 0 => 2 * (x * z + w * y), 1 => 2 * (y * z - w * x), _ => 1 - 2 * (x * x + y * y) };
    }

    /// <summary>The CTF flags and the flame throwers' streams, whole every snapshot (a few bytes each).</summary>
    /// <summary>
    /// The tail: the CTF flags, the flame streams, then (protocol 15) a byte of what follows of a mission's script: 1 the
    /// gates it slid that are off their places, 2 its camera while it holds the view (a co-op mission's; a melee's is 0), and
    /// (protocol 19) 4 the personal cameras: the seats whose own camera scene holds their view (docs/multiplayer.md "Personal
    /// cut-scenes"), each with its camera; every client takes the whole list and shows its own seat's.
    /// </summary>
    public static void WriteTail(NetWriter w, IReadOnlyList<FlagState>? flags, IReadOnlyList<FlameState>? flames = null,
        IReadOnlyList<GateState>? gates = null, CameraState? camera = null, IReadOnlyList<(byte Seat, CameraState Camera)>? personal = null)
    {
        w.U8((byte)(flags?.Count ?? 0));
        if (flags is not null) foreach (var f in flags) f.Write(w);
        int nf = Math.Min(flames?.Count ?? 0, 255);
        w.U8((byte)nf);
        for (int i = 0; i < nf; i++) flames![i].Write(w);
        int ng = Math.Min(gates?.Count ?? 0, 255);
        int np = Math.Min(personal?.Count ?? 0, 255);
        w.U8((byte)((ng > 0 ? 1 : 0) | (camera is not null ? 2 : 0) | (np > 0 ? 4 : 0)));
        if (ng > 0) { w.U8((byte)ng); for (int i = 0; i < ng; i++) gates![i].Write(w); }
        camera?.Write(w);
        if (np > 0) { w.U8((byte)np); for (int i = 0; i < np; i++) { w.U8(personal![i].Seat); personal[i].Camera.Write(w); } }
    }

    /// <summary>A snapshot read (the type byte already read): its frame, the cars and rounds it carried this tick, and the rest.</summary>
    public sealed class Decoded
    {
        public required SnapFrame Frame;
        public uint AckedInput;
        public sbyte Slack;
        public bool HasOwn, HasMotion, HasFire, Based;
        /// <summary>The motion was confirmed rather than sent: the client's state for the acked input whose hash ends in <see cref="Echo"/>.</summary>
        public bool Confirmed;
        public ushort Echo;
        public readonly List<int> FreshCars = [], RemovedCars = [];
        public readonly List<FlagState> Flags = [];
        public readonly List<FlameState> Flames = [];
        public readonly List<GateState> Gates = [];
        public CameraState? Camera;
        public readonly List<(byte Seat, CameraState Camera)> Personal = [];
    }

    /// <summary>
    /// Reads a snapshot against the frame <paramref name="baseline"/> finds for its baseline tick. Null when it names one
    /// that is not there (the client has let it go): the snapshot cannot be read, and is lost like a dropped datagram.
    /// </summary>
    public static Decoded? Read(ref NetReader r, float tickSeconds, Func<uint, SnapFrame?> baseline)
    {
        uint tick = r.U32(), acked = r.U32();
        sbyte slack = r.I8();
        uint back = r.VarU();
        SnapFrame? b = null;
        if (back != 0 && (back > tick || (b = baseline(tick - back)) is null)) return null;
        var d = new Decoded { Frame = new SnapFrame { Tick = tick }, AckedInput = acked, Slack = slack, Based = b is not null };
        var f = d.Frame;
        byte has = r.U8();
        (d.HasOwn, d.HasMotion, d.HasFire, d.Confirmed) = ((has & 1) != 0, (has & 2) != 0, (has & 8) != 0, (has & 16) != 0);
        if (d.Confirmed && (b is null || d.HasMotion)) throw new WireException("a motion confirmed without a baseline");
        if (d.HasOwn) f.Own = XorDelta.Read(ref r, b?.Own ?? []);
        if (d.Confirmed) { d.Echo = r.U16(); f.Motion = b!.Motion; }
        if (d.HasMotion) f.Motion = XorDelta.Read(ref r, b?.Motion ?? []);
        f.Slow = (has & 4) != 0 ? XorDelta.Read(ref r, b?.Slow ?? []) : d.HasMotion || d.Confirmed ? b?.Slow ?? [] : [];
        if (d.HasFire) f.Fire = XorDelta.Read(ref r, b?.Fire ?? []);
        ReadEntries(ref r, tick, tickSeconds, b?.Cars, f.Cars, d.FreshCars, d.RemovedCars, null, true);
        ReadEntries(ref r, tick, tickSeconds, b?.Rounds, f.Rounds, null, null, b?.Cars, false);
        int n = r.U8();
        for (int i = 0; i < n; i++) d.Flags.Add(FlagState.Read(ref r));
        n = r.U8();
        for (int i = 0; i < n; i++) d.Flames.Add(FlameState.Read(ref r));
        byte script = r.U8();
        if ((script & 1) != 0) for (n = r.U8(); n > 0; n--) d.Gates.Add(GateState.Read(ref r));
        if ((script & 2) != 0) d.Camera = CameraState.Read(ref r);
        if ((script & 4) != 0) for (n = r.U8(); n > 0; n--) d.Personal.Add((r.U8(), CameraState.Read(ref r)));
        return d;
    }

    private static void ReadEntries(ref NetReader r, uint tick, float ts, Dictionary<int, EntityRec>? b, Dictionary<int, EntityRec> into,
        List<int>? fresh, List<int>? removed, Dictionary<int, EntityRec>? baseCars, bool car)
    {
        if (b is not null) foreach (var (k, v) in b) into[k] = v;
        var groups = car ? CarState.Groups : RoundState.Groups;
        int coldBit = car ? CarCold : RoundCold, posGroup = car ? 0 : 1, hotCount = car ? CarState.HotCount : RoundState.HotCount;
        var zero = car ? CarZero : RoundZero;
        int count = JoinAccept.Count(ref r, car ? 4096 : 65536);
        long serial = 0;
        Span<int> steps = stackalloc int[3];
        for (int k = 0; k < count; k++)
        {
            serial += r.VarU();
            if (serial > 0xffffff) throw new WireException($"serial {serial}");
            int s = (int)serial;
            uint ctl = r.VarU();
            if (ctl == 1) { into.Remove(s); removed?.Add(s); continue; }
            bool coarse = (ctl & 1) != 0;
            if (coarse && !car) throw new WireException("a coarse round");
            uint mask = ctl >> 1;
            if (mask >= 1u << (coldBit + 1)) throw new WireException($"entry groups {mask}");
            var b0 = b?.GetValueOrDefault(s);
            bool coldFirst = b0 is null && !car;
            byte[]? cold = coldFirst && (mask & 1u << coldBit) != 0 ? XorDelta.Read(ref r, [], 1024) : null;
            var shooter = coldFirst && cold is not null ? FromShooter(cold, baseCars, tick, ts) : null;
            var hot = (int[])(b0?.Hot ?? shooter ?? zero).Clone();
            if (hot.Length != hotCount) throw new WireException("a baseline of another kind");
            steps.Clear();
            for (int g = 0; g < groups.Length; g++)
            {
                if ((mask & 1u << g) == 0 || g == coldBit) continue;
                var (start, n) = groups[g];
                for (int i = start; i < start + n; i++)
                {
                    int v = r.VarI();
                    if (g == posGroup) steps[i - start] = v;
                    else hot[i] = Apply(hot[i], v, coarse ? CoarseUnits[i] : 1, car, i);
                }
            }
            var (ps, pn) = groups[posGroup];
            for (int i = 0; i < pn; i++)
                hot[ps + i] = Clamp((shooter is not null ? shooter[ps + i] : Pred(b0, hot, tick, ts, i, car)) + (double)steps[i] * (coarse ? CoarseUnits[ps + i] : 1));
            cold ??= (mask & 1u << coldBit) != 0 ? XorDelta.Read(ref r, b0?.Cold ?? [], 1024) : b0?.Cold ?? [];
            into[s] = new EntityRec(hot, cold, tick);
            fresh?.Add(s);
        }
    }
}

/// <summary>
/// The server's side of one client's delta snapshots: the frames sent it lately (1.6 s of them) and the newest tick it
/// acknowledged (in its input messages). A snapshot goes against that tick's frame, or in full when there is none (a
/// new joiner, an ack too old, a client whose acks stopped).
/// </summary>
public sealed class SnapshotEncoder(I76.Sim.Session.SimRate rate)
{
    private readonly Dictionary<uint, SnapFrame> _sent = [];
    private readonly int _keep = rate.TicksFor(1.6f);
    public uint Acked { get; private set; }
    /// <summary>Snapshots written in full (no baseline the client has), and against one.</summary>
    public long Full, Deltas;

    /// <summary>The client has this tick's snapshot (a tick it was never sent is ignored).</summary>
    public void Ack(uint tick)
    {
        if (tick > Acked && _sent.ContainsKey(tick)) Acked = tick;
    }

    /// <summary>The baseline for a snapshot of <paramref name="tick"/>: the acked frame, if still kept.</summary>
    public SnapFrame? Baseline(uint tick) => Acked != 0 && tick > Acked && tick - Acked <= _keep ? _sent.GetValueOrDefault(Acked) : null;

    /// <summary>Keeps the frame just sent; forgets those too old to be a baseline.</summary>
    public void Sent(SnapFrame f)
    {
        _sent[f.Tick] = f;
        if (_sent.Count > _keep + 4)
            foreach (var t in _sent.Keys.Where(t => t + _keep < f.Tick).ToList()) _sent.Remove(t);
    }
}

/// <summary>
/// The client's side: the frames decoded lately (3 s of them, more than the server keeps), the newest tick decoded (the
/// ack its inputs carry), and each car's newest state, which a snapshot that did not carry the car this tick lists it with.
/// </summary>
public sealed class SnapshotDecoder(I76.Sim.Session.SimRate rate)
{
    private readonly SortedList<uint, SnapFrame> _frames = [];
    private readonly Dictionary<int, CarState> _latest = [];
    private readonly Dictionary<int, uint> _removedAt = [];
    private readonly int _keep = rate.TicksFor(3f);
    private readonly float _tickSeconds = rate.TickSeconds;
    /// <summary>The newest tick decoded.</summary>
    public uint Newest { get; private set; }
    /// <summary>
    /// The tick of the snapshot decoded last: what the client acknowledges (an older one after a reordering only makes
    /// the baseline older; a stray tick of another round, which the server never sent this round, is not held on to).
    /// </summary>
    public uint Last { get; private set; }
    /// <summary>Snapshots that named a baseline this client no longer had (lost), and those read against one.</summary>
    public long Misses, Deltas;

    public void Clear()
    {
        _frames.Clear();
        _latest.Clear();
        _removedAt.Clear();
        Newest = Last = 0;
    }

    /// <summary>Reads a snapshot (the type byte already read); null if its baseline is gone.</summary>
    public Snapshot? Read(ref NetReader r)
    {
        var d = SnapshotCodec.Read(ref r, _tickSeconds, t => _frames.GetValueOrDefault(t));
        if (d is null) { Misses++; return null; }
        var f = d.Frame;
        if (_frames.ContainsKey(f.Tick)) return null;
        _frames.Add(f.Tick, f);
        while (_frames.Count > 0 && _frames.Keys[0] + _keep < f.Tick) _frames.RemoveAt(0);
        if (f.Tick > Newest) Newest = f.Tick;
        Last = f.Tick;
        if (d.Based) Deltas++;
        if (_removedAt.Count > 256) foreach (var k in _removedAt.Where(kv => kv.Value + _keep < f.Tick).Select(kv => kv.Key).ToList()) _removedAt.Remove(k);
        foreach (var s in d.RemovedCars) { _latest.Remove(s); _removedAt[s] = f.Tick; }
        foreach (var s in d.FreshCars)
        {
            if (_removedAt.TryGetValue(s, out var gone) && gone >= f.Tick) continue;
            if (!_latest.TryGetValue(s, out var was) || was.SampleTick < f.Tick) _latest[s] = CarState.FromRec(s, f.Cars[s]);
        }
        return View(d, s => _latest.GetValueOrDefault(s));
    }

    /// <summary>The snapshot as the views take it: the cars it carried at its tick, the others as the newest each has.</summary>
    internal static Snapshot View(SnapshotCodec.Decoded d, Func<int, CarState?> latest)
    {
        var f = d.Frame;
        var s = new Snapshot { Tick = f.Tick, AckedInput = d.AckedInput, Slack = d.Slack };
        if (d.HasOwn)
        {
            var r = new NetReader(f.Own);
            s.Own = OwnCar.Read(ref r);
        }
        if ((d.HasMotion || d.Confirmed) && f.Slow.Length > 0)
        {
            var rs = new NetReader(f.Slow);
            s.Slow = OwnMotion.SlowMotion.Read(ref rs);
        }
        if (d.HasMotion)
        {
            var r = new NetReader(f.Motion);
            s.Motion = OwnMotion.Read(ref r);
            s.Motion.Slow = s.Slow;
        }
        if (d.Confirmed) s.MotionEcho = d.Echo;
        if (d.HasFire)
        {
            var r = new NetReader(f.Fire);
            s.Fire = OwnFire.Read(ref r);
        }
        var fresh = new HashSet<int>(d.FreshCars);
        foreach (var (serial, rec) in f.Cars.OrderBy(kv => kv.Key))
            s.Cars.Add(fresh.Contains(serial) ? latest(serial) is { SampleTick: var t } c && t == f.Tick ? c : CarState.FromRec(serial, rec)
                : latest(serial) ?? CarState.FromRec(serial, rec));
        foreach (var (serial, rec) in f.Rounds.OrderBy(kv => kv.Key)) s.Rounds.Add(RoundState.FromRec(serial, rec));
        s.Flags.AddRange(d.Flags);
        s.Flames.AddRange(d.Flames);
        s.Gates.AddRange(d.Gates);
        s.Camera = d.Camera;
        s.Personal.AddRange(d.Personal);
        return s;
    }
}
