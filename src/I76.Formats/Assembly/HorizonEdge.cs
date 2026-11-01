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

    /// <summary>The sinking rule of a mission: the original's for a stock map, <see cref="CustomCap"/> over its lowest ground for any other (and for any map when <paramref name="capped"/>: the map editor's free camera, hundreds of metres up, would sink the original's rule's panels out of sight).</summary>
    public static Level LevelOf(MissionWorld world, bool capped = false) =>
        !capped && MapFileSet.IsStockMission(world.Mission.Path) ? Level.Original : new Level((float)world.LowestGroundHeight(), CustomCap);

    /// <summary>A panel foot's height relative to the eye, at the ring's radius (the sky's shader: the formula for ground the clip reaches).</summary>
    public static float Foot(Level level, float camY, float far = Clip) =>
        level.Low - (HorizonList.Radius - 0.8f * far) / far * level.Eye(camY) - camY;

    /// <summary>A panel foot standing on the ground's end <paramref name="de"/> away at height <paramref name="endHeight"/>, relative to the eye (the shader's).</summary>
    public static float EndFoot(float camY, float endHeight, float de)
    {
        de = Math.Max(de, MinEnd);
        return -(camY - endHeight) * (HorizonList.Radius + 0.2f * de) / de;
    }

    /// <summary>
    /// The nearest end a custom map's foot stands on (<see cref="Measure"/> with <c>inClip</c>). A player's map can be a canyon or a
    /// strip whose ground ends a few hundred metres off on one side and kilometres off along the other: the ends inside the original's
    /// 600 m clip are real ends, and a foot that follows them (the higher of the end's and the sinking rule's, so an end just under the eye never sinks the
    /// panels out of sight, and not an end a few metres off: <see cref="CustomFoot"/>) is what keeps the panels' foot from jumping between the two as the bearing, or the car, moves.
    /// </summary>
    public const float MinEnd = 10f;

    /// <summary>How far a custom map's end at <paramref name="de"/> metres is followed: not at all inside 100 m, wholly past 250 m (smoothstep). A rim a few metres off swings a foot standing on it by degrees with the slightest slope, and the ground in front hides what stands below its edge.</summary>
    public static float EndWeight(float de)
    {
        float t = Math.Clamp((de - 100f) / 150f, 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>A custom map's panel foot for an end <paramref name="de"/> away at <paramref name="endHeight"/>: the higher of the end's and the sinking rule's, blended in by <see cref="EndWeight"/> (the sky's shader).</summary>
    public static float CustomFoot(Level level, float camY, float endHeight, float de)
    {
        float sink = Foot(level, camY);
        return sink + (Math.Max(EndFoot(camY, endHeight, de), sink) - sink) * EndWeight(de);
    }

    /// <summary>How far the eye moves before the ends are measured again: the table is read by the azimuth from the eye as it is now, so the farther the eye has moved from where it measured, the more a near end's bearing is off (a stock map's ends lie beyond the clip, where 60 m is a fraction of a degree).</summary>
    public const float Remeasure = 60f;
    /// <summary>The same for a custom map, whose ends may lie a few hundred metres off (a canyon's walls): a measure takes a few milliseconds.</summary>
    public const float RemeasureCustom = 15f;

    /// <summary>
    /// The most the modern ring of a custom map sinks for: one fixed ring serves a whole hilly map, and the panels it stands in for
    /// sink with each eye (a car in the valleys, where most of the driving is, sees them well above the hills; the average ground's
    /// <see cref="CustomCap"/> would put the ring below the hills' own skyline there). Low enough that the ring's peaks stand over
    /// the hills of a real-world map, a few metres of the cap's worth too high on the hilltops, where the panels would sink more.
    /// </summary>
    public const float RingCap = 30f;

    /// <summary>The game height of the modern ring's reference eye: over the map's centre at its average ground, three metres up, sunk by <see cref="Level"/> (a custom map's: at most <see cref="RingCap"/> over its lowest ground).</summary>
    public static float RingEyeY(MissionWorld world)
    {
        var level = LevelOf(world);
        if (level.Cap < float.MaxValue) level = level with { Cap = Math.Min(level.Cap, RingCap) };
        return level.Low + level.Eye((float)world.AverageGroundHeight() + 3f);
    }

    /// <summary>
    /// For each bearing (bin i looks along azimuth (i + 0.5)/Bins · 360° from game +X toward +Z) from the eye (x, z): the
    /// distance to where the ground's data ends (at least <see cref="Clip"/>, at most <see cref="MaxDistance"/>) and the
    /// ground's height there. A bearing that meets no end keeps the maximum. For a stock map an end inside the clip is held at the clip (the
    /// original's rule takes it); with <paramref name="inClip"/> (a custom map) it is the end itself, at least <see cref="MinEnd"/> away.
    /// </summary>
    public static (float[] Distance, float[] Height) Measure(MissionWorld world, double x, double z, bool inClip = false)
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
            dist[i] = Math.Max(d, inClip ? MinEnd : Clip);
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
