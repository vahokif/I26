using System.Numerics;
using I76.Editor;
using I76.Editor.Objects;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Math;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>The objects no stock map places (ObjectCatalogue.Unplaced): each one is saved into a map and loaded by the sim of both installs, with the colliders it builds.</summary>
public class EditorUnplacedTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-unplaced-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string? Dir(string v) => Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private string Scratch(string real)
    {
        var dir = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(dir, "addon"));
        foreach (var e in Directory.EnumerateFileSystemEntries(real))
        {
            string name = Path.GetFileName(e);
            if (name.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(Path.Combine(dir, name), e); else File.CreateSymbolicLink(Path.Combine(dir, name), e);
        }
        return dir;
    }

    /// <summary>The names to try: the catalogue's table, plus the ones set aside (<see cref="ObjectCatalogue.Excluded"/>) when PROBE_ALL is set (the check that decided to set them aside).</summary>
    private static IEnumerable<(string Name, uint Type)> Names() =>
        ObjectCatalogue.Unplaced.Select(kv => (kv.Key, kv.Value)).Concat(ObjectCatalogue.NitroPackStructures.Select(kv => (kv.Key, kv.Value.Type)))
            .Concat(Environment.GetEnvironmentVariable("I76_PROBE_ALL") == "1" ? ObjectCatalogue.Excluded.Keys.Select(k => (k, 2u)) : []);

    [Theory]
    [InlineData("I76_INSTALL", false)]
    [InlineData("I76_NITRO_INSTALL", true)]
    public void EveryUnplacedObjectAssemblesBuildsItsColliderAndLoadsInASavedMap(string env, bool nitro)
    {
        if (Dir(env) is not { } real) return;
        var install0 = new GameInstall(Scratch(real));
        var addon = Path.Combine(_root, "install", "addon");
        var (project, _) = NewMap.CreateFlat(addon, "unp", "Unplaced", 2, 20, nitro);
        var vfs = new AssetVfs(install0);
        var asm = new ModelAssembler(vfs);
        var names = Names().Where(n => vfs.Exists(n.Name + ".sdf")).ToList();
        float y = project.Mission.Objects.First().Position.Y;
        int i = 0;
        var wanted = new List<string>();
        foreach (var (n, type) in names)
        {
            var m = asm.Assemble(n + ".sdf");
            if (m is not { Parts.Count: > 0 }) { log.WriteLine($"{n}: NO MODEL"); continue; }
            project.Mission.Objects.Add(MapObject.Create(n, type, new Vector3(4 * 128 * 5 + 60 + (i % 10) * 90, y, 72 * 128 * 5 + 60 + (i / 10) * 120), 0));
            wanted.Add(n); i++;
        }
        var files = project.SaveTo(addon);
        string mission = files.First(f => f.EndsWith(nitro ? ".cbt" : ".lvl"));
        var install = new GameInstall(Path.Combine(_root, "install"));
        var world = new MissionWorld(vfs, asm, mission);
        var s = MissionSetup.Build(vfs, world, asm, "vppirna1.vcf", rand: new I76.Sim.Rng.CrtRand(1), melee: ["vppirna1.vcf"]);
        for (int f = 0; f < 40; f++) s.World.Frame(0.05f);
        var built = s.PlacementOf.Values.Select(p => p.Object.Name).ToHashSet();
        foreach (var n in wanted) log.WriteLine($"{n}: {(built.Contains(n) ? "collider" : "NO COLLIDER")}");
        Assert.All(wanted.Where(n => ObjectCatalogue.Unplaced.ContainsKey(n) || ObjectCatalogue.NitroPackStructures.ContainsKey(n)), n => Assert.Contains(n, built));
    }

    /// <summary>A tunnel is laid over a road that runs through it (the road tool does not cut it): a car drives its tube from portal to portal.</summary>
    [Theory]
    [InlineData("betunnl1")]
    [InlineData("betunnl2")]
    [InlineData("betunnl3")]
    public void ACarDrivesThroughATunnelFromPortalToPortal(string name)
    {
        if (Dir("I76_NITRO_INSTALL") is not { } real) return;
        var install0 = new GameInstall(Scratch(real));
        var addon = Path.Combine(_root, "install", "addon");
        var (project, _) = NewMap.CreateFlat(addon, "tun", "Tunnel", 2, 20, nitro: true);
        var vfs = new AssetVfs(install0);
        var asm = new ModelAssembler(vfs);
        var item = ObjectCatalogue.LoadOrScan(install0, asm, Path.Combine(_root, "c.json")).Single(i => i.Name == name);
        Assert.True(item.IsTunnel);
        Assert.InRange(item.TunnelWidth, 11, 13);
        Assert.False(item.CutsRoad);
        var ends = item.DeckEnds();
        Assert.Equal(item.TunnelZ0, ends[0].Position.Z); Assert.Equal(item.TunnelZ1, ends[1].Position.Z);
        float y = project.Mission.Objects.First().Position.Y;
        var o = new Vector3(3000, y, 46500);
        project.Mission.Objects.Add(MapObject.Create(name, item.Type, o, 0));
        string mission = project.SaveTo(addon).First(f => f.EndsWith(".cbt"));
        var world = new MissionWorld(vfs, asm, mission);
        var setup = MissionSetup.Build(vfs, world, asm, "vppirna1.vcf", script: false, rand: new I76.Sim.Rng.CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        double ax = o.X + item.TunnelX, z0 = o.Z + item.TunnelZ0, z1 = o.Z + item.TunnelZ1;
        v.Pos = new Double3(ax, y + 1, z0 - 45);
        v.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        v.Vel = Vector3.Zero;
        double inside = 0; bool through = false;
        for (int f = 1; f <= 28 * 90 && !through; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            if (v.Pos.Z > z0 + 3 && v.Pos.Z < z1 - 3) { inside = System.Math.Max(inside, v.Pos.Z - z0); Assert.InRange(v.Pos.X, ax - 7, ax + 7); }
            through = v.Pos.Z > z1 + 5;
        }
        log.WriteLine($"{name}: through {through}, furthest in {inside:0} of {z1 - z0:0} m, car at z {v.Pos.Z - o.Z:0}");
        Assert.True(through, $"the car stopped {inside:0} m into the tunnel");
    }
}
