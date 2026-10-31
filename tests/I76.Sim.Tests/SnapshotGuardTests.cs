using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Save;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The snapshot guard (docs/savestate.md): every instance field of the simulation's state is in the field audit,
/// saved or marked structural, derived or transient with a reason; every class the state reaches is audited; and
/// every field the audit says is saved does reach the snapshot's bytes.
/// </summary>
public class SnapshotGuardTests(ITestOutputHelper output)
{
    private static readonly Assembly Sim = typeof(SimWorld).Assembly;
    private static readonly SaveAudit Audit = SaveAudit.All;

    private static bool Generated(Type t) => t.Name.Contains('<') || t.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    [Fact]
    public void TheAuditNamesEveryFieldOfEveryAuditedClass()
    {
        var problems = new List<string>();
        foreach (var t in Audit.Types.OrderBy(t => t.FullName))
        {
            var audited = Audit.Of(t)!;
            var fields = SaveAudit.FieldsOf(t).Select(SaveAudit.NameOf).ToHashSet();
            foreach (var f in fields.Order())
                if (!audited.ContainsKey(f)) problems.Add($"{t.Name}.{f} is neither saved nor marked structural, derived or transient: add it to {t.Name}'s snapshot audit (docs/savestate.md)");
            foreach (var a in audited.Keys.Order())
                if (!fields.Contains(a)) problems.Add($"{t.Name}'s snapshot audit names {a}, which is not one of its fields");
            foreach (var (name, (kind, why)) in audited)
                if (kind != SaveKind.Saved && string.IsNullOrWhiteSpace(why)) problems.Add($"{t.Name}.{name} is {kind} without a reason");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The classes the state reaches through saved fields, from the session, the static world and the damage model.</summary>
    private static HashSet<Type> Reachable(List<string> problems)
    {
        var seen = new HashSet<Type>();
        var todo = new Queue<(Type T, string Via)>([(typeof(SimSession), "the session"), (typeof(StaticWorld), "the static world"), (typeof(Damage.DamageModel), "the damage model")]);
        IEnumerable<Type> Expand(Type t)
        {
            if (t.IsArray) { foreach (var x in Expand(t.GetElementType()!)) yield return x; yield break; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) foreach (var x in Expand(a)) yield return x;
            if (t.Assembly != Sim || t.IsEnum || t.IsInterface || Generated(t)) yield break;
            yield return t;
            foreach (var sub in Sim.GetTypes().Where(s => s != t && t.IsAssignableFrom(s) && !Generated(s))) yield return sub;
        }
        while (todo.TryDequeue(out var item))
        {
            var (t, via) = item;
            if (!seen.Add(t)) continue;
            // the plain value types of the maths (positions, rotations) are values, not state classes
            if (t.Namespace == typeof(Double3).Namespace) continue;
            bool hasFields = false;
            for (var k = t; k is not null && k != typeof(object) && k != typeof(ValueType); k = k.BaseType)
            {
                if (k.Assembly != Sim) break;
                var fields = SaveAudit.FieldsOf(k).ToList();
                if (fields.Count == 0) continue;
                hasFields = true;
                if (Audit.Of(k) is null) { problems.Add($"{k.FullName} (reached through {via}) has no snapshot audit"); continue; }
                foreach (var f in fields)
                    if (Audit.KindOf(f)?.Kind == SaveKind.Saved)
                        foreach (var u in Expand(f.FieldType)) todo.Enqueue((u, $"{k.Name}.{SaveAudit.NameOf(f)}"));
            }
            _ = hasFields;
        }
        return seen;
    }

    [Fact]
    public void EveryClassTheStateReachesIsAudited()
    {
        var problems = new List<string>();
        var seen = Reachable(problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Contains(typeof(Vehicles.Vehicle), seen);
        Assert.Contains(typeof(Fsm.FsmVm.Thread), seen);
        Assert.Contains(typeof(Damage.SpecialPart), seen);
    }

    // ---- the mutation check: a saved field that does not reach the snapshot's bytes is caught ----

    private sealed record Site(string Key, Func<object?> Get, Action<object?> Set, Type Type);

    private static object? Perturb(object? v, Type t)
    {
        var u = Nullable.GetUnderlyingType(t);
        if (u is not null) return v is null ? Perturb(Activator.CreateInstance(u), u) : null;
        return v switch
        {
            bool b => !b,
            int i => i == int.MaxValue ? 0 : i + 1,
            uint x => x ^ 1u,
            short s => (short)(s + 1),
            byte b => (byte)(b ^ 1),
            long l => l + 1,
            ulong x => x ^ 1UL,
            float f => f == 0 ? 0.375f : f * 1.5f + 0.125f,
            double d => d == 0 ? 0.375 : d * 1.5 + 0.125,
            Enum e => Enum.ToObject(t, Convert.ToInt64(e) ^ 1),
            Vector2 v2 => v2 + new Vector2(0.5f, 0.25f),
            Vector3 v3 => v3 + new Vector3(0.5f, 0.25f, 0.125f),
            Double3 d3 => new Double3(d3.X + 0.5, d3.Y + 0.25, d3.Z + 0.125),
            Rows r => new Rows(r.Up, r.Right, -r.Forward),
            string s => s + "x",
            _ => throw new NotSupportedException(t.Name),
        };
    }

    private static bool Perturbable(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u.IsPrimitive || u.IsEnum || u == typeof(Vector2) || u == typeof(Vector3) || u == typeof(Double3) || u == typeof(Rows) || u == typeof(string);
    }

    /// <summary>Every saved field of a plain kind on the live state, as a site that can be read and written (structs inside classes and arrays included).</summary>
    private static List<Site> Sites(SimSession s)
    {
        var sites = new List<Site>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Obj(object? o)
        {
            if (o is null || o is Delegate || o.GetType().IsValueType || !seen.Add(o)) return;
            var t = o.GetType();
            if (o is IDictionary d) { foreach (DictionaryEntry e in d) { Obj(e.Key); Value(e.Value); } return; }
            if (o is Array a)
            {
                var et = t.GetElementType()!;
                if (Perturbable(et) || (et.IsValueType && et.Assembly == Sim))
                    for (int i = 0; i < System.Math.Min(a.Length, 8); i++) Elem(a, i, et);
                foreach (var x in a) Value(x);
                return;
            }
            if (o is IEnumerable seq && t.Assembly != Sim) { foreach (var x in seq) Value(x); return; }
            if (t.Assembly != Sim) return;
            for (var k = t; k is not null && k.Assembly == Sim; k = k.BaseType)
                foreach (var f in SaveAudit.FieldsOf(k))
                {
                    if (Audit.KindOf(f)?.Kind != SaveKind.Saved) continue;
                    string key = $"{k.Name}.{SaveAudit.NameOf(f)}";
                    if (Perturbable(f.FieldType)) sites.Add(new Site(key, () => f.GetValue(o), v => f.SetValue(o, v), f.FieldType));
                    else if (f.FieldType.IsValueType && f.FieldType.Assembly == Sim) Struct(key, () => f.GetValue(o), v => f.SetValue(o, v), f.FieldType);
                    else Value(f.GetValue(o));
                }
        }
        void Value(object? v)
        {
            if (v is null) return;
            // a tuple's arrays (the FSM actors' instance ids) are the program's, not saved state
            if (v is ITuple tup) { for (int i = 0; i < tup.Length; i++) if (tup[i] is not Array) Value(tup[i]); return; }
            Obj(v);
        }
        void Struct(string outer, Func<object?> get, Action<object?> set, Type st)
        {
            foreach (var g in SaveAudit.FieldsOf(st))
            {
                if (Audit.KindOf(g)?.Kind != SaveKind.Saved) continue;
                string key = $"{st.Name}.{SaveAudit.NameOf(g)}";
                var inner = Nullable.GetUnderlyingType(g.FieldType) ?? g.FieldType;
                if (Perturbable(g.FieldType))
                    sites.Add(new Site(key, () => g.GetValue(get()), v => { var box = get(); g.SetValue(box, v); set(box); }, g.FieldType));
                else if (inner.IsValueType && inner.Assembly == Sim)
                {
                    if (g.GetValue(get()) is not null) Struct(key, () => g.GetValue(get()), v => { var box = get(); g.SetValue(box, v); set(box); }, inner);
                }
                else Value(g.GetValue(get()));
            }
        }
        void Elem(Array a, int i, Type et)
        {
            if (Perturbable(et)) sites.Add(new Site($"{et.Name}[]", () => a.GetValue(i), v => a.SetValue(v, i), et));
            else Struct($"{et.Name}[]", () => a.GetValue(i), v => a.SetValue(v, i), et);
        }
        Obj(s);
        Obj(s.World.Rand);
        foreach (var v in s.AllCars()) Obj(v);
        if (s.World.Ground is IHasStatics hs) foreach (var o in hs.World.Objects.Where(o => !o.Pristine).Concat(hs.World.Objects.Take(3))) Obj(o);
        Obj(s.Combat?.Damage);
        return sites;
    }

    /// <summary>The saved fields tried on StateHash, and those whose change changed it.</summary>
    private static readonly HashSet<string> HashTried = [], HashSeen = [];

    private static HashSet<string> Mutate(SimSession s, ITestOutputHelper output, HashSet<string> verified)
    {
        var baseline = s.SnapshotBody();
        ulong hash0 = s.StateHash();
        foreach (var g in Sites(s).GroupBy(x => x.Key))
        {
            bool seen = false;
            foreach (var site in g.Take(6))
            {
                var was = site.Get();
                try { site.Set(Perturb(was, site.Type)); seen |= s.StateHash() != hash0; }
                catch (Exception) { seen = true; }
                finally { site.Set(was); }
                if (seen) break;
            }
            lock (HashTried) { HashTried.Add(g.Key); if (seen) HashSeen.Add(g.Key); }
        }
        var failed = new HashSet<string>();
        foreach (var g in Sites(s).GroupBy(x => x.Key))
        {
            if (verified.Contains(g.Key)) continue;
            foreach (var site in g.Take(6))
            {
                var was = site.Get();
                bool caught;
                try
                {
                    site.Set(Perturb(was, site.Type));
                    caught = !s.SnapshotBody().AsSpan().SequenceEqual(baseline);
                }
                catch (Exception) { caught = true; }
                finally { site.Set(was); }
                if (caught) { verified.Add(g.Key); break; }
            }
            if (!verified.Contains(g.Key)) failed.Add(g.Key);
        }
        Assert.True(s.SnapshotBody().AsSpan().SequenceEqual(baseline), "the mutation check left the state changed");
        return failed;
    }

    /// <summary>
    /// Sessions in the middle of things (a scripted mission with helicopters and wreckage, a melee in a fight, a
    /// network melee, a mission with gun emplacements); every saved field of a plain kind on their state is
    /// changed in turn, and the snapshot's bytes must change with it.
    /// </summary>
    [Fact]
    public void EverySavedFieldReachesTheSnapshot()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var verified = new HashSet<string>();
        var stillFailing = new HashSet<string>();
        void Session(SessionBuild b, int ticks, Action<SimSession>? before = null, IReadOnlyList<(string, int)>? players = null, bool search = false)
        {
            var (s, setup) = SnapshotRig.Make(l, b, SimRate.R20, 3, players);
            before?.Invoke(s);
            for (int i = 0; i < ticks; i++) { SnapshotRig.Tick(s); if (i < ticks - 1) SnapshotRig.TakeEvents(s); }
            // a CB line on the air, whatever the mission's own timing left queued at this tick
            if (setup.Script?.Host.Cb is { Empty: true } cb) { SnapshotRig.TakeEvents(s); cb.Say(CbRadio.SpeakerDiedLine, null, 1); SnapshotRig.Tick(s); }
            // a search in progress: they last a few frames (the planner's 1000-node pool), so one is started here
            if (search && setup.Script?.Host.Entities.FirstOrDefault(e => e.Vehicle is not null && e.Search is null) is { } planner)
            {
                var at = planner.Position;
                planner.Search = Ai.PathSearch.Begin(new Vector2(at.X, at.Z), new Vector2(at.X + 3000, at.Z));
                Assert.False(planner.Search.Step(setup.Ground, true));
            }
            // wreckage: some structures hit, one destroyed whole
            var objs = setup.Statics.Objects.Where(o => o.Nodes.Count > 1 && o.Vehicle is null).Take(3).ToList();
            foreach (var o in objs.Take(2)) setup.Damage.StructureSlot(o, o.Nodes.Count - 1, new Weapons.DamagePacket { Mask = 2, Ch = { [1] = 1 } });
            if (objs.Count > 2) setup.Damage.DestroyStructure(objs[2]);
            // one event of every kind the views have not taken yet
            var car = s.World.Vehicles[0];
            var w = s.AllCars().Select(v => v.Armory?.LoadOrder.FirstOrDefault()).FirstOrDefault(x => x is not null);
            var so = setup.Statics.Objects[0];
            setup.Combat.Events.AddRange([new Weapons.Impact("xbulg1.xdf", "wmgr2.wav", Vector3.One, car, 1, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
                new Weapons.Impact("x", "y", Vector3.Zero, so), new Weapons.WeaponClick(car, "wclick.wav")]);
            if (w is not null) setup.Combat.Events.Add(new Weapons.ShotFired(s.AllCars().First(v => v.Armory?.LoadOrder.FirstOrDefault() == w), w, Vector3.One, Vector3.UnitZ));
            // a cluster bomb's bomblets still to go off (their round has left the round list)
            if (w?.Def.Ordnance is { } ord)
            {
                var shooter = s.AllCars().First(v => v.Armory?.LoadOrder.FirstOrDefault() == w);
                var bl = new Weapons.Bomblets { Round = new Weapons.Round { Kind = 5, Shooter = shooter, Def = w.Def, Ord = ord, Pos = Vector3.One, Count = 1, Damage = 45 }, Left = 2 };
                bl.Live[0] = bl.Live[1] = bl.Live[2] = true;
                bl.At[1] = new Vector3(3, 0, 4); bl.Due[2] = 1.5f;
                setup.Combat.Clusters.Add(bl);
            }
            setup.Damage.Events.AddRange([new Damage.VehicleKilled(car, true), new Damage.VehicleWrecked(car), new Damage.TyreBlown(car, 2), new Damage.PartFlung(car, 3, Vector3.One),
                new Damage.Debris(Vector3.One, Vector3.UnitY), new Damage.StructureDestroyed(so, [-1, 2]), new Damage.ZoneDamaged(car, 1, 2), new Damage.EngineStarted(car),
                new Damage.EngineStopped(car), new Damage.IgnitionStarted(car, "ign.wav"), new Damage.SignKnocked(so, Vector3.UnitY)]);
            stillFailing.UnionWith(Mutate(s, output, verified));
            stillFailing.ExceptWith(verified);
        }
        string[] fighters = ["s05vdl02.vcf", "s02vcm01.vcf", "s05vjs01.vcf"];
        Session(SnapshotRig.BuildOf(dir, "t05"), 1300);
        Session(SnapshotRig.BuildOf(dir, "t07"), 300);
        Session(SnapshotRig.BuildOf(dir, "m01", "s05vpp01.vcf", melee: fighters), 700);
        Session(SnapshotRig.BuildOf(dir, "m02", melee: ["s03cg02.vcf"], rules: new MeleeRules { Network = true }), 600, players: [("s05vpp01.vcf", 0), ("s01fp01.vcf", 0)],
            before: s => { s.Melee!.AddBot("s01fp01.vcf", "Bot (bot)", 0, BotSkill.Normal); s.AssignIds(); });
        foreach (var m in new[] { "s01", "s04", "t11", "t16" }) Session(SnapshotRig.BuildOf(dir, m), 1500, search: m == "s04");
        // a co-op mission (the seats, the per-human threads and counts), a seat waiting for its respawn
        Session(SnapshotRig.BuildOf(dir, "t01", coop: CoopSetup.Of(3)), 700,
            before: s => s.FrameDone += () => { if (s.Ticks == 650) (s.Combat!.Damage as Damage.DamageModel)!.Destroy(s.Script!.Host.Coop!.Seats[2].Car); });
        output.WriteLine($"{verified.Count} saved fields reach the snapshot");
        // StateHash is the snapshot's body bar the views' events: every other saved field changes it
        var eventTypes = typeof(Weapons.CombatEvent).Assembly.GetTypes().Where(t => typeof(Weapons.CombatEvent).IsAssignableFrom(t) || typeof(Damage.DamageEvent).IsAssignableFrom(t)).Select(t => t.Name + ".").ToList();
        var hashGaps = HashTried.Except(HashSeen).Where(k => !eventTypes.Any(k.StartsWith)).Order().ToList();
        Assert.True(hashGaps.Count == 0, "saved fields StateHash does not cover: " + string.Join(", ", hashGaps));
        // the Nitro Pack's own (CTF flags, race laps) only with its install
        var nitroOnly = new HashSet<string> { "CtfFlag.State", "CtfFlag.Pos", "RaceTracker._cur", "RaceTracker.Laps" };
        if (SnapshotRig.NitroInstall is { } nd)
        {
            var nl = new SnapshotRig.Loaded(nd);
            foreach (var (mission, rules) in new[] { ("n11", new MeleeRules { Network = true, Kind = MeleeKind.Ctf, TeamPlay = true }), ("n21", new MeleeRules { Network = true, Kind = MeleeKind.Race }) })
            {
                var (s, _) = SnapshotRig.Make(nl, SnapshotRig.BuildOf(nd, mission, melee: [], rules: rules), SimRate.R20, 3, [("vppirna1.vcf", 1), ("vcgcour1.vcf", 2)]);
                for (int i = 0; i < 400; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
                stillFailing.UnionWith(Mutate(s, output, verified));
                stillFailing.ExceptWith(verified);
            }
        }
        else stillFailing.ExceptWith(nitroOnly);
        // every saved field of a plain kind of every audited class, met on a live object
        var unmet = Audit.Types.SelectMany(t => Audit.Of(t)!.Where(kv => kv.Value.Kind == SaveKind.Saved).Select(kv => (t, kv.Key)))
            .Where(x => SaveAudit.FieldsOf(x.t).FirstOrDefault(f => SaveAudit.NameOf(f) == x.Key) is { } f && Perturbable(f.FieldType))
            .Select(x => $"{x.t.Name}.{x.Key}").Where(k => !verified.Contains(k) && (SnapshotRig.NitroInstall is not null || !nitroOnly.Contains(k))).Order().ToList();
        foreach (var u in unmet) output.WriteLine($"not met on a live object: {u}");
        Assert.True(stillFailing.Count == 0, "saved fields whose change does not reach the snapshot: " + string.Join(", ", stillFailing.Order()));
        Assert.True(unmet.Count == 0, "saved fields no test session had a live object of: " + string.Join(", ", unmet));
    }

    /// <summary>The types the state dump met on a live session are all audited (the classes held in object-typed fields too).</summary>
    [Fact]
    public void TheLiveStateHoldsOnlyAuditedClasses()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "m01", "s05vpp01.vcf", melee: ["s05vdl02.vcf", "s02vcm01.vcf"]), SimRate.R20, 3);
        for (int i = 0; i < 600; i++) SnapshotRig.Tick(s);
        var dump = new StateDump();
        dump.Dump(s);
        var missing = dump.SimTypes.Where(t => !t.IsValueType || t.Namespace != typeof(Double3).Namespace)
            .Where(t => Audit.Of(t) is null && SaveAudit.FieldsOf(t).Any()).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, "unaudited classes in the live state: " + string.Join(", ", missing));
    }
}
