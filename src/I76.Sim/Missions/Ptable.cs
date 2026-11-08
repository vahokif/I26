using System.Numerics;
using I76.Formats.Exe;
using I76.Sim.Ai;
using I76.Sim.Collision;
using I76.Sim.Fsm;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// The AI's behaviour state machine, the "Ptable":
/// The AI order is the AI's whole per-frame tick, run when the script
/// (re-)issues an order: the top state's rules (pop, or re-choose the base),
/// its tick, its transitions (push), then the fire decision at the order's
/// target. The table itself is data, read from the player's executable
/// (<see cref="TacticsTable"/>); this class binds its handlers: the driver and
/// ChooseTactic, the navigation planner and driver pairs (here), the combat
/// tactics (PtableTactics.cs) and the shared predicates and escape states
/// (PtableEscapes.cs).
/// </summary>
public sealed partial class Ptable
{
    public const int TacticsInit = 0, DirtCircle = 15, DoSearch = 22, FollowPath = 23, DoEvadeSearch = 24, Evade = 25, DoRace = 26, Race = 27,
        FollowLeader = 30, Sit = 31, Guard = 32, Hide = 33;

    private delegate void Handler(Entity e, Entity? target);
    private delegate bool Pred(Entity e, Entity? target);

    private sealed record Rule(PtRule Data, Pred? Test);
    private sealed record State(PtEntry Data, Handler Tick, Handler? Enter, Handler? OnMatch, Pred[] Pre, (Pred Pred, int Next)[] Trans, Rule[] Rules);

    private readonly ScriptHost host;
    private readonly State[]? _states;
    /// <summary>The bound table (a world built without one, as some tests are, can do everything but issue AI orders).</summary>
    private State[] S => _states ?? throw new InvalidOperationException("Ptable: the script host has no tactics table (TacticsTable.For(install))");
    private const int MaxDepth = 0x31;

    public Ptable(ScriptHost host)
    {
        this.host = host;
        _states = host.Tactics is { } t ? Bind(t) : null;
    }

    private SimWorld World => host.World;
    private float Dt => host.FrameDelta;
    private float Now => host.Now;
    private int Rand() => World.Rand.Next();

    public static bool IsAttack(int order) => order is >= 0 and <= 15 and not 7;
    public static int StateOf(AiOrder order) => order switch
    {
        AiOrder.Goto => DoSearch, AiOrder.Evade => DoEvadeSearch, AiOrder.Race => DoRace,
        AiOrder.Follow => FollowLeader, AiOrder.Sit => Sit, AiOrder.Guard => Guard, AiOrder.Hide => Hide,
        _ => (int)order,
    };

    /// <summary>A state's name in the table, for logs.</summary>
    public string NameOf(int s) => host.Tactics is { } t && s >= 0 && s < t.Length ? t[s].Name : $"state {s}";

    // ---- the driver ----

    /// <summary>The AI order: the order's state machine, one frame of it, then the guns.</summary>
    public void Issue(Entity e, Entity? target, AiOrder order, bool resetAlways)
    {
        if (e.Vehicle is null) return;
        if (target is not null && target.Removed) { Brake(e); return; }
        int ord = (int)order;
        bool attack = IsAttack(ord);
        if (attack && target is null) { Brake(e); return; }
        var st = e.Stack;
        int s;
        if (e.Depth != -1 && !resetAlways && st.Count > 0)
        {
            bool same = attack ? IsAttack(st[0]) && e.LastTarget == target
                               : !IsAttack(st[0]) && st[0] == StateOf(order) && e.LastTarget == target;
            if (same)
            {
                s = st[e.Depth];
                while (true)
                {
                    int r = MatchingRule(e, target, s);
                    if (r < 0) break;
                    S[s].OnMatch?.Invoke(e, target);
                    if (e.Depth < 1)
                    {
                        s = IsAttack(st[0]) ? ChooseTactic(e, target, s, r) : StateOf(order);
                        st[0] = s;
                        S[s].Enter?.Invoke(e, target);
                        break;
                    }
                    e.Depth--;
                    st.RemoveAt(st.Count - 1);
                    s = st[e.Depth];
                }
                Tick(e, target, s);
                Fire(e, target, order);
                return;
            }
        }
        // reset: new order or target, the first order, or the player under script control
        e.LastTarget = target;
        s = attack ? ChooseTactic(e, target, TacticsInit, 0) : StateOf(order);
        st.Clear(); st.Add(s); e.Depth = 0;
        S[s].Enter?.Invoke(e, target);
        Tick(e, target, s);
        Fire(e, target, order);
    }

    private int MatchingRule(Entity e, Entity? target, int s)
    {
        var rules = S[s].Rules;
        for (int r = 0; r < rules.Length; r++)
            if (rules[r].Test is { } t && t(e, target)) return r;
        return -1;
    }

    private void Tick(Entity e, Entity? target, int s)
    {
        S[s].Tick(e, target);
        for (int guard = 0; guard < 64; guard++)
        {
            int next = -1;
            foreach (var (pred, n) in S[s].Trans)
                if (pred(e, target)) { next = n; break; }
            if (next < 0) break;
            s = next;
            if (e.Depth < MaxDepth)
            {
                e.Depth++;
                e.Stack.Add(s);
                S[s].Enter?.Invoke(e, target);
                S[s].Tick(e, target);
            }
            else host.Log("Ptable stack overflow");
        }
    }

    /// <summary>
    /// ChooseTactic: a weighted draw over rule r of state s
    /// in the aggression row, one rand() per attempt; a candidate whose
    /// preconditions fail is struck and the draw repeats (at most 18 tries);
    /// dirt_circle when nothing is left.
    /// </summary>
    public int ChooseTactic(Entity e, Entity? target, int s, int r)
    {
        int agg = e.Aggression;
        if (agg is < 0 or > 4) { host.Log("calc_sum - agg out of bounds"); agg = System.Math.Clamp(agg, 0, 4); }
        var R = S[s].Rules[r].Data;
        var w = R.Weights[agg].ToArray();
        int sum = w.Sum();
        int tries = 0;
        while (sum > 0)
        {
            int x = Rand() % sum;
            int i = 0;
            while (w[i] <= x) { x -= w[i]; i++; }
            int cand = R.Options[i];
            if (S[cand].Pre.All(p => p(e, target))) return cand;
            sum -= w[i]; w[i] = 0;
            if (++tries >= 18) break;
        }
        return DirtCircle;
    }

    /// <summary>The tail of the AI order: follow fires at its ref when flagged; any other order at its target.</summary>
    private void Fire(Entity e, Entity? target, AiOrder order)
    {
        var at = order == AiOrder.Follow ? (e.FollowFlag != 0 ? e.FollowRef : null) : target;
        if (at is null || at == e) return;
        host.FireAt(e, at);
    }

    // ---- the per-frame globals the game rolls after the FSM tick ----

    private Entity? _planHolder;
    private bool _planDenied, _planned;
    private int _losCaller, _losTurn;
    private bool _losGranted;

    /// <summary>
    /// The end of the AI frame: the planner token (an unfinished search whose
    /// owner was not ticked this frame is aborted) and the round-robin line
    /// of sight budget.
    /// </summary>
    public void EndFrame()
    {
        _planDenied = false;
        if (!_planned && _planHolder is { } h) { h.Search = null; _planHolder = null; }
        _planned = false;
        if (JammerActive())
        {
            if (!_losGranted) _losTurn = 0;
            _losGranted = false; _losCaller = 0;
        }
    }

    /// <summary>Only the holder of an unfinished search plans, or anyone while nobody was denied this frame.</summary>
    private bool CanPlan(Entity e) => _planHolder is { } h ? h == e : !_planDenied;
    /// <summary>A planner call was refused this frame.</summary>
    private void Denied() => _planDenied = true;
    /// <summary>The search state after a planner call (the holder while unfinished).</summary>
    private void Planned(Entity? holder) { _planned = true; _planHolder = holder; }

    /// <summary>At most one AI per frame runs the line of sight test, round-robin.</summary>
    private bool LosBudget()
    {
        bool grant = false;
        if (!_losGranted && _losCaller == _losTurn) { _losTurn++; _losGranted = true; grant = true; }
        _losCaller++;
        return grant;
    }

    // ---- actuators (§3.0, §4.0) ----

    private float Traction(Vehicle v, float m) => World.Step.TractionLimit(v, m, World.IsFar(v));
    private void Steer(Entity e, float heading) => AiDriver.Steer(e.Vehicle!, e.Ai, World.Ground, heading, Dt, World.IsFar(e.Vehicle!));
    private void Throttle(Entity e, float speed) => AiDriver.Throttle(e.Vehicle!, e.Ai, World.Ground, speed, Dt, m => Traction(e.Vehicle!, m));
    /// <summary>Full brake, limited by traction.</summary>
    private void Brake(Entity e) { if (e.Vehicle is { } v) v.Throttle = Traction(v, -1f) * 0.9999f; }
    /// <summary>Floor it, limited by traction.</summary>
    private void FloorIt(Entity e) { var v = e.Vehicle!; v.Throttle = Traction(v, 1f) * 0.9999f; }
    /// <summary>Ramp toward the traction limit by the acceleration factor, capped at 0.75 + 0.25·factor.</summary>
    private void FullThrottle(Entity e)
    {
        var v = e.Vehicle!;
        float a = e.Ai.AccelFactor, old = v.Throttle;
        v.Throttle = MathF.Min(old + (Traction(v, 1f) - old) * a, 0.75f + 0.25f * a);
    }
    /// <summary>The grip steering limit.</summary>
    private float SteerLimit(Entity e) => AiDriver.SteerLimit(e.Vehicle!, World.Ground, World.IsFar(e.Vehicle!));
    /// <summary>Forward gear: veh+0xf0 = 0, veh+0xe8 = +1, drive state 3.</summary>
    private static void Forward(Vehicle v) { v.Handbrake = 0; v.Gear = 1f; v.DriveState = 3; }
    /// <summary>Reverse gear: veh+0xf0 = 0, veh+0xe8 = −1, drive state 1.</summary>
    private static void Reverse(Vehicle v) { v.Handbrake = 0; v.Gear = -1f; v.DriveState = 1; }
    /// <summary>The gear flip of the escape states: +1 → −1 (drive state 1), otherwise → +1 (3).</summary>
    private static void FlipGear(Vehicle v)
    {
        if (MathF.Abs(v.Gear - 1f) < 1e-4f) { v.Gear = -1f; v.DriveState = 1; }
        else { v.Gear = 1f; v.DriveState = 3; }
    }

    /// <summary>GetAiCruiseSpeed: the speed where full lock meets the flat grip, sqrt(7.84·Σgrip / gain).</summary>
    public static float Cruise(Vehicle v)
    {
        float sum = v.Wheels.Sum(w => w.Grip);
        return sum < 0 || v.P.SteerGain <= 0 ? 1f : MathF.Sqrt(7.84f * sum / v.P.SteerGain);
    }
    /// <summary>The AI's cruise: 10 for a helicopter, else the cruise speed.</summary>
    private static float Cruise(Entity e) => e.IsHeli ? 10f : Cruise(e.Vehicle!);

    // ---- geometry (GetCachedObjectGeometry and the pair cache) ----

    private static Vector3 Pos(Entity e) => e.Position;
    private static float Heading(Vector3 d) => MathF.Atan2(d.X, d.Z);
    /// <summary>hdg(o): atan2 of forward × gear.</summary>
    private static float Hdg(Entity e) => e.Vehicle is { } v ? AiDriver.Heading(v) : Heading(e.Rot.Forward);
    /// <summary>brg(a, b): the bearing from a to b.</summary>
    private static float Brg(Entity a, Entity b) => Heading(AimPointOf(b) - AimPointOf(a));
    /// <summary>The distance between two objects' aim points on the ground plane.</summary>
    private static float Dist(Entity a, Entity b) { var d = AimPointOf(b) - AimPointOf(a); return MathF.Sqrt(d.X * d.X + d.Z * d.Z); }
    /// <summary>The distance between two objects' aim points in space.</summary>
    private static float Spatial(Entity a, Entity b) => Vector3.Distance(AimPointOf(a), AimPointOf(b));
    private static float Delta(float a, float b) => AiDriver.AngleDelta(a, b);
    /// <summary>Velocity and speed (zero for objects that do not move).</summary>
    private static (Vector3 Vel, float Speed) Motion(Entity? e) => e?.Vehicle is { } v ? (v.Vel, v.Speed) : (Vector3.Zero, 0f);
    private static bool IsVehicle(Entity? e) => e?.Vehicle is not null;
    /// <summary>
    /// The aim point, which the pair cache behind brg and the planar and spatial distances
    /// (GetCachedPairPlanarDistance) measures from: the entity slot's centre through
    /// the object's transform. For a structure that is its collider centre, which can stand far from its placement (the
    /// Wagon Wheel diner's is 53 m from it), and the fire decision aims there too; steering, bearings and range bands
    /// taken from the placement sent T01's attackers past the diner with their guns 30-60 degrees off it. A car keeps its
    /// position here (the slot's centre is close to it).
    /// </summary>
    private static Vector3 AimPointOf(Entity o) => o.Vehicle is null && o.Static is { } s ? s.Centre : o.Position;
    /// <summary>Time of closest approach, 1e7 when the relative velocity is tiny.</summary>
    private static float ClosestTime(Vector3 ps, Vector3 vs, Vector3 pt, Vector3 vt)
    {
        var rv = vt - vs;
        float rr = rv.LengthSquared();
        return rr < 0.01f ? 1e7f : Vector3.Dot(ps - pt, rv) / rr;
    }

    /// <summary>Where o will be at t + 0.09 s (<see cref="TargetPredictor"/>). Non-vehicles: the aim point (<see cref="AimPointOf"/>).</summary>
    private static Vector3 Predict(Entity o, float t) => o.Vehicle is { } v ? TargetPredictor.Predict(v, t) : AimPointOf(o);

    /// <summary>The octant of b − a, 0 dead ahead, 4 dead behind.</summary>
    public static int Octant(float a, float b)
    {
        float r = b - a;
        if (b < a) r += 2 * MathF.PI;
        r = r <= 5.8904862f ? r + 0.3926991f : r - 5.8904862f;
        return System.Math.Clamp((int)MathF.Floor(r * 1.2732395f), 0, 7);
    }

    // ---- navigation: the planner and driver pair (§2) ----

    /// <summary>The start of a path follow: the commanded path from point 0, arrival cleared, forward gear.</summary>
    private void BeginPathFollow(Entity e, Entity? _)
    {
        StartPath(e);
        e.PlanState = 2; e.Arrived = false;
        Forward(e.Vehicle!);
    }

    /// <summary>Follow the commanded path from point 0.</summary>
    private static void StartPath(Entity e)
    {
        e.NavPath = e.Path; e.NavIndex = 0; e.Waypoints.Clear(); e.WaypointBridge.Clear(); e.WaypointIndex = 0;
    }

    /// <summary>do race's enter: as goto, but a racer that had not just arrived starts at the nearest point ahead.</summary>
    private void BeginRace(Entity e, Entity? t)
    {
        bool arrived = e.Arrived;
        BeginPathFollow(e, t);
        if (arrived || e.NavPath is not { Count: > 0 } path) return;
        var p = Pos(e);
        int i = 0; float best = float.MaxValue;
        for (int k = 0; k < path.Count; k++)
        {
            float d = new Vector2(path[k].X - p.X, path[k].Z - p.Z).LengthSquared();
            if (d < best) { best = d; i = k; }
        }
        int start = i;
        while (true)
        {
            var next = i < path.Count - 1 ? path[i + 1] : path[0];
            if (Ahead(path[i], next, p) <= -0.1f) break;
            i = (i + 1) % path.Count;
            if (i == start) break;
        }
        e.NavIndex = i;
    }

    /// <summary>
    /// The path follow's tick: the planner, under the one-vehicle
    /// token. A helicopter gets the nav point as its one waypoint; anything
    /// else runs PlanPath. While planning (or waiting for the token) it creeps
    /// straight on at a quarter of the cruise speed.
    /// </summary>
    private void PlannerTick(Entity e, Entity? _)
    {
        e.PlanStamp = -10f;
        if (CanPlan(e))
        {
            e.ReachedLeg = false;
            if (e.IsHeli)
            {
                e.Waypoints.Clear(); e.WaypointBridge.Clear(); e.WaypointIndex = 0;
                if (e.NavPath is { } np && e.NavIndex < np.Count) { e.Waypoints.Add(np[e.NavIndex]); e.WaypointBridge.Add(false); }
                e.PlanState = 0;
            }
            else e.PlanState = PlanPath(e);
            Planned(e.PlanState == 1 ? e : null);
        }
        else Denied();
        if (e.PlanState == 0) { e.ReachedEnd = false; return; }
        Throttle(e, Cruise(e) * 0.25f);
        e.Vehicle!.Steer = 0;
    }

    private BridgeLinks? _bridges;
    internal BridgeLinks? Bridges => _bridges ??= World.Ground is IHasStatics hs ? BridgeLinks.From(hs.World) : null;

    /// <summary>
    /// PlanPath toward the current nav point: on a search's
    /// first call the road route (same road only), else the A*
    /// of §2.2, 100 expansions a call; 1 while unfinished, 0 with the
    /// waypoints built. A search in progress
    /// (AI+0x9cf4) carries on toward the nav point as it is now: the goal is
    /// read on every call and only a new search tries the road route, so a
    /// goal that moves (handle too far snapshots its target on every re-entry)
    /// does not restart it.
    /// </summary>
    private int PlanPath(Entity e)
    {
        e.Waypoints.Clear(); e.WaypointBridge.Clear(); e.WaypointIndex = 0;
        if (e.NavPath is not { } np || e.NavIndex >= np.Count) { e.Search = null; return 0; }
        var goal = new Vector2(np[e.NavIndex].X, np[e.NavIndex].Z);
        var p = Pos(e);
        if (e.Search is { } search) search.Goal = goal;
        else
        {
            if (host.Roads is { } net && RoadRoute.TryRoute(net, PathSearch.Round(p.X), PathSearch.Round(p.Z), goal.X, goal.Y, e.DetourLimit) is { } route)
            {
                SetWaypoints(e, route);
                e.Search = null;
                return 0;
            }
            e.Search = search = PathSearch.Begin(new Vector2(p.X, p.Z), goal, Bridges);
        }
        if (!search.Step(World.Ground, e.Avoid)) return 1;
        SetWaypoints(e, RoadRoute.BuildWaypoints(host.Roads, World.Ground, search.Chain(), Bridges, e.DetourLimit));
        e.Search = null;
        return 0;
    }

    /// <summary>The waypoints from x, z and the bridge flag; the walk starts at the first.</summary>
    private static void SetWaypoints(Entity e, List<PathNode> w)
    {
        e.Waypoints.Clear(); e.WaypointBridge.Clear();
        foreach (var n in w) { e.Waypoints.Add(new Vector3(n.X, 0, n.Z)); e.WaypointBridge.Add(n.Flag == 2f); }
        e.WaypointIndex = 0;
    }

    /// <summary>unit(B − A) · unit(P − A) in x/z (1 for degenerate vectors).</summary>
    private static float Ahead(Vector3 a, Vector3 b, Vector3 p)
    {
        var u = new Vector2(b.X - a.X, b.Z - a.Z); var w = new Vector2(p.X - a.X, p.Z - a.Z);
        if (u.LengthSquared() < 1e-4f || w.LengthSquared() < 1e-4f) return 1f;
        return Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(w));
    }

    /// <summary>Along the waypoints; within 40 of the nav point (Entity.NavReach), on to the next; the last one sets Arrived. Nothing once the planned path's end is reached.</summary>
    private void Advance(Entity e)
    {
        if (e.ReachedEnd) return;
        var p = Pos(e);
        var W = e.Waypoints;
        while (!e.ReachedEnd && W.Count > 0)
        {
            int i = e.WaypointIndex;
            if (i >= W.Count) break;
            var d = new Vector2(W[i].X - p.X, W[i].Z - p.Z);
            if (i == W.Count - 1)
            {
                if (d.LengthSquared() < 100) { e.ReachedEnd = true; e.ReachedLeg = true; }
                break;
            }
            if (i == 0)
            {
                if (d.LengthSquared() >= 725) break;
                e.WaypointIndex = 1;
            }
            else
            {
                if (Ahead(W[i], W[i + 1], p) <= -0.1f) break;
                e.WaypointIndex++;
            }
        }
        if (e.PlanState != 1 && e.NavPath is { Count: > 0 } path && e.NavIndex < path.Count)
        {
            int n = e.NavIndex;
            if (new Vector2(path[n].X - p.X, path[n].Z - p.Z).LengthSquared() < e.NavReach * e.NavReach)
            {
                e.ReachedLeg = true;
                e.NavIndex = n + 1;
                if (path.Count == n + 1) { e.Arrived = true; e.NavIndex = 0; }
            }
        }
    }

    /// <summary>The heading to a point `look` ahead along the waypoints, optionally 5 to the side.</summary>
    private float PathHeading(Entity e, bool offset, float look)
    {
        var W = e.Waypoints;
        var p = Pos(e);
        if (W.Count == 0) return AiDriver.Heading(e.Vehicle!);
        if (e.IsHeli) look = 24f;
        int i = System.Math.Min(e.WaypointIndex, W.Count - 1);
        Vector3 Side(int j)
        {
            var q = W[j];
            if (!offset) return q;
            var dir = j < 1 ? (W.Count > 1 ? W[1] - W[0] : W[0] - p) : W[j] - W[j - 1];
            float l2 = dir.X * dir.X + dir.Z * dir.Z;
            float k = l2 is > -1e-4f and < 1e-4f ? 0.2f : 1f / MathF.Sqrt(l2);
            return new Vector3(q.X - 5 * k * dir.Z, q.Y, q.Z + 5 * k * dir.X);
        }
        var T = Side(i);
        var d = T - p;
        float dist = new Vector2(d.X, d.Z).Length();
        if (look * look < dist * dist || i == W.Count - 1) return Heading(d);
        float rem = look - dist; var prev = T;
        for (int j = i + 1; j < W.Count; j++)
        {
            var Q = Side(j);
            float L = new Vector2(Q.X - prev.X, Q.Z - prev.Z).Length();
            if (rem < L) { var P = prev + (Q - prev) * (rem / L); return Heading(P - p); }
            rem -= L; prev = Q;
        }
        return Heading(W[i] - p);
    }

    /// <summary>The start of a search (and follow leader's enter): forward gear.</summary>
    private void BeginDriver(Entity e, Entity? _) => Forward(e.Vehicle!);

    /// <summary>The search's tick: steer 11 ahead; cruise speed when the wheel is past 0.45.</summary>
    private void FollowPathTick(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        Advance(e);
        Steer(e, PathHeading(e, false, 11f));
        v.Gear = 1f; v.DriveState = 3;
        Throttle(e, !e.IsHeli && MathF.Abs(v.Steer) > 0.45f ? Cruise(e) : e.OrderSpeed);
    }

    /// <summary>No team-0 vehicle within 80 of p, as the original tests it, quirk kept: a dot product of world positions, not a distance.</summary>
    private bool NoTeam0Near(Vector3 p)
    {
        foreach (var x in host.Entities)
            if (x.Vehicle is not null && x.Team == 0 && Vector3.Dot(p, x.Position) < 6400) return false;
        return true;
    }

    /// <summary>The evade / race tick: weave 5 to the side within 90 of the target.</summary>
    private void WeaveTick(Entity e, Entity? target, bool race)
    {
        var v = e.Vehicle!;
        Advance(e);
        if (e.WeaveTimer < Now)
        {
            e.Weave = !e.Weave;
            e.WeaveTimer = Rand() % 4 + Now + 2f;
        }
        bool near = target is not null && Vector3.DistanceSquared(target.Position, Pos(e)) < 8100 && NoTeam0Near(Pos(e))
                    && (!race || Now < e.LastDamageTime + 20f);
        float h, lim;
        if (near) { h = PathHeading(e, e.Weave, 22f); lim = 0.6f; }
        else { h = PathHeading(e, false, 11f); lim = race ? 0.5f : 0.4f; }
        Steer(e, h);
        Throttle(e, e.IsHeli || MathF.Abs(v.Steer) <= lim ? e.OrderSpeed : Cruise(e));
    }

    /// <summary>termSrch: arrived, or the script changed the path.</summary>
    private static bool TermSearch(Entity e, Entity? _) => e.Arrived || !ReferenceEquals(e.NavPath, e.Path);
    /// <summary>termFllw: the end of the planned path, the leg's nav point, or no waypoints left.</summary>
    private static bool TermFollow(Entity e, Entity? _) => e.ReachedEnd || e.ReachedLeg || e.WaypointIndex >= e.Waypoints.Count;
    /// <summary>CheckAiSearchComplete.</summary>
    private static bool SearchComplete(Entity e, Entity? _) => e.PlanState == 0;

    // ---- sit, guard, hide ----

    /// <summary>sit / guard / hide's tick: wheel straight, full brake (not traction-limited).</summary>
    private void SitTick(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        v.Steer = 0;
        v.Throttle = -1f;
    }

    /// <summary>
    /// hide's enter: a running engine is switched off, and its loop falls silent
    /// (the engine sound's stop event), so Groove asleep at T12's start has no idle until the ignition.
    /// </summary>
    private void HideEnter(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        if ((v.Flags & VehicleFlags.EngineOn) != 0) host.Damage?.Events.Add(new I76.Sim.Damage.EngineStopped(v));
        v.Flags &= ~VehicleFlags.EngineOn;
        v.DriveState = 0;
    }

    /// <summary>hide's on-match (unreachable: hide has no rules): the engine restarts.</summary>
    private void HideMatch(Entity e, Entity? _)
    {
        var v = e.Vehicle!;
        if (host.Damage is { } d) d.Ignite(v);
        else { v.Flags |= VehicleFlags.Started | (VehicleFlags)0x80000000; v.Timer = 0; }
        v.DriveState = 3;
    }

    // ---- follow ----

    /// <summary>follow leader's tick: keep station at (f1, 0, f2) in the leader's frame; "at follow" within 2.5.</summary>
    private void FollowTick(Entity e, Entity? leader)
    {
        var v = e.Vehicle!;
        if (leader is null) { Brake(e); return; }
        float hSelf = AiDriver.Heading(v);
        var velSelf = v.Vel;
        var (velLead, vLead) = Motion(leader);
        var station = leader.Position + leader.Rot.ToWorld(new Vector3(e.FollowF1, 0, e.FollowF2));
        var pos = Pos(e);
        var d = station - pos;
        float dist = d.Length();
        e.AtFollow = dist < 2.5f;
        Steer(e, dist < 1e-6f ? hSelf : Heading(d));
        float steer = v.Steer;
        float cruise = Cruise(e);
        if (vLead < 1f)
        {
            if (steer < 0.4f)
            {
                if (dist < 4f) Brake(e);
                else Throttle(e, MathF.Max(0, (dist - 1f) * 0.3f));
            }
            else Throttle(e, cruise);
        }
        else if (MathF.Abs(steer) < 0.4f)
        {
            if (dist > 150) Throttle(e, 1000f);
            else if (dist > 50) Throttle(e, vLead + MathF.Max(0, (dist - 2f) * 0.7f));
            else Throttle(e, vLead + MathF.Max(0, dist - 1f) * 0.4f);
        }
        else if (dist < 100 && vLead > 5f)
        {
            var fS = v.Rot.Forward; var fL = leader.Rot.Forward;
            if (Vector3.Dot(fS, fL) > 0.7f && Vector3.Dot(d, fS) < 0)
            {
                station += fL * (4f * dist);
                d = station - pos;
                Steer(e, Heading(d));
                Throttle(e, 0.75f * vLead);
            }
            else
            {
                float t = ClosestTime(pos, velSelf, station, velLead);
                Throttle(e, t > 0 ? vLead - 5f : cruise);
            }
        }
        else Throttle(e, cruise);
    }

    // ---- binding the table ----

    /// <summary>
    /// Two slots whose functions are separate in one build and one function in another: the 1997 CD release has each of
    /// these as its own (byte-identical) copy of the function the GOG builds share; bound alike.
    /// </summary>
    private static readonly Dictionary<string, string> SameAs = new()
    {
        ["dirt_rvs.match"] = "r_rvs_flee.match",
        ["evade.enter"] = "follow path.enter",
    };

    private static string Canonical(string key) => SameAs.GetValueOrDefault(key, key);

    /// <summary>
    /// How the table binds without the original's addresses: each handler slot (a state's tick, enter or on-match, its Nth
    /// precondition or transition, or the predicate of a rule by name) takes the port's handler keyed by the first slot that
    /// holds the same function, in table order (states in order; in each its tick, enter, on-match, preconditions,
    /// transitions, then rules): "road_ram.tick", "road_ram.pre0", "road_ram.trans4", "road_ram.clsnDmg". Every address
    /// differs between the executables, but which slots share a function does not (the GOG Interstate '76 and Nitro Pack
    /// builds; the 1997 CD release with the two <see cref="SameAs"/> pairs), so these keys name the same functions in all of
    /// them, where an ordinal of distinct functions would shift at the first function one build splits. A slot the port
    /// binds by its own key (or a <see cref="SameAs"/>) that the table makes share an earlier, differently bound slot's
    /// function is an error, so a build that merged two functions the port keeps apart cannot bind silently; one that split
    /// a function the port shares has a slot with no handler, also an error.
    /// </summary>
    public static List<(int State, string Slot, string Key)> SlotKeys(PtEntry[] table)
    {
        var first = new Dictionary<uint, string>();
        var list = new List<(int, string, string)>();
        foreach (var d in table)
        {
            void Add(string field, uint fn)
            {
                if (fn == 0) return;
                string slot = $"{d.Name}.{field}";
                if (!first.TryGetValue(fn, out var key)) first[fn] = key = slot;
                list.Add((d.Id, slot, Canonical(key)));
            }
            Add("tick", d.Tick); Add("enter", d.Enter); Add("match", d.OnMatch);
            for (int i = 0; i < d.Pre.Length; i++) Add($"pre{i}", d.Pre[i]);
            for (int i = 0; i < d.Trans.Length; i++) Add($"trans{i}", d.Trans[i].Pred);
            foreach (var r in d.Rules) Add(r.Name, r.Pred);
        }
        return list;
    }

    private State[] Bind(PtEntry[] table)
    {
        var handlers = new Dictionary<string, Handler>
        {
            // ticks
            ["road_ram.tick"] = (e, t) => RoadWar(e, t!, 0), ["rw_flee.tick"] = (e, t) => RoadWar(e, t!, 1), ["road_side.tick"] = (e, t) => RoadWar(e, t!, 3),
            ["dirt_ram.tick"] = (e, t) => DirtCombat(e, t!, 0), ["dirt_gunner.tick"] = (e, t) => DirtCombat(e, t!, 1), ["dirt_miner.tick"] = (e, t) => DirtCombat(e, t!, 2),
            ["dirt_brave.tick"] = (e, t) => DirtCombat(e, t!, 3), ["dirt_flee.tick"] = (e, t) => DirtCombat(e, t!, 4),
            ["tactic_skid.tick"] = SkidTick, ["dirt_standoff.tick"] = StandoffTick, ["road_flee.tick"] = RoadFleeTick, ["r_rvs_flee.tick"] = RoadFleeTick,
            ["rockford.tick"] = RockfordTick, ["dirt_circle.tick"] = CircleTick,
            ["stop_sliding_backward.tick"] = StopSlidingTick, ["avoid clsn.tick"] = AvoidTick, ["back_away.tick"] = BackAwayTick, ["just do it.tick"] = JustDoItTick,
            ["handle too far search.tick"] = PlannerTick, ["handle too far follow.tick"] = FollowPathTick, ["evade.tick"] = (e, t) => WeaveTick(e, t, false), ["race.tick"] = (e, t) => WeaveTick(e, t, true),
            ["tactic on deck.tick"] = OnDeckTick, ["handle follow leader.tick"] = FollowTick, ["sit.tick"] = SitTick,
            // enters
            ["road_ram.enter"] = RoadRamEnter, ["rw_flee.enter"] = RwFleeEnter, ["road_side.enter"] = RoadSideEnter,
            ["dirt_ram.enter"] = (e, t) => DirtEnter(e, t, Now + 7f), ["dirt_brave.enter"] = (e, t) => DirtEnter(e, t, Now + 20f + Rand() % 15),
            ["dirt_gunner.enter"] = (e, t) => DirtEnter(e, t, Now + 20f + Rand() % 15), ["dirt_miner.enter"] = (e, t) => DirtEnter(e, t, Now + 10f + Rand() % 10),
            ["tactic_skid.enter"] = SkidEnter, ["dirt_standoff.enter"] = StandoffEnter, ["road_flee.enter"] = RoadFleeEnter, ["r_rvs_flee.enter"] = ReverseFleeEnter,
            ["rockford.enter"] = RockfordEnter, ["dirt_rvs.enter"] = DirtReverseEnter, ["dirt_flee.enter"] = DirtFleeEnter, ["dirt_circle.enter"] = CircleEnter,
            ["stop_sliding_backward.enter"] = StopSlidingEnter, ["avoid clsn.enter"] = AvoidEnter, ["back_away.enter"] = BackAwayEnter, ["just do it.enter"] = JustDoItEnter,
            ["handle too far search.enter"] = TooFarEnter, ["do search.enter"] = BeginPathFollow, ["follow path.enter"] = BeginDriver, ["do race.enter"] = BeginRace,
            ["tactic on deck.enter"] = OnDeckEnter, ["handle follow leader.enter"] = BeginDriver, ["hide.enter"] = HideEnter,
            // on-match
            ["r_rvs_flee.match"] = (e, _) => { e.Vehicle!.Gear = 1f; e.Vehicle!.DriveState = 3; },
            ["rockford.match"] = (e, _) => Forward(e.Vehicle!),
            ["avoid clsn.match"] = AvoidMatch, ["back_away.match"] = BackAwayMatch, ["just do it.match"] = (e, _) => FlipGear(e.Vehicle!),
            ["tactic on deck.match"] = (e, t) => e.AttackCommit = t, ["hide.match"] = HideMatch,
        };
        var preds = new Dictionary<string, Pred>
        {
            // preconditions
            ["road_ram.pre0"] = (_, _) => true, ["road_ram.pre1"] = (_, t) => IsVehicle(t), ["road_ram.pre2"] = FrontIntact,
            ["road_ram.pre3"] = SharesRoad, ["rw_flee.pre2"] = CloseAndHurt, ["dirt_ram.pre2"] = OffRoadAllowed, ["dirt_brave.pre1"] = (e, _) => ForwardGunLoaded(e),
            ["dirt_standoff.pre2"] = StandoffWeapon, ["road_flee.pre1"] = SameRoad, ["dirt_rvs.pre1"] = (e, t) => e.Vehicle!.Speed < 5 && Dist(e, t!) < 40,
            // transitions
            ["road_ram.trans0"] = OnDeckNeeded, ["road_ram.trans1"] = LosLost, ["road_ram.trans2"] = TooFar, ["road_ram.trans3"] = SlidingBackward,
            ["road_ram.trans4"] = (e, t) => AvoidTrigger(e, t, AvoidKind.KeepSteer), ["dirt_gunner.trans4"] = (e, t) => AvoidTrigger(e, t, AvoidKind.Vehicles),
            ["rw_flee.trans2"] = (e, t) => AvoidTrigger(e, t, AvoidKind.Terrain), ["road_ram.trans5"] = BoxedIn, ["dirt_brave.trans6"] = SkidChance,
            ["avoid clsn.trans0"] = AvoidToBackAway, ["back_away.trans0"] = BackAwayToJustDoIt, ["handle too far search.trans0"] = SearchComplete,
            // rules
            ["road_ram.clsnDmg"] = (e, _) => e.RammedTick == host.FsmTick - 1 || e.RammedTick == host.FsmTick,
            ["road_ram.timeUp"] = (e, _) => Now > e.TacticDeadline, ["tactic_skid.clsnTime"] = (e, _) => Now > e.EscapeTimer,
            ["road_ram.termRW"] = TermRoadWar, ["road_side.termSd"] = (e, t) => Motion(t).Speed < 5 && Dist(e, t!) < 30,
            ["dirt_brave.termRW"] = TermDirtWar, ["road_flee.termflee"] = TermFlee, ["rockford.termRock"] = TermRock,
            ["stop_sliding_backward.termRev"] = (e, t) => !SlidingBackward(e, t), ["back_away.termBK"] = TermBackAway, ["handle too far search.termTF"] = TermTooFar,
            ["handle too far search.termSrch"] = TermSearch, ["handle too far follow.termFllw"] = TermFollow, ["tactic on deck.termOndk"] = (e, t) => SlotAvailable(e, t), ["handle los.termJam"] = (e, t) => !LosLost(e, t),
        };
        var keys = SlotKeys(table);
        foreach (var (_, slot, key) in keys)
            if ((handlers.ContainsKey(slot) || preds.ContainsKey(slot) || SameAs.ContainsKey(slot)) && Canonical(slot) != key)
                throw new InvalidDataException($"Ptable: {slot} shares {key}'s function in this executable; the port binds them apart");
        var bySlot = keys.ToDictionary(k => k.Slot, k => k.Key);
        Handler H(PtEntry d, string field) => handlers.TryGetValue(bySlot[$"{d.Name}.{field}"], out var h) ? h
            : throw new InvalidDataException($"Ptable: no handler for {d.Name}.{field} ({bySlot[$"{d.Name}.{field}"]})");
        Pred P(PtEntry d, string field) => preds.TryGetValue(bySlot[$"{d.Name}.{field}"], out var p) ? p
            : throw new InvalidDataException($"Ptable: no predicate for {d.Name}.{field} ({bySlot[$"{d.Name}.{field}"]})");
        return table.Select(d => new State(d,
            d.Tick == 0 ? (_, _) => { } : H(d, "tick"),
            d.Enter == 0 ? null : H(d, "enter"),
            d.OnMatch == 0 ? null : H(d, "match"),
            d.Pre.Select((_, i) => P(d, $"pre{i}")).ToArray(),
            d.Trans.Select((t, i) => (P(d, $"trans{i}"), t.Next)).ToArray(),
            d.Rules.Select(r => new Rule(r, r.Pred == 0 ? null : P(d, r.Name))).ToArray())).ToArray();
    }
}
