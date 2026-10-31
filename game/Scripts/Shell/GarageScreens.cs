using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Sim.Trip;

namespace I76.Game.Shell;

/// <summary>
/// What FIELD SALVAGE and BUILD AND REPAIR share:
/// font 0x45 for entries (ink 15, the display's default remap) and 0x47 for
/// labels, the 17-px row pitch, the "(C)/(V)/(R)" legends ({ | } in font
/// 0x45), the highlighter strokes of banks 0x21–0x23 (one random shape per
/// colour per screen build) and the blueprint's damage index.
/// </summary>
public abstract class GarageBase : TripScreen
{
    protected const int P = 17;
    protected ShellFont F45 = null!, F47 = null!;
    private readonly SpriteBank?[] _strokes = new SpriteBank?[3];
    private readonly int[] _stroke = new int[3];
    protected TripInventory Inv = null!;

    protected void Setup(TripInventory? inv = null)
    {
        F45 = Shell.Db.Font(0x45);
        F47 = Shell.Db.Font(0x47);
        for (int k = 0; k < 3; k++) { _strokes[k] = Shell.Db.Bank(0x21 + k); _stroke[k] = Shell.Rand() % 5; }
        Inv = inv ?? Shell.Trip.Inventory(Shell)!;
    }

    protected int Text(ShellFont f, int x, int y, string s) => Frame.Text(f, x, y, s, ShellFrame.DefaultInk);

    /// <summary>Red below a third, yellow below two thirds, green below full, none at full; at the text's own point.</summary>
    protected void Stroke(PartNode n, int x, int y)
    {
        int m = n.Part.MaxHp, h = n.Health;
        if (m <= 0 || h >= m) return;
        int bank = h < m * (1 / 3.0) ? 0 : h < m * (2 / 3.0) ? 1 : 2;
        Frame.BlitAt(_strokes[bank]?[_stroke[bank]], x, y);
    }

    /// <summary>The legend a list prints in front of a part: "(C)", "(V)" or "(R)"; field salvage gets none.</summary>
    protected static string Legend(PartNode n) => n.State switch { PartState.Car => "{ ", PartState.Van => "| ", PartState.Repair => "} ", _ => "" };

    /// <summary>0 full, 1 green, 2 yellow, 3 red.</summary>
    protected static int Damage(PartNode n)
    {
        int m = n.Part.MaxHp, h = n.Health;
        if (m <= 0 || h >= m) return 0;
        return h >= m * (2 / 3.0) ? 1 : h >= m * (1 / 3.0) ? 2 : 3;
    }

    /// <summary>The digits of a component code (atoi(part+0x31)): eng01 → 1; 0 for a placeholder.</summary>
    protected static int CodeNumber(PartDef p) => p.Code.Length > 3 && int.TryParse(new string(p.Code.Skip(3).TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0;

    /// <summary>The inventory weapons' order: slg, spp, flm, mor, then the rest; then the number; then health.</summary>
    protected static int WeaponClass(PartDef p) => p.Code.Length < 3 ? 4 : p.Code[..3] switch { "slg" => 0, "spp" => 1, "flm" => 2, "mor" => 3, _ => 4 };

    public static int ClassOf(PartDef p) => WeaponClass(p);
    public static int NumberOf(PartDef p) => CodeNumber(p);

    protected static bool In(Vector2I m, (int X0, int X1, int Y0, int Y1) r) => r.X0 < m.X && m.X < r.X1 && r.Y0 < m.Y && m.Y < r.Y1;

    protected void Notice(int sprite, Action? then = null) =>
        Shell.Modal(new NoticePopup { Sprite = sprite, Ok = new Rect2I(119, 61, 42, 16), Closed = then });
}

/// <summary>
/// 0xc017 FIELD SALVAGE: record 3
/// (30 after T04), the six inventory lists over the six salvage lists; a
/// click moves a part to its twin list (the inventory takes only as many as
/// it has rows), ACCEPT SALVAGE goes to the garage.
/// </summary>
public sealed class FieldSalvageScreen : GarageBase
{
    private sealed record Region(int Rows, int X0, int X1, int Y0, int Y1, (int, int, int, int)? Up = null, (int, int, int, int)? Down = null)
    {
        public bool Salvage => Up is not null;
    }

    private static readonly Region[] Regions =
    [
        new(3, 25, 172, 34, 85), new(9, 25, 172, 103, 255), new(11, 173, 318, 51, 238), new(11, 320, 465, 50, 238), new(5, 464, 613, 34, 118), new(4, 469, 613, 136, 203),
        new(2, 10, 154, 275, 338, (10, 28, 275, 291), (10, 28, 326, 338)),
        new(9, 167, 312, 275, 463, (167, 185, 275, 291), (167, 185, 447, 463)),
        new(5, 10, 154, 344, 457, (9, 28, 344, 360), (9, 28, 441, 457)),
        new(8, 325, 470, 275, 446, (325, 344, 275, 291), (325, 344, 430, 446)),
        new(4, 484, 629, 275, 377, (484, 501, 275, 291), (484, 501, 361, 377)),
        new(2, 484, 629, 378, 443, (483, 501, 378, 393), (483, 501, 430, 443)),
    ];
    private static readonly (int, int, int, int) Accept = (331, 471, 452, 467), Bookmark = (490, 631, 450, 469);
    private static readonly int[] Twin = [6, 8, 7, 9, 10, 11, 0, 2, 1, 3, 4, 5];
    // the inventory list a part type sorts into (engines, specials, weapons, wheels, brakes, suspensions); salvage is + 6 in this table's order
    private static int ListOf(int type) => type switch { 2 => 0, 12 or 13 => 1, >= 6 and <= 11 => 2, 5 => 3, 1 or 4 => 4, 3 => 5, _ => -1 };
    private static readonly int[] SalvageOf = [6, 8, 7, 9, 10, 11];

    private readonly List<PartNode>[] _lists = Enumerable.Range(0, 12).Select(_ => new List<PartNode>()).ToArray();
    private readonly int[] _scroll = new int[12];
    private bool _moved;

    public override void Enter()
    {
        Setup();
        Build();
        Repaint();
        bool first = !Shell.Trip.SalvageVisited;
        Shell.Trip.SalvageVisited = true;
        if (first && Shell.Trip.Mission == 2 && !Shell.Trip.FromBookmark) Shell.Voice(0x49);       // the intro, after T01
        if (first && Shell.Trip.Mission == 5) Notice(33);                                            // CAN'T SALVAGE FROM A DREAM…
    }

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db, Shell.Trip.Mission == 5 ? 30 : 3);
        for (int i = 0; i < 12; i++) Draw(i);
    }

    /// <summary>The part ring sorted into the twelve lists.</summary>
    private void Build()
    {
        foreach (var n in Inv.Nodes.ToList())
        {
            if (n.Part.IsEmpty) continue;
            int inv = ListOf(n.Part.Type);
            if (inv < 0) continue;
            if (n.State == PartState.Field)
            {
                if (inv == 1) n.Health = n.Part.MaxHp;               // a salvaged special shows full health
                SortIn(SalvageOf[inv], n);
                continue;
            }
            if (inv == 2 && _lists[2].Count > 10) { n.State = PartState.Field; n.Slot = -1; SortIn(7, n); continue; }
            if (inv == 1 && n.State == PartState.Car && n.Health < n.Part.MaxHp) continue;     // listed nowhere
            if (inv == 2) SortIn(2, n);
            else
            {
                int at = _lists[inv].FindIndex(x => x.State != PartState.Car);
                if (n.State == PartState.Car && at >= 0) _lists[inv].Insert(at, n); else _lists[inv].Add(n);
            }
        }
    }

    /// <summary>Salvage by number then health; inventory weapons by class, number, health.</summary>
    private void SortIn(int list, PartNode n)
    {
        int Cmp(PartNode a, PartNode b)
        {
            int c = list == 2 || list == 7 ? WeaponClass(a.Part).CompareTo(WeaponClass(b.Part)) : 0;
            if (c == 0) c = CodeNumber(a.Part).CompareTo(CodeNumber(b.Part));
            return c != 0 ? c : a.Health.CompareTo(b.Health);
        }
        var l = _lists[list];
        int i = l.FindIndex(x => Cmp(n, x) < 0);
        if (i < 0) l.Add(n); else l.Insert(i, n);
    }

    /// <summary>The region restored, then the rows — every inventory entry, or a salvage list's visible rows below its header.</summary>
    private void Draw(int id)
    {
        var r = Regions[id];
        Frame.Restore(r.X0, r.Y0, r.X1 - r.X0 + 1, r.Y1 - r.Y0 + 1);
        int x = r.X0 + 2;
        var l = _lists[id];
        if (!r.Salvage)
            for (int i = 0; i < l.Count; i++) { int y = r.Y0 + 1 + i * P; Stroke(l[i], x, y); Text(F45, x, y, Legend(l[i]) + l[i].Part.Id); }
        else
            for (int i = 0; i < r.Rows && _scroll[id] + i < l.Count; i++)
            {
                var n = l[_scroll[id] + i];
                int y = r.Y0 + 1 + (i + 1) * P;
                Stroke(n, x, y);
                Text(F45, x, y, n.Part.Id);
            }
    }

    private bool ScrollList(int id, int by)
    {
        int to = Math.Clamp(_scroll[id] + by, 0, Math.Max(0, _lists[id].Count - Regions[id].Rows));
        if (to == _scroll[id]) return false;
        _scroll[id] = to;
        Draw(id);
        return true;
    }

    /// <summary>ACCEPT SALVAGE, the bookmark button, and each list's rows (the salvage lists scroll on a press past their first or last).</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion> { PadRegion.Of(Accept), PadRegion.Of(Bookmark) };
            for (int id = 0; id < 12; id++)
            {
                var r = Regions[id];
                int n = r.Salvage ? Math.Min(r.Rows, _lists[id].Count - _scroll[id]) : Math.Min(r.Rows, _lists[id].Count), i0 = r.Salvage ? 1 : 0;
                for (int k = 0; k < n; k++)
                {
                    int kk = k, ii = id;
                    l.Add(PadRegion.Of(r.X0 + 1, r.Y0 + P * (k + i0), r.X1 - r.X0 - 2, P,
                        r.Salvage ? d => d == NavDir.Up && kk == 0 ? ScrollList(ii, -1) : d == NavDir.Down && kk == n - 1 ? ScrollList(ii, 1) : false : null));
                }
            }
            return l;
        }
    }

    private readonly I76.Formats.Input.HoldRepeat _hold = new();

    /// <summary>The salvage lists' arrows as hold-repeat hotspots: 2·list for the up arrow, 2·list + 1 for the down one, -1 elsewhere.</summary>
    private int ArrowAt(Vector2I m)
    {
        for (int id = 6; id < 12; id++)
        {
            var r = Regions[id];
            if (!r.Salvage || !In(m, (r.X0, r.X1, r.Y0, r.Y1))) continue;
            return In(m, r.Up!.Value) ? 2 * id : In(m, r.Down!.Value) ? 2 * id + 1 : -1;
        }
        return -1;
    }

    private void ArrowStep(int hot)
    {
        int id = hot / 2;
        var r = Regions[id];
        if (hot % 2 == 0) { if (_scroll[id] > 0) { _scroll[id]--; Shell.Sound(82); Draw(id); } }
        else if (_lists[id].Count - (_scroll[id] + 1) >= r.Rows) { _scroll[id]++; Shell.Sound(82); Draw(id); }
    }

    public override void Tick()
    {
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, ArrowAt(Shell.Mouse), Shell.Delta); n > 0; n--) ArrowStep(held);
        if (Shell.Wheel != 0)
            for (int id = 6; id < 12; id++)
            {
                var r = Regions[id];
                if (!In(Shell.Mouse, (r.X0, r.X1, r.Y0, r.Y1))) continue;
                int max = Math.Max(0, _lists[id].Count - r.Rows), to = Math.Clamp(_scroll[id] + Shell.WheelRows, 0, max);
                if (to != _scroll[id]) { _scroll[id] = to; Draw(id); }
            }
        if (!Shell.Clicked) return;
        var m = Shell.Mouse;
        if (In(m, Accept)) { Shell.Sound(82); AcceptSalvage(); return; }
        if (In(m, Bookmark)) { Shell.Sound(82); SaveBookmark(); return; }
        for (int id = 0; id < 12; id++)
        {
            var r = Regions[id];
            if (!In(m, (r.X0, r.X1, r.Y0, r.Y1))) continue;
            var l = _lists[id];
            if (r.Salvage)
            {
                if (In(m, r.Up!.Value)) { _hold.Begin(2 * id); ArrowStep(2 * id); return; }
                if (In(m, r.Down!.Value)) { _hold.Begin(2 * id + 1); ArrowStep(2 * id + 1); return; }
                if (m.Y <= r.Up.Value.Item4 || m.Y >= r.Down.Value.Item3) return;
                int k = (m.Y - r.Y0) / P - 1 + _scroll[id];
                if (k >= 0 && k < l.Count) Move(id, k);
            }
            else
            {
                int k = (m.Y - r.Y0) / P;
                if (k >= 0 && k < l.Count) Move(id, k);
            }
            return;
        }
    }

    /// <summary>
    /// The entry to its twin list — into an inventory list only
    /// while it has room (its rows); field salvage becomes the van's, anything
    /// else (a car part included, unchecked) goes to the field. Appended to an
    /// inventory list, sorted into a salvage one.
    /// </summary>
    private void Move(int from, int k)
    {
        int to = Twin[from];
        if (!Regions[to].Salvage && _lists[to].Count >= Regions[to].Rows) return;
        var n = _lists[from][k];
        _lists[from].RemoveAt(k);
        if (n.State == PartState.Field) n.State = PartState.Van;
        else { if (n.State == PartState.Repair) Inv.Unqueue(n); n.State = PartState.Field; n.Slot = -1; }
        if (Regions[to].Salvage) SortIn(to, n); else _lists[to].Add(n);
        if (Regions[from].Salvage) _scroll[from] = Math.Max(0, Math.Min(_scroll[from], _lists[from].Count - Regions[from].Rows));
        _moved = true;
        Shell.Sound(82);
        Draw(from);
        Draw(to);
    }

    /// <summary>ACCEPT SALVAGE: after T01 with nothing taken the "Leave without taking salvage?" question; after T02 the bookmark reminder; then the garage.</summary>
    private void AcceptSalvage()
    {
        Shell.StopVoice();
        bool firstAfterT01 = Shell.Trip.Mission == 2 && !Shell.Trip.FromBookmark && !Shell.Trip.GarageVisited;
        if (firstAfterT01 && !_moved)
        {
            Shell.Voice(0x4a);
            Shell.Modal(new ConfirmPopup
            {
                Lines = ["Leave without taking salvage?"],
                Answer = yes =>
                {
                    if (yes) { Shell.Voice(0x4b); ToGarage(); }
                    else { Shell.StopVoice(); Shell.Voice(0x4c); Repaint(); }
                },
            });
            return;
        }
        Shell.Sound(0x4d);
        ToGarage();
    }

    private void ToGarage()
    {
        if (Shell.Trip.Mission == 3 && !Shell.Trip.GarageVisited) Notice(32, () => Shell.Go(new GarageScreen()));     // REMEMBER, USE THE "SAVE BOOKMARK" BUTTON…
        else Shell.Go(new GarageScreen());
    }
}

/// <summary>
/// 0xc00f BUILD AND REPAIR FORM,
/// the trip's record 1: the car's parts, weapons, droppers and specials as
/// lists with their blueprint sprites, the REPAIR ORDER and its gauge,
/// DEFENSE, the weight; the pickers and the CAR/VAN INVENTORY pop-up; DONE
/// launches the next mission.
/// </summary>
public sealed class GarageScreen : GarageBase, IChassisForm
{
    /// <summary>Opened from a melee form's CONFIGURE CHASSIS: the CHASSIS CONFIGURATION FORM for that model and variant.</summary>
    public MeleeChassis? Melee { get; init; }
    private bool _built, _changed, _renamed, _renaming;
    private string _entryName = "", _entryVtf = "";
    private readonly List<int> _parts = [], _weapons = [], _droppers = [], _specials = [];
    private readonly List<string> _weaponLabels = [];
    private readonly List<int> _weaponGroup = [];       // 0 Fwd, 1 Top, 2 Rear
    private int _repairScroll;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();
    private Vector2I _repeatAtMouse;
    private static readonly string[] PartLabels = ["Engine", "Suspension", "Brakes", "Tire FR", "Tire FL", "Tire RR", "Tire RL"];

    public override void Enter()
    {
        if (Melee is { } mc)
        {
            // built once: PARTS CATALOG comes back to the form as it was
            if (!_built)
            {
                _built = true;
                var vfs = Shell.Vfs;
                var car = TripCar.FromVcf(vfs.Read((mc.Data ?? Shell.Melee).Vcf(mc.Model, mc.Variant)), n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, n));
                Setup(TripInventory.FromCar(car, Shell.Parts, vfs));
                (_entryName, _entryVtf) = (Inv.Car.Variant, Inv.Car.Vtf);
                BuildLists();
            }
            Repaint();
            return;
        }
        Setup();
        BuildLists();
        bool first = !Shell.Trip.GarageVisited;
        Shell.Trip.GarageVisited = true;
        if (first && Shell.Trip.Mission == 2 && !Shell.Trip.FromBookmark) Shell.Voice(0x4e);        // the garage intro, after T01
        Repaint();
    }

    private void BuildLists()
    {
        var slots = Inv.Slots;
        int Find(int type, string? label = null) => slots.FindIndex(s => s.Type == type && (label is null || s.Label == label));
        _parts.Clear(); _weapons.Clear(); _droppers.Clear(); _specials.Clear(); _weaponLabels.Clear(); _weaponGroup.Clear();
        _parts.AddRange([Find(2), Find(3), Find(4), Find(5, "WHL FR"), Find(5, "WHL FL"), Find(5, "WHL BR"), Find(5, "WHL BL")]);
        // All Fwd, then all Top, then all Rear, then the droppers, numbered within each group
        int Group(CarSlot s) => s.MountStyle is 1 or 3 ? 1 : s.Facing == 2 ? 2 : 0;
        var weaponSlots = slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 7).OrderBy(x => Group(x.s)).ToList();
        string[] names = ["Fwd", "Top", "Rear"];
        int[] count = new int[3];
        foreach (var (s, i) in weaponSlots) { int g = Group(s); _weapons.Add(i); _weaponGroup.Add(g); _weaponLabels.Add($"#{++count[g]} {names[g]}"); }
        _droppers.AddRange(slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 8).Select(x => x.i));
        _specials.AddRange(slots.Select((s, i) => (s, i)).Where(x => x.s.Type == 0xc).Select(x => x.i).Take(3));
    }

    // the regions, the weapon and dropper ones sized from the slot counts
    private (int, int, int, int) PartsR => (106, 263, 39, 156);
    private (int, int, int, int) WeaponsR => (106, 266, 165, 165 + _weapons.Count * P);
    private (int, int, int, int) DroppersR => (106, 266, 165 + _weapons.Count * P, 165 + (_weapons.Count + _droppers.Count) * P);
    private static readonly (int, int, int, int) SpecialsR = (106, 263, 274, 323), RepairR = (450, 616, 48, 263), DefenseR = (106, 265, 353, 436),
        SalvageR = (170, 309, 451, 468), DoneR = (330, 469, 451, 468), BookmarkR = (490, 631, 450, 469);
    // the melee variant's own: region 4 CANCEL, 7 PARTS CATALOG (where FIELD SALVAGE is), 8 RENAME, 10 the paint scheme
    private static readonly (int, int, int, int) CancelR = (492, 630, 450, 469), CatalogR = (170, 309, 451, 468), RenameR = (309, 359, 20, 32), PaintR = (251, 266, 330, 348);

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db, Melee is null ? 1 : 2);
        var arrow = Shell.Db.Bank(0x24)[14];
        if (Melee is { } mc)
        {
            // make/model and the variant's name at the top, the 3/4 sketch where the trip has the repair order, the paint scheme under WEAPONS
            Text(F45, 105, 2, Inv.MakeModel);
            DrawName();
            // Nitro: bank 55 by the car's own art id
            var art = (mc.Data ?? Shell.Melee).Models[mc.Model].Art;
            Frame.Blit(Shell.Install.IsNitro ? Shell.Db.BankNative(55)[art] : Shell.Db.Bank(0x3d)[art], 450, 70);
            Frame.BlitAt(arrow, 254, 332);
            DrawPaint();
        }
        else Text(F45, 82, 10, Inv.MakeModel);
        for (int k = 0; k < PartLabels.Length; k++) { Text(F47, 31, 43 + k * P, PartLabels[k]); Frame.BlitAt(arrow, 254, 40 + k * P); }
        for (int k = 0; k < _weapons.Count + _droppers.Count; k++)
        {
            Text(F47, 31, 169 + k * P, k < _weapons.Count ? _weaponLabels[k] : $"#{k - _weapons.Count + 1} Dropper");
            Frame.BlitAt(arrow, 254, 166 + k * P);
        }
        Text(F47, 31, 255, "Hand");
        Text(F45, 108, 251, ".45 CAL");
        for (int k = 0; k < 3; k++) { Text(F47, 31, 278 + k * P, $"Special {k + 1}"); Frame.BlitAt(arrow, 254, 275 + k * P); }
        DrawLists();
        if (Melee is null) DrawRepairs();
        DrawDefense();
        DrawWeight();
    }

    private PartNode? Node(int slot) => slot >= 0 ? Inv.InSlot(slot) : null;

    /// <summary>The variant's name at (82, 17) over a restore of (77, 19, 224, 14), the cursor while RENAME edits it.</summary>
    private void DrawName()
    {
        Frame.Restore(77, 19, 224, 14);
        Text(F45, 82, 17, Inv.Car.Variant + (_renaming ? "_" : ""));
    }

    /// <summary>The paint scheme's display name (the type-0xe part named by the car's .vtf) at (108, 332).</summary>
    private void DrawPaint()
    {
        Frame.Restore(105, 331, 130, 17);
        var paint = Shell.Parts.Parts.FirstOrDefault(p => p.Type == 14 && p.Stem.Equals(Inv.Car.Vtf, StringComparison.OrdinalIgnoreCase));
        Text(F45, 108, 332, paint?.Id ?? "");
    }
    private string Name(int slot) => Node(slot)?.Part.Id ?? "Empty";

    /// <summary>Each list restored minus its arrow column, entries at (x0+1, y0+2+i·17), each part also drawn on the blueprint.</summary>
    private void DrawLists()
    {
        void List((int X0, int X1, int Y0, int Y1) r, List<int> slots, Action<int, PartNode?> blueprint)
        {
            Frame.Restore(r.X0, r.Y0, r.X1 - r.X0 - 14, r.Y1 - r.Y0 + 1);
            for (int i = 0; i < slots.Count; i++)
            {
                var n = Node(slots[i]);
                int x = r.X0 + 1, y = r.Y0 + 2 + i * P;
                if (n is not null) Stroke(n, x, y);
                Text(F45, x, y, n?.Part.Id ?? "Empty");
                blueprint(i, n);
            }
        }
        List(PartsR, _parts, (i, n) => Blueprint(i < 3 ? new[] { 2, 3, 4 }[i] : 5, i < 3 ? 0 : i - 3, n));
        List(WeaponsR, _weapons, (i, n) => Weapon(_weaponGroup[i], _weaponGroup.Take(i).Count(g => g == _weaponGroup[i]), n));
        List(DroppersR, _droppers, (i, n) => Blueprint(8, i, n));
        List(SpecialsR, _specials, (i, n) => Blueprint(0xc, i, n));
    }

    private static readonly (int X, int Y)[] TyreAt = [(408, 119), (270, 119), (408, 312), (270, 312)];
    private static readonly (int X, int Y)[][] WeaponAt = [[(305, 52), (395, 52), (336, 52)], [(316, 229), (372, 229)], [(400, 382), (297, 382)]];
    private static readonly (int X, int Y)[] DropperAt = [(331, 382), (331, 405)], SpecialAt = [(403, 191), (403, 226), (403, 260)];

    /// <summary>The part's rectangle restored, then its sprite (bank by type and number, sprite by position and damage) at its fixed place.</summary>
    private void Blueprint(int type, int counter, PartNode? n)
    {
        int ord = n is null ? -1 : CodeNumber(n.Part) - 1, dmg = n is null ? 0 : Damage(n);
        switch (type)
        {
            case 2: Frame.Restore(325, 112, 64, 92); if (ord is >= 0 and <= 3) Frame.BlitAt(Shell.Db.Bank(0x25 + ord)[dmg], 325, 112); break;
            case 3: Frame.Restore(307, 298, 100, 80); if (ord is >= 0 and <= 3) Frame.BlitAt(Shell.Db.Bank(0x29 + ord)[dmg], 307, 298); break;
            case 4: Frame.Restore(283, 174, 40, 24); if (ord is >= 0 and <= 3) Frame.BlitAt(Shell.Db.Bank(0x2d + ord)[dmg], 283, 174); break;
            case 5 when counter < 4:
                var (tx, ty) = TyreAt[counter];
                Frame.Restore(tx, ty, 36, 52);
                if (ord is >= 0 and <= 3) Frame.BlitAt(Shell.Db.Bank(0x31 + ord)[4 * counter + dmg], tx, ty);
                break;
            case 8 when counter < 2:
                Frame.Restore(DropperAt[counter].X, DropperAt[counter].Y, 52, 22);
                if (ord is >= 0 and <= 6) Frame.BlitAt(Shell.Db.Bank(0x39)[4 * ord + dmg], DropperAt[counter].X, DropperAt[counter].Y);
                break;
            case 0xc when counter < 3:
                Frame.Restore(SpecialAt[counter].X, SpecialAt[counter].Y, 40, 24);
                if (ord is >= 0 and <= 8) Frame.BlitAt(Shell.Db.Bank(0x3b)[4 * ord + dmg], SpecialAt[counter].X, SpecialAt[counter].Y);
                break;
        }
    }

    /// <summary>A weapon by its component code (slg 0x35, spp 0x36, flm 0x37, mor 0x38), at its group's spot; the rear ones rotated 180° about (x+16, y+51).</summary>
    private void Weapon(int group, int i, PartNode? n)
    {
        if (i >= WeaponAt[group].Length) return;
        var (x, y) = WeaponAt[group][i];
        Frame.Restore(x, y, 22, 54);
        if (n is null || n.Part.Code.Length < 5) return;
        int bank = n.Part.Code[..3] switch { "slg" => 0x35, "spp" => 0x36, "flm" => 0x37, "mor" => 0x38, _ => 0 };
        int num = CodeNumber(n.Part);
        if (bank == 0 || num < 1) return;
        var s = Shell.Db.Bank(bank)[4 * (num - 1) + Damage(n)];
        if (group == 2) Frame.BlitRotated(s, x + 16, y + 51); else Frame.BlitAt(s, x, y);
    }

    /// <summary>Up to ten entries from the scroll, at (451, 67+i·17); the gauge from the visible rows (bank 0x3f at (618, 48)).</summary>
    private void DrawRepairs()
    {
        var (x0, x1, y0, y1) = RepairR;
        Frame.Restore(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        int sum = 0;
        for (int i = 0; i < 10 && _repairScroll + i < Inv.Repairs.Count; i++)
        {
            var n = Inv.Repairs[_repairScroll + i];
            Stroke(n, 451, 67 + i * P);
            Text(F45, 451, 67 + i * P, n.Part.Id);
            sum += n.RepairCost;
        }
        Frame.Restore(618, 48, 12, 216);
        Frame.BlitAt(Shell.Db.Bank(0x3f)[Math.Min(sum, TripInventory.RepairBudget) >> 1], 618, 48);
    }

    // DEFENSE: rows FRONT, RIGHT, LEFT, REAR in the shell's order; the car keeps front, left, right, rear
    private static readonly int[] Zone = [0, 2, 1, 3];
    private static readonly int[][] RowY = [[355, 366], [373, 382], [390, 400], [406, 416]];
    private static readonly int[][] PlusY = [[355, 366], [372, 382], [390, 400], [406, 416]];

    /// <summary>Each zone's armour and chassis as "%1.1f" of a tenth, LBS. AVAILABLE (the ninth value) below.</summary>
    private void DrawDefense()
    {
        var (x0, x1, y0, y1) = DefenseR;
        Frame.Restore(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        var c = Inv.Car;
        for (int k = 0; k < 4; k++)
        {
            Text(F47, 124, 357 + k * P, (c.Armour[Zone[k]] * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            Text(F47, 224, 357 + k * P, (c.Chassis[Zone[k]] * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }
        Text(F47, 132, 424, (c.Ninth * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>"%d LBS" at (538, 420).</summary>
    private void DrawWeight()
    {
        Frame.Restore(518, 420, 110, 17);
        Text(F45, 538, 420, $"{Inv.Weight()} LBS");
    }

    public override void Tick()
    {
        // DEFENSE ± and the repair list's arrows repeat while held (HoldRepeat): 0 the defense button, 1 and 2 the arrows
        int held = _hold.Id, hit = In(Shell.Mouse, DefenseR) ? 0 : In(Shell.Mouse, (450, 468, 48, 64)) ? 1 : In(Shell.Mouse, (450, 468, 235, 264)) ? 2 : -1;
        for (int n = _hold.Update(Shell.MouseHeld, hit, Shell.Delta); n > 0; n--)
            if (held == 0) Defense(_repeatAtMouse); else ScrollRepairs(held == 1 ? -1 : 1);
        if (_renaming) { Rename(); return; }
        if (Shell.Wheel != 0 && In(Shell.Mouse, RepairR))
        {
            int to = Math.Clamp(_repairScroll + Shell.WheelRows, 0, Math.Max(0, Inv.Repairs.Count - 1));
            if (to != _repairScroll) { _repairScroll = to; DrawRepairs(); }
        }
        if (!Shell.Clicked) return;
        var m = Shell.Mouse;
        if (In(m, PartsR)) { Click(); PartsClick(m); }
        else if (_weapons.Count > 0 && In(m, WeaponsR)) { Click(); SlotClick(m, WeaponsR, _weapons, 7); }
        else if (_droppers.Count > 0 && In(m, DroppersR)) { Click(); SlotClick(m, DroppersR, _droppers, 8); }
        else if (In(m, SpecialsR)) { Click(); SlotClick(m, SpecialsR, _specials, 0xc); }
        else if (Melee is { } mc)
        {
            if (In(m, CancelR)) { Click(); CancelMelee(); }
            else if (In(m, DefenseR)) { Click(); if (Defense(m)) { _hold.Begin(0); _repeatAtMouse = m; } }
            else if (In(m, CatalogR)) { Click(); Shell.Go(new PartsCatalogScreen { Back = this }); }
            else if (In(m, RenameR)) { Click(); _renaming = true; DrawName(); }
            else if (In(m, DoneR)) { Click(); MeleeDone(); }
            else if (In(m, PaintR)) { Click(); PaintPicker(); }
        }
        else if (In(m, RepairR)) { Click(); RepairClick(m); }
        else if (In(m, DefenseR)) { Click(); if (Defense(m)) { _hold.Begin(0); _repeatAtMouse = m; } }
        else if (In(m, SalvageR)) { Click(); Shell.StopVoice(); Shell.Go(new FieldSalvageScreen()); }
        else if (In(m, DoneR)) { Click(); Done(); }
        else if (In(m, BookmarkR)) { Click(); SaveBookmark(); }
    }

    private void Click() => Shell.Sound(82);

    /// <summary>A melee chassis form's CANCEL: the variant as it came, and back to the form that opened it.</summary>
    public void CancelMelee()
    {
        if (Melee is not { } mc) return;
        (Inv.Car.Variant, Inv.Car.Vtf) = (_entryName, _entryVtf);
        mc.Cancel();
    }

    public override bool TextEditing => _renaming;
    public override void StopEditing() { _renaming = false; if (Inv.Car.Variant != _entryName) _renamed = true; DrawName(); }

    private bool ScrollRepairs(int by)
    {
        int to = Math.Clamp(_repairScroll + by, 0, Math.Max(0, Inv.Repairs.Count - 1));
        if (to == _repairScroll) return false;
        _repairScroll = to;
        DrawRepairs();
        return true;
    }

    /// <summary>
    /// Everything a click does here: the arrow of each part row, a weapon, dropper or special row's name (strips it to the van) and its
    /// arrow (the picker), the repair order's rows, the DEFENSE buttons, and the screen's buttons.
    /// </summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            for (int k = 0; k < _parts.Count; k++) if (_parts[k] >= 0) l.Add(PadRegion.Of(252, 39 + P * k, 11, P));
            void Slots((int X0, int X1, int Y0, int Y1) r, List<int> slots)
            {
                for (int k = 0; k < slots.Count; k++)
                {
                    if (Node(slots[k]) is not null) l.Add(PadRegion.Of(r.X0 + 1, r.Y0 + P * k, 146, P));
                    l.Add(PadRegion.Of(254, r.Y0 + P * k, 10, P));
                }
            }
            Slots(WeaponsR, _weapons);
            Slots(DroppersR, _droppers);
            Slots(SpecialsR, _specials);
            if (Melee is null)
            {
                int n = Math.Min(10, Inv.Repairs.Count - _repairScroll);
                for (int k = 0; k < n; k++)
                {
                    int kk = k;
                    l.Add(PadRegion.Of(451, 65 + P * k, 164, P, d => d == NavDir.Up && kk == 0 ? ScrollRepairs(-1) : d == NavDir.Down && kk == n - 1 ? ScrollRepairs(1) : false));
                }
                l.Add(PadRegion.Of(482, 241, 123, 18));
            }
            for (int k = 0; k < 4; k++)
            {
                l.Add(PadRegion.Of((108, 117, RowY[k][0], RowY[k][1])));
                l.Add(PadRegion.Of((152, 161, PlusY[k][0], PlusY[k][1])));
                l.Add(PadRegion.Of((208, 217, RowY[k][0], RowY[k][1])));
                l.Add(PadRegion.Of((254, 263, RowY[k][0], RowY[k][1])));
            }
            l.Add(PadRegion.Of((185, 246, 422, 435)));
            if (Melee is not null) l.AddRange([PadRegion.Of(CancelR), PadRegion.Of(CatalogR), PadRegion.Of(RenameR), PadRegion.Of(DoneR), PadRegion.Of(PaintR)]);
            else l.AddRange([PadRegion.Of(SalvageR), PadRegion.Of(DoneR), PadRegion.Of(BookmarkR)]);
            return l;
        }
    }

    private void PartsClick(Vector2I m)
    {
        int row = (m.Y - 39) / P;
        if (row < 0 || row >= _parts.Count || m.X <= 251 || _parts[row] < 0) return;
        int type = row < 3 ? new[] { 2, 3, 4 }[row] : 5;
        var list = Candidates(type);
        if (Melee is not null && type == 5) list = [.. list.Where(n => TyreOfAxle(n.Part.Stem, row < 5 ? 0 : 2))];
        var pick = row switch
        {
            0 => Fixed(0, 172, (13, 158, 29, 96), (88, 158, 110, 125), list),
            1 => Fixed(1, 172, (13, 158, 29, 97), (89, 158, 111, 124), list),
            2 => Fixed(2, 164, (9, 158, 29, 112), (90, 158, 125, 138), list),
            _ => Scrolling(10, list),
        };
        // Nitro's melee tyre picker fits the other wheel of the axle too: rows 3+4 are the front pair, 5+6 the rear
        pick.Picked = type == 5 && Melee is not null && Shell.Install.IsNitro
            ? n => { Inv.InstallAxle(_parts[row], _parts[row < 5 ? (row == 3 ? 4 : 3) : (row == 5 ? 6 : 5)], n); _changed = true; Repaint(); }
            : n => Fit(_parts[row], n, null);
        Shell.Modal(pick);
    }

    /// <summary>
    /// The melee tyre picker's filter: a tyre row lists the parts whose stem matches the first seven characters of
    /// that axle's .wdf (record +0x83a front, +0x854 rear) and whose eighth character is not '.': the car's own wheel style, in its sizes.
    /// </summary>
    private bool TyreOfAxle(string stem, int axle)
    {
        var wdf = Inv.Car.Wdf[axle];
        if (wdf.Length < 7 || wdf.Equals("null", StringComparison.OrdinalIgnoreCase)) wdf = Inv.Car.Wdf[axle == 0 ? 2 : 0];
        return wdf.Length >= 7 && stem.Length >= 8 && stem[7] != '.' && stem.StartsWith(wdf[..7], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A weapon, dropper or special row: left of the arrow column strips the part to the van; the arrow opens the picker.</summary>
    private void SlotClick(Vector2I m, (int X0, int X1, int Y0, int Y1) r, List<int> slots, int type)
    {
        int row = (m.Y - r.Y0) / P;
        if (row < 0 || row >= slots.Count) return;
        if (m.X < 254)
        {
            if (Node(slots[row]) is not { } n) return;
            if (Melee is not null) { Inv.Nodes.Remove(n); _changed = true; Repaint(); return; }
            // a stripped dropper keeps its "(C)" (the state is not changed), weapons and specials go to the van
            if (type != 8) n.State = PartState.Van;
            n.Slot = -1;
            Repaint();
            return;
        }
        // Nitro's SPECIALS list has eleven rows, its art two rows taller: CANCEL sits two rows lower
        int extra = Shell.Install.IsNitro ? 2 * P : 0;
        var pick = type == 0xc ? Fixed(5, 130, (13, 158, 29, 182 + extra), (89, 158, 194 + extra, 208 + extra), Candidates(0xc)) : Scrolling(8, Candidates(type));
        pick.Picked = n => Fit(slots[row], n, type == 0xc ? slots : null);
        Shell.Modal(pick);
    }

    /// <summary>The inventory a picker lists: that kind's parts in the car, the van and repair; weapons sorted by class, number, health.</summary>
    private List<PartNode> Candidates(int type)
    {
        if (Melee is not null) return Catalogue(type);
        var l = Inv.Nodes.Where(n => n.State != PartState.Field && n.Part.Type switch
        {
            7 or 6 or 9 or 10 or 11 => type == 7,
            8 => type == 8,
            12 or 13 => type == 0xc,
            1 or 4 => type == 4,
            var t => t == type,
        }).ToList();
        if (type is 7 or 8) return [.. l.OrderBy(n => WeaponClass(n.Part)).ThenBy(n => CodeNumber(n.Part)).ThenBy(n => n.Health)];
        return [.. l.OrderBy(n => n.State == PartState.Car ? 0 : 1)];
    }

    /// <summary>
    /// A pick (TripInventory.Install): the slot's old part to the van, the
    /// picked one into the slot and out of the repair order; a part that sat in
    /// another slot leaves it "Empty" (a move). Every part is its own node, so a
    /// second tyre of one size never displaces the first; only a special of the
    /// same name is swapped out.
    /// </summary>
    private void Fit(int slot, PartNode picked, IEnumerable<int>? sameName)
    {
        // the melee's catalogue has no inventory: the entry goes in as a node of its own, the old part goes
        Inv.Install(slot, picked, catalogue: Melee is not null, sameName);
        if (Melee is not null) _changed = true;
        Repaint();
    }

    private PartPicker Fixed(int sprite, int y, (int, int, int, int) list, (int, int, int, int) cancel, List<PartNode> items) =>
        new() { Sprite = sprite, X = 234, Y = y, List = list, Cancel = cancel, Items = items, Owner = this };

    /// <summary>
    /// The melee pickers' lists (the catalogue sorted by type): engines,
    /// suspensions, brakes (types 1 and 4), tyres; weapons (6, 7, 9, 10, 11) without the shell's banned ones (the
    /// base game's list or Nitro's shorter one, half of it open to a variant named "natk…"), sorted as the inventory's;
    /// droppers without the banned ones; the nine specials. Bare names, no "Empty". Hidden Weapons &amp; Cars adds the
    /// complete hidden weapons (docs/hidden-content.md).
    /// </summary>
    private List<PartNode> Catalogue(int type) => ChassisRules.Catalogue(Shell.Parts, type, Shell.Install.IsNitro, Shell.Settings.HiddenContent, Inv.Car.Variant);

    /// <summary>Region 10: bank-0x24 sprite 15 at (234, 156), five rows at (+18, +48), every paint scheme sharing the .vtf's first four letters.</summary>
    private void PaintPicker()
    {
        var stem = Inv.Car.Vtf.Length >= 4 ? Inv.Car.Vtf[..4] : Inv.Car.Vtf;
        var items = Shell.Parts.Parts.Where(p => p.Type == 14 && p.Stem.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .Select(p => new PartNode(p, p.MaxHp, PartState.Van)).ToList();
        Shell.Modal(new PartPicker
        {
            Sprite = 15, X = 234, Y = 156, List = (13, 158, 48, 131), Cancel = (89, 158, 141, 157), Up = (17, 35, 10, 26), Down = (17, 35, 135, 150),
            Visible = 5, RowAt = new Vector2I(18, 48), Items = items, Owner = this,
            Picked = n => { Inv.Car.Vtf = n.Part.Stem; _changed = true; Repaint(); },
        });
    }

    /// <summary>RENAME: a line edit at (82, 17), at most 15 characters in 160 px; a different name counts as renamed.</summary>
    private void Rename()
    {
        var name = Inv.Car.Variant;
        bool changed = false;
        foreach (char c in Shell.Typed) if (name.Length < 15 && F45.Measure(name + c) <= 160) { name += c; changed = true; }
        if (Shell.Keys.Contains(Key.Backspace) && name.Length > 0) { name = name[..^1]; changed = true; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter) || Shell.Keys.Contains(Key.Escape) || Shell.Clicked)
        {
            _renaming = false;
            changed = true;
            if (name != _entryName) _renamed = true;
        }
        Inv.Car.Variant = name;
        if (changed) DrawName();
    }

    /// <summary>
    /// DONE on the chassis form: the checks, then —
    /// unchanged, straight back; changed but not renamed, a stock variant asks
    /// for a new name (notice 30), a saved one "Overwrite the current
    /// variant?"; renamed, "Wanna save the new variant?" (no restores the name
    /// and paint) or, for a saved variant's name, the overwrite question. The
    /// car goes to the variant's file (addon\&lt;stem&gt;&lt;n&gt;.vcf).
    /// </summary>
    private void MeleeDone()
    {
        var mc = Melee!;
        var data = mc.Data ?? Shell.Melee;
        var model = data.Models[mc.Model];
        var plan = ChassisRules.PlanDone(Inv, _changed, _renamed, mc.Variant, model.Variants, model.Base, Shell.UnlimTurrets);
        void Save(int n)
        {
            data.SaveVariant(mc.Model, n, ChassisRules.VariantFile(Inv, model.MakeModel, Shell.Install.IsNitro, out var car), car.Variant);
            mc.Done(n);
        }
        void Ask(string line, Action yes, Action no) => Shell.Modal(new ConfirmPopup { Lines = [line], Answer = a => { if (a) yes(); else no(); } });
        switch (plan.Kind)
        {
            case ChassisRules.DoneKind.Invalid: Notice(plan.Notice, Repaint); break;
            case ChassisRules.DoneKind.Unchanged: mc.Done(mc.Variant); break;
            case ChassisRules.DoneKind.NeedsName: Notice(plan.Notice, Repaint); break;
            case ChassisRules.DoneKind.AskOverwrite: Ask("Overwrite the current variant?", () => Save(plan.Save), Repaint); break;
            case ChassisRules.DoneKind.AskSaveNew:
                Ask("Wanna save the new variant?", () => Save(plan.Save), () =>
                {
                    // base: NO restores the name and paint; Nitro stays on the form with what was typed
                    if (!Shell.Install.IsNitro)
                    {
                        (Inv.Car.Variant, Inv.Car.Vtf) = (_entryName, _entryVtf);
                        _renamed = false;
                    }
                    Repaint();
                });
                break;
        }
    }

    private PartPicker Scrolling(int sprite, List<PartNode> items) =>
        new() { Sprite = sprite, X = 234, Y = 106, List = (17, 155, 48, 231), Cancel = (88, 158, 241, 257), Up = (18, 36, 8, 24), Down = (18, 36, 235, 251), Items = items, Owner = this };

    private void RepairClick(Vector2I m)
    {
        if (m.X >= 482 && m.X <= 604 && m.Y >= 241 && m.Y <= 258) { Shell.Modal(new InventoryPopup { Owner = this }); return; }
        if (In(m, (450, 468, 48, 64))) { _hold.Begin(1); if (_repairScroll > 0) { _repairScroll--; DrawRepairs(); } return; }
        if (In(m, (450, 468, 235, 264))) { _hold.Begin(2); if (_repairScroll + 1 < Inv.Repairs.Count) { _repairScroll++; DrawRepairs(); } return; }
        if (m.Y < 64 || m.Y > 235) return;
        int k = (m.Y - 65) / P + _repairScroll;
        if (k < 0 || k >= Inv.Repairs.Count) return;
        // Out of the order and into the van (state 2)
        var n = Inv.Repairs[k];
        Inv.Repairs.RemoveAt(k);
        n.State = PartState.Van;
        n.Slot = -1;
        Repaint();
    }

    /// <summary>A DEFENSE button: ±10 units between a zone and the pool (min a zone at trunc(total/32)), or BALANCE. True when a ± was hit (it repeats).</summary>
    private bool Defense(Vector2I m)
    {
        var c = Inv.Car;
        if (m.X > 185 && m.X < 246 && m.Y > 422 && m.Y < 435)
        {
            ChassisRules.Balance(c);
            _changed = true;
            DrawDefense(); DrawWeight();
            return false;
        }
        for (int k = 0; k < 4; k++)
        {
            bool row = m.Y > RowY[k][0] && m.Y < RowY[k][1], plusRow = m.Y > PlusY[k][0] && m.Y < PlusY[k][1];
            int z = Zone[k];
            if (row && m.X > 108 && m.X < 117) ChassisRules.Adjust(c, z, chassis: false, more: false);
            else if (plusRow && m.X > 152 && m.X < 161) ChassisRules.Adjust(c, z, chassis: false, more: true);
            else if (row && m.X > 208 && m.X < 217) ChassisRules.Adjust(c, z, chassis: true, more: false);
            else if (row && m.X > 254 && m.X < 263) ChassisRules.Adjust(c, z, chassis: true, more: true);
            else continue;
            _changed = true;
            DrawDefense(); DrawWeight();
            return true;
        }
        return false;
    }

    /// <summary>DONE: the checks; after T01 the "Groove…" question; then the next mission in this car.</summary>
    private void Done()
    {
        if (Inv.Validate(Shell.UnlimTurrets) is { } box) { Notice(box, Repaint); return; }
        void Go()
        {
            Shell.StopVoice();
            Shell.Sound(0x50);
            Inv.LeaveField();
            Shell.Trip.Save();
            Shell.LaunchTrip(Inv);
        }
        if (Shell.Trip.Mission == 2 && !Shell.Trip.FromBookmark)
        {
            Shell.Modal(new ConfirmPopup
            {
                Lines = ["Groove, anything still in 'Field", "Salvage' gotta be left behind.", "You okay with that?"],
                At = [new(225, 180), new(215, 200), new(265, 220)],
                Answer = yes => { if (yes) Go(); else Shell.Go(new FieldSalvageScreen()); },
            });
            return;
        }
        Go();
    }

    /// <summary>For the pop-ups: draw a stroke and an entry as the lists do.</summary>
    public void Entry(PartNode n, int x, int y, bool legend = true)
    {
        Stroke(n, x, y);
        Text(F45, x, y, (legend && Melee is null ? Legend(n) : "") + n.Part.Id);
    }

    public TripInventory Inventory => Inv;
}

/// <summary>
/// A garage part picker: a fixed list (frame at x 234,
/// rows from the list rectangle's corner + (3, 1)) or a scrolling one
/// (rows at (x+17, y+48), eleven visible), a 6-px shadow, rows
/// with stroke and legend; a row picks (past the last entry is ignored),
/// CANCEL closes.
/// </summary>
public sealed class PartPicker : ShellScreen
{
    public required int Sprite { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required (int X0, int X1, int Y0, int Y1) List { get; init; }
    public required (int X0, int X1, int Y0, int Y1) Cancel { get; init; }
    public (int X0, int X1, int Y0, int Y1)? Up { get; init; }
    public (int X0, int X1, int Y0, int Y1)? Down { get; init; }
    public required List<PartNode> Items { get; init; }
    public required GarageScreen Owner { get; init; }
    public Action<PartNode> Picked { get; set; } = _ => { };
    /// <summary>A scrolling list's visible rows and their origin in the frame (eleven at (17, 48); the paint picker five at (18, 48)).</summary>
    public int Visible { get; init; } = 11;
    public Vector2I RowAt { get; init; } = new(17, 48);
    public override bool EscapeOpensOptions => false;
    private int _scroll, _w, _h;
    private bool Scrolls => Up is not null;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();
    private int ArrowAt(Vector2I p) => Up is { } u && In(u, p) ? 0 : Down is { } d && In(d, p) ? 1 : -1;
    private void ArrowStep(int a) { if (a == 0) { if (_scroll > 0) { _scroll--; Draw(); } } else if (_scroll + Visible < Items.Count) { _scroll++; Draw(); } }

    public override void Enter()
    {
        var s = Shell.Db.Bank(0x24)[Sprite];
        _w = s?.Width ?? 172; _h = s?.Height ?? 136;
        Draw();
    }

    private void Draw()
    {
        Frame.Fill(X + 6, Y + _h, _w, 6, 0x0f);
        Frame.Fill(X + _w, Y + 6, 6, _h, 0x0f);
        Frame.BlitAt(Shell.Db.Bank(0x24)[Sprite], X, Y);
        int rows = Scrolls ? Visible : Items.Count;
        for (int i = 0; i < rows && _scroll + i < Items.Count; i++)
        {
            int x = Scrolls ? X + RowAt.X : X + List.X0 + 3, y = Scrolls ? Y + RowAt.Y + i * 17 : Y + List.Y0 + 1 + i * 17;
            Owner.Entry(Items[_scroll + i], x, y);
        }
    }

    private bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;

    private bool ScrollBy(int by)
    {
        int to = Math.Clamp(_scroll + by, 0, Math.Max(0, Items.Count - Visible));
        if (to == _scroll) return false;
        _scroll = to;
        Draw();
        return true;
    }

    private PadRegion Rel((int X0, int X1, int Y0, int Y1) r) => PadRegion.Of((X + r.X0, X + r.X1, Y + r.Y0, Y + r.Y1));

    /// <summary>The visible rows (past the first or last a scrolling list scrolls), the arrows, CANCEL.</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            int n = Scrolls ? Math.Min(Visible, Items.Count - _scroll) : Items.Count;
            for (int i = 0; i < n; i++)
            {
                int ii = i;
                l.Add(PadRegion.Of(X + List.X0 + 1, Y + List.Y0 + 17 * i, List.X1 - List.X0 - 2, 17,
                    Scrolls ? d => d == NavDir.Up && ii == 0 ? ScrollBy(-1) : d == NavDir.Down && ii == n - 1 ? ScrollBy(1) : false : null));
            }
            l.Add(Rel(Cancel));
            return l;
        }
    }

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(); return; }
        if (Shell.Wheel != 0 && Scrolls) { _scroll = Math.Clamp(_scroll + Shell.WheelRows, 0, Math.Max(0, Items.Count - Visible)); Draw(); }
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, ArrowAt(Shell.Mouse), Shell.Delta); n > 0; n--) ArrowStep(held);
        if (!Shell.Clicked) return;
        var p = Shell.Mouse;
        if (ArrowAt(p) is >= 0 and var arrow) { _hold.Begin(arrow); ArrowStep(arrow); return; }
        if (In(Cancel, p)) { Close(); return; }
        if (!In(List, p)) return;
        int i = (p.Y - Y - List.Y0) / 17 + _scroll;
        if (i < 0 || i >= Items.Count) return;
        Shell.Sound(82);
        Close();
        Picked(Items[i]);
    }

    private void Close()
    {
        Frame.Restore(X, Y, _w + 10, _h + 10);
        Shell.Modal(null);
        Owner.Repaint();
    }
}

/// <summary>
/// The CAR/VAN INVENTORY pop-up: bank 0x24 sprite 6 at
/// (16, 96) with a 12-px shadow, six columns of the inventory; a click puts
/// a damaged part into repair or takes it back out (over the 32-point budget
/// the "too much" voice, 0x4f); DONE keeps the order, CANCEL undoes it.
/// </summary>
public sealed class InventoryPopup : ShellScreen
{
    public required GarageScreen Owner { get; init; }
    public override bool EscapeOpensOptions => false;
    private const int X = 16, Y = 96;
    private static readonly (int X0, int X1, int Y0, int Y1, int List)[] Columns =
        [(10, 156, 31, 81, 0), (10, 156, 99, 251, 5), (157, 303, 49, 234, 4), (305, 451, 49, 234, 3), (448, 597, 31, 115, 2), (451, 597, 134, 200, 1)];
    private static readonly (int, int, int, int) DoneR = (298, 438, 262, 278), CancelR = (459, 598, 262, 278);
    private readonly List<(PartNode Node, bool WasQueued)> _touched = [];

    private List<PartNode> Column(int list)
    {
        var l = Owner.Inventory.Nodes.Where(n => n.State != PartState.Field && TripInventory.ListOf(n.Part.Type) == list);
        // the inventory weapons sort by class, number and health; the other lists keep the car's parts first
        return list == 4 ? [.. l.OrderBy(n => GarageBase.ClassOf(n.Part)).ThenBy(n => GarageBase.NumberOf(n.Part)).ThenBy(n => n.Health)]
                         : [.. l.OrderBy(n => n.State == PartState.Car ? 0 : 1)];
    }

    public override void Enter()
    {
        Frame.Fill(X + 12, Y + 288, 608, 12, 0x0f);
        Frame.Fill(X + 608, Y + 12, 12, 288, 0x0f);
        Draw();
    }

    private void Draw()
    {
        Frame.BlitAt(Shell.Db.Bank(0x24)[6], X, Y);
        foreach (var (x0, _, y0, _, list) in Columns)
        {
            var col = Column(list);
            for (int i = 0; i < col.Count; i++) Owner.Entry(col[i], X + x0 + 3, Y + y0 + 2 + i * 17);
        }
    }

    private static bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            foreach (var (x0, x1, y0, _, list) in Columns)
                for (int i = 0; i < Column(list).Count; i++) l.Add(PadRegion.Of(X + x0 + 1, Y + y0 + 2 + 17 * i, x1 - x0 - 2, 17));
            l.Add(PadRegion.Of((X + DoneR.Item1, X + DoneR.Item2, Y + DoneR.Item3, Y + DoneR.Item4)));
            l.Add(PadRegion.Of((X + CancelR.Item1, X + CancelR.Item2, Y + CancelR.Item3, Y + CancelR.Item4)));
            return l;
        }
    }

    public override void Tick()
    {
        bool esc = Shell.Keys.Contains(Key.Escape);        // the pad's B and Start: CANCEL
        if (!Shell.Clicked && !esc) return;
        var p = esc ? new Vector2I(X + (CancelR.Item1 + CancelR.Item2) / 2, Y + (CancelR.Item3 + CancelR.Item4) / 2) : Shell.Mouse;
        var inv = Owner.Inventory;
        if (In(DoneR, p)) { Shell.StopVoice(); Close(); return; }
        if (In(CancelR, p))
        {
            for (int i = _touched.Count - 1; i >= 0; i--)
            {
                var (n, was) = _touched[i];
                bool now = inv.Repairs.Contains(n);
                if (now && !was) inv.Unqueue(n); else if (!now && was) inv.QueueRepair(n);
            }
            Close();
            return;
        }
        foreach (var (x0, x1, y0, y1, list) in Columns)
        {
            if (!In((x0, x1, y0, y1), p)) continue;
            var col = Column(list);
            int i = (p.Y - Y - y0 - 2) / 17;
            if (i < 0 || i >= col.Count) return;
            var n = col[i];
            bool was = inv.Repairs.Contains(n);
            if (was) inv.Unqueue(n);
            else if (n.Health >= n.Part.MaxHp) return;
            else if (!inv.QueueRepair(n)) { Shell.StopVoice(); Shell.Voice(0x4f); return; }      // too much
            _touched.Add((n, was));
            Shell.Sound(82);
            Draw();
            return;
        }
    }

    private void Close()
    {
        Frame.Restore(X, Y, 608 + 16, 288 + 16);
        Shell.Modal(null);
        Owner.Repaint();
    }
}

/// <summary>A melee form's CONFIGURE CHASSIS: the model and variant on display, then back with the variant to show, or cancelled.</summary>
public sealed record MeleeChassis(int Model, int Variant, Action<int> Done, Action Cancel)
{
    /// <summary>The car list this model indexes (the scenario's on Nitro); the melee's when null.</summary>
    public MeleeData? Data { get; init; }
    /// <summary>The 3D garage can change the car (MAKE / MODEL, VARIANT): back with the model and variant it ended on. Without it the car is the form's alone.</summary>
    public Action<int, int>? DoneAs { get; init; }
}

/// <summary>0xc01f PARTS CATALOG: record 4, static; DONE (249..388, 452..469) goes back to the chassis form.</summary>
public sealed class PartsCatalogScreen : ShellScreen
{
    public override bool EscapeOpensOptions => false;
    public required ShellScreen Back { get; init; }

    public override void Enter() => Repaint();
    public override void Repaint() => Frame.LoadBackground(Shell.Db, 4);

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of((249, 388, 452, 469))];

    public override void Tick()
    {
        var m = Shell.Mouse;
        if (Shell.Keys.Contains(Key.Escape)) { Shell.Go(Back); return; }
        if (Shell.Clicked && 249 < m.X && m.X < 388 && 452 < m.Y && m.Y < 469) { Shell.Sound(82); Shell.Go(Back); }
    }
}

/// <summary>A melee form's CONFIGURE CHASSIS screen, the 2D form or the 3D garage: Esc in a hosted shell cancels through it.</summary>
public interface IChassisForm
{
    void CancelMelee();
}

/// <summary>Which CONFIGURE CHASSIS the melee forms open: the 3D garage (Graphic Detail's 3D Garage, on by default) or the original's 2D form.</summary>
public static class ChassisForm
{
    public static ShellScreen Create(Shell shell, MeleeChassis mc) =>
        shell.Settings.Garage3D && System.Environment.GetEnvironmentVariable("I76_GARAGE_2D") != "1" ? new I76.Game.Shell.Garage3D.Garage3DScreen { Melee = mc } : new GarageScreen { Melee = mc };
}
