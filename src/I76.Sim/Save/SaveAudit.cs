using System.Reflection;

namespace I76.Sim.Save;

/// <summary>What a snapshot does with a field of the simulation's state.</summary>
public enum SaveKind
{
    /// <summary>Written and read back.</summary>
    Saved,
    /// <summary>Not written: the mission build (from the install and the <see cref="SessionBuild"/>) makes it again, the same.</summary>
    Structural,
    /// <summary>Not written: recomputed on restore from what is.</summary>
    Derived,
    /// <summary>Not written and of no consequence to what the simulation does next (per-tick outputs, wiring to the game, logging).</summary>
    Transient,
}

/// <summary>
/// The field audit (docs/savestate.md): for every class of the simulation's state, what the snapshot does with
/// each of its instance fields. The serialisers declare it next to their Write and Read. The snapshot guard test
/// fails when a state class has a field not in it, so a field added to the simulation is either serialised or
/// marked with the reason it need not be.
/// </summary>
public sealed class SaveAudit
{
    public static SaveAudit All { get; } = Build();

    private readonly Dictionary<Type, Dictionary<string, (SaveKind Kind, string Why)>> _types = [];

    public IReadOnlyCollection<Type> Types => _types.Keys;

    public IReadOnlyDictionary<string, (SaveKind Kind, string Why)>? Of(Type t) => _types.GetValueOrDefault(t);

    /// <summary>The field's audit entry, or null when its type or the field is not audited.</summary>
    public (SaveKind Kind, string Why)? KindOf(FieldInfo f) =>
        f.DeclaringType is { } t && _types.TryGetValue(t, out var m) && m.TryGetValue(NameOf(f), out var e) ? e : null;

    /// <summary>A field's name as source code says it: an auto-property's backing field by its property, a captured primary-constructor parameter by the parameter.</summary>
    public static string NameOf(FieldInfo f)
    {
        var n = f.Name;
        if (n.StartsWith('<') && n.IndexOf('>') is var k and > 1) return n[1..k];
        return n;
    }

    /// <summary>The instance fields a type declares itself (its bases are audited on their own).</summary>
    public static IEnumerable<FieldInfo> FieldsOf(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    public sealed class Entry(Dictionary<string, (SaveKind, string)> m, Type t)
    {
        private Entry Add(SaveKind k, string why, string[] names)
        {
            if (k != SaveKind.Saved && string.IsNullOrWhiteSpace(why)) throw new ArgumentException($"{t.Name}: a {k} field needs a reason");
            foreach (var n in names)
                if (!m.TryAdd(n, (k, why))) throw new InvalidOperationException($"{t.Name}.{n} is audited twice");
            return this;
        }
        public Entry Saved(params string[] names) => Add(SaveKind.Saved, "", names);
        public Entry Structural(string why, params string[] names) => Add(SaveKind.Structural, why, names);
        public Entry Derived(string why, params string[] names) => Add(SaveKind.Derived, why, names);
        public Entry Transient(string why, params string[] names) => Add(SaveKind.Transient, why, names);
    }

    public Entry For(Type t)
    {
        if (!_types.TryGetValue(t, out var m)) _types[t] = m = [];
        return new Entry(m, t);
    }

    public Entry For<T>() => For(typeof(T));

    private static SaveAudit Build()
    {
        var a = new SaveAudit();
        Session.SimSession.AuditFields(a);
        SimWorld.AuditFields(a);
        VehicleSave.AuditFields(a);
        Weapons.Combat.AuditFields(a);
        Damage.DamageModel.AuditFields(a);
        Collision.StaticObject.AuditFields(a);
        Missions.MissionScript.AuditFields(a);
        Missions.ScriptHost.AuditFields(a);
        Missions.Entity.AuditFields(a);
        Ai.PathSearch.AuditFields(a);
        Fsm.FsmVm.AuditFields(a);
        Missions.MeleeGame.AuditFields(a);
        return a;
    }
}
