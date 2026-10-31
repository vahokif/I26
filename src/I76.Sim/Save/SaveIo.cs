using System.Numerics;
using System.Text;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("I76.Sim.Tests")]

namespace I76.Sim.Save;

/// <summary>A snapshot that cannot be read (truncated, corrupted, or written by code that reads it differently).</summary>
public class SnapshotFormatException(string message) : Exception(message);

/// <summary>A snapshot of a different mission, install, format version or tick rate than the session it is restored into.</summary>
public sealed class SnapshotMismatchException(string message) : SnapshotFormatException(message);

/// <summary>
/// The snapshot's byte writer: variable-length integers (zigzag for signed), floats and doubles as their exact
/// bits, strings as UTF-8 with a length. Cross-object references go through <see cref="Refs"/> as stable ids.
/// </summary>
public sealed class SaveWriter
{
    private byte[] _b = new byte[1 << 16];
    private int _n;
    internal SaveRefs Refs { get; }

    internal SaveWriter(SaveRefs refs) => Refs = refs;

    public int Length => _n;
    public byte[] ToArray() => _b.AsSpan(0, _n).ToArray();

    private void Ensure(int k)
    {
        if (_n + k <= _b.Length) return;
        Array.Resize(ref _b, System.Math.Max(_b.Length * 2, _n + k));
    }

    public void U8(byte v) { Ensure(1); _b[_n++] = v; }
    public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public void VarU(ulong v)
    {
        Ensure(10);
        while (v >= 0x80) { _b[_n++] = (byte)(v | 0x80); v >>= 7; }
        _b[_n++] = (byte)v;
    }
    public void VarI(long v) => VarU((ulong)((v << 1) ^ (v >> 63)));
    public void Int(int v) => VarI(v);
    public void U32(uint v) => VarU(v);
    public void F32(float v) { Ensure(4); BitConverter.TryWriteBytes(_b.AsSpan(_n), BitConverter.SingleToUInt32Bits(v)); _n += 4; }
    public void F64(double v) { Ensure(8); BitConverter.TryWriteBytes(_b.AsSpan(_n), BitConverter.DoubleToUInt64Bits(v)); _n += 8; }
    public void V2(Vector2 v) { F32(v.X); F32(v.Y); }
    public void V3(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
    public void Rows(Rows r) { V3(r.Right); V3(r.Up); V3(r.Forward); }
    public void D3(Double3 d) { F64(d.X); F64(d.Y); F64(d.Z); }
    public void Str(string? s)
    {
        if (s is null) { VarU(0); return; }
        int k = Encoding.UTF8.GetByteCount(s);
        VarU((ulong)k + 1);
        Ensure(k);
        _n += Encoding.UTF8.GetBytes(s, _b.AsSpan(_n));
    }
    public void Bytes(ReadOnlySpan<byte> b) { VarU((ulong)b.Length); Ensure(b.Length); b.CopyTo(_b.AsSpan(_n)); _n += b.Length; }
    public void NF32(float? v) { Bool(v is not null); if (v is { } x) F32(x); }
    public void NInt(int? v) { Bool(v is not null); if (v is { } x) Int(x); }
    public void NV3(Vector3? v) { Bool(v is not null); if (v is { } x) V3(x); }
    public void NRows(Rows? v) { Bool(v is not null); if (v is { } x) Rows(x); }
    /// <summary>A section marker: a reader that meets another one has lost step with the writer.</summary>
    public void Tag(string fourcc) { foreach (char c in fourcc) U8((byte)c); }
    public void Ints(IReadOnlyList<int> a) { VarU((ulong)a.Count); foreach (int x in a) Int(x); }

    // references, as stable ids
    public void Car(Vehicle? v) => Refs.WriteCar(this, v);
    public void Ent(Entity? e) => Refs.WriteEntity(this, e);
    public void Obj(object? o) => Refs.WriteObject(this, o);
    public void Path(IReadOnlyList<Vector3>? p) => Refs.WritePath(this, p);
}

/// <summary>The snapshot's byte reader, the mirror of <see cref="SaveWriter"/>.</summary>
public sealed class SaveReader
{
    private readonly byte[] _b;
    private int _p;
    private readonly int _end;
    internal SaveRefs Refs { get; }

    internal SaveReader(byte[] b, SaveRefs refs, int start = 0, int end = -1)
    {
        _b = b; _p = start; _end = end < 0 ? b.Length : end; Refs = refs;
    }

    public bool AtEnd => _p >= _end;
    private void Need(int k) { if (_p + k > _end) throw new SnapshotFormatException($"snapshot truncated at byte {_p}"); }

    public byte U8() { Need(1); return _b[_p++]; }
    public bool Bool() => U8() switch { 0 => false, 1 => true, var x => throw new SnapshotFormatException($"bad bool {x} at byte {_p - 1}") };
    public ulong VarU()
    {
        ulong v = 0;
        for (int s = 0; ; s += 7)
        {
            if (s > 63) throw new SnapshotFormatException($"bad varint at byte {_p}");
            byte b = U8();
            v |= (ulong)(b & 0x7f) << s;
            if (b < 0x80) return v;
        }
    }
    public long VarI() { ulong u = VarU(); return (long)(u >> 1) ^ -(long)(u & 1); }
    public int Int() => checked((int)VarI());
    public uint U32() => checked((uint)VarU());
    public int Count(int max = 1 << 24)
    {
        ulong n = VarU();
        if (n > (ulong)max) throw new SnapshotFormatException($"count {n} out of range at byte {_p}");
        return (int)n;
    }
    public float F32() { Need(4); float v = BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(_b, _p)); _p += 4; return v; }
    public double F64() { Need(8); double v = BitConverter.UInt64BitsToDouble(BitConverter.ToUInt64(_b, _p)); _p += 8; return v; }
    public Vector2 V2() => new(F32(), F32());
    public Vector3 V3() => new(F32(), F32(), F32());
    public Rows Rows() => new(V3(), V3(), V3());
    public Double3 D3() => new(F64(), F64(), F64());
    public string? Str()
    {
        ulong k = VarU();
        if (k == 0) return null;
        int n = checked((int)(k - 1));
        Need(n);
        var s = Encoding.UTF8.GetString(_b, _p, n);
        _p += n;
        return s;
    }
    public byte[] Bytes() { int n = Count(); Need(n); var a = _b.AsSpan(_p, n).ToArray(); _p += n; return a; }
    public float? NF32() => Bool() ? F32() : null;
    public int? NInt() => Bool() ? Int() : null;
    public Vector3? NV3() => Bool() ? V3() : null;
    public Rows? NRows() => Bool() ? Rows() : null;
    public void Tag(string fourcc)
    {
        int at = _p;
        foreach (char c in fourcc)
            if (U8() != (byte)c) throw new SnapshotFormatException($"section {fourcc} expected at byte {at}: the reader has lost step with the writer");
    }
    public int[] Ints() { int n = Count(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = Int(); return a; }

    public Vehicle? Car() => Refs.ReadCar(this);
    public Entity? Ent() => Refs.ReadEntity(this);
    public object? Obj() => Refs.ReadObject(this);
    public IReadOnlyList<Vector3>? Path() => Refs.ReadPath(this);

    /// <summary>A value read that does not fit the rebuilt mission.</summary>
    public SnapshotMismatchException Mismatch(string what) => new($"the snapshot does not fit the rebuilt mission: {what}");
}

/// <summary>
/// Stable ids for what objects refer to: a car by its <see cref="Vehicle.NetId"/>, a mission entity by its place
/// in the script host's list, a structure by its <see cref="StaticObject.NetId"/>, a nav path by its place in
/// the mission's FSM program (or an entity's own one-point path), a weapon by its car and slot.
/// </summary>
internal sealed class SaveRefs
{
    public readonly Dictionary<int, Vehicle> CarById = [];
    public readonly Dictionary<Entity, int> EntityIndex = [];
    public readonly List<Entity> Entities = [];
    public readonly Dictionary<int, StaticObject> StaticById = [];
    public IReadOnlyList<Formats.Mission.NavPath> Paths = [];

    public void AddEntities(IEnumerable<Entity> es)
    {
        foreach (var e in es) { EntityIndex.TryAdd(e, Entities.Count); Entities.Add(e); }
    }

    public void WriteCar(SaveWriter w, Vehicle? v)
    {
        if (v is null) { w.Int(0); return; }
        if (v.NetId == 0 || !CarById.ContainsKey(v.NetId)) throw new InvalidOperationException($"snapshot: a car outside the session's table ({v.Definition}, id {v.NetId})");
        w.Int(v.NetId);
    }

    public Vehicle? ReadCar(SaveReader r)
    {
        int id = r.Int();
        if (id == 0) return null;
        return CarById.TryGetValue(id, out var v) ? v : throw new SnapshotFormatException($"snapshot names car {id:x}, which it does not hold");
    }

    public void WriteEntity(SaveWriter w, Entity? e)
    {
        if (e is null) { w.Int(-1); return; }
        w.Int(EntityIndex.TryGetValue(e, out int i) ? i : throw new InvalidOperationException($"snapshot: entity {e.Name} is not the script host's"));
    }

    public Entity? ReadEntity(SaveReader r)
    {
        int i = r.Int();
        if (i == -1) return null;
        return i >= 0 && i < Entities.Count ? Entities[i] : throw new SnapshotFormatException($"snapshot names entity {i} of {Entities.Count}");
    }

    private const byte OBNull = 0, OBCar = 1, OBStatic = 2, OBEntity = 3;

    /// <summary>Whatever an object-typed field holds: a car, a structure, an entity, or nothing.</summary>
    public void WriteObject(SaveWriter w, object? o)
    {
        switch (o)
        {
            case null: w.U8(OBNull); break;
            case Vehicle v: w.U8(OBCar); WriteCar(w, v); break;
            case StaticObject s: w.U8(OBStatic); w.Int(s.NetId); break;
            case Entity e: w.U8(OBEntity); WriteEntity(w, e); break;
            default: throw new InvalidOperationException($"snapshot: no id for a {o.GetType().Name}");
        }
    }

    public object? ReadObject(SaveReader r) => r.U8() switch
    {
        OBNull => null,
        OBCar => ReadCar(r),
        OBStatic => ReadStatic(r.Int()),
        OBEntity => ReadEntity(r),
        var x => throw new SnapshotFormatException($"bad object reference kind {x}"),
    };

    public StaticObject ReadStatic(int id) =>
        StaticById.TryGetValue(id, out var s) ? s : throw new SnapshotFormatException($"snapshot names structure {id:x}, which the mission does not have");

    /// <summary>A nav path: 0 none, 1 + i the program's path i, then an entity's own one-point path (Entity.SnapshotPath), then a path a bot's melee gave it (Entity.OwnPath), then, 4, a list that is nobody's now (a bot's old route, still the AI's NavPath), by its points.</summary>
    public void WritePath(SaveWriter w, IReadOnlyList<Vector3>? p)
    {
        if (p is null) { w.U8(0); return; }
        for (int i = 0; i < Paths.Count; i++)
            if (ReferenceEquals(Paths[i].Points, p)) { w.U8(1); w.Int(i); return; }
        for (int i = 0; i < Entities.Count; i++)
            if (ReferenceEquals(Entities[i].SnapshotPath, p)) { w.U8(2); w.Int(i); return; }
        for (int i = 0; i < Entities.Count; i++)
            if (ReferenceEquals(Entities[i].OwnPath, p)) { w.U8(3); w.Int(i); return; }
        // a list nobody owns now: a bot given a new route (Entity.OwnPath) while the AI's NavPath still walks the old one until its next plan.
        // The points are written out; the restored list is a copy, which is no one's own either, as it was
        w.U8(4); w.VarU((ulong)p.Count); foreach (var q in p) w.V3(q);
    }

    public IReadOnlyList<Vector3>? ReadPath(SaveReader r)
    {
        switch (r.U8())
        {
            case 0: return null;
            case 1:
            {
                int i = r.Int();
                return i >= 0 && i < Paths.Count ? Paths[i].Points : throw new SnapshotFormatException($"snapshot names nav path {i} of {Paths.Count}");
            }
            case 2: return ReadEntity(r)?.SnapshotPath;
            case 3: return ReadEntity(r)?.OwnPath;
            case 4:
            {
                int n = r.Count();
                var list = new List<Vector3>(n);
                for (int k = 0; k < n; k++) list.Add(r.V3());
                return list;
            }
            case var x: throw new SnapshotFormatException($"bad path reference kind {x}");
        }
    }

    /// <summary>A weapon's slot on its car: its place in the load order, or 100 + the handgun's.</summary>
    public static int SlotOf(Armory? a, Weapon w)
    {
        if (a is null) return -1;
        int i = a.LoadOrder.IndexOf(w);
        if (i >= 0) return i;
        for (int k = 0; k < a.Handguns.Length; k++) if (ReferenceEquals(a.Handguns[k], w)) return 100 + k;
        return -1;
    }

    public static Weapon? AtSlot(Armory? a, int slot) =>
        a is null ? null : slot >= 100 ? (slot - 100 < a.Handguns.Length ? a.Handguns[slot - 100] : null) : slot >= 0 && slot < a.LoadOrder.Count ? a.LoadOrder[slot] : null;

    /// <summary>A weapon: on its own car if it is still there, else on whichever car has it (a renewed car's old rounds).</summary>
    public void WriteWeapon(SaveWriter w, Vehicle? owner, Weapon weapon)
    {
        if (owner is not null && SlotOf(owner.Armory, weapon) is var s and >= 0) { WriteCar(w, owner); w.Int(s); return; }
        foreach (var v in CarById.Values.OrderBy(v => v.NetId))
            if (SlotOf(v.Armory, weapon) is var k and >= 0) { WriteCar(w, v); w.Int(k); return; }
        // a renewed car's old armoury: the same slot of what it has now (its definition is the same file)
        if (owner is not null)
        {
            WriteCar(w, owner);
            w.Int(-2);
            w.Str(weapon.Def.File); w.U32(weapon.Def.Tier); w.U32(weapon.Hardpoint.Id);
            return;
        }
        throw new InvalidOperationException($"snapshot: weapon {weapon.Def.File} is on no car");
    }

    public Weapon ReadWeapon(SaveReader r)
    {
        var car = ReadCar(r);
        int slot = r.Int();
        if (slot == -2)
        {
            string? file = r.Str(); uint tier = r.U32(), hp = r.U32();
            var all = (car?.Armory?.LoadOrder ?? []).Concat(car?.Armory?.Handguns.OfType<Weapon>() ?? []);
            return all.FirstOrDefault(x => x.Def.File == file && x.Def.Tier == tier && x.Hardpoint.Id == hp)
                ?? throw r.Mismatch($"car {car?.NetId:x} has no {file} on hardpoint {hp}");
        }
        return AtSlot(car?.Armory, slot) ?? throw r.Mismatch($"car {car?.NetId:x} has no weapon in slot {slot}");
    }
}
