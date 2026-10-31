using Godot;
using I76.Formats.Input;
using I76.Game.Sim;
using I76.Net;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game.Net;

/// <summary>
/// The players' names over their cars in a network game, and the original's TOGGLE_ALL_NAMES game key (Ctrl+T, 0x28):
/// the toggle flips a flag and the draw reads it. Off, only the radar target's name is drawn (when it is a
/// player's car); on, every player's car that is alive and engine-on. The text is the player's name,
/// centred above the car in the team's colour, when it is in front of the camera and on screen; it
/// does not depend on the view. The key exists only in network games; the table starts on and the last choice is kept in shell.cfg (the original's started off and was not saved).
/// </summary>
public partial class NetNames : CanvasLayer
{
    public required ClientWorld World { get; init; }
    public required Dictionary<Vehicle, CarNode> Nodes { get; init; }
    /// <summary>Whether a key press may act now (not typing a chat line, over a menu or a picture).</summary>
    public Func<bool>? CanAct { get; init; }
    /// <summary>Every name shown: on by default and as the player last left it (shell.cfg [port] net_names), unlike the original's table, which started off.</summary>
    public bool All { get; private set; } = I76.Game.Shell.ShellSettings.Load().NetNames;
    /// <summary>The game's kind, for the modern look's team colours (HudStyle.Team: the base game's eight from their names).</summary>
    public I76.Sim.Missions.MeleeKind Kind { get; init; }
    /// <summary>The modern look is in use: the names in Arial Bold and the HUD's team colours (else the default face and the flags' colours).</summary>
    public Func<bool>? Modern { get; init; }

    private const float Range = 800f;
    /// <summary>The most names drawn at once, nearest first.</summary>
    public const int MaxNames = 12;

    /// <summary>A name's opacity by distance: full within 150 m, down to a third at the range's end.</summary>
    public static float Alpha(float d) => Mathf.Lerp(1f, 0.35f, Mathf.Clamp((d - 150f) / (Range - 150f), 0f, 1f));
    private Control _canvas = null!;

    public override void _Ready()
    {
        Layer = 9;
        _canvas = new NamesCanvas { Owner2 = this, MouseFilter = Control.MouseFilterEnum.Ignore };
        _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_canvas);
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && !Controls.Suspended && CanAct?.Invoke() != false
            && GameKeys.CommandOf(k) == GameKey.ToggleAllNames)
        {
            All = !All;
            var ls = I76.Game.Shell.ShellSettings.Load();
            ls.NetNames = All;
            ls.Save();
        }
    }

    /// <summary>The cars whose names are drawn now: the target alone, or every player's car that is alive with its engine on.</summary>
    public IEnumerable<MirrorCar> Shown()
    {
        if (World.Own is not { } me) yield break;
        if (!All)
        {
            if (World.Setup.Combat?.RadarTarget.GetValueOrDefault(me.Vehicle) is { } t && World.Cars.Values.FirstOrDefault(c => c.Vehicle == t) is { IsPlayer: true } tc && Visible(tc))
                yield return tc;
            yield break;
        }
        foreach (var c in World.Cars.Values)
            if (c != me && c.IsPlayer && c.Present && Visible(c)) yield return c;
    }

    private static bool Visible(MirrorCar c) => c.Vehicle.Flags.HasFlag(VehicleFlags.EngineOn) && !Combat.IsDead(c.Vehicle);

    private partial class NamesCanvas : Control
    {
        public required NetNames Owner2 { get; init; }

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            var cam = GetViewport().GetCamera3D();
            if (cam is null) return;
            var size = GetViewportRect().Size;
            bool modern = Owner2.Modern?.Invoke() == true;
            var font = modern ? HudStyle.Bold : ThemeDB.FallbackFont;
            int fs = modern ? Math.Max(16, (int)Math.Round(18 * size.Y / 1080f)) : 16;
            // the port's, for games of many players: the nearest MaxNames only, a name that would sit on a nearer one is
            // skipped, and the far ones fade (all 63 names would cover the screen)
            var cands = new List<(MirrorCar C, CarNode Node, Vector2 S, float D)>();
            foreach (var c in Owner2.Shown())
            {
                if (!Owner2.Nodes.TryGetValue(c.Vehicle, out var node) || !IsInstanceValid(node) || !node.Visible) continue;
                var v = c.Vehicle;
                // above the roof
                var w = node.GlobalTransform * Coords.ToGodot(new System.Numerics.Vector3(0, (v.BoxY.Length > 3 ? v.BoxY[3] : 1.5f) + 0.6f, 0));
                float d = (w - cam.GlobalPosition).Length();
                if (cam.IsPositionBehind(w) || d > Range) continue;
                var s = cam.UnprojectPosition(w);
                if (s.X < 0 || s.Y < 0 || s.X > size.X || s.Y > size.Y) continue;
                cands.Add((c, node, s, d));
            }
            cands.Sort((x, y) => x.D.CompareTo(y.D));
            var taken = new List<Rect2>();
            int drawn = 0;
            foreach (var (c, _, s, d) in cands)
            {
                if (drawn >= MaxNames) break;
                var v = c.Vehicle;
                string name = c.Info.Owner < 0 ? I76.Formats.Shell.MeleeDrivers.ForFillEntity(c.Info.Name) ?? c.Info.Name
                    : c.Info.Name.Length > 0 ? c.Info.Name : $"player {c.Info.Owner + 1}";
                var width = font.GetStringSize(name, HorizontalAlignment.Left, -1, fs).X;
                var at = new Vector2(s.X - width / 2, s.Y - 9);
                var box = new Rect2(at.X, at.Y - fs + 2, width, fs + 4);
                if (taken.Any(t => t.Intersects(box))) continue;
                taken.Add(box);
                drawn++;
                int team = Owner2.World.Setup.Combat?.Team(v) ?? 0;
                var col = modern ? HudStyle.Team(Owner2.Kind, team) : NetHud.TeamColour(team);
                col.A = Alpha(d);
                var ink = modern ? new Color(HudStyle.Ink, 0.85f * col.A) : new Color(0, 0, 0, col.A);
                DrawStringOutline(font, at, name, HorizontalAlignment.Left, -1, fs, modern ? Math.Max(5, fs / 3) : 5, ink);
                DrawString(font, at, name, HorizontalAlignment.Left, -1, fs, col);
            }
        }
    }
}
