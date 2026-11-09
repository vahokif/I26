using System.Numerics;
using I76.Sim.Save;
using I76.Sim.Vehicles;

namespace I76.Sim
{
    public sealed partial class SimWorld
    {
        internal static void AuditFields(SaveAudit a)
        {
            a.For<SimWorld>()
                .Saved(nameof(Vehicles), nameof(Player), nameof(Humans), nameof(Step), nameof(Ai), nameof(Time), nameof(CameraRange))
                .Structural("the mission build's ground and rand stream (whose seed the session saves)", nameof(Ground), nameof(Rand))
                .Structural("the session's options", nameof(Far))
                .Structural("the mission build's co-op flag", nameof(Coop))
                .Structural("the damage model's and the game layer's hooks, set again by the build and the host", nameof(CarContact), nameof(External),
                    nameof(HeliDamage), nameof(ExternalFrame))
                .Structural("the script host's camera, saved with the host", nameof(Scene));
            a.For<VehicleStep>()
                .Saved(nameof(VehicleStep.FrameDelta), nameof(VehicleStep.Wrecked))
                .Structural("the mission build's ground, rand stream and damage model", "ground", "rand", nameof(VehicleStep.Damage), nameof(VehicleStep.ExternalContacts))
                .Structural("the session's options, set by the build", nameof(VehicleStep.NetSession))
                .Structural("the session's cut-scene test, set by the session", nameof(VehicleStep.ScriptDrives))
                .Transient("a per-tick output (sounds, the views), cleared at the next tick", nameof(VehicleStep.ObjectHits), nameof(VehicleStep.Impacts),
                    nameof(VehicleStep.CarHits), nameof(VehicleStep.Landings), nameof(VehicleStep.DriveOns), nameof(VehicleStep.Honks));
        }

        internal void Save(SaveWriter w)
        {
            w.F32(Time); w.F32(CameraRange); w.F32(Step.FrameDelta); w.Bool(Step.Wrecked);
            // the cars another model drives (the game's placeholder chase): which still are, and their blocks; their targets are the host's
            var ai = Ai.OrderBy(kv => kv.Key.NetId).ToList();
            w.VarU((ulong)ai.Count);
            foreach (var (v, (b, _)) in ai) { w.Car(v); w.F32(b.Skill); w.F32(b.AccelFactor); w.F32(b.CommandSpeed); w.F32(b.FireAccuracy); w.F32(b.FireSkill); }
        }

        internal void Load(SaveReader r)
        {
            Time = r.F32(); CameraRange = r.F32(); Step.FrameDelta = r.F32(); Step.Wrecked = r.Bool();
            var keep = new HashSet<Vehicle>();
            for (int k = r.Count(); k > 0; k--)
            {
                var v = r.Car()!;
                if (!Ai.TryGetValue(v, out var e)) throw r.Mismatch($"car {v.NetId:x} is AI-driven in the snapshot, not in the rebuilt mission");
                (e.Ai.Skill, e.Ai.AccelFactor, e.Ai.CommandSpeed, e.Ai.FireAccuracy, e.Ai.FireSkill) = (r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
                keep.Add(v);
            }
            foreach (var v in Ai.Keys.Where(v => !keep.Contains(v)).ToList()) Ai.Remove(v);
        }
    }
}

namespace I76.Sim.Collision
{
    public sealed partial class StaticObject
    {
        internal static void AuditFields(SaveAudit a)
        {
            const string geometry = "its geometry after node deaths and gate slides, recomputed on restore (Refresh, Collapse)";
            a.For<StaticObject>()
                .Saved(nameof(Destroyed), nameof(Hp), nameof(MaxHp), nameof(Nodes), nameof(NetId), nameof(Collapsed), nameof(Revision), nameof(_gates))
                // the bounds too: a Refresh that leaves no geometry keeps the last ones (they still place the root for a splash)
                .Saved(nameof(Centre), nameof(Radius), nameof(BoundRadius))
                .Derived(geometry, nameof(_intact), nameof(_uncollapsed), nameof(Polys), nameof(Surfaces), nameof(Boxes), nameof(Spheres), nameof(MeshBounds))
                .Structural("the placement's, from the install", nameof(Name), nameof(Type), nameof(Owner), nameof(Vehicle), nameof(Marker), nameof(SphereRadius),
                    nameof(Explosion), nameof(Sound), nameof(Remains), nameof(Deck));
            a.For<StructureNode>()
                .Saved(nameof(StructureNode.Hp), nameof(StructureNode.MaxHp), nameof(StructureNode.Destroyed), nameof(StructureNode.Gate))
                .Structural("the structure's .sdf", nameof(StructureNode.Name), nameof(StructureNode.Parent), nameof(StructureNode.Type), nameof(StructureNode.Flags),
                    nameof(StructureNode.Base));
            a.For<GateMotion>()
                .Saved(nameof(GateMotion.X), nameof(GateMotion.Target), nameof(GateMotion.Moving))
                .Structural("the gate's record and placement", nameof(GateMotion.Offset), nameof(GateMotion.Delta), nameof(GateMotion.World), nameof(GateMotion.Rate));
            a.For<StaticWorld>()
                .Saved(nameof(StaticWorld.Objects))
                .Derived("the placements with a gate, found when first asked for", "_gated");
        }

        /// <summary>As the mission build left it: nothing hit, destroyed, swapped or slid.</summary>
        internal bool Pristine =>
            Hp == MaxHp && !Destroyed && !Collapsed && Revision == 0 && !_gates && _intact is null
            && Nodes.All(n => n.Hp == n.MaxHp && !n.Destroyed && (n.Gate is not { } g || (g.X == 0 && g.Target == 1 && !g.Moving)));

        /// <summary>
        /// Back to <see cref="Pristine"/>: a snapshot leaves out what was pristine when it was taken, so a restore into the
        /// same session puts back one hit, destroyed, swapped or slid since.
        /// </summary>
        internal void ResetToPristine()
        {
            Hp = MaxHp; Destroyed = false;
            foreach (var n in Nodes)
            {
                n.Hp = n.MaxHp; n.Destroyed = false;
                if (n.Gate is { } g) { g.X = 0; g.Target = 1; g.Moving = false; }
            }
            if ((_intact ?? _uncollapsed) is { } i)
            {
                Polys.Clear(); Polys.AddRange(i.Polys); Surfaces.Clear(); Surfaces.AddRange(i.Surfaces);
                Boxes.Clear(); Boxes.AddRange(i.Boxes); Spheres.Clear(); Spheres.AddRange(i.Spheres);
                MeshBounds.Clear(); MeshBounds.AddRange(i.MeshBounds);
                (Centre, Radius, BoundRadius) = (i.Centre, i.Radius, i.BoundRadius);
            }
            _intact = null; _uncollapsed = null; _gates = false;
            Collapsed = false; Revision = 0;
        }

        internal void Save(SaveWriter w)
        {
            w.Int(Hp); w.Int(MaxHp); w.Bool(Destroyed); w.Bool(Collapsed); w.Int(Revision); w.Bool(_gates); w.Bool(_intact is not null);
            w.V3(Centre); w.F32(Radius); w.F32(BoundRadius);
            w.VarU((ulong)Nodes.Count);
            foreach (var n in Nodes)
            {
                w.Int(n.Hp); w.Int(n.MaxHp); w.Bool(n.Destroyed);
                if (n.Gate is { } g) { w.F32(g.X); w.F32(g.Target); w.Bool(g.Moving); }
            }
        }

        /// <summary>
        /// The state, then the geometry it makes: Refresh if the original had rebuilt it from its nodes, else the whole swap if it had
        /// collapsed; then the bounds as they were (a Refresh that leaves nothing keeps the previous ones, so they carry history).
        /// </summary>
        internal void Load(SaveReader r)
        {
            Hp = r.Int(); MaxHp = r.Int(); Destroyed = r.Bool();
            bool collapsed = r.Bool(); int revision = r.Int(); _gates = r.Bool(); bool refreshed = r.Bool();
            var (centre, radius, bound) = (r.V3(), r.F32(), r.F32());
            if (r.Count() != Nodes.Count) throw r.Mismatch($"structure {Name} has {Nodes.Count} nodes");
            foreach (var n in Nodes)
            {
                n.Hp = r.Int(); n.MaxHp = r.Int(); n.Destroyed = r.Bool();
                if (n.Gate is { } g) { g.X = r.F32(); g.Target = r.F32(); g.Moving = r.Bool(); }
            }
            if (refreshed) Refresh();
            else if (collapsed) Collapse();
            Collapsed = collapsed;
            Revision = revision;
            (Centre, Radius, BoundRadius) = (centre, radius, bound);
        }
    }
}

namespace I76.Sim.Damage
{
    public sealed partial class DamageModel
    {
        internal static void AuditFields(SaveAudit a)
        {
            a.For<DamageModel>()
                .Saved(nameof(Events), nameof(UnlimitedArmour), nameof(UnlimitedChassis))
                .Structural("the mission build's world and combat", nameof(World), nameof(Combat))
                .Structural("the mission build's co-op damage factor (the snapshot's SessionBuild.Coop)", nameof(CoopDamageFactor))
                .Transient("the hooks of the script host, the melee, the salvage and the views (set again by whoever builds the session)",
                    nameof(Recorded), nameof(Killed), nameof(Salvageable), nameof(KilledBy), nameof(Damaging));
            a.For<VehicleKilled>().Saved(nameof(VehicleKilled.Vehicle), nameof(VehicleKilled.DriverKilled));
            a.For<VehicleWrecked>().Saved(nameof(VehicleWrecked.Vehicle));
            a.For<TyreBlown>().Saved(nameof(TyreBlown.Vehicle), nameof(TyreBlown.Slot));
            a.For<PartFlung>().Saved(nameof(PartFlung.Vehicle), nameof(PartFlung.Slot), nameof(PartFlung.Velocity));
            a.For<Debris>().Saved(nameof(Debris.At), nameof(Debris.Velocity));
            a.For<StructureDestroyed>().Saved(nameof(StructureDestroyed.Object), nameof(StructureDestroyed.Booms));
            a.For<ZoneDamaged>().Saved(nameof(ZoneDamaged.Vehicle), nameof(ZoneDamaged.Zone), nameof(ZoneDamaged.Level));
            a.For<EngineStarted>().Saved(nameof(EngineStarted.Vehicle));
            a.For<EngineStopped>().Saved(nameof(EngineStopped.Vehicle));
            a.For<IgnitionStarted>().Saved(nameof(IgnitionStarted.Vehicle), nameof(IgnitionStarted.Sound));
            a.For<SignKnocked>().Saved(nameof(SignKnocked.Object), nameof(SignKnocked.Velocity));
        }

        internal void Save(SaveWriter w, bool events = true)
        {
            w.Bool(UnlimitedArmour); w.Bool(UnlimitedChassis);
            w.VarU(events ? (ulong)Events.Count : 0);
            foreach (var e in events ? Events : [])
                switch (e)
                {
                    case VehicleKilled k: w.U8(1); w.Car(k.Vehicle); w.Bool(k.DriverKilled); break;
                    case VehicleWrecked x: w.U8(2); w.Car(x.Vehicle); break;
                    case TyreBlown t: w.U8(3); w.Car(t.Vehicle); w.Int(t.Slot); break;
                    case PartFlung p: w.U8(4); w.Car(p.Vehicle); w.Int(p.Slot); w.V3(p.Velocity); break;
                    case Debris d: w.U8(5); w.V3(d.At); w.V3(d.Velocity); break;
                    case StructureDestroyed s: w.U8(6); w.Obj(s.Object); w.Ints(s.Booms); break;
                    case ZoneDamaged z: w.U8(7); w.Car(z.Vehicle); w.Int(z.Zone); w.Int(z.Level); break;
                    case EngineStarted es: w.U8(8); w.Car(es.Vehicle); break;
                    case EngineStopped st: w.U8(9); w.Car(st.Vehicle); break;
                    case IgnitionStarted ig: w.U8(10); w.Car(ig.Vehicle); w.Str(ig.Sound); break;
                    case SignKnocked sk: w.U8(11); w.Obj(sk.Object); w.V3(sk.Velocity); break;
                    default: throw new InvalidOperationException($"snapshot: no tag for a {e.GetType().Name}");
                }
        }

        internal void Load(SaveReader r)
        {
            UnlimitedArmour = r.Bool(); UnlimitedChassis = r.Bool();
            Events.Clear();
            for (int k = r.Count(); k > 0; k--)
                Events.Add(r.U8() switch
                {
                    1 => new VehicleKilled(r.Car()!, r.Bool()),
                    2 => new VehicleWrecked(r.Car()!),
                    3 => new TyreBlown(r.Car()!, r.Int()),
                    4 => new PartFlung(r.Car()!, r.Int(), r.V3()),
                    5 => new Debris(r.V3(), r.V3()),
                    6 => new StructureDestroyed((Collision.StaticObject)r.Obj()!, r.Ints()),
                    7 => new ZoneDamaged(r.Car()!, r.Int(), r.Int()),
                    8 => new EngineStarted(r.Car()!),
                    9 => new EngineStopped(r.Car()!),
                    10 => new IgnitionStarted(r.Car()!, r.Str()),
                    11 => new SignKnocked((Collision.StaticObject)r.Obj()!, r.V3()),
                    var t => throw new SnapshotFormatException($"bad damage event tag {t}"),
                });
        }
    }
}
