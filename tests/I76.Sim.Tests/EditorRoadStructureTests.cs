using System.Numerics;
using I76.Editor;
using I76.Editor.Objects;
using I76.Editor.Roads;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The road tool's bridge and tunnel stretches in the sim (docs/map-editor.md, Bridges and tunnels): a map is made with the editor library
/// (ground road, a bridge over a valley, ground road again; a tunnel with the road through it), saved, loaded as the game loads a map, and a
/// car drives it end to end. Only shipped files are read (the install's models and the scratch map the test writes).
/// </summary>
public class EditorRoadStructureTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-roadstruct-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string? NitroDir => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

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

    private sealed record Built(string Dir, string Mission, GameInstall Install, AssetVfs Vfs, ModelAssembler Asm, Vector2 Origin);

    private (TerrainDocument Doc, ObjectLayer Layer, RoadStore Roads, List<CatalogueItem> Catalogue, Built B) NewMapOf(string stem, Func<float, float, float> ground)
    {
        var dir = Scratch(NitroDir!);
        var addon = Path.Combine(dir, "addon");
        var (_, files) = NewMap.CreateFlat(addon, stem, stem, 2, 50, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        var store = RoadStore.FromMission(File.ReadAllBytes(mission));
        var doc = new TerrainDocument(project.Terrain, stem, stem, mission, addon, roads: store);
        float ox = 4 * 128 * 5, oz = 72 * 128 * 5;
        for (int sz = 72 * 128; sz < 72 * 128 + 256; sz++)
            for (int sx = 4 * 128; sx < 4 * 128 + 256; sx++)
                doc.SetHeight(sx, sz, (int)System.Math.Round(ground(sx * 5f - ox, sz * 5f - oz) / Units.HeightScale));
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var cat = ObjectCatalogue.LoadOrScan(install, asm, Path.Combine(_root, "c-" + stem + ".json"));
        var layer = new ObjectLayer(project.Mission, n => asm.ResolveDefinition(n), store) { Ground = (x, z) => RoadTerrain.Ground(doc, x, z) };
        doc.Layer = layer;
        return (doc, layer, store, cat, new Built(dir, mission, install, vfs, asm, new Vector2(ox, oz)));
    }

    private static SimSession Drive(MissionSetup setup, out Vehicle car)
    {
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var player = setup.World.Player!;
        car = player;
        s.AddDriver(player);
        setup.World.Vehicles.RemoveAll(x => x != player);
        return s;
    }

    [Fact]
    public void ACarDrivesGroundRoadBridgeAndGroundRoadEndToEndWithoutFallingOrHittingALip()
    {
        if (NitroDir is null) return;
        // a valley 20 m deep across z = 250..450 on ground that climbs 1 % along z: the bridge is pitched along the grade
        var (doc, layer, roads, cat, b) = NewMapOf("brg", (x, z) => 50 + z * 0.01f - (z is > 250 and < 450 ? 20 * MathF.Sin((z - 250) / 200 * MathF.PI) : 0));
        var o = b.Origin;
        float x0 = o.X + 400;
        var grade = new RoadGrade.Settings(); var shoulder = new RoadTerrain.Settings();
        // ground road to the valley
        var r1 = RoadPlanner.Plan(RoadPlanner.Path(DrawMode.Straight, [new(x0, o.Y + 60), new(x0, o.Y + 200)]), 0, null, null, roads);
        Assert.NotNull(RoadPlanner.Commit(doc, roads, r1, true, grade, shoulder));
        // the bridge, from the road's end
        var plan = RoadStructures.Plan(new StretchQuery
        {
            Catalogue = cat, Mode = BuildMode.Bridge, Start = new(x0, o.Y + 200), Aim = new(x0, o.Y + 520), Ground = p => RoadTerrain.Ground(doc, p.X, p.Y),
            StartHeight = RoadTerrain.Ground(doc, x0, o.Y + 200),
        });
        Assert.True(plan.Ok, plan.Problem);
        log.WriteLine($"{plan.Summary}; warnings: {string.Join(" | ", plan.Warnings)}");
        var res = RoadStructures.Commit(doc, layer, roads, plan, true, grade, shoulder)!;
        log.WriteLine(res.Message);
        // the ground road on from the bridge's far end, at its height (a structure end pins it, as the road tool's snap does)
        var attach = new StructureAttach.Attach(new Vector3(plan.End.X, plan.EndHeight, plan.End.Y), plan.Dir, 10f, false) { Slope = plan.Grade };
        var snap = new SnapResult(plan.End, SnapKind.Structure, Structure: attach);
        var r2 = RoadPlanner.Plan(RoadPlanner.Path(DrawMode.Straight, [plan.End, new(x0, plan.End.Y + 120)]), 0, snap, null, roads);
        Assert.Null(r2.Problem);
        Assert.NotNull(RoadPlanner.Commit(doc, roads, r2, true, grade, shoulder));
        doc.Save();

        var world = new MissionWorld(b.Vfs, b.Asm, b.Mission);
        var setup = MissionSetup.Build(b.Vfs, world, b.Asm, "vppirna1.vcf", script: false, rand: new I76.Sim.Rng.CrtRand(1), melee: new List<string>(), seed: 1);
        // the pieces were loaded as bridges with their colliders
        Assert.Equal(plan.Pieces.Count, setup.PlacementOf.Values.Count(p => p.Object.Name.StartsWith("be")));
        var s = Drive(setup, out var v);
        float yStart = setup.Ground.BareHeight(x0, o.Y + 50);
        v.Pos = new Double3(x0, yStart + 1, o.Y + 50);
        v.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        v.Vel = Vector3.Zero;
        double Deck(double z) { for (int i = 0; i + 1 < plan.Samples.Count; i++) if (z <= plan.Samples[i + 1].Y) return plan.Deck[i] + (plan.Deck[i + 1] - plan.Deck[i]) * (z - plan.Samples[i].Y) / (plan.Samples[i + 1].Y - plan.Samples[i].Y); return plan.Deck[^1]; }
        var onDeck = new List<double>();
        double endZ = plan.End.Y + 100, lowest = double.MaxValue;
        bool through = false;
        for (int f = 1; f <= 28 * 90 && !through; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            if (v.Pos.Z > plan.Start.Y + 8 && v.Pos.Z < plan.End.Y - 8)
            {
                onDeck.Add(v.Pos.Y - Deck(v.Pos.Z));
                lowest = System.Math.Min(lowest, v.Pos.Y - Deck(v.Pos.Z));
                Assert.InRange(v.Pos.X, x0 - 4.5, x0 + 4.5);
            }
            through = v.Pos.Z > endZ;
        }
        Assert.True(through, $"the car stopped at z {v.Pos.Z - o.Y:0} of {endZ - o.Y:0}");
        Assert.True(onDeck.Count > 50, $"{onDeck.Count} ticks on the deck");
        // a stock deck is a 1 m hump (up over its first 20 m, down over its last 20), so the car rides 0 to 1 m over the planned line, and
        // the joints are dips; a lip would be a jump from one tick to the next
        double worstStep = 0;
        for (int i = 1; i < onDeck.Count; i++) worstStep = System.Math.Max(worstStep, System.Math.Abs(onDeck[i] - onDeck[i - 1]));
        log.WriteLine($"on the deck {onDeck.Count} ticks, height over the planned line {onDeck.Min():0.00}..{onDeck.Max():0.00}, worst step between ticks {worstStep:0.00} m");
        Assert.True(lowest > -0.4, $"the car sank {-lowest:0.0} m through the deck");
        Assert.InRange(onDeck.Max(), 0.5, 1.6);
        Assert.True(worstStep < 0.2, $"a lip of {worstStep:0.00} m");
    }

    [Fact]
    public void ACarDrivesTheRoadThroughATunnelTheToolLaidOnASlope()
    {
        if (NitroDir is null) return;
        var (doc, layer, roads, cat, b) = NewMapOf("tnl", (x, z) => 50 + z * 0.04f);
        var o = b.Origin;
        float x0 = o.X + 500;
        var grade = new RoadGrade.Settings(); var shoulder = new RoadTerrain.Settings();
        var plan = RoadStructures.Plan(new StretchQuery { Catalogue = cat, Mode = BuildMode.Tunnel, Start = new(x0, o.Y + 300), Aim = new(x0, o.Y + 420), Ground = p => RoadTerrain.Ground(doc, p.X, p.Y) });
        Assert.True(plan.Ok, plan.Problem);
        log.WriteLine($"{plan.Summary}; warnings: {string.Join(" | ", plan.Warnings)}");
        var res = RoadStructures.Commit(doc, layer, roads, plan, true, grade, shoulder, roadKind: 0)!;
        Assert.NotNull(res.Road);
        // the road on from the far portal, and one leading in, graded to the tunnel's floor
        var attachNear = new SnapResult(roads[0].Points[0].Centre, SnapKind.RoadEnd, 0, 0, AtStart: true);
        var lead = RoadPlanner.Plan(RoadPlanner.Path(DrawMode.Straight, [new(x0, o.Y + 160), roads[0].Points[0].Centre]), 0, null, attachNear, roads);
        Assert.Null(lead.Problem);
        Assert.NotNull(RoadPlanner.Commit(doc, roads, lead, true, grade, shoulder));
        doc.Save();

        var world = new MissionWorld(b.Vfs, b.Asm, b.Mission);
        var setup = MissionSetup.Build(b.Vfs, world, b.Asm, "vppirna1.vcf", script: false, rand: new I76.Sim.Rng.CrtRand(1), melee: new List<string>(), seed: 1);
        Assert.Contains(setup.PlacementOf.Values, p => p.Object.Name == plan.Pieces[0].Item.Name);
        var s = Drive(setup, out var v);
        float zStart = o.Y + 175;
        v.Pos = new Double3(x0, setup.Ground.BareHeight(x0, zStart) + 1, zStart);
        v.Rot = new Rows(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        v.Vel = Vector3.Zero;
        double z0 = plan.Start.Y, z1 = plan.End.Y;
        bool through = false; double inside = 0, lowest = double.MaxValue;
        for (int f = 1; f <= 28 * 90 && !through; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= 100) i.Buttons = CarButtons.ThrottleUp;
            s.Tick([i]);
            setup.Damage?.Events.Clear();
            if (v.Pos.Z > z0 + 3 && v.Pos.Z < z1 - 3) { inside = System.Math.Max(inside, v.Pos.Z - z0); Assert.InRange(v.Pos.X, x0 - 6, x0 + 6); lowest = System.Math.Min(lowest, v.Pos.Y - plan.StartHeight); }
            through = v.Pos.Z > z1 + 20;
        }
        log.WriteLine($"furthest in {inside:0} of {z1 - z0:0} m, lowest {lowest:0.00} over the floor");
        Assert.True(through, $"the car stopped {inside:0} m into the tunnel");
        Assert.True(lowest > -1.0, "the car is on the floor");
    }
}
