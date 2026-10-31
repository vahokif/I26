using I76.Formats.Assembly;
using I76.Formats.Vfs;

namespace I76.Formats.Mission;

/// <summary>
/// What identifies a map between two installs: the mission file's size and CRC, and its terrain's. The original
/// checks only the mission file: the host's shell takes its CRC into
/// the launch block (+0x6c), the engine sends it in its JO message (+0x44) and a joining shell compares
/// it with its own copy's: "Your mission file %s does not match the host's". The port adds the terrain,
/// which a community map ships beside it.
/// </summary>
public sealed record MapFingerprint(string Mission, uint MissionSize, uint MissionCrc, uint TerrainSize, uint TerrainCrc)
{
    /// <summary>A map's fingerprint in this install, or null if the install has no such mission (or its terrain).</summary>
    public static MapFingerprint? Of(GameInstall install, string mission)
    {
        if (install.FindMission(mission) is not { } path) return null;
        var data = File.ReadAllBytes(path);
        var msn = new MissionFile(path, data);
        if (MissionWorld.FindTerrain(install, msn) is not { } ter) return null;
        var t = File.ReadAllBytes(ter);
        return new MapFingerprint(Path.GetFileName(path), (uint)data.Length, Crc(data), (uint)t.Length, Crc(t));
    }

    /// <summary>
    /// The shell's checksum: CRC-32 with polynomial 0x04C11DB7, most significant bit first, register
    /// 0xFFFFFFFF, result inverted (the CRC-32/BZIP2 parameters).
    /// </summary>
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xffffffff;
        foreach (byte b in data) c = (c << 8) ^ Table[(c >> 24) ^ b];
        return ~c;
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04c11db7 : c << 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>
    /// Why a client cannot play the host's map, or null if it can: the map is missing from its install, or its copy
    /// differs from the host's (the mission file or the terrain).
    /// </summary>
    public static string? Mismatch(GameInstall install, string mission, uint missionSize, uint missionCrc, uint terrainSize, uint terrainCrc)
    {
        var mine = Of(install, mission);
        if (mine is null)
            return install.FindMission(mission) is null
                ? $"The host's map {mission} is not in your game's miss16 or addon folder."
                : $"The host's map {mission} is in your game, but not its terrain.";
        if (mine.MissionSize != missionSize || mine.MissionCrc != missionCrc)
            return $"Your mission file {mission} does not match the host's.";
        if (mine.TerrainSize != terrainSize || mine.TerrainCrc != terrainCrc)
            return $"Your terrain for {mission} does not match the host's.";
        return null;
    }
}
