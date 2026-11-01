using I76.Formats.Mission;
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
    /// How the horizon sinks with height. The original's panels stand on a ring whose foot is 10.2 x the eye's game height below it, so
    /// a mission on high ground loses them (the stock maps lie between 4 and 66 m on average, but for Hot Pursuit's M04 and M15, which
    /// have none to see). A map a player made or edited can lie anywhere (a real place's ground is 100 to 400 m above its lowest
    /// point; the editor raises ground freely), and its mountains must not vanish or slide in and out as the car climbs and drops. For
    /// such a map the height that sinks the horizon is counted from the map's lowest ground and held to <see cref="CustomCap"/>;
    /// a stock map keeps the original's formula (<see cref="Low"/> 0, no cap).
    /// </summary>
    public readonly record struct Level(float Low, float Cap)
    {
        /// <summary>The original's rule: the eye's height above sea level, no limit.</summary>
        public static readonly Level Original = new(0f, float.MaxValue);
        /// <summary>The height that sinks the horizon for an eye at <paramref name="camY"/>.</summary>
        public float Eye(float camY) => Math.Min(camY - Low, Cap);
    }

    /// <summary>The most a custom map's horizon sinks for: the eye counts as no higher than this over the map's lowest ground.</summary>
    public const float CustomCap = 70f;

    /// <summary>The sinking rule of a mission: the original's for a stock map, <see cref="CustomCap"/> over its lowest ground for any other.</summary>
    public static Level LevelOf(MissionWorld world) =>
        MapFileSet.IsStockMission(world.Mission.Path) ? Level.Original : new Level((float)world.LowestGroundHeight(), CustomCap);

    /// <summary>A panel foot's height relative to the eye, at the ring's radius (the sky's shader: the formula for ground the clip reaches).</summary>
    public static float Foot(Level level, float camY, float far = Clip) =>
        level.Low - (HorizonList.Radius - 0.8f * far) / far * level.Eye(camY) - camY;

    /// <summary>A panel foot standing on the ground's end <paramref name="de"/> away at height <paramref name="endHeight"/>, relative to the eye (the shader's).</summary>
    public static float EndFoot(float camY, float endHeight, float de) => -(camY - endHeight) * (HorizonList.Radius + 0.2f * de) / de;

    /// <summary>The game height of the modern ring's reference eye: over the map's centre at its average ground, three metres up, sunk by <see cref="Level"/>.</summary>
    public static float RingEyeY(MissionWorld world)
    {
        var level = LevelOf(world);
        return level.Low + level.Eye((float)world.AverageGroundHeight() + 3f);
    }

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
                else if (inside)
                {
                    // the end lies between the last sample inside and this one: found to a metre, and the ground's height taken there
                    // (a 25 m step left it anywhere in the last 25 m, which on a slope moves the height by metres from one bearing to the next)
                    double lo = t - Step, hi = t;
                    while (hi - lo > 1)
                    {
                        double mid = (lo + hi) / 2;
                        if (world.Terrain.Sample(MissionWorld.ToSample(x + dx * mid), MissionWorld.ToSample(z + dz * mid)) is not null) lo = mid; else hi = mid;
                    }
                    d = (float)lo; h = (float)world.GroundHeight(x + dx * lo, z + dz * lo); goto found;
                }
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
