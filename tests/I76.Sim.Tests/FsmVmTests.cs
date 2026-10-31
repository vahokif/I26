using System.Numerics;
using I76.Formats.Mission;
using I76.Sim.Fsm;
using I76.Sim.Rng;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>A host with inert objects: records what scripts ask of it.</summary>
public class RecordingHost : IFsmHost
{
    public sealed class Obj(string name) { public string Name = name; public override string ToString() => Name; }
    public float Now { get; set; }
    public readonly List<string> Calls = [];
    public readonly List<string> Logs = [];
    public readonly Dictionary<string, Obj> Objects = [];
    public bool Ended;

    public object? FindByInstanceId(byte[] id)
    {
        var k = Convert.ToHexString(id);
        if (!Objects.TryGetValue(k, out var o)) Objects[k] = o = new Obj(k);
        return o;
    }
    public bool IsRemoved(object o) => false;
    public bool IsDead(object o) => false;
    public bool IsVehicle(object o) => true;
    public bool IsStructure(object o) => false;
    public bool IsAirborne(object o) => false;
    public float HealthPercent(object o) => 100;
    public Vector3 Position(object o) => Vector3.Zero;
    public int Team(object o) => 1;
    public IEnumerable<object> Vehicles => Objects.Values;
    public IEnumerable<object> Structures => [];
    public void Order(object actor, object? target, AiOrder order, IReadOnlyList<Vector3>? path, float speed, int followFlag = 0, float f1 = 0, float f2 = 0, object? reference = null)
        => Calls.Add($"order {actor} {order} {speed}");
    public bool ConsumeArrived(object actor) => false;
    public bool ConsumeAtFollow(object actor) => false;
    public bool ConsumeShot(object o) => false;
    public bool ConsumeRammed(object o) => false;
    public bool ConsumeAttacked(object o) => false;
    public object? LastAttacker(object o, int kind) => null;
    public bool KilledByPlayer(object o) => false;
    public void SetSkill(object o, int a, int b) { }
    public void SetAggression(object o, int n) { }
    public void SetAvoid(object o, int? on) { }
    public void SetMaxAttackers(object o, int n) { }
    public void CbSay(string sound, object? speaker, int priority) => Calls.Add($"cb {sound} {priority}");
    public bool CbEmpty => true;
    public void CbKill() { }
    public void CbStop() { }
    public void ObjectiveSuccess(int n) => Calls.Add($"success {n}");
    public void ObjectiveFail(int n) => Calls.Add($"fail {n}");
    public void ObjectiveReveal(int n) => Calls.Add($"reveal {n}");
    public void EndMission(bool success, float delay, int failMessage) { Ended = true; Calls.Add($"end {success} {delay}"); }
    public void Teleport(object o, Vector3 at, int angleDegrees, float speed) => Calls.Add($"teleport {o}");
    public void Destroy(object o) => Calls.Add($"destroy {o}");
    public void StartCar(object o) { }
    public void SetLights(object o, bool on) { }
    public void SetSiren(object o, bool on) { }
    public void TriggerGate(object o) { }
    public void Camera(string op, IReadOnlyList<int> args, IReadOnlyList<object?> objects, IReadOnlyList<Vector3>? path) => Calls.Add($"cam {op}");
    public bool CameraArrived() => true;
    public bool SkipPressed() => false;
    public void Control(object o, int braccel, int steer, int? ebrake, int? reverse) { }
    public void DriveControl(object o) { }
    public bool ControlDone(object o) => true;
    public void PlayMovie(string name) { }
    public void Log(string message) => Logs.Add(message);
}

public class FsmVmTests
{
    private static FsmProgram Program(List<string> protos, (uint, uint)[] code, int depth, int[] stack, int[] data) =>
        new(protos, [], [], [], [(0, depth, stack)], data, code, true);

    private static uint I(int v) => unchecked((uint)v);

    [Fact]
    public void CountsUpYieldingEachFrameThenSucceedsAndReturns()
    {
        // data[0] counts once per frame; once it passes a local set to 5, success(0) and return.
        string[] p = ["set", "inc", "isGreater", "success"];
        (uint, uint)[] code =
        [
            (6, 1),            // 0 allocLocals 1
            (1, 5),            // 1 pushImm 5
            (4, 1),            // 2 argLocalAddr 1
            (4, 2),            // 3 argLocalAddr 2
            (13, 0),           // 4 set(local1, 5)
            (7, 1),            // 5 freeLocals 1
            (5, I(-2)),        // 6 argLocal -2 (= &data[0])
            (13, 1),           // 7 inc
            (5, I(-2)),        // 8
            (4, 1),            // 9
            (13, 2),           // 10 isGreater(data0, local1)
            (9, 14),           // 11 branchIfFalse 14
            (13, 3),           // 12 success
            (12, 1),           // 13 return 1
            (10, 6),           // 14 yieldJump 6
        ];
        var host = new RecordingHost();
        var vm = new FsmVm(Program([.. p], code, 1, [0], [0]), host, new CrtRand());
        for (int i = 0; i < 5; i++) vm.Tick();
        Assert.Equal(5, vm.Data[0]);
        Assert.Empty(host.Calls);
        vm.Tick();
        Assert.Equal(6, vm.Data[0]);
        Assert.Equal(["success 0"], host.Calls);
        Assert.True(vm.Threads[0].Finished);
        vm.Tick();
        Assert.Equal(6, vm.Data[0]);
    }

    [Fact]
    public void ALoopThatNeverYieldsTripsTheWatchdog()
    {
        var host = new RecordingHost();
        var vm = new FsmVm(Program(["null"], [(13, 0), (8, 0)], 0, [], []), host, new CrtRand());
        vm.Tick();
        Assert.True(vm.Watchdog);
        Assert.Contains(host.Logs, l => l.Contains("watchdog"));
    }

    [Fact]
    public void TimersCompareAgainstMissionSeconds()
    {
        // startTimer(local1); loop: timeGreater(local1, 3) → success.
        string[] p = ["startTimer", "timeGreater", "success"];
        (uint, uint)[] code =
        [
            (6, 1), (4, 1), (13, 0),          // 0–2 startTimer(local1)
            (1, 3), (4, 1), (4, 2), (13, 1), (7, 1),   // 3–7 timeGreater(local1, 3)
            (9, 10), (13, 2),                 // 8–9
            (10, 3),                          // 10 yieldJump 3
        ];
        var host = new RecordingHost { Now = 10 };
        var vm = new FsmVm(Program([.. p], code, 0, [], []), host, new CrtRand());
        vm.Tick();
        host.Now = 12.4f; vm.Tick();
        Assert.Empty(host.Calls);
        host.Now = 13.6f; vm.Tick();    // (int)13.6 + 0.5 > 10 + 3
        Assert.Contains("success 0", host.Calls);
    }
}

public class FsmScriptTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EveryShippedScriptRunsTwoMinutesWithoutFaults()
    {
        if (InstallDir is null) return;
        var dir = new I76.Formats.Vfs.GameInstall(InstallDir).MissionDir!;
        int ran = 0;
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)).Order())
        {
            var prog = new MissionFile(f).ReadFsmProgram();
            if (prog is null || prog.Code.Length == 0) continue;
            ran++;
            var host = new RecordingHost();
            var vm = new FsmVm(prog, host, new CrtRand());
            for (int i = 0; i < 3333 && !host.Ended; i++) { host.Now = i * 0.036f; vm.Tick(); }
            var faults = host.Logs.Where(l => l.Contains("unknown prototype") || l.Contains("out of range") || l.Contains("watchdog") || l.Contains("Unknown fsm")).ToList();
            log.WriteLine($"{Path.GetFileName(f)}: {vm.Threads.Count(t => t.Finished)}/{vm.Threads.Count} finished, {host.Calls.Count} calls, ended={host.Ended}; {string.Join(" | ", host.Calls.Take(6))}");
            foreach (var l in host.Logs.Distinct().Take(5)) log.WriteLine("   " + l);
            Assert.True(faults.Count == 0, $"{f}: {string.Join("; ", faults.Distinct())}");
        }
        Assert.Equal(25, ran);
    }
}
