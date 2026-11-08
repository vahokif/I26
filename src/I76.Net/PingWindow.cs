namespace I76.Net;

/// <summary>
/// The ping shown in the score window and the HUD: the lowest of the transport's last round-trip samples (one a
/// second). Under packet loss the transport's sample reads far above the link's round trip (measured: a 60 ms link
/// with 10 % loss read a median of 155 ms and up to 624); the lowest of the window reads the link.
/// </summary>
public sealed class PingWindow
{
    /// <summary>Samples kept: about ten seconds at the transport's one ping a second.</summary>
    public const int Size = 10;

    private readonly int[] _samples = new int[Size];
    private int _count, _next;

    public void Add(int ms)
    {
        _samples[_next] = ms;
        _next = (_next + 1) % Size;
        if (_count < Size) _count++;
    }

    /// <summary>The lowest sample of the window, ms (0 before the first).</summary>
    public int Low
    {
        get
        {
            if (_count == 0) return 0;
            int low = int.MaxValue;
            for (int i = 0; i < _count; i++) low = Math.Min(low, _samples[i]);
            return low;
        }
    }
}
