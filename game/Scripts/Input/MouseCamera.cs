using Godot;
using I76.Sim.Views;

namespace I76.Game;

/// <summary>
/// The mouse orbit of the chase view (Mouse Camera) and in-car free look (Cockpit Mouse Look; <see cref="MouseLook"/> is the math).
/// Main decides when the mouse is the camera's (<see cref="Active"/>: in a mission, the chase view or 3D cockpit, no menu, pause or
/// cut-scene, the window focused), captures the cursor while it is, and feeds the relative motion here; the chase camera or cockpit
/// takes what has built up each frame. The mouse's buttons are not touched: they reach the key bindings as ever.
/// </summary>
public static class MouseCamera
{
    public static int Level = MouseLook.Normal;
    public static float Sensitivity = MouseLook.DefaultSensitivity;
    public static bool InvertY;
    /// <summary>Mouse free look in the F1 3D cockpit (shell.cfg [port] mouse_cockpit).</summary>
    public static bool CockpitLook;
    /// <summary>The mouse wheel cycles weapons instead of zooming the external views (shell.cfg [port] wheel_cycle_weapon).</summary>
    public static bool WheelCycleWeapon;
    /// <summary>The camera eases back behind the car (or forward in the cockpit) after the mouse or stick rests (Camera Return); off, it stays where it was left.</summary>
    public static bool ReturnBehind = true;
    /// <summary>Keep the mouse in the window: the cursor is confined to it, hidden in a mission and shown in the menus (shell.cfg [port] keep_mouse_in_window).</summary>
    public static bool Confine;
    private static float _dx, _dy;

    /// <summary>The mouse wheel's zoom factor of each external view (<see cref="ViewZoom"/>; 1 the original's framing). Every mission starts at 1, and with View Zoom Reset (<see cref="KeepZoom"/> false) every switch into a view and every respawn too.</summary>
    public static readonly float[] Zooms = Enumerable.Repeat(ViewZoom.Default, ViewZoom.Count).ToArray();
    public static float ZoomOf(ViewZoom.Kind k) => Zooms[(int)k];
    /// <summary>Counts the resets, so the cameras drop their eased zoom (and pull-back and mouse offset) at once instead of easing from the old one.</summary>
    public static int ZoomEpoch { get; private set; }
    public static void ResetZoom(ViewZoom.Kind k) => Zooms[(int)k] = ViewZoom.Default;
    /// <summary>Every view back at the original's framing, now (a mission start, a switch into a view, the hand-back from a cut-scene).</summary>
    public static void ResetAllZooms() { System.Array.Fill(Zooms, ViewZoom.Default); ZoomEpoch++; }
    /// <summary>The view the wheel zooms now: an external view in a mission with no menu, pause or cut-scene up (null: the cockpit and the rest keep the wheel).</summary>
    public static ViewZoom.Kind? WheelView { get; set; }
    public static bool WheelActive => !WheelCycleWeapon && WheelView is not null;

    public static void Wheel(float notches)
    {
        if (WheelCycleWeapon || WheelView is not { } v) return;
        Zooms[(int)v] = ViewZoom.Notch(v, Zooms[(int)v], notches);
    }

    /// <summary>The mouse is captured and active for either the chase view or the cockpit.</summary>
    public static bool Active { get; set; }

    /// <summary>The mouse is captured specifically for the 3D cockpit view.</summary>
    public static bool CockpitActive { get; set; }

    public static void Apply(I76.Game.Shell.ShellSettings s)
    {
        Level = s.MouseCamera;
        Sensitivity = MouseLook.ClampSensitivity(s.MouseSensitivity);
        InvertY = s.MouseInvertY;
        CockpitLook = s.MouseCockpit;
        WheelCycleWeapon = s.WheelCycleWeapon;
        Confine = s.KeepMouseInWindow;
        ReturnBehind = s.CameraReturn;
        KeepZoom = !s.ZoomReset;
    }

    /// <summary>View Zoom Reset off: the views keep their wheel zoom through switches of view, cut-scenes and respawns (a new mission still starts at 1).</summary>
    public static bool KeepZoom { get; set; }

    public static void Motion(Vector2 relative)
    {
        if (!Active) return;
        if (CockpitActive ? !CockpitLook : Level == MouseLook.Off) return;
        _dx += relative.X;
        _dy += relative.Y;
    }

    /// <summary>The (yaw, pitch) change for the motion since the last call, game signs.</summary>
    public static (float Yaw, float Pitch) Take()
    {
        var (dx, dy) = (_dx, _dy);
        _dx = _dy = 0;
        if (!Active) return (0, 0);
        bool enabled = CockpitActive ? CockpitLook : Level != MouseLook.Off;
        return MouseLook.Orbit(dx, dy, enabled, Sensitivity, InvertY);
    }
}
