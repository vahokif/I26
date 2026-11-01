using System.Numerics;
using System.Runtime.CompilerServices;
using I76.Formats.Audio;
using I76.Formats.Vfs;
using I76.Sim.Missions;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The police siren. setSiren plays the siren (aps2.wav) on the car it names, as the 1997 CD build did on any car: started with
/// no voice settings of its own, so its GAS0 header decides, and that is mode 1: it loops until setSiren 0 (or the car's wreck).
/// The AI's honk plays the car's own engsnd.dat horn file the same way (the 1998 build), and only the stock police car's row 10
/// has the siren for its horn: such a car that honks at a car in its way has its siren on from then on. Every other horn is mode
/// 0 and plays once. I26 played setSiren's sound once, and as the car's horn: the trip's and the scenarios' own police variants
/// carry a plain engine row, so theirs was an ordinary beep.
/// </summary>
public class SirenTests(ITestOutputHelper output)
{
    private static IEnumerable<(string Env, AssetVfs Vfs)> Installs() =>
        new[] { "I76_INSTALL", "I76_NITRO_INSTALL" }
            .Select(e => (e, Dir: Environment.GetEnvironmentVariable(e)))
            .Where(x => x.Dir is { Length: > 0 } && Directory.Exists(x.Dir))
            .Select(x => (x.e, new AssetVfs(new GameInstall(x.Dir!))));

    private sealed class Flat : I76.Sim.Terrain.IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public I76.Sim.Terrain.Surface Surface(int id) => new(2f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void Only_the_police_cars_horn_loops()
    {
        foreach (var (env, vfs) in Installs())
        {
            Assert.True(SoundFiles.Loops(vfs, VehicleFactory.SirenSound), env);
            Assert.Equal((60, 2), SoundFiles.Limits(vfs, VehicleFactory.SirenSound));
            for (int horn = 1; horn <= 6; horn++) Assert.False(SoundFiles.Loops(vfs, $"vhorn{horn}.wav"), $"{env} vhorn{horn}");
            Assert.False(SoundFiles.Loops(vfs, "nosuchsound.wav"));
            // the stock police cars (T07's Sheriffs among them) are engsnd.dat row 10, whose horn is the siren; the trip's and the
            // scenarios' own police variants (T07, T10, T14, S06) carry a plain engine row: their honk is an ordinary horn, once
            foreach (var (vcf, siren) in new[] { ("vcpolic1.vcf", true), ("vcpolic3.vcf", true), ("t07cp01.vcf", false), ("t10cp01.vcf", false),
                         ("t14cp01.vcf", false), ("s06vc01.vcf", false), ("vppirna1.vcf", false), ("vxbus1.vcf", false) })
            {
                if (vfs.TryRead(vcf) is null) continue;
                var v = VehicleFactory.Create(vfs, vcf);
                output.WriteLine($"{env} {vcf}: engine {v.EngineNumber}, horn {VehicleFactory.HornSound(vfs, v.EngineNumber)}, loops {v.HornLoops}");
                Assert.Equal(siren, v.HornLoops);
            }
        }
    }

    [Fact]
    public void A_police_cars_honk_turns_its_siren_on_until_setSiren_turns_it_off()
    {
        var world = new SimWorld(new Flat(), new I76.Sim.Rng.CrtRand(1));
        var police = new Entity("vcpolic1", new byte[8], 1, 0) { Vehicle = new Vehicle { HornLoops = true } };
        var pickup = new Entity("vppirna1", new byte[8], 1, 0) { Vehicle = new Vehicle() };
        var host = new ScriptHost(world, [police, pickup], (Objectives)RuntimeHelpers.GetUninitializedObject(typeof(Objectives)), _ => null);

        host.Ptable.Horn(pickup);   // a plain horn: a one-shot the view plays, nothing kept on
        Assert.Equal([pickup.Vehicle!], world.Step.Honks);
        Assert.False(pickup.Siren);

        world.Step.Honks.Clear();
        host.Ptable.Horn(police);   // the siren loops from the honk on: the same voice setSiren starts
        Assert.True(police.Siren);
        Assert.Empty(world.Step.Honks);

        host.SetSiren(police, false);   // setSiren 0 stops the voice of that file on the car, however it was started
        Assert.False(police.Siren);
        host.SetSiren(police, true);
        host.Ptable.Horn(police);
        Assert.True(police.Siren);
    }
}
