using System.Numerics;
using I76.Editor;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The map editor's surface table in the sim: a flat Nitro map made by the editor, its ground painted with one surface type, that type's
/// row set by a preset and the map saved; the melee the game would play is built from the saved file and the Piranha driven over it.
/// On ICE and BLACK ICE it slides where it holds on the stock row; on DAMAGING ground it loses its chassis reinforcement as it drives, and
/// STRIPPING takes that and its parts at once without killing it (the original's terrain damage has no kill roll).
/// </summary>
public class EditorSurfaceTableTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-surftable-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string? Dir(string v) => Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private GameInstall? Scratch()
    {
        if (Dir("I76_NITRO_INSTALL") is not { } real) return null;
        var dir = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(dir, "addon"));
        foreach (var e in Directory.EnumerateFileSystemEntries(real))
        {
            string name = Path.GetFileName(e);
            if (name.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(Path.Combine(dir, name), e); else File.CreateSymbolicLink(Path.Combine(dir, name), e);
        }
        return new GameInstall(dir);
    }

    /// <summary>A flat 2x2-tile map, every sample painted <paramref name="paint"/>, that id's row made by <paramref name="preset"/> (null: stock), saved.</summary>
    private static void MakeMap(GameInstall install, string name, int paint, SurfacePreset? preset)
    {
        var (_, files) = NewMap.CreateFlat(install.AddonDir!, name, name, 2, 10, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        var doc = new TerrainDocument(project.Terrain, name, name, mission, install.AddonDir, install);
        doc.InitSurfaces(project.Mission.Surfaces, null);
        const int n = TerrainTiles.Tile * TerrainTiles.Tile;
        for (int t = 0; t < doc.Tiles.TileCount; t++)
            for (int i = 0; i < n; i++) doc.SetSample(t, i, SurfaceTypes.With(doc.GetSample(t, i), paint));
        if (preset is not null) doc.SetSurfaces(doc.Surfaces!.With(paint, preset.Apply(paint, doc.Surfaces[paint])));
        doc.Save();
    }

    /// <param name="Dead">Dying or dead (flags 0x8000, 0x20): a wrecked car.</param>
    /// <param name="Health0">The armour and chassis reinforcement pools at the start; <paramref name="HealthLost"/> what they lost.</param>
    private sealed record Run(float SpeedAtTurn, float MaxSlipDegrees, int SlidingTicks, int Health0, int HealthLost, int EngineHp, bool Dead, int Surface, float Grip);

    /// <summary>The engine started, full throttle for 6 s, then full throttle and full left lock for 3 s.</summary>
    private Run Drive(GameInstall install, string map)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var w = new MissionWorld(vfs, asm, install.FindMission(map)!);
        var setup = MissionSetup.Build(vfs, w, asm, "vppirna1.vcf", script: false, rand: new CrtRand(1), melee: new List<string>(), seed: 1);
        setup.Melee?.Start();
        var s = new SimSession(setup, SessionOptions.SinglePlayer, SimRate.FromLabel(28));
        var v = setup.World.Player!;
        s.AddDriver(v);
        setup.World.Vehicles.RemoveAll(x => x != v);
        var h = v.Health!;
        int Health() => h.Armour.Sum() + h.Chassis.Sum();
        int health0 = Health();
        const int go = 100, turn = go + 6 * 28, end = turn + 3 * 28;
        float speedAtTurn = 0, maxSlip = 0;
        int sliding = 0;
        for (int f = 1; f <= end; f++)
        {
            var i = new CarInput();
            if (f is >= 70 and <= 80) i.Commands = CarCommands.StartEngine;
            if (f >= go) i.Buttons = CarButtons.ThrottleUp;
            if (f >= turn) i.Buttons |= CarButtons.SteerLeft;
            s.Tick([i]);
            setup.Damage.Events.Clear();
            if (f == turn) speedAtTurn = v.Speed;
            if (f < turn) continue;
            if ((v.Flags & VehicleFlags.Sliding) != 0) sliding++;
            var flat = new Vector2(v.Vel.X, v.Vel.Z);
            var fwd = new Vector2(v.Rot.Forward.X, v.Rot.Forward.Z);
            if (flat.Length() > 2 && fwd.Length() > 0.1f)
                maxSlip = MathF.Max(maxSlip, MathF.Acos(System.Math.Clamp(Vector2.Dot(Vector2.Normalize(flat), Vector2.Normalize(fwd)), -1f, 1f)) * 180 / MathF.PI);
        }
        var g = setup.World.Ground;
        var r = new Run(speedAtTurn, maxSlip, sliding, health0, health0 - Health(), h.Engine?.Hp ?? -1, ((uint)v.Flags & 0x8020) != 0, v.Surface, g.Surface(v.Surface).Grip);
        log.WriteLine($"{map}: {r}");
        return r;
    }

    [Fact]
    public void ACarSlidesOnPaintedIceAndHoldsOnTheStockRow()
    {
        if (Scratch() is not { } install) return;
        MakeMap(install, "grip", 3, null);
        MakeMap(install, "ice", 3, SurfacePresets.Ice);
        MakeMap(install, "blackice", 3, SurfacePresets.BlackIce);
        var stock = Drive(install, "grip");
        Assert.Equal(1.9f, stock.Grip);
        Assert.Equal(0, stock.SlidingTicks);
        Assert.True(stock.MaxSlipDegrees < 5, $"stock slip {stock.MaxSlipDegrees}");
        foreach (var (map, grip) in new[] { ("ice", 0.5f), ("blackice", 0.2f) })
        {
            var ice = Drive(install, map);
            Assert.Equal(3, ice.Surface);
            Assert.Equal(grip, ice.Grip);
            // on ice the wheels spin (slower off the line) and the turn slides: the car spins round, its path leaving its heading
            Assert.True(ice.SpeedAtTurn < stock.SpeedAtTurn * 0.6f, $"{map}: {ice.SpeedAtTurn} m/s against {stock.SpeedAtTurn}");
            Assert.True(ice.SlidingTicks > 56, $"{map}: sliding ticks {ice.SlidingTicks}");
            Assert.True(ice.MaxSlipDegrees > 90, $"{map}: slip {ice.MaxSlipDegrees}");
        }
    }

    [Fact]
    public void ACarLosesHealthOnDamagingGroundAndIsStrippedOnStrippingGround()
    {
        if (Scratch() is not { } install) return;
        MakeMap(install, "safe", 0, null);
        MakeMap(install, "hurt", 0, SurfacePresets.Damaging);
        MakeMap(install, "strip", 0, SurfacePresets.Stripping);
        var safe = Drive(install, "safe");
        var hurt = Drive(install, "hurt");
        var strip = Drive(install, "strip");
        Assert.Equal(0, safe.HealthLost);   // stock dirt takes nothing
        Assert.False(safe.Dead);
        // 200 a second for the 8-odd seconds the car is faster than 7.65 m/s, out of the chassis reinforcement (terrain damage goes under the armour)
        Assert.InRange(hurt.HealthLost, 1000, 2000);
        Assert.False(hurt.Dead);
        // the reinforcement and the parts (the engine among them) gone at once, but alive: terrain damage has no kill roll
        Assert.Equal(0, strip.EngineHp);
        Assert.True(strip.HealthLost > 0);
        Assert.False(strip.Dead);
    }
}
