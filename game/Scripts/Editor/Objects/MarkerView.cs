using Godot;
using I76.Editor.Objects;
using I76.Formats.Mission.Edit;
using I76.Game.Net;
using I76.Sim.Missions;

namespace I76.Game.Editor.Objects;

/// <summary>
/// The editor's drawing of the marker objects (spawn points, regen pads, race checkpoints, CTF flag bases) and of the selection:
/// flat, unshaded, in Godot's frame where a marker's forward is -Z (Coords.ToGodot puts the game's +Z there), so a node placed with
/// <c>Coords.ToGodot(rotation, position)</c> faces the right way.
/// </summary>
public static class MarkerView
{
    public static readonly Color SpawnGreen = new(0.35f, 0.95f, 0.35f), RegenOrange = new(1f, 0.6f, 0.15f), CheckRed = new(1f, 0.3f, 0.25f), FinishWhite = new(0.95f, 0.95f, 0.95f), Select = new(1f, 0.82f, 0.2f), Hover = new(1f, 1f, 1f, 0.55f);

    public static Color TeamColour(int team) => HudStyle.Team(MeleeKind.Ctf, Math.Clamp(team, 1, 4));

    private static readonly Dictionary<bool, StandardMaterial3D> Mats = [];

    /// <summary>One shared material for vertex-coloured geometry: unshaded, alpha, both faces; <paramref name="onTop"/> draws over the terrain.</summary>
    public static StandardMaterial3D Material(bool onTop = false)
    {
        if (Mats.TryGetValue(onTop, out var m) && GodotObject.IsInstanceValid(m)) return m;
        m = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = onTop, RenderPriority = onTop ? 20 : 5,
        };
        return Mats[onTop] = m;
    }

    private sealed class Geo
    {
        public readonly SurfaceTool Tris = new(), Lines = new();
        public Geo() { Tris.Begin(Mesh.PrimitiveType.Triangles); Lines.Begin(Mesh.PrimitiveType.Lines); }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col) { Tris.SetColor(col); Tris.AddVertex(a); Tris.SetColor(col); Tris.AddVertex(b); Tris.SetColor(col); Tris.AddVertex(c); }
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col) { Tri(a, b, c, col); Tri(a, c, d, col); }
        public void Line(Vector3 a, Vector3 b, Color col) { Lines.SetColor(col); Lines.AddVertex(a); Lines.SetColor(col); Lines.AddVertex(b); }
        public void Ring(Vector3 c, float r, int seg, Color col) { for (int i = 0; i < seg; i++) { float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg; Line(c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r, col); } }
        public void Disc(Vector3 c, float r, int seg, Color col) { for (int i = 0; i < seg; i++) { float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg; Tri(c, c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r, col); } }

        public Node3D Finish(bool onTop, string name)
        {
            var root = new Node3D { Name = name };
            var mat = Material(onTop);
            var t = Tris.Commit();
            if (t.GetSurfaceCount() > 0) root.AddChild(new MeshInstance3D { Mesh = t, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            var l = Lines.Commit();
            if (l.GetSurfaceCount() > 0) root.AddChild(new MeshInstance3D { Mesh = l, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            return root;
        }
    }

    public static Label3D Label(string text, Color colour, float height, float size = 0.0005f)
    {
        var l = new Label3D
        {
            Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = size, NoDepthTest = true,
            Modulate = colour, OutlineModulate = new Color(0.05f, 0.03f, 0.02f), OutlineSize = 10, FontSize = 64, Position = new Vector3(0, height, 0), RenderPriority = 30, OutlineRenderPriority = 29,
            Font = HudStyle.Bold,
        };
        return l;
    }

    /// <summary>A spawn point: a car-sized pad with an arrow at the front, and its label.</summary>
    public static Node3D Spawn(string label, Color colour)
    {
        var g = new Geo();
        var fill = new Color(colour, 0.35f);
        float w = 1.3f, back = 2.3f, front = -2.3f;
        g.Quad(new(-w, 0.2f, back), new(w, 0.2f, back), new(w, 0.2f, front), new(-w, 0.2f, front), fill);
        g.Tri(new(-w - 0.4f, 0.22f, front), new(w + 0.4f, 0.22f, front), new(0, 0.22f, front - 2.6f), new Color(colour, 0.75f));
        g.Line(new(-w, 0.25f, back), new(w, 0.25f, back), colour); g.Line(new(w, 0.25f, back), new(w, 0.25f, front), colour);
        g.Line(new(w, 0.25f, front), new(-w, 0.25f, front), colour); g.Line(new(-w, 0.25f, front), new(-w, 0.25f, back), colour);
        g.Line(new(0, 0.2f, 0), new(0, 5f, 0), colour);
        var n = g.Finish(false, "Spawn");
        n.AddChild(Label(label, colour, 5.8f));
        return n;
    }

    public static Node3D Regen(Color colour)
    {
        var g = new Geo();
        float h = 3.2f;
        g.Quad(new(-h, 0.2f, h), new(h, 0.2f, h), new(h, 0.2f, -h), new(-h, 0.2f, -h), new Color(colour, 0.28f));
        for (int i = 0; i < 4; i++) { float a = Mathf.Pi / 2 * i; var u = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)); var v = new Vector3(-u.Z, 0, u.X); g.Line(u * h + v * h + new Vector3(0, 0.25f, 0), v * h - u * h + new Vector3(0, 0.25f, 0), colour); }
        g.Quad(new(-0.5f, 0.3f, 2f), new(0.5f, 0.3f, 2f), new(0.5f, 0.3f, -2f), new(-0.5f, 0.3f, -2f), new Color(colour, 0.85f));
        g.Quad(new(-2f, 0.3f, 0.5f), new(2f, 0.3f, 0.5f), new(2f, 0.3f, -0.5f), new(-2f, 0.3f, -0.5f), new Color(colour, 0.85f));
        var n = g.Finish(false, "Regen");
        n.AddChild(Label("REGEN", colour, 4.2f));
        return n;
    }

    /// <summary>A race checkpoint: a pole with a banner, and the 30 m sphere the game tests (a ring on the ground).</summary>
    public static Node3D Checkpoint(int number, bool showRing = true)
    {
        bool finish = number >= 99;
        var colour = finish ? FinishWhite : CheckRed;
        var g = new Geo();
        g.Line(new(0, 0, 0), new(0, 9f, 0), colour);
        g.Quad(new(0, 9f, 0), new(0, 9f, -4.5f), new(0, 6.2f, -4.5f), new(0, 6.2f, 0), new Color(colour, 0.9f));
        g.Quad(new(0, 9f, 0), new(0, 9f, 4.5f), new(0, 6.2f, 4.5f), new(0, 6.2f, 0), new Color(colour, 0.9f));
        if (showRing)
        {
            g.Ring(new(0, 0.4f, 0), MapChecks.CheckpointRadius, 72, new Color(colour, 0.85f));
            g.Disc(new(0, 0.3f, 0), MapChecks.CheckpointRadius, 48, new Color(colour, 0.06f));
        }
        var n = g.Finish(true, "Checkpoint");
        n.AddChild(Label(finish ? "FINISH" : number.ToString(), colour, 11f, 0.0007f));
        return n;
    }

    /// <summary>The ring a CTF flag stands in, in its team's colour.</summary>
    public static Node3D FlagBase(int team)
    {
        var c = TeamColour(team);
        var g = new Geo();
        g.Ring(new(0, 0.35f, 0), 6f, 40, c); g.Ring(new(0, 0.35f, 0), 5.5f, 40, c);
        g.Disc(new(0, 0.3f, 0), 6f, 40, new Color(c, 0.22f));
        var n = g.Finish(true, "FlagBase");
        n.AddChild(Label($"TEAM {team} FLAG", c, 24f));
        return n;
    }

    /// <summary>The route: a line through the checkpoints in their order with an arrowhead on each leg, and a dim leg back to the start for the next lap.</summary>
    public static Node3D Route(IReadOnlyList<(Godot.Vector3 At, int Number)> stops)
    {
        var g = new Geo();
        var gold = new Color(1f, 0.78f, 0.2f, 0.95f);
        void Leg(Vector3 a, Vector3 b, Color c)
        {
            var d = b - a; float len = d.Length();
            if (len < 1) return;
            var dir = d / len;
            g.Line(a, b, c);
            var mid = (a + b) / 2;
            var side = new Vector3(-dir.Z, 0, dir.X);
            float s = Math.Clamp(len * 0.08f, 5f, 14f);
            g.Tri(mid + dir * s, mid - dir * s * 0.6f + side * s * 0.55f, mid - dir * s * 0.6f - side * s * 0.55f, c);
        }
        for (int i = 0; i + 1 < stops.Count; i++) Leg(stops[i].At, stops[i + 1].At, gold);
        if (stops.Count >= 2)
        {
            // the lap: after the last (or the finish) back to checkpoint 1
            var last = stops[^1].At; var first = stops.FirstOrDefault(s => s.Number == 1).At;
            if (stops.Any(s => s.Number == 1) && stops[^1].Number != 1) Leg(last, first, new Color(gold, 0.4f));
        }
        return g.Finish(true, "Route");
    }

    /// <summary>A wire box (in the object's frame, Godot axes), for the selection and the hover.</summary>
    public static Node3D WireBox(Vector3 min, Vector3 max, Color colour)
    {
        var g = new Geo();
        Vector3 P(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        for (int i = 0; i < 8; i++)
            for (int b = 0; b < 3; b++)
                if ((i & (1 << b)) == 0) g.Line(P(i), P(i | (1 << b)), colour);
        return g.Finish(true, "Box");
    }

    /// <summary>A flat footprint outline at the ground (the ghost's), turned red when it is not allowed.</summary>
    public static Node3D Outline(Vector3[] corners, Color colour)
    {
        var g = new Geo();
        for (int i = 0; i < corners.Length; i++) g.Line(corners[i], corners[(i + 1) % corners.Length], colour);
        return g.Finish(true, "Outline");
    }
}
