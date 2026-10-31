namespace I76.Sim.Rng;

/// <summary>
/// The MSVC C runtime's rand(): one LCG shared by everything that calls it.
/// The original game draws physics jitter, effects and AI decisions from this single
/// stream, so the order of calls is part of the behaviour.
///
/// In oracle mode the values come from a recorded trace instead, so a replay
/// can stay in step with the original while consumers are still being
/// identified.
/// </summary>
public sealed class CrtRand
{
    public const int RandMax = 0x7FFF;

    public uint Seed { get; set; }
    private readonly Queue<int>? _oracle;

    public CrtRand(uint seed = 1) => Seed = seed;

    private CrtRand(IEnumerable<int> oracle) => _oracle = new Queue<int>(oracle);

    public static CrtRand Oracle(IEnumerable<int> values) => new(values);

    public bool IsOracle => _oracle is not null;

    /// <summary>srand: the CRT's default seed is 1.</summary>
    public void SRand(uint seed) => Seed = seed;

    public int Next()
    {
        if (_oracle is not null)
            return _oracle.Count > 0 ? _oracle.Dequeue() : throw new InvalidOperationException("rand oracle exhausted");
        Seed = unchecked(Seed * 214013u + 2531011u);
        return (int)((Seed >> 16) & RandMax);
    }
}
