using System.Numerics;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Fsm;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// A recording host with a human group (docs/multiplayer.md "Co-op"): the player's instance id (8 zero bytes) is
/// seat 0's object, the other seats are objects of their own; positions and deaths are the test's.
/// </summary>
public sealed class CoopFakeHost : RecordingHost, IFsmHost
{
    public readonly List<Obj> Seats = [];
    public readonly HashSet<Obj> Dead = [];
    public readonly Dictionary<object, Vector3> At = [];
    public readonly Dictionary<object, int> Teams = [];
    /// <summary>The attacks a friend took, in order: (victim, attacker); isAttacked takes one, whoAttacked reads it.</summary>
    public readonly Queue<(object Victim, object Attacker)> Attacks = [];
    private object? _lastAttacker;

    public CoopFakeHost(int seats)
    {
        for (int k = 0; k < seats; k++) Seats.Add(k == 0 ? (Obj)base.FindByInstanceId(new byte[8])! : new Obj($"s{k}"));
        Seats[0].Name = "s0";
    }

    public new object? FindByInstanceId(byte[] id) => base.FindByInstanceId(id);
    public new bool IsDead(object o) => o is Obj x && Dead.Contains(x);
    public new Vector3 Position(object o) => At.GetValueOrDefault(o);
    public new int Team(object o) => Teams.GetValueOrDefault(o, 1);
    public new IEnumerable<object> Vehicles => Objects.Values.Concat(Seats.Skip(1));
    public new void Order(object actor, object? target, AiOrder order, IReadOnlyList<Vector3>? path, float speed, int followFlag = 0, float f1 = 0, float f2 = 0, object? reference = null)
        => Calls.Add($"order {actor} {order} {target}");
    public new bool ConsumeAttacked(object o)
    {
        if (Attacks.TryPeek(out var a) && a.Victim == o) { _lastAttacker = Attacks.Dequeue().Attacker; return true; }
        return false;
    }
    public new object? LastAttacker(object o, int kind) => _lastAttacker;

    public int CoopSeats => Seats.Count;
    public bool IsPlayerHandle(object o) => o == Seats[0];
    public object? Human(int seat) => seat >= 0 && seat < Seats.Count ? Seats[seat] : null;
    public bool HumanAlive(int seat) => seat >= 0 && seat < Seats.Count && !Dead.Contains(Seats[seat]);
    public int HumanSeat(object o) => o is Obj x ? Seats.IndexOf(x) : -1;
    public int LeadSeat => Enumerable.Range(0, Seats.Count).FirstOrDefault(HumanAlive);
    public object? NearestHuman(object commanded) =>
        Seats.Where(s => !Dead.Contains(s)).OrderBy(s => Vector3.DistanceSquared(Position(s), Position(commanded))).FirstOrDefault();
    public void FormUp(int leadSeat) => Calls.Add($"formup {leadSeat}");
    public bool Abandon(int seat, Func<int, bool> inBounds)
    {
        bool back = Enumerable.Range(0, Seats.Count).Any(k => k != seat && HumanAlive(k) && inBounds(k));
        Calls.Add($"abandon {seat} {back}");
        return back;
    }
    public void LeaveWarning(int seat, string sound, int priority) => Calls.Add($"warn {seat} {sound}");

    /// <summary>The gates standing open: their line's point and slide.</summary>
    public readonly Dictionary<object, (Vector3 At, Vector3 Slide)> OpenGates = [];
    public new void TriggerGate(object o) { Calls.Add($"gate {o}"); OpenGates.Remove(o); }
    public (Vector3 At, Vector3 Slide)? OpenGate(object o) => OpenGates.TryGetValue(o, out var g) ? g : null;
    public void BringThrough(int seat, int beside)
    {
        Calls.Add($"through {seat} beside {beside}");
        At[Seats[seat]] = At.GetValueOrDefault(Seats[beside]) + new Vector3(0, 0, 5);
    }
}

/// <summary>A small FSM assembler for the tests: statements as the mission compiler lays them out.</summary>
public sealed class FsmAsm
{
    public readonly List<string> Protos = [];
    public readonly List<(uint, uint)> Code = [];
    public int Locals;
    public int Pc => Code.Count;
    /// <summary>A thread parameter, fp − n.</summary>
    public readonly record struct P(int N);
    /// <summary>A local's address.</summary>
    public readonly record struct L(int N);

    private int Proto(string n) { int i = Protos.IndexOf(n); if (i < 0) { Protos.Add(n); i = Protos.Count - 1; } return i; }
    public void Alloc(int n) { Code.Add((6, (uint)n)); Locals += n; }
    /// <summary>exec name(args): an int is an immediate (pushImm into a temporary), a <see cref="P"/> a parameter, an <see cref="L"/> a local.</summary>
    public void Exec(string name, params object[] args)
    {
        int temps = 0;
        foreach (var a in args)
            switch (a)
            {
                case P p: Code.Add((5, unchecked((uint)-p.N))); break;
                case L l: Code.Add((4, (uint)l.N)); break;
                case int v: Code.Add((1, unchecked((uint)v))); Code.Add((4, (uint)(Locals + 1 + temps))); temps++; break;
            }
        Code.Add((13, (uint)Proto(name)));
        if (temps > 0) Code.Add((7, (uint)temps));
    }
    /// <summary>branchIfFalse, jump, yieldJump: the index to patch with <see cref="Patch"/>.</summary>
    public int Bf() { Code.Add((9, 0)); return Pc - 1; }
    public int Jump() { Code.Add((8, 0)); return Pc - 1; }
    public void Yield(int to) => Code.Add((10, (uint)to));
    public void Ret(int n) => Code.Add((12, (uint)n));
    public void Not() => Code.Add((14, 0));
    public void Patch(int at, int to) => Code[at] = (Code[at].Item1, (uint)to);

    /// <param name="actors">Labels; actor 0 is the player (instance id of zeros), the others ids of their own.</param>
    public FsmProgram Build(string[] actors, List<NavPath> paths, int[] data, params (int Start, int[] Stack)[] threads) =>
        new(Protos, actors.Select((a, i) => (a, i == 0 ? new byte[8] : BitConverter.GetBytes((long)i + 1))).ToList(), ["s0.wav", "s1.wav", "s2.wav"], paths,
            threads.Select(t => (t.Start, t.Stack.Length, t.Stack)).ToList(), data, [.. Code], true);
}

/// <summary>The script's player as the human group, in the VM: what each use of the player actor means with 2–4 humans.</summary>
public class CoopVmTests
{
    private static NavPath Zone(float x, float z) => new("zone", [new Vector3(x, 0, z)]);

    /// <summary>isWithinNav(zone, player, 10) → destroy(player): any human in the zone trips it, and the destroy goes to that one.</summary>
    [Fact]
    public void AConditionOnThePlayerIsAnyHumansAndTheActionFollowsTheMatch()
    {
        var a = new FsmAsm();
        int top = a.Pc;
        a.Exec("isWithinNav", new FsmAsm.P(2), new FsmAsm.P(3), 10);
        int bf = a.Bf();
        a.Exec("destroy", new FsmAsm.P(3));
        a.Ret(2);
        a.Patch(bf, a.Pc);
        a.Yield(top);
        // the thread's parameters: data 0 (the player, actor 0), data 1 (the zone, path 0): fp−3 and fp−2
        var host = new CoopFakeHost(3);
        var vm = new FsmVm(a.Build(["user"], [Zone(0, 0)], [0, 0], (0, [0, 1])), host, new CrtRand());
        host.At[host.Seats[0]] = new(100, 0, 0); host.At[host.Seats[1]] = new(500, 0, 0); host.At[host.Seats[2]] = new(500, 0, 0);
        vm.Tick();
        Assert.Empty(host.Calls);
        host.At[host.Seats[2]] = new(3, 0, 4);
        vm.Tick();
        Assert.Equal(["destroy s2"], host.Calls);
    }

    /// <summary>
    /// isWithinNav(zone, player, 10) → triggerGate(gate): a gate across x = 0 (sliding along z), the zone past it at x = −60.
    /// The program as S06's nowayback has it.
    /// </summary>
    private static (FsmVm Vm, CoopFakeHost Host, object Gate) GateProgram(int seats, bool open = true)
    {
        var a = new FsmAsm();
        int top = a.Pc;
        a.Exec("isWithinNav", new FsmAsm.P(3), new FsmAsm.P(2), 10);
        int bf = a.Bf();
        a.Exec("triggerGate", new FsmAsm.P(4));
        a.Ret(3);
        a.Patch(bf, a.Pc);
        a.Exec("null");
        a.Yield(top);
        var host = new CoopFakeHost(seats);
        // the thread's parameters: data 1 (the gate, actor 1) fp−4, data 2 (the zone, path 0) fp−3, data 0 (the player) fp−2
        var vm = new FsmVm(a.Build(["user", "gate"], [Zone(-60, 0)], [0, 1, 0], (0, [1, 2, 0])), host, new CrtRand());
        var gate = vm.Actors[1].Handle!;
        if (open) host.OpenGates[gate] = (new Vector3(0, 0, 0), new Vector3(0, 0, -10));
        return (vm, host, gate);
    }

    private static void Run(FsmVm vm, CoopFakeHost host, float seconds)
    {
        for (float end = host.Now + seconds; host.Now < end; host.Now += 0.05f) vm.Tick();
    }

    /// <summary>The first human in the zone does not shut the gate on the second, still on the near side; it shuts once they are through.</summary>
    [Fact]
    public void AGateTheScriptShutsBehindThePlayerWaitsForTheHumansBehindIt()
    {
        var (vm, host, gate) = GateProgram(2);
        Assert.Single(vm.GateSites);
        host.At[host.Seats[0]] = new(-60, 0, 2);     // in the zone
        host.At[host.Seats[1]] = new(80, 0, 0);      // still on the approach
        Run(vm, host, 10);
        Assert.Empty(host.Calls);
        host.At[host.Seats[0]] = new(-400, 0, 0);    // drove on: the hold stands
        host.At[host.Seats[1]] = new(1, 0, 0);       // in the gateway: not through yet
        Run(vm, host, 1);
        Assert.Empty(host.Calls);
        host.At[host.Seats[1]] = new(-20, 0, 0);     // through, short of the zone
        vm.Tick();
        Assert.Equal([$"gate {gate}"], host.Calls);
    }

    /// <summary>A human who never comes is brought through beside the teammate furthest past the gate after the hold, and the gate shuts.</summary>
    [Fact]
    public void AStragglerIsBroughtThroughWhenTheHoldRunsOut()
    {
        var (vm, host, gate) = GateProgram(3);
        host.At[host.Seats[0]] = new(-60, 0, 2);
        host.At[host.Seats[1]] = new(-30, 0, 0);     // through
        host.At[host.Seats[2]] = new(2000, 0, 0);    // far behind
        Run(vm, host, FsmVm.GateHoldSeconds - 1);
        Assert.Empty(host.Calls);
        Run(vm, host, 2);
        Assert.Equal(2, host.Calls.Count);
        Assert.Equal("through 2 beside 0", host.Calls[0]);
        Assert.StartsWith("gate ", host.Calls[1]);
        Assert.True(host.At[host.Seats[2]].X < -FsmVm.GateClear);
    }

    /// <summary>
    /// Nobody behind: it shuts at once, as single player's. A dead human is nobody to wait for. A gate that is shut (or the
    /// test before it opened) is the script's as before; one human alone is single player's.
    /// </summary>
    [Fact]
    public void AGateShutsAtOnceWithNobodyBehindIt()
    {
        var (vm, host, _) = GateProgram(2);
        host.At[host.Seats[0]] = new(-60, 0, 2);
        host.At[host.Seats[1]] = new(80, 0, 0);
        host.Dead.Add(host.Seats[1]);
        vm.Tick();
        Assert.Single(host.Calls, c => c.StartsWith("gate "));

        (vm, host, _) = GateProgram(2, open: false);
        host.At[host.Seats[0]] = new(-60, 0, 2);
        host.At[host.Seats[1]] = new(80, 0, 0);
        vm.Tick();
        Assert.Single(host.Calls, c => c.StartsWith("gate "));

        (vm, host, _) = GateProgram(1);
        host.At[host.Seats[0]] = new(-60, 0, 2);
        vm.Tick();
        Assert.Single(host.Calls, c => c.StartsWith("gate "));
    }

    /// <summary>isDead(player) → success: true only once every human is dead.</summary>
    [Fact]
    public void ThePlayerIsDeadWhenEveryHumanIs()
    {
        var a = new FsmAsm();
        int top = a.Pc;
        a.Exec("isDead", new FsmAsm.P(2));
        int bf = a.Bf();
        a.Exec("success", 1);
        a.Ret(1);
        a.Patch(bf, a.Pc);
        a.Yield(top);
        var host = new CoopFakeHost(2);
        var vm = new FsmVm(a.Build(["user"], [], [0], (0, [0])), host, new CrtRand());
        host.Dead.Add(host.Seats[0]);
        vm.Tick(); vm.Tick();
        Assert.Empty(host.Calls);
        host.Dead.Add(host.Seats[1]);
        vm.Tick();
        Assert.Equal(["success 1"], host.Calls);
    }

    /// <summary>attack(enemy, player) aims at the human nearest the enemy; goto(player) is the lead's; a teleport moves the lead and forms up.</summary>
    [Fact]
    public void OrdersAtThePlayerAimAtTheNearestHumanAndTheLeadTakesTheCutScene()
    {
        var a = new FsmAsm();
        int top = a.Pc;
        a.Exec("attack", new FsmAsm.P(4), new FsmAsm.P(3), 1);
        a.Exec("goto", new FsmAsm.P(3), new FsmAsm.P(2), 20);
        a.Exec("teleport", new FsmAsm.P(3), new FsmAsm.P(2), 0, 90);
        a.Yield(top);
        var host = new CoopFakeHost(3);
        // data: 0 the player, 1 the enemy (actor 1), 2 path 0; parameters fp−4, fp−3, fp−2 = enemy, player, path
        var vm = new FsmVm(a.Build(["user", "enemy"], [Zone(0, 0)], [0, 1, 0], (0, [1, 0, 2])), host, new CrtRand());
        var enemy = vm.Actors[1].Handle!;
        host.At[enemy] = new(0, 0, 0);
        host.At[host.Seats[0]] = new(300, 0, 0); host.At[host.Seats[1]] = new(30, 0, 0); host.At[host.Seats[2]] = new(100, 0, 0);
        vm.Tick();
        Assert.Equal([$"order {enemy} Attack s1", "order s0 Goto ", "teleport s0", "formup 0"], host.Calls);
        // the lead is the first living human
        host.Calls.Clear();
        host.Dead.Add(host.Seats[0]);
        vm.Tick();
        Assert.Equal([$"order {enemy} Attack s1", "order s1 Goto ", "teleport s1", "formup 1"], host.Calls);
    }

    /// <summary>
    /// The friendly-fire routine's counter (whoAttacked of a friend, isEqualId with the player, inc) counts per human:
    /// the limit (more than 1) trips when one human has hit the friend twice, not when two have once each.
    /// </summary>
    [Theory]
    [InlineData(3, 3)]
    [InlineData(0, 2)]
    public void FriendlyFireCountsPerHuman(int seats, int failsAtHit)
    {
        var a = new FsmAsm();
        // thread 0, (friend, player, counter) at fp−4, fp−3, fp−2, a local for the attacker
        a.Alloc(1);
        int loop = a.Pc;
        a.Exec("isAttacked", new FsmAsm.P(4));
        int bf1 = a.Bf();
        a.Exec("whoAttacked", new FsmAsm.P(4), new FsmAsm.L(1));
        a.Exec("isEqualId", new FsmAsm.L(1), new FsmAsm.P(3));
        int bf2 = a.Bf();
        a.Exec("inc", new FsmAsm.P(2));
        a.Patch(bf1, a.Pc); a.Patch(bf2, a.Pc);
        a.Yield(loop);
        // thread 1: the limit, (counter) at fp−2
        int t1 = a.Pc;
        a.Locals = 0;
        a.Exec("isGreater", new FsmAsm.P(2), 1);
        int bf3 = a.Bf();
        a.Exec("failAll", 0);
        a.Ret(1);
        a.Patch(bf3, a.Pc);
        a.Yield(t1);
        // data: 0 the player, 1 the friend (actor 1), 2 the counter
        var prog = a.Build(["user", "friend"], [], [0, 1, 0], (0, [1, 0, 2]), (t1, [2]));
        IFsmHost host;
        List<object> shooters;
        CoopFakeHost? coop = null;
        RecordingHost single;
        if (seats > 0) { coop = new CoopFakeHost(seats); host = single = coop; shooters = [coop.Seats[1], coop.Seats[2], coop.Seats[1]]; }
        else { single = new SingleWithAttacks(); host = single; shooters = []; }
        var vm = new FsmVm(prog, host, new CrtRand());
        var friend = vm.Actors[1].Handle!;
        if (seats > 0)
        {
            Assert.Equal(CoopRoutines.Counter.FriendlyFire, Assert.Single(vm.Routines.Counters).Value);
        }
        else
        {
            var player = vm.Actors[0].Handle!;
            shooters = [player, player, player];
            ((SingleWithAttacks)single).Friend = friend;
        }
        for (int hit = 1; hit <= 3; hit++)
        {
            if (coop is not null) coop.Attacks.Enqueue((friend, shooters[hit - 1]));
            else ((SingleWithAttacks)single).Pending++;
            vm.Tick();
            Assert.Equal(hit >= failsAtHit, single.Ended);
        }
    }

    /// <summary>The single player's host for the friendly-fire routine: the player attacks the friend.</summary>
    private sealed class SingleWithAttacks : RecordingHost, IFsmHost
    {
        public object? Friend;
        public int Pending;
        public new bool ConsumeAttacked(object o) { if (o == Friend && Pending > 0) { Pending--; return true; } return false; }
        public new object? LastAttacker(object o, int kind) => FindByInstanceId(new byte[8]);
    }

    /// <summary>
    /// The leave-map routine runs once per human: the warnings are the straying human's, the fail radius brings them
    /// back beside a teammate who is away from the exits, and with nobody to come back to the mission fails.
    /// </summary>
    [Fact]
    public void TheLeaveMapRoutineRunsPerHuman()
    {
        var a = new FsmAsm();
        // (player, exit) at fp−3, fp−2: 275 → cb(0), 175 → cb(1), 50 → failAllObj(6, 2)
        int l0 = a.Pc;
        a.Exec("isWithinSqNav", new FsmAsm.P(2), new FsmAsm.P(3), 275);
        int b0 = a.Bf();
        a.Exec("cb", 0);
        int j0 = a.Jump();
        a.Patch(b0, a.Pc); a.Yield(l0);
        int l1 = a.Pc; a.Patch(j0, l1);
        a.Exec("isWithinSqNav", new FsmAsm.P(2), new FsmAsm.P(3), 175);
        int b1 = a.Bf();
        a.Exec("cb", 1);
        int j1 = a.Jump();
        a.Patch(b1, a.Pc); a.Yield(l1);
        int l2 = a.Pc; a.Patch(j1, l2);
        a.Exec("isWithinSqNav", new FsmAsm.P(2), new FsmAsm.P(3), 50);
        int b2 = a.Bf();
        a.Exec("failAllObj", 6, 2);
        a.Ret(2);
        a.Patch(b2, a.Pc); a.Yield(l2);
        var host = new CoopFakeHost(3);
        var vm = new FsmVm(a.Build(["user"], [Zone(0, 0)], [0, 0], (0, [0, 1])), host, new CrtRand());
        Assert.Equal([0], vm.Routines.LeaveMapThreads);
        Assert.Equal(3, vm.Threads.Count);
        foreach (var s in host.Seats) host.At[s] = new(1000, 0, 1000);
        vm.Tick();
        Assert.Empty(host.Calls);
        host.At[host.Seats[2]] = new(10, 0, 10);
        vm.Tick();
        Assert.Equal(["warn 2 s0.wav", "warn 2 s1.wav", "abandon 2 True"], host.Calls);
        Assert.False(host.Ended);
        Assert.False(vm.Threads[2].Finished);
        // nobody to come back to: the others are near the exits too
        host.Calls.Clear();
        foreach (var s in host.Seats) host.At[s] = new(10, 0, 10);
        vm.Tick();
        Assert.True(host.Ended);
        Assert.Contains("abandon 0 False", host.Calls);
    }
}

/// <summary>Co-op in the stock missions (docs/multiplayer.md "Co-op"), with the installs.</summary>
public class CoopMissionTests(ITestOutputHelper output)
{
    private static (SimSession S, MissionSetup Setup) Make(SnapshotRig.Loaded l, string mission, int humans, uint seed = 1) =>
        SnapshotRig.Make(l, SnapshotRig.BuildOf(l.Dir, mission, coop: CoopSetup.Of(humans)), SimRate.R20, seed);

    private static void Idle(SimSession s, int ticks)
    {
        for (int i = 0; i < ticks; i++) { s.Tick(new CarInput[s.Drivers.Count]); SnapshotRig.TakeEvents(s); }
    }

    private static Vector3 Path(MissionSetup setup, string name) =>
        setup.Script!.Vm!.Program.Paths.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Points[0];

    private static void Put(MissionSetup setup, Vehicle v, Vector3 at)
    {
        at.Y = setup.World.Ground.Query(at.X, 500, at.Z).Height;
        v.Pos = new Math.Double3(at.X, at.Y, at.Z);
        v.Vel = Vector3.Zero; v.Speed = 0;
    }

    /// <summary>The routines found in every stock mission of both games: the leave-map threads, the friendly-fire and civilian counters.</summary>
    [Fact]
    public void TheCoopRoutinesAreFoundByTheirBytecode()
    {
        var found = new Dictionary<string, (int Leave, string Counters)>();
        foreach (var dir in new[] { SnapshotRig.Install, SnapshotRig.NitroInstall })
        {
            if (dir is null) continue;
            var install = new GameInstall(dir);
            foreach (var m in SnapshotRig.Missions(dir))
            {
                if (install.FindMission(m) is not { } path || new MissionFile(path) is var mf && mf.ReadFsmProgram() is not { Code.Length: > 0 } p) continue;
                var objs = mf.PlacedObjects();
                PlacedObject? Of(int a) => a < 0 || a >= p.Actors.Count ? null
                    : objs.LastOrDefault(o => o.Raw.Length >= 8 && o.Raw.AsSpan(0, 8).SequenceEqual(p.Actors[a].InstanceId.AsSpan(0, System.Math.Min(8, p.Actors[a].InstanceId.Length))));
                var r = CoopRoutines.Analyze(p, a => Of(a)?.IsPlayer == true, a => Of(a) is { } o ? (int)((o.ObjectFlags >> 16) & 7) : null);
                found[m] = (r.LeaveMap.Count, string.Join(",", r.Counters.Select(kv => $"{(kv.Key.Thread < 0 ? "d" : "local")}{(kv.Key.Thread < 0 ? kv.Key.Index : "")}:{kv.Value}").Order()));
                output.WriteLine($"{m}: {r.LeaveMap.Count} leave-map threads, counters [{found[m].Counters}]");
            }
        }
        if (SnapshotRig.Install is not null)
        {
            // every mission with exits watches each one; the trainer and two cut-scene-led missions have none
            foreach (var m in new[] { "s01", "s03", "s04", "s05", "s07" }.Concat(Enumerable.Range(1, 17).Select(i => $"t{i:00}"))) Assert.True(found[m].Leave > 0, m);
            foreach (var m in new[] { "a01", "s02", "s06" }) Assert.Equal(0, found[m].Leave);
            Assert.Equal("d12:FriendlyFire", found["t01"].Counters);
            Assert.Equal("d62:FriendlyFire", found["t02"].Counters);
            Assert.Equal("d46:FriendlyFire", found["t03"].Counters);
            Assert.Equal("d74:FriendlyFire", found["t06"].Counters);
            Assert.Equal("d45:CivilianKill", found["s04"].Counters);
            Assert.All(found.Where(kv => kv.Key[0] is 'a' or 's' or 't' && kv.Key is not ("t01" or "t02" or "t03" or "t06" or "s04")), kv => Assert.Equal("", kv.Value.Counters));
        }
        if (SnapshotRig.NitroInstall is not null)
        {
            // Nitro keeps the friendly-fire count in the routine's own local
            var ff = new[] { "b01", "p01", "p03", "p04", "p06", "p07", "p08", "p19" };
            foreach (var m in found.Keys.Where(k => k[0] is 'p' or 'b'))
                Assert.Equal(ff.Contains(m), found[m].Counters.Contains("local:FriendlyFire"));
            foreach (var m in Enumerable.Range(1, 19).Select(i => $"p{i:00}")) Assert.True(found[m].Leave > 0, m);
        }
    }

    /// <summary>The other seats' cars stand beside and behind the player's, clear of every car, on the ground, each a human of the player's side.</summary>
    [Fact]
    public void TheSeatsStartInFormationBesideThePlayer()
    {
        foreach (var (dir, mission) in new[] { (SnapshotRig.Install, "t01"), (SnapshotRig.Install, "t05"), (SnapshotRig.Install, "s01"), (SnapshotRig.NitroInstall, "p01") })
        {
            if (dir is null) continue;
            var l = new SnapshotRig.Loaded(dir);
            var (s, setup) = Make(l, mission, 4);
            var host = setup.Script!.Host;
            Assert.Equal(4, setup.Humans.Count);
            Assert.Equal(3, s.World.Humans.Count);
            Assert.True(s.World.Coop);
            Assert.Equal(4, s.Drivers.Count);
            Assert.Equal(setup.World.Player, setup.Humans[0]);
            var lead = setup.Humans[0];
            foreach (var h in setup.Humans.Skip(1))
            {
                var e = host.Of(h)!;
                Assert.Equal(1, e.Team);
                Assert.True(host.IsHumanEntity(e));
                Assert.True(h.Health!.IsPlayer);
                float d = Vector3.Distance(h.Pos.ToVector3(), lead.Pos.ToVector3());
                Assert.InRange(d, 4f, 60f);
                foreach (var o in setup.World.Vehicles.Where(o => o != h && !o.IsEmplacement))
                    Assert.True(Vector3.Distance(o.Pos.ToVector3(), h.Pos.ToVector3()) > 3.5f, $"{mission}: seat car overlaps {o.Definition}");
            }
            Idle(s, 100);
            Assert.All(setup.Humans, h => Assert.False(Damage.DamageModel.IsDead(h)));
            output.WriteLine($"{mission}: seats at {string.Join("; ", setup.Humans.Select(h => $"{h.Pos.X:0},{h.Pos.Z:0}"))}");
        }
    }

    /// <summary>
    /// The gates the scripts shut behind the player (CoopGates): a position test on the player whose branch runs
    /// triggerGate first. S06's nowayback (twopill), T07's gate1 (gateblock), the Nitro trainer B01's four walls; no other
    /// stock mission has one (T10, T12, S07, P18 and P19 move their gates on flags, timers or a computer car).
    /// </summary>
    [Fact]
    public void TheGatesShutBehindThePlayerAreFoundByTheirBytecode()
    {
        var found = new Dictionary<string, int>();
        foreach (var dir in new[] { SnapshotRig.Install, SnapshotRig.NitroInstall })
        {
            if (dir is null) continue;
            var install = new GameInstall(dir);
            foreach (var m in SnapshotRig.Missions(dir))
            {
                if (install.FindMission(m) is not { } path || new MissionFile(path) is var mf && mf.ReadFsmProgram() is not { Code.Length: > 0 } p) continue;
                var objs = mf.PlacedObjects();
                bool IsPlayer(int a) => a >= 0 && a < p.Actors.Count && objs.LastOrDefault(o => o.Raw.Length >= 8
                    && o.Raw.AsSpan(0, 8).SequenceEqual(p.Actors[a].InstanceId.AsSpan(0, System.Math.Min(8, p.Actors[a].InstanceId.Length))))?.IsPlayer == true;
                var sites = CoopGates.Analyze(p, IsPlayer);
                if (sites.Count == 0) continue;
                found[m] = sites.Count;
                output.WriteLine($"{m}: {string.Join(", ", sites.Values.Select(x => $"{p.Prototypes[(int)p.Code[x.Test].Arg]} at {x.Test} → triggerGate at {x.GatePc}"))}");
            }
        }
        var expected = new Dictionary<string, int>();
        if (SnapshotRig.Install is not null) { expected["s06"] = 1; expected["t07"] = 1; }
        if (SnapshotRig.NitroInstall is not null) expected["b01"] = 4;
        Assert.Equal(expected.OrderBy(kv => kv.Key), found.OrderBy(kv => kv.Key));
    }

    /// <summary>T07's gate1 (afence5 across the road at x 1455) and its leaf's box, as GateTests has them.</summary>
    private static (StaticObject Gate1, GateMotion Gate, Func<Vector3> Arm) T07Gate(MissionSetup setup)
    {
        var gate1 = setup.Statics.Objects.Single(o => o.Name == "afence5" && o.Centre.X > 1000);
        int gi = gate1.Nodes.FindIndex(n => n.Gate is not null);
        return (gate1, gate1.Nodes[gi].Gate!, () => gate1.Boxes.Single(b => b.Node == gi).Centre);
    }

    private static void Place(MissionSetup setup, Vehicle v, float x, float z, Vector3 heading, float speed)
    {
        setup.World.Ai.Remove(v);   // nobody else at the wheel (the intro's goto)
        v.Pos = new Math.Double3(x, setup.Ground.BareHeight(x, z) + 0.5f, z);
        v.Rot = new Math.Rows(Vector3.Cross(Vector3.UnitY, heading), Vector3.UnitY, heading);
        v.Vel = heading * speed;
        v.AngVel = default;
    }

    private static void Drive(SimSession s, float seconds, int seat = -1, float throttle = 0, Func<bool>? until = null)
    {
        var inputs = new CarInput[s.Drivers.Count];
        if (seat >= 0) inputs[seat] = new CarInput { Buttons = CarButtons.Analog, Throttle = (short)(throttle * 32767) };
        for (float t = 0; t < seconds && until?.Invoke() != true; t += s.TickSeconds) { s.Tick(inputs); SnapshotRig.TakeEvents(s); }
    }

    /// <summary>
    /// T07 with two humans: A drives through gate1 and comes within 600 of gateblock, where single player's shuts the gate
    /// behind the player. B is still on the road behind it: the gate stands open until B has driven through, then shuts.
    /// </summary>
    [Fact]
    public void T07sRoadBlockWaitsForTheHumanBehindIt()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t07", 2);
        var (_, gate, arm) = T07Gate(setup);
        var across = arm();
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        Drive(s, 26);
        Assert.Equal(1f, gate.X);                    // opened at the start
        Place(setup, a, across.X + 25, across.Z, -Vector3.UnitX, 15);
        Place(setup, b, across.X + 400, across.Z, -Vector3.UnitX, 0);
        Drive(s, 4, 0, 0.6f);
        Assert.True(a.Pos.X < across.X - 10, $"A stopped at {a.Pos.X:0.0}");
        Place(setup, a, 1000, 47750, -Vector3.UnitX, 0);
        Drive(s, 20, until: () => gate.Moving);
        Assert.False(gate.Moving, "gate1 shut with B behind it");
        Assert.Equal(1f, gate.X);
        // B drives through where the arm stands when shut
        Place(setup, b, across.X + 25, across.Z, -Vector3.UnitX, 15);
        Drive(s, 4, 1, 0.6f, () => gate.Moving);
        Drive(s, 0.5f);
        Assert.True(b.Pos.X < across.X - FsmVm.GateClear, $"B stopped at {b.Pos.X:0.0}");
        Assert.True(gate.Moving || gate.X == 0, "gate1 did not shut once B was through");
        Drive(s, 25);
        Assert.Equal(0f, gate.X);
        Assert.True(b.Pos.X < across.X && a.Pos.X < across.X);
        Assert.Null(setup.Script!.Host.End);
    }

    /// <summary>S06's nowayback (opened at the start, shut once the player is within 10 of twopill, past it): held while B is on the near side, shut once B is through.</summary>
    [Fact]
    public void S06sNoWayBackWaitsForTheHumanBehindIt()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "s06", 2);
        var vm = setup.Script!.Vm!;
        var e = (Entity)vm.Actors.First(x => x.Label.Equals("nowayback", StringComparison.OrdinalIgnoreCase)).Handle!;
        var gate = e.Static!.Nodes.First(n => n.Gate is not null).Gate!;
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        var zone = Path(setup, "twopill");
        setup.Script!.Host.Logged += m => output.WriteLine($"{setup.Script!.Host.Now:0.0}: {m}");
        setup.Damage.UnlimitedArmour = setup.Damage.UnlimitedChassis = true;   // the race's opening fire is not the test's
        Drive(s, 25);
        Assert.Equal(1f, gate.X);
        Place(setup, a, zone.X, zone.Z, -Vector3.UnitX, 0);
        Place(setup, b, zone.X + 250, zone.Z, -Vector3.UnitX, 0);
        Drive(s, 10, until: () => gate.Moving);
        Assert.False(gate.Moving, "nowayback shut with B behind it");
        Place(setup, b, zone.X - 30, zone.Z, -Vector3.UnitX, 0);
        Drive(s, 1, until: () => gate.Moving);
        Assert.True(gate.Moving, "nowayback did not shut once B was through");
    }

    /// <summary>T07 with two humans, B left far behind at the start: after the hold B is brought through beside A and gate1 shuts.</summary>
    [Fact]
    public void T07sRoadBlockBringsAStragglerThroughWhenTheHoldRunsOut()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t07", 2);
        var (_, gate, arm) = T07Gate(setup);
        var across = arm();
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        var events = new List<CoopEvent>();
        setup.Script!.Host.Coop!.Happened += events.Add;
        Drive(s, 26);
        Place(setup, a, across.X + 25, across.Z, -Vector3.UnitX, 15);
        Place(setup, b, 2900, 48000, -Vector3.UnitX, 0);
        Drive(s, 4, 0, 0.6f);
        Place(setup, a, 1000, 47750, -Vector3.UnitX, 0);
        Drive(s, FsmVm.GateHoldSeconds - 2, until: () => gate.Moving);
        Assert.False(gate.Moving);
        Drive(s, 4, until: () => gate.Moving);
        Assert.True(gate.Moving, "gate1 did not shut after the hold");
        Assert.True(b.Pos.X < across.X - FsmVm.GateClear, $"B left at {b.Pos.X:0.0}");
        Assert.InRange(Vector3.Distance(a.Pos.ToVector3(), b.Pos.ToVector3()), 3f, 60f);
        Assert.Contains(events, e => e is CoopRespawned { Seat: 1, Why: CoopReturn.Gate, Beside: 0 });
    }

    /// <summary>T12's jump zone (isWithinNav(gotair, user, 15) → destroy(user)) takes the human who is in it, not the others.</summary>
    [Fact]
    public void T12sZoneDestroysTheHumanInIt()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t12", 2);
        Idle(s, 40);
        var seat1 = setup.Humans[1];
        Put(setup, seat1, Path(setup, "gotair"));
        Idle(s, 3);
        Assert.True(Damage.DamageModel.IsDead(seat1));
        Assert.False(Damage.DamageModel.IsDead(setup.Humans[0]));
        Assert.Null(setup.Script!.Host.End);
    }

    /// <summary>
    /// T05 fails on the player's death (isDead(user) → failAllObj): with two humans one death does not end it, the
    /// destroyed human comes back beside the other after the respawn delay; both dead at once fails it.
    /// </summary>
    [Fact]
    public void ADeadHumanRespawnsBesideATeammateAndTheMissionFailsWhenAllAreDead()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t05", 2);
        var host = setup.Script!.Host;
        var events = new List<CoopEvent>();
        host.Coop!.Happened += events.Add;
        Idle(s, 200);
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        setup.Damage.Destroy(b);
        Idle(s, s.Rate.TicksFor(4f));
        Assert.Null(host.End);
        Assert.True(Damage.DamageModel.IsDead(b));
        Assert.Equal(0, host.LeadSeat);
        Idle(s, s.Rate.TicksFor(1.5f));
        Assert.False(Damage.DamageModel.IsDead(b));
        Assert.Contains(new CoopRespawned(1, CoopReturn.Died, 0), events);
        Assert.InRange(Vector3.Distance(a.Pos.ToVector3(), b.Pos.ToVector3()), 4f, 60f);
        Assert.Null(host.End);
        // the lead dies: seat 1 leads; then both
        setup.Damage.Destroy(a);
        Idle(s, 2);
        Assert.Equal(1, host.LeadSeat);
        Assert.Null(host.End);
        setup.Damage.Destroy(b);
        Idle(s, 4);
        Assert.Equal(false, host.End?.Success);
    }

    /// <summary>
    /// T05's leave-map routine per human: a human on an exit point is warned (an event for them alone, nothing on the
    /// radio) and comes back beside the other; both on exits fails the mission, "abandoned".
    /// </summary>
    [Fact]
    public void LeavingTheMapBringsAHumanBackAndFailsOnlyWhenAllLeave()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t05", 2);
        var host = setup.Script!.Host;
        var events = new List<CoopEvent>();
        host.Coop!.Happened += events.Add;
        Assert.Equal(14, setup.Script.Vm!.Threads.Count(t => t.LeaveMap));
        Idle(s, 200);
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        var exit = Path(setup, "exit1");
        Put(setup, b, exit);
        Idle(s, 1);
        Assert.Equal(2, events.OfType<CoopLeaveWarning>().Count(e => e.Seat == 1));
        Assert.DoesNotContain(events.OfType<CoopLeaveWarning>(), e => e.Seat == 0);
        Assert.Contains(new CoopRespawned(1, CoopReturn.LeftMap, 0), events);
        Assert.InRange(Vector3.Distance(a.Pos.ToVector3(), b.Pos.ToVector3()), 4f, 60f);
        Assert.Null(host.End);
        Idle(s, 20);
        Assert.Null(host.End);
        Put(setup, a, exit);
        Put(setup, b, Path(setup, "exit2"));
        Idle(s, 1);
        Assert.Equal((false, 1), (host.End?.Success, host.End?.FailMessage));
    }

    /// <summary>A player who leaves is out of the group (their car a wreck, no respawn); joining again is a fresh car beside the lead.</summary>
    [Fact]
    public void ASeatLeavesAndJoinsAgain()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t05", 3);
        var host = setup.Script!.Host;
        Idle(s, 200);
        host.CoopLeave(2);
        Assert.True(Damage.DamageModel.IsDead(setup.Humans[2]));
        Idle(s, s.Rate.TicksFor(8f));
        Assert.True(Damage.DamageModel.IsDead(setup.Humans[2]));
        Assert.Equal([host.Of(setup.Humans[0]), host.Of(setup.Humans[1])], host.Players);
        Assert.True(host.CoopJoin(2));
        Assert.False(Damage.DamageModel.IsDead(setup.Humans[2]));
        Assert.InRange(Vector3.Distance(setup.Humans[0].Pos.ToVector3(), setup.Humans[2].Pos.ToVector3()), 4f, 60f);
        Assert.Equal(3, host.Players.Count());
        // the others dead with seats 1 and 2 gone: the group is dead
        host.CoopLeave(2);
        host.CoopLeave(1);
        setup.Damage.Destroy(setup.Humans[0]);
        Idle(s, 3);
        Assert.Equal(false, host.End?.Success);
        Assert.False(host.CoopJoin(2));   // nobody to join beside
    }

    /// <summary>An order at "the player" aims at the nearest living human and keeps its aim until another is clearly nearer.</summary>
    [Fact]
    public void AnOrderAtThePlayerKeepsItsAimWithHysteresis()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (_, setup) = Make(new SnapshotRig.Loaded(dir), "t01", 2);
        var host = setup.Script!.Host;
        var enemy = host.Entities.First(e => e.Vehicle is { IsEmplacement: false } && e.Team == 2);
        var at = enemy.Position;
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        Put(setup, a, at + new Vector3(100, 0, 0));
        Put(setup, b, at + new Vector3(0, 0, 90));
        Assert.Same(host.Of(b), host.NearestHuman(enemy));
        Put(setup, a, at + new Vector3(80, 0, 0));     // nearer, but not by a quarter
        Assert.Same(host.Of(b), host.NearestHuman(enemy));
        Put(setup, a, at + new Vector3(60, 0, 0));
        Assert.Same(host.Of(a), host.NearestHuman(enemy));
        setup.Damage.Destroy(a);
        Assert.Same(host.Of(b), host.NearestHuman(enemy));
    }

    /// <summary>T11's gun emplacements hold the nearest human their radar may see, with hysteresis, and fire at them.</summary>
    [Fact]
    public void EmplacementsAimAtTheNearestHuman()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var (s, setup) = Make(new SnapshotRig.Loaded(dir), "t11", 2);
        var gun = setup.World.Vehicles.First(v => v.IsEmplacement);
        var (a, b) = (setup.Humans[0], setup.Humans[1]);
        foreach (var h in setup.Humans) h.Flags |= VehicleFlags.EngineOn | VehicleFlags.Started;
        var at = gun.Pos.ToVector3();
        Put(setup, a, at + new Vector3(400, 0, 0));
        Put(setup, b, at + new Vector3(0, 0, 60));
        Emplacements.Frame(setup.World, setup.Combat, false);
        Assert.Same(b, setup.Combat.RadarTarget[gun]);
        Put(setup, a, at + new Vector3(55, 0, 0));
        Emplacements.Frame(setup.World, setup.Combat, false);
        Assert.Same(b, setup.Combat.RadarTarget[gun]);
        Put(setup, a, at + new Vector3(30, 0, 0));
        Emplacements.Frame(setup.World, setup.Combat, false);
        Assert.Same(a, setup.Combat.RadarTarget[gun]);
        // an engine off hides that human from the radar
        a.Flags &= ~VehicleFlags.EngineOn;
        Emplacements.Frame(setup.World, setup.Combat, false);
        Assert.Same(b, setup.Combat.RadarTarget[gun]);
        // and the guns fire on the human
        a.Flags |= VehicleFlags.EngineOn;
        var shots = 0;
        for (int i = 0; i < 400; i++)
        {
            foreach (var h in setup.Humans) { h.Vel = Vector3.Zero; }
            s.Tick(new CarInput[s.Drivers.Count]);
            shots += setup.Combat.Events.OfType<Weapons.ShotFired>().Count(e => e.Shooter.IsEmplacement);
            SnapshotRig.TakeEvents(s);
        }
        output.WriteLine($"emplacement shots {shots}");
        Assert.True(shots > 0);
    }
}

/// <summary>The cut-scenes drive the lead, and the others form up behind it when a teleport or the end of the scene moves it.</summary>
public class CoopCutSceneTests(ITestOutputHelper output)
{
    /// <summary>
    /// T05 and T06 with two humans, seat 1 (not the lead) sent to the mid-mission trigger (T05's taurus, T06's fillup):
    /// the intro drives the lead and the other forms up behind it when it ends; the trigger's movie and the player's
    /// teleport after it move the lead there, the other in formation behind. (T05's trigger waits on two of its
    /// progress flags, data 60 and 66, which the earlier objectives set: they are set here.)
    /// </summary>
    [Theory]
    [InlineData("t05", "taurus")]
    [InlineData("t06", "fillup")]
    public void TheLeadTakesTheCutScenesAndTheOthersFormUp(string mission, string trigger)
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, mission, coop: CoopSetup.Of(2)), SimRate.R20, 1);
        var host = setup.Script!.Host;
        var vm = setup.Script.Vm!;
        var (lead, other) = (setup.Humans[0], setup.Humans[1]);
        var target = vm.Actors.FirstOrDefault(a => a.Label.Equals(trigger, StringComparison.OrdinalIgnoreCase)).Handle as Entity;
        var at = target?.Position ?? vm.Program.Paths.First(p => p.Name.Equals(trigger, StringComparison.OrdinalIgnoreCase)).Points[0];
        string? movie = null;
        host.Movie += m => movie = m;
        var start = lead.Pos.ToVector3();
        bool was = false, intro = false;
        static float D(Vehicle a, Vehicle b) => Vector3.Distance(a.Pos.ToVector3(), b.Pos.ToVector3());
        for (int i = 0; i < 2000 && movie is null; i++)
        {
            if (mission == "t05" && i == 300) { vm.Data[60] = 1; vm.Data[66] = 2; }
            if (i > 300 && i % 20 == 0) Put(setup, other, at + new Vector3(5, 0, 0));
            s.Tick(new CarInput[2]);
            SnapshotRig.TakeEvents(s);
            if (was && !host.View.Active && !intro)
            {
                intro = true;
                output.WriteLine($"{mission}: the intro ends at tick {s.Ticks}, the lead {Vector3.Distance(start, lead.Pos.ToVector3()):0} m from its start, the other {D(lead, other):0} m from it");
                Assert.True(Vector3.Distance(start, lead.Pos.ToVector3()) > 100, "the intro drove the lead");
                Assert.InRange(D(lead, other), 4f, 40f);
            }
            was = host.View.Active;
        }
        Assert.True(intro);
        Assert.NotNull(movie);
        output.WriteLine($"{mission}: {movie} at tick {s.Ticks}, the lead {Vector3.Distance(at, lead.Pos.ToVector3()):0} m from the trigger, the other {D(lead, other):0} m from the lead");
        Assert.InRange(D(lead, other), 4f, 40f);
        Assert.Null(host.End);
    }

    private static void Put(MissionSetup setup, Vehicle v, Vector3 at)
    {
        at.Y = setup.World.Ground.Query(at.X, 500, at.Z).Height;
        v.Pos = new Math.Double3(at.X, at.Y, at.Z);
        v.Vel = Vector3.Zero; v.Speed = 0;
    }
}

/// <summary>Every scripted mission of both games with 2 and 4 humans, and co-op snapshots.</summary>
public class CoopCorpusTests(ITestOutputHelper output)
{
    private static readonly string[] Base = ["a01", .. Enumerable.Range(1, 7).Select(i => $"s{i:00}"), .. Enumerable.Range(1, 17).Select(i => $"t{i:00}")];
    private static readonly string[] Nitro = ["b01", .. Enumerable.Range(1, 19).Select(i => $"p{i:00}")];

    /// <summary>
    /// Three game minutes of each with the corpus's driving (keys in spells, fire, hotkeys, the skip key from 16 s on)
    /// for every seat: no exception, every seat a human with a finite position, and what co-op did on the way.
    /// </summary>
    [Fact]
    public void EveryScriptedMissionRunsWithTwoAndFourHumans()
    {
        var jobs = new List<(string Dir, string Mission, int Humans)>();
        foreach (var (dir, list) in new[] { (SnapshotRig.Install, Base), (SnapshotRig.NitroInstall, Nitro) })
            if (dir is not null) foreach (var m in list) foreach (int n in (int[])[2, 4]) jobs.Add((dir, m, n));
        if (jobs.Count == 0) return;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = System.Math.Clamp(Environment.ProcessorCount / 2, 1, 12) }, job =>
        {
            try
            {
                var l = new SnapshotRig.Loaded(job.Dir);
                var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(job.Dir, job.Mission, coop: CoopSetup.Of(job.Humans)), SimRate.R20, 7);
                var host = setup.Script!.Host;
                int respawns = 0, left = 0, warnings = 0, formUps = 0;
                host.Coop!.Happened += e => { if (e is CoopRespawned r) { if (r.Why == CoopReturn.Died) respawns++; else left++; } else warnings++; };
                host.Logged += m => { if (m.StartsWith("co-op: form up")) formUps++; };
                int ticks = s.Rate.TicksFor(180f);
                for (int i = 0; i < ticks && setup.Script.Result is null; i++) { SnapshotRig.Tick(s); SnapshotRig.TakeEvents(s); }
                if (s.World.Humans.Count != job.Humans - 1 || host.Coop.Seats.Count != job.Humans) failures.Add($"{job.Mission}x{job.Humans}: the group changed");
                foreach (var h in setup.Humans)
                    if (!float.IsFinite(h.Pos.ToVector3().LengthSquared())) failures.Add($"{job.Mission}x{job.Humans}: a seat's car is at {h.Pos}");
                lines.Add($"{job.Mission}x{job.Humans}: {s.Ticks} ticks, end {setup.Script.Result?.ToString() ?? "-"}, respawns {respawns}, left the map {left}, warnings {warnings}, form-ups {formUps}, " +
                    $"deaths {string.Join("/", host.Coop.Seats.Select(x => x.Deaths))}");
            }
            catch (Exception ex) { failures.Add($"{job.Mission}x{job.Humans}: {ex}"); }
        });
        foreach (var x in lines.Order()) output.WriteLine(x);
        output.WriteLine($"{jobs.Count} runs in {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(failures.IsEmpty, string.Join("\n\n", failures.Order().Take(8)));
    }

    /// <summary>A co-op session's snapshot restores exactly (the seats, the per-human threads and counts, the respawns) and goes on the same.</summary>
    [Fact]
    public void ACoopSessionRestoresExactly()
    {
        var failures = new List<string>();
        foreach (var (dir, mission) in new[] { (SnapshotRig.Install, "t01"), (SnapshotRig.Install, "t05"), (SnapshotRig.NitroInstall, "p01") })
        {
            if (dir is null) continue;
            var l = new SnapshotRig.Loaded(dir);
            var b = SnapshotRig.BuildOf(dir, mission, coop: CoopSetup.Of(2));
            // a seat destroyed on the way: a snapshot while it waits, one after it is back
            var rep = SnapshotEquivalence.Run(l, b, SimRate.R20, [200, 330, 500, 1300], 60,
                prepare: s => s.FrameDone += () => { if (s.Ticks == 300 && s.Script?.Host.Coop is { } c) (s.Combat!.Damage as Damage.DamageModel)!.Destroy(c.Seats[1].Car); });
            failures.AddRange(rep.Failures);
            output.WriteLine($"{mission}: {rep.Snapshots} snapshots, largest {rep.MaxBytes} bytes");
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(6)));
    }
}
