namespace I76.Game.Shell;

/// <summary>
/// Where things are on the base game's network host form as the port draws it (<see cref="MeleeScreen"/>, modes 3 and 4): a ruled
/// sheet in Mouse Options' style, 23 rows of 17 px in two tables. The left one holds the game: LOCAL DRIVER, the game type, AREA OF
/// PLAY with its list, GAME NAME, NO. DRIVERS, TEAM PLAY, INVITE ONLY, MAX SCORE, the password and the bots, with rows to spare. The
/// right one holds the host's rules (the simulation rate and the port's options), then the car: MAKE / MODEL, VARIANT, its weapons
/// and parts, and the blueprint's side view. CONFIGURE CHASSIS, BROADCAST GAME and CANCEL are the sheet's button bars. Pure layout,
/// no engine: the hotspot ids are the form's own (MeleeScreen's table).
/// </summary>
public static class HostFormLayout
{
    public const int RowH = 17, RowTop = 42, Rows = 23;
    /// <summary>The left table (x 24..318) and its name/value rule; the right table (x 322..616) and its rule.</summary>
    public const int L0 = 24, LDiv = 136, L1 = 318, R0 = 322, RDiv = 486, R1 = 616;
    /// <summary>The area list: six rows from row 3 of the left table, its arrows in a column of their own at the left.</summary>
    public const int ListRow = 3, ListRows = 6, ArrowX1 = L0 + 18;
    /// <summary>The right table: the rules in rows 0..6, the car from row 7, the weapons and parts under their headings (row 9), the blueprint from row 17.</summary>
    public const int CarRow = 7, KitHeadRow = 9, KitRow = 10, KitRows = 7, PictureRow = 17;
    public const string Title = "NET MELEE•HOST EVENT FORM";
    /// <summary>The button bars (Mouse Options' slots 1..3): CONFIGURE CHASSIS, BROADCAST GAME, CANCEL, and their ids.</summary>
    public static readonly string[] SlotText = ["", "CONFIGURE CHASSIS", "BROADCAST GAME", "CANCEL"];
    public static readonly int[] SlotId = [-1, 5, 6, 15];
    public static readonly (int X, int Y, int W, int H)[] Slots = [(9, 450, 138, 19), (168, 450, 140, 19), (330, 450, 140, 19), (490, 450, 140, 19)];

    public static int RowY(int k) => RowTop + k * RowH;

    /// <summary>A row's strict hit rectangle between two x (x0 &lt; x &lt; x1): from the rule above it to the one below.</summary>
    public static (int X0, int X1, int Y0, int Y1) Strip(int k, int x0, int x1) => (x0, x1, RowY(k) - 1, RowY(k + 1) - 1);

    /// <summary>The list row a y falls in (0 for the first), as the hotspot's strict rectangle has it.</summary>
    public static int ListRowAt(int y) => (y - (RowY(ListRow) - 1)) / RowH;

    /// <summary>What the form shows: its settings as they stand.</summary>
    public sealed record State
    {
        public bool Coop { get; init; }
        public string Driver { get; init; } = "";
        public bool Renaming { get; init; }
        public string Area { get; init; } = "";
        public string GameName { get; init; } = "";
        public bool Naming { get; init; }
        public int Drivers { get; init; }
        public bool TeamPlay { get; init; }
        public bool InviteOnly { get; init; }
        public bool ScoreOn { get; init; }
        public int MaxScore { get; init; }
        public bool Password { get; init; }
        public int Bots { get; init; }
        public string BotSkill { get; init; } = "";
        public string BotCars { get; init; } = "";
        public int Rate { get; init; }
        public bool ShareMaps { get; init; }
        public bool Hidden { get; init; }
        public bool Turrets { get; init; }
        public bool CarReset { get; init; }
        public bool Cb { get; init; }
        public bool Crosshair { get; init; }
        /// <summary>A co-op game's Co-op Damage as the row shows it ("x1").</summary>
        public string CoopDamage { get; init; } = "x1";
        /// <summary>A co-op game's Co-op Scaling as the row shows it ("Off", "Up to +2").</summary>
        public string CoopScale { get; init; } = "Off";
        /// <summary>A co-op game's Rolling Start.</summary>
        public bool RollingStart { get; init; }
        /// <summary>The host's ignition lock: nobody switches their engine off.</summary>
        public bool IgnitionLock { get; init; }
        /// <summary>The host's Turrets: the 1997 CD build's slew rather than the 1998 build's.</summary>
        public bool Turrets1997 { get; init; }
        public string MakeModel { get; init; } = "";
        public string Variant { get; init; } = "";
    }

    /// <summary>Words in a row: between two x, centred or from the left; a hotspot of its own when <see cref="Id"/> is one.</summary>
    public readonly record struct Cell(int X0, int X1, string Text, int Id = -1, bool Centre = false);

    /// <summary>One labelled row: its table, its row, its name, the words and hotspots in its value column, and the hotspot of the whole row (or -1).</summary>
    public sealed record Row(bool Right, int Index, string Label, IReadOnlyList<Cell> Cells, int RowId = -1);

    private static string OnOff(bool on) => on ? "On" : "Off";

    /// <summary>The Turrets setting's words (Play Options' and the host form's): the build whose turret slew the game plays.</summary>
    public static string TurretsText(bool cd1997) => cd1997 ? "1997 CD" : "1998 (GOG/Nitro)";

    /// <summary>The same in a short value column (Play Options' sheet, the Nitro form's game options): the builds by their years.</summary>
    public static string TurretsShort(bool cd1997) => cd1997 ? "1997 CD" : "1998";

    /// <summary>The rows for these settings. A co-op game has no team play, score limit, bots or maps to share: those rows are left out and the rest close up.</summary>
    public static List<Row> RowsOf(State s)
    {
        var rows = new List<Row>();
        int k = 0;
        void Left(string label, string value, int id) => rows.Add(new(false, k++, label, [new(LDiv, L1, value)], id));
        // a number with its - and + (each a hotspot), the number between them
        void Steps(string label, string value, int minus, int plus) => rows.Add(new(false, k++, label,
            [new(LDiv, LDiv + 26, "-", minus, true), new(LDiv + 26, LDiv + 58, value, -1, true), new(LDiv + 58, LDiv + 84, "+", plus, true)]));
        Left("Local Driver", s.Driver + (s.Renaming ? "_" : ""), 14);
        Left("Game Type", s.Coop ? "Co-op" : "Melee", 32);
        rows.Add(new(false, k++, "Area of Play", [new(LDiv, L1, s.Area)]));
        k += ListRows;
        Left("Game Name", s.GameName + (s.Naming ? "_" : ""), 22);
        Steps("No. Drivers", $"{s.Drivers:00}", 8, 9);
        if (!s.Coop) Left("Team Play", OnOff(s.TeamPlay), 16);
        Left("Invite Only", OnOff(s.InviteOnly), 17);
        // MAX SCORE: its box (On / Off), then the score with its - and +, as the original's line has them
        if (!s.Coop)
            rows.Add(new(false, k++, "Max Score",
                [new(LDiv, LDiv + 40, OnOff(s.ScoreOn), 18), new(LDiv + 40, LDiv + 66, "-", 20, true), new(LDiv + 66, LDiv + 122, s.MaxScore.ToString(), -1, true), new(LDiv + 122, LDiv + 148, "+", 21, true)]));
        Left("Password", s.Password ? "Set" : "None", 27);
        // the port's co-op rules (docs/deviations.md): only a co-op game has them
        if (s.Coop)
        {
            Left("Co-op Damage", s.CoopDamage, 40);
            Left("Co-op Scaling", s.CoopScale, 41);
            Left("Rolling Start", OnOff(s.RollingStart), 42);
        }
        if (!s.Coop)
        {
            Steps("Bots", $"{s.Bots:00}", 29, 30);
            Left("Bot Skill", s.BotSkill, 31);
            Left("A.I. Cars", s.BotCars, 34);
        }
        // the port's game rules a melee and a co-op game share (docs/deviations.md)
        Left("Ignition Lock", OnOff(s.IgnitionLock), 43);
        Left("Turrets", TurretsText(s.Turrets1997), 44);
        k = 0;
        void Right(string label, string value, int id) => rows.Add(new(true, k++, label, [new(RDiv, R1, value)], id));
        Right("Sim Rate", $"{s.Rate} Hz", 33);
        if (!s.Coop) Right("Share Maps", OnOff(s.ShareMaps), 26);
        Right("Hidden Weapons & Cars", OnOff(s.Hidden), 28);
        Right("Unlimited Turrets", OnOff(s.Turrets), 35);
        Right("Car Reset", OnOff(s.CarReset), 36);
        Right("CB Radio", OnOff(s.Cb), 37);
        Right("Chase Crosshair", OnOff(s.Crosshair), 38);
        k = CarRow;
        Right("Make / Model", s.MakeModel, 3);
        Right("Variant", s.Variant, 4);
        return rows;
    }

    /// <summary>Every hotspot of the form by id: the rows', the area list and its arrows, the button bars.</summary>
    public static Dictionary<int, (int X0, int X1, int Y0, int Y1)> Hot(IEnumerable<Row> rows)
    {
        var hot = new Dictionary<int, (int, int, int, int)>();
        foreach (var r in rows)
        {
            if (r.RowId >= 0) hot[r.RowId] = r.Right ? Strip(r.Index, R0, R1) : Strip(r.Index, L0, L1);
            foreach (var c in r.Cells)
                if (c.Id >= 0) hot[c.Id] = Strip(r.Index, c.X0, c.X1);
        }
        hot[12] = Strip(ListRow, L0, ArrowX1);
        hot[13] = Strip(ListRow + ListRows - 1, L0, ArrowX1);
        hot[11] = (ArrowX1, L1, RowY(ListRow) - 1, RowY(ListRow + ListRows) - 1);
        for (int i = 1; i < Slots.Length; i++)
        {
            var s = Slots[i];
            hot[SlotId[i]] = (s.X, s.X + s.W, s.Y, s.Y + s.H);
        }
        return hot;
    }

    /// <summary>List row k's strict rectangle (the pad's region for it).</summary>
    public static (int X0, int X1, int Y0, int Y1) ListRowRect(int k) => Strip(ListRow + k, ArrowX1, L1);

    /// <summary>The pad's regions as the screen gives them: the hotspots by id, the area list (id 11) as its rows, as many as are shown.</summary>
    public static List<(int Id, int ListRow, (int X0, int X1, int Y0, int Y1) Rect)> PadOrder(IReadOnlyDictionary<int, (int X0, int X1, int Y0, int Y1)> hot, int listShown)
    {
        var l = new List<(int, int, (int, int, int, int))>();
        foreach (int id in hot.Keys.Order())
            if (id == 11) for (int k = 0; k < Math.Min(ListRows, listShown); k++) l.Add((11, k, ListRowRect(k)));
            else l.Add((id, -1, hot[id]));
        return l;
    }

    /// <summary>
    /// The sheet's rules as rectangles (x, y, w, h): Mouse Options' double frame and the tables' double meeting rule; the left table's
    /// rows and its name rule (not through the area list, whose arrows have their own column); the right table's rows and rule down to the
    /// blueprint, which sits on plain paper; the weapons and parts columns meet at the right table's rule.
    /// </summary>
    public static IEnumerable<(int X, int Y, int W, int H)> Rules()
    {
        const int x0 = 20, y0 = 37, x1 = 620, y1 = 439, inset = 4;
        yield return (x0, y0, x1 - x0, 2); yield return (x0, y1 - 2, x1 - x0, 2);
        yield return (x0, y0, 2, y1 - y0); yield return (x1 - 2, y0, 2, y1 - y0);
        int i0 = x0 + inset, j0 = y0 + inset, i1 = x1 - inset, j1 = y1 - inset;
        yield return (i0, j0, i1 - i0, 1); yield return (i0, j1 - 1, i1 - i0, 1);
        yield return (i0, j0, 1, j1 - j0); yield return (i1 - 1, j0, 1, j1 - j0);
        yield return (L1, j0, 1, j1 - j0); yield return (L1 + 3, j0, 1, j1 - j0);
        // the left table: its name rule above and below the list, the arrows' column beside the list
        yield return (LDiv, j0, 1, RowY(ListRow) - 1 - j0);
        yield return (LDiv, RowY(ListRow + ListRows) - 1, 1, j1 - (RowY(ListRow + ListRows) - 1));
        yield return (ArrowX1, RowY(ListRow) - 1, 1, ListRows * RowH);
        for (int k = 1; k < Rows; k++) yield return (i0, RowY(k) - 1, L1 - i0, 1);
        // the right table: rows and its rule to the blueprint
        int pic = RowY(PictureRow) - 1;
        yield return (RDiv, j0, 1, pic - j0);
        for (int k = 1; k <= PictureRow; k++) yield return (L1 + 4, RowY(k) - 1, i1 - L1 - 4, 1);
    }

    /// <summary>The blueprint's room under the weapons and parts: x0, y0, x1, y1.</summary>
    public static (int X0, int Y0, int X1, int Y1) Picture => (R0, RowY(PictureRow), R1, RowY(Rows) - 1);
}
