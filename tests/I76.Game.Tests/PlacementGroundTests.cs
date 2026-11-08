using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Tests;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The modern look's ground stands where the simulation's does under everything placed on a map and everywhere a car drives, as the
/// 1997 look's does: on every stock map (and the Nitro Pack's), a one-tile map and a two-tile arena with its stands on a ridge no car
/// reaches and objects in the map's last cells (the shape of a player's map whose ticket booths and gate floated in the modern look).
/// </summary>
public class PlacementGroundTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("i26-pins-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>The most the drawn ground may stand off the simulation's, metres (the surface holds open cells within 4 cm).</summary>
    private const double Near = 0.05;

    private static MissionWorld Load(AssetVfs vfs, string path) => new(vfs, new ModelAssembler(vfs), path);

    /// <summary>
    /// The simulation's ground (TerrainGround: bilinear on the raw samples, the outside ground's past the tiles) in double precision: its
    /// own single-precision sample coordinate is off by up to 2 mm this far from the origin, 4 cm on a cell with a 70 m cliff in it.
    /// </summary>
    private sealed class Ground(MissionWorld w)
    {
        private double Raw(int sx, int sz) => w.Terrain.Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : w.Outside?.Raw(sx, sz) ?? 0;
        public double Height(double x, double z)
        {
            double fx = x * MissionWorld.SampleScale, fz = z * MissionWorld.SampleScale;
            int sx = (int)Math.Floor(fx), sz = (int)Math.Floor(fz);
            double tx = fx - sx, tz = fz - sz;
            double a = Raw(sx, sz) + (Raw(sx + 1, sz) - Raw(sx, sz)) * tx, b = Raw(sx, sz + 1) + (Raw(sx + 1, sz + 1) - Raw(sx, sz + 1)) * tx;
            return (a + (b - a) * tz) * 0.1;
        }
    }

    /// <summary>
    /// One tile: a 10 m floor with a mesa 15 m above it (walls of 60°), a tower and a booth on its top, a booth halfway up its wall,
    /// and a tyre wall on the floor; the car starts on the floor.
    /// </summary>
    private string TinyMap()
    {
        var p = MapProject.CreateBlank("pintiny", "pintiny", ".rac", 1, 1, 100, 40, 40);
        var (r0, _, c0, _) = p.Terrain.Bounds();
        int ox = c0 * 128, oz = r0 * 128;
        for (int z = 0; z < 128; z++)
            for (int x = 0; x < 128; x++)
            {
                double d = Math.Max(Math.Abs(x - 64), Math.Abs(z - 64));   // samples from the mesa's middle
                double h = 100 + 150 * Math.Clamp((14 - d) / 2.0, 0, 1);    // 15 m over two samples: 56°
                p.Terrain.SetHeight(ox + x, oz + z, (int)h);
            }
        float W(int s) => s * 5f;
        p.Mission.Objects.Add(MapObject.Spawn(new Vector3(W(ox + 20), 10, W(oz + 20))));
        p.Mission.Objects.Add(MapObject.Create("bhwtowr2", 2, new Vector3(W(ox + 60), 25, W(oz + 62))));
        p.Mission.Objects.Add(MapObject.Create("bcstrak6", 2, new Vector3(W(ox + 68), 25, W(oz + 66)), 0.7f));
        p.Mission.Objects.Add(MapObject.Create("bcstrak6", 2, new Vector3(W(ox + 64) + 2, 20, W(oz + 79))));   // on the wall
        p.Mission.Objects.Add(MapObject.Create("aitire1", 3, new Vector3(W(ox + 30), 10, W(oz + 40)), 1.2f));
        return p.SaveTo(_dir).First(f => f.EndsWith(".rac", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Two tiles at the grid's west edge, as the player's arena: a 10 m floor, a 15 m ridge across it whose sides rise 5 m in a sample
    /// (45°: no car climbs it) with ticket booths, a gate and a tower along its top, a 10 m bank along the map's north edge with booths
    /// in its last cells, and a tyre wall on the floor below the ridge; the cars start on the floor.
    /// </summary>
    private string ArenaMap()
    {
        var p = MapProject.CreateBlank("pinarena", "pinarena", ".rac", 2, 1, 50, 75, 0);
        var (r0, _, c0, _) = p.Terrain.Bounds();
        int ox = c0 * 128, oz = r0 * 128;
        for (int z = 0; z < 128; z++)
            for (int x = 0; x < 256; x++)
            {
                int h = 100;
                if (z >= 86 && z <= 90 && x >= 70 && x <= 150) h = 150;            // the ridge
                else if (z >= 85 && z <= 91 && x >= 69 && x <= 151) h = 100;      // one sample from floor to top
                if (z >= 124) h = 100 + (z - 123) * 25;                            // the bank: 2.5 m a sample, to 20 m at the edge
                if ((x * 7 + z * 3) % 11 == 0 && z < 80) h += 3;                   // a little roughness on the floor
                p.Terrain.SetHeight(ox + x, oz + z, h);
            }
        float W(int s) => s * 5f;
        p.Mission.Objects.Add(MapObject.Spawn(new Vector3(W(ox + 100), 10, W(oz + 60))));
        p.Mission.Objects.Add(MapObject.Spawn(new Vector3(W(ox + 130), 10, W(oz + 60)), team: 1));
        for (int i = 0; i < 4; i++)
            p.Mission.Objects.Add(MapObject.Create(i % 2 == 0 ? "bcstrak6" : "bcstrak7", 2, new Vector3(W(ox + 100 + i * 8) + 2.5f, 15, W(oz + 89) + 0.3f)));
        p.Mission.Objects.Add(MapObject.Create("bcstrak4", 2, new Vector3(W(ox + 92), 15, W(oz + 88))));
        p.Mission.Objects.Add(MapObject.Create("bcstrak5", 2, new Vector3(W(ox + 80), 15, W(oz + 88)), 0.2f));
        p.Mission.Objects.Add(MapObject.Create("bhwtowr2", 2, new Vector3(W(ox + 140), 15, W(oz + 87))));
        p.Mission.Objects.Add(MapObject.Create("aitire1", 3, new Vector3(W(ox + 110), 10, W(oz + 84))));
        // in the last cells along the north edge, and at the west end of the grid
        p.Mission.Objects.Add(MapObject.Create("bcstrak1", 2, new Vector3(W(ox + 170), 20, W(oz + 126))));
        p.Mission.Objects.Add(MapObject.Create("bcstrak1", 2, new Vector3(W(ox + 20), 20, W(oz + 126) + 2)));
        p.Mission.Objects.Add(MapObject.Create("bcstrak6", 2, new Vector3(W(ox + 60), 20, W(oz + 127) + 2.5f)));   // the very last cell: half of it off the map
        p.Mission.Objects.Add(MapObject.Create("bcstrak7", 2, new Vector3(W(ox) + 3, 10, W(oz + 10))));
        return p.SaveTo(_dir).First(f => f.EndsWith(".rac", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<(string Label, MissionWorld World)> Maps()
    {
        var vfs = TestInstall.Vfs;
        yield return ("one-tile map, mesa", Load(vfs, TinyMap()));
        yield return ("two-tile arena, stands on a ridge", Load(vfs, ArenaMap()));
        foreach (var f in Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
            yield return (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), Load(vfs, f));
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } nd && Directory.Exists(nd))
        {
            var ni = new GameInstall(nd);
            var nv = new AssetVfs(ni);
            foreach (var f in Directory.EnumerateFiles(ni.MissionDir!).Where(MapFileSet.IsStockMission).OrderBy(x => x))
                yield return ("nitro " + Path.GetFileName(f).ToLowerInvariant(), Load(nv, f));
        }
    }

    /// <summary>
    /// Under every placed object's ground-standing parts (points a metre apart over each outline, and each object's own origin) and on
    /// every cell a car can reach (its corners and middle, the map's last cells included, where the ground runs on into the outside
    /// ground), the modern look's surface is the simulation's ground within <see cref="Near"/>.
    /// </summary>
    [InstallFact]
    public void TheModernGroundIsTheSimulationsUnderObjectsAndWhereCarsDrive()
    {
        var problems = new List<string>();
        foreach (var (label, w) in Maps())
        {
            if (w.Terrain.Huge) continue;
            var pins = w.Footprints();
            var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w), w.Outside, pins);
            var ground = new Ground(w);
            double worstObj = 0;
            string worstAt = "";
            int points = 0, bad = 0;
            void Check(double x, double z, string what, ref double worst)
            {
                var p = surf.Point(x / 5, z / 5, out _);
                double d = p.Y - ground.Height(x, z);
                // a held point is drawn where it is: no relief moves it sideways
                double side = Math.Max(Math.Abs(p.X - x), Math.Abs(p.Z - z));
                if (Math.Abs(d) > worst) { worst = Math.Abs(d); worstAt = $" at {x:0},{z:0}"; }
                if ((Math.Abs(d) > Near || side > 1e-3) && bad++ < 12)
                    problems.Add($"{label}: {what} at {x:0.0},{z:0.0}: drawn {p.Y:0.00} where the simulation has {ground.Height(x, z):0.00}" + (side > 1e-3 ? $", moved {side:0.00} m sideways" : ""));
            }
            foreach (var b in pins)
            {
                var u = b.AxisU.LengthSquared() < 1e-8f ? Vector2.UnitX : Vector2.Normalize(b.AxisU);
                var v = new Vector2(-u.Y, u.X);
                int nu = (int)MathF.Ceiling(b.HalfU), nv = (int)MathF.Ceiling(b.HalfV);
                for (int i = -nu; i <= nu; i++)
                    for (int j = -nv; j <= nv; j++)
                    {
                        var q = b.Centre + u * Math.Clamp(i, -b.HalfU, b.HalfU) + v * Math.Clamp(j, -b.HalfV, b.HalfV);
                        Check(q.X, q.Y, "an object's outline", ref worstObj);
                        points++;
                    }
            }
            foreach (var p in w.Placements.Concat(w.LinePlacements))
                if (p.Object.ObjectType != 1 && p.Definition is not null)
                {
                    Check(p.Object.Position.X, p.Object.Position.Z, p.Object.Name, ref worstObj);
                    points++;
                    // and the object itself stands there, as both looks draw and the simulation's colliders have it
                    var at = w.GroundedPosition(p);
                    double drawn = surf.Point(at.X / 5, at.Z / 5, out _).Y;
                    if (Math.Abs(at.Y - drawn) > Near && bad++ < 12)
                        problems.Add($"{label}: {p.Object.Name} at {at.X:0.0},{at.Z:0.0} stands at {at.Y:0.00} on ground drawn at {drawn:0.00}");
                }
            // every drivable cell's corner and middle (the corners of its neighbours are theirs), a row of cells per job
            var (r0, r1, c0, c1) = w.Terrain.Bounds();
            int z0 = r0 * 128 - 1, rows = (r1 - r0 + 1) * 128 + 1;
            var rowWorst = new double[rows];
            var rowCells = new int[rows];
            var off = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, rows, r =>
            {
                int sz = z0 + r;
                for (int sx = c0 * 128 - 1; sx < (c1 + 1) * 128; sx++)
                {
                    if (!surf.Open(sx, sz)) continue;
                    rowCells[r]++;
                    foreach (var (a, c) in new[] { (0.0, 0.0), (0.5, 0.5), (1.0, 1.0) })
                    {
                        double x = (sx + a) * 5, z = (sz + c) * 5;
                        double d = Math.Abs(surf.Point(x / 5, z / 5, out _).Y - ground.Height(x, z));
                        rowWorst[r] = Math.Max(rowWorst[r], d);
                        if (d > Near) off.Add($"{label}: drivable ground at {x:0.0},{z:0.0}: drawn {d:0.00} m off the simulation's");
                    }
                }
            });
            problems.AddRange(off.Take(12));
            log.WriteLine($"{label}: {pins.Count} outlines, {points} points under objects (worst {worstObj:0.000} m{worstAt}); {rowCells.Sum()} drivable cells (worst {rowWorst.Max():0.000} m)");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The synthetic maps' objects stand where the test means them to: on the ridge and the mesa, cells no car reaches.</summary>
    [InstallFact]
    public void TheSyntheticMapsPutObjectsOnGroundNoCarReaches()
    {
        foreach (var path in new[] { TinyMap(), ArenaMap() })
        {
            var w = Load(TestInstall.Vfs, path);
            var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w), w.Outside);
            var placed = w.Placements.Where(p => p.Object.ObjectType != 1).ToList();
            Assert.All(placed, p => Assert.NotNull(p.Definition));
            int off = placed.Count(p => !surf.Open((int)Math.Floor(p.Object.Position.X / 5), (int)Math.Floor(p.Object.Position.Z / 5)));
            log.WriteLine($"{Path.GetFileName(path)}: {off} of {placed.Count} objects on ground no car reaches");
            Assert.True(off >= placed.Count / 2, $"{Path.GetFileName(path)}: only {off} of {placed.Count} objects off the drivable ground");
        }
    }
}
