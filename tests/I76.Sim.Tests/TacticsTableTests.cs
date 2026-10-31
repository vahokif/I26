using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Exe;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The AI's tactics table as read from each install's own executable (<see cref="TacticsTable"/>): found, whole, and every
/// handler slot bound to one of the port's (<see cref="Ptable.SlotKeys"/>). Runs on I76_INSTALL and I76_NITRO_INSTALL when set.
/// </summary>
public class TacticsTableTests(ITestOutputHelper output)
{
    public static TheoryData<string> Installs()
    {
        var d = new TheoryData<string>();
        foreach (var v in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } dir && Directory.Exists(dir)) d.Add(v);
        if (d.Count == 0) d.Add("");
        return d;
    }

    /// <summary>The table of the first install set (I76_INSTALL, then I76_NITRO_INSTALL), for tests that issue AI orders in a world of their own; null without one.</summary>
    public static PtEntry[]? Installed => _installed.Value;
    private static readonly Lazy<PtEntry[]?> _installed = new(() =>
        new[] { "I76_INSTALL", "I76_NITRO_INSTALL" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(d => d is { Length: > 0 } && Directory.Exists(d)) is { } dir
            ? TacticsTable.For(new GameInstall(dir)) : null);

    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    /// <summary>Binds the table the way a mission does (the first AI order), throwing if any slot has no handler.</summary>
    internal static int Bind(PtEntry[] table)
    {
        var host = new ScriptHost(new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1)), [],
            (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null) { Tactics = table };
        var s = typeof(Ptable).GetProperty("S", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try { return ((Array)s.GetValue(host.Ptable)!).Length; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is { } inner) { throw inner; }
    }

    [Theory]
    [MemberData(nameof(Installs))]
    public void TheExecutableHoldsAWholeTableThatBinds(string env)
    {
        if (env == "") { output.WriteLine("set I76_INSTALL / I76_NITRO_INSTALL to run"); return; }
        var install = new GameInstall(Environment.GetEnvironmentVariable(env)!);
        var t = TacticsTable.For(install);
        output.WriteLine($"{TacticsTable.ExecutableOf(install)}: {t.Length} states, {Ptable.SlotKeys(t).Select(k => k.Key).Distinct().Count()} handlers");
        Assert.Equal(34, t.Length);
        Assert.Equal(Enumerable.Range(0, 34), t.Select(e => e.Id));
        Assert.Equal("tactics init", t[Ptable.TacticsInit].Name);
        Assert.Equal("road_ram", t[1].Name);
        Assert.Equal("dirt_circle", t[Ptable.DirtCircle].Name);
        Assert.Equal("do search", t[Ptable.DoSearch].Name);
        Assert.Equal("race", t[Ptable.Race].Name);
        Assert.Equal(["sit", "guard", "hide"], t[Ptable.Sit..].Select(e => e.Name));
        Assert.Equal(6, t[0].Rules[0].Options.Length);
        Assert.All(t.SelectMany(e => e.Rules), r => Assert.All(r.Weights, w => Assert.Equal(r.Options.Length, w.Length)));
        Assert.Equal(34, Bind(t));
    }

    /// <summary>A build that merged two functions the port keeps apart, or split one it shares, does not bind.</summary>
    [Theory]
    [MemberData(nameof(Installs))]
    public void ABuildSharingFunctionsOtherwiseDoesNotBind(string env)
    {
        if (env == "") return;
        var t = TacticsTable.For(new GameInstall(Environment.GetEnvironmentVariable(env)!));
        // rw_flee's tick made road_ram's: merged
        var merged = t.Select(e => e.Id == 2 ? e with { Tick = t[1].Tick } : e).ToArray();
        Assert.Throws<InvalidDataException>(() => Bind(merged));
        // dirt_rvs's tick (dirt_gunner's function) made a function of its own: split
        uint unused = t.SelectMany(e => new[] { e.Tick, e.Enter, e.OnMatch }).Max() + 0x10;
        var split = t.Select(e => e.Name == "dirt_rvs" ? e with { Tick = unused } : e).ToArray();
        Assert.Throws<InvalidDataException>(() => Bind(split));
    }

    [Fact]
    public void ReadRejectsWhatIsNotAnExecutable()
    {
        Assert.Null(TacticsTable.Read(new byte[0x2000]));
        var b = new byte[0x4000];
        "tactics init\0"u8.CopyTo(b.AsSpan(0x100));
        Assert.Null(TacticsTable.Read(b));
    }
}
