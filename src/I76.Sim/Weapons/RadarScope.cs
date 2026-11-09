using System.Numerics;
using I76.Sim.Math;

namespace I76.Sim.Weapons;

/// <summary>The radar scope's projection: heading-up, +X on the scope is the car's right, +Z (up the picture) its front.</summary>
public static class RadarScope
{
    /// <summary>
    /// The weapon camera's mask (zradmask.map) paints the box's outside in palette index 1 (cream), where the radar frames
    /// (zradf) have the transparent index 255. The original's panel shows the dash through those corners (a capture of its 3D dash
    /// in the original game), so the picture must leave them clear: this marks the mask's texels to clear.
    /// </summary>
    public static bool[] WeaponCameraClear(byte[] maskIndices)
    {
        var clear = new bool[maskIndices.Length];
        for (int i = 0; i < clear.Length; i++) clear[i] = maskIndices[i] == 1;
        return clear;
    }

    /// <summary>The contact's scope position, each axis -1..1 over the range, or null outside the range circle.</summary>
    /// <summary>The ZRADB frame (0..29) of a locked radar: the original takes atan2(x, z) of the target in car-local terms, adds 2π if negative, scales by 30/2π, truncates and wraps at 30.</summary>
    public static int LockedFrame(float x, float z)
    {
        float a = (MathF.Abs(x) < 1e-5f && MathF.Abs(z) < 1e-5f) ? 0f : MathF.Atan2(x, z);
        if (a < 0) a += MathF.Tau;
        int f = (int)(a * 4.7746487f);
        return f >= 30 ? f - 30 : f < 0 ? f + 30 : f;
    }

    /// <summary>The sweep's frames per turn: the original's step table cycles 0..29, one step every 0.2 s, a turn in 6 s.</summary>
    public const int SweepFrames = 30;
    /// <summary>The ping window's divisor: the original scales the steps by 2π/29 although the art has 30 frames, so pings run up to a step off the beam and the 29 -> 0 window is empty.</summary>
    public const int PingSteps = 29;

    /// <summary>The sweep frame after <paramref name="cur"/>.</summary>
    public static int NextSweep(int cur) => (cur + 1) % SweepFrames;

    /// <summary>
    /// The radar's ping test: a blip at scope position (x, z) pings when its bearing, atan2(x, z) in (-π, π], lies between the
    /// step the beam left (prev) and the one it shows (cur), each scaled by 2π/29 and taken into (-π, π] on its own. The original
    /// compares the two ends as they are, with no wrap: a window whose start is past its end (the 14 -> 15 step, where the beam
    /// crosses the bottom of the scope) holds nothing. Every pass pings again. A locked radar runs the same test every tick,
    /// with cur its target's frame and prev the step the sweep stopped on.
    /// </summary>
    public static bool InPingWedge(int prev, int cur, float x, float z)
    {
        float step = MathF.Tau / PingSteps;
        float a0 = prev * step, a1 = cur * step;
        if (a0 > MathF.PI) a0 -= MathF.Tau;
        if (a1 > MathF.PI) a1 -= MathF.Tau;
        float ang = MathF.Abs(x) < 1e-5f && MathF.Abs(z) < 1e-5f ? 0f : MathF.Atan2(x, z);
        return ang >= a0 && ang <= a1;
    }

    public static (float X, float Z)? Project(Rows carRot, Vector3 carPos, Vector3 contactPos, float range)
    {
        var d = carRot.ToLocal(contactPos - carPos);
        float nx = d.X / range, nz = d.Z / range;
        return nx * nx + nz * nz > 1 ? null : (nx, nz);
    }
}
