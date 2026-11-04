using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Fsm;
using I76.Sim.Save;

namespace I76.Sim.Missions;

public sealed partial class Entity
{
    internal static void AuditFields(SaveAudit a)
    {
        const string placement = "the placement it stands for; a melee's entities are made again from the snapshot's entity table";
        a.For<Entity>()
            .Structural(placement, nameof(Name), nameof(Id), nameof(Type), nameof(ObjectFlags), nameof(Vehicle), nameof(Static))
            .Structural("set on a bot's entity every frame by its game", nameof(DetourLimit), nameof(NavReach))
            .Saved(nameof(OwnPath))
            .Saved(nameof(StaticPosition), nameof(StaticRot), nameof(_dead), nameof(Removed),
                nameof(ShotTick), nameof(RammedTick), nameof(AttackedTick), nameof(LastRammer), nameof(LastShooter), nameof(LastAttacker), nameof(KilledByPlayer),
                nameof(Order), nameof(OrderTarget), nameof(FollowRef), nameof(Path), nameof(OrderSpeed), nameof(FollowF1), nameof(FollowF2), nameof(FollowFlag),
                nameof(Arrived), nameof(AtFollow), nameof(Ai), nameof(Stack), nameof(Depth), nameof(LastTarget), nameof(NavPath), nameof(NavIndex),
                nameof(Waypoints), nameof(WaypointIndex), nameof(PlanState), nameof(ReachedLeg), nameof(ReachedEnd), nameof(WeaveTimer), nameof(Weave),
                nameof(LastDamageTime), nameof(SkillA), nameof(SkillB), nameof(Aggression), nameof(Avoid), nameof(MaxAttackers),
                nameof(TacticDeadline), nameof(AttackCommit), nameof(Road), nameof(TacticStage), nameof(SavedForward), nameof(OnDeckMode), nameof(LosSeen),
                nameof(EscapeTimer), nameof(AvoidSkip), nameof(AvoidSpeed), nameof(BackSteer), nameof(AvoidStart), nameof(ImpactTime), nameof(Obstacle),
                nameof(AvoidSteer), nameof(BackHeading), nameof(BackHeight), nameof(ClearanceRing), nameof(StallTime), nameof(BackStart), nameof(BackFarthest), nameof(PlanStamp), nameof(SnapshotPath),
                nameof(WaypointBridge), nameof(Search), nameof(Controls), nameof(ControlDone), nameof(ScriptDriven), nameof(Lights), nameof(Siren),
                nameof(CoopAim), nameof(KillerSeat));
        a.For<AiBlock>().Saved(nameof(AiBlock.Skill), nameof(AiBlock.AccelFactor), nameof(AiBlock.CommandSpeed), nameof(AiBlock.FireAccuracy), nameof(AiBlock.FireSkill));
        a.For<RoadPos>().Saved(nameof(RoadPos.Idx), nameof(RoadPos.Dir), nameof(RoadPos.Band), nameof(RoadPos.Cls), nameof(RoadPos.A960), nameof(RoadPos.Off),
            nameof(RoadPos.Heading), nameof(RoadPos.Seg));
        a.For<RoadPiece>().Structural("the mission's roads (a road record names its piece by its place in them)", nameof(RoadPiece.Road), nameof(RoadPiece.Points),
            nameof(RoadPiece.Left), nameof(RoadPiece.Centre), nameof(RoadPiece.Next), nameof(RoadPiece.Prev), nameof(RoadPiece.End));
    }

    internal void Save(SaveWriter w, IReadOnlyList<RoadPiece>? pieces)
    {
        w.V3(StaticPosition); w.Rows(StaticRot); w.Bool(_dead); w.Bool(Removed);
        w.Bool(OwnPath is not null);
        if (OwnPath is not null) { w.VarU((ulong)OwnPath.Count); foreach (var q in OwnPath) w.V3(q); }
        w.Int(ShotTick); w.Int(RammedTick); w.Int(AttackedTick); w.Ent(LastRammer); w.Ent(LastShooter); w.Ent(LastAttacker); w.Bool(KilledByPlayer);
        w.NInt(Order is { } o ? (int)o : null); w.Ent(OrderTarget); w.Ent(FollowRef); w.Path(Path); w.F32(OrderSpeed); w.F32(FollowF1); w.F32(FollowF2); w.Int(FollowFlag);
        w.Bool(Arrived); w.Bool(AtFollow);
        w.F32(Ai.Skill); w.F32(Ai.AccelFactor); w.F32(Ai.CommandSpeed); w.F32(Ai.FireAccuracy); w.F32(Ai.FireSkill);
        w.Ints(Stack); w.Int(Depth); w.Ent(LastTarget); w.Path(NavPath); w.Int(NavIndex);
        w.VarU((ulong)Waypoints.Count); foreach (var p in Waypoints) w.V3(p);
        w.Int(WaypointIndex); w.Int(PlanState); w.Bool(ReachedLeg); w.Bool(ReachedEnd); w.F32(WeaveTimer); w.Bool(Weave);
        w.F32(LastDamageTime); w.Int(SkillA); w.Int(SkillB); w.Int(Aggression); w.Bool(Avoid); w.Int(MaxAttackers);
        w.F32(TacticDeadline); w.Ent(AttackCommit);
        w.Int(Road.Idx); w.Int(Road.Dir); w.Int(Road.Band); w.Int(Road.Cls); w.F32(Road.A960); w.F32(Road.Off); w.F32(Road.Heading);
        w.Int(Road.Seg is null ? -1 : pieces is null ? throw new InvalidOperationException("snapshot: a road piece without roads") : IndexOf(pieces, Road.Seg));
        w.Int(TacticStage); w.V3(SavedForward); w.Int(OnDeckMode); w.F32(LosSeen);
        w.F32(EscapeTimer); w.Bool(AvoidSkip); w.F32(AvoidSpeed); w.F32(BackSteer); w.F32(AvoidStart); w.F32(ImpactTime); w.Ent(Obstacle);
        w.F32(AvoidSteer); w.F32(BackHeading); w.F32(BackHeight);
        foreach (float c in ClearanceRing) w.F32(c);
        w.F32(StallTime); w.V3(BackStart); w.F32(BackFarthest);
        w.F32(PlanStamp); w.V3(SnapshotPath[0]);
        w.VarU((ulong)WaypointBridge.Count); foreach (bool b in WaypointBridge) w.Bool(b);
        w.Bool(Search is not null);
        Search?.Save(w);
        w.VarU((ulong)Controls.Count);
        foreach (var c in Controls) { w.F32(c.BrAccel); w.F32(c.Steer); w.F32(c.Reverse); w.Int(c.EBrake); }
        w.Bool(ControlDone); w.Bool(ScriptDriven); w.Bool(Lights); w.Bool(Siren); w.Ent(CoopAim); w.Int(KillerSeat);
    }

    private static int IndexOf(IReadOnlyList<RoadPiece> pieces, RoadPiece p)
    {
        for (int i = 0; i < pieces.Count; i++) if (ReferenceEquals(pieces[i], p)) return i;
        throw new InvalidOperationException("snapshot: a road piece not in the mission's roads");
    }

    internal void Load(SaveReader r, IReadOnlyList<RoadPiece>? pieces, BridgeLinks? links)
    {
        StaticPosition = r.V3(); StaticRot = r.Rows(); _dead = r.Bool(); Removed = r.Bool();
        if (r.Bool()) { OwnPath = []; for (int k = r.Count(); k > 0; k--) OwnPath.Add(r.V3()); } else OwnPath = null;
        ShotTick = r.Int(); RammedTick = r.Int(); AttackedTick = r.Int(); LastRammer = r.Ent(); LastShooter = r.Ent(); LastAttacker = r.Ent(); KilledByPlayer = r.Bool();
        Order = r.NInt() is { } o ? (AiOrder)o : null; OrderTarget = r.Ent(); FollowRef = r.Ent(); Path = r.Path(); OrderSpeed = r.F32(); FollowF1 = r.F32(); FollowF2 = r.F32(); FollowFlag = r.Int();
        Arrived = r.Bool(); AtFollow = r.Bool();
        Ai.Skill = r.F32(); Ai.AccelFactor = r.F32(); Ai.CommandSpeed = r.F32(); Ai.FireAccuracy = r.F32(); Ai.FireSkill = r.F32();
        Stack.Clear(); Stack.AddRange(r.Ints()); Depth = r.Int(); LastTarget = r.Ent(); NavPath = r.Path(); NavIndex = r.Int();
        Waypoints.Clear(); for (int k = r.Count(); k > 0; k--) Waypoints.Add(r.V3());
        WaypointIndex = r.Int(); PlanState = r.Int(); ReachedLeg = r.Bool(); ReachedEnd = r.Bool(); WeaveTimer = r.F32(); Weave = r.Bool();
        LastDamageTime = r.F32(); SkillA = r.Int(); SkillB = r.Int(); Aggression = r.Int(); Avoid = r.Bool(); MaxAttackers = r.Int();
        TacticDeadline = r.F32(); AttackCommit = r.Ent();
        Road.Idx = r.Int(); Road.Dir = r.Int(); Road.Band = r.Int(); Road.Cls = r.Int(); Road.A960 = r.F32(); Road.Off = r.F32(); Road.Heading = r.F32();
        int seg = r.Int();
        Road.Seg = seg < 0 ? null : pieces is not null && seg < pieces.Count ? pieces[seg] : throw r.Mismatch($"road piece {seg}");
        TacticStage = r.Int(); SavedForward = r.V3(); OnDeckMode = r.Int(); LosSeen = r.F32();
        EscapeTimer = r.F32(); AvoidSkip = r.Bool(); AvoidSpeed = r.F32(); BackSteer = r.F32(); AvoidStart = r.F32(); ImpactTime = r.F32(); Obstacle = r.Ent();
        AvoidSteer = r.F32(); BackHeading = r.F32(); BackHeight = r.F32();
        for (int i = 0; i < ClearanceRing.Length; i++) ClearanceRing[i] = r.F32();
        StallTime = r.F32(); BackStart = r.V3(); BackFarthest = r.F32();
        PlanStamp = r.F32(); SnapshotPath[0] = r.V3();
        WaypointBridge.Clear(); for (int k = r.Count(); k > 0; k--) WaypointBridge.Add(r.Bool());
        Search = r.Bool() ? PathSearch.Load(r, links) : null;
        Controls.Clear();
        for (int k = r.Count(); k > 0; k--) Controls.Enqueue((r.F32(), r.F32(), r.F32(), r.Int()));
        ControlDone = r.Bool(); ScriptDriven = r.Bool(); Lights = r.Bool(); Siren = r.Bool(); CoopAim = r.Ent(); KillerSeat = r.Int();
    }
}
