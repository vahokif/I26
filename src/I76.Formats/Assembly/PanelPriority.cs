using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// The cockpit's roof panels (weapons WEP, specials SPC, damage SYS, radar RADR). The original draws the interior as sorted
/// faces (a tree on each face's centroid against the faces' planes, painted back to front, no depth buffer),
/// so a small panel whose centroid is on the eye's side of a roof face lies wholly over it. A depth buffer cuts the panel
/// where one corner pokes through a sloping roof (the Goon's weapons panel, #32). The port draws the panels shrunk toward
/// the eye, which looks identical from it and puts them in front. Only those above the eye: every car's specials panel
/// (SPC) sits on the dash below it, behind the steering wheel (SWHL), and pulled forward it was drawn over the wheel (#36).
/// </summary>
public static class PanelPriority
{
    /// <summary>How far toward the eye: well inside any roof face a panel touches, well outside the near plane (0.05; the panels sit 0.2 to 0.3 away).</summary>
    public const float Scale = 0.6f;

    /// <summary>A detail-5 node of that name (GO51WEP6, GO51RADR ...) is one of them.</summary>
    public static bool IsRoofPanel(string node) =>
        node.Length == 8 && (node[4..7] is "WEP" or "SPC" or "SYS" || node[4..8] == "RADR");

    /// <summary>Whether a part is drawn nearer: one of the panels, its vertices on average above the eye (engine frame, Y up).</summary>
    public static bool PullForward(ModelPart part, Vector3 eye)
    {
        if (!IsRoofPanel(part.NodeName) || part.Mesh.Vertices.Length == 0) return false;
        float y = 0;
        foreach (var v in part.Mesh.Vertices) y += part.ToModel.Apply(v).Y;
        return y / part.Mesh.Vertices.Length > eye.Y;
    }
}
