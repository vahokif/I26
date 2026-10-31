using Godot;
using I76.Game.Sim;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game.Ui;

/// <summary>
/// The box around the radar's current target, as the original draws one:
/// corner brackets fitted to the target's projected collision box, its
/// health as a bar above it (just over the box in the main
/// view, coloured by thirds, white while unhurt), and its distance beneath. Red for the other side, green
/// for ours, grey for nobody's; when the target is off screen, an arrow at the
/// screen's edge points the way.
/// </summary>
public partial class TargetIndicator : Control
{
    public required Func<Vehicle?> Player { get; init; }
    public required Combat Combat { get; init; }
    public required Func<System.Numerics.Vector3> Origin { get; init; }
    /// <summary>
    /// The node the target is drawn as. The box follows it rather than the
    /// sim's position: a far car (the cheap path, flag 0x20000) keeps its
    /// height while it drives and is drawn on the ground, and drawn cars are
    /// interpolated between ticks.
    /// </summary>
    public Func<Vehicle, Node3D?>? CarOf { get; init; }
    /// <summary>The 1997 look's box (the original's sprites, bar and starburst), drawn instead while it is on.</summary>
    public TargetIndicator1997? Look1997 { get; init; }

    /// <summary>
    /// Drawn inside the rear-view mirror's viewport (inMirror = 1): the same box and health bar, no
    /// off-screen marker and no distance. The mirror's shader flips the picture left-right, so the bar fills from the other side.
    /// </summary>
    public bool Mirror { get; init; }

    public override void _Ready()
    {
        AnchorRight = 1; AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta) => QueueRedraw();

    /// <summary>The target's health as a 0–1 fraction for the bar (the health percentage: HealthGauge); none left once it is dead.</summary>
    private static float Health(Vehicle v) => Combat.IsDead(v) ? 0 : I76.Sim.Damage.HealthGauge.Fraction(v);

    public override void _Draw()
    {
        if (Player() is not { } p || Combat.RadarTarget.GetValueOrDefault(p) is not { } t || Combat.IsDead(t)) return;
        var cam = GetViewport().GetCamera3D();
        if (cam is null) return;
        if (Look1997?.On() == true) { Look1997.Draw(this, cam, t, Health(t), Mirror); return; }
        var origin = Origin();
        int team = Combat.Team(t), mine = Combat.Team(p);
        var col = Combat.IsDead(t) ? new Color(0.5f, 0.5f, 0.5f) : team == 0 ? new Color(0.85f, 0.85f, 0.85f) : team == mine ? new Color(0.35f, 1f, 0.45f) : new Color(1f, 0.3f, 0.2f);
        var size = GetViewportRect().Size;

        var node = CarOf?.Invoke(t) is { } n && IsInstanceValid(n) ? n : null;
        // the eight corners of its box, on screen
        var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
        int visible = 0;
        foreach (var x in new[] { t.BoxX[0], t.BoxX[3] })
            foreach (var y in new[] { t.BoxY[0], t.BoxY[3] })
                foreach (var z in new[] { t.BoxZ[0], t.BoxZ[3] })
                {
                    var corner = new System.Numerics.Vector3(x, y, z);
                    var w = node is not null ? node.GlobalTransform * Coords.ToGodot(corner)
                        : Coords.ToGodot(t.Pos.ToVector3() + t.Rot.ToWorld(corner) - origin);
                    if (cam.IsPositionBehind(w)) continue;
                    var s = cam.UnprojectPosition(w);
                    min = min.Min(s); max = max.Max(s);
                    visible++;
                }
        float dist = (t.Pos.ToVector3() - p.Pos.ToVector3()).Length();
        var font = ThemeDB.FallbackFont;

        bool onScreen = visible == 8 && max.X > 0 && max.Y > 0 && min.X < size.X && min.Y < size.Y;
        if (!onScreen && Mirror) return;
        if (!onScreen)
        {
            // an arrow at the edge, toward the target
            var centre = node?.GlobalPosition ?? Coords.ToGodot(t.Pos.ToVector3() - origin);
            var local = cam.GlobalTransform.AffineInverse() * centre;
            var dir = new Vector2(local.X, -local.Y);
            if (dir.LengthSquared() < 1e-6f) dir = Vector2.Up;
            dir = dir.Normalized();
            var c = size / 2;
            float r = Mathf.Min(size.X, size.Y) * 0.42f;
            var tip = c + dir * r;
            var side = new Vector2(-dir.Y, dir.X);
            DrawColoredPolygon([tip + dir * 14, tip - dir * 6 + side * 10, tip - dir * 6 - side * 10], col);
            DrawString(font, tip - dir * 26 - new Vector2(20, -5), $"{dist:0} m", HorizontalAlignment.Center, 40, 13, col);
            return;
        }

        // pad small boxes so the brackets stay readable at range
        var mid = (min + max) / 2;
        float u = Mirror ? size.X / 256f : 1;   // the mirror's picture is 256x64 in the 1997 size, four times that in the modern one
        var half = ((max - min) / 2).Max(new Vector2(14, 10) * u) + new Vector2(6, 6) * u;
        min = mid - half; max = mid + half;
        float arm = Mathf.Clamp(Mathf.Min(half.X, half.Y) * 0.6f, 6 * u, 22 * u);
        float W = 2.5f * u;
        var outline = new Color(0, 0, 0, 0.55f);
        foreach (var (corner, sx, sy) in new[] { (min, 1, 1), (new Vector2(max.X, min.Y), -1, 1), (new Vector2(min.X, max.Y), 1, -1), (max, -1, -1) })
        {
            foreach (var (c, w) in new[] { (outline, W + 2), (col, W) })
            {
                DrawLine(corner, corner + new Vector2(sx * arm, 0), c, w);
                DrawLine(corner, corner + new Vector2(0, sy * arm), c, w);
            }
        }

        // the health bar over the box, in the original's bands; the distance under it
        float hp = Health(t);
        float bw = Mathf.Max(max.X - min.X, 60 * u), bh = 6 * u;
        var bar = new Rect2(new Vector2(mid.X - bw / 2, min.Y - 6 * u - bh), new Vector2(bw, bh));
        DrawRect(bar.Grow(1), outline);
        DrawRect(bar, new Color(0.15f, 0.15f, 0.15f, 0.7f));
        // white unhurt, then green, yellow and red by thirds (the colours the original builds)
        var fill = hp >= 1 ? new Color(1f, 1f, 1f) : hp > 2 / 3f ? new Color(0.32f, 0.61f, 0.32f) : hp > 1 / 3f ? new Color(0.8f, 0.78f, 0.13f) : new Color(0.82f, 0f, 0.16f);
        DrawRect(new Rect2(new Vector2(Mirror ? bar.End.X - bw * hp : bar.Position.X, bar.Position.Y), new Vector2(bw * hp, bh)), fill);
        if (!Mirror) DrawString(font, new Vector2(mid.X - 40, max.Y + 18), $"{dist:0} m", HorizontalAlignment.Center, 80, 13, col);
    }
}
