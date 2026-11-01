namespace I76.Formats.Audio;

/// <summary>A random track after another that is never the one just played (the garage's boombox, the map editor's music).</summary>
public static class TrackShuffle
{
    /// <summary>An index in 0..count-1 other than <paramref name="last"/> (unless it is the only one); -1 when there are no tracks.</summary>
    public static int Next(int count, int last, Random rand)
    {
        if (count <= 0) return -1;
        if (count == 1) return 0;
        // one of the others, uniformly: draw from count-1 and step over the last
        int k = rand.Next(count - 1);
        return last >= 0 && last < count && k >= last ? k + 1 : k;
    }
}
