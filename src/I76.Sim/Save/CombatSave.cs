using System.Numerics;
using I76.Sim.Collision;
using I76.Sim.Save;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>
/// Combat's state: its settings, the radars (targets, range, camera flag, the E key's 5 s timer, the per-arc
/// picks), every round in flight (mines and drops lying on the ground included), the flame streams, and the
/// events the views have not taken yet. A round's definition is its shooter's weapon's, saved as that weapon.
/// </summary>
public sealed partial class Combat
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<Combat>()
            .Saved(nameof(Rounds), nameof(Events), nameof(RadarTarget), nameof(StructureTarget), nameof(RadarRange), nameof(RadarCamera), nameof(Difficulty), nameof(UnlimitedAmmo),
                nameof(Unmodelled), nameof(MeleeActive), nameof(NetSession), nameof(_nextTimer), nameof(_arcs), nameof(RemoteSim), nameof(FrameDt),
                nameof(Flames), nameof(FlameHoldEnabled), nameof(Clusters))
            .Structural("the mission build's world, effects, damage sink and sides", nameof(World), "effects", nameof(Damage), nameof(Team), nameof(IsBot), nameof(FreeForAll))
            .Structural("the install's rules, set by the mission build", nameof(NitroRules))
            .Transient("the host's hook", nameof(HandgunShot))
            .Transient("a per-frame output for the sounds, rebuilt by every frame's aims", nameof(LockTone), nameof(LockedOn))
            .Transient("a network client's own predicted rounds and their hook: drawn only, never in a server's or a single player's state", nameof(PredictedRounds), nameof(PredictedShot))
            .Transient("set only inside PredictFrame and BuildFlameView, false between ticks", nameof(_cosmetic), nameof(_silent));
        a.For<Round>()
            .Saved(nameof(Round.Kind), nameof(Round.Shooter), nameof(Round.Def), nameof(Round.Pos), nameof(Round.Rot), nameof(Round.LocalVel), nameof(Round.Life),
                nameof(Round.Age), nameof(Round.Travelled), nameof(Round.Count), nameof(Round.Damage), nameof(Round.Target), nameof(Round.PrevPos), nameof(Round.Vel),
                nameof(Round.Launched), nameof(Round.Settled), nameof(Round.NetId))
            .Derived("the definition's ordnance", nameof(Round.Ord));
        a.For<Bomblets>()
            .Saved(nameof(Bomblets.Round), nameof(Bomblets.Left), nameof(Bomblets.Live), nameof(Bomblets.At), nameof(Bomblets.Due));
        a.For<Flame>()
            .Saved(nameof(Flame.Owner), nameof(Flame.Weapon), nameof(Flame.Style), nameof(Flame.Damage), nameof(Flame.Count), nameof(Flame.Fired), nameof(Flame.Built),
                nameof(Flame.PrevEnd), nameof(Flame.Cross), nameof(Flame.OwnerPos), nameof(Flame.OwnerRot), nameof(Flame.FirstHit));
        a.For<ShotFired>().Saved(nameof(ShotFired.Shooter), nameof(ShotFired.Weapon), nameof(ShotFired.Muzzle), nameof(ShotFired.Forward));
        a.For<Impact>().Saved(nameof(Impact.Effect), nameof(Impact.Sound), nameof(Impact.At), nameof(Impact.Victim), nameof(Impact.Kind), nameof(Impact.Hit),
            nameof(Impact.N), nameof(Impact.Dir));
        a.For<Explosion>().Saved(nameof(Explosion.Effect), nameof(Explosion.At));
        a.For<WeaponClick>().Saved(nameof(WeaponClick.Shooter), nameof(WeaponClick.Sound));
    }

    private static IEnumerable<KeyValuePair<Vehicle, T>> ById<T>(Dictionary<Vehicle, T> d) => d.OrderBy(kv => kv.Key.NetId);

    internal void Save(SaveWriter w, bool events = true)
    {
        w.Int(Difficulty); w.Bool(UnlimitedAmmo); w.Bool(MeleeActive); w.Bool(NetSession); w.Bool(RemoteSim); w.F32(FrameDt); w.Bool(FlameHoldEnabled);
        w.VarU((ulong)Unmodelled.Count); foreach (uint k in Unmodelled.Order()) w.U32(k);
        w.VarU((ulong)RadarTarget.Count); foreach (var (k, v) in ById(RadarTarget)) { w.Car(k); w.Car(v); }
        w.VarU((ulong)StructureTarget.Count); foreach (var (k, v) in ById(StructureTarget)) { w.Car(k); w.Obj(v); }
        w.VarU((ulong)RadarRange.Count); foreach (var (k, v) in ById(RadarRange)) { w.Car(k); w.Int(v); }
        w.VarU((ulong)RadarCamera.Count); foreach (var k in RadarCamera.OrderBy(v => v.NetId)) w.Car(k);
        w.VarU((ulong)_nextTimer.Count); foreach (var (k, v) in ById(_nextTimer)) { w.Car(k); w.F32(v); }
        w.VarU((ulong)_arcs.Count); foreach (var (k, v) in ById(_arcs)) { w.Car(k); foreach (var t in v) w.Car(t); }

        w.VarU((ulong)Rounds.Count);
        foreach (var r in Rounds)
        {
            w.Int(r.NetId); w.U32(r.Kind); w.Car(r.Shooter);
            w.Refs.WriteWeapon(w, r.Shooter, DefOwner(r));
            w.V3(r.Pos); w.Rows(r.Rot); w.V3(r.LocalVel); w.F32(r.Life); w.F32(r.Age); w.F32(r.Travelled); w.Int(r.Count); w.Int(r.Damage);
            w.Obj(r.Target); w.V3(r.PrevPos); w.V3(r.Vel); w.Bool(r.Launched); w.Bool(r.Settled);
        }
        w.VarU((ulong)Flames.Count);
        foreach (var f in Flames)
        {
            w.Car(f.Owner); w.Refs.WriteWeapon(w, f.Owner, f.Weapon); w.Int(f.Style); w.Int(f.Damage); w.Int(f.Count); w.Bool(f.Fired); w.Bool(f.Built);
            w.V3(f.PrevEnd); foreach (var p in f.Cross) w.V3(p); w.V3(f.OwnerPos); w.Rows(f.OwnerRot); w.Int(f.FirstHit);
        }
        // the bomblets' round has left the round list: its shooter, weapon, damage and count, its pose as it landed
        w.VarU((ulong)Clusters.Count);
        foreach (var b in Clusters)
        {
            var r = b.Round;
            w.Int(r.NetId); w.U32(r.Kind); w.Car(r.Shooter); w.Refs.WriteWeapon(w, r.Shooter, DefOwner(r));
            w.V3(r.Pos); w.Rows(r.Rot); w.Int(r.Count); w.Int(r.Damage); w.Obj(r.Target);
            w.Int(b.Left);
            for (int i = 0; i < Bomblets.Slots; i++) { w.Bool(b.Live[i]); w.V3(b.At[i]); w.F32(b.Due[i]); }
        }
        w.VarU(events ? (ulong)Events.Count : 0);
        if (events) foreach (var e in Events) WriteEvent(w, e);
    }

    /// <summary>The weapon whose definition a round carries: its shooter's (the one with that very definition).</summary>
    private static Weapon DefOwner(Round r)
    {
        var a = r.Shooter.Armory;
        var all = (a?.LoadOrder ?? []).Concat(a?.Handguns.OfType<Weapon>() ?? []);
        return all.FirstOrDefault(w => ReferenceEquals(w.Def, r.Def))
            ?? all.FirstOrDefault(w => w.Def.File == r.Def.File && w.Def.Class == r.Def.Class && w.Def.Tier == r.Def.Tier)
            ?? throw new InvalidOperationException($"snapshot: a round of {r.Def.File} whose shooter has no such weapon");
    }

    internal void Load(SaveReader r)
    {
        Difficulty = r.Int(); UnlimitedAmmo = r.Bool(); MeleeActive = r.Bool(); NetSession = r.Bool(); RemoteSim = r.Bool(); FrameDt = r.F32(); FlameHoldEnabled = r.Bool();
        Unmodelled.Clear(); for (int k = r.Count(); k > 0; k--) Unmodelled.Add(r.U32());
        RadarTarget.Clear(); for (int k = r.Count(); k > 0; k--) RadarTarget[r.Car()!] = r.Car();
        StructureTarget.Clear(); for (int k = r.Count(); k > 0; k--) StructureTarget[r.Car()!] = r.Obj() as StaticObject ?? throw r.Mismatch("a structure target that is not a structure");
        RadarRange.Clear(); for (int k = r.Count(); k > 0; k--) RadarRange[r.Car()!] = r.Int();
        RadarCamera.Clear(); for (int k = r.Count(); k > 0; k--) RadarCamera.Add(r.Car()!);
        _nextTimer.Clear(); for (int k = r.Count(); k > 0; k--) _nextTimer[r.Car()!] = r.F32();
        _arcs.Clear(); for (int k = r.Count(); k > 0; k--) _arcs[r.Car()!] = [r.Car(), r.Car(), r.Car(), r.Car()];

        Rounds.Clear();
        for (int k = r.Count(); k > 0; k--)
        {
            int id = r.Int(); uint kind = r.U32(); var shooter = r.Car()!;
            var def = r.Refs.ReadWeapon(r).Def;
            Rounds.Add(new Round
            {
                NetId = id, Kind = kind, Shooter = shooter, Def = def, Ord = def.Ordnance ?? throw r.Mismatch($"{def.File} has no ordnance"),
                Pos = r.V3(), Rot = r.Rows(), LocalVel = r.V3(), Life = r.F32(), Age = r.F32(), Travelled = r.F32(), Count = r.Int(), Damage = r.Int(),
                Target = r.Obj(), PrevPos = r.V3(), Vel = r.V3(), Launched = r.Bool(), Settled = r.Bool(),
            });
        }
        Flames.Clear();
        for (int k = r.Count(); k > 0; k--)
        {
            var f = new Flame { Owner = r.Car()!, Weapon = r.Refs.ReadWeapon(r), Style = r.Int() };
            f.Damage = r.Int(); f.Count = r.Int(); f.Fired = r.Bool(); f.Built = r.Bool();
            f.PrevEnd = r.V3(); for (int i = 0; i < f.Cross.Length; i++) f.Cross[i] = r.V3(); f.OwnerPos = r.V3(); f.OwnerRot = r.Rows(); f.FirstHit = r.Int();
            Flames.Add(f);
        }
        Clusters.Clear();
        for (int k = r.Count(); k > 0; k--)
        {
            int id = r.Int(); uint kind = r.U32(); var shooter = r.Car()!;
            var def = r.Refs.ReadWeapon(r).Def;
            var round = new Round
            {
                NetId = id, Kind = kind, Shooter = shooter, Def = def, Ord = def.Ordnance ?? throw r.Mismatch($"{def.File} has no ordnance"),
                Pos = r.V3(), Rot = r.Rows(), Count = r.Int(), Damage = r.Int(), Target = r.Obj(),
            };
            round.PrevPos = round.Pos;
            var b = new Bomblets { Round = round, Left = r.Int() };
            for (int i = 0; i < Bomblets.Slots; i++) { b.Live[i] = r.Bool(); b.At[i] = r.V3(); b.Due[i] = r.F32(); }
            Clusters.Add(b);
        }
        Events.Clear();
        for (int k = r.Count(); k > 0; k--) Events.Add(ReadEvent(r));
    }

    private void WriteEvent(SaveWriter w, CombatEvent e)
    {
        switch (e)
        {
            case ShotFired s: w.U8(1); w.Car(s.Shooter); w.Refs.WriteWeapon(w, s.Shooter, s.Weapon); w.V3(s.Muzzle); w.V3(s.Forward); break;
            case Impact i: w.U8(2); w.Str(i.Effect); w.Str(i.Sound); w.V3(i.At); w.Obj(i.Victim); w.U32(i.Kind); w.V3(i.Hit); w.V3(i.N); w.V3(i.Dir); break;
            case Explosion x: w.U8(3); w.Str(x.Effect.File); w.V3(x.At); break;
            case WeaponClick c: w.U8(4); w.Car(c.Shooter); w.Str(c.Sound); break;
            default: throw new InvalidOperationException($"snapshot: no tag for a {e.GetType().Name}");
        }
    }

    private CombatEvent ReadEvent(SaveReader r) => r.U8() switch
    {
        1 => new ShotFired(r.Car()!, r.Refs.ReadWeapon(r), r.V3(), r.V3()),
        2 => new Impact(r.Str() ?? "", r.Str() ?? "", r.V3(), r.Obj(), r.U32(), r.V3(), r.V3(), r.V3()),
        3 => r.Str() is var file && file is not null && effects(file) is { } fx ? new Explosion(fx, r.V3()) : throw r.Mismatch($"effect {file}"),
        4 => new WeaponClick(r.Car()!, r.Str() ?? ""),
        var t => throw new SnapshotFormatException($"bad combat event tag {t}"),
    };
}
