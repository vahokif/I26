using Godot;
using I76.Formats.Assembly;
using I76.Game.Sim;
using I76.Sim.Trip;
using I76.Sim.Vehicles;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// A car on the turntable: the game's own car node (<see cref="CarNode"/>: body, weapon gizmos, turrets, wheels at the body's WLOC
/// slots, lamps) built from a trip car's file by the same assembly the missions use, parked at rest, its wheels on y = 0.
/// Positions are the car's, in Godot axes (the car faces -Z).
/// </summary>
public partial class GarageCar : Node3D
{
    public CarNode Node { get; private set; } = null!;
    public AssembledModel Model { get; private set; } = null!;
    public Aabb Bounds { get; private set; }
    /// <summary>Each hardpoint (the .vdf's HLOC id) at its place on the car.</summary>
    public Dictionary<int, (Vector3 Pos, Vector3 Out)> Hardpoints { get; } = [];
    /// <summary>The wheel centres by WLOC slot (0 FR, 1 FL, 2 MR, 3 ML, 4 RR, 5 RL), on the ground.</summary>
    public Dictionary<int, Vector3> Wheels { get; } = [];
    public string Name2 { get; private set; } = "";
    /// <summary>The scale the turntable shows it at: 1 for the cars the bay is made for, less for a helicopter or a jet (a 16 m Huey is taller than the bay),
    /// more for the Armadillo. Positions in <see cref="Bounds"/>, <see cref="Hardpoints"/> and <see cref="Wheels"/> stay the car's own.</summary>
    public float Fit { get; private set; } = 1f;

    /// <summary>A helicopter: no wheels, it stands on its skids.</summary>
    public bool IsHeli { get; private set; }

    /// <summary>Builds the car of <paramref name="car"/>'s file; the 1997 look draws the original's wheels and shaders, the modern the round ones.</summary>
    public static GarageCar Build(GarageAssets assets, TripCar car, bool mode1997, I76.Formats.Imaging.CarPaint? paint = null)
    {
        var (name, vehicle, heli, parts) = Parts(assets, car);
        var node = new CarNode { Name = "Car", Definition = name };
        var g = new GarageCar { Name = "GarageCar", Node = node, Model = parts.Model, Name2 = name };
        g.IsHeli = heli;
        g.AddChild(node);
        node.Init(vehicle, parts.Body);
        node.SetLamps(parts.Lamps);
        foreach (var (w, m) in parts.Weapons) node.AddWeapon(w, m);
        foreach (var (w, m, pivot) in parts.Turrets) node.AddTurret(w, m, pivot);
        // a helicopter rides on its skids: the .vcf's WLOC slots are the car format's, with nothing to draw (the mission's rule)
        if (heli) { }
        else if (parts.Wheels is { } shared) foreach (var (slot, wm, centre) in shared) node.AddWheel(slot, wm, centre);
        else
            foreach (var loc in parts.Model.Locations.Where(l => l.Tag == "WLOC"))
            {
                var t = loc.Transform.Translation;
                if (MathF.Abs(t[0]) + MathF.Abs(t[1]) + MathF.Abs(t[2]) < 1e-6f) continue;
                if (assets.Meshes.BuildWheel(parts.Model, loc.Index) is { } wm) node.AddWheel(loc.Index, wm, new System.Numerics.Vector3(t[0], t[1], t[2]));
            }
        node.Modern = !mode1997;
        // a respray: the scheme's masks (made once) and this car's colours
        if (paint is { IsNone: false }) { assets.Meshes.EnsurePaint(parts.Model); node.SetPaint(paint); }
        node.Place(1f, System.Numerics.Vector3.Zero);     // one pose, at rest: the wheels at their locations
        var vdf = assets.Vfs.TryRead(car.Vdf);
        g.Settle(parts, vdf is null ? [] : I76.Formats.Bwd2.Hardpoint.Read(new I76.Formats.Bwd2.Bwd2File(vdf)));
        return g;
    }

    /// <summary>The car's simulation (for its node) and its parts, made into the assets' cache (the warm-up's thread makes them ahead: <see cref="Prepare"/>).</summary>
    private static (string Name, I76.Sim.Vehicles.Vehicle Vehicle, bool Heli, CarPartsCache.Parts Parts) Parts(GarageAssets assets, TripCar car)
    {
        var vcf = car.ToVcf();
        string name = assets.NameOf(vcf);
        I76.Sim.Vehicles.Vehicle vehicle;
        using (assets.Serve(name, vcf)) vehicle = VehicleFactory.Create(assets.Vfs, name);
        bool heli = vehicle.ObjectClass == 9;   // the vdf's class; the mission loader sets the flight state up on top, the garage has none
        return (name, vehicle, heli, assets.PartsOf(name, vcf, heli));
    }

    /// <summary>The car's parts (assembly, meshes, textures) made into the assets' cache, no node: on a thread that owns the assets.</summary>
    public static void Prepare(GarageAssets assets, TripCar car) => Parts(assets, car);

    /// <summary>Lowers the car so its wheels (else its body) stand on y = 0, and measures it.</summary>
    private void Settle(CarPartsCache.Parts parts, IReadOnlyList<I76.Formats.Bwd2.Hardpoint> hardpoints)
    {
        float low = float.MaxValue;
        foreach (var c in Node.GetChildren().OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("Wheel")))
            if (c.Mesh is { } mesh) low = MathF.Min(low, c.Position.Y + mesh.GetAabb().Position.Y);
        if (low == float.MaxValue) low = Node.CallBodyBounds().Position.Y;
        Node.Position = new Vector3(0, -low, 0);
        var all = Node.CallBodyBounds();
        foreach (var c in Node.GetChildren().OfType<MeshInstance3D>().Where(m => m.Mesh is not null && m.Name.ToString() != "Body"))
            all = all.Merge(new Aabb(c.Position + c.Mesh!.GetAabb().Position, c.Mesh.GetAabb().Size));
        Bounds = new Aabb(all.Position + Node.Position, all.Size);
        Fit = GarageViews.FitFor(Bounds.GetLongestAxisSize());
        foreach (var h in hardpoints)
        {
            var t = h.Transform.Translation;
            Hardpoints[(int)h.Id] = (Coords.ToGodot(new System.Numerics.Vector3(t[0], t[1], t[2])) + Node.Position, Vector3.Zero);
        }
        foreach (var l in parts.Model.Locations.Where(l => !IsHeli && l.Tag == "WLOC"))
        {
            var t = l.Transform.Translation;
            Wheels[l.Index] = Coords.ToGodot(new System.Numerics.Vector3(t[0], t[1], t[2])) + Node.Position;
        }
    }
}

internal static class CarNodeExt
{
    /// <summary>The body's bounds in the car's frame (Godot axes).</summary>
    public static Aabb CallBodyBounds(this CarNode n) => n.BodyBounds;
}
