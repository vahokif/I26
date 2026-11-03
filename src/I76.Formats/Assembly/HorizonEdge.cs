using I76.Formats.Mission;
namespace I76.Formats.Assembly;

/// <summary>
/// The distant horizon's height. The 1997 look's 16 panels stand as the original's do (<see cref="Lighting.HorizonFoot"/>): on a ring
/// centred on the eye, their foot at game height -((6000 - 0.8F)/F) x the eye's height (F the 600 m clip, whatever the draw distance), so
/// over high ground they sink below the horizon. The modern look's mountain ring is not moved by the eye: it is sunk once for the map, see <see cref="Ring"/>.
/// </summary>
public static class HorizonEdge
{
    /// <summary>The original's clip, F in the panels' foot formula.</summary>
    public const float Clip = 600f;
    private const float Step = 25f;

    /// <summary>The most a custom map's modern ring sinks for, in metres of reference eye over the map's lowest ground (see <see cref="Ring"/>).</summary>
    public const float RingCap = 30f;

    /// <summary>The modern ring's sinking, fixed for a map: the cone through the panels' foot is counted from <paramref name="Low"/> and stands for an eye at game height <paramref name="EyeY"/>.</summary>
    public readonly record struct RingSink(float Low, float EyeY);

    /// <summary>
    /// The one height the modern look's mountain ring stands at for a map, worked out when the map loads and never changed (not by the
    /// eye, the bearing, the time or the map being edited). A stock map takes the original's rule for an eye three metres over its average
    /// ground (what its panels sink to there: low maps show their mountains, M04 and M15's high plains hide them as they hide the panels).
    /// A map a player made or edited can lie anywhere (a real place's ground is 100 to 400 m over its lowest point), so for it the sinking is
    /// counted from the lowest ground and that eye is held to <see cref="RingCap"/> over it: the ring's peaks stand over the horizon on
    /// flat ground at any altitude and over the hills of a rolling map.
    /// </summary>
    public static RingSink Ring(MissionWorld world)
    {
        float avgEye = (float)world.AverageGroundHeight() + 3f;
        if (MapFileSet.IsStockMission(world.Mission.Path)) return new RingSink(0f, avgEye);
        float low = (float)world.LowestGroundHeight();
        return new RingSink(low, low + Math.Min(avgEye - low, RingCap));
    }

    /// <summary>
    /// The lowest ground within <paramref name="radius"/> of the eye (x, z), sampled every 25 units: the 1997 software
    /// sky's band stands on the line where this height meets the far clip (the 1997 build keeps the least
    /// height of the terrain vertices it drew; 0 with no terrain drawn). Off the map's tiles, the outside ground where the map has
    /// one (the 1997 look draws it within the clip).
    /// </summary>
    public static float Lowest(MissionWorld world, double x, double z, float radius = Clip)
    {
        float low = float.MaxValue;
        var outside = world.Outside;
        for (float dz = -radius; dz <= radius; dz += Step)
            for (float dx = -radius; dx <= radius; dx += Step)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                double px = x + dx, pz = z + dz;
                int sx = MissionWorld.ToSample(px), sz = MissionWorld.ToSample(pz);
                if (world.Terrain.Sample(sx, sz) is not null) low = Math.Min(low, (float)world.GroundHeight(px, pz));
                else if (outside is not null) low = Math.Min(low, outside.Raw(sx, sz) * (float)MissionWorld.HeightScale);
            }
        return low == float.MaxValue ? 0f : low;
    }
}
