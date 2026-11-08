namespace I76.Sim.Views;

/// <summary>What the mouse pointer does: the port's own state for it, mapped one to one onto Godot's Input.MouseModeEnum by the game.</summary>
public enum CursorMode
{
    /// <summary>Shown and free: every menu, and any screen while the window is not focused.</summary>
    Visible,
    /// <summary>Shown and kept inside the window: every menu with Keep the mouse in the window on.</summary>
    Confined,
    /// <summary>Hidden, free to leave the window (a mission with Keep the mouse in the window off).</summary>
    Hidden,
    /// <summary>Hidden and kept inside the window (a mission with Keep the mouse in the window on).</summary>
    ConfinedHidden,
    /// <summary>Hidden and taken, relative motion only (the Mouse Camera in the chase view or Cockpit Mouse Look in the 3D cockpit).</summary>
    Captured,
}

/// <summary>The one place that says what the pointer does. Main calls it every frame from "which screen is up", so no transition (a mission's end, a failed game, an abort, focus lost) can leave it wrong.</summary>
public static class CursorPolicy
{
    /// <summary>The cursor for a screen. <paramref name="inMission"/>: a mission is running with no menu, map, notepad, loading screen or question over it (paused or not). <paramref name="chaseView"/>: the view is the free chase camera. <paramref name="cockpitView"/>: the view is the 3D cockpit (outside binoculars).</summary>
    public static CursorMode For(bool inMission, bool chaseView, int mouseCamera, bool focused, bool confine, bool cockpitView = false, bool mouseCockpit = false)
    {
        if (!focused) return CursorMode.Visible;
        if (!inMission) return confine ? CursorMode.Confined : CursorMode.Visible;
        if (chaseView && mouseCamera != MouseLook.Off || cockpitView && mouseCockpit) return CursorMode.Captured;
        return confine ? CursorMode.ConfinedHidden : CursorMode.Hidden;
    }
}
