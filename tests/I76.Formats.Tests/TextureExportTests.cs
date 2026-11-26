using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>TextureExport (`i76 export-textures`): made-up textures, no install.</summary>
public class TextureExportTests
{
    /// <summary>A PNG's RGBA pixels, read back through the editor-free path: the IDAT inflated and unfiltered (filter 0 rows).</summary>
    private static (int W, int H, byte[] Rgba) ReadPng(string path)
    {
        var d = File.ReadAllBytes(path);
        int w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
        using var idat = new MemoryStream();
        for (int at = 8; at < d.Length;)
        {
            int len = (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
            if (System.Text.Encoding.ASCII.GetString(d, at + 4, 4) == "IDAT") idat.Write(d, at + 8, len);
            at += 12 + len;
        }
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream(); z.CopyTo(raw);
        var r = raw.ToArray(); var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) { Assert.Equal(0, r[y * (w * 4 + 1)]); Array.Copy(r, y * (w * 4 + 1) + 1, px, y * w * 4, w * 4); }
        return (w, h, px);
    }

    [Fact]
    public void MapsComeOutUprightKeyedAndListedBottomUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "i76-texexport-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a 2x3 .map, its first stored row (the picture's foot) index 1, then 2, then the key; a 1x1 .vqm; a palette of greys
            var map = new IndexedTexture(2, 3, [1, 1, 2, 2, 255, 255], null);
            var vqm = new IndexedTexture(1, 1, [7], null);
            var act = new byte[Palette.Size];
            for (int i = 0; i < 256; i++) act[i * 3] = act[i * 3 + 1] = act[i * 3 + 2] = (byte)(i * 10 % 256);
            var textures = new Dictionary<string, (string, IndexedTexture)?> { ["zz_1wall"] = ("zz_1wall.map", map), ["aa_1sky"] = ("aa_1sky.vqm", vqm), ["gone"] = null };
            int n = TextureExport.Write(textures.Keys, s => textures[s], new Palette(act), dir);
            Assert.Equal(2, n);
            Assert.Equal(new[] { "zz_1wall" }, File.ReadAllLines(Path.Combine(dir, "bottom-up.txt")));
            var (w, h, px) = ReadPng(Path.Combine(dir, "zz_1wall.png"));
            Assert.Equal((2, 3), (w, h));
            Assert.Equal(0, px[3]);                                        // the top row: the key, transparent
            Assert.Equal(new byte[] { 20, 20, 20, 255 }, px.AsSpan(8, 4).ToArray());   // the middle row: index 2
            Assert.Equal(new byte[] { 10, 10, 10, 255 }, px.AsSpan(16, 4).ToArray());   // the foot: index 1
            Assert.Equal(new byte[] { 70, 70, 70, 255 }, ReadPng(Path.Combine(dir, "aa_1sky.png")).Rgba);   // a .vqm as stored
            Assert.False(File.Exists(Path.Combine(dir, "gone.png")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
