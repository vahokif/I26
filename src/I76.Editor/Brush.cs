namespace I76.Editor;

public enum BrushKind
{
    /// <summary>Lifts the ground under the brush.</summary>
    Raise,
    /// <summary>Lowers it.</summary>
    Lower,
    /// <summary>Evens the ground out: each sample moves toward the mean of the ones around it.</summary>
    Smooth,
    /// <summary>Pulls the ground toward the height under the first click of the stroke.</summary>
    Flatten,
    /// <summary>Pulls the ground toward a chosen height (typed, or taken from the ground with the eyedropper).</summary>
    Level,
    /// <summary>Roughens the ground with noise.</summary>
    Noise,
    /// <summary>Paints the ground's surface type (a hard-edged disc: the type is discrete, so strength and falloff do not apply).</summary>
    Surface,
}

/// <summary>How the brush weakens from its centre to its rim.</summary>
public enum Falloff
{
    /// <summary>Cosine: full at the centre, nothing at the rim, no crease at either.</summary>
    Smooth,
    /// <summary>A straight cone.</summary>
    Linear,
    /// <summary>A sharp spike: the weight falls with the square of the distance from the rim.</summary>
    Sharp,
    /// <summary>Full over the inner half, then a smooth edge: for terraces and plateaus.</summary>
    Plateau,
}

/// <summary>A brush's settings. Radius is in world units (metres), strength 0..1.</summary>
public sealed class BrushSettings
{
    public const float MinRadius = 5f, MaxRadius = 400f;
    public const float MinStrength = 0.02f, MaxStrength = 1f;

    public BrushKind Kind { get; set; } = BrushKind.Raise;
    public Falloff Falloff { get; set; } = Falloff.Smooth;
    public float Radius { get; set; } = 40f;
    public float Strength { get; set; } = 0.3f;
    /// <summary>The height <see cref="BrushKind.Level"/> pulls toward, metres.</summary>
    public float TargetHeight { get; set; } = 20f;
    /// <summary>The surface id <see cref="BrushKind.Surface"/> paints (0..7, <see cref="SurfaceTypes"/>).</summary>
    public int SurfaceId { get; set; } = 5;

    public void ScaleRadius(float factor) => Radius = Math.Clamp(Radius * factor, MinRadius, MaxRadius);
    public void AddStrength(float delta) => Strength = Math.Clamp(Strength + delta, MinStrength, MaxStrength);
}

public static class BrushMath
{
    /// <summary>Metres a sample moves in a second at strength 1 by Raise and Lower (and the base of the other rates).</summary>
    public const double RaiseRate = 24.0;
    /// <summary>Metres per second Noise adds at strength 1.</summary>
    public const double NoiseRate = 12.0;
    /// <summary>Per second, at strength 1 and weight 1, how much of the distance to the target Flatten, Level and Smooth cover (a fraction, capped at 1 a stamp).</summary>
    public const double PullRate = 12.0;

    /// <summary>The brush's weight at <paramref name="t"/> (distance over radius, 0 at the centre): 1 at the centre, 0 at and past the rim.</summary>
    public static double Weight(Falloff f, double t)
    {
        if (t >= 1) return 0;
        if (t <= 0) return 1;
        return f switch
        {
            Falloff.Linear => 1 - t,
            Falloff.Sharp => (1 - t) * (1 - t),
            Falloff.Plateau => t < 0.5 ? 1 : 0.5 * (1 + Math.Cos(Math.PI * (t - 0.5) * 2)),
            _ => 0.5 * (1 + Math.Cos(Math.PI * t)),
        };
    }

    /// <summary>
    /// Deterministic value noise about -1..1 at a sample, two octaves (a feature of about four samples and one of eight), so a
    /// roughened patch has bumps a car feels and the same noise comes back where it is brushed again.
    /// </summary>
    public static double Noise(int sx, int sz)
    {
        static double Hash(int x, int z)
        {
            unchecked
            {
                uint h = (uint)x * 0x8da6b343u ^ (uint)z * 0xd8163841u;
                h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
                return (h & 0xffff) / 32767.5 - 1;
            }
        }
        static double Octave(int sx, int sz, int cell)
        {
            int ix = (int)Math.Floor((double)sx / cell), iz = (int)Math.Floor((double)sz / cell);
            double fx = (double)sx / cell - ix, fz = (double)sz / cell - iz;
            double u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
            double a = Hash(ix, iz), b = Hash(ix + 1, iz), c = Hash(ix, iz + 1), d = Hash(ix + 1, iz + 1);
            return (a + (b - a) * u) + ((c + (d - c) * u) - (a + (b - a) * u)) * v;
        }
        return (Octave(sx, sz, 4) * 0.65 + Octave(sx + 1000, sz - 1000, 8) * 0.35);
    }
}

/// <summary>
/// The ground's surface classes: the .ter sample's bits 13-15 (the sim's SurfaceId), which drive the tyre
/// grip, rolling and bump (WRLD's surface table), the tyre sounds, the ground clutter and the
/// plants. Bit 12 of the sample is the AI's impassable flag and is not part of the type.
/// </summary>
public static class SurfaceTypes
{
    public const int Count = 8;

    /// <summary>A class as the editor lists it: name, what it does (sounds, clutter), and its colour in the overlay (RGB).</summary>
    public sealed record Info(int Id, string Name, string Tip, byte R, byte G, byte B);

    public static readonly Info[] All =
    [
        new(0, "DIRT", "Id 0: dirt (dirt rolling sound, small tufts). Most of the melee arenas and A01", 176, 128, 70),
        new(1, "SAND", "Id 1: sand (sand rolling sound, waist-high bushes). Most of the trip and Nitro maps", 232, 205, 110),
        new(2, "ASPHALT", "Id 2: asphalt road (road skids, silent rolling, no plants). Under asphalt roads", 90, 90, 100),
        new(3, "DIRT ROAD", "Id 3: dirt road (dirt sounds, no plants). Under dirt roads", 140, 85, 45),
        new(4, "PLANKS", "Id 4: plank road (dirt sounds, no plants). Under plank roads and bridges", 150, 110, 60),
        new(5, "GRAVEL", "Id 5: rough ground (gravel sounds, yuccas and cholla). Big patches in S02 and M06", 130, 150, 120),
        new(6, "PARKING LOT", "Id 6: parking lot (road skids, silent rolling). Under asphalt intersections", 70, 130, 190),
        new(7, "DIRT JUNCTION", "Id 7: dirt intersection (dirt sounds). Under dirt intersections", 200, 90, 160),
    ];

    /// <summary>The surface id of a raw sample (the sim's SurfaceId).</summary>
    public static int Of(ushort sample) => sample >> 13 & 7;

    /// <summary>The sample with its surface id set: the height and the impassable flag (bit 12) are kept.</summary>
    public static ushort With(ushort sample, int id) => (ushort)((sample & 0x1fff) | (id & 7) << 13);
}
