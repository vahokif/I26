using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Game.Boot;

namespace I76.Game.Shell;

/// <summary>
/// The trip's bookmarks ("Bookmarks"): what
/// savegame.dir lists for each — the scene (mission) number, the name, the
/// file stem save%03d, the game state at saving (1 = between a won mission
/// and the next) and whether salvage management was off. Kept in
/// user://saves/bookmarks.cfg. The car and its inventory (the .cmp body) join
/// with the garage.
/// </summary>
public sealed class Bookmarks
{
    private const string Path = "user://saves/bookmarks.cfg";

    public sealed record Entry(int Mission, string Name, string Stem, int State, bool NoSalvage, bool Checkpoint = false)
    {
        /// <summary>"Scene %d. " — one on when the entry was saved after a win.</summary>
        public int Scene => Mission + (State == 1 ? 1 : 0);
    }

    public List<Entry> Entries { get; } = [];

    public static Bookmarks Load()
    {
        var b = new Bookmarks();
        var c = new ConfigFile();
        if (c.Load(Path) != Error.Ok) return b;
        foreach (var sec in c.GetSections())
            b.Entries.Add(new Entry((int)c.GetValue(sec, "mission", 1), (string)c.GetValue(sec, "name", ""), sec,
                (int)c.GetValue(sec, "state", 0), (bool)c.GetValue(sec, "nosalvage", false), (bool)c.GetValue(sec, "checkpoint", false)));
        b.Entries.Sort((x, y) => string.CompareOrdinal(x.Stem, y.Stem));
        return b;
    }

    public void Save()
    {
        var c = new ConfigFile();
        foreach (var e in Entries)
        {
            c.SetValue(e.Stem, "mission", e.Mission);
            c.SetValue(e.Stem, "name", e.Name);
            c.SetValue(e.Stem, "state", e.State);
            c.SetValue(e.Stem, "nosalvage", e.NoSalvage);
            if (e.Checkpoint) c.SetValue(e.Stem, "checkpoint", true);
        }
        DirAccess.MakeDirRecursiveAbsolute("user://saves");
        c.Save(Path);
    }

    /// <summary>The car and inventory a bookmark keeps (the .cmp body): user://saves/bookmark_save000.json.</summary>
    private static string CarPath(string stem) => $"user://saves/bookmark_{stem}.json";

    public static void WriteCar(string stem, string json)
    {
        DirAccess.MakeDirRecursiveAbsolute("user://saves");
        using var f = Godot.FileAccess.Open(CarPath(stem), Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(json);
    }

    /// <summary>The kept car, or null for a bookmark with none (one saved before the car was kept).</summary>
    public static I76.Sim.Trip.TripInventory? ReadCar(string stem, I76.Sim.Trip.PartsDatabase db, I76.Formats.Vfs.AssetVfs vfs)
    {
        using var f = Godot.FileAccess.Open(CarPath(stem), Godot.FileAccess.ModeFlags.Read);
        return f is null ? null : I76.Sim.Trip.TripInventory.Load(f.GetAsText(), db, vfs);
    }

    /// <summary>The simulation checkpoint snapshot a bookmark keeps: user://saves/bookmark_save000.snap.</summary>
    private static string SnapPath(string stem) => $"user://saves/bookmark_{stem}.snap";

    public static void WriteSnap(string stem, byte[] data)
    {
        DirAccess.MakeDirRecursiveAbsolute("user://saves");
        using var f = Godot.FileAccess.Open(SnapPath(stem), Godot.FileAccess.ModeFlags.Write);
        f?.StoreBuffer(data);
    }

    public static byte[]? ReadSnap(string stem)
    {
        using var f = Godot.FileAccess.Open(SnapPath(stem), Godot.FileAccess.ModeFlags.Read);
        return f is null ? null : f.GetBuffer((long)f.GetLength());
    }

    public static void DeleteSnap(string stem)
    {
        string p = SnapPath(stem);
        if (Godot.FileAccess.FileExists(p)) DirAccess.RemoveAbsolute(p);
    }

    /// <summary>A new entry's stem: save%03d with the number one past the largest (parsed back from the stem).</summary>
    public string NextStem() => $"save{(Entries.Count == 0 ? 0 : Entries.Max(e => int.TryParse(e.Stem.AsSpan(4), out int n) ? n : 0) + 1):000}";
}

/// <summary>
/// The Save and Load Bookmark overlays' shared frame:
/// "Scene %d. " at (186, 132) and the name at (275, 132) in font 0x46; a
/// ten-row list from y 184 (rows font 0x45's height + 8) with scroll arrows;
/// the action and Cancel at (328, 420), Cancel 25 px after the first.
/// </summary>
public abstract class BookmarkScreen : OverlayScreen
{
    /// <summary>Opened from the main menu's LOAD BOOKMARK: Cancel goes straight back to it.</summary>
    public bool FromMainMenu { get; init; }
    protected Bookmarks Marks = null!;
    protected int Top, Picked = -1;
    protected ShellFont Rows = null!;
    protected int RowH => Rows.Height + 8;
    protected abstract string Action { get; }

    protected override void Build()
    {
        Marks = Bookmarks.Load();
        Rows = Shell.Db.Font(0x45);
        int done = Small.Measure(Action);
        Add(328, 420, Action, DoAction, Small);
        Add(328 + done + 25, 420, "Cancel", Cancel, Small);   // x = 0x148 + width + 0x19
        Add(201, 158, () => "", () => Top = Math.Max(0, Top - 1), w: 20, h: 18).Repeats = true;                                  // scroll up
        Add(201, 413, () => "", () => Top = Math.Min(Math.Max(0, Marks.Entries.Count - 10), Top + 1), w: 20, h: 17).Repeats = true;   // scroll down
    }

    protected abstract void DoAction();

    protected void Cancel()
    {
        if (FromMainMenu) { Shell.Overlay(null); return; }
        Shell.Overlay(new OptionsScreen());
    }

    protected override void Escape() => Shell.Overlay(new OptionsScreen());

    private long _lastClickTime;
    private int _lastClickRow = -1;

    protected override void ClickElsewhere(Vector2I m)
    {
        if (m.X <= 185 || m.X >= 452 || m.Y <= 179 || m.Y >= 408) return;
        int row = Top + (m.Y - 184) / RowH;
        if (row < 0 || row >= Marks.Entries.Count) return;
        long now = System.Environment.TickCount64;
        bool doubleClick = row == _lastClickRow && (now - _lastClickTime) < 500;
        _lastClickTime = now;
        _lastClickRow = row;
        Pick(row);
        Redraw();
        if (doubleClick) DoAction();
    }

    protected virtual void Pick(int row) => Picked = row;

    protected override bool Wheeled(int rows)
    {
        int to = Math.Clamp(Top + rows, 0, Math.Max(0, Marks.Entries.Count - 10));
        if (to == Top) return false;
        Top = to;
        return true;
    }

    private bool Scroll(int by)
    {
        int to = Math.Clamp(Top + by, 0, Math.Max(0, Marks.Entries.Count - 10));
        if (to == Top) return false;
        Top = to;
        Redraw();
        return true;
    }

    /// <summary>The buttons and the visible rows of the list (a press past the top or bottom row scrolls it).</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = base.PadRegions.ToList();
            int shown = Math.Min(10, Marks.Entries.Count - Top);
            for (int i = 0; i < shown; i++)
            {
                int k = i;
                l.Add(PadRegion.Of(190, 184 + i * RowH, 262, RowH, dir => dir == NavDir.Up && k == 0 ? Scroll(-1) : dir == NavDir.Down && k == 9 ? Scroll(1) : false));
            }
            return l;
        }
    }

    protected abstract (int Scene, string Name) Header();

    protected override void DrawExtra()
    {
        if (Marks.Entries.Count == 0 && Action == "Load") Frame.Text(Label, 228, 252, "(No Games available)", ShellFrame.OverlayInk);
        var (scene, name) = Header();
        Frame.Text(Label, 186, 132, $"Scene {scene}. ", ShellFrame.OverlayInk);
        Frame.Text(Label, 275, 132, name, ShellFrame.OverlayInk);
        for (int i = 0; i < 10 && Top + i < Marks.Entries.Count; i++)
        {
            var e = Marks.Entries[Top + i];
            Frame.Text(Rows, 190, 184 + i * RowH, $"Scene {e.Scene}. {e.Name}", ShellFrame.OverlayInk);
        }
        if (Picked - Top is >= 0 and var rowIdx && rowIdx < 10 && Picked < Marks.Entries.Count)
        {
            byte red = Frame.Nearest(ControllerScreen.RedPencil);
            int ry = 184 + rowIdx * RowH;
            Frame.Fill(189, ry + Rows.Height + 1, 262, 1, red);
            for (int p = 0; p < 3; p++) Frame.Fill(183 + p, ry + 3 + p, 1, 7 - 2 * p, red);
        }
    }
}

/// <summary>Save Bookmark, overlay 1 (record 11): name the bookmark, pick a row to overwrite, Save.</summary>
public sealed class SaveBookmarkScreen : BookmarkScreen
{
    protected override int Record => 11;
    protected override string Action => "Save";
    private string _name = "";
    private bool _editing = true;       // the name is typed as soon as the form is up

    protected override void Build()
    {
        base.Build();
        Add(183, 127, () => "", () => _editing = true, w: 271, h: 23);      // the name field, 32 characters
    }

    protected override (int, string) Header() => (Shell.Trip.Mission, _name + (_editing ? "_" : ""));

    public override bool TextEditing => _editing;
    public override void StopEditing() { _editing = false; Redraw(); }

    protected override void Pick(int row)
    {
        base.Pick(row);
        _name = Marks.Entries[row].Name;
    }

    public override void Tick()
    {
        if (_editing)
        {
            bool changed = false;
            // '~' and characters the font lacks are refused; name + '_' stays under 190 px
            foreach (char c in Shell.Typed) if (_name.Length < 32 && c != '~' && Label.Glyph(c) is not null && Label.Measure(_name + c + '_') < 190) { _name += c; changed = true; }
            if (Shell.Keys.Contains(Key.Backspace) && _name.Length > 0) { _name = _name[..^1]; changed = true; }
            // the first Esc only ends the edit (a second opens Options); Enter and Tab save, as the original's name field does
            // (the edit returns 1 and the bookmark is written)
            if (Shell.Keys.Contains(Key.Escape)) { _editing = false; Redraw(); return; }
            if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter) || Shell.Keys.Contains(Key.Tab)) { _editing = false; Redraw(); DoAction(); return; }
            if (changed) Redraw();
        }
        base.Tick();
    }

    /// <summary>Save: written as named, an empty name too, as the original does.</summary>
    protected override void DoAction()
    {
        int mission = Shell.Trip.Mission;
        var same = Picked >= 0 ? Picked : Marks.Entries.FindIndex(e => e.Mission == mission && e.Name == _name);
        if (same < 0) { Write(Marks.NextStem()); return; }
        Shell.Modal(new ConfirmPopup
        {
            Lines = ["Overwrite an existing bookmark?"],
            Answer = yes => { if (yes) Write(Marks.Entries[same].Stem); else { _editing = true; Redraw(); } },   // No: back to editing the name
        });
    }

    private void Write(string stem)
    {
        Marks.Entries.RemoveAll(e => e.Stem == stem);
        Marks.Entries.Add(new Bookmarks.Entry(Shell.Trip.Mission, _name, stem, 0, (Shell.Settings.PlayOptions & 2) != 0));
        Marks.Entries.Sort((x, y) => string.CompareOrdinal(x.Stem, y.Stem));
        // the .cmp: the car, its parts (the garage's current lists, the field salvage still waiting) and its repair order
        if (Shell.Trip.Inventory(Shell) is { } inv) Bookmarks.WriteCar(stem, inv.Save());
        Bookmarks.DeleteSnap(stem);
        Marks.Save();
        Shell.Overlay(null);    // Back to the screen underneath; only Cancel and Esc return to Options
    }
}

/// <summary>
/// Load Bookmark, overlay 11 (record 12): pick a row, Load resumes the trip
/// through the shell's post-mission routing — mission 17 to CHOOSE YOUR VEHICLE, salvage
/// management off to the record-29 notice, mission 13 to YOU ARE UNCONSCIOUS,
/// otherwise field salvage (the router's own branches).
/// </summary>
public sealed class LoadBookmarkScreen : BookmarkScreen
{
    protected override int Record => 12;
    protected override string Action => "Load";

    protected override void Build()
    {
        base.Build();
        if (Marks.Entries.Count > 0 && Picked < 0) Pick(0);
    }

    protected override (int, string) Header() =>
        Picked >= 0 && Picked < Marks.Entries.Count ? (Marks.Entries[Picked].Scene, Marks.Entries[Picked].Name) : (Shell.Trip.Mission, "");

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter))
        {
            DoAction();
            return;
        }
        base.Tick();
    }

    protected override void DoAction()
    {
        if (Picked < 0 || Picked >= Marks.Entries.Count) return;
        var e = Marks.Entries[Picked];
        Shell.Trip.Mission = e.Scene;
        Shell.Trip.Highest = Math.Max(Shell.Trip.Highest, e.Scene);
        Shell.Trip.FromBookmark = true;
        // the saved car and parts replace the trip's (a bookmark from before they were kept leaves them as they are)
        if (Bookmarks.ReadCar(e.Stem, Shell.Parts, Shell.Vfs) is { } car) Shell.Trip.SetInventory(car);
        Shell.Trip.Save();
        if (e.NoSalvage) Shell.Settings.PlayOptions |= 2; else Shell.Settings.PlayOptions &= ~2;
        Shell.Settings.Save();      // I76PLYR.DEF is written at once
        Shell.Overlay(null);

        if (e.Checkpoint && Bookmarks.ReadSnap(e.Stem) is { } snapBytes && Checkpoint.FromBytes(snapBytes) is { } cp)
        {
            var inv = Shell.Trip.Inventory(Shell);
            if (inv is not null)
                I76.Formats.Vfs.AssetVfs.Addon["vehscn.vcf"] = inv.ToCar().ToVcf();
            string carVcf = inv is not null ? "vehscn.vcf" : $"vppt{e.Scene:00}.vcf";

            Main.QueueCheckpointResume(cp);
            Shell.Launch(new ShellLaunch(cp.Mission, GameKind.Trip, carVcf)
            {
                State = inv?.State(),
                NoSalvage = e.NoSalvage
            });
            return;
        }

        Shell.Go(new TripRouterScreen());
    }
}
