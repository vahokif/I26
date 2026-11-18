using Godot;
using I76.Net;

namespace I76.Game.Net;

/// <summary>
/// CTF's flags in the world, from the server's snapshots. A flag that stands on its base
/// (CtfFlag.StandsAtHome: at home or inactive) is the arena's own a1flag model; one that
/// left it (carried, or dropped) is a pole and a pennant in the team's colour riding on
/// the carrier's car or where it lies, a placeholder shape, and its base stands empty
/// (<see cref="ShowHome"/> hides the model's pole and pennant). So the flag is drawn once.
/// </summary>
public partial class FlagView : Node3D
{
    public required ClientWorld World { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    /// <summary>The drawn car node of a car serial (the predicted one for the own car), null if there is none.</summary>
    public required Func<int, Node3D?> CarNode { get; init; }
    /// <summary>Shows or hides the arena model's pole and pennant on flag i's base (the snapshot's flag order).</summary>
    public Action<int, bool>? ShowHome { get; init; }
    private readonly List<Node3D> _nodes = [];
    private readonly List<bool?> _home = [];

    public FlagView() { ProcessPriority = 1001; }   // after SimDriver and CabClip have placed the cars for the frame

    /// <summary>Where a carried flag rides on the car, in the car's own frame.</summary>
    private static readonly Vector3 CarriedAt = Vector3.Up * 1.5f;

    public override void _Process(double delta)
    {
        var flags = World.Flags;
        while (_nodes.Count < flags.Count) { var n = Make(); _nodes.Add(n); AddChild(n); }
        var origin = Origin();
        for (int i = 0; i < _nodes.Count; i++)
        {
            // on its base the arena's own flag stands; away, the placeholder is the flag and the base is empty
            bool home = i >= flags.Count || I76.Sim.Missions.CtfFlag.StandsAtHome(flags[i].State);
            while (_home.Count <= i) _home.Add(null);
            if (_home[i] != home) { _home[i] = home; ShowHome?.Invoke(i, home); }
            _nodes[i].Visible = !home;
            if (i >= flags.Count) continue;
            var f = flags[i];
            if (f.State == 4 && CarNode(f.Carrier) is { Visible: true } car)
            {
                // carried: on the carrier's drawn car, which is interpolated (or predicted) on the render clock; the snapshot's
                // position steps at the server's rate and jittered against it
                _nodes[i].GlobalTransform = new Transform3D(car.GlobalTransform.Basis, car.GlobalTransform * CarriedAt);
            }
            else
            {
                // loose (or its carrier not drawn): between the two snapshots the view is between, like every other object
                var pos = f.Pos;
                if (i < World.PreviousFlags.Count && World.PreviousFlags[i] is { } p && p.State == f.State && p.State != 4 && p.Team == f.Team)
                    pos = System.Numerics.Vector3.Lerp(p.Pos, f.Pos, World.Alpha);
                _nodes[i].Basis = Basis.Identity;
                _nodes[i].Position = I76.Game.Sim.Coords.ToGodot(pos - origin) + Vector3.Up * (f.State == 4 ? 1.5f : 0);
            }
            var c = NetHud.TeamColour(f.Team);
            foreach (var m in _nodes[i].GetChildren().OfType<MeshInstance3D>())
                ((StandardMaterial3D)m.MaterialOverride!).AlbedoColor = m.Name == "Pennant" ? c : new Color(0.2f, 0.2f, 0.2f);
        }
    }

    private static Node3D Make()
    {
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Name = "Pole", Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.08f, Height = 5 }, Position = new Vector3(0, 2.5f, 0), MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
        n.AddChild(new MeshInstance3D { Name = "Pennant", Mesh = new BoxMesh { Size = new Vector3(0.05f, 1.2f, 2f) }, Position = new Vector3(0, 4.3f, 1f), MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
        return n;
    }
}
