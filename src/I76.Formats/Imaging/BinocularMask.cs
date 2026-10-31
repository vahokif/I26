namespace I76.Formats.Imaging;

/// <summary>
/// The binoculars' two-circle frame (bmask*.msk), which the game lays over the whole view while the binoculars are up
/// (drawn from the game loop after the frame is rendered and before the text panes, while a mission is running
/// and the reticle switch is on). One file per display mode, named by a 13-byte table indexed
/// by the mode, each the size of that mode's screen: a run-length list of little-endian u16 words, the low 15 bits a run
/// of pixels and bit 15 set for a masked run, row-major from the top, ended by a 0 word or the last pixel. The game fills
/// the masked pixels with black (index 255, or the 16-bit black) and leaves the rest.
/// </summary>
public static class BinocularMask
{
    /// <summary>The files the stock data holds, with their sizes: 640x480 (mode 5), 800x600, 1024x768, the reduced
    /// 480x360 viewport (mode 6) and 320x200 (modes 7, 8). The table also names bmask128 and bmask160 for 1280x1024
    /// and 1600x1200, which the data does not have.</summary>
    public static readonly (string File, int Width, int Height)[] Files =
        [("bmask320.msk", 320, 200), ("bmask460.msk", 480, 360), ("bmask640.msk", 640, 480), ("bmask800.msk", 800, 600), ("bmask102.msk", 1024, 768)];

    /// <summary>The mask as one byte a pixel, rows from the top: 1 masked (drawn black), 0 clear.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        var px = new byte[width * height];
        int at = 0;
        for (int i = 0; i + 1 < data.Length && at < px.Length; i += 2)
        {
            int w = data[i] | data[i + 1] << 8;
            if (w == 0) break;
            int end = System.Math.Min(px.Length, at + (w & 0x7fff));
            if ((w & 0x8000) != 0) px.AsSpan(at, end - at).Fill(1);
            at = end;
        }
        return px;
    }
}
