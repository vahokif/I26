using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The Nitro Pack's SCENARIO·DRIVER ENTRY FORM, 0xc024: background 21,
/// the character, his missions with a green stroke through the done ones, the three cars the selected mission recommends, and any of
/// the 31 scenario cars with their variants and CONFIGURE CHASSIS. ENTER AREA starts the mission with the chosen car, fresh.
/// </summary>
public sealed class ScenarioScreen : ShellScreen
{
    public int Character { get; init; }

    // the hotspot table (id → x0, x1, y0, y1)
    private static readonly (int Id, int X0, int X1, int Y0, int Y1)[] Spots =
    [
        (0, 200, 215, 44, 58), (1, 31, 216, 150, 270), (2, 9, 27, 128, 144), (3, 9, 27, 250, 268), (4, 9, 216, 306, 390),
        (5, 610, 621, 400, 415), (6, 437, 449, 419, 434), (7, 456, 616, 422, 435), (8, 229, 369, 453, 471), (9, 489, 630, 453, 471),
    ];
    private const int Row = 17, Rows = 6;

    private ShellFont _font = null!;
    private SpriteBank _cars = null!, _marks = null!;
    private MeleeData _data = null!;
    private NitroProgress _progress = null!;
    private NitUserMel _user = null!;
    private int _ch, _sel, _scroll, _model, _variant = 1;
    private bool _built;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();

    private ScenarioCharacter Char => _progress.Data.Characters[_ch];
    private ScenarioMission Mission => Char.Missions[_sel];

    public override void Enter()
    {
        if (!_built)
        {
            _built = true;
            _font = Shell.Db.Font(0x45);
            _cars = Shell.Db.BankNative(54);
            _marks = Shell.Db.BankNative(29);
            _data = Shell.ScenarioCars;
            _progress = Shell.NitroProgress;
            _user = NitroUser.Load(Shell.Install);
            if (_data.Models.Count == 0 || _progress.Data.Characters.Count == 0) { Shell.Go(new MainMenuScreen()); return; }
            _ch = Math.Clamp(Character, 0, _progress.Data.Characters.Count - 1);
            // The scenario screen's reset: mission 0, and the car nituser.mel remembers
            _model = Math.Clamp(_user.ScenarioModel, 0, _data.Models.Count - 1);
            _variant = Math.Clamp(_user.ScenarioVariant, 1, _data.Models[_model].Variants.Count);
        }
        Repaint();
    }

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db.BackgroundNative(21), native: 21);
        Draw();
    }

    private (int Model, int Variant) Pair(int k) => Mission.Recommended[k] is var (a, b) ? (Math.Clamp(a, 0, _data.Models.Count - 1), b) : (0, 1);

    private void Draw()
    {
        Frame.RestoreAll();
        Frame.Text(_font, 12, 45, Char.Name);
        Frame.Text(_font, 288, 58, Char.Name);
        for (int k = 0; k < Rows && _scroll + k < Char.Missions.Count; k++)
        {
            var m = Char.Missions[_scroll + k];
            int y = 149 + Row * k;
            if (m.Done) Frame.Blit(_marks[5], 15, y);     // the green stroke, behind the title
            Frame.Text(_font, 35, y, m.Title);
        }
        Frame.Text(_font, 35, 109, Mission.Title);
        for (int k = 0; k < Mission.Recommended.Count; k++)
            Frame.Text(_font, 10, 306 + Row * k, _data.Models[Pair(k).Model].MakeModel);
        MeleeScreen.DrawCar(Frame, _font, _cars, _data, _model, _variant, Shell.Parts);
    }

    private bool ScrollMissions(int by)
    {
        int to = Math.Clamp(_scroll + by, 0, Math.Max(0, Char.Missions.Count - Rows));
        if (to == _scroll) return false;
        _scroll = to;
        Draw();
        return true;
    }

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            foreach (var sp in Spots)
            {
                if (sp.Id == 1)
                {
                    int n = Math.Min(Rows, Char.Missions.Count - _scroll);
                    for (int k = 0; k < n; k++)
                    {
                        int kk = k;
                        l.Add(PadRegion.Of(sp.X0 + 1, 151 + Row * k, sp.X1 - sp.X0 - 2, Row, d => d == NavDir.Up && kk == 0 ? ScrollMissions(-1) : d == NavDir.Down && kk == n - 1 ? ScrollMissions(1) : false));
                    }
                }
                else if (sp.Id == 4)
                    for (int k = 0; k < Mission.Recommended.Count; k++) l.Add(PadRegion.Of(sp.X0 + 1, 308 + Row * k, sp.X1 - sp.X0 - 2, Row));
                else l.Add(PadRegion.Of((sp.X0, sp.X1, sp.Y0, sp.Y1)));
            }
            return l;
        }
    }

    private int HitId(Vector2I p) => Spots.Where(s => s.X0 < p.X && p.X < s.X1 && s.Y0 < p.Y && p.Y < s.Y1).Select(s => s.Id).DefaultIfEmpty(-1).First();

    public override void Tick()
    {
        int held = _hold.Id;
        if (_hold.Update(Shell.MouseHeld, HitId(Shell.Mouse), Shell.Delta) is var reps and > 0) { for (int r = 0; r < reps; r++) Act(held, Shell.Mouse); Draw(); }
        if (Shell.Wheel != 0) { _scroll = Math.Clamp(_scroll + Shell.WheelRows, 0, Math.Max(0, Char.Missions.Count - Rows)); Draw(); }
        if (!Shell.Clicked) return;
        int id = HitId(Shell.Mouse);
        if (id < 0) return;
        Shell.Sound(82);   // the click: Nitro record 75 (0x4b), base 82 in the shell's numbering
        if (id is 2 or 3) _hold.Begin(id);
        Act(id, Shell.Mouse);
        if (Shell.Current == this) Draw();
    }

    private void Act(int id, Vector2I p)
    {
        switch (id)
        {
            case 0: CharacterPopup(); break;
            case 1:
                int row = _scroll + (p.Y - 151) / Row;
                if (row >= 0 && row < Char.Missions.Count) _sel = row;       // no lock check; the car is kept
                break;
            case 2: _scroll = Math.Max(0, _scroll - 1); break;
            case 3: _scroll = Math.Min(Math.Max(0, Char.Missions.Count - Rows), _scroll + 1); break;
            case 4:
                int k = (p.Y - 308) / Row;
                if (k >= 0 && k < Mission.Recommended.Count) (_model, _variant) = (Pair(k).Model, Math.Clamp(Pair(k).Variant, 1, _data.Models[Pair(k).Model].Variants.Count));
                break;
            case 5: ModelPopup(); break;
            case 6: VariantPopup(); break;
            case 7:
                var (cm, cv) = (_model, _variant);
                Shell.Go(ChassisForm.Create(Shell, new MeleeChassis(cm, cv, n => { _variant = n; Shell.Go(this); }, () => Shell.Go(this)) { Data = _data, DoneAs = (m, n) => { (_model, _variant) = (m, n); Shell.Go(this); } }));
                break;
            case 8: EnterArea(); break;
            case 9: Shell.Go(new MainMenuScreen()); break;
        }
    }

    private void CharacterPopup()
    {
        // the character list: three, or four once Natty Dread is unlocked
        int n = _progress.AllDoneExceptLast ? _progress.Data.Characters.Count : Math.Min(3, _progress.Data.Characters.Count);
        Shell.Modal(new ListPopup
        {
            Sprite = 7, NativeSprite = 32, X = 234, Y = 173, TextX = 12, TextY = 29, Visible = 4,
            Items = _progress.Data.Characters.Take(n).Select(c => c.Name).ToList(), Font = _font,
            Up = new(0, 0, 0, 0), Down = new(0, 0, 0, 0), List = new(12, 159, 29, 97), Cancel = new(89, 160, 109, 125),   // the table
            Picked = i =>
            {
                if (i >= 0 && i < n)
                {
                    // Mission 0 and its first recommended car
                    (_ch, _sel, _scroll) = (i, 0, 0);
                    if (Mission.Recommended.Count > 0) (_model, _variant) = (Pair(0).Model, Math.Clamp(Pair(0).Variant, 1, _data.Models[Pair(0).Model].Variants.Count));
                }
                Repaint();
            },
        });
    }

    private void ModelPopup() => Shell.Modal(new ListPopup
    {
        Sprite = 7, NativeSprite = 6, X = 206, Y = 122, TextX = 22, TextY = 48, Visible = 9,
        Items = _data.Models.Select(m => m.MakeModel).ToList(), Font = _font,
        Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 214, 47, 198), Cancel = new(146, 215, 209, 225),
        Picked = i => { if (i >= 0) (_model, _variant) = (i, 1); Repaint(); },     // Variant back to 1
    });

    private void VariantPopup() => VariantPopups.Show(Shell, _font, _data, _model, _variant, true,
        v => { _variant = v; Repaint(); },
        (n, left) => { _variant = VariantFiles.Remap(_variant, n, left); _user.ScenarioVariant = Math.Clamp(_user.ScenarioVariant, 1, left); },
        Repaint);

    /// <summary>ENTER AREA: the car as chosen, a fresh one (no vehscn.vsf), the mission's number; nituser.mel keeps the car.</summary>
    private void EnterArea()
    {
        _user.ScenarioModel = _model;
        _user.ScenarioVariant = _variant;
        NitroUser.Save(_user);
        var m = _data.Models[_model];
        Shell.Launch(new ShellLaunch(Mission.Stem, GameKind.Scenario, _data.Vcf(_model, _variant)) { Driver = Char.Name, Model = m.MakeModel, ModelIndex = _model });
    }
}
