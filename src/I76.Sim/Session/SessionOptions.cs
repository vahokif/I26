namespace I76.Sim.Session;

/// <summary>
/// What a session allows besides the simulation itself: the things that are
/// fine on one machine but would let a client change a shared outcome.
/// </summary>
public sealed record SessionOptions
{
    /// <summary>The capture hooks (I76_SPAWN_AT, I76_BOOM): a host may honour them only when this is set.</summary>
    public bool DebugHooks { get; init; } = true;
    /// <summary>The port's optional modern physics for the player's car (docs/modern-physics.md).</summary>
    public bool ModernPhysics { get; init; } = true;
    /// <summary>Whose position decides the cars' near and far paths.</summary>
    public FarPolicy Far { get; init; } = FarPolicy.Player;

    /// <summary>One player at one machine: everything as the game has it.</summary>
    public static SessionOptions SinglePlayer { get; } = new();

    /// <summary>A networked session: no debug hooks, the original physics only, near and far by the players' cars.</summary>
    public static SessionOptions Network { get; } = new() { DebugHooks = false, ModernPhysics = false, Far = FarPolicy.Humans };
}
