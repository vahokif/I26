using I76.Formats.Assembly;
using I76.Formats.Export;
using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Index 255 is a key only for a keyed draw (a cut-out or translucent face). An opaque face shows it as a colour, black
/// past the end of the texture's palette: the school bus's stripes and lettering, most of the black limo's paint. The
/// keyed copy's texels are bled from their neighbours, which turned the bus's stripes orange and haloed the limo's
/// black in the chrome and glass beside it.
/// </summary>
public class OpaqueKeyTexelTests
{
    [Fact]
    public void AnOpaqueDrawKeepsIndex255AKeyedDrawBleedsIt()
    {
        // orange above and below a row of index 255, a palette of two (255 past its end)
        var t = new IndexedTexture(4, 3, [0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0], [0xF3C0, 0xFFFF]);
        var opaque = TextureReader.DrawRgba(t, null, keyed: false);
        var keyed = TextureReader.DrawRgba(t, null, keyed: true);
        var orange = TextureReader.DecodeRgb565(0xF3C0);
        for (int x = 0; x < 4; x++)
        {
            int i = (4 + x) * 4;
            Assert.Equal((0, 0, 0), (opaque[i], opaque[i + 1], opaque[i + 2]));
            Assert.Equal((orange.R, orange.G, orange.B), (keyed[i], keyed[i + 1], keyed[i + 2]));
            Assert.Equal(0, keyed[i + 3]);
        }
        // the rest alike
        for (int i = 0; i < 16; i++) Assert.Equal(opaque[i], keyed[i]);
    }

    [InstallFact]
    public void TheSchoolBusDrawsItsStripesBlack()
    {
        var vfs = TestInstall.Vfs;
        var res = new TextureResolver(vfs);
        var m = new ModelAssembler(vfs).Assemble("nobus1.vcf")!;
        int black = 0, faces = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in m.Parts)
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Texture.Length == 0 || f.Unk_0x20 == 0 || ModelObj.ModeOf(f) != ModelObj.FaceMode.Opaque) continue;
                if (res.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is not { } member || !seen.Add(member)) continue;
                if (res.Load(member) is not { } tex) continue;
                faces++;
                var rgba = TextureReader.DrawRgba(tex, null, keyed: false);
                for (int i = 0; i < tex.Indices.Length; i++)
                    if (tex.Indices[i] == TextureReader.TransparentIndex && (tex.Palette?.Length ?? 256) <= 255)
                    {
                        Assert.Equal(0, rgba[i * 4] | rgba[i * 4 + 1] | rgba[i * 4 + 2]);
                        black++;
                    }
            }
        Assert.True(faces > 5, $"{faces} opaque textures");
        // the sides' stripes and lettering, the bumpers, the band under the windscreen: thousands of texels
        Assert.True(black > 2000, $"{black} black texels");
    }
}
