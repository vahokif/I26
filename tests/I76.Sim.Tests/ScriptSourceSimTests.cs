using I76.Formats.Assembly;
using I76.Formats.Fsm;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// A mission recompiled from its YAML source (renamed through a names sidecar, so the source is not the decompiler's own)
/// plays exactly as the original: the same FSM bytes, and the same simulation state every tick.
/// </summary>
public class ScriptSourceSimTests(ITestOutputHelper log)
{
    private static string? InstallDir => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void ARecompiledMissionRunsTickForTickAsTheOriginal()
    {
        if (InstallDir is null) return;
        var install = new GameInstall(InstallDir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var path = install.FindMission("t01")!;
        var file = File.ReadAllBytes(path);
        var payload = MissionScriptFile.Payload(file);

        // every script, thread and global renamed, then compiled back from the YAML text
        var plain = MissionScriptDecompiler.Decompile(payload, "T01").Script;
        var sb = new System.Text.StringBuilder("threads:\n");
        foreach (var t in plain.Threads) sb.Append($"  {t.Name}: renamed_{t.Name}\n");
        sb.Append("scripts:\n");
        foreach (var s in plain.Scripts) sb.Append($"  {s.Name}: {{name: renamed_{s.Name}, fields: {{f0: first}}, variables: {{v0: counter}}}}\n");
        sb.Append("globals:\n");
        foreach (var g in plain.Globals) sb.Append($"  {g.Name}: cell_{g.Name}\n");
        var names = ScriptNames.Read(sb.ToString());
        var yaml = MissionScriptYaml.Write(MissionScriptDecompiler.Decompile(payload, "T01", names).Script);
        Assert.Contains("renamed_script0:", yaml);
        var recompiled = MissionScriptFile.Compile(yaml, file);
        Assert.Equal(file, recompiled);

        (SimSession Session, MissionSetup Setup) Start(byte[]? fsm)
        {
            var world = new MissionWorld(vfs, asm, path);
            if (fsm is not null) world.Mission.FsmOverride = fsm;
            var setup = MissionSetup.Build(vfs, world, asm, seed: 7);
            var session = new SimSession(setup, rate: SimRate.FromLabel(20));
            session.AddDriver(setup.World.Player!);
            return (session, setup);
        }
        var (a, sa) = Start(null);
        var (b, sb2) = Start(MissionScriptFile.Payload(recompiled));
        for (int t = 0; t < 400; t++)
        {
            a.Tick([new CarInput()]);
            b.Tick([new CarInput()]);
            Assert.True(a.StateHash() == b.StateHash(), $"tick {t}: the recompiled mission diverged");
        }
        var vm = sb2.Script!.Vm!;
        var initial = FsmChunk.Read(payload).Data;
        int changed = vm.Data.Where((v, i) => v != initial[i]).Count();
        log.WriteLine($"after {sb2.Script.Host.Now:0.0} s: {changed} of {vm.Data.Length} data cells changed by the script, {vm.Threads.Count(x => x.Finished)} of {vm.Threads.Count} threads done");
        Assert.True(changed > 0, "the script did nothing");
    }
}
