namespace I76.Sim.Views;

/// <summary>
/// Whether the player's own car is drawn. The original's object pass skips it while the camera is in the cockpit or is the
/// hood view (F6), whose eye stands inside the body's bounds on a long car; a script camera sees it from outside whatever
/// the player's view. Presentation only.
/// </summary>
public static class OwnCar
{
    public static bool Drawn(bool cockpit, bool hoodView, bool scriptCamera) => scriptCamera || !(cockpit || hoodView);
}
