namespace I76.Formats.Vegetation;

/// <summary>
/// The vegetation's own random stream (SplitMix64): seeded from the mission, a chunk or a plant, so the same mission
/// grows the same plants every time on every machine. Visual only: nothing here touches the simulation's rand.
/// </summary>
public struct VegRng(ulong seed)
{
    private ulong _s = seed;

    public ulong Next()
    {
        ulong z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float Float() => (Next() >> 40) * (1f / (1 << 24));

    public float Range(float a, float b) => a + (b - a) * Float();

    /// <summary>Uniform in [0, n).</summary>
    public int Int(int n) => (int)((Next() >> 33) % (ulong)n);

    /// <summary>Symmetric, roughly bell-shaped in [-1, 1] (the mean of three uniforms).</summary>
    public float Bell() => (Float() + Float() + Float()) * (2f / 3f) - 1f;

    /// <summary>A random stream derived from this one and a key, leaving this one where it was.</summary>
    public readonly VegRng Fork(ulong key) => new(VegHash.Mix(_s, key));
}

public static class VegHash
{
    public static ulong Mix(ulong a, ulong b)
    {
        ulong h = a * 0x9E3779B97F4A7C15UL ^ (b + 0x632BE59BD9B4E019UL);
        h ^= h >> 32; h *= 0xD6E8FEB86659FD93UL; h ^= h >> 32; h *= 0xD6E8FEB86659FD93UL; h ^= h >> 32;
        return h;
    }

    public static ulong Mix(ulong a, ulong b, ulong c) => Mix(Mix(a, b), c);

    /// <summary>FNV-1a of a name, case-folded (mission names come in either case).</summary>
    public static ulong Of(string s)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (char ch in s.ToLowerInvariant()) { h ^= ch; h *= 0x100000001b3UL; }
        return h;
    }

    /// <summary>A float in [0, 1) from integer coordinates and a key (value noise lattice).</summary>
    public static float Unit(int x, int z, ulong key) => (Mix(key, (ulong)(uint)x << 32 | (uint)z) >> 40) * (1f / (1 << 24));
}
