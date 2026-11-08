using System.Numerics;
using I76.Formats.Mission;
using I76.Sim.Rng;

namespace I76.Sim.Fsm;

/// <summary>
/// The mission-script VM: every thread stepped once per
/// frame in file order (FsmTick) until it yields or returns;
/// The script step for the 14 bytecode instructions;
/// The original's script opcode handler for the game opcodes, by name.
/// </summary>
public sealed partial class FsmVm
{
    /// <summary>A pointer into the shared data array or into a thread's stack.</summary>
    public readonly record struct Ref(bool Data, int Index, Thread? T);

    /// <summary>A stack slot: an int, or a pointer (thread parameters are pointers into data).</summary>
    public struct Slot { public int Value; public Ref? Ptr; }

    public sealed class Thread
    {
        public Slot[] Stack = new Slot[1024];
        public readonly List<Ref> Args = [];
        public int Pc, Fp, Sp;
        public bool Cond;
        public bool Finished;
        public int Index;
        /// <summary>Co-op: the human a condition on "the player" matched; an action on the player this tick goes to them. Cleared at the yield.</summary>
        public object? Match;
        /// <summary>Co-op: the seat last matched, tried first by the next condition (a jump camera keeps its jumper).</summary>
        public int Prefer = -1;
        /// <summary>Co-op: a per-human copy of a <see cref="CoopRoutines"/> routine; "the player" is this seat alone (−1: the group).</summary>
        public int Bound = -1;
        /// <summary>The program's thread this one runs: its own index, or the one a per-human copy copies.</summary>
        public int Source;
        /// <summary>Co-op: the leave-map routine (<see cref="CoopRoutines"/>), run once per human.</summary>
        public bool LeaveMap;
        /// <summary>Co-op: the seat whose personal camera scene (<see cref="CoopCameras"/>) this thread is in, −1 outside one; its group arguments are that human alone.</summary>
        public int Scene = -1;
        /// <summary>The pushCams of that scene not yet popped.</summary>
        public int SceneDepth;
        /// <summary>Co-op: the gate site (<see cref="CoopGates"/>, its test's pc) whose shutting this thread holds for the humans still behind the gate; −1 none.</summary>
        public int GatePc = -1;
        /// <summary>When that hold began (mission time).</summary>
        public float GateSince;
    }

    private readonly FsmProgram _p;
    private readonly IFsmHost _host;
    private readonly CrtRand _rand;
    public int[] Data { get; }
    public List<Thread> Threads { get; } = [];
    /// <summary>The actor table: label, instance id, resolved handle (null once removed). Grows at run time.</summary>
    public List<(string Label, byte[] Id, object? Handle)> Actors { get; } = [];
    /// <summary>The nav paths' points, for the CLI's listing.</summary>
    public IEnumerable<IReadOnlyList<Vector3>> PathsDebug => _p.Paths.Select(p => p.Points);
    public int TickCount { get; private set; }
    public bool Watchdog { get; private set; }
    private readonly HashSet<string> _warned = [];
    /// <summary>The paths whose node 0 a teleport has put on the ground, with the height it wrote there.</summary>
    private readonly SortedDictionary<int, float> _grounded = [];

    public FsmVm(FsmProgram p, IFsmHost host, CrtRand rand)
    {
        _p = p; _host = host; _rand = rand;
        Data = (int[])p.Data.Clone();
        foreach (var (label, id) in p.Actors)
            Actors.Add((label, id, host.FindByInstanceId(id)));
        for (int i = 0; i < p.Threads.Count; i++)
        {
            var t = new Thread { Index = i, Source = i };
            Start(t);
            Threads.Add(t);
        }
        if (host.CoopSeats > 0) CoopSetup();
    }

    /// <summary>A thread at the start of its routine, its parameters pointing into the data as its table entry says.</summary>
    private void Start(Thread t)
    {
        var (pc, depth, stack) = _p.Threads[t.Source];
        Array.Clear(t.Stack);
        t.Args.Clear();
        for (int k = 0; k < depth; k++) t.Stack[k].Ptr = new Ref(true, stack[k], null);
        t.Sp = depth;
        t.Stack[t.Sp++].Value = 0;          // fake old fp
        t.Fp = t.Sp;
        t.Stack[t.Sp++].Value = pc;         // fake return pc = start
        t.Pc = pc;
        t.Cond = false;
        t.Match = null;
        t.GatePc = -1;
        EndScene(t);
    }

    // ---- co-op (docs/multiplayer.md "Co-op") ----
    /// <summary>The script names the human group with these actor slots (the player placement's; T16 and T17 have two).</summary>
    private readonly HashSet<int> _group = [];
    private bool _coop;
    /// <summary>The penalty counters (<see cref="CoopRoutines"/>), each human's own count; null until first written.</summary>
    private readonly Dictionary<CoopRoutines.Cell, int[]?> _shadows = [];
    /// <summary>What <see cref="CoopRoutines"/> found in the program.</summary>
    public CoopRoutines.Analysis Routines { get; private set; } = CoopRoutines.Analysis.None;
    /// <summary>A per-human leave-map thread was given back its human: it starts over once this instruction is done.</summary>
    private bool _restart;
    /// <summary>The pushCam instructions that begin a player's own camera scene (<see cref="CoopCameras"/>).</summary>
    private readonly HashSet<int> _personalCams = [];
    /// <summary>What <see cref="CoopCameras"/> made of every pushCam.</summary>
    public IReadOnlyList<CoopCameras.Site> CameraSites { get; private set; } = [];
    /// <summary>The gates the script shuts behind the player (<see cref="CoopGates"/>), by their test's pc.</summary>
    public IReadOnlyDictionary<int, CoopGates.Site> GateSites { get; private set; } = new Dictionary<int, CoopGates.Site>();
    /// <summary>How long a gate the script shuts behind the player waits for the humans still behind it before they are brought through (mission seconds).</summary>
    public const float GateHoldSeconds = 30;
    /// <summary>How far past a gate's line a human must be to count as through it (metres).</summary>
    public const float GateClear = 3;

    /// <summary>
    /// The group slots, then the routines that run per human: each leave-map thread is the first seat's, and a copy
    /// of it for every other seat is added after the program's threads; the penalty counters are counted per human.
    /// </summary>
    private void CoopSetup()
    {
        _coop = true;
        for (int i = 0; i < Actors.Count; i++)
            if (Actors[i].Handle is { } h && _host.IsPlayerHandle(h)) _group.Add(i);
        Routines = CoopRoutines.Analyze(_p, i => _group.Contains(i), i => i >= 0 && i < Actors.Count && Actors[i].Handle is { } h ? _host.Team(h) : null);
        CameraSites = CoopCameras.Analyze(_p, i => _group.Contains(i));
        foreach (var site in CameraSites) if (site.Kind == CoopCameras.Kind.Personal) _personalCams.Add(site.Pc);
        GateSites = CoopGates.Analyze(_p, i => _group.Contains(i));
        foreach (var c in Routines.Counters.Keys) _shadows[c] = null;
        foreach (int ti in Routines.LeaveMapThreads)
        {
            Threads[ti].Bound = 0;
            Threads[ti].LeaveMap = true;
            for (int k = 1; k < _host.CoopSeats; k++)
            {
                var t = new Thread { Index = Threads.Count, Source = ti, Bound = k, LeaveMap = true };
                Start(t);
                Threads.Add(t);
            }
        }
    }

    /// <summary>
    /// A human away from every exit: outside the leave-map routines' edge radius (the last warning's) of each exit
    /// point, as isWithinSqNav measures. A teammate to come back beside.
    /// </summary>
    private bool InBounds(int seat)
    {
        if (_host.Human(seat) is not { } o) return false;
        var p = _host.Position(o);
        foreach (var lm in Routines.LeaveMap)
        {
            if (lm.PathCell < 0 || lm.PathCell >= Data.Length || Data[lm.PathCell] is var pi && (pi < 0 || pi >= _p.Paths.Count) || _p.Paths[pi].Points.Count == 0) continue;
            var x = _p.Paths[pi].Points[0];
            if (MathF.Abs(x.X - p.X) < lm.Edge && MathF.Abs(x.Z - p.Z) < lm.Edge) return false;
        }
        return true;
    }

    /// <summary>The one seat a thread's group arguments are: its bound seat, or the seat of the personal camera scene it is in; −1 for the group.</summary>
    private static int Pin(Thread t) => t.Bound >= 0 ? t.Bound : t.Scene;

    /// <summary>The thread's personal camera scene is over (it returned, started over, or the script was reset): the human's view is given back.</summary>
    private void EndScene(Thread t)
    {
        if (t.Scene >= 0) _host.EndPersonalScene(t.Scene);
        t.Scene = -1;
        t.SceneDepth = 0;
    }

    /// <summary>Whether argument i names the human group.</summary>
    private bool Group(Thread t, int i) => _coop && _group.Contains(V(t, i)) && O(t, i) is not null;

    /// <summary>
    /// A gate the script shuts behind the player (<see cref="CoopGates"/>): the test at <paramref name="t"/>'s pc came
    /// true for a human (<paramref name="cond"/>), or a hold of it goes on. While a living human is still on the near
    /// side of the gate's line (the side away from <paramref name="zone"/>, the test's point) the test reads false and
    /// the gate stays open, for <see cref="GateHoldSeconds"/> at most; then those humans are brought through beside the
    /// teammate furthest past it and the gate shuts. The test stands as it is with nobody behind, for a gate already
    /// shut or sliding, and outside co-op.
    /// </summary>
    private bool GateHold(Thread t, Vector3 zone, bool cond)
    {
        if (!_coop || Pin(t) >= 0 || !GateSites.TryGetValue(t.Pc, out var site)) return cond;
        if (!cond && t.GatePc != t.Pc) return false;
        var arg = site.ArgOp == 5 ? t.Stack[t.Fp + site.Arg].Ptr ?? new Ref(false, t.Fp + site.Arg, t) : new Ref(false, t.Fp + site.Arg, t);
        int gi = Get(arg);
        var gate = gi >= 0 && gi < Actors.Count ? Actors[gi].Handle : null;
        if (gate is null || _host.IsRemoved(gate) || _host.OpenGate(gate) is not var (at, slide)) { t.GatePc = -1; return cond; }
        var n = Vector3.Normalize(new Vector3(-slide.Z, 0, slide.X));
        float Side(Vector3 p) => (p.X - at.X) * n.X + (p.Z - at.Z) * n.Z;
        float far = Side(zone);
        if (MathF.Abs(far) < 1) { t.GatePc = -1; return cond; }
        far = MathF.Sign(far);
        var behind = new List<int>();
        int anchor = -1; float best = GateClear;
        for (int k = 0; k < _host.CoopSeats; k++)
        {
            if (!_host.HumanAlive(k) || _host.Human(k) is not { } o) continue;
            float d = Side(_host.Position(o)) * far;
            if (d < GateClear) behind.Add(k);
            else if (d >= best) { best = d; anchor = k; }
        }
        if (behind.Count == 0) { t.GatePc = -1; return true; }
        // nobody through to wait for or bring the others beside: the hold starts over when someone passes again
        if (anchor < 0) { t.GatePc = -1; return false; }
        if (t.GatePc != t.Pc)
        {
            t.GatePc = t.Pc; t.GateSince = _host.Now;
            _host.Log($"co-op: gate held open for seat{(behind.Count > 1 ? "s" : "")} {string.Join(", ", behind)} behind it");
        }
        if (_host.Now - t.GateSince < GateHoldSeconds) return false;
        foreach (int k in behind) _host.BringThrough(k, anchor);
        t.GatePc = -1;
        return true;
    }

    /// <summary>The living humans a group argument stands for: the bound seat alone, else the matched (or last matched) first, then by seat.</summary>
    private IEnumerable<object> Members(Thread t)
    {
        if (Pin(t) >= 0)
        {
            if (_host.HumanAlive(Pin(t)) && _host.Human(Pin(t)) is { } b) yield return b;
            yield break;
        }
        int n = _host.CoopSeats;
        int first = t.Match is { } m ? _host.HumanSeat(m) : t.Prefer;
        if (first >= 0 && first < n && _host.HumanAlive(first) && _host.Human(first) is { } f) yield return f;
        for (int k = 0; k < n; k++)
            if (k != first && _host.HumanAlive(k) && _host.Human(k) is { } x) yield return x;
    }

    /// <summary>A condition on the group: true if a living human satisfies it, who becomes the thread's match.</summary>
    private bool Any(Thread t, Func<object, bool> test)
    {
        foreach (var m in Members(t))
            if (test(m)) { t.Match = m; t.Prefer = _host.HumanSeat(m); return true; }
        return false;
    }

    /// <summary>A condition on argument i: the group's any-human, else the object's own (false with none).</summary>
    private bool Test(Thread t, int i, Func<object, bool> test)
    {
        if (Group(t, i)) return Any(t, test);
        var o = O(t, i);
        return o is not null && test(o);
    }

    /// <summary>The one car an action on argument i acts on: the bound seat, the thread's match, else the lead.</summary>
    private object? Act(Thread t, int i)
    {
        if (!Group(t, i)) return O(t, i);
        if (Pin(t) >= 0) return _host.Human(Pin(t));
        return t.Match ?? _host.Human(_host.LeadSeat);
    }

    /// <summary>Every human for a setting on the group (the bound seat alone), else the object.</summary>
    private IEnumerable<object> All(Thread t, int i)
    {
        if (!Group(t, i)) { if (O(t, i) is { } o) yield return o; yield break; }
        if (Pin(t) >= 0) { if (_host.Human(Pin(t)) is { } b) yield return b; yield break; }
        for (int k = 0; k < _host.CoopSeats; k++) if (_host.Human(k) is { } h) yield return h;
    }

    /// <summary>An order's target on argument i: the group is the living human nearest the commanded car (with hysteresis).</summary>
    private object? Aim(Thread t, int i, object commanded)
    {
        if (!Group(t, i)) return O(t, i);
        if (t.Bound >= 0) return _host.Human(t.Bound);
        return _host.NearestHuman(commanded) ?? _host.Human(_host.LeadSeat);
    }

    /// <summary>
    /// set / inc / dec on a penalty counter: the human whose deed it counts (the thread's match; with none, every
    /// human) has their own count changed, and the cell holds the highest, which the script's limit is tested against.
    /// </summary>
    private bool Counted(Thread t, Func<int, int> op)
    {
        if (_shadows.Count == 0 || t.Args.Count == 0) return false;
        var r = t.Args[0];
        var key = new CoopRoutines.Cell(r.Data ? -1 : r.T?.Index ?? -2, r.Index);
        if (!_shadows.TryGetValue(key, out var per)) return false;
        if (per is null) { per = new int[_host.CoopSeats]; Array.Fill(per, Get(r)); _shadows[key] = per; }
        int seat = t.Bound >= 0 ? t.Bound : t.Match is { } m ? _host.HumanSeat(m) : -1;
        for (int k = 0; k < per.Length; k++) if (seat < 0 || k == seat) per[k] = op(per[k]);
        Set(r, per.Max());
        return true;
    }

    /// <summary>FsmTick: run every live thread once, then advance the tick counter.</summary>
    public void Tick()
    {
        foreach (var t in Threads)
        {
            // a scene the host ended under the thread (its human left the map, died, or left the game)
            if (t.Scene >= 0 && !_host.PersonalSceneActive(t.Scene) && !t.Finished) { t.Scene = -1; t.SceneDepth = 0; }
            if (!t.Finished && Step(t)) { t.Finished = true; EndScene(t); }
        }
        TickCount++;
    }

    private int Get(Ref r) => r.Data ? Data[r.Index] : r.T!.Stack[r.Index].Value;
    private void Set(Ref r, int v)
    {
        if (r.Data) Data[r.Index] = v;
        else { r.T!.Stack[r.Index].Value = v; r.T.Stack[r.Index].Ptr = null; }
    }

    /// <summary>The script step: returns true when the thread has finished.</summary>
    private bool Step(Thread t)
    {
        for (int n = 0; ; n++)
        {
            if (n > 800) Watchdog = true;
            if (n >= 1000) { _host.Log($"FSM watchdog: thread {t.Index} ran 1000 instructions; the original exits here"); t.Match = null; return false; }
            if (t.Pc < 0 || t.Pc >= _p.Code.Length) { _host.Log($"FSM thread {t.Index}: pc {t.Pc} out of range"); return true; }
            var (op, arg) = _p.Code[t.Pc];
            int a = (int)arg;
            switch (op)
            {
                case 1: t.Stack[t.Sp++] = new Slot { Value = a }; t.Pc++; break;
                case 2: t.Stack[t.Sp++] = new Slot { Ptr = new Ref(false, t.Fp + a, t) }; t.Pc++; break;
                case 3: t.Stack[t.Sp++] = t.Stack[t.Fp + a]; t.Pc++; break;
                case 4: t.Args.Add(new Ref(false, t.Fp + a, t)); t.Pc++; break;
                case 5:
                {
                    var s = t.Stack[t.Fp + a];
                    // argLocal forwards a parameter: a pointer to data (or, if a plain int, a pointer to that slot)
                    t.Args.Add(s.Ptr ?? new Ref(false, t.Fp + a, t));
                    t.Pc++;
                    break;
                }
                case 6: t.Sp += a; t.Pc++; break;                // cells keep whatever they held (zero at start)
                case 7: t.Sp -= a; t.Pc++; break;
                case 8: t.Pc = a; break;
                case 9: t.Pc = t.Cond ? t.Pc + 1 : a; break;
                case 10: t.Pc = a; t.Match = null; return false;
                case 11:
                    t.Stack[t.Sp++].Value = t.Fp;
                    t.Fp = t.Sp;
                    t.Stack[t.Sp++].Value = t.Pc + 1;
                    t.Pc = a;
                    break;
                case 12:
                {
                    int f = t.Fp;
                    t.Pc = t.Stack[f].Value;
                    t.Sp = f - (a + 1);
                    t.Fp = t.Stack[f - 1].Value;
                    if (t.Sp <= 0) return true;
                    break;
                }
                case 13:
                    Exec(t, a);
                    t.Args.Clear();
                    t.Pc++;
                    if (_restart) { _restart = false; Start(t); return false; }
                    break;
                case 14: t.Cond = !t.Cond; t.Pc++; break;
                default:
                    _host.Log($"Unknown fsm assembler instuction {op} at {t.Pc}");
                    return false;
            }
        }
    }

    // ---- argument helpers ----
    private int V(Thread t, int i) => i < t.Args.Count ? Get(t.Args[i]) : 0;
    private void W(Thread t, int i, int v) { if (i < t.Args.Count) Set(t.Args[i], v); }

    /// <summary>The object-handle lookup: a removed object resolves to null, for good.</summary>
    private object? O(Thread t, int i)
    {
        int idx = V(t, i);
        if (idx < 0 || idx >= Actors.Count) return null;
        var (label, id, h) = Actors[idx];
        if (h is not null && _host.IsRemoved(h)) { Actors[idx] = (label, id, null); return null; }
        return h;
    }

    private void Ground(int path, float y)
    {
        var pts = _p.Paths[path].Points;
        pts[0] = pts[0] with { Y = y };
        _grounded[path] = y;
    }

    private IReadOnlyList<Vector3>? N(Thread t, int i)
    {
        int idx = V(t, i);
        return idx >= 0 && idx < _p.Paths.Count ? _p.Paths[idx].Points : null;
    }

    private string? S(Thread t, int i)
    {
        int idx = V(t, i);
        return idx >= 0 && idx < _p.Sounds.Count ? _p.Sounds[idx] : null;
    }

    /// <summary>
    /// whoX / nearestX: the actor index of an object (null: the first entry whose handle is null), appending a "NoLabel"
    /// entry if needed. In co-op a human is never named by a group slot: the script then means that human, not the group.
    /// </summary>
    private int ActorIndex(object? o)
    {
        bool human = _coop && o is not null && _host.HumanSeat(o) >= 0;
        for (int i = 0; i < Actors.Count; i++) if (ReferenceEquals(Actors[i].Handle, o) && !(human && _group.Contains(i))) return i;
        Actors.Add(($"NoLabel{Actors.Count}", [], o));
        return Actors.Count - 1;
    }

    /// <summary>
    /// isEqualId: the same object; co-op, "the player" against a human is true for any human (who becomes the match:
    /// T01's friendly-fire count goes against the one who fired), and the group against itself always.
    /// </summary>
    private bool EqualId(Thread t)
    {
        bool ga = Group(t, 0), gb = Group(t, 1);
        if (!ga && !gb) return ReferenceEquals(O(t, 0), O(t, 1));
        if (ga && gb) return true;
        var other = ga ? O(t, 1) : O(t, 0);
        if (other is null || _host.HumanSeat(other) is not (var seat and >= 0) || Pin(t) >= 0 && seat != Pin(t)) return false;
        t.Match = other;
        t.Prefer = seat;
        return true;
    }

    private void Warn(string what)
    {
        if (_warned.Add(what)) _host.Log(what);
    }

    /// <summary>The original's script opcode handler: the game opcode named by the mission's prototype table.</summary>
    private void Exec(Thread t, int proto)
    {
        var name = proto >= 0 && proto < _p.Prototypes.Count ? _p.Prototypes[proto] : "";
        var h = _host;
        switch (name.ToLowerInvariant())
        {
            case "null": break;
            case "behave": case "do": Warn($"FSM: {name} is not implimented"); break;
            case "test": case "islit": Warn($"FSM: {name} is not implimented"); t.Cond = false; break;
            case "playscene": h.Log("FSM: playScene has no case in the interpreter; the original aborts"); break;

            case "set": case "setid": { int v = V(t, 1); if (!Counted(t, _ => v)) W(t, 0, v); break; }
            case "inc": if (!Counted(t, x => x + 1)) W(t, 0, V(t, 0) + 1); break;
            case "dec": if (!Counted(t, x => x - 1)) W(t, 0, V(t, 0) - 1); break;
            case "rand": { int m = V(t, 1); W(t, 0, m == 0 ? 0 : _rand.Next() % m); break; }
            case "starttimer": W(t, 0, (int)(h.Now + 0.5f)); break; // whole seconds, rounded to the nearest
            case "true": t.Cond = true; break;
            case "false": t.Cond = false; break;
            case "isequal": t.Cond = V(t, 0) == V(t, 1); break;
            case "isgreater": t.Cond = V(t, 0) > V(t, 1); break;
            case "islesser": t.Cond = V(t, 0) < V(t, 1); break;
            case "timegreater": t.Cond = (float)(V(t, 0) + V(t, 1)) < (int)h.Now + 0.5f; break;
            case "timelesser": t.Cond = (int)h.Now + 0.5f < (float)(V(t, 0) + V(t, 1)); break;

            // co-op: the group is dead when every human is (a bound copy: its own human)
            case "isdead": { t.Cond = Group(t, 0) ? !Members(t).Any() : O(t, 0) is { } d && h.IsDead(d); break; }
            case "isequalid": t.Cond = EqualId(t); break;
            case "iswithin":
            {
                float r = V(t, 2);
                bool Near(object a, object b) => Vector3.DistanceSquared(h.Position(a), h.Position(b)) < r * r;
                bool ga = Group(t, 0), gb = Group(t, 1);
                if (ga && gb) t.Cond = Any(t, _ => 0 < r * r);
                else if (ga) t.Cond = O(t, 1) is { } b && GateHold(t, h.Position(b), Any(t, a => Near(a, b)));
                else if (gb) t.Cond = O(t, 0) is { } a && GateHold(t, h.Position(a), Any(t, b => Near(a, b)));
                else { var a = O(t, 0); var b = O(t, 1); t.Cond = a is not null && b is not null && Near(a, b); }
                break;
            }
            case "iswithinnav": case "iswithinsqnav":
            {
                var path = N(t, 0); float r = V(t, 2);
                if (path is null || path.Count == 0) { _ = O(t, 1); t.Cond = false; break; }
                bool sq = !name.Equals("isWithinNav", StringComparison.OrdinalIgnoreCase);
                t.Cond = Test(t, 1, o =>
                {
                    var p = h.Position(o);
                    float dx = path[0].X - p.X, dz = path[0].Z - p.Z;
                    return !sq ? dx * dx + dz * dz < r * r : MathF.Abs(dx) < r && MathF.Abs(dz) < r;
                });
                if (Group(t, 1)) t.Cond = GateHold(t, path[0], t.Cond);
                break;
            }
            case "iswithinenemy":
            {
                float r = V(t, 1);
                // The AI vehicle lists of teams 1..7 but its own (no emplacements, no structures)
                t.Cond = Test(t, 0, o =>
                {
                    var p = h.Position(o); int team = h.Team(o);
                    foreach (var x in h.AiVehicles)
                    {
                        if (ReferenceEquals(x, o) || h.Team(x) == team || h.Team(x) == 0 || h.IsDead(x)) continue;
                        var q = h.Position(x);
                        if ((q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z) < r * r) return true;
                    }
                    return false;
                });
                break;
            }
            case "hplesser": { int pct = V(t, 2); t.Cond = Test(t, 0, o => h.HealthPercent(o) < pct); break; }
            case "isairborne": t.Cond = Test(t, 0, h.IsAirborne); break;
            case "isarrived": t.Cond = Test(t, 0, h.ConsumeArrived); break;
            case "isatfollow": t.Cond = Test(t, 0, h.ConsumeAtFollow); break;
            case "isshot": t.Cond = Test(t, 0, h.ConsumeShot); break;
            case "isrammed": t.Cond = Test(t, 0, h.ConsumeRammed); break;
            case "isattacked": t.Cond = Test(t, 0, h.ConsumeAttacked); break;
            case "isgroovesfault":
            {
                var o = O(t, 0);
                t.Cond = o is not null && h.KilledByPlayer(o);
                // co-op: whichever human it was; a following inc counts it against them
                if (t.Cond && _coop && h.KillerSeat(o!) is var ks and >= 0 && h.Human(ks) is { } killer) t.Match = killer;
                break;
            }
            case "allenemydead":
            {
                var o = O(t, 0); int team = o is null ? 0 : h.Team(o);
                t.Cond = h.AiVehicles.Where(x => h.Team(x) != team && h.Team(x) != 0).All(h.IsDead);
                break;
            }
            case "allblgdead": t.Cond = h.Structures.All(h.IsDead); break;
            case "cansee":
            {
                bool ga = Group(t, 0), gb = Group(t, 1);
                if (ga && !gb) t.Cond = O(t, 1) is { } b && Any(t, a => h.CanSee(a, b));
                else if (gb && !ga) t.Cond = O(t, 0) is { } a && Any(t, b => h.CanSee(a, b));
                else { var a = Act(t, 0); var b = Act(t, 1); t.Cond = a is not null && b is not null && h.CanSee(a, b); }
                break;
            }
            case "ammolesser": t.Cond = false; break;
            case "iscbempty": t.Cond = h.CbEmpty; break;
            case "camisarrived": t.Cond = t.Scene >= 0 ? h.PersonalCameraArrived(t.Scene) : h.CameraArrived(); break;
            case "iskeypress": t.Cond = t.Scene < 0 && h.SkipPressed(); break;   // a personal scene is no one's to skip: it is short and ends by its own clock
            case "controldone": { var o = Act(t, 0); t.Cond = o is not null && h.ControlDone(o); break; }

            case "whorammed": case "whoshot": case "whoattacked":
            {
                int kind = name.Equals("whoRammed", StringComparison.OrdinalIgnoreCase) ? 0 : name.Equals("whoShot", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                // co-op: the group's is the matched (or bound) human's attacker, else the last any human had
                var who = Group(t, 0) && t.Bound < 0 && t.Match is null ? h.HumansLastAttacker(kind)
                    : Act(t, 0) is { } o ? h.LastAttacker(o, kind) : null;
                // With nobody yet it logs and still writes, the first actor with a null handle (else a new one)
                if (who is null) Warn($"FSM {name} called but nobody has done it yet");
                W(t, 1, ActorIndex(who));
                break;
            }
            case "nearestenemy": case "nearestblg":
            {
                var o = O(t, 0);
                if (o is null) break;
                int team = h.Team(o);
                // co-op: nearest to any living human
                var from = Group(t, 0) ? Members(t).Select(h.Position).ToList() : [h.Position(o)];
                if (from.Count == 0) from.Add(h.Position(o));
                bool blg = name.Equals("nearestBlg", StringComparison.OrdinalIgnoreCase);
                // nearestEnemy (Nitro): live vehicles of the AI lists, teams 1..7 but its own;
                // nearestBlg: live structures of the root collider lists, any team
                var pool = blg ? h.Structures.Where(x => !h.IsDead(x)) : h.AiVehicles.Where(x => h.Team(x) != team && h.Team(x) != 0 && !h.IsDead(x));
                object? best = null; float bd = float.MaxValue;
                foreach (var x in pool)
                {
                    var q = h.Position(x);
                    foreach (var p in from)
                    {
                        float d = (q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z);
                        if (d < bd) { bd = d; best = x; }
                    }
                }
                if (best is null && blg)
                {
                    // none alive: the last structure the walk saw, dead or not; with none at all, the null handle's entry
                    best = h.Structures.LastOrDefault();
                    if (best is null) Warn("FSM nearestEnemy could not find any buildings");
                    W(t, 1, ActorIndex(best));
                    break;
                }
                // nearestEnemy with none: the slot past the table, its handle null, not appended
                if (best is null) { W(t, 1, Actors.Count); break; }
                W(t, 1, ActorIndex(best));
                break;
            }

            // co-op: an order to the group goes to one car (the match, else the lead); an order at the group, at the
            // living human nearest the ordered car
            case "attack": { var o = Act(t, 0); if (o is not null) h.Order(o, Aim(t, 1, o), AiOrder.Attack, null, 0); break; }
            case "sit": { var o = Act(t, 0); if (o is not null) h.Order(o, null, AiOrder.Sit, null, 0); break; }
            case "hide": { var o = Act(t, 0); if (o is not null) h.Order(o, null, AiOrder.Hide, null, 0); break; }
            case "guard": { var o = Act(t, 0); if (o is not null) h.Order(o, Aim(t, 1, o), AiOrder.Guard, null, 0); break; }
            case "goto": { var o = Act(t, 0); if (o is not null) h.Order(o, null, AiOrder.Goto, N(t, 1), V(t, 2)); break; }
            case "evade": { var o = Act(t, 0); if (o is not null) h.Order(o, Aim(t, 3, o), AiOrder.Evade, N(t, 1), V(t, 2)); break; }
            case "race": { var o = Act(t, 0); if (o is not null) h.Order(o, Aim(t, 3, o), AiOrder.Race, N(t, 1), V(t, 2)); break; }
            case "follow":
            {
                var o = Act(t, 0);
                if (o is not null) h.Order(o, Aim(t, 1, o), AiOrder.Follow, null, 0, V(t, 3), V(t, 4), V(t, 5), Aim(t, 2, o));
                break;
            }
            // settings on the group: every human's
            case "setskill": foreach (var o in All(t, 0)) h.SetSkill(o, V(t, 1), V(t, 2)); break;
            case "setagg": foreach (var o in All(t, 0)) h.SetAggression(o, V(t, 1)); break;
            case "toggleavoid": foreach (var o in All(t, 0)) h.SetAvoid(o, null); break;
            case "setavoid": foreach (var o in All(t, 0)) h.SetAvoid(o, V(t, 1)); break;
            case "setmaxattackers": foreach (var o in All(t, 0)) h.SetMaxAttackers(o, V(t, 1)); break;

            // co-op: a per-human leave-map copy's warnings are for its own human's screen, not everyone's radio
            case "cb": { if (S(t, 0) is { } s) { if (t.LeaveMap && t.Bound >= 0) h.LeaveWarning(t.Bound, s, 3); else h.CbSay(s, null, 3); } break; }
            case "cbprior": { if (S(t, 0) is { } s) { if (t.LeaveMap && t.Bound >= 0) h.LeaveWarning(t.Bound, s, V(t, 1)); else h.CbSay(s, null, V(t, 1)); } break; }
            case "cbfrom": { if (S(t, 0) is { } s) { if (t.LeaveMap && t.Bound >= 0) h.LeaveWarning(t.Bound, s, 3); else h.CbSay(s, Act(t, 1), 3); } break; }
            case "cbfromprior": { if (S(t, 0) is { } s) { if (t.LeaveMap && t.Bound >= 0) h.LeaveWarning(t.Bound, s, V(t, 2)); else if (t.Scene >= 0) h.LeaveWarning(t.Scene, s, V(t, 2)); else h.CbSay(s, Act(t, 1), V(t, 2)); } break; }
            case "killcb": h.CbKill(); break;
            case "stopcb": h.CbStop(); break;

            case "success": h.ObjectiveSuccess(V(t, 0)); break;
            case "fail": h.ObjectiveFail(V(t, 0)); break;
            case "reveal": h.ObjectiveReveal(V(t, 0)); break;
            case "successall": h.EndMission(true, V(t, 0), -2); break;
            case "failall": h.EndMission(false, V(t, 0), -2); break;
            case "failallobj":
                // co-op: a human who drove off the map comes back beside a teammate; the mission fails only with nobody to come back to
                if (t.LeaveMap && t.Bound >= 0 && V(t, 1) == 2 && h.Abandon(t.Bound, InBounds)) { _restart = true; break; }
                h.EndMission(false, V(t, 0), V(t, 1) - 1);
                break;

            case "teleport": case "teleportoffset":
            {
                var path = N(t, 1);
                if (O(t, 0) is null || path is null || path.Count == 0) break;
                int angle = V(t, 3);
                if (angle < 0 || angle > 360) h.Log($"teleport angle out of bounds {angle}");
                // the original writes the terrain height under node 0 into the path itself, and the car takes that height
                // (an offset moves it across, not up or down)
                if (h.GroundHeight(path[0].X, path[0].Z) is { } y) Ground(V(t, 1), y);
                var at = path[0];
                if (name.Equals("teleportOffset", StringComparison.OrdinalIgnoreCase)) at += new Vector3(V(t, 4) / 100f, 0, V(t, 5) / 100f);
                // co-op: the lead, and the others into formation behind it (a teleport of the player is a change of
                // scene: T05's after its movie, the intros, the rubber bands once nobody is near)
                if (Group(t, 0) && t.Bound < 0)
                {
                    int lead = h.LeadSeat;
                    if (h.Human(lead) is { } l) { h.Teleport(l, at, angle, V(t, 2)); h.FormUp(lead); }
                    break;
                }
                if (Act(t, 0) is { } o) h.Teleport(o, at, angle, V(t, 2));
                break;
            }
            case "destroy":
            {
                // co-op: the matched (or bound) human; with neither, every one
                if (Group(t, 0) && t.Bound < 0 && t.Match is null) { foreach (var o in Members(t).ToList()) h.Destroy(o); break; }
                if (Act(t, 0) is { } d) h.Destroy(d);
                break;
            }
            case "startcar": foreach (var o in All(t, 0)) h.StartCar(o); break;
            case "setlights": foreach (var o in All(t, 0)) h.SetLights(o, V(t, 1) != 0); break;
            case "setsiren": foreach (var o in All(t, 0)) h.SetSiren(o, V(t, 1) != 0); break;
            case "triggergate": { var o = O(t, 0); if (o is not null) h.TriggerGate(o); break; }
            case "control": { var o = Act(t, 0); if (o is not null) h.Control(o, V(t, 1), V(t, 2), null, null); break; }
            case "controlall": { var o = Act(t, 0); if (o is not null) h.Control(o, V(t, 1), V(t, 2), V(t, 3), V(t, 4)); break; }
            case "drivecontrol": { var o = Act(t, 0); if (o is not null) h.DriveControl(o); break; }
            case "playmovie": { if (S(t, 0) is { } s) h.PlayMovie(s); break; }

            case "pushcam": case "popcam": case "camobjobj": case "camobjdir": case "camposobj": case "camposdir":
            case "camtransobj": case "camf12": case "camtransdir":
            {
                bool push = name.Equals("pushCam", StringComparison.OrdinalIgnoreCase), pop = name.Equals("popCam", StringComparison.OrdinalIgnoreCase);
                // co-op: a scene the player's own (CoopCameras) is the camera of the human who triggered it alone
                if (push && t.Scene < 0 && _coop && _personalCams.Contains(t.Pc))
                {
                    int seat = t.Bound >= 0 ? t.Bound : t.Match is { } mm ? _host.HumanSeat(mm) : t.Prefer >= 0 ? t.Prefer : _host.LeadSeat;
                    if (seat >= 0 && _host.Human(seat) is not null) { t.Scene = seat; t.SceneDepth = 0; }
                }
                var ints = Enumerable.Range(0, t.Args.Count).Select(i => V(t, i)).ToArray();
                var objs = Enumerable.Range(0, t.Args.Count).Select(i => (object?)null).ToArray();
                IReadOnlyList<Vector3>? path = null;
                // co-op: a camera on the group watches the matched human, else the lead
                switch (name.ToLowerInvariant())
                {
                    case "camobjobj": objs[0] = Act(t, 0); if (objs.Length > 4) objs[4] = Act(t, 4); break;
                    case "camobjdir": if (objs.Length > 0) objs[0] = Act(t, 0); break;
                    case "camposobj": path = N(t, 0); if (objs.Length > 2) objs[2] = Act(t, 2); break;
                    case "camposdir": path = N(t, 0); break;
                    case "camtransobj": path = N(t, 0); if (objs.Length > 3) objs[3] = Act(t, 3); break;
                    case "camtransdir": path = N(t, 0); break;
                    case "camf12": if (objs.Length > 1) { objs[0] = Act(t, 0); objs[1] = Act(t, 1); } break;
                }
                if (t.Scene >= 0)
                {
                    if (push) t.SceneDepth++;
                    h.PersonalCamera(t.Scene, name, ints, objs, path);
                    if (pop && --t.SceneDepth <= 0) t.Scene = -1;
                }
                else h.Camera(name, ints, objs, path);
                break;
            }
            case "setheliheight": foreach (var o in All(t, 0)) h.SetHeliHeight(o, V(t, 1)); break;
            case "setartillery": foreach (var o in All(t, 0)) h.SetArtillery(o, V(t, 1)); break;
            case "setartilleryrate": foreach (var o in All(t, 0)) h.SetArtilleryRate(o, V(t, 1)); break;
            case "salvage": { if (S(t, 0) is { } s) h.Salvage(s); break; }
            case "setuserradar":
                Warn($"FSM: {name} not yet handled here");
                break;
            default:
                h.Log($"FSM: unknown prototype {name}");
                break;
        }
    }
}
