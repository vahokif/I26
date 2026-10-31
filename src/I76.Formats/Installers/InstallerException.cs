namespace I76.Formats.Installers;

public enum InstallerError
{
    /// <summary>Not an Inno Setup installer at all.</summary>
    NotInno,
    /// <summary>A GOG Galaxy downloader (a small online stub with no game data).</summary>
    GalaxyStub,
    /// <summary>An Inno Setup data version this reader doesn't parse.</summary>
    UnsupportedVersion,
    /// <summary>A password-protected (encrypted) installer.</summary>
    Encrypted,
    /// <summary>A compression method this reader doesn't decode.</summary>
    UnsupportedCompression,
    /// <summary>The setup-N.bin parts the installer's data lives in are not next to the .exe.</summary>
    MissingParts,
    /// <summary>A file's data doesn't match the checksum stored for it.</summary>
    ChecksumMismatch,
    /// <summary>The headers or the data are damaged (CRC errors, bad structures, truncation).</summary>
    Corrupt,
    /// <summary>The installer holds neither Interstate '76 nor the Nitro Pack.</summary>
    NotTheGame,
    /// <summary>The target folder's disk has too little free space.</summary>
    NoSpace,
    /// <summary>The target folder already holds a game install that I26 didn't extract (it is never overwritten).</summary>
    TargetInUse,
}

/// <summary>Why an installer can't be read or extracted, with a message meant for the player.</summary>
public sealed class InstallerException(InstallerError kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public InstallerError Kind { get; } = kind;
}
