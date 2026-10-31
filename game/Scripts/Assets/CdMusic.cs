namespace I76.Game.Assets;

/// <summary>
/// The CD's music as the GOG install ships it, music/N.mp3: the sixteen audio
/// tracks of disc 2, twelve under their own number and four in one cycle
/// (matched against a rip of
/// the disc): disc track 3 is 16.mp3, 15 is 17.mp3, 16 is 15.mp3, 17 is 3.mp3.
/// </summary>
public static class CdMusic
{
    public static int FileFor(int discTrack) => discTrack switch { 3 => 16, 15 => 17, 16 => 15, 17 => 3, _ => discTrack };

    /// <summary>The install's MP3 for a disc track, or null (track 1 is the data track).</summary>
    public static string? Path(I76.Formats.Vfs.GameInstall install, int discTrack)
    {
        if (discTrack < 2 || install.FindDir("music") is not { } dir) return null;
        // the Nitro Pack's track N is music/N.mp3, no remap
        var name = $"{(install.IsNitro ? discTrack : FileFor(discTrack))}.mp3";
        return System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The disc's last audio track the install has a file for (17 for I76's disc 2, 11 for Nitro's).</summary>
    public static int LastTrack(I76.Formats.Vfs.GameInstall install)
    {
        for (int t = 40; t > 2; t--) if (Path(install, t) is not null) return t;
        return 2;
    }
}
