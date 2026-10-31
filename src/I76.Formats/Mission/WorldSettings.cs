using System.Numerics;

namespace I76.Formats.Mission;

/// <summary>
/// WDEF's WRLD chunk, as the game reads it. Offsets are
/// from the start of the payload.
/// </summary>
public sealed record WorldSettings(
    uint CdTrack,          // +0x00
    string Briefing,       // +0x04 .smk: played before the mission
    string Outro,          // +0x11 .smk: played after a win
    string Palette,        // +0x1e .act
    string Luminance,      // +0x2b .lum
    string Translucency,   // +0x38 .tbl
    string Text,           // +0x45 .npt
    string Sky,            // +0x52, the dome's texture
    string Scrounge,       // +0x5f .sdf
    string Ground,         // +0x6c ground family (name[4] must be '1'; levels 2..6)
    string Overview,       // +0x79, the map screen
    string Horizon,        // +0x86 .hzd
    int Hour,              // +0x93: time of day, picks the light preset
    byte[] Unk_0x97,       // +0x97..0x136: 8 x 20 bytes
    int Unk_0x137)         // +0x137 as a float (600 in every campaign mission)
{
    /// <summary>WREV 8's WRLD (the 1997 release, I76EDIT's .lvl files): no horizon field, so everything from +0x86 is 13 bytes earlier.</summary>
    public const int OldLayoutShift = 13;

    /// <summary>
    /// A melee arena's name, the 16 bytes at WRLD chunk start + 0x143 = payload + 0x13b (the shell's mission-name reader callback);
    /// null if the chunk is short. A WREV 8 level keeps it 13 bytes earlier, at payload + 0x12e,
    /// where its author put it; the GOG base game reads +0x13b regardless and so shows such a level's name blank or cut
    /// off: the port reads it where it is (docs/deviations.md).
    /// </summary>
    public static string? ArenaName(MissionFile msn)
    {
        int at = msn.WorldRevision() == 8 ? 0x13b - OldLayoutShift : 0x13b;
        foreach (var c in msn.File.Chunks.Where(c => c.Tag == "WDEF"))
        {
            var body = msn.Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in MissionFile.SubChunks(body))
                if (sc.Tag == "WRLD" && sc.PayloadSize >= at + 16)
                    return Bin.CStr(body.AsSpan(sc.PayloadOffset + at, 16));
        }
        return null;
    }

    /// <summary>
    /// Writes a look over the WRLD of a mission read into memory (the map editor's preview: what the world builds from and
    /// what <see cref="Read"/> and <see cref="MissionFile.WdefRefs"/> say follow it; the file is not touched). False if there is no WRLD.
    /// </summary>
    public static bool Patch(MissionFile msn, MapLook look)
    {
        uint rev = msn.WorldRevision();
        foreach (var c in msn.File.Chunks.Where(c => c.Tag == "WDEF"))
        {
            var body = msn.Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in MissionFile.SubChunks(body))
                if (sc.Tag == "WRLD") { look.WriteTo(msn.Data, rev, c.PayloadOffset + sc.PayloadOffset); return true; }
        }
        return false;
    }

    /// <summary>
    /// The WRLD settings. A WREV 8 level (no horizon) is read in its own layout, as the 1997 engine read it: horizon empty,
    /// the hour at +0x86, the light table at +0x8a, the last word at +0x12a. The 1998 engines read every WRLD in the WREV 9
    /// layout and so misread such a level's hour, lights and horizon (docs/deviations.md).
    /// </summary>
    public static WorldSettings? Read(MissionFile msn)
    {
        bool old = msn.WorldRevision() == 8;
        int k = old ? OldLayoutShift : 0;
        foreach (var c in msn.File.Chunks.Where(c => c.Tag == "WDEF"))
        {
            var body = msn.Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in MissionFile.SubChunks(body))
            {
                if (sc.Tag != "WRLD" || sc.PayloadSize < 0x13b - k) continue;
                var p = body.AsSpan(sc.PayloadOffset, sc.PayloadSize).ToArray();
                string S(int o) => Bin.CStr(p, o, 13);
                return new WorldSettings(Bin.U32(p, 0), S(0x04), S(0x11), S(0x1e), S(0x2b), S(0x38), S(0x45), S(0x52), S(0x5f),
                    S(0x6c), S(0x79), old ? "" : S(0x86), Bin.I32(p, 0x93 - k), p[(0x97 - k)..(0x137 - k)], Bin.I32(p, 0x137 - k));
            }
        }
        return null;
    }
}

/// <summary>One of the engine's eight times of day (a table of 20-byte rows).</summary>
public sealed record LightPreset(int Index, int Type, string Sprite, float ZenithAngle, float Intensity, float Ambient)
{
    /// <summary>Type 1 is sunlight; anything else is night, with the moon and the dynamic (headlight) lights.</summary>
    public bool IsDay => Type == 1;

    /// <summary>
    /// The light vector, pointing toward the light: the original takes row 1
    /// of a rotation about +Z by the zenith angle, (sin a, cos a, 0).
    /// </summary>
    public Vector3 Direction => new(MathF.Sin(ZenithAngle), MathF.Cos(ZenithAngle), 0);
}

/// <summary>The engine's lighting constants (and the preset table).</summary>
public static class Lighting
{
    public static readonly LightPreset[] Presets =
    [
        new(0, 0, "nk_1mon1.map", BitConverter.Int32BitsToSingle(0x3f060a92), BitConverter.Int32BitsToSingle(0x3dc00000), 0),
        new(1, 0, "nk_1mon1.map", BitConverter.Int32BitsToSingle(0x3f9c61aa), BitConverter.Int32BitsToSingle(0x3d000000), 0),
        new(2, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3fa78d36), BitConverter.Int32BitsToSingle(0x3f200000), 0),
        new(3, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3f75be0b), 1.0f, 0),
        new(4, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3e32b8c2), 1.0f, 0),
        new(5, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3f490fda), 1.0f, 0),
        new(6, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3f9c61aa), 1.0f, 0),
        new(7, 1, "nk_1sun1.map", BitConverter.Int32BitsToSingle(0x3fa78d36), 1.0f, 0),
    ];

    /// <summary>The view-depth fade both terrain and objects apply: clamp(z*A + B, 0.75, 1).</summary>
    public static readonly float FadeA = BitConverter.Int32BitsToSingle(0x39dad16a);
    public static readonly float FadeB = BitConverter.Int32BitsToSingle(0x3f3fe4a6);

    /// <summary>Preset = ((hour + 2) % 24) * 8 / 24, in 3-hour bins.</summary>
    public static LightPreset ForHour(int hour) => Presets[(((hour + 2) % 24 + 24) % 24) * 8 / 24];

    /// <summary>
    /// The horizon ring's foot relative to the eye, in units at the ring: the ring is centred on the
    /// eye in x and z and stands at game height -((6000 - 0.8F)/F)·camY, F the far clip, so its foot is
    /// (6000 + 0.2F)/F·camY below the eye: 10.2·camY at F = 600.
    /// </summary>
    public static float HorizonFoot(float camY, float far = 600f) => -((HorizonList.Radius - 0.8f * far) / far) * camY - camY;

    /// <summary>
    /// Game height of the cone from the eye through the horizon ring's foot, at horizontal distance
    /// <paramref name="rho"/> from the eye: camY + HorizonFoot(camY)·rho/6000. A 3D ring whose base lies on this cone
    /// and whose features stand h·rho/6000 high where a panel is h high shows the panels' skyline: a point at
    /// rho on the cone, plus h·rho/6000, is at elevation atan((h + HorizonFoot)/6000), the panel's.
    /// </summary>
    public static float HorizonCone(float camY, float rho, float far = 600f) => camY + HorizonFoot(camY, far) * rho / HorizonList.Radius;
}

/// <summary>
/// A car's night light (kind 1, a spot; the hdlt_msk object at VLOC 38 carries it, pointing along the
/// car's nose), as the M04 light table holds it at run time: range 20 (1/range² = 0.0025) and exponent 18.
/// </summary>
public static class NightSpot
{
    public const float Range = 20f, Exponent = 18f;
    public const float K = 1f / (Range * Range);

    /// <summary>The cone, (1 + cos)/2 to the exponent (0 at or under 0.001), cos measured from the nose to <paramref name="toPoint"/> normalised by |d| + 0.5.</summary>
    private static float Cone(Vector3 toPoint, Vector3 forward, float exponent)
    {
        float s = (1f + Vector3.Dot(toPoint, forward) / (toPoint.Length() + 0.5f)) * 0.5f;
        return s > 0.001f ? MathF.Pow(s, exponent) : 0f;
    }

    private static float Attenuation(float d2) => 1f / (K * d2 + 1f);

    /// <summary>How strongly the light reaches an object's origin, which picks an object's four lights.</summary>
    public static float Strength(Vector3 origin, Vector3 pos, Vector3 forward) =>
        Cone(origin - pos, forward, Exponent) * Attenuation(Vector3.DistanceSquared(origin, pos));

    /// <summary>
    /// (night presets): what the light adds to a vertex at <paramref name="v"/> with the outward normal
    /// <paramref name="normal"/>: cone · attenuation · the face's turn toward the nose (−forward·n, nothing if not
    /// positive; not toward the light) · 1.25.
    /// </summary>
    public static float AtVertex(Vector3 v, Vector3 normal, Vector3 pos, Vector3 forward)
    {
        float facing = -Vector3.Dot(forward, normal);
        if (facing <= 0) return 0;
        return Cone(v - pos, forward, Exponent) * Attenuation(Vector3.DistanceSquared(v, pos)) * facing * 1.25f;
    }

    /// <summary>
    /// The direction toward the light a drop shadow cast at <paramref name="c"/> takes, or null for the
    /// moon. A light under 25 above the ground is taken from 25 above it, its cone (exponent over
    /// four) still measured from where it is; the strongest wins if it beats 1.5 times the preset's intensity, and the
    /// shadow aims 15 times its cone below the raised light. <paramref name="ground"/> is the ground's height at x, z.
    /// </summary>
    public static Vector3? ShadowLight(Vector3 c, IEnumerable<(Vector3 Pos, Vector3 Forward)> lights, Func<float, float, float> ground, float intensity)
    {
        float best = 0, bestCone = 0;
        Vector3 bestPos = default;
        foreach (var (pos, forward) in lights)
        {
            var raised = pos;
            if (pos.Y - ground(pos.X, pos.Z) < 25f) raised.Y += 25f;
            var toLight = pos - c;   // the original measures the cone to the light itself, the raise taken back out
            float s = (Vector3.Dot(-forward, toLight) / MathF.Max(toLight.Length(), 1e-6f) + 1f) * 0.5f;
            float cone = s > 0.001f ? MathF.Pow(s, Exponent * 0.25f) : 0f;
            float strength = cone * Attenuation(Vector3.DistanceSquared(raised, c));
            if (strength > best) (best, bestCone, bestPos) = (strength, cone, raised);
        }
        if (best <= intensity * 1.5f) return null;
        return Vector3.Normalize(bestPos - c - new Vector3(0, bestCone * 15f, 0));
    }
}

/// <summary>A .hzd: the horizon ring's 16 panel textures, one name per line.</summary>
public static class HorizonList
{
    public const int Panels = 16;
    public const float Radius = 6000f;

    public static string[]? Parse(byte[]? d)
    {
        if (d is null) return null;
        var names = Bin.Latin1(d).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return names.Length >= Panels ? names[..Panels] : null;
    }

    /// <summary>The top ring's height: half a panel's chord.</summary>
    public static float PanelHeight => 0.5f * 2 * Radius * MathF.Sin(MathF.PI / Panels);
}
