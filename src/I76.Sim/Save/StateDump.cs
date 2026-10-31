using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using I76.Sim.Collision;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Sim.Save;

/// <summary>
/// A canonical text dump of a session's whole state, by reflection over every field: independent of the
/// snapshot's hand-written serialisers, so it shows what they miss (docs/savestate.md). One line per value,
/// "path = value". Fields audited <see cref="SaveKind.Transient"/> are left out; <see cref="SaveKind.Structural"/>
/// ones are shown shallow (values, and references by identity), everything else in full. Cars, entities,
/// structures and rounds are named by their stable ids wherever they are referred to and dumped in full in their
/// own lists; other shared objects are dumped where first met and named by that path after. Dictionaries and sets
/// are in key order, so two equal states dump the same however their tables were filled. Static geometry
/// (polygons, boxes) is dumped as a checksum.
/// </summary>
public sealed class StateDump
{
    private readonly StringBuilder _sb = new();
    private readonly Dictionary<object, string> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, int> _entityIndex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MeleePlayer, int> _playerIndex = new(ReferenceEqualityComparer.Instance);
    private static readonly SaveAudit Audit = SaveAudit.All;
    private static readonly Assembly SimAssembly = typeof(SimWorld).Assembly;
    private static readonly Assembly FormatsAssembly = typeof(Formats.Bin).Assembly;

    /// <summary>The types met in the sim's assembly, with the fields they were met through (for the snapshot guard).</summary>
    public HashSet<Type> SimTypes { get; } = [];

    public static string Of(SimSession s) => new StateDump().Dump(s);

    public string Dump(SimSession s)
    {
        if (s.Script is { } sc)
        {
            for (int i = 0; i < sc.Host.Entities.Count; i++) _entityIndex.TryAdd(sc.Host.Entities[i], i);
            if (sc.Melee is { } m) for (int i = 0; i < m.Players.Count; i++) _playerIndex.TryAdd(m.Players[i], i);
        }
        Value("session", s, deep: true);
        var cars = s.AllCars();
        Line("cars.count", cars.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var v in cars) Fields($"cars[{Key(v)}]", v);
        if (s.World.Ground is IHasStatics hs)
            for (int i = 0; i < hs.World.Objects.Count; i++) Fields($"statics[{Key(hs.World.Objects[i])}]", hs.World.Objects[i]);
        if (s.Combat?.Damage is Damage.DamageModel dm) Value("damage", dm, deep: true);
        return _sb.ToString();
    }

    private void Line(string path, string value) => _sb.Append(path).Append(" = ").Append(value).Append('\n');

    /// <summary>A car, entity, structure, round or melee player by its stable id.</summary>
    private string? Key(object o) => o switch
    {
        Vehicle v => $"car#{v.NetId:x}",
        Entity e => _entityIndex.TryGetValue(e, out int i) ? $"ent#{i}" : $"ent?{e.Name}",
        StaticObject so => $"obj#{so.NetId:x}",
        Round r => $"round#{r.NetId:x}",
        MeleePlayer p => _playerIndex.TryGetValue(p, out int k) ? $"player#{k}" : "player?",
        _ => null,
    };

    /// <summary>Where cars, entities, structures and rounds are dumped in full: their own lists.</summary>
    private static bool IsHome(FieldInfo f) =>
        (f.DeclaringType == typeof(ScriptHost) && SaveAudit.NameOf(f) == nameof(ScriptHost.Entities))
        || (f.DeclaringType == typeof(Combat) && SaveAudit.NameOf(f) == nameof(Combat.Rounds))
        || (f.DeclaringType == typeof(MeleeGame) && SaveAudit.NameOf(f) == nameof(MeleeGame.Players));

    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = [];

    private static FieldInfo[] AllFields(Type t)
    {
        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(t, out var fs)) return fs;
            var chain = new List<Type>();
            for (var k = t; k is not null && k != typeof(object) && k != typeof(ValueType); k = k.BaseType) chain.Insert(0, k);
            fs = [.. chain.SelectMany(k => SaveAudit.FieldsOf(k).OrderBy(f => f.MetadataToken))];
            FieldCache[t] = fs;
            return fs;
        }
    }

    private void Fields(string path, object o, bool home = true)
    {
        var t = o.GetType();
        if (t.Assembly == SimAssembly) SimTypes.Add(t);
        foreach (var f in AllFields(t))
        {
            if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
            var kind = Audit.KindOf(f)?.Kind;
            if (kind == SaveKind.Transient) continue;
            var v = f.GetValue(o);
            string p = path + "." + SaveAudit.NameOf(f);
            if (kind == SaveKind.Structural) Shallow(p, v);
            else Value(p, v, deep: true, homeList: IsHome(f));
        }
    }

    private void Shallow(string path, object? v)
    {
        switch (v)
        {
            case null: Line(path, "null"); return;
            case Delegate: return;
        }
        var t = v.GetType();
        if (Leaf(v) is { } s) { Line(path, s); return; }
        if (Key(v) is { } k) { Line(path, k); return; }
        if (t.IsValueType && t.Assembly == SimAssembly) { Value(path, v, deep: true); return; }
        if (v is Array a && t.GetElementType()!.IsPrimitive) { Value(path, v, deep: true); return; }
        if (v is ICollection c) { Line(path, $"{Name(t)} ({c.Count})"); return; }
        Line(path, Name(t));
    }

    private static string Name(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">" : t.Name;

    private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    private static string D(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A value printed whole, or null for something with parts.</summary>
    private static string? Leaf(object v) => v switch
    {
        bool b => b ? "true" : "false",
        float f => F(f),
        double d => D(d),
        string s => "\"" + s + "\"",
        Enum e => Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture) + " " + e,
        IConvertible c when v.GetType().IsPrimitive => c.ToString(CultureInfo.InvariantCulture),
        Vector2 v2 => $"({F(v2.X)}, {F(v2.Y)})",
        Vector3 v3 => $"({F(v3.X)}, {F(v3.Y)}, {F(v3.Z)})",
        Quaternion q => $"({F(q.X)}, {F(q.Y)}, {F(q.Z)}, {F(q.W)})",
        _ when v.GetType().Assembly == FormatsAssembly => StaticName(v),
        _ => null,
    };

    /// <summary>Static data from the install: its type and file or name, not its contents.</summary>
    private static string StaticName(object v)
    {
        var t = v.GetType();
        var p = t.GetProperty("File") ?? t.GetProperty("Name");
        return p?.GetValue(v) is string s ? $"{t.Name}:{s}" : t.Name;
    }

    private void Value(string path, object? v, bool deep, bool homeList = false)
    {
        if (v is null) { Line(path, "null"); return; }
        if (v is Delegate) return;
        if (Leaf(v) is { } s) { Line(path, s); return; }
        var t = v.GetType();
        if (Key(v) is { } k) { Line(path, k); return; }
        if (!t.IsValueType)
        {
            if (_seen.TryGetValue(v, out var first)) { Line(path, "@" + first); return; }
            _seen[v] = path;
        }
        switch (v)
        {
            case IList<Poly> polys: Line(path, Geometry(polys.SelectMany(PolyFloats), polys.Count)); return;
            case IList<NodeBox> boxes: Line(path, Geometry(boxes.SelectMany(BoxFloats), boxes.Count)); return;
            case Random:
                Fields(path, v);   // its private state: the generator as it stands
                return;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PriorityQueue<,>))
        {
            var items = (IEnumerable)t.GetProperty("UnorderedItems")!.GetValue(v)!;
            int i = 0;
            foreach (var it in items) Value($"{path}[{i++}]", it, deep);
            Line(path + ".count", i.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (v is IDictionary dict)
        {
            var entries = new List<(string Key, object? Value)>();
            foreach (DictionaryEntry e in dict) entries.Add((KeyString(e.Key), e.Value));
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Line(path + ".count", entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var (key, val) in entries) Value($"{path}[{key}]", val, deep);
            return;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>))
        {
            var keys = ((IEnumerable)v).Cast<object>().Select(KeyString).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Line(path, "{" + string.Join(", ", keys) + "}");
            return;
        }
        if (v is IEnumerable seq && (v is Array || v is IList || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Queue<>))))
        {
            var items = seq.Cast<object?>().ToList();
            int n = items.Count;
            // a long array of values (an FSM stack): only up to its last element that is not the default
            if (v is Array && t.GetElementType()!.IsValueType && n > 64)
            {
                var zero = Activator.CreateInstance(t.GetElementType()!);
                while (n > 0 && Equals(items[n - 1], zero)) n--;
                Line(path + ".length", items.Count.ToString(CultureInfo.InvariantCulture));
            }
            Line(path + ".count", n.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < n; i++)
            {
                var it = items[i];
                if (homeList && it is not null && Key(it) is { } ik) Fields($"{path}[{ik}]", it);
                else Value($"{path}[{i}]", it, deep);
            }
            return;
        }
        Fields(path, v);
    }

    /// <summary>A dictionary key or set element as a sortable string.</summary>
    private string KeyString(object? k) => k switch
    {
        null => "null",
        _ when Key(k) is { } s => s,
        _ when Leaf(k) is { } s => s,
        ITuple tup => "(" + string.Join(",", Enumerable.Range(0, tup.Length).Select(i => KeyString(tup[i]))) + ")",
        _ => k.GetType().Name,
    };

    private static IEnumerable<float> PolyFloats(Poly p) => [p.N.X, p.N.Y, p.N.Z, p.D, p.Node, .. p.V.SelectMany(v => new[] { v.X, v.Y, v.Z })];
    private static IEnumerable<float> BoxFloats(NodeBox b) =>
        [b.Rot.Right.X, b.Rot.Right.Y, b.Rot.Right.Z, b.Rot.Up.X, b.Rot.Up.Y, b.Rot.Up.Z, b.Rot.Forward.X, b.Rot.Forward.Y, b.Rot.Forward.Z,
         b.Origin.X, b.Origin.Y, b.Origin.Z, b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z, b.Radius, b.Node];

    private static string Geometry(IEnumerable<float> fs, int count)
    {
        ulong h = 14695981039346656037UL;
        foreach (float f in fs) { h ^= BitConverter.SingleToUInt32Bits(f); h *= 1099511628211UL; }
        return $"{count} ({h:x16})";
    }

    /// <summary>The first line where two dumps differ, with its neighbours, or null when they are the same.</summary>
    public static string? FirstDifference(string a, string b, int context = 3)
    {
        if (a == b) return null;
        var la = a.Split('\n'); var lb = b.Split('\n');
        int i = 0;
        while (i < la.Length && i < lb.Length && la[i] == lb[i]) i++;
        var sb = new StringBuilder($"dumps differ at line {i + 1}:\n");
        for (int k = System.Math.Max(0, i - context); k < System.Math.Min(System.Math.Max(la.Length, lb.Length), i + context + 1); k++)
        {
            string x = k < la.Length ? la[k] : "<end>", y = k < lb.Length ? lb[k] : "<end>";
            sb.Append(x == y ? "  " : "- ").Append(x).Append('\n');
            if (x != y) sb.Append("+ ").Append(y).Append('\n');
        }
        return sb.ToString();
    }
}
