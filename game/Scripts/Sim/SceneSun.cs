using Godot;

namespace I76.Game.Sim;

/// <summary>
/// The scene's sun (the first DirectionalLight3D in the tree) for the smoke's lighting: found once and shared by the trails, the
/// damage smoke, the smoke layer and the mirror's smoke, each of which searched the whole tree for it on its first puff (a walk of
/// every node of a mission: 118 ms in the frame of the first missile fired, 55 ms at the first hit that set a car smoking).
/// The effects' rehearsal finds it behind the loading card; a new mission's sun is found again when the last one leaves the tree.
/// </summary>
public static class SceneSun
{
    private static DirectionalLight3D? _sun;

    public static DirectionalLight3D? Of(Node from)
    {
        if (_sun is not null && IsInstanceValid(_sun) && _sun.IsInsideTree()) return _sun;
        _sun = from.GetTree()?.Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>().FirstOrDefault();
        return _sun;
    }

    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
}
