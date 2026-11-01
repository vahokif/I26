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

    /// <summary>The widest notch in a custom map's foot that <see cref="Follow"/> closes over, in bins either side of its deepest: a gap a bin or few wide in a rim (a corner the rim leaves open, a cut a few metres wide).</summary>
    public const int NotchBins = 2;

    /// <summary>How much lower than all round it (metres at the ring's radius, about two degrees) a notch's foot must be to be closed over: shallower dips, the rim's own crests, are followed (closing them would stand the panels above the ground's real skyline there).</summary>
    public const float NotchDepth = 200f;

    /// <summary>How far round each bearing a custom map's foot is smoothed, in bins either side: the sky's shader weighs the bins within this many of a direction by a tent (<see cref="FollowFoot"/>).</summary>
    public const int SmoothBins = 3;

    /// <summary>
    /// The ends a custom map's panels stand on, from the ends <see cref="Measure"/> found from an eye at <paramref name="camY"/>, bin by
    /// bin and laid along the bin's own bearing. A map's ground can end a bin or few lower than all round it (a gap in a canyon's rim
    /// a few metres wide, a corner it leaves open): the foot there (<see cref="CustomFoot"/>) drops by hundreds of metres for a degree,
    /// and every column of the panels' texture, which the foot shifts, dropped with it; the old shader's lowest of taps half a degree
    /// apart then ran up and down that dip, sawing the panels into a zigzag. Here a notch at most <see cref="NotchBins"/> bins either
    /// side of its bottom and deeper than <see cref="NotchDepth"/> is closed over (the end of the highest of its sides is taken; the
    /// rim the gap is cut in, standing in front of it, hides what lies beyond), and then the lowest end within <see cref="SmoothBins"/>
    /// is taken, so that the shader's smoothing over that many bins never stands the panels above the ground's end.
    /// </summary>
    public static (float[] Distance, float[] Height) Follow(Level level, float camY, float[] dist, float[] height)
    {
        int n = dist.Length;
        var foot = new float[n];
        for (int i = 0; i < n; i++) foot[i] = CustomFoot(level, camY, height[i], dist[i]);
        // a closing over NotchBins (the lowest of the highest), kept only where it fills a notch deeper than NotchDepth;
        // then the lowest within SmoothBins (each as the index of the end it takes)
        var up = new int[n];
        for (int i = 0; i < n; i++)
        {
            int best = i;
            for (int k = -NotchBins; k <= NotchBins; k++) { int j = ((i + k) % n + n) % n; if (foot[j] > foot[best]) best = j; }
            up[i] = best;
        }
        var fill = new int[n];
        for (int i = 0; i < n; i++)
        {
            int best = up[i];
            for (int k = -NotchBins; k <= NotchBins; k++) { int j = up[((i + k) % n + n) % n]; if (foot[j] < foot[best]) best = j; }
            fill[i] = foot[best] - foot[i] > NotchDepth ? best : i;
        }
        var d = new float[n];
        var h = new float[n];
        for (int i = 0; i < n; i++)
        {
            int best = fill[i];
            for (int k = -SmoothBins; k <= SmoothBins; k++) { int j = fill[((i + k) % n + n) % n]; if (foot[j] < foot[best]) best = j; }
            d[i] = dist[best];
            h[i] = height[best];
        }
        return (d, h);
    }

    /// <summary>
    /// A custom map's panel foot along <paramref name="azimuth"/> (radians from game +X toward +Z) for an eye at (<paramref name="x"/>,
    /// <paramref name="camY"/>, <paramref name="z"/>), from the ends <see cref="Follow"/> made as points (game x, z and height; bin i
    /// along (i + 0.5)/Bins · 360°): the bins' feet from the eye as it is now, weighed by a tent <see cref="SmoothBins"/> bins either
    /// side of the azimuth (the sky's shader). The weights move smoothly with the azimuth over fixed bins, so the foot is a smooth
    /// function of the bearing: the panels' texture, mapped from it, keeps its columns together.
    /// </summary>
    public static float FollowFoot(ReadOnlySpan<(float X, float Z, float H)> ends, Level level, double x, double z, float camY, double azimuth)
    {
        int n = ends.Length;
        double f = azimuth / (2 * Math.PI) * n - 0.5;
        int j0 = (int)Math.Floor(f);
        float t = (float)(f - j0), sum = 0, weights = 0;
        for (int k = 1 - SmoothBins; k <= SmoothBins; k++)
        {
            float w = SmoothBins - Math.Abs(k - t);
            if (w <= 0) continue;
            var e = ends[((j0 + k) % n + n) % n];
            float de = MathF.Sqrt((float)((e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z)));
            sum += w * CustomFoot(level, camY, e.H, de);
            weights += w;
        }
        return sum / weights;
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
