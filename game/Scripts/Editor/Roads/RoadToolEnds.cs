using Godot;
using I76.Editor.Intersections;
using I76.Editor.Roads;
using I76.Formats.Mission.Edit;
using V2 = System.Numerics.Vector2;

namespace I76.Game.Editor.RoadTools;

/// <summary>
/// The road-end choice (docs/map-editor.md, Road ends): with a road selected, each of its open ends can stay open or take an end cap, a
/// turn-around or (asphalt) a J strip where it turns to dirt; the pieces are the road tool's (<see cref="Junctions.SetEnd"/>), one undo step.
/// </summary>
public sealed partial class RoadTool
{
    private static readonly (RoadEnd End, string Name, string Tip)[] EndButtons =
    [
        (RoadEnd.Open, "OPEN", "The road just stops"),
        (RoadEnd.Cap, "CAP", "An end cap: dirt ijd27t1, planks ijw29t1; an asphalt road gets a J strip and a dirt cap beyond it, as the stock maps end one"),
        (RoadEnd.TurnAround, "TURN", "A turn-around (ica2n_1, 20 x 20) in the map's asphalt look"),
        (RoadEnd.ToDirt, "TO DIRT", "A J strip (ija2n71): the asphalt turns to dirt; draw a dirt road on from its far edge"),
    ];

    private VBoxContainer? _endBox;
    private readonly Dictionary<(bool AtStart, RoadEnd End), Button> _endButtons = [];
    private readonly Dictionary<bool, Label> _endLabels = [];
    private (int Sel, int Roads, int Pieces) _endsFor = (-2, -1, -1);

    private void BuildEndSection(VBoxContainer bx, float s)
    {
        _endBox = new VBoxContainer();
        _endBox.AddThemeConstantOverride("separation", (int)(4 * s));
        var l = new Label { Text = "ROAD ENDS   (the selected road's)" };
        EditorStyle.Body(l, 15 * s);
        _endBox.AddChild(l);
        foreach (bool atStart in new[] { true, false })
        {
            var name = new Label { Text = atStart ? "START" : "END" };
            EditorStyle.Body(name, 14 * s);
            _endBox.AddChild(name);
            _endLabels[atStart] = name;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", (int)(4 * s));
            foreach (var (end, text, tip) in EndButtons)
            {
                bool a = atStart; var e = end;
                var b = EditorStyle.Button(text, () => SetRoadEnd(a, e), s, 15, toggle: true);
                b.TooltipText = tip; b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(b);
                _endButtons[(atStart, end)] = b;
            }
            _endBox.AddChild(row);
        }
        _endBox.AddChild(new HSeparator());
        bx.AddChild(_endBox);
    }

    /// <summary>The end rows for the selected road: which choices its kind has and which one each end has now (when the selection, roads or pieces change).</summary>
    private void SyncEnds(bool force = false)
    {
        if (_endBox is null || !IsInstanceValid(_endBox)) return;
        bool show = Active && Mode == RoadMode.Select && _sel >= 0 && _sel < Roads.Count;
        _endBox.Visible = show;
        if (!show) { _endsFor = (-2, -1, -1); return; }
        var key = (_sel, Roads.Version, _junctions.Pieces.Version);
        if (!force && key == _endsFor) return;
        _endsFor = key;
        var road = Roads[_sel];
        var choices = Junctions.EndChoices(road.Kind);
        _syncing = true;
        foreach (bool atStart in new[] { true, false })
        {
            var now = OpenEnd(road, atStart, out string why);
            _endLabels[atStart].Text = (atStart ? "START" : "END") + (now is null ? $"   ({why})" : "");
            foreach (var (end, _, _) in EndButtons)
            {
                var b = _endButtons[(atStart, end)];
                b.Visible = choices.Contains(end);
                b.Disabled = now is null;
                b.SetPressedNoSignal(now == end);
            }
        }
        _syncing = false;
    }

    /// <summary>What an end of a road is finished with, or null with why it is not an open end (a junction, a bridge, another road).</summary>
    private RoadEnd? OpenEnd(RoadEdit road, bool atStart, out string why)
    {
        why = "";
        var at = Junctions.EndFrame(road, atStart).At;
        if (_structures.Any(e => V2.Distance(new V2(e.Position.X, e.Position.Z), at) < 3f)) { why = "on a bridge or ramp"; return null; }
        if (_junctions.EndOf(road, atStart) is not { } e) { why = !RoadBuilder.IsStandard(road) ? "a wide road" : "a junction or another road"; return null; }
        return e.End;
    }

    /// <summary>Finishes an end of the selected road as asked (one undo step with its pieces, ground and surface).</summary>
    public void SetRoadEnd(bool atStart, RoadEnd want)
    {
        if (_syncing) return;
        if (_sel < 0 || _sel >= Roads.Count) { Ed.Toast("Select a road first", true); return; }
        var road = Roads[_sel];
        if (OpenEnd(road, atStart, out string why) is null) { Ed.Toast($"That end is {why}: only an open end takes a cap", true); SyncEnds(true); return; }
        var cmd = _junctions.SetEnd(road, atStart, want, out why, Conform);
        if (cmd is null) { if (why.Length > 0) Ed.Toast(why, true); SyncEnds(true); return; }
        Ed.Undo.Push(cmd);
        GD.Print($"editor roads: road {_sel} {(atStart ? "start" : "end")}: {Junctions.Describe(want)}; {_junctions.Pieces.Count} pieces");
        Ed.Toast($"Road {_sel + 1}, {(atStart ? "start" : "end")}: {Junctions.Describe(want)}");
        SyncEnds(true);
    }
}
