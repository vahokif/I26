using I76.Formats.Audio;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>
/// The police car's horn/siren aps2.wav is looped whole while the horn key is held (GAS0 mode 1 in its own header; DirectSound's
/// looping flag), and its last sample runs straight into its first: no click at the seam (GitHub #48 reported one; the Godot mix
/// of it was recorded too, 44.1 and 48 kHz, and is continuous). The other horns' files do jump at their seams in the data itself.
/// </summary>
public class HornSeamTests
{
    [Fact]
    public void The_siren_loops_without_a_jump_at_its_seam()
    {
        foreach (var env in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) continue;
            var pcm = SoundFiles.Read(new AssetVfs(new GameInstall(dir)), "aps2.wav");
            Assert.NotNull(pcm);
            Assert.Equal(8, pcm!.Bits);
            var d = pcm.Data;
            int seam = System.Math.Abs(d[0] - d[^1]);
            int typical = Enumerable.Range(1, d.Length - 1).Select(i => System.Math.Abs(d[i] - d[i - 1])).Max();
            Assert.True(seam <= 2, $"seam jump {seam}");
            Assert.True(typical >= 20);   // a siren at -18 dB whose own steps are far larger than the seam's
        }
    }
}
