using Godot;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// Two small globals for the world's loose geometry (plants, smoke, flames) that has no collision with the cars:
/// the player's cab as an oriented box (i76_cab_inv, i76_cab_half; cab_clip.gdshaderinc cuts effects away inside it),
/// set only while the cockpit is the current camera and the car is whole; and the cars' ground positions
/// (i76_cars: x, y, z and a radius per car in a 16 x 1 float texture, i76_car_count), which the plants bend away from.
/// </summary>
public partial class CabClip : Node
{
    public const int Max = 16;
    /// <summary>How far from a car's centre a plant is flattened, and from where it starts to part (metres).</summary>
    public const float Radius = 2.6f;
    /// <summary>Cars farther than this from the player's car are not sent: the plants fade out by 250 m, but a bend is only seen near the camera.</summary>
    private const float Reach = 120f;

    /// <summary>The cars of this frame (global positions and node ids; the first <see cref="Count"/>), for the tumbleweeds to burst against (World/Tumbleweeds.cs).</summary>
    public static readonly Vector3[] Positions = new Vector3[Max];
    public static readonly ulong[] Ids = new ulong[Max];
    public static int Count { get; private set; }

    public required Func<CarNode?> Player { get; init; }
    public required Func<IEnumerable<CarNode>> Cars { get; init; }

    private Image _img = null!;
    private ImageTexture _tex = null!;
    private bool _lastOn;
    /// <summary>I76_CAB_CLIP=0: neither the clip nor the bend (for before and after captures).</summary>
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("I76_CAB_CLIP") == "0";

    public override void _Ready()
    {
        ProcessPriority = 1000;   // after the cars are placed for the frame
        _img = Image.CreateEmpty(Max, 1, false, Image.Format.Rgbaf);
        _tex = ImageTexture.CreateFromImage(_img);
        RenderingServer.GlobalShaderParameterSet("i76_cars", _tex);
    }

    /// <summary>The cab box as the shaders have it (the globals cannot be read back outside the editor): the world to the box, and its half-extents (zero: off). SmokeLayer reads them.</summary>
    public static Transform3D Inv { get; private set; } = Transform3D.Identity;
    public static Vector3 Half { get; private set; }

    public override void _ExitTree()
    {
        RenderingServer.GlobalShaderParameterSet("i76_cab_half", Vector3.Zero);
        Half = Vector3.Zero;
        RenderingServer.GlobalShaderParameterSet("i76_car_count", 0);
        Count = 0;
    }

    /// <summary>The cab in the car's frame: the interior model's bounds (the windscreen and the roof included), grown a little.</summary>
    private static bool Box(CarNode car, out Vector3 centre, out Vector3 half)
    {
        centre = half = default;
        if (car.CockpitBounds is not { } cb) return false;
        var bb = cb.Grow(0.1f);
        if (System.Environment.GetEnvironmentVariable("I76_CAB_DEBUG") == "1") GD.Print($"cab box {bb} eye {car.Eye}");
        // the interior model reaches over the hood; the cab ends 0.85 m ahead of the eye, about the windscreen's foot, so smoke over the hood shows
        float front = car.Eye.Z - 0.85f;
        bb = new Aabb(new Vector3(bb.Position.X, bb.Position.Y, front), new Vector3(bb.Size.X, bb.Size.Y, bb.End.Z - front));
        centre = bb.GetCenter();
        half = bb.Size * 0.5f;
        return true;
    }

    public override void _Process(double delta)
    {
        var p = Player();
        bool on = false;
        if (!Off && p is not null && p.IsInsideTree() && p.CockpitCamera is { Current: true } && !p.SeenFromOutside && !p.Wrecked && Box(p, out var c, out var h))
        {
            on = true;
            var inv = (p.GlobalTransform * new Transform3D(Basis.Identity, c)).AffineInverse();
            RenderingServer.GlobalShaderParameterSet("i76_cab_inv", inv);
            RenderingServer.GlobalShaderParameterSet("i76_cab_half", h);
            Inv = inv; Half = h;
        }
        if (!on && _lastOn && !Off) { RenderingServer.GlobalShaderParameterSet("i76_cab_half", Vector3.Zero); Half = Vector3.Zero; }
        _lastOn = on;

        int n = 0;
        var at = p is not null && p.IsInsideTree() ? p.GlobalPosition : Vector3.Zero;
        void Add(CarNode car)
        {
            if (n == Max || !car.IsInsideTree() || !car.Visible) return;
            var g = car.GlobalPosition;
            if (n > 0 && g.DistanceSquaredTo(at) > Reach * Reach) return;
            Positions[n] = g;
            Ids[n] = car.GetInstanceId();
            _img.SetPixel(n++, 0, new Color(g.X, g.Y, g.Z, Radius));
        }
        if (p is not null) Add(p);
        foreach (var car in Cars()) if (car != p) Add(car);
        Count = n;
        if (Off) return;
        if (n > 0) _tex.Update(_img);
        RenderingServer.GlobalShaderParameterSet("i76_car_count", n);
    }
}
