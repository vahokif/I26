using Godot;

namespace I76.Game.World;

/// <summary>
/// The object shaders' global light (the sun's direction, its strength, the ambient, the distance fade, the light ramp, the look): set by a
/// mission's sky and look switch through here, and remembered, so that the 3D garage, which lights its own car with these same
/// globals, can put the mission's back when it closes over a game that goes on (the network Reconfigure).
/// </summary>
public static class ObjectLight
{
    private static readonly Dictionary<string, Variant> Held = [];

    public static void Set(string name, Variant value)
    {
        Held[name] = value;
        RenderingServer.GlobalShaderParameterSet(name, value);
    }

    /// <summary>Every global as the last mission setting set it again; those never set are left as the garage had them.</summary>
    public static void Reapply()
    {
        foreach (var (k, v) in Held) RenderingServer.GlobalShaderParameterSet(k, v);
    }

    /// <summary>A new game or a new mission build: nothing is held.</summary>
    public static void Forget() => Held.Clear();
}
