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

    /// <summary>
    /// The light ramp (ramp.gdshaderinc): 32 entries of (a, b), interleaved in <paramref name="ab"/>, as 16 vec4 globals of two entries
    /// each. Plain uniforms rather than a texture, which the vertex stage of some drivers cannot fetch (#133).
    /// </summary>
    public static void SetRamp(float[] ab, bool hold = true)
    {
        for (int i = 0; i < 16; i++)
        {
            var v = new Vector4(ab[4 * i], ab[4 * i + 1], ab[4 * i + 2], ab[4 * i + 3]);
            if (hold) Set("i76_luma_" + i, v); else RenderingServer.GlobalShaderParameterSet("i76_luma_" + i, v);
        }
    }

    /// <summary>Every global as the last mission setting set it again; those never set are left as the garage had them.</summary>
    public static void Reapply()
    {
        foreach (var (k, v) in Held) RenderingServer.GlobalShaderParameterSet(k, v);
    }

    /// <summary>A new game or a new mission build: nothing is held.</summary>
    public static void Forget() => Held.Clear();
}
