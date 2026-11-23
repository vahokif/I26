using Godot;

namespace I76.Game.Sim;

/// <summary>
/// Follows the listener's motion for the voices' Doppler shift (<see cref="Voice3D.TrackListener"/>): processed after every
/// other node, so it reads the camera where this frame placed it, over this frame's time.
/// </summary>
public partial class ListenerTracker : Node
{
    public ListenerTracker() => ProcessPriority = int.MaxValue;

    public override void _EnterTree() => Voice3D.ListenerCut();

    public override void _Process(double delta) => Voice3D.TrackListener(GetViewport(), delta);
}
