using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The melee forms:
/// mode 0 INSTANT MELEE (record 24: an arena and up to seven A.I. drivers),
/// mode 1 SCENARIO (record 25), and the network melees (NetForms.cs): 3 and
/// 4 the NET MELEE HOST EVENT FORM (record 19, the LAN and an address), 7
/// and 8 its ENTRY ROSTER (record 18). The buttons are painted in the art;
/// clicks go through the hotspot table (strict), every handled click plays
/// record 82, all text is font 0x45, lists show six 17-px rows. The car's
/// blueprint front and side views come from bank 0x3c.
/// </summary>
public sealed partial class MeleeScreen : ShellScreen
{
    public required int Mode { get; init; }
    /// <summary>A notice to show over the form on entry (why the last network game did not start or ended).</summary>
    public string? Notice { get; init; }
    private bool Nitro => Shell.Install.IsNitro;
    private NitUserMel? _user;
    /// <summary>The instant melee's arenas: Nitro's .cbt files by their names, else the base game's fifteen and its addon\*.lvl levels.</summary>
    private IReadOnlyList<(string Name, string Msn)> Arenas => Coop ? CoopList : Nitro ? (Hosting ? HostArenas : Shell.NitroArenas) : Shell.BaseArenas;
    private bool Net => Mode >= 2;
    private bool Hosting => Mode is >= 2 and <= 5;

    // the hotspot table: id → x0, x1, y0, y1
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> Hot = new()
    {
        [0] = (31, 216, 150, 270), [1] = (9, 27, 128, 144), [2] = (9, 27, 250, 268), [3] = (610, 621, 400, 415), [4] = (437, 449, 419, 434),
        [5] = (456, 616, 422, 435), [6] = (229, 369, 453, 471), [7] = (12, 165, 44, 58), [8] = (150, 166, 288, 303), [9] = (198, 213, 288, 303),
        [10] = (9, 215, 335, 454), [11] = (31, 216, 150, 270), [12] = (9, 27, 128, 144), [13] = (9, 27, 250, 268), [14] = (173, 213, 46, 57),
        [15] = (489, 630, 453, 471), [16] = (84, 97, 380, 392), [17] = (185, 199, 380, 392), [18] = (84, 98, 402, 414),
        [20] = (104, 117, 399, 412), [21] = (192, 206, 399, 412), [22] = (173, 213, 306, 317),
        // the port's: the base host form's SHARE MAPS line, on the parchment under the options box (docs/multiplayer.md "Custom maps")
        [26] = (10, 148, 434, 448),
        // the password line beside it (protocol 12)
        [27] = (149, 215, 434, 448),
        // the base host form's HIDDEN WPNS & CARS line under them
        [28] = (10, 116, 449, 462),
        // the port's: UNLIM. TURRETS ("Turrets") on the free line above SHARE MAPS (docs/multiplayer.md)
        [35] = (10, 125, 419, 433),
        // and the Chase Crosshair's ("X-hair", the aiming aid), at that line's right end
        [38] = (126, 215, 419, 433),
        // the port's: the base host form's BOTS line, between NO. DRIVERS and the options: fewer, more, and the skill (docs/multiplayer.md "Bots")
        [29] = (53, 67, 356, 370), [30] = (90, 104, 356, 370), [31] = (105, 165, 356, 370),
        // and its A.I. CARS (BotCarsPopup), at the line's right end
        [34] = (167, 215, 356, 370),
        // the port's: the host's simulation rate, a click steps it, at the Hidden Wpns line's right end
        [33] = (120, 215, 449, 462),
        // and the Car Reset's (the reset-upright key's), on the line under them
        [36] = (10, 148, 463, 477),
        // and the CB Radio's (the voice chat), beside it
        [37] = (149, 215, 463, 477),
        // the port's: the base host form's game type, the button on the AREA OF PLAY bar's right end (RENAME's look): Melee or Co-op (a co-op game's story missions, docs/multiplayer.md "Co-op over the network")
        [32] = (173, 213, 78, 89),
        // the Nitro host form's WEAPON RESTRICTIONS (its own rectangle is NitroHost's)
        [24] = (31, 193, 310, 326),
    };
    // The ids each mode keeps (the host forms' NO. DRIVERS ⊟ ⊞ moved down to y 338..353)
    private int[] Live => Mode switch
    {
        0 => [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        1 => [0, 1, 2, 3, 4, 5, 6, 7, 14, 15],
        3 or 4 when Nitro => [3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 8, 9, 22, 23, 24, 25, 27],
        // a co-op game has no team play, score limit, Bots or custom map to share: the box's rows that are left are INVITE ONLY and the password
        3 or 4 when _coopBase => [3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 8, 9, 17, 22, 27, 28, 32, 33, 35, 36, 37, 38],
        3 or 4 => [3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 8, 9, 16, 17, 18, 20, 21, 22, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38],
        _ => [3, 4, 5, 6, 7, 11, 12, 13, 14, 15],
    };
    private (int X0, int X1, int Y0, int Y1) ModeBtn => Hot[32];
    private (int X0, int X1, int Y0, int Y1) Rect(int id) =>
        // Nitro's instant melee table is the base one's but for the driver box, 11..170: the MAKE / MODEL and VARIANT arrows are only the arrows
        Nitro && !Hosting && id == 7 ? Hot[7] with { X0 = 11, X1 = 170 } :
        Nitro && Hosting && NitroHost.TryGetValue(id, out var r) ? r : Hosting && id is 8 or 9 ? Hot[id] with { Y0 = 338, Y1 = 353 } : Hot[id];

    private const int Row = 17, Rows = 6;
    /// <summary>The drivers' box shows seven rows (the original's whole list: the player and seven opponents fill it); I26 allows <see cref="MaxOpponents"/> opponents and scrolls the box.</summary>
    private const int DriverRows = 7;
    /// <summary>
    /// Opponents in an instant melee: 31, so a game is 32 cars like the online game's (docs/multiplayer.md "Many players"). The
    /// arenas have 8-17 spawn points: the rest are spread to clear ground beside the best free point (MeleeGame.Place).
    /// </summary>
    public const int MaxOpponents = MaxDrivers - 1;
    /// <summary>The original's own limit: the player and seven opponents (MULTICAR.def's eight slots). nituser.mel gets no more than this.</summary>
    public const int OriginalSlots = 8;
    private int _dscroll;
    private ShellFont _font = null!;
    private SpriteBank _cars = null!;
    private MeleeData _data = null!;
    private MeleeSetup _setup = null!;
    private readonly List<(string Name, int Model, int Variant)> _slots = [];
    private readonly List<string> _pool = [];
    private int _shown, _scroll;
    private bool _renaming;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();
    private readonly Random _rand = new();

    public override void Enter()
    {
        // back from the chassis form: the form as it was
        if (_slots.Count > 0) { Repaint(); NetResume(); return; }
        _font = Shell.Db.Font(0x45);
        _cars = Nitro ? Shell.Db.BankNative(54) : Shell.Db.Bank(0x3c);
        _data = Shell.Melee;
        _setup = MeleeSetup.Load();
        if (Nitro && Mode == 0)
        {
            // nituser.mel's instant melee block
            _user = NitroUser.Load(Shell.Install);
            (Area, _setup.Model, _setup.Variant, _setup.Drivers, _setup.Driver) = (_user.MeleeArena, _user.MeleeModel, _user.MeleeVariant, _user.MeleeDrivers, _user.MeleeDriver);
        }
        if (_data.Models.Count == 0) { Shell.Go(new MainMenuScreen()); return; }
        _setup.Model = Math.Clamp(_setup.Model, 0, _data.Models.Count - 1);
        _setup.Variant = Math.Clamp(_setup.Variant, 1, _data.Models[_setup.Model].Variants.Count);
        _slots.Add((_setup.Driver, _setup.Model, _setup.Variant));
        if (Mode == 0)
        {
            // The 53 names, and one A.I. driver to start with
            _pool.AddRange(MeleeData.DriverNames);
            AddDriver();
            Area = Math.Clamp(Area, 0, Arenas.Count - 1);
        }
        else if (Mode == 1) Area = Math.Clamp(Area, 0, MeleeData.Scenarios.Length - 1);
        else NetEnter();
        _scroll = Mode is 7 or 8 ? 0 : Math.Clamp(Area - Rows + 1, 0, Math.Max(0, ListCount - Rows));
        Repaint();
        if (Net) NetShown();
        else if (Notice is { Length: > 0 } n) Say(n);
    }

    private int ListCount => Mode switch { 1 => MeleeData.Scenarios.Length, 7 or 8 => _games.Count, _ => Arenas.Count };
    /// <summary>
    /// One row of the list. A co-op trip mission's title can be longer than the list is wide ("7. The Blood On The Highway"): the "7. " goes, then
    /// the smaller handwriting (font 0x44), and only then is it cut.
    /// </summary>
    private void DrawListRow(int i, int x, int y)
    {
        const int w = 182;
        string t = ListName(i), bare = Coop && t.IndexOf(". ", StringComparison.Ordinal) is > 0 and var d && d <= 2 && char.IsDigit(t[0]) ? t[(d + 2)..] : t;
        var small = Shell.Db.Font(0x44);
        if (_font.Measure(t) <= w) Frame.Text(_font, x, y, t);
        else if (_font.Measure(bare) <= w) Frame.Text(_font, x, y, bare);
        else if (small.Measure(t) <= w) Frame.Text(small, x, y + 2, t);
        else Frame.Text(small, x, y + 2, AddressBookPopup.Fit(small, bare, w));
    }

    /// <summary>
    /// Where list row k's text goes. The Nitro host form's art has its first row 22 px tall (the highlight's own row, rules at 147 and 169) and the
    /// rest 17 each from 169, where the other forms' rules run 147, 164, 181...; the rows follow the rules there, so no line cuts through the words.
    /// </summary>
    private int ListRowY(int k) => Nitro && Hosting && k > 0 ? 154 + Row * k : 149 + Row * k;
    private int ListRowAt(int y) => Nitro && Hosting ? (y < 169 ? 0 : 1 + (y - 169) / Row) : (y - 151) / Row;

    private string ListName(int i) => Mode == 1 ? MeleeData.Scenarios[i].Name : Arenas[i].Name;

    private void AddDriver()
    {
        if (_slots.Count > MaxOpponents || _pool.Count == 0) return;
        int n = _rand.Next(Math.Max(1, _pool.Count - 1));
        int model = _rand.Next(Math.Max(1, _data.StockCount));      // never a hidden model (Hidden Weapons & Cars)
        // Nitro's drivers get a random variant too (base: always the first)
        _slots.Add((_pool[n], model, Nitro ? _rand.Next(_data.Models[model].Variants.Count) + 1 : 1));
        _pool.RemoveAt(n);
        _dscroll = DriverScrollMax;
    }

    private void RemoveDriver()
    {
        if (_slots.Count <= 1) return;     // the handler's `cmp count,1 / jle`: down to no A.I. driver at all
        _pool.Add(_slots[^1].Name);
        _slots.RemoveAt(_slots.Count - 1);
        _shown = Math.Min(_shown, _slots.Count - 1);
        _dscroll = Math.Clamp(_dscroll, 0, DriverScrollMax);
    }

    private int DriverScrollMax => Math.Max(0, _slots.Count - 1 - DriverRows);
    private bool OverDrivers(Vector2I p) => Rect(10) is var r && r.X0 < p.X && p.X < r.X1 && r.Y0 < p.Y && p.Y < r.Y1;

    /// <summary>The scrollbar of a drivers' box with more than seven opponents: a thin thumb at the box's right edge.</summary>
    private void DrawDriverBar()
    {
        int opp = _slots.Count - 1;
        if (opp <= DriverRows) return;
        int top = 338, h = Row * DriverRows - 2;
        int th = Math.Max(8, h * DriverRows / opp), ty = top + (h - th) * _dscroll / DriverScrollMax;
        Frame.Fill(212, top, 1, h, Frame.Nearest(Color.Color8(70, 50, 30)));
        Frame.Fill(211, ty, 3, th, Frame.Nearest(Color.Color8(150, 36, 22)));
    }

    public override void Repaint()
    {
        _modeBtnBaked = false;
        if (Nitro && Mode == 0) Frame.LoadBackground(Shell.Db.BackgroundNative(20), 24, 20);   // base 24's art, byte for byte
        else Frame.LoadBackground(Shell.Db, Mode switch { 0 => 24, 1 => 25, 3 or 4 => 19, _ => 18 });
        Draw();
    }

    /// <summary>Every text and the car pictures, over the clean art.</summary>
    private void Draw()
    {
        Frame.RestoreAll();
        var (name, model, variant) = _slots[_shown];
        int x0 = Mode == 1 ? 40 : 34;
        Frame.Text(_font, 12, 45, _slots[0].Name + (_renaming ? "_" : ""));
        Frame.Text(_font, 288, 58, name);
        if (Mode is 7 or 8) DrawGames();
        else if (ListCount == 0) Frame.Text(_font, x0, 109, "No arenas");
        else
        {
            DrawListRow(Area, x0, Nitro && Hosting ? 130 : 109);
            for (int k = 0; k < Rows && _scroll + k < ListCount; k++) DrawListRow(_scroll + k, x0, ListRowY(k));
        }
        if (Nitro && Hosting) DrawNitroHost();
        else if (Hosting) DrawHost();
        if (Mode == 1 && Victory.Lightest(MeleeData.Scenarios[Area].Msn) is { } best && best.Model >= 0 && best.Model < _data.Models.Count)
            Frame.Text(_font, 16, 308, _data.Models[best.Model].MakeModel);     // after the art's "Lightest car to win:"
        if (Mode == 0)
        {
            Frame.Text(_font, 173, 288, $"{_slots.Count - 1:00}");
            for (int s = 1 + _dscroll; s < _slots.Count && s - _dscroll <= DriverRows; s++) Frame.Text(_font, 20, 338 + Row * (s - 1 - _dscroll), _slots[s].Name);
            DrawDriverBar();
        }
        if (StoryCar)
        {
            // a co-op seat in the mission's own car: the story's, whatever it is (its model is the mission's, not the list's)
            Frame.Text(_font, 330, 399, "Mission's own car");
            Frame.Text(_font, 292, 421, "the story's own");
        }
        else DrawCar(Frame, _font, _cars, _data, model, variant, Shell.Parts);
    }

    /// <summary>The car panel the melee and scenario forms share: the blueprint views, the weapons, the make and model, the variant.</summary>
    public static void DrawCar(ShellFrame frame, ShellFont font, SpriteBank cars, MeleeData data, int model, int variant, I76.Sim.Trip.PartsDatabase parts, string? makeModel = null, string? variantText = null)
    {
        var m = data.Models[model];
        int v = m.Art;
        frame.Blit(cars[2 * v], 229, 79);
        frame.Blit(cars[2 * v + 1], 229, 301);
        var weapons = data.Weapons(model, variant);
        for (int k = 0; k < weapons.Count; k++) frame.Text(font, 470, 58 + Row * k, weapons[k]);
        var kit = data.Parts(model, variant, parts);
        for (int k = 0; k < kit.Count; k++) frame.Text(font, 470, 177 + Row * k, kit[k]);
        frame.Text(font, 330, 399, makeModel ?? m.MakeModel);
        frame.Text(font, 292, 421, variantText ?? m.Variants[Math.Clamp(variant - 1, 0, m.Variants.Count - 1)]);
    }

    public override bool TextEditing => _renaming || _naming;
    public override void StopEditing() { _renaming = false; _naming = false; Draw(); }

    private bool ScrollDrivers(int by)
    {
        int to = Math.Clamp(_dscroll + by, 0, DriverScrollMax);
        if (to == _dscroll) return false;
        _dscroll = to;
        Draw();
        return true;
    }

    private bool ScrollList(int by)
    {
        int rows = Mode is 7 or 8 ? ListRows : Rows;
        int to = Math.Clamp(_scroll + by, 0, Math.Max(0, ListCount - rows));
        if (to == _scroll) return false;
        _scroll = to;
        Draw();
        return true;
    }

    /// <summary>The form's hotspots, but for the lists: their rows one by one (the first and last scroll a press past them), the drivers' list likewise.</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            foreach (int id in Live)
            {
                if (id is 0 or 11)
                {
                    bool games = Mode is 7 or 8;
                    int rows = games ? ListRows : Rows, y0 = id == 11 ? (games ? (Nitro ? 173 : 151) : 151) : Hot[0].Y0 + 1;
                    int n = Math.Min(rows, ListCount - _scroll), x0 = Rect(id).X0 + 1, x1 = games ? 206 : Rect(id).X1 - 1;
                    bool nh = !games && Nitro && Hosting;
                    for (int k = 0; k < n; k++)
                    {
                        int kk = k;
                        l.Add(PadRegion.Of(x0, nh ? (k == 0 ? 148 : 169 + Row * (k - 1)) : y0 + Row * k, x1 - x0, nh && k == 0 ? 21 : Row, d => d == NavDir.Up && kk == 0 ? ScrollList(-1) : d == NavDir.Down && kk == n - 1 ? ScrollList(1) : false));
                    }
                }
                else if (id == 10)
                    for (int sl = 1 + _dscroll; sl < _slots.Count && sl - _dscroll <= DriverRows; sl++)
                    {
                        int first = 1 + _dscroll, last = Math.Min(_slots.Count - 1, _dscroll + DriverRows), s2 = sl;
                        l.Add(PadRegion.Of(10, 338 + Row * (sl - 1 - _dscroll), 204, Row, d => d == NavDir.Up && s2 == first && _dscroll > 0 ? ScrollDrivers(-1) : d == NavDir.Down && s2 == last && _dscroll < DriverScrollMax ? ScrollDrivers(1) : false));
                    }
                else l.Add(PadRegion.Of(Rect(id)));
            }
            return l;
        }
    }

    private int HitId(Vector2I p) => Live.Where(id => Rect(id) is var r && r.X0 < p.X && p.X < r.X1 && r.Y0 < p.Y && p.Y < r.Y1).DefaultIfEmpty(-1).First();

    public override void Tick()
    {
        if (Mode is 7 or 8) PollGames();
        if (_renaming) { Rename(); return; }
        if (_naming) { RenameGame(); return; }
        if (Shell.Wheel != 0 && Mode == 0 && OverDrivers(Shell.Mouse)) { _dscroll = Math.Clamp(_dscroll + Shell.WheelRows, 0, DriverScrollMax); Draw(); }
        else if (Shell.Wheel != 0) { _scroll = Math.Clamp(_scroll + Shell.WheelRows, 0, Math.Max(0, ListCount - (Mode is 7 or 8 ? ListRows : Rows))); Draw(); }
        // ⊞ / ⊟ and the list arrows repeat every 100 ms while the button is held
        int held = _hold.Id;
        if (_hold.Update(Shell.MouseHeld, HitId(Shell.Mouse), Shell.Delta) is var reps and > 0) { for (int r = 0; r < reps; r++) Act(held, Shell.Mouse); Draw(); }
        if (!Shell.Clicked) return;
        int id = HitId(Shell.Mouse);
        if (id < 0) return;
        Shell.Sound(82);
        if (id is 1 or 2 or 8 or 9 or 12 or 13 or 20 or 21) _hold.Begin(id);
        Act(id, Shell.Mouse);
        if (Shell.Current == this) Draw();
    }

    private void Act(int id, Vector2I p)
    {
        if (Net && NetAct(id, p)) return;
        switch (id)
        {
            case 0 or 11:      // a list row
                // Row = scroll + (y - 151) / 17, refused past the six drawn rows
                if (p.Y < 151 || ListRowAt(p.Y) >= Rows) break;
                int row = _scroll + ListRowAt(p.Y);
                if (row >= 0 && row < ListCount) Area = row;
                break;
            case 1 or 12: _scroll = Math.Max(0, _scroll - 1); break;
            case 2 or 13: _scroll = Math.Min(Math.Max(0, ListCount - Rows), _scroll + 1); break;
            case 3: ModelPopup(); break;
            case 4: VariantPopup(); break;
            case 5:            // CONFIGURE CHASSIS: the chassis form for the car on display
                var (who, cm, cv) = _slots[_shown];
                Shell.Go(ChassisForm.Create(Shell, new MeleeChassis(cm, cv, n => { _slots[_shown] = (who, cm, n); OwnCarOff(); Shell.Go(this); }, () => Shell.Go(this))
                    { DoneAs = (m, n) => { _slots[_shown] = (who, m, n); OwnCarOff(); Shell.Go(this); } }));
                break;
            case 6: EnterArea(); break;
            case 7: _shown = 0; break;
            case 8: RemoveDriver(); break;
            case 9: AddDriver(); break;
            case 10:
                int slot = (p.Y - 338) / Row + 1 + _dscroll;
                if (slot >= 1 && slot < _slots.Count) _shown = slot;
                break;
            case 14: _renaming = true; break;
            case 15: Leave(save: false); StopGames(); Shell.Go(new MainMenuScreen()); break;
        }
    }

    private void Rename()
    {
        var (name, model, variant) = _slots[0];
        bool changed = false;
        foreach (char c in Shell.Typed) if (name.Length < 24) { name += c; changed = true; }
        if (Shell.Keys.Contains(Key.Backspace) && name.Length > 0) { name = name[..^1]; changed = true; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter) || Shell.Keys.Contains(Key.Escape) || Shell.Clicked) { _renaming = false; changed = true; }
        _slots[0] = (name, model, variant);
        if (changed) Draw();
    }

    private void ModelPopup()
    {
        // a co-op seat's list begins with the mission's own car (the default); a pick of a model takes the player's own car
        int own = StoryContext ? 1 : 0;
        Shell.Modal(new ListPopup
        {
            Sprite = 7, NativeSprite = Nitro ? 6 : null, X = 206, Y = 122, TextX = 22, TextY = 48, Visible = 9,
            Items = [.. (own == 1 ? new[] { "Mission's own car" } : []), .. _data.Models.Select(m => m.MakeModel)], Font = _font,
            Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 214, 47, 198), Cancel = new(146, 215, 209, 225),
            Picked = i =>
            {
                if (own == 1 && i >= 0) { Shell.Settings.CoopOwnCar = i == 0; Shell.Settings.Save(); i--; }
                // Variant back to 1, the model into the slot on show
                // choosing the model already on show changes nothing (the variant stays)
                if (i >= 0 && i != _slots[_shown].Model) _slots[_shown] = (_slots[_shown].Name, i, 1);
                Repaint();
            },
        });
    }

    /// <summary>A car picked on a co-op seat's form is the player's own pick, not the mission's.</summary>
    private void OwnCarOff()
    {
        if (!StoryContext || !Shell.Settings.CoopOwnCar) return;
        Shell.Settings.CoopOwnCar = false;
        Shell.Settings.Save();
    }

    private void VariantPopup() => VariantPopups.Show(Shell, _font, _data, _slots[_shown].Model, _slots[_shown].Variant, Nitro,
        v => { _slots[_shown] = (_slots[_shown].Name, _slots[_shown].Model, v); OwnCarOff(); Repaint(); },
        (n, left) =>
        {
            // the cars on this model that used a later variant slide down with it; one that had the deleted one takes its neighbour
            int model = _slots[_shown].Model;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Model == model) _slots[i] = (_slots[i].Name, model, VariantFiles.Remap(_slots[i].Variant, n, left));
            _setup.Variant = Math.Clamp(_setup.Variant, 1, left);
        },
        Repaint);

    /// <summary>ENTER AREA: the opponents (MULTICAR.def's slots 1..n), the player's car (vehscn.vcf) and the arena or scenario.</summary>
    private void EnterArea()
    {
        Leave(save: true);
        var (_, model, variant) = _slots[0];
        var car = _data.Vcf(model, variant);
        var (driver, make) = (_slots[0].Name, _data.Models[model].MakeModel);
        if (Mode == 0)
        {
            var opponents = _slots.Skip(1).Select(s => new MeleeOpponent(s.Name, _data.Vcf(s.Model, s.Variant))).ToList();
            Shell.Launch(new ShellLaunch(Arenas[Area].Msn, GameKind.AutoMelee, car) { Opponents = opponents, Driver = driver, Model = make, ModelIndex = model });
        }
        else Shell.Launch(new ShellLaunch(MeleeData.Scenarios[Area].Msn, GameKind.Scenario, car) { Driver = driver, Model = make, ModelIndex = model });
    }

    /// <summary>Any exit but CANCEL writes user.mel.</summary>
    private void Leave(bool save)
    {
        if (!save) return;
        _setup.Driver = _slots[0].Name;
        _setup.Model = _slots[0].Model;
        _setup.Variant = _slots[0].Variant;
        if (!Net) _setup.Drivers = _slots.Count;     // I26's own count (melee.cfg): up to MaxOpponents + 1
        if (_user is { } u)
        {
            (u.MeleeArena, u.MeleeModel, u.MeleeVariant, u.MeleeDrivers, u.MeleeDriver) = (Area, _setup.Model, _setup.Variant, Math.Min(_setup.Drivers, OriginalSlots), _setup.Driver);   // the original's file never holds more than it reads
            NitroUser.Save(u);
            MeleeSetup.SaveNitroDrivers(_setup.Drivers);     // nituser.mel holds at most the original's eight; I26's own count is beside it
            return;
        }
        if (Hosting && !Nitro) _setup.CoopHost = _coopBase;
        if (Coop) _setup.CoopArena = Arenas.Count > 0 ? Arenas[Math.Clamp(Area, 0, Arenas.Count - 1)].Msn : _setup.CoopArena;
        if (Hosting) { if (!Coop) _setup.Game = _gameName; _setup.Bots = _bots; _setup.BotSkill = (int)_botSkill; _setup.BotCars = [.. _botCars]; _setup.HostRate = _rate; _setup.HostTurrets = _turrets; }
        if (Nitro && Hosting)
        {
            _setup.NitroKind = _kind;
            _setup.NitroArena = HostArenas.Count > 0 ? HostArenas[Math.Clamp(Area, 0, HostArenas.Count - 1)].Msn : "";
        }
        _setup.Save();
    }

    /// <summary>Off the screen (to the chassis form, or for good): the session search stops; it starts again on the way back.</summary>
    public override void Leave() => StopGames();
}

/// <summary>
/// A list picker (a modal loop): a
/// bank 0x24 sprite with a 6 px shadow, rows from (TextX, TextY) relative to
/// it, ▲ ▼ to scroll, a row picks and closes, CANCEL closes without.
/// Hotspots are relative to the popup and strict.
/// </summary>
public sealed class ListPopup : ShellScreen
{
    public required int Sprite { get; init; }
    public required int X { get; set; }
    public required int Y { get; set; }
    public required int TextX { get; set; }
    public required int TextY { get; set; }
    public required int Visible { get; set; }
    public required List<string> Items { get; init; }
    public required ShellFont Font { get; init; }
    public required (int X0, int X1, int Y0, int Y1) Up { get; set; }
    public required (int X0, int X1, int Y0, int Y1) Down { get; set; }
    public required (int X0, int X1, int Y0, int Y1) List { get; set; }
    public required (int X0, int X1, int Y0, int Y1) Cancel { get; set; }
    public required Action<int> Picked { get; init; }
    /// <summary>The Nitro shell's popups are bank 30 sprites: by their own number, and (if its art differs from the base popup's) where things are.</summary>
    public int? NativeSprite { get; init; }
    public PopupLayout? NativeLayout { get; init; }
    /// <summary>Where the list starts, and where it was left (the shell remembers some lists' offsets).</summary>
    public int Scroll { get; init; }
    public Action<int>? Scrolled { get; init; }
    /// <summary>
    /// The Nitro VARIANT popup's mode: a click on a row only selects it, DONE (<see cref="Done"/>) takes the
    /// selected row, DELETE (<see cref="DeleteBtn"/>) asks <see cref="DeleteRequested"/> about it and CANCEL leaves.
    /// </summary>
    public (int X0, int X1, int Y0, int Y1)? Done { get; set; }
    public (int X0, int X1, int Y0, int Y1)? DeleteBtn { get; set; }
    public Action<int>? DeleteRequested { get; init; }
    /// <summary>
    /// I26's addition to the base game's VARIANT popup (which has only CANCEL): a DELETE button drawn beside CANCEL in the
    /// art's own button colours, for the row under the mouse or the pad's cursor; the Delete key and the pad's X do the same.
    /// Rows still pick on a click, as the original's.
    /// </summary>
    public (int X0, int X1, int Y0, int Y1)? DrawnDelete { get; set; }
    /// <summary>The row selected on opening (select mode).</summary>
    public int Cursor { get; init; }
    /// <summary>The pad picks (the port's): the D-pad and the left stick move a cursor, A picks, B and Start cancel. Always on now; the screens that asked for it still may.</summary>
    public bool PadPicks { get; init; }
    public override bool OwnsPad => true;
    /// <summary>The cursor shows from the start (the list was opened from the pad); else on the D-pad's first press.</summary>
    public bool CursorShown { get; set; }
    public override bool EscapeOpensOptions => false;
    private int _scroll, _w, _h, _cursor;
    private const int Row = 17;
    private readonly I76.Formats.Input.PadRepeater _pad = new();
    private readonly I76.Formats.Input.HoldRepeat _hold = new();

    public override void Enter()
    {
        if (NativeLayout is { } nl)
        {
            (X, Y, TextX, TextY, Visible, Up, Down, List, Cancel) = (nl.X, nl.Y, nl.TextX, nl.TextY, nl.Visible, nl.Up, nl.Down, nl.List, nl.Cancel);
            if (DeleteRequested is not null) (Done, DeleteBtn) = (nl.Done, nl.Delete);
        }
        else if (DeleteRequested is not null) DrawnDelete = (Cancel.X0 - 49, Cancel.X0 - 4, Cancel.Y0, Cancel.Y1);
        var s = Art;
        _w = s?.Width ?? 200; _h = s?.Height ?? 236;
        _scroll = Math.Clamp(Scroll, 0, Math.Max(0, Items.Count - Visible));
        _cursor = _scroll;
        if (Select || DrawnDelete is not null)
        {
            _cursor = Math.Clamp(Cursor, 0, Math.Max(0, Items.Count - 1));
            _scroll = Math.Clamp(_scroll, _cursor - Visible + 1, _cursor);
            CursorShown = true;
        }
        // the Nitro VARIANT popup (case 6): with more than 8 variants it opens scrolled to count - 8, the newest
        if (Select && Items.Count > Visible) _scroll = Items.Count - Visible;
        _pad.Settle();      // the A that opened it
        Draw();
    }

    private bool Select => Done is not null && DeleteBtn is not null;

    private Sprite? Art => NativeSprite is { } n ? Shell.Db.BankNative(30)[n] : Shell.Db.Bank(0x24)[Sprite];

    private void Draw()
    {
        Frame.Blit(Art, X, Y);
        Frame.Fill(X + 6, Y + _h, _w, 6, 0x0f);
        Frame.Fill(X + _w, Y + 6, 6, _h, 0x0f);
        // an entry marked '*' (a list header) is drawn without it
        for (int k = 0; k < Visible && _scroll + k < Items.Count; k++) Frame.Text(Font, X + TextX, Y + TextY + Row * k, Items[_scroll + k].StartsWith('*') ? Items[_scroll + k][1..] : Items[_scroll + k]);
        if (DrawnDelete is { } d) DrawDeleteButton(d);
        // the pad's cursor: a red pencil mark before its row and a line under it, inside the list
        if (CursorShown && _cursor - _scroll is >= 0 and var k2 && k2 < Visible)
        {
            byte red = Frame.Nearest(Color.Color8(150, 36, 22));
            int ry = Y + TextY + Row * k2;
            Frame.Fill(X + TextX - 1, ry + Font.Height, _w - TextX - 14, 1, red);
            for (int i = 0; i < 3; i++) Frame.Fill(X + TextX - 7 + i, ry + 3 + i, 1, 7 - 2 * i, red);
        }
    }

    /// <summary>
    /// The DELETE button: CANCEL's frame and fill (read from the popup's art, so both looks and every palette agree) with
    /// the word in the shell's label font; there is no such sprite in the base game's banks.
    /// </summary>
    private void DrawDeleteButton((int X0, int X1, int Y0, int Y1) d)
    {
        var a = Art;
        if (a is null) return;
        // CANCEL's own pixels, its label's columns stretched out of the plain ones beside them (the arrow above must stay clear)
        int x = X + d.X0, y = Y + d.Y0, w = d.X1 - d.X0 + 1, h = d.Y1 - d.Y0 + 1, cw = Cancel.X1 - Cancel.X0 + 1, edge = 6;
        for (int ty = 0; ty < h; ty++)
            for (int tx = 0; tx < w; tx++)
            {
                int sx = tx < edge ? tx : tx >= w - edge ? cw - (w - tx) : edge;
                int px = Math.Clamp(Cancel.X0 + sx, 0, a.Width - 1), py = Math.Clamp(Cancel.Y0 + ty, 0, a.Height - 1);
                if (a.Opaque[py * a.Width + px]) Frame.Fill(x + tx, y + ty, 1, 1, a.Pixels[py * a.Width + px]);
            }
        var f = Shell.Db.Font(0x47);
        Frame.Text(f, x + (w - f.Measure("DELETE")) / 2, y + (h - f.Height) / 2, "DELETE", ShellFrame.DefaultInk);
    }

    private Vector2I _lastMouse;
    private bool _padDeleteHeld;

    /// <summary>Asks to delete the cursor's row (the button, the Delete key, the pad's X).</summary>
    private void AskDelete()
    {
        if (_cursor < 0 || _cursor >= Items.Count) return;
        Shell.Sound(82);
        DeleteRequested!(_cursor);
    }

    /// <summary>The pad: up and down (D-pad or left stick, repeating) move the cursor (the list scrolling to keep it in view), A picks, B and Start cancel.</summary>
    private void PadTick()
    {
        var pads = I76.Game.Controls.PadSource.Read();
        var st = _pad.Update(pads.Count > 0 && I76.Game.Controls.Focused() ? pads[0] : default, Shell.Delta);
        int step = st.Dir == I76.Formats.Input.NavDir.Up ? -1 : st.Dir == I76.Formats.Input.NavDir.Down ? 1 : 0;
        if (step != 0)
        {
            if (CursorShown) _cursor = Math.Clamp(_cursor + step, 0, Items.Count - 1);
            CursorShown = true;
            _scroll = Math.Clamp(_scroll, _cursor - Visible + 1, _cursor);
            Shell.Sound(82);
            Draw();
        }
        if (DrawnDelete is not null)
        {
            bool x = pads.Count > 0 && I76.Game.Controls.Focused() && pads[0].Has(I76.Formats.Input.PadButton.X);
            bool edge = x && !_padDeleteHeld;
            _padDeleteHeld = x;
            if (edge) { AskDelete(); return; }
        }
        if ((st.Buttons & I76.Formats.Input.PadRepeater.Btn.Accept) != 0 && CursorShown) Close(_cursor);
        else if ((st.Buttons & (I76.Formats.Input.PadRepeater.Btn.Back | I76.Formats.Input.PadRepeater.Btn.Start)) != 0) Close(-1);
    }

    private bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;

    public override void Tick()
    {
        PadTick();
        if (Shell.Current != this) return;
        // Esc cancels, as the pad's B does (the port's: the original's lists took only CANCEL)
        if (Shell.Keys.Contains(Key.Escape)) { Close(-1); return; }
        if (Shell.Wheel != 0) { _scroll = Math.Clamp(_scroll + Shell.WheelRows, 0, Math.Max(0, Items.Count - Visible)); Draw(); }
        var p = Shell.Mouse;
        if (DrawnDelete is not null)
        {
            // the mouse moving over a row moves the mark to it (the row DELETE will take); a click still picks it
            if (p != _lastMouse && In(List, p) && _scroll + (p.Y - Y - TextY) / Row is var hr && hr >= 0 && hr < Items.Count && hr != _cursor) { _cursor = hr; Draw(); }
            _lastMouse = p;
            if (Shell.Keys.Contains(Key.Delete)) { AskDelete(); return; }
        }
        // the arrows repeat while the button is held (HoldRepeat): 0 up, 1 down
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, In(Up, p) ? 0 : In(Down, p) ? 1 : -1, Shell.Delta); n > 0; n--) ArrowStep(held);
        if (!Shell.Clicked) return;
        if (DrawnDelete is { } dd && In(dd, p)) { AskDelete(); return; }
        if (In(Up, p)) { _hold.Begin(0); ArrowStep(0); }
        else if (In(Down, p)) { _hold.Begin(1); ArrowStep(1); }
        else if (In(List, p))
        {
            int i = _scroll + (p.Y - Y - TextY) / Row;
            if (i < 0 || i >= Items.Count) return;
            if (Select) { _cursor = i; Shell.Sound(82); Draw(); }
            else Close(i);
        }
        else if (Select && In(Done!.Value, p)) { if (Items.Count > 0) Close(_cursor); }
        else if (Select && In(DeleteBtn!.Value, p)) { Shell.Sound(82); DeleteRequested!(_cursor); }
        else if (In(Cancel, p)) Close(-1);
    }

    private void ArrowStep(int arrow)
    {
        int to = Math.Clamp(_scroll + (arrow == 0 ? -1 : 1), 0, Math.Max(0, Items.Count - Visible));
        if (to == _scroll) return;
        _scroll = to; Shell.Sound(82); Draw();
    }

    private void Close(int i)
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, _w + 6, _h + 6);
        Shell.Modal(null);
        Scrolled?.Invoke(_scroll);
        Picked(i);
    }
}

/// <summary>
/// victory.rec: per scenario, the lightest car to win it — its
/// model and built mass — updated when a win comes in lighter (or first).
/// Kept in user://victory.cfg.
/// </summary>
public static class Victory
{
    private const string Path = "user://victory.cfg";

    public static (int Model, int Mass)? Lightest(string scenario)
    {
        var c = new ConfigFile();
        if (c.Load(Path) != Error.Ok || !c.HasSectionKey(scenario, "model")) return null;
        return ((int)c.GetValue(scenario, "model"), (int)c.GetValue(scenario, "mass"));
    }

    public static void Record(string scenario, int model, float mass)
    {
        if (model < 0) return;
        int m = (int)mass;
        if (Lightest(scenario) is { } best && best.Mass <= m) return;
        var c = new ConfigFile();
        c.Load(Path);
        c.SetValue(scenario, "model", model);
        c.SetValue(scenario, "mass", m);
        c.Save(Path);
    }
}

/// <summary>
/// 0xc025 STANDINGS: record 28, NSCR.DAT's rows at
/// y = 70 + 24i — driver at x 32, model 191, deaths 436, kills 500, score
/// 553 — one row in a single-player melee, every player's in a network one
/// (best first). DONE goes back to the melee form the game came from.
/// </summary>
public sealed class StandingsScreen : ShellScreen
{
    public MeleeResult? Result { get; init; }
    /// <summary>A network melee's rows (the car files are named by their model).</summary>
    public IReadOnlyList<StandingRow>? Rows { get; init; }
    /// <summary>Where DONE goes: the instant melee form unless told.</summary>
    public Func<ShellScreen>? Then { get; init; }
    /// <summary>Said over the standings on entry (the game ended early: the server went away).</summary>
    public string? Notice { get; init; }

    public override void Enter()
    {
        Repaint();
        if (Notice is { Length: > 0 } n) Shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = n, Closed = Repaint });
    }

    /// <summary>The network game had team play (NSCR's teamMode): a header row per team with the summed stats, members indented.</summary>
    public bool TeamPlay { get; init; }
    /// <summary>A Nitro network game's kind: race and CTF show only the score column on their own backgrounds.</summary>
    public I76.Sim.Missions.MeleeKind Kind { get; init; }
    /// <summary>The team names: the base game's eight, and Nitro's table goes on to four more.</summary>
    private static readonly string[] TeamNames = [.. MeleeScreen.Teams, "Red Dawn", "Gang Green", "Purple Reign", "Black Plague"];

    public override void Repaint()
    {
        // Background 23 for combat, 24 for CTF, 25 for race; race and CTF list only the score (x 500)
        bool scoreOnly = Shell.Install.IsNitro && Kind is I76.Sim.Missions.MeleeKind.Race or I76.Sim.Missions.MeleeKind.Ctf;
        int bg = Kind switch { I76.Sim.Missions.MeleeKind.Ctf => 24, I76.Sim.Missions.MeleeKind.Race => 25, _ => 23 };
        if (Shell.Install.IsNitro) Frame.LoadBackground(Shell.Db.BackgroundNative(bg), native: bg);
        else Frame.LoadBackground(Shell.Db, 28);
        // (indent, driver, model, deaths, kills, score)
        var lines = new List<(bool Header, string Driver, string Model, int Deaths, int Kills, int Score)>();
        if (Rows is { } all)
        {
            var sorted = all.OrderByDescending(r => r.Score).ToList();
            if (TeamPlay)
            {
                // One header per team (its name, the members' sums), then its members as "  name"
                foreach (var g in sorted.GroupBy(r => r.Team).OrderByDescending(g => g.Sum(r => r.Score)))
                {
                    int t = g.Key - 1;
                    lines.Add((true, t >= 0 && t < TeamNames.Length ? TeamNames[t] : "", "", g.Sum(r => r.Deaths), g.Sum(r => r.Kills), g.Sum(r => r.Score)));
                    foreach (var r in g) lines.Add((false, "  " + r.Driver, Shell.Melee.MakeModelOf(r.Car) ?? "", r.Deaths, r.Kills, r.Score));
                }
            }
            else foreach (var r in sorted) lines.Add((false, r.Driver, Shell.Melee.MakeModelOf(r.Car) ?? "", r.Deaths, r.Kills, r.Score));
        }
        else if (Result is { } m) lines.Add((false, m.Driver, m.Model, m.Deaths, m.Kills, m.Score));
        var f = Shell.Db.Font(0x45);
        int y = 70;
        _count = lines.Count;
        _top = Math.Clamp(_top, 0, Math.Max(0, _count - PerPage));
        // the sheet holds 16 rows; more drivers (to 64) page: the wheel, Up/Down, PgUp/PgDn
        if (_count > PerPage) Frame.Text(f, 410, 459, $"{_top + 1}-{Math.Min(_top + PerPage, _count)} of {_count}", ShellFrame.DefaultInk);
        foreach (var l in lines.Skip(_top).Take(PerPage))
        {
            Frame.Text(f, 32, y, l.Driver, ShellFrame.DefaultInk);
            if (!scoreOnly)
            {
                Frame.Text(f, 191, y, l.Model, ShellFrame.DefaultInk);
                Frame.Text(f, 436, y, l.Deaths.ToString(), ShellFrame.DefaultInk);
                Frame.Text(f, 500, y, l.Kills.ToString(), ShellFrame.DefaultInk);
            }
            Frame.Text(f, scoreOnly ? 500 : 553, y, l.Score.ToString(), ShellFrame.DefaultInk);
            y += 24;
        }
    }

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of(250, 456, 138, 15)];

    private const int PerPage = 16;
    private int _top, _count;

    private void Page(int rows)
    {
        int to = Math.Clamp(_top + rows, 0, Math.Max(0, _count - PerPage));
        if (to == _top) return;
        _top = to;
        Repaint();
    }

    public override void Tick()
    {
        if (Shell.Wheel != 0) Page(Shell.WheelRows);
        if (Shell.Keys.Contains(Key.Down)) Page(1);
        if (Shell.Keys.Contains(Key.Up)) Page(-1);
        if (Shell.Keys.Contains(Key.Pagedown)) Page(PerPage);
        if (Shell.Keys.Contains(Key.Pageup)) Page(-PerPage);
        var m = Shell.Mouse;
        if (Shell.Clicked && 249 < m.X && m.X < 389 && 455 < m.Y && m.Y < 472) { Shell.Sound(82); Shell.Go(Then?.Invoke() ?? new MeleeScreen { Mode = 0 }); }
    }
}

/// <summary>Where a Nitro popup sits and what it holds (relative to the popup: arrows, rows, CANCEL).</summary>
public sealed record PopupLayout(int X, int Y, int TextX, int TextY, int Visible, (int X0, int X1, int Y0, int Y1) Up, (int X0, int X1, int Y0, int Y1) Down, (int X0, int X1, int Y0, int Y1) List, (int X0, int X1, int Y0, int Y1) Cancel)
{
    /// <summary>The VARIANT popup's DONE and DELETE (ids 3 and 4), relative to the popup.</summary>
    public (int X0, int X1, int Y0, int Y1) Done { get; init; } = (65, 136, 238, 254);
    public (int X0, int X1, int Y0, int Y1) Delete { get; init; } = (12, 83, 259, 275);
}

/// <summary>
/// The VARIANT list (bank 36 sprite 9; Nitro's bank 30 sprite 8 with DONE, DELETE and CANCEL). In Nitro a click
/// selects a row and DONE takes it; DELETE removes the player's saved variant after "Sure you wanna delete this variant?",
/// a stock one with only a notice (bank 36 sprite 34).
/// The base game's list (no DELETE in the original) keeps its click-to-pick rows and gets a DELETE button, the Delete key and
/// the pad's X on the marked row, with the same confirm and rules (I26's addition, docs/deviations.md).
/// </summary>
public static class VariantPopups
{
    /// <summary>Shows the list for <paramref name="model"/> with <paramref name="current"/> (1-based) selected. <paramref name="deleted"/> gets the number deleted and the count left.</summary>
    public static void Show(Shell shell, ShellFont font, MeleeData data, int model, int current, bool nitro,
        Action<int> picked, Action<int, int> deleted, Action repaint)
    {
        var m = data.Models[model];
        void Reopen(int cur) { repaint(); Show(shell, font, data, model, cur, nitro, picked, deleted, repaint); }   // the form again: the notice and the receipt wiped part of it
        shell.Modal(new ListPopup
        {
            Sprite = 9, X = 235, Y = 122, TextX = 15, TextY = 50, Visible = 9,
            Items = [.. m.Variants], Font = font, Cursor = current - 1,
            Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 158, 47, 198), Cancel = new(89, 158, 209, 225),
            NativeSprite = nitro ? 8 : null,
            NativeLayout = nitro ? new PopupLayout(220, 97, 16, 82, 8, new(14, 38, 60, 78), new(14, 38, 220, 238), new(12, 188, 80, 216), new(118, 188, 259, 275)) : null,
            Picked = i => { if (i >= 0) picked(i + 1); else repaint(); },
            DeleteRequested = i => Delete(shell, data, model, i + 1, nitro, deleted, Reopen),
        });
    }

    private static void Delete(Shell shell, MeleeData data, int model, int n, bool nitro, Action<int, int> deleted, Action<int> reopen)
    {
        var m = data.Models[model];
        if (n <= m.Base)
        {
            // Nitro has the notice's own art (bank 36 sprite 34); the base game has none, so the blank notice carries the words
            shell.Modal(nitro
                ? new NoticePopup { Sprite = 0, NativeSprite = 34, Ok = new Rect2I(119, 61, 42, 16), Closed = () => reopen(n) }
                : new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = "Can't delete a default variant.", Closed = () => reopen(n) });
            return;
        }
        shell.Modal(new ConfirmPopup
        {
            Lines = ["Sure you wanna delete", "this variant?"],
            Answer = yes =>
            {
                if (!yes) { reopen(n); return; }
                var r = data.DeleteVariant(model, n);
                if (r == VariantFiles.Result.Deleted)
                {
                    int left = m.Variants.Count;
                    shell.CarListsChanged(data);
                    deleted(n, left);
                    // the selection stays where it was, or moves to the variant that slid into its place
                    reopen(Math.Min(n, left));
                }
                else shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = "That variant is not one of yours to delete.", Closed = () => reopen(n) });
            },
        });
    }
}
