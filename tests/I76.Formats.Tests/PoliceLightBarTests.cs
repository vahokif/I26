using I76.Formats.Assembly;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class PoliceLightBarTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// The modern look's SirenBar (the game layer) builds on these facts: every Sheriff car has a *SIRN part whose lit faces
    /// name an animated .tmt of three frames at 4 a second, which the texture resolver finds.
    /// </summary>
    [Theory]
    [InlineData("vcpolic1.vcf")]
    [InlineData("vcpolic2.vcf")]
    [InlineData("vcpolic3.vcf")]
    public void TheSheriffCarsBarIsAnAnimatedThreeFrameTexture(string car)
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var m = new ModelAssembler(vfs) { SkipParts = [] }.Assemble(car)!;
        var bar = m.Parts.Where(p => p.NodeName.Length >= 8 && p.NodeName[4..8].Equals("SIRN", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(bar);
        var lit = bar.SelectMany(p => p.Mesh.Faces).Where(f => f.Texture.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(lit);
        var anim = new TextureResolver(vfs).AnimatedFace(lit[0].Texture);
        Assert.NotNull(anim);
        Assert.Equal(3, anim!.Value.Frames.Length);
        Assert.Equal(4f, anim.Value.Fps);
    }
}
