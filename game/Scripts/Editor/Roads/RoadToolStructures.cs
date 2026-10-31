using Godot;
using I76.Editor;
using I76.Editor.Intersections;
using I76.Editor.Objects;
using I76.Editor.Roads;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.RoadTools;

/// <summary>
/// The road tool's bridge and tunnel modes (docs/map-editor.md, Bridges and tunnels). With BRIDGE or TUNNEL on, STRAIGHT, CURVE and FREEFORM all
/// lay one straight stretch from the first click toward the second: it is planned in <see cref="RoadStructures"/> (whole pieces, one grade,
/// the ground brought to the deck under each joint) and shown as ghosts of the pieces; the second click makes it, with the ground and
/// the cut roads, as one undo step. Ground roads go on from its end through the usual snap to structure ends.
/// </summary>
public sealed partial class RoadTool
{
    /// <summary>What the next stretch is made of; kept for the session.</summary>
    public static BuildMode Build = BuildMode.Ground;
    /// <summary>The model chosen in the panel for a bridge or tunnel; empty for the automatic choice.</summary>
    public static string PieceName = "";

    private StretchPlan? _stretch;
    private bool _catalogueReady;

    /// <summary>The stretch tools are on: a draw tool with BRIDGE or TUNNEL.</summary>
    public bool IsStretch => Active && Build != BuildMode.Ground && Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform;

    private List<CatalogueItem> Catalogue => Ed.Install is null ? [] : Ed.Objects.Catalogue;

    /// <summary>The models the panel offers for the current mode.</summary>
    public List<CatalogueItem> StretchPiecesNow()
    {
        var list = StretchPieces.For(Catalogue, Build);
        if (!_catalogueReady && list.Count > 0)
        {
            _catalogueReady = true;
            foreach (var i in list) Ed.Objects.EnsureEnds(i);      // the decks' ends as the sim's collider has them
        }
        return list;
    }

    private CatalogueItem? ChosenPiece => PieceName.Length == 0 ? null : StretchPiecesNow().FirstOrDefault(i => i.Name == PieceName);

    public void SetBuild(BuildMode m, bool toast = true)
    {
        if (!Active && m != BuildMode.Ground) SetMode(RoadMode.Straight);
        if (Active && Build == BuildMode.Ground && m != BuildMode.Ground) ResumeGroundIfDrawn();
        Build = m;
        if (m == BuildMode.Ground || ChosenPiece is null || (m == BuildMode.Bridge) != StretchPieces.IsBridge(ChosenPiece)) PieceName = "";
        _stretch = null; _planKey = 0;
        if (m == BuildMode.Ground) _vis.ShowStretch(null, _ => null, _ => 0);
        if (Mode is RoadMode.Select or RoadMode.Upgrade or RoadMode.Bulldoze && m != BuildMode.Ground) SetMode(RoadMode.Straight);
        SyncPanel();
        Ed.RoadToolChanged();
        if (toast) Ed.Toast(m switch
        {
            BuildMode.Bridge => "Bridge: click where it starts, click toward the far end. It snaps to whole pieces and runs straight on one grade.",
            BuildMode.Tunnel => "Tunnel: click where it starts, click toward the far end. One tunnel piece, level, with the road through it.",
            _ => "Ground: the road follows the terrain.",
        });
    }

    public void CycleBuild() => SetBuild((BuildMode)(((int)Build + 1) % 3));

    /// <summary>A ground road half drawn when the mode changes is made as far as it goes, and the new stretch starts where it ended (mixed drawing).</summary>
    private void ResumeGroundIfDrawn()
    {
        if (_draft is not { } d || d.Clicks.Count < 2 && d.Done.Count == 0) return;
        var at = d.Clicks[^1];
        Finish(false);
        if (_draft is not null) return;                       // it was not made (a problem was toasted)
        int idx = _sel >= 0 && _sel < Roads.Count ? _sel : Roads.Count - 1;
        if (idx >= 0 && Roads[idx].Points.Count > 1 && V2.Distance(Roads[idx].Points[^1].Centre, at) < 2f)
            Resume(at, new SnapResult(Roads[idx].Points[^1].Centre, SnapKind.RoadEnd, idx, Roads[idx].Points.Count - 1, false));
    }

    private void Resume(V2 at, SnapResult start)
    {
        _draft = new RoadDraft { Mode = I76.Editor.Roads.DrawMode.Straight, Start = start };
        _draft.Clicks.Add(at);
        _planKey = 0;
    }

    // ---- clicking and committing ----------------------------------------------------------------------------------------------

    private void ClickStretch(bool shift)
    {
        if (_draft is null)
        {
            _draft = new RoadDraft { Mode = I76.Editor.Roads.DrawMode.Straight, Start = _snap };
            _draft.Clicks.Add(_snap.Point);
            _planKey = 0;
            return;
        }
        CommitStretch(shift);
    }

    private void CommitStretch(bool shift)
    {
        if (_draft is null) return;
        _planKey = 0; UpdateStretch();                       // from where the cursor is now, not from the last frame
        if (_stretch is not { } plan) return;
        if (!plan.Ok) { Ed.Toast(plan.Problem ?? "Nothing to lay", true); return; }
        var start = _draft.Start;
        var before = Build == BuildMode.Tunnel ? _junctions.Take() : null;
        var res = RoadStructures.Commit(Ed.Doc, Ed.Objects.Layer, Roads, plan, Ed.Objects.CutRoads, Grade(), Shoulders(), Kind, start, Conform);
        if (res is null) { Ed.Toast("Could not lay that", true); return; }
        var follow = before is not null ? _junctions.Settle(before, "Junctions", Conform) : null;
        Ed.Undo.Push(Junctions.Join(res.Command.Name, res.Command, follow));
        Ed.Toast(res.Message + (plan.Warnings.Count > 0 ? "  (" + plan.Warnings.Count + " warning" + (plan.Warnings.Count > 1 ? "s" : "") + ")" : ""));
        bool go = shift || Chain;
        Cancel();
        _sel = -1;
        if (go)
        {
            // on from its end: a bridge's far deck end (height and heading pinned), a tunnel's road end
            if (plan.Mode == BuildMode.Bridge)
            {
                var a = new StructureAttach.Attach(new System.Numerics.Vector3(plan.End.X, plan.EndHeight, plan.End.Y), plan.Dir, RoadStructures.BridgeWidth, false) { Slope = plan.Grade };
                Resume(plan.End, new SnapResult(plan.End, SnapKind.Structure, Structure: a));
            }
            else if (Roads.Count > 0)
            {
                int idx = Roads.Count - 1;
                for (int i = 0; i < Roads.Count; i++) if (V2.Distance(Roads[i].Points[^1].Centre, plan.End) < 2f) idx = i;
                Resume(plan.End, new SnapResult(plan.End, SnapKind.RoadEnd, idx, Roads[idx].Points.Count - 1, false));
            }
        }
    }

    // ---- the preview ------------------------------------------------------------------------------------------------------------

    private void UpdateStretch()
    {
        if (_draft is null) { _stretch = null; _vis.ShowStretch(null, _ => null, _ => 0); return; }
        int key = HashCode.Combine(_snap.Point, _draft.Start?.Point, Build, PieceName, Conform, MaxGrade, Ed.Objects.Layer.Version, Roads.Version);
        if (key == _planKey) return;
        _planKey = key;
        _vis.ShowRoad(null, null); _vis.ShowGrade(null, null, null, 0);
        _proposals = []; _pieceView.Show(_proposals, GroundV);
        var s = _draft.Start;
        float? h = null; V2? heading = null;
        if (s is { Kind: SnapKind.Structure, Structure: { } st }) { h = st.Position.Y; heading = st.Direction; }
        else if (s is { Kind: SnapKind.RoadEnd } re) h = GroundV(re.Point);     // a road's end is on the ground: a tunnel from it is level at its height
        var world = Ed.Builder.World!;
        var plan = RoadStructures.Plan(new StretchQuery
        {
            Catalogue = Catalogue, Mode = Build, Start = _draft.Clicks[0], Aim = _snap.Point, Ground = GroundV, Only = ChosenPiece, Heading = heading, StartHeight = h,
            MaxGrade = RoadStructures.BridgeMaxGrade,
            OnMap = p => p.X > 5 && p.Y > 5 && world.BilinearHeight(p.X, p.Y) > -1 && Ed.Doc.Has((int)(p.X / 5), (int)(p.Y / 5)),
        });
        _stretch = plan;
        _problem = plan.Problem ?? "";
        _hint = plan.Ok ? string.Join("  ", new[] { plan.Summary }.Concat(plan.Notes).Concat(plan.Warnings)) : "";
        _vis.ShowStretch(plan, MeshOf, GroundV);
    }

    private Mesh? MeshOf(CatalogueItem item) => item.Definition.Length > 0 && Ed.Builder.Scenery!.EditMeshOf(item.Definition) is { } m ? m.Mesh : null;

    private string StretchReadout()
    {
        if (_stretch is not { } p) return Build == BuildMode.Ground ? "" : "Click the start of the " + (Build == BuildMode.Bridge ? "bridge" : "tunnel") + ".";
        if (!p.Ok) return p.Problem ?? "";
        return $"{p.Tiling!.Describe()}\n{p.Run:0} m   grade {p.Grade * 100:+0.0;-0.0;0}%\nstart {p.StartHeight:0.0} m   end {p.EndHeight:0.0} m{(p.Warnings.Count > 0 ? $"\n{p.Warnings.Count} warning{(p.Warnings.Count > 1 ? "s" : "")}" : "")}";
    }
}
