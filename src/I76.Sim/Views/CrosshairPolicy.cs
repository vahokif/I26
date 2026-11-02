namespace I76.Sim.Views;

/// <summary>
/// Whether the chase crosshair (an aiming aid the original does not have) shows: in single player by the player's Chase Crosshair
/// setting alone; in a network game (a melee or co-op) only where the host allows it (the host form's Chase Crosshair, off by
/// default), and then by the player's setting. The view's alone: nothing in the simulation reads it.
/// </summary>
public static class CrosshairPolicy
{
    public static bool Shown(bool setting, bool network, bool hostAllows) => setting && (!network || hostAllows);
}
