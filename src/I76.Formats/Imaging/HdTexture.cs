namespace I76.Formats.Imaging;

/// <summary>
/// An upscaled replacement for a world, car or cockpit texture, from a player's pack (none ships with the port): a file named
/// by the member's stem ("pp11ftf1.dds", "zsy_.png") at a whole multiple (2x, 3x, 4x, ...) of the original's size, the same both
/// ways. A .dds is block-compressed with its mipmaps, in the texture's stored row order and already keyed (alpha 0 or 255) and
/// bled, and goes to the GPU as it is; a picture (.png, .webp, .jpg) is the right way up and is fitted on load (<see cref="Fit"/>).
/// </summary>
public static class HdTexture
{
    /// <summary>The extensions looked for, in order.</summary>
    public static readonly string[] Extensions = [".dds", ".png", ".webp", ".jpg"];

    /// <summary>The folder of a pack in the user folder: tex-world, or tex-world-4x and so on for a pack of one size.</summary>
    public const string Folder = "tex-world";

    /// <summary>The sizes whose folders are looked for, largest first.</summary>
    public static readonly int[] Sizes = [8, 4, 3, 2];

    public static string FolderOf(int size) => $"{Folder}-{size}x";

    /// <summary>
    /// The folders read, in order: every tex-world-Nx present, largest first, so a partial pack (an 8x of only the cockpit and
    /// the cars) is read over a full one and a texture it lacks comes from the next size down; else the plain tex-world; else
    /// none. <paramref name="exists"/> takes a folder name.
    /// </summary>
    public static string[] Folders(Func<string, bool> exists)
    {
        var sized = Sizes.Select(FolderOf).Where(exists).ToArray();
        return sized.Length > 0 ? sized : exists(Folder) ? [Folder] : [];
    }

    /// <summary>The pack paths that can hold the member's upscale, in the order tried.</summary>
    public static IEnumerable<string> Candidates(string member)
    {
        var stem = Path.GetFileNameWithoutExtension(member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..]).ToLowerInvariant();
        return Extensions.Select(e => stem + e);
    }

    /// <summary>The picture's scale over the original: a whole multiple (1, 2, 3, 4, ...), the same both ways; 0 otherwise.</summary>
    public static int ScaleOf(int w, int h, int ow, int oh)
    {
        if (ow <= 0 || oh <= 0 || w % ow != 0 || h % oh != 0) return 0;
        int s = w / ow;
        return s == h / oh && s > 0 ? s : 0;
    }

    /// <summary>
    /// A picture's RGBA8 (rows top-down, as decoded) in the texture's stored order: rows flipped when <paramref name="bottomUp"/>
    /// (a .map or .m16 keeps a DIB's order; a .vqm is top-down), alpha 0 under one half and 255 above, or, when the picture is
    /// opaque throughout, the original's keyed texels (alpha 0 in <paramref name="original"/>, in stored order) each covering a
    /// <paramref name="scale"/>-square block. A keyed texel takes the colour of the original's texel under it (the original bled
    /// under its key), so filtering and mipmaps pull no key colour into the edges whatever the pack put there.
    /// </summary>
    public static byte[] Fit(byte[] rgba, int w, int h, int scale, byte[] original, int ow, int oh, bool bottomUp)
    {
        var o = new byte[w * h * 4];
        bool anyAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 255) { anyAlpha = true; break; }
        for (int y = 0; y < h; y++)
        {
            int sy = bottomUp ? h - 1 - y : y;
            Buffer.BlockCopy(rgba, sy * w * 4, o, y * w * 4, w * 4);
            int orow = (y / scale) * ow;
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                int oi = (orow + x / scale) * 4;
                bool keyed = anyAlpha ? o[i + 3] < 128 : original[oi + 3] == 0;
                if (keyed) { o[i] = original[oi]; o[i + 1] = original[oi + 1]; o[i + 2] = original[oi + 2]; o[i + 3] = 0; }
                else o[i + 3] = 255;
            }
        }
        return o;
    }
}
