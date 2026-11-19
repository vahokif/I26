using System.Numerics;
using I76.Editor;
using I76.Editor.Intersections;
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

/// <summary>
/// The road pieces the editor now places (the palette's ROAD PIECES, the road tool's caps, turn-arounds and J strips): each one saved into a
/// map and loaded by the world and the sim of both installs, painted into the ground (no model, no collider, as the original), and driven over.
/// </summary>
public class EditorRoadPieceTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-roadpieces-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Every piece the editor can place now, with the type it writes: the hand-placed ones and the road tool's road ends.</summary>
    private static IEnumerable<(string Name, uint Type)> Pieces() =>
        ObjectCatalogue.HandPieces.Select(kv => (kv.Key, kv.Value))
            .Concat(PieceCatalogue.All.Where(d => d.IsEnd && !d.Name.Contains("isect")).Select(d => (d.Name, d.ObjectType)));

    [Theory]
    [InlineData("I76_INSTALL", false)]
    [InlineData("I76_NITRO_INSTALL", true)]
    public void EveryPlaceableRoadPieceLoadsIsPaintedIntoTheGroundAndHasNoCollider(string env, bool nitro)
    {
        if (Dir(env) is not { } real) return;
        var install0 = new GameInstall(Scratch(real));
        var addon = Path.Combine(_root, "install", "addon");
        var (project, _) = NewMap.CreateFlat(addon, "rpc", "Road pieces", 2, 20, nitro);
        var vfs = new AssetVfs(install0);
        var asm = new ModelAssembler(vfs);
        var tex = new TextureResolver(vfs);
        float y = project.Mission.Objects.First().Position.Y;
        int i = 0;
        var wanted = Pieces().ToList();
        foreach (var (n, type) in wanted)
        {
            project.Mission.Objects.Add(MapObject.Create(n, type, new Vector3(4 * 128 * 5 + 60 + (i % 6) * 60, y, 72 * 128 * 5 + 60 + (i / 6) * 230), (i % 4) * MathF.PI / 2));
            i++;
        }
        string mission = project.SaveTo(addon).First(f => f.EndsWith(nitro ? ".cbt" : ".lvl"));
        var world = new MissionWorld(vfs, asm, mission);
        foreach (var (n, type) in wanted)
        {
            var p = world.Placements.Single(p => p.Object.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(type, p.Object.ObjectType);
            var model = asm.Assemble(p.Definition!);
            Assert.True(RoadPieceObjects.Painted(type, model), $"{n}: not a flat piece the loader paints");
            // every top face's texture is in this install's data
            var drawn = RoadPieceObjects.Drawn(model!);
            var faces = drawn.Parts.SelectMany(part => part.Mesh.Faces).Where(f => f.Texture.Length > 0).ToList();
            Assert.NotEmpty(faces);
            Assert.All(faces, f => Assert.NotNull(tex.ResolveFace(f.Texture, drawn.ObjectCode, drawn.TmtNames, vtfMaps: drawn.VtfMaps)));
            log.WriteLine($"{n}: type {type:x}, painted, {faces.Count} faces, textures {string.Join(",", faces.Select(f => f.Texture).Distinct())}");
        }
        var s = MissionSetup.Build(vfs, world, asm, "vppirna1.vcf", rand: new I76.Sim.Rng.CrtRand(1), melee: ["vppirna1.vcf"]);
        for (int f = 0; f < 40; f++) s.World.Frame(0.05f);
        var objects = s.PlacementOf.Values.Select(p => p.Object.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(wanted, w => Assert.DoesNotContain(w.Name, objects));        // no world object, no collider: drawn over the ground only
    }

    /// <summary>
    /// The Asset Bible's two notes on pieces: ita23_1 "not displayed in I'76" (its texture is in both GOG installs, and the base game's own
    /// T01 places it) and ijd28t1 "crashes the game" (its texture, R2DNR_18, is in neither install: it is left out of the editor).
    /// </summary>
    [Theory]
    [InlineData("I76_INSTALL")]
    [InlineData("I76_NITRO_INSTALL")]
    public void TheBiblesBrokenPiecesAsTheInstallsHaveThem(string env)
    {
        if (Dir(env) is not { } real) return;
        var vfs = new AssetVfs(new GameInstall(real));
        var asm = new ModelAssembler(vfs);
        var tex = new TextureResolver(vfs);
        string? Texture(string name)
        {
            var m = RoadPieceObjects.Drawn(asm.Assemble(name + ".sdf")!);
            var f = m.Parts.SelectMany(p => p.Mesh.Faces).First(f => f.Texture.Length > 0);
            return tex.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps);
        }
        Assert.NotNull(Texture("ita23_1"));
        Assert.Null(Texture("ijd28t1"));
        Assert.Null(PieceCatalogue.Lookup("ijd28t1"));
        Assert.False(ObjectCatalogue.IsHandPiece("ijd28t1"));
    }

    /// <summary>A car driven straight over a runway, a start line, a turn-around, a J strip and a cap laid end to end crosses them all at speed: they are ground.</summary>
    [Theory]
    [InlineData("I76_INSTALL", false)]
    [InlineData("I76_NITRO_INSTALL", true)]
    public void ACarDrivesOverThePlacedPieces(string env, bool nitro)
    {
        if (Dir(env) is not { } real) return;
        var install0 = new GameInstall(Scratch(real));
        var addon = Path.Combine(_root, "install", "addon");
        var (project, _) = NewMap.CreateFlat(addon, "rpd", "Road drive", 2, 20, nitro);
        var vfs = new AssetVfs(install0);
        var asm = new ModelAssembler(vfs);
        float y = project.Mission.Objects.First().Position.Y;
        double ax = 3000;
        float z = 46500;
        // each centred on x = ax, one after the other along +z
        foreach (var (n, type, w, d) in new[] { ("rerwend1", 0x50u, 20f, 25f), ("rmrwmid1", 0x50u, 20f, 200f), ("ima21_1", 0x50u, 10f, 10f), ("ica21_1", 0x50u, 20f, 20f), ("ija2171", 0x50u, 10f, 20f), ("ijd27t1", 0x52u, 10f, 10f), ("iyd27_1", 0x52u, 30f, 45f) })
        {
            project.Mission.Objects.Add(MapObject.Create(n, type, new Vector3((float)ax - w / 2, y, z), 0));
            z += d;
        }
        string mission = project.SaveTo(addon).First(f => f.EndsWith(nitro ? ".cbt" : ".lvl"));
        var world = new MissionWorld(vfs, asm, mission);
        var setup = MissionSetup.Build(vfs, world, asm, "vppirna1.vcf", script: false, rand: new I76.Sim.Rng.CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        v.Pos = new Double3(ax, y + 1, 46500 - 40);
        v.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        v.Vel = Vector3.Zero;
        bool over = false; double slowest = double.MaxValue;
        for (int f = 1; f <= 28 * 60 && !over; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            if (v.Pos.Z > 46500 + 20 && v.Pos.Z < z - 5) { slowest = System.Math.Min(slowest, v.Vel.Length()); Assert.InRange(v.Pos.X, ax - 8, ax + 8); Assert.InRange(v.Pos.Y, y - 1, y + 3); }
            over = v.Pos.Z > z + 5;
        }
        log.WriteLine($"over {over}, car at z {v.Pos.Z - 46500:0} of {z - 46500:0} m, slowest {slowest:0.0} m/s on the pieces");
        Assert.True(over, $"the car stopped at {v.Pos.Z - 46500:0} m");
        Assert.True(slowest > 5, $"the car slowed to {slowest:0.0} m/s on the pieces");
    }
}
