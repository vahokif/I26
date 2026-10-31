namespace I76.Formats.Assembly;

/// <summary>
/// A car's damage textures and damage shapes, zone by zone.
///
/// After a hit the original sets the struck zone's look from its level (0 above 75 %, 1 above 50 %,
/// 2 above 25 %, else 3). The texture steps axis 0 of each of the zone's face slots' .TMT
/// (the texture animation axis), so a slot shows that entry of its table; the shape sets the zone node's damage
/// state. Death puts every zone on entry 3 and state 3.
///
/// The slots are a fixed table in the exe (13-byte names, 10 to a zone, with a count per zone), compared
/// with the faces' texture names as written (_stricmp). Only these change; the rest (MD FT, MD TP, MD UN ...) keep entry 0.
/// </summary>
public static class DamageTextures
{
    /// <summary>The slots of zones 0 (front) .. 4 (top). V5 FT TP is the cockpit's hood.</summary>
    public static readonly string[][] ZoneSlots =
    [
        ["V1 FT FT.MAP", "V1 FT LF.MAP", "V1 FT RT.MAP", "V1 FT TP.MAP", "V1 FT UN.MAP", "V5 FT TP.MAP"],
        ["V1 MD LF.MAP"],
        ["V1 MD RT.MAP"],
        ["V1 BK BK.MAP", "V1 BK LF.MAP", "V1 BK RT.MAP", "V1 BK TP.MAP", "V1 BK UN.MAP"],
        ["V1 TP TP.MAP"],
    ];

    /// <summary>The .TMT entry a dead car's slots show (the texture animation axis, 0 to 3).</summary>
    public const int WreckFrame = 3;

    /// <summary>The zone whose damage a face's texture follows, or -1.</summary>
    public static int Zone(string faceTexture)
    {
        for (int z = 0; z < ZoneSlots.Length; z++)
            foreach (var s in ZoneSlots[z])
                if (s.Equals(faceTexture, StringComparison.OrdinalIgnoreCase)) return z;
        return -1;
    }

    /// <summary>A zone's level (0..3, 4 dead) to the .TMT entry its slots show: 0, 1, 1, 2, 3.</summary>
    public static int Frame(int level) => level switch { <= 0 => 0, 1 or 2 => 1, 3 => 2, _ => WreckFrame };

    /// <summary>
    /// A zone's level (0..3) to the condition digit of its parts (state + 1): states 0, 0, 1, 2, so a zone
    /// keeps its intact shape until it is under half. State 3 (level 4) is the dead car's hull (<see cref="ModelAssembler.Wrecked"/>).
    /// </summary>
    public static char Condition(int level) => (char)('1' + Math.Clamp(level - 1, 0, 2));
}
