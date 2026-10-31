using System.Numerics;
using I76.Editor.Objects;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Collision;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The gallery map (<see cref="Gallery"/>: every breakable or drive-on structure of an install on flat ground, a cell each): each
/// structure is left standing, destroyed the player's way (rounds from a car's guns on every node that is left, from four sides) and
/// destroyed whole (the script's destroy-object action's DestroyStructure), and a car is driven at it along its natural ramp direction in each state.
/// The table of what that gives, per structure, goes to the log and to I76_GALLERY_REPORT (a folder) when set; a structure the player's
/// way leaves in a state the whole destroy does not is a suspect for "the ramp does not work".
/// </summary>
public class GalleryTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-gallery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static string? Dir(string env) => Environment.GetEnvironmentVariable(env) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>What a drive at a structure came to.</summary>
    public enum Outcome { Flat, Ramp, Wall }

    public sealed record Run(Outcome Outcome, float Climbed, bool Airborne, float Reached);

    public sealed record Row(string Name, uint Type, string Centre, float Radius, int Nodes, string Left, string Dir, string Intact, string Player, string Whole, string Profile, bool Suspect);

    [Fact]
    public void TheBaseGamesStructures() => Check("I76_INSTALL", nitro: false);

    [Fact]
    public void TheNitroPacksStructures() => Check("I76_NITRO_INSTALL", nitro: true);

    private void Check(string env, bool nitro)
    {
        if (Dir(env) is not { } real) return;
        string addon = Path.Combine(_root, "install", "addon");
        Directory.CreateDirectory(addon);
        var dir = Path.Combine(_root, "install");
        foreach (var e in Directory.EnumerateFileSystemEntries(real))
        {
            string n = Path.GetFileName(e);
            if (n.Equals("addon", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(e)) Directory.CreateSymbolicLink(Path.Combine(dir, n), e); else File.CreateSymbolicLink(Path.Combine(dir, n), e);
        }
        // the real install's own addon maps (the Nitro Pack's stock arenas) are not wanted in the way; the gallery is built into the scratch one
        var install = new GameInstall(dir);
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var realInstall = new GameInstall(real);
        var (files, placed) = Gallery.Build(realInstall, new ModelAssembler(new AssetVfs(realInstall)), addon, Gallery.DefaultStem, Path.Combine(_root, "cache"), nitro);
        string mission = files.First(f => GameInstall.IsMissionFile(f));

        var rows = new List<Row>();
        var intact = Setup(vfs, asm, mission, out var sIntact);
        var player = Setup(vfs, asm, mission, out var sPlayer);
        var whole = Setup(vfs, asm, mission, out var sWhole);
        foreach (var p in placed)
        {
            StaticObject? Find(MissionSetup s) => s.Statics.Objects.Where(o => o.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && o.Type == p.Type)
                .OrderBy(o => Vector2.Distance(new(o.Centre.X, o.Centre.Z), new(p.At.X, p.At.Z))).FirstOrDefault(o => Vector2.Distance(new(o.Centre.X, o.Centre.Z), new(p.At.X, p.At.Z)) < 80);
            if (Find(intact) is not { } oi || Find(player) is not { } op || Find(whole) is not { } ow) { rows.Add(new Row(p.Name, p.Type, "", 0, 0, "", "", "not built", "", "", "", false)); continue; }
            var dirV = Direction(ow);
            var (runI, profI) = DriveAt(intact, sIntact, oi, dirV);
            // the player's way
            string left = ShootApart(player, sPlayer, op);
            var (runP, profP) = DriveAt(player, sPlayer, op, dirV);
            // whole
            whole.Damage.DestroyStructure(ow);
            var (runW, profW) = DriveAt(whole, sWhole, ow, dirV);
            bool same = runP.Outcome == runW.Outcome && MathF.Abs(runP.Climbed - runW.Climbed) < 0.5f && op.Surfaces.Count == ow.Surfaces.Count && op.Polys.Count == ow.Polys.Count;
            rows.Add(new Row(p.Name, p.Type, $"{ow.Centre.X:0.0},{ow.Centre.Z:0.0}", ow.Radius, op.Nodes.Count, left, $"{dirV.X:0.##},{dirV.Z:0.##}", Fmt(runI), Fmt(runP), Fmt(runW), $"{profP} | {profW}", !same));
        }
        var lines = rows.Select(r => string.Join('\t', r.Name, $"0x{r.Type:x}", r.Centre, $"{r.Radius:0.0}", r.Nodes, r.Left, r.Dir, r.Intact, r.Player, r.Whole, r.Suspect ? "DIFFERS" : "", r.Profile));
        string table = "name\ttype\tcentre\tradius\tnodes\tunshot\tdir\tintact\tplayer\twhole\tflag\tprofile(player|whole)\n" + string.Join('\n', lines);
        if (Environment.GetEnvironmentVariable("I76_GALLERY_REPORT") is { Length: > 0 } rd)
        {
            Directory.CreateDirectory(rd);
            File.WriteAllText(Path.Combine(rd, nitro ? "gallery-nitro.tsv" : "gallery-base.tsv"), table);
        }
        log.WriteLine(table);
        // the point of it: whatever the whole destroy turns into a ramp, the player's guns do too once they have shot every node
        var lost = rows.Where(r => r.Whole.StartsWith("Ramp") && !r.Intact.StartsWith("Ramp") && r.Left.Length == 0 && !r.Player.StartsWith("Ramp")).Select(r => r.Name).ToList();
        Assert.True(lost.Count == 0, "destroyed whole these are ramps, shot apart they are not: " + string.Join(", ", lost));
        Assert.True(rows.Count(r => r.Whole.StartsWith("Ramp") && !r.Intact.StartsWith("Ramp")) >= 3, "the water tower, the hopper and the lean-to are ramps once destroyed");
        log.WriteLine($"{rows.Count} structures; ramps whole: {rows.Count(r => r.Whole.StartsWith("Ramp"))}, ramps the player's way: {rows.Count(r => r.Player.StartsWith("Ramp"))}; differing: {rows.Count(r => r.Suspect)}; guns left nodes standing: {rows.Count(r => r.Left.Length > 0)}");
    }

    private static string Fmt(Run r) => $"{r.Outcome} {r.Climbed:0.0}{(r.Airborne ? " air" : "")}";

    private static MissionSetup Setup(AssetVfs vfs, ModelAssembler asm, string mission, out SimSession s)
    {
        var setup = MissionSetup.Build(vfs, new MissionWorld(vfs, asm, mission), asm, script: false);
        var v = setup.World.Player!;
        setup.World.Vehicles.RemoveAll(x => x != v);
        setup.Combat.UnlimitedAmmo = true;
        s = new SimSession(setup);
        s.AddDriver(v);
        // the engine cranks for a few seconds first, and the guns wait for it
        for (int i = 0; i < 20 * 30; i++) s.Tick([new CarInput { Commands = CarCommands.Skip }]);
        return setup;
    }

    private static bool Steep(Poly p) => p.N.Y is > 0.3f and < 0.97f;

    /// <summary>The approach direction in the world: up the highest sloping polygon of what it leaves (or of what it is), else north.</summary>
    private static Vector3 Direction(StaticObject o)
    {
        var pool = (o.Remains?.Surfaces ?? []).Concat(o.Remains?.Polys ?? []).Concat(o.Surfaces).Where(Steep).ToList();
        if (pool.Count == 0) return Vector3.UnitZ;
        var top = pool.OrderByDescending(p => p.V.Max(v => v.Y)).First();
        var h = new Vector2(-top.N.X, -top.N.Z);
        return h.LengthSquared() < 1e-6f ? Vector3.UnitZ : new Vector3(Vector2.Normalize(h).X, 0, Vector2.Normalize(h).Y);
    }

    private static void Place(MissionSetup s, Vehicle car, Vector3 at, Vector3 fwd, float spd = 0)
    {
        car.Pos = new Double3(at.X, s.Ground.BareHeight(at.X, at.Z) + 0.6f, at.Z);
        car.Rot = new Rows(Vector3.Cross(Vector3.UnitY, fwd), Vector3.UnitY, fwd);
        car.Vel = fwd * spd;
    }

    /// <summary>Rounds from the car's guns, 3 s a try, at the first node left from each side in turn (at the root if it has none), until none is left or 16 tries; the nodes still standing, named.</summary>
    private static string ShootApart(MissionSetup s, SimSession sim, StaticObject o)
    {
        var car = s.World.Player!;
        var hp = car.Health!;
        var sides = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0) };
        bool Done() => o.Nodes.Count == 0 ? o.Destroyed : !o.Nodes.Any(n => !n.Destroyed && n.Damageable);
        for (int t = 0; t < 16 && !Done(); t++)
        {
            var node = o.Nodes.FirstOrDefault(n => !n.Destroyed && n.Damageable);
            var aim = node?.Base ?? o.Centre;
            var side = sides[t % 4];
            var from = new Vector3(aim.X, 0, aim.Z) - side * (o.Radius + 12);
            for (int i = 0; i < 60; i++)
            {
                Array.Copy(hp.ArmourMax, hp.Armour, 4); Array.Copy(hp.ChassisMax, hp.Chassis, 4);
                Place(s, car, from, side);
                sim.Tick([new CarInput { Buttons = CarButtons.Fire | CarButtons.Hardpoint1 | CarButtons.Hardpoint2 }]);
                s.Combat.Events.Clear(); s.Damage.Events.Clear();
            }
        }
        return string.Join('+', o.Nodes.Where(n => !n.Destroyed && n.Damageable).Select(n => n.Name));
    }

    /// <summary>A car at 15 u/s from <c>radius + 40</c> m out, steered at the structure's centre along <paramref name="dir"/>; and the collider's top along that line.</summary>
    private static (Run, string) DriveAt(MissionSetup s, SimSession sim, StaticObject o, Vector3 dir)
    {
        var car = s.World.Player!;
        var hp = car.Health!;
        var c = new Vector3(o.Centre.X, 0, o.Centre.Z);
        float reach = MathF.Min(o.Radius + 40, 80);
        var start = c - dir * reach;
        Place(s, car, start, dir);
        float climbed = 0, along = 0; bool air = false;
        for (int i = 0; i < 20 * 8; i++)
        {
            Array.Copy(hp.ArmourMax, hp.Armour, 4); Array.Copy(hp.ChassisMax, hp.Chassis, 4);
            float err = MathF.Atan2(dir.X, dir.Z) - MathF.Atan2(car.Rot.Forward.X, car.Rot.Forward.Z);
            while (err > MathF.PI) err -= 2 * MathF.PI;
            while (err < -MathF.PI) err += 2 * MathF.PI;
            sim.Tick([new CarInput
            {
                Buttons = CarButtons.Analog,
                Steer = (short)(System.Math.Clamp(-err * 1.5f, -1f, 1f) * 32767),
                Throttle = (short)(System.Math.Clamp((15 - car.Speed) * 0.3f, -1f, 1f) * 32767),
            }]);
            var p = car.Pos.ToVector3();
            float above = p.Y - s.Ground.BareHeight(p.X, p.Z);
            climbed = MathF.Max(climbed, above);
            along = MathF.Max(along, Vector3.Dot(new Vector3(p.X, 0, p.Z) - start, dir));
            if ((car.Flags & VehicleFlags.Airborne) != 0 && above > 0.8f) air = true;
        }
        // the collider's top every 10 m along the lane, over the bare ground
        var prof = new List<string>();
        for (float d = -reach / 2; d <= reach / 2; d += 10)
        {
            var q = c + dir * d;
            float g = s.Ground.BareHeight(q.X, q.Z);
            prof.Add($"{s.Ground.Query(q.X, g + 30, q.Z).Height - g:0.0}");
        }
        // through is six metres past the centre; a car that climbed more than a metre and got through, or left the ground over it, rode a ramp
        var o2 = along >= reach + 6 ? (climbed > 1.0f || air ? Outcome.Ramp : Outcome.Flat) : Outcome.Wall;
        return (new Run(o2, climbed, air, along), string.Join(' ', prof));
    }
}
