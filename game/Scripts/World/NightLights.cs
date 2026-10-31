using Godot;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The 1997 look's night lights: under a night preset every lit car carries one, a spot at its headlight pool
/// (VLOC 38) along its nose (range 20, so 1/(1 + d²/400), exponent 18, as the light table read at run
/// time in M04 holds them). The original keeps a light on the list while its range touches the view; the objects
/// take the four strongest at their origin into their vertex light (object.gdshaderinc),
/// and a drop shadow is cast from the strongest light at the caster when that beats 1.5 times the moon's intensity.
/// The terrain is lit by none of them: the pools are what lights it.
/// The lights go to the shaders as a 16 x 2 float texture (i76_night_lights: position and exponent, then the nose
/// and 1/range²) and a count (i76_night_light_count).
/// </summary>
public partial class NightLights : Node
{
    public const int Max = 16;
    private const float Range = I76.Formats.Mission.NightSpot.Range;

    /// <summary>The cars, whose NightLight is set while their lamps are lit.</summary>
    public required Func<IEnumerable<CarNode>> Cars { get; init; }
    /// <summary>The ground height under a global (Godot) x, z, in Godot space.</summary>
    public required Func<float, float, float> Ground { get; init; }
    /// <summary>The preset's intensity: a light casts the shadows only where it beats 1.5 times it.</summary>
    public float MoonIntensity { get; init; }

    private readonly List<(Vector3 Pos, Vector3 Forward)> _active = [];
    private Image _img = null!;
    private ImageTexture _tex = null!;

    public override void _Ready()
    {
        ProcessPriority = 1000;   // after the cars are placed for the frame
        _img = Image.CreateEmpty(Max, 2, false, Image.Format.Rgbaf);
        _tex = ImageTexture.CreateFromImage(_img);
        RenderingServer.GlobalShaderParameterSet("i76_night_lights", _tex);
        RenderingServer.GlobalShaderParameterSet("i76_night_light_count", 0);
    }

    public override void _ExitTree() => RenderingServer.GlobalShaderParameterSet("i76_night_light_count", 0);

    public override void _Process(double delta)
    {
        _active.Clear();
        var cam = GetViewport().GetCamera3D();
        var planes = cam?.GetFrustum();
        foreach (var car in Cars())
        {
            if (car.NightLight is not { } l || _active.Count == Max) continue;
            // A light whose range lies wholly outside the view is left off the list
            if (planes is not null && planes.Any(p => p.DistanceTo(l.Pos) > Range)) continue;
            _active.Add(l);
        }
        for (int i = 0; i < _active.Count; i++)
        {
            var (p, f) = _active[i];
            _img.SetPixel(i, 0, new Color(p.X, p.Y, p.Z, I76.Formats.Mission.NightSpot.Exponent));
            _img.SetPixel(i, 1, new Color(f.X, f.Y, f.Z, I76.Formats.Mission.NightSpot.K));
        }
        if (_active.Count > 0) _tex.Update(_img);
        RenderingServer.GlobalShaderParameterSet("i76_night_light_count", _active.Count);
    }

    /// <summary>NightSpot.ShadowLight: the direction toward the light a caster centred at <paramref name="c"/> (global) takes its drop shadow from, or null for the moon.</summary>
    public Vector3? ShadowLight(Vector3 c)
    {
        static System.Numerics.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
        var l = I76.Formats.Mission.NightSpot.ShadowLight(N(c), _active.Select(a => (N(a.Pos), N(a.Forward))), Ground, MoonIntensity);
        return l is { } d ? new Vector3(d.X, d.Y, d.Z) : null;
    }
}
