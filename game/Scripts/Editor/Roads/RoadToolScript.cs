using Godot;
using I76.Editor.Roads;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.RoadTools;

public sealed partial class RoadTool
{
    private static float F(string[] a, int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// I76_EDITOR_SCRIPT commands "road:..." (docs/dev-hooks.md), for captures and tests without a mouse: mode,name | build,ground|bridge|tunnel | piece,name|auto | stretch | click,x,z[,shift] |
    /// finish | cursor,x,z (or off) | select,x,z | drag,x,z,toX,toZ[,single] | insert,x,z | delete | split,x,z | bulldoze,x,z |
    /// upgrade,x,z,kind | kind,n | grade,percent | conform,0/1 | snap,angle|grid|roads|structures,0/1 | shoulder,m | log.
    /// </summary>
    public void Script(string line)
    {
        int comma = line.IndexOf(',');
        string cmd = comma < 0 ? line : line[..comma];
        var args = comma < 0 ? [] : line[(comma + 1)..].Split(',');
        switch (cmd)
        {
            case "mode": SetMode(Enum.Parse<RoadMode>(args[0], true)); break;
            case "off": Deactivate(); break;
            case "build": SetBuild(Enum.Parse<BuildMode>(args[0], true)); break;
            case "piece": PieceName = args[0] == "auto" ? "" : args[0]; _planKey = 0; SyncPanel(); break;
            case "stretch": GD.Print(_stretch is { } sp ? $"editor roads: stretch {sp.Mode} {(sp.Ok ? sp.Summary : "problem: " + sp.Problem)}; warnings: {string.Join(" | ", sp.Warnings)}" : "editor roads: no stretch"); break;
            case "cursor": _virtual = args[0] == "off" ? null : new V2(F(args, 0), F(args, 1)); break;
            case "click":
                {
                    _virtual = new V2(F(args, 0), F(args, 1));
                    var cur = _virtual.Value;
                    UpdateSnap(cur, 1f);
                    if (Mode is RoadMode.Straight or RoadMode.Curve or RoadMode.Freeform) ClickDraw(args.Length > 2 && args[2] == "1", false);
                    else Press(false, false, false);
                    _planKey = 0;
                    break;
                }
            case "finish": Finish(false); break;
            case "select":
                _virtual = new V2(F(args, 0), F(args, 1));
                _sel = PickRoad(_virtual.Value); _selPt = -1;
                break;
            case "drag":
                {
                    var from = new V2(F(args, 0), F(args, 1)); var to = new V2(F(args, 2), F(args, 3));
                    _sel = PickRoad(from);
                    if (_sel < 0) { GD.Print("editor roads: nothing to drag there"); break; }
                    var pts = Roads[_sel].Points;
                    int k = Enumerable.Range(0, pts.Count).MinBy(i => V2.Distance(pts[i].Centre, from));
                    _dragSingle = args.Length > 4 && args[4] == "single";
                    int reach = _dragSingle ? 0 : Reach;
                    var after = RoadOps.MovePoint(Roads[_sel], k, to, reach);
                    int sel = _sel;
                    Edit(() => RoadEditing.Replace(Ed.Doc, Roads, sel, after, k - reach - 2, k + reach + 2, Conform, Grade(), Shoulders(), "Move road point"), "Move road point");
                    _selPt = k;
                    break;
                }
            case "insert": _virtual = new V2(F(args, 0), F(args, 1)); _sel = PickRoad(_virtual.Value); InsertAt(_virtual.Value); break;
            case "delete": DeleteSelection(); break;
            case "split":
                {
                    var at = new V2(F(args, 0), F(args, 1));
                    _sel = PickRoad(at);
                    if (_sel >= 0) { var pts = Roads[_sel].Points; _selPt = Enumerable.Range(0, pts.Count).MinBy(i => V2.Distance(pts[i].Centre, at)); SplitSelection(); }
                    break;
                }
            case "bulldoze":
                {
                    int r = PickRoad(new V2(F(args, 0), F(args, 1)));
                    if (r >= 0) Edit(() => RoadEditing.Bulldoze(Ed.Doc, Roads, r), "Bulldoze road");
                    break;
                }
            case "upgrade":
                {
                    int r = PickRoad(new V2(F(args, 0), F(args, 1)));
                    if (r >= 0) Edit(() => RoadEditing.Retype(Ed.Doc, Roads, r, uint.Parse(args[2])), "Change road type");
                    break;
                }
            case "kind": SetKind(uint.Parse(args[0]), false); break;
            case "grade": MaxGrade = F(args, 0) / 100f; SyncPanel(); break;
            case "conform": Conform = args[0] == "1"; SyncPanel(); break;
            case "shoulder": Shoulder = F(args, 0); SyncPanel(); break;
            case "snap":
                {
                    bool on = args[1] == "1";
                    switch (args[0]) { case "angle": Snap.Angle = on; break; case "grid": Snap.Grid = on; break; case "roads": Snap.Roads = on; break; case "structures": Snap.Structures = on; break; }
                    SyncPanel();
                    break;
                }
            case "pieces":
                GD.Print($"editor pieces: {_junctions.Pieces.Count}, dirty {_junctions.Pieces.Dirty}");
                foreach (var p in _junctions.Pieces.Items)
                    GD.Print($"  piece {p.Id}: {p.Def.Name} type {p.Data.ObjectType:x} at ({p.Data.Position.X:0.0}, {p.Data.Position.Y:0.0}, {p.Data.Position.Z:0.0}) yaw {p.Data.Yaw * 180 / MathF.PI:0.0}{(p.FromFile ? " (the map's)" : "")}");
                break;
            case "log":
                GD.Print($"editor roads: {Roads.Count} roads, {_structures.Count} structure ends (+{StructuresLeft} to read), dirty {Roads.Dirty}, painter {(_painter.Busy ? "busy" : $"idle, last {_painter.LastChunks} chunks in {_painter.LastMs:0} ms")}");
                for (int i = args.Length > 0 && args[0] == "all" ? 0 : Math.Max(0, Roads.Count - 3); i < Roads.Count; i++)
                    GD.Print($"  road {i}: kind {Roads[i].Kind}, {Roads[i].Points.Count} points, {Roads[i].Length():0} m{(Roads[i].Points.Count > 0 ? $", from {Roads[i].Points[0].Centre} to {Roads[i].Points[^1].Centre}" : "")}");
                break;
            case "structures":
                foreach (var s in _structures) GD.Print($"  structure end ({s.Position.X:0}, {s.Position.Y:0.0}, {s.Position.Z:0}) dir ({s.Direction.X:0.00}, {s.Direction.Y:0.00}) width {s.Width:0.0} {(s.RoadLevel ? "road level" : "raised")}");
                break;
            default: GD.PrintErr($"editor roads: unknown script command '{cmd}'"); break;
        }
    }
}
