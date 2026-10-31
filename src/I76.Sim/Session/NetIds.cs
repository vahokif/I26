namespace I76.Sim.Session;

/// <summary>What a network id names.</summary>
public enum NetKind : byte { None = 0, Vehicle = 1, Round = 2, Structure = 3 }

/// <summary>
/// Stable ids for what the session's state is made of, the same on every
/// machine that runs the same session: the kind in the top byte, then a
/// serial in creation order from 1. Serials only grow, so an id is never
/// reused within a session (a car renewed in place keeps its id and bumps
/// its <see cref="Vehicles.Vehicle.NetGeneration"/> instead). Structures are
/// numbered by their place in the static world, which never changes.
/// </summary>
public sealed partial class NetIds
{
    private readonly int[] _next = new int[4];

    public int Next(NetKind kind)
    {
        int serial = ++_next[(int)kind];
        if (serial > 0xffffff) throw new InvalidOperationException($"{kind} ids exhausted");
        return (int)kind << 24 | serial;
    }

    public static NetKind KindOf(int id) => (NetKind)((uint)id >> 24);
    public static int SerialOf(int id) => id & 0xffffff;
}
