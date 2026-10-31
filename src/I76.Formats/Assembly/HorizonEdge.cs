namespace I76.Formats.Assembly;

/// <summary>
/// Where the drawn ground ends, bearing by bearing, for the 1997 look at the Far draw distance.
///
/// The original's horizon panels stand on a ring whose foot is placed for its 600 m clip: the ground ends at the clip
/// and the panels start there. Drawn farther (Far) the ground runs on beyond 600 m until the map's own data ends, so
/// the panels' foot lies behind it and a strip of bare sky shows between the ground's real end and whatever lies
/// beyond. Moving the foot to that end (its distance and ground height along each bearing) closes the strip.
/// </summary>
public static class HorizonEdge
{
    public const int Bins = 360;
    /// <summary>The original's clip, where the unmoved foot meets the ground.</summary>
    public const float Clip = 600f;
    /// <summary>The farthest end that moves the foot: the ring is at 6000, and its foot formula needs the end well inside it.</summary>
    public const float MaxDistance = 5000f;
    private const float Step = 25f;

    /// <summary>
    /// For each bearing (bin i looks along azimuth (i + 0.5)/Bins · 360° from game +X toward +Z) from the eye (x, z): the
    /// distance to where the ground's data ends (at least <see cref="Clip"/>, at most <see cref="MaxDistance"/>) and the
    /// ground's height there. A bearing that meets no end keeps the maximum.
    /// </summary>
    public static (float[] Distance, float[] Height) Measure(MissionWorld world, double x, double z)
    {
        var dist = new float[Bins];
        var height = new float[Bins];
        for (int i = 0; i < Bins; i++)
        {
            double a = (i + 0.5) / Bins * 2 * Math.PI, dx = Math.Cos(a), dz = Math.Sin(a);
            float d = MaxDistance, h = 0;
            double lastH = world.GroundHeight(x, z);
            bool inside = world.Terrain.Sample(MissionWorld.ToSample(x), MissionWorld.ToSample(z)) is not null;
            for (float t = Step; t <= MaxDistance; t += Step)
            {
                double px = x + dx * t, pz = z + dz * t;
                bool here = world.Terrain.Sample(MissionWorld.ToSample(px), MissionWorld.ToSample(pz)) is not null;
                if (here) { lastH = world.GroundHeight(px, pz); inside = true; }
                else if (inside) { d = t; h = (float)lastH; goto found; }
            }
            h = (float)lastH;
        found:
            dist[i] = Math.Max(d, Clip);
            height[i] = h;
        }
        return (dist, height);
    }

    /// <summary>
    /// The lowest ground within <paramref name="radius"/> of the eye (x, z), sampled every 25 units: the 1997 software
    /// sky's band stands on the line where this height meets the far clip (the 1997 build keeps the least
    /// height of the terrain vertices it drew; 0 with no terrain drawn).
    /// </summary>
    public static float Lowest(MissionWorld world, double x, double z, float radius = Clip)
    {
        float low = float.MaxValue;
        for (float dz = -radius; dz <= radius; dz += Step)
            for (float dx = -radius; dx <= radius; dx += Step)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                double px = x + dx, pz = z + dz;
                if (world.Terrain.Sample(MissionWorld.ToSample(px), MissionWorld.ToSample(pz)) is null) continue;
                low = Math.Min(low, (float)world.GroundHeight(px, pz));
            }
        return low == float.MaxValue ? 0f : low;
    }
}
