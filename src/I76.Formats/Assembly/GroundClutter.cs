using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// The ground clutter ("scrounge" cards): the engine scatters sixteen one-quad cards round the
/// camera, a fixed pattern repeated every 100 m over the 3x3 cells about the camera's, each picked by the terrain surface under
/// it. This is the placement only (which card, where), a pure function of the camera and the map.
/// </summary>
public static class GroundClutter
{
    public const double Cell = 100;
    public const double DepthLimit = 120.00001;
    public const double HeightLimit = 5.0;

    /// <summary>B01..B16: x and z in metres within a cell (the same in ngscrng1.sdf and ngscrng2.sdf).</summary>
    public static readonly (float X, float Z)[] Points =
    [
        (6.95f, 14.30f), (27.35f, 9.00f), (86.45f, 15.40f), (10.05f, 36.70f),
        (17.55f, 33.80f), (55.65f, 26.30f), (64.25f, 30.40f), (9.15f, 59.20f),
        (12.05f, 60.90f), (66.25f, 50.10f), (90.85f, 50.50f), (34.55f, 74.80f),
        (37.65f, 76.00f), (11.85f, 94.40f), (72.95f, 88.60f), (75.75f, 89.80f),
    ];

    /// <summary>The 3x3 cell offsets in the engine's order.</summary>
    public static readonly (int X, int Z)[] Offsets =
    [
        (-100, -100), (0, -100), (100, -100), (-100, 0), (0, 0), (100, 0), (-100, 100), (0, 100), (100, 100),
    ];

    /// <summary>The detail digit (1 NG1_, 2 NG2_, 3 NG3_) the surface id selects, or 0 for no clutter.</summary>
    public static int DetailOf(int surface) => surface switch { 0 => 1, 1 => 2, 5 => 3, _ => 0 };

    /// <summary>The cell holding the camera: its corner, the coordinate floored to 100.</summary>
    public static double CellOf(double c) => Math.Floor(c / Cell) * Cell;

    /// <summary>The camera: its position, its view axes (game frame, right-handed as the view: x right, y up, z forward) and the tangents of its half field of view.</summary>
    public readonly record struct View(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float TanH, float TanV);

    /// <summary>One card to draw: its point (0..15), its detail digit and where it stands (game frame).</summary>
    public readonly record struct Card(int Point, int Detail, Vector3 Position);

    /// <summary>
    /// The cards the engine draws this frame. <paramref name="height"/> is the bilinear ground height and
    /// <paramref name="surface"/> the surface id. A card is dropped when its ground stands over 5 m above the
    /// ground under the camera, when it is over 120 m deep in the view, or outside the frustum (a sphere of radius 2, 2.2 on
    /// the side planes).
    /// </summary>
    public static List<Card> Place(View v, Func<double, double, double> height, Func<double, double, int> surface)
    {
        var o = new List<Card>(144);
        double ax = CellOf(v.Position.X), az = CellOf(v.Position.Z);
        double g0 = height(v.Position.X, v.Position.Z);
        double cosH = 1 / Math.Sqrt(1 + (double)v.TanH * v.TanH), cosV = 1 / Math.Sqrt(1 + (double)v.TanV * v.TanV);
        for (int p = 0; p < Points.Length; p++)
            foreach (var (ox, oz) in Offsets)
            {
                double x = ax + ox + Points[p].X, z = az + oz + Points[p].Z;
                int detail = DetailOf(surface(x, z));
                if (detail == 0) continue;
                double y = height(x, z);
                if (y - g0 > HeightLimit) continue;
                var d = new Vector3((float)(x - v.Position.X), (float)(y - v.Position.Y), (float)(z - v.Position.Z));
                double vx = Vector3.Dot(d, v.Right), vy = Vector3.Dot(d, v.Up), vz = Vector3.Dot(d, v.Forward);
                if (vz > DepthLimit) continue;
                if (Outside(vx, vy, vz, v.TanH, v.TanV, cosH, cosV)) continue;
                o.Add(new Card(p, detail, new Vector3((float)x, (float)y, (float)z)));
            }
        return o;
    }

    /// <summary>A sphere of radius 2 at view-space (x, y, z) wholly outside a side, top or bottom plane, or behind the eye.</summary>
    private static bool Outside(double x, double y, double z, double tanH, double tanV, double cosH, double cosV)
    {
        if (z < -2) return true;
        // the plane through the eye with normal (cosH, 0, -sinH) and its mirror: distance = (|x| - z tanH) cosH
        if ((Math.Abs(x) - z * tanH) * cosH > 2.2) return true;
        if ((Math.Abs(y) - z * tanV) * cosV > 2.0) return true;
        return false;
    }
}
