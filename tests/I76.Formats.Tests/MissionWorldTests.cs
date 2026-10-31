using I76.Formats.Assembly;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

public class MissionWorldTests(ITestOutputHelper log)
{
    /// <summary>
    /// M1 criterion (c): every ODEF placement in every mission is either a
    /// keyword (spawn, regen, empty) or resolves to a definition that
    /// assembles into at least one mesh, and every textured face resolves.
    /// </summary>
    [InstallFact]
    public void EveryPlacementInEveryMissionLoads()
    {
        var vfs = TestInstall.Vfs;
        var asm = new ModelAssembler(vfs);
        var tex = new TextureResolver(vfs);
        var dir = TestInstall.Install.MissionDir!;
        var models = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        int missions = 0, placements = 0, drawn = 0, faces = 0;
        var failures = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)))
        {
            missions++;
            var w = new MissionWorld(vfs, asm, f);
            foreach (var p in w.Placements)
            {
                placements++;
                if (p.Object.IsKeyword || p.Object.Name.Length == 0) continue;
                if (p.Definition is null) { failures.Add($"{w.Mission.Name}: {p.Object.Name} unresolved"); continue; }
                if (!models.TryGetValue(p.Definition, out var m)) models[p.Definition] = m = asm.Assemble(p.Definition);
                if (m is null || m.Parts.Count == 0) { failures.Add($"{w.Mission.Name}: {p.Definition} has no parts"); continue; }
                drawn++;
            }
        }
        foreach (var m in models.Values.OfType<AssembledModel>())
            foreach (var face in m.Parts.SelectMany(p => p.Mesh.Faces).Where(x => x.Texture.Length > 0))
            {
                faces++;
                if (tex.ResolveFace(face.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is null)
                    failures.Add($"{m.Definition}: face texture {face.Texture} unresolved");
            }
        log.WriteLine($"{missions} missions, {placements} placements, {drawn} drawn, {models.Count} definitions, {faces} textured faces");
        Assert.Equal(40, missions);
        Assert.Empty(failures.Distinct());
    }

    [InstallFact]
    public void VehiclesGetWheelsAtTheirWlocSlots()
    {
        var asm = new ModelAssembler(TestInstall.Vfs);
        var m = asm.Assemble("vppirna1.vcf")!;
        // 4 wheels x (2 caps + 16 tread quads)
        Assert.Equal(4 * 18, Wheels.Build(m).Count);
    }
}

public class RoadTests
{
    [Theory]
    [InlineData(2, new int[0])]                 // one quad: dropped, as the original does
    [InlineData(3, new[] { 0, 3 })]
    [InlineData(25, new[] { 0, 25 })]
    [InlineData(84, new[] { 0, 22, 21, 22, 42, 22, 63, 21 })]
    public void SplitsLikeTheEngine(int points, int[] expect)
    {
        var got = I76.Formats.Assembly.Roads.Split(points).SelectMany(p => new[] { p.Start, p.Count }).ToArray();
        Assert.Equal(expect, got);
        // pieces share end points and cover every quad
        var runs = I76.Formats.Assembly.Roads.Split(points).ToList();
        if (runs.Count > 0) Assert.Equal(points - 1, runs.Sum(r => r.Count - 1));
    }
}

public class LightingTests
{
    [Fact]
    public void S01MorningIsTheMeasuredLight()
    {
        // hour 6 -> preset 2; measured L = (0.9659, 0.2588, 0) in s01
        var p = I76.Formats.Mission.Lighting.ForHour(6);
        Assert.Equal(2, p.Index);
        Assert.Equal(0.9659f, p.Direction.X, 3);
        Assert.Equal(0.2588f, p.Direction.Y, 3);
        Assert.Equal(0.000417f, I76.Formats.Mission.Lighting.FadeA, 6);
        Assert.Equal(0.7496f, I76.Formats.Mission.Lighting.FadeB, 4);
    }

    [InstallFact]
    public void EveryMissionHasWorldSettings()
    {
        foreach (var f in Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)))
        {
            var w = I76.Formats.Mission.WorldSettings.Read(new I76.Formats.Mission.MissionFile(f));
            Assert.NotNull(w);
            Assert.InRange(w!.Hour, 0, 23);
            Assert.EndsWith(".act", w.Palette, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public class VehicleFactoryTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [InstallFact]
    public void PiranhaFromItsFiles()
    {
        var v = I76.Sim.Vehicles.VehicleFactory.Create(TestInstall.Vfs, "vppirna1.vcf");
        log.WriteLine($"mass {v.P.Mass} drag {v.P.Drag} wheelbase {v.P.Wheelbase} track {v.P.Track} steer {v.P.SteerGain} pivot {v.P.PivotForward} " +
                      $"engine A {v.Engine.PeakForce} brake {v.P.BrakeStrength} susp {v.P.ChassisGrip} grip {v.FrontLeft.Grip}/{v.RearLeft.Grip} FR {v.FrontRight.Offset} RL {v.RearLeft.Offset}");
        Assert.InRange(v.P.Mass, 400, 5000);
        Assert.InRange(v.P.Wheelbase, 1.5f, 5f);
        Assert.True(v.FrontRight.Offset.Z > 0 && v.RearLeft.Offset.Z < 0, "front wheels ahead of rear");
        Assert.True(v.Engine.PeakForce >= 104440);
    }
}

public class SoundTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [InstallTheory]
    [InlineData("eishp.wav")]
    [InlineData("einp1.wav")]
    [InlineData("vtcoll.wav")]
    [InlineData("vvch2.wav")]
    [InlineData("vvcre2.wav")]
    [InlineData("vvcbb3.wav")]
    [InlineData("vnvcs1.wav")]
    [InlineData("vnvcs3.wav")]
    public void SoundsDecode(string name)
    {
        var pcm = I76.Formats.Audio.SoundFiles.Read(TestInstall.Vfs, name);
        Assert.NotNull(pcm);
        log.WriteLine($"{name}: {pcm!.Channels} ch, {pcm.Rate} Hz, {pcm.Bits} bit, {pcm.Data.Length} bytes");
        Assert.True(pcm.Data.Length > 100);
    }
}

public class FsmProgramTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [InstallFact]
    public void EveryScriptParsesToTheChunkEnd()
    {
        int scripted = 0;
        foreach (var f in Directory.EnumerateFiles(TestInstall.Install.MissionDir!).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)))
        {
            var p = new I76.Formats.Mission.MissionFile(f).ReadFsmProgram();
            if (p is null || p.Code.Length == 0) continue;
            scripted++;
            Assert.True(p.EndsExactly, f);
            Assert.All(p.Threads, t => Assert.InRange(t.StartPc, 0, p.Code.Length - 1));
            log.WriteLine($"{Path.GetFileName(f)}: {p.Threads.Count} threads, {p.Data.Length} data, {p.Code.Length} instructions");
        }
        Assert.Equal(25, scripted);
    }
}
