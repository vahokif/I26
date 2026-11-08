using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>The combat tactics, states 1–15.</summary>
public sealed partial class Ptable
{
    private RoadNetwork? Roads => host.Roads;

    // ---- enters: deadline, attacker slot, gear ----

    /// <summary>Road_ram.</summary>
    private void RoadRamEnter(Entity e, Entity? t) { e.TacticDeadline = Now + 5f; e.AttackCommit = t; e.OrderSpeed = 35f; }

    /// <summary>Rw_flee: the slot is kept with chance agg/4.</summary>
    private void RwFleeEnter(Entity e, Entity? t)
    {
        int r1 = Rand() % 4;
        e.TacticDeadline = IsVehicle(t) ? Now + Rand() % 5 + 3f : Now + Rand() % 4 + 1f;
        e.AttackCommit = r1 < e.Aggression ? t : null;
        e.OrderSpeed = 35f;
    }

    /// <summary>Road_side.</summary>
    private void RoadSideEnter(Entity e, Entity? t) { e.TacticDeadline = Now + Rand() % 15 + 25f; e.AttackCommit = t; e.OrderSpeed = 35f; }

    /// <summary>The dirt tactics' enters (dirt_ram dirt_brave dirt_gunner dirt_miner).</summary>
    private void DirtEnter(Entity e, Entity? t, float deadline)
    {
        e.TacticDeadline = deadline; e.AttackCommit = t; e.OrderSpeed = 35f;
        Forward(e.Vehicle!);
    }

    /// <summary>Dirt_standoff: first open the range.</summary>
    private void StandoffEnter(Entity e, Entity? t)
    {
        e.TacticDeadline = Now + 7f + Rand() % 10; e.TacticStage = 1; e.AttackCommit = t; e.OrderSpeed = 35f;
        Forward(e.Vehicle!);
    }

    /// <summary>The flee enters' road record: the own road point, heading for the longer part of the piece.</summary>
    private void FleeRoad(Entity e)
    {
        var R = e.Road;
        Roads?.Resolve(Pos(e), R);
        R.A960 = 0;
        if (R.Seg is { } s) R.Dir = R.Idx > s.Count / 2 ? -1 : 1;
    }

    /// <summary>Road_flee.</summary>
    private void RoadFleeEnter(Entity e, Entity? t)
    {
        int r1 = Rand() % 4;
        e.TacticDeadline = IsVehicle(t) ? Now + e.Aggression + Rand() % 5 + 6f : Now + Rand() % 4 + 1f;
        e.AttackCommit = r1 < e.Aggression ? t : null;
        e.OrderSpeed = 35f;
        FleeRoad(e);
    }

    /// <summary>R_rvs_flee: road_flee in reverse gear.</summary>
    private void ReverseFleeEnter(Entity e, Entity? t)
    {
        e.TacticDeadline = Now + 2f + Rand() % 2; e.AttackCommit = t; e.OrderSpeed = 35f;
        Reverse(e.Vehicle!);
        FleeRoad(e);
    }

    /// <summary>Rockford: reverse, stage 0, the forward row saved.</summary>
    private void RockfordEnter(Entity e, Entity? t)
    {
        e.TacticDeadline = Now + 7f; e.AttackCommit = t; e.TacticStage = 0;
        e.SavedForward = e.Rot.Forward;
        Reverse(e.Vehicle!);
    }

    /// <summary>Dirt_rvs: dirt_gunner in reverse.</summary>
    private void DirtReverseEnter(Entity e, Entity? t)
    {
        e.TacticDeadline = Now + 5f + Rand() % 4; e.AttackCommit = t; e.OrderSpeed = 20f;
        Reverse(e.Vehicle!);
    }

    /// <summary>Dirt_flee: a short burst away.</summary>
    private void DirtFleeEnter(Entity e, Entity? t)
    {
        int r1 = Rand() % 4;
        e.TacticDeadline = IsVehicle(t) ? Now + 0.3f + 0.1f * e.Aggression : Now + 0.5f;
        e.AttackCommit = r1 < e.Aggression ? t : null;
        e.OrderSpeed = 35f;
        Forward(e.Vehicle!);
    }

    /// <summary>Dirt_circle: no slot.</summary>
    private void CircleEnter(Entity e, Entity? _)
    {
        e.TacticDeadline = Now + 30f + Rand() % 5; e.AttackCommit = null; e.OrderSpeed = 35f;
        Forward(e.Vehicle!);
    }

    /// <summary>Tactic_skid: full lock on the side the wheel is on, handbrake, half a second.</summary>
    private void SkidEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        v.Steer = v.Steer >= 0 ? 1f : -1f;
        v.Handbrake = 1;
        e.EscapeTimer = Now + 0.5f;
        e.TacticStage = 1;
    }

    // ---- ticks ----

    /// <summary>The handbrake for the push frame only; the throttle ramps on.</summary>
    private void SkidTick(Entity e, Entity? _)
    {
        if (e.TacticStage == 0) e.Vehicle!.Handbrake = 0;
        else e.TacticStage = 0;
        FullThrottle(e);
    }

    /// <summary>Dirt_circle / handle los: a slow constant circle.</summary>
    private void CircleTick(Entity e, Entity? _)
    {
        Throttle(e, 10f);
        Steer(e, Hdg(e) + 1f);
    }

    /// <summary>
    /// Dirt_standoff: drive away to 70, then face the target and
    /// stop. The away heading's wrap is the original's mirror (6.24 − x).
    /// </summary>
    private void StandoffTick(Entity e, Entity? t)
    {
        float h = Hdg(e), d = Dist(e, t!);
        if (e.TacticStage == 0 || d >= 70f)
        {
            e.TacticStage = 0;
            float b = Brg(e, t!);
            Steer(e, b);
            if (MathF.Abs(Delta(h, b)) < 0.1f) Brake(e); else Throttle(e, Cruise(e));
        }
        else
        {
            float x = Brg(e, t!) + 3.14f;
            if (x > 3.14f) x = 6.24f - x;
            Steer(e, x);
            FullThrottle(e);
        }
    }

    /// <summary>Rockford: the J-turn.</summary>
    private void RockfordTick(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        switch (e.TacticStage)
        {
            case 0:
                if (v.Speed >= 14f)
                {
                    e.TacticStage = 1;
                    e.SavedForward = e.Rot.Forward;
                    v.Steer = Rand() % 2 == 0 ? -1f : 1f;
                    v.Handbrake = 1;
                    FullThrottle(e);
                }
                else { v.Steer = 0; FullThrottle(e); }
                break;
            case 1:
                if (v.Speed < 1f || Vector3.Dot(e.Rot.Forward, e.SavedForward) < -0.4f) { e.TacticStage = 2; v.Handbrake = 0; }
                break;
            case 2:
                if (v.Speed <= 1f)
                {
                    e.TacticStage = 3;
                    v.Steer = -v.Steer;
                    FlipGear(v);
                }
                else Brake(e);
                break;
            case 3:
                Throttle(e, Cruise(e));
                break;
            default:
                host.Log("unkown turn state");
                break;
        }
    }

    /// <summary>The intercept ram: full throttle at where the target will be.</summary>
    private void InterceptRam(Entity e, Entity t)
    {
        var v = e.Vehicle!;
        var my = Pos(e);
        var (tv, _) = Motion(t);
        float time = System.Math.Clamp(ClosestTime(my, v.Vel, AimPointOf(t), tv), 0.01f, 5f);
        var aim = Predict(t, time);
        Throttle(e, 1000f);
        Steer(e, Heading(aim - my));
    }

    /// <summary>Tactic on deck: flee or wait on the side while the attacker roster is full.</summary>
    private void OnDeckTick(Entity e, Entity? t)
    {
        float d = Dist(e, t!);
        if (d < 50f) { e.OnDeckMode = 0; e.TacticDeadline = Now + 4f + Rand() % 5; }
        else if (d > 160f) { e.OnDeckMode = 1; e.TacticDeadline = Now + 4f + Rand() % 5; }
        else if (Now >= e.TacticDeadline) { e.OnDeckMode = e.OnDeckMode == 0 ? 1 : 0; e.TacticDeadline = Now + 4f + Rand() % 5; }
        bool onRoad = SharesRoad(e, t);
        if (e.OnDeckMode == 0) { if (onRoad) RoadFleeTick(e, t); else DirtCombat(e, t!, 4); }
        else { if (onRoad) RoadWar(e, t!, 3); else DirtCombat(e, t!, 2); }
    }

    /// <summary>Tactic on deck's enter: give up the slot, pick a mode.</summary>
    private void OnDeckEnter(Entity e, Entity? t)
    {
        var v = e.Vehicle!;
        Forward(v);
        e.AttackCommit = null;
        e.TacticDeadline = Now + 3f + Rand() % 5;
        e.OnDeckMode = Dist(e, t!) >= 30f ? Rand() % 2 : 0;
    }

    // ---- road war ----

    /// <summary>The road-war behaviour's table: int32[mode][lat][bucket][myCls][theirCls].</summary>
    private static readonly int[,,,,] RoadWarTable = BuildRoadWarTable();

    private static int[,,,,] BuildRoadWarTable()
    {
        var t = new int[4, 3, 3, 4, 4];
        void Row(int mode, int lat, int bucket, string s)
        {
            var d = s.Replace(" ", "");
            for (int i = 0; i < 16; i++) t[mode, lat, bucket, i / 4, i % 4] = d[i] - '0';
        }
        const string zero = "0000 0000 0000 0000", four = "4444 4444 4444 4444";
        for (int lat = 0; lat < 3; lat++)
            for (int b = 0; b < 3; b++) { Row(0, lat, b, zero); Row(1, lat, b, "2222 2222 2222 2222"); Row(3, lat, b, zero); }
        foreach (int lat in new[] { 0, 2 })
        {
            Row(2, lat, 0, "3646 2336 2444 2633"); Row(2, lat, 1, "3646 4444 4444 4444"); Row(2, lat, 2, four);
            Row(3, lat, 0, "3646 2336 2444 2633");
        }
        Row(2, 1, 0, "3444 2333 2333 2333"); Row(2, 1, 1, "5353 4444 4444 4444"); Row(2, 1, 2, four);
        Row(3, 1, 0, "3444 2333 2333 2333");
        return t;
    }

    /// <summary>
    /// The road-war behaviour: both cars onto the road, the
    /// direction toward the target along it, lanes and relative classes, then
    /// one atomic behaviour from the table by lateral side, point-index
    /// bucket and the two classes.
    /// </summary>
    private void RoadWar(Entity a, Entity t, int mode)
    {
        var Rm = a.Road; var Rt = t.Road;
        if (Roads is not { } roads) { Brake(a); return; }
        roads.Resolve(Pos(a), Rm);
        roads.Resolve(t.Position, Rt);
        if (Rm.Seg is null || Rt.Seg is null) { Brake(a); return; }
        int dir = DirTo(Rm, Rt);
        Rm.Dir = dir; Rt.Dir = -dir;
        int k = System.Math.Abs(Rm.Idx - Rt.Idx);
        int bucket = k == 0 ? 0 : k >= 9 ? 2 : 1;
        LaneUpdate(Pos(a), Rm); LaneUpdate(t.Position, Rt);
        Rm.Cls = RelClass(a, Rm, bucket, t); Rt.Cls = RelClass(t, Rt, bucket, a);
        int lat = Rm.Off < Rt.Off - 2f ? 2 : Rm.Off > Rt.Off + 2f ? 0 : 1;
        int b = RoadWarTable[mode, lat, bucket, Rm.Cls, Rt.Cls];
        float h;
        switch (b)
        {
            case 0: RoadPursuit(a, Rm, 20f, Rt.Off, out h); break;
            case 1: RoadPursuit(a, Rm, 20f, Rt.Off + 5f, out h); break;
            case 2:
            {
                float o = Rt.Off < 5f ? 10f : 0f;
                Rm.Dir = -Rm.Dir;
                RoadPursuit(a, Rm, 20f, o, out h);
                break;
            }
            case 3: InterceptRam(a, t); return;
            case 4: case 6: RoadCatchUp(a, Rm, Rt, 20f, t); return;
            case 5:
            {
                float o = Rt.Off is >= 0f and <= 10f ? (Rt.Off < 5f ? 10f : 0f) : (Rm.Dir == 1 ? 1.7f : 8.2f);
                RoadPursuit(a, Rm, 20f, o, out h);
                break;
            }
            default: host.Log("Unknown atomic behavior in road war"); return;
        }
        Steer(a, h);
        float s = MathF.Abs(a.Vehicle!.Steer);
        Throttle(a, s > 0.75f ? Cruise(a) : s > 0.5f ? 30f : 100f);
    }

    /// <summary>The direction from Rm toward Rt along the road: by index on one piece, else +1 if Rt's piece lies ahead before the road ends.</summary>
    private static int DirTo(RoadPos Rm, RoadPos Rt)
    {
        if (Rm.Seg == Rt.Seg) return Rm.Idx >= Rt.Idx ? -1 : 1;
        var s = Rm.Seg!;
        while (!s.End && s.Next is { } n)
        {
            if (s == Rt.Seg) return 1;
            s = n;
        }
        return s == Rt.Seg ? 1 : -1;
    }

    /// <summary>The signed offset from the road line (positive on the travel side) and the lane band.</summary>
    private static void LaneUpdate(Vector3 pos, RoadPos R)
    {
        var s = R.Seg!; int i = R.Idx;
        var p0 = R.Seg![R.Idx];
        if (!RoadNetwork.NextPoint(ref s, R.Dir, ref i)) { R.Band = 2; R.Off = 3.45f; return; }
        var p1 = s[i];
        var dl = p1 - p0;
        float ll = dl.LengthSquared();
        var foot = ll > 0 ? p0 + dl * (Vector3.Dot(pos - p0, dl) / ll) : p0;
        var to = pos - foot;
        float d = to.Length();
        var u = d > 0 ? to / d : Vector3.Zero;
        float c = -(p1.Z - p0.Z) * u.X + (p1.X - p0.X) * u.Z + (p1.Y - p0.Y) * u.Y;
        bool good = R.Dir == 1 ? c > 0 : c < 0;
        if (!good) { R.Off = -d; R.Band = d < 5 ? 1 : 0; }
        else { R.Off = d; R.Band = d < 5 ? 2 : d < 10 ? 3 : d < 15 ? 4 : 5; }
    }

    /// <summary>An angular quadrant at the same road point, else facing along (0), across (1/3) or against (2) the travel direction.</summary>
    private static int RelClass(Entity o, RoadPos R, int bucket, Entity other)
    {
        if (bucket < 1)
        {
            float rel = Brg(o, other) - Hdg(o);
            if (rel < 0) rel += 2 * MathF.PI;
            return rel <= 5.497787f ? System.Math.Clamp((int)MathF.Floor((rel + MathF.PI / 4) * 0.63661975f), 0, 3) : 0;
        }
        var s = R.Seg!; int i = R.Idx;
        if (!RoadNetwork.NextPoint(ref s, R.Dir, ref i)) return 2;
        var d = s[i] - R.Seg![R.Idx];
        var f = o.Rot.Forward;
        float x = Vector3.Dot(f, d);
        if (x < -0.707f) return 2;
        if (x > 0.707f) return 0;
        return f.Y * d.Y - (f.X + f.Z) * d.X <= 0 ? 1 : 3;
    }

    /// <summary>
    /// The heading to a point `look` ahead along the road at
    /// lateral offset off; true when that point was found. Copied with its
    /// overshoot past the last point and the absolute-position heading when
    /// the road ends during the walk.
    /// </summary>
    private static bool RoadPursuit(Entity a, RoadPos R, float look, float off, out float h)
    {
        var A = Pos(a);
        var s = R.Seg!; int i = R.Idx;
        if (!RoadNetwork.NextPoint(ref s, R.Dir, ref i)) { h = Heading(a.Rot.Forward); return false; }
        var p0 = R.Seg![R.Idx];
        var d = s[i] - p0;
        float L = d.Length();
        var n = new Vector3(-d.Z, d.Y, d.X);
        if (L <= -1e-4f || L >= 1e-4f) n *= R.Dir * off / L;
        var prev = p0 + n;
        var dd = prev - A;
        float D = dd.Length();
        if (look < D) { h = MathF.Atan2(dd.X, dd.Z); return true; }
        s = R.Seg!; i = R.Idx;
        if (!RoadNetwork.NextPoint(ref s, R.Dir, ref i)) { h = MathF.Atan2(dd.X, dd.Z); return false; }
        float rem = look - D;
        while (true)
        {
            var q = s[i] + n;
            var qd = q - prev;
            L = qd.Length();
            if (rem <= L)
            {
                var t = q + qd * (L > 0 ? rem / L : 0);
                h = MathF.Atan2(t.X - A.X, t.Z - A.Z);
                return true;
            }
            prev = q;
            if (!RoadNetwork.NextPoint(ref s, R.Dir, ref i)) break;
            rem -= L;
        }
        h = MathF.Atan2(A.X, A.Z);
        return false;
    }

    /// <summary>Ride beside the target, 4 over, at its speed plus 5 per road point behind.</summary>
    private void RoadCatchUp(Entity a, RoadPos Rm, RoadPos Rt, float look, Entity t)
    {
        float o = Rt.Off < -12f ? -10f : Rt.Off > 22f ? 20f : Rt.Off + 4f;
        RoadPursuit(a, Rm, look, o, out float h);
        Steer(a, h);
        if (MathF.Abs(a.Vehicle!.Steer) > 0.6f) Throttle(a, Cruise(a));
        else Throttle(a, Motion(t).Speed + 5f * System.Math.Abs(Rt.Idx - Rm.Idx));
    }

    /// <summary>
    /// Road_flee / r_rvs_flee: away from the target along the
    /// road in the other lane, or away from a dead end. When neither applies
    /// the original's lane offset is an uninitialised local; the last stored
    /// offset stands in for it.
    /// </summary>
    private void RoadFleeTick(Entity e, Entity? t)
    {
        var Rm = e.Road;
        if (Roads is not { } roads) { Brake(e); return; }
        roads.Resolve(Pos(e), Rm);
        if (Rm.Seg is not { } seg) { Brake(e); return; }
        float o = Rm.Off;
        if (seg.End && Rm.Idx > seg.Count - 5) { Rm.Dir = -1; o = 8.2f; }
        else if ((seg.Prev is null || seg.Prev.End) && Rm.Idx < 5) { Rm.Dir = 1; o = 1.7f; }
        else if (IsVehicle(t))
        {
            var Rt = t!.Road;
            roads.Resolve(t.Position, Rt);
            if (Rt.Seg is not null && Rt.Seg.Road == seg.Road)
            {
                o = Rm.Off;
                int dir = DirTo(Rm, Rt);
                Rm.Dir = dir; LaneUpdate(Pos(e), Rm);
                Rt.Dir = -dir; LaneUpdate(t.Position, Rt);
                Rm.Dir = -Rm.Dir;
                if (Rt.Off < 3.5f) o = 8.2f;
                else if (Rt.Off > 6.5f) o = 1.7f;
            }
        }
        if (Rm.Dir is not (1 or -1)) { Rm.Dir = 1; o = 1.7f; }
        Rm.Off = o;
        if (RoadPursuit(e, Rm, 9f, o, out Rm.Heading))
        {
            Steer(e, Rm.Heading);
            Throttle(e, t is not null && Dist(e, t) <= 50f && MathF.Abs(e.Vehicle!.Steer) <= 0.65f ? 50f : Cruise(e));
        }
    }

    // ---- dirt combat ----

    /// <summary>Table: int8[mode][range band][my octant][its octant], one hex digit each.</summary>
    private static readonly byte[,,,] DirtTable = BuildDirtTable();

    private static byte[,,,] BuildDirtTable()
    {
        string[][] rows =
        [
            ["55555555 55455555 33444444 32222000 73224001 12220000 14444441 55555545",
             "55555555 55455555 33444444 22222000 72224000 02220000 14444441 55555545",
             "55555555 55445555 33444444 22444000 72224000 02224440 14444441 55555445",
             "55555555 55445555 33444444 33224201 72224000 13000001 14444441 55555445",
             "55555555 55445555 33444444 22222222 32224000 00000000 14444441 55555445"],
            ["71866683 aa488663 33888888 22888000 73228001 02228880 18888881 a166668a",
             "71866680 aa866663 33888888 22888800 73228001 02288880 18886681 a166668a",
             "71666663 aa866663 33888888 22444400 73244401 02244440 14446641 a166664a",
             "71566653 aa555663 33444444 22444400 73244401 02244440 14446641 a166665a",
             "55555555 55555555 33444444 22444400 73244401 02244440 14444441 55555555"],
            ["72220000 aaaaaaaa 33222233 32222223 b0004222 11000000 11110001 aaaaaaaa",
             "73330111 aaaaaaaa 32222233 33222223 b0004222 11000000 11110001 aaaaaaaa",
             "a3355511 33333a33 33222233 32222223 b0004222 11000000 11110001 111a1111",
             "a3555551 33333333 33222233 32222223 b0004222 11000000 11110001 11111111",
             "55555555 55446555 33444444 22222222 32224000 00000000 14444441 55556445"],
            ["88666688 8a488668 33888888 22888000 73228001 02228880 18888881 a166888a",
             "88866688 88866668 33888888 22888800 72228000 02288880 18886681 88666688",
             "66666666 66866666 22444444 22444400 72244400 02244440 04446640 66666646",
             "66566656 66565666 22444444 22444400 72244401 02244440 04446640 66665656",
             "55555555 55555555 22444444 22444400 72244400 02244440 04444440 55555555"],
            ["70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "55666665 55446655 33444444 33224201 72224000 13000001 14444441 55566445",
             "55555555 55446555 33444444 22222222 32224000 00000000 14444441 55556445"],
            ["70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "70007222 00000000 11111111 11111111 73337111 33333333 33333333 33333333",
             "eeeeeeee eeeeeeee 33eeeeee 22eeee00 7322e001 022eeee0 1eee66e1 ee6666ee",
             "55555555 55555555 33444444 22444400 73244401 02244440 14444441 55555555"],
        ];
        var t = new byte[6, 5, 8, 8];
        for (int m = 0; m < 6; m++)
            for (int db = 0; db < 5; db++)
            {
                var d = rows[m][db].Replace(" ", "");
                for (int i = 0; i < 64; i++) t[m, db, i / 8, i % 8] = Convert.ToByte(d[i].ToString(), 16);
            }
        return t;
    }

    /// <summary>
    /// Off-road combat. Range band, where the target is
    /// around me and where I am around it pick one of the atomic moves.
    /// </summary>
    private void DirtCombat(Entity a, Entity t, int mode)
    {
        float h = Hdg(a), b = Brg(a, t);
        int mo = Octant(h, b);
        float hT = 0, bT = 0;
        int to;
        if (!IsVehicle(t)) to = 4;
        else
        {
            hT = Hdg(t); bT = Brg(t, a);
            to = (t.Vehicle!.Flags & VehicleFlags.Sliding) != 0 ? 4 : Octant(hT, bT);
        }
        float d = Dist(a, t);
        int db = d < 9 ? 0 : d < 24 ? 1 : d < 60 ? 2 : d < 200 ? 3 : 4;
        const float q = MathF.PI / 4;
        switch (DirtTable[mode, db, mo, to])
        {
            case 0: Throttle(a, Cruise(a)); Steer(a, h - q); break;
            case 1: Throttle(a, 1000f); Steer(a, h - q); break;
            case 2: Throttle(a, Cruise(a)); Steer(a, h + q); break;
            case 3: Throttle(a, 1000f); Steer(a, h + q); break;
            case 4: Pursue(a, t); break;
            case 5: InterceptRam(a, t); break;
            case 6: Shadow(a, t, h, d); break;
            case 7:
            {
                float e = Delta(hT, bT);
                float turn = MathF.Abs(Delta(h, b)) < MathF.PI / 2 ? (e <= 0 ? q : -q) : (e >= 0 ? q : -q);
                Throttle(a, 1000f); Steer(a, h + turn);
                break;
            }
            case 8:
            {
                float ang = MathF.Abs(Delta(h, b));
                Pursue(a, t);
                if (d < 24 && ang < 0.19f) Brake(a);
                break;
            }
            case 0xa: Throttle(a, 1000f); Steer(a, h); break;
            case 0xb: Throttle(a, Cruise(a)); Steer(a, h); break;
            case 0xc: case 0xd: break;
            case 0xe:
            {
                float ang = MathF.Abs(Delta(h, b));
                Pursue(a, t);
                if (d < 140 && ang < 0.1f) Brake(a);
                break;
            }
            default: host.Log("Unknown ai combat state"); break;
        }
    }

    /// <summary>
    /// Lead pursuit, the lead from the first forward gun's
    /// round speed; speed matched to the target. A non-vehicle target's
    /// speed is an uninitialised local in the original; 0 here.
    /// </summary>
    private void Pursue(Entity a, Entity t)
    {
        var v = a.Vehicle!;
        var P = Pos(a);
        Vector3 delta;
        float tSpd = 0, time = 0;
        if (!IsVehicle(t)) delta = AimPointOf(t) - P;
        else
        {
            var va = v.Vel;
            if (ForwardWeapons(a).FirstOrDefault() is { } w) va += v.Rot.Forward * w.Def.Speed;
            (var vt, tSpd) = Motion(t);
            time = System.Math.Clamp(ClosestTime(P, va, AimPointOf(t), vt), 0.01f, 5f);
            delta = Predict(t, time) - P;
        }
        float hh = MathF.Atan2(delta.X, delta.Z), r = MathF.Max(delta.Length() - 20f, 0);
        Steer(a, hh);
        bool straight = MathF.Abs(v.Steer) < 0.4f;
        // #42: at a standstill the original's steering for its drivers writes 0 whatever the heading error, so the wheel reads
        // straight, and a stopped car with a stopped target within 24 braked for good, even with it alongside or
        // behind. Here a stopped car reads straight only while the aim point is within 0.19 rad of its heading (case
        // 8's dead-ahead angle); otherwise it drives off at the cruise speed and turns in (docs/deviations.md).
        if (tSpd < 1f && v.Speed < 1e-4f && MathF.Abs(Delta(Hdg(a), hh)) >= 0.19f) straight = false;
        if (tSpd >= 1f)
        {
            if (straight)
            {
                if (r > 150) Throttle(a, 1000f);
                else if (r < 1) Throttle(a, tSpd * 0.5f);
                else if (r > 50) Throttle(a, tSpd + MathF.Max(0, (r - 2f) * 0.7f));
                else Throttle(a, tSpd + MathF.Max(0, r - 1f) * 0.4f);
                return;
            }
            if (r < 100)
            {
                if (Vector3.Dot(t.Rot.Forward, v.Rot.Forward) > 0.7f && Vector3.Dot(v.Rot.Forward, delta) < 0) { Throttle(a, tSpd * 0.75f); return; }
                if (time > 0) { Throttle(a, tSpd - 5f); return; }
            }
        }
        else if (straight)
        {
            if (r < 4) Brake(a); else Throttle(a, (r - 1f) * 0.3f);
            return;
        }
        Throttle(a, Cruise(a));
    }

    /// <summary>Match the velocity that arrives where the target will be.</summary>
    private void Shadow(Entity a, Entity t, float h, float d)
    {
        var w = ShadowVelocity(a, t);
        float hh = MathF.Abs(w.X) >= 1e-4f || MathF.Abs(w.Z) >= 1e-4f ? MathF.Atan2(w.X, w.Z) : h;
        Steer(a, hh);
        float tSpd = Motion(t).Speed;
        if (tSpd >= 1f)
        {
            if (MathF.Abs(a.Vehicle!.Throttle) <= 0.4f) Throttle(a, w.Length()); else Throttle(a, Cruise(a));
        }
        else if (MathF.Abs(Delta(h, hh)) < 0.19f)
        {
            if (d < 60) Brake(a); else Throttle(a, (d - 60f) * 0.2f);
        }
        else Throttle(a, Cruise(a));
    }

    /// <summary>The velocity that reaches the target's predicted spot, eased toward 0.9 × my speed.</summary>
    private static Vector3 ShadowVelocity(Entity a, Entity t)
    {
        var (va, sA) = Motion(a);
        var D = AimPointOf(t) - Pos(a);
        float time = System.Math.Clamp(D.Length() / (sA + 25f), 0.4f, 1.75f);
        var w = (Predict(t, time) - Pos(a)) / time;
        float wl = w.Length();
        Vector3 w2 = wl <= 25f ? (wl > 1e-4f ? w * (0.9f * sA / wl) : w * 1000f) : w * (1f - 25f / wl);
        return Vector3.Dot(va, w2) > 0 ? w2 : w;
    }

    /// <summary>
    /// tactic_skid: one chance in 8 + 40·(1 − skill) a frame
    /// while above 12 and the steering asks for more than 13 of lateral
    /// acceleration.
    /// </summary>
    private bool SkidChance(Entity e, Entity? t)
    {
        if (e.IsHeli || !IsVehicle(t)) return false;
        if (Rand() % SkidOdds(e.Ai.Skill) != 0) return false;
        var v = e.Vehicle!;
        if (v.Speed < 12f) return false;
        float lat = v.Wheels.Sum(w => w.Grip) * 7.84f, sp = v.Speed, k = v.P.SteerGain;
        float s = k * sp * sp <= lat ? v.Steer * k * sp : v.Steer * lat / sp;
        return MathF.Abs(s * sp) > 13f;
    }

    /// <summary>
    /// The skid's odds, 8 + trunc(40·(1 − skill)). The original works it out in the x87's extended precision: at the
    /// default skill 0.4 (the float 0.400000006) that is trunc(23.9999998) = 23, so one in 31, where float arithmetic
    /// rounds 1 − 0.4 to 0.6 and gives 32.
    /// </summary>
    internal static int SkidOdds(float skill) => (int)((1.0 - skill) * 40.0) + 8;

    // ---- preconditions ----

    /// <summary>The forward-facing weapons (muzzle forward · vehicle forward &gt; 0.96).</summary>
    private static IEnumerable<Weapons.Weapon> ForwardWeapons(Entity e) =>
        e.Vehicle?.Armory?.Weapons.Where(w => w.BaseRot.Forward.Z > 0.96f) ?? [];

    /// <summary>The first chassis zone is not gone.</summary>
    private static bool FrontIntact(Entity e, Entity? _) => e.Vehicle!.Health is not { } h || h.Chassis[0] > 0;

    /// <summary>Some forward-facing weapon still has ammunition.</summary>
    private static bool ForwardGunLoaded(Entity e) => ForwardWeapons(e).Any(w => w.Ammo != 0);

    /// <summary>
    /// A non-vehicle target, or a mortar, or a loaded guided
    /// missile — read over the first forward-count entries of the full
    /// weapon list, as the original indexes it.
    /// </summary>
    private static bool StandoffWeapon(Entity e, Entity? t)
    {
        if (!IsVehicle(t)) return true;
        var all = e.Vehicle!.Armory?.Weapons ?? [];
        int n = System.Math.Min(ForwardWeapons(e).Count(), all.Count);
        for (int i = 0; i < n; i++)
        {
            var w = all[i];
            if (w.Def.Class == 4) { if (w.Def.Tier is 1 or 2 or 3 or 4 or 0x65 or 0x66 or 0x67 && w.Ammo != 0) return true; }
            else if (w.Def.Class == 3) return true;
        }
        return false;
    }

    /// <summary>The outside-road class check: not a vehicle, or its surface outside 2..4.</summary>
    private static bool OutsideRoadClass(Entity? t) => t?.Vehicle is not { } v || v.Surface < 2 || v.Surface > 4;

    /// <summary>
    /// The shares-a-road check, with its defect: both road
    /// points are resolved from the actor's position (the target's record is
    /// overwritten with it), so it reads "the target is on a road surface and
    /// the actor is on some road".
    /// </summary>
    private bool SharesRoad(Entity e, Entity? t)
    {
        if (e.IsHeli || OutsideRoadClass(t)) return false;
        return ResolveBoth(e, t!);
    }

    /// <summary>The same "same road" test without the road-class test.</summary>
    private bool SameRoad(Entity e, Entity? t) => !e.IsHeli && IsVehicle(t) && ResolveBoth(e, t!);

    private bool ResolveBoth(Entity e, Entity t)
    {
        if (Roads is not { } roads) return false;
        var p = Pos(e);
        roads.Resolve(p, e.Road);
        roads.Resolve(p, t.Road);
        return e.Road.Seg is { } a && t.Road.Seg is { } b && a.Road == b.Road;
    }

    /// <summary>Within 80 and hurt below 17·agg + 30 percent.</summary>
    private static bool CloseAndHurt(Entity e, Entity? t) => Dist(e, t!) < 80f && e.Health < 17f * e.Aggression + 30f;

    /// <summary>Off-road tactics are allowed unless the target is road-class and the roads are shared.</summary>
    private bool OffRoadAllowed(Entity e, Entity? t) => OutsideRoadClass(t) || !SharesRoad(e, t);

    // ---- rule predicates ----

    /// <summary>
    /// termRW, read literally: on the same road as the target
    /// and (the target is off the road surfaces or the road continues ahead).
    /// </summary>
    private bool TermRoadWar(Entity e, Entity? t)
    {
        if (e.Road.Seg is not { } a || t?.Road.Seg is not { } b || a.Road != b.Road) return false;
        if (OutsideRoadClass(t)) return true;
        var s = a; int i = e.Road.Idx;
        return RoadNetwork.NextPoint(ref s, e.Road.Dir, ref i);
    }

    /// <summary>termRW: a non-vehicle target within its radius (here the collider's bounding radius) + 35.</summary>
    private static bool TermDirtWar(Entity e, Entity? t) =>
        t is not null && !IsVehicle(t) && Dist(e, t) < (t.Static?.Radius ?? 0f) + 35f;

    /// <summary>termflee: the road goes on in the flee direction and the target is past 160.</summary>
    private bool TermFlee(Entity e, Entity? t)
    {
        if (e.Road.Seg is not { } s) return false;
        int i = e.Road.Idx;
        return RoadNetwork.NextPoint(ref s, e.Road.Dir, ref i) && Dist(e, t!) > 160f;
    }

    /// <summary>termRock: time up, or turned through ~150°.</summary>
    private bool TermRock(Entity e, Entity? _) => Now > e.TacticDeadline || Vector3.Dot(e.Rot.Forward, e.SavedForward) < -0.85f;
}
