using Godot;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The modern look's night: the road signs' faces are retroreflective sheeting, as real ones are (<see cref="Retroreflection"/>).
/// Each frame the lit headlamps nearest the camera (<see cref="Max"/>; the camera's own car's are the nearest) go to the
/// object shader as globals (i76_retro_at: position and strength; i76_retro_aim: beam axis and the cosine of its edge;
/// i76_retro_count), where a sign face's material (retro, MaterialLibrary) adds what they send back to the eye as emission
/// (object.gdshaderinc: retro_light). No lights are added. Rendering only; made under a night preset only (by day the sun
/// outshines the lamps, and nothing is sent).
/// </summary>
public partial class RetroLamps : Node
{
    public const int Max = 4;

    /// <summary>The lit headlamps (global place and beam axis).</summary>
    public required Func<IEnumerable<(Vector3 At, Vector3 Aim)>> Lamps { get; init; }

    private readonly List<(Vector3 At, Vector3 Aim, float D2)> _near = [];
    private readonly Vector4[] _at = new Vector4[Max], _aim = new Vector4[Max];

    public override void _Ready() => ProcessPriority = 1000;   // after the cars are placed for the frame

    public override void _ExitTree() => RenderingServer.GlobalShaderParameterSet("i76_retro_count", 0);

    public override void _Process(double delta)
    {
        _near.Clear();
        if (GetViewport().GetCamera3D() is { } cam)
        {
            var eye = cam.GlobalPosition;
            foreach (var (at, aim) in Lamps()) _near.Add((at, aim, eye.DistanceSquaredTo(at)));
            _near.Sort((a, b) => a.D2.CompareTo(b.D2));
        }
        int n = Math.Min(_near.Count, Max);
        for (int i = 0; i < Max; i++)
        {
            _at[i] = i < n ? new Vector4(_near[i].At.X, _near[i].At.Y, _near[i].At.Z, Retroreflection.Strength) : Vector4.Zero;
            _aim[i] = i < n ? new Vector4(_near[i].Aim.X, _near[i].Aim.Y, _near[i].Aim.Z, CarNode.HeadEdgeCos) : Vector4.Zero;
        }
        RenderingServer.GlobalShaderParameterSet("i76_retro_at", new Projection(_at[0], _at[1], _at[2], _at[3]));
        RenderingServer.GlobalShaderParameterSet("i76_retro_aim", new Projection(_aim[0], _aim[1], _aim[2], _aim[3]));
        RenderingServer.GlobalShaderParameterSet("i76_retro_count", n);
    }
}

/// <summary>
/// A road sign's retroreflective sheeting (the modern look, at night): it sends a lamp's light back toward the lamp in a narrow
/// cone, so in the beam of the car's own headlamps the sign shows bright to the driver from a few hundred metres, with far less
/// falloff than the road's diffuse light, and to an eye well away from the lamp (another car's beam, seen from the side) next to
/// nothing. The original draws the sign faces at full brightness so they read at night; this is the modern look's way to the
/// same end. object.gdshaderinc's retro_light is the same rule with the same constants (RETRO_*); keep them together.
/// </summary>
public static class Retroreflection
{
    /// <summary>One lamp's return off a white face on the beam's axis, the eye beside the lamp and the face close: a multiple of the face's colour.</summary>
    public const float Strength = 0.9f;

    /// <summary>The beam's hot spot (radians off its axis for 1/e), which is what carries far.</summary>
    public const float Hot = 0.262f;
    /// <summary>The return cone (radians between the lamp and the eye, as the face sees them, for 1/e).</summary>
    public const float Lobe = 0.0873f;
    /// <summary>The falloff with the lamp's distance: half at this many metres, a third at twice it.</summary>
    public const float Reach = 150f;
    /// <summary>Nothing past this many metres (fading from three quarters of it).</summary>
    public const float Far = 600f;

    /// <summary>
    /// What one lamp sends back off a sign face toward the eye, as a multiple of <see cref="Strength"/>: the lamp's intensity
    /// toward the face (<paramref name="beamCos"/>, the cosine from the beam's axis; the hot spot, and nothing past the beam's
    /// edge <paramref name="edgeCos"/>, the cone the headlamp's light lays on the road), the lamp's distance
    /// <paramref name="distance"/>, the observation angle <paramref name="observation"/> (radians, between the lamp and the eye
    /// as the face sees them), and mildly the entrance angle (<paramref name="entranceCos"/>, the cosine from the face's normal
    /// to the lamp; nothing from behind).
    /// </summary>
    public static float Factor(float distance, float observation, float entranceCos, float beamCos, float edgeCos)
    {
        if (entranceCos <= 0 || beamCos <= edgeCos) return 0;
        float t = MathF.Acos(Math.Clamp(beamCos, -1f, 1f));
        float beam = MathF.Exp(-t * t / (Hot * Hot));
        float lobe = MathF.Exp(-observation * observation / (Lobe * Lobe));
        float reach = 1 / (1 + distance / Reach) * (1 - SmoothStep(Far * 0.75f, Far, distance));
        return beam * MathF.Sqrt(entranceCos) * lobe * reach;
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
